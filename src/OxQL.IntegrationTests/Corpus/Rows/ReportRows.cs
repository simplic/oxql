using static OxQL.IntegrationTests.Fixtures.Rows.RowKit;
using D = OxQL.IntegrationTests.Fleet.Models.Directory;
using F = OxQL.IntegrationTests.Fleet.Models.Fleet;
using L = OxQL.IntegrationTests.Fleet.Models.Ledger;
using S = OxQL.IntegrationTests.Fleet.Models.Staff;
using T = OxQL.IntegrationTests.Fleet.Models.Transport;

namespace OxQL.IntegrationTests.Fixtures.Rows;

/// <summary>
/// The rows of the report scenarios (DESIGN §2, A1–A5), all in organisation R: one invoice mixing
/// shipment and tour lines, the ERP billing lines those lines came from, the shipments and tours
/// holding the source lines, delivery attempts, the resources the tours plan with, the vehicles
/// and employees those resources are, the clerk, and the recipient contact; plus one invoice per
/// failure the scenarios name. Ids come from <see cref="ReportSeed"/>.
/// </summary>
internal static class ReportRows
{
    private static readonly Org R = Org.R;

    private static readonly DateTime At = Dt("2026-04-01T08:00:00Z");

    // ── directory ─────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<CorpusRow> Contacts(Org org) => org == R ?
    [
        Row(ReportSeed.Contact, "recipient", "the invoice recipient: e-mail, phone, company name and a GeoJSON location", ReportSeed.RecipientContactId, new D.Contact
        {
            Address = new D.ContactAddress
            {
                CompanyName = "Nordhafen Logistik GmbH", Street = "Am Kai", HouseNumber = "7", Zipcode = "20457", City = "Hamburg",
                CountryIso = "DE", Country = "Germany", Latitude = 53.5438, Longitude = 9.9661, MatchCode = "NORDHAFEN",
                Location = MongoDB.Driver.GeoJsonObjectModel.GeoJson.Point(MongoDB.Driver.GeoJsonObjectModel.GeoJson.Geographic(9.9661, 53.5438)),
            },
            MatchCode = "NORDHAFEN",
            PrimaryEmailAddress = new D.EmailAddress { Email = "rechnung@nordhafen.example", Type = "invoice" },
            PrimaryPhoneNumber = new D.PhoneNumber { Number = "+49 40 555 0100", Type = "office" },
            EmailAddresses = [new D.EmailAddress { Email = "rechnung@nordhafen.example", Type = "invoice" }, new D.EmailAddress { Email = "dispo@nordhafen.example", Type = "dispatch" }],
            PhoneNumbers = [new D.PhoneNumber { Number = "+49 40 555 0100", Type = "office" }],
            Functions = ["customer"],
            CreateDateTime = At, UpdateDateTime = At, CreateUserId = LabUser, CreateUserName = "lab", UpdateUserName = "lab",
        }),
        Row(ReportSeed.Contact, "no-email", "a contact without a primary e-mail address", ReportSeed.ContactWithoutEmailId, new D.Contact
        {
            Address = new D.ContactAddress { CompanyName = "Elbe Spedition KG", City = "Bremen", CountryIso = "DE" },
            MatchCode = "ELBE",
            PrimaryEmailAddress = null,
            PrimaryPhoneNumber = new D.PhoneNumber { Number = "+49 421 777", Type = "office" },
            CreateDateTime = At, UpdateDateTime = At, CreateUserName = "lab", UpdateUserName = "lab",
        }),
    ] : [];

