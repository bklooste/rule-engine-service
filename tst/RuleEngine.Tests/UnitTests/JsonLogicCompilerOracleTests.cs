using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Templates;
using Templates.Compiled;

namespace ServiceTests.UnitTests;

// P-21's required oracle test, re-anchored onto v2's own frontends by P-20 (v1's interpreted
// EngineMatching and CompiledRule are gone). Every generated condition is emitted twice from one source of
// truth - once as the flat string-DSL dataToMatch and once as its JsonLogic equivalent from the §1a
// mapping table - and v2's two frontends (CompiledV2Rule's flat-DSL path and its JsonLogic path) must
// agree on every randomised event. The equivalence this proves is still live: v2 serves un-migrated
// flat-DSL documents alongside migrated JsonLogic ones. Agreement includes which exception is thrown,
// since several malformed template/event pairings fail at match time and neither frontend changes that.
// No property-based framework is in use in this repo, so this is a seeded generator over many iterations;
// the seed and the failing condition are reported in the assertion message.
//
// Time: ago() reads the clock per evaluation on both sides and both compile with v2's sign (D-6, past),
// so generated event times stay >= 60s away from every generated ago() boundary - the reads are
// microseconds apart, so nothing here is clock-flaky.
[Trait("TestType", "Unit")]
public class JsonLogicCompilerOracleTests
{
    private const int IterationsPerSeed = 4000;

    private static readonly string[] StringValues =
    [
        "AUS", "NZL", "aus", "GBR", "Racing-Thoroughbred", "Racing-Greyhounds", "Racing-Harness",
        "Sports-Soccer", "Metro", "Country", "Provincial", "win", "tote_win", "exchange", "vip",
        "new", "gawler", "daytona beach", "milan", "1672396", "true", "false", "1.5", "-3", "0", "",
    ];

    private static readonly string[] PropertyNames =
    [
        "eventType", "startTime", "marketTypes", "country", "locationClass", "customerTags",
        "venueName", "eventId", "isSport", "distance", "raceNumber", "betId", "type",
    ];

    private static readonly int[] EventTimeOffsets = [-200000, -50000, -5000, -2500, -1000, 0, 1000, 2500, 5000, 50000, 200000];

    private static readonly (int Count, string Unit)[] AgoOffsets = [(1, "m"), (30, "m"), (1, "h"), (12, "h"), (1, "d")];

    private enum Kind
    {
        Equality, Negation, EndsWith, StartsWith, Contains, BareStar,
        GreaterThan, LessThan, NumericEqual, AgoGreaterThan, AgoLessThan,
        ArrayIntersection, Boolean, Null, UnsupportedNumber, MalformedAgo,
    }

    [Theory]
    [InlineData(20260918)]
    [InlineData(1)]
    [InlineData(987654321)]
    [InlineData(424242)]
    [InlineData(7)]
    [InlineData(31337)]
    public void jsonlogic_frontend_agrees_with_string_dsl_frontend_on_random_conditions(int seed)
    {
        var random = new Random(seed);
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        var kindsSeen = new Dictionary<Kind, int>();

        for (var iteration = 0; iteration < IterationsPerSeed; iteration++)
        {
            var eventData = RandomEvent(random);
            var clauses = RandomClauses(random, eventData);
            foreach (var clause in clauses)
                kindsSeen[clause.Kind] = kindsSeen.GetValueOrDefault(clause.Kind) + 1;

            var outcome = AssertAgree(seed, iteration, eventData, clauses);
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
        }

        // Every operator in the mapping table has to have been exercised, and the outcomes have to have a
        // spread - a generator that only ever produced "false" would agree trivially.
        foreach (var kind in Enum.GetValues<Kind>())
            Assert.True(kindsSeen.GetValueOrDefault(kind) > 20, $"operator {kind} barely generated: {kindsSeen.GetValueOrDefault(kind)}");

        Assert.True(outcomes.GetValueOrDefault("True") > 100, $"too few matches: {Describe(outcomes)}");
        Assert.True(outcomes.GetValueOrDefault("False") > 100, $"too few non-matches: {Describe(outcomes)}");
        Assert.True(outcomes.Where(o => o.Key.StartsWith("threw", StringComparison.Ordinal)).Sum(o => o.Value) > 50, $"too few thrown outcomes: {Describe(outcomes)}");
    }

