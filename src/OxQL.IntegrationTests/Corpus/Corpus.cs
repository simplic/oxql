using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures.Rows;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Conformance;
using OxQL.IntegrationTests.Fleet.Models.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Ledger;
using OxQL.IntegrationTests.Fleet.Models.Staff;
using OxQL.IntegrationTests.Fleet.Models.Transport;

namespace OxQL.IntegrationTests.Fixtures;

/// <summary>
/// The fixture corpus and the oracle: every row of every lab entity, deterministic and designed,
/// and the predicates a test computes its expected answer with before it sends a request. A test
/// never hard-codes a row set and never reads an expectation off the engine:
/// <code>
/// var expected = Corpus.SortedIds(Corpus.Shipment, "shipmentNumber", filter: row => Corpus.Text(row, "status.name") == "Open");
/// </code>
/// Every predicate reads the document as the seeder stores it (<see cref="CorpusRow.Stored"/>), so
/// a missing member, a decimal stored as a string or a value no enum member names is seen exactly
/// as the database sees it.
/// </summary>
public static class Corpus
{
    public const string Employee = StaffRows.Entity;
    public const string Vehicle = FleetRows.Vehicle;
    public const string Equipment = FleetRows.Equipment;
    public const string Department = FleetRows.Department;
    public const string Status = FleetRows.Status;
    public const string Shipment = TransportRows.Shipment;
    public const string Template = TransportRows.Template;
    public const string Transaction = LedgerRows.Entity;
    public const string Conformance = ConformanceRows.Entity;
    public const string ConformanceRef = ConformanceRows.Ref;
    public const string ConformanceChild = ConformanceRows.Child;

    /// <summary>How many templates organisation A holds: above the published offset ceiling (5 000).</summary>
    public const int TemplateVolume = TransportRows.TemplateVolume;

    /// <summary>How many templates organisation B holds.</summary>
    public const int TemplateVolumeB = TransportRows.TemplateVolumeB;

    /// <summary>Template ordinals that share <c>templateName</c> <c>T-DUP</c>.</summary>
    public static (int From, int To) TemplateDupRange => TransportRows.TemplateDupRange;

    /// <summary>Template ordinals whose <c>templateName</c> is null.</summary>
    public static (int From, int To) TemplateNullRange => TransportRows.TemplateNullRange;

    /// <summary>Template ordinals whose <c>templateName</c> is missing.</summary>
    public static (int From, int To) TemplateMissingRange => TransportRows.TemplateMissingRange;

    /// <summary>The stored templateName of a template ordinal; null for both the null and the missing range.</summary>
    public static string? TemplateName(int n) => TransportRows.TemplateName(n);

    private static readonly Dictionary<Org, IReadOnlyList<AddonDef>> FleetDefinitions = new() { [Org.A] = Addons.Fleet, [Org.B] = Addons.Fleet };

    /// <summary>Every entity of the corpus, in service order.</summary>
    public static readonly IReadOnlyList<CorpusEntity> Entities =
    [
        new(LabService.Staff, typeof(Employee), true, StaffRows.For, FleetDefinitions),
        new(LabService.Fleet, typeof(Vehicle), true, FleetRows.Vehicles, FleetDefinitions),
        new(LabService.Fleet, typeof(Equipment), false, FleetRows.Equipments),
        new(LabService.Fleet, typeof(Department), false, FleetRows.Departments),
        new(LabService.Fleet, typeof(VehicleStatus), false, FleetRows.Statuses),
        new(LabService.Transport, typeof(Shipment), true, TransportRows.Shipments, new Dictionary<Org, IReadOnlyList<AddonDef>>
        {
            // The retired twin is stored first, deliberately: see Addons.RetiredShadow.
            [Org.A] = [Addons.RetiredShadow, .. Addons.Fleet],
            [Org.B] = Addons.Fleet,
        }),
        new(LabService.Transport, typeof(ShipmentTemplate), false, TransportRows.Templates),
        new(LabService.Ledger, typeof(Transaction), false, LedgerRows.For),
        new(LabService.Conformance, typeof(ConformanceEntity), true, ConformanceRows.Entities, new Dictionary<Org, IReadOnlyList<AddonDef>> { [Org.A] = Addons.Conformance }),
        new(LabService.Conformance, typeof(ConformanceRef), false, ConformanceRows.Refs),
        new(LabService.Conformance, typeof(ConformanceChild), false, ConformanceRows.Children),
    ];

