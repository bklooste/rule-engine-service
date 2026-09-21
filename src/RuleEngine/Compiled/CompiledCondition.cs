using System.Text.Json;

namespace Templates.Compiled;

// §1: one dataToMatch entry with its operator parsed once, at compile time, instead of re-parsed from
// the string on every request. Predicates are plain closures/virtual calls - never Expression.Compile,
// which only runs interpreted under NativeAOT. Every subclass reproduces EngineMatching's interpreted
// matcher exactly, including its exception behaviour - a bad ago() spec or non-string template array
// element is captured at compile time and rethrown from Evaluate, so compiling never turns a per-request
// failure into a load failure. Fixing those (A7) is P-14, not this task.
internal abstract class CompiledCondition(string propertyName)
{
    // The event property this condition reads; looked up case-insensitively, as today.
    public string PropertyName { get; } = propertyName;

    // Evaluates against a present, non-null event property value.
    public abstract bool Evaluate(in JsonElement value);

    protected NotSupportedException Unsupported(JsonValueKind templateKind, JsonValueKind dataKind) =>
        new(templateKind.ToString() + "match" + dataKind.ToString());
}

// A null condition value: matched everything in the old switch, still does.
internal sealed class AlwaysMatchCondition(string propertyName) : CompiledCondition(propertyName)
{
    public override bool Evaluate(in JsonElement value) => true;
}

// A template value kind the old switch had no arm for (numbers, objects) - it threw NotSupportedException
// at match time and so does this.
internal sealed class UnsupportedCondition(string propertyName, JsonValueKind templateKind) : CompiledCondition(propertyName)
{
    public JsonValueKind TemplateKind { get; } = templateKind;

    public override bool Evaluate(in JsonElement value) => throw Unsupported(TemplateKind, value.ValueKind);
}

internal enum NumericOperator { None, LessThan, GreaterThan, Equal }

internal enum FallbackMatch { Equality, EndsWith, StartsWith, Contains }

// The pre-parsed form of a string condition ("AUS", "!AUS", "Racing-*", "<{ago(12h)}", ">1.5"). Immutable;
// safe to share across requests and threads. Only ago() reads the clock, and does so per evaluation - it
// is time-dependent by design.
internal sealed class CompiledStringMatcher
{
    private const StringComparison Comparison = StringComparison.InvariantCultureIgnoreCase;

    private readonly string raw;
    private readonly bool isEmpty;
    private readonly bool isNegation;
    private readonly string negationNeedle = string.Empty;

    private readonly bool hasNumericPrefix;
    private readonly bool rawContainsAgo;
    private readonly NumericOperator numericOperator;
    private readonly bool operandIsAgo;
    private readonly long agoOffsetSeconds;
    private readonly Exception? agoParseError;
    private readonly bool operandParsed;
    private readonly decimal operand;

    // D-6: +1 keeps v1's (wrongly signed) "ago(1h) is one hour from now"; the v2 compile path is the
    // only caller that passes agoIsPast, and gets -1 so ago(1h) is one hour in the past. v1 never sets
    // it, so the interpreted engine and v1's compiled serving path are bit-for-bit unchanged.
    private readonly int agoSign = 1;

    private readonly FallbackMatch fallback;
    private readonly string fallbackNeedle = string.Empty;

    public CompiledStringMatcher(string? templateValue, bool agoIsPast = false)
    {
        raw = templateValue ?? string.Empty;
        agoSign = agoIsPast ? -1 : 1;
        isEmpty = string.IsNullOrEmpty(raw);
        if (isEmpty)
            return;

        if (raw[0] == '!')
        {
            isNegation = true;
            negationNeedle = raw[1..];
            return;
        }

        rawContainsAgo = raw.Contains("ago");
        hasNumericPrefix = raw.StartsWith("==") || raw[0] == '<' || raw[0] == '>';
        if (hasNumericPrefix)
        {
            // Same precedence as NumericComparisons: '<', then '>', then "==".
            string operandText;
            if (raw[0] == '<')
                (numericOperator, operandText) = (NumericOperator.LessThan, raw[1..]);
            else if (raw[0] == '>')
                (numericOperator, operandText) = (NumericOperator.GreaterThan, raw[1..]);
            else
                (numericOperator, operandText) = (NumericOperator.Equal, raw[2..]);

            operandIsAgo = operandText.Contains("{ago(");
            if (operandIsAgo)
            {
                try
                {
                    agoOffsetSeconds = operandText.GetAgoOffsetSeconds();
                }
                catch (Exception ex)
                {
                    // Thrown per request today; deferred to evaluation so PUT/load behaviour is unchanged.
                    agoParseError = ex;
                }
            }
            else
            {
                operandParsed = decimal.TryParse(operandText, out operand);
            }
        }

        // The numeric branch above falls through to these when the event value is not numeric, so a
        // wildcard/equality form is always prepared too.
        if (!raw.Contains('*'))
        {
            fallback = FallbackMatch.Equality;
            return;
        }

        var needle = raw;
        var startWild = needle[0] == '*';
        var endWild = needle[^1] == '*';
        if (startWild)
            needle = needle[1..];
        if (endWild && needle.Length > 1)
            needle = needle[..^1];

        fallbackNeedle = needle;
        fallback = (startWild, endWild) switch
        {
            (true, false) => FallbackMatch.EndsWith,
            (false, true) => FallbackMatch.StartsWith,
            _ => FallbackMatch.Contains,
        };
    }

