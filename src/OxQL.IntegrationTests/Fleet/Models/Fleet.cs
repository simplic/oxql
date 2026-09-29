using MongoDB.Bson.Serialization.Attributes;
using OxQL.Core.Attributes;
using OxQL.Model.Attributes;

namespace OxQL.IntegrationTests.Fleet.Models.Fleet;

/// <summary>
/// The numeric entity: decimals stored as Decimal128 and as strings, zero, negative and many
/// places, an int at the edge of Int32, nested collections, a nested entity that is null or
/// names nothing, a storage name the derivation does not give, and the addon bag.
/// </summary>
[OxQLType("fleet.vehicle", "vehicle", Extendable = true)]
public class Vehicle
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? Location { get; set; }

    public string? MatchCode { get; set; }

    /// <summary>The status embedded whole: a member typed as another entity.</summary>
    public VehicleStatus? Status { get; set; }

    public decimal? Mileage { get; set; }

    public DateTime? MileageDate { get; set; }

    public decimal? OperatingHours { get; set; }

    public int? FuelTankCapacity { get; set; }

    public string? Remark { get; set; }

    public string? DispositionSortingKey { get; set; }

    public DateTime? YearOfManufacturing { get; set; }

    public string? RegistrationDocument { get; set; }

    public string? VehicleRegistration { get; set; }

    public TechnicalData? AdditionalTechnicalData { get; set; }

    public RegistrationPlate? RegistrationPlate { get; set; }

    public bool IsSystemVehicle { get; set; }

    public string? PhoneNumber { get; set; }

    public string? EMailAddress { get; set; }

    public Dictionary<string, object>? Addon { get; set; }

    public VehicleDepartment? Department { get; set; }

    public string? RegistrationDocumentLocation { get; set; }

    public List<Appointment>? Appointments { get; set; }

    public List<LoadingSlot>? LoadingSlots { get; set; }

    public DateTime CreateDateTime { get; set; }

    public DateTime UpdateDateTime { get; set; }

    public Guid CreateUserId { get; set; }

    public Guid? UpdateUserId { get; set; }

    public string? CreateUserName { get; set; }

    public string? UpdateUserName { get; set; }

    public DateTime? UsableUntil { get; set; }

    /// <summary>Stored as <c>QRCode</c>, which the storage-name derivation does not give.</summary>
    [BsonElement("QRCode")]
    public string? QrCode { get; set; }

    public string? ExternalReference { get; set; }
}

public class TechnicalData
{
    public decimal? EmptyWeight { get; set; }

    public decimal? TotalWeight { get; set; }

    public int? TireAmount { get; set; }

    public decimal? Payload { get; set; }

    public string? RimSizeAxle1 { get; set; }

    public string? RimSizeAxle2 { get; set; }

    public string? RimSizeAxle3 { get; set; }

    public string? FrameColor { get; set; }

    public string? VehicleExecution { get; set; }

    public bool HasFixedSuperstructure { get; set; }

    public int SuperstructureParkingSpaces { get; set; }
}

public class RegistrationPlate
{
    public string? CountryIso { get; set; }

    public bool IsSeasonal { get; set; }

    public string? RegistrationIdentifier { get; set; }

    public string? Remark { get; set; }
}

/// <summary>The department a vehicle belongs to, embedded as a subset.</summary>
public class VehicleDepartment
{
    [OxQLReference("fleet.department")]
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public bool IsSelectable { get; set; }

    public string? Color { get; set; }
}

public class Appointment
{
    public Guid Id { get; set; }

    public AppointmentType? AppointmentType { get; set; }

    public DateTime? LastDate { get; set; }

    public DateTime? NextDate { get; set; }

    public string? Remark { get; set; }

    public Guid? SupplierGuid { get; set; }

    public int? LastValue { get; set; }

    public int? NextValue { get; set; }

    public int CheckType { get; set; }
}

public class AppointmentType
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? DisplayName { get; set; }

    public string? DisplayKey { get; set; }

    public int Interval { get; set; }

    public string? IntervalType { get; set; }

    public DateTime CreateDateTime { get; set; }

    public DateTime UpdateDateTime { get; set; }

    public Guid CreateUserId { get; set; }

    public Guid? UpdateUserId { get; set; }

    public string? CreateUserName { get; set; }

    public string? UpdateUserName { get; set; }
}

public class LoadingSlot
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// Small and extendable without a bag; its <c>vehicle.id</c> names another entity of the same
/// service, the candidate for a local resolve.
/// </summary>
[OxQLType("fleet.equipment", "equipment", Extendable = true)]
public class Equipment
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? Name { get; set; }

    public string? Number { get; set; }

    public EquipmentType? EquipmentType { get; set; }

    public VehicleSubset? Vehicle { get; set; }

    /// <summary>The shipment the equipment travels with: a reference into transport, so a lookup continued there reaches this service (DESIGN §3.4.4).</summary>
    [OxQLReference("transport.shipment", "id")]
    public Guid? AssignedShipmentId { get; set; }

    public DateTime CreateDateTime { get; set; }

    public DateTime UpdateDateTime { get; set; }

    public Guid CreateUserId { get; set; }

    public Guid? UpdateUserId { get; set; }

    public string? CreateUserName { get; set; }

    public string? UpdateUserName { get; set; }
}

public class EquipmentType
{
    public Guid Id { get; set; }

    public string? DisplayName { get; set; }

    public string? DisplayKey { get; set; }
}

/// <summary>The vehicle an equipment is mounted on, embedded as a subset.</summary>
public class VehicleSubset
{
    [OxQLReference("fleet.vehicle")]
    public Guid Id { get; set; }

    public string? RegistrationPlate { get; set; }

    public string? MatchCode { get; set; }
}

/// <summary>A small lookup table, not extendable; the target of vehicle and shipment departments.</summary>
[OxQLType("fleet.department", "department")]
public class Department
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? Name { get; set; }

    public bool IsSelectable { get; set; }

    public string? Color { get; set; }
}

/// <summary>A small lookup table, not extendable; embedded whole in the vehicle.</summary>
[OxQLType("fleet.status", "status")]
public class VehicleStatus
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? Name { get; set; }

    public string? DisplayName { get; set; }

    public string? DisplayKey { get; set; }

    public string? HexColor { get; set; }

    public bool IsSelectable { get; set; }

    public DateTime CreateDateTime { get; set; }

    public DateTime UpdateDateTime { get; set; }

    public Guid CreateUserId { get; set; }

    public Guid? UpdateUserId { get; set; }

    public string? CreateUserName { get; set; }

    public string? UpdateUserName { get; set; }
}
