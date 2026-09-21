namespace Templates.Data;

// Bound from the "TemplateApi" config section. A4: v1 hard-coded "1 to 100" events per request in
// APIValidator; v2 takes the limit from here and names it in the error.
public sealed class TemplateApiOptions
{
    public const string SectionName = "TemplateApi";

    public int MaxEventsPerRequest { get; set; } = 100;

    // The cap applied to one v2/templates/evaluate call: templateIds x events.
    public int MaxTemplateIdsPerRequest { get; set; } = 10;
}
