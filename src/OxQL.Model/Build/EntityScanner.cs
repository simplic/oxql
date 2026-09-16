using System.Reflection;
using System.Text;

namespace OxQL.Model.Build;

/// <summary>One entity declaration found by the scan.</summary>
/// <param name="Id">The normalised id: trimmed and lower-cased.</param>
/// <param name="DeclaredId">The id as the attribute spelled it, trimmed.</param>
/// <param name="ClrType">The type the entity is read from: the most derived concrete subclass of the declaring type.</param>
/// <param name="Collection">The collection name.</param>
/// <param name="Database">The database override, or null.</param>
/// <param name="Extendable">Whether the entity carries an addon bag.</param>
public sealed record EntityDeclaration(string Id, string DeclaredId, Type ClrType, string Collection, string? Database, bool Extendable);

/// <summary>
/// Finds the entities of a set of assemblies: every class carrying <c>[OxQLType]</c>. The
/// attribute is read by name, so the model has no dependency on the package that defines it
/// and reads it across package versions.
/// </summary>
/// <remarks>
/// The rules are the v1 registry's: when the attribute sits on a base class, the most derived
/// concrete subclass found in the scanned assemblies is the entity's type, so every member is
/// visible; and the schema's: an id more than one declaration claims is dropped for every
/// claimant, and a type claimed under two ids is described under the first.
/// </remarks>
public static class EntityScanner
{
    /// <summary>The simple name of the attribute the scan looks for.</summary>
    public const string AttributeName = "OxQLTypeAttribute";

    /// <summary>The declarations of the scanned assemblies, ordered by id.</summary>
    public static IReadOnlyList<EntityDeclaration> Scan(IReadOnlyList<Assembly> assemblies, ICollection<BuildFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(findings);

        if (assemblies.Count == 0)
        {
            findings.Add(new BuildFinding(
                BuildCodes.EntityAssembliesMissing,
                "",
                "No assemblies were named to scan, so the model describes no entities at all."));

            return [];
        }

        List<Type> allTypes;

        try
        {
            allTypes = assemblies.SelectMany(assembly => assembly.GetTypes()).ToList();
        }
        catch (Exception exception)
        {
            findings.Add(new BuildFinding(
                BuildCodes.EntityScanFailed,
                "",
                "The entity scan failed, so the model describes no entities at all.",
                ScanFailureDetail(exception)));

            return [];
        }

        var subclasses = allTypes
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => InheritanceChain(type).Skip(1).Select(baseType => (Base: baseType, Derived: type)))
            .GroupBy(pair => pair.Base, pair => pair.Derived)
            .ToDictionary(group => group.Key, group => group.ToList());

        var claims = new Dictionary<string, List<(Type Type, Attribute Attribute)>>(StringComparer.Ordinal);

        foreach (var type in allTypes)
        {
            var attribute = type.GetCustomAttributes(inherit: false).OfType<Attribute>().FirstOrDefault(candidate => candidate.GetType().Name == AttributeName);

            if (attribute is null)
                continue;

            var declared = ReadString(attribute, "TypeName");

            if (string.IsNullOrWhiteSpace(declared))
                continue;

            var id = WireNames.NormalizeEntityId(declared);

            if (!claims.TryGetValue(id, out var claimants))
                claims[id] = claimants = [];

            claimants.Add((type, attribute));
        }

        var declarations = new List<EntityDeclaration>();
        var claimedTypes = new Dictionary<Type, string>();

        foreach (var (id, claimants) in claims.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (claimants.Count > 1)
            {
                findings.Add(new BuildFinding(
                    BuildCodes.DuplicateEntityId,
                    id,
                    $"{claimants.Count} declarations claim this id, so none of them is described.",
                    string.Join(", ", claimants.Select(claim => claim.Type.FullName ?? claim.Type.Name).OrderBy(name => name, StringComparer.Ordinal))));

                continue;
            }

            var (type, attribute) = claimants[0];
            var representative = subclasses.TryGetValue(type, out var derived) && derived.Count > 0
                ? derived.OrderByDescending(InheritanceDepth).ThenBy(candidate => candidate.FullName, StringComparer.Ordinal).First()
                : type;

            if (!claimedTypes.TryAdd(representative, id))
            {
                findings.Add(new BuildFinding(
                    BuildCodes.EntityTypeShared,
                    id,
                    $"The type behind this id is already described as '{claimedTypes[representative]}', so this id is not described.",
                    representative.FullName));

                continue;
            }

            declarations.Add(new EntityDeclaration(
                id,
                ReadString(attribute, "TypeName")!.Trim(),
                representative,
                ReadString(attribute, "CollectionName")?.Trim() ?? "",
                NullIfBlank(ReadString(attribute, "DatabaseName")),
                ReadBool(attribute, "Extendable")));
        }

        return declarations;
    }

    private static string? ReadString(Attribute attribute, string property) =>
        attribute.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(attribute) as string;

    private static bool ReadBool(Attribute attribute, string property) =>
        attribute.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(attribute) is true;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IEnumerable<Type> InheritanceChain(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            yield return current;
    }

    private static int InheritanceDepth(Type type)
    {
        var depth = 0;

        for (var current = type.BaseType; current is not null; current = current.BaseType)
            depth++;

        return depth;
    }

    private static string ScanFailureDetail(Exception exception)
    {
        var detail = new StringBuilder($"{exception.GetType().Name}: {exception.Message}");

        if (exception is ReflectionTypeLoadException load)
            foreach (var message in load.LoaderExceptions
                .Where(inner => inner is not null)
                .Select(inner => inner!.Message)
                .Distinct(StringComparer.Ordinal)
                .Where(message => !exception.Message.Contains(message, StringComparison.Ordinal))
                .Take(5))
                detail.Append($" | {message}");

        return detail.ToString();
    }
}
