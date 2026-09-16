using System.Text;
using System.Text.Json;

namespace OxQL.Model.Build;

/// <summary>
/// The naming rules the schema derives wire names, storage names and labels with. Every rule
/// here is a copy of the schema's, because the model must publish the same spellings.
/// </summary>
public static class WireNames
{
    /// <summary>The naming policy that produces the REST response body; every wire name comes from it.</summary>
    public static readonly JsonNamingPolicy Policy = JsonNamingPolicy.CamelCase;

    /// <summary>The wire name of the root key member.</summary>
    public const string IdWire = "id";

    /// <summary>The element name the driver stores a document key under.</summary>
    public const string IdStorage = "_id";

    /// <summary>The wire name of the addon bag on an extendable entity.</summary>
    public const string AddonWire = "addon";

    /// <summary>The properties that name an instance, in preference order.</summary>
    public static readonly string[] DisplayCandidates = ["name", "matchCode", "number"];

    /// <summary>Suffixes stripped from a type name before it becomes a label.</summary>
    private static readonly string[] LabelSuffixes = ["Model", "Response", "Dto"];

    /// <summary>The camelCase wire name of a CLR member name.</summary>
    public static string Wire(string clrName) => Policy.ConvertName(clrName);

    /// <summary>Upper-cases the first character and changes nothing else.</summary>
    public static string Pascalize(string wireName) =>
        string.IsNullOrEmpty(wireName) ? wireName : char.ToUpperInvariant(wireName[0]) + wireName[1..];

    /// <summary>
    /// The storage name a document reader derives when the document publishes none:
    /// <c>id</c> is <c>_id</c> at every depth, everything else is the wire name with its first
    /// letter upper-cased.
    /// </summary>
    public static string DerivedStorage(string wireName) =>
        wireName == IdWire ? IdStorage : Pascalize(wireName);

    /// <summary>The published storage name of a property: null where it equals the derivation from the wire name.</summary>
    public static string? PublishedStorageName(string clrName, string wireName) =>
        clrName == Pascalize(wireName) ? null : clrName;

    /// <summary>The label of a property, or null when it equals the one a consumer derives from the wire name.</summary>
    public static string? PropertyLabelOf(string clrName, string wireName)
    {
        var label = Humanize(clrName);

        return label == Humanize(Pascalize(wireName)) ? null : label;
    }

    /// <summary>The label of an entity: its CLR name with a known suffix stripped, then de-PascalCased.</summary>
    public static string TypeLabel(Type type) => Humanize(StripLabelSuffix(type.Name));

    private static string StripLabelSuffix(string name)
    {
        foreach (var suffix in LabelSuffixes)
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
                return name[..^suffix.Length];

        return name;
    }

    /// <summary>Splits a PascalCase name into words; an acronym run stays together, so <c>QRCode</c> becomes "QR Code".</summary>
    public static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        var label = new StringBuilder(name.Length + 8);

        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];

            var startsWord = index > 0
                && char.IsUpper(character)
                && (!char.IsUpper(name[index - 1]) || (index + 1 < name.Length && char.IsLower(name[index + 1])));

            if (startsWord)
                label.Append(' ');

            label.Append(character);
        }

        return label.ToString();
    }

    /// <summary>The one normalisation every entity id passes through: trimmed and lower-cased.</summary>
    public static string NormalizeEntityId(string id) => id.Trim().ToLowerInvariant();
}
