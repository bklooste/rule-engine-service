using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;

using Scenarify;

namespace ServiceTests;

[Collection(nameof(ServiceTestCollection))]
public class HealthTests(Fixture fixture)
{
    [Fact]
    public async Task Health_returns_200_when_redis_is_connected()
    {
        var response = await fixture.Client().GetAsync("health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_is_served()
    {
        var response = await fixture.Client().GetAsync("openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("v2/templates/{templateId}", await response.Content.ReadAsStringAsync());
    }
}

public class ConfigValidationTests
{
    [Theory]
    [InlineData("Templates:KeyPrefix", "has spaces!")]
    [InlineData("Templates:TenantKeyFormat", "no-placeholder")]
    [InlineData("Templates:ChangedByHeader", " ")]
    public void Bad_config_fails_fast_on_start(string key, string value)
    {
        var validator = new Templates.Data.TemplatesOptionsValidator();
        var options = new Templates.Data.TemplatesOptions();
        typeof(Templates.Data.TemplatesOptions).GetProperty(key.Split(':')[1])!.SetValue(options, value);
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(key, result.FailureMessage);
    }

    [Fact]
    public void Defaults_are_valid()
    {
        Assert.True(new Templates.Data.TemplatesOptionsValidator().Validate(null, new()).Succeeded);
    }
}
