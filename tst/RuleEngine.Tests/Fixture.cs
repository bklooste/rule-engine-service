using Scenarify;

using Xunit;

[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "test names")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "CA1707", Justification = "test names")]

namespace ServiceTests;

// Each scenario stores its own template, named {{guid}}.
public sealed class Fixture() : ServiceTestFixture(new ServiceTestOptions { Settings = Settings })
{
    // The per-pattern history configuration a consumer of 2.1 runs (design 2.1, D4): ids starting with
    // "promotion-" keep at least their last 5 versions and keep ended rules for 380 days. Every other id,
    // including every {{guid}} a scenario writes, takes the defaults.
    public static readonly IReadOnlyDictionary<string, string> Settings = new Dictionary<string, string>
    {
        ["TemplateHistory__Rules__0__Pattern"] = "promotion-*",
        ["TemplateHistory__Rules__0__RetentionDays"] = "1",
        ["TemplateHistory__Rules__0__MinVersions"] = "5",
        ["TemplateHistory__Rules__0__ExpiredRuleRetentionDays"] = "380",
        ["TemplateHistory__Rules__1__Pattern"] = "loyalty-*",
        ["TemplateHistory__Rules__1__RetentionDays"] = "0",
    };
}

[CollectionDefinition(nameof(ServiceTestCollection))]
public sealed class ServiceTestCollection : ICollectionFixture<Fixture>;

public static class TemplateScenarios
{
    public const string TemplatePath = "v2/templates/{{guid}}";
    public const string MatchPath = "v2/templates/{{guid}}/evaluate";

    /// <summary>A template document named {{guid}} holding one rule per (match, fragment) pair.</summary>
    public static Scenario WithTemplate(this Scenario scenario, params (object match, object fragment)[] rules) =>
        scenario
            .With("eventId", Guid.NewGuid().ToString())
            .With("templates", rules.Select(r => new { id = "1", name = "{{guid}}", createdAt = "{{now}}", dataToMatch = r.match, matchFragment = r.fragment }))
            .Given(Http.Put(TemplatePath, "cases/templates/template.json"));

    // P-14. The v2 routes; rules are passed whole so a scenario can name them individually (rule-level
    // endpoints) and carry the group/tags/expiresAt metadata.
    public const string V2TemplatePath = "v2/templates/{{guid}}";
    public const string V2EvaluatePath = "v2/templates/{{guid}}/evaluate";
    public const string V2ExplainPath = "v2/templates/{{guid}}/explain";

    public static Scenario WithV2Template(this Scenario scenario, params object[] rules) =>
        scenario
            .With("eventId", Guid.NewGuid().ToString())
            .With("templates", rules)
            .Given(Http.Put(V2TemplatePath, "cases/templates/template.json"));

    public static object Rule(string name, object dataToMatch, object matchFragment) =>
        new { name, createdAt = "{{now}}", dataToMatch, matchFragment };
}