    // ── staff ─────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<CorpusRow> Employees(Org org) => org == R ?
    [
        Row(ReportSeed.Employee, "clerk", "the clerk: the employee whose userId the mixed invoice's createUserId is", ReportSeed.ClerkEmployeeId,
            Employee("Anna", "Becker", "anna.becker@lab.example", ReportSeed.ClerkUserId)),
        Row(ReportSeed.Employee, "clerk-dup-a", "one of two employees sharing one userId", ReportSeed.DuplicateUserEmployeeIds[0],
            Employee("Paul", "Krause", "paul.krause@lab.example", ReportSeed.DuplicateUserId)),
        Row(ReportSeed.Employee, "clerk-dup-b", "the other employee sharing that userId", ReportSeed.DuplicateUserEmployeeIds[1],
            Employee("Pia", "Krause", "pia.krause@lab.example", ReportSeed.DuplicateUserId)),
        Row(ReportSeed.Employee, "driver", "the driver: the employee the driver resource is", ReportSeed.DriverEmployeeId,
            Employee("Jonas", "Weber", "jonas.weber@lab.example", userId: null)),
    ] : [];

    private static S.Employee Employee(string first, string last, string email, Guid? userId) => new()
    {
        Address = new S.EmployeeAddress { FirstName = first, LastName = last, City = "Hamburg", CountryIso = "DE" },
        Employment = new S.Employment { Number = $"R-{last}", IsActive = true, EntryDate = Dt("2021-01-01T00:00:00Z") },
        PrimaryEmailAddress = new S.EmailAddress { Email = email, Type = "work" },
        EmailAddresses = [new S.EmailAddress { Email = email, Type = "work" }],
        MatchCode = $"{first}.{last}".ToUpperInvariant(),
        UserId = userId,
        CreateDateTime = At, UpdateDateTime = At, CreateUserId = LabUser, UpdateUserId = LabUser, CreateUserName = "lab", UpdateUserName = "lab",
    };

    // ── fleet ─────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<CorpusRow> Vehicles(Org org) => org == R ?
    [
        Row(ReportSeed.Vehicle, "tractor", "the tractor unit the tractor resource is", ReportSeed.TractorVehicleId, Vehicle("HH-NH 1204", "ZM-01")),
        Row(ReportSeed.Vehicle, "trailer", "the trailer the trailer resource is", ReportSeed.TrailerVehicleId, Vehicle("HH-NH 5570", "AH-07")),
    ] : [];

    private static F.Vehicle Vehicle(string plate, string matchCode) => new()
    {
        MatchCode = matchCode,
        RegistrationPlate = new F.RegistrationPlate { CountryIso = "DE", RegistrationIdentifier = plate },
        Location = "Hamburg",
        CreateDateTime = At, UpdateDateTime = At, CreateUserId = LabUser, UpdateUserId = LabUser, CreateUserName = "lab", UpdateUserName = "lab",
    };

    // ── transport ─────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<CorpusRow> Resources(Org org) => org == R ?
    [
        Resource("driver", "DriverResource: its id is the driver employee", new T.DriverResource { Id = ReportSeed.DriverEmployeeId, DisplayName = "Jonas Weber", MatchCode = "WEBER" }),
        Resource("tractor", "TractorUnitResource: its id is the tractor vehicle", ReportSeed.TractorResource()),
        Resource("trailer", "TrailerResource: its id is the trailer vehicle; carries loading slots", ReportSeed.TrailerResource()),
        Resource("car", "CarResource: a vehicle id the fleet does not hold", new T.CarResource { Id = ReportSeed.CarResourceId, DisplayName = "Pool car", MatchCode = "PKW-3" }),
        Resource("container", "ContainerResource: a vehicle id the fleet does not hold; carries loading slots", new T.ContainerResource
        {
            Id = ReportSeed.ContainerResourceId, DisplayName = "Container 40ft", MatchCode = "CT-40", IsLoadable = true,
            LoadingSlots = [new T.ResourceLoadingSlot { Id = ReportSeed.Id(Spaces.Resource, 104), Name = "front" }],
        }),
        Resource("carrier", "CarrierResource: names neither an employee nor a vehicle", ReportSeed.CarrierResource()),
        Resource("equipment", "EquipmentResource: a vehicle id the fleet does not hold", new T.EquipmentResource { Id = ReportSeed.EquipmentResourceId, DisplayName = "Tail lift", MatchCode = "HB-1" }),
    ] : [];

