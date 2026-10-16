using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

using Templates;
using Templates.Api;
using Templates.Data;

namespace ServiceTests;

// Design 2.1, L4 (D6): ended rules stay stored for ExpiredRuleRetentionDays and are listable with
// ?includeExpired=true; they never match. The fixture runs the service with promotion-* ids keeping ended
// rules for 380 days, and every other id at the 2.0 default of 0.
[Collection(nameof(ServiceTestCollection))]
[Trait("TestType", "Service")]
public class EndedRuleTests(Fixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string id = "promotion-" + Guid.NewGuid().ToString("N");
    private readonly string eventId = Guid.NewGuid().ToString("N");

    private string Doc => $"v2/templates/{id}";

    [Fact]
    public async Task an_expired_rule_survives_a_later_unrelated_write()
    {
        await PutDocumentAsync(R("live"), R("ended", ended: true));

        await SendOkAsync(HttpMethod.Put, Doc + "/rules/other", R("other"));

        Names(await SendOkAsync(HttpMethod.Get, Doc + "?includeExpired=true")).Should().Equal("live", "ended", "other");
    }

    [Fact]
    public async Task is_listed_with_include_expired_and_not_without()
    {
        await PutDocumentAsync(R("live", group: "g"), R("ended", ended: true, group: "g"));
        await SendOkAsync(HttpMethod.Put, Doc + "/rules/other", R("other"));

        Names(await SendOkAsync(HttpMethod.Get, Doc)).Should().Equal("live", "other");
        Names(await SendOkAsync(HttpMethod.Get, Doc + "?includeExpired=false")).Should().Equal("live", "other");
        Names(await SendOkAsync(HttpMethod.Get, Doc + "?includeExpired=true")).Should().Equal("live", "ended", "other");

        (await SendAsync(HttpMethod.Get, Doc + "/rules/ended")).Status.Should().Be(HttpStatusCode.NotFound);
        (await SendOkAsync(HttpMethod.Get, Doc + "/rules/ended?includeExpired=true")).Json.GetProperty("name").GetString().Should().Be("ended");

        Names(await SendOkAsync(HttpMethod.Get, Doc + "/groups/g"), "rules").Should().Equal("live");
        Names(await SendOkAsync(HttpMethod.Get, Doc + "/groups/g?includeExpired=true"), "rules").Should().Equal("live", "ended");
        Names(await SendOkAsync(HttpMethod.Get, Doc + "/versions/2"), "templates").Should().Equal("live", "ended", "other");
    }

    [Fact]
    public async Task never_matches()
    {
        await PutDocumentAsync(R("live"), R("ended", ended: true));
        await SendOkAsync(HttpMethod.Put, Doc + "/rules/other", R("other"));

        var evaluated = await SendOkAsync(HttpMethod.Post, Doc + "/evaluate", $$"""{ "eventId": "{{eventId}}" }""");
        var explained = await SendOkAsync(HttpMethod.Post, Doc + "/explain", $$"""{ "eventId": "{{eventId}}" }""");

        evaluated.Json.TryGetProperty("ended", out _).Should().BeFalse();
        evaluated.Json.GetProperty("live").GetString().Should().Be("yes");
        var ended = explained.As<ExplainResponse>().rules.Single(r => r.name == "ended");
        ended.matched.Should().BeFalse();
        ended.note.Should().StartWith("expired:");
    }

    [Fact]
    public async Task the_group_etag_covers_ended_rules()
    {
        await PutDocumentAsync(R("live", group: "g"), R("ended", ended: true, group: "g"));
        var before = await SendOkAsync(HttpMethod.Get, Doc + "/groups/g");

        await SendOkAsync(HttpMethod.Put, Doc + "/rules/ended", R("ended", ended: true, group: "g", value: "changed"));

        var after = await SendOkAsync(HttpMethod.Get, Doc + "/groups/g");
        Names(after, "rules").Should().Equal("live");
        after.ETag.Should().NotBe(before.ETag, "the ended rule changed in storage, and the ETag covers stored rules");
        (await SendAsync(HttpMethod.Put, Doc + "/groups/g", """{ "rules": [""" + R("live") + "] }", ifMatch: before.ETag))
            .Status.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task a_rule_put_to_the_name_of_an_ended_rule_replaces_it_in_place()
    {
        await PutDocumentAsync(R("a"), R("ended", ended: true), R("b"));

        await SendOkAsync(HttpMethod.Put, Doc + "/rules/ended", R("ended", value: "revived"));

        Names(await SendOkAsync(HttpMethod.Get, Doc)).Should().Equal("a", "ended", "b");
    }

    [Fact]
    public async Task a_whole_document_put_replaces_ended_rules_too()
    {
        await PutDocumentAsync(R("live"), R("ended", ended: true));

        await PutDocumentAsync(R("live"));

        Names(await SendOkAsync(HttpMethod.Get, Doc + "?includeExpired=true")).Should().Equal("live");
    }

    [Fact]
    public async Task an_ended_rule_is_dropped_after_its_retention()
    {
        var (logic, repository, clock) = NewLogic();
        var promotion = "promotion-" + Guid.NewGuid().ToString("N");
        await PutAsync(logic, promotion, Rule("live"), Rule("ending", Start.AddDays(1)));

        clock.Advance(TimeSpan.FromDays(10));
        await UnrelatedWriteAsync(logic, promotion, "w1");
        Stored(await repository.ReadAsync(promotion)).Should().Equal("live", "ending", "w1");

        clock.Advance(TimeSpan.FromDays(371)); // day 381: one day past expiresAt + 380
        await UnrelatedWriteAsync(logic, promotion, "w2");
        Stored(await repository.ReadAsync(promotion)).Should().Equal("live", "w1", "w2");
    }

    [Fact]
    public async Task the_default_retention_keeps_2_0_behaviour()
    {
        var (logic, repository, clock) = NewLogic();
        var other = "b-" + Guid.NewGuid().ToString("N");
        await PutAsync(logic, other, Rule("live"), Rule("ending", Start.AddHours(1)));

        Stored(await repository.ReadAsync(other)).Should().Equal("live", "ending");
        clock.Advance(TimeSpan.FromHours(2));
        await UnrelatedWriteAsync(logic, other, "w1");

        Stored(await repository.ReadAsync(other)).Should().Equal("live", "w1");
    }

    [Fact]
    public async Task dropping_an_ended_rule_records_a_change_to_its_group()
    {
        var (logic, repository, clock) = NewLogic();
        var other = "b-" + Guid.NewGuid().ToString("N");
        await PutAsync(logic, other, Rule("g-ending", Start.AddHours(1), group: "g"), Rule("h-live", group: "h"));

        clock.Advance(TimeSpan.FromHours(2));
        await UnrelatedWriteAsync(logic, other, "loose", changedBy: "carol");

        var history = await repository.ListGroupVersionsAsync(other, "g", 100, null);
        history.Select(v => (v.Version, v.ChangedBy)).Should().Equal((2L, "carol"), (1L, "tester"));
        (await repository.ListGroupVersionsAsync(other, "h", 100, null)).Select(v => v.Version).Should().Equal(1);
        (await repository.ListVersionsAsync(other)).First().Groups.Should().Equal("g");
    }

    // A V2Logic over its own key prefix, on a clock the test controls, with the promotions configuration.
    private (V2Logic Logic, Repository Repository, FakeTimeProvider Clock) NewLogic()
    {
        var multiplexer = fixture.Services.GetRequiredService<ConnectionMultiplexer>();
        var prefix = $"test-ended-{Guid.NewGuid():N}:";
        var clock = new FakeTimeProvider(Start);
        var history = new TemplateHistoryOptions { Rules = [new() { Pattern = "promotion-*", ExpiredRuleRetentionDays = 380 }] };
        var repository = new Repository(() => multiplexer.GetDatabase().WithKeyPrefix(prefix), history, "{tenant}-templates", clock);
        var logic = new V2Logic(repository, fixture.Services.GetRequiredService<DynamicRulesEngine>(),
            Options.Create(new TemplateApiOptions()), clock, Options.Create(history));
        return (logic, repository, clock);
    }

    private static async Task PutAsync(V2Logic logic, string templateId, params DynamicTemplate[] rules) =>
        (await logic.PutAsync(templateId, new Template(rules, "code"), null, "tester")).Outcome.Should().Be(WriteOutcome.Ok);

    private static async Task UnrelatedWriteAsync(V2Logic logic, string templateId, string name, string changedBy = "tester") =>
        (await logic.PutRuleAsync(templateId, name, Rule(name), null, changedBy)).Outcome.Should().Be(WriteOutcome.Ok);

    private static string[] Stored(StoredTemplate stored) => [.. stored.Document!.Templates.Select(r => r.Name)];

    private static DynamicTemplate Rule(string name, DateTimeOffset? expiresAt = null, string? group = null) =>
        new(name, JsonDocument.Parse("""{ "eventId": "x" }""").RootElement, JsonDocument.Parse($$"""{ "{{name}}": "yes" }""").RootElement)
        {
            ExpiresAt = expiresAt?.UtcDateTime,
            Group = group,
        };

    private string R(string name, bool ended = false, string? group = null, string value = "yes")
    {
        var rule = new Dictionary<string, object>
        {
            ["name"] = name,
            ["dataToMatch"] = new { eventId },
            ["matchFragment"] = new Dictionary<string, string> { [name] = value },
        };
        if (ended)
            rule["expiresAt"] = DateTime.UtcNow.AddHours(-1).ToString("o", CultureInfo.InvariantCulture);
        if (group != null)
            rule["group"] = group;
        return JsonSerializer.Serialize(rule);
    }

    private sealed record Reply(HttpStatusCode Status, string? ETag, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
        public T As<T>() => JsonSerializer.Deserialize<T>(Body, JsonSerializerOptions.Web)!;
    }

    private static string[] Names(Reply reply, string array = "templates") =>
        [.. reply.Json.GetProperty(array).EnumerateArray().Select(r => r.GetProperty("name").GetString()!)];

    private Task PutDocumentAsync(params string[] rules) =>
        SendOkAsync(HttpMethod.Put, Doc, """{ "aggregationField": "code", "templates": [""" + string.Join(",", rules) + "] }");

    private async Task<Reply> SendOkAsync(HttpMethod method, string path, string? body = null)
    {
        var reply = await SendAsync(method, path, body);
        ((int)reply.Status).Should().BeInRange(200, 299, $"{method} {path} answered {reply.Body}");
        return reply;
    }

    private async Task<Reply> SendAsync(HttpMethod method, string path, string? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (ifMatch != null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await fixture.Client(userId: "tester").SendAsync(request, Ct);
        return new Reply(response.StatusCode, response.Headers.ETag?.Tag, await response.Content.ReadAsStringAsync(Ct));
    }
}
