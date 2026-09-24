using System.Globalization;
using OxQL.IntegrationTests.Fleet.Models.Conformance;
using static OxQL.IntegrationTests.Fixtures.Rows.RowKit;

namespace OxQL.IntegrationTests.Fixtures.Rows;

/// <summary>
/// The conformance service, every kind the model can represent, written wholly through the model
/// classes (the driver's own forms: a long above 2^53 as Int64, a char as Int32, a TimeSpan as its
/// string, a DateOnly as a date, a long enum as Int64, a Guid-keyed dictionary as an array of
/// key/value documents).
/// <list type="bullet">
/// <item><c>conformance.entity</c>: row 1 every member filled and every reference a hit; row 2 a
/// second full row for order and ranges (the enum value above 2^53); row 3 every nullable member
/// null, the collections empty and every reference a miss; row 4 in organisation B.</item>
/// <item><c>conformance.ref</c>, keyed on <c>code</c>: two in A, one in B.</item>
/// <item><c>conformance.child</c>, the lookup children: two under row 1, one under row 2, one in B
/// whose parent id names an A row.</item>
/// </list>
/// </summary>
internal static class ConformanceRows
{
    public const string Entity = "conformance.entity";
    public const string Ref = "conformance.ref";
    public const string Child = "conformance.child";

    private static Guid EntityId(Org org, int n) => Ids.Of(Spaces.ConformanceEntity, org, n);

    /// <summary>The id of a child; ordinal 99 names none.</summary>
    public static Guid ChildId(Org org, int n) => Ids.Of(Spaces.ConformanceChild, org, n);

    public static IReadOnlyList<CorpusRow> Entities(Org org) => org switch
    {
        Org.A =>
        [
            Full(Org.A, 1, "c-alpha", "every member filled; refCode, childId, employeeId and the widget codes all hit",
                "Alpha", 1, 9007199254740993L, 1234.50m, 0.25d, true, "2026-03-05", "2026-03-05T08:30:00Z", "01:30:00", 'A', [1, 2, 3, 4], ConformanceState.Active,
                "REF-A", ChildId(Org.A, 1), Ids.Of(Spaces.Employee, Org.A, 1), "W-1"),
            Full(Org.A, 2, "c-bravo", "a second full row with other values, for order and range predicates; its state is the long enum value above 2^53, and its employeeId names no employee",
                "Bravo", 2, 42L, 99.99m, 1.5d, false, "2026-04-06", "2026-04-06T17:45:00Z", "10:00:00", 'B', [9, 9], ConformanceState.Huge,
                "REF-B", ChildId(Org.A, 3), Ids.Dangling(4), "W-2"),
            Sparse(),
        ],
        Org.B =>
        [
            Full(Org.B, 4, "c-other-org", "organisation B, so tenant isolation is falsifiable",
                "Other org", 4, 7L, 1m, 1d, true, "2026-01-01", "2026-01-01T00:00:00Z", "00:10:00", 'Z', [7], ConformanceState.Draft,
                "REF-A", null, Ids.Of(Spaces.Employee, Org.B, 1), "W-3"),
        ],
        _ => [],
    };

    public static IReadOnlyList<CorpusRow> Refs(Org org) => org switch
    {
        Org.A =>
        [
            RefRow(Org.A, 1, "ref-a", "the target of c-alpha's refCode", "REF-A", "Alpha reference", 1),
            RefRow(Org.A, 2, "ref-b", "the target of c-bravo's refCode", "REF-B", "Beta reference", 2),
        ],
        Org.B => [RefRow(Org.B, 3, "b-ref-a", "organisation B's reference; its code differs because the key is the code alone", "REF-A-B", "Alpha of the other org", 1)],
        _ => [],
    };

    public static IReadOnlyList<CorpusRow> Children(Org org) => org switch
    {
        Org.A =>
        [
            ChildRow(Org.A, 1, "child-one", "the first child of c-alpha", EntityId(Org.A, 1), "Child one", 1),
            ChildRow(Org.A, 2, "child-two", "the second child of c-alpha", EntityId(Org.A, 1), "Child two", 2),
            ChildRow(Org.A, 3, "child-three", "the only child of c-bravo", EntityId(Org.A, 2), "Child three", 1),
        ],
        Org.B => [ChildRow(Org.B, 4, "b-child", "organisation B's child, whose parent id names an A row: the scope must hide it from A's lookup", EntityId(Org.A, 1), "Child of the other org", 9)],
        _ => [],
    };

    private static CorpusRow RefRow(Org org, int n, string key, string purpose, string code, string name, int rank)
    {
        var model = new ConformanceRef { Code = code, Name = name, Rank = rank, OrganizationId = org.Id() };

        return new CorpusRow<ConformanceRef>(Ref, org, n, key, purpose, model, Guid.Empty, RawStorage.None, code);
    }

    private static CorpusRow ChildRow(Org org, int n, string key, string purpose, Guid parent, string name, int sequence)
    {
        var model = new ConformanceChild { Id = ChildId(org, n), OrganizationId = org.Id(), ParentId = parent, Name = name, Sequence = sequence };

        return new CorpusRow<ConformanceChild>(Child, org, n, key, purpose, model, model.Id, RawStorage.None);
    }

