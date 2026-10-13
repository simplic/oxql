using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using F = OxQL.IntegrationTests.Fleet.Models.Fleet;
using L = OxQL.IntegrationTests.Fleet.Models.Ledger;
using S = OxQL.IntegrationTests.Fleet.Models.Staff;
using T = OxQL.IntegrationTests.Fleet.Models.Transport;

namespace OxQL.IntegrationTests.Suites.Differential;

/// <summary>
/// Generated rows across four services, for the joins that cross them: invoices whose items nest in
/// groups (a tree to flatten), the billing lines the items name, the shipments and tours whose
/// billing lines those came from (a reference onto an element, typed by a sibling member, with a
/// target of two kinds), the tours a shipment is on, the resources the tours plan with (a reference
/// typed by the variant: a vehicle of one service, an employee of another, or nothing), and the
/// clerk of an invoice (a reference onto a member that is not the key). Every reference takes its
/// target from a short list by the row's number, so each kind of outcome occurs many times and in
/// combination: a target that is there, one that is gone, one held twice, a reference that is null,
/// one no case selects, text that is no key.
/// <para>
/// Nothing is expected by hand: <see cref="WeaveOracle"/> follows the same references through the
/// objects written here.
/// </para>
/// </summary>
public static class WeaveCorpus
{
    public const string Transaction = "ledger.transaction";
    public const string Shipment = "transport.shipment";
    public const string Tour = "transport.tour";
    public const string Vehicle = "fleet.vehicle";
    public const string Employee = "staff.employee";

    public const int VehicleCount = 10;
    public const int EmployeeCount = 6;
    public const int TourCount = 16;
    public const int ShipmentCount = 22;
    public const int LineCount = 60;
    public const int TransactionCount = 180;

    private static readonly Org Organisation = Org.A;

    private static readonly DateTime At = new(2026, 5, 4, 8, 0, 0, DateTimeKind.Utc);

