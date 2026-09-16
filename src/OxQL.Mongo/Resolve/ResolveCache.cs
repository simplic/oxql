using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using OxQL.Core.Models;

namespace OxQL.Mongo.Resolve;

/// <summary>
/// The in-process cache of resolved remote rows: bounded by entry count, expiring after
/// <c>Cache:ResolveTtlSeconds</c>, keyed by target entity, organisation, key, and the hashes
/// of <c>select</c> and <c>filter</c>. A warm second page resolves without a call.
/// </summary>
public sealed class ResolveCache : IDisposable
{
    private readonly MemoryCache cache;
    private readonly TimeSpan ttl;

    public ResolveCache(OxQLOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = Math.Max(1, options.Cache.ResolveCacheMaxEntries) });
        ttl = TimeSpan.FromSeconds(Math.Max(1, options.Cache.ResolveTtlSeconds));
    }

    /// <summary>The key of one resolved row.</summary>
    public static string KeyOf(string targetEntity, Guid organisation, string key, string selectHash, string filterHash) =>
        string.Join('|', targetEntity, organisation.ToString("D"), key, selectHash, filterHash);

    /// <summary>A short hash of a select list or a filter, so two requests with the same shape share entries.</summary>
    public static string HashOf(string? text) =>
        text is null ? "-" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>The cached row, or false. A hit is returned as a clone: a node cannot have two parents.</summary>
    public bool TryGet(string key, out JsonNode? row)
    {
        if (cache.TryGetValue(key, out JsonNode? cached))
        {
            row = cached?.DeepClone();
            return true;
        }

        row = null;
        return false;
    }

    /// <summary>Stores one row (or a null: the owner has no such key) for the TTL.</summary>
    public void Set(string key, JsonNode? row) =>
        cache.Set(key, row?.DeepClone(), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ttl });

    /// <summary>How many entries the cache holds.</summary>
    public int Count => cache.Count;

    /// <inheritdoc/>
    public void Dispose() => cache.Dispose();
}
