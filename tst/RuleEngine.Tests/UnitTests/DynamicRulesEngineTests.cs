using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using Templates;
using Templates.Data;

[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "test names")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "CA1707", Justification = "test names")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "CA1825", Justification = "test names")]

namespace ServiceTests.UnitTests;



[Trait("TestType", "Unit")]
public class DynamicRulesEngineTests
{
    static readonly JsonSerializerOptions settings = new(JsonSerializerDefaults.Web);


    [Theory]
    [AutoData]
    internal void given_match_then_fragment_applies(DummyData eventData, MarketDefaults marketFragment)
    {
        eventData.State = "state";
        var template = GetTemplate(null, state: "state", null, null, null, null, null, marketFragment);

        var response = EngineHarness.Apply([template], JsonSerializer.SerializeToElement(eventData, settings));
        var result = response.Deserialize<MarketDefaults>(settings);

        marketFragment.Should().BeEquivalentTo(result);
    }

    [Theory]
    [AutoData]
    internal void given_match_then_fragment_aggregates(DummyData eventData, MarketDefaults marketFragment, Product product)
    {
        eventData.State = "state";
        marketFragment.Products = [product];
        var template = GetTemplate(null, state: "state", null, null, null, null, null, marketFragment);



        var response = EngineHarness.Apply([template, template, template], JsonSerializer.SerializeToElement(eventData, settings));
        var result = response.Deserialize<MarketDefaults>(settings);

        Assert.NotNull(result);
        result.Products.Should().HaveCount(1);
        product.Should().BeEquivalentTo(result.Products?.First());
    }

    [Theory]
    [AutoData]
    internal void given_match_then_fragment_aggregates_by_field(DummyData eventData, MarketDefaults marketFragment, MarketDefaults marketFragment2, Product product1, Product product2)
    {
        eventData.State = "state";
        marketFragment.Products = [product1];
        marketFragment2.Products = [product2];

        var template1 = GetTemplate(null, state: "state", null, null, null, null, null, marketFragment);
        var template2 = GetTemplate(null, state: "state", null, null, null, null, null, marketFragment2);


        var response = EngineHarness.Apply([template1, template2], JsonSerializer.SerializeToElement(eventData, settings), "dummy");
        var result = response.Deserialize<MarketDefaults>(settings);

        Assert.NotNull(result);
        result.Products.Should().HaveCount(2);
    }

    [Theory]
    [AutoData]
    internal void given_match_then_fragment_aggregates_by_field_w_name(DummyData eventData, MarketDefaults marketFragment, MarketDefaults marketFragment2, Product product1, Product product2)
    {
        eventData.State = "state";
        marketFragment.Products = [product1];
        marketFragment2.Products = [product2];

        var template1 = GetTemplate(null, state: "state", null, null, null, null, null, marketFragment);
        var template2 = GetTemplate(null, state: "state", null, null, null, null, null, marketFragment2);


        var response = EngineHarness.Apply([template1, template2], JsonSerializer.SerializeToElement(eventData, settings), "name");
        var result = response.Deserialize<MarketDefaults>(settings);

        Assert.NotNull(result);
        result.Products.Should().HaveCount(2);
    }

    [Theory]
    [AutoData]
    internal void given_match_then_fragment_aggregates_by_field_w_same_name(DummyData eventData, MarketDefaults marketFragment, MarketDefaults marketFragment2, Product product1, Product product2)
    {
        eventData.State = "state";
        marketFragment.Products = [product1];
        product2 = new Product(product1.Name, product2.Code, product2.Description, product2.Disable);
        marketFragment2.Products = [product2];

        var template1 = GetTemplate(null, state: "state", null, null, null, null, null, marketFragment);
        var template2 = GetTemplate(null, state: "state", null, null, null, null, null, marketFragment2);


        var response = EngineHarness.Apply([template1, template2], JsonSerializer.SerializeToElement(eventData, settings), "name");
        var result = response.Deserialize<MarketDefaults>(settings);

        Assert.NotNull(result);
        result.Products.Should().HaveCount(1);
    }


    [Theory]
    [AutoData]
    internal void given_no_match_then_fragment_not_applied(DummyData eventData, MarketDefaults marketFragment)
    {
        eventData.State = "state";
        var template = GetTemplate(null, state: "abc", null, null, null, null, null, marketFragment);

        var response = EngineHarness.Apply([template], JsonSerializer.SerializeToElement(eventData, settings));
        var result = response.Deserialize<MarketDefaults>(settings);

        marketFragment.Should().NotBeEquivalentTo(result);
    }

