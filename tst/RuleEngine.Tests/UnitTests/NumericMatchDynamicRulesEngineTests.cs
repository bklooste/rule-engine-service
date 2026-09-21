using System.Text.Json;
using System.Text.Json.Nodes;

using Templates;
using Templates.Data;

namespace ServiceTests.UnitTests;

[Trait("TestType", "Unit")]
public class NumericMatchDynamicRulesEngineTests
{
    static readonly JsonSerializerOptions settings = new(JsonSerializerDefaults.Web);


    // D-6: ago(N) reads as N in the PAST, so startTime is set N seconds ago and ">{ago(1h)}" means
    // "more recent than one hour ago". v1's opposite (future-pointing) sign is gone with v1 (P-20).
    [Theory]
    [InlineAutoData(">{ago(1h)}", 3700, false)]
    [InlineAutoData(">{ago(1h)}", 3500, true)]
    [InlineAutoData("<{ago(1h)}", 3700, true)]
    [InlineAutoData("<{ago(1h)}", 3500, false)]
    [InlineAutoData("<{ago(1m)}", 70, true)]
    [InlineAutoData("<{ago(1m)}", 50, false)]
    [InlineAutoData("<{ago(1d)}", 86500, true)]
    [InlineAutoData("<{ago(1d)}", 86300, false)]
    internal void given_eventData_with_start_time_then_ago_matched_correctly(string templateString, int secondsAgo, bool expectedResult, DummyData eventData)
    {
        eventData.StartTime = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(secondsAgo);

        var templateMatchBlock = new JsonObject
        {
                  { "startTime", templateString }
        };
        var template = GetTemplate(templateMatchBlock);
        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(eventData, settings), template.DataToMatch);
        Assert.Equal(expectedResult, result);
    }

    [Theory]
    [InlineAutoData(">1", 3, true)]
    [InlineAutoData(">3", 1, false)]
    [InlineAutoData(">3", -1, false)]
    [InlineAutoData("<2", 1, true)]
    [InlineAutoData("<2", -1, true)]
    [InlineAutoData("<1", 3, false)]
    [InlineAutoData("==1", 1, true)]
    [InlineAutoData("==1", 3, false)]
    [InlineAutoData("== 1", 1, true)]
    [InlineAutoData("==0", 0, true)]
    [InlineAutoData("==0", 1, false)]

    internal void given_eventData_with_numeric_then_matched_correctly(string templateString, int val, bool expectedResult, DummyData eventData)
    {
        eventData.aInt = val;
        var templateMatchBlock = new JsonObject
        {
                  { "aint", templateString }
        };
        var template = GetTemplate(templateMatchBlock);
        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(eventData, settings), template.DataToMatch);
        Assert.Equal(expectedResult, result);
    }

    // dummy data as long


    static DynamicTemplate GetTemplate(JsonObject matcher, MarketDefaults? marketDefault = null)
    {
        marketDefault ??= new MarketDefaults();


        return new DynamicTemplate("name", JsonSerializer.SerializeToElement(matcher, settings), JsonSerializer.SerializeToElement(marketDefault, settings));
    }

    //TODO proper json tests
}
