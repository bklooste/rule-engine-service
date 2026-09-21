using System.Text.Json.Serialization;

namespace Templates.Data;

// One entry in a template's version history (P-13). Metadata only - GetVersionDocumentAsync
// fetches the full document for a given version.
public record TemplateVersionSummary(
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    [property: JsonPropertyName("changedBy")] string ChangedBy);
