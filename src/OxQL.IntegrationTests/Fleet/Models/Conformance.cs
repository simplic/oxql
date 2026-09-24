using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;
using OxQL.Core.Attributes;
using OxQL.Model.Attributes;

namespace OxQL.IntegrationTests.Fleet.Models.Conformance;

/// <summary>
/// Every kind the model can represent, each in a non-nullable and a nullable form where that
/// means something, and every reference direction: the parts of the contract the fleet's
/// business-shaped entities cannot reach (a long above 2^53, a calendar date, binary, a
/// long-backed enum, dictionaries in both representations, unstored members).
/// </summary>
[OxQLType("conformance.entity", "conformance", Extendable = true)]
public class ConformanceEntity
{
    public Guid Id { get; set; }

    /// <summary>A stored root Guid under this name is what gives the engine its scope stage.</summary>
    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    /// <summary>The display member.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Note { get; set; }

    public int Count { get; set; }

    public int? OptionalCount { get; set; }

    /// <summary>Seeded above 2^53: exact as a long, lossy through a JavaScript number.</summary>
    public long Magnitude { get; set; }

    public long? OptionalMagnitude { get; set; }

    public decimal Amount { get; set; }

    public decimal? OptionalAmount { get; set; }

    public double Ratio { get; set; }

    public double? OptionalRatio { get; set; }

    public bool Active { get; set; }

    public bool? OptionalActive { get; set; }

    public Guid Marker { get; set; }

    public Guid? OptionalMarker { get; set; }

    public DateTime Moment { get; set; }

    public DateTime? OptionalMoment { get; set; }

    public DateOnly Day { get; set; }

    public DateOnly? OptionalDay { get; set; }

    public TimeSpan Duration { get; set; }

    public TimeSpan? OptionalDuration { get; set; }

    /// <summary>Model kind string; the driver stores a char as Int32.</summary>
    public char Grade { get; set; }

    public char? OptionalGrade { get; set; }

    public byte[] Payload { get; set; } = [];

    public byte[]? OptionalPayload { get; set; }

    public ConformanceState State { get; set; }

    public ConformanceState? OptionalState { get; set; }

    public ConformanceAddress Address { get; set; } = new();

    public ConformanceAddress? OptionalAddress { get; set; }

    public List<string> Tags { get; set; } = [];

    /// <summary>A collection of objects, each carrying a collection of its own.</summary>
    public List<ConformanceItem> Items { get; set; } = [];

    /// <summary>String keys: the driver stores the dictionary as a document.</summary>
    public Dictionary<string, string> Labels { get; set; } = [];

    /// <summary>Guid keys: the driver stores the dictionary as an array of key/value documents.</summary>
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<Guid, int> Quantities { get; set; } = [];

    /// <summary>No describable shape: projectable, never filterable.</summary>
    public object? Opaque { get; set; }

    /// <summary>In the wire view, never stored.</summary>
    [BsonIgnore]
    public string? Scratch { get; set; }

    /// <summary>Get-only and computed: in the wire view, never stored.</summary>
    public string Computed => Name + "/" + Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A local reference onto an entity keyed on <c>code</c>, not <c>id</c>.</summary>
    [OxQLReference("conformance.ref")]
    public string? RefCode { get; set; }

    /// <summary>A local reference onto an entity keyed on <c>id</c>.</summary>
    [OxQLReference("conformance.child")]
    public Guid? ChildId { get; set; }

    /// <summary>A remote reference onto another lab service, with the target field declared.</summary>
    [OxQLReference("staff.employee", "id")]
    public Guid? EmployeeId { get; set; }

    /// <summary>
    /// A remote reference without a target field: the host cannot read a remote key, so the
    /// model reports a finding and emits no reference. Keep it fieldless.
    /// </summary>
    [OxQLReference("owner.widget")]
    public string? WidgetCode { get; set; }

    /// <summary>The same remote reference with the field declared: the control for <see cref="WidgetCode"/>.</summary>
    [OxQLReference("owner.widget", "code")]
    public string? WidgetCodeExplicit { get; set; }

    /// <summary>The addon bag: only a root member named <c>addon</c> on an extendable entity is one.</summary>
    public Dictionary<string, object>? Addon { get; set; } = [];
}

/// <summary>Long-backed, with a retired member and a value above 2^53.</summary>
public enum ConformanceState : long
{
    Draft = 0,

    Active = 1,

    [Obsolete("Retired; kept so the model carries an inactive enum value.")]
    Legacy = 2,

    /// <summary>Exact as a long, lossy through a JavaScript number.</summary>
    Huge = 9007199254740993L,
}

public class ConformanceAddress
{
    public string City { get; set; } = string.Empty;

    public string? Zip { get; set; }

    public int Floor { get; set; }
}

public class ConformanceItem
{
    /// <summary>Stored as <c>_id</c> at depth: the id convention applies to nested class maps too.</summary>
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public List<ConformancePart> Parts { get; set; } = [];
}

public class ConformancePart
{
    public string Sku { get; set; } = string.Empty;

    public decimal Weight { get; set; }
}

/// <summary>
/// The local resolve target keyed on <c>code</c>: a local reference reads the target's real
/// key, where a remote one has to be told.
/// </summary>
[OxQLType("conformance.ref", "conformance_ref")]
public class ConformanceRef
{
    /// <summary>Stored under <c>_id</c>; the wire name stays <c>code</c>.</summary>
    [BsonId]
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Rank { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }
}

/// <summary>The lookup child: its backward reference onto the entity is what makes a <c>lookup</c> legal.</summary>
[OxQLType("conformance.child", "conformance_child")]
public class ConformanceChild
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    [OxQLReference("conformance.entity")]
    public Guid ParentId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Sequence { get; set; }
}
