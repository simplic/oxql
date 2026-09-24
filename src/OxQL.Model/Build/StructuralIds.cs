using System.Security.Cryptography;
using System.Text;

namespace OxQL.Model.Build;

/// <summary>
/// The ids of structural pool entries, by the schema's rule: <c>t_</c> plus the CLR type name
/// in camelCase, with a hash tail where two pooled types share a name. Assigned in one pass
/// over the finished pool, because whether a name is shared is a property of the whole model.
/// </summary>
internal static class StructuralIds
{
    /// <summary>The prefix of every structural id.</summary>
    public const string Prefix = "t_";

    private const int InitialTailLength = 6;

    /// <summary>
    /// Assigns every non-entity type its structural id. Every claimant of a shared name takes
    /// a tail, never only the later ones, so the ids cannot depend on discovery order.
    /// </summary>
    public static void Assign(IReadOnlyDictionary<Type, TypeDef> pool)
    {
        var claimants = new SortedDictionary<string, List<Type>>(StringComparer.Ordinal);

        foreach (var (clrType, type) in pool)
        {
            if (type.IsEntity)
                continue;

            var readable = ReadableId(clrType);

            if (!claimants.TryGetValue(readable, out var types))
                claimants[readable] = types = [];

            types.Add(clrType);
        }

        var taken = new HashSet<string>(pool.Values.Where(type => type.IsEntity).Select(type => type.PoolId), StringComparer.Ordinal);

        foreach (var (readable, types) in claimants)
            foreach (var clrType in types.OrderBy(ClrIdentity, StringComparer.Ordinal))
            {
                var id = types.Count == 1 ? readable : Tailed(readable, clrType, taken);

                taken.Add(id);
                pool[clrType].PoolId = id;
            }
    }

    private static string Tailed(string readable, Type clrType, HashSet<string> taken)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ClrIdentity(clrType))));

        for (var length = InitialTailLength; length <= digest.Length; length += 2)
        {
            var candidate = $"{readable}_{digest[..length]}";

            if (!taken.Contains(candidate))
                return candidate;
        }

        throw new InvalidOperationException($"Two pooled types share the CLR identity '{ClrIdentity(clrType)}'.");
    }

    /// <summary>The bare structural id of a type: its CLR name without a generic arity suffix, camelCased.</summary>
    public static string ReadableId(Type type)
    {
        var name = type.Name;
        var arity = name.IndexOf('`', StringComparison.Ordinal);

        if (arity > 0)
            name = name[..arity];

        return Prefix + WireNames.Wire(name);
    }

    /// <summary>
    /// A type's identity without any version: nesting chain or namespace, name, generic
    /// arguments with their assembly, and the assembly's simple name.
    /// </summary>
    public static string ClrIdentity(Type type)
    {
        var identity = new StringBuilder();

        AppendIdentity(identity, type);
        identity.Append(", ").Append(type.Assembly.GetName().Name);

        return identity.ToString();
    }

    private static void AppendIdentity(StringBuilder identity, Type type)
    {
        if (type.DeclaringType is not null)
        {
            AppendIdentity(identity, type.DeclaringType);
            identity.Append('+');
        }
        else if (!string.IsNullOrEmpty(type.Namespace))
        {
            identity.Append(type.Namespace).Append('.');
        }

        identity.Append(type.Name);

        if (!type.IsConstructedGenericType)
            return;

        identity.Append('[');

        var arguments = type.GetGenericArguments();

        for (var index = 0; index < arguments.Length; index++)
        {
            if (index > 0)
                identity.Append(',');

            AppendIdentity(identity, arguments[index]);
            identity.Append(", ").Append(arguments[index].Assembly.GetName().Name);
        }

        identity.Append(']');
    }
}
