using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using BenchmarkDotNet.Attributes;

using Templates;
using Templates.Compiled;
using Templates.Data;

namespace RuleEngine.Benchmarks;

// In-process benchmarks of the engine's real serving path (no HTTP, no Redis). Every rule has three
// conditions (wildcard, equality, numeric compare) and a fragment with two {path} tokens; roughly half the
// rules match the sample events. "Evaluate" rows start from the event's UTF-8 bytes and end with the
// response bytes, i.e. the same span of work the service does per request minus the HTTP hop.
// The "Naive" rows are a deliberately simple in-repo baseline (JsonNode tree + hand-interpreted conditions +
// DeepClone merge). It is NOT another rule engine; it only shows what the compile-once/no-tree design saves.
[MemoryDiagnoser]
public class EngineBenchmarks
{
    [Params(1, 10, 100)]
    public int Rules { get; set; }

    private Template flatTemplate = null!;
    private Template logicTemplate = null!;
    private CompiledV2Template flat = null!;
    private CompiledV2Template logic = null!;
    private byte[] oneEvent = null!;
    private byte[] batchEvents = null!;
    private JsonElement oneParsed;
    private JsonDocument oneDoc = null!;
    private JsonNode[] naiveRules = null!;
    private readonly ArrayBufferWriter<byte> output = new(64 * 1024);
    private readonly DateTime now = DateTime.UtcNow;

