using OxQL.Model.Attributes;

namespace OxQL.Model.Build;

/// <summary>
/// Host-side reference declarations: references on members a service cannot annotate (an
/// inherited <c>Id</c>, a type from a shared package), keyed on the <b>described pooled type</b>
/// and the <b>wire member</b>. A declaration applies to that pooled type and to its variants
/// wherever they are embedded, and never to another type that inherits the same CLR member.
/// Handed to <see cref="ClrModelBuilder.Build(IEnumerable{System.Reflection.Assembly}, IReadOnlyDictionary{string, IReadOnlyList{string}}?, ReferenceDeclarations?)"/>.
/// </summary>
/// <remarks>
/// The rules are the attributes': <see cref="MemberReferences.To"/> is
/// <see cref="OxQLReferenceAttribute"/>, <see cref="MemberReferences.When(string, string, string, string?, OxQLKeyAs)"/>
/// is <see cref="OxQLReferenceWhenAttribute"/>. A member that gets both, or also carries an
/// attribute, is <c>reference-declaration-unresolved</c> and keeps no reference.
/// </remarks>
/// <example>
/// <code>
/// var declarations = new ReferenceDeclarations();
///
/// declarations.For&lt;Resource&gt;("id")
///     .When(ReferenceDeclarations.Variant, "DriverResource", "staff.employee", field: "id")
///     .When(ReferenceDeclarations.Variant, "VehicleResource", "fleet.vehicle", field: "id");
/// </code>
/// </example>
public sealed class ReferenceDeclarations
{
    /// <summary>The path that conditions a case on the stored variant of the object holding the member.</summary>
    public const string Variant = OxQLReferenceWhenAttribute.Variant;

    private readonly List<ReferenceDeclaration> declarations = [];

    /// <summary>Every declaration in the order it was made.</summary>
    public IReadOnlyList<ReferenceDeclaration> All => declarations;

    /// <summary>Starts declaring references on the wire member <paramref name="wireMember"/> of <typeparamref name="T"/>.</summary>
    public MemberReferences For<T>(string wireMember) => For(typeof(T), wireMember);

    /// <summary>Starts declaring references on the wire member <paramref name="wireMember"/> of <paramref name="type"/>.</summary>
    public MemberReferences For(Type type, string wireMember)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (string.IsNullOrWhiteSpace(wireMember))
            throw new ArgumentException("wireMember must not be empty.", nameof(wireMember));

        return new MemberReferences(this, type, wireMember.Trim());
    }

    internal void Add(ReferenceDeclaration declaration) => declarations.Add(declaration);

    /// <summary>The declarations on one pooled type's wire member, keyed by both.</summary>
    internal ILookup<(Type Type, string WireMember), ReferenceDeclaration> ByMember() =>
        declarations.ToLookup(declaration => (declaration.Type, declaration.WireMember));
}

/// <summary>The declarations of one pooled type's wire member; every call adds one case.</summary>
public sealed class MemberReferences
{
    private readonly ReferenceDeclarations owner;

    internal MemberReferences(ReferenceDeclarations owner, Type type, string wireMember)
    {
        this.owner = owner;
        Type = type;
        WireMember = wireMember;
    }

    /// <summary>The described pooled type.</summary>
    public Type Type { get; }

    /// <summary>The member's wire name.</summary>
    public string WireMember { get; }

    /// <summary>
    /// An unconditional reference to <paramref name="target"/> (<c>entity</c>, or
    /// <c>entity#itemPath</c>), matching <paramref name="field"/> or the key; <paramref name="item"/>
    /// is the other spelling of the item path. What <see cref="OxQLReferenceAttribute"/> declares.
    /// </summary>
    public MemberReferences To(string target, string? field = null, string? item = null, OxQLKeyAs keyAs = OxQLKeyAs.None)
    {
        var spelled = RequireTarget(target);

        if (!string.IsNullOrWhiteSpace(item))
        {
            if (spelled.Contains('#', StringComparison.Ordinal))
                throw new ArgumentException("The item path is given twice: in the target and as item.", nameof(item));

            spelled = $"{spelled}#{item.Trim()}";
        }

        owner.Add(new ReferenceDeclaration(Type, WireMember, null, null, [spelled], Normalise(field), keyAs));

        return this;
    }

    /// <summary>
    /// One case: the member names <paramref name="target"/> when the sibling
    /// <paramref name="path"/> holds <paramref name="equals"/>, or, with
    /// <see cref="ReferenceDeclarations.Variant"/>, when the holding object is stored as that
    /// variant. What <see cref="OxQLReferenceWhenAttribute"/> declares.
    /// </summary>
    public MemberReferences When(string path, string equals, string target, string? field = null, OxQLKeyAs keyAs = OxQLKeyAs.None) =>
        When(path, equals, [target], field, keyAs);

    /// <summary>One case with several targets, tried in order.</summary>
    public MemberReferences When(string path, string equals, IReadOnlyList<string> targets, string? field = null, OxQLKeyAs keyAs = OxQLKeyAs.None)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path must not be empty.", nameof(path));

        ArgumentNullException.ThrowIfNull(equals);
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0)
            throw new ArgumentException("A case names at least one target.", nameof(targets));

        owner.Add(new ReferenceDeclaration(Type, WireMember, path.Trim(), equals, [.. targets.Select(RequireTarget)], Normalise(field), keyAs));

        return this;
    }

    private static string RequireTarget(string target) =>
        string.IsNullOrWhiteSpace(target) ? throw new ArgumentException("A target must not be empty.", nameof(target)) : target.Trim();

    private static string? Normalise(string? field) => string.IsNullOrWhiteSpace(field) ? null : field.Trim();
}

/// <summary>One host-side reference case on a pooled type's wire member.</summary>
/// <param name="Type">The described pooled type.</param>
/// <param name="WireMember">The member's wire name.</param>
/// <param name="Path">The condition's sibling wire name or <see cref="ReferenceDeclarations.Variant"/>; null for an unconditional reference.</param>
/// <param name="Value">The value (or variant name) the condition tests; null for an unconditional reference.</param>
/// <param name="Targets">The targets, <c>entity</c> or <c>entity#itemPath</c>, in the order they are tried.</param>
/// <param name="Field">The wire path the value matches; null for the key.</param>
/// <param name="KeyAs">How the stored value becomes the targets' key.</param>
public sealed record ReferenceDeclaration(
    Type Type,
    string WireMember,
    string? Path,
    string? Value,
    IReadOnlyList<string> Targets,
    string? Field,
    OxQLKeyAs KeyAs);
