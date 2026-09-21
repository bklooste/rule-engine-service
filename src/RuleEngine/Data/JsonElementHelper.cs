using System.Text.Json;

namespace Templates
{
    public static class JsonElementHelper
    {
        public const string Class = nameof(Class);
        public const string Condition = nameof(Condition);
        public const string Country = nameof(Country);
        public const string Distance = nameof(Distance);
        public const string MeetingId = nameof(MeetingId);
        public const string Name = nameof(Name);
        public const string RaceNumber = nameof(RaceNumber);
        public const string State = nameof(State);
        public const string Surface = nameof(Surface);
        public const string Weather = nameof(Weather);
        public const string EventType = nameof(EventType);
        public const string EventId = nameof(EventId);
        public const string IsFuture = nameof(IsFuture);
        public const string CustomerId = nameof(CustomerId);
        public const string MarketTypes = nameof(MarketTypes);
        private static readonly char[] separator = ['.'];

        static JsonElement? GetPropertyByName(this JsonElement json, string name)
        {
            var properties = json.EnumerateObject()
                .Where(p => string.Compare(p.Name, name, StringComparison.OrdinalIgnoreCase) == 0);
            if (properties.Count() == 1)
                return properties.Single().Value;
            return null;
        }

        public static JsonElement? GetFromPath(this JsonElement jsonElement, string path)
        {
            if (jsonElement.ValueKind == JsonValueKind.Null ||
                jsonElement.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            string[] segments =
                path.Split(separator, StringSplitOptions.RemoveEmptyEntries);

            for (int n = 0; n < segments.Length; n++)
            {
                jsonElement = jsonElement.TryGetProperty(segments[n], out JsonElement value) ? value : default;

                if (jsonElement.ValueKind == JsonValueKind.Null ||
                    jsonElement.ValueKind == JsonValueKind.Undefined)
                {
                    return null;
                }
            }

            return jsonElement;
        }

        public static string? GetEventId(this JsonElement json) => json.GetPropertyByName(EventId)?.GetString();

        public static string? GetEventType(this JsonElement json) => json.GetPropertyByName(EventType)?.GetString();


        public static IEnumerable<string> GetMarketTypes(this JsonElement json)
        {
            var prop = json.GetPropertyByName(MarketTypes);
            if (prop == null) return [];

            return ((JsonElement)prop).EnumerateArray().Select(x => x.GetString() ?? throw new InvalidOperationException("no eventId")).ToArray();

        }
    }
}
