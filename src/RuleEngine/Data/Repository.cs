using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.Options;

using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

using Templates.Data;

namespace Templates;

internal class Repository
{
    private const string RedisConnectionString = "ConnectionStrings:Redis";
    private readonly ILogger<Repository> logger;

    private readonly Func<IDatabase> dbFunc;
    private readonly AppJsonSerializerContext context;
    private readonly TemplateHistoryOptions historyOptions;
    private readonly string tenantKeyFormat;

    public Repository(ConnectionMultiplexer connectionMultiplexer, IConfiguration config, ILoggerFactory logFact,
        IOptions<TemplateHistoryOptions> historyOptions, IOptions<TemplatesOptions> templatesOptions)
    {
        var environment = config["ENVIRONMENT"] ?? "local";
        var configured = templatesOptions.Value.KeyPrefix;
        var prefix = string.IsNullOrEmpty(configured) ? $"{environment}:plat-templates:" : configured;
        this.tenantKeyFormat = templatesOptions.Value.TenantKeyFormat;
        this.logger = logFact.CreateLogger<Repository>();
        this.dbFunc = () => connectionMultiplexer.GetDatabase().WithKeyPrefix(prefix);
        this.context = AppJsonSerializerContext.GlobalDefault;
        this.historyOptions = historyOptions.Value;
    }

    internal static ConnectionMultiplexer CreateClient(ConfigurationManager config)
    {
        // 
        return ConnectionMultiplexer.Connect(config[RedisConnectionString] ?? throw new InvalidOperationException("No Redis connection string found"));
    }

    // Bumped in the same transaction as the write below so a reader's version check (DynamicRulesEngine,
    // via HybridCache) can never see a version ahead of the template content it points at.
    internal async Task<long> SetAsync(Template template, string brand)
    {
        template.UpdatedAt = DateTime.UtcNow;

        var db = this.dbFunc();
        var json = JsonSerializer.Serialize(template, context.Template);

        var transaction = db.CreateTransaction();
        var setTask = transaction.StringSetAsync(GetKey(brand), json);
        var versionTask = transaction.StringIncrementAsync(GetVersionKey(brand));
        // P-14 (A11): the id index the v2 list endpoint reads. A SET maintained on write beats SCANning
        // a Redis instance several environments share - no cross-env key walk, no pattern that has to
        // exclude the ":version"/":versions"/":v{n}" siblings.
        var indexTask = transaction.SetAddAsync(IndexKey, brand);
        var committed = await transaction.ExecuteAsync();
        if (!committed)
            throw new InvalidDataException("could not create");

        // Post-Execute: StackExchange.Redis only resolves queued command tasks once the transaction commits.
        await Task.WhenAll(setTask, versionTask, indexTask);
        var newVersion = versionTask.Result;

        // History (P-13): the current pointer above is what GetDynamicAsync reads; this persists an
        // immutable per-version snapshot on the side so it never gets lost when a later PUT overwrites
        // the current key. Deliberately a second round trip (not part of the transaction above) - the
        // current-pointer/version-counter atomicity is what readers depend on, this is best-effort history.
        await SaveVersionSnapshotAsync(db, brand, newVersion, json, template.UpdatedAt);
        return newVersion;
    }

    // P-14 (A8): the If-Match half of the optimistic-concurrency pair. The version-key compare rides in
    // the transaction itself, so a writer that lost the race never overwrites - unlike a read-compare-write
    // in the API, which has a window between the two.
    internal async Task<long?> SetIfVersionAsync(Template template, string brand, long expectedVersion)
    {
        template.UpdatedAt = DateTime.UtcNow;

        var db = this.dbFunc();
        var json = JsonSerializer.Serialize(template, context.Template);

        var transaction = db.CreateTransaction();
        transaction.AddCondition(expectedVersion <= 0
            ? Condition.KeyNotExists(GetVersionKey(brand))
            : Condition.StringEqual(GetVersionKey(brand), expectedVersion));
        var setTask = transaction.StringSetAsync(GetKey(brand), json);
        var versionTask = transaction.StringIncrementAsync(GetVersionKey(brand));
        var indexTask = transaction.SetAddAsync(IndexKey, brand);
        if (!await transaction.ExecuteAsync())
            return null;

        await Task.WhenAll(setTask, versionTask, indexTask);
        var newVersion = versionTask.Result;
        await SaveVersionSnapshotAsync(db, brand, newVersion, json, template.UpdatedAt);
        return newVersion;
    }

    // The current document and the version it is at, read together so a v2 response's ETag and body
    // always describe the same write.
    internal async Task<(Template Template, long Version)?> GetWithVersionAsync(string templateName)
    {
        var db = this.dbFunc();
        var batch = db.CreateBatch();
        var documentTask = batch.StringGetLeaseAsync(GetKey(templateName));
        var versionTask = batch.StringGetAsync(GetVersionKey(templateName));
        batch.Execute();
        var lease = await documentTask;
        var versionValue = await versionTask;
        if (lease == null) return null;

        var template = JsonSerializer.Deserialize(lease!.Span, context.Template);
        if (template == null) return null;
        return (template, versionValue.HasValue ? (long)versionValue : 0);
    }

    // P-14 (A11): every template id this service has written. Deletes take theirs back out.
    internal async Task<string[]> ListTemplateIdsAsync()
    {
        var db = this.dbFunc();
        var members = await db.SetMembersAsync(IndexKey);
        var ids = members.Select(m => (string)m!).Where(id => !string.IsNullOrEmpty(id)).ToArray();
        Array.Sort(ids, StringComparer.Ordinal);
        return ids;
    }