    private static CorpusRow Full(
        Org org, int n, string key, string purpose,
        string name, int count, long magnitude, decimal amount, double ratio, bool active, string day, string moment, string duration,
        char grade, byte[] payload, ConformanceState state, string? refCode, Guid? childId, Guid? employeeId, string? widget) =>
        new CorpusRow<ConformanceEntity>(Entity, org, n, key, purpose,
            Build(org, n, name, count, magnitude, amount, ratio, active, day, moment, duration, grade, payload, state, refCode, childId, employeeId, widget),
            EntityId(org, n), RawStorage.None);

    private static ConformanceEntity Build(
        Org org, int n, string name, int count, long magnitude, decimal amount, double ratio, bool active, string day, string moment, string duration,
        char grade, byte[] payload, ConformanceState state, string? refCode, Guid? childId, Guid? employeeId, string? widget) => new()
    {
        Id = EntityId(org, n),
        OrganizationId = org.Id(),
        IsDeleted = false,
        Name = name,
        Note = name + " note",
        Count = count,
        OptionalCount = count,
        Magnitude = magnitude,
        OptionalMagnitude = magnitude,
        Amount = amount,
        OptionalAmount = amount,
        Ratio = ratio,
        OptionalRatio = ratio,
        Active = active,
        OptionalActive = active,
        Marker = Ids.Of(Spaces.ConformanceEntity, Org.A, count),
        OptionalMarker = Ids.Of(Spaces.ConformanceEntity, Org.A, count),
        Moment = Dt(moment),
        OptionalMoment = Dt(moment),
        Day = DateOnly.Parse(day, CultureInfo.InvariantCulture),
        OptionalDay = DateOnly.Parse(day, CultureInfo.InvariantCulture),
        Duration = TimeSpan.Parse(duration, CultureInfo.InvariantCulture),
        OptionalDuration = TimeSpan.Parse(duration, CultureInfo.InvariantCulture),
        Grade = grade,
        OptionalGrade = grade,
        Payload = payload,
        OptionalPayload = payload,
        State = state,
        OptionalState = state,
        Address = new ConformanceAddress { City = "Lab City " + count, Zip = "1000" + count, Floor = count },
        OptionalAddress = new ConformanceAddress { City = "Second City " + count, Zip = null, Floor = count + 1 },
        Tags = ["alpha", "beta", name.ToLowerInvariant()],
        Items =
        [
            new ConformanceItem
            {
                Id = Ids.Of(Spaces.ConformanceItem, org, count * 10 + 1),
                Code = $"IT-{count}-1",
                Quantity = count,
                Parts = [new ConformancePart { Sku = $"SKU-{count}-1-1", Weight = 1.5m }, new ConformancePart { Sku = $"SKU-{count}-1-2", Weight = 2.25m }],
            },
            new ConformanceItem
            {
                Id = Ids.Of(Spaces.ConformanceItem, org, count * 10 + 2),
                Code = $"IT-{count}-2",
                Quantity = count * 2,
                Parts = [new ConformancePart { Sku = $"SKU-{count}-2-1", Weight = 0.5m }],
            },
        ],
        Labels = new() { ["colour"] = "red", ["size"] = "L", ["with space"] = "yes" },
        Quantities = new() { [Ids.Of(Spaces.ConformanceQuantityKey, Org.A, 1)] = 10 * count, [Ids.Of(Spaces.ConformanceQuantityKey, Org.A, 2)] = 20 * count },
        Opaque = null,
        Scratch = "never stored",
        RefCode = refCode,
        ChildId = childId,
        EmployeeId = employeeId,
        WidgetCode = widget,
        WidgetCodeExplicit = widget,
        Addon = new Dictionary<string, object>
        {
            ["contractNumber"] = "C-" + count,
            ["Ablieferbelege vorhanden"] = active,
            ["zuschlag"] = 12.5m,
            ["status"] = count == 1 ? "open" : "closed",
            ["looseKey"] = "undefined on purpose",
            ["vincario"] = new Dictionary<string, object> { ["make"] = "Lab" },
        },
    };

    /// <summary>Row 3: every nullable member null, the collections empty, and every reference a miss.</summary>
    private static CorpusRow Sparse()
    {
        var sparse = Build(Org.A, 3, "Charlie", 3, 0L, 0m, 0d, false, "2026-05-07", "2026-05-07T00:00:00Z", "00:00:00", 'C', [], ConformanceState.Draft, null, null, null, null);

        sparse.Note = null;
        sparse.OptionalCount = null;
        sparse.OptionalMagnitude = null;
        sparse.OptionalAmount = null;
        sparse.OptionalRatio = null;
        sparse.OptionalActive = null;
        sparse.OptionalMarker = null;
        sparse.OptionalMoment = null;
        sparse.OptionalDay = null;
        sparse.OptionalDuration = null;
        sparse.OptionalGrade = null;
        sparse.OptionalPayload = null;
        sparse.OptionalState = null;
        sparse.OptionalAddress = null;
        sparse.Tags = [];
        sparse.Items = [];
        sparse.Labels = [];
        sparse.Quantities = [];
        sparse.Addon = [];
        sparse.RefCode = "REF-MISSING";
        sparse.ChildId = ChildId(Org.A, 99);
        sparse.EmployeeId = null;
        sparse.WidgetCode = "W-3";
        sparse.WidgetCodeExplicit = "W-3";

        return new CorpusRow<ConformanceEntity>(Entity, Org.A, 3, "c-charlie", "every nullable member null, the collections empty, refCode and childId name nothing, employeeId is null; the widget codes still hit",
            sparse, sparse.Id, RawStorage.None);
    }
}
