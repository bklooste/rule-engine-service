using System.Net;

using FluentAssertions;

using Scenarify;

using Templates.Data;

using Xunit;

using static ServiceTests.TemplateScenarios;

namespace ServiceTests;

// P-14: v2 document CRUD, rule-level edits and list (A7-A11, D-15, D-16).
[Collection(nameof(ServiceTestCollection))]
[Trait("TestType", "Service")]
public class V2TemplateApiTests(Fixture fixture)
{
    private const string SecondVersion = """
        { "aggregationField": "code", "templates": [{ "name": "r1", "dataToMatch": { "eventId": "{{eventId}}" }, "matchFragment": { "response": "second" } }] }
        """;

    private const string ThirdVersion = """
        { "aggregationField": "code", "templates": [{ "name": "r1", "dataToMatch": { "eventId": "{{eventId}}" }, "matchFragment": { "response": "third" } }] }
        """;

    private static object TwoNamedRules => new object[]
    {
        Rule("racing", new { eventId = "{{eventId}}" }, new { a = "racing" }),
        Rule("racingmetro", new { eventId = "{{eventId}}" }, new { b = "racingmetro" }),
    };

    // A8: the document's ETag is its version, and a second write against a version that has moved on
    // is refused rather than silently winning.
    [Fact]
    public Task get_carries_an_etag_and_a_matching_if_match_write_succeeds() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { response = "first" }))
            .Given(Http.Get(V2TemplatePath).HasHeader("ETag", "\"1\""))
            .When(Http.Put(V2TemplatePath, SecondVersion).WithHeader("If-Match", "\"1\""))
            .Then(Http.Get(V2TemplatePath).HasHeader("ETag", "\"2\"").Matches("""{ "templates": [{ "matchFragment": { "response": "second" } }] }"""))
            .RunAsync();

    [Fact]
    public Task a_stale_if_match_is_a_precondition_failure() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { response = "first" }))
            .Given(Http.Put(V2TemplatePath, SecondVersion).WithHeader("If-Match", "\"1\""))
            .When(Http.Put(V2TemplatePath, ThirdVersion).WithHeader("If-Match", "\"1\""))
            .ThenStatus(HttpStatusCode.PreconditionFailed)
            .Then(Http.Get(V2TemplatePath).Matches("""{ "templates": [{ "matchFragment": { "response": "second" } }] }"""))
            .RunAsync();

    [Fact]
    public Task a_put_without_if_match_still_works_for_bulk_import() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { response = "first" }))
            .When(Http.Put(V2TemplatePath, SecondVersion))
            .Then(Http.Get(V2TemplatePath).HasHeader("ETag", "\"2\""))
            .RunAsync();

    // A7: v1's APIValidator skipped validation outright for any id containing "override", which is how
    // a rule that throws on every request reached storage. v2 compiles every rule, whatever the id.
    [Fact]
    public Task a_rule_that_cannot_compile_is_rejected_by_name_even_on_an_override_template() =>
        fixture.Scenario()
            .When(Http.Put("v2/templates/{{guid}}-override", """
                { "aggregationField": "code", "templates": [{ "name": "badOdds", "dataToMatch": { "odds": 1.5 }, "matchFragment": { "response": "x" } }] }
                """).BodyContains("badOdds").BodyContains("Number"))
            .ThenStatus(HttpStatusCode.BadRequest)
            .RunAsync();

    [Fact]
    public Task a_malformed_ago_operand_is_rejected_at_write_time() =>
        fixture.Scenario()
            .When(Http.Put(V2TemplatePath, """
                { "aggregationField": "code", "templates": [{ "name": "badAgo", "dataToMatch": { "startTime": "<{ago(4x)}" }, "matchFragment": { "response": "x" } }] }
                """).BodyContains("badAgo"))
            .ThenStatus(HttpStatusCode.BadRequest)
            .RunAsync();

    [Fact]
    public Task a_document_with_no_rules_is_rejected() =>
        fixture.Scenario()
            .With("templates", Array.Empty<object>())
            .When(Http.Put(V2TemplatePath, "cases/templates/template.json"))
            .ThenStatus(HttpStatusCode.BadRequest)
            .RunAsync();

    // A9: v1 took changedBy from the request body, defaulting to "Unknown".
    [Fact]
    public Task changed_by_comes_from_the_userid_header_not_the_body() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { response = "first" }))
            .When(Http.Put(V2TemplatePath, """
                { "aggregationField": "code", "changedBy": "i-said-so", "templates": [{ "name": "r1", "dataToMatch": { "eventId": "{{eventId}}" }, "matchFragment": { "response": "second" } }] }
                """).AsUser("alice"))
            .Then(Http.Get("v2/templates/{{guid}}/versions").Satisfies<TemplateVersionSummary[]>(versions =>
                versions[0].ChangedBy.Should().Be("alice")))
            .RunAsync();

    // A11
    [Fact]
    public Task list_contains_every_written_template_id() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { response = "first" }))
            .When(Http.Get("v2/templates").BodyContains("{{guid}}"))
            .RunAsync();

    [Fact]
    public Task delete_removes_the_template_and_its_list_entry() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { response = "first" }))
            .When(Http.Delete(V2TemplatePath))
            .ThenStatus(HttpStatusCode.NoContent)
            .Then(Http.Get(V2TemplatePath).Status(HttpStatusCode.NotFound))
            .Then(Http.Post(V2EvaluatePath, """{ "eventId": "{{eventId}}" }""").Status(HttpStatusCode.NotFound))
            .RunAsync();

    [Fact]
    public Task delete_of_an_unknown_template_is_not_found() =>
        fixture.Scenario()
            .When(Http.Delete(V2TemplatePath))
            .ThenStatus(HttpStatusCode.NotFound)
            .RunAsync();

    [Fact]
    public Task delete_with_a_stale_if_match_is_a_precondition_failure() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { response = "first" }))
            .When(Http.Delete(V2TemplatePath).WithHeader("If-Match", "\"99\""))
            .ThenStatus(HttpStatusCode.PreconditionFailed)
            .RunAsync();

    // D-15/A8: rule-level edits match on the EXACT name. offer-products' Name.StartsWith key space is
    // what made group "racing" also hit "racingmetro"; nothing here matches on a prefix.
    [Fact]
    public Task a_rule_edit_replaces_only_the_exactly_named_rule() =>
        fixture.Scenario()
            .With("templates", TwoNamedRules)
            .With("eventId", Guid.NewGuid().ToString())
            .Given(Http.Put(V2TemplatePath, "cases/templates/template.json"))
            .When(Http.Put("v2/templates/{{guid}}/rules/racing", """
                { "name": "racing", "dataToMatch": { "eventId": "{{eventId}}" }, "matchFragment": { "a": "edited" } }
                """))
            .Then(Http.Get(V2TemplatePath).Satisfies<Template>(document =>
            {
                document.Templates.Select(r => r.Name).Should().Equal("racing", "racingmetro");
                document.Templates[0].MatchFragment.GetProperty("a").GetString().Should().Be("edited");
                document.Templates[1].MatchFragment.GetProperty("b").GetString().Should().Be("racingmetro");
            }))
            .RunAsync();

    [Fact]
    public Task a_rule_delete_removes_only_the_exactly_named_rule() =>
        fixture.Scenario()
            .With("templates", TwoNamedRules)
            .With("eventId", Guid.NewGuid().ToString())
            .Given(Http.Put(V2TemplatePath, "cases/templates/template.json"))
            .When(Http.Delete("v2/templates/{{guid}}/rules/racing"))
            .ThenStatus(HttpStatusCode.NoContent)
            .Then(Http.Get(V2TemplatePath).Satisfies<Template>(document =>
                document.Templates.Select(r => r.Name).Should().Equal("racingmetro")))
            .RunAsync();

    [Fact]
    public Task a_rule_edit_creates_a_new_version() =>
        fixture.Scenario()
            .With("templates", TwoNamedRules)
            .With("eventId", Guid.NewGuid().ToString())
            .Given(Http.Put(V2TemplatePath, "cases/templates/template.json"))
            .When(Http.Delete("v2/templates/{{guid}}/rules/racing").AsUser("bob"))
            .Then(Http.Get("v2/templates/{{guid}}/versions").Satisfies<TemplateVersionSummary[]>(versions =>
            {
                versions.Select(v => v.Version).Should().Equal(2, 1);
                versions[0].ChangedBy.Should().Be("bob");
            }))
            .RunAsync();

    // The rule's own ETag is its content hash, so an If-Match only conflicts with an edit to the same
    // rule - the whole point of D-15's rule-level versioning for the override template.
    [Fact]
    public Task a_rule_edit_with_a_stale_rule_etag_is_a_precondition_failure() =>
        fixture.Scenario()
            .With("templates", TwoNamedRules)
            .With("eventId", Guid.NewGuid().ToString())
            .Given(Http.Put(V2TemplatePath, "cases/templates/template.json"))
            .When(Http.Put("v2/templates/{{guid}}/rules/racing", """
                { "name": "racing", "dataToMatch": { "eventId": "{{eventId}}" }, "matchFragment": { "a": "edited" } }
                """).WithHeader("If-Match", "\"0000000000000000\""))
            .ThenStatus(HttpStatusCode.PreconditionFailed)
            .RunAsync();

    [Fact]
    public Task a_rule_edit_with_if_match_star_succeeds_when_the_rule_exists() =>
        fixture.Scenario()
            .With("templates", TwoNamedRules)
            .With("eventId", Guid.NewGuid().ToString())
            .Given(Http.Put(V2TemplatePath, "cases/templates/template.json"))
            .Given(Http.Get("v2/templates/{{guid}}/rules/racing").HasHeader("ETag", "\""))
            .When(Http.Put("v2/templates/{{guid}}/rules/racing", """
                { "name": "racing", "dataToMatch": { "eventId": "{{eventId}}" }, "matchFragment": { "a": "edited" } }
                """).WithHeader("If-Match", "*"))
            .Then(Http.Get(V2TemplatePath).Matches("""
                { "templates": [{ "name": "racing", "matchFragment": { "a": "edited" } }, { "name": "racingmetro" }] }
                """))
            .RunAsync();

    [Fact]
    public Task a_rule_edit_can_add_a_rule_that_did_not_exist() =>
        fixture.Scenario()
            .With("templates", TwoNamedRules)
            .With("eventId", Guid.NewGuid().ToString())
            .Given(Http.Put(V2TemplatePath, "cases/templates/template.json"))
            .When(Http.Put("v2/templates/{{guid}}/rules/EventId-42", """
                { "dataToMatch": { "eventId": "42" }, "matchFragment": { "c": "new" }, "group": "events", "expiresAt": "{{now+1d}}" }
                """))
            .Then(Http.Get(V2TemplatePath).Satisfies<Template>(document =>
                document.Templates.Select(r => r.Name).Should().Equal("racing", "racingmetro", "EventId-42")))
            .RunAsync();

    [Fact]
    public Task deleting_an_unknown_rule_is_not_found() =>
        fixture.Scenario()
            .With("templates", TwoNamedRules)
            .With("eventId", Guid.NewGuid().ToString())
            .Given(Http.Put(V2TemplatePath, "cases/templates/template.json"))
            .When(Http.Delete("v2/templates/{{guid}}/rules/racingm"))
            .ThenStatus(HttpStatusCode.NotFound)
            .RunAsync();

    // The metadata the matcher ignores (§7) has to survive a write/read round trip so admin tooling can
    // key on it instead of on rule-name prefixes.
    [Fact]
    public Task rule_metadata_round_trips_through_document_crud() =>
        fixture.Scenario()
            .With("eventId", Guid.NewGuid().ToString())
            .With("templates", new object[]
            {
                new
                {
                    name = "tagged",
                    dataToMatch = new { eventId = "{{eventId}}" },
                    matchFragment = new { response = "ok" },
                    group = "racing",
                    tags = new[] { "vip", "metro" },
                    expiresAt = "{{now+1d}}",
                },
            })
            .Given(Http.Put(V2TemplatePath, "cases/templates/template.json"))
            .When(Http.Get(V2TemplatePath).Satisfies<Template>(document =>
            {
                document.Templates[0].Group.Should().Be("racing");
                document.Templates[0].Tags.Should().Equal("vip", "metro");
                document.Templates[0].ExpiresAt.Should().NotBeNull();
            }))
            .RunAsync();

    // D-16: an expired rule is not returned by a read either, so an admin sees what the service serves.
    [Fact]
    public Task an_expired_rule_is_pruned_from_reads() =>
        fixture.Scenario()
            .With("eventId", Guid.NewGuid().ToString())
            .With("templates", new object[]
            {
                new { name = "live", dataToMatch = new { eventId = "{{eventId}}" }, matchFragment = new { a = "1" } },
                new { name = "stale", dataToMatch = new { eventId = "{{eventId}}" }, matchFragment = new { b = "2" }, expiresAt = "{{now-1h}}" },
            })
            .Given(Http.Put(V2TemplatePath, "cases/templates/template.json"))
            .When(Http.Get(V2TemplatePath).Satisfies<Template>(document =>
                document.Templates.Select(r => r.Name).Should().Equal("live")))
            .RunAsync();

    // A10: P-13's history, now on its v2 routes, still round-trips a rollback through the v2 read path.
    [Fact]
    public Task rollback_restores_an_old_version_and_the_next_evaluate_sees_it() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { response = "first" }))
            .Given(Http.Put(V2TemplatePath, SecondVersion))
            .When(Http.Post("v2/templates/{{guid}}/rollback/1"))
            .ThenBody("""{ "version": 3, "template": { "templates": [{ "matchFragment": { "response": "first" } }] } }""")
            .Then(Http.Post(V2EvaluatePath, """{ "eventId": "{{eventId}}" }""").Matches("""{ "response": "first" }"""))
            .RunAsync();
}
