using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Templates.Api;
using Templates.Data;

namespace Templates;


[JsonSerializable(typeof(JsonElement[]))]
[JsonSerializable(typeof(DynamicTemplate[]))]
[JsonSerializable(typeof(DynamicTemplate))]
[JsonSerializable(typeof(Template))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonObject[]))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(TemplateVersionSummary))]
[JsonSerializable(typeof(TemplateVersionSummary[]))]
[JsonSerializable(typeof(RollbackResult))]
[JsonSerializable(typeof(long))]
//[JsonSerializable(typeof((string name, JsonObject state)[]))]
[JsonSerializable(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))]
[JsonSerializable(typeof(Microsoft.AspNetCore.Http.HttpValidationProblemDetails))]
[JsonSerializable(typeof(DynamicTemplate[]))]
[JsonSerializable(typeof(Microsoft.AspNetCore.Mvc.NotFoundResult))]
[JsonSerializable(typeof(bool?))]
// P-14: the v2 surface's request/response shapes.
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(EvaluateRequest))]
[JsonSerializable(typeof(MultiEvaluateResponse))]
[JsonSerializable(typeof(Dictionary<string, JsonObject>))]
[JsonSerializable(typeof(Dictionary<string, JsonObject>[]))]
[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(ExplainResponse))]
[JsonSerializable(typeof(ExplainRule))]
[JsonSerializable(typeof(ExplainRule[]))]
[JsonSerializable(typeof(ExplainCondition[]))]
[JsonSerializable(typeof(ExplainToken[]))]
[JsonSerializable(typeof(WriteAck))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
    internal static AppJsonSerializerContext GlobalDefault = new(new(JsonSerializerDefaults.Web));

}