    // JsonLogic puts the operator in the node, so an operand is always a literal - these are the cases the
    // string DSL cannot express at all, and where the two frontends are meant to differ.
    [Theory]
    [InlineData("!AUS", "AUS", false)]
    [InlineData("!AUS", "!AUS", true)]
    [InlineData("Racing-*", "Racing-Thoroughbred", false)]
    [InlineData("Racing-*", "racing-*", true)]
    [InlineData(">1.5", "2", false)]
    [InlineData(">1.5", ">1.5", true)]
    public void jsonlogic_equality_treats_operator_characters_as_literal_text(string condition, string eventValue, bool expected)
    {
        var logic = And(Node("==ci", Var("country"), condition));
        var compiled = CompiledV2Rule.CompileConditions(JsonSerializer.SerializeToElement(logic));
        var eventData = JsonSerializer.SerializeToElement(new JsonObject { ["country"] = eventValue });

        Assert.Equal(expected, CompiledV2Rule.Matches(compiled, new EventPropertyIndex(eventData)));
    }

    [Theory]
    [InlineData("""{"and": [{"nope": [{"var": "a"}, "b"]}]}""")]
    [InlineData("""{"and": [{"or": [{"var": "a"}, "b"]}]}""")]
    [InlineData("""{"and": [{"==ci": [{"var": "a"}]}]}""")]
    [InlineData("""{"and": [{"==ci": [{"lit": "a"}, "b"]}]}""")]
    [InlineData("""{"and": [{"==ci": [{"var": ""}, "b"]}]}""")]
    [InlineData("""{"and": [{"!": [{"startsWith": [{"var": "a"}, "b"]}]}]}""")]
    [InlineData("""{"and": [{">": [{"var": "a"}, "b"]}]}""")]
    [InlineData("""{"and": [{">": [{"var": "a"}, {"ago": [1]}]}]}""")]
    [InlineData("""{"and": [{"some": [{"var": "a"}, {"in": [{"var": "b"}, ["x"]]}]}]}""")]
    [InlineData("""{"and": [{"some": [{"var": "a"}, {"in": [{"var": ""}, "x"]}]}]}""")]
    [InlineData("""{"and": {"==ci": [{"var": "a"}, "b"]}}""")]
    [InlineData("""[]""")]
    public void malformed_jsonlogic_is_rejected_at_compile_time(string json)
    {
        var rule = JsonDocument.Parse(json).RootElement;
        Assert.Throws<JsonLogicCompileException>(() => JsonLogicConditionCompiler.Compile(rule));
    }

    [Fact]
    public void an_empty_rule_compiles_to_no_conditions()
    {
        Assert.Empty(JsonLogicConditionCompiler.Compile(JsonDocument.Parse("{}").RootElement));
        Assert.Empty(JsonLogicConditionCompiler.Compile(JsonDocument.Parse("""{"and": []}""").RootElement));
    }