    [GlobalSetup]
    public void Setup()
    {
        var flatRules = new JsonArray();
        var logicRules = new JsonArray();
        var naive = new List<JsonNode>();
        for (var i = 0; i < Rules; i++)
        {
            var prefix = i % 2 == 0 ? "Racing" : "Sport";
            var min = i % 5;
            flatRules.Add(JsonNode.Parse($$$$"""
                {"name":"r{{{{i}}}}","dataToMatch":{"eventType":"{{{{prefix}}}}*","country":"USA","raceNumber":">{{{{min}}}}"},
                 "matchFragment":{"rule{{{{i}}}}":"yes","track":"{trackName}","meta":{"id":"{eventId}"}}}
                """));
            logicRules.Add(JsonNode.Parse($$$$"""
                {"name":"r{{{{i}}}}","dataToMatch":{"and":[
                   {"startsWith":[{"var":"eventType"},"{{{{prefix}}}}"]},
                   {"==ci":[{"var":"country"},"USA"]},
                   {">":[{"var":"raceNumber"},{{{{min}}}}]}]},
                 "matchFragment":{"rule{{{{i}}}}":"yes","track":"{trackName}","meta":{"id":"{eventId}"}}}
                """));
            naive.Add(JsonNode.Parse($$$$"""{"p":"{{{{prefix}}}}","min":{{{{min}}}},"frag":{"rule{{{{i}}}}":"yes","track":"{trackName}","meta":{"id":"{eventId}"}}}""")!);
        }
        naiveRules = [.. naive];
        flatTemplate = ParseTemplate(flatRules);
        logicTemplate = ParseTemplate(logicRules);
        flat = CompiledV2Template.Compile(flatTemplate);
        logic = CompiledV2Template.Compile(logicTemplate);

        oneEvent = Encoding.UTF8.GetBytes(EventJson(0));
        batchEvents = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Range(0, 100).Select(EventJson)) + "]");
        oneDoc = JsonDocument.Parse(oneEvent);
        oneParsed = oneDoc.RootElement;
    }

    private static Template ParseTemplate(JsonArray rules) =>
        JsonSerializer.Deserialize(new JsonObject { ["templates"] = rules, ["aggregationField"] = "name" }.ToJsonString(), AppJsonSerializerContext.GlobalDefault.Template)!;

    private static string EventJson(int i) => $$"""
        {"name":"Race {{i}} - Maiden Special Weight","eventId":"10820{{i}}","meetingId":"470757036917785845","raceNumber":{{i % 8}},
         "trackName":"Canterbury Park","eventType":"{{(i % 3 == 0 ? "Sport-Soccer" : "Racing-Thoroughbred")}}","startTime":"2023-07-30T00:07:00Z",
         "state":"MN","country":"USA","status":"Open","isFuture":false,"class":"MAIDEN SPECIAL WEIGHT"}
        """;

    // ---- compile (once per template version, never per request) ----
    [Benchmark] public object Compile_Flat() => CompiledV2Template.Compile(flatTemplate);
    [Benchmark] public object Compile_JsonLogic() => CompiledV2Template.Compile(logicTemplate);

    // ---- match only, one pre-parsed event ----
    [Benchmark] public int Match_Flat() => MatchCount(flat);
    [Benchmark] public int Match_JsonLogic() => MatchCount(logic);

    private int MatchCount(CompiledV2Template t)
    {
        var index = new EventPropertyIndex(oneParsed);
        var n = 0;
        foreach (var rule in t.Rules)
            if (rule.Matches(index)) n++;
        return n;
    }

    // ---- full: parse event bytes -> match -> merge plan -> UTF-8 response bytes ----
    [Benchmark] public int Evaluate_1Event() => Evaluate(flat, oneEvent, single: true);
    [Benchmark] public int Evaluate_100Events() => Evaluate(flat, batchEvents, single: false);
    [Benchmark] public int Evaluate_JsonLogic_1Event() => Evaluate(logic, oneEvent, single: true);

    private int Evaluate(CompiledV2Template t, byte[] body, bool single)
    {
        output.ResetWrittenCount();
        using var doc = JsonDocument.Parse(body);
        using var writer = new Utf8JsonWriter(output);
        if (single)
            DynamicRulesEngine.ApplyV2TemplatesToPlan(t, doc.RootElement, now).WriteTo(writer);
        else
        {
            writer.WriteStartArray();
            foreach (var e in doc.RootElement.EnumerateArray())
                DynamicRulesEngine.ApplyV2TemplatesToPlan(t, e, now).WriteTo(writer);
            writer.WriteEndArray();
        }
        writer.Flush();
        return output.WrittenCount;
    }

    // ---- naive baseline (same rules, same output shape, no compilation) ----
    [Benchmark] public int Naive_Evaluate_1Event() => NaiveEvaluate(oneEvent, single: true);
    [Benchmark] public int Naive_Evaluate_100Events() => NaiveEvaluate(batchEvents, single: false);

    private int NaiveEvaluate(byte[] body, bool single)
    {
        var root = JsonNode.Parse(body)!;
        if (single)
            return NaiveOne(root.AsObject()).ToJsonString().Length;
        var results = new JsonArray();
        foreach (var e in root.AsArray())
            results.Add(NaiveOne(e!.AsObject()));
        return results.ToJsonString().Length;
    }

    private JsonObject NaiveOne(JsonObject evt)
    {
        var result = new JsonObject();
        var type = evt["eventType"]?.GetValue<string>();
        var country = evt["country"]?.GetValue<string>();
        var race = evt["raceNumber"]?.GetValue<decimal>();
        foreach (var rule in naiveRules)
        {
            if (type == null || !type.StartsWith(rule["p"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(country, "USA", StringComparison.OrdinalIgnoreCase)) continue;
            if (race == null || !(race > rule["min"]!.GetValue<decimal>())) continue;

            foreach (var (key, value) in rule["frag"]!.AsObject())
            {
                var clone = value!.DeepClone();
                if (clone is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith('{'))
                    clone = evt[s.Trim('{', '}')]?.DeepClone();
                else if (clone is JsonObject o)
                    foreach (var (k, inner) in o.ToArray())
                        if (inner is JsonValue iv && iv.TryGetValue<string>(out var t) && t.StartsWith('{'))
                            o[k] = evt[t.Trim('{', '}')]?.DeepClone();
                result[key] = clone;
            }
        }
        return result;
    }
}
