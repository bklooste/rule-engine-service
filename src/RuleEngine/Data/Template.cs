using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Templates.Data;

public record DynamicTemplate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dataToMatch")] JsonElement DataToMatch,
    [property: JsonPropertyName("matchFragment")] JsonElement MatchFragment)
{
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    // P-14 rule metadata (§7): the matcher never reads these - they exist so admin tooling can key on a
    // real field instead of a name prefix, and so a per-event rule can carry its own expiry (D-16).
    // All three are omitted when null so a v1 document round-trips byte-identically.
    [JsonPropertyName("group")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Group { get; set; }

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Tags { get; set; }

    // Past expiry means v2's read and evaluate paths skip the rule; it is pruned from storage the next
    // time something rewrites the document, not by a background job (same pragmatism as P-13's prune).
    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ExpiresAt { get; set; }

    internal bool IsExpired(DateTime utcNow) => ExpiresAt is { } expiry && expiry <= utcNow;

    readonly internal JsonProperty[] DataToMatchLazy = [.. DataToMatch.EnumerateObject().Where(x => x.Value.ValueKind != JsonValueKind.Null)];
    readonly internal JsonProperty[] MatchFragmentLazy = [.. MatchFragment.EnumerateObject()];

    //slow but static and cached
    readonly internal JsonObject MatchFragmentAsNodeLazy = JsonObject.Create(MatchFragment, new JsonNodeOptions() { PropertyNameCaseInsensitive = false })
        ?? throw new InvalidOperationException("no node");
};


public record Template(
    [property: JsonPropertyName("templates")] DynamicTemplate[] Templates,
    [property: JsonPropertyName("aggregationField")] string AggregationField,
    [property: JsonPropertyName("changedBy")] string ChangedBy = "Unknown")
{
    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
};






