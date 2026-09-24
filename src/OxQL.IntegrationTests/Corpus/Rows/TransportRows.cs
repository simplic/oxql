using OxQL.IntegrationTests.Fleet.Models.Transport;
using static OxQL.IntegrationTests.Fixtures.Rows.RowKit;

namespace OxQL.IntegrationTests.Fixtures.Rows;

/// <summary>
/// The transport service: <c>transport.shipment</c> (the temporal, enum and collection entity:
/// DST, the UTC day boundary, ISO weeks across a year, every enum member plus an absent one and a
/// stored value no member names, items and billing lines with inner collections, tags, the bag,
/// and a department that references the fleet service) and <c>transport.shipment_template</c>
/// (the volume entity: 6 000 thin rows in A, above the published offset ceiling).
/// </summary>
internal static class TransportRows
{
    public const string Shipment = "transport.shipment";
    public const string Template = "transport.shipment_template";

    // ── embedded things several rows share ──────────────────────────────────────────────────

    private static ShipmentStatus StatusOf(Org org, string which) => which switch
    {
        "open" => new() { Id = Ids.Of(Spaces.ShipmentStatus, org, 1), Name = "Open", Number = "10", Roles = ["dispatch"], HexColor = "#22c55e", OrderNr = 10, Resolver = "open", OrganizationId = org.Id() },
        "planned" => new() { Id = Ids.Of(Spaces.ShipmentStatus, org, 2), Name = "Planned", Number = "20", Roles = ["dispatch", "billing"], HexColor = "#3b82f6", OrderNr = 20, Resolver = "partially_planned", OrganizationId = org.Id() },
        "closed" => new() { Id = Ids.Of(Spaces.ShipmentStatus, org, 3), Name = "Closed", Number = "30", Roles = [], HexColor = "#64748b", OrderNr = 30, Resolver = "closed", OrganizationId = org.Id() },
        _ => throw new ArgumentOutOfRangeException(nameof(which)),
    };

    private static ShipmentItemStatus ItemStatusOf(Org org, bool open) => open
        ? new() { Id = Ids.Of(Spaces.ShipmentItemStatus, org, 1), Name = "Open", Number = "10", Roles = ["dispatch"], HexColor = "#22c55e", OrganizationId = org.Id() }
        : new() { Id = Ids.Of(Spaces.ShipmentItemStatus, org, 2), Name = "Closed", Number = "20", Roles = [], HexColor = "#64748b", OrganizationId = org.Id() };

    private static Quantity Qty(Org org, double value) => new()
    {
        Value = value,
        QuantityUnit = new QuantityUnit { Guid = Ids.Of(Spaces.QuantityUnit, org, 1), Name = "Kilogram", ShortName = "kg", Digits = 2 },
    };

    private static Article ArticleOf(Org org, int n, string name) => new() { Id = Ids.Of(Spaces.ShipmentArticle, org, n), Number = $"A-{n:D3}", Name = name };

    private static LoadingAidType AidTypeOf(Org org) => new()
    {
        Id = Ids.Of(Spaces.ShipmentLoadingAidType, org, 1),
        Number = 1,
        DisplayName = "Europalette",
        Weight = 25,
        ShortText = "EUR",
        Width = 800,
        Length = 1200,
        StoragePosition = 1,
    };

    private static ContactAddress ContactOf(Org org, string city = "Koeln", string zipcode = "50667") => new()
    {
        ContactId = Ids.Of(Spaces.Contact, org, 1),
        CompanyName = "Lab Customer AG",
        FirstName = "Max",
        LastName = "Kunde",
        Street = "Ringstrasse",
        HouseNumber = "7",
        Zipcode = zipcode,
        Country = "Germany",
        CountryIso = "DE",
        City = city,
        Latitude = 50.938,
        Longitude = 6.96,
        MatchCode = "KUNDE",
        OrganizationId = org.Id(),
    };