    private static CorpusRow Resource(string key, string purpose, T.Resource resource)
    {
        resource.CreateDateTime = At;
        resource.UpdateDateTime = At;
        resource.CreateUserName = "lab";

        return Row<T.Resource>(ReportSeed.Resource, key, purpose, resource.Id, resource);
    }

    public static IReadOnlyList<CorpusRow> Shipments(Org org) => org == R ?
    [
        Row(ReportSeed.Shipment, "shipment", "the scenario shipment: numbers, addresses, the delivery end, weight notes, two billing lines, its tour", ReportSeed.ShipmentId, new T.Shipment
        {
            ShipmentNumber = "SN-2026-0001",
            ReferenceNumber = "KD-4711",
            DeliveryNoteNumber = "LS-2026-0001",
            LoadAddress = Address("Hamburg Hafen GmbH", "Hamburg", "20457"),
            DeliveryAddress = Address("Weser Handel AG", "Bremen", "28195"),
            EffectiveDeliveryEnd = Dt("2026-04-08T14:30:00Z"),
            OrderDate = Dt("2026-04-06T09:00:00Z"),
            Items =
            [
                new T.ShipmentItem
                {
                    Id = ReportSeed.Id(Spaces.ShipmentItem, 1), Text = "Steel coils", OrderNumber = 1,
                    WeightNotes =
                    [
                        WeightNote(1, "WN-2026-0001", 24.5),
                        WeightNote(2, "WN-2026-0002", 12.25),
                    ],
                },
            ],
            BillingLines =
            [
                ShipmentLine(ReportSeed.ShipmentLineIds[0], "Freight Hamburg - Bremen", 950m, 10, "h", "shipment", ReportSeed.ShipmentId),
                ShipmentLine(ReportSeed.ShipmentLineIds[1], "Waiting time", 120m, 2, "h", "shipment", ReportSeed.ShipmentId),
            ],
            Tours = [new T.ShipmentTour { Id = ReportSeed.Id(Spaces.ShipmentTourEntry, 1), Number = "TR-2026-0101", TourId = ReportSeed.TractorTourId, Resource = ReportSeed.TractorResource() }],
            CreateDateTime = At, UpdateDateTime = At, CreateUserId = LabUser,
        }),
        Row(ReportSeed.Shipment, "shipment-no-attempts", "a shipment with no delivery attempt, on the carrier tour", ReportSeed.ShipmentWithoutAttemptsId, new T.Shipment
        {
            ShipmentNumber = "SN-2026-0002",
            ReferenceNumber = "KD-4712",
            LoadAddress = Address("Hamburg Hafen GmbH", "Hamburg", "20457"),
            DeliveryAddress = Address("Ems Bau GmbH", "Emden", "26721"),
            BillingLines = [ShipmentLine(ReportSeed.ShipmentLineIds[2], "Freight Hamburg - Emden", 640m, 1, "pc", "shipment", ReportSeed.ShipmentWithoutAttemptsId)],
            Tours = [new T.ShipmentTour { Id = ReportSeed.Id(Spaces.ShipmentTourEntry, 2), Number = "TR-2026-0102", TourId = ReportSeed.CarrierTourId, Resource = ReportSeed.CarrierResource() }],
            CreateDateTime = At, UpdateDateTime = At, CreateUserId = LabUser,
        }),
        Row(ReportSeed.Shipment, "shipment-dup-line", "holds a billing line whose id the carrier tour also holds", ReportSeed.ShipmentWithDuplicateLineId, new T.Shipment
        {
            ShipmentNumber = "SN-2026-0003",
            ReferenceNumber = "KD-4713",
            BillingLines = [ShipmentLine(ReportSeed.DuplicateLineId, "Duplicate line (shipment copy)", 80m, 1, "pc", "shipment", ReportSeed.ShipmentWithDuplicateLineId)],
            CreateDateTime = At, UpdateDateTime = At, CreateUserId = LabUser,
        }),
    ] : [];