    private static string Describe(Dictionary<string, int> outcomes) =>
        string.Join(", ", outcomes.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => $"{o.Key}={o.Value}"));

    private static string AssertAgree(int seed, int iteration, JsonElement eventData, Clause[] clauses)
    {
        var dataToMatch = JsonSerializer.SerializeToElement(DataToMatch(clauses));
        var logic = JsonSerializer.SerializeToElement(JsonLogic(clauses));

        var dslCompiled = Run(() => CompiledV2Rule.Matches(CompiledV2Rule.CompileConditions(dataToMatch), new EventPropertyIndex(eventData)));
        var jsonLogicCompiled = Run(() => CompiledV2Rule.Matches(CompiledV2Rule.CompileConditions(logic), new EventPropertyIndex(eventData)));

        if (dslCompiled == jsonLogicCompiled)
            return dslCompiled;

        Assert.Fail($"""
            frontends disagreed (seed {seed}, iteration {iteration})
              dataToMatch      : {dataToMatch.GetRawText()}
              jsonLogic        : {logic.GetRawText()}
              event            : {eventData.GetRawText()}
              dsl compiled     : {dslCompiled}
              jsonlogic compiled: {jsonLogicCompiled}
            """);
        return dslCompiled;
    }

    private static string Run(Func<bool> match)
    {
        try
        {
            return match().ToString();
        }
        catch (Exception ex)
        {
            return "threw " + ex.GetType().Name;
        }
    }

    // One generated condition, kept in its structured form so the string-DSL text and the JsonLogic node
    // are two renderings of the same thing rather than one being parsed back out of the other.
    private sealed record Clause(string Field, Kind Kind, string Text = "", int Number = 0, string Unit = "", bool Flag = false, string[]? Values = null, int[]? NumericValues = null);

    private static JsonObject DataToMatch(Clause[] clauses)
    {
        var json = new JsonObject();
        foreach (var clause in clauses)
            json[clause.Field] = Dsl(clause);
        return json;
    }

    private static JsonNode? Dsl(Clause clause) => clause.Kind switch
    {
        Kind.Equality => JsonValue.Create(clause.Text),
        Kind.Negation => JsonValue.Create("!" + clause.Text),
        Kind.EndsWith => JsonValue.Create("*" + clause.Text),
        Kind.StartsWith => JsonValue.Create(clause.Text + "*"),
        Kind.Contains => JsonValue.Create("*" + clause.Text + "*"),
        Kind.BareStar => JsonValue.Create("*"),
        Kind.GreaterThan => JsonValue.Create(">" + clause.Number.ToString(CultureInfo.InvariantCulture)),
        Kind.LessThan => JsonValue.Create("<" + clause.Number.ToString(CultureInfo.InvariantCulture)),
        Kind.NumericEqual => JsonValue.Create("==" + clause.Number.ToString(CultureInfo.InvariantCulture)),
        Kind.AgoGreaterThan => JsonValue.Create(">{ago(" + clause.Number + clause.Unit + ")}"),
        Kind.AgoLessThan or Kind.MalformedAgo => JsonValue.Create("<{ago(" + clause.Number + clause.Unit + ")}"),
        Kind.ArrayIntersection => ConditionArray(clause),
        Kind.Boolean => JsonValue.Create(clause.Flag),
        Kind.Null => null,
        _ => JsonValue.Create(clause.Number),
    };

    private static JsonObject JsonLogic(Clause[] clauses)
    {
        var and = new JsonArray();
        foreach (var clause in clauses)
            and.Add(Logic(clause));
        return new JsonObject { ["and"] = and };
    }

    private static JsonNode Logic(Clause clause) => clause.Kind switch
    {
        Kind.Equality => Node("==ci", Var(clause.Field), JsonValue.Create(clause.Text)),
        Kind.Negation => new JsonObject { ["!"] = new JsonArray(Node("==ci", Var(clause.Field), JsonValue.Create(clause.Text))) },
        Kind.EndsWith => Node("endsWith", Var(clause.Field), JsonValue.Create(clause.Text)),
        Kind.StartsWith => Node("startsWith", Var(clause.Field), JsonValue.Create(clause.Text)),
        Kind.Contains => Node("contains", Var(clause.Field), JsonValue.Create(clause.Text)),
        Kind.BareStar => Node("contains", Var(clause.Field), JsonValue.Create("")),
        Kind.GreaterThan => Node(">", Var(clause.Field), JsonValue.Create(clause.Number)),
        Kind.LessThan => Node("<", Var(clause.Field), JsonValue.Create(clause.Number)),
        Kind.NumericEqual => Node("==", Var(clause.Field), JsonValue.Create(clause.Number)),
        Kind.AgoGreaterThan => Node(">", Var(clause.Field), Ago(clause)),
        Kind.AgoLessThan or Kind.MalformedAgo => Node("<", Var(clause.Field), Ago(clause)),
        Kind.ArrayIntersection => Node("some", Var(clause.Field), new JsonObject { ["in"] = new JsonArray(Var(""), ConditionArray(clause)) }),
        Kind.Boolean => Node("==ci", Var(clause.Field), JsonValue.Create(clause.Flag)),
        Kind.Null => Node("==ci", Var(clause.Field), null),
        _ => Node("==ci", Var(clause.Field), JsonValue.Create(clause.Number)),
    };

    private static JsonArray ConditionArray(Clause clause)
    {
        var array = new JsonArray();
        foreach (var value in clause.Values!)
            array.Add(JsonValue.Create(value));
        foreach (var value in clause.NumericValues!)
            array.Add(JsonValue.Create(value));
        return array;
    }

    private static JsonObject Ago(Clause clause) =>
        new() { ["ago"] = new JsonArray(JsonValue.Create(clause.Number), JsonValue.Create(clause.Unit)) };

    private static JsonObject Var(string field) => new() { ["var"] = field };

    private static JsonObject Node(string op, JsonNode field, JsonNode? operand) =>
        new() { [op] = new JsonArray(field, operand) };

    private static JsonObject And(JsonNode clause) => new() { ["and"] = new JsonArray(clause) };

    private static JsonElement RandomEvent(Random random)
    {
        var json = new JsonObject();
        var count = random.Next(1, PropertyNames.Length);
        foreach (var name in Shuffled(random, PropertyNames).Take(count))
        {
            json[name] = RandomEventValue(random);

            // Rare: a second property differing only in case, which makes all three engines throw.
            if (random.Next(40) == 0)
                json[name.ToUpperInvariant()] = RandomEventValue(random);
        }
        return JsonSerializer.SerializeToElement(json);
    }

    private static JsonNode? RandomEventValue(Random random) => random.Next(10) switch
    {
        0 => JsonValue.Create(RandomTimestamp(random)),
        1 => JsonValue.Create(random.Next(-100, 100) + (decimal)random.NextDouble()),
        2 => JsonValue.Create(random.Next(2) == 0),
        3 or 4 => RandomArray(random),
        5 when random.Next(4) == 0 => null,
        6 when random.Next(6) == 0 => new JsonObject { ["nested"] = "x" },
        7 when random.Next(8) == 0 => new JsonArray(JsonValue.Create(1), JsonValue.Create(2)), // non-string array: throws in all three
        _ => JsonValue.Create(Pick(random, StringValues)),
    };

    private static JsonArray RandomArray(Random random)
    {
        var array = new JsonArray();
        var length = random.Next(0, 4);
        for (var i = 0; i < length; i++)
            array.Add(JsonValue.Create(Pick(random, StringValues)));
        return array;
    }

    private static string RandomTimestamp(Random random) =>
        DateTimeOffset.UtcNow.AddSeconds(Pick(random, EventTimeOffsets)).ToString("O", CultureInfo.InvariantCulture);

    // Field names are drawn without replacement: a duplicate key collapses in the dataToMatch object but
    // not in the JsonLogic "and" array, which would make the two renderings different rules.
    private static Clause[] RandomClauses(Random random, JsonElement eventData)
    {
        var present = eventData.EnumerateObject().Select(p => p.Name).ToArray();
        var count = random.Next(0, 4);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clauses = new List<Clause>();
        for (var i = 0; i < count; i++)
        {
            // Conditions mostly name a property the event carries - one on an absent property is skipped
            // by all three engines and tests nothing about the operators.
            var field = present.Length > 0 && random.Next(4) != 0 ? Pick(random, present) : Pick(random, PropertyNames);
            if (!used.Add(field))
                continue;
            clauses.Add(RandomClause(random, field));
        }
        return [.. clauses];
    }

    private static Clause RandomClause(Random random, string field)
    {
        var value = Pick(random, StringValues);
        // Wildcards use a random fragment of a value, not the whole value: "*AUS" against "AUS" matches
        // whichever end the needle is anchored to, and would hide a prefix/suffix mix-up.
        var fragment = RandomFragment(random, value);
        var (agoCount, agoUnit) = Pick(random, AgoOffsets);

        return random.Next(16) switch
        {
            0 => new Clause(field, Kind.Equality, Text: value),
            1 => new Clause(field, Kind.Negation, Text: value),
            2 => new Clause(field, Kind.EndsWith, Text: fragment),
            3 => new Clause(field, Kind.StartsWith, Text: fragment),
            4 => new Clause(field, Kind.Contains, Text: fragment),
            5 => new Clause(field, Kind.BareStar),
            6 => new Clause(field, Kind.GreaterThan, Number: random.Next(-10, 10)),
            7 => new Clause(field, Kind.LessThan, Number: random.Next(-10, 10)),
            8 => new Clause(field, Kind.NumericEqual, Number: random.Next(-10, 10)),
            9 => new Clause(field, Kind.AgoGreaterThan, Number: agoCount, Unit: agoUnit),
            10 => new Clause(field, Kind.AgoLessThan, Number: agoCount, Unit: agoUnit),
            11 => RandomArrayClause(random, field),
            12 => new Clause(field, Kind.Boolean, Flag: random.Next(2) == 0),
            13 => new Clause(field, Kind.Null),
            14 => new Clause(field, Kind.UnsupportedNumber, Number: random.Next(10)),   // throws at evaluation
            _ => new Clause(field, Kind.MalformedAgo, Number: 4, Unit: "x"),            // throws at evaluation
        };
    }

    // A rare non-string element makes both frontends defer the same error to evaluation, in the same order.
    private static Clause RandomArrayClause(Random random, string field)
    {
        var strings = new List<string>();
        var numbers = new List<int>();
        var length = random.Next(0, 4);
        for (var i = 0; i < length; i++)
        {
            if (random.Next(20) == 0)
                numbers.Add(random.Next(10));
            else
                strings.Add(Pick(random, StringValues));
        }
        return new Clause(field, Kind.ArrayIntersection, Values: [.. strings], NumericValues: [.. numbers]);
    }

    // Never empty: an empty needle is the bare-star case, generated separately as Kind.BareStar.
    private static string RandomFragment(Random random, string value)
    {
        if (value.Length == 0)
            return "A";
        if (value.Length < 2)
            return value;
        var start = random.Next(value.Length - 1);
        var length = random.Next(1, value.Length - start + 1);
        return value.Substring(start, length);
    }

    private static T Pick<T>(Random random, T[] values) => values[random.Next(values.Length)];

    private static IEnumerable<string> Shuffled(Random random, string[] values)
    {
        var copy = (string[])values.Clone();
        for (var i = copy.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }
}
