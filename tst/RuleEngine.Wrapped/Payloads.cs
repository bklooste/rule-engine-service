using System.Text;
using System.Text.Json;

namespace RuleEngine.Wrapped;

/// <summary>Single-event payloads use index 7 (Racing, raceNumber 7: every Racing rule matches, so about half of all rules match). One shared payload generator for the benchmarks and the HTTP load files, so both engines see identical bytes.</summary>
public static class Payloads
{
    public static string Small(int i) => $$"""
        {"name":"Race {{i}} - Maiden Special Weight","eventId":"10820{{i}}","meetingId":"470757036917785845","raceNumber":{{i % 8}},
         "trackName":"Canterbury Park","eventType":"{{(i % 3 == 0 ? "Sport-Soccer" : "Racing-Thoroughbred")}}","startTime":"2023-07-30T00:07:00Z",
         "state":"MN","country":"USA","status":"Open","isFuture":false,"class":"MAIDEN SPECIAL WEIGHT"}
        """.ReplaceLineEndings("");

    public static string Deep(int i)
    {
        var start = new DateTime(2023, 7, 30, 0, 7, 0, DateTimeKind.Utc);
        var e = new EventDto
        {
            Name = $"Race {i} - Maiden Special Weight", EventId = $"10820{i}", MeetingId = "470757036917785845", RaceNumber = i % 8,
            TrackName = "Canterbury Park", EventType = i % 3 == 0 ? "Sport-Soccer" : "Racing-Thoroughbred", StartTime = start,
            State = "MN", Country = "USA", Status = EventStatus.Open, IsFuture = false, Class = "MAIDEN SPECIAL WEIGHT",
            Version = 7, CreatedAt = start.AddDays(-30), UpdatedAt = start.AddMinutes(-5), PrizeMoney = 25000.50m,
            Venue = new VenueDto
            {
                Code = "CBY", Name = "Canterbury Park", Timezone = "America/Chicago", Latitude = 44.7, Longitude = -93.4, Surface = SurfaceType.Dirt,
                Track = new TrackDto
                {
                    Condition = "Fast", Rating = 3, DistanceMetres = 1207.5, LastWatered = start.AddDays(-1),
                    Notes = { ["rail"] = "3 metres out", ["inspection"] = "passed", ["sealed"] = "false" },
                },
            },
            Weather = new WeatherDto { TempC = 24.5, Sky = "Clear", WindKph = 12 },
            Source = new SourceDto { Provider = "example-feed", Feed = "racing", ReceivedAt = start.AddMinutes(-2) },
            Attributes =
            {
                ["broadcast"] = "FS1", ["hasPhotoFinish"] = true, ["purse"] = 25000, ["ratio"] = 1.75, ["notes"] = null,
                ["sponsors"] = new[] { "Acme", "Globex", "Initech" }, ["extra"] = new Dictionary<string, object?> { ["a"] = 1, ["b"] = "two" },
            },
            Tags = { ["region"] = "midwest", ["tier"] = "B", ["source"] = "feed", ["channel"] = "retail", ["lang"] = "en", ["priority"] = "3" },
        };
        for (var c = 0; c < 8; c++)
        {
            var comp = new CompetitorDto
            {
                Id = $"c{i}-{c}", Name = $"Runner Number {c}", Number = c + 1, Barrier = c % 3 == 0 ? null : c + 2, Status = RunnerStatus.Active,
                Weight = 55.5m + c, Born = new DateTime(2019, 3, 1 + c, 0, 0, 0, DateTimeKind.Utc),
                Trainer = new TrainerDto { Id = $"t{c}", Name = $"Trainer {c}", Location = "Shakopee", Wins = 100 + c },
                Attributes = { ["colour"] = "bay", ["age"] = 4 + c % 3, ["gelding"] = c % 2 == 0, ["silks"] = "red and white" },
            };
            for (var f = 0; f < 4; f++)
                comp.Form.Add(new FormDto { Date = start.AddDays(-14 * (f + 1)), Venue = "Canterbury Park", Position = f + 1, Starters = 9, Margin = 1.25m * f });
            e.Competitors.Add(comp);
        }
        for (var m = 0; m < 3; m++)
        {
            var mk = new MarketDto { Id = $"m{i}-{m}", Type = m == 0 ? "winner" : "place", BettingOpen = true };
            for (var s = 0; s < 8; s++)
                mk.Selections.Add(new SelectionDto { Id = $"s{m}-{s}", Name = $"Runner Number {s}", Price = 2.5m + s, Meta = { ["fixed"] = "true", ["src"] = "feed" } });
            e.Markets.Add(mk);
        }
        return JsonSerializer.Serialize(e, WrappedJson.Default.EventDto);
    }

    public static byte[] Bytes(Func<int, string> one, bool batch) =>
        Encoding.UTF8.GetBytes(batch ? "[" + string.Join(",", Enumerable.Range(0, 100).Select(one)) + "]" : one(7));
}