    public static IReadOnlyList<CorpusRow> Tours(Org org) => org == R ?
    [
        Row(ReportSeed.Tour, "tour-tractor", "the tour of the scenario shipment: a tractor, a driver and a trailer attached, one action of every variant, a tour billing line", ReportSeed.TractorTourId, new T.Tour
        {
            Number = "TR-2026-0101",
            Reference = "Hamburg - Bremen",
            Resource = ReportSeed.TractorResource(),
            StartAddress = Address("Depot Nord", "Hamburg", "21129"),
            EndAddress = Address("Depot Nord", "Hamburg", "21129"),
            StartDateTime = Dt("2026-04-08T06:00:00Z"),
            EndDateTime = Dt("2026-04-08T16:00:00Z"),
            ActualStartDateTime = Dt("2026-04-08T06:10:00Z"),
            ActualEndDateTime = Dt("2026-04-08T15:40:00Z"),
            AttachedResources =
            [
                new T.AttachedResource
                {
                    Id = ReportSeed.Id(Spaces.AttachedResource, 1),
                    Resource = new T.DriverResource { Id = ReportSeed.DriverEmployeeId, DisplayName = "Jonas Weber", MatchCode = "WEBER" },
                    AttachAction = new T.AttachResourceAction { Id = ReportSeed.Id(Spaces.TourAction, 1), OrderId = 1, DateTime = Dt("2026-04-08T06:00:00Z") },
                    DetachAction = new T.DetachResourceAction { Id = ReportSeed.Id(Spaces.TourAction, 7), OrderId = 7, DateTime = Dt("2026-04-08T16:00:00Z") },
                },
                new T.AttachedResource { Id = ReportSeed.Id(Spaces.AttachedResource, 2), Resource = ReportSeed.TrailerResource() },
            ],
            Actions =
            [
                new T.AttachResourceAction { Id = ReportSeed.Id(Spaces.TourAction, 1), OrderId = 1, DateTime = Dt("2026-04-08T06:00:00Z"), Address = Address("Depot Nord", "Hamburg", "21129") },
                new T.AttachShipmentAction { Id = ReportSeed.Id(Spaces.TourAction, 2), OrderId = 2, DateTime = Dt("2026-04-08T07:00:00Z"), ShipmentId = ReportSeed.ShipmentId, Address = Address("Hamburg Hafen GmbH", "Hamburg", "20457") },
                new T.CheckVehicleAction { Id = ReportSeed.Id(Spaces.TourAction, 3), OrderId = 3, DateTime = Dt("2026-04-08T07:30:00Z") },
                new T.DetachShipmentAction { Id = ReportSeed.Id(Spaces.TourAction, 4), OrderId = 4, DateTime = Dt("2026-04-08T14:30:00Z"), ShipmentId = ReportSeed.ShipmentId, Address = Address("Weser Handel AG", "Bremen", "28195") },
                new T.TaskAction { Id = ReportSeed.Id(Spaces.TourAction, 5), OrderId = 5, Title = "Return pallets", StartDateTime = Dt("2026-04-08T14:40:00Z"), EndDateTime = Dt("2026-04-08T14:50:00Z") },
                new T.CleaningAction { Id = ReportSeed.Id(Spaces.TourAction, 6), OrderId = 6, DateTime = Dt("2026-04-08T15:30:00Z"), Notes = "wash" },
                new T.DetachResourceAction { Id = ReportSeed.Id(Spaces.TourAction, 7), OrderId = 7, DateTime = Dt("2026-04-08T16:00:00Z") },
            ],
            BillingLines = [ShipmentLine(ReportSeed.TourLineId, "Tour flat rate", 480m, 1, "pc", "tour", ReportSeed.TractorTourId)],
            CreateDateTime = At, UpdateDateTime = At,
        }),
        Row(ReportSeed.Tour, "tour-carrier", "a tour driven by a carrier resource; holds the billing line id the third shipment also holds", ReportSeed.CarrierTourId, new T.Tour
        {
            Number = "TR-2026-0102",
            Resource = ReportSeed.CarrierResource(),
            StartDateTime = Dt("2026-04-09T06:00:00Z"),
            EndDateTime = Dt("2026-04-09T18:00:00Z"),
            BillingLines = [ShipmentLine(ReportSeed.DuplicateLineId, "Duplicate line (tour copy)", 80m, 1, "pc", "tour", ReportSeed.CarrierTourId)],
            CreateDateTime = At, UpdateDateTime = At,
        }),
    ] : [];

