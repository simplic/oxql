using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures.Rows;
using OxQL.IntegrationTests.Fleet;
using Transport = OxQL.IntegrationTests.Fleet.Models.Transport;

namespace OxQL.IntegrationTests.Fixtures;

/// <summary>
/// The report scenario data (DESIGN §2, A1–A5, and the failure modes of §3.6): rows in
/// organisation R only, in the collections of every entity the scenarios cross, written by
/// <see cref="CorpusSeeder"/> alongside the corpus, so the shared fleet holds them and no case
/// over organisations A and B sees them. The corpus entities (<see cref="Corpus.Entities"/>) are
/// unchanged; these rows are reached through <see cref="Entities"/>, <see cref="Rows"/> and
/// <see cref="Row"/>, and every oracle helper of <see cref="Corpus"/> that takes a row reads them.
/// <code>
/// var ledger = await Lab.ClientAsync(LabService.Ledger, Org.R);
/// var answer = await ledger.SendAsync(ReportSeed.Transaction, $$"""[{ "match": { "id": { "eq": "{{ReportSeed.TransactionId.Wire()}}" } } }]""");
/// </code>
/// </summary>
public static class ReportSeed
{
    public const string Transaction = "ledger.transaction";
    public const string BillingLine = "ledger.billing_line";
    public const string Shipment = "transport.shipment";
    public const string Tour = "transport.tour";
    public const string DeliveryAttempt = "transport.delivery_attempt";
    public const string Resource = "transport.resource";
    public const string Employee = "staff.employee";
    public const string Vehicle = "fleet.vehicle";
    public const string Contact = "directory.contact";

    // ── the anchors the scenario requests name ─────────────────────────────────────────────

    /// <summary>The mixed invoice: the <c>transactionId</c> variable of A1, A3 and A5.</summary>
    public static readonly Guid TransactionId = Guid.Parse("7d0e4c1a-3b9e-4d55-9a57-3f1f1b0f2c11");

    /// <summary>The scenario shipment: the <c>shipmentId</c> variable of A2a.</summary>
    public static readonly Guid ShipmentId = Guid.Parse("5b2a7e10-0c1d-4a5e-8f7b-9a1c2d3e4f50");

    // ── ledger ────────────────────────────────────────────────────────────────────────────

    public static readonly Guid MissingSourceTransactionId = Id(Spaces.Transaction, 2);
    public static readonly Guid AmbiguousSourceTransactionId = Id(Spaces.Transaction, 3);
    public static readonly Guid AmbiguousClerkTransactionId = Id(Spaces.Transaction, 4);
    public static readonly Guid DeepNestingTransactionId = Id(Spaces.Transaction, 5);
    public static readonly Guid MissingClerkTransactionId = Id(Spaces.Transaction, 6);

    /// <summary>
    /// The ERP billing lines, in order: 0 shipment freight, 1 shipment waiting time, 2 the tour line,
    /// 3 the deleted source, 4 the duplicate source, 5 the tariff-only line (source on the second shipment).
    /// </summary>
    public static readonly IReadOnlyList<Guid> ErpLineIds = [.. Enumerable.Range(1, 6).Select(n => Id(Spaces.LedgerBillingLine, n))];

    // ── transport ─────────────────────────────────────────────────────────────────────────

    public static readonly Guid ShipmentWithoutAttemptsId = Id(Spaces.Shipment, 2);
    public static readonly Guid ShipmentWithDuplicateLineId = Id(Spaces.Shipment, 3);
    public static readonly Guid TractorTourId = Id(Spaces.Tour, 1);
    public static readonly Guid CarrierTourId = Id(Spaces.Tour, 2);

    /// <summary>The logistics billing lines of the shipments: 0 and 1 on the scenario shipment, 2 on the second shipment.</summary>
    public static readonly IReadOnlyList<Guid> ShipmentLineIds = [Id(Spaces.BillingLine, 1), Id(Spaces.BillingLine, 2), Id(Spaces.BillingLine, 3)];

    /// <summary>The billing line of the tractor tour.</summary>
    public static readonly Guid TourLineId = Id(Spaces.BillingLine, 4);

    /// <summary>A billing line id held by the third shipment and by the carrier tour: a key found in two parents.</summary>
    public static readonly Guid DuplicateLineId = Id(Spaces.BillingLine, 5);

    /// <summary>A source line id no shipment and no tour holds: the deleted source line.</summary>
    public static readonly Guid DeletedSourceLineId = Id(Spaces.BillingLine, 99);

    // ── the resources, and what they are ──────────────────────────────────────────────────

    public static readonly Guid DriverEmployeeId = Id(Spaces.Employee, 4);
    public static readonly Guid TractorVehicleId = Id(Spaces.Vehicle, 1);
    public static readonly Guid TrailerVehicleId = Id(Spaces.Vehicle, 2);

