using System.Text.Json.Nodes;
using MongoDB.Bson;

namespace OxQL.Mongo.Explain;

/// <summary>One line of the advisory: a field or a stage, whether an index serves it, which one, and what to know.</summary>
public sealed record IndexAdvice(string Field, bool? Used, string? Index, string? Note)
{
    /// <summary>The wire form: <c>{ field, used, index?, note? }</c>.</summary>
    public JsonObject ToJson()
    {
        var node = new JsonObject { ["field"] = Field, ["used"] = Used };

        if (Index is not null)
            node["index"] = Index;

        if (Note is not null)
            node["note"] = Note;

        return node;
    }
}

/// <summary>
/// The index advisory of an explain: the leading <c>$match</c> fields (the organisation scope,
/// the cursor predicate and the caller's first match, which the server coalesces) and the
/// <c>$sort</c> prefix matched against <c>listIndexes</c>, and every <c>$lookup</c> read from
/// the server's explain in both shapes it takes (<c>EQ_LOOKUP</c> with <c>strategy</c> and
/// <c>indexName</c>; the pipelined stage with <c>indexesUsed</c>). Pure: the server is read
/// through <see cref="IIndexSource"/> before this runs.
/// </summary>
public static class IndexAdvisor
{
    private const string KeyStorage = "_id";

    private enum PredicateKind
    {
        Equality,
        Range,
        Inequality,
        Regex,
        Exists,
        Other,
    }

    private sealed record Predicate(string Field, PredicateKind Kind, bool InOr, bool Anchored, bool IgnoreCase);

    private sealed record IndexDef(string Name, IReadOnlyList<(string Field, int Direction)> Keys);

    private sealed record LookupFinding(string? Index, string? Strategy, IReadOnlyList<string>? IndexesUsed);

    /// <summary>The advisory for a compiled page pipeline; <paramref name="collation"/> when the aggregate carries one.</summary>
    public static IReadOnlyList<JsonNode> Advise(IReadOnlyList<BsonDocument> pageStages, IReadOnlyList<BsonDocument> indexes, BsonDocument? explain, BsonDocument? collation = null)
    {
        ArgumentNullException.ThrowIfNull(pageStages);
        ArgumentNullException.ThrowIfNull(indexes);

        var defs = indexes.Select(Parse).Where(index => index is not null).Select(index => index!).ToList();
        var advice = new List<IndexAdvice>();

        var predicates = LeadingMatch(pageStages);
        var equalities = predicates.Where(predicate => predicate.Kind == PredicateKind.Equality && !predicate.InOr).Select(predicate => predicate.Field).ToHashSet(StringComparer.Ordinal);

        foreach (var field in predicates.Select(predicate => predicate.Field).Distinct(StringComparer.Ordinal))
            advice.Add(AdviseField(field, predicates.Where(predicate => predicate.Field == field).ToList(), defs, equalities));

        var sort = pageStages.FirstOrDefault(stage => stage.Contains("$sort"))?["$sort"].AsBsonDocument;

        if (sort is not null)
            advice.Add(AdviseSort(sort, defs, equalities));

        var lookups = pageStages.Where(stage => stage.Contains("$lookup")).Select(stage => stage["$lookup"].AsBsonDocument).ToList();

        if (lookups.Count > 0)
        {
            var findings = explain is null ? [] : LookupFindings(explain);

            for (var index = 0; index < lookups.Count; index++)
                advice.Add(AdviseLookup(lookups[index], index < findings.Count ? findings[index] : null, explain is null));
        }

        // An index serves a collated string comparison only when it was built with the same
        // collation; the advisory cannot tell a string field from another, so it says so once.
        if (collation is not null)
            advice.Add(new IndexAdvice("collation", null, null,
                $"the aggregate runs under collation {collation.GetValue("locale", "")}/{collation.GetValue("strength", "")}: string comparisons, sorts and group keys fold case, and an index serves them only when it was built with the same collation; other kinds are unaffected"));

        return advice.Select(entry => (JsonNode)entry.ToJson()).ToList();
    }

    // ---- indexes ----------------------------------------------------------------------------

    private static IndexDef? Parse(BsonDocument index)
    {
        if (!index.TryGetValue("key", out var key) || key is not BsonDocument keys)
            return null;

        var name = index.TryGetValue("name", out var value) ? value.ToString() ?? "" : "";
        var list = new List<(string, int)>();

        foreach (var element in keys)
            list.Add((element.Name, element.Value.IsNumeric ? Math.Sign(element.Value.ToDouble()) : 0));

        return new IndexDef(name, list);
    }

