using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Templates.Api;
using Templates.Compiled;
using Templates.Data;

namespace Templates;

// A group as a read sees it: the rules to show, the group ETag (over every stored rule of the group, ended
// ones included) and the document version they were read at.
internal sealed record GroupView(DynamicTemplate[] Rules, string ETag, long Version);

// Design 2.1, L3 (D3, D5): write, list the history of, and roll back one `group` of rules. "Version n of a
// group" is document version n; a group's history is the list of document versions that changed it.
internal sealed partial class V2Logic
{
    private const int MaxGroupNameLength = 256;
    internal const int DefaultHistoryLimit = 100;
    internal const int MaxHistoryLimit = 1000;

    // D3: non-empty, at most 256 characters, no control characters, so "\0" (the group index separator)
    // can never occur in one.
    internal static bool IsValidGroupName(string? group) =>
        !string.IsNullOrEmpty(group) && group.Length <= MaxGroupNameLength && !group.Any(char.IsControl);

    // Rules whose `group` is set must name a valid group. An empty `group` is treated as no group.
    private static IEnumerable<V2ValidationError> GroupNameErrors(IEnumerable<DynamicTemplate> rules) =>
        rules.Where(r => !string.IsNullOrEmpty(r.Group) && !IsValidGroupName(r.Group))
            .Select(r => new V2ValidationError(string.IsNullOrEmpty(r.Name) ? "(unnamed)" : r.Name,
                $"group must be at most {MaxGroupNameLength} characters with no control characters"));

    private static bool InGroup(DynamicTemplate rule, string group) => string.Equals(rule.Group, group, StringComparison.Ordinal);

    private static DynamicTemplate[] RulesOf(Template? document, string group) =>
        document == null ? [] : [.. document.Templates.Where(r => InGroup(r, group))];

    // The group ETag: the first 16 hex characters of a SHA-256 over the serialized ordered list of the
    // group's stored rules, the same construction as the rule ETag. Not the document version: two clients
    // editing different groups of one document would otherwise conflict (D5).
    internal static string GroupETagValue(IReadOnlyCollection<DynamicTemplate> rules)
    {
        var json = JsonSerializer.Serialize(rules.ToArray(), AppJsonSerializerContext.GlobalDefault.DynamicTemplateArray);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
    }

