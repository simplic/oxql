using OxQL.IntegrationTests.Fleet.Models.Fleet;
using static OxQL.IntegrationTests.Fixtures.Rows.RowKit;

namespace OxQL.IntegrationTests.Fixtures.Rows;

/// <summary>
/// The fleet service: <c>fleet.vehicle</c> (the numeric entity: decimals as Decimal128 and as
/// strings, zero, negative and many places, an int at Int32 max, collections of zero, one and
/// several, null and a dangling nested status, the rich bag), <c>fleet.equipment</c> (a local
/// reference onto vehicles with a hit, a duplicate, a miss and a null source),
/// <c>fleet.department</c> (the target of the vehicle and the shipment departments, with a
/// duplicate name) and <c>fleet.status</c> (a small lookup table with a null name).
/// </summary>
internal static class FleetRows
{
    public const string Vehicle = "fleet.vehicle";
    public const string Equipment = "fleet.equipment";
    public const string Department = "fleet.department";
    public const string Status = "fleet.status";

    // ── fleet.status ────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<CorpusRow> Statuses(Org org) => org switch
    {
        Org.A =>
        [
            StatusRow(Org.A, 1, "st-available", "the status most vehicles point at", "Available", "available", "#22c55e"),
            StatusRow(Org.A, 2, "st-workshop", "a second status, for a group with two buckets", "Workshop", "workshop", "#f59e0b"),
            StatusRow(Org.A, 3, "st-retired", "isSelectable false", "Retired", "retired", "#64748b", selectable: false),
            StatusRow(Org.A, 4, "st-nameless", "name and displayName are null: a null sort key on a lookup table", null, null, "#64748b"),
        ],
        Org.B => [StatusRow(Org.B, 1, "b-st-available", "a clone of st-available in the other organisation", "Available", "available", "#22c55e")],
        _ => [],
    };

    private static CorpusRow StatusRow(Org org, int n, string key, string purpose, string? name, string? displayKey, string hexColor, bool selectable = true) =>
        Row(Status, Spaces.VehicleStatus, org, n, key, purpose, o => (StatusOf(o, n, name, displayKey, hexColor, selectable), RawStorage.None));

    /// <summary>A status as the table holds it and as a vehicle embeds it.</summary>
    private static VehicleStatus StatusOf(Org org, int n, string? name, string? displayKey, string hexColor, bool selectable = true) => new()
    {
        Id = Ids.Of(Spaces.VehicleStatus, org, n),
        OrganizationId = org.Id(),
        Name = name,
        DisplayName = name,
        DisplayKey = displayKey,
        HexColor = hexColor,
        IsSelectable = selectable,
        CreateDateTime = Dt("2026-01-01T00:00:00Z"),
        UpdateDateTime = Dt("2026-01-01T00:00:00Z"),
        CreateUserId = LabUser,
        UpdateUserId = LabUser,
        CreateUserName = "lab",
        UpdateUserName = "lab",
    };

    // ── fleet.department ────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<CorpusRow> Departments(Org org) => org switch
    {
        Org.A =>
        [
            DepartmentRow(Org.A, 1, "dep-fleet", "the baseline department; every vehicle and most shipments name it", "Fleet", true, "#0ea5e9"),
            DepartmentRow(Org.A, 2, "dep-workshop", "a second department", "Workshop", true, "#f59e0b"),
            DepartmentRow(Org.A, 3, "dep-fleet-dup", "the same name as dep-fleet: a duplicate on the only sort key a three-member entity has", "Fleet", false, null),
        ],
        Org.B => [DepartmentRow(Org.B, 1, "b-dep-fleet", "a clone of dep-fleet in the other organisation", "Fleet", true, "#0ea5e9")],
        _ => [],
    };

    private static CorpusRow DepartmentRow(Org org, int n, string key, string purpose, string name, bool selectable, string? color) =>
        Row(Department, Spaces.Department, org, n, key, purpose, _ => (new Department { Name = name, IsSelectable = selectable, Color = color }, RawStorage.None));

