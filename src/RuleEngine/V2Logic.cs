using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using Templates.Api;
using Templates.Compiled;
using Templates.Data;

namespace Templates;

internal enum WriteOutcome { Ok, NotFound, PreconditionFailed, Invalid, Conflict }

internal sealed record WriteResult(WriteOutcome Outcome, long Version = 0, string? ETag = null, IReadOnlyList<V2ValidationError>? Errors = null, string? Detail = null)
{
    public static WriteResult Failed(WriteOutcome outcome, string? detail = null) => new(outcome, Detail: detail);
}

// P-14: everything the v2 routes do, so Api/APIv2*.cs stays route wiring and status codes. v1's Logic
// was deleted with the rest of v1 in P-20; this is the only serving logic left.
internal sealed class V2Logic(Repository repository, DynamicRulesEngine engine, IOptions<TemplateApiOptions> options)
{
    // How many times a rule-level write retries when an unrelated concurrent write moved the document
    // version between its read and its conditional write. D-15: a rule edit must only conflict with an
    // edit to the SAME rule, so a lost version race is retried rather than reported as a 412.
    private const int RuleWriteAttempts = 4;

    public TemplateApiOptions Options { get; } = options.Value;

    internal Task<(CompiledV2Template Compiled, long Version)?> LoadAsync(string templateId, CancellationToken ct) =>
        engine.LoadV2Async(templateId, ct);

    internal static JsonObject Evaluate(CompiledV2Template compiled, JsonElement postedData, DateTime utcNow) =>
        DynamicRulesEngine.ApplyV2Templates(compiled, postedData, utcNow);

    // P-07/P-20: the evaluate endpoints only need the merged bytes, so they take the plan form and skip
    // the JsonObject tree entirely - see DynamicRulesEngine.ApplyV2TemplatesToPlan.
    internal static MergePlan EvaluateToPlan(CompiledV2Template compiled, JsonElement postedData, DateTime utcNow) =>
        DynamicRulesEngine.ApplyV2TemplatesToPlan(compiled, postedData, utcNow);

    internal static ExplainResponse Explain(string templateId, long version, CompiledV2Template compiled, JsonElement postedData) =>
        V2Explain.Explain(templateId, version, compiled, postedData);

    internal Task<string[]> ListAsync() => repository.ListTemplateIdsAsync();

    internal Task<List<TemplateVersionSummary>> ListVersionsAsync(string templateId) => repository.ListVersionsAsync(templateId);

    internal Task<Template?> GetVersionAsync(string templateId, long version) => repository.GetVersionDocumentAsync(templateId, version);

    // D-16: expired rules are skipped on read as well as on evaluate, so an admin GET shows the document
    // the service actually serves. They leave storage the next time something rewrites the document.
    internal async Task<(Template Template, long Version)?> GetAsync(string templateId)
    {
        var loaded = await repository.GetWithVersionAsync(templateId);
        if (loaded == null) return null;
        return (Prune(loaded.Value.Template), loaded.Value.Version);
    }

    internal async Task<WriteResult> PutAsync(string templateId, Template? template, string? ifMatch, string? changedBy)
    {
        if (template == null)
            return new WriteResult(WriteOutcome.Invalid, Errors: [new V2ValidationError("(document)", "no body")]);

        // A7: every rule is validated by compiling it. v1 skipped validation entirely for any id
        // containing "override" (APIValidator.IsValid), which is how uncompilable rules reached storage.
        var errors = CompiledV2Template.Validate(template);
        if (errors.Count > 0)
            return new WriteResult(WriteOutcome.Invalid, Errors: errors);

        // A9: changedBy is the gateway's userid header, never the request body.
        var document = template with { ChangedBy = string.IsNullOrEmpty(changedBy) ? "Unknown" : changedBy };

        long? version;
        if (ifMatch == null)
        {
            version = await repository.SetAsync(document, templateId);
        }
        else if (ifMatch == "*")
        {
            var current = await repository.GetWithVersionAsync(templateId);
            if (current == null)
                return WriteResult.Failed(WriteOutcome.PreconditionFailed);
            version = await repository.SetIfVersionAsync(document, templateId, current.Value.Version);
        }
        else
        {
            if (!long.TryParse(ifMatch, out var expected))
                return WriteResult.Failed(WriteOutcome.PreconditionFailed);
            version = await repository.SetIfVersionAsync(document, templateId, expected);
        }

        if (version == null)
            return WriteResult.Failed(WriteOutcome.PreconditionFailed);

        await engine.InvalidateAsync(templateId);
        return new WriteResult(WriteOutcome.Ok, version.Value, DocumentETag(version.Value));
    }

