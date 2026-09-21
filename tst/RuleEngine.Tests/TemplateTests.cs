using System.Net;

using Scenarify;

using Xunit;

using static ServiceTests.TemplateScenarios;

namespace ServiceTests;

[Collection(nameof(ServiceTestCollection))]
[Trait("TestType", "Service")]
public class TemplateTests(Fixture fixture)
{
    private const string OneEvent = """[{ "eventId": "{{eventId}}", "eventType": "AEvent" }]""";

    [Fact]
    public Task added_template_is_available() =>
        fixture.Scenario()
            .WithTemplate((new { aId = "{{eventId}}" }, new { response = "{{eventId}}" }))
            .When(Http.Get(TemplatePath))
            .ThenBody("""{ "aggregationField": "code", "templates": [{ "name": "{{guid}}", "dataToMatch": { "aId": "{{eventId}}" } }] }""")
            .RunAsync();

    [Fact]
    public Task unknown_template_is_not_found() =>
        fixture.Scenario()
            .When(Http.Get(TemplatePath))
            .ThenStatus(HttpStatusCode.NotFound)
            .RunAsync();

    [Fact]
    public Task template_without_rules_is_rejected() =>
        fixture.Scenario()
            .With("templates", Array.Empty<object>())
            .When(Http.Put(TemplatePath, "cases/templates/template.json"))
            .ThenStatus(HttpStatusCode.BadRequest)
            .RunAsync();

    [Fact]
    public Task no_match_returns_one_empty_result_per_event() =>
        fixture.Scenario()
            .WithTemplate((new { aId = "{{eventId}}" }, new { response = "{{eventId}}" }))
            .When(Http.Post(MatchPath, """[{ "eventId": "12", "eventType": "AEvent" }]"""))
            .ThenBody("[{}]")
            .RunAsync();

    [Fact]
    public Task matching_rule_returns_its_fragment() =>
        fixture.Scenario()
            .WithTemplate((new { eventId = "{{eventId}}" }, new { response = new { eventId = "{{eventId}}", operation = 12 } }))
            .When(Http.Post(MatchPath, OneEvent))
            .ThenBody("""[{ "response": { "eventId": "{{eventId}}", "operation": 12 } }]""")
            .RunAsync();

    [Fact]
    public Task two_matching_rules_merge_with_the_last_winning() =>
        fixture.Scenario()
            .WithTemplate(
                (new { eventId = "{{eventId}}" }, new { eventId = "{{eventId}}", operation = "12" }),
                (new { eventId = "{{eventId}}" }, new { eventId = "dummy", operation = "13" }))
            .When(Http.Post(MatchPath, OneEvent))
            .ThenBody("""[{ "eventId": "dummy", "operation": "13" }]""")
            .RunAsync();

    [Fact]
    public Task wildcard_rule_matches_every_event() =>
        fixture.Scenario()
            .WithTemplate((new { eventId = "*" }, new { eventId1 = "{{eventId}}", operation = "{{guid}}" }))
            .When(Http.Post(MatchPath, """[{ "eventId": "{{eventId}}", "eventType": "AEvent" }, { "eventId": "{{customerId}}", "eventType": "AEvent" }]"""))
            .ThenBody("""[{ "operation": "{{guid}}" }, { "operation": "{{guid}}" }]""")
            .RunAsync();

    // P-04: PUT bumps the template's Redis version in the same transaction as the write, and matching
    // goes through a short HybridCache version check, so a change reaches an already-cached pod within
    // that check period instead of the old 300s MemoryCache expiry. Eventually() because the version
    // check has its own short cache window (TemplateVersionCache:Duration, 5s default) not shortened here.
    [Fact]
    public Task put_after_a_cached_match_is_picked_up_within_the_version_check_period() =>
        fixture.Scenario()
            .WithTemplate((new { eventId = "{{eventId}}" }, new { response = "first" }))
            .Given(Http.Post(MatchPath, OneEvent).Matches("""[{ "response": "first" }]"""))
            .When(Http.Put(TemplatePath, """
                { "aggregationField": "code", "templates": [{ "id": "1", "name": "{{guid}}", "dataToMatch": { "eventId": "{{eventId}}" }, "matchFragment": { "response": "second" } }] }
                """))
            .Then(Http.Post(MatchPath, OneEvent).Eventually().Matches("""[{ "response": "second" }]"""))
            .RunAsync();
}
