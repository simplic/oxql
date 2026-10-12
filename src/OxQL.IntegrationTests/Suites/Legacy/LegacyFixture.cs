using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;

namespace OxQL.IntegrationTests.Suites.Legacy;

/// <summary>
/// The data the contract 1 legacy suites query: documents shaped as report queries read them
/// (<c>Fixtures/requests/report-*.json</c>), written as the driver stores them and not through the
/// fleet's models, because the legacy pipeline reads stored documents without a model: items with
/// discriminators (<c>_t</c>) and references, items that hold items of their own kind, stored members
/// no model declares (<c>ReportingInformation</c>, <c>Pricing</c>, an employee's <c>Bank</c>,
/// <c>Items</c>, <c>TaxClass</c>), and collections that are no entity (<c>context_data</c>,
/// <c>employee.holiday</c>, and <c>settings</c>, none of whose documents carries an organisation).
/// Every other collection also holds documents of another organisation and documents without one,
/// which no answer may contain.
/// <para>
/// A private fleet holds them (<see cref="FleetAsync"/>), so no other suite sees them; its hosts run
/// with the page sizes of the recording (500, 100) and set nothing of contract 1 compatibility, so
/// what they answer a request without the contract header is the product's default.
/// </para>
/// </summary>
public static class LegacyFixture
{
    /// <summary>The organisation the reports are printed for.</summary>
    public static readonly Guid Organisation = Id(900_001);

    /// <summary>Another organisation, whose documents share keys and join values with the first one's.</summary>
    public static readonly Guid Foreign = Id(900_002);

    public static readonly Guid InvoiceId = Id(1);
    public static readonly Guid SecondInvoiceId = Id(2);
    public static readonly Guid ForeignInvoiceId = Id(9);
    public static readonly Guid EmployeeId = Id(701);
    public static readonly Guid GlobalTourId = Id(1100);

    /// <summary>The page sizes the recorded answers were taken under.</summary>
    public static readonly IReadOnlyDictionary<string, string?> RecordedLimits = new Dictionary<string, string?>
    {
        ["OxQL:Limits:MaxPageSize"] = "500",
        ["OxQL:Limits:DefaultPageSize"] = "100",
    };

