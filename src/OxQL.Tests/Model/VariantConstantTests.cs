using System.Text.Json;
using FluentAssertions;
using MongoDB.Bson.Serialization;
using OxQL.Model;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.Tests.Model;

/// <summary>
/// The model's own type member: a member of a polymorphic type that the driver does not store and
/// that is a constant of the class in every variant carries its value per variant
/// (<see cref="MemberDef.ByVariant"/>). The build reads the constant without running a constructor
/// and never fails for a class it cannot read; a schema document carries the values to a reader.
/// </summary>
public class VariantConstantTests
{
    private const string Entity = "probe.parcel";

    private static readonly Lazy<EntityModel> Built = new(() =>
    {
        VariantConstantRegistrations.Ensure();

        return ClrModelBuilder.Build([new EntityDeclaration(Entity, Entity, typeof(Parcel), "parcels", null, false)]);
    });

    private static TypeDef CarrierType => Built.Value.Entities[Entity].Root.Member("carrier")!.Type!;

    [Fact]
    public void A_constant_of_the_class_is_read_per_variant_without_running_a_constructor()
    {
        CarrierType.Member("type")!.ByVariant.Should().Equal(new Dictionary<string, string> { ["Courier"] = "courier", ["Drone"] = "drone", ["Throwing"] = "thrown" });
        Throwing.Constructed.Should().Be(0, "the getter is called on an instance no constructor ran for");
        CarrierType.Member("type")!.Stored.Should().BeFalse();
    }

    [Theory]
    [InlineData("Label", "the getter reads the instance")]
    [InlineData("Stamp", "the getter answers a different value each time")]
    [InlineData("Nothing", "the getter answers null")]
    [InlineData("Broken", "the getter cannot be read (InvalidOperationException)")]
    [InlineData("Settable", "the property has a setter")]
    [InlineData("Count", "the property is not a string")]
    [InlineData("Missing", "the class has no public property of that name")]
    public void What_is_no_constant_of_the_class_is_said_and_nothing_is_thrown(string property, string why)
    {
        VariantConstants.Read(typeof(Odd), property, out var value).Should().Be(why);
        value.Should().BeNull();
    }

    [Fact]
    public void The_instance_the_constant_is_read_from_is_never_finalized()
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Read()
        {
            VariantConstants.Read(typeof(Finalized), "Type", out var value).Should().BeNull();
            value.Should().Be("finalized");
        }

        Read();

        // Nothing holds the instance any more: a finalizer that could run would run now.
        for (var round = 0; round < 3; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Finalized.Finalizers.Should().Be(0, "no constructor ran for the instance, so its finalizer would read an object nothing initialised");
    }

    [Fact]
    public void A_constant_read_from_a_field_a_literal_and_a_static_call_is_a_constant()
    {
        VariantConstants.Read(typeof(Odd), "Literal", out var literal).Should().BeNull();
        literal.Should().Be("odd");
        VariantConstants.Read(typeof(Odd), "FromConstant", out var constant).Should().BeNull();
        constant.Should().Be("named");
        VariantConstants.Read(typeof(Odd), "FromStatic", out var computed).Should().BeNull();
        computed.Should().Be("ODD");
        VariantConstants.Read(typeof(Carrier), "Type", out _).Should().Be("the property has no getter of its own", "an abstract class answers nothing itself");
    }

    [Fact]
    public void A_member_that_is_a_constant_in_no_class_is_no_finding_and_one_that_is_in_some_is()
    {
        CarrierType.Member("summary")!.ByVariant.Should().BeNull();
        CarrierType.Member("code")!.ByVariant.Should().BeNull();

        var findings = Built.Value.Findings.Where(finding => finding.Code == BuildCodes.VariantConstantUnresolved).ToList();

        findings.Should().ContainSingle().Which.Target.Should().EndWith("#code");
        findings[0].Detail.Should().Be("Drone: the getter reads the instance");
    }