    private static ShipmentItem ItemOf(Org org, int n, int orderNumber = 1, Article? article = null, bool open = true, double quantity = 7) => new()
    {
        Id = Ids.Of(Spaces.ShipmentItem, org, n),
        Status = ItemStatusOf(org, open),
        Text = "lab item",
        Article = article ?? ArticleOf(org, 1, "Sand"),
        WeightNotes = [],
        LoadingMeters = 1.5,
        LoadingAidType = AidTypeOf(org),
        Quantity = Qty(org, quantity),
        Weight = Qty(org, 1200),
        OrderNumber = orderNumber,
    };

    private static BillingLine LineOf(Org org, int n, BillingLineType type = BillingLineType.Customer, double totalPrice = 100) => new()
    {
        Id = Ids.Of(Spaces.BillingLine, org, n),
        Date = Dt("2026-06-01T00:00:00Z"),
        Type = type,
        Text = $"line {n}",
        SinglePrice = 100,
        TotalPrice = totalPrice,
        Quantity = Qty(org, 1),
    };

    private static BillingLineReference ReferenceOf(Org org, int n, string dataType, string referenceId) =>
        new() { Id = Ids.Of(Spaces.BillingLineReference, org, n), DataType = dataType, ReferenceId = referenceId };

    private static CostCenterAssignment CostCenterOf(Org org, int n, string name, int number, decimal percentage) => new()
    {
        Id = Ids.Of(Spaces.BillingLineCostCenter, org, n),
        CostCenter = new CostCenter { Id = Ids.Of(Spaces.BillingLineCostCenter, org, 100 + n), Name = name, Number = number, ValidFrom = Dt("2026-01-01T00:00:00Z") },
        Percentage = percentage,
    };

    private static ShipmentTag TagOf(Org org, int n, string name, string group, string color) =>
        new() { Id = Ids.Of(Spaces.ShipmentTag, org, n), Name = name, GroupName = group, HexColor = color, OrganizationId = org.Id() };

    /// <summary>The department a shipment names: a <c>fleet.department</c> id, so the remote resolve has something to hit.</summary>
    private static ShipmentDepartment DepartmentOf(Guid id) => new() { Id = id, Name = "Dispatch", OrderId = 1, HexColor = "#0ea5e9" };

    // ── transport.shipment ──────────────────────────────────────────────────────────────────

    public static IReadOnlyList<CorpusRow> Shipments(Org org) => org switch
    {
        Org.A => ShipmentsA,
        Org.B => ShipmentsB,
        _ => [],
    };

