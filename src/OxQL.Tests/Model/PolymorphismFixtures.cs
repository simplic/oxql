// The polymorphic graphs the polymorphism tests build from. None carries [OxQLType]: the probe
// build scans this whole assembly, so these are declared by hand (or emitted into an assembly of
// their own for the scan). Their class maps are registered once, before any build looks them up,
// the way a host registers its maps before the schema startup filter builds the model.

using System.Reflection;
using System.Reflection.Emit;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Conventions;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;

namespace OxQL.Tests.Model.Fixtures.Polymorphism;

/// <summary>An entity whose members hold an abstract base: two registered variants, one unregistered subclass.</summary>
public class Ledger
{
    public Guid Id { get; set; }
    public List<Line> Lines { get; set; } = [];
    public Line? Head { get; set; }
}

public abstract class Line
{
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
}

public class BillingLine : Line
{
    [OxQLReference("poly.billing_line")]
    public Guid BillingLineId { get; set; }

    [System.ComponentModel.Description("The billing line's note.")]
    public string Note { get; set; } = "";
}

/// <summary>A variant nesting its own base: the merge must not depend on which one is walked first.</summary>
public class GroupLine : Line
{
    public string Note { get; set; } = "";
    public List<Line> Children { get; set; } = [];
}

/// <summary>A concrete subclass the host never registers.</summary>
public class UnregisteredLine : Line
{
    public string? Secret { get; set; }
}

/// <summary>The target of <see cref="BillingLine.BillingLineId"/>.</summary>
public class BillingLineEntry
{
    public Guid Id { get; set; }
}

/// <summary>Variants disagreeing on a member's kind and on another's storage name; its own discriminator element.</summary>
public class TagHolder
{
    public Guid Id { get; set; }
    public Tagged? Tagged { get; set; }
}

public abstract class Tagged
{
    public Guid Id { get; set; }
}

public class TagText : Tagged
{
    public string Code { get; set; } = "";

    [BsonElement("x")]
    public string Tag { get; set; } = "";
}

public class TagNumber : Tagged
{
    public int Code { get; set; }

    [BsonElement("y")]
    public string Tag { get; set; } = "";
}

/// <summary>Members typed as interfaces: one with registered implementations, one without.</summary>
public class Assignment
{
    public Guid Id { get; set; }
    public IResource? Resource { get; set; }
    public List<IResource> Pool { get; set; } = [];
    public ILoose? Loose { get; set; }
}

public interface IResource
{
    string? Name { get; }
}

public class DriverResource : IResource
{
    public string? Name { get; set; }
    public Guid EmployeeId { get; set; }
}

public class TruckResource : IResource
{
    public string? Name { get; set; }
    public string Plate { get; set; } = "";
}

public interface ILoose
{
    string? Name { get; }
}

public class LooseImplementation : ILoose
{
    public string? Name { get; set; }
}

/// <summary>A hierarchical discriminator, a custom discriminator value, and a variant found through <c>[BsonKnownTypes]</c> only.</summary>
public class Zoo
{
    public Guid Id { get; set; }
    public Animal? Animal { get; set; }
    public KnownShape? Shape { get; set; }
}

[BsonDiscriminator(RootClass = true)]
public abstract class Animal
{
    public string? Name { get; set; }
}

public class Dog : Animal
{
    public bool Barks { get; set; }
}

[BsonDiscriminator("pup")]
public class Puppy : Dog
{
    public int Weeks { get; set; }
}

[BsonKnownTypes(typeof(KnownCircle))]
public abstract class KnownShape
{
    public string? Label { get; set; }
}

public class KnownCircle : KnownShape
{
    public double Radius { get; set; }
}

/// <summary>Registers the fixtures' class maps once per process, before any build looks one up.</summary>
internal static class PolymorphismRegistrations
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        BsonSerializer.RegisterDiscriminatorConvention(typeof(Tagged), new ScalarDiscriminatorConvention("kind"));

        Register<BillingLine>();
        Register<GroupLine>();
        Register<TagText>();
        Register<TagNumber>();
        Register<DriverResource>();
        Register<TruckResource>();
        Register<Dog>();
        Register<Puppy>();

        foreach (var type in EmittedEntities.Registered)
            BsonClassMap.RegisterClassMap(AutoMapped(type));

        return true;
    });

    public static void Ensure() => _ = Registered.Value;

    private static void Register<T>() => BsonClassMap.RegisterClassMap<T>();

    private static BsonClassMap AutoMapped(Type type)
    {
        var map = new BsonClassMap(type);

        map.AutoMap();

        return map;
    }
}