    /// <summary>The corpus entity with this id; throws on an id the corpus does not cover.</summary>
    public static CorpusEntity Entity(string entityId) =>
        Entities.FirstOrDefault(entity => entity.Id == entityId)
        ?? throw new ArgumentException($"'{entityId}' is not a corpus entity; one of {string.Join(", ", Entities.Select(entity => entity.Id))} is.", nameof(entityId));

    // ── rows and ids ────────────────────────────────────────────────────────────────────────

    /// <summary>Every row of an entity in one organisation, in ordinal order.</summary>
    public static IReadOnlyList<CorpusRow> Rows(string entityId, Org org = Org.A) => Entity(entityId).Rows(org);

    /// <summary>Every row of an entity in one organisation, typed.</summary>
    public static IReadOnlyList<CorpusRow<T>> Rows<T>(string entityId, Org org = Org.A) where T : class =>
        Rows(entityId, org).Cast<CorpusRow<T>>().ToList();

    /// <summary>One row by its key, in either organisation.</summary>
    public static CorpusRow Row(string entityId, string key) =>
        Rows(entityId, Org.A).Concat(Rows(entityId, Org.B)).FirstOrDefault(row => row.Key == key)
        ?? throw new ArgumentException($"'{entityId}' has no row keyed '{key}'.", nameof(key));

    /// <summary>One row by its key, typed.</summary>
    public static CorpusRow<T> Row<T>(string entityId, string key) where T : class => (CorpusRow<T>)Row(entityId, key);

    /// <summary>The id of a row, by key.</summary>
    public static Guid IdOf(string entityId, string key) => Row(entityId, key).Id;

    /// <summary>The ids of several rows, by key, in the order given.</summary>
    public static IReadOnlyList<Guid> IdsOf(string entityId, params string[] keys) => keys.Select(key => IdOf(entityId, key)).ToList();

    /// <summary>The ids of a row list, in the order given.</summary>
    public static IReadOnlyList<Guid> IdsOf(IEnumerable<CorpusRow> rows) => rows.Select(row => row.Id).ToList();

    /// <summary>Every id of an entity in one organisation, in ordinal (and so in id) order.</summary>
    public static IReadOnlyList<Guid> AllIds(string entityId, Org org = Org.A) => IdsOf(Rows(entityId, org));

    /// <summary>How many rows an entity has in organisations A and B.</summary>
    public static (int A, int B) Counts(string entityId) => (Rows(entityId, Org.A).Count, Rows(entityId, Org.B).Count);

    /// <summary>The rows of an entity a predicate keeps, in ordinal order.</summary>
    public static IReadOnlyList<CorpusRow> Where(string entityId, Func<CorpusRow, bool> predicate, Org org = Org.A) =>
        Rows(entityId, org).Where(predicate).ToList();

    /// <summary>The ids of the rows a predicate keeps, in id order.</summary>
    public static IReadOnlyList<Guid> IdsWhere(string entityId, Func<CorpusRow, bool> predicate, Org org = Org.A) =>
        IdsOf(Where(entityId, predicate, org));

    // ── reading a member ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The stored value at a wire path: <c>null</c> when the member is missing, <see cref="BsonNull"/>
    /// when it is stored null. A path through a collection (or ending on one) answers a
    /// <see cref="BsonArray"/> of the values, flattened, with missing elements left out; a wrapped
    /// addon value answers the value, not the wrapper.
    /// </summary>
    public static BsonValue? ValueAt(CorpusRow row, string wirePath) => StoragePath.Read(row.Stored, row.ModelType, wirePath);

    /// <summary>Whether the member exists and is neither null nor missing.</summary>
    public static bool Present(CorpusRow row, string wirePath) => ValueAt(row, wirePath) is { IsBsonNull: false };

    /// <summary>Whether the member is missing: not written at all.</summary>
    public static bool Missing(CorpusRow row, string wirePath) => ValueAt(row, wirePath) is null;

    /// <summary>Whether the member is stored as null.</summary>
    public static bool Null(CorpusRow row, string wirePath) => ValueAt(row, wirePath) is BsonNull;