    internal async Task<WriteResult> DeleteAsync(string templateId, string? ifMatch)
    {
        var current = await repository.GetWithVersionAsync(templateId);
        if (current == null)
            return WriteResult.Failed(WriteOutcome.NotFound, $"no template '{templateId}'");

        if (ifMatch != null && ifMatch != "*" && (!long.TryParse(ifMatch, out var expected) || expected != current.Value.Version))
            return WriteResult.Failed(WriteOutcome.PreconditionFailed);

        var deleted = await repository.DeleteAsync(templateId);
        await engine.InvalidateAsync(templateId);
        return deleted ? new WriteResult(WriteOutcome.Ok) : WriteResult.Failed(WriteOutcome.NotFound);
    }

    internal async Task<WriteResult> RollbackAsync(string templateId, long version)
    {
        var newVersion = await repository.RollbackAsync(templateId, version);
        if (newVersion == null)
            return WriteResult.Failed(WriteOutcome.NotFound);

        await engine.InvalidateAsync(templateId);
        return new WriteResult(WriteOutcome.Ok, newVersion.Value, DocumentETag(newVersion.Value));
    }

    internal async Task<(DynamicTemplate Rule, string ETag)?> GetRuleAsync(string templateId, string ruleName)
    {
        var loaded = await repository.GetWithVersionAsync(templateId);
        var rule = loaded?.Template.Templates.FirstOrDefault(r => string.Equals(r.Name, ruleName, StringComparison.Ordinal));
        return rule == null ? null : (rule, RuleETag(rule));
    }

    // D-15: rule-level edits, matched on the EXACT name. offer-products' prefix matching (DeleteGroup /
    // SetDisable on Name.StartsWith) is what made "racing" also hit "racingmetro"; nothing here matches
    // on a prefix. The If-Match ETag is the rule's own content hash, not the document version, so an
    // unrelated automated EventId-* write between an admin's read and their write is not a conflict.
    internal async Task<WriteResult> PutRuleAsync(string templateId, string ruleName, DynamicTemplate? rule, string? ifMatch, string? changedBy)
    {
        if (rule == null)
            return new WriteResult(WriteOutcome.Invalid, Errors: [new V2ValidationError(ruleName, "no body")]);
        if (!string.IsNullOrEmpty(rule.Name) && !string.Equals(rule.Name, ruleName, StringComparison.Ordinal))
            return new WriteResult(WriteOutcome.Invalid, Errors: [new V2ValidationError(ruleName, $"body names the rule '{rule.Name}'; it must match the route or be omitted")]);

        var named = string.IsNullOrEmpty(rule.Name) ? rule with { Name = ruleName } : rule;
        return await MutateRulesAsync(templateId, ruleName, ifMatch, changedBy, rules =>
        {
            var errors = CompiledV2Template.Validate(new Template([named], "name"));
            if (errors.Count > 0)
                return (null, new WriteResult(WriteOutcome.Invalid, Errors: errors));

            var index = rules.FindIndex(r => string.Equals(r.Name, ruleName, StringComparison.Ordinal));
            if (index < 0)
                rules.Add(named);
            else
                rules[index] = named;
            return (rules, null);
        });
    }

