using System.Text.Json;

namespace Templates.Compiled;

// Thrown while compiling a JsonLogic rule, i.e. for syntax the grammar does not accept. Operand
// problems that today only surface per request (a malformed ago() spec, a non-string array element,
// a number where a string was expected) are deliberately NOT raised here - they stay deferred to
// Evaluate exactly as the old string-DSL frontend leaves them.
internal sealed class JsonLogicCompileException(string message) : Exception(message);

// §1a / D-17: the second frontend. It reads a stored condition written in JsonLogic and compiles it into
// the same CompiledCondition[] the old string-DSL frontend (CompiledRule.CompileConditions) produces -
// no interpreter, no Expression.Compile, the operator is resolved once per template version and what
// runs per request is the identical closure/virtual call.
//
// Grammar (D-17): native var/and/==/!/some/in plus the custom ==ci/startsWith/endsWith/contains/ago.
//
//   { "and": [ clause, ... ] }                                  the implicit AND across dataToMatch keys
//   { "==ci":       [ {"var":"f"}, "value" ] }                   equality (case-insensitive: today's default)
//   { "!":          [ {"==ci": [ {"var":"f"}, "value" ]} ] }     negation
//   { "startsWith": [ {"var":"f"}, "needle" ] }                  and endsWith / contains
//   { ">" | "<" | "==": [ {"var":"f"}, 1.5 ] }                   numeric compare
//   { ">": [ {"var":"f"}, {"ago":[12,"h"]} ] }                   time offset, today's sign (D-6 is P-14)
//   { "some": [ {"var":"f"}, {"in": [ {"var":""}, ["a","b"] ]} ] } array intersection
//
// A ==ci operand that is a bool compiles to a BooleanCondition, null to an always-match, anything else
// to the same UnsupportedCondition the old frontend built for that value kind.
//
// Not wired into serving: migration of stored templates is P-22 and the v2 API is P-14.
internal static class JsonLogicConditionCompiler
{
    // agoIsPast is v2's D-6 sign flip, threaded straight through to the numeric matcher. It defaults to
    // false so P-21's oracle tests and any v1 use compile exactly as before.
    public static CompiledCondition[] Compile(JsonElement rule, bool agoIsPast = false)
    {
        if (rule.ValueKind != JsonValueKind.Object)
            throw new JsonLogicCompileException("a JsonLogic rule must be an object");

        if (rule.TryGetProperty("and", out var clauses))
        {
            if (clauses.ValueKind != JsonValueKind.Array)
                throw new JsonLogicCompileException("'and' takes an array of clauses");

            var conditions = new CompiledCondition[clauses.GetArrayLength()];
            var index = 0;
            foreach (var clause in clauses.EnumerateArray())
                conditions[index++] = CompileClause(clause, agoIsPast);
            return conditions;
        }

        // A rule with no conditions - the JsonLogic spelling of an empty dataToMatch.
        return rule.EnumerateObject().Any() ? [CompileClause(rule, agoIsPast)] : [];
    }

    // One clause on its own, so v2 can unwrap its {"required": [clause]} marker before compiling
    // (the required flag lives on v2's condition wrapper, not on CompiledCondition).
    public static CompiledCondition CompileOne(JsonElement clause, bool agoIsPast = false) =>
        CompileClause(clause, agoIsPast);

