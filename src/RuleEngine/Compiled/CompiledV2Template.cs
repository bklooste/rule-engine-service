using System.Text.Json;

using Templates.Data;

namespace Templates.Compiled;

// P-14: the v2 compile path. It was built as a separate type tree from v1's CompiledRule/CompiledTemplate
// (deleted in P-20) rather than a flag on them, because v2 changes what a compiled rule *means*:
//   - ago(Nh) is N hours in the PAST (D-6), the opposite sign to v1
//   - dataToMatch may be written in JsonLogic (§1a/D-17) as well as the flat string DSL
//   - a condition may be marked required (D-11 revised), which v1 has no equivalent of
// D-11 was reversed by the business owner: absent-field conditions are SKIPPED by default, exactly as v1
// does, because many real events legitimately do not carry fields like locationClass/venueName. Strictness
// is now opt-in per condition via `required`.
// P-20 deleted v1, so this is now the only compile path; CompiledStringMatcher's agoIsPast parameter and
// JsonLogicConditionCompiler's still default to the old sign for callers that do not opt in.
internal sealed class V2Condition(string expression, CompiledCondition condition, bool required)
{
    public string Field => Condition.PropertyName;

    // The condition's original JSON text, kept for explain (A12) only - never read while matching.
    public string Expression { get; } = expression;

    public CompiledCondition Condition { get; } = condition;

    // D-11 (revised): a required condition fails the rule when the event does not carry its field, instead
    // of being skipped. Absent from a condition means the default, which is v1's skip-if-absent behaviour.
    public bool Required { get; } = required;
}

// One rule name plus why it could not be compiled (A7). PUT reports these instead of storing a template
// that throws on every later evaluate.
internal sealed record V2ValidationError(string Rule, string Reason);

internal sealed class CompiledV2Rule
{
    private readonly V2Condition[] conditions;

    public string Name { get; }

    public DynamicTemplate Source { get; }

    public CompiledFragment Fragment { get; }

    public IReadOnlyList<V2Condition> Conditions => conditions;

    public DateTime? ExpiresAt => Source.ExpiresAt;

    private CompiledV2Rule(DynamicTemplate source, V2Condition[] conditions, CompiledFragment fragment)
    {
        Source = source;
        Name = source.Name;
        this.conditions = conditions;
        Fragment = fragment;
    }

    public static CompiledV2Rule Compile(DynamicTemplate rule) =>
        new(rule, CompileConditions(rule.DataToMatch), CompiledFragment.Compile(rule.MatchFragmentAsNodeLazy));

    // D-11 (revised): loose by default, exactly as v1 was - a condition whose property the event does
    // not carry is skipped, not failed, unless marked required.
    // Every evaluated (present) condition must pass, and at least one condition must actually have been
    // evaluated for the rule to match (same "no evidence, no match" rule v1 applies). An empty dataToMatch
    // still matches everything.
    public bool Matches(EventPropertyIndex index) => Matches(conditions, index);

    public static bool Matches(V2Condition[] conditions, EventPropertyIndex index)
    {
        if (conditions.Length == 0)
            return true;

        var haveMatch = false;
        foreach (var condition in conditions)
        {
            if (!index.TryGetProperty(condition.Condition.PropertyName, out var value))
            {
                if (condition.Required)
                    return false;
                continue;
            }

            if (!condition.Condition.Evaluate(value))
                return false;
            haveMatch = true;
        }
        return haveMatch;
    }

    public static V2Condition[] CompileConditions(JsonElement dataToMatch)
    {
        if (dataToMatch.ValueKind != JsonValueKind.Object)
            throw new JsonLogicCompileException("dataToMatch must be an object");

        if (JsonLogicConditionCompiler.LooksLikeJsonLogic(dataToMatch))
            return CompileJsonLogic(dataToMatch);

        var conditions = new List<V2Condition>();
        foreach (var property in dataToMatch.EnumerateObject())
            conditions.Add(CompileFlat(property));
        return [.. conditions];
    }

    // §1a/D-17: dataToMatch written as JsonLogic. Detected structurally (a single key that is an operator)
    // so a v1-shaped document still compiles through the flat path below - P-22 migrates stored documents.
    private static V2Condition[] CompileJsonLogic(JsonElement dataToMatch)
    {
        if (dataToMatch.TryGetProperty("and", out var clauses))
        {
            if (clauses.ValueKind != JsonValueKind.Array)
                throw new JsonLogicCompileException("'and' takes an array of clauses");

            var conditions = new V2Condition[clauses.GetArrayLength()];
            var index = 0;
            foreach (var clause in clauses.EnumerateArray())
                conditions[index++] = CompileJsonLogicClause(clause);
            return conditions;
        }

        return [CompileJsonLogicClause(dataToMatch)];
    }