    // ── fleet.vehicle ───────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<CorpusRow> Vehicles(Org org) => org switch
    {
        Org.A => VehiclesA,
        Org.B => VehiclesB,
        _ => [],
    };

    private static readonly IReadOnlyList<CorpusRow> VehiclesA =
    [
        V(1, "dec-decimal128", "mileage and operatingHours as Decimal128, the representation the driver writes today"),
        V(2, "dec-string", "the same two numbers held as strings, as rows written before Decimal128 hold them: a numeric range misses this row unless the tolerant form reaches it, and a sort puts it after every numeric row whatever its value", (v, raw) =>
        {
            v.MatchCode = "VEH-002";
            v.RegistrationPlate!.RegistrationIdentifier = "B-LA 200";
            raw.DecimalAsString("mileage", "125000.00").DecimalAsString("operatingHours", "1234.5");
        }),
        V(3, "dec-string-mid", "a second string-typed row with a different value, so the string bracket has an order of its own", (v, raw) =>
        {
            v.MatchCode = "VEH-003";
            v.RegistrationPlate!.RegistrationIdentifier = "B-LA 300";
            v.Mileage = 99999.99m;
            v.OperatingHours = 10.25m;
            raw.DecimalAsString("mileage", "99999.99").DecimalAsString("operatingHours", "10.25");
        }),
        V(4, "dec-zero", "zero, which is neither null nor missing", (v, _) => { v.MatchCode = "VEH-004"; v.Mileage = 0m; v.OperatingHours = 0m; }),
        V(5, "dec-negative", "a negative decimal", (v, _) => { v.MatchCode = "VEH-005"; v.Mileage = -42.5m; v.OperatingHours = -1.25m; }),
        V(6, "dec-many-places", "a decimal with 25 fractional digits: no double holds it", (v, _) =>
        {
            v.MatchCode = "VEH-006";
            v.Mileage = 0.1234567890123456789012345m;
            v.OperatingHours = 3.14159265358979323846m;
        }),
        V(7, "payload-null", "a nullable decimal deep in an object is null", (v, _) => { v.MatchCode = "VEH-007"; v.AdditionalTechnicalData!.Payload = null; }),
        V(8, "payload-missing", "the same nullable decimal is absent", (v, raw) =>
        {
            v.MatchCode = "VEH-008";
            v.AdditionalTechnicalData!.Payload = null;
            raw.Unset("additionalTechnicalData.payload", "a nullable decimal absent, not null");
        }),
        V(9, "int-edge", "fuelTankCapacity at Int32 max, so a numeric comparison that widens to double is visible", (v, _) => { v.MatchCode = "VEH-009"; v.FuelTankCapacity = int.MaxValue; }),
        V(10, "appt-none", "the collections are the empty array", (v, _) => { v.MatchCode = "VEH-010"; v.Appointments = []; v.LoadingSlots = []; }),
        V(11, "appt-one", "exactly one element", (v, _) => { v.MatchCode = "VEH-011"; v.Appointments = [AppointmentOf(Org.A, 11)]; }),
        V(12, "appt-split", "two elements that each satisfy one half of appointments.checkType eq 1 AND appointments.nextValue gt 0; any() over the same condition does not match", (v, _) =>
        {
            v.MatchCode = "VEH-012";
            v.Appointments = [AppointmentOf(Org.A, 12, checkType: 1, nextValue: 0), AppointmentOf(Org.A, 13, checkType: 2, nextValue: 500)];
            v.LoadingSlots = [Slot(Org.A, 12, "Slot 1", "rear"), Slot(Org.A, 13, "Slot 2", "front"), Slot(Org.A, 14, "Slot 3", "side")];
        }),
        V(13, "appt-null", "a nullable collection is null, which is not the empty array", (v, _) => { v.MatchCode = "VEH-013"; v.Appointments = null; }),
        V(14, "status-null", "the nested status object is null", (v, _) => { v.MatchCode = "VEH-014"; v.Status = null; }),
        V(15, "status-dangling", "status.id names no fleet.status row: a resolve on it answers null", (v, _) =>
        {
            v.MatchCode = "VEH-015";
            v.Status = StatusOf(Org.A, 9, "Ghost", null, "#64748b");
            v.QrCode = "QR-015";
        }),
        V(16, "addon-rich", "the full addon bag, and qrCode, whose storage name is QRCode and not the derivation", (v, _) => { v.MatchCode = "VEH-016"; v.Addon = Addons.Rich(); v.QrCode = "QR-016"; }),
        V(17, "status-workshop", "a second status value, so a group on status.name has more than one bucket", (v, _) => { v.MatchCode = "VEH-017"; v.Status = StatusOf(Org.A, 2, "Workshop", "workshop", "#f59e0b"); }),
        V(18, "deleted", "isDeleted is true", (v, _) => { v.MatchCode = "VEH-018"; v.IsDeleted = true; }),
        V(19, "matchcode-dup", "the same matchCode as the baseline: a duplicate on the sort key a grid uses", (v, _) => { v.MatchCode = "VEH-001"; v.Mileage = 7.5m; v.OperatingHours = 7.5m; }),
        V(20, "location-null", "a nullable string is null while another holds the empty string", (v, _) => { v.MatchCode = "VEH-020"; v.Location = null; v.Remark = ""; }),
    ];