    internal Task<WriteResult> DeleteRuleAsync(string templateId, string ruleName, string? ifMatch, string? changedBy) =>
        MutateRulesAsync(templateId, ruleName, ifMatch, changedBy, rules =>
        {
            var index = rules.FindIndex(r => string.Equals(r.Name, ruleName, StringComparison.Ordinal));
            if (index < 0)
                return (null, WriteResult.Failed(WriteOutcome.NotFound, $"no rule '{ruleName}'"));
            rules.RemoveAt(index);
            return (rules, null);
        });

    private async Task<WriteResult> MutateRulesAsync(
        string templateId,
        string ruleName,
        string? ifMatch,
        string? changedBy,
        Func<List<DynamicTemplate>, (List<DynamicTemplate>? Rules, WriteResult? Failure)> mutate)
    {
        for (var attempt = 0; attempt < RuleWriteAttempts; attempt++)
        {
            var loaded = await repository.GetWithVersionAsync(templateId);
            if (loaded == null)
                return WriteResult.Failed(WriteOutcome.NotFound, $"no template '{templateId}'");

            var (document, version) = loaded.Value;
            var existing = document.Templates.FirstOrDefault(r => string.Equals(r.Name, ruleName, StringComparison.Ordinal));
            if (ifMatch != null && ifMatch != "*")
            {
                if (existing == null || RuleETagValue(existing) != ifMatch)
                    return WriteResult.Failed(WriteOutcome.PreconditionFailed);
            }
            else if (ifMatch == "*" && existing == null)
            {
                return WriteResult.Failed(WriteOutcome.PreconditionFailed);
            }

            var now = DateTime.UtcNow;
            var rules = document.Templates.Where(r => !r.IsExpired(now)).ToList();
            var (mutated, failure) = mutate(rules);
            if (failure != null)
                return failure;

            var updated = document with
            {
                Templates = [.. mutated!],
                ChangedBy = string.IsNullOrEmpty(changedBy) ? "Unknown" : changedBy,
            };

            var newVersion = await repository.SetIfVersionAsync(updated, templateId, version);
            if (newVersion == null)
                continue; // another writer landed in between; re-read and reapply (see D-15)

            await engine.InvalidateAsync(templateId);
            var stored = updated.Templates.FirstOrDefault(r => string.Equals(r.Name, ruleName, StringComparison.Ordinal));
            return new WriteResult(WriteOutcome.Ok, newVersion.Value, stored == null ? null : RuleETag(stored));
        }

        return WriteResult.Failed(WriteOutcome.Conflict);
    }

    private static Template Prune(Template template)
    {
        var now = DateTime.UtcNow;
        if (!Array.Exists(template.Templates, r => r.IsExpired(now)))
            return template;

        return template with { Templates = [.. template.Templates.Where(r => !r.IsExpired(now))] };
    }

    // The document's ETag is its version: a value both sides already have, and the one the version-key
    // mechanism from P-04 makes conditional writes cheap against.
    internal static string DocumentETag(long version) => Quote(version.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // A rule's own version: the first 16 hex characters of a SHA-256 over its serialized form. A rule does
    // not need its own counter - the content hash changes exactly when the rule does, which is the only
    // thing a rule-level If-Match has to detect.
    internal static string RuleETagValue(DynamicTemplate rule)
    {
        var json = JsonSerializer.Serialize(rule, AppJsonSerializerContext.GlobalDefault.DynamicTemplate);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
    }

    internal static string RuleETag(DynamicTemplate rule) => Quote(RuleETagValue(rule));

    private static string Quote(string value) => "\"" + value + "\"";

    // An If-Match value with its quotes and any weak marker stripped, or null when the header is absent.
    internal static string? NormalizeIfMatch(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return null;

        var value = header.Trim();
        if (value == "*")
            return "*";
        if (value.StartsWith("W/", StringComparison.Ordinal))
            value = value[2..];
        return value.Trim().Trim('"');
    }
}
