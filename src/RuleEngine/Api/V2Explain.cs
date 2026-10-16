using System.Text.Json;
using System.Text.Json.Nodes;

using Templates.Compiled;
using Templates.Data;

namespace Templates.Api;

// A12: why a rule matched or did not. Deliberately a separate walk from ApplyV2Templates - explain is a
// debug endpoint, so it allocates a record per condition and a cumulative snapshot per matched rule
// rather than sharing the serving path's short-circuiting loop.
public record ExplainCondition(string field, string expression, bool required, string status, string reason);

public record ExplainToken(string key, string? objectKey, string path, bool resolved, string? value);

public record ExplainRule(string name, bool matched, string? note, ExplainCondition[] conditions, ExplainToken[] tokens, JsonObject? cumulativeResult);

public record ExplainResponse(string templateId, long version, ExplainRule[] rules, JsonObject result);

internal static class V2Explain
{
    internal const string Passed = "passed";
    internal const string Failed = "failed";
    internal const string Absent = "absent";
    internal const string Skipped = "skipped";
    internal const string NotEvaluated = "notEvaluated";
    internal const string Error = "error";

    internal static ExplainResponse Explain(string templateId, long version, CompiledV2Template compiled, JsonElement postedData)
    {
        var now = DateTime.UtcNow;
        var index = new EventPropertyIndex(postedData);
        var response = new JsonObject();
        var rules = new List<ExplainRule>(compiled.Rules.Count);

        foreach (var rule in compiled.Rules)
        {
            if (rule.Source.IsExpired(now))
            {
                rules.Add(new ExplainRule(rule.Name, false, $"expired: expiresAt {rule.ExpiresAt:o} has passed", [], [], null));
                continue;
            }

            var (matched, conditions) = ExplainConditions(rule, index);
            if (!matched)
            {
                rules.Add(new ExplainRule(rule.Name, false, null, conditions, [], null));
                continue;
            }

            var tokens = ExplainTokens(rule, postedData);
            CompiledTransform.MergeFragment(response, rule.Fragment, postedData, compiled.AggregationField, null);
            rules.Add(new ExplainRule(rule.Name, true, null, conditions, tokens, response.DeepClone().AsObject()));
        }

        return new ExplainResponse(templateId, version, [.. rules], response);
    }

    // Stops at the first failing (or missing-and-required) condition, exactly as Matches() does, and
    // reports the rest as not evaluated - so the output names the one condition that decided the rule.
    // D-11 (revised): a condition on a field the event does not carry is skipped by default (loose), not
    // failed - matching v1. It only fails the rule if explicitly marked required. As in Matches(), the
    // rule needs at least one condition to actually evaluate (present) to match at all.
    private static (bool Matched, ExplainCondition[] Conditions) ExplainConditions(CompiledV2Rule rule, EventPropertyIndex index)
    {
        var results = new ExplainCondition[rule.Conditions.Count];
        var failed = false;
        var haveMatch = false;
        for (var i = 0; i < rule.Conditions.Count; i++)
        {
            var condition = rule.Conditions[i];
            var field = condition.Condition.PropertyName;
            if (failed)
            {
                results[i] = new ExplainCondition(field, condition.Expression, condition.Required, NotEvaluated, "an earlier condition already failed the rule");
                continue;
            }

            JsonElement value;
            try
            {
                if (!index.TryGetProperty(field, out value))
                {
                    if (!condition.Required)
                    {
                        results[i] = new ExplainCondition(field, condition.Expression, false, Skipped, $"'{field}' is absent, and the condition is not required (v2 evaluates loosely by default)");
                        continue;
                    }

                    failed = true;
                    results[i] = new ExplainCondition(field, condition.Expression, true, Absent, $"'{field}' is absent from the event, and the condition is required");
                    continue;
                }
            }
            catch (Exception ex)
            {
                failed = true;
                results[i] = new ExplainCondition(field, condition.Expression, condition.Required, Error, ex.Message);
                continue;
            }

            try
            {
                if (condition.Condition.Evaluate(value))
                {
                    haveMatch = true;
                    results[i] = new ExplainCondition(field, condition.Expression, condition.Required, Passed, $"'{field}' = {Describe(value)} satisfies {condition.Expression}");
                }
                else
                {
                    failed = true;
                    results[i] = new ExplainCondition(field, condition.Expression, condition.Required, Failed, $"'{field}' = {Describe(value)} does not satisfy {condition.Expression}");
                }
            }
            catch (Exception ex)
            {
                failed = true;
                results[i] = new ExplainCondition(field, condition.Expression, condition.Required, Error, ex.Message);
            }
        }

        var matched = !failed && (rule.Conditions.Count == 0 || haveMatch);
        return (matched, results);
    }

    private static ExplainToken[] ExplainTokens(CompiledV2Rule rule, JsonElement postedData)
    {
        var tokens = rule.Fragment.Tokens;
        var results = new ExplainToken[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var found = token.IsRoot ? postedData : GetFromPath(postedData, token.PathSegments);
            results[i] = found == null
                ? new ExplainToken(token.Key, token.ObjectKey, token.Path, false, null)
                : new ExplainToken(token.Key, token.ObjectKey, token.Path, true, Truncate(found.Value.GetRawText()));
        }
        return results;
    }

    private static JsonElement? GetFromPath(JsonElement element, string[] segments)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        foreach (var segment in segments)
        {
            element = element.TryGetProperty(segment, out var value) ? value : default;
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
        }
        return element;
    }

    private static string Describe(in JsonElement value) => Truncate(value.GetRawText());

    private static string Truncate(string text) => text.Length > 200 ? text[..200] + "..." : text;
}
