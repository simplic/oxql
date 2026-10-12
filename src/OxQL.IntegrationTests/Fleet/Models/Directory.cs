using MongoDB.Driver.GeoJsonObjectModel;
using OxQL.Core.Attributes;

namespace OxQL.IntegrationTests.Fleet.Models.Directory;

/// <summary>
/// The contact of the directory service, mirroring the contact service's contact: the primary
/// e-mail address and phone number a report prints, and the address with its GeoJSON location,
/// a driver type the model cannot look into. What a transaction's contact address id names.
/// </summary>
[OxQLType("directory.contact", "contact", Extendable = true)]
public class Contact
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public ContactAddress Address { get; set; } = new();

    public DateTime CreateDateTime { get; set; }

    public Guid? CreateUserId { get; set; }

    public string? CreateUserName { get; set; }

    public DateTime UpdateDateTime { get; set; }

    public Guid? UpdateUserId { get; set; }

    public string? UpdateUserName { get; set; }

    public List<string> Functions { get; set; } = [];

    public string? MatchCode { get; set; }

    public EmailAddress? PrimaryEmailAddress { get; set; }

    public PhoneNumber? PrimaryPhoneNumber { get; set; }

    public List<EmailAddress> EmailAddresses { get; set; } = [];

    public List<PhoneNumber> PhoneNumbers { get; set; } = [];

    public string? ExternalReference { get; set; }

    public Dictionary<string, object>? Addon { get; set; }
}

public class ContactAddress
{
    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? CompanyName { get; set; }

    public string? Street { get; set; }

    public string? HouseNumber { get; set; }

    public string? Zipcode { get; set; }

    public string? City { get; set; }

    public string? CountryIso { get; set; }

    public string? Country { get; set; }

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public string? MatchCode { get; set; }

    /// <summary>The geocoded point, stored as GeoJSON by the driver.</summary>
    public GeoJsonPoint<GeoJson2DGeographicCoordinates>? Location { get; set; }
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