    public static IReadOnlyList<CorpusRow> DeliveryAttempts(Org org) => org == R ?
    [
        Attempt(1, "attempt-first", "the first attempt on the scenario shipment: failed", ReportSeed.ShipmentId, "2026-04-07T16:00:00Z", "failed", "Recipient absent"),
        Attempt(2, "attempt-latest", "the latest attempt on the scenario shipment: delivered", ReportSeed.ShipmentId, "2026-04-08T14:30:00Z", "delivered", "Delivered to gate 3"),
        Attempt(3, "attempt-middle", "an attempt between the two, stored last", ReportSeed.ShipmentId, "2026-04-08T10:00:00Z", "failed", "Gate closed"),
        Attempt(4, "attempt-dup-shipment", "the one attempt on the third shipment", ReportSeed.ShipmentWithDuplicateLineId, "2026-04-09T11:00:00Z", "delivered", "Delivered"),
    ] : [];

    private static CorpusRow Attempt(int n, string key, string purpose, Guid shipmentId, string at, string status, string text) =>
        Row(ReportSeed.DeliveryAttempt, key, purpose, ReportSeed.Id(Spaces.DeliveryAttempt, n), new T.DeliveryAttempt
        {
            ShipmentId = shipmentId,
            DateTime = Dt(at),
            Text = text,
            Status = new T.DeliveryAttemptStatus { Id = ReportSeed.Id(Spaces.DeliveryAttemptStatus, status == "delivered" ? 1 : 2), DisplayName = status, DisplayKey = status },
        });

    private static T.ContactAddress Address(string company, string city, string zipcode) =>
        new() { CompanyName = company, City = city, Zipcode = zipcode, CountryIso = "DE", Country = "Germany" };

    private static T.WeightNote WeightNote(int n, string number, double tonnes) => new()
    {
        Id = ReportSeed.Id(Spaces.WeightNote, n),
        Number = number,
        Weight = tonnes,
        Quantity = new T.Quantity { Value = tonnes, QuantityUnit = new T.QuantityUnit { Guid = ReportSeed.Id(Spaces.QuantityUnit, 1), Name = "tonne", ShortName = "t", Digits = 3 } },
    };

    /// <summary>A logistics billing line with its self-reference to the shipment or tour holding it.</summary>
    private static T.BillingLine ShipmentLine(Guid id, string text, decimal total, double quantity, string unit, string dataType, Guid parent) => new()
    {
        Id = id,
        Type = T.BillingLineType.Customer,
        Status = "released",
        Text = text,
        SinglePrice = (double)total / quantity,
        TotalPrice = (double)total,
        Date = Dt("2026-04-08T00:00:00Z"),
        Quantity = new T.Quantity { Value = quantity, QuantityUnit = new T.QuantityUnit { Guid = ReportSeed.Id(Spaces.QuantityUnit, 2), Name = unit, ShortName = unit, Digits = 2 } },
        References = [new T.BillingLineReference { Id = ReportSeed.Id(Spaces.BillingLineReference, OrdinalOf(id)), DataType = dataType, ReferenceId = parent.ToString("D") }],
    };

    private static long OrdinalOf(Guid id) => Ids.OrdinalOf(id);