    private static readonly IReadOnlyList<CorpusRow> ShipmentsA =
    [
        // Temporal: DST, the UTC day boundary, ISO weeks across a year boundary, several months.
        S(1, "dst-spring-before", "loadStart 30 minutes before the Europe/Berlin spring-forward: local 01:30 CET on 2026-03-29", Load("2026-03-29T00:30:00Z", "2026-03-29T00:45:00Z")),
        S(2, "dst-spring-after", "loadStart 30 minutes after the same transition: local 03:30 CEST, one UTC hour later but two local hours", Load("2026-03-29T01:30:00Z", "2026-03-29T01:45:00Z")),
        S(3, "dst-autumn-first", "the first pass through the repeated local hour on 2026-10-25: local 02:30 CEST", Load("2026-10-25T00:30:00Z", "2026-10-25T00:45:00Z")),
        S(4, "dst-autumn-second", "the second pass through the same local time: local 02:30 CET, one UTC hour later", Load("2026-10-25T01:30:00Z", "2026-10-25T01:45:00Z")),
        S(5, "utc-day-before", "UTC 2026-06-15 23:30, which is 2026-06-16 01:30 in Berlin: the UTC and local days differ", Load("2026-06-15T23:30:00Z", "2026-06-15T23:45:00Z")),
        S(6, "utc-day-after", "UTC 2026-06-16 00:30, the same Berlin day as utc-day-before on the next UTC day", Load("2026-06-16T00:30:00Z", "2026-06-16T00:45:00Z")),
        S(7, "utc-day-local-prev", "UTC 2026-06-15 21:30: the same UTC day as utc-day-before, the previous Berlin day", Load("2026-06-15T21:30:00Z", "2026-06-15T21:45:00Z")),
        S(8, "week-53-2026", "Wednesday 2026-12-30, ISO week 53 of 2026", Load("2026-12-30T12:00:00Z", "2026-12-30T13:00:00Z", "2026-12-31T08:00:00Z", "2026-12-31T10:00:00Z")),
        S(9, "week-53-2027side", "Saturday 2027-01-02: calendar year 2027, still ISO week 53 of 2026, so a week bucket crosses the year", Load("2027-01-02T12:00:00Z", "2027-01-02T13:00:00Z", "2027-01-03T08:00:00Z", "2027-01-03T10:00:00Z")),
        S(10, "week-01-2027", "Tuesday 2027-01-05, ISO week 1 of 2027: the first bucket after the crossing week", Load("2027-01-05T12:00:00Z", "2027-01-05T13:00:00Z", "2027-01-06T08:00:00Z", "2027-01-06T10:00:00Z")),
        S(11, "month-jan", "a January row, so a month bucket exists before the DST months", Load("2026-01-20T10:00:00Z", "2026-01-20T11:00:00Z")),
        S(12, "month-mar", "a March row before the transition, so the March bucket holds both offsets", Load("2026-03-10T10:00:00Z", "2026-03-10T11:00:00Z")),
        S(13, "midnight-berlin", "exactly midnight in Berlin (22:00Z the day before in summer): the dateTrunc day boundary itself", Load("2026-06-14T22:00:00Z", "2026-06-14T23:00:00Z")),

        // Enums, and the departments: a second department, the duplicate-named one, a miss and none.
        S(14, "enum-none", "loadingTimeType = None (0); department names the second fleet department", (s, _) =>
        {
            Times(s, LoadingDateTimeType.None, "planned");
            s.Department = DepartmentOf(Ids.Of(Spaces.Department, Org.A, 2));
        }),
        S(15, "enum-fixed", "loadingTimeType = Fixed (1); department names the fleet department whose name duplicates the first", (s, _) =>
        {
            Times(s, LoadingDateTimeType.Fixed, "planned");
            s.Department = DepartmentOf(Ids.Of(Spaces.Department, Org.A, 3));
        }),
        S(16, "enum-booking", "loadingTimeType = FixedWithBooking (2); department names no fleet department, so a remote resolve answers null", (s, _) =>
        {
            Times(s, LoadingDateTimeType.FixedWithBooking, "planned");
            s.Department = DepartmentOf(Ids.Dangling(3));
        }),
        S(17, "enum-absent", "loadingTimeType is absent although the model calls it non-nullable; department is null, so a remote resolve has no key", (s, raw) =>
        {
            s.Status = StatusOf(Org.A, "planned");
            s.Department = null;
            raw.Unset("loadingTimeType", "a non-nullable enum missing from storage, as when the member is added after the rows were written");
        }),
        S(18, "enum-unknown-value", "loadingTimeType holds 99, which names no member", (s, _) =>
        {
            s.Status = StatusOf(Org.A, "planned");
            s.LoadingTimeType = (LoadingDateTimeType)99;
        }),

        // Collections.
        S(19, "items-none", "items is the empty array", (s, _) => { s.Items = []; Closed(s); }),
        S(20, "items-one", "exactly one item, Open with quantity 7: it satisfies the correlated and the uncorrelated form", (s, _) => { s.Items = [ItemOf(Org.A, 20)]; Closed(s); }),
        S(21, "items-three", "three items, all Open", (s, _) =>
        {
            s.Items = [ItemOf(Org.A, 21), ItemOf(Org.A, 22, 2, ArticleOf(Org.A, 2, "Gravel")), ItemOf(Org.A, 23, 3, ArticleOf(Org.A, 3, "Cement"))];
            Closed(s);
        }),
        S(22, "items-split", "two items that each satisfy one half of items.status.name eq Open AND items.quantity.value gt 0; any() over the same condition does not match", (s, _) =>
        {
            s.Items = [ItemOf(Org.A, 24, quantity: 0), ItemOf(Org.A, 25, open: false, quantity: 5)];
            Closed(s);
        }),
        S(23, "items-missing", "items is absent although the model calls it non-nullable", (s, raw) =>
        {
            s.Items = [];
            Closed(s);
            raw.Unset("items", "a non-nullable collection absent from storage");
        }),
        S(24, "billing-nested", "two billing lines, the first with two references and two cost centres, the second with one reference and none: billingLines.references is a collection under a collection, so the outer unwind must come first", (s, _) =>
        {
            Closed(s);
            var first = LineOf(Org.A, 1);
            first.References = [ReferenceOf(Org.A, 1, "order", "O-1"), ReferenceOf(Org.A, 2, "invoice", "I-1")];
            first.CostCenters = [CostCenterOf(Org.A, 1, "CC-1", 100, 60.00m), CostCenterOf(Org.A, 2, "CC-2", 200, 40.00m)];
            var second = LineOf(Org.A, 2, BillingLineType.Carrier, 250);
            second.References = [ReferenceOf(Org.A, 3, "order", "O-2")];
            s.BillingLines = [first, second];
        }),
        S(25, "billing-none", "billingLines is the empty array, so the outer unwind drops the row unless it preserves empties", (s, _) => Closed(s)),
        S(26, "tags-none", "tags is the empty array", (s, _) => Closed(s)),
        S(27, "tags-many", "three tags, two sharing a groupName", (s, _) =>
        {
            s.Tags = [TagOf(Org.A, 1, "Urgent", "priority", "#ef4444"), TagOf(Org.A, 2, "Fragile", "handling", "#f59e0b"), TagOf(Org.A, 3, "Cooled", "handling", "#0ea5e9")];
            Closed(s);
        }),
        S(28, "tags-null", "tags is null, which is neither the empty array nor absent", (s, _) => { s.Tags = null; Closed(s); }),

        // Strings, nulls and duplicates on the sort key a grid uses.
        S(29, "number-null", "shipmentNumber is null", (s, _) => s.ShipmentNumber = null),
        S(30, "number-missing", "shipmentNumber is absent", (s, raw) => { s.ShipmentNumber = null; raw.Unset("shipmentNumber", "missing, which is not null"); }),
        S(31, "number-empty", "shipmentNumber is the empty string", (s, _) => s.ShipmentNumber = ""),
        S(32, "number-dup-a", "shipmentNumber S-DUP, first by id", (s, _) => s.ShipmentNumber = "S-DUP"),
        S(33, "number-dup-b", "shipmentNumber S-DUP, second by id", (s, _) => s.ShipmentNumber = "S-DUP"),
        S(34, "city-lower", "loadAddress.city in lower case, paired with city-upper", (s, _) => s.LoadAddress = ContactOf(Org.A, "köln")),
        S(35, "city-upper", "the same city upper-cased, differing only in case", (s, _) => s.LoadAddress = ContactOf(Org.A, "KÖLN")),
        S(36, "addon-rich", "the full addon bag on a shipment", (s, _) => s.Addon = Addons.Rich()),
        S(37, "addon-missing", "the bag is absent", (s, raw) => { s.Addon = null; raw.Unset("addon", "the bag absent, not empty"); }),
        S(38, "deleted", "isDeleted is true", (s, _) => s.IsDeleted = true),
        S(39, "status-open-late", "an Open shipment far in the future, so an Open filter and a date filter can disagree", Load("2027-06-01T08:00:00Z", "2027-06-01T10:00:00Z", "2027-06-02T08:00:00Z", "2027-06-02T10:00:00Z")),
        S(40, "weight-null", "actualWeight is null, so a sum over it skips the row rather than fail", (s, _) => s.ActualWeight = null),
    ];

