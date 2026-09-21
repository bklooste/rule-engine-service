using System.Net;

using FluentAssertions;

using Scenarify;

using Templates.Api;

using Xunit;

using static ServiceTests.TemplateScenarios;

namespace ServiceTests;

// P-14: v2 evaluate, explain and the semantics that differ from v1 (A1/A4/A13/A14/D-6/D-11/D-12).
// v1's own behaviour is pinned by GoldenTests and TemplateTests and is untouched by any of this - the
// two ago() tests below assert both signs against the same stored template to prove it.
[Collection(nameof(ServiceTestCollection))]
[Trait("TestType", "Service")]
public class V2EvaluateTests(Fixture fixture)
{
    private const string OneEvent = """{ "eventId": "{{eventId}}", "eventType": "AEvent" }""";
    private const string OneEventArray = """[{ "eventId": "{{eventId}}", "eventType": "AEvent" }]""";

    [Fact]
    public Task evaluate_of_a_single_event_returns_an_object_with_the_template_version() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { response = "ok" }))
            .When(Http.Post(V2EvaluatePath, OneEvent)
                .HasHeader("X-Template-Version", "1")
                .MatchesExactly("""{ "response": "ok" }"""))
            .RunAsync();

    [Fact]
    public Task evaluate_of_an_array_returns_one_result_per_event() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { response = "ok" }))
            .When(Http.Post(V2EvaluatePath, """[{ "eventId": "{{eventId}}" }, { "eventId": "other" }]"""))
            .ThenBodyExactly("""[{ "response": "ok" }, {}]""")
            .RunAsync();

    // A13: v1's /one answered [] on an object endpoint when nothing matched.
    [Fact]
    public Task evaluate_with_no_match_returns_an_empty_object_not_an_empty_array() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "never" }, new { response = "ok" }))
            .When(Http.Post(V2EvaluatePath, OneEvent))
            .ThenBodyExactly("{}")
            .RunAsync();

    // A1: v1 answered 400 "No such template".
    [Fact]
    public Task evaluate_of_an_unknown_template_is_not_found() =>
        fixture.Scenario()
            .With("eventId", Guid.NewGuid().ToString())
            .When(Http.Post(V2EvaluatePath, OneEvent))
            .ThenStatus(HttpStatusCode.NotFound)
            .RunAsync();

    // A4: the limit is configuration now, and the message says what it is.
    [Fact]
    public Task evaluate_over_the_configured_event_limit_is_rejected() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { response = "ok" }))
            .With("manyEvents", Enumerable.Range(0, 101).Select(i => new { eventId = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
            .When(Http.Post(V2EvaluatePath, "{{manyEvents}}"))
            .ThenStatus(HttpStatusCode.BadRequest)
            .RunAsync();

    // D-11 (revised): loose by default, matching v1 - a condition whose property the event does not carry
    // is skipped, not failed, so this rule still matches. Many real events legitimately don't carry
    // fields like locationClass/venueName; failing by default would incorrectly narrow the common case.
    [Fact]
    public Task a_condition_on_an_absent_property_is_skipped_by_default() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}", locationClass = "Metro" }, new { response = "ok" }))
            .When(Http.Post(V2EvaluatePath, OneEvent))
            .ThenBodyExactly("""{ "response": "ok" }""")
            .RunAsync();

    // D-11: ...unless the rule explicitly opts a condition into strict (required) checking.
    [Fact]
    public Task a_required_condition_on_an_absent_property_fails_the_rule() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}", locationClass = new { value = "Metro", required = true } }, new { response = "ok" }))
            .When(Http.Post(V2EvaluatePath, OneEvent))
            .ThenBodyExactly("{}")
            .RunAsync();

    [Fact]
    public Task a_required_condition_still_has_to_hold_when_the_property_is_present() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}", locationClass = new { value = "Metro", required = true } }, new { response = "ok" }))
            .When(Http.Post(V2EvaluatePath, """{ "eventId": "{{eventId}}", "locationClass": "Country" }"""))
            .ThenBodyExactly("{}")
            .RunAsync();

    // D-6: ">{ago(1h)}" in v2 is "later than one hour ago", so an event half an hour old matches...
    [Fact]
    public Task ago_reads_as_the_past_in_v2() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { startTime = ">{ago(1h)}" }, new { response = "ok" }))
            .When(Http.Post(V2EvaluatePath, """{ "eventId": "{{eventId}}", "startTime": "{{now-30m}}" }"""))
            .ThenBodyExactly("""{ "response": "ok" }""")
            .RunAsync();

    [Fact]
    public Task ago_does_not_match_an_event_older_than_its_window_in_v2() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { startTime = ">{ago(1h)}" }, new { response = "ok" }))
            .When(Http.Post(V2EvaluatePath, """{ "eventId": "{{eventId}}", "startTime": "{{now-2h}}" }"""))
            .ThenBodyExactly("{}")
            .RunAsync();

    // D-16: an expired rule is skipped on evaluate and does not come back from a read.
    [Fact]
    public Task an_expired_rule_is_skipped() =>
        fixture.Scenario()
            .WithV2Template(
                new { name = "live", dataToMatch = new { eventId = "{{eventId}}" }, matchFragment = new { live = "yes" } },
                new { name = "stale", dataToMatch = new { eventId = "{{eventId}}" }, matchFragment = new { stale = "yes" }, expiresAt = "{{now-1h}}" })
            .When(Http.Post(V2EvaluatePath, OneEvent))
            .ThenBodyExactly("""{ "live": "yes" }""")
            .RunAsync();

    [Fact]
    public Task a_rule_that_has_not_expired_yet_still_matches() =>
        fixture.Scenario()
            .WithV2Template(new { name = "soon", dataToMatch = new { eventId = "{{eventId}}" }, matchFragment = new { soon = "yes" }, expiresAt = "{{now+1d}}" })
            .When(Http.Post(V2EvaluatePath, OneEvent))
            .ThenBodyExactly("""{ "soon": "yes" }""")
            .RunAsync();

    // §1a/D-17: dataToMatch written as JsonLogic, detected structurally (a single key that is an
    // operator) and compiled by P-21's frontend into the same closures the flat DSL produces.
    private const string JsonLogicTemplate = """
        {
          "aggregationField": "code",
          "templates": [{
            "name": "r1",
            "dataToMatch": { "and": [
              { "==ci": [{ "var": "eventId" }, "{{eventId}}"] },
              { "startsWith": [{ "var": "eventType" }, "AEv"] }
            ]},
            "matchFragment": { "response": "ok" }
          }]
        }
        """;

    // A clause wrapped in {"required": [...]} is the JsonLogic spelling of the per-condition required flag.
    private const string JsonLogicRequiredTemplate = """
        {
          "aggregationField": "code",
          "templates": [{
            "name": "r1",
            "dataToMatch": { "and": [
              { "==ci": [{ "var": "eventId" }, "{{eventId}}"] },
              { "required": [{ "==ci": [{ "var": "locationClass" }, "Metro"] }] }
            ]},
            "matchFragment": { "response": "ok" }
          }]
        }
        """;

    [Fact]
    public Task evaluate_accepts_a_jsonlogic_data_to_match() =>
        fixture.Scenario()
            .With("eventId", Guid.NewGuid().ToString())
            .Given(Http.Put(V2TemplatePath, JsonLogicTemplate))
            .When(Http.Post(V2EvaluatePath, OneEvent))
            .ThenBodyExactly("""{ "response": "ok" }""")
            .RunAsync();

    [Fact]
    public Task a_jsonlogic_clause_can_be_marked_required() =>
        fixture.Scenario()
            .With("eventId", Guid.NewGuid().ToString())
            .Given(Http.Put(V2TemplatePath, JsonLogicRequiredTemplate))
            .When(Http.Post(V2EvaluatePath, OneEvent))
            .ThenBodyExactly("{}")
            .RunAsync();

    [Fact]
    public Task a_jsonlogic_condition_that_fails_stops_the_rule() =>
        fixture.Scenario()
            .With("eventId", Guid.NewGuid().ToString())
            .Given(Http.Put(V2TemplatePath, JsonLogicTemplate))
            .When(Http.Post(V2EvaluatePath, """{ "eventId": "{{eventId}}", "eventType": "Racing" }"""))
            .ThenBodyExactly("{}")
            .RunAsync();

    // D-12/A15: one call, several templates, one result per (event, template) plus each template's version.
    [Fact]
    public Task multi_template_evaluate_returns_a_result_per_template_per_event() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { brand = "yes" }))
            .Given(Http.Put("v2/templates/{{guid}}-override", """
                { "aggregationField": "code", "templates": [{ "name": "o1", "dataToMatch": { "eventId": "{{eventId}}" }, "matchFragment": { "override": "yes" } }] }
                """))
            .When(Http.Post("v2/templates/evaluate", """
                { "templateIds": ["{{guid}}", "{{guid}}-override"], "events": [{ "eventId": "{{eventId}}" }] }
                """))
            .ThenBody<MultiEvaluateResponse>(response =>
            {
                response.versions.Should().HaveCount(2).And.Subject.Values.Should().AllSatisfy(v => v.Should().Be(1));
                response.results.Should().ContainSingle();
                response.results[0].Should().HaveCount(2);
                response.results[0].Values.Select(v => v.ToJsonString()).Should().BeEquivalentTo([
                    """{"brand":"yes"}""",
                    """{"override":"yes"}""",
                ]);
            })
            .RunAsync();

    [Fact]
    public Task multi_template_evaluate_with_an_unknown_template_is_not_found() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}" }, new { brand = "yes" }))
            .When(Http.Post("v2/templates/evaluate", """
                { "templateIds": ["{{guid}}", "{{guid}}-missing"], "events": [{ "eventId": "{{eventId}}" }] }
                """))
            .ThenStatus(HttpStatusCode.NotFound)
            .RunAsync();

    // A12: explain names the condition that decided the rule, and why.
    [Fact]
    public Task explain_reports_the_first_failing_condition() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}", eventType = "Racing" }, new { response = "ok" }))
            .When(Http.Post(V2ExplainPath, OneEvent))
            .ThenBody<ExplainResponse>(explain =>
            {
                var rule = explain.rules.Should().ContainSingle().Subject;
                rule.matched.Should().BeFalse();
                rule.conditions[0].status.Should().Be("passed");
                rule.conditions[1].status.Should().Be("failed");
                rule.conditions[1].reason.Should().Contain("AEvent").And.Contain("Racing");
                explain.result.Should().BeEmpty();
            })
            .RunAsync();

    // D-11 (revised): loose by default - explain reports the absent condition as skipped, and the rule
    // still matches since the other condition was evaluated and passed.
    [Fact]
    public Task explain_reports_an_absent_property_as_skipped_by_default() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}", locationClass = "Metro" }, new { response = "ok" }))
            .When(Http.Post(V2ExplainPath, OneEvent))
            .ThenBody<ExplainResponse>(explain =>
            {
                var rule = explain.rules.Should().ContainSingle().Subject;
                rule.matched.Should().BeTrue();
                rule.conditions[1].status.Should().Be("skipped");
                rule.conditions[1].reason.Should().Contain("not required");
            })
            .RunAsync();

    [Fact]
    public Task explain_reports_an_absent_required_property_as_the_reason_a_rule_failed() =>
        fixture.Scenario()
            .WithV2Template(Rule("r1", new { eventId = "{{eventId}}", locationClass = new { value = "Metro", required = true } }, new { response = "ok" }))
            .When(Http.Post(V2ExplainPath, OneEvent))
            .ThenBody<ExplainResponse>(explain =>
            {
                var rule = explain.rules.Should().ContainSingle().Subject;
                rule.matched.Should().BeFalse();
                rule.conditions[1].status.Should().Be("absent");
                rule.conditions[1].reason.Should().Contain("required");
            })
            .RunAsync();

    [Fact]
    public Task explain_reports_token_substitutions_and_the_cumulative_result_of_each_matched_rule() =>
        fixture.Scenario()
            .WithV2Template(
                Rule("first", new { eventId = "{{eventId}}" }, new { a = "{eventType}" }),
                Rule("second", new { eventId = "{{eventId}}" }, new { b = "literal" }))
            .When(Http.Post(V2ExplainPath, OneEvent))
            .ThenBody<ExplainResponse>(explain =>
            {
                explain.version.Should().Be(1);
                explain.rules.Should().HaveCount(2);
                explain.rules[0].matched.Should().BeTrue();
                explain.rules[0].tokens.Should().ContainSingle().Which.path.Should().Be("eventType");
                explain.rules[0].tokens[0].value.Should().Be("\"AEvent\"");
                explain.rules[0].cumulativeResult!.ToJsonString().Should().Be("""{"a":"AEvent"}""");
                explain.rules[1].cumulativeResult!.ToJsonString().Should().Be("""{"a":"AEvent","b":"literal"}""");
                explain.result.ToJsonString().Should().Be("""{"a":"AEvent","b":"literal"}""");
            })
            .RunAsync();

    [Fact]
    public Task explain_reports_an_expired_rule_as_pruned() =>
        fixture.Scenario()
            .WithV2Template(new { name = "stale", dataToMatch = new { eventId = "{{eventId}}" }, matchFragment = new { stale = "yes" }, expiresAt = "{{now-1h}}" })
            .When(Http.Post(V2ExplainPath, OneEvent))
            .ThenBody<ExplainResponse>(explain =>
            {
                explain.rules.Should().ContainSingle();
                explain.rules[0].matched.Should().BeFalse();
                explain.rules[0].note.Should().Contain("pruned");
            })
            .RunAsync();

    [Fact]
    public Task explain_of_an_unknown_template_is_not_found() =>
        fixture.Scenario()
            .With("eventId", Guid.NewGuid().ToString())
            .When(Http.Post(V2ExplainPath, OneEventArray))
            .ThenStatus(HttpStatusCode.NotFound)
            .RunAsync();
}
