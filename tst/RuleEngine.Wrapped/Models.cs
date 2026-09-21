using System.Text.Json.Serialization;

namespace RuleEngine.Wrapped;

[JsonConverter(typeof(JsonStringEnumConverter<EventStatus>))]
public enum EventStatus { Scheduled, Open, InPlay, Suspended, Closed, Settled }
[JsonConverter(typeof(JsonStringEnumConverter<SurfaceType>))]
public enum SurfaceType { Turf, Dirt, Synthetic, Hardcourt, Grass }
[JsonConverter(typeof(JsonStringEnumConverter<RunnerStatus>))]
public enum RunnerStatus { Active, Scratched, Withdrawn, Reserve }

// A deliberately realistic "heavy DTO": 4 nesting levels, lists of nested objects, dictionary bags,
// nullables, enums and date types, ~45 scalar/collection properties in total.
public sealed class EventDto
{
    public string Name { get; set; } = "";
    public string EventId { get; set; } = "";
    public string? MeetingId { get; set; }
    public int RaceNumber { get; set; }
    public string TrackName { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTime StartTime { get; set; }
    public string? State { get; set; }
    public string Country { get; set; } = "";
    public EventStatus Status { get; set; }
    public bool IsFuture { get; set; }
    public string? Class { get; set; }
    public int? Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public decimal? PrizeMoney { get; set; }
    public VenueDto? Venue { get; set; }
    public List<CompetitorDto> Competitors { get; set; } = [];
    public List<MarketDto> Markets { get; set; } = [];
    public WeatherDto? Weather { get; set; }
    public SourceDto? Source { get; set; }
    public Dictionary<string, object?> Attributes { get; set; } = [];
    public Dictionary<string, string> Tags { get; set; } = [];
}

public sealed class VenueDto
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Timezone { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public SurfaceType Surface { get; set; }
    public TrackDto? Track { get; set; }
}

public sealed class TrackDto
{
    public string Condition { get; set; } = "";
    public int? Rating { get; set; }
    public double DistanceMetres { get; set; }
    public DateTime? LastWatered { get; set; }
    public Dictionary<string, string> Notes { get; set; } = [];
}

public sealed class CompetitorDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Number { get; set; }
    public int? Barrier { get; set; }
    public RunnerStatus Status { get; set; }
    public decimal? Weight { get; set; }
    public DateTime? Born { get; set; }
    public TrainerDto? Trainer { get; set; }
    public List<FormDto> Form { get; set; } = [];
    public Dictionary<string, object?> Attributes { get; set; } = [];
}

public sealed class TrainerDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Location { get; set; }
    public int? Wins { get; set; }
}

public sealed class FormDto
{
    public DateTimeOffset Date { get; set; }
    public string Venue { get; set; } = "";
    public int? Position { get; set; }
    public int Starters { get; set; }
    public decimal? Margin { get; set; }
}

public sealed class MarketDto
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public bool BettingOpen { get; set; }
    public List<SelectionDto> Selections { get; set; } = [];
}

public sealed class SelectionDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal? Price { get; set; }
    public Dictionary<string, string> Meta { get; set; } = [];
}

public sealed class WeatherDto
{
    public double? TempC { get; set; }
    public string? Sky { get; set; }
    public int? WindKph { get; set; }
}

public sealed class SourceDto
{
    public string Provider { get; set; } = "";
    public string? Feed { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

// Small payload variant: the flat ~12-field event the earlier benchmarks used.
public sealed class SmallEventDto
{
    public string Name { get; set; } = "";
    public string EventId { get; set; } = "";
    public string MeetingId { get; set; } = "";
    public int RaceNumber { get; set; }
    public string TrackName { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTime StartTime { get; set; }
    public string State { get; set; } = "";
    public string Country { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsFuture { get; set; }
    public string Class { get; set; } = "";
}

public sealed class EvalResult
{
    public string? Track { get; set; }
    public ResultMeta? Meta { get; set; }
    // rule{i}: "yes" entries flattened into the top-level object, like the engine's merged output.
    [JsonExtensionData] public Dictionary<string, object?> Rules { get; set; } = [];
}

public sealed class ResultMeta { public string? Id { get; set; } }
