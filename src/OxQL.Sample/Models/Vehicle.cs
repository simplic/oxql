using OxQL.Core.Attributes;

namespace OxQL.Sample.Models;

/// <summary>
/// Base class for all vehicle documents.
/// The [OxQLType] attribute registers this type with OxQL at startup and maps it
/// to the "vehicle" collection inside the "vehicle.vehicle" namespace.
/// </summary>
[OxQLType("vehicle.vehicle")]
public class VehicleBase
{
}

/// <summary>
/// Concrete vehicle document model.
/// Inherits <see cref="VehicleBase"/> so the [OxQLType] attribute is picked up
/// when the assembly is scanned at startup.
/// </summary>
public class Vehicle : VehicleBase
{
    public string? MatchCode          { get; set; }
    public string? RegistrationPlate  { get; set; }
    public Guid    OrganizationId     { get; set; }

    public IList<VehicleAppointment> Appointments { get; set; }
}

public class VehicleAppointment 
{
public DateTime NextDate { get; set; }
}
