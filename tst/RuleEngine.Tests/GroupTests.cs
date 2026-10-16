using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Templates.Data;

namespace ServiceTests;

// Design 2.1, L3 (D3, D5): write, list the history of, and roll back one `group` of rules.
[Collection(nameof(ServiceTestCollection))]
[Trait("TestType", "Service")]
public class GroupTests(Fixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string id = "groups-" + Guid.NewGuid().ToString("N");

    private string Doc => $"v2/templates/{id}";
    private string Group(string group) => $"v2/templates/{id}/groups/{group}";

    [Fact]
    public async Task two_groups_edited_concurrently_both_land()
    {
        var groups = Enumerable.Range(0, 4).Select(i => $"g{i}").ToArray();
        await PutDocumentAsync([.. groups.Select(g => R($"{g}-r", "v1", g))]);

        var replies = await Task.WhenAll(groups.Select(g => SendAsync(HttpMethod.Put, Group(g), GroupBody(R($"{g}-r", "v2")))));

        replies.Select(r => r.Status).Should().AllBeEquivalentTo(HttpStatusCode.OK);
        var document = (await SendAsync(HttpMethod.Get, Doc)).As<Template>();
        document.Templates.Select(r => (r.Name, Value(r))).Should().Equal(groups.Select(g => ($"{g}-r", (string?)"v2")));
        replies.Select(r => r.Version).Should().BeEquivalentTo(new long[] { 2, 3, 4, 5 });
    }

    [Fact]
    public async Task rollback_of_group_a_leaves_group_b_at_its_latest()
    {
        await PutDocumentAsync(R("a1", "v1", "a"), R("b1", "v1", "b"));          // v1
        await SendOkAsync(HttpMethod.Put, Group("a"), GroupBody(R("a1", "v2")));  // v2
        await SendOkAsync(HttpMethod.Put, Group("b"), GroupBody(R("b1", "v3")));  // v3

        var rollback = await SendOkAsync(HttpMethod.Post, Group("a") + "/rollback/1");

        rollback.Json.GetProperty("version").GetInt64().Should().Be(4);
        rollback.Json.GetProperty("rules")[0].GetProperty("matchFragment").GetProperty("a1").GetString().Should().Be("v1");
        rollback.ETag.Should().Be((await SendOkAsync(HttpMethod.Get, Group("a"))).ETag);
        var document = (await SendAsync(HttpMethod.Get, Doc)).As<Template>();
        document.Templates.Select(r => (r.Name, Value(r))).Should().Equal(("a1", "v1"), ("b1", "v3"));
    }

    [Fact]
    public async Task a_stale_group_etag_returns_412()
    {
        await PutDocumentAsync(R("a1", "v1", "a"));
        var read = await SendOkAsync(HttpMethod.Get, Group("a"));
        await SendOkAsync(HttpMethod.Put, Group("a"), GroupBody(R("a1", "v2")), ifMatch: read.ETag);

        var stale = await SendAsync(HttpMethod.Put, Group("a"), GroupBody(R("a1", "v3")), ifMatch: read.ETag);

        stale.Status.Should().Be(HttpStatusCode.PreconditionFailed);
        (await SendAsync(HttpMethod.Delete, Group("a"), ifMatch: read.ETag)).Status.Should().Be(HttpStatusCode.PreconditionFailed);
        (await SendAsync(HttpMethod.Post, Group("a") + "/rollback/1", ifMatch: read.ETag)).Status.Should().Be(HttpStatusCode.PreconditionFailed);
        Value((await SendOkAsync(HttpMethod.Get, Group("a"))).As<GroupRulesBody>().rules[0]).Should().Be("v2");
    }

    [Fact]
    public async Task an_edit_to_another_group_does_not_change_this_groups_etag()
    {
        await PutDocumentAsync(R("a1", "v1", "a"), R("b1", "v1", "b"));
        var a = await SendOkAsync(HttpMethod.Get, Group("a"));

        await SendOkAsync(HttpMethod.Put, Group("b"), GroupBody(R("b1", "v2")));
        await SendOkAsync(HttpMethod.Put, $"v2/templates/{id}/rules/loose", R("loose", "v1"));

        var again = await SendOkAsync(HttpMethod.Get, Group("a"));
        again.ETag.Should().Be(a.ETag);
        again.Headers["X-Template-Version"].Should().Be("3");
        (await SendAsync(HttpMethod.Put, Group("a"), GroupBody(R("a1", "v2")), ifMatch: a.ETag)).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task if_match_star_needs_the_group_to_exist()
    {
        await PutDocumentAsync(R("a1", "v1", "a"));

        (await SendAsync(HttpMethod.Put, Group("new"), GroupBody(R("n1", "v1")), ifMatch: "*")).Status.Should().Be(HttpStatusCode.PreconditionFailed);
        (await SendAsync(HttpMethod.Put, Group("a"), GroupBody(R("a1", "v2")), ifMatch: "*")).Status.Should().Be(HttpStatusCode.OK);
    }

    // Design 2.1, bug 2: both rollbacks record their caller, not the author of the version they restore.
    [Fact]
    public async Task changed_by_is_recorded_per_version()
    {
        await PutDocumentAsync([R("a1", "v1", "a"), R("b1", "v1", "b")], user: "alice");                      // v1
        await SendOkAsync(HttpMethod.Put, Group("a"), GroupBody(R("a1", "v2")), user: "bob");                 // v2
        await SendOkAsync(HttpMethod.Put, $"v2/templates/{id}/rules/b1", R("b1", "v3", "b"), user: "carol");  // v3
        await SendOkAsync(HttpMethod.Post, Group("a") + "/rollback/1", user: "dave");                          // v4
        await SendOkAsync(HttpMethod.Post, Doc + "/rollback/2", user: "erin");                                 // v5
        await SendOkAsync(HttpMethod.Delete, Group("b"), user: "frank");                                       // v6

        var versions = (await SendOkAsync(HttpMethod.Get, Doc + "/versions")).As<TemplateVersionSummary[]>();
        versions.Select(v => (v.Version, v.ChangedBy)).Should().Equal(
            (6L, "frank"), (5L, "erin"), (4L, "dave"), (3L, "carol"), (2L, "bob"), (1L, "alice"));
        var groupA = (await SendOkAsync(HttpMethod.Get, Group("a") + "/versions")).As<TemplateVersionSummary[]>();
        groupA.Select(v => (v.Version, v.ChangedBy)).Should().Equal((5L, "erin"), (4L, "dave"), (2L, "bob"), (1L, "alice"));
    }

    [Fact]
    public async Task group_versions_lists_only_versions_that_changed_the_group()
    {
        await PutDocumentAsync(R("a1", "v1", "a"), R("b1", "v1", "b"));                    // v1: a, b
        await SendOkAsync(HttpMethod.Put, Group("a"), GroupBody(R("a1", "v2")));            // v2: a
        await SendOkAsync(HttpMethod.Put, Group("b"), GroupBody(R("b1", "v3")));            // v3: b
        await SendOkAsync(HttpMethod.Put, $"v2/templates/{id}/rules/loose", R("loose", "v4")); // v4: none

        Versions(await SendOkAsync(HttpMethod.Get, Group("a") + "/versions")).Should().Equal(2, 1);
        Versions(await SendOkAsync(HttpMethod.Get, Group("b") + "/versions")).Should().Equal(3, 1);
        (await SendOkAsync(HttpMethod.Get, Doc + "/versions")).As<TemplateVersionSummary[]>()
            .Select(v => string.Join(",", v.Groups!)).Should().Equal("", "b", "a", "a,b");
    }

    [Fact]
    public async Task a_rule_level_edit_to_a_grouped_rule_appears_in_group_history()
    {
        await PutDocumentAsync(R("a1", "v1", "a"), R("b1", "v1", "b"));                       // v1
        await SendOkAsync(HttpMethod.Put, $"v2/templates/{id}/rules/a1", R("a1", "v2", "a"));  // v2: a
        await SendOkAsync(HttpMethod.Put, $"v2/templates/{id}/rules/b1", R("b1", "v1", "a"));  // v3: b1 moves b -> a
        await SendOkAsync(HttpMethod.Delete, $"v2/templates/{id}/rules/a1");                    // v4: a

        Versions(await SendOkAsync(HttpMethod.Get, Group("a") + "/versions")).Should().Equal(4, 3, 2, 1);
        Versions(await SendOkAsync(HttpMethod.Get, Group("b") + "/versions")).Should().Equal(3, 1);
    }

    [Fact]
    public async Task group_history_pages_with_limit_and_before()
    {
        await PutDocumentAsync(R("a1", "v1", "a"));
        for (var i = 2; i <= 5; i++)
            await SendOkAsync(HttpMethod.Put, Group("a"), GroupBody(R("a1", $"v{i}")));

        Versions(await SendOkAsync(HttpMethod.Get, Group("a") + "/versions?limit=2")).Should().Equal(5, 4);
        Versions(await SendOkAsync(HttpMethod.Get, Group("a") + "/versions?limit=2&before=4")).Should().Equal(3, 2);
        Versions(await SendOkAsync(HttpMethod.Get, Doc + "/versions?limit=3&before=5")).Should().Equal(4, 3, 2);
        (await SendAsync(HttpMethod.Get, Group("a") + "/versions?limit=1001")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, Doc + "/versions?limit=0")).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task a_group_version_is_the_group_as_it_stood_then()
    {
        await PutDocumentAsync(R("a1", "v1", "a"), R("b1", "v1", "b"));
        await SendOkAsync(HttpMethod.Put, Group("a"), GroupBody(R("a1", "v2"), R("a2", "v2")));
        await SendOkAsync(HttpMethod.Delete, Group("b"));

        (await SendOkAsync(HttpMethod.Get, Group("a") + "/versions/1")).As<GroupRulesBody>().rules.Select(r => r.Name).Should().Equal("a1");
        (await SendOkAsync(HttpMethod.Get, Group("a") + "/versions/2")).As<GroupRulesBody>().rules.Select(r => r.Name).Should().Equal("a1", "a2");
        (await SendOkAsync(HttpMethod.Get, Group("b") + "/versions/2")).As<GroupRulesBody>().rules.Select(r => r.Name).Should().Equal("b1");
        (await SendAsync(HttpMethod.Get, Group("b") + "/versions/3")).Status.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, Group("a") + "/versions/99")).Status.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, Group("b"))).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task a_no_op_group_put_adds_no_version()
    {
        await PutDocumentAsync(R("a1", "v1", "a"), R("b1", "v1", "b"));
        var before = await SendOkAsync(HttpMethod.Get, Group("a"));

        var put = await SendOkAsync(HttpMethod.Put, Group("a"), GroupBody(R("a1", "v1")));

        put.Version.Should().Be(1);
        put.ETag.Should().Be(before.ETag);
        Versions(await SendOkAsync(HttpMethod.Get, Doc + "/versions")).Should().Equal(1);
    }

    [Fact]
    public async Task a_group_put_creates_a_missing_document()
    {
        (await SendAsync(HttpMethod.Put, Group("a"), GroupBody(R("a1", "v1")))).Status.Should().Be(HttpStatusCode.BadRequest, "aggregationField is needed to create the document");

        var created = await SendOkAsync(HttpMethod.Put, Group("a"), """{ "aggregationField": "code", "rules": [""" + R("a1", "v1") + "] }");

        created.Version.Should().Be(1);
        var document = (await SendOkAsync(HttpMethod.Get, Doc)).As<Template>();
        document.AggregationField.Should().Be("code");
        document.Templates.Select(r => (r.Name, r.Group)).Should().Equal(("a1", "a"));
        (await SendAsync(HttpMethod.Put, Group("b"), """{ "aggregationField": "other", "rules": [""" + R("b1", "v1") + "] }"))
            .Status.Should().Be(HttpStatusCode.BadRequest, "a supplied aggregationField must equal the stored one");
    }

    [Fact]
    public async Task a_rule_name_owned_by_another_group_is_rejected()
    {
        await PutDocumentAsync(R("shared", "v1", "a"), R("loose", "v1"));

        var taken = await SendAsync(HttpMethod.Put, Group("b"), GroupBody(R("shared", "v1")));
        var ungrouped = await SendAsync(HttpMethod.Put, Group("b"), GroupBody(R("loose", "v1")));

        taken.Status.Should().Be(HttpStatusCode.BadRequest);
        taken.Body.Should().Contain("shared").And.Contain("group 'a'");
        ungrouped.Status.Should().Be(HttpStatusCode.BadRequest);
        ungrouped.Body.Should().Contain("ungrouped");
    }

    [Fact]
    public async Task a_group_put_body_is_validated()
    {
        await PutDocumentAsync(R("a1", "v1", "a"));

        (await SendAsync(HttpMethod.Put, Group("a"), """{ "rules": [] }""")).Status.Should().Be(HttpStatusCode.BadRequest, "an empty group is a DELETE");
        (await SendAsync(HttpMethod.Put, Group("a"), GroupBody(R("x", "1"), R("x", "2")))).Status.Should().Be(HttpStatusCode.BadRequest, "names are unique");
        (await SendAsync(HttpMethod.Put, Group("a"), GroupBody(R("x", "1", "b")))).Status.Should().Be(HttpStatusCode.BadRequest, "a rule's group must match the route");
        (await SendAsync(HttpMethod.Put, Group("a"), GroupBody("""{ "name": "bad", "dataToMatch": { "odds": 1.5 }, "matchFragment": { "a": 1 } }""")))
            .Body.Should().Contain("bad");
        Versions(await SendOkAsync(HttpMethod.Get, Doc + "/versions")).Should().Equal(1);
    }

    [Fact]
    public async Task placement_keeps_the_order_of_other_groups()
    {
        await PutDocumentAsync(R("x", "1"), R("a1", "1", "a"), R("b1", "1", "b"), R("a2", "1", "a"), R("y", "1"));

        await SendOkAsync(HttpMethod.Put, Group("a"), GroupBody(R("a3", "2"), R("a4", "2")));
        await SendOkAsync(HttpMethod.Put, Group("c"), GroupBody(R("c1", "2")));

        var document = (await SendOkAsync(HttpMethod.Get, Doc)).As<Template>();
        document.Templates.Select(r => r.Name).Should().Equal("x", "a3", "a4", "b1", "y", "c1");
    }

    [Fact]
    public async Task a_group_delete_removes_every_rule_of_the_group()
    {
        await PutDocumentAsync(R("a1", "1", "a"), R("b1", "1", "b"), R("a2", "1", "a"));

        (await SendAsync(HttpMethod.Delete, Group("a"))).Status.Should().Be(HttpStatusCode.NoContent);

        (await SendOkAsync(HttpMethod.Get, Doc)).As<Template>().Templates.Select(r => r.Name).Should().Equal("b1");
        (await SendAsync(HttpMethod.Delete, Group("a"))).Status.Should().Be(HttpStatusCode.NotFound);
        Versions(await SendOkAsync(HttpMethod.Get, Group("a") + "/versions")).Should().Equal(2, 1);
    }

    [Fact]
    public async Task group_rollback_to_a_version_without_the_group_is_404()
    {
        await PutDocumentAsync(R("b1", "1", "b"));                                  // v1: no group a
        await SendOkAsync(HttpMethod.Put, Group("a"), GroupBody(R("a1", "2")));     // v2

        var rollback = await SendAsync(HttpMethod.Post, Group("a") + "/rollback/1");

        rollback.Status.Should().Be(HttpStatusCode.NotFound);
        rollback.Body.Should().Contain("use DELETE");
        (await SendAsync(HttpMethod.Post, Group("a") + "/rollback/99")).Status.Should().Be(HttpStatusCode.NotFound);
        (await SendOkAsync(HttpMethod.Get, Group("a"))).As<GroupRulesBody>().rules.Select(r => r.Name).Should().Equal("a1");
    }

    [Fact]
    public async Task group_rollback_to_a_name_another_group_now_owns_is_409()
    {
        await PutDocumentAsync(R("r", "1", "a"));                                   // v1: r in a
        await SendOkAsync(HttpMethod.Delete, Group("a"));                           // v2
        await SendOkAsync(HttpMethod.Put, Group("b"), GroupBody(R("r", "3")));      // v3: r in b

        var rollback = await SendAsync(HttpMethod.Post, Group("a") + "/rollback/1");

        rollback.Status.Should().Be(HttpStatusCode.Conflict);
        rollback.Body.Should().Contain("group 'a'").And.Contain("group 'b'");
    }

    [Fact]
    public async Task an_invalid_group_name_is_rejected_on_every_write_path()
    {
        (await SendAsync(HttpMethod.Put, Doc, """{ "aggregationField": "code", "templates": [""" + R("r", "1", "bad\u0001group") + "] }"))
            .Status.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Put, Group(new string('g', 257)), """{ "aggregationField": "code", "rules": [""" + R("r", "1") + "] }"))
            .Status.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record GroupRulesBody(DynamicTemplate[] rules);

    private sealed record Reply(HttpStatusCode Status, string? ETag, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
        public long Version => Json.GetProperty("version").GetInt64();
        public T As<T>() => JsonSerializer.Deserialize<T>(Body, JsonSerializerOptions.Web)!;
    }

    private static long[] Versions(Reply reply) => [.. reply.As<TemplateVersionSummary[]>().Select(v => v.Version)];

    private static string? Value(DynamicTemplate rule) => rule.MatchFragment.GetProperty(rule.Name).GetString();

    private static string R(string name, string value, string? group = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["dataToMatch"] = new { eventId = "x" },
            ["matchFragment"] = new Dictionary<string, string> { [name] = value },
            ["group"] = group,
        }.Where(kv => kv.Value != null).ToDictionary());

    private static string GroupBody(params string[] rules) => """{ "rules": [""" + string.Join(",", rules) + "] }";

    private Task PutDocumentAsync(params string[] rules) => PutDocumentAsync(rules, user: "tester");

    private Task PutDocumentAsync(string[] rules, string user) =>
        SendOkAsync(HttpMethod.Put, Doc, """{ "aggregationField": "code", "templates": [""" + string.Join(",", rules) + "] }", user: user);

    private async Task<Reply> SendOkAsync(HttpMethod method, string path, string? body = null, string? ifMatch = null, string user = "tester")
    {
        var reply = await SendAsync(method, path, body, ifMatch, user);
        ((int)reply.Status).Should().BeInRange(200, 299, $"{method} {path} answered {reply.Body}");
        return reply;
    }

    private async Task<Reply> SendAsync(HttpMethod method, string path, string? body = null, string? ifMatch = null, string user = "tester")
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (ifMatch != null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await fixture.Client(userId: user).SendAsync(request, Ct);
        var headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        return new Reply(response.StatusCode, response.Headers.ETag?.Tag, headers, await response.Content.ReadAsStringAsync(Ct));
    }
}
