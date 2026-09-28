namespace OxQL.Model.Attributes;

/// <summary>
/// The English description the schema publishes for a type, a member or an enum value. It wins
/// over <c>[Description]</c> and the XML doc comment's <c>&lt;summary&gt;</c>, and it is normalised
/// the same way: whitespace collapsed, capped at 500 characters.
/// </summary>
/// <example>
/// <code>
/// [OxQLDescription("The billing line the item was priced from.")]
/// public Guid BillingLineId { get; set; }
/// </code>
/// </example>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = false)]
public sealed class OxQLDescriptionAttribute : Attribute
{
    /// <summary>Declares <paramref name="description"/> as the description.</summary>
    public OxQLDescriptionAttribute(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("description must not be empty.", nameof(description));

        Description = description;
    }

    /// <summary>The description as written.</summary>
    public string Description { get; }
}