    /// <summary>The index that reaches <paramref name="field"/> through a prefix of equality fields, the shortest prefix first.</summary>
    private static IndexDef? Serving(string field, IReadOnlyList<IndexDef> indexes, IReadOnlySet<string> equalities)
    {
        IndexDef? best = null;
        var bestPosition = int.MaxValue;

        foreach (var index in indexes)
        {
            for (var position = 0; position < index.Keys.Count; position++)
            {
                if (index.Keys[position].Field == field)
                {
                    if (position < bestPosition)
                    {
                        best = index;
                        bestPosition = position;
                    }

                    break;
                }

                if (!equalities.Contains(index.Keys[position].Field))
                    break;
            }
        }

        return best;
    }

    // ---- leading match ----------------------------------------------------------------------

    private static List<Predicate> LeadingMatch(IReadOnlyList<BsonDocument> stages)
    {
        var predicates = new List<Predicate>();

        foreach (var stage in stages)
        {
            if (!stage.Contains("$match"))
                break;

            Collect(stage["$match"].AsBsonDocument, "", inOr: false, predicates);
        }

        return predicates;
    }

    private static void Collect(BsonDocument filter, string prefix, bool inOr, List<Predicate> into)
    {
        foreach (var element in filter)
        {
            switch (element.Name)
            {
                case "$and":
                    foreach (var member in element.Value.AsBsonArray.OfType<BsonDocument>())
                        Collect(member, prefix, inOr, into);
                    break;

                case "$or":
                case "$nor":
                    foreach (var member in element.Value.AsBsonArray.OfType<BsonDocument>())
                        Collect(member, prefix, inOr: true, into);
                    break;

                default:
                    if (element.Name.StartsWith('$'))
                        break;

                    Classify(prefix + element.Name, element.Value, inOr, into);
                    break;
            }
        }
    }

    private static void Classify(string field, BsonValue value, bool inOr, List<Predicate> into)
    {
        switch (value)
        {
            case BsonRegularExpression regex:
                into.Add(new Predicate(field, PredicateKind.Regex, inOr, regex.Pattern.StartsWith('^'), regex.Options.Contains('i')));
                return;

            case BsonDocument operators when operators.ElementCount > 0 && operators.GetElement(0).Name.StartsWith('$'):
                foreach (var op in operators)
                {
                    switch (op.Name)
                    {
                        case "$eq":
                        case "$in":
                            into.Add(new Predicate(field, PredicateKind.Equality, inOr, false, false));
                            break;

                        case "$gt":
                        case "$gte":
                        case "$lt":
                        case "$lte":
                            into.Add(new Predicate(field, PredicateKind.Range, inOr, false, false));
                            break;

                        case "$ne":
                        case "$nin":
                        case "$not":
                            into.Add(new Predicate(field, PredicateKind.Inequality, inOr, false, false));
                            break;

                        case "$regex":
                            var pattern = op.Value is BsonRegularExpression rx ? rx.Pattern : op.Value.ToString() ?? "";
                            var options = op.Value is BsonRegularExpression rxo ? rxo.Options : operators.TryGetValue("$options", out var declared) ? declared.ToString() ?? "" : "";

                            into.Add(new Predicate(field, PredicateKind.Regex, inOr, pattern.StartsWith('^'), options.Contains('i')));
                            break;

                        case "$exists":
                            into.Add(new Predicate(field, PredicateKind.Exists, inOr, false, false));
                            break;

                        case "$elemMatch" when op.Value is BsonDocument inner:
                            Collect(inner, field + ".", inOr, into);
                            break;

                        case "$options":
                            break;

                        default:
                            into.Add(new Predicate(field, PredicateKind.Other, inOr, false, false));
                            break;
                    }
                }

                return;

            default:
                into.Add(new Predicate(field, PredicateKind.Equality, inOr, false, false));
                return;
        }
    }

    private static IndexAdvice AdviseField(string field, IReadOnlyList<Predicate> predicates, IReadOnlyList<IndexDef> indexes, IReadOnlySet<string> equalities)
    {
        var serving = Serving(field, indexes, equalities);
        var notes = new List<string>();

        if (field == KeyStorage)
            notes.Add("the key: served by the _id index");

        foreach (var predicate in predicates)
        {
            switch (predicate.Kind)
            {
                case PredicateKind.Regex when predicate.IgnoreCase && predicate.Anchored:
                    notes.Add("case-insensitive equality: a full walk of the field's index, never a document scan");
                    break;

                case PredicateKind.Regex when predicate.Anchored:
                    notes.Add("anchored pattern: an index range on the prefix");
                    break;

                case PredicateKind.Regex:
                    notes.Add("unanchored pattern: every value of the member is scanned");
                    break;

                case PredicateKind.Inequality:
                    notes.Add("negation: the whole index is walked");
                    break;

                case PredicateKind.Range:
                    notes.Add("range");
                    break;

                case PredicateKind.Exists:
                    notes.Add("exists: an index on the field is walked in full");
                    break;
            }

            if (predicate.InOr)
                notes.Add("inside an or: every branch needs its own index");
        }

        if (serving is null)
            notes.Add(equalities.Count > 0 ? "no index starts with this field or reaches it through the leading equalities" : "no index starts with this field");

        return new IndexAdvice(field, serving is not null, serving?.Name, notes.Count == 0 ? null : string.Join("; ", notes.Distinct()));
    }

