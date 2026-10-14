using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet.Models.Conformance;

namespace OxQL.IntegrationTests.Suites.Differential;

/// <summary>What a member of the edge corpus holds, as far as a comparison goes.</summary>
public enum EdgeKind
{
    Text,
    Number,
    Flag,
    Guid,
    Moment,
    Day,
    Span,
    Letter,
    State,
}

/// <summary>
/// A condition as a case writes it and as the documentation reads it: the JSON a <c>match</c>
/// takes, and whether a stored row meets it, computed here from the row alone. The rules are the
/// ones the semantics guide states (<i>Null, absent and empty</i>, <i>Case and accents</i>):
/// <list type="bullet">
/// <item>The values a path reaches: the member's value; through a collection the value of every
/// element; a member that is null or not stored, anywhere on the way, is a null; an empty
/// collection reaches nothing.</item>
/// <item><c>eq</c> holds when a value reached is equal (to null: when a null is reached), an
/// ordered or a text comparison when a value that is not null meets it, <c>neq</c>, <c>nin</c>
/// and <c>not</c> when their positive does not. <c>exists</c> asks whether the member is stored.</item>
/// <item>Text folds case and accents unless the condition says <c>caseSensitive</c>; a single
/// character folds its case; numbers of every stored type compare by value.</item>
/// </list>
/// Nothing here reads the engine.
/// </summary>
public abstract record EdgeCondition
{
    /// <summary>The condition as a <c>match</c> stage (or a filter entry's <c>where</c>) takes it.</summary>
    public abstract JsonObject Json();

    /// <summary>Whether a stored row meets it; with <paramref name="item"/> the row is one element of <c>items</c>, which an <c>any</c> reads its inner paths relative to.</summary>
    public abstract bool Holds(BsonValue row, bool item = false);

    public sealed override string ToString() => Json().ToJsonString();

    public sealed record Leaf(string Path, string Op, JsonNode? Operand, bool? CaseSensitive = null) : EdgeCondition
    {
        public override JsonObject Json()
        {
            var comparison = new JsonObject { [Op] = Operand?.DeepClone() };

            if (CaseSensitive is { } exact)
                comparison["options"] = new JsonObject { ["caseSensitive"] = exact };

            return new JsonObject { [Path] = comparison };
        }

        public override bool Holds(BsonValue row, bool item = false) => EdgeOracle.Holds(row, this, item);
    }

    public sealed record And(IReadOnlyList<EdgeCondition> Conditions) : EdgeCondition
    {
        public override JsonObject Json() => new() { ["and"] = new JsonArray(Conditions.Select(condition => (JsonNode?)condition.Json()).ToArray()) };

        public override bool Holds(BsonValue row, bool item = false) => Conditions.All(condition => condition.Holds(row, item));
    }

    public sealed record Or(IReadOnlyList<EdgeCondition> Conditions) : EdgeCondition
    {
        public override JsonObject Json() => new() { ["or"] = new JsonArray(Conditions.Select(condition => (JsonNode?)condition.Json()).ToArray()) };

        public override bool Holds(BsonValue row, bool item = false) => Conditions.Any(condition => condition.Holds(row, item));
    }

    public sealed record Not(EdgeCondition Condition) : EdgeCondition
    {
        public override JsonObject Json() => new() { ["not"] = Condition.Json() };

        public override bool Holds(BsonValue row, bool item = false) => !Condition.Holds(row, item);
    }

    /// <summary>One element of a collection of objects meets every inner condition; the inner paths are relative to the element.</summary>
    public sealed record Any(string Path, EdgeCondition Inner) : EdgeCondition
    {
        public override JsonObject Json() => new() { [Path] = new JsonObject { ["any"] = Inner.Json() } };

        public override bool Holds(BsonValue row, bool item = false) =>
            EdgeOracle.At(row, EdgeOracle.Storage(Path)) is BsonArray elements && elements.Any(element => element is BsonDocument && Inner.Holds(element, item: true));
    }
}

