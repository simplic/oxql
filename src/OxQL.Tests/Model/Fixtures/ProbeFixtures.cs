// The fixture entity graph every OxQL.Model test builds from. It covers every kind and
// structure the schema knows, every registry fact the Stage 0 spike measured, and every
// discovery rule: one type per fact, named after it. `Fixtures/schemas/probe.json` is the
// schema document describing the same graph.

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Bson.Serialization.Serializers;
using OxQL.Core.Attributes;
using OxQL.Model.Attributes;

namespace OxQL.Tests.Model.Fixtures;

/// <summary>
/// Stands in for the base package's navigation-property attribute, which the model reads by
/// simple name and property name so it needs no reference to the base package.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ReferenceIdAttribute(string referenceIdPropertyName) : Attribute
{
    public string ReferenceIdPropertyName => referenceIdPropertyName;
}

public abstract class Keyed
{
    public Guid Id { get; set; }
    public DateTime CreateDateTime { get; set; }
}

public enum OrderState
{
    Open = 0,
    Shipped = 1,
    [Obsolete] Legacy = 2,
}

[Flags]
public enum OrderOptions
{
    None = 0,
    Express = 1,
    Insured = 2,
}

public enum WideState : long
{
    Small = 1,
    Huge = 5_000_000_000,
}

/// <summary>The main fixture: every scalar, every composite, every attribute the registry observes.</summary>
[OxQLType("probe.order", "orders", Extendable = true)]
public class OrderModel : Keyed
{
    public string Number { get; set; } = "";
    public int Count { get; set; }
    public long Big { get; set; }
    public double Ratio { get; set; }
    public decimal Amount { get; set; }
    public bool Flag { get; set; }
    public DateOnly Day { get; set; }
    public DateTime When { get; set; }
    public DateTimeOffset Stamp { get; set; }
    public TimeSpan Span { get; set; }
    public TimeOnly Tod { get; set; }
    public byte[] Blob { get; set; } = [];
    public char Initial { get; set; }
    public Uri? Link { get; set; }
    public object? Anything { get; set; }
    public OrderState State { get; set; }
    public OrderState? MaybeState { get; set; }
    public OrderOptions Options { get; set; }
    public WideState Wide { get; set; }
    public int? MaybeCount { get; set; }
    public string? Note { get; set; }
    public byte Small { get; set; }
    public uint Unsigned { get; set; }
    public float Single { get; set; }
    public Address? ShipTo { get; set; }
    public List<OrderItem> Items { get; set; } = [];
    public string[] Tags { get; set; } = [];
    public ICollection<Guid> RelatedIds { get; set; } = [];
    public Dictionary<string, object> Addon { get; set; } = [];
    public Dictionary<string, Money> Prices { get; set; } = [];

    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<string, Money> PriceList { get; set; } = [];

    public Dictionary<int, string> ByNumber { get; set; } = [];

    [BsonIgnore]
    public string Hidden { get; set; } = "";

    [BsonElement("x")]
    public string Renamed { get; set; } = "";

    [BsonRepresentation(BsonType.String)]
    public decimal MoneyText { get; set; }

    [BsonRepresentation(BsonType.String)]
    public Guid GuidText { get; set; }

    [BsonRepresentation(BsonType.String)]
    public OrderState StateName { get; set; }

    public string Computed => Number + "!";

    [OxQLReference("probe.customer")]
    public Guid CustomerId { get; set; }

    [OxQLReference("crm.contact", "number")]
    public string? ContactNumber { get; set; }

    [OxQLReference("probe.missing")]
    public Guid MissingId { get; set; }

    [ReferenceId("SupplierId")]
    public SupplierModel? Supplier { get; set; }

    public Guid SupplierId { get; set; }

    [ReferenceId("Nope")]
    public SupplierModel? Broken { get; set; }

    [ReferenceId("SupplierId")]
    public Address? NotAnEntity { get; set; }

    public GeoPoint? Location { get; set; }
    public Node? Tree { get; set; }
    public Figure? Figure { get; set; }
    public CustomerModel? Customer { get; set; }
    public string QRCode { get; set; } = "";
    public List<List<int>> Matrix { get; set; } = [];
}

