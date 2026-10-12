// LEGACY(compat.v1): contract 1 legacy support. Remove with contract 1 compatibility; see src/docs/LEGACY.md.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace OxQL.Mongo.Legacy;

/// <summary>
/// The aggregate of a contract 1 request: the caller's organisation first, then every stage in the
/// order written, each path as written (<c>id</c> alone becomes <c>_id</c>), the default sort and
/// page added, a tie-breaker on <c>id</c> appended to the sort. Ported from the 1.1 normaliser and
/// builders; <c>src/docs/LEGACY.md</c> lists the behaviour that is kept as it is.
/// </summary>
internal sealed class LegacyAggregate
{
    private readonly LegacyVariables? variables;
    private readonly int regexMaxLength;

    private LegacyAggregate(LegacyVariables? variables, int regexMaxLength)
    {
        this.variables = variables;
        this.regexMaxLength = regexMaxLength;
    }

    /// <summary>The stages of the page, with one row more than the limit so a next page shows.</summary>
    public IReadOnlyList<BsonDocument> Page { get; private set; } = [];

    /// <summary>The stages of the count, when the page asks for it: everything but project, sort and page, then <c>$count</c>.</summary>
    public IReadOnlyList<BsonDocument>? Count { get; private set; }

    /// <summary>Every sort field of the request, in order: what a cursor is made of.</summary>
    public IReadOnlyList<LegacySortField> Sort { get; private set; } = [];

    public int Limit { get; private set; }

    /// <summary>
    /// Builds the aggregate. <paramref name="organisation"/> is the condition on the caller's
    /// organisation: it is matched before any stage of the request, merged into the first match and
    /// into every lookup's filter, so nothing is read, grouped or cut to a page before it.
    /// </summary>
    public static LegacyAggregate Build(LegacyRequest request, BsonDocument organisation, int defaultPageSize, int maxPageSize, int regexMaxLength)
    {
        var builder = new LegacyAggregate(request.Variables, regexMaxLength);
        var scoped = new LegacyCondition { Built = organisation };
        var pipeline = Normalise(Scope(request.Pipeline, scoped), defaultPageSize, maxPageSize);
        var sort = pipeline.Where(stage => stage.Sort is not null).SelectMany(stage => stage.Sort!).ToList();
        var page = pipeline.Select(stage => stage.Page).FirstOrDefault(stage => stage is not null) ?? new LegacyPage(50, null, false);
        var cursor = string.IsNullOrEmpty(page.Cursor) ? null : LegacyCursor.Read(page.Cursor, sort);
        var stages = new List<BsonDocument>();

        // The organisation is matched before any stage of the request, also where its first stage is no match.
        if (request.Pipeline.Count == 0 || request.Pipeline[0].Match is null)
            stages.Add(new BsonDocument("$match", organisation.DeepClone()));

        foreach (var stage in pipeline)
        {
            builder.Shared(stage, stages);

            if (stage.Project is { } project)
                stages.Add(Project(project));

            if (stage.Sort is { } fields)
                stages.Add(SortOf(fields));

            if (stage.Page is { } paged)
            {
                if (cursor is not null)
                    stages.Add(new BsonDocument("$match", LegacyCursor.Filter(cursor)));

                stages.Add(new BsonDocument("$limit", paged.Limit + 1));
            }
        }

        List<BsonDocument>? count = null;

        if (page.IncludeTotalCount)
        {
            count = [];

            if (request.Pipeline.Count == 0 || request.Pipeline[0].Match is null)
                count.Add(new BsonDocument("$match", organisation.DeepClone()));

            foreach (var stage in pipeline)
                builder.Shared(stage, count);

            count.Add(new BsonDocument("$count", "totalCount"));
        }

        builder.Page = stages;
        builder.Count = count;
        builder.Sort = sort;
        builder.Limit = page.Limit;

        return builder;
    }

    /// <summary>The stages the page and the count have in common.</summary>
    private void Shared(LegacyStage stage, List<BsonDocument> stages)
    {
        if (stage.Match is { } match)
            stages.Add(new BsonDocument("$match", Filter(match)));

        if (stage.Lookup is { } lookup)
            stages.Add(Lookup(lookup));

        if (stage.Unwind is { } unwind)
            stages.AddRange(Unwind(unwind));

        if (stage.Group is { } group)
            stages.AddRange(Group(group));
    }