    // P-21: the structural constructors the JsonLogic frontend (JsonLogicConditionCompiler) compiles
    // into. JsonLogic carries the operator in the node, so the operand is always a literal - "!AUS" as a
    // ==ci value is the string "!AUS", not a negation. `raw` is still kept as the equivalent old-DSL text
    // because StringCondition's event-array arm does ordinal membership on it; that quirk is P-14's.
    private CompiledStringMatcher(string raw, bool negation, string negationNeedle, FallbackMatch fallback, string fallbackNeedle)
    {
        this.raw = raw;
        isEmpty = string.IsNullOrEmpty(raw);
        isNegation = negation;
        this.negationNeedle = negationNeedle;
        this.fallback = fallback;
        this.fallbackNeedle = fallbackNeedle;
    }

    private CompiledStringMatcher(string raw, NumericOperator op, string operandText, bool agoIsPast)
    {
        this.raw = raw;
        isEmpty = string.IsNullOrEmpty(raw);
        agoSign = agoIsPast ? -1 : 1;
        hasNumericPrefix = true;
        rawContainsAgo = raw.Contains("ago");
        numericOperator = op;
        operandIsAgo = operandText.Contains("{ago(");
        if (operandIsAgo)
        {
            try
            {
                agoOffsetSeconds = operandText.GetAgoOffsetSeconds();
            }
            catch (Exception ex)
            {
                agoParseError = ex;
            }
        }
        else
        {
            operandParsed = decimal.TryParse(operandText, out operand);
        }
        fallback = FallbackMatch.Equality;
    }

    public static CompiledStringMatcher Equality(string value) =>
        new(value, false, string.Empty, FallbackMatch.Equality, string.Empty);

    public static CompiledStringMatcher Negation(string value) =>
        new("!" + value, true, value, FallbackMatch.Equality, string.Empty);

    // An empty needle is the bare "*" of the old syntax, which it parsed as contains-nothing.
    public static CompiledStringMatcher StartsWith(string needle) =>
        new(needle.Length == 0 ? "*" : needle + "*", false, string.Empty, FallbackMatch.StartsWith, needle);

    public static CompiledStringMatcher EndsWith(string needle) =>
        new(needle.Length == 0 ? "*" : "*" + needle, false, string.Empty, FallbackMatch.EndsWith, needle);

    public static CompiledStringMatcher Contains(string needle) =>
        new(needle.Length == 0 ? "*" : "*" + needle + "*", false, string.Empty, FallbackMatch.Contains, needle);

    public static CompiledStringMatcher Numeric(NumericOperator op, string operandText, bool agoIsPast = false) =>
        new(OperatorText(op) + operandText, op, operandText, agoIsPast);

    // A malformed ago() spec that today only throws per request. v2's PUT validation (A7) reports it
    // instead of storing a template that fails every later evaluate; evaluation still defers to it.
    internal Exception? OperandError => agoParseError;

    private static string OperatorText(NumericOperator op) => op switch
    {
        NumericOperator.LessThan => "<",
        NumericOperator.GreaterThan => ">",
        NumericOperator.Equal => "==",
        _ => string.Empty,
    };

    // The unparsed condition text, used by the ordinal array-membership paths.
    public string Raw => raw;

    public bool Match(string? dataValue)
    {
        if (isEmpty || string.IsNullOrEmpty(dataValue))
            return false;

        if (isNegation)
            return !negationNeedle.Equals(dataValue, Comparison);

        if (hasNumericPrefix)
        {
            if (decimal.TryParse(dataValue, out var decimalValue))
                return MatchNumber(decimalValue);

            if (rawContainsAgo && DateTimeOffset.TryParse(dataValue, out var dateValue))
                return MatchNumber(dateValue.ToUnixTimeSeconds());
        }

        return fallback switch
        {
            FallbackMatch.EndsWith => dataValue.EndsWith(fallbackNeedle, Comparison),
            FallbackMatch.StartsWith => dataValue.StartsWith(fallbackNeedle, Comparison),
            FallbackMatch.Contains => dataValue.Contains(fallbackNeedle, Comparison),
            _ => raw.Equals(dataValue, Comparison),
        };
    }

