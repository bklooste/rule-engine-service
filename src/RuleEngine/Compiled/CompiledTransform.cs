using System.Text.Json;
using System.Text.Json.Nodes;

namespace Templates.Compiled;

// P-06 (D-3 step 1): transform and merge a matched fragment straight into the running response, with the
// redundant clones gone. The interpreted path deep-clones the fragment, clones each element again in the
// merge, again per array element and again per property; a compiled fragment is immutable and shared, so
// the only copies left here are the ones that genuinely get a new parent in the response.
//
// Output has to stay byte-identical to the interpreted path (goldens, P-01), which includes key order:
// the old transform removes and re-adds every token-substituted property, so a substituted property moves
// to the end of its object. That reordering is reproduced below rather than removed.
internal static class CompiledTransform
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    public static void MergeFragment(JsonObject current, CompiledFragment fragment, JsonElement postedData, string aggregationField, ILogger? logger)
    {
        var tokens = fragment.Tokens;
        JsonNode?[] values = tokens.Count == 0 ? [] : new JsonNode?[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
            values[i] = Resolve(tokens[i], postedData, logger);

        foreach (var element in fragment.Source)
        {
            if (HasResolvedTopLevelToken(tokens, values, element.Key))
                continue;
            MergeProperty(current, element.Key, element.Value, null, tokens, values, aggregationField, logger);
        }

        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].ObjectKey == null && values[i] != null)
                MergeProperty(current, tokens[i].Key, fragment.Source[tokens[i].Key], values[i], tokens, values, aggregationField, logger);
    }

    // P-07 (D-3 step 2): the same merge, but into a MergePlan instead of a JsonObject. Every branch below
    // mirrors the JsonObject one above line for line - the only difference is that nothing is cloned, since
    // the plan holds references to the immutable compiled fragment and the response is written from it.
    public static void MergeFragment(MergePlan current, CompiledFragment fragment, JsonElement postedData, string aggregationField, ILogger? logger)
    {
        var tokens = fragment.Tokens;
        JsonNode?[] values = tokens.Count == 0 ? [] : new JsonNode?[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
            values[i] = Resolve(tokens[i], postedData, logger);

        foreach (var element in fragment.Source)
        {
            if (HasResolvedTopLevelToken(tokens, values, element.Key))
                continue;
            MergeProperty(current, element.Key, element.Value, null, tokens, values, aggregationField, logger);
        }

        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].ObjectKey == null && values[i] != null)
                MergeProperty(current, tokens[i].Key, fragment.Source[tokens[i].Key], values[i], tokens, values, aggregationField, logger);
    }

    private static void MergeProperty(MergePlan current, string key, JsonNode? literal, JsonNode? replacement, IReadOnlyList<CompiledFragmentToken> tokens, JsonNode?[] values, string aggregationField, ILogger? logger)
    {
        var effective = replacement ?? literal;
        if (effective == null)
            return;

        switch (effective.GetValueKind())
        {
            case JsonValueKind.Array:
                MergeArray(current, key, effective.AsArray(), aggregationField, logger);
                return;
            case JsonValueKind.Object:
                current.Set(key, BuildObjectPlan(key, effective.AsObject(), replacement != null, tokens, values));
                return;
            case JsonValueKind.String:
                // The interpreted path round-trips the string through JsonValue.Create, which re-escapes it;
                // writing the source node raw would keep the original escapes, so keep the round trip.
                var stringValue = effective.GetValue<string>();
                if (stringValue != null)
                    current.Set(key, JsonValue.Create(stringValue));
                return;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                current.Set(key, effective);
                return;
        }
    }

    private static void MergeArray(MergePlan current, string key, JsonArray source, string aggregationField, ILogger? logger)
    {
        if (!current.TryGet(key, out var existingSlot))
        {
            var fresh = new List<MergePlan.PlanElement>(source.Count);
            foreach (var arrayElement in source)
                fresh.Add(new MergePlan.PlanElement(arrayElement));
            current.Set(key, fresh);
            return;
        }

        logger?.LogDebug("merge sub template {Name} replacing", key);
        if (existingSlot is not List<MergePlan.PlanElement> entries)
            return;

        foreach (var arrayElement in source)
        {
            if (arrayElement == null)
                continue;

            var aggregationFieldData = arrayElement[aggregationField]?.ToString();
            MergePlan.PlanElement? existing = null;
            if (aggregationFieldData != null)
                foreach (var entry in entries)
                    if (entry.Source != null && entry.AggregationKey(aggregationField) == aggregationFieldData)
                    {
                        existing = entry;
                        break;
                    }

            if (existing != null)
            {
                foreach (var item in arrayElement.AsObject())
                    if (item.Value != null)
                        existing.Set(item.Key, item.Value);
            }
            else
                entries.Add(new MergePlan.PlanElement(arrayElement));
        }
    }

    // Token-free objects are written straight off the shared fragment; one with inner {path} tokens becomes
    // a PlanObject, which writes the untouched properties in source order and the substituted ones last.
    private static object BuildObjectPlan(string key, JsonObject source, bool ownsSource, IReadOnlyList<CompiledFragmentToken> tokens, JsonNode?[] values)
    {
        if (ownsSource)
            return source;

        List<KeyValuePair<string, JsonNode?>>? replacements = null;
        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].ObjectKey != null && values[i] != null && tokens[i].Key == key)
            {
                replacements ??= [];
                replacements.Add(new(tokens[i].ObjectKey!, values[i]));
            }

        return replacements == null ? source : new MergePlan.PlanObject(source, replacements);
    }

    private static bool HasResolvedTopLevelToken(IReadOnlyList<CompiledFragmentToken> tokens, JsonNode?[] values, string key)
    {
        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].ObjectKey == null && values[i] != null && tokens[i].Key == key)
                return true;
        return false;
    }

    // replacement, when set, is a freshly built node this call owns and can hand straight to the response.
    private static void MergeProperty(JsonObject current, string key, JsonNode? literal, JsonNode? replacement, IReadOnlyList<CompiledFragmentToken> tokens, JsonNode?[] values, string aggregationField, ILogger? logger)
    {
        var effective = replacement ?? literal;
        if (effective == null)
            return;

        var kind = effective.GetValueKind();
        switch (kind)
        {
            case JsonValueKind.Array:
                MergeArray(current, key, effective.AsArray(), replacement != null, aggregationField, logger);
                return;
            case JsonValueKind.Object:
                current[key] = BuildObject(key, effective.AsObject(), replacement != null, tokens, values);
                return;
            case JsonValueKind.String:
                var stringValue = effective.GetValue<string>();
                if (stringValue != null)
                    current[key] = stringValue;
                return;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                // D-5 fix 1: top-level numbers/booleans used to be dropped here.
                current[key] = replacement != null ? effective : effective.DeepClone();
                return;
        }
    }

    private static void MergeArray(JsonObject current, string key, JsonArray source, bool ownsSource, string aggregationField, ILogger? logger)
    {
        if (!current.ContainsKey(key))
        {
            // No dedup on first insert - two fragment entries with the same aggregation field both survive,
            // as they do today.
            current.Add(key, ownsSource ? source : source.DeepClone().AsArray());
            return;
        }

        logger?.LogDebug("merge sub template {Name} replacing", key);
        var existingNode = current[key];
        if (existingNode == null || existingNode.GetValueKind() != JsonValueKind.Array)
            return;

        var currentArray = existingNode.AsArray();
        foreach (var arrayElement in source)
        {
            if (arrayElement == null)
                continue;

            var aggregationFieldData = arrayElement[aggregationField]?.ToString();
            var existing = aggregationFieldData == null
                ? null
                : currentArray.FirstOrDefault(x => x != null && x.AsObject()?[aggregationField]?.ToString() == aggregationFieldData);

            if (existing != null)
            {
                // Shallow merge straight off the shared fragment element: one clone per property that
                // actually lands in the response, instead of cloning the element and then its properties.
                var target = existing.AsObject();
                foreach (var item in arrayElement.AsObject())
                    if (item.Value != null)
                        target[item.Key] = item.Value.DeepClone();
            }
            else
                currentArray.Add(arrayElement.DeepClone());
        }
    }

    // A fragment's object value is shared and already parented, so it is copied once here. Inner {path}
    // tokens are substituted while copying, and land last in the object - see the key-order note above.
    private static JsonObject BuildObject(string key, JsonObject source, bool ownsSource, IReadOnlyList<CompiledFragmentToken> tokens, JsonNode?[] values)
    {
        if (ownsSource)
            return source;

        var hasInnerTokens = false;
        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].ObjectKey != null && values[i] != null && tokens[i].Key == key)
            {
                hasInnerTokens = true;
                break;
            }

        if (!hasInnerTokens)
            return source.DeepClone().AsObject();

        var result = new JsonObject();
        foreach (var inner in source)
            if (!IsReplacedInner(tokens, values, key, inner.Key))
                result[inner.Key] = inner.Value?.DeepClone();

        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].ObjectKey != null && values[i] != null && tokens[i].Key == key)
                result[tokens[i].ObjectKey!] = values[i];

        return result;
    }

    private static bool IsReplacedInner(IReadOnlyList<CompiledFragmentToken> tokens, JsonNode?[] values, string key, string innerKey)
    {
        for (var i = 0; i < tokens.Count; i++)
            if (values[i] != null && tokens[i].Key == key && tokens[i].ObjectKey == innerKey)
                return true;
        return false;
    }

    // Reads the token's path off the posted event with the paths pre-split at compile time (P-05).
    // Returns null when the path is absent or the value kind has no replacement today.
    private static JsonNode? Resolve(CompiledFragmentToken token, JsonElement postedData, ILogger? logger)
    {
        JsonElement? found = token.IsRoot ? postedData : GetFromPath(postedData, token.PathSegments);
        if (found == null)
        {
            logger?.LogWarning("property in template not found {Name}", token.Path);
            return null;
        }

        var value = found.Value;
        return value.ValueKind switch
        {
            JsonValueKind.Array => JsonArray.Create(value, NodeOptions),
            JsonValueKind.String => JsonValue.Create(value.ToString()),
            // D-5 fix 2: a numeric token used to be stringified here.
            JsonValueKind.Number => JsonValue.Create(value.GetDecimal()),
            JsonValueKind.Object => JsonObject.Create(value),
            _ => null,
        };
    }

    private static JsonElement? GetFromPath(JsonElement element, string[] segments)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        foreach (var segment in segments)
        {
            element = element.TryGetProperty(segment, out var value) ? value : default;
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
        }

        return element;
    }
}
