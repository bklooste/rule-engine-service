using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuleEngine.Wrapped;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(EventDto))]
[JsonSerializable(typeof(List<EventDto>))]
[JsonSerializable(typeof(SmallEventDto))]
[JsonSerializable(typeof(List<SmallEventDto>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(EvalResult))]
[JsonSerializable(typeof(List<EvalResult>))]
public partial class WrappedJson : JsonSerializerContext;