    [Fact]
    public void The_value_per_variant_is_in_the_fingerprint_and_survives_a_schema_document()
    {
        var path = Built.Value.Entities[Entity].Path("carrier.type")!;

        path.Derived!.Storage.Should().Be("Carrier._t");
        path.Stored.Should().BeFalse();
        path.Filterable.Should().BeFalse();

        var document = JsonDocument.Parse("""
            {
              "schemaVersion": "1.1",
              "namespace": "probe",
              "types": {
                "probe.parcel": { "entity": true, "properties": [
                  { "name": "id", "kind": "guid", "storageName": "_id" },
                  { "name": "carrier", "kind": "object", "type": "#/types/t_carrier", "nullable": true } ] },
                "t_carrier": {
                  "discriminator": { "element": "_t", "form": "scalar" },
                  "variants": [ { "name": "Courier", "type": "#/types/t_courier" }, { "name": "Drone", "type": "#/types/t_drone" } ],
                  "properties": [
                    { "name": "type", "kind": "string", "stored": false, "values": [ { "value": "courier" }, { "value": "drone" } ], "byVariant": { "Courier": "courier", "Drone": "drone" } },
                    { "name": "summary", "kind": "string", "stored": false } ] },
                "t_courier": { "properties": [ { "name": "type", "kind": "string", "stored": false } ] },
                "t_drone": { "properties": [ { "name": "type", "kind": "string", "stored": false } ] }
              }
            }
            """);
        var read = DocumentModelBuilder.Build(document.RootElement);
        var member = read.Entities[Entity].Path("carrier.type")!;

        member.Member.ByVariant.Should().Equal(new Dictionary<string, string> { ["Courier"] = "courier", ["Drone"] = "drone" });
        member.Derived!.Storage.Should().Be("Carrier._t");
        read.Entities[Entity].Path("carrier.summary")!.Derived.Should().BeNull();
    }

    // ---- fixture ---------------------------------------------------------------------------------

    public class Parcel
    {
        public Guid Id { get; set; }

        public Carrier? Carrier { get; set; }
    }

    public abstract class Carrier
    {
        public string Name { get; set; } = "";

        public abstract string Type { get; }

        /// <summary>Computed from the instance in every class: an ordinary computed member.</summary>
        public abstract string Summary { get; }

        /// <summary>A constant in two classes, read from the instance in the third.</summary>
        public abstract string Code { get; }
    }

    public class Courier : Carrier
    {
        public override string Type => "courier";

        public override string Summary => "courier " + Name;

        public override string Code => "C";
    }

    public class Drone : Carrier
    {
        public override string Type => "drone";

        public override string Summary => "drone " + Name;

        public override string Code => Name;
    }

    /// <summary>Its constructor throws and counts: the build never runs it.</summary>
    public class Throwing : Carrier
    {
        public static int Constructed;

        public Throwing()
        {
            Constructed++;

            throw new InvalidOperationException("a constructor with a side effect");
        }

        public override string Type => "thrown";

        public override string Summary => "thrown " + Name;

        public override string Code => "T";
    }

    /// <summary>Its finalizer counts: it never runs for the instance the build reads the constant from.</summary>
    public class Finalized
    {
        public static int Finalizers;

        public string Type => "finalized";

        ~Finalized() => System.Threading.Interlocked.Increment(ref Finalizers);
    }

    public class Odd
    {
        private const string Named = "named";
        private static int ticks;
        private readonly string label = "x";

        public string Label => label;

        public string Stamp => "t" + System.Threading.Interlocked.Increment(ref ticks);

        public string Nothing => null!;

        public string Broken => throw new InvalidOperationException("no");

        public string Settable { get; set; } = "";

        public int Count => 3;

        public string Literal => "odd";

        public string FromConstant => Named;

        public string FromStatic => "odd".ToUpperInvariant();
    }
}

/// <summary>The class maps of <see cref="VariantConstantTests"/>, registered before its model is built.</summary>
internal static class VariantConstantRegistrations
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        BsonClassMap.RegisterClassMap<VariantConstantTests.Courier>();
        BsonClassMap.RegisterClassMap<VariantConstantTests.Drone>();
        BsonClassMap.RegisterClassMap<VariantConstantTests.Throwing>();

        return true;
    });

    public static void Ensure() => _ = Registered.Value;
}