    /// <summary>The string at a path, or null when the member is missing, null or not a string.</summary>
    public static string? Text(CorpusRow row, string wirePath) => ValueAt(row, wirePath) is BsonString text ? text.Value : null;

    /// <summary>The strings a path through a collection reaches; empty when there are none.</summary>
    public static IReadOnlyList<string> Texts(CorpusRow row, string wirePath) => ValueAt(row, wirePath) switch
    {
        BsonArray array => array.OfType<BsonString>().Select(value => value.Value).ToList(),
        BsonString text => [text.Value],
        _ => [],
    };

    /// <summary>The elements a path through a collection reaches: an empty list for missing or null.</summary>
    public static IReadOnlyList<BsonValue> Elements(CorpusRow row, string wirePath) => ValueAt(row, wirePath) switch
    {
        BsonArray array => array.ToList(),
        null or BsonNull => [],
        var single => [single],
    };

    /// <summary>A stored number as an exact decimal literal, or null when it is missing, null or not a number (a decimal stored as a string is not one).</summary>
    public static string? Number(CorpusRow row, string wirePath) =>
        ValueAt(row, wirePath) is { IsNumeric: true } number ? Order.NumberText(number) : null;

    /// <summary>A stored number as a <see cref="decimal"/>, or null when it is missing, null or not a number; a value beyond decimal's precision rounds.</summary>
    public static decimal? Decimal(CorpusRow row, string wirePath) =>
        Number(row, wirePath) is { } text ? decimal.Parse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>A stored date, or null when it is missing, null or not a date.</summary>
    public static DateTime? Date(CorpusRow row, string wirePath) =>
        ValueAt(row, wirePath) is BsonDateTime date ? date.ToUniversalTime() : null;

    /// <summary>The Guid at a path, or null.</summary>
    public static Guid? GuidAt(CorpusRow row, string wirePath) =>
        ValueAt(row, wirePath) is BsonBinaryData { SubType: BsonBinarySubType.UuidStandard } binary ? binary.ToGuid(GuidRepresentation.Standard) : null;

    /// <summary>The rows whose member at <paramref name="wirePath"/> is missing.</summary>
    public static IReadOnlyList<CorpusRow> RowsMissing(string entityId, string wirePath, Org org = Org.A) =>
        Where(entityId, row => Missing(row, wirePath), org);

    /// <summary>The rows whose member at <paramref name="wirePath"/> is stored null.</summary>
    public static IReadOnlyList<CorpusRow> RowsNull(string entityId, string wirePath, Org org = Org.A) =>
        Where(entityId, row => Null(row, wirePath), org);

    // ── order ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ids an entity's rows come back in for <c>sort</c> on <paramref name="wirePath"/>, with the
    /// <c>id: asc</c> tie-breaker the engine appends to every root sort. <paramref name="filter"/>
    /// narrows the set first. Strings compare under the default collation unless
    /// <paramref name="strings"/> says <see cref="StringOrder.Binary"/>.
    /// </summary>
    public static IReadOnlyList<Guid> SortedIds(
        string entityId, string wirePath, bool descending = false, Org org = Org.A,
        Func<CorpusRow, bool>? filter = null, StringOrder strings = StringOrder.Collated) =>
        IdsOf(Sorted(entityId, [(wirePath, descending)], org, filter, strings));

    /// <summary>The rows in the order of several sort keys, then <c>id: asc</c>.</summary>
    public static IReadOnlyList<CorpusRow> Sorted(
        string entityId, IReadOnlyList<(string WirePath, bool Descending)> keys, Org org = Org.A,
        Func<CorpusRow, bool>? filter = null, StringOrder strings = StringOrder.Collated)
    {
        var set = filter is null ? Rows(entityId, org) : Rows(entityId, org).Where(filter);

        return set.Order(Comparer<CorpusRow>.Create((a, b) =>
        {
            foreach (var (path, descending) in keys)
            {
                var primary = Order.Compare(ValueAt(a, path), ValueAt(b, path), strings, descending);

                if (primary != 0)
                    return descending ? -primary : primary;
            }

            return CompareIds(a, b);
        })).ToList();
    }

    /// <summary>The root order of two rows by key: binary subtype 4 compares byte for byte, which for these ids is text order.</summary>
    public static int CompareIds(CorpusRow a, CorpusRow b) => Order.CompareUtf8(a.WireId, b.WireId);

