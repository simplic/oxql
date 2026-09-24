using static OxQL.IntegrationTests.Fixtures.Rows.RowKit;

namespace OxQL.IntegrationTests.Fixtures;

/// <summary>
/// The addon bags and definitions. A bag has no schema, so every value is a CLR value the
/// driver's object serializer stores in its own form: a long, int, double, string, bool, date or
/// null lands plain; a decimal and a nested dictionary are wrapped in <c>{ _t, _v }</c>. A Guid
/// cannot be written into a bag at all, so a GUID-looking value is a string.
/// </summary>
public static class Addons
{
    /// <summary>The discriminator the driver writes for a decimal in a bag.</summary>
    public const string DecimalDiscriminator = "System.Decimal";

    /// <summary>The discriminator the driver writes for a nested dictionary in a bag.</summary>
    public const string DictionaryDiscriminator = "System.Collections.Generic.Dictionary`2[System.String,System.Object]";

    /// <summary>The one-key bag every row without a purpose-built bag carries, so a group over the bag has two buckets.</summary>
    public static Dictionary<string, object> OneKey() => new() { ["shiftModel"] = "late" };

    /// <summary>The rich bag: one key per storage behaviour a bag has.</summary>
    public static Dictionary<string, object> Rich() => new()
    {
        ["weight"] = 1234.5678m,
        ["Ablieferbelege vorhanden"] = "ja",
        ["tourCount"] = 9007199254740993L,
        ["probationEnd"] = Dt("2026-06-16T00:00:00Z"),
        ["shiftModel"] = "early",
        ["legacyRef"] = "7b7e2a4e-0000-4000-8000-000000000042",
        ["plainCount"] = 7,
        ["ratio"] = 0.5,
        ["flag"] = true,
        ["undefinedKey"] = "opaque",
        ["nullKey"] = null!,
        ["vincario"] = new Dictionary<string, object> { ["make"] = "MAN", ["axles"] = 3, ["ratio"] = 0.75m },
    };

    /// <summary>
    /// The definitions each organisation declares on the three business bags. A path is the
    /// storage path under the bag, wrappers included: a nested key lives under <c>_v</c>.
    /// <c>undefinedKey</c>, <c>nullKey</c> and <c>plainCount</c> are left undefined on purpose,
    /// and <c>retiredKey</c> is defined and retired.
    /// </summary>
    public static readonly IReadOnlyList<AddonDef> Fleet =
    [
        new("weight", "decimal", "Weight"),
        new("Ablieferbelege vorhanden", "string", "Delivery notes present"),
        new("tourCount", "long", "Tour count"),
        new("probationEnd", "date", "Probation end"),
        new("shiftModel", "string", "Shift model", Values: [("early", "Early"), ("late", "Late"), ("night", "Night")]),
        new("ratio", "double", "Ratio"),
        new("flag", "bool", "Flag"),
        new("legacyRef", "string", "Legacy reference"),
        new("vincario", "object", "Vincario"),
        new("vincario._v.make", "string", "Vincario make"),
        new("vincario._v.axles", "int", "Vincario axles"),
        new("retiredKey", "string", "Retired key", Retired: true),
    ];

    /// <summary>
    /// A retired definition of <c>weight</c> on <c>transport.shipment</c> in organisation A,
    /// stored before the live one: the normal life of a definition is retire and recreate, and a
    /// reader that takes the first definition of a path takes this one. Deliberate.
    /// </summary>
    public static readonly AddonDef RetiredShadow = new("weight", "decimal", "Weight (retired)", Retired: true);

    /// <summary>The five definitions organisation A declares on <c>conformance.entity</c>; B declares none.</summary>
    public static readonly IReadOnlyList<AddonDef> Conformance =
    [
        new("Ablieferbelege vorhanden", "bool", "Proof of delivery"),
        new("contractNumber", "string", "Contract number"),
        new("status", "string", "Status", Values: [("open", "Open"), ("closed", "Closed"), ("void", "Void")]),
        new("vincario", "object", "Vincario data"),
        new("zuschlag", "decimal", "Surcharge"),
    ];
}

/// <summary>One addon definition as the seeder stores it.</summary>
public sealed record AddonDef(
    string Path,
    string Kind,
    string DisplayName,
    bool Retired = false,
    IReadOnlyList<(string Value, string? Label)>? Values = null);
