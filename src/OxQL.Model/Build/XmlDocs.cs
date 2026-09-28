using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OxQL.Model.Attributes;

namespace OxQL.Model.Build;

/// <summary>
/// The descriptions the model publishes for types, members and enum values, in source order:
/// <see cref="OxQLDescriptionAttribute"/>, then <see cref="DescriptionAttribute"/>, then the
/// <c>&lt;summary&gt;</c> of the XML documentation file the compiler writes beside the assembly
/// (<c>&lt;inheritdoc/&gt;</c> resolved). Every description is normalised deterministically,
/// because it enters the schema document's revision: boilerplate openings dropped, references
/// rendered as simple names, whitespace collapsed, capped at <see cref="MaxLength"/> characters.
/// </summary>
/// <remarks>
/// Descriptions are English and never part of the model fingerprint. A type or member without
/// any of the three sources has no description; nothing is inferred from its name.
/// </remarks>
public sealed partial class XmlDocs
{
    /// <summary>The longest description, in characters; a longer one is cut at a word boundary and ends in an ellipsis.</summary>
    public const int MaxLength = 500;

    private const char ParagraphBreak = (char)0x2029;
    private const string Ellipsis = "…";
    private const int MaxInheritDepth = 8;

    private static readonly IReadOnlyDictionary<string, XElement> NoMembers = new Dictionary<string, XElement>(StringComparer.Ordinal);

    private readonly Func<Assembly, XDocument?> source;
    private readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, XElement>> files = new();

    /// <summary>Reads each assembly's documentation through <paramref name="source"/>, once per assembly; null means the assembly has none.</summary>
    public XmlDocs(Func<Assembly, XDocument?> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        this.source = source;
    }

    /// <summary>The documentation files beside the assemblies: <c>&lt;assembly&gt;.xml</c> next to <see cref="Assembly.Location"/>.</summary>
    public static XmlDocs Beside { get; } = new(LoadBeside);

    /// <summary>The description of a type, a property or an enum field by the source order; null when no source describes it.</summary>
    public string? Description(MemberInfo member)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (member.GetCustomAttribute<OxQLDescriptionAttribute>(inherit: false) is { } declared && Normalise(declared.Description) is { } explicitText)
            return explicitText;

        if (member.GetCustomAttribute<DescriptionAttribute>(inherit: false) is { } described && Normalise(described.Description) is { } componentText)
            return componentText;