    // ---- the organisation, the default sort and page ----------------------------------------------

    /// <summary>The organisation condition merged into the first match and into every lookup's filter; a match of it alone goes first when the request has none.</summary>
    private static List<LegacyStage> Scope(IReadOnlyList<LegacyStage> pipeline, LegacyCondition organisation)
    {
        var scoped = new List<LegacyStage>(pipeline.Count + 1);
        var merged = false;

        foreach (var stage in pipeline)
        {
            var current = stage;

            if (!merged && current.Match is { } match)
            {
                current = current with { Match = Merge(match, organisation) };
                merged = true;
            }

            if (current.Lookup is { } lookup)
                current = current with { Lookup = lookup with { Filter = lookup.Filter is { } filter ? Merge(filter, organisation) : new LegacyMatch { And = [organisation] } } };

            scoped.Add(current);
        }

        if (!merged)
            scoped.Insert(0, new LegacyStage { Match = new LegacyMatch { And = [organisation] } });

        return scoped;
    }

    private static LegacyMatch Merge(LegacyMatch existing, LegacyCondition organisation)
    {
        var written = existing.And is not null ? new LegacyCondition { And = existing.And }
            : existing.Or is not null ? new LegacyCondition { Or = existing.Or }
            : existing.Not is not null ? new LegacyCondition { Not = existing.Not }
            : existing.Condition;

        return new LegacyMatch { And = written is null ? [organisation] : [written, organisation] };
    }

    private static List<LegacyStage> Normalise(List<LegacyStage> pipeline, int defaultPageSize, int maxPageSize)
    {
        var normalised = new List<LegacyStage>(pipeline.Count + 2);
        var sorted = false;
        var paged = false;

        foreach (var stage in pipeline)
        {
            if (stage.Sort is { } fields)
            {
                sorted = true;
                normalised.Add(stage with { Sort = fields.Any(field => string.Equals(field.Path, "id", StringComparison.OrdinalIgnoreCase)) ? fields : [.. fields, new LegacySortField("id", "asc")] });
            }
            else if (stage.Page is { } page)
            {
                paged = true;
                normalised.Add(stage with { Page = page with { Limit = page.Limit <= 0 ? defaultPageSize : Math.Min(page.Limit, maxPageSize) } });
            }
            else
            {
                normalised.Add(stage);
            }
        }

        if (!sorted)
        {
            var sort = new LegacyStage { Sort = [new LegacySortField("id", "asc")] };
            var at = normalised.FindIndex(stage => stage.Page is not null);

            if (at >= 0)
                normalised.Insert(at, sort);
            else
                normalised.Add(sort);
        }

        if (!paged)
            normalised.Add(new LegacyStage { Page = new LegacyPage(defaultPageSize, null, false) });

        return normalised;
    }

    // ---- stages -------------------------------------------------------------------------------------