    // ── ledger ────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<CorpusRow> BillingLines(Org org) => org == R ?
    [
        ErpLine(1, "erp-line-shipment-freight", "copied from the shipment's first billing line", ReportSeed.ShipmentLineIds[0], "Freight Hamburg - Bremen", 950m, [ShipmentRef(1, ReportSeed.ShipmentId)]),
        ErpLine(2, "erp-line-shipment-waiting", "copied from the shipment's second billing line", ReportSeed.ShipmentLineIds[1], "Waiting time", 120m, [ShipmentRef(2, ReportSeed.ShipmentId)]),
        ErpLine(3, "erp-line-tour", "copied from the tour's billing line", ReportSeed.TourLineId, "Tour flat rate", 480m, [TourRef(3, ReportSeed.TractorTourId), TariffRef(4)]),
        ErpLine(4, "erp-line-deleted-source", "its source line was deleted: the id is in no shipment and no tour", ReportSeed.DeletedSourceLineId, "Deleted source", 50m, [ShipmentRef(5, ReportSeed.ShipmentId)]),
        ErpLine(5, "erp-line-duplicate-source", "its source line id is held by a shipment and a tour", ReportSeed.DuplicateLineId, "Duplicate source", 80m, []),
        ErpLine(6, "erp-line-tariff-only", "copied from the second shipment's line; its references hold only a tariff", ReportSeed.ShipmentLineIds[2], "Freight Hamburg - Emden", 640m, [TariffRef(6)]),
    ] : [];

    private static CorpusRow ErpLine(int n, string key, string purpose, Guid sourceLineId, string text, decimal total, List<L.BillingLineReference> references) =>
        Row(ReportSeed.BillingLine, key, purpose, ReportSeed.ErpLineIds[n - 1], new L.BillingLine
        {
            Date = Dt("2026-04-08T00:00:00Z"),
            Text = text,
            SourceText = text,
            Quantity = new L.Quantity { Value = 1m, Unit = Unit("pc") },
            SinglePrice = total,
            TotalPrice = total,
            References = references,
            State = L.BillingLineState.Invoiced,
            SourceBillingLineReference = new L.SourceBillingLineReference { Type = "logistics", Id = sourceLineId },
            CreateDateTime = At, UpdateDateTime = At, CreateUserId = ReportSeed.ClerkUserId, CreateUserName = "anna.becker",
        });

    private static L.BillingLineReference ShipmentRef(int n, Guid shipmentId) => new() { Id = ReportSeed.Id(Spaces.LedgerLineReference, n), DataType = "shipment", ReferenceId = shipmentId.ToString("D") };

    /// <summary>A tour self-reference, spelled upper-case: the key conversion accepts any guid format.</summary>
    private static L.BillingLineReference TourRef(int n, Guid tourId) => new() { Id = ReportSeed.Id(Spaces.LedgerLineReference, n), DataType = "tour", ReferenceId = tourId.ToString("D").ToUpperInvariant() };

    private static L.BillingLineReference TariffRef(int n) => new() { Id = ReportSeed.Id(Spaces.LedgerLineReference, n), DataType = "Tariff", ReferenceId = "TARIFF-2026-STD" };

    private static L.QuantityUnit Unit(string name) => new() { Id = ReportSeed.Id(Spaces.QuantityUnit, 3), Name = name, ShortName = name };