    private static readonly IReadOnlyList<CorpusRow> ShipmentsB =
    [
        Row(Shipment, Spaces.Shipment, Org.B, 1, "b-items-one", "an exact clone of items-one in the other organisation", ShipmentBase, (s, _) =>
        {
            s.ShipmentNumber = "S-0020";
            s.Items = [ItemOf(Org.B, 20)];
        }),
        Row(Shipment, Spaces.Shipment, Org.B, 2, "b-number-dup", "an exact clone of number-dup-a", ShipmentBase, (s, _) =>
        {
            s.ShipmentNumber = "S-DUP";
            s.Items = [ItemOf(Org.B, 21)];
        }),
        Row(Shipment, Spaces.Shipment, Org.B, 3, "b-dst-spring-before", "an exact clone of dst-spring-before, so a temporal assertion also proves the scope", ShipmentBase, (s, _) =>
        {
            s.ShipmentNumber = "S-0001";
            s.LoadStart = Dt("2026-03-29T00:30:00Z");
            s.LoadEnd = Dt("2026-03-29T00:45:00Z");
            s.Items = [ItemOf(Org.B, 22)];
        }),
    ];

    private static CorpusRow S(int n, string key, string purpose, Action<Shipment, RawStorage>? change = null) =>
        Row(Shipment, Spaces.Shipment, Org.A, n, key, purpose, ShipmentBase, (s, raw) =>
        {
            s.ShipmentNumber = $"S-{n:D4}";
            change?.Invoke(s, raw);
        });