    /// <summary>The slice of an id list a <c>page</c> with <paramref name="limit"/> and <paramref name="offset"/> returns.</summary>
    public static IReadOnlyList<T> PageOf<T>(IReadOnlyList<T> ids, int limit = 100, int offset = 0) =>
        ids.Skip(offset).Take(limit).ToList();

    // ── ready-made predicates ───────────────────────────────────────────────────────────────

    /// <summary>The shipments whose <c>status.name</c> is exactly <paramref name="name"/>.</summary>
    public static IReadOnlyList<CorpusRow> ShipmentsWithStatus(string name, Org org = Org.A) =>
        Where(Shipment, row => Text(row, "status.name") == name, org);

    /// <summary>The shipments whose stored <c>loadingTimeType</c> is the named member's value; never the absent or the unknown row.</summary>
    public static IReadOnlyList<CorpusRow> ShipmentsWithLoadingTimeType(LoadingDateTimeType member, Org org = Org.A) =>
        Where(Shipment, row => ValueAt(row, "loadingTimeType") is BsonInt32 value && value.Value == (int)member, org);

    /// <summary>The shipments with exactly <paramref name="count"/> items; null counts the rows whose items are null or missing.</summary>
    public static IReadOnlyList<CorpusRow> ShipmentsWithItemCount(int? count, Org org = Org.A) =>
        Where(Shipment, row => ValueAt(row, "items") switch
        {
            null or BsonNull => count is null,
            BsonArray items => items.Count == count,
            _ => false,
        }, org);

    /// <summary>The employees whose <c>matchCode</c> equals <paramref name="value"/> under <paramref name="strings"/>.</summary>
    public static IReadOnlyList<CorpusRow> EmployeesWithMatchCode(string value, StringOrder strings = StringOrder.Collated, Org org = Org.A) =>
        Where(Employee, row => Text(row, "matchCode") is { } found && (strings == StringOrder.Collated ? Order.EqualsCi(found, value) : found == value), org);

    /// <summary>The vehicles whose <c>mileage</c> is stored as a number inside <c>[lo, hi]</c>; a decimal stored as a string never matches a numeric range.</summary>
    public static IReadOnlyList<CorpusRow> VehiclesWithMileageBetween(string lo, string hi, Org org = Org.A) =>
        Where(Vehicle, row => Number(row, "mileage") is { } mileage && Order.CompareDecimal(mileage, lo) >= 0 && Order.CompareDecimal(mileage, hi) <= 0, org);

    /// <summary>The vehicles whose <c>mileage</c> is stored as a string.</summary>
    public static IReadOnlyList<CorpusRow> VehiclesWithStringMileage(Org org = Org.A) =>
        Where(Vehicle, row => ValueAt(row, "mileage") is BsonString, org);

    /// <summary>The transactions whose stored <c>convertState</c> is the named member's value.</summary>
    public static IReadOnlyList<CorpusRow> TransactionsWithConvertState(ConvertState member, Org org = Org.A) =>
        Where(Transaction, row => ValueAt(row, "convertState") is BsonInt32 value && value.Value == (int)member, org);

    // ── temporal ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The buckets a <c>group</c> with a <c>dateTrunc</c> over <paramref name="wirePath"/> produces:
    /// bucket key (as the engine spells it) to the ids in it, in ascending bucket order. Rows whose
    /// member is not a date fall in no bucket here; the engine puts them in a null bucket.
    /// </summary>
    public static IReadOnlyList<(string Bucket, IReadOnlyList<Guid> Ids)> DateTruncBuckets(
        string entityId, string wirePath, string unit, string timeZone = Temporal.Berlin, string weekStart = "monday",
        Org org = Org.A, Func<CorpusRow, bool>? filter = null) =>
        (filter is null ? Rows(entityId, org) : Rows(entityId, org).Where(filter))
            .Select(row => (Row: row, Date: Date(row, wirePath)))
            .Where(entry => entry.Date is not null)
            .GroupBy(entry => Temporal.DateTruncUtc(entry.Date!.Value, unit, timeZone, weekStart))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, (IReadOnlyList<Guid>)group.Select(entry => entry.Row).Order(Comparer<CorpusRow>.Create(CompareIds)).Select(row => row.Id).ToList()))
            .ToList();
}
