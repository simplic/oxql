using OxQL.Core.Attributes;

namespace OxQL.IntegrationTests.Fleet.Models.Staff;

/// <summary>
/// The string entity: null, missing and empty strings, unicode and case, duplicate and null
/// sort keys, scalar and object collections of zero, one and several, and the addon bag.
/// </summary>
[OxQLType("staff.employee", "employee", Extendable = true)]
public class Employee
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public EmployeeAddress Address { get; set; } = new();

    public DateTime? Birthday { get; set; }

    public string? PlaceOfBirth { get; set; }

    public string? Religion { get; set; }

    public double? Children { get; set; }

    public string? Citizenship { get; set; }

    public bool SeverelyDisabled { get; set; }

    public int? DisabilityLevel { get; set; }

    public string? TaxOffice { get; set; }

    public string? SocialSecurityNumber { get; set; }

    public string? HealthInsurance { get; set; }

    public string? IdentityCardNumber { get; set; }

    public string? TaxIdentificationNumber { get; set; }

    public string? HealthInsuranceNumber { get; set; }

    public string? HandicappedIdNumber { get; set; }

    public string? IssuingAuthority { get; set; }

    public DateTime? ValidUntil { get; set; }

    public Employment Employment { get; set; } = new();

    public EmployeeGroup? Group { get; set; }

    public DateTime CreateDateTime { get; set; }

    public DateTime UpdateDateTime { get; set; }

    public Guid CreateUserId { get; set; }

    public Guid? UpdateUserId { get; set; }

    public string? CreateUserName { get; set; }

    public string? UpdateUserName { get; set; }

    public List<string> Functions { get; set; } = [];

    public List<EmailAddress> EmailAddresses { get; set; } = [];

    public List<PhoneNumber> PhoneNumbers { get; set; } = [];

    public EmailAddress? PrimaryEmailAddress { get; set; }

    public PhoneNumber? PrimaryPhoneNumber { get; set; }

    public string? MatchCode { get; set; }

    public string? ExternalReference { get; set; }

    public Dictionary<string, object>? Addon { get; set; }
}

public class EmployeeAddress
{
    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? CompanyName { get; set; }

    public string? Additional01 { get; set; }

    public string? Additional02 { get; set; }

    public string? Street { get; set; }

    public string? HouseNumber { get; set; }

    public string? Zipcode { get; set; }

    public string? City { get; set; }

    public string? District { get; set; }

    public string? FederalState { get; set; }

    public string? CountryIso { get; set; }

    public string? Country { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }
}

public class Employment
{
    public string? Number { get; set; }

    public bool IsActive { get; set; }

    public DateTime? EntryDate { get; set; }

    public DateTime? ExitDate { get; set; }
}

public class EmployeeGroup
{
    public Guid Id { get; set; }

    public string? DisplayName { get; set; }

    public string? DisplayKey { get; set; }

    public string? InternalName { get; set; }

    public string? HexColor { get; set; }

    public List<string> DefaultFunctions { get; set; } = [];
}

public class EmailAddress
{
    public string? Email { get; set; }

    public string? Type { get; set; }
}

public class PhoneNumber
{
    public string? Number { get; set; }

    public string? Type { get; set; }
}