    // D3: the groups whose content (the ordered list of their rules, compared by rule content hash)
    // differs between the stored document and the one about to be committed. Ungrouped rules, and
    // groups whose stored name is not a valid group name, are not indexed.
    internal static string[] ChangedGroups(Template? before, Template after)
    {
        var old = GroupContents(before?.Templates ?? []);
        var now = GroupContents(after.Templates);
        return [.. old.Keys.Union(now.Keys, StringComparer.Ordinal)
            .Where(g => !(old.TryGetValue(g, out var a) && now.TryGetValue(g, out var b) && a.SequenceEqual(b, StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal)];
    }

    private static Dictionary<string, List<string>> GroupContents(IEnumerable<DynamicTemplate> rules)
    {
        var contents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (!IsValidGroupName(rule.Group))
                continue;
            if (!contents.TryGetValue(rule.Group!, out var list))
                contents[rule.Group!] = list = [];
            list.Add(RuleETagValue(rule));
        }
        return contents;
    }

    // D5 placement: the group's rules are written contiguously, in the given order, at the position of the
    // group's first existing rule; the group's other rules are removed. A new group is appended. So the
    // order across groups never changes because one group was edited.
    private static DynamicTemplate[] Place(DynamicTemplate[] stored, string group, IReadOnlyCollection<DynamicTemplate> replacement)
    {
        var result = new List<DynamicTemplate>(stored.Length + replacement.Count);
        var placed = false;
        foreach (var rule in stored)
        {
            if (!InGroup(rule, group))
            {
                result.Add(rule);
            }
            else if (!placed)
            {
                result.AddRange(replacement);
                placed = true;
            }
        }
        if (!placed)
            result.AddRange(replacement);
        return [.. result];
    }

    // If-Match on a group write: absent = last writer wins for this group; "*" = the group must exist; a
    // group ETag = the group's stored rules must still hash to it.
    private static bool GroupPreconditionHolds(DynamicTemplate[] storedGroup, string? ifMatch) =>
        ifMatch switch
        {
            null => true,
            "*" => storedGroup.Length > 0,
            _ => storedGroup.Length > 0 && GroupETagValue(storedGroup) == ifMatch,
        };

    // 404 (null) only when the group has no stored rules; a group whose rules have all ended is an empty
    // list by default, so a client can still read its ETag. The ETag covers ended rules (D6).
    internal async Task<GroupView?> GetGroupAsync(string templateId, string group, bool includeExpired = false)
    {
        var stored = await repository.ReadAsync(templateId);
        var rules = RulesOf(stored.Document, group);
        if (rules.Length == 0)
            return null;

        var now = Now;
        return new GroupView(includeExpired ? rules : [.. rules.Where(r => !r.IsExpired(now))], Quote(GroupETagValue(rules)), stored.Version);
    }

    internal async Task<DynamicTemplate[]?> GetGroupVersionAsync(string templateId, string group, long version)
    {
        var document = await repository.GetVersionDocumentAsync(templateId, version);
        var rules = RulesOf(document, group);
        return rules.Length == 0 ? null : rules;
    }

    internal Task<List<TemplateVersionSummary>> ListGroupVersionsAsync(string templateId, string group, int limit, long? before) =>
        repository.ListGroupVersionsAsync(templateId, group, limit, before);

    internal Task<WriteResult> PutGroupAsync(string templateId, string group, GroupWriteRequest? body, string? ifMatch, string? changedBy)
    {
        if (!IsValidGroupName(group))
            return Task.FromResult(Invalid("(group)", $"a group name is 1 to {MaxGroupNameLength} characters with no control characters"));
        if (body?.Rules == null)
            return Task.FromResult(Invalid("(document)", "no body; expected {\"rules\": [...]}"));
        if (body.Rules.Length == 0)
            return Task.FromResult(Invalid("rules", "no rules; use DELETE to remove a group"));

        var errors = new List<V2ValidationError>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in body.Rules)
        {
            if (string.IsNullOrEmpty(rule.Name))
                errors.Add(new V2ValidationError("(unnamed)", "every rule in a group write needs a name"));
            else if (!names.Add(rule.Name))
                errors.Add(new V2ValidationError(rule.Name, "the name appears more than once in the body"));
            else if (rule.Group != null && !InGroup(rule, group))
                errors.Add(new V2ValidationError(rule.Name, $"body puts the rule in group '{rule.Group}'; it must match the route or be omitted"));
        }
        if (errors.Count == 0)
            errors.AddRange(CompiledV2Template.Validate(new Template(body.Rules, body.AggregationField ?? "name")));
        if (errors.Count > 0)
            return Task.FromResult(new WriteResult(WriteOutcome.Invalid, Errors: errors));

        DynamicTemplate[] rules = [.. body.Rules.Select(r => r with { Group = group })];
        return WriteGroupAsync(templateId, group, rules, body.AggregationField, body.AggregationField, ifMatch, changedBy, nameTakenOutcome: WriteOutcome.Invalid);
    }

    internal Task<WriteResult> DeleteGroupAsync(string templateId, string group, string? ifMatch, string? changedBy) =>
        CommitLoopAsync(templateId, stored =>
        {
            var storedGroup = RulesOf(stored.Document, group);
            if (storedGroup.Length == 0)
                return Decision.Stop(WriteResult.Failed(WriteOutcome.NotFound, $"no group '{group}' in template '{templateId}'"));
            if (!GroupPreconditionHolds(storedGroup, ifMatch))
                return Decision.Stop(WriteResult.Failed(WriteOutcome.PreconditionFailed));

            // Every rule of the group goes, ended ones included.
            var next = stored.Document! with
            {
                Templates = [.. stored.Document.Templates.Where(r => !InGroup(r, group))],
                ChangedBy = ChangedByOrUnknown(changedBy),
            };
            return new Decision(next, (v, _) => new WriteResult(WriteOutcome.Ok, v));
        });