        return Summary(member);
    }

    /// <summary>The normalised <c>&lt;summary&gt;</c> of a member's XML documentation, <c>&lt;inheritdoc/&gt;</c> resolved; null when there is none.</summary>
    public string? Summary(MemberInfo member)
    {
        ArgumentNullException.ThrowIfNull(member);

        return Resolve(member, 0) is { } summary ? Render(summary) : null;
    }

    /// <summary>
    /// Normalises a plain-text description: paragraphs are separated by a blank line, whitespace
    /// within one collapses to a space, a leading "Gets or sets", "Gets" or "Represents" is dropped
    /// and the first letter capitalised, and the text is capped at <see cref="MaxLength"/>. Null
    /// when nothing remains.
    /// </summary>
    public static string? Normalise(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Finish(BlankLine().Split(text));

    /// <summary>The documentation id of a type (<c>T:</c>), a property (<c>P:</c>) or a field (<c>F:</c>); null for anything else.</summary>
    public static string? DocumentationId(MemberInfo member) => member switch
    {
        Type type => TypeId(type) is { } id ? "T:" + id : null,
        PropertyInfo property when property.DeclaringType is { } owner && TypeId(owner) is { } id => $"P:{id}.{property.Name}",
        FieldInfo field when field.DeclaringType is { } owner && TypeId(owner) is { } id => $"F:{id}.{field.Name}",
        _ => null,
    };

    private static string? TypeId(Type type)
    {
        if (type.IsGenericParameter)
            return null;

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
            type = type.GetGenericTypeDefinition();

        return type.FullName?.Replace('+', '.');
    }

    private static XDocument? LoadBeside(Assembly assembly)
    {
        if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
            return null;

        var path = Path.ChangeExtension(assembly.Location, ".xml");

        if (!File.Exists(path))
            return null;

        try
        {
            return XDocument.Load(path, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private IReadOnlyDictionary<string, XElement> MembersOf(Assembly assembly) =>
        files.GetOrAdd(assembly, key =>
        {
            var document = source(key);
            var members = document?.Root?.Element("members");

            if (members is null)
                return NoMembers;

            var index = new Dictionary<string, XElement>(StringComparer.Ordinal);

            foreach (var member in members.Elements("member"))
                if (member.Attribute("name")?.Value is { Length: > 0 } name)
                    index.TryAdd(name, member);

            return index;
        });

    /// <summary>The <c>&lt;summary&gt;</c> element that describes a member, following <c>&lt;inheritdoc/&gt;</c>.</summary>
    private XElement? Resolve(MemberInfo member, int depth)
    {
        if (depth > MaxInheritDepth || DocumentationId(member) is not { } id || member.Module.Assembly is not { } assembly)
            return null;

        if (!MembersOf(assembly).TryGetValue(id, out var element))
            return null;

        var summary = element.Element("summary");
        var inherit = element.Element("inheritdoc") ?? (summary is not null && OnlyInheritDoc(summary) is { } nested ? nested : null);

        if (inherit is null)
            return summary;

        if (inherit.Attribute("cref")?.Value is { Length: > 0 } cref)
        {
            foreach (var candidate in new[] { assembly }.Concat(InheritanceCandidates(member).Select(each => each.Module.Assembly)).Distinct())
                if (MembersOf(candidate).TryGetValue(cref, out var referenced) && referenced.Element("summary") is { } referencedSummary)
                    return referencedSummary;

            return null;
        }

        foreach (var candidate in InheritanceCandidates(member))
            if (Resolve(candidate, depth + 1) is { } inherited)
                return inherited;

        return null;
    }

    /// <summary>The <c>&lt;inheritdoc/&gt;</c> of a summary that holds nothing else.</summary>
    private static XElement? OnlyInheritDoc(XElement summary)
    {
        var elements = summary.Elements().ToList();

        return elements is [{ Name.LocalName: "inheritdoc" } only] && string.IsNullOrWhiteSpace(string.Concat(summary.Nodes().OfType<XText>().Select(text => text.Value)))
            ? only
            : null;
    }

    /// <summary>
    /// Where an <c>&lt;inheritdoc/&gt;</c> without a <c>cref</c> inherits from: a type's base
    /// type, then its interfaces; a property's namesake on each base type, then on each
    /// interface. Interfaces ordinally by full name.
    /// </summary>
    private static IEnumerable<MemberInfo> InheritanceCandidates(MemberInfo member)
    {
        switch (member)
        {
            case Type type:
                if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum))
                    yield return baseType;

                foreach (var contract in type.GetInterfaces().OrderBy(each => each.FullName, StringComparer.Ordinal))
                    yield return contract;

                break;

            case PropertyInfo property when property.DeclaringType is { } owner:
                const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

                for (var current = owner.BaseType; current is not null && current != typeof(object); current = current.BaseType)
                    if (current.GetProperties(declared).FirstOrDefault(each => each.Name == property.Name && each.GetIndexParameters().Length == 0) is { } overridden)
                        yield return overridden;

                foreach (var contract in owner.GetInterfaces().OrderBy(each => each.FullName, StringComparer.Ordinal))
                    if (contract.GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance) is { } implemented)
                        yield return implemented;

                break;
        }
    }

    /// <summary>A summary element as normalised text.</summary>
    private static string? Render(XElement summary)
    {
        var text = new StringBuilder();

        RenderNodes(summary, text);

        return Finish(text.ToString().Split(ParagraphBreak));
    }

    private static void RenderNodes(XElement element, StringBuilder text)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText plain:
                    text.Append(plain.Value);
                    break;

                case XElement child:
                    RenderElement(child, text);
                    break;
            }
        }
    }

    private static void RenderElement(XElement element, StringBuilder text)
    {
        switch (element.Name.LocalName)
        {
            case "para" or "item" or "listheader":
                text.Append(ParagraphBreak);
                RenderNodes(element, text);
                text.Append(ParagraphBreak);
                break;

            case "see" or "seealso" when !element.Nodes().Any():
                text.Append(
                    element.Attribute("cref")?.Value is { Length: > 0 } cref ? SimpleName(cref)
                    : element.Attribute("langword")?.Value ?? element.Attribute("href")?.Value ?? "");
                break;

            case "paramref" or "typeparamref":
                text.Append(element.Attribute("name")?.Value ?? "");
                break;

            case "br":
                text.Append(' ');
                break;

            case "inheritdoc":
                break;

            default:
                RenderNodes(element, text);
                break;
        }
    }

    /// <summary>A <c>cref</c> as the simple name it points at: no id prefix, namespace, parameter list or generic arity.</summary>
    internal static string SimpleName(string cref)
    {
        var name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var parameters = name.IndexOf('(', StringComparison.Ordinal);

        if (parameters >= 0)
            name = name[..parameters];

        var segments = name.Split('.');
        var last = segments[^1] == "#ctor" && segments.Length > 1 ? segments[^2] : segments[^1];
        var arity = last.IndexOf('`', StringComparison.Ordinal);

        return arity > 0 ? last[..arity] : last;
    }

    private static string? Finish(IEnumerable<string> paragraphs)
    {
        var kept = paragraphs
            .Select(paragraph => Whitespace().Replace(paragraph, " ").Trim())
            .Where(paragraph => paragraph.Length > 0)
            .ToList();

        if (kept.Count == 0)
            return null;

        kept[0] = Capitalise(Boilerplate().Replace(kept[0], "", 1));

        return Cap(string.Join("\n\n", kept));
    }

    private static string Capitalise(string text) =>
        text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    /// <summary>The text, or its longest prefix ending at a word boundary followed by an ellipsis, within <see cref="MaxLength"/> characters.</summary>
    private static string Cap(string text)
    {
        if (text.Length <= MaxLength)
            return text;

        var cut = text[..(MaxLength - 1)];
        var boundary = cut.LastIndexOfAny([' ', '\n']);

        if (boundary > 0)
            cut = cut[..boundary];

        return cut.TrimEnd() + Ellipsis;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\r?\n[ \t]*\r?\n")]
    private static partial Regex BlankLine();

    [GeneratedRegex(@"^(?:Gets or sets|Gets|Represents)\s+(?=\S)", RegexOptions.CultureInvariant)]
    private static partial Regex Boilerplate();
}
