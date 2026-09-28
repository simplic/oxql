using System.Globalization;
using OxQL.IntegrationTests.Fleet;

namespace OxQL.IntegrationTests.Fixtures;

/// <summary>
/// The organisations of the corpus. <see cref="A"/> is the one every query runs as unless a test
/// says otherwise; <see cref="B"/> holds clones of A rows, so a scope that stops firing shows up
/// as extra rows rather than as nothing; <see cref="C"/> holds only the bulk volume rows, which
/// are seeded on demand (<see cref="CorpusFleet.SeedBulkAsync"/>); <see cref="R"/> holds only
/// the report scenario rows (<see cref="ReportSeed"/>), so no case over A or B sees them.
/// </summary>
public enum Org
{
    A,
    B,
    C,
    R,
}

/// <summary>The organisation ids and id tags.</summary>
public static class Orgs
{
    /// <summary>The bulk organisation: nothing but the 100 001 volume rows lives in it.</summary>
    public static readonly Guid OrganisationC = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>The report organisation: nothing but the report scenario rows live in it.</summary>
    public static readonly Guid OrganisationR = Guid.Parse("77777777-7777-7777-7777-777777777777");

    /// <summary>The organisation id.</summary>
    public static Guid Id(this Org org) => org switch
    {
        Org.A => LabIdentity.OrganisationA,
        Org.B => LabIdentity.OrganisationB,
        Org.C => OrganisationC,
        Org.R => OrganisationR,
        _ => throw new ArgumentOutOfRangeException(nameof(org)),
    };

    /// <summary>The four hex digits an id of this organisation carries in its second group.</summary>
    public static string Tag(this Org org) => org switch
    {
        Org.A => "0001",
        Org.B => "0002",
        Org.C => "0055",
        Org.R => "0077",
        _ => throw new ArgumentOutOfRangeException(nameof(org)),
    };
}

/// <summary>
/// The id spaces. Every id in the corpus is <c>TTTTTTTT-OOOO-4000-8000-NNNNNNNNNNNN</c>: the space,
/// the organisation tag, the RFC 4122 version and variant nibbles, and the ordinal in twelve
/// decimal digits. Derived, never random, so a test names a row and gets the id the seeder wrote.
/// <c>10*</c> is transport, <c>20*</c> fleet, <c>30*</c> staff, <c>40*</c> ledger, <c>50*</c> addon
/// definitions, <c>60*</c> conformance, <c>70*</c> directory; <c>*0001</c>–<c>*0009</c> are entity rows and <c>*01xx</c> the
/// embedded things they own. <see cref="Dangling"/> names nothing, on purpose.
/// </summary>
public static class Spaces
{
    public const string Dangling = "00000000";
    public const string Shipment = "10000001";
    public const string ShipmentTemplate = "10000002";
    public const string Tour = "10000003";
    public const string DeliveryAttempt = "10000004";
    public const string Resource = "10000005";
    public const string ShipmentItem = "10000101";
    public const string BillingLine = "10000102";
    public const string BillingLineReference = "10000103";
    public const string BillingLineCostCenter = "10000104";
    public const string ShipmentTag = "10000105";
    public const string ShipmentStatus = "10000106";
    public const string ShipmentItemStatus = "10000107";
    public const string ShipmentArticle = "10000109";
    public const string ShipmentLoadingAidType = "10000110";
    public const string QuantityUnit = "10000112";
    public const string Contact = "10000113";
    public const string ShipmentTourEntry = "10000114";
    public const string WeightNote = "10000115";
    public const string TourAction = "10000116";
    public const string AttachedResource = "10000117";
    public const string DeliveryAttemptStatus = "10000118";
    public const string Vehicle = "20000001";
    public const string Equipment = "20000002";
    public const string Department = "20000003";
    public const string VehicleStatus = "20000004";
    public const string VehicleAppointment = "20000101";
    public const string AppointmentType = "20000102";
    public const string LoadingSlot = "20000103";
    public const string Employee = "30000001";
    public const string EmployeeGroup = "30000101";
    public const string UserAccount = "30000102";
    public const string Transaction = "40000001";
    public const string LedgerBillingLine = "40000002";
    public const string TransactionItem = "40000101";
    public const string TransactionType = "40000102";
    public const string TransactionItemType = "40000103";
    public const string Currency = "40000104";
    public const string LedgerLineReference = "40000105";
    public const string TransactionContact = "40000106";
    public const string TermsOfPayment = "40000107";
    public const string AddonDefinition = "50000001";
    public const string ConformanceEntity = "60000001";
    public const string ConformanceChild = "60000002";
    public const string ConformanceItem = "60000003";
    public const string ConformanceQuantityKey = "60000004";
    public const string DirectoryContact = "70000001";
}

/// <summary>Builds corpus ids.</summary>
public static class Ids
{
    /// <summary>One id: <paramref name="space"/> (one of <see cref="Spaces"/>), the organisation, the ordinal.</summary>
    public static Guid Of(string space, Org org, long n)
    {
        if (space.Length != 8 || !space.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException($"'{space}' is not an id space.", nameof(space));

        if (n is < 0 or > 999_999_999_999)
            throw new ArgumentOutOfRangeException(nameof(n), n, "The ordinal takes twelve decimal digits.");

        return Guid.Parse($"{space}-{org.Tag()}-4000-8000-{n.ToString("D12", CultureInfo.InvariantCulture)}");
    }

    /// <summary>An id that names nothing, whatever it is compared against.</summary>
    public static Guid Dangling(long n = 1, Org org = Org.A) => Of(Spaces.Dangling, org, n);

    /// <summary>The ordinal an id carries in its last twelve digits.</summary>
    public static long OrdinalOf(Guid id) => long.Parse(id.ToString("D")[^12..], CultureInfo.InvariantCulture);

    /// <summary>The wire spelling of a Guid: lower-case, hyphenated.</summary>
    public static string Wire(this Guid id) => id.ToString("D");
}