    private static readonly Lazy<Task<CorpusFleet>> Shared = new(
        () => CorpusFleet.CreateAsync("legacy", seed: [], extra: SeedAsync, contract1AsShipped: true),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The suites' fleet: no corpus, only these documents; it lives until the run ends.</summary>
    public static Task<CorpusFleet> FleetAsync() => Shared.Value;

    public static Guid Id(int n) => Guid.Parse($"1e9ac700-0000-4000-8000-{n:D12}");

    public static string FixturesDirectory([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Fixtures");

    /// <summary>
    /// A client of <paramref name="service"/> as the reports' organisation, without the contract
    /// header, on a host with the recording's page sizes and <paramref name="configuration"/> over them.
    /// </summary>
    public static async Task<LabClient> ClientAsync(LabService service, string variant = "legacy", IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var settings = new Dictionary<string, string?>(RecordedLimits, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
            settings[key] = value;

        return (await FleetAsync()).Variant(service, variant, settings).As(Organisation).Contract(null);
    }

    // ── documents ────────────────────────────────────────────────────────────────────────────

    private static BsonBinaryData U(Guid id) => new(id, GuidRepresentation.Standard);

    private static BsonBinaryData U(int n) => U(Id(n));

    private static string S(int n) => Id(n).ToString("D");

    private static BsonDateTime D(string instant) => new(DateTime.Parse(instant, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal));

    private static BsonDecimal128 M(string value) => new(Decimal128.Parse(value));

    private static async Task SeedAsync(LabFleet fleet)
    {
        var ledger = await fleet.DatabaseAsync(LabService.Ledger);
        var staff = await fleet.DatabaseAsync(LabService.Staff);
        var transport = await fleet.DatabaseAsync(LabService.Transport);

        await ledger.GetCollection<BsonDocument>("transaction").InsertManyAsync(Transactions());
        await ledger.GetCollection<BsonDocument>("context_data").InsertManyAsync(ContextData());
        await staff.GetCollection<BsonDocument>("employee").InsertManyAsync(Employees());
        await staff.GetCollection<BsonDocument>("employee.holiday").InsertManyAsync(Holidays());
        await transport.GetCollection<BsonDocument>("tour").InsertManyAsync(Tours());
        await transport.GetCollection<BsonDocument>("settings").InsertManyAsync(Settings());
    }

    private static BsonDocument Address(int n, string company, string city) => new()
    {
        ["_id"] = U(n),
        ["CompanyName"] = company,
        ["Street"] = "Am Kai",
        ["HouseNumber"] = "7",
        ["Zipcode"] = "20457",
        ["City"] = city,
        ["CountryIso"] = "DE",
    };

    private static BsonDocument Partner(int n, string company) => new()
    {
        ["_id"] = U(n),
        ["AccountNumber"] = "10001",
        ["CompanyName"] = company,
        ["FirstName"] = BsonNull.Value,
        ["LastName"] = BsonNull.Value,
        ["TaxGroupId"] = U(202),
        ["VatId"] = "DE123456789",
        ["Address"] = Address(n + 1000, company, "Hamburg"),
        ["CreditLimit"] = M("25000.00"),
    };

    private static BsonDocument Reference(int n, string type, BsonValue referenceId) => new()
    {
        ["_id"] = U(n),
        ["DataType"] = type,
        ["ReferenceId"] = referenceId,
    };

    private static BsonDocument ItemType(string deserializationType, string name) => new()
    {
        ["_id"] = U(150),
        ["DeserializationType"] = deserializationType,
        ["Name"] = name,
    };

    private static BsonArray Pricing(params (string Key, string Value)[] entries) =>
        new(entries.Select(entry => new BsonDocument { ["k"] = entry.Key, ["v"] = M(entry.Value) }));

    private static BsonDocument Line(int n, int sortNumber, string text, string net, BsonArray references) => new()
    {
        ["_t"] = "BillingLineTransactionItem",
        ["_id"] = U(n),
        ["Type"] = ItemType("billing_line", "Abrechnungszeile"),
        ["SortNumber"] = sortNumber,
        ["Text"] = text,
        ["Quantity"] = new BsonDocument { ["Value"] = M("1"), ["Unit"] = new BsonDocument { ["Name"] = "Stück", ["ShortName"] = "Stk" } },
        ["SinglePriceNet"] = M(net),
        ["TotalPriceNet"] = M(net),
        ["SinglePriceGross"] = M("1130.50"),
        ["TotalPriceGross"] = M("1130.50"),
        ["Pricing"] = Pricing(("base", net)),
        ["AssignedTransactionId"] = U(InvoiceId),
        ["References"] = references,
    };

    private static List<BsonDocument> Transactions() =>
    [
        new BsonDocument
        {
            ["_id"] = U(InvoiceId),
            ["OrganizationId"] = U(Organisation),
            ["IsDeleted"] = false,
            ["Number"] = "RE-2026-0042",
            ["Reference"] = "KD-4711",
            ["Type"] = new BsonDocument
            {
                ["_id"] = U(101), ["Name"] = "Rechnung", ["ReportName"] = "invoice", ["ShortName"] = "RE", ["Number"] = 10,
                ["Subtype"] = new BsonDocument { ["Number"] = 1, ["Name"] = "Standard", ["Report"] = new BsonDocument { ["Name"] = "invoice-standard", ["Engine"] = "telerik" } },
            },
            ["Date"] = D("2026-04-10T00:00:00Z"),
            ["DeliveryDate"] = D("2026-04-07T22:00:00Z"),
            ["FinancialPartner"] = Partner(201, "Nordhafen Logistik GmbH"),
            ["Items"] = new BsonArray
            {
                Line(401, 0, "Fracht Hamburg – München", "950.00",
                [
                    Reference(501, "shipment", S(601)),
                    Reference(502, "tour", S(602).ToUpperInvariant()),
                    Reference(503, "shipment", S(605)),
                ]),
                new BsonDocument
                {
                    ["_t"] = "TextTransactionItem", ["_id"] = U(402), ["Type"] = ItemType("text", "Text"), ["SortNumber"] = 1,
                    ["Text"] = "Wir berechnen folgende Leistungen <b>netto</b> & „ohne Abzug“:",
                },
                new BsonDocument
                {
                    ["_t"] = "BasicDiscountSurchargeOperationItem", ["_id"] = U(403), ["Type"] = ItemType("basic_discount_surcharge_operation", "Zuschlag"), ["SortNumber"] = 2,
                    ["Text"] = "Dieselzuschlag 5 %", ["ValueOperator"] = 0, ["Amount"] = M("5"), ["DeltaValue"] = M("47.50"),
                    ["AssignedTransactionItems"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["_t"] = "BillingLineTransactionItem", ["_id"] = U(401), ["Text"] = "Fracht Hamburg – München", ["SortNumber"] = 0,
                            ["SinglePriceNet"] = M("950.00"), ["TotalPriceNet"] = M("950.00"), ["SinglePriceGross"] = M("1130.50"), ["TotalPriceGross"] = M("1130.50"),
                            ["AssignedTransactionId"] = U(InvoiceId),
                            ["Pricing"] = Pricing(("base", "950.00"), ("surcharge", "47.50"), ("result", "997.50")),
                        },
                        new BsonDocument
                        {
                            ["_t"] = "ArticleTransactionItem", ["_id"] = U(405), ["Text"] = "Palettentausch", ["SortNumber"] = 4,
                            ["SinglePriceNet"] = M("12.50"), ["TotalPriceNet"] = M("50.00"), ["SinglePriceGross"] = M("14.875"), ["TotalPriceGross"] = M("59.50"),
                            ["AssignedTransactionId"] = U(InvoiceId),
                            ["Pricing"] = Pricing(("base", "50.00"), ("result", "52.50")),
                        },
                    },
                    ["References"] = new BsonArray(),
                },
                new BsonDocument
                {
                    ["_t"] = "GroupTransactionItem", ["_id"] = U(404), ["Type"] = ItemType("group", "Gruppe"), ["SortNumber"] = 3, ["Text"] = "Nebenleistungen",
                    ["Items"] = new BsonArray
                    {
                        new BsonDocument { ["_t"] = "ArticleTransactionItem", ["_id"] = U(405), ["Text"] = "Palettentausch", ["SortNumber"] = 4, ["TotalPriceNet"] = M("50.00") },
                    },
                    ["References"] = new BsonArray
                    {
                        Reference(505, "shipment", S(603)),
                        Reference(506, "shipment", S(604)),
                        Reference(507, "Tariff", "TARIFF-2026-STD"),
                        Reference(508, "shipment", BsonNull.Value),
                    },
                },
            },
            ["ConvertState"] = 0,
            ["FinancialAccountingPeriod"] = "2026-04",
            ["States"] = new BsonArray { "posted", "exported" },
            ["TaxGroup"] = "DE",
            ["TotalPrice"] = M("1190.00"),
            ["TotalPriceGross"] = M("1190.00"),
            ["TotalPriceNet"] = M("1000.00"),
            ["TotalPriceTax"] = M("190.00"),
            ["TransactionYear"] = 2026,
            ["SignedTotalPriceNet"] = M("-1000.00"),
            ["SignedTotalPriceGross"] = M("-1190.00"),
            ["SignedTotalPrice"] = M("-1190.00"),
            ["SignedTotalPriceTax"] = M("-190.00"),
            ["TaxKeyTotalPrices"] = new BsonArray
            {
                new BsonDocument { ["TaxKey"] = "19", ["TaxKeyValue"] = M("19"), ["TotalPrice"] = M("1130.50"), ["TotalPriceNet"] = M("950.00"), ["TotalPriceGross"] = M("1130.50"), ["TotalPriceTax"] = M("180.50") },
                new BsonDocument { ["TaxKey"] = "7", ["TaxKeyValue"] = M("7"), ["TotalPrice"] = M("53.50"), ["TotalPriceNet"] = M("50.00"), ["TotalPriceGross"] = M("53.50"), ["TotalPriceTax"] = M("3.50") },
            },
            ["Sign"] = -1,
            ["OperationItemCombinationMode"] = 0,
            ["IsGross"] = false,
            ["FinancialExportDisabled"] = false,
            ["ValidationResult"] = new BsonDocument { ["IsValid"] = true, ["Errors"] = new BsonArray() },
            ["DefaultCostCenters"] = new BsonArray { new BsonDocument { ["_id"] = U(160), ["Percentage"] = M("100") } },
            ["DefaultCostObjects"] = new BsonArray(),
            ["CreateDateTime"] = D("2026-04-10T07:15:30.123Z"),
            ["CreateUserId"] = U(301),
            ["UpdateDateTime"] = D("2026-04-11T09:00:00Z"),
            ["UpdateUserId"] = BsonNull.Value,
            ["UpdateUser"] = new BsonDocument { ["_id"] = U(302), ["Name"] = "anna.becker" },
            ["ReferenceNumber"] = "REF-2026-1",
            ["TermsOfPayment"] = new BsonDocument { ["_id"] = U(170), ["Name"] = "14 Tage netto", ["Number"] = 14, ["CashDiscount"] = M("2") },
            ["PaymentMethod"] = new BsonDocument { ["Name"] = "Überweisung", ["Number"] = 1 },
            ["Currency"] = new BsonDocument { ["_id"] = U(180), ["Name"] = "Euro", ["Number"] = 978, ["ShortName"] = "EUR", ["Symbol"] = "€" },
            ["Description"] = BsonNull.Value,
            ["DeliveryAddress"] = Partner(210, "Südlager München KG"),
            ["InvoiceRecipient"] = Partner(220, "Nordhafen Logistik GmbH"),
            ["Payer"] = BsonNull.Value,
            ["Creator"] = Partner(230, "Simplic Spedition"),
            ["CashDiscountDate"] = D("2026-04-17T00:00:00Z"),
            ["DueDate"] = D("2026-04-24T00:00:00Z"),
            ["Notes"] = "Vielen Dank für Ihren Auftrag.",
            ["ManualVat"] = BsonNull.Value,
            ["Period"] = "04/2026",
            ["CashDiscountTotal"] = M("23.80"),
            ["CashDiscountPercentValue"] = M("2"),
            ["BillToText"] = "Nordhafen Logistik GmbH\nAm Kai 7\n20457 Hamburg",
            ["Barcode"] = "RE20260042",
            ["Balance"] = M("0"),
            ["AlternativePaymentDeadline"] = BsonNull.Value,
            // No model declares it; the report prints it.
            ["ReportingInformation"] = new BsonDocument
            {
                ["Headline"] = "Rechnung", ["Lines"] = new BsonArray { "Zeile 1", "Zeile 2" }, ["Weight"] = 1250.5, ["Positions"] = 3L, ["PrintedAt"] = D("2026-04-10T08:00:00Z"),
            },
        },
        new BsonDocument
        {
            ["_id"] = U(SecondInvoiceId), ["OrganizationId"] = U(Organisation), ["IsDeleted"] = false, ["Number"] = "RE-2026-0043",
            // Its one reference names the context document whose organisation is stored as a legacy UUID.
            ["Items"] = new BsonArray { Line(411, 0, "Lagergeld", "10.00", [Reference(511, "shipment", S(606))]) },
            ["TotalPriceNet"] = M("10.00"), ["TransactionYear"] = 2026, ["TaxKeyTotalPrices"] = new BsonArray(),
        },
        new BsonDocument
        {
            ["_id"] = U(ForeignInvoiceId), ["OrganizationId"] = U(Foreign), ["IsDeleted"] = false, ["Number"] = "FOREIGN-0001", ["TotalPriceNet"] = M("777.00"), ["TransactionYear"] = 2026,
            ["Items"] = new BsonArray { Line(491, 0, "Foreign line", "777.00", [Reference(591, "shipment", S(601))]) },
            ["TaxKeyTotalPrices"] = new BsonArray(),
        },
        // A document without an organisation: never read.
        new BsonDocument { ["_id"] = U(8), ["IsDeleted"] = false, ["Number"] = "ORPHAN-0001", ["TotalPriceNet"] = M("5.00"), ["TransactionYear"] = 2026, ["Items"] = new BsonArray() },
    ];

    private static List<BsonDocument> ContextData() =>
    [
        new BsonDocument
        {
            ["_id"] = U(601), ["_t"] = "ShipmentContextData", ["OrganizationId"] = U(Organisation), ["ShipmentNumber"] = "S-1001", ["LoadNumber"] = "L-77",
            ["Status"] = new BsonDocument { ["Name"] = "delivered" }, ["Weight"] = 1250.5, ["Pieces"] = 12,
            ["LoadingAddress"] = Address(611, "Nordhafen Logistik GmbH", "Hamburg"), ["DeliveryAddress"] = Address(612, "Südlager München KG", "München"),
            ["LoadingDate"] = D("2026-04-07T06:30:00Z"), ["AssignedTransactionId"] = U(InvoiceId),
            ["Items"] = new BsonArray { new BsonDocument { ["Text"] = "Paletten", ["Quantity"] = new BsonDocument { ["Value"] = M("12") } } },
        },
        new BsonDocument
        {
            ["_id"] = U(602), ["_t"] = "TourContextData", ["OrganizationId"] = U(Organisation), ["Number"] = "TR-2026-0101", ["Driver"] = "Erika Mustermann",
            ["StartDateTime"] = D("2026-04-07T05:00:00Z"), ["Distance"] = 780L,
        },
        // Of another organisation: a reference to it joins nothing.
        new BsonDocument { ["_id"] = U(603), ["_t"] = "ShipmentContextData", ["OrganizationId"] = U(Foreign), ["ShipmentNumber"] = "FOREIGN" },
        // Without an organisation: never read.
        new BsonDocument { ["_id"] = U(604), ["_t"] = "ShipmentContextData", ["ShipmentNumber"] = "NO-ORGANISATION" },
        // The organisation as a text and as a legacy UUID: both are the caller's. (A row that holds a legacy UUID is a
        // coded 500. The second invoice joins this document and reads one text of it.)
        new BsonDocument { ["_id"] = U(605), ["_t"] = "ShipmentContextData", ["OrganizationId"] = Organisation.ToString("D"), ["ShipmentNumber"] = "S-1005" },
        new BsonDocument { ["_id"] = U(606), ["_t"] = "ShipmentContextData", ["OrganizationId"] = new BsonBinaryData(Organisation, GuidRepresentation.CSharpLegacy), ["ShipmentNumber"] = "S-1006" },
    ];

    private static List<BsonDocument> Employees() =>
    [
        new BsonDocument
        {
            ["_id"] = U(EmployeeId), ["OrganizationId"] = U(Organisation), ["IsDeleted"] = false, ["MatchCode"] = "MUSTERMANN, ERIKA",
            ["Address"] = new BsonDocument { ["FirstName"] = "Erika", ["LastName"] = "Mustermann", ["Street"] = "Lindenweg 3", ["Zipcode"] = "86150", ["City"] = "Augsburg" },
            ["Birthday"] = D("1988-11-02T23:00:00Z"),
            ["Employment"] = new BsonDocument { ["EntryDate"] = D("2019-02-28T23:00:00Z"), ["PersonnelNumber"] = "P-0042" },
            ["EmailAddresses"] = new BsonArray
            {
                new BsonDocument { ["_id"] = U(711), ["Address"] = "erika.mustermann@example.org", ["Type"] = "work" },
                new BsonDocument { ["_id"] = U(712), ["Address"] = "erika@example.net", ["Type"] = "private" },
            },
            ["PhoneNumbers"] = new BsonArray { new BsonDocument { ["_id"] = U(721), ["Number"] = "+49 821 555 0142", ["Type"] = "mobile" } },
            // None of the four is a member of the model.
            ["Items"] = new BsonArray
            {
                new BsonDocument { ["_t"] = "ContractItem", ["Name"] = "Arbeitsvertrag", ["ValidFrom"] = D("2019-02-28T23:00:00Z"), ["Salary"] = M("4200.00") },
                new BsonDocument { ["_t"] = "BenefitItem", ["Name"] = "Jobticket", ["ValidFrom"] = D("2021-12-31T23:00:00Z") },
            },
            ["Bank"] = new BsonArray { new BsonDocument { ["Iban"] = "DE02120300000000202051", ["Bic"] = "BYLADEM1001", ["AccountHolder"] = "Erika Mustermann" } },
            ["TaxClass"] = 1,
            ["MaritalStatus"] = "married",
            ["WageGroup"] = new BsonDocument { ["Name"] = "E9", ["Level"] = 3 },
            ["CreateDateTime"] = D("2019-02-01T08:00:00Z"),
        },
        new BsonDocument
        {
            ["_id"] = U(702), ["OrganizationId"] = U(Organisation), ["IsDeleted"] = false, ["MatchCode"] = "WEBER, TOM",
            ["Address"] = new BsonDocument { ["FirstName"] = "Tom", ["LastName"] = "Weber" }, ["EmailAddresses"] = new BsonArray(), ["PhoneNumbers"] = new BsonArray(),
        },
        new BsonDocument { ["_id"] = U(709), ["OrganizationId"] = U(Foreign), ["IsDeleted"] = false, ["MatchCode"] = "FOREIGN, EMPLOYEE", ["TaxClass"] = 6 },
    ];

    private static List<BsonDocument> Holidays() =>
    [
        new BsonDocument { ["_id"] = U(801), ["OrganizationId"] = U(Organisation), ["Employee"] = new BsonDocument { ["_id"] = U(EmployeeId), ["Name"] = "Erika Mustermann" }, ["Year"] = 2026, ["Entitlement"] = 30, ["Taken"] = M("12.5") },
        new BsonDocument { ["_id"] = U(802), ["OrganizationId"] = U(Organisation), ["Employee"] = new BsonDocument { ["_id"] = U(EmployeeId), ["Name"] = "Erika Mustermann" }, ["Year"] = 2025, ["Entitlement"] = 30, ["Taken"] = M("30") },
        new BsonDocument { ["_id"] = U(803), ["OrganizationId"] = U(Organisation), ["Employee"] = new BsonDocument { ["_id"] = U(702), ["Name"] = "Tom Weber" }, ["Year"] = 2026, ["Entitlement"] = 28, ["Taken"] = M("0") },
        // The same employee's key under another organisation, and under none.
        new BsonDocument { ["_id"] = U(809), ["OrganizationId"] = U(Foreign), ["Employee"] = new BsonDocument { ["_id"] = U(EmployeeId) }, ["Year"] = 2026, ["Entitlement"] = 99 },
        new BsonDocument { ["_id"] = U(808), ["Employee"] = new BsonDocument { ["_id"] = U(EmployeeId) }, ["Year"] = 2024, ["Entitlement"] = 98 },
    ];

    /// <summary>A collection beside the entities whose documents carry no organisation at all: nothing of it is anybody's to read.</summary>
    private static List<BsonDocument> Settings() =>
    [
        new BsonDocument { ["_id"] = U(2001), ["IsDeleted"] = false, ["Name"] = "smtp", ["Secret"] = "NOBODYS-SETTING" },
        new BsonDocument { ["_id"] = U(2002), ["IsDeleted"] = false, ["OrganizationId"] = BsonNull.Value, ["Name"] = "licence", ["Secret"] = "NULL-ORGANISATION-SETTING" },
    ];

    private static BsonDocument Resource(string type, int n, string name) => new()
    {
        ["_t"] = new BsonArray { "Resource", type }, ["_id"] = U(n), ["MatchCode"] = name.ToUpperInvariant(), ["DisplayName"] = name,
    };

    private static BsonDocument Tour(int n, Guid organisation, string number, Guid global, bool mirrored, bool deleted, string end, BsonArray? actions = null) => new()
    {
        ["_id"] = U(n),
        ["OrganizationId"] = U(organisation),
        ["IsDeleted"] = deleted,
        ["Number"] = number,
        ["Reference"] = "REF-" + number,
        ["GlobalTourId"] = U(global),
        ["GeneratedTourNrCounter"] = n,
        ["IsMirroredTour"] = mirrored,
        ["Status"] = new BsonDocument { ["_id"] = U(1300), ["Name"] = mirrored ? "mirrored" : "planned", ["Color"] = "#3366ff" },
        ["Resource"] = Resource("TractorUnitResource", 1200 + n, "M-AN 4" + n),
        ["StartDateTime"] = D("2026-06-01T04:00:00Z"),
        ["EndDateTime"] = D(end),
        ["ActualStartDateTime"] = BsonNull.Value,
        ["StartAddress"] = Address(1400 + n, "Depot Nord", "Hamburg"),
        ["EndAddress"] = Address(1500 + n, "Depot Süd", "München"),
        ["Notes"] = mirrored ? BsonNull.Value : "Rampe 4",
        ["Actions"] = actions ?? [],
        ["AttachedResources"] = new BsonArray
        {
            new BsonDocument
            {
                ["Resource"] = Resource("TrailerResource", 1600 + n, "M-TR 9" + n),
                ["AttachAction"] = new BsonDocument { ["GlobalActionId"] = U(1700 + n), ["DateTime"] = D("2026-06-01T04:10:00Z"), ["ActualDateTime"] = BsonNull.Value },
                ["DetachAction"] = BsonNull.Value,
            },
        },
        ["AttachedEntities"] = new BsonArray
        {
            new BsonDocument { ["Entitiy"] = new BsonDocument { ["_t"] = "Shipment", ["_id"] = U(1800 + n), ["ShipmentNumber"] = "S-20" + n }, ["AttachingAction"] = new BsonDocument { ["GlobalActionId"] = U(1700 + n) } },
        },
        ["Transits"] = new BsonArray
        {
            new BsonDocument { ["StartDateTime"] = D("2026-06-01T04:30:00Z"), ["EndDateTime"] = D("2026-06-01T12:00:00Z"), ["Distance"] = 612.4, ["DrivingTime"] = 27000L, ["StartAddress"] = new BsonDocument { ["City"] = "Hamburg" }, ["EndAddress"] = new BsonDocument { ["City"] = "München" } },
        },
    };

    private static List<BsonDocument> Tours()
    {
        var actions = new BsonArray
        {
            new BsonDocument
            {
                ["_t"] = new BsonArray { "TourActionBase", "AttachShipmentAction" }, ["_id"] = U(1901), ["GlobalActionId"] = U(1701), ["OrderId"] = 1, ["OutOfTour"] = false,
                ["DateTime"] = D("2026-06-01T06:00:00Z"), ["CalculatedDateTime"] = D("2026-06-01T06:05:00Z"), ["ActualDateTime"] = BsonNull.Value, ["Notes"] = "Avis 30 min",
                ["Address"] = Address(1911, "Nordhafen Logistik GmbH", "Hamburg"),
                ["Resource"] = Resource("DriverResource", 1921, "Erika Mustermann"),
                ["Entity"] = new BsonDocument
                {
                    ["_t"] = "Shipment", ["_id"] = U(1931), ["ShipmentNumber"] = "S-1001", ["Number"] = "1001", ["LoadNumber"] = "L-77",
                    ["Status"] = new BsonDocument { ["Name"] = "loaded" },
                    ["DeliveryAddress"] = Address(1941, "Südlager München KG", "München"),
                    ["Items"] = new BsonArray
                    {
                        new BsonDocument { ["Text"] = "Paletten", ["Quantity"] = new BsonDocument { ["Value"] = M("12") }, ["Weight"] = new BsonDocument { ["Value"] = M("1250.5") }, ["Internal"] = "not projected" },
                    },
                },
            },
            new BsonDocument
            {
                ["_t"] = new BsonArray { "TourActionBase", "CheckVehicleAction" }, ["_id"] = U(1902), ["GlobalActionId"] = U(1702), ["OrderId"] = 2, ["OutOfTour"] = true,
                ["DateTime"] = D("2026-06-01T05:00:00Z"), ["MirroredTourNumber"] = "TR-M-1",
            },
        };

        return
        [
            Tour(1, Organisation, "TR-2026-0101", GlobalTourId, mirrored: false, deleted: false, "2026-06-01T14:00:00Z", actions),
            Tour(2, Organisation, "TR-M-1", GlobalTourId, mirrored: true, deleted: false, "2026-06-01T15:00:00Z"),
            Tour(3, Organisation, "TR-M-2", GlobalTourId, mirrored: true, deleted: true, "2026-06-01T16:00:00Z"),
            Tour(4, Organisation, "TR-2026-0102", Id(1101), mirrored: false, deleted: false, "2026-06-08T23:59:59Z"),
            // After the report's date: not a row.
            Tour(5, Organisation, "TR-2026-0199", Id(1102), mirrored: false, deleted: false, "2026-06-20T10:00:00Z"),
            // The same global tour under another organisation: neither a row nor a sub-tour.
            Tour(6, Foreign, "FOREIGN-M-1", GlobalTourId, mirrored: true, deleted: false, "2026-06-01T15:00:00Z"),
        ];
    }
}