    private BsonDocument Lookup(LegacyLookup lookup)
    {
        var local = Translate(lookup.LocalPath!);
        var foreign = Translate(lookup.ForeignPath!);

        // Every lookup reads the other collection through a sub-pipeline that asks for the caller's organisation:
        // there is no form of it without one.
        var filter = lookup.Filter ?? throw new InvalidOperationException("A legacy lookup is built with the organisation's condition in its filter.");

        // Both values of 'convert' coerce the local value to a UUID.
        BsonValue left = string.IsNullOrWhiteSpace(lookup.Convert) ? "$$localValue" : CoerceToUuid("$$localValue");
        var pipeline = new BsonArray
        {
            new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { left, "$" + foreign }))),
        };

        pipeline.Add(new BsonDocument("$match", Filter(filter)));

        return new BsonDocument("$lookup", new BsonDocument
        {
            ["from"] = lookup.From,
            ["let"] = new BsonDocument("localValue", "$" + local),
            ["pipeline"] = pipeline,
            ["as"] = lookup.As,
        });
    }

    /// <summary>The coercion of a string to a UUID, in the database's JavaScript: a value that is no UUID string stays as it is.</summary>
    internal const string UuidCoerceBody =
        "function(v){ if (typeof v !== 'string') { return v; } try { return UUID(v); } catch (e) { return v; } }";

    private static BsonDocument CoerceToUuid(BsonValue input) => new("$function", new BsonDocument
    {
        ["body"] = UuidCoerceBody,
        ["args"] = new BsonArray { input },
        ["lang"] = "js",
    });

    private static IEnumerable<BsonDocument> Unwind(LegacyUnwind unwind)
    {
        var path = "$" + Translate(unwind.Path!);
        var document = new BsonDocument { ["path"] = path, ["preserveNullAndEmptyArrays"] = unwind.PreserveNull };

        if (unwind.IncludeIndex is not null)
            document["includeArrayIndex"] = unwind.IncludeIndex;

        yield return new BsonDocument("$unwind", document);

        // Without an alias the element takes the collection's place; with one it is also set under the alias.
        if (unwind.As is not null)
            yield return new BsonDocument("$set", new BsonDocument(unwind.As, path));
    }

    private IEnumerable<BsonDocument> Group(LegacyGroup group)
    {
        var document = new BsonDocument { ["_id"] = GroupId(group.By) };
        var distinct = new List<string>();

        foreach (var (name, aggregate) in group.Fields)
        {
            document[name] = Accumulator(aggregate);

            if (string.Equals(aggregate.Function, "countDistinct", StringComparison.OrdinalIgnoreCase))
                distinct.Add(name);
        }

        yield return new BsonDocument("$group", document);

        if (distinct.Count > 0)
        {
            var sizes = new BsonDocument();

            foreach (var name in distinct)
                sizes[name] = new BsonDocument("$size", "$" + name);

            yield return new BsonDocument("$addFields", sizes);
        }

        var reshape = new BsonDocument();

        foreach (var key in group.By)
            reshape[key.As] = group.By.Count == 1 ? "$_id" : "$_id." + key.As;

        foreach (var (name, _) in group.Fields)
            reshape[name] = "$" + name;

        reshape["_id"] = 0;

        yield return new BsonDocument("$project", reshape);
    }

    private static BsonValue GroupId(IReadOnlyList<LegacyGroupBy> by)
    {
        if (by.Count == 1)
            return GroupKey(by[0]);

        var id = new BsonDocument();

        foreach (var key in by)
            id[key.As] = GroupKey(key);

        return id;
    }

    private static BsonValue GroupKey(LegacyGroupBy key) => key.DateTruncPath is not null
        ? new BsonDocument("$dateTrunc", new BsonDocument { ["date"] = "$" + Translate(key.DateTruncPath), ["unit"] = key.DateTruncUnit })
        : new BsonString("$" + Translate(key.Path!));

    private BsonValue Accumulator(LegacyAggregation aggregate) => aggregate.Function?.ToLowerInvariant() switch
    {
        "count" => new BsonDocument("$sum", 1),
        "countdistinct" => new BsonDocument("$addToSet", Argument(aggregate.Argument)),
        "sum" => new BsonDocument("$sum", Argument(aggregate.Argument)),
        "avg" => new BsonDocument("$avg", Argument(aggregate.Argument)),
        "min" => new BsonDocument("$min", Argument(aggregate.Argument)),
        "max" => new BsonDocument("$max", Argument(aggregate.Argument)),
        "first" => new BsonDocument("$first", Argument(aggregate.Argument)),
        "last" => new BsonDocument("$last", Argument(aggregate.Argument)),
        "push" => new BsonDocument("$push", Argument(aggregate.Argument)),
        _ => throw new InvalidOperationException($"Unknown aggregation function: {aggregate.Function}"),
    };

    private BsonValue Argument(LegacyExpression? expression) => expression is null ? new BsonInt32(1) : Expression(expression);

    private BsonValue Expression(LegacyExpression expression)
    {
        if (expression.IsPath)
            return new BsonString("$" + Translate(expression.Path!));

        // A variable reaches the aggregate as the text of its JSON.
        if (expression.IsVar)
            return variables?.Get(expression.Var!) is { } value ? new BsonString(value.ToString()) : BsonNull.Value;

        if (expression.IsLiteral)
            return new BsonDocument("$literal", expression.Literal switch
            {
                string text => new BsonString(text),
                long whole => new BsonInt64(whole),
                double number => new BsonDouble(number),
                bool flag => flag ? BsonBoolean.True : BsonBoolean.False,
                var other => new BsonString(other!.ToString()!),
            });

        if (expression.IsArithmetic)
        {
            var operands = new BsonArray(expression.Operands!.Select(Expression));

            return expression.Operator switch
            {
                "add" => new BsonDocument("$add", operands),
                "subtract" => new BsonDocument("$subtract", operands),
                "multiply" => new BsonDocument("$multiply", operands),
                "divide" => new BsonDocument("$divide", operands),
                "coalesce" => new BsonDocument("$ifNull", operands),
                _ => throw new InvalidOperationException($"Unknown arithmetic operator: {expression.Operator}"),
            };
        }

        return BsonNull.Value;
    }

    private static BsonDocument Project(LegacyProject project)
    {
        var document = new BsonDocument();

        foreach (var path in project.Include)
            document[Translate(path)] = 1;

        return new BsonDocument("$project", document);
    }

    private static BsonDocument SortOf(IReadOnlyList<LegacySortField> fields)
    {
        var document = new BsonDocument();

        foreach (var field in fields)
            document[Translate(field.Path)] = field.IsAscending ? 1 : -1;

        return new BsonDocument("$sort", document);
    }

    /// <summary>The whole path <c>id</c> is the document's key; every other path is the database's as written.</summary>
    internal static string Translate(string path) => path == "id" ? "_id" : path;

    // ---- filters ------------------------------------------------------------------------------------

    private BsonDocument Filter(LegacyMatch match)
    {
        if (match.And is not null)
            return new BsonDocument("$and", new BsonArray(match.And.Select(Condition)));

        if (match.Or is not null)
            return new BsonDocument("$or", new BsonArray(match.Or.Select(Condition)));

        if (match.Not is not null)
            return new BsonDocument("$nor", new BsonArray { Condition(match.Not) });

        return match.Condition is not null ? Condition(match.Condition) : [];
    }

    private BsonDocument Condition(LegacyCondition condition)
    {
        if (condition.Built is not null)
            return (BsonDocument)condition.Built.DeepClone();

        if (condition.And is not null)
            return new BsonDocument("$and", new BsonArray(condition.And.Select(Condition)));

        if (condition.Or is not null)
            return new BsonDocument("$or", new BsonArray(condition.Or.Select(Condition)));

        if (condition.Not is not null)
            return new BsonDocument("$nor", new BsonArray { Condition(condition.Not) });

        var path = Translate(condition.Path!);
        var value = Bounded(Operand(condition.Value), condition);

        return condition.Op?.ToLowerInvariant() switch
        {
            "eq" => Equality(path, value, negate: false, condition.IgnoreCase),
            "neq" => Equality(path, value, negate: true, condition.IgnoreCase),
            "gt" => new BsonDocument(path, new BsonDocument("$gt", value)),
            "gte" => new BsonDocument(path, new BsonDocument("$gte", value)),
            "lt" => new BsonDocument(path, new BsonDocument("$lt", value)),
            "lte" => new BsonDocument(path, new BsonDocument("$lte", value)),
            "in" => new BsonDocument(path, new BsonDocument("$in", value.AsBsonArray)),
            "nin" => new BsonDocument(path, new BsonDocument("$nin", value.AsBsonArray)),
            "contains" => new BsonDocument(path, new BsonDocument("$regex", new BsonRegularExpression(Regex.Escape(value.AsString), condition.IgnoreCase ? "i" : ""))),
            "startswith" => new BsonDocument(path, new BsonDocument("$regex", new BsonRegularExpression("^" + Regex.Escape(value.AsString)))),
            "endswith" => new BsonDocument(path, new BsonDocument("$regex", new BsonRegularExpression(Regex.Escape(value.AsString) + "$"))),
            "exists" => new BsonDocument(path, new BsonDocument("$exists", value.AsBoolean)),
            "regex" => new BsonDocument(path, new BsonDocument("$regex", new BsonRegularExpression(value.AsString))),
            _ => throw new InvalidOperationException($"Unknown operator: {condition.Op}"),
        };
    }

    /// <summary>
    /// The operand, once every pattern in it is within <c>Limits:RegexMaxLength</c>: the text of a
    /// <c>regex</c> however it was written (as text or as a variable) and a pattern given by the
    /// <c>$regex</c> hint, also inside a list.
    /// </summary>
    private BsonValue Bounded(BsonValue operand, LegacyCondition condition)
    {
        var patterns = operand switch
        {
            BsonRegularExpression pattern => [pattern.Pattern],
            BsonArray items => items.OfType<BsonRegularExpression>().Select(pattern => pattern.Pattern),
            BsonString text when string.Equals(condition.Op, "regex", StringComparison.OrdinalIgnoreCase) => [text.Value],
            _ => Enumerable.Empty<string>(),
        };

        if (patterns.Any(pattern => pattern.Length > regexMaxLength))
            throw new LegacyRefusalException("REGEX_TOO_LONG", $"Regex pattern exceeds maximum length of {regexMaxLength}.", condition.Path);

        return operand;
    }

    /// <summary>An equality; a text that is a GUID matches the value stored as a UUID of either subtype and as that text.</summary>
    internal static BsonDocument Equality(string path, BsonValue value, bool negate, bool ignoreCase)
    {
        if (ignoreCase && value is BsonString folded)
        {
            var matching = new BsonDocument(path, new BsonDocument("$regex", new BsonRegularExpression("^" + Regex.Escape(folded.Value) + "$", "i")));

            return negate ? new BsonDocument("$nor", new BsonArray { matching }) : matching;
        }

        if (value is BsonString text && Guid.TryParse(text.Value, out var guid))
        {
            var candidates = new BsonArray
            {
                new BsonDocument(path, new BsonDocument("$eq", new BsonBinaryData(guid, GuidRepresentation.Standard))),
                new BsonDocument(path, new BsonDocument("$eq", new BsonBinaryData(guid, GuidRepresentation.CSharpLegacy))),
                new BsonDocument(path, new BsonDocument("$eq", value)),
            };

            return new BsonDocument(negate ? "$nor" : "$or", candidates);
        }

        return new BsonDocument(path, new BsonDocument(negate ? "$ne" : "$eq", value));
    }

    private BsonValue Operand(JsonElement? operand)
    {
        if (operand is not { } element)
            return BsonNull.Value;

        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("$var", out var name))
            return variables?.Get(name.GetString()!) is { } value ? Value(value) : BsonNull.Value;

        return Value(element);
    }

    /// <summary>A JSON operand as a database value, with the v1 type hints; an object that is no hint is its own text.</summary>
    internal static BsonValue Value(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("$uuid", out var uuid) && Guid.TryParse(uuid.GetString(), out var standard))
                return new BsonBinaryData(standard, GuidRepresentation.Standard);

            if (element.TryGetProperty("$uuid3", out var uuid3) && Guid.TryParse(uuid3.GetString(), out var legacy))
                return new BsonBinaryData(legacy, GuidRepresentation.CSharpLegacy);

            if (element.TryGetProperty("$date", out var date)
                && DateTimeOffset.TryParse(date.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
                return new BsonDateTime(instant.UtcDateTime);

            if (element.TryGetProperty("$oid", out var oid) && ObjectId.TryParse(oid.GetString(), out var objectId))
                return new BsonObjectId(objectId);

            if (element.TryGetProperty("$long", out var whole)
                && long.TryParse(whole.ValueKind == JsonValueKind.String ? whole.GetString() : whole.GetRawText(), out var number))
                return new BsonInt64(number);

            if (element.TryGetProperty("$decimal", out var dec)
                && Decimal128.TryParse(dec.ValueKind == JsonValueKind.String ? dec.GetString()! : dec.GetRawText(), out var decimal128))
                return new BsonDecimal128(decimal128);

            if (element.TryGetProperty("$regex", out var regex))
                return new BsonRegularExpression(regex.GetString()!);

            if (element.TryGetProperty("$null", out _))
                return BsonNull.Value;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => new BsonString(element.GetString()!),
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? new BsonInt64(integer) : new BsonDouble(element.GetDouble()),
            JsonValueKind.True => BsonBoolean.True,
            JsonValueKind.False => BsonBoolean.False,
            JsonValueKind.Null => BsonNull.Value,
            JsonValueKind.Array => new BsonArray(element.EnumerateArray().Select(Value)),
            _ => new BsonString(element.GetRawText()),
        };
    }
}
