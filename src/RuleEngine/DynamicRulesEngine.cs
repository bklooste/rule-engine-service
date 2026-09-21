using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using Templates.Compiled;
using Templates.Data;

namespace Templates;


internal class DynamicRulesEngine(Repository repository, ILoggerFactory loggerFact, HybridCache versionCache, IOptions<TemplateVersionCacheOptions> versionCacheOptions)
{
    // Compiled templates keyed by id, each entry remembering the Redis version it was loaded at. A long
    // backstop expiry just bounds memory for ids that stop being used; freshness itself is driven
    // entirely by the version check below, not by this expiry.
    readonly IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions(), loggerFact);
    readonly TemplateVersionCacheOptions versionCacheOptions = versionCacheOptions.Value;

    private HybridCacheEntryOptions VersionCacheEntryOptions => new()
    {
        Expiration = versionCacheOptions.Expiration,
        LocalCacheExpiration = versionCacheOptions.Expiration,
        // Memory only: plat-templates has no L2 configured for HybridCache, this just bounds the
        // per-pod Redis version-key reads to once per Expiration window.
        Flags = HybridCacheEntryFlags.DisableDistributedCache,
    };

    // Cached as a string: HybridCache ships built-in (non-reflection) serializers for string and
    // byte[], so this stays safe under this service's NativeAOT publish without a custom
    // IHybridCacheSerializer<T> (see StatusSnapshotCacheSerializer in offer-events for the pattern
    // when the cached value is a complex type).
    private ValueTask<string> GetVersionStringAsync(string template, CancellationToken ct) =>
        versionCache.GetOrCreateAsync(
            $"templates-version:{template}",
            (repository, template),
            static async (state, token) => (await state.repository.GetVersionAsync(state.template)).ToString(CultureInfo.InvariantCulture),
            VersionCacheEntryOptions,
            cancellationToken: ct);

    // The compiled form is built once per (id, version) (D-2) and shared by every request that sees that
    // version. The version comes back alongside, because every v2 response reports it (A5).
    private readonly record struct CachedV2Template(long Version, CompiledV2Template Compiled);

    internal async Task<(CompiledV2Template Compiled, long Version)?> LoadV2Async(string template, CancellationToken ct = default)
    {
        var version = long.Parse(await GetVersionStringAsync(template, ct), CultureInfo.InvariantCulture);
        var key = "v2:" + template;
        if (memoryCache.TryGetValue(key, out CachedV2Template cached) && cached.Version == version)
            return (cached.Compiled, cached.Version);

        var loaded = await repository.GetDynamicAsync(template);
        if (loaded == null)
            return null;

        var compiled = CompiledV2Template.Compile(loaded);
        memoryCache.Set(key, new CachedV2Template(version, compiled), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
        return (compiled, version);
    }

    // A v2 write knows the version it produced, so the pod that served the write skips the version-check
    // window entirely instead of waiting it out. Other pods still converge through that check (D-4).
    internal async Task InvalidateAsync(string template)
    {
        memoryCache.Remove("v2:" + template);
        await versionCache.RemoveAsync($"templates-version:{template}");
    }

    // P-20: the v2 serving path, producing the P-07 merge plan the endpoint writes straight to the response
    // body. Nothing is cloned and no JsonObject tree is built - see Compiled/MergePlan.cs.
    internal static MergePlan ApplyV2TemplatesToPlan(CompiledV2Template compiled, JsonElement postedData, DateTime utcNow)
    {
        var plan = new MergePlan();
        var index = new EventPropertyIndex(postedData);
        foreach (var rule in compiled.Rules)
        {
            if (rule.Source.IsExpired(utcNow))
                continue;
            if (!rule.Matches(index))
                continue;

            CompiledTransform.MergeFragment(plan, rule.Fragment, postedData, compiled.AggregationField, null);
        }
        return plan;
    }

    // The same v2 serving walk as a JsonObject. Explain and the engine unit tests need a real node tree;
    // the evaluate endpoints take the plan form above.
    internal static JsonObject ApplyV2Templates(CompiledV2Template compiled, JsonElement postedData, DateTime utcNow)
    {
        var response = new JsonObject();
        var index = new EventPropertyIndex(postedData);
        foreach (var rule in compiled.Rules)
        {
            if (rule.Source.IsExpired(utcNow))
                continue;
            if (!rule.Matches(index))
                continue;

            CompiledTransform.MergeFragment(response, rule.Fragment, postedData, compiled.AggregationField, null);
        }
        return response;
    }

}
