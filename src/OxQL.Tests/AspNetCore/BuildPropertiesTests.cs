using System.Reflection;
using FluentAssertions;
using OxQL.AspNetCore.Controllers;
using OxQL.AspNetCore.Models;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// Tests for OxQLController.BuildProperties — the private static helper that
/// reflects CLR types into <see cref="OxQLPropertyDescriptor"/> trees.
/// Verified concerns:
///   - scalar kinds are mapped correctly
///   - nested objects recurse and emit "object" descriptors
///   - reference cycles do not cause a stack-overflow or infinite output
///   - output depth never exceeds MaxPropertyDepth (currently 8)
///   - collection element types are reflected
///   - dictionary key/value types are reflected
///   - nullable value types are unwrapped and marked nullable
/// </summary>
public class BuildPropertiesTests
{
    // ── reflection plumbing ───────────────────────────────────────────────────

    private static readonly MethodInfo _buildProperties =
        typeof(OxQLController)
            .GetMethod("BuildProperties",
                BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly int MaxDepth =
        (int)typeof(OxQLController)
            .GetField("MaxPropertyDepth",
                BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static IReadOnlyList<OxQLPropertyDescriptor> BuildProperties(Type type)
        => (IReadOnlyList<OxQLPropertyDescriptor>)_buildProperties
            .Invoke(null, [type, null, 0])!;

    // ── fixture types ─────────────────────────────────────────────────────────

    private class Flat
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public bool Active { get; set; }
        public Guid Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public decimal Amount { get; set; }
    }

    private class WithNullable
    {
        public int? OptionalInt { get; set; }
        public Guid? OptionalGuid { get; set; }
        public string? OptionalString { get; set; }
    }

    private class Child
    {
        public string Label { get; set; } = "";
    }

    private class Parent
    {
        public string Name { get; set; } = "";
        public Child Child { get; set; } = new();
    }

    // Self-referencing type — the cycle guard must prevent infinite recursion
    private class Node
    {
        public string Value { get; set; } = "";
        public Node? Next { get; set; }
    }

    // Mutually-recursive types
    private class Left
    {
        public string L { get; set; } = "";
        public Right? R { get; set; }
    }

    private class Right
    {
        public string R { get; set; } = "";
        public Left? L { get; set; }
    }

    // Deeply nested (deeper than MaxPropertyDepth)
    private class D8 { public string V { get; set; } = ""; }
    private class D7 { public D8 Next { get; set; } = new(); }
    private class D6 { public D7 Next { get; set; } = new(); }
    private class D5 { public D6 Next { get; set; } = new(); }
    private class D4 { public D5 Next { get; set; } = new(); }
    private class D3 { public D4 Next { get; set; } = new(); }
    private class D2 { public D3 Next { get; set; } = new(); }
    private class D1 { public D2 Next { get; set; } = new(); }
    private class D0 { public D1 Next { get; set; } = new(); }

    private class WithList
    {
        public List<string> Tags { get; set; } = [];
        public string[] Names { get; set; } = [];
    }

    private class WithDictionary
    {
        public Dictionary<string, int> Scores { get; set; } = [];
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static int MaxDepthOf(IReadOnlyList<OxQLPropertyDescriptor> props)
    {
        int Max(OxQLPropertyDescriptor d, int current)
        {
            int m = current;
            if (d.Properties is { Count: > 0 })
                m = d.Properties.Max(p => Max(p, current + 1));
            if (d.Items is not null)
                m = Math.Max(m, Max(d.Items, current + 1));
            return m;
        }
        return props.Count == 0 ? 0 : props.Max(p => Max(p, 1));
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Flat_scalars_are_mapped_to_correct_kinds()
    {
        var props = BuildProperties(typeof(Flat))
            .ToDictionary(p => p.Name);

        props["Name"].Kind.Should().Be("string");
        props["Count"].Kind.Should().Be("number");
        props["Active"].Kind.Should().Be("boolean");
        props["Id"].Kind.Should().Be("Guid");
        props["CreatedAt"].Kind.Should().Be("DateTime");
        props["UpdatedAt"].Kind.Should().Be("DateTimeOffset");
        props["Amount"].Kind.Should().Be("number");
    }

    [Fact]
    public void Value_type_scalars_are_not_nullable()
    {
        var props = BuildProperties(typeof(Flat))
            .ToDictionary(p => p.Name);

        props["Count"].Nullable.Should().BeFalse();
        props["Active"].Nullable.Should().BeFalse();
        props["Id"].Nullable.Should().BeFalse();
    }

    [Fact]
    public void Nullable_value_types_are_marked_nullable()
    {
        var props = BuildProperties(typeof(WithNullable))
            .ToDictionary(p => p.Name);

        props["OptionalInt"].Nullable.Should().BeTrue();
        props["OptionalInt"].Kind.Should().Be("number");
        props["OptionalGuid"].Nullable.Should().BeTrue();
        props["OptionalGuid"].Kind.Should().Be("Guid");
    }

    [Fact]
    public void Reference_type_properties_are_nullable()
    {
        var props = BuildProperties(typeof(Flat))
            .ToDictionary(p => p.Name);

        props["Name"].Nullable.Should().BeTrue();
    }

    [Fact]
    public void Nested_object_produces_object_descriptor_with_child_properties()
    {
        var props = BuildProperties(typeof(Parent))
            .ToDictionary(p => p.Name);

        props["Child"].Kind.Should().Be("object");
        props["Child"].Properties.Should().NotBeNullOrEmpty();
        props["Child"].Properties!.Should().ContainSingle(p => p.Name == "Label" && p.Kind == "string");
    }

    [Fact]
    public void Self_referencing_type_does_not_overflow_or_cycle()
    {
        var act = () => BuildProperties(typeof(Node));

        act.Should().NotThrow();
        var props = act();
        props.Should().NotBeEmpty();

        // The back-edge (Next → Node) must be cut; nested Next has no further properties
        var next = props.Single(p => p.Name == "Next");
        next.Kind.Should().Be("object");
        next.Properties.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Mutually_recursive_types_do_not_overflow()
    {
        var act = () => BuildProperties(typeof(Left));
        act.Should().NotThrow();
    }

    [Fact]
    public void Output_depth_never_exceeds_max_property_depth()
    {
        var props = BuildProperties(typeof(D0));
        MaxDepthOf(props).Should().BeLessThanOrEqualTo(MaxDepth);
    }

    [Fact]
    public void List_property_is_described_as_array_with_item_descriptor()
    {
        var props = BuildProperties(typeof(WithList))
            .ToDictionary(p => p.Name);

        props["Tags"].Kind.Should().Be("array");
        props["Tags"].Items.Should().NotBeNull();
        props["Tags"].Items!.Kind.Should().Be("string");

        props["Names"].Kind.Should().Be("array");
        props["Names"].Items!.Kind.Should().Be("string");
    }

    [Fact]
    public void Dictionary_property_is_described_with_key_kind_and_value_items()
    {
        var props = BuildProperties(typeof(WithDictionary))
            .ToDictionary(p => p.Name);

        props["Scores"].Kind.Should().Be("dictionary");
        props["Scores"].KeyKind.Should().Be("string");
        props["Scores"].Items.Should().NotBeNull();
        props["Scores"].Items!.Kind.Should().Be("number");
    }

    [Fact]
    public void Null_clr_type_returns_empty_list()
    {
        var result = (IReadOnlyList<OxQLPropertyDescriptor>)_buildProperties
            .Invoke(null, [null, null, 0])!;

        result.Should().BeEmpty();
    }
}
