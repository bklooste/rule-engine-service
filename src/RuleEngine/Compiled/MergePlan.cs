using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Templates.Compiled;

// P-07 (D-3 step 2): the merged response as a plan instead of a JsonNode tree.
//
// A single forward-only Utf8JsonWriter pass cannot do the merge on its own - array-by-aggregationField
// merging needs random access to "is there already an element with this name", and object/scalar keys can
// be overwritten by a later rule. So the plan is the smallest structure that still supports those lookups:
// an ordered key list plus, per key, either a reference to a node the compiled fragment already owns, or a
// tiny overlay describing which properties a later rule replaced. Nothing is cloned and no JsonObject is
// built - the shared immutable fragment nodes are written straight to the response writer at the end.
//
// Key order (and therefore the goldens) has to match CompiledTransform exactly:
//  - top level: existing key keeps its position when overwritten, a new key is appended (JsonObject order);
//  - an object with {path} tokens: untouched properties in source order, substituted ones appended last;
//  - an array element merged into: replaced properties in place, new properties appended.
internal sealed class MergePlan
{
    private readonly List<string> keys = [];
    private readonly List<object?> values = [];
    private readonly Dictionary<string, int> slots = new(StringComparer.Ordinal);

    // A fragment object copied by reference, with the {path} substitutions that move to the end of it.
    internal sealed class PlanObject(JsonObject source, List<KeyValuePair<string, JsonNode?>> replacements)
    {
        public JsonObject Source { get; } = source;

        public List<KeyValuePair<string, JsonNode?>> Replacements { get; } = replacements;
    }

    // One array element: the shared source node plus the shallow property merges later rules applied.
    internal sealed class PlanElement(JsonNode? source)
    {
        public JsonNode? Source { get; } = source;

        public List<KeyValuePair<string, JsonNode>>? Overlay { get; private set; }

        private string? aggregationField;
        private string? aggregationValue;

        // The merge scans every existing element for a matching aggregation-field value, so the value is
        // read (and stringified) once per element instead of once per comparison.
        public string? AggregationKey(string field)
        {
            if (aggregationField != null && string.Equals(aggregationField, field, StringComparison.Ordinal))
                return aggregationValue;

            aggregationField = field;
            aggregationValue = Lookup(field)?.ToString();
            return aggregationValue;
        }

        public void Set(string key, JsonNode value)
        {
            if (aggregationField != null && string.Equals(aggregationField, key, StringComparison.Ordinal))
                aggregationField = null;

            Overlay ??= [];
            for (var i = 0; i < Overlay.Count; i++)
                if (string.Equals(Overlay[i].Key, key, StringComparison.Ordinal))
                {
                    Overlay[i] = new(key, value);
                    return;
                }
            Overlay.Add(new(key, value));
        }

        public JsonNode? Lookup(string key)
        {
            if (Overlay != null)
                foreach (var entry in Overlay)
                    if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                        return entry.Value;
            return Source?.AsObject()?[key];
        }
    }

    // Property names come from a small fixed set (the compiled fragments), so transcoding and escaping each
    // one once is much cheaper than letting the writer redo it on every property of every response.
    private static readonly ConcurrentDictionary<string, JsonEncodedText> EncodedNames = new(StringComparer.Ordinal);

    private static JsonEncodedText Encoded(string name) =>
        EncodedNames.TryGetValue(name, out var encoded) ? encoded : EncodedNames.GetOrAdd(name, JsonEncodedText.Encode(name));

    internal bool TryGet(string key, out object? value)
    {
        if (slots.TryGetValue(key, out var slot))
        {
            value = values[slot];
            return true;
        }
        value = null;
        return false;
    }

    internal void Set(string key, object? value)
    {
        if (slots.TryGetValue(key, out var slot))
            values[slot] = value;
        else
        {
            slots[key] = keys.Count;
            keys.Add(key);
            values.Add(value);
        }
    }

    internal bool ContainsKey(string key) => slots.ContainsKey(key);

    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        for (var i = 0; i < keys.Count; i++)
        {
            writer.WritePropertyName(Encoded(keys[i]));
            WriteValue(writer, values[i]);
        }
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonNode node:
                node.WriteTo(writer);
                return;
            case PlanObject planObject:
                WriteObject(writer, planObject);
                return;
            case List<PlanElement> elements:
                writer.WriteStartArray();
                foreach (var element in elements)
                    WriteElement(writer, element);
                writer.WriteEndArray();
                return;
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, PlanObject planObject)
    {
        writer.WriteStartObject();
        foreach (var property in planObject.Source)
        {
            if (Replaces(planObject.Replacements, property.Key))
                continue;
            writer.WritePropertyName(Encoded(property.Key));
            WriteNode(writer, property.Value);
        }
        foreach (var replacement in planObject.Replacements)
        {
            writer.WritePropertyName(Encoded(replacement.Key));
            WriteNode(writer, replacement.Value);
        }
        writer.WriteEndObject();
    }

    private static bool Replaces(List<KeyValuePair<string, JsonNode?>> replacements, string key)
    {
        foreach (var replacement in replacements)
            if (string.Equals(replacement.Key, key, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static void WriteElement(Utf8JsonWriter writer, PlanElement element)
    {
        if (element.Overlay == null)
        {
            WriteNode(writer, element.Source);
            return;
        }

        var source = element.Source!.AsObject();
        writer.WriteStartObject();
        foreach (var property in source)
        {
            writer.WritePropertyName(Encoded(property.Key));
            var overlaid = false;
            foreach (var entry in element.Overlay)
                if (string.Equals(entry.Key, property.Key, StringComparison.Ordinal))
                {
                    WriteNode(writer, entry.Value);
                    overlaid = true;
                    break;
                }
            if (!overlaid)
                WriteNode(writer, property.Value);
        }

        // Overlay keys the source element never had are appended, in the order they were merged in.
        foreach (var entry in element.Overlay)
            if (!ContainsOrdinal(source, entry.Key))
            {
                writer.WritePropertyName(Encoded(entry.Key));
                WriteNode(writer, entry.Value);
            }
        writer.WriteEndObject();
    }

    private static bool ContainsOrdinal(JsonObject source, string key)
    {
        foreach (var property in source)
            if (string.Equals(property.Key, key, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        if (node == null)
            writer.WriteNullValue();
        else
            node.WriteTo(writer);
    }
}
