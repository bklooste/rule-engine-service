using System.Text.Json;
using System.Text.Json.Nodes;

namespace Templates.Compiled;

// A {path} token found in a match fragment while compiling it, pre-split; scan depth mirrors today's
// transform (top level + one object deep) since widening that is latent bug 3 (D-5), out of scope here.
internal sealed record CompiledFragmentToken(string Key, string? ObjectKey, string Path, string[] PathSegments)
{
    public bool IsRoot => Path == "root";
}

// A match fragment pre-scanned into literal parts and {path} tokens (§1). Shared by the compile path and
// the transform/merge in CompiledTransform/MergePlan; v1's CompiledRule/CompiledTemplate are gone (P-20).
internal sealed class CompiledFragment
{
    public JsonObject Source { get; }

    public IReadOnlyList<CompiledFragmentToken> Tokens { get; }

    public bool HasTokens => Tokens.Count > 0;

    private CompiledFragment(JsonObject source, CompiledFragmentToken[] tokens)
    {
        Source = source;
        Tokens = tokens;
    }

    public static CompiledFragment Compile(JsonObject fragment)
    {
        var tokens = new List<CompiledFragmentToken>();
        foreach (var element in fragment)
        {
            var kind = element.Value?.GetValueKind();
            if (kind == JsonValueKind.String)
            {
                if (TryToken(element.Value!, element.Key, null, out var token))
                    tokens.Add(token!);
            }
            else if (kind == JsonValueKind.Object)
            {
                foreach (var inner in element.Value!.AsObject())
                {
                    if (inner.Value?.GetValueKind() != JsonValueKind.String)
                        continue;
                    if (!inner.Value.GetValue<string>().Contains('{'))
                        continue;
                    if (TryToken(inner.Value, element.Key, inner.Key, out var token))
                        tokens.Add(token!);
                }
            }
        }
        return new CompiledFragment(fragment, [.. tokens]);
    }

    private static bool TryToken(JsonNode node, string key, string? objectKey, out CompiledFragmentToken? token)
    {
        token = null;
        var path = node.GetValue<string>().GetNestedString();
        // "ago(...)" inside a fragment is not a path - the old transform skips it and so do we.
        if (string.IsNullOrEmpty(path) || path.StartsWith("ago(", StringComparison.Ordinal))
            return false;

        token = new CompiledFragmentToken(key, objectKey, path, path.Split('.', StringSplitOptions.RemoveEmptyEntries));
        return true;
    }
}