/// <summary>The hand-made declarations of the fixture entities.</summary>
internal static class PolymorphismModel
{
    public const string Ledger = "poly.ledger";
    public const string TagHolder = "poly.tag_holder";
    public const string Assignment = "poly.assignment";
    public const string Zoo = "poly.zoo";

    public static EntityModel Build()
    {
        PolymorphismRegistrations.Ensure();

        return ClrModelBuilder.Build(
        [
            new EntityDeclaration(Ledger, Ledger, typeof(Fixtures.Polymorphism.Ledger), "ledgers", null, false),
            new EntityDeclaration("poly.billing_line", "poly.billing_line", typeof(BillingLineEntry), "billing_lines", null, false),
            new EntityDeclaration(TagHolder, TagHolder, typeof(Fixtures.Polymorphism.TagHolder), "tag_holders", null, false),
            new EntityDeclaration(Assignment, Assignment, typeof(Fixtures.Polymorphism.Assignment), "assignments", null, false),
            new EntityDeclaration(Zoo, Zoo, typeof(Fixtures.Polymorphism.Zoo), "zoos", null, false),
        ]);
    }
}

/// <summary>
/// Entities declared through <c>[OxQLType]</c> in an assembly emitted at run time, so the scan can
/// be exercised without adding entities to the probe assembly: a declared base with registered
/// subclass maps (it stays the root), and one without (the most derived subclass stands in).
/// </summary>
internal static class EmittedEntities
{
    public static readonly Assembly Assembly;
    public static readonly Type Resource;
    public static readonly Type DriverResource;
    public static readonly Type VehicleResource;
    public static readonly Type FallbackBase;
    public static readonly Type FallbackDerived;

    static EmittedEntities()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OxQL.Tests.Emitted.Polymorphism"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("OxQL.Tests.Emitted.Polymorphism");

        Resource = Define(module, "Emitted.Resource", null, ("poly.resource", "resources"), ("Id", typeof(Guid)), ("Name", typeof(string)));
        DriverResource = Define(module, "Emitted.DriverResource", Resource, null, ("EmployeeId", typeof(Guid)));
        VehicleResource = Define(module, "Emitted.VehicleResource", Resource, null, ("VehicleId", typeof(Guid)), ("Name2", typeof(string)));
        FallbackBase = Define(module, "Emitted.FallbackBase", null, ("poly.fallback", "fallbacks"), ("Id", typeof(Guid)));
        FallbackDerived = Define(module, "Emitted.FallbackDerived", FallbackBase, null, ("Extra", typeof(string)));
        Assembly = assembly;
    }

    /// <summary>The emitted classes the host registers: the resource variants, never the fallback's subclass.</summary>
    public static IEnumerable<Type> Registered => [DriverResource, VehicleResource];

    private static Type Define(ModuleBuilder module, string name, Type? parent, (string Id, string Collection)? entity, params (string Name, Type Type)[] properties)
    {
        var type = module.DefineType(name, TypeAttributes.Public | TypeAttributes.Class, parent ?? typeof(object));

        type.DefineDefaultConstructor(MethodAttributes.Public);

        if (entity is { } declared)
            type.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(OxQL.Core.Attributes.OxQLTypeAttribute).GetConstructor([typeof(string), typeof(string), typeof(string)])!,
                [declared.Id, declared.Collection, null]));

        foreach (var (propertyName, propertyType) in properties)
        {
            var field = type.DefineField("_" + propertyName, propertyType, FieldAttributes.Private);
            var property = type.DefineProperty(propertyName, PropertyAttributes.None, propertyType, null);
            const MethodAttributes accessor = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;

            var getter = type.DefineMethod("get_" + propertyName, accessor, propertyType, Type.EmptyTypes);
            var get = getter.GetILGenerator();

            get.Emit(OpCodes.Ldarg_0);
            get.Emit(OpCodes.Ldfld, field);
            get.Emit(OpCodes.Ret);

            var setter = type.DefineMethod("set_" + propertyName, accessor, null, [propertyType]);
            var set = setter.GetILGenerator();

            set.Emit(OpCodes.Ldarg_0);
            set.Emit(OpCodes.Ldarg_1);
            set.Emit(OpCodes.Stfld, field);
            set.Emit(OpCodes.Ret);

            property.SetGetMethod(getter);
            property.SetSetMethod(setter);
        }

        return type.CreateType();
    }
}