    private static Action<Shipment, RawStorage> Load(string start, string end, string? deliveryStart = null, string? deliveryEnd = null) => (s, _) =>
    {
        s.LoadStart = Dt(start);
        s.LoadEnd = Dt(end);

        if (deliveryStart is not null)
            s.DeliveryStart = Dt(deliveryStart);

        if (deliveryEnd is not null)
            s.DeliveryEnd = Dt(deliveryEnd);
    };

    private static void Times(Shipment shipment, LoadingDateTimeType type, string status)
    {
        shipment.LoadingTimeType = type;
        shipment.DeliveryTimeType = type;
        shipment.Status = StatusOf(Org.A, status);
    }

    private static void Closed(Shipment shipment) => shipment.Status = StatusOf(Org.A, "closed");

    private static (Shipment, RawStorage) ShipmentBase(Org org) => (new Shipment
    {
        CreateDateTime = Dt("2026-01-04T08:00:00Z"),
        UpdateDateTime = Dt("2026-02-04T08:00:00Z"),
        CreateUserId = LabUser,
        UpdateUserId = LabUser,
        LoadAddress = ContactOf(org),
        DeliveryAddress = ContactOf(org, "Hamburg", "20095"),
        Customer = new BillableContact { Address = ContactOf(org), AccountNumber = "10000", PersonalAccountId = null },
        LoadStart = Dt("2026-06-15T08:00:00Z"),
        LoadEnd = Dt("2026-06-15T10:00:00Z"),
        LoadingTimeType = LoadingDateTimeType.None,
        DeliveryStart = Dt("2026-06-16T08:00:00Z"),
        DeliveryEnd = Dt("2026-06-16T10:00:00Z"),
        DeliveryTimeType = LoadingDateTimeType.None,
        OrderDate = Dt("2026-06-01T08:00:00Z"),
        ShipmentNumber = "S-0001",
        ReferenceNumber = "R-0001",
        ActualWeight = Qty(org, 1200),
        IsTemplate = false,
        Status = StatusOf(org, "open"),
        Items = [ItemOf(org, 1)],
        BillingLines = [],
        Documents = [],
        Tags = [],
        IsDeleted = false,
        Tours = [],
        Department = DepartmentOf(Ids.Of(Spaces.Department, org, 1)),
        Addon = Addons.OneKey(),
    }, RawStorage.None
        .Unset("items.addon", "an item's own dictionary is absent: only the root member is the bag")
        .Unset("billingLines.addon", "a billing line's own dictionary is absent"));

    // ── transport.shipment_template ─────────────────────────────────────────────────────────

    /// <summary>How many templates organisation A holds: above the published offset ceiling on purpose.</summary>
    public const int TemplateVolume = 6000;

    /// <summary>How many templates organisation B holds: enough to show in a page if the scope ever stops firing.</summary>
    public const int TemplateVolumeB = 40;

    /// <summary>Ordinals whose templateName is the same value, so tie-breaking is observable at scale.</summary>
    public static readonly (int From, int To) TemplateDupRange = (1001, 1010);