    public static IReadOnlyList<CorpusRow> Transactions(Org org) => org == R ?
    [
        Row(ReportSeed.Transaction, "mixed-invoice", "the acceptance invoice: every item variant, groups two deep, shipment and tour lines, a tariff-only line", ReportSeed.TransactionId,
            Invoice("RE-2026-0001", ReportSeed.ClerkUserId,
            [
                new L.TextTransactionItem { Id = ItemId(1), SortNumber = 1, DeserializationType = "text", Text = "We invoice the following services:" },
                LineItem(2, 0, [ShipmentRef(11, ReportSeed.ShipmentId)]),
                new L.GroupTransactionItem
                {
                    Id = ItemId(3), SortNumber = 3, DeserializationType = "group", Text = "Tour TR-2026-0101",
                    Items =
                    [
                        LineItem(4, 2, [TariffRef(12), TourRef(13, ReportSeed.TractorTourId)]),
                        new L.GroupTransactionItem
                        {
                            Id = ItemId(5), SortNumber = 5, DeserializationType = "group", Text = "Extras",
                            Items = [LineItem(6, 1, [ShipmentRef(14, ReportSeed.ShipmentId)])],
                        },
                    ],
                },
                LineItem(7, 5, [TariffRef(15)]),
                new L.ArticleTransactionItem { Id = ItemId(8), SortNumber = 8, DeserializationType = "article", Text = "Pallet exchange", ArticleId = ReportSeed.Id(Spaces.TransactionItemType, 90), Quantity = new L.Quantity { Value = 4m, Unit = Unit("pc") }, SinglePrice = 12.5m, TotalPrice = 50m, TotalPriceNet = 50m, TotalPriceGross = 59.5m },
                new L.GeneralLedgerAccountTransactionItem { Id = ItemId(9), SortNumber = 9, DeserializationType = "general_ledger_account", Text = "Toll", GeneralLedgerAccount = new L.GeneralLedgerAccount { Id = ReportSeed.Id(Spaces.TransactionItemType, 91), Number = "4730", Name = "Toll" }, Quantity = new L.Quantity { Value = 1m }, TotalPrice = 38.4m, TotalPriceNet = 38.4m },
                new L.BasicDiscountSurchargeOperationItem { Id = ItemId(10), SortNumber = 10, DeserializationType = "basic_discount_surcharge_operation", Text = "Fuel surcharge 5 %", ValueOperator = L.ValueOperator.Percentage, Amount = 5m, DeltaValue = 47.5m },
                new L.CashDiscountOperationItem { Id = ItemId(11), SortNumber = 11, DeserializationType = "cash_discount", Text = "2 % cash discount", ValueOperator = L.ValueOperator.Percentage, Amount = 2m, DeltaValue = -19m },
            ])),
        Row(ReportSeed.Transaction, "missing-source", "one line whose ERP billing line points at a deleted source line", ReportSeed.MissingSourceTransactionId,
            Invoice("RE-2026-0002", ReportSeed.ClerkUserId, [LineItem(21, 3, [ShipmentRef(21, ReportSeed.ShipmentId)])])),
        Row(ReportSeed.Transaction, "ambiguous-source", "one line whose source line id is in a shipment and a tour", ReportSeed.AmbiguousSourceTransactionId,
            Invoice("RE-2026-0003", ReportSeed.ClerkUserId, [LineItem(31, 4, [])])),
        Row(ReportSeed.Transaction, "clerk-ambiguous", "created by a user id two employees share", ReportSeed.AmbiguousClerkTransactionId,
            Invoice("RE-2026-0004", ReportSeed.DuplicateUserId, [LineItem(41, 0, [ShipmentRef(41, ReportSeed.ShipmentId)])])),
        Row(ReportSeed.Transaction, "deep-nesting", "a billing line item seven groups deep, below the flatten depth of five, and one at the top", ReportSeed.DeepNestingTransactionId,
            Invoice("RE-2026-0005", ReportSeed.ClerkUserId, [LineItem(51, 0, [ShipmentRef(51, ReportSeed.ShipmentId)]), Nested(52, 7, LineItem(59, 1, [ShipmentRef(59, ReportSeed.ShipmentId)]))])),
        Row(ReportSeed.Transaction, "clerk-missing", "created by a user id no employee has", ReportSeed.MissingClerkTransactionId,
            Invoice("RE-2026-0006", ReportSeed.UnknownUserId, [LineItem(61, 0, [ShipmentRef(61, ReportSeed.ShipmentId)])])),
    ] : [];

    private static Guid ItemId(int n) => ReportSeed.Id(Spaces.TransactionItem, n);

    /// <summary>A billing-line item for ERP line <paramref name="erpLine"/> (0-based into <see cref="ReportSeed.ErpLineIds"/>).</summary>
    private static L.BillingLineTransactionItem LineItem(int n, int erpLine, List<L.BillingLineReference> references)
    {
        var total = new[] { 950m, 120m, 480m, 50m, 80m, 640m }[erpLine];

        return new L.BillingLineTransactionItem
        {
            Id = ItemId(n),
            SortNumber = n,
            DeserializationType = "billing_line",
            Text = $"Billing line {n}",
            BillingLineId = ReportSeed.ErpLineIds[erpLine],
            Quantity = new L.Quantity { Value = 1m, Unit = Unit("pc") },
            SinglePrice = total, TotalPrice = total, SinglePriceNet = total, TotalPriceNet = total,
            SinglePriceGross = total * 1.19m, TotalPriceGross = total * 1.19m, SinglePriceVat = total * 0.19m, TotalPriceVat = total * 0.19m,
            TaxRate = new L.TransactionTaxRate { Id = ReportSeed.Id(Spaces.TransactionItemType, 80), Name = "19 %", Rate = 19m },
            References = references,
        };
    }