    // D5: restore the group as it stood at document version n, as a new version. It is then a group PUT of
    // those rules, except that a name another group now owns is a 409 naming both groups (the caller did
    // not choose the names, history did).
    internal async Task<(WriteResult Result, DynamicTemplate[] Rules)> RollbackGroupAsync(string templateId, string group, long version, string? ifMatch, string? changedBy)
    {
        var snapshot = await repository.GetVersionDocumentAsync(templateId, version);
        if (snapshot == null)
            return (WriteResult.Failed(WriteOutcome.NotFound, $"no version {version} of template '{templateId}'"), []);

        var rules = RulesOf(snapshot, group);
        if (rules.Length == 0)
            return (WriteResult.Failed(WriteOutcome.NotFound, $"group '{group}' is not present at version {version}; use DELETE to remove it"), []);

        var result = await WriteGroupAsync(templateId, group, rules, snapshot.AggregationField, null, ifMatch, changedBy, nameTakenOutcome: WriteOutcome.Conflict);
        return (result, rules);
    }

    private Task<WriteResult> WriteGroupAsync(
        string templateId,
        string group,
        DynamicTemplate[] rules,
        string? createWithAggregationField,
        string? requiredAggregationField,
        string? ifMatch,
        string? changedBy,
        WriteOutcome nameTakenOutcome)
    {
        var etag = GroupETagValue(rules);
        return CommitLoopAsync(templateId, stored =>
        {
            var storedRules = stored.Document?.Templates ?? [];
            var storedGroup = RulesOf(stored.Document, group);
            if (!GroupPreconditionHolds(storedGroup, ifMatch))
                return Decision.Stop(WriteResult.Failed(WriteOutcome.PreconditionFailed));

            if (stored.Document == null && string.IsNullOrEmpty(createWithAggregationField))
                return Decision.Stop(Invalid("aggregationField", $"template '{templateId}' does not exist; aggregationField is required to create it"));
            if (stored.Document != null && requiredAggregationField != null && !string.Equals(requiredAggregationField, stored.Document.AggregationField, StringComparison.Ordinal))
                return Decision.Stop(Invalid("aggregationField", $"the template's aggregationField is '{stored.Document.AggregationField}'"));

            // Rule names stay unique across the document, which the rule-level routes rely on.
            foreach (var rule in rules)
            {
                var owner = Array.Find(storedRules, r => string.Equals(r.Name, rule.Name, StringComparison.Ordinal) && !InGroup(r, group));
                if (owner == null)
                    continue;
                var owned = string.IsNullOrEmpty(owner.Group) ? "an ungrouped rule" : $"group '{owner.Group}'";
                return Decision.Stop(nameTakenOutcome == WriteOutcome.Conflict
                    ? WriteResult.Failed(WriteOutcome.Conflict, $"rule '{rule.Name}' of group '{group}' is now owned by {owned}")
                    : Invalid(rule.Name, $"the name is already used by {owned}"));
            }

            // D2: a write that asks for exactly what the group already holds commits nothing, so a client's
            // retry of a group write is idempotent.
            if (stored.Document != null && storedGroup.Length > 0 && GroupETagValue(storedGroup) == etag)
                return Decision.Stop(new WriteResult(WriteOutcome.Ok, stored.Version, Quote(etag)));

            var document = stored.Document ?? new Template([], createWithAggregationField!);
            var next = document with
            {
                Templates = Place(storedRules, group, rules),
                ChangedBy = ChangedByOrUnknown(changedBy),
            };
            return new Decision(next, (v, committed) => new WriteResult(WriteOutcome.Ok, v, Quote(GroupETagValue(RulesOf(committed, group)))));
        });
    }

    private static WriteResult Invalid(string field, string reason) =>
        new(WriteOutcome.Invalid, Errors: [new V2ValidationError(field, reason)]);
}
