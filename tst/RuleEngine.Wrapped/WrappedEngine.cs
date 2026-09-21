using System.Buffers;
using System.Text.Json;

namespace RuleEngine.Wrapped;

/// <summary>
/// The wrapped-engine architecture, hand-written and given every advantage: source-generated System.Text.Json
/// for model binding, and rule conditions read straight off typed properties (no reflection, no expression trees).
/// Same rules as the benchmark workload: startsWith(eventType, prefix) AND country ==ci "USA" AND raceNumber > min,
/// matched fragment {rule{i}:"yes", track:{trackName}, meta:{id:{eventId}}}.
/// </summary>
public sealed class WrappedEngine
{
    private readonly (string Prefix, int Min, string Key)[] rules;

    public WrappedEngine(int ruleCount)
    {
        rules = new (string, int, string)[ruleCount];
        for (var i = 0; i < ruleCount; i++)
            rules[i] = (i % 2 == 0 ? "Racing" : "Sport", i % 5, "rule" + i);
    }

    public EvalResult Evaluate(EventDto e) => Evaluate(e.EventType, e.Country, e.RaceNumber, e.TrackName, e.EventId);
    public EvalResult Evaluate(SmallEventDto e) => Evaluate(e.EventType, e.Country, e.RaceNumber, e.TrackName, e.EventId);

    private EvalResult Evaluate(string? type, string? country, int race, string? track, string? eventId)
    {
        var result = new EvalResult();
        foreach (var (prefix, min, key) in rules)
        {
            if (type is null || !type.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(country, "USA", StringComparison.OrdinalIgnoreCase)) continue;
            if (!(race > min)) continue;
            result.Rules[key] = "yes";
            result.Track = track;
            result.Meta = new ResultMeta { Id = eventId };
        }
        return result;
    }

    // bytes in -> model -> rules -> result model -> bytes out
    public int EvaluateDeep(ReadOnlySpan<byte> body, bool single, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        if (single)
            JsonSerializer.Serialize(writer, Evaluate(JsonSerializer.Deserialize(body, WrappedJson.Default.EventDto)!), WrappedJson.Default.EvalResult);
        else
        {
            var events = JsonSerializer.Deserialize(body, WrappedJson.Default.ListEventDto)!;
            var results = new List<EvalResult>(events.Count);
            foreach (var e in events) results.Add(Evaluate(e));
            JsonSerializer.Serialize(writer, results, WrappedJson.Default.ListEvalResult);
        }
        writer.Flush();
        return (int)writer.BytesCommitted;
    }

    public int EvaluateSmall(ReadOnlySpan<byte> body, bool single, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        if (single)
            JsonSerializer.Serialize(writer, Evaluate(JsonSerializer.Deserialize(body, WrappedJson.Default.SmallEventDto)!), WrappedJson.Default.EvalResult);
        else
        {
            var events = JsonSerializer.Deserialize(body, WrappedJson.Default.ListSmallEventDto)!;
            var results = new List<EvalResult>(events.Count);
            foreach (var e in events) results.Add(Evaluate(e));
            JsonSerializer.Serialize(writer, results, WrappedJson.Default.ListEvalResult);
        }
        writer.Flush();
        return (int)writer.BytesCommitted;
    }
}