    // {"required": [clause]} marks one clause fail-if-absent, the opt-in for D-11's revised default. It
    // wraps rather than extends the grammar so JsonLogicConditionCompiler stays the pure operator frontend
    // P-21 built.
    private static V2Condition CompileJsonLogicClause(JsonElement clause)
    {
        var required = false;
        var target = clause;
        if (clause.ValueKind == JsonValueKind.Object && SingleKey(clause) == "required")
        {
            required = true;
            var inner = clause.GetProperty("required");
            target = inner.ValueKind == JsonValueKind.Array && inner.GetArrayLength() == 1 ? inner[0] : inner;
        }

        return new V2Condition(target.GetRawText(), JsonLogicConditionCompiler.CompileOne(target, agoIsPast: true), required);
    }

    // The flat {field: condition} form v1 stores, with two v2 additions: the value may be the object form
    // {"value": <condition>, "required": true} (D-11), and ago() compiles with the flipped sign (D-6).
    private static V2Condition CompileFlat(JsonProperty property)
    {
        var value = property.Value;
        var required = false;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var inner))
        {
            required = value.TryGetProperty("required", out var flag) && flag.ValueKind == JsonValueKind.True;
            value = inner;
        }

        CompiledCondition condition = value.ValueKind switch
        {
            JsonValueKind.Null => new AlwaysMatchCondition(property.Name),
            JsonValueKind.Array => new ArrayCondition(property.Name, value),
            JsonValueKind.String => new StringCondition(property.Name, new CompiledStringMatcher(value.GetString(), agoIsPast: true)),
            JsonValueKind.True => new BooleanCondition(property.Name, true),
            JsonValueKind.False => new BooleanCondition(property.Name, false),
            _ => new UnsupportedCondition(property.Name, value.ValueKind),
        };
        return new V2Condition(value.GetRawText(), condition, required);
    }

    private static string? SingleKey(JsonElement node)
    {
        string? key = null;
        var count = 0;
        foreach (var property in node.EnumerateObject())
        {
            key = property.Name;
            count++;
        }
        return count == 1 ? key : null;
    }
}

// A whole document compiled for serving, cached per (id, version) in DynamicRulesEngine.
internal sealed class CompiledV2Template
{
    private readonly CompiledV2Rule[] rules;

    public Template Source { get; }

    public string AggregationField { get; }

    public IReadOnlyList<CompiledV2Rule> Rules => rules;

    private CompiledV2Template(Template source, CompiledV2Rule[] rules)
    {
        Source = source;
        AggregationField = source.AggregationField;
        this.rules = rules;
    }

    public static CompiledV2Template Compile(Template template) =>
        new(template, [.. template.Templates.Select(CompiledV2Rule.Compile)]);

    // A7: compile every rule and report the ones that cannot be served, by name and reason. Operand
    // problems the matcher defers to evaluation time (a malformed ago() spec, a dataToMatch value kind
    // with no operator) are surfaced here too - storing them is what makes them a per-request failure.
    public static List<V2ValidationError> Validate(Template template)
    {
        var errors = new List<V2ValidationError>();
        if (template.Templates == null || template.Templates.Length == 0)
        {
            errors.Add(new V2ValidationError("(document)", "no templates found - ensure JSON has a 'templates' array"));
            return errors;
        }

        foreach (var rule in template.Templates)
        {
            var name = string.IsNullOrEmpty(rule.Name) ? "(unnamed)" : rule.Name;
            if (rule.DataToMatch.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                errors.Add(new V2ValidationError(name, "no dataToMatch"));
                continue;
            }
            if (rule.MatchFragment.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                errors.Add(new V2ValidationError(name, "no matchFragment"));
                continue;
            }
            if (rule.MatchFragment.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new V2ValidationError(name, "matchFragment must be an object"));
                continue;
            }

            V2Condition[] conditions;
            try
            {
                conditions = CompiledV2Rule.CompileConditions(rule.DataToMatch);
                CompiledFragment.Compile(rule.MatchFragmentAsNodeLazy);
            }
            catch (Exception ex)
            {
                errors.Add(new V2ValidationError(name, ex.Message));
                continue;
            }

            foreach (var condition in conditions)
            {
                switch (condition.Condition)
                {
                    case UnsupportedCondition unsupported:
                        errors.Add(new V2ValidationError(name, $"condition on '{unsupported.PropertyName}' has an unsupported value kind {unsupported.TemplateKind}"));
                        break;
                    case StringCondition { Matcher.OperandError: { } error } stringCondition:
                        errors.Add(new V2ValidationError(name, $"condition on '{stringCondition.PropertyName}' has a bad operand: {error.Message}"));
                        break;
                }
            }
        }
        return errors;
    }
}