    private static readonly Lazy<Task<CorpusFleet>> Shared = new(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Task<CorpusFleet> FleetAsync() => Shared.Value;

    public static async Task<LabClient> LedgerAsync() => (await FleetAsync()).Client(LabService.Ledger, Organisation);

    // ── the keys ────────────────────────────────────────────────────────────────────────────

    public static Guid VehicleId(int n) => Ids.Of(Spaces.Vehicle, Organisation, 8_000 + n);

    public static Guid EmployeeId(int n) => Ids.Of(Spaces.Employee, Organisation, 8_000 + n);

    public static Guid UserId(int n) => Ids.Of(Spaces.UserAccount, Organisation, 8_000 + n);

    public static Guid TourId(int n) => Ids.Of(Spaces.Tour, Organisation, 8_000 + (n * 11 % 97));

    public static Guid ShipmentId(int n) => Ids.Of(Spaces.Shipment, Organisation, 8_000 + (n * 13 % 97));

    public static Guid SourceLineId(int n) => Ids.Of(Spaces.BillingLine, Organisation, 8_000 + n);

    public static Guid LineId(int n) => Ids.Of(Spaces.LedgerBillingLine, Organisation, 8_000 + n);

    public static Guid TransactionId(int n) => Ids.Of(Spaces.Transaction, Organisation, 8_000 + (n * 17 % 197));

    // ── the lines a shipment and a tour hold (before the rows: the rows are built from them) ──

    private const int SharedAcrossKinds = 900;

    private const int SharedWithinKind = 901;

    private const int Deleted = 999;

    private static int TourLine(int tour, int k) => 100 + tour * 4 + k;

    private static int ShipmentLine(int shipment, int k) => 300 + shipment * 4 + k;

    private static readonly IReadOnlyList<int> ShipmentLines = [.. Enumerable.Range(1, ShipmentCount).SelectMany(n => Enumerable.Range(0, n % 4).Select(k => ShipmentLine(n, k)))];

    private static readonly IReadOnlyList<int> TourLines = [.. Enumerable.Range(1, TourCount).SelectMany(n => Enumerable.Range(0, n % 3).Select(k => TourLine(n, k)))];

    // ── the rows, as objects ────────────────────────────────────────────────────────────────

    public static IReadOnlyList<F.Vehicle> Vehicles { get; } = [.. Enumerable.Range(1, VehicleCount).Select(n => new F.Vehicle
    {
        Id = VehicleId(n), OrganizationId = Organisation.Id(), MatchCode = $"V-{n:D2}", Location = "Weave",
        CreateDateTime = At, UpdateDateTime = At, CreateUserName = "weave", UpdateUserName = "weave",
    })];

    /// <summary>Employees 3 and 4 share one user id.</summary>
    public static IReadOnlyList<S.Employee> Employees { get; } = [.. Enumerable.Range(1, EmployeeCount).Select(n => new S.Employee
    {
        Id = EmployeeId(n), OrganizationId = Organisation.Id(), MatchCode = $"E-{n:D2}", UserId = UserId(n == 4 ? 3 : n),
        Address = new S.EmployeeAddress { FirstName = "Weave", LastName = $"Employee {n:D2}" },
        CreateDateTime = At, UpdateDateTime = At, CreateUserName = "weave", UpdateUserName = "weave",
    })];

    public static IReadOnlyList<T.Tour> Tours { get; } = [.. Enumerable.Range(1, TourCount).Select(BuildTour)];

    public static IReadOnlyList<T.Shipment> Shipments { get; } = [.. Enumerable.Range(1, ShipmentCount).Select(BuildShipment)];

    public static IReadOnlyList<L.BillingLine> Lines { get; } = [.. Enumerable.Range(1, LineCount).Select(BuildLine)];

    public static IReadOnlyList<L.Transaction> Transactions { get; } = [.. Enumerable.Range(1, TransactionCount).Select(BuildTransaction)];

    /// <summary>The resource a tour plans with, by the tour's number: a vehicle (some of which the fleet does not hold), a driver, a carrier, or none.</summary>
    private static T.Resource? ResourceOf(int n) => (n % 6) switch
    {
        0 => new T.TractorUnitResource { Id = VehicleId(1 + (n % 13)), MatchCode = "tractor" },
        1 => new T.DriverResource { Id = EmployeeId(1 + (n % 8)), MatchCode = "driver" },
        2 => new T.CarrierResource { Id = Ids.Of(Spaces.Resource, Organisation, 8_000 + n), MatchCode = "carrier" },
        3 => new T.TrailerResource { Id = VehicleId(1 + (n * 3 % 13)), MatchCode = "trailer" },
        4 => null,
        _ => new T.CarResource { Id = VehicleId(1 + (n * 5 % 13)), MatchCode = "car" },
    };

    private static T.BillingLine SourceLine(int id, string text) => new()
    {
        Id = SourceLineId(id), Type = T.BillingLineType.Customer, Status = "released", Text = text, Date = At,
    };

    private static T.Tour BuildTour(int n)
    {
        var lines = Enumerable.Range(0, n % 3).Select(k => SourceLine(TourLine(n, k), $"tour {n} line {k}")).ToList();

        // A line id a shipment holds as well.
        if (n == 4)
            lines.Add(SourceLine(SharedAcrossKinds, "held by a shipment and a tour"));

        return new T.Tour
        {
            Id = TourId(n), OrganizationId = Organisation.Id(), Number = $"WT-{n:D3}", Resource = ResourceOf(n), BillingLines = lines,
            AttachedResources = [.. Enumerable.Range(0, n % 3).Select(k => new T.AttachedResource { Id = Ids.Of(Spaces.AttachedResource, Organisation, 8_000 + n * 10 + k), Resource = ResourceOf(n + k + 1) })],
            CreateDateTime = At, UpdateDateTime = At,
        };
    }

    private static T.Shipment BuildShipment(int n)
    {
        var lines = Enumerable.Range(0, n % 4).Select(k => SourceLine(ShipmentLine(n, k), $"shipment {n} line {k}")).ToList();

        if (n == 5)
            lines.Add(SourceLine(SharedAcrossKinds, "held by a shipment and a tour"));

        // A line id two shipments hold.
        if (n is 9 or 10)
            lines.Add(SourceLine(SharedWithinKind, "held by two shipments"));

        return new T.Shipment
        {
            Id = ShipmentId(n), OrganizationId = Organisation.Id(), ShipmentNumber = $"WS-{n:D3}", BillingLines = lines,
            Tours = [.. Enumerable.Range(0, n % 4).Select(i => new T.ShipmentTour
            {
                Id = Ids.Of(Spaces.ShipmentTourEntry, Organisation, 8_000 + n * 10 + i),
                Number = $"entry {i}",
                TourId = TourOfEntry(n, i),
            })],
            CreateDateTime = At, UpdateDateTime = At, CreateUserId = LabIdentity.User,
        };
    }

    /// <summary>The tour an entry of a shipment names: none, one that is gone, or a tour.</summary>
    private static Guid? TourOfEntry(int n, int i) =>
        (n + i) % 5 == 0 ? null
        : (n + i) % 7 == 0 ? Ids.Of(Spaces.Tour, Organisation, 9_900 + n)
        : TourId(1 + ((n * 3 + i) % TourCount));

    private static L.BillingLine BuildLine(int n)
    {
        L.SourceBillingLineReference? source = (n % 9) switch
        {
            0 => null,
            1 => new L.SourceBillingLineReference { Type = "manual", Id = SourceLineId(ShipmentLines[n % ShipmentLines.Count]) },
            2 => new L.SourceBillingLineReference { Type = "logistics", Id = SourceLineId(Deleted) },
            3 or 4 or 5 => new L.SourceBillingLineReference { Type = "logistics", Id = SourceLineId(ShipmentLines[n * 5 % ShipmentLines.Count]) },
            6 or 7 => new L.SourceBillingLineReference { Type = "logistics", Id = SourceLineId(TourLines[n * 3 % TourLines.Count]) },
            _ when n % 27 == 8 => new L.SourceBillingLineReference { Type = "logistics", Id = SourceLineId(n % 2 == 0 ? SharedAcrossKinds : SharedWithinKind) },
            _ => new L.SourceBillingLineReference { Type = "logistics", Id = SourceLineId(ShipmentLines[n * 7 % ShipmentLines.Count]) },
        };

        return new L.BillingLine
        {
            Id = LineId(n), OrganizationId = Organisation.Id(), Text = $"line {n}", Date = At, State = L.BillingLineState.Invoiced,
            SourceBillingLineReference = source, References = ReferencesOf(n),
            CreateDateTime = At, UpdateDateTime = At, CreateUserName = "weave",
        };
    }

    /// <summary>The references an item or a line carries beside its source: a shipment, a tour spelled in upper case, a tariff, text that is no key, a shipment that is gone.</summary>
    public static List<L.BillingLineReference> ReferencesOf(int n) => [.. Enumerable.Range(0, n % 4).Select(i => ((n + i) % 5) switch
    {
        0 => Reference(n, i, "shipment", ShipmentId(1 + ((n + i) % ShipmentCount)).ToString("D")),
        1 => Reference(n, i, "tour", TourId(1 + ((n + i) % TourCount)).ToString("D").ToUpperInvariant()),
        2 => Reference(n, i, "Tariff", "TARIFF-STD"),
        3 => Reference(n, i, "shipment", "not a key"),
        _ => Reference(n, i, "shipment", Ids.Of(Spaces.Shipment, Organisation, 9_900 + n).ToString("N")),
    })];

    private static L.BillingLineReference Reference(int n, int i, string dataType, string referenceId) =>
        new() { Id = Ids.Of(Spaces.LedgerLineReference, Organisation, 8_000 + n * 10 + i), DataType = dataType, ReferenceId = referenceId };

    private static L.Transaction BuildTransaction(int n)
    {
        var counter = n * 31;

        // Two invoices nest deeper than a flatten reads; every tenth has no items.
        var items = n is 40 or 41 ? [Chain(ref counter, n == 40 ? 6 : 8)] : Items(ref counter, count: n % 10 == 0 ? 0 : 5 + (n % 9), depth: 1);

        return new L.Transaction
        {
            Id = TransactionId(n), OrganizationId = Organisation.Id(), Number = $"WX-{n:D4}", Reference = $"batch {n % 4}",
            Items = items, States = ["posted"], CreateUserId = UserId(1 + (n % 8)), CreateUserName = "weave",
            Date = At.AddDays(n % 9), CreateDateTime = At, UpdateDateTime = At,
        };
    }

    private static List<L.TransactionItem> Items(ref int counter, int count, int depth)
    {
        var items = new List<L.TransactionItem>();

        for (var index = 0; index < count; index++)
        {
            var c = ++counter;

            items.Add((c % 5) switch
            {
                0 => new L.TextTransactionItem { Id = ItemId(c), SortNumber = c % 4, DeserializationType = "text", Text = $"text {c % 6}" },
                1 or 2 or 4 => new L.BillingLineTransactionItem
                {
                    Id = ItemId(c), SortNumber = c % 4, DeserializationType = "billing_line", Text = $"billing {c % 6}",
                    BillingLineId = LineId(1 + (c * 7 % (LineCount + 4))), References = ReferencesOf(c),
                },
                3 when depth < 4 => new L.GroupTransactionItem { Id = ItemId(c), SortNumber = c % 4, DeserializationType = "group", Text = $"group {c % 6}", Items = Items(ref counter, c % 4, depth + 1) },
                _ => new L.ArticleTransactionItem { Id = ItemId(c), SortNumber = c % 4, DeserializationType = "article", Text = $"article {c % 6}" },
            });
        }

        return items;
    }

    /// <summary>Groups holding one another <paramref name="depth"/> deep, a billing line item at the bottom.</summary>
    private static L.TransactionItem Chain(ref int counter, int depth)
    {
        var c = ++counter;

        return depth <= 1
            ? new L.BillingLineTransactionItem { Id = ItemId(c), SortNumber = 1, DeserializationType = "billing_line", Text = "deep", BillingLineId = LineId(3) }
            : new L.GroupTransactionItem { Id = ItemId(c), SortNumber = 1, DeserializationType = "group", Text = $"level {depth}", Items = [Chain(ref counter, depth - 1)] };
    }

    private static Guid ItemId(int c) => Ids.Of(Spaces.TransactionItem, Organisation, 800_000 + c);

    // ── the fleet ───────────────────────────────────────────────────────────────────────────

    private static async Task<CorpusFleet> StartAsync() =>
        await CorpusFleet.CreateAsync("weave", seed: [], extra: async fleet =>
        {
            await WriteAsync(fleet, LabService.Fleet, Vehicle, Vehicles);
            await WriteAsync(fleet, LabService.Staff, Employee, Employees);
            await WriteAsync(fleet, LabService.Transport, Tour, Tours);
            await WriteAsync(fleet, LabService.Transport, Shipment, Shipments);
            await WriteAsync(fleet, LabService.Ledger, "ledger.billing_line", Lines);
            await WriteAsync(fleet, LabService.Ledger, Transaction, Transactions);
        });

    private static async Task WriteAsync<TRow>(LabFleet fleet, LabService service, string entity, IReadOnlyList<TRow> rows)
    {
        var database = await fleet.DatabaseAsync(service);

        await database.GetCollection<BsonDocument>(service.Model.Entities[entity].Collection)
            .InsertManyAsync(rows.Select(row => row.ToBsonDocument(typeof(TRow))), new InsertManyOptions { IsOrdered = false });
    }
}
