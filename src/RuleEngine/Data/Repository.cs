using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

using Templates.Data;

namespace Templates;

// The current document (null when there is none) and the version counter, read together. The counter
// survives a delete, so Version can be non-zero with no Document; 0 means the id was never written.
internal readonly record struct StoredTemplate(Template? Document, long Version);

internal class Repository
{
    private const string RedisConnectionString = "ConnectionStrings:Redis";
    private readonly ILogger<Repository> logger;

    private readonly Func<IDatabase> dbFunc;
    private readonly AppJsonSerializerContext context;
    private readonly TemplateHistoryOptions historyOptions;
    private readonly string tenantKeyFormat;
    private readonly TimeProvider timeProvider;

    public Repository(ConnectionMultiplexer connectionMultiplexer, IConfiguration config, ILoggerFactory logFact,
        IOptions<TemplateHistoryOptions> historyOptions, IOptions<TemplatesOptions> templatesOptions, TimeProvider timeProvider)
        : this(Prefixed(connectionMultiplexer, config, templatesOptions.Value), historyOptions.Value,
            templatesOptions.Value.TenantKeyFormat, timeProvider, logFact.CreateLogger<Repository>())
    {
    }

    // Design 2.1, "Tests, per step": a test passes its own database factory so it can wrap the database
    // and see every command the repository sends, and its own clock so retention can be tested.
    internal Repository(Func<IDatabase> dbFunc, TemplateHistoryOptions historyOptions, string tenantKeyFormat,
        TimeProvider? timeProvider = null, ILogger<Repository>? logger = null)
    {
        this.dbFunc = dbFunc;
        this.historyOptions = historyOptions;
        this.tenantKeyFormat = tenantKeyFormat;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<Repository>.Instance;
        this.context = AppJsonSerializerContext.GlobalDefault;
    }

    private static Func<IDatabase> Prefixed(ConnectionMultiplexer connectionMultiplexer, IConfiguration config, TemplatesOptions options)
    {
        var environment = config["ENVIRONMENT"] ?? "local";
        var prefix = string.IsNullOrEmpty(options.KeyPrefix) ? $"{environment}:plat-templates:" : options.KeyPrefix;
        return () => connectionMultiplexer.GetDatabase().WithKeyPrefix(prefix);
    }

    internal static ConnectionMultiplexer CreateClient(ConfigurationManager config)
    {
        return ConnectionMultiplexer.Connect(config[RedisConnectionString] ?? throw new InvalidOperationException("No Redis connection string found"));
    }

    // Design 2.1, D1: the one write primitive. Every write path reads the stored document and its version,
    // decides, and commits here conditional on that version. Because the commit only lands when nothing
    // moved the counter since the read, the new version is exactly observedVersion + 1, so its snapshot
    // key is known before MULTI and the document, the counter, the snapshot, the version index, the
    // version metadata and the group index entries all go in ONE transaction: there is no point between
    // two writes at which a crash leaves a committed version without its history.
    //
    // Returns null when the version moved since it was read; the caller re-reads and retries.
    internal async Task<long?> CommitAsync(string templateId, Template next, long observedVersion, IReadOnlyCollection<string> changedGroups)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var document = next with { UpdatedAt = now };
        var version = observedVersion + 1;
        var json = JsonSerializer.Serialize(document, context.Template);
        var meta = JsonSerializer.Serialize(new VersionMeta(now, document.ChangedBy, [.. changedGroups]), context.VersionMeta);

        var db = this.dbFunc();
        var transaction = db.CreateTransaction();
        // A deleted document keeps (and bumps) its counter, so observedVersion 0 can only mean "never
        // written", and then the counter must still not exist.
        transaction.AddCondition(observedVersion <= 0
            ? Condition.KeyNotExists(GetVersionKey(templateId))
            : Condition.StringEqual(GetVersionKey(templateId), observedVersion));

        var queued = new List<Task>(7 + changedGroups.Count);
        queued.Add(transaction.StringSetAsync(GetKey(templateId), json));
        var versionTask = transaction.StringIncrementAsync(GetVersionKey(templateId));
        queued.Add(versionTask);
        // P-14 (A11): the id index the v2 list endpoint reads. A SET maintained on write beats SCANning
        // a Redis instance several environments share.
        queued.Add(transaction.SetAddAsync(IndexKey, templateId));
        queued.Add(transaction.StringSetAsync(GetVersionedKey(templateId, version), json));
        queued.Add(transaction.SortedSetAddAsync(GetVersionsIndexKey(templateId), version, ToUnixSeconds(now)));
        queued.Add(transaction.HashSetAsync(GetMetaKey(templateId), version, meta));
        foreach (var group in changedGroups)
            queued.Add(transaction.SortedSetAddAsync(GetGroupIndexKey(templateId), GroupIndexMember(group, version), 0));

        if (!await transaction.ExecuteAsync())
            return null;

        // Post-Execute: StackExchange.Redis only resolves queued command tasks once the transaction commits.
        await Task.WhenAll(queued);
        if (versionTask.Result != version)
            throw new InvalidDataException($"CRITICAL commit of {templateId} expected version {version} but the counter is {versionTask.Result}");

