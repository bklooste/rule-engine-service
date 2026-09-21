using System.Text;
using System.Text.Json.Nodes;

using BenchmarkDotNet.Running;

using RuleEngine.Wrapped;

// `dotnet run -c Release --project tst/RuleEngine.Benchmarks -- emit <dir>` writes the shared payloads and the
// template bodies used by the HTTP load runs (so both hosts see byte-identical requests and equivalent rules).
if (args.Length == 2 && args[0] == "emit")
{
    Directory.CreateDirectory(args[1]);
    File.WriteAllBytes(Path.Combine(args[1], "small1.json"), Payloads.Bytes(Payloads.Small, false));
    File.WriteAllBytes(Path.Combine(args[1], "small100.json"), Payloads.Bytes(Payloads.Small, true));
    File.WriteAllBytes(Path.Combine(args[1], "deep1.json"), Payloads.Bytes(Payloads.Deep, false));
    File.WriteAllBytes(Path.Combine(args[1], "deep100.json"), Payloads.Bytes(Payloads.Deep, true));
    foreach (var n in new[] { 10, 100 })
    {
        var rules = new JsonArray();
        for (var i = 0; i < n; i++)
            rules.Add(JsonNode.Parse($$$$"""
                {"name":"r{{{{i}}}}","dataToMatch":{"eventType":"{{{{(i % 2 == 0 ? "Racing" : "Sport")}}}}*","country":"USA","raceNumber":">{{{{i % 5}}}}"},
                 "matchFragment":{"rule{{{{i}}}}":"yes","track":"{trackName}","meta":{"id":"{eventId}"}}}
                """));
        File.WriteAllText(Path.Combine(args[1], $"t{n}.json"), new JsonObject { ["templates"] = rules, ["aggregationField"] = "name" }.ToJsonString());
    }
    foreach (var f in new[] { "small1", "small100", "deep1", "deep100" })
        Console.WriteLine($"{f}.json {new FileInfo(Path.Combine(args[1], f + ".json")).Length} bytes");
    return 0;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;
