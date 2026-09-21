using System.Net;

using FluentAssertions;

using Scenarify;

using Templates.Data;

using Xunit;

using static ServiceTests.TemplateScenarios;

namespace ServiceTests;

// P-13: whole-document version history and rollback, built on the version-key-on-PUT mechanism
// already covered by TemplateTests.put_after_a_cached_match_is_picked_up_within_the_version_check_period.
[Collection(nameof(ServiceTestCollection))]
[Trait("TestType", "Service")]
public class TemplateHistoryTests(Fixture fixture)
{
    private const string SecondVersionBody = """
        { "aggregationField": "code", "templates": [{ "id": "1", "name": "{{guid}}", "dataToMatch": { "eventId": "{{eventId}}" }, "matchFragment": { "response": "second" } }] }
        """;

    [Fact]
    public Task put_creates_a_queryable_version() =>
        fixture.Scenario()
            .WithTemplate((new { eventId = "{{eventId}}" }, new { response = "first" }))
            .When(Http.Get("v2/templates/{{guid}}/versions"))
            .ThenBody<TemplateVersionSummary[]>(versions =>
            {
                versions.Should().ContainSingle();
                versions[0].Version.Should().Be(1);
            })
            .RunAsync();

    [Fact]
    public Task listing_versions_returns_every_retained_version_newest_first() =>
        fixture.Scenario()
            .WithTemplate((new { eventId = "{{eventId}}" }, new { response = "first" }))
            .Given(Http.Put(TemplatePath, SecondVersionBody))
            .When(Http.Get("v2/templates/{{guid}}/versions"))
            .ThenBody<TemplateVersionSummary[]>(versions =>
                versions.Select(v => v.Version).Should().Equal(2, 1))
            .RunAsync();

    [Fact]
    public Task getting_a_specific_version_returns_its_exact_historical_content() =>
        fixture.Scenario()
            .WithTemplate((new { eventId = "{{eventId}}" }, new { response = "first" }))
            .Given(Http.Put(TemplatePath, SecondVersionBody))
            .When(Http.Get("v2/templates/{{guid}}/versions/1"))
            .ThenBody("""{ "aggregationField": "code", "templates": [{ "name": "{{guid}}", "dataToMatch": { "eventId": "{{eventId}}" }, "matchFragment": { "response": "first" } }] }""")
            .RunAsync();

    [Fact]
    public Task getting_an_unknown_version_is_not_found() =>
        fixture.Scenario()
            .WithTemplate((new { eventId = "{{eventId}}" }, new { response = "first" }))
            .When(Http.Get("v2/templates/{{guid}}/versions/999"))
            .ThenStatus(HttpStatusCode.NotFound)
            .RunAsync();

    [Fact]
    public Task rollback_to_an_old_version_restores_its_content_as_a_new_version() =>
        fixture.Scenario()
            .WithTemplate((new { eventId = "{{eventId}}" }, new { response = "first" }))
            .Given(Http.Put(TemplatePath, SecondVersionBody))
            .When(Http.Post("v2/templates/{{guid}}/rollback/1"))
            .ThenBody("""
                {
                  "version": 3,
                  "template": { "aggregationField": "code", "templates": [{ "name": "{{guid}}", "dataToMatch": { "eventId": "{{eventId}}" }, "matchFragment": { "response": "first" } }] }
                }
                """)
            .Then(Http.Get(TemplatePath).Matches("""{ "aggregationField": "code", "templates": [{ "name": "{{guid}}", "dataToMatch": { "eventId": "{{eventId}}" }, "matchFragment": { "response": "first" } }] }"""))
            .Then(Http.Get("v2/templates/{{guid}}/versions").Satisfies<TemplateVersionSummary[]>(versions =>
                versions.Select(v => v.Version).Should().Equal(3, 2, 1)))
            .RunAsync();

    [Fact]
    public Task rollback_to_an_unknown_version_is_not_found() =>
        fixture.Scenario()
            .WithTemplate((new { eventId = "{{eventId}}" }, new { response = "first" }))
            .When(Http.Post("v2/templates/{{guid}}/rollback/999"))
            .ThenStatus(HttpStatusCode.NotFound)
            .RunAsync();
}
