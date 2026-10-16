using System.Text.Json.Serialization;

namespace Templates.Data;

// The summary of one committed version, stored as field n of K:vmeta in the same transaction as the
// version itself (design 2.1, D3). It lets the history lists answer without reading whole snapshots.
public sealed record VersionMeta(
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    [property: JsonPropertyName("changedBy")] string ChangedBy,
    [property: JsonPropertyName("groups")] string[] Groups);
