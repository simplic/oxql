using FluentAssertions;
using OxQL.Model;
using OxQL.Model.Build;
using OxQL.Tests.Model.Fixtures.Descriptions;
using OxQL.Tests.Model.Fixtures.Polymorphism;
using Xunit;
using Undescribed = OxQL.Tests.Model.Fixtures.Undescribed;

namespace OxQL.Tests.Model;

/// <summary>
/// Descriptions, deprecation and constraints (DESIGN §3.9): the source order, the XML
/// documentation with <c>&lt;inheritdoc/&gt;</c>, every normalisation rule, and that none of it
/// moves the model fingerprint.
/// </summary>
public class DescriptionTests
{
    private const string Ns = "OxQL.Tests.Model.Fixtures.Descriptions";

    private static readonly EntityModel Model = ClrModelBuilder.Build([DescriptionFixtures.Declaration(typeof(Order))]);

    private static TypeDef OrderType => Model.Entities["desc.order"].Root;

    private static XmlDocs Docs => DescriptionFixtures.FixtureDocs;

    private static string? Rendered(string summary) =>
        new XmlDocs(_ => DescriptionFixtures.Docs(($"T:{Ns}.DocBase", $"<summary>{summary}</summary>"))).Summary(typeof(DocBase));

    // Source order.

    [Fact]
    public void The_OxQL_attribute_wins_over_Description_and_the_XML_summary()
    {
        Docs.Description(typeof(Order)).Should().Be("An order as the attribute describes it.");
        Docs.Description(typeof(Order).GetProperty(nameof(Order.Number))!).Should().Be("The order's number.");
    }

    [Fact]
    public void Description_wins_over_the_XML_summary_and_is_normalised_too()
    {
        Docs.Description(typeof(Order).GetProperty(nameof(Order.CustomerName))!).Should().Be("The customer's display name.");
        Docs.Description(typeof(OrderState)).Should().Be("The state of an order.");
    }

    [Fact]
    public void An_empty_Description_falls_through_to_the_XML_summary()
    {
        Docs.Description(typeof(Order).GetProperty(nameof(Order.Blank))!).Should().Be("The blank from XML.");
        Docs.Description(typeof(Order).GetProperty(nameof(Order.Note))!).Should().Be("A free-text note.");
    }

    [Fact]
    public void Enum_values_take_the_same_source_order()
    {
        Docs.Description(typeof(OrderState).GetField(nameof(OrderState.Open))!).Should().Be("Not yet shipped.");
        Docs.Description(typeof(OrderState).GetField(nameof(OrderState.Shipped))!).Should().Be("Shipped.");
        Docs.Description(typeof(OrderState).GetField(nameof(OrderState.Cancelled))!).Should().Be("A cancelled order.");
    }

    [Fact]
    public void A_member_without_any_source_has_no_description()
    {
        Docs.Description(typeof(DocDerived).GetProperty(nameof(DocDerived.Undocumented))!).Should().BeNull();
        new XmlDocs(_ => null).Description(typeof(DocBase)).Should().BeNull();
    }

    [Fact]
    public void The_OxQL_attribute_refuses_an_empty_description()
    {
        var create = () => new OxQL.Model.Attributes.OxQLDescriptionAttribute("  ");

        create.Should().Throw<ArgumentException>();
    }

    // XML documentation and <inheritdoc/>.

    [Fact]
    public void The_documentation_ids_are_the_compiler_s()
    {
        XmlDocs.DocumentationId(typeof(Order)).Should().Be($"T:{Ns}.Order");
        XmlDocs.DocumentationId(typeof(Order).GetProperty(nameof(Order.Note))!).Should().Be($"P:{Ns}.Order.Note");
        XmlDocs.DocumentationId(typeof(OrderState).GetField(nameof(OrderState.Open))!).Should().Be($"F:{Ns}.OrderState.Open");
        XmlDocs.DocumentationId(typeof(List<int>)).Should().Be("T:System.Collections.Generic.List`1");
        XmlDocs.DocumentationId(typeof(Dictionary<string, int>.KeyCollection)).Should().Be("T:System.Collections.Generic.Dictionary`2.KeyCollection");
    }

    [Fact]
    public void Inheritdoc_on_a_type_inherits_the_base_type_s_summary()
    {
        Docs.Summary(typeof(DocDerived)).Should().Be("A base DocBase of documents.");
    }

    [Fact]
    public void Inheritdoc_on_an_override_inherits_the_overridden_member_s_summary_through_every_level()
    {
        Docs.Summary(typeof(DocDerived).GetProperty(nameof(DocDerived.Label))!).Should().Be("The label.");
        Docs.Summary(typeof(DocDeeper).GetProperty(nameof(DocDeeper.Label))!).Should().Be("The label.");
    }

    [Fact]
    public void Inheritdoc_inside_a_summary_inherits_from_the_implemented_interface()
    {
        Docs.Summary(typeof(DocDerived).GetProperty(nameof(DocDerived.Name))!).Should().Be("The name.");
    }