/// <summary>The members of the edge corpus a condition names, and the evaluation of one comparison.</summary>
public static class EdgeOracle
{
    /// <summary>The members a comparison names, with what each holds.</summary>
    public static readonly IReadOnlyDictionary<string, EdgeKind> Members = new Dictionary<string, EdgeKind>
    {
        ["name"] = EdgeKind.Text, ["note"] = EdgeKind.Text, ["address.city"] = EdgeKind.Text, ["address.zip"] = EdgeKind.Text, ["optionalAddress.zip"] = EdgeKind.Text, ["optionalAddress.city"] = EdgeKind.Text,
        ["refCode"] = EdgeKind.Text, ["tags"] = EdgeKind.Text, ["items.code"] = EdgeKind.Text, ["items.parts.sku"] = EdgeKind.Text,
        ["count"] = EdgeKind.Number, ["optionalCount"] = EdgeKind.Number, ["magnitude"] = EdgeKind.Number, ["optionalMagnitude"] = EdgeKind.Number,
        ["amount"] = EdgeKind.Number, ["optionalAmount"] = EdgeKind.Number, ["ratio"] = EdgeKind.Number, ["optionalRatio"] = EdgeKind.Number,
        ["address.floor"] = EdgeKind.Number, ["optionalAddress.floor"] = EdgeKind.Number, ["items.quantity"] = EdgeKind.Number, ["items.parts.weight"] = EdgeKind.Number,
        ["active"] = EdgeKind.Flag, ["optionalActive"] = EdgeKind.Flag, ["isDeleted"] = EdgeKind.Flag,
        ["id"] = EdgeKind.Guid, ["marker"] = EdgeKind.Guid, ["optionalMarker"] = EdgeKind.Guid, ["childId"] = EdgeKind.Guid, ["items.id"] = EdgeKind.Guid,
        ["moment"] = EdgeKind.Moment, ["optionalMoment"] = EdgeKind.Moment,
        ["day"] = EdgeKind.Day, ["optionalDay"] = EdgeKind.Day,
        ["duration"] = EdgeKind.Span, ["optionalDuration"] = EdgeKind.Span,
        ["grade"] = EdgeKind.Letter, ["optionalGrade"] = EdgeKind.Letter,
        ["state"] = EdgeKind.State, ["optionalState"] = EdgeKind.State,
    };

    /// <summary>The members of an element of <c>items</c>, as an <c>any</c> on it names them.</summary>
    private static readonly IReadOnlyDictionary<string, EdgeKind> ItemMembers = new Dictionary<string, EdgeKind>
    {
        ["code"] = EdgeKind.Text, ["quantity"] = EdgeKind.Number, ["id"] = EdgeKind.Guid, ["parts.sku"] = EdgeKind.Text, ["parts.weight"] = EdgeKind.Number,
    };

    /// <summary>The storage path of a wire path of the entity.</summary>
    public static string Storage(string wire) => StoragePath.StorageNameOf(typeof(ConformanceEntity), wire);

    /// <summary>The value at a storage path that crosses no collection; null when a step is not stored.</summary>
    public static BsonValue? At(BsonValue row, string storage)
    {
        BsonValue? current = row;

        foreach (var step in storage.Split('.'))
        {
            if (current is not BsonDocument document || !document.TryGetValue(step, out current))
                return null;
        }

        return current;
    }

    /// <summary>
    /// The values a path reaches (see <see cref="EdgeCondition"/>), and whether the member is stored
    /// anywhere it reaches: what <c>exists</c> asks.
    /// </summary>
    public static (List<BsonValue> Values, bool Stored) Reach(BsonValue row, string storage)
    {
        var values = new List<BsonValue>();
        var stored = false;

        Walk(row, storage.Split('.'), 0);

        return (values, stored);

        void Walk(BsonValue node, string[] steps, int at)
        {
            if (at == steps.Length)
            {
                stored = true;

                // A collection of scalars reaches each of them; an empty one reaches nothing.
                if (node is BsonArray leaves)
                    values.AddRange(leaves);
                else
                    values.Add(node);

                return;
            }

            switch (node)
            {
                case BsonArray elements:
                    foreach (var each in elements)
                        Walk(each, steps, at);
                    break;

                case BsonDocument document when document.TryGetValue(steps[at], out var next):
                    Walk(next, steps, at + 1);
                    break;

                // Null, not stored, or no object: nothing is held there.
                default:
                    values.Add(BsonNull.Value);
                    break;
            }
        }
    }

    internal static bool Holds(BsonValue row, EdgeCondition.Leaf leaf, bool item)
    {
        var (storage, kind) = item
            ? (StoragePath.StorageNameOf(typeof(ConformanceItem), leaf.Path), ItemMembers[leaf.Path])
            : (Storage(leaf.Path), Members[leaf.Path]);
        var (values, stored) = Reach(row, storage);

        switch (leaf.Op)
        {
            case "exists":
                return stored == leaf.Operand!.GetValue<bool>();

            case "eq":
                return values.Any(value => Equal(value, leaf.Operand, kind, leaf.CaseSensitive));

            case "neq":
                return !values.Any(value => Equal(value, leaf.Operand, kind, leaf.CaseSensitive));

            case "in":
                return leaf.Operand!.AsArray().Any(operand => values.Any(value => Equal(value, operand, kind, leaf.CaseSensitive)));

            case "nin":
                return !leaf.Operand!.AsArray().Any(operand => values.Any(value => Equal(value, operand, kind, leaf.CaseSensitive)));

            case "gt" or "gte" or "lt" or "lte":
                return values.Any(value => Compare(value, leaf.Operand!, kind) is { } order && leaf.Op switch { "gt" => order > 0, "gte" => order >= 0, "lt" => order < 0, _ => order <= 0 });

            case "contains" or "startsWith" or "endsWith":
            {
                var exact = leaf.CaseSensitive == true;
                var operand = leaf.Operand!.GetValue<string>();
                var asked = exact ? operand : Fold(operand);

                return values.OfType<BsonString>().Select(value => exact ? value.Value : Fold(value.Value)).Any(text => leaf.Op switch
                {
                    "contains" => text.Contains(asked, StringComparison.Ordinal),
                    "startsWith" => text.StartsWith(asked, StringComparison.Ordinal),
                    _ => text.EndsWith(asked, StringComparison.Ordinal),
                });
            }

            case "regex":
                return values.OfType<BsonString>().Any(value => Regex.IsMatch(value.Value, leaf.Operand!.GetValue<string>(), RegexOptions.CultureInvariant));

            default:
                throw new NotSupportedException($"The oracle does not read '{leaf.Op}'.");
        }
    }