    /// <summary>Ordinals whose templateName is null.</summary>
    public static readonly (int From, int To) TemplateNullRange = (2001, 2010);

    /// <summary>Ordinals whose templateName is absent, so missing sorts beside null rather than with it.</summary>
    public static readonly (int From, int To) TemplateMissingRange = (3001, 3010);

    private static readonly DateTime TemplateBase = Dt("2026-01-01T00:00:00Z");

    private static readonly LoadingDateTimeType[] TemplateEnums = [LoadingDateTimeType.None, LoadingDateTimeType.Fixed, LoadingDateTimeType.FixedWithBooking];

    private static readonly Lazy<IReadOnlyList<CorpusRow>> TemplatesA = new(() => Enumerable.Range(1, TemplateVolume).Select(n => TemplateRow(Org.A, n)).ToList());

    private static readonly Lazy<IReadOnlyList<CorpusRow>> TemplatesB = new(() => Enumerable.Range(1, TemplateVolumeB).Select(n => TemplateRow(Org.B, n)).ToList());

    public static IReadOnlyList<CorpusRow> Templates(Org org) => org switch
    {
        Org.A => TemplatesA.Value,
        Org.B => TemplatesB.Value,
        _ => [],
    };

    private static bool In(int n, (int From, int To) range) => n >= range.From && n <= range.To;

    /// <summary>The stored templateName of an ordinal: a value, null, or missing (the member left out).</summary>
    public static string? TemplateName(int n) =>
        In(n, TemplateDupRange) ? "T-DUP"
        : In(n, TemplateNullRange) || In(n, TemplateMissingRange) ? null
        : $"T-{n:D6}";

    /// <summary>
    /// One volume row. The first twenty rows carry a <c>createUserId</c> that names a
    /// <c>fleet.vehicle</c> (four real ones, then one that names nothing, cycling), because the
    /// member is declared a remote reference onto vehicles. Everything above twenty carries the
    /// lab user, which names no vehicle.
    /// </summary>
    private static CorpusRow TemplateRow(Org org, int n)
    {
        var purpose = In(n, TemplateDupRange) ? "volume, duplicate templateName"
            : In(n, TemplateNullRange) ? "volume, null templateName"
            : In(n, TemplateMissingRange) ? "volume, absent templateName"
            : "volume";

        return Row(Template, Spaces.ShipmentTemplate, org, n, $"{(org == Org.B ? "b-" : "")}tpl-{n:D6}", purpose, o =>
        {
            var even = n % 2 == 0;
            var raw = RawStorage.None.Unset("addon", "the volume rows carry no bag");

            if (In(n, TemplateMissingRange))
                raw.Unset("templateName", "missing, which is not null, at scale");

            return (new ShipmentTemplate
            {
                TemplateName = TemplateName(n),
                ShipmentNumber = $"SN-{n:D6}",
                ReferenceNumber = n % 7 == 0 ? null : $"R-{n:D6}",
                LoadingTimeType = TemplateEnums[n % 3],
                DeliveryTimeType = TemplateEnums[(n + 1) % 3],
                TimeMode = even ? TemplateTimeMode.Absolute : TemplateTimeMode.RelativeFromStart,
                LoadStart = even
                    ? new TemplateTime { AbsoluteTime = TemplateBase.AddMinutes(n), RelativeTime = null }
                    : new TemplateTime { AbsoluteTime = null, RelativeTime = new TimeSpan(1, 30, 0) },
                OrderDate = TemplateBase.AddHours(n),
                IsShipmentConversionDisabled = n % 11 == 0,
                CreateDateTime = TemplateBase.AddMinutes(n),
                UpdateDateTime = TemplateBase.AddMinutes(n).AddSeconds(1),
                CreateUserId = n <= 20 ? (n % 5 == 0 ? Ids.Dangling(2) : Ids.Of(Spaces.Vehicle, o, ((n - 1) % 4) + 1)) : LabUser,
                UpdateUserId = LabUser,
                CreateUserName = "lab",
                UpdateUserName = "lab",
                IsDeleted = n % 500 == 0,
            }, raw);
        });
    }
}
