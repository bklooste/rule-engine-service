using Microsoft.Extensions.Options;

namespace Templates.Data;

/// <summary>
/// Bound from the "TemplateHistory" config section: how long a template id keeps its past. The top-level
/// values are the defaults; <see cref="Rules"/> overrides them per template id pattern, first match wins
/// (design 2.1, D4). Every property MUST appear in the README config table.
/// </summary>
public sealed class TemplateHistoryOptions
{
    public const string SectionName = "TemplateHistory";

    /// <summary>Days a version snapshot is kept before prune-on-write may delete it. 0 = keep every version.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>The newest this-many versions are always kept, whatever their age.</summary>
    public int MinVersions { get; set; } = 0;

    /// <summary>
    /// Days a rule stays stored after its <c>expiresAt</c> before a write drops it from the document.
    /// 0 = dropped by the first write after it ends (the 2.0 behaviour).
    /// </summary>
    public int ExpiredRuleRetentionDays { get; set; } = 0;

    /// <summary>Ordered per-id overrides; the first whose <see cref="TemplateHistoryRule.Pattern"/> matches wins.</summary>
    public List<TemplateHistoryRule> Rules { get; set; } = [];

    /// <summary>
    /// The values that apply to <paramref name="templateId"/>: those of the first matching rule, with each
    /// field the rule leaves unset taken from the top level (no merging across several matching rules).
    /// </summary>
    public TemplateHistoryPolicy Resolve(string templateId)
    {
        foreach (var rule in Rules)
        {
            if (!TemplateHistoryRule.Matches(rule.Pattern, templateId))
                continue;
            return new TemplateHistoryPolicy(
                rule.RetentionDays ?? RetentionDays,
                rule.MinVersions ?? MinVersions,
                rule.ExpiredRuleRetentionDays ?? ExpiredRuleRetentionDays);
        }
        return new TemplateHistoryPolicy(RetentionDays, MinVersions, ExpiredRuleRetentionDays);
    }
}

/// <summary>One per-pattern override in <see cref="TemplateHistoryOptions.Rules"/>. A null field takes the top-level value.</summary>
public sealed class TemplateHistoryRule
{
    /// <summary>Matched against the template id, ordinal and case-sensitive; <c>*</c> matches any run of characters.</summary>
    public string Pattern { get; set; } = "";

    public int? RetentionDays { get; set; }

    public int? MinVersions { get; set; }

    public int? ExpiredRuleRetentionDays { get; set; }

    // A glob with '*' as the only wildcard: greedy-with-backtrack over the last '*', linear in practice.
    internal static bool Matches(string pattern, string value)
    {
        int p = 0, v = 0, star = -1, mark = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (p < pattern.Length && pattern[p] == value[v])
            {
                p++;
                v++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}

/// <summary>The resolved history settings for one template id.</summary>
public readonly record struct TemplateHistoryPolicy(int RetentionDays, int MinVersions, int ExpiredRuleRetentionDays);

/// <summary>
/// Fail-fast validation, written by hand because DataAnnotations is not trim/AOT safe. A bad value stops the
/// service at start instead of silently keeping or dropping history.
/// </summary>
public sealed class TemplateHistoryOptionsValidator : IValidateOptions<TemplateHistoryOptions>
{
    public ValidateOptionsResult Validate(string? name, TemplateHistoryOptions o)
    {
        var errors = new List<string>();
        NotNegative(errors, "TemplateHistory:RetentionDays", o.RetentionDays);
        NotNegative(errors, "TemplateHistory:MinVersions", o.MinVersions);
        NotNegative(errors, "TemplateHistory:ExpiredRuleRetentionDays", o.ExpiredRuleRetentionDays);
        for (var i = 0; i < o.Rules.Count; i++)
        {
            var rule = o.Rules[i];
            if (string.IsNullOrEmpty(rule.Pattern))
                errors.Add($"TemplateHistory:Rules:{i}:Pattern must not be empty");
            NotNegative(errors, $"TemplateHistory:Rules:{i}:RetentionDays", rule.RetentionDays);
            NotNegative(errors, $"TemplateHistory:Rules:{i}:MinVersions", rule.MinVersions);
            NotNegative(errors, $"TemplateHistory:Rules:{i}:ExpiredRuleRetentionDays", rule.ExpiredRuleRetentionDays);
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void NotNegative(List<string> errors, string key, int? value)
    {
        if (value < 0)
            errors.Add($"{key} must be 0 or more (was {value})");
    }
}