public class Address
{
    public string? Street { get; set; }
    public string? City { get; set; }
    public double? Latitude { get; set; }
}

public class OrderItem
{
    public Guid Id { get; set; }
    public int Quantity { get; set; }
    public Money? Price { get; set; }
    public List<string> Notes { get; set; } = [];
    public List<Address> Stops { get; set; } = [];
}

public class Money
{
    public decimal Net { get; set; }
    public string Currency { get; set; } = "EUR";
}

/// <summary>A type that reaches itself directly and through a collection.</summary>
public class Node
{
    public string? Name { get; set; }
    public Node? Next { get; set; }
    public List<Node> Children { get; set; } = [];
}

/// <summary>A polymorphic base: the driver exposes its own members only, and its get-only member is not stored.</summary>
public abstract class Figure
{
    public Guid Id { get; set; }
    public string? Label { get; set; }
    public abstract string FigureKind { get; }
}

public class Circle : Figure
{
    public double Radius { get; set; }
    public override string FigureKind => "circle";
}

public class Square : Figure
{
    public double Side { get; set; }
    public override string FigureKind => "square";
}

/// <summary>A type with a serializer that is not a document serializer: the model cannot describe it.</summary>
[BsonSerializer(typeof(GeoPointSerializer))]
public class GeoPoint
{
    public double Lat { get; set; }
    public double Lng { get; set; }
}

public class GeoPointSerializer : SerializerBase<GeoPoint>
{
    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, GeoPoint value)
    {
        if (value is null)
        {
            context.Writer.WriteNull();
            return;
        }

        context.Writer.WriteStartArray();
        context.Writer.WriteDouble(value.Lng);
        context.Writer.WriteDouble(value.Lat);
        context.Writer.WriteEndArray();
    }

    public override GeoPoint Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        context.Reader.ReadStartArray();
        var lng = context.Reader.ReadDouble();
        var lat = context.Reader.ReadDouble();
        context.Reader.ReadEndArray();

        return new GeoPoint { Lat = lat, Lng = lng };
    }
}

[OxQLType("probe.customer", "customers")]
public class CustomerModel : Keyed
{
    public string Name { get; set; } = "";
    public string MatchCode { get; set; } = "";
    public string? Number { get; set; }
}

[OxQLType("probe.supplier", "suppliers", "purchasing")]
public class SupplierModel : Keyed
{
    public string? MatchCode { get; set; }
}

/// <summary>Two declarations of one id, differing only in case: both are dropped.</summary>
[OxQLType("probe.twin", "twins")]
public class TwinA
{
    public Guid Id { get; set; }
}

[OxQLType("Probe.Twin", "twins")]
public class TwinB
{
    public Guid Id { get; set; }
}

/// <summary>A declaration on an abstract base: the most derived concrete subclass is the entity's type.</summary>
[OxQLType("probe.base_only", "bases")]
public abstract class BaseOnly
{
    public Guid Id { get; set; }
    public string? Common { get; set; }
}

public class DerivedOnly : BaseOnly
{
    public string? Extra { get; set; }
}

public class MoreDerived : DerivedOnly
{
    public string? Deepest { get; set; }
}

/// <summary>Two ids whose most derived type is the same class: the second id is dropped.</summary>
[OxQLType("probe.shared_base", "shared")]
public class SharedBase
{
    public Guid Id { get; set; }
}

[OxQLType("probe.shared_derived", "shared")]
public class SharedDerived : SharedBase
{
    public string? More { get; set; }
}

/// <summary>An entity with no member the driver stores under <c>_id</c>.</summary>
[OxQLType("probe.keyless", "keyless")]
public class KeylessModel
{
    public string? Name { get; set; }
}

/// <summary>
/// Walked by the freeze test only, through a hand-made declaration rather than the attribute,
/// so no other test looks it up first and the test controls the registration order.
/// </summary>
public class FrozenModel
{
    public Guid Id { get; set; }
    public FrozenPart? Part { get; set; }
}

public class FrozenPart
{
    public string? A { get; set; }
}