    [Fact]
    public void Inheritdoc_with_a_cref_inherits_the_named_member_s_summary()
    {
        Docs.Summary(typeof(DocDerived).GetProperty(nameof(DocDerived.Crefd))!).Should().Be("The plain member.");
    }

    [Fact]
    public void An_inherited_member_is_documented_under_its_declaring_type_and_an_undocumented_one_has_none()
    {
        Docs.Summary(typeof(DocDerived).GetProperty(nameof(DocDerived.Plain))!).Should().Be("The plain member.", "an inherited property is documented under its declaring type");
        Docs.Summary(typeof(DocDerived).GetProperty(nameof(DocDerived.Undocumented))!).Should().BeNull();
    }

    [Fact]
    public void The_documentation_file_beside_an_assembly_is_read()
    {
        XmlDocs.Beside.Description(typeof(MemberDef).GetProperty(nameof(MemberDef.WireName))!).Should().Be("The camelCase wire name.");
        XmlDocs.Beside.Description(typeof(EntityModel))!.Should().StartWith("One immutable model per host:");
        XmlDocs.Beside.Description(typeof(DocBase)).Should().BeNull("the test assembly writes no documentation file");
    }

    // Normalisation.

    [Theory]
    [InlineData("Gets or sets the billing line by ID.", "The billing line by ID.")]
    [InlineData("Gets or sets whether the line is open.", "Whether the line is open.")]
    [InlineData("Gets a value indicating whether it is closed.", "A value indicating whether it is closed.")]
    [InlineData("Represents an item of a transaction.", "An item of a transaction.")]
    [InlineData("the lower-case start.", "The lower-case start.")]
    [InlineData("It gets or sets nothing mid-sentence.", "It gets or sets nothing mid-sentence.")]
    [InlineData("Gets", "Gets")]
    public void A_boilerplate_opening_is_dropped_and_the_first_letter_capitalised(string text, string expected)
    {
        XmlDocs.Normalise(text).Should().Be(expected);
    }

    [Fact]
    public void A_see_cref_renders_as_its_simple_name()
    {
        Rendered("Of a <see cref=\"T:Ns.Outer.Inner\"/>.").Should().Be("Of a Inner.");
        Rendered("Of <see cref=\"T:System.Collections.Generic.List`1\"/>.").Should().Be("Of List.");
        Rendered("Calls <see cref=\"M:Ns.Service.Run(System.Int32,System.String)\"/>.").Should().Be("Calls Run.");
        Rendered("Built by <see cref=\"M:Ns.Service.#ctor\"/>.").Should().Be("Built by Service.");
        Rendered("Reads <see cref=\"P:Ns.Service.Name\"/> and <seealso cref=\"F:Ns.Kind.Open\"/>.").Should().Be("Reads Name and Open.");
    }

    [Fact]
    public void A_see_with_text_langword_or_href_renders_as_that()
    {
        Rendered("Is <see cref=\"T:Ns.X\">the text</see>.").Should().Be("Is the text.");
        Rendered("Never <see langword=\"null\"/>.").Should().Be("Never null.");
        Rendered("See <see href=\"https://example.org\"/>.").Should().Be("See https://example.org.");
    }

    [Fact]
    public void Paramref_typeparamref_and_c_render_as_their_text()
    {
        Rendered("When <paramref name=\"count\"/> of <typeparamref name=\"T\"/> is <c>0</c>.").Should().Be("When count of T is 0.");
    }

    [Fact]
    public void A_para_becomes_a_blank_line()
    {
        Rendered("Gets the first.<para>The second\n   one.</para><para>The third.</para>").Should().Be("The first.\n\nThe second one.\n\nThe third.");
        XmlDocs.Normalise("First.\n\n  Second\r\n  \r\nThird.").Should().Be("First.\n\nSecond\n\nThird.");
    }

    [Fact]
    public void Whitespace_is_collapsed_and_trimmed()
    {
        Rendered("\n    Gets or sets   the\n    amount,\tnet.\n    ").Should().Be("The amount, net.");
        XmlDocs.Normalise("   ").Should().BeNull();
        Rendered("  ").Should().BeNull();
    }

    [Fact]
    public void A_description_is_capped_at_500_characters_at_a_word_boundary()
    {
        var words = string.Join(" ", Enumerable.Range(0, 120).Select(index => $"Word{index:D3}"));
        var capped = XmlDocs.Normalise(words)!;

        words.Length.Should().BeGreaterThan(XmlDocs.MaxLength);
        capped.Length.Should().BeLessThanOrEqualTo(XmlDocs.MaxLength);
        capped.Should().EndWith("…");
        words.Should().StartWith(capped[..^1] + " ", "the cut falls on a word boundary");

        var unbroken = XmlDocs.Normalise(new string('a', 600))!;

        unbroken.Length.Should().Be(XmlDocs.MaxLength);
        XmlDocs.Normalise(new string('a', XmlDocs.MaxLength)).Should().HaveLength(XmlDocs.MaxLength, "a text at the cap is kept whole");
    }

    // The model.