    private static readonly IReadOnlyList<CorpusRow> VehiclesB =
    [
        Row(Vehicle, Spaces.Vehicle, Org.B, 1, "b-dec-decimal128", "an exact clone of dec-decimal128 in the other organisation", VehicleBase, (v, _) =>
        {
            v.Appointments = [AppointmentOf(Org.B, 1)];
            v.LoadingSlots = [Slot(Org.B, 1, "Slot 1", "rear")];
        }),
        Row(Vehicle, Spaces.Vehicle, Org.B, 2, "b-dec-string", "an exact clone of dec-string", VehicleBase, (v, raw) =>
        {
            v.MatchCode = "VEH-002";
            v.Appointments = [AppointmentOf(Org.B, 2)];
            v.LoadingSlots = [];
            raw.DecimalAsString("mileage", "125000.00").DecimalAsString("operatingHours", "1234.5");
        }),
    ];

    private static CorpusRow V(int n, string key, string purpose, Action<Vehicle, RawStorage>? change = null) =>
        Row(Vehicle, Spaces.Vehicle, Org.A, n, key, purpose, VehicleBase, change);

    private static (Vehicle, RawStorage) VehicleBase(Org org) => (new Vehicle
    {
        Location = "Depot Berlin",
        MatchCode = "VEH-001",
        Status = StatusOf(org, 1, "Available", "available", "#22c55e"),
        Mileage = 125000.00m,
        MileageDate = Dt("2026-02-01T00:00:00Z"),
        OperatingHours = 1234.5m,
        FuelTankCapacity = 600,
        Remark = null,
        DispositionSortingKey = "A",
        YearOfManufacturing = Dt("2020-01-01T00:00:00Z"),
        AdditionalTechnicalData = new TechnicalData
        {
            EmptyWeight = 7500.000m,
            TotalWeight = 18000.000m,
            TireAmount = 6,
            Payload = 10500.000m,
            RimSizeAxle1 = "22.5",
            RimSizeAxle2 = "22.5",
            RimSizeAxle3 = "",
            FrameColor = "white",
            VehicleExecution = "standard",
            HasFixedSuperstructure = false,
            SuperstructureParkingSpaces = 0,
        },
        RegistrationPlate = new RegistrationPlate { CountryIso = "DE", IsSeasonal = false, RegistrationIdentifier = "B-LA 100", Remark = null },
        IsSystemVehicle = false,
        Addon = Addons.OneKey(),
        Department = new VehicleDepartment { Id = Ids.Of(Spaces.Department, org, 1), Name = "Fleet", IsSelectable = true, Color = "#0ea5e9" },
        Appointments = [AppointmentOf(org, 1)],
        LoadingSlots = [Slot(org, 1, "Slot 1", "rear")],
        CreateDateTime = Dt("2026-01-02T08:00:00Z"),
        UpdateDateTime = Dt("2026-02-02T08:00:00Z"),
        CreateUserId = LabUser,
        UpdateUserId = LabUser,
        CreateUserName = "lab",
        UpdateUserName = "lab",
        IsDeleted = false,
    }, RawStorage.None);

