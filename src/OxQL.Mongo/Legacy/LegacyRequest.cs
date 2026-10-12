// LEGACY(compat.v1): contract 1 legacy support. Remove with contract 1 compatibility; see src/docs/LEGACY.md.
using System.Text.Json;

namespace OxQL.Mongo.Legacy;

/// <summary>A request as the 1.1 engine read it: nothing is checked against a model.</summary>
/// <param name="Faulty">
/// True for a body with a stage or a sort entry that is <c>null</c>: the answer is 500 once the
/// entity is known, as contract 1 callers got it.
/// </param>
internal sealed record LegacyRequest(string EntityType, LegacyVariables? Variables, IReadOnlyList<LegacyStage> Pipeline, bool Faulty = false);

/// <summary>The variables of a request, by name without regard to case.</summary>
internal sealed class LegacyVariables
{
    private readonly Dictionary<string, JsonElement> values = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string name, JsonElement value) => values[name] = value;

    /// <summary>The value of a variable, or null when the request names none of that name.</summary>
    public JsonElement? Get(string name) => values.TryGetValue(name, out var value) ? value : null;
}

/// <summary>One stage object. The 1.1 engine read every key it knew of one object and applied them in a fixed order.</summary>
internal sealed record LegacyStage
{
    public LegacyMatch? Match { get; init; }
    public LegacyLookup? Lookup { get; init; }
    public LegacyResolve? Resolve { get; init; }
    public LegacyUnwind? Unwind { get; init; }
    public LegacyGroup? Group { get; init; }
    public LegacyProject? Project { get; init; }
    public IReadOnlyList<LegacySortField>? Sort { get; init; }
    public LegacyPage? Page { get; init; }
}

/// <summary>A match: one logical group, one condition, or everything.</summary>
internal sealed record LegacyMatch
{
    public IReadOnlyList<LegacyCondition>? And { get; init; }
    public IReadOnlyList<LegacyCondition>? Or { get; init; }
    public LegacyCondition? Not { get; init; }
    public LegacyCondition? Condition { get; init; }
}

/// <summary>A condition: a logical group, or a path with one operator and its operand.</summary>
internal sealed record LegacyCondition
{
    public string? Path { get; init; }
    public string? Op { get; init; }
    public JsonElement? Value { get; init; }
    public bool IgnoreCase { get; init; }
    public IReadOnlyList<LegacyCondition>? And { get; init; }
    public IReadOnlyList<LegacyCondition>? Or { get; init; }
    public LegacyCondition? Not { get; init; }

    /// <summary>A condition the engine adds itself (the organisation), already in the database's form.</summary>
    public MongoDB.Bson.BsonDocument? Built { get; init; }

    public bool IsLogical => And is not null || Or is not null || Not is not null;
}

/// <summary>A v1 lookup: a collection by name, joined on one member of each side.</summary>
internal sealed record LegacyLookup(string? From, string? LocalPath, string? ForeignPath, string? As, string? Convert, LegacyMatch? Filter);

/// <summary>A v1 resolve: validated and then left out of the aggregate, as the 1.1 engine did.</summary>
internal sealed record LegacyResolve(string? Source, string? LocalPath, string? As);

internal sealed record LegacyUnwind(string? Path, string? As, bool PreserveNull, string? IncludeIndex);

internal sealed record LegacyGroup(IReadOnlyList<LegacyGroupBy> By, IReadOnlyList<KeyValuePair<string, LegacyAggregation>> Fields);

internal sealed record LegacyGroupBy(string? Path, string? DateTruncPath, string? DateTruncUnit, string? As);

internal sealed record LegacyAggregation(string? Function, LegacyExpression? Argument);

/// <summary>An aggregate's argument: a path, a variable, a literal, or an arithmetic operator over more of them.</summary>
internal sealed record LegacyExpression
{
    public string? Path { get; init; }
    public string? Var { get; init; }
    public object? Literal { get; init; }
    public string? Operator { get; init; }
    public IReadOnlyList<LegacyExpression>? Operands { get; init; }

    public bool IsPath => Path is not null;
    public bool IsVar => Var is not null;
    public bool IsArithmetic => Operator is not null;
    public bool IsLiteral => Literal is not null && !IsPath && !IsVar && Operator is null;
}

