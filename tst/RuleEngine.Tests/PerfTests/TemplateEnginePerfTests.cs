using System.Buffers;
using System.Diagnostics;
using System.Text.Json;

using Templates;
using Templates.Compiled;
using Templates.Data;

namespace ServiceTests.PerfTests;

// P-02/P-11's harness. Since P-20 deleted v1, the interpreted and v1-compiled baseline rows are gone and
// every row measures the v2 serving path; the P-11 gate numbers stay in the plan's "P-07 as built"
// section as the historical record. Runs both the brand and override templates against post.json. Asserts correctness only (deserialize
// produced templates, match produced results) - a CI box's timings aren't a contract, same as
// OrderBookPerfTests/WalletLoadLatencyTests elsewhere in the repo. Median time/bytes per request are
// written to test output for humans to compare against the plan's table by eye.
// Run with: dotnet test --filter "TestType=PerfTest"
[Trait("TestType", "PerfTest")]
public class TemplateEnginePerfTests
{
    private const int Iterations = 200;
    private const int WarmupIterations = 20;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string TestDataDir = Path.Combine(AppContext.BaseDirectory, "testData");

    private static readonly AppJsonSerializerContext Ctx = AppJsonSerializerContext.GlobalDefault;

    [Theory]
    [InlineData("currentTemplate.json", "brand template")]
    [InlineData("currentOverride.json", "override template")]
    public void full_benchmark_table(string templateFile, string label)
    {
        var templateBytes = File.ReadAllBytes(Path.Combine(TestDataDir, templateFile));
        var postBytes = File.ReadAllBytes(Path.Combine(TestDataDir, "post.json"));

        // sanity: the fixtures actually parse before we start timing them
        var template = JsonSerializer.Deserialize(templateBytes, Ctx.Template);
        Assert.NotNull(template);
        var events = JsonSerializer.Deserialize(postBytes, Ctx.JsonElementArray);
        Assert.NotNull(events);
        Assert.NotEmpty(events!);

        var report = new List<string>
        {
            $"### {label} ({templateFile}, {templateBytes.Length / 1024.0:F0} KB) vs post.json ({events!.Length} events, {postBytes.Length / 1024.0:F1} KB)",
            "",
            "| Step | time/request (median) | allocated/request (median) |",
            "|---|---|---|",
        };

        Measure(report, "Template deserialize (cache miss)",
            () => JsonSerializer.Deserialize(templateBytes, Ctx.Template));

        Measure(report, "Parse request body",
            () => JsonSerializer.Deserialize(postBytes, Ctx.JsonElementArray));

        // P-20: v1 (the interpreted engine and its compiled serving path) is gone, so every row below is
        // the v2 path offer-products actually calls. Compilation is not in the per-request loops - it
        // happens once per template version (D-2), in DynamicRulesEngine's version-keyed cache.
        var compiledTemplate = CompiledV2Template.Compile(template!);
        var now = DateTime.UtcNow;

        Measure(report, "Match only, compiled", () =>
        {
            var matchCount = 0;
            foreach (var evt in events)
            {
                var index = new EventPropertyIndex(evt);
                foreach (var rule in compiledTemplate.Rules)
                    if (rule.Matches(index))
                        matchCount++;
            }
            return matchCount;
        });

        Measure(report, "Compile template (once per version, not per request)",
            () => CompiledV2Template.Compile(template!));

        Measure(report, "Match + transform + merge, JsonObject", () =>
            events.Select(evt => DynamicRulesEngine.ApplyV2Templates(compiledTemplate, evt, now)).ToArray());

        var results = events.Select(evt => DynamicRulesEngine.ApplyV2Templates(compiledTemplate, evt, now)).ToArray();
        Measure(report, "Serialize response",
            () => JsonSerializer.Serialize(results, Ctx.JsonObjectArray));

        // P-07: the merge plan written straight to a writer over the response body - no JsonObject tree.
        // The response buffer and writer stand in for ASP.NET's pooled PipeWriter/Utf8JsonWriter, so they
        // are reused across iterations rather than counted as per-request allocation.
        var responseBuffer = new ArrayBufferWriter<byte>(64 * 1024);
        var responseWriter = new Utf8JsonWriter(responseBuffer);
        Measure(report, "Match + transform + merge + write, plan (P-07)", () =>
        {
            responseBuffer.ResetWrittenCount();
            responseWriter.Reset(responseBuffer);
            var writer = responseWriter;
            writer.WriteStartArray();
            foreach (var evt in events)
                DynamicRulesEngine.ApplyV2TemplatesToPlan(compiledTemplate, evt, now).WriteTo(writer);
            writer.WriteEndArray();
            writer.Flush();
            return responseBuffer.WrittenCount;
        });

        // What a real request actually costs once the template is compiled and cached (D-2). Everything a
        // warm POST v2/templates/{id}/evaluate does is here: parse the body, match/transform/merge on the
        // compiled rules, write the merged result to the response body.
        Measure(report, "Full end-to-end, plan, cache-warm (production path, P-07 on v2)", () =>
        {
            var evts = JsonSerializer.Deserialize(postBytes, Ctx.JsonElementArray)!;
            responseBuffer.ResetWrittenCount();
            responseWriter.Reset(responseBuffer);
            var writer = responseWriter;
            writer.WriteStartArray();
            foreach (var evt in evts)
                DynamicRulesEngine.ApplyV2TemplatesToPlan(compiledTemplate, evt, now).WriteTo(writer);
            writer.WriteEndArray();
            writer.Flush();
            return responseBuffer.WrittenCount;
        });

        // The same warm request on the pre-P-07 JsonObject path, for comparison.
        Measure(report, "Full end-to-end, JsonObject, cache-warm (pre-P-07)", () =>
        {
            var evts = JsonSerializer.Deserialize(postBytes, Ctx.JsonElementArray)!;
            var res = evts.Select(evt => DynamicRulesEngine.ApplyV2Templates(compiledTemplate, evt, now)).ToArray();
            return JsonSerializer.Serialize(res, Ctx.JsonObjectArray);
        });

        var text = string.Join(Environment.NewLine, report);
        Console.WriteLine(text);
        TestContext.Current.TestOutputHelper?.WriteLine(text);
    }

    // Runs action WarmupIterations times unmeasured (JIT, first-call costs), then Iterations times
    // measured, and appends the median time and allocated bytes per call to report as a markdown row.
    private static void Measure(List<string> report, string step, Func<object?> action)
    {
        for (var i = 0; i < WarmupIterations; i++)
            action();

        var timesMs = new double[Iterations];
        var allocatedBytes = new long[Iterations];

        for (var i = 0; i < Iterations; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            action();
            timesMs[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocatedBytes[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var medianMs = Median(timesMs);
        var medianBytes = Median(allocatedBytes.Select(b => (double)b).ToArray());

        report.Add($"| {step} | {FormatMicros(medianMs)} | {medianBytes / 1024.0:F1} KB |");
    }

    private static string FormatMicros(double ms) => ms < 1
        ? $"{ms * 1000:F0} us"
        : $"{ms:F2} ms";

    private static double Median(double[] values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }
}