    private static Appointment AppointmentOf(Org org, int n, int checkType = 1, int nextValue = 200) => new()
    {
        Id = Ids.Of(Spaces.VehicleAppointment, org, n),
        AppointmentType = new AppointmentType
        {
            Id = Ids.Of(Spaces.AppointmentType, org, 1),
            OrganizationId = org.Id(),
            DisplayName = "Main inspection",
            DisplayKey = "tuev",
            Interval = 24,
            IntervalType = "month",
            CreateDateTime = Dt("2026-01-01T00:00:00Z"),
            UpdateDateTime = Dt("2026-01-01T00:00:00Z"),
            CreateUserId = LabUser,
            UpdateUserId = LabUser,
            CreateUserName = "lab",
            UpdateUserName = "lab",
        },
        LastDate = Dt("2025-06-01T00:00:00Z"),
        NextDate = Dt("2027-06-01T00:00:00Z"),
        Remark = "lab",
        SupplierGuid = null,
        LastValue = 100,
        NextValue = nextValue,
        CheckType = checkType,
    };

    private static LoadingSlot Slot(Org org, int n, string name, string description) =>
        new() { Id = Ids.Of(Spaces.LoadingSlot, org, n), Name = name, Description = description };

    // ── fleet.equipment ─────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<CorpusRow> Equipments(Org org) => org switch
    {
        Org.A =>
        [
            E(1, "eq-veh1", "vehicle.id names vehicle row 1"),
            E(2, "eq-veh1-dup", "the same vehicle and the same name: a duplicate sort key on a joinable member", (e, _) => e.Number = "EQ-002"),
            E(3, "eq-veh2", "vehicle.id names vehicle row 2", (e, _) =>
            {
                e.Name = "Tail lift";
                e.Number = "EQ-003";
                e.Vehicle = new VehicleSubset { Id = Ids.Of(Spaces.Vehicle, Org.A, 2), RegistrationPlate = "B-LA 200", MatchCode = "VEH-002" };
            }),
            E(4, "eq-dangling", "vehicle.id names no vehicle: a resolve on it answers null", (e, _) =>
            {
                e.Name = "Ghost";
                e.Number = "EQ-004";
                e.Vehicle = new VehicleSubset { Id = Ids.Dangling(1), RegistrationPlate = "X-XX 1", MatchCode = "GHOST" };
            }),
            E(5, "eq-null-vehicle", "the whole nested vehicle object is null", (e, _) => { e.Name = "Loose"; e.Number = "EQ-005"; e.Vehicle = null; }),
        ],
        Org.B => [Row(Equipment, Spaces.Equipment, Org.B, 1, "b-eq-veh1", "a clone of eq-veh1 pointing at the other organisation's vehicle", EquipmentBase)],
        _ => [],
    };

    private static CorpusRow E(int n, string key, string purpose, Action<Equipment, RawStorage>? change = null) =>
        Row(Equipment, Spaces.Equipment, Org.A, n, key, purpose, EquipmentBase, change);

    private static (Equipment, RawStorage) EquipmentBase(Org org) => (new Equipment
    {
        Name = "Crane",
        Number = "EQ-001",
        EquipmentType = new EquipmentType { Id = Ids.Of(Spaces.Equipment, org, 900), DisplayName = "Crane", DisplayKey = "crane" },
        Vehicle = new VehicleSubset { Id = Ids.Of(Spaces.Vehicle, org, 1), RegistrationPlate = "B-LA 100", MatchCode = "VEH-001" },
        CreateDateTime = Dt("2026-01-03T08:00:00Z"),
        UpdateDateTime = Dt("2026-02-03T08:00:00Z"),
        CreateUserId = LabUser,
        UpdateUserId = LabUser,
        CreateUserName = "lab",
        UpdateUserName = "lab",
        IsDeleted = false,
    }, RawStorage.None);
}
