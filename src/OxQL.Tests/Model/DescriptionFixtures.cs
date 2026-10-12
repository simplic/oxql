// The types the description tests read. None carries [OxQLType]: the probe build scans this whole
// assembly, so the entity is declared by hand. This assembly writes no XML documentation file, so
// every XML summary the tests read comes from DescriptionFixtures.Docs, handed to an XmlDocs.

#pragma warning disable CS0618 // The [Obsolete] members are read by reflection only.

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Linq;
using OxQL.Model.Attributes;
using OxQL.Model.Build;

namespace OxQL.Tests.Model.Fixtures.Descriptions
{
    /// <summary>An entity carrying every description source, deprecation and constraint form.</summary>
    [OxQLDescription("An order as the attribute describes it.")]
    [Description("Loses to the OxQL attribute.")]
    public class Order
    {
        public Guid Id { get; set; }

        [OxQLDescription("  The   order's\n  number.  ")]
        [Description("Loses to the OxQL attribute.")]
        public string Number { get; set; } = "";

        [Description("Gets or sets the customer's display name.")]
        public string? CustomerName { get; set; }

        [Description("")]
        public string? Blank { get; set; }

        public string? Note { get; set; }

        [Obsolete("  Use number instead.  ")]
        public string? LegacyNumber { get; set; }

        [Obsolete]
        public string? Old { get; set; }

        [MaxLength(40)]
        [StringLength(30)]
        public string? Code { get; set; }

        [MaxLength(20)]
        public List<string> Tags { get; set; } = [];

        [Range(1, 10)]
        public int Quantity { get; set; }

        [Range(0.5, double.PositiveInfinity)]
        public double Weight { get; set; }

        [Range(typeof(decimal), "0.01", "99.99")]
        public decimal Price { get; set; }

        [RegularExpression("^[A-Z]{3}$")]
        public string? Currency { get; set; }

        public OrderState State { get; set; }
    }

    [Description("The state of an order.")]
    public enum OrderState
    {
        [OxQLDescription("Not yet shipped.")]
        Open,

        [Description("Shipped.")]
        Shipped,

        Cancelled,
    }

    public class DocBase
    {
        public virtual string? Label { get; set; }

        public string? Plain { get; set; }
    }

    public interface IDocNamed
    {
        string? Name { get; }
    }

    public class DocDerived : DocBase, IDocNamed
    {
        public override string? Label { get; set; }

        public string? Name { get; set; }

        public string? Crefd { get; set; }

        public string? Undocumented { get; set; }
    }

    public class DocDeeper : DocDerived
    {
        public override string? Label { get; set; }
    }

    /// <summary>The declarations and the XML documentation the tests hand over.</summary>
    internal static class DescriptionFixtures
    {
        private const string Ns = "OxQL.Tests.Model.Fixtures.Descriptions";

        public static EntityDeclaration Declaration(Type type) => new("desc.order", "desc.order", type, "orders", null, false);

        /// <summary>An XML documentation file as the compiler writes it, one member per entry.</summary>
        public static XDocument Docs(params (string Id, string Body)[] members) =>
            XDocument.Parse(
                "<?xml version=\"1.0\"?><doc><assembly><name>OxQL.Tests</name></assembly><members>"
                + string.Concat(members.Select(member => $"<member name=\"{member.Id}\">{member.Body}</member>"))
                + "</members></doc>",
                LoadOptions.PreserveWhitespace);

        /// <summary>The documentation of the fixture types the source-order and inheritance tests read.</summary>
        public static XmlDocs FixtureDocs { get; } = new(_ => Docs(
            ($"T:{Ns}.Order", "<summary>XML loses to the attributes.</summary>"),
            ($"P:{Ns}.Order.Number", "<summary>XML loses to the attributes.</summary>"),
            ($"P:{Ns}.Order.CustomerName", "<summary>XML loses to [Description].</summary>"),
            ($"P:{Ns}.Order.Blank", "<summary>Gets or sets the blank from XML.</summary>"),
            ($"P:{Ns}.Order.Note", "<summary>\n  Gets or sets a free-text note.\n  </summary>"),
            ($"F:{Ns}.OrderState.Cancelled", "<summary>Represents a cancelled order.</summary>"),
            ($"T:{Ns}.DocBase", $"<summary>Represents a base <see cref=\"T:{Ns}.DocBase\"/> of documents.</summary>"),
            ($"T:{Ns}.DocDerived", "<inheritdoc/>"),
            ($"P:{Ns}.DocBase.Label", "<summary>Gets or sets the label.</summary>"),
            ($"P:{Ns}.DocDerived.Label", "<inheritdoc/>"),
            ($"P:{Ns}.DocDeeper.Label", "<summary><inheritdoc/></summary>"),
            ($"P:{Ns}.IDocNamed.Name", "<summary>Gets the name.</summary>"),
            ($"P:{Ns}.DocDerived.Name", "<summary><inheritdoc/></summary>"),
            ($"P:{Ns}.DocDerived.Crefd", $"<inheritdoc cref=\"P:{Ns}.DocBase.Plain\"/>"),
            ($"P:{Ns}.DocBase.Plain", "<summary>The plain member.</summary>")));
    }
}

namespace OxQL.Tests.Model.Fixtures.Undescribed
{
    /// <summary>The description fixture's entity without any description, deprecation or constraint.</summary>
    public class Order
    {
        public Guid Id { get; set; }

        public string Number { get; set; } = "";

        public string? CustomerName { get; set; }

        public string? Blank { get; set; }

        public string? Note { get; set; }

        public string? LegacyNumber { get; set; }

        public string? Old { get; set; }

        public string? Code { get; set; }

        public List<string> Tags { get; set; } = [];

        public int Quantity { get; set; }

        public double Weight { get; set; }

        public decimal Price { get; set; }

        public string? Currency { get; set; }

        public OrderState State { get; set; }
    }

    public enum OrderState
    {
        Open,
        Shipped,
        Cancelled,
    }
}
