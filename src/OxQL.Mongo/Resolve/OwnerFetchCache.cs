using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using OxQL.Core.Models;

namespace OxQL.Mongo.Resolve;

/// <summary>The two modes of the keyed fetch; the first segment of every <see cref="OwnerFetchCache"/> key.</summary>
public enum OwnerFetchMode
{
    /// <summary>After the page: the owner's rows for the keys the page references.</summary>
    ByKeys,

    /// <summary>Before the page (the semi-join): the target keys a condition on the owner's rows selects.</summary>
    ByCondition,
}

/// <summary>
/// The one in-process cache of what owners answered a keyed fetch, per organisation, expiring after
/// <c>Cache:ResolveTtlSeconds</c>. Every key starts with the <see cref="OwnerFetchMode"/>, so the two
/// modes never read each other's entries.
/// <list type="bullet">
///   <item><b>by keys</b>: one owner row (or null: the owner has no such key) per target entity,
///   <b>target field</b>, organisation, key, and the hashes of <c>select</c> and <c>filter</c>. A warm
///   second page resolves without a call. The target field is part of the key because two references
///   may point at one entity through different members of it; without it, whichever stage ran first
///   inside the TTL answered for both.</item>
///   <item><b>by condition</b>: the owner's wire values of the target field, keyed by target entity,
///   organisation and the hash of the first-page query the owner is sent. A grid paging a filtered list
///   asks the same question on every block. How a parent stores its reference member is not part of
///   the answer, so each caller encodes the values for its own member.</item>
/// </list>
/// <para>
/// Each mode keeps its own budget of <c>Cache:OwnerFetchCacheMaxEntries</c>: a row costs one unit, a
/// list of ids one unit per id, and a semi-join answer of several thousand ids never evicts the rows
/// of the resolves beside it.
/// </para>
/// </summary>
public sealed class OwnerFetchCache : IDisposable
{
    private readonly MemoryCache rows;
    private readonly MemoryCache keys;
    private readonly TimeSpan ttl;

    /// <summary>An empty cache sized and timed by <paramref name="options"/>' <c>Cache</c> section.</summary>
    public OwnerFetchCache(OxQLOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var limit = Math.Max(1, options.Cache.OwnerFetchCacheMaxEntries);

        rows = new MemoryCache(new MemoryCacheOptions { SizeLimit = limit });
        keys = new MemoryCache(new MemoryCacheOptions { SizeLimit = limit });
        ttl = TimeSpan.FromSeconds(Math.Max(1, options.Cache.ResolveTtlSeconds));
    }

    /// <summary>The key of one by-keys owner row.</summary>
    public static string KeyOf(string targetEntity, string targetField, Guid organisation, string key, string selectHash, string filterHash) =>
        string.Join('|', nameof(OwnerFetchMode.ByKeys), targetEntity, targetField, organisation.ToString("D"), key, selectHash, filterHash);

    /// <summary>The key of one by-condition answer: the organisation and the first-page query its owner is sent.</summary>
    public static string KeyOf(Guid organisation, QueryRequest ownerQuery)
    {
        ArgumentNullException.ThrowIfNull(ownerQuery);

        var sent = JsonSerializer.Serialize(ownerQuery, OxQLJson.Wire);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sent)));

        return string.Join('|', nameof(OwnerFetchMode.ByCondition), ownerQuery.EntityType, organisation.ToString("D"), hash);
    }

    /// <summary>A short hash of a select list or a filter, so two requests with the same shape share entries.</summary>
    public static string HashOf(string? text) =>
        text is null ? "-" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>The cached by-keys row, or false. A hit is returned as a clone: a node cannot have two parents.</summary>
    public bool TryGet(string key, out JsonNode? row)
    {
        if (rows.TryGetValue(key, out JsonNode? cached))
        {
            row = cached?.DeepClone();
            return true;
        }

        row = null;
        return false;
    }

    /// <summary>Stores one by-keys row (or a null: the owner has no such key) for the TTL.</summary>
    public void Set(string key, JsonNode? row) =>
        rows.Set(key, row?.DeepClone(), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ttl });

    /// <summary>The cached by-condition wire values, or false. The list is shared and read-only; a caller encodes it for its own reference member.</summary>
    public bool TryGetKeys(string key, out IReadOnlyList<string> values)
    {
        if (keys.TryGetValue(key, out string[]? cached) && cached is not null)
        {
            values = cached;
            return true;
        }

        values = [];
        return false;
    }

    /// <summary>Stores one by-condition answer for the TTL, at a size of one per id.</summary>
    public void SetKeys(string key, IReadOnlyList<string> values) =>
        keys.Set(key, values.ToArray(), new MemoryCacheEntryOptions { Size = Math.Max(1, values.Count), AbsoluteExpirationRelativeToNow = ttl });

    /// <summary>How many entries the cache holds, both modes together.</summary>
    public int Count => rows.Count + keys.Count;

    /// <inheritdoc/>
    public void Dispose()
    {
        rows.Dispose();
        keys.Dispose();
    }
}