    public bool MatchNumber(decimal dataValue)
    {
        if (!hasNumericPrefix)
            return false;

        decimal matchValue;
        if (operandIsAgo)
        {
            if (agoParseError != null)
                throw agoParseError;
            matchValue = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (agoSign * agoOffsetSeconds);
        }
        else
        {
            if (!operandParsed)
                return false;
            matchValue = operand;
        }

        return numericOperator switch
        {
            NumericOperator.LessThan => dataValue < matchValue,
            NumericOperator.GreaterThan => dataValue > matchValue,
            NumericOperator.Equal => dataValue == matchValue,
            _ => false,
        };
    }
}

// A string dataToMatch value, against a string, array or number event value.
internal sealed class StringCondition(string propertyName, CompiledStringMatcher matcher) : CompiledCondition(propertyName)
{
    public CompiledStringMatcher Matcher => matcher;

    public override bool Evaluate(in JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return matcher.Match(value.GetString());

            case JsonValueKind.Array:
                // MatchStringToDataArray: ordinal membership of the raw condition text, no operators.
                foreach (var element in value.EnumerateArray())
                {
                    var elementValue = element.GetString() ?? throw new Exception("");
                    if (string.Equals(elementValue, matcher.Raw, StringComparison.Ordinal))
                        return true;
                }
                return false;

            case JsonValueKind.Number:
                return matcher.MatchNumber(value.GetDecimal());

            default:
                throw Unsupported(JsonValueKind.String, value.ValueKind);
        }
    }
}

// An array dataToMatch value (country: ["AUS", "NZL"]). Against an event array it is a case-insensitive
// intersection, precomputed into a lowered HashSet; against an event string it is ordinal membership of
// the untouched values. Both sets are built once per version.
internal sealed class ArrayCondition : CompiledCondition
{
    private readonly int templateCount;
    private readonly HashSet<string> loweredValues = new(StringComparer.Ordinal);
    private readonly Exception? intersectionCompileError;

    // The membership path kept its order: the old code enumerated lazily through LINQ's Contains, so a
    // malformed element only threw if no earlier element matched first. Entries carry their own error.
    private readonly (string? Value, Exception? Error)[] membershipEntries;
    private readonly HashSet<string>? membershipSet;

    public ArrayCondition(string propertyName, JsonElement templateArray) : base(propertyName)
    {
        templateCount = templateArray.GetArrayLength();
        try
        {
            foreach (var element in templateArray.EnumerateArray())
            {
                var elementValue = element.GetString() ?? throw new InvalidDataException("Bad format or null in Array Exception");
                loweredValues.Add(elementValue.ToLower());
            }
        }
        catch (Exception ex)
        {
            // The old code materialised the whole template array here, so one bad element failed it all.
            intersectionCompileError = ex;
        }

        membershipEntries = new (string?, Exception?)[templateCount];
        var index = 0;
        foreach (var element in templateArray.EnumerateArray())
        {
            try
            {
                membershipEntries[index] = (element.GetString() ?? throw new Exception(""), null);
            }
            catch (Exception ex)
            {
                membershipEntries[index] = (null, ex);
            }
            index++;
        }

        if (Array.TrueForAll(membershipEntries, e => e.Error == null))
            membershipSet = new HashSet<string>(membershipEntries.Select(e => e.Value!), StringComparer.Ordinal);
    }

    public override bool Evaluate(in JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            // The old matcher materialised the event array before looking at the template, so a bad
            // event array throws even when the template array is empty. Order kept.
            var eventValues = new List<string>();
            foreach (var element in value.EnumerateArray())
                eventValues.Add(element.GetString() ?? throw new InvalidDataException("Bad format or null in Array Exception"));

            if (intersectionCompileError != null)
                throw intersectionCompileError;

            if (templateCount == 0)
                return true;

            if (eventValues.Count == 0)
                return false;

            foreach (var eventValue in eventValues)
                if (loweredValues.Contains(eventValue.ToLower()))
                    return true;

            return false;
        }

        var dataValue = value.GetString();
        if (dataValue == null)
            return false;

        if (membershipSet != null)
            return membershipSet.Contains(dataValue);

        foreach (var (templateValue, error) in membershipEntries)
        {
            if (error != null)
                throw error;
            if (string.Equals(templateValue, dataValue, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

// A boolean dataToMatch value (isSport: false).
internal sealed class BooleanCondition(string propertyName, bool templateValue) : CompiledCondition(propertyName)
{
    private readonly CompiledStringMatcher literal = new(templateValue ? "true" : "false");

    public override bool Evaluate(in JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? literal.Match(value.GetString())
            : value.GetBoolean() == templateValue;
}
