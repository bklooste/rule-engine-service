using System.Text.Json;
using System.Text.Json.Nodes;

using Templates;
using Templates.Data;

namespace ServiceTests.UnitTests;

[Trait("TestType", "Unit")]
public class MatchDynamicRulesEngineTests
{
    static readonly JsonSerializerOptions settings = new(JsonSerializerDefaults.Web);




    [Theory]
    [InlineAutoData("State", "state", true)]
    [InlineAutoData("State", null, true)]
    [InlineAutoData("State", "notState", false)]
    internal void given_eventData_has_state_then_matches(string eventCountry, string templateMatchCountry, bool expectedResult, DummyData eventData)
    {
        eventData.State = eventCountry;
        var template = GetTemplate(null, state: templateMatchCountry);

        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(eventData, settings), template.DataToMatch);

        Assert.Equal(expectedResult, result);
    }

    [Theory]
    [InlineAutoData("A", "a", true)]
    [InlineAutoData("A", null, true)]
    [InlineAutoData("A", "b", false)]
    internal void given_eventData_has_marketType_then_exact_matches(string marketType, string? templateMatchMarketType, bool expectedResult, DummyData eventData)
    {
        var template = GetTemplate(templateMatchMarketType);
        var matcher = JsonObject.Create(JsonSerializer.SerializeToElement(eventData));
        matcher!["marketType"] = marketType;
        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(matcher), template.DataToMatch);
        Assert.Equal(expectedResult, result);
    }





    [Theory]
    [InlineAutoData("ABC", "abc", true)]
    [InlineAutoData("ABC", null, true)]
    [InlineAutoData("ABC", "notABC", false)]
    [InlineAutoData("AB", "ABC", false)]
    internal void given_eventData_has_country_then_matches(string eventCountry, string? templateMatchCountry, bool expectedResult, DummyData eventData)
    {
        eventData.Country = eventCountry;
        var template = GetTemplate(null, null, country: templateMatchCountry);

        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(eventData, settings), template.DataToMatch);

        Assert.Equal(expectedResult, result);
    }

    [Theory]
    [InlineAutoData("ABC", "abc", true)]
    [InlineAutoData("ABC", null, true)]
    [InlineAutoData("ABC", "notABC", false)]
    [InlineAutoData("AB", "ABC", false)]
    internal void given_eventData_has_class_then_matches(string eventClass, string? templateMatchClass, bool expectedResult, DummyData eventData)
    {
        eventData.EventClass = eventClass;
        var template = GetTemplate(null, null, null, eventClass: templateMatchClass);

        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(eventData, settings), template.DataToMatch);

        Assert.Equal(expectedResult, result);
    }

    [Theory]
    [InlineAutoData("ABC", "abc", true)]
    [InlineAutoData("ABC", null, true)]
    [InlineAutoData("ABC", "notABC", false)]
    [InlineAutoData("AB", "ABC", false)]
    internal void given_eventData_has_eventType_then_matches(string eventType, string? templateMatchEventType, bool expectedResult, DummyData eventData)
    {
        eventData.EventType = eventType;
        var template = GetTemplate(null, null, null, null, eventType: templateMatchEventType);

        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(eventData), template.DataToMatch);

        Assert.Equal(expectedResult, result);
    }


    [Theory]
    [InlineAutoData("ABC", "abc", true)]
    [InlineAutoData("ABC", null, true)]
    [InlineAutoData("ABC", "notABC", false)]
    [InlineAutoData("AB", "ABC", false)]
    internal void given_eventData_has_eventId_then_exact_matches(string eventId, string? templateMatchEventId, bool expectedResult, DummyData eventData)
    {
        eventData.EventId = eventId;
        var template = GetTemplate(null, null, null, null, null, eventId: templateMatchEventId);

        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(eventData), template.DataToMatch);

        Assert.Equal(expectedResult, result);
    }

    [Theory]
    [InlineAutoData("ABC", "!cd", true)]
    [InlineAutoData("ABC", "!abc", false)]
    internal void given_eventData_has_eventId_with_notequal_then_exact_matches(string eventId, string? templateMatchEventId, bool expectedResult, DummyData eventData)
    {
        eventData.EventId = eventId;
        var template = GetTemplate(null, null, null, null, null, eventId: templateMatchEventId);

        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(eventData), template.DataToMatch);

        Assert.Equal(expectedResult, result);
    }

    [Theory]
    [InlineAutoData("ABC", "ab*", true)]
    [InlineAutoData("AB", "*ab", true)]
    [InlineAutoData("ABC", "*ab", false)]
    [InlineAutoData("ABC", "*ab*", true)]
    [InlineAutoData("ABC", "*b*", true)]
    [InlineAutoData("ABC", "*", true)]
    [InlineAutoData("ABC", "*bc", true)]
    [InlineAutoData("ABC", "ac*", false)]
    [InlineAutoData("ABC", "*ac", false)]
    internal void given_eventData_with_wild_card_has_eventId_then_exact_matches(string eventId, string? templateMatchEventId, bool expectedResult, DummyData eventData)
    {
        eventData.EventId = eventId;
        var template = GetTemplate(null, null, null, null, null, eventId: templateMatchEventId);

        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(eventData), template.DataToMatch);

        Assert.Equal(expectedResult, result);
    }

    [Theory]
    [InlineAutoData(true, true, true)]
    [InlineAutoData(false, false, true)]
    [InlineAutoData(true, null, true)]
    [InlineAutoData(false, null, true)]
    [InlineAutoData(true, false, false)]
    [InlineAutoData(false, true, false)]
    internal void given_eventData_has_future_then_matches(bool isFuture, bool? templateMatchFuture, bool expectedResult, DummyData eventData)
    {
        eventData.IsFuture = isFuture;
        var template = GetTemplate(null, null, null, null, null, null, templateMatchFuture);

        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(eventData, settings), template.DataToMatch);

        Assert.Equal(expectedResult, result);
    }

    [Theory]
    [InlineAutoData("State", "state", "Country", "country", true)]
    [InlineAutoData("State", "notState", "Country", "country", false)]
    [InlineAutoData("State", "notState", "Country", "notCountry", false)]
    [InlineAutoData("State", "State", "Country", "notCountry", false)]
    internal void given_eventData_has_2_matches_then_matches(string eventState, string templateMatchState, string eventCountry, string templateMatchCountry, bool expectedResult, DummyData eventData)
    {
        eventData.State = eventState;
        eventData.Country = eventCountry;
        var template = GetTemplate(null, state: templateMatchState, country: templateMatchCountry);

        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(eventData), template.DataToMatch);

        Assert.Equal(expectedResult, result);
    }


    [Theory]
    //[InlineAutoData(null , new string[] { }, true)] TODO this fails because its inconclusive it would pass if there was another match in the template
    [InlineAutoData(new string[] { "tag1", "tag2" }, null, true)]
    [InlineAutoData(new string[] { "tag1", "tag2" }, new string[] { }, true)]
    [InlineAutoData(new string[] { "tag1", "tag2" }, new string[] { "tag1" }, true)]
    [InlineAutoData(new string[] { "tag1", "tag2" }, new string[] { "tag3" }, false)]
    [InlineAutoData(new string[] { "tag1", "tag2" }, new string[] { "tag3", "tag4" }, false)]
    [InlineAutoData(new string[] { "tag1", "tag2" }, new string[] { "tag1", "tag3" }, true)]
    [InlineAutoData(new string[] { "tag1" }, new string[] { "tag3", "tag4" }, false)]
    [InlineAutoData(new string[] { "tag1" }, new string[] { "tag1", "tag3" }, true)]
    internal void given_eventData_has_array_then_template_array_matches(string[]? tags, string[]? templateTags, bool expectedResult, DummyData eventData)
    {
        eventData.Tags = tags;
        var template = GetTemplate(null, null, null, null, null, null, null, null, templateTags);
        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(eventData), template.DataToMatch);

        Assert.Equal(expectedResult, result);
    }


    [Theory]
    [InlineAutoData("Racing-Thoroughbred", "Racing-*", true)]
    [InlineAutoData("Racing-Harness",      "Racing-*", true)]
    [InlineAutoData("Racing-Greyhounds",   "Racing-*", true)]
    [InlineAutoData("Exchange-Politics",   "Racing-*", false)]
    [InlineAutoData("Exchange-Prediction", "Racing-*", false)]
    [InlineAutoData("Sports-Football",     "Racing-*", false)]
    [InlineAutoData("Sports-Football",     "Sports-*", true)]
    [InlineAutoData("Exchange-Politics",   "Sports-*", false)]
    internal void given_eventType_prefix_then_matches_only_correct_category(string eventType, string templateEventType, bool expected, DummyData eventData)
    {
        eventData.EventType = eventType;
        var template = GetTemplate(null, null, null, null, eventType: templateEventType);
        var result = EngineHarness.Matches(JsonSerializer.SerializeToElement(eventData), template.DataToMatch);
        Assert.Equal(expected, result);
    }

    static DynamicTemplate GetTemplate(string? marketType = null, string? state = null, string? country = null, string? eventClass = null, string? eventType = null, string? eventId = null, bool? isFuture = null, MarketDefaults? marketDefault = null, string[]? tags = null)
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
            matcher.Add("isFuture", isFuture.Value);
        if (tags != null)
        {
            JsonArray newTags = [.. tags];
            //var tagsSer = JsonSerializer.SerializeToElement(tags, settings);
            matcher.Add("tags", newTags);
        }

        return new DynamicTemplate("name", JsonSerializer.SerializeToElement(matcher, settings), JsonSerializer.SerializeToElement(marketDefault, settings));
    }

    //TODO proper json tests
}