    /// <summary><paramref name="depth"/> groups, each holding the next; the innermost holds <paramref name="leaf"/>.</summary>
    private static L.GroupTransactionItem Nested(int n, int depth, L.TransactionItem leaf)
    {
        L.TransactionItem inner = leaf;

        for (var level = depth; level >= 1; level--)
            inner = new L.GroupTransactionItem { Id = ItemId(n * 10 + level), SortNumber = level, DeserializationType = "group", Text = $"Level {level}", Items = [inner] };

        return (L.GroupTransactionItem)inner;
    }

    private static L.Transaction Invoice(string number, Guid createUserId, List<L.TransactionItem> items)
    {
        var net = 1000.00m;

        return new L.Transaction
        {
            Number = number,
            Reference = "KD-4711",
            Type = new L.TransactionType { Id = ReportSeed.Id(Spaces.TransactionType, 1), Name = "Invoice", Number = 10, ReportName = "invoice", ShortName = "INV" },
            Date = Dt("2026-04-10T00:00:00Z"),
            DeliveryDate = Dt("2026-04-08T00:00:00Z"),
            DueDate = Dt("2026-04-24T00:00:00Z"),
            TermsOfPayment = new L.TermsOfPayment
            {
                Id = ReportSeed.Id(Spaces.TermsOfPayment, 1), Number = 14, Name = "14 days net", PaymentDeadlineDays = 14,
                CashDiscount = 2m, CashDiscountDays = 7, FormattedText = "Payable within 14 days without deduction; 2 % cash discount within 7 days.",
            },
            InvoiceRecipient = new L.TransactionContact
            {
                Id = ReportSeed.Id(Spaces.TransactionContact, 1), AccountNumber = "10001", CompanyName = "Nordhafen Logistik GmbH", VatId = "DE123456789",
                Address = new L.TransactionAddress
                {
                    Id = ReportSeed.RecipientContactId, Number = "10001", Name = "Nordhafen Logistik GmbH",
                    Street = "Am Kai 7", Zipcode = "20457", City = "Hamburg", Country = "Germany", CountryIso = "DE",
                },
            },
            Items = items,
            States = ["posted"],
            TotalPrice = net,
            TotalPriceNet = net,
            TotalPriceGross = net * 1.19m,
            TotalPriceTax = net * 0.19m,
            SignedTotalPrice = net,
            SignedTotalPriceNet = net,
            SignedTotalPriceGross = net * 1.19m,
            SignedTotalPriceTax = net * 0.19m,
            Sign = 1,
            TransactionYear = 2026,
            TaxKeyTotalPrices = [new L.TaxKeyTotalPrice { TaxKey = "19", TotalPrice = net }],
            CreateDateTime = Dt("2026-04-10T09:15:00Z"),
            UpdateDateTime = Dt("2026-04-10T09:15:00Z"),
            CreateUserId = createUserId,
            CreateUserName = "anna.becker",
            UpdateUserName = "anna.becker",
        };
    }

    // ── rows ──────────────────────────────────────────────────────────────────────────────

    private static CorpusRow<TModel> Row<TModel>(string entityId, string key, string purpose, Guid id, TModel model) where TModel : class
    {
        var type = model.GetType();
        type.GetProperty("Id")?.SetValue(model, id);
        type.GetProperty("OrganizationId")?.SetValue(model, Org.R.Id());

        // The two scenario anchors are literal ids outside the id scheme; they carry ordinal 0.
        var ordinal = long.TryParse(id.ToString("D")[^12..], out var n) && n <= int.MaxValue ? (int)n : 0;

        return new CorpusRow<TModel>(entityId, Org.R, ordinal, key, purpose, model, id, RawStorage.None);
    }
}
