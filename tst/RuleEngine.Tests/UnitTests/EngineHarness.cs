using System.Text.Json;
using System.Text.Json.Nodes;

using Templates;
using Templates.Compiled;
using Templates.Data;

namespace ServiceTests.UnitTests;

// P-20 deleted v1's interpreted EngineMatching and its CompiledRule/CompiledTemplate serving path, so the
// engine unit tests drive v2's compile path instead. The matching, transform and merge semantics these
// tests assert are the ones v2 kept (D-11 stayed loose-by-default); only ago()'s sign differs (D-6), and
// the tests that use ago() were rewritten to read in the past.
internal static class EngineHarness
{
    internal static bool Matches(JsonElement eventData, JsonElement dataToMatch) =>
        CompiledV2Rule.Matches(CompiledV2Rule.CompileConditions(dataToMatch), new EventPropertyIndex(eventData));

    internal static JsonObject Apply(DynamicTemplate[] rules, JsonElement eventData, string aggregationField = "name") =>
        DynamicRulesEngine.ApplyV2Templates(CompiledV2Template.Compile(new Template(rules, aggregationField)), eventData, DateTime.UtcNow);
}