/// <summary>A projection, flattened to dotted paths in the order written; only the paths written with 1 are kept.</summary>
internal sealed record LegacyProject(IReadOnlyList<string> Include);

internal sealed record LegacySortField(string Path, string Direction)
{
    public bool IsAscending => string.Equals(Direction, "asc", StringComparison.OrdinalIgnoreCase);
}

internal sealed record LegacyPage(int Limit, string? Cursor, bool IncludeTotalCount);

/// <summary>What the body is not: the 1.1 host answered such a body 400 before the engine saw it.</summary>
internal sealed class LegacyBodyException(string message) : Exception(message);

/// <summary>
/// Reads a request body the way the 1.1 models and their converters did: stage keys by their exact
/// name, the members of a stage without regard to case, a nested projection flattened, the first
/// operator of a condition taken and the rest ignored, a member nobody knows ignored.
/// </summary>
internal static class LegacyRequestReader
{
    private static readonly HashSet<string> AggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sum", "avg", "min", "max", "first", "last", "push", "count", "countDistinct",
    };

    private static readonly HashSet<string> ArithmeticOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "subtract", "multiply", "divide", "coalesce",
    };

    public static LegacyRequest Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new LegacyBodyException("The request is a JSON object with an entityType and a pipeline.");

        var entityType = Member(root, "entityType") is { ValueKind: JsonValueKind.String } named ? named.GetString() : null;

        if (Member(root, "pipeline") is not { ValueKind: JsonValueKind.Array } stages)
            throw new LegacyBodyException("The request carries no pipeline.");

        LegacyVariables? variables = null;

        if (Member(root, "variables") is { ValueKind: JsonValueKind.Object } declared)
        {
            variables = new LegacyVariables();

            foreach (var variable in declared.EnumerateObject())
                variables.Set(variable.Name, variable.Value.Clone());
        }

        // A null where a stage or a sort entry stands is answered 500 (see LegacyRequest.Faulty).
        var faulty = stages.EnumerateArray().Any(stage => stage.ValueKind == JsonValueKind.Null
            || (stage.ValueKind == JsonValueKind.Object && stage.TryGetProperty("sort", out var sort) && sort.ValueKind == JsonValueKind.Array && sort.EnumerateArray().Any(entry => entry.ValueKind == JsonValueKind.Null)));

        return new LegacyRequest(entityType ?? "", variables, stages.EnumerateArray().Select(Stage).ToList(), faulty);
    }

    private static LegacyStage Stage(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Null)
            return new LegacyStage();

        if (root.ValueKind != JsonValueKind.Object)
            throw new LegacyBodyException("A stage is a JSON object.");

        var stage = new LegacyStage();

        if (Key(root, "match") is { } match)
            stage = stage with { Match = Match(match) };

        if (Key(root, "lookup") is { } lookup)
            stage = stage with
            {
                Lookup = new LegacyLookup(Text(lookup, "from"), Text(lookup, "localPath"), Text(lookup, "foreignPath"), Text(lookup, "as"), Text(lookup, "convert"),
                    Member(lookup, "filter") is { ValueKind: not JsonValueKind.Null } filter ? Match(filter) : null),
            };

        if (Key(root, "resolve") is { } resolve)
            stage = stage with { Resolve = new LegacyResolve(Text(resolve, "source"), Text(resolve, "localPath"), Text(resolve, "as")) };

        if (Key(root, "unwind") is { } unwind)
            stage = stage with
            {
                Unwind = new LegacyUnwind(Text(unwind, "path"), Text(unwind, "as"), Flag(unwind, "preserveNull", "An unwind's 'preserveNull' is true or false."), Text(unwind, "includeIndex")),
            };

        if (Key(root, "group") is { } group)
            stage = stage with { Group = Group(group) };

        if (Key(root, "project") is { } project)
        {
            var fields = new List<KeyValuePair<string, int>>();

            Flatten(project, null, fields);

            stage = stage with { Project = new LegacyProject(fields.Where(field => field.Value == 1).Select(field => field.Key).ToList()) };
        }

        if (root.TryGetProperty("sort", out var sort) && sort.ValueKind == JsonValueKind.Array)
            stage = stage with { Sort = sort.EnumerateArray().Where(entry => entry.ValueKind != JsonValueKind.Null).Select(SortField).ToList() };

        if (Key(root, "page") is { } page)
            stage = stage with
            {
                Page = new LegacyPage(PageLimit(page), PageCursor(page), PageCount(page)),
            };

        return stage;
    }

    // The 1.1 page stage was a typed model: a member of another kind than its own never reached the engine, the host answered 400.

    private static int PageLimit(JsonElement page) => Member(page, "limit") switch
    {
        null => 50,
        { ValueKind: JsonValueKind.Number } limit when limit.TryGetInt32(out var size) => size,
        // The 1.1 hosts read a number written as text ("2"), as ASP.NET Core's JSON options do.
        { ValueKind: JsonValueKind.String } text when int.TryParse(text.GetString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var size) => size,
        _ => throw new LegacyBodyException("A page's 'limit' is a whole number."),
    };

    private static string? PageCursor(JsonElement page) => Member(page, "cursor") switch
    {
        null or { ValueKind: JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.String } cursor => cursor.GetString(),
        _ => throw new LegacyBodyException("A page's 'cursor' is a text."),
    };

    private static bool PageCount(JsonElement page) => Flag(page, "includeTotalCount", "A page's 'includeTotalCount' is true or false.");

    private static bool Flag(JsonElement holder, string name, string otherwise) => Member(holder, name) switch
    {
        null or { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.True } => true,
        _ => throw new LegacyBodyException(otherwise),
    };

    /// <summary>The object under a stage key, by the key's exact name; null for a key that is absent or holds no object.</summary>
    private static JsonElement? Key(JsonElement stage, string name) =>
        stage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    /// <summary>A member of a stage by its name without regard to case, as the host's JSON options read it: the last one, where it is written twice.</summary>
    private static JsonElement? Member(JsonElement holder, string name)
    {
        JsonElement? found = null;

        foreach (var property in holder.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                found = property.Value;

        return found;
    }

    private static string? Text(JsonElement holder, string name) =>
        Member(holder, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    // ---- match ---------------------------------------------------------------------------------

    private static LegacyMatch Match(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new LegacyBodyException("A match is a JSON object.");

        if (root.TryGetProperty("and", out var and))
            return new LegacyMatch { And = Conditions(and) };

        if (root.TryGetProperty("or", out var or))
            return new LegacyMatch { Or = Conditions(or) };

        if (root.TryGetProperty("not", out var not))
            return new LegacyMatch { Not = Condition(not) };

        return root.EnumerateObject().Any() ? new LegacyMatch { Condition = Condition(root) } : new LegacyMatch();
    }

    private static List<LegacyCondition> Conditions(JsonElement group) =>
        group.ValueKind == JsonValueKind.Array
            ? group.EnumerateArray().Select(Condition).ToList()
            : throw new LegacyBodyException("'and' and 'or' take an array of conditions.");

    private static LegacyCondition Condition(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new LegacyBodyException("A condition is a JSON object.");

        if (root.TryGetProperty("and", out var and))
            return new LegacyCondition { And = Conditions(and) };

        if (root.TryGetProperty("or", out var or))
            return new LegacyCondition { Or = Conditions(or) };

        if (root.TryGetProperty("not", out var not))
            return new LegacyCondition { Not = Condition(not) };

        // The first member that is no logical key is the path; what follows it was never read.
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("and", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("or", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("not", StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                string? op = null;
                JsonElement? operand = null;
                var ignoreCase = false;

                foreach (var written in property.Value.EnumerateObject())
                {
                    if (written.Name.Equals("options", StringComparison.OrdinalIgnoreCase))
                    {
                        if (written.Value.ValueKind == JsonValueKind.Object)
                            ignoreCase = written.Value.TryGetProperty("ignoreCase", out var flag) && flag.ValueKind == JsonValueKind.True;
                    }
                    else if (op is null)
                    {
                        op = written.Name;
                        operand = written.Value.Clone();
                    }
                }

                if (op is not null)
                    return new LegacyCondition { Path = property.Name, Op = op, Value = operand, IgnoreCase = ignoreCase };
            }

            // A bare value is an equality.
            return new LegacyCondition { Path = property.Name, Op = "eq", Value = property.Value.Clone() };
        }

        return new LegacyCondition();
    }

    // ---- group ---------------------------------------------------------------------------------

    private static LegacyGroup Group(JsonElement root)
    {
        var by = new List<LegacyGroupBy>();

        if (Member(root, "by") is { ValueKind: JsonValueKind.Array } keys)
            foreach (var key in keys.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object)
                    throw new LegacyBodyException("A group key is a JSON object.");

                var dateTrunc = Member(key, "dateTrunc") is { ValueKind: JsonValueKind.Object } truncated ? truncated : (JsonElement?)null;

                by.Add(new LegacyGroupBy(Text(key, "path"), dateTrunc is { } path ? Text(path, "path") : null, dateTrunc is { } unit ? Text(unit, "unit") : null, Text(key, "as")));
            }
        else
            throw new LegacyBodyException("A group carries 'by'.");

        var fields = new List<KeyValuePair<string, LegacyAggregation>>();

        if (Member(root, "fields") is { ValueKind: JsonValueKind.Object } aggregates)
            foreach (var aggregate in aggregates.EnumerateObject())
                fields.Add(new(aggregate.Name, Aggregation(aggregate.Value)));
        else
            throw new LegacyBodyException("A group carries 'fields'.");

        return new LegacyGroup(by, fields);
    }

    private static LegacyAggregation Aggregation(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new LegacyBodyException("An aggregate is a JSON object.");

        foreach (var property in root.EnumerateObject())
        {
            var function = property.Name.ToLowerInvariant();

            if (!AggregateFunctions.Contains(function))
                continue;

            return function == "count" && property.Value.ValueKind == JsonValueKind.True
                ? new LegacyAggregation("count", null)
                : new LegacyAggregation(function, Expression(property.Value));
        }

        throw new LegacyBodyException("Unknown aggregation function.");
    }

    private static LegacyExpression Expression(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.String)
            return new LegacyExpression { Path = root.GetString() };

        if (root.ValueKind != JsonValueKind.Object)
            throw new LegacyBodyException("An aggregate's argument is a path or a JSON object.");

        if (root.TryGetProperty("path", out var path))
            return new LegacyExpression { Path = path.GetString() };

        if (root.TryGetProperty("$var", out var variable))
            return new LegacyExpression { Var = variable.GetString() };

        if (root.TryGetProperty("literal", out var literal))
            return new LegacyExpression { Literal = LiteralOf(literal) };

        foreach (var property in root.EnumerateObject())
            if (ArithmeticOperators.Contains(property.Name))
                return new LegacyExpression
                {
                    Operator = property.Name.ToLowerInvariant(),
                    Operands = property.Value.ValueKind == JsonValueKind.Array ? property.Value.EnumerateArray().Select(Expression).ToList() : [],
                };

        return new LegacyExpression { Literal = LiteralOf(root) };
    }

    private static object? LiteralOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetInt64(out var whole) ? whole : element.GetDouble(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };

    // ---- project, sort -------------------------------------------------------------------------

    /// <summary>A nested projection as dotted paths; a path written twice keeps its first place and its last value.</summary>
    private static void Flatten(JsonElement element, string? prefix, List<KeyValuePair<string, int>> fields)
    {
        foreach (var property in element.EnumerateObject())
        {
            var path = prefix is null ? property.Name : prefix + "." + property.Name;

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                Flatten(property.Value, path, fields);
                continue;
            }

            var value = property.Value.ValueKind == JsonValueKind.Number
                ? property.Value.TryGetInt32(out var written) ? written : throw new LegacyBodyException($"The projection of '{path}' is 1 or 0.")
                : 1;
            var at = fields.FindIndex(field => string.Equals(field.Key, path, StringComparison.Ordinal));

            if (at >= 0)
                fields[at] = new(path, value);
            else
                fields.Add(new(path, value));
        }
    }

    private static LegacySortField SortField(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var property in root.EnumerateObject())
                return new LegacySortField(property.Name, property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString()!,
                    JsonValueKind.Null => "asc",
                    _ => throw new LegacyBodyException("A sort direction is 'asc' or 'desc'."),
                });

        throw new LegacyBodyException("A sort field is an object with one member: { \"path\": \"asc\" | \"desc\" }.");
    }
}
