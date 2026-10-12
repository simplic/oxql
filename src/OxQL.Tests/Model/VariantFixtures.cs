// The graph the variant tests bind, compile and encode over: a scoped entity whose members hold
// polymorphic values (a hierarchical abstract base, a concrete scalar base), collections of them,
// and self-similar trees for unwind.flatten. Its class maps are registered by ModelTestSetup, once,
// before any build looks them up.

using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using OxQL.Model;
using OxQL.Model.Build;

namespace OxQL.Tests.Model.Fixtures.Variants;

/// <summary>The entity: every place a variant test or a flatten can stand.</summary>
public class Kennel
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public Pet? Pet { get; set; }
    public List<Pet> Pets { get; set; } = [];
    public Vehicle? Car { get; set; }
    public List<Vehicle> Fleet { get; set; } = [];
    public List<Stall> Stalls { get; set; } = [];
    public List<Node> Nodes { get; set; } = [];
    public List<Block> Blocks { get; set; } = [];
}

/// <summary>A hierarchical abstract base: every value carries the discriminators from the root down.</summary>
[BsonDiscriminator(RootClass = true)]
public abstract class Pet
{
    public string? Name { get; set; }
}

public class Cat : Pet
{
    public bool Indoor { get; set; }

    /// <summary>Stored under another name than <see cref="Hound.Tag"/>: the merged member is unstored, the variant's is not.</summary>
    [BsonElement("ct")]
    public string Tag { get; set; } = "";
}

public class Hound : Pet
{
    public bool Barks { get; set; }

    [BsonElement("ht")]
    public string Tag { get; set; } = "";
}

/// <summary>A descendant of a variant with a discriminator of its own.</summary>
[BsonDiscriminator("beagle")]
public class Beagle : Hound
{
    public int Weeks { get; set; }
}

/// <summary>A concrete base with the scalar discriminator: its own values are stored without one.</summary>
public class Vehicle
{
    public string Plate { get; set; } = "";
}

public class Truck : Vehicle
{
    public int Axles { get; set; }
}

/// <summary>A collection element holding a polymorphic member.</summary>
public class Stall
{
    public Pet? Occupant { get; set; }
}

/// <summary>A self-similar tree of one type.</summary>
public class Node
{
    public string Label { get; set; } = "";
    public List<Node> Children { get; set; } = [];
    public List<string> Tags { get; set; } = [];
}

/// <summary>A self-similar tree through a variant: only a section nests blocks, and it nests the base.</summary>
public abstract class Block
{
    public string Title { get; set; } = "";
}

public class Section : Block
{
    public List<Block> Blocks { get; set; } = [];
}

public class Paragraph : Block
{
    public string Text { get; set; } = "";
}

/// <summary>Registers the fixtures' class maps once per process, before any build looks one up.</summary>
internal static class VariantRegistrations
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        BsonClassMap.RegisterClassMap<Cat>();
        BsonClassMap.RegisterClassMap<Hound>();
        BsonClassMap.RegisterClassMap<Beagle>();
        BsonClassMap.RegisterClassMap<Truck>();
        BsonClassMap.RegisterClassMap<Section>();
        BsonClassMap.RegisterClassMap<Paragraph>();

        return true;
    });

    public static void Ensure() => _ = Registered.Value;
}

/// <summary>The model over <see cref="Kennel"/>.</summary>
internal static class VariantModel
{
    public const string Kennel = "variant.kennel";

    private static readonly Lazy<EntityModel> Built = new(() =>
    {
        VariantRegistrations.Ensure();

        return ClrModelBuilder.Build([new EntityDeclaration(Kennel, Kennel, typeof(Fixtures.Variants.Kennel), "kennels", null, false)]);
    });

    public static EntityModel Model => Built.Value;
}
