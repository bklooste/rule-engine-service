namespace Templates.Data;

// Bound from the "TemplateVersionCache" config section; controls the short-lived HybridCache
// check that lets a cached template follow a PUT without a per-request Redis round trip.
public sealed class TemplateVersionCacheOptions
{
    public const string SectionName = "TemplateVersionCache";

    // How long a pod trusts its last version check before re-reading Redis, in milliseconds.
    public int Duration { get; set; } = 5000;

    public TimeSpan Expiration => TimeSpan.FromMilliseconds(Duration <= 0 ? 1 : Duration);
}
