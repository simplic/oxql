namespace OxQL.Model.Attributes;

/// <summary>
/// How a reference's stored value becomes its targets' key, as <see cref="OxQLReferenceAttribute"/>
/// and <see cref="OxQLReferenceWhenAttribute"/> declare it; the model's <see cref="Model.KeyAs"/>,
/// the schema's <c>"keyAs"</c>.
/// </summary>
public enum OxQLKeyAs
{
    /// <summary>The value is the key as stored.</summary>
    None,

    /// <summary>A string member holding a guid: parsed in any .NET format and sent as the lower-case <c>D</c> form.</summary>
    Guid,
}
