using System.Text.Json;

using FluentAssertions;

using ServiceTests.UnitTests;

using Templates;
using Templates.Data;

namespace UnitTests;

[Trait("TestType", "Unit")]
public class TransformRulesEngineTests
{
    static readonly JsonSerializerOptions settings = new(JsonSerializerDefaults.Web);


    [Theory]
    [AutoData]
    internal void given_match_then_transform_applies(DummyData eventData)
    {
        var matchResultFragment = new { aState = "{state}" };
        eventData.State = "state";
        var template = GetTemplate(new { state = eventData.State }, matchResultFragment);

        var response = EngineHarness.Apply([template], JsonSerializer.SerializeToElement(eventData, settings));

        eventData.State.Should().BeEquivalentTo(response["aState"]!.ToString());
    }

    [Theory]
    [AutoData]
    internal void given_match_when_int_then_transform_applies(DummyData eventData)
    {
        var matchResultFragment = new { aState = "{aInt}" };
        eventData.State = "state";
        var template = GetTemplate(new { state = eventData.State }, matchResultFragment);

        var response = EngineHarness.Apply([template], JsonSerializer.SerializeToElement(eventData, settings));

        eventData.aInt.ToString().Should().BeEquivalentTo(response["aState"]!.ToString());
    }


    [Theory]
    [AutoData]
    internal void given_match_when_array_then_transform_applies(DummyData eventData)
    {
        var matchResultFragment = new { aState = "{tags}" };
        eventData.State = "state";
        var template = GetTemplate(new { state = eventData.State }, matchResultFragment);

        var response = EngineHarness.Apply([template], JsonSerializer.SerializeToElement(eventData, settings));

        eventData.Tags.Should().BeEquivalentTo(response["aState"]!.AsArray().Select(x => x!.ToString()));
    }


    [Theory]
    [AutoData]
    internal void given_match_when_int_then_transform_emits_a_real_number(DummyData eventData)
    {
        var matchResultFragment = new { aState = "{aInt}" };
        eventData.State = "state";
        var template = GetTemplate(new { state = eventData.State }, matchResultFragment);

        var response = EngineHarness.Apply([template], JsonSerializer.SerializeToElement(eventData, settings));

        response["aState"]!.GetValueKind().Should().Be(JsonValueKind.Number);
        response["aState"]!.GetValue<decimal>().Should().Be(eventData.aInt);
    }

    [Theory]
    [AutoData]
    internal void given_top_level_number_fragment_then_it_is_not_dropped(DummyData eventData)
    {
        var matchResultFragment = new { order = 5 };
        eventData.State = "state";
        var template = GetTemplate(new { state = eventData.State }, matchResultFragment);

        var response = EngineHarness.Apply([template], JsonSerializer.SerializeToElement(eventData, settings));

        response["order"].Should().NotBeNull();
        response["order"]!.GetValueKind().Should().Be(JsonValueKind.Number);
        response["order"]!.GetValue<int>().Should().Be(5);
    }

    [Theory]
    [AutoData]
    internal void given_top_level_boolean_fragment_then_it_is_not_dropped(DummyData eventData)
    {
        var matchResultFragment = new { disable = true };
        eventData.State = "state";
        var template = GetTemplate(new { state = eventData.State }, matchResultFragment);

        var response = EngineHarness.Apply([template], JsonSerializer.SerializeToElement(eventData, settings));

        response["disable"].Should().NotBeNull();
        response["disable"]!.GetValueKind().Should().Be(JsonValueKind.True);
        response["disable"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [AutoData]
    internal void given_deep_match_then_transform_applies(string deepField)
    {
        var post = new
        {
            deep = new { f2 = deepField }
        };
        // bracers in result will replace in source
        var matchResultFragment = new { aField = "{deep.f2}" };
        var template = new DynamicTemplate("name", JsonSerializer.SerializeToElement(new { }, settings), JsonSerializer.SerializeToElement(matchResultFragment, settings));

        var response = EngineHarness.Apply([template], JsonSerializer.SerializeToElement(post, settings));

        deepField.Should().BeEquivalentTo(response["aField"]!.ToString());
    }

    [Theory]
    [AutoData]
    internal void given_root_match_then_transform_applies(string deepField)
    {
        var post = new
        {
            deep = new { f2 = deepField }
        };
        // bracers in result will replace in source
        var matchResultFragment = new { aField = "{root}" };
        var template = new DynamicTemplate("name", JsonSerializer.SerializeToElement(new { }, settings), JsonSerializer.SerializeToElement(matchResultFragment, settings));

        var response = EngineHarness.Apply([template], JsonSerializer.SerializeToElement(post, settings));

        //should compare that whole root has been put there but cant be A . so just check a field
        deepField.Should().BeEquivalentTo(response["aField"]?["deep"]?["f2"]?.ToString());
    }

    //TODO make sure child is not escaped

    [Theory]
    [AutoData]
    internal void given_sub_object_then_transform_applies(string aField)
    {
        var post = new
        {
            field = aField
        };
        // bracers in result will replace in source
        var matchResultFragment = new { obj = new { subField = "{field}" } };
        var template = new DynamicTemplate("name", JsonSerializer.SerializeToElement(new { }, settings), JsonSerializer.SerializeToElement(matchResultFragment, settings));

        var response = EngineHarness.Apply([template], JsonSerializer.SerializeToElement(post, settings));

        var obj = response["obj"];
        Assert.NotNull(obj);
        aField.Should().BeEquivalentTo(obj["subField"]!.ToString());
    }

    static DynamicTemplate GetTemplate(object match, object matchResultFragment)
    {
        return new DynamicTemplate("name", JsonSerializer.SerializeToElement(match, settings), JsonSerializer.SerializeToElement(matchResultFragment, settings));
    }
}