    // True when this object is a JsonLogic node rather than a flat {field: condition} dataToMatch.
    public static bool LooksLikeJsonLogic(JsonElement rule)
    {
        if (rule.ValueKind != JsonValueKind.Object)
            return false;

        var count = 0;
        var isOperator = false;
        foreach (var property in rule.EnumerateObject())
        {
            count++;
            isOperator = Operators.Contains(property.Name);
        }
        return count == 1 && isOperator;
    }

    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "and", "or", "!", "==ci", "==", "<", ">", "startsWith", "endsWith", "contains", "some", "required",
    };

    private static CompiledCondition CompileClause(JsonElement clause, bool agoIsPast)
    {
        var (op, args) = SingleOperator(clause);
        return op switch
        {
            "==ci" => CompileEquality(args),
            "!" => CompileNegation(args),
            "startsWith" => CompileWildcard(args, FallbackMatch.StartsWith),
            "endsWith" => CompileWildcard(args, FallbackMatch.EndsWith),
            "contains" => CompileWildcard(args, FallbackMatch.Contains),
            "<" => CompileNumeric(args, NumericOperator.LessThan, agoIsPast),
            ">" => CompileNumeric(args, NumericOperator.GreaterThan, agoIsPast),
            "==" => CompileNumeric(args, NumericOperator.Equal, agoIsPast),
            "some" => CompileSome(args),
            // The compiled backend has no disjunction node and the old syntax had no way to write one,
            // so there is nothing to compile 'or' into and nothing to check it against. P-22 decides
            // whether to widen the backend or keep rules conjunctive.
            "or" => throw new JsonLogicCompileException("'or' is not supported yet - conditions are conjunctive"),
            _ => throw new JsonLogicCompileException($"unknown JsonLogic operator '{op}'"),
        };
    }

    private static CompiledCondition CompileEquality(JsonElement args)
    {
        var (field, operand) = FieldAndOperand(args, "==ci");
        return operand.ValueKind switch
        {
            JsonValueKind.String => new StringCondition(field, CompiledStringMatcher.Equality(operand.GetString()!)),
            JsonValueKind.True => new BooleanCondition(field, true),
            JsonValueKind.False => new BooleanCondition(field, false),
            JsonValueKind.Null => new AlwaysMatchCondition(field),
            _ => new UnsupportedCondition(field, operand.ValueKind),
        };
    }

    private static CompiledCondition CompileNegation(JsonElement args)
    {
        var inner = args.ValueKind == JsonValueKind.Array
            ? (args.GetArrayLength() == 1 ? args[0] : throw new JsonLogicCompileException("'!' takes exactly one clause"))
            : args;

        var (op, innerArgs) = SingleOperator(inner);
        if (op != "==ci")
            throw new JsonLogicCompileException($"'!' only negates '==ci', not '{op}'");

        var (field, operand) = FieldAndOperand(innerArgs, "==ci");
        if (operand.ValueKind != JsonValueKind.String)
            throw new JsonLogicCompileException("'!' only negates a string operand");

        return new StringCondition(field, CompiledStringMatcher.Negation(operand.GetString()!));
    }

    private static CompiledCondition CompileWildcard(JsonElement args, FallbackMatch kind)
    {
        var (field, operand) = FieldAndOperand(args, kind.ToString());
        if (operand.ValueKind != JsonValueKind.String)
            throw new JsonLogicCompileException($"'{kind}' takes a string needle");

        var needle = operand.GetString()!;
        var matcher = kind switch
        {
            FallbackMatch.StartsWith => CompiledStringMatcher.StartsWith(needle),
            FallbackMatch.EndsWith => CompiledStringMatcher.EndsWith(needle),
            _ => CompiledStringMatcher.Contains(needle),
        };
        return new StringCondition(field, matcher);
    }

    private static CompiledCondition CompileNumeric(JsonElement args, NumericOperator op, bool agoIsPast)
    {
        var (field, operand) = FieldAndOperand(args, op.ToString());
        var operandText = operand.ValueKind switch
        {
            JsonValueKind.Number => operand.GetRawText(),
            // The ago() text is rebuilt rather than parsed here so a bad unit or count throws from
            // Evaluate, with the same exception, at the same moment as it does today.
            JsonValueKind.Object => AgoText(operand),
            _ => throw new JsonLogicCompileException($"'{op}' takes a number or an 'ago' operand"),
        };
        return new StringCondition(field, CompiledStringMatcher.Numeric(op, operandText, agoIsPast));
    }

    private static string AgoText(JsonElement operand)
    {
        var (op, args) = SingleOperator(operand);
        if (op != "ago")
            throw new JsonLogicCompileException($"expected an 'ago' operand, found '{op}'");
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() != 2)
            throw new JsonLogicCompileException("'ago' takes [count, unit]");

        var count = args[0];
        var unit = args[1];
        if (count.ValueKind != JsonValueKind.Number || unit.ValueKind != JsonValueKind.String)
            throw new JsonLogicCompileException("'ago' takes a numeric count and a string unit");

        return "{ago(" + count.GetRawText() + unit.GetString() + ")}";
    }

    // { "some": [ {"var":"field"}, {"in": [ {"var":""}, ["a","b"] ]} ] } - the array-intersection form.
    // The literal array is handed to ArrayCondition untouched so its element-level error handling (and
    // ordering) is the same one the old frontend gets.
    private static CompiledCondition CompileSome(JsonElement args)
    {
        var (field, test) = FieldAndOperand(args, "some");
        var (op, inArgs) = SingleOperator(test);
        if (op != "in")
            throw new JsonLogicCompileException($"'some' takes an 'in' test, not '{op}'");
        if (inArgs.ValueKind != JsonValueKind.Array || inArgs.GetArrayLength() != 2)
            throw new JsonLogicCompileException("'in' takes [value, array]");

        var (scopeOp, scopeArg) = SingleOperator(inArgs[0]);
        if (scopeOp != "var" || scopeArg.ValueKind != JsonValueKind.String || scopeArg.GetString()!.Length != 0)
            throw new JsonLogicCompileException("'in' inside 'some' must test the iteration variable {\"var\": \"\"}");

        var values = inArgs[1];
        if (values.ValueKind != JsonValueKind.Array)
            throw new JsonLogicCompileException("'in' takes an array of values");

        return new ArrayCondition(field, values);
    }

    // Every node is a one-key object: {"<operator>": <args>}.
    private static (string Operator, JsonElement Args) SingleOperator(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            throw new JsonLogicCompileException("expected a JsonLogic node object");

        (string, JsonElement)? found = null;
        foreach (var property in node.EnumerateObject())
        {
            if (found != null)
                throw new JsonLogicCompileException("a JsonLogic node must have exactly one operator");
            found = (property.Name, property.Value);
        }
        return found ?? throw new JsonLogicCompileException("a JsonLogic node must have exactly one operator");
    }

    // The [{"var":"field"}, operand] argument shape every binary operator here takes.
    private static (string Field, JsonElement Operand) FieldAndOperand(JsonElement args, string op)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() != 2)
            throw new JsonLogicCompileException($"'{op}' takes [{{\"var\": \"field\"}}, operand]");

        var (varOp, varArg) = SingleOperator(args[0]);
        if (varOp != "var")
            throw new JsonLogicCompileException($"'{op}' reads its field through {{\"var\": \"field\"}}");
        if (varArg.ValueKind != JsonValueKind.String || varArg.GetString()!.Length == 0)
            throw new JsonLogicCompileException("'var' takes a non-empty field name");

        return (varArg.GetString()!, args[1]);
    }
}