    /// <summary>Text as the default collation compares it for equality: case and accents folded, and <c>ß</c> as <c>ss</c>.</summary>
    public static string Fold(string text) => Order.FoldCi(text).Replace("ß", "ss", StringComparison.Ordinal);

    private static bool IsNull(BsonValue value) => value.IsBsonNull || value.IsBsonUndefined;

    private static bool Equal(BsonValue value, JsonNode? operand, EdgeKind kind, bool? caseSensitive)
    {
        if (operand is null)
            return IsNull(value);

        if (IsNull(value))
            return false;

        return kind switch
        {
            EdgeKind.Text => value is BsonString text && (caseSensitive == true ? text.Value == operand.GetValue<string>() : Fold(text.Value) == Fold(operand.GetValue<string>())),
            EdgeKind.Letter => value is BsonInt32 code && (caseSensitive == true
                ? code.Value == operand.GetValue<string>()[0]
                : char.ToUpperInvariant((char)code.Value) == char.ToUpperInvariant(operand.GetValue<string>()[0])),
            EdgeKind.Flag => value is BsonBoolean flag && flag.Value == (operand.GetValueKind() == JsonValueKind.String ? bool.Parse(operand.GetValue<string>()) : operand.GetValue<bool>()),
            EdgeKind.Guid => value is BsonBinaryData binary && binary.ToGuid(GuidRepresentation.Standard) == System.Guid.Parse(operand.GetValue<string>()),
            _ => Compare(value, operand, kind) == 0,
        };
    }

    /// <summary>The order of a stored value against an operand; null where they do not compare (a null, a value of another type).</summary>
    private static int? Compare(BsonValue value, JsonNode operand, EdgeKind kind)
    {
        if (IsNull(value))
            return null;

        switch (kind)
        {
            case EdgeKind.Number:
                return value.IsNumeric ? Order.CompareDecimal(Order.NumberText(value), NumberText(operand)) : null;

            case EdgeKind.State:
                return value.IsNumeric ? Order.CompareDecimal(Order.NumberText(value), operand.GetValueKind() == JsonValueKind.String ? ((long)Enum.Parse<ConformanceState>(operand.GetValue<string>())).ToString(CultureInfo.InvariantCulture) : NumberText(operand)) : null;

            case EdgeKind.Text:
                return value is BsonString text ? Order.CompareCollated(text.Value, operand.GetValue<string>()) : null;

            case EdgeKind.Letter:
                return value is BsonInt32 code ? code.Value.CompareTo(operand.GetValue<string>()[0]) : null;

            case EdgeKind.Moment:
                return value is BsonDateTime moment ? moment.MillisecondsSinceEpoch.CompareTo(BsonUtils.ToMillisecondsSinceEpoch(DateTime.Parse(operand.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal))) : null;

            case EdgeKind.Day:
                return value is BsonDateTime day ? day.MillisecondsSinceEpoch.CompareTo(BsonUtils.ToMillisecondsSinceEpoch(DateTime.SpecifyKind(DateTime.ParseExact(operand.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc))) : null;

            case EdgeKind.Span:
                return value is BsonString span ? TimeSpan.Parse(span.Value, CultureInfo.InvariantCulture).CompareTo(XmlConvert.ToTimeSpan(operand.GetValue<string>())) : null;

            case EdgeKind.Flag:
                return value is BsonBoolean flag ? flag.Value.CompareTo(operand.GetValue<bool>()) : null;

            default:
                return null;
        }
    }

    private static string NumberText(JsonNode operand) => operand.GetValueKind() == JsonValueKind.String ? operand.GetValue<string>() : operand.ToJsonString();

    /// <summary>The keys of the rows of an organisation that meet a condition, as the wire writes them, in key order.</summary>
    public static List<string> Matching(EdgeCondition condition, Org org = Org.A) =>
        [.. EdgeCorpus.Stored(org).Where(row => condition.Holds(row)).Select(EdgeCorpus.WireId).Order(StringComparer.Ordinal)];
}
