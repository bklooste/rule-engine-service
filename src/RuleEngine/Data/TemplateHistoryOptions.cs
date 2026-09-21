namespace Templates.Data;

// Bound from the "TemplateHistory" config section; controls how long immutable per-version
// document snapshots (P-13) are kept before prune-on-write deletes them.
public sealed class TemplateHistoryOptions
{
    public const string SectionName = "TemplateHistory";

    // How many days a version snapshot is kept before it is eligible for pruning. D-14 suggests
    // "last 50 or 90 days" - default to 90; services with high write volume can shorten this.
    public int RetentionDays { get; set; } = 90;

    public TimeSpan Retention => TimeSpan.FromDays(RetentionDays <= 0 ? 1 : RetentionDays);
}
