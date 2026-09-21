using System.Text.Json;

namespace Templates.Compiled;

// §2: one case-insensitive property lookup per posted event, shared by every compiled condition
// evaluated against it - replaces the per-template EnumerateObject + per-condition LINQ scan in
// EngineMatching.CheckTemplateMatch. Built lazily on first lookup, so a rule with no conditions never
// touches the event - keeps today's behaviour where an empty dataToMatch matches a non-object payload.
internal sealed class EventPropertyIndex(JsonElement postedData)
{
    private Dictionary<string, JsonElement>? properties;
    private HashSet<string>? ambiguous;

    public JsonElement PostedData => postedData;

    public bool TryGetProperty(string name, out JsonElement value)
    {
        properties ??= Build();
        if (ambiguous != null && ambiguous.Contains(name))
            // The old matcher used Enumerable.Single() over case-insensitive name matches, so two
            // properties differing only in case threw. Kept identical rather than quietly picking one.
            throw new InvalidOperationException("Sequence contains more than one matching element");

        return properties.TryGetValue(name, out value);
    }

    private Dictionary<string, JsonElement> Build()
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in postedData.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
                continue;

            if (!map.TryAdd(property.Name, property.Value))
                (ambiguous ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(property.Name);
        }
        return map;
    }
}