    [Theory]
    [AutoData]
    internal void given_multiple_match_then_last_fragment_applies(DummyData eventData, MarketDefaults marketFragment, MarketDefaults lastFragment)
    {
        eventData.State = "state";
        var template1 = GetTemplate(null, state: "state", null, null, null, null, null, marketFragment);
        var template2 = GetTemplate(null, state: "state", null, null, null, null, null, lastFragment);

        var response = EngineHarness.Apply([template1, template2], JsonSerializer.SerializeToElement(eventData, settings));
        var result = response.Deserialize<MarketDefaults>(settings);

        lastFragment.Should().BeEquivalentTo(result, x => x.Excluding(x => x!.Products));
    }

    [Theory]
    [AutoData]
    internal void given_multiple_match_then_parts_from_all_fragments_applies_ignoring_null(DummyData eventData, MarketDefaults firstFragment, MarketDefaults lastFragment)
    {
        firstFragment.Limits = null;
        lastFragment.Selection = null;
        eventData.State = "state";
        var template1 = GetTemplate(null, state: "state", null, null, null, null, null, firstFragment);
        var template2 = GetTemplate(null, state: "state", null, null, null, null, null, lastFragment);

        var response = EngineHarness.Apply([template1, template2], JsonSerializer.SerializeToElement(eventData, settings));
        var result = response.Deserialize<MarketDefaults>(settings);

        lastFragment.Limits.Should().BeEquivalentTo(result?.Limits);
        firstFragment.Selection.Should().BeEquivalentTo(result?.Selection);
    }

    [Theory]
    [AutoData]
    internal void given_multiple_match_with_dif_code_then_products_accumulate(DummyData eventData, MarketDefaults marketFragment, MarketDefaults lastFragment)
    {
        eventData.State = "state";
        var template1 = GetTemplate(null, state: "state", null, null, null, null, null, marketFragment);
        var template2 = GetTemplate(null, state: "state", null, null, null, null, null, lastFragment);

        var response = EngineHarness.Apply([template1, template2], JsonSerializer.SerializeToElement(eventData, settings));

        var productNamesInResponse = response["products"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray();
        foreach (var prod in lastFragment.Products!)
            Assert.Contains(prod.Name, productNamesInResponse);
        foreach (var prod in marketFragment.Products!)
            Assert.Contains(prod.Name, productNamesInResponse);
    }

    [Theory]
    [AutoData]
    internal void given_multiple_match_with_same_code_then_last_products_merge_by_name(DummyData eventData, MarketDefaults marketFragment, MarketDefaults lastFragment)
    {
        lastFragment.Products = marketFragment.Products;
        eventData.State = "state";
        var template1 = GetTemplate(null, state: "state", null, null, null, null, null, marketFragment);
        var template2 = GetTemplate(null, state: "state", null, null, null, null, null, lastFragment);

        var response = EngineHarness.Apply([template1, template2], JsonSerializer.SerializeToElement(eventData, settings));

        var productNamesInResponse = response["products"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray();
        foreach (var prod in lastFragment?.Products!)
            Assert.Single(productNamesInResponse, x => x.Contains(prod.Name));
    }

    static DynamicTemplate GetTemplate(string? marketType = null, string? state = null, string? country = null, string? eventClass = null, string? eventType = null, string? eventId = null, bool? isFuture = null, MarketDefaults? marketDefault = null)
    {
        marketDefault ??= new MarketDefaults();
        var matcher = new JsonObject();
        if (marketType != null)
            matcher.Add("marketType", marketType);
        if (state != null)
            matcher.Add("state", state);
        if (country != null)
            matcher.Add("country", country);
        if (eventClass != null)
            matcher.Add("eventClass", eventClass);
        if (eventType != null)
            matcher.Add("eventType", eventType);
        if (eventId != null)
            matcher.Add("eventId", eventId);

        if (isFuture != null)
            matcher.Add("isFuture", isFuture.Value.ToString()); //TODO bool 

        return new DynamicTemplate("name", JsonSerializer.SerializeToElement(matcher, settings), JsonSerializer.SerializeToElement(marketDefault, settings));
    }

    //TODO proper json tests
}
