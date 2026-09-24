using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Staff;
using OxQL.IntegrationTests.Fleet.Models.Transport;
using Xunit;

namespace OxQL.IntegrationTests.Spike;

/// <summary>
/// A private fleet with a handful of rows written through the model classes and the driver,
/// as a service's repository writes them: seeded once for the class, read by every test,
/// dropped afterwards.
/// </summary>
public sealed class SpikeFleetFixture : IAsyncLifetime
{
    public static readonly Guid DepartmentA = Guid.Parse("20000003-0001-4000-8000-000000000001");

    public static readonly Guid DepartmentB = Guid.Parse("20000003-0002-4000-8000-000000000001");

    public LabFleet Fleet { get; } = LabFleet.Create("spike");

    public async Task InitializeAsync()
    {
        var staff = await Fleet.DatabaseAsync(LabService.Staff);

        await staff.GetCollection<Employee>("employee").InsertManyAsync(
        [
            Employee(1, "ALPHA", "Berlin", "early"),
            Employee(2, "BRAVO", "Berlin", "night"),
            Employee(3, "CHARLIE", "Hamburg", "night"),
            Employee(4, "DELTA", "Berlin", null),
            Employee(5, "ECHO", "Berlin", "late", deleted: true),
        ]);

        await staff.GetCollection<BsonDocument>(TestAddonSource.Collection).InsertOneAsync(
            TestAddonSource.Document(Guid.Parse("50000001-0001-4000-8000-000000000001"), LabIdentity.OrganisationA, "staff.employee", "shiftModel", "string", "Shift model"));

        var fleet = await Fleet.DatabaseAsync(LabService.Fleet);

        await fleet.GetCollection<Department>("department").InsertManyAsync(
        [
            new Department { Id = DepartmentA, OrganizationId = LabIdentity.OrganisationA, Name = "Fleet", IsSelectable = true, Color = "#0ea5e9" },
            new Department { Id = DepartmentB, OrganizationId = LabIdentity.OrganisationB, Name = "Fleet B", IsSelectable = true },
        ]);

        var transport = await Fleet.DatabaseAsync(LabService.Transport);

        await transport.GetCollection<Shipment>("shipment").InsertManyAsync(
        [
            Shipment(1, LabIdentity.OrganisationA, DepartmentA),
            Shipment(2, LabIdentity.OrganisationA, Guid.Parse("00000000-0001-4000-8000-000000000001")),
            Shipment(3, LabIdentity.OrganisationB, DepartmentB),
        ]);
    }

    public async Task DisposeAsync() => await Fleet.DisposeAsync();

    public static Guid EmployeeId(int n) => Guid.Parse($"30000001-0001-4000-8000-{n:D12}");

    public static Guid ShipmentId(int n) => Guid.Parse($"10000001-0001-4000-8000-{n:D12}");

    private static Employee Employee(int n, string matchCode, string city, string? shiftModel, bool deleted = false) => new()
    {
        Id = EmployeeId(n),
        OrganizationId = LabIdentity.OrganisationA,
        IsDeleted = deleted,
        MatchCode = matchCode,
        Address = new EmployeeAddress { LastName = matchCode, City = city },
        CreateDateTime = new DateTime(2026, 1, n, 8, 0, 0, DateTimeKind.Utc),
        UpdateDateTime = new DateTime(2026, 2, n, 8, 0, 0, DateTimeKind.Utc),
        CreateUserId = LabIdentity.User,
        Addon = shiftModel is null ? null : new Dictionary<string, object> { ["shiftModel"] = shiftModel },
    };

    private static Shipment Shipment(int n, Guid organisation, Guid department) => new()
    {
        Id = ShipmentId(n),
        OrganizationId = organisation,
        ShipmentNumber = $"S-{n:D4}",
        LoadStart = new DateTime(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc),
        CreateDateTime = new DateTime(2026, 1, 4, 8, 0, 0, DateTimeKind.Utc),
        UpdateDateTime = new DateTime(2026, 2, 4, 8, 0, 0, DateTimeKind.Utc),
        CreateUserId = LabIdentity.User,
        Department = new ShipmentDepartment { Id = department, Name = "Dispatch", OrderId = 1 },
    };
}
