using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using Scenarify;

using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

using Templates;
using Templates.Data;

using static ServiceTests.TemplateScenarios;

namespace ServiceTests;

// Design 2.1, L1: a committed version always has its snapshot. One MULTI/EXEC carries the document, the
// counter, the snapshot, the version index and the version metadata, so there is no point between two
// writes at which a fault can leave a version without its history.
[Collection(nameof(ServiceTestCollection))]
[Trait("TestType", "Service")]
public class CommitTests(Fixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_commit_issues_no_write_outside_its_transaction()
    {
        var recorder = new CommandRecorder();
        var repository = NewRepository(recorder, out _);
        var id = NewId();

        var first = await repository.ReadAsync(id);
        (await repository.CommitAsync(id, Doc("first"), first.Version, ["g1"])).Should().Be(1);
        var second = await repository.ReadAsync(id);
        (await repository.CommitAsync(id, Doc("second"), second.Version, ["g1", "g2"])).Should().Be(2);

        recorder.Writes.Where(w => !w.Scope.StartsWith("transaction", StringComparison.Ordinal))
            .Should().BeEmpty("every write must be inside a transaction; a batch is pipelined, not atomic");

        // ...and the document, the counter, the snapshot and the history indexes of each version go in the
        // SAME transaction: 2.0 wrote the snapshot in a second one, after the version was already committed.
        foreach (var version in new[] { 1L, 2L })
        {
            var snapshotWrite = recorder.Writes.Single(w => w.Key == repository.GetVersionedKey(id, version) && w.Command == "StringSetAsync");
            recorder.Writes.Where(w => w.Scope == snapshotWrite.Scope).Select(w => w.Key).Should().BeEquivalentTo(
            [
                repository.GetKey(id), repository.GetVersionKey(id), Repository.IndexKey, repository.GetVersionedKey(id, version),
                repository.GetVersionsIndexKey(id), repository.GetMetaKey(id), .. Enumerable.Repeat(repository.GetGroupIndexKey(id), (int)version),
            ]);
        }
    }

    // A writer decides on the document it read and commits conditional on the counter it read, so the two
    // must come from one MULTI/EXEC: pipelined, another commit could land between them, and the write would
    // then overwrite that commit's change with its condition still holding (a lost update).
    [Fact]
    public async Task the_read_a_write_decides_on_is_one_transaction()
    {
        var recorder = new CommandRecorder();
        var repository = NewRepository(recorder, out _);
        var id = NewId();

        await repository.ReadAsync(id);

        var reads = recorder.Commands.Where(c => c.Key == repository.GetKey(id) || c.Key == repository.GetVersionKey(id)).ToList();
        reads.Should().HaveCount(2);
        reads.Select(c => c.Scope).Distinct().Should().ContainSingle().Which.Should().StartWith("transaction");
    }

    [Fact]
    public async Task a_commit_that_fails_before_exec_changes_nothing()
    {
        var recorder = new CommandRecorder();
        var repository = NewRepository(recorder, out var plain);
        var id = NewId();
        (await repository.CommitAsync(id, Doc("first"), 0, ["g1"])).Should().Be(1);
        var before = await SnapshotKeysAsync(plain, id, 2);

        recorder.BeforeExecute = () => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "injected before EXEC");
        var commit = () => repository.CommitAsync(id, Doc("second"), 1, ["g1", "g2"]);
        await commit.Should().ThrowAsync<RedisConnectionException>();

        (await SnapshotKeysAsync(plain, id, 2)).Should().BeEquivalentTo(before);
        (await plain.Database.KeyExistsAsync(plain.GetVersionedKey(id, 2))).Should().BeFalse();
    }

    // Several writers of every kind at once: whatever each was told, every version that exists has its
    // snapshot and its metadata, and every version number handed back to a caller is one of them.
    [Fact]
    public async Task every_version_returned_by_a_write_has_a_snapshot()
    {
        var id = NewId();
        var client = fixture.Client(userId: "writer");
        (await client.PutAsync($"v2/templates/{id}", Body("v1"), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var writes = Enumerable.Range(0, 16).Select(i => (i % 4) switch
        {
            0 => client.PutAsync($"v2/templates/{id}", Body($"put-{i}"), Ct),
            1 => client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"v2/templates/{id}") { Content = Body($"if-match-{i}"), Headers = { { "If-Match", "\"1\"" } } }, Ct),
            2 => client.PutAsync($"v2/templates/{id}/rules/rule-{i}", RuleBody($"rule-{i}"), Ct),
            _ => client.PostAsync($"v2/templates/{id}/rollback/1", null, Ct),
        }).ToArray();
        var responses = await Task.WhenAll(writes);

        var returned = new List<long>();
        foreach (var response in responses)
        {
            response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.PreconditionFailed, HttpStatusCode.Conflict);
            if (response.StatusCode == HttpStatusCode.OK)
                returned.Add((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt64());
        }

        var repository = fixture.Services.GetRequiredService<Repository>();
        var current = await repository.GetVersionAsync(id);
        for (var n = 1L; n <= current; n++)
        {
            (await repository.Database.KeyExistsAsync(repository.GetVersionedKey(id, n))).Should().BeTrue($"version {n} must have its snapshot");
            (await repository.Database.HashExistsAsync(repository.GetMetaKey(id), n)).Should().BeTrue($"version {n} must have its metadata");
        }
        returned.Should().OnlyHaveUniqueItems().And.OnlyContain(v => v >= 2 && v <= current);
    }

    [Fact]
    public async Task an_unconditional_put_retries_a_lost_race()
    {
        var recorder = new CommandRecorder();
        var repository = NewRepository(recorder, out var plain);
        var logic = NewLogic(repository);
        var id = NewId();
        (await plain.CommitAsync(id, Doc("first"), 0, [])).Should().Be(1);

        var raced = 0;
        recorder.BeforeExecute = async () =>
        {
            if (Interlocked.Increment(ref raced) == 1)
                await plain.CommitAsync(id, Doc("other writer"), 1, []);
        };

        var result = await logic.PutAsync(id, Doc("mine"), ifMatch: null, changedBy: "me");

        result.Outcome.Should().Be(WriteOutcome.Ok);
        result.Version.Should().Be(3);
        var stored = await plain.ReadAsync(id);
        stored.Document!.Templates[0].MatchFragment.GetProperty("response").GetString().Should().Be("mine");
    }

    [Fact]
    public async Task an_unconditional_put_returns_409_after_the_attempts()
    {
        var recorder = new CommandRecorder();
        var repository = NewRepository(recorder, out var plain);
        var logic = NewLogic(repository);
        var id = NewId();
        (await plain.CommitAsync(id, Doc("first"), 0, [])).Should().Be(1);

        var attempts = 0;
        recorder.BeforeExecute = async () =>
        {
            Interlocked.Increment(ref attempts);
            var now = await plain.ReadAsync(id);
            await plain.CommitAsync(id, Doc("other writer"), now.Version, []);
        };

        var result = await logic.PutAsync(id, Doc("mine"), ifMatch: null, changedBy: "me");

        result.Outcome.Should().Be(WriteOutcome.Conflict);
        attempts.Should().Be(4);
        (await plain.GetVersionDocumentAsync(id, await plain.GetVersionAsync(id)))!.Templates[0]
            .MatchFragment.GetProperty("response").GetString().Should().Be("other writer");
    }

    [Fact]
    public Task a_write_after_a_delete_continues_the_counter() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { response = "first" }))
            .Given(Http.Delete(V2TemplatePath))
            .When(Http.Put(V2TemplatePath, """{ "aggregationField": "code", "templates": [{ "name": "r1", "dataToMatch": { "eventId": "x" }, "matchFragment": { "response": "again" } }] }"""))
            .ThenBody("""{ "version": 3 }""")
            .Then(Http.Get("v2/templates/{{guid}}/versions").Satisfies<TemplateVersionSummary[]>(versions =>
                versions.Select(v => v.Version).Should().Equal(3)))
            .Then(Http.Get("v2/templates/{{guid}}/versions/3").Matches("""{ "templates": [{ "matchFragment": { "response": "again" } }] }"""))
            .RunAsync();

    // Design 2.1, bug 2: a rollback re-wrote the old snapshot as it was, so the new version claimed the
    // old version's author.
    [Fact]
    public Task a_rollback_records_its_caller_as_changed_by() =>
        fixture.Scenario()
            .Given(Http.Put(V2TemplatePath, """{ "aggregationField": "code", "templates": [{ "name": "r1", "dataToMatch": { "eventId": "x" }, "matchFragment": { "response": "first" } }] }""").AsUser("alice"))
            .Given(Http.Put(V2TemplatePath, """{ "aggregationField": "code", "templates": [{ "name": "r1", "dataToMatch": { "eventId": "x" }, "matchFragment": { "response": "second" } }] }""").AsUser("bob"))
            .When(Http.Post("v2/templates/{{guid}}/rollback/1").AsUser("carol"))
            .Then(Http.Get("v2/templates/{{guid}}/versions").Satisfies<TemplateVersionSummary[]>(versions =>
                versions.Select(v => (v.Version, v.ChangedBy)).Should().Equal((3L, "carol"), (2L, "bob"), (1L, "alice"))))
            .Then(Http.Get("v2/templates/{{guid}}/versions/3").Matches("""{ "changedBy": "carol", "templates": [{ "matchFragment": { "response": "first" } }] }"""))
            .RunAsync();

    private static string NewId() => "commit-" + Guid.NewGuid().ToString("N");

    // A repository over its own key prefix on the fixture's Redis, wrapped by the recorder; `plain` is the
    // same keys without the wrapper, for arranging races and inspecting what was stored.
    private Repository NewRepository(CommandRecorder recorder, out Repository plain)
    {
        var multiplexer = fixture.Services.GetRequiredService<ConnectionMultiplexer>();
        var prefix = $"test-commit-{Guid.NewGuid():N}:";
        plain = new Repository(() => multiplexer.GetDatabase().WithKeyPrefix(prefix), new TemplateHistoryOptions(), "{tenant}-templates");
        return new Repository(() => recorder.Wrap(multiplexer.GetDatabase().WithKeyPrefix(prefix)), new TemplateHistoryOptions(), "{tenant}-templates");
    }

    private V2Logic NewLogic(Repository repository) =>
        new(repository, fixture.Services.GetRequiredService<DynamicRulesEngine>(), Options.Create(new TemplateApiOptions()), TimeProvider.System, Options.Create(new TemplateHistoryOptions()));

    private static async Task<Dictionary<string, string?>> SnapshotKeysAsync(Repository repository, string id, long upTo)
    {
        var db = repository.Database;
        var result = new Dictionary<string, string?>
        {
            ["document"] = await db.StringGetAsync(repository.GetKey(id)),
            ["version"] = await db.StringGetAsync(repository.GetVersionKey(id)),
            ["versions"] = string.Join(",", await db.SortedSetRangeByRankAsync(repository.GetVersionsIndexKey(id))),
            ["vmeta"] = string.Join(",", (await db.HashGetAllAsync(repository.GetMetaKey(id))).Select(e => $"{e.Name}={e.Value}")),
            ["gidx"] = string.Join(",", await db.SortedSetRangeByRankAsync(repository.GetGroupIndexKey(id))),
        };
        for (var n = 1L; n <= upTo; n++)
            result[$"v{n}"] = await db.StringGetAsync(repository.GetVersionedKey(id, n));
        return result;
    }

    private static Template Doc(string response) =>
        new([new DynamicTemplate("r1", JsonDocument.Parse("""{ "eventId": "x" }""").RootElement, JsonDocument.Parse($$"""{ "response": "{{response}}" }""").RootElement)], "code");

    private static StringContent Body(string response) =>
        new($$"""{ "aggregationField": "code", "templates": [{ "name": "r1", "dataToMatch": { "eventId": "x" }, "matchFragment": { "response": "{{response}}" } }] }""", Encoding.UTF8, "application/json");

    private static StringContent RuleBody(string name) =>
        new($$"""{ "name": "{{name}}", "dataToMatch": { "eventId": "x" }, "matchFragment": { "{{name}}": true } }""", Encoding.UTF8, "application/json");
}
