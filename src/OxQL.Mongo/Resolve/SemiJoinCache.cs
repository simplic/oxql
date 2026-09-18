using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Models;

namespace OxQL.Mongo.Resolve;

/// <summary>
/// The id lists a semi-join asked an owner for, keyed by target entity, organisation and the
/// canonical form of the condition. A grid paging a filtered list asks the same question on
/// every block; without this every block pays the owner again.
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

    /// <summary>The key of one semi-join answer.</summary>
    public static string KeyOf(string targetEntity, Guid organisation, BoundCondition condition)
    {
        var canonical = BoundCanonical.RenderCondition(condition).ToJsonString();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];

        return string.Join('|', targetEntity, organisation.ToString("D"), hash);
    }

    /// <summary>The cached ids, or false. Returned as a copy: the caller fills a live filter slot with them.</summary>
    public bool TryGet(string key, out IReadOnlyList<BsonValue> ids)
    {
        if (cache.TryGetValue(key, out BsonValue[]? cached) && cached is not null)
        {
            ids = cached;
            return true;
        }

        ids = [];
        return false;
    }

    /// <summary>Stores one answer for the TTL, at a size of one per id.</summary>
    public void Set(string key, IReadOnlyList<BsonValue> ids) =>
        cache.Set(key, ids.ToArray(), new MemoryCacheEntryOptions { Size = Math.Max(1, ids.Count), AbsoluteExpirationRelativeToNow = ttl });

    /// <inheritdoc/>
    public void Dispose() => cache.Dispose();
}
