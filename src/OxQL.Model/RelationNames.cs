namespace OxQL.Model;

/// <summary>
/// The name of the relation a reference stands for, on the member a reader finds it at: the schema
/// document's <c>relation</c>. Names are derived by the engine alone, from what the model already
/// says; nothing declares one and nothing is refused or logged for one. They label declared
/// references for tooling (a relation list, a default alias); they never create a reference and no
/// query names one.
/// </summary>
/// <param name="Name">The name: a wire segment, unique among the relation names of its type.</param>
/// <param name="Member">
/// Null when the name is that of the reference the member itself carries. Otherwise the member is a
/// slot (an embedded object, or a collection of objects) and the name is that of the reference its
/// key member carries: <c>id</c> or <c>referenceId</c>, the wire name written here.
/// </param>
public sealed record RelationDef(string Name, string? Member = null);

/// <summary>
/// Derives the relation names of a model (<see cref="RelationDef"/>). One rule, applied to every
/// pooled type, by wire names only:
/// <list type="number">
///   <item>A reference member whose id member a navigation property names (<c>[ReferenceId]</c>)
///   takes the navigation property's wire name: <c>StartAddress</c> for <c>startAddressId</c> is
///   <c>startAddress</c>.</item>
///   <item>A reference on the key member (<c>id</c>, else <c>referenceId</c>) of an embedded
///   object or of the objects of a collection is named at the slot, the member that holds the
///   object or the collection, by the slot's wire name without a trailing <c>Reference</c> or
///   <c>Ref</c>: <c>sourceBillingLineReference.id</c> is <c>sourceBillingLine</c>,
///   <c>resources[].id</c> is <c>resources</c>.</item>
///   <item>Any other reference member is named by its own wire name without a trailing
///   <c>Ids</c> (which leaves the plural <c>s</c>), <c>Id</c>, <c>Number</c> or <c>Key</c>:
///   <c>tourId</c> is <c>tour</c>, <c>vehicleIds</c> is <c>vehicles</c>.</item>
///   <item>Where none of those ends the name, without a trailing <c>Reference</c> or <c>Ref</c>
///   (<c>ownerRef</c> is <c>owner</c>), else the wire name as it is (<c>reference</c>).</item>
/// </list>
/// A suffix is stripped only when something is left, and the result starts in lower case. Two
/// members of one type that derive the same name both take their own wire name instead, which is
/// unique; so does a member whose derived name is the wire name another one fell back to.
/// </summary>
public static class RelationNames
{
    /// <summary>The key members of an embedded object whose reference is named at the slot, in the order they are looked for.</summary>
    public static readonly IReadOnlyList<string> SlotKeys = ["id", "referenceId"];

    private static readonly (string Suffix, string Replacement)[] LeafSuffixes = [("Ids", "s"), ("Id", ""), ("Number", ""), ("Key", "")];

    private static readonly string[] ReferenceSuffixes = ["Reference", "Ref"];

    /// <summary>
    /// The derived name of a reference member by its wire name (rules 1, 3 and 4):
    /// <paramref name="navigation"/> is the wire name of the navigation property that names the
    /// member, or null.
    /// </summary>
    public static string OfMember(string wireName, string? navigation = null)
    {
        ArgumentNullException.ThrowIfNull(wireName);

        if (!string.IsNullOrEmpty(navigation))
            return LowerFirst(navigation);

        foreach (var (suffix, replacement) in LeafSuffixes)
            if (wireName.Length > suffix.Length && wireName.EndsWith(suffix, StringComparison.Ordinal))
                return LowerFirst(wireName[..^suffix.Length] + replacement);

        return LowerFirst(WithoutReferenceSuffix(wireName));
    }

    /// <summary>The derived name of the reference a slot's key member carries (rule 2), by the slot's wire name.</summary>
    public static string OfSlot(string slotWireName)
    {
        ArgumentNullException.ThrowIfNull(slotWireName);

        return LowerFirst(WithoutReferenceSuffix(slotWireName));
    }

    /// <summary>
    /// The name of the relation the reference at <paramref name="path"/> of <paramref name="entity"/>
    /// stands for, as a reader of the schema document finds it: the name at the slot above the path
    /// when the path ends in that slot's key member, else the name at the path's own member; null
    /// when the path carries no reference.
    /// </summary>
    public static string? At(EntityDef entity, PathDef path)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(path);

        var cut = path.Wire.LastIndexOf('.');

        if (cut > 0 && entity.PathIndex.TryGetValue(path.Wire[..cut], out var above)
            && above.Member.Relation is { Member: { } key } slot && key == path.Member.WireName)
            return slot.Name;

        return path.Member.Relation is { Member: null } own ? own.Name : null;
    }

    /// <summary>
    /// Names the relations of <paramref name="type"/>'s members. <paramref name="navigations"/> maps
    /// the wire name of an id member to the wire name of the navigation property that names it. A
    /// type whose members already carry names (read from a schema document) keeps them.
    /// </summary>
    internal static void Assign(TypeDef type, IReadOnlyDictionary<string, string>? navigations = null)
    {
        if (type.Members.Count == 0 || type.Members.Any(member => member.Relation is not null))
            return;

        var named = new List<(MemberDef Member, string Name, string? Key)>();

        foreach (var member in type.Members)
        {
            if (member.References.Count > 0)
            {
                named.Add((member, OfMember(member.WireName, navigations?.GetValueOrDefault(member.WireName)), null));
                continue;
            }

            if (SlotKeyOf(member) is { } key)
                named.Add((member, OfSlot(member.WireName), key));
        }

        // A name two members derive is nobody's: each takes its own wire name, which is unique in
        // the type. That may be the name a third one derived, which then takes its own as well.
        var names = named.Select(each => each.Name).ToList();

        for (var changed = true; changed;)
        {
            changed = false;

            foreach (var duplicate in names.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToList())
                for (var index = 0; index < names.Count; index++)
                    if (names[index] == duplicate && names[index] != named[index].Member.WireName)
                    {
                        names[index] = named[index].Member.WireName;
                        changed = true;
                    }
        }

        for (var index = 0; index < named.Count; index++)
            named[index].Member.Relation = new RelationDef(names[index], named[index].Key);
    }

    /// <summary>
    /// The key member of the object a member holds (an embedded object, or the elements of a
    /// collection of objects) that carries a reference: <c>id</c>, else <c>referenceId</c>; null when
    /// the member holds no object or its key member is no reference.
    /// </summary>
    private static string? SlotKeyOf(MemberDef member)
    {
        ShapeDef shape = member;

        while (shape is { Kind: Kind.Array, Of: { } element })
            shape = element;

        if (shape is not { Kind: Kind.Object, Type: { } held })
            return null;

        foreach (var key in SlotKeys)
            if (held.Members.FirstOrDefault(candidate => candidate.WireName == key) is { References.Count: > 0 })
                return key;

        return null;
    }

    private static string WithoutReferenceSuffix(string text)
    {
        foreach (var suffix in ReferenceSuffixes)
            if (text.Length > suffix.Length && text.EndsWith(suffix, StringComparison.Ordinal))
                return text[..^suffix.Length];

        return text;
    }

    private static string LowerFirst(string text) => text.Length == 0 || char.IsLower(text[0]) ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