    // A11: removes the current document, its history and its index entry. The version counter is kept and
    // bumped instead of deleted, so every pod's cached copy misses on its next version check and a template
    // recreated under the same id can never collide with a stale cache entry at the same number.
    internal async Task<bool> DeleteAsync(string templateName)
    {
        var db = this.dbFunc();
        var versions = await db.SortedSetRangeByScoreAsync(GetVersionsIndexKey(templateName));
        var versionKeys = versions.Select(v => (RedisKey)GetVersionedKey(templateName, (long)v)).ToArray();

        var transaction = db.CreateTransaction();
        var deleted = transaction.KeyDeleteAsync(GetKey(templateName));
        _ = transaction.KeyDeleteAsync(GetVersionsIndexKey(templateName));
        if (versionKeys.Length > 0)
            _ = transaction.KeyDeleteAsync(versionKeys);
        _ = transaction.StringIncrementAsync(GetVersionKey(templateName));
        _ = transaction.SetRemoveAsync(IndexKey, templateName);
        if (!await transaction.ExecuteAsync())
            return false;

        return await deleted;
    }

    internal async Task<Template?> GetDynamicAsync(string templateName)
    {
        var stopWatch = Stopwatch.StartNew();
        var db = this.dbFunc();
        var lease = await db.StringGetLeaseAsync(GetKey(templateName));
        if (lease == null) return null;
        var result = JsonSerializer.Deserialize(lease!.Span, context.Template);

        logger.LogInformation("get template for {templateName} took {Elapsed} ms ", templateName, stopWatch.ElapsedMilliseconds);
        return result ?? throw new InvalidDataException($"CRITICAL no templates found for {templateName}");
    }

    // The template's current version, or 0 when it has never been written.
    internal async Task<long> GetVersionAsync(string templateName)
    {
        var db = this.dbFunc();
        var value = await db.StringGetAsync(GetVersionKey(templateName));
        return value.HasValue ? (long)value : 0;
    }

    // A specific historical version's full document, or null if it never existed or has been pruned.
    internal async Task<Template?> GetVersionDocumentAsync(string templateName, long version)
    {
        var db = this.dbFunc();
        var lease = await db.StringGetLeaseAsync(GetVersionedKey(templateName, version));
        if (lease == null) return null;
        return JsonSerializer.Deserialize(lease!.Span, context.Template);
    }

    // Every retained version, most recent first.
    internal async Task<List<TemplateVersionSummary>> ListVersionsAsync(string templateName)
    {
        var db = this.dbFunc();
        var members = await db.SortedSetRangeByScoreAsync(GetVersionsIndexKey(templateName), order: Order.Descending);
        if (members.Length == 0) return [];

        var versions = members.Select(m => (long)m).ToArray();
        var keys = versions.Select(v => (RedisKey)GetVersionedKey(templateName, v)).ToArray();
        var docs = await db.StringGetAsync(keys);

        var result = new List<TemplateVersionSummary>(versions.Length);
        for (var i = 0; i < versions.Length; i++)
        {
            if (!docs[i].HasValue) continue; // pruned between the ZRANGE and the MGET
            var doc = JsonSerializer.Deserialize(((byte[])docs[i]!).AsSpan(), context.Template);
            if (doc != null)
                result.Add(new TemplateVersionSummary(versions[i], doc.UpdatedAt, doc.ChangedBy));
        }
        return result;
    }

    // Rollback is a new write, not a rewind (D-14: versions are immutable, linear history). Restores
    // the target version's content as a brand new current version and returns the new version number.
    internal async Task<long?> RollbackAsync(string templateName, long version)
    {
        var old = await GetVersionDocumentAsync(templateName, version);
        if (old == null) return null;
        return await SetAsync(old, templateName);
    }

    private async Task SaveVersionSnapshotAsync(IDatabase db, string brand, long version, string json, DateTime updatedAt)
    {
        var score = new DateTimeOffset(DateTime.SpecifyKind(updatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds();

        var transaction = db.CreateTransaction();
        var setTask = transaction.StringSetAsync(GetVersionedKey(brand, version), json);
        var indexTask = transaction.SortedSetAddAsync(GetVersionsIndexKey(brand), version.ToString(), score);
        var committed = await transaction.ExecuteAsync();
        if (!committed)
            throw new InvalidDataException("could not save version history");
        await Task.WhenAll(setTask, indexTask);

        await PruneOldVersionsAsync(db, brand, score);
    }

    // Prune-on-write: simpler and correct enough for retention that's measured in days, so a background
    // job would be over-engineering - every PUT/rollback already touches this key, and stale versions
    // that fall out of the window are deleted here rather than left to expire on their own.
    private async Task PruneOldVersionsAsync(IDatabase db, string brand, long nowUnixSeconds)
    {
        var cutoff = nowUnixSeconds - (long)historyOptions.Retention.TotalSeconds;
        var expired = await db.SortedSetRangeByScoreAsync(GetVersionsIndexKey(brand), double.NegativeInfinity, cutoff);
        if (expired.Length == 0) return;

        var expiredKeys = expired.Select(v => (RedisKey)GetVersionedKey(brand, (long)v)).ToArray();
        var transaction = db.CreateTransaction();
        _ = transaction.KeyDeleteAsync(expiredKeys);
        _ = transaction.SortedSetRemoveAsync(GetVersionsIndexKey(brand), expired);
        await transaction.ExecuteAsync();
    }

    private string GetKey(string brand) => tenantKeyFormat.Replace("{tenant}", brand);
    private string GetVersionKey(string brand) => GetKey(brand) + ":version";
    private string GetVersionedKey(string brand, long version) => $"{GetKey(brand)}:v{version}";
    private string GetVersionsIndexKey(string brand) => GetKey(brand) + ":versions";
    private const string IndexKey = "templates:index";
}