        // Pruning only ever removes history, so it runs after the commit, in its own transaction: a prune
        // that fails or never runs leaves more history, never less (D1).
        try
        {
            await PruneAsync(db, templateId, now);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "pruning the history of {TemplateId} failed; it is retried on the next write", templateId);
        }
        return version;
    }

    internal async Task<StoredTemplate> ReadAsync(string templateId)
    {
        var db = this.dbFunc();
        var batch = db.CreateBatch();
        var documentTask = batch.StringGetLeaseAsync(GetKey(templateId));
        var versionTask = batch.StringGetAsync(GetVersionKey(templateId));
        batch.Execute();
        using var lease = await documentTask;
        var versionValue = await versionTask;
        var version = versionValue.HasValue ? (long)versionValue : 0;
        var template = lease == null ? null : JsonSerializer.Deserialize(lease.Span, context.Template);
        return new StoredTemplate(template, version);
    }

    // The current document and the version it is at, read together so a v2 response's ETag and body
    // always describe the same write.
    internal async Task<(Template Template, long Version)?> GetWithVersionAsync(string templateName)
    {
        var stored = await ReadAsync(templateName);
        return stored.Document == null ? null : (stored.Document, stored.Version);
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
    //
    // Conditional on the counter it read, like a commit: a write landing between the read of the version
    // list and the delete would otherwise leave its snapshot behind with nothing pointing at it.
    internal async Task<bool> DeleteAsync(string templateName)
    {
        var db = this.dbFunc();
        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            var counter = await db.StringGetAsync(GetVersionKey(templateName));
            var versions = await db.SortedSetRangeByScoreAsync(GetVersionsIndexKey(templateName));
            var versionKeys = versions.Select(v => (RedisKey)GetVersionedKey(templateName, (long)v)).ToArray();

            var transaction = db.CreateTransaction();
            transaction.AddCondition(counter.HasValue
                ? Condition.StringEqual(GetVersionKey(templateName), counter)
                : Condition.KeyNotExists(GetVersionKey(templateName)));
            var deleted = transaction.KeyDeleteAsync(GetKey(templateName));
            _ = transaction.KeyDeleteAsync([GetVersionsIndexKey(templateName), GetMetaKey(templateName), GetGroupIndexKey(templateName)]);
            if (versionKeys.Length > 0)
                _ = transaction.KeyDeleteAsync(versionKeys);
            _ = transaction.StringIncrementAsync(GetVersionKey(templateName));
            _ = transaction.SetRemoveAsync(IndexKey, templateName);
            if (await transaction.ExecuteAsync())
                return await deleted;
        }
        throw new InvalidDataException($"{templateName} changed on every one of {DeleteAttempts} delete attempts");
    }

    private const int DeleteAttempts = 8;

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

    // Retained versions, most recent first: at most `limit`, and only those below `before` when given.
    // The summaries come from K:vmeta; a version written before 2.1 has no field there, and only those
    // are read from their snapshot.
    internal async Task<List<TemplateVersionSummary>> ListVersionsAsync(string templateName, int limit = int.MaxValue, long? before = null)
    {
        var db = this.dbFunc();
        var members = await db.SortedSetRangeByRankAsync(GetVersionsIndexKey(templateName));
        var versions = members.Select(m => (long)m).Where(v => before == null || v < before).OrderDescending().Take(limit).ToArray();
        return await SummariesAsync(db, templateName, versions, legacyFallback: true);
    }

    // Design 2.1, D3: the versions that changed a group, newest first, from K:gidx, without reading any
    // snapshot. A version whose snapshot is gone (pruned by a 2.0.x pod, which leaves K:gidx alone) is
    // skipped, so a listed version can always be fetched.
    internal async Task<List<TemplateVersionSummary>> ListGroupVersionsAsync(string templateId, string group, int limit, long? before)
    {
        var db = this.dbFunc();
        var max = before is { } b ? (RedisValue)GroupIndexMember(group, b) : (RedisValue)(group + "\u0001");
        var members = await db.SortedSetRangeByValueAsync(GetGroupIndexKey(templateId), group + "\0", max, Exclude.Stop, Order.Descending, 0, limit);
        if (members.Length == 0) return [];

        var versions = members.Select(m => long.Parse(((string)m!).AsSpan(group.Length + 1), CultureInfo.InvariantCulture)).ToArray();
        var scores = await db.SortedSetScoresAsync(GetVersionsIndexKey(templateId), [.. versions.Select(v => (RedisValue)v)]);
        versions = [.. versions.Where((_, i) => scores[i].HasValue)];
        return await SummariesAsync(db, templateId, versions, legacyFallback: false);
    }

    private async Task<List<TemplateVersionSummary>> SummariesAsync(IDatabase db, string templateName, long[] versions, bool legacyFallback)
    {
        if (versions.Length == 0) return [];
        var metas = await db.HashGetAsync(GetMetaKey(templateName), [.. versions.Select(v => (RedisValue)v)]);

        var summaries = new TemplateVersionSummary?[versions.Length];
        var legacy = new List<int>();
        for (var i = 0; i < versions.Length; i++)
        {
            var meta = metas[i].HasValue ? JsonSerializer.Deserialize(((byte[])metas[i]!).AsSpan(), context.VersionMeta) : null;
            if (meta == null)
                legacy.Add(i);
            else
                summaries[i] = new TemplateVersionSummary(versions[i], meta.UpdatedAt, meta.ChangedBy, meta.Groups);
        }

        if (legacyFallback && legacy.Count > 0)
        {
            var docs = await db.StringGetAsync([.. legacy.Select(i => (RedisKey)GetVersionedKey(templateName, versions[i]))]);
            for (var j = 0; j < legacy.Count; j++)
            {
                if (!docs[j].HasValue) continue; // pruned between the ZRANGE and the MGET
                var doc = JsonSerializer.Deserialize(((byte[])docs[j]!).AsSpan(), context.Template);
                if (doc != null)
                    summaries[legacy[j]] = new TemplateVersionSummary(versions[legacy[j]], doc.UpdatedAt, doc.ChangedBy);
            }
        }

        return [.. summaries.OfType<TemplateVersionSummary>()];
    }

    // Prune-on-write: every write already touches the document, so versions that fall out of the window
    // are deleted here rather than by a background job. Design 2.1, D4, for the id's resolved
    // (RetentionDays, MinVersions): RetentionDays 0 keeps everything; otherwise a version is kept if it
    // was written at or after the cutoff, OR it is the highest-numbered version written before the cutoff
    // (the version in force when the window opened, so every moment inside the window has its version;
    // 2.0 deleted it), OR it is one of the MinVersions highest-numbered versions. "Highest" is by version
    // number, not by write time, so clock skew between pods cannot pick the wrong one.
    //
    // Each pruned version takes its snapshot, its K:versions member, its K:vmeta field and its K:gidx
    // members with it in one transaction, so the indexes and the snapshots never disagree.
    private async Task PruneAsync(IDatabase db, string templateId, DateTime now)
    {
        var policy = historyOptions.Resolve(templateId);
        if (policy.RetentionDays == 0) return;

        var entries = await db.SortedSetRangeByRankWithScoresAsync(GetVersionsIndexKey(templateId));
        var expired = SelectPruned(entries.Select(e => ((long)e.Element, e.Score)), ToUnixSeconds(now) - (long)policy.RetentionDays * 86400, policy.MinVersions);
        if (expired.Length == 0) return;

        var members = expired.Select(v => (RedisValue)v).ToArray();
        var metas = await db.HashGetAsync(GetMetaKey(templateId), members);
        var groupMembers = new List<RedisValue>();
        for (var i = 0; i < expired.Length; i++)
        {
            if (!metas[i].HasValue) continue;
            var meta = JsonSerializer.Deserialize(((byte[])metas[i]!).AsSpan(), context.VersionMeta);
            foreach (var group in meta?.Groups ?? [])
                groupMembers.Add(GroupIndexMember(group, expired[i]));
        }

        var transaction = db.CreateTransaction();
        _ = transaction.KeyDeleteAsync([.. expired.Select(v => (RedisKey)GetVersionedKey(templateId, v))]);
        _ = transaction.SortedSetRemoveAsync(GetVersionsIndexKey(templateId), members);
        _ = transaction.HashDeleteAsync(GetMetaKey(templateId), members);
        if (groupMembers.Count > 0)
            _ = transaction.SortedSetRemoveAsync(GetGroupIndexKey(templateId), [.. groupMembers]);
        await transaction.ExecuteAsync();
    }

    // The versions D4 prunes from (version, write time in unix seconds) pairs.
    internal static long[] SelectPruned(IEnumerable<(long Version, double Written)> versions, long cutoff, int minVersions)
    {
        var newestFirst = versions.OrderByDescending(v => v.Version).ToArray();
        var inForceAtCutoff = Array.Find(newestFirst, v => v.Written < cutoff).Version; // 0 when none
        return [.. newestFirst
            .Skip(minVersions)
            .Where(v => v.Written < cutoff && v.Version != inForceAtCutoff)
            .Select(v => v.Version)];
    }

    private static long ToUnixSeconds(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    // D3: "<group>\0<version, 19 digits>". \0 sorts below every byte a validated group name can hold, so
    // one group's members form one lexical range, and the padding makes lexical order version order.
    internal static string GroupIndexMember(string group, long version) =>
        group + "\0" + version.ToString("D19", CultureInfo.InvariantCulture);

    // The keys, relative to the prefix, so a test can inspect them through the same database.
    internal IDatabase Database => dbFunc();
    internal string GetKey(string brand) => tenantKeyFormat.Replace("{tenant}", brand);
    internal string GetVersionKey(string brand) => GetKey(brand) + ":version";
    internal string GetVersionedKey(string brand, long version) => $"{GetKey(brand)}:v{version}";
    internal string GetVersionsIndexKey(string brand) => GetKey(brand) + ":versions";
    internal string GetMetaKey(string brand) => GetKey(brand) + ":vmeta";
    internal string GetGroupIndexKey(string brand) => GetKey(brand) + ":gidx";
    internal const string IndexKey = "templates:index";
}