    [Fact]
    public void The_CLR_build_describes_the_entity_its_members_and_its_enum()
    {
        OrderType.Description.Should().Be("An order as the attribute describes it.");
        OrderType.Member("number")!.Description.Should().Be("The order's number.");
        OrderType.Member("customerName")!.Description.Should().Be("The customer's display name.");
        OrderType.Member("note")!.Description.Should().BeNull("this assembly has no documentation file");

        var state = OrderType.Member("state")!.Type!;

        state.Description.Should().Be("The state of an order.");
        state.EnumValues.Select(value => value.Description).Should().Equal("Not yet shipped.", "Shipped.", null);
    }

    [Fact]
    public void Obsolete_marks_a_member_deprecated_with_its_message_as_the_note()
    {
        OrderType.Member("legacyNumber")!.Deprecated.Should().Be(new DeprecationDef { Note = "Use number instead." });
        OrderType.Member("old")!.Deprecated.Should().Be(new DeprecationDef());
        OrderType.Member("number")!.Deprecated.Should().BeNull();
    }

    [Fact]
    public void DataAnnotations_become_constraints()
    {
        OrderType.Member("code")!.Constraints.Should().Be(new ConstraintsDef { MaxLength = 30 }, "the smaller of [MaxLength] and [StringLength]");
        OrderType.Member("tags")!.Constraints.Should().BeNull("a length on a collection is no string length");
        OrderType.Member("quantity")!.Constraints.Should().Be(new ConstraintsDef { Min = "1", Max = "10" });
        OrderType.Member("weight")!.Constraints.Should().Be(new ConstraintsDef { Min = "0.5" }, "an infinite bound is no bound");
        OrderType.Member("price")!.Constraints.Should().Be(new ConstraintsDef { Min = "0.01", Max = "99.99" });
        OrderType.Member("currency")!.Constraints.Should().Be(new ConstraintsDef { Pattern = "^[A-Z]{3}$" });
        OrderType.Member("note")!.Constraints.Should().BeNull();
    }

    [Fact]
    public void A_merged_variant_member_keeps_the_first_carrier_s_description()
    {
        var model = PolymorphismModel.Build();

        model.TypePool["t_line"].Member("note")!.Description.Should().Be("The billing line's note.");
        model.TypePool["t_billingLine"].Member("note")!.Description.Should().Be("The billing line's note.");
    }

    [Fact]
    public void Descriptions_deprecation_and_constraints_leave_the_fingerprint_unchanged()
    {
        var plain = ClrModelBuilder.Build([DescriptionFixtures.Declaration(typeof(Undescribed.Order))]);

        plain.Entities["desc.order"].Root.Member("code")!.Constraints.Should().BeNull();
        plain.Fingerprint.Should().Be(Model.Fingerprint);
    }

    [Fact]
    public void The_document_build_reads_descriptions_deprecation_and_constraints_back()
    {
        var model = DocumentModelBuilder.Build(Document(described: true));
        var order = model.Entities["desc.order"].Root;
        var code = order.Member("code")!;

        order.Description.Should().Be("An order.");
        code.Description.Should().Be("The code.");
        code.Deprecated.Should().Be(new DeprecationDef { Since = "2.0", ReplacedBy = "number", Note = "Use number." });
        code.Constraints.Should().Be(new ConstraintsDef { MaxLength = 30, Min = "1", Max = "10", Pattern = "^x$" });
        order.Member("id")!.Description.Should().BeNull();
        order.Member("id")!.Deprecated.Should().BeNull();
        order.Member("id")!.Constraints.Should().BeNull();
        model.TypePool["t_state"].Description.Should().Be("The state.");
        model.TypePool["t_state"].EnumValues.Should().Equal(new EnumValueDef("Open", 0, true, "Not yet shipped."), new EnumValueDef("Shipped", 1, true));
    }

    [Fact]
    public void The_document_build_s_fingerprint_ignores_descriptions_deprecation_and_constraints()
    {
        DocumentModelBuilder.Build(Document(described: true)).Fingerprint
            .Should().Be(DocumentModelBuilder.Build(Document(described: false)).Fingerprint);
    }

    private static string Document(bool described)
    {
        string Extra(string text) => described ? text : "";

        return $$"""
            {
              "schemaVersion": "1.1",
              "types": {
                "desc.order": {
                  "entity": true,
                  {{Extra("\"description\": \"An order.\",")}}
                  "properties": [
                    { "name": "id", "kind": "guid", "nullable": false },
                    { "name": "code", "kind": "string", "nullable": true
                      {{Extra(", \"description\": \"The code.\", \"deprecated\": { \"since\": \"2.0\", \"replacedBy\": \"number\", \"note\": \"Use number.\" }, \"constraints\": { \"maxLength\": 30, \"min\": \"1\", \"max\": \"10\", \"pattern\": \"^x$\" }")}} },
                    { "name": "state", "kind": "enum", "type": "#/types/t_state", "nullable": false }
                  ]
                },
                "t_state": {
                  "kind": "enum",
                  {{Extra("\"description\": \"The state.\",")}}
                  "values": [ { "name": "Open", "value": 0 {{Extra(", \"description\": \"Not yet shipped.\"")}} }, { "name": "Shipped", "value": 1 } ]
                }
              }
            }
            """;
    }
}