    /// <summary>Vehicle-variant resource ids the fleet service holds no vehicle for.</summary>
    public static readonly Guid CarResourceId = Id(Spaces.Vehicle, 3);
    public static readonly Guid ContainerResourceId = Id(Spaces.Vehicle, 4);
    public static readonly Guid EquipmentResourceId = Id(Spaces.Vehicle, 5);

    /// <summary>The carrier resource: its id names nothing in any service.</summary>
    public static readonly Guid CarrierResourceId = Id(Spaces.Resource, 6);

    // ── staff and directory ───────────────────────────────────────────────────────────────

    public static readonly Guid ClerkEmployeeId = Id(Spaces.Employee, 1);
    public static readonly Guid ClerkUserId = Id(Spaces.UserAccount, 1);

    /// <summary>The user id two employees share.</summary>
    public static readonly Guid DuplicateUserId = Id(Spaces.UserAccount, 2);
    public static readonly IReadOnlyList<Guid> DuplicateUserEmployeeIds = [Id(Spaces.Employee, 2), Id(Spaces.Employee, 3)];

    /// <summary>A user id no employee has.</summary>
    public static readonly Guid UnknownUserId = Id(Spaces.UserAccount, 99);

    public static readonly Guid RecipientContactId = Id(Spaces.DirectoryContact, 1);
    public static readonly Guid ContactWithoutEmailId = Id(Spaces.DirectoryContact, 2);

    /// <summary>An id of organisation R.</summary>
    public static Guid Id(string space, long n) => Ids.Of(space, Org.R, n);

    // ── resource snapshots, embedded where the logistics service embeds them ──────────────

    public static Transport.TractorUnitResource TractorResource() => new() { Id = TractorVehicleId, DisplayName = "Tractor ZM-01", MatchCode = "ZM-01" };

    public static Transport.TrailerResource TrailerResource() => new()
    {
        Id = TrailerVehicleId, DisplayName = "Trailer AH-07", MatchCode = "AH-07", IsLoadable = true,
        LoadingSlots = [new Transport.ResourceLoadingSlot { Id = Id(Spaces.Resource, 101), Name = "front" }, new Transport.ResourceLoadingSlot { Id = Id(Spaces.Resource, 102), Name = "rear" }],
    };

    public static Transport.CarrierResource CarrierResource() => new() { Id = CarrierResourceId, DisplayName = "Elbe Spedition KG", MatchCode = "ELBE" };

    // ── the rows ──────────────────────────────────────────────────────────────────────────

    /// <summary>Every entity the report rows fill: the entity id, the owning service and the rows. The collection is the model's.</summary>
    public static readonly IReadOnlyList<(string Entity, LabService Service, IReadOnlyList<CorpusRow> Rows)> Entities =
    [
        (Contact, LabService.Directory, ReportRows.Contacts(Org.R)),
        (Employee, LabService.Staff, ReportRows.Employees(Org.R)),
        (Vehicle, LabService.Fleet, ReportRows.Vehicles(Org.R)),
        (Resource, LabService.Transport, ReportRows.Resources(Org.R)),
        (Shipment, LabService.Transport, ReportRows.Shipments(Org.R)),
        (Tour, LabService.Transport, ReportRows.Tours(Org.R)),
        (DeliveryAttempt, LabService.Transport, ReportRows.DeliveryAttempts(Org.R)),
        (BillingLine, LabService.Ledger, ReportRows.BillingLines(Org.R)),
        (Transaction, LabService.Ledger, ReportRows.Transactions(Org.R)),
    ];

    /// <summary>The report rows of one entity, in seed order.</summary>
    public static IReadOnlyList<CorpusRow> Rows(string entityId) =>
        Entities.FirstOrDefault(entity => entity.Entity == entityId).Rows
        ?? throw new ArgumentException($"'{entityId}' holds no report rows.", nameof(entityId));

    /// <summary>One report row by entity and key.</summary>
    public static CorpusRow Row(string entityId, string key) =>
        Rows(entityId).FirstOrDefault(row => row.Key == key)
        ?? throw new ArgumentException($"'{entityId}' has no report row keyed '{key}'.", nameof(key));

    /// <summary>Writes the report rows of <paramref name="services"/> into <paramref name="fleet"/>.</summary>
    public static Task SeedAsync(LabFleet fleet, IReadOnlySet<LabService> services, CancellationToken cancellationToken = default) =>
        Task.WhenAll(Entities.Where(entity => services.Contains(entity.Service) && entity.Rows.Count > 0).Select(async entity =>
        {
            var database = await fleet.DatabaseAsync(entity.Service);

            await database.GetCollection<BsonDocument>(entity.Service.Model.Entities[entity.Entity].Collection)
                .InsertManyAsync(entity.Rows.Select(row => row.Stored), new InsertManyOptions { IsOrdered = false }, cancellationToken);
        }));
}