    // ---- sort -------------------------------------------------------------------------------

    private static IndexAdvice AdviseSort(BsonDocument sort, IReadOnlyList<IndexDef> indexes, IReadOnlySet<string> equalities)
    {
        var fields = sort.Select(element => (element.Name, Direction: element.Value.IsNumeric ? Math.Sign(element.Value.ToDouble()) : 0)).ToList();
        var label = "sort:" + string.Join(",", fields.Select(field => field.Name));

        if (Covering(fields, indexes, equalities) is { } full)
            return new IndexAdvice(label, true, full.Name, "the index orders the rows; no in-memory sort");

        var withoutTieBreak = fields.Count > 1 && fields[^1].Name == KeyStorage ? fields.Take(fields.Count - 1).ToList() : null;

        if (withoutTieBreak is not null && Covering(withoutTieBreak, indexes, equalities) is { } partial)
            return new IndexAdvice(label, false, partial.Name, "the index covers the sort fields but not the _id tie-breaker; the sort runs in memory");

        return new IndexAdvice(label, false, null, "no index supports this sort; it runs in memory, and with allowDiskUse off a large result is refused as QUERY_TOO_EXPENSIVE");
    }

    /// <summary>An index whose keys, after a prefix of leading equality fields, are the sort fields in order and in one direction.</summary>
    private static IndexDef? Covering(IReadOnlyList<(string Name, int Direction)> fields, IReadOnlyList<IndexDef> indexes, IReadOnlySet<string> equalities)
    {
        foreach (var index in indexes)
        {
            for (var start = 0; start + fields.Count <= index.Keys.Count; start++)
            {
                if (start > 0 && !equalities.Contains(index.Keys[start - 1].Field))
                    break;

                if (Matches(index, start, fields))
                    return index;
            }
        }

        return null;
    }

    private static bool Matches(IndexDef index, int start, IReadOnlyList<(string Name, int Direction)> fields)
    {
        int? flip = null;

        for (var offset = 0; offset < fields.Count; offset++)
        {
            var key = index.Keys[start + offset];

            if (key.Field != fields[offset].Name || key.Direction == 0 || fields[offset].Direction == 0)
                return false;

            var sign = key.Direction * fields[offset].Direction;

            flip ??= sign;

            if (sign != flip)
                return false;
        }

        return true;
    }

    // ---- lookups ----------------------------------------------------------------------------

    private static List<LookupFinding> LookupFindings(BsonDocument explain)
    {
        var findings = new List<LookupFinding>();

        Walk(explain);

        return findings;

        void Walk(BsonValue value)
        {
            switch (value)
            {
                case BsonDocument document:
                    if (document.Contains("$lookup") && document.TryGetValue("indexesUsed", out var used) && used is BsonArray usedArray)
                        findings.Add(new LookupFinding(usedArray.Count > 0 ? usedArray[0].ToString() : null, null, usedArray.Select(item => item.ToString() ?? "").ToList()));
                    else if (document.TryGetValue("stage", out var stage) && stage.IsString && stage.AsString is "EQ_LOOKUP" or "EQ_LOOKUP_UNWIND")
                        findings.Add(new LookupFinding(
                            document.TryGetValue("indexName", out var indexName) ? indexName.ToString() : null,
                            document.TryGetValue("strategy", out var strategy) ? strategy.ToString() : null,
                            null));

                    foreach (var element in document)
                        Walk(element.Value);
                    break;

                case BsonArray array:
                    foreach (var item in array)
                        Walk(item);
                    break;
            }
        }
    }

    private static IndexAdvice AdviseLookup(BsonDocument lookup, LookupFinding? finding, bool explainUnavailable)
    {
        var alias = lookup.TryGetValue("as", out var name) ? name.ToString() ?? "" : "";
        var label = "lookup:" + alias;

        if (finding is null)
            return new IndexAdvice(label, null, null, explainUnavailable
                ? "the server explain is unavailable; the join's index use is not known"
                : "the server explain reported no plan for this lookup");

        if (finding.IndexesUsed is not null)
            return new IndexAdvice(label, finding.IndexesUsed.Count > 0, finding.Index, finding.IndexesUsed.Count > 0
                ? "pipelined lookup; indexes used: " + string.Join(", ", finding.IndexesUsed)
                : "pipelined lookup without an index: every parent row scans the child collection");

        return new IndexAdvice(label, finding.Index is not null, finding.Index, finding.Strategy is null ? "EQ_LOOKUP" : "EQ_LOOKUP strategy " + finding.Strategy);
    }
}
