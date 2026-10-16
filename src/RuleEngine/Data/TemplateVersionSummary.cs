using System.Text.Json.Serialization;

namespace Templates.Data;

// One entry in a template's version history (P-13). Metadata only - GetVersionDocumentAsync
// fetches the full document for a given version. Groups (design 2.1, D3) lists the groups the version
// changed; it is omitted for a version written before 2.1, where it is unknown.
public record TemplateVersionSummary(
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    [property: JsonPropertyName("changedBy")] string ChangedBy,
    [property: JsonPropertyName("groups"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Groups = null);
