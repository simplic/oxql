using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using OxQL.Core.Models;

namespace OxQL.Mongo.Resolve;

/// <summary>
/// The id lists a semi-join asked an owner for, keyed by target entity, organisation and the
/// query the owner was sent. A grid paging a filtered list asks the same question on every
/// block; without this every block pays the owner again.
/// <para>
/// An entry holds the owner's wire values, which depend on nothing but the organisation and
/// that query: the target entity, the condition relative to it, and the target field it
/// projects. How a parent stores its reference member is not part of the answer, so each
/// caller encodes the values for its own member, and two references into one entity share an
/// entry only when they ask the owner the same thing.
/// </para>
/// <para>
/// Sized by id count rather than by entry, which is why this is not <see cref="ResolveCache"/>:
/// one resolved row is one unit, a list of several thousand ids is not.
/// </para>
/// </summary>
public sealed class SemiJoinCache : IDisposable
{
    private readonly MemoryCache cache;
    private readonly TimeSpan ttl;

    public SemiJoinCache(OxQLOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The same budget as the resolve cache, counted in ids: a list of the semi-join cap
        // costs as much room as that many resolved rows.
        cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = Math.Max(1, options.Cache.ResolveCacheMaxEntries) });
        ttl = TimeSpan.FromSeconds(Math.Max(1, options.Cache.ResolveTtlSeconds));
    }

    /// <summary>The key of one semi-join answer: the organisation and the first-page query its owner is sent.</summary>
    public static string KeyOf(Guid organisation, QueryRequest ownerQuery)
    {
        ArgumentNullException.ThrowIfNull(ownerQuery);

        var sent = JsonSerializer.Serialize(ownerQuery, OxQLJson.Wire);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sent)));

        return string.Join('|', ownerQuery.EntityType, organisation.ToString("D"), hash);
    }

    /// <summary>The cached wire values, or false. The list is shared and read-only; a caller encodes it for its own reference member.</summary>
    public bool TryGet(string key, out IReadOnlyList<string> ids)
    {
        if (cache.TryGetValue(key, out string[]? cached) && cached is not null)
        {
            ids = cached;
            return true;
        }

        ids = [];
        return false;
    }

    /// <summary>Stores one answer for the TTL, at a size of one per id.</summary>
    public void Set(string key, IReadOnlyList<string> ids) =>
        cache.Set(key, ids.ToArray(), new MemoryCacheEntryOptions { Size = Math.Max(1, ids.Count), AbsoluteExpirationRelativeToNow = ttl });

    /// <inheritdoc/>
    public void Dispose() => cache.Dispose();
}
