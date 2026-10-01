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
/// What an owner answered for one key of a by-keys fetch (DESIGN §3.5.2, §3.6): its rows (one for
/// an entity keyed by its own key, up to <see cref="KeyedFetch.PerKey"/> for a grouped query), or
/// none. <see cref="Excluded"/> marks a key the target's filter left out while the existence probe
/// found it: present, not missing.
/// </summary>
public sealed record OwnerAnswer(IReadOnlyList<JsonObject> Rows, bool Excluded = false)
{
    /// <summary>The owner holds no record for the key (<c>not_found</c>): a negative entry, kept for <c>Cache:NegativeResolveTtlSeconds</c>.</summary>
    public bool IsNegative => Rows.Count == 0 && !Excluded;
}

/// <summary>
/// The one in-process cache of what owners answered a keyed fetch, per organisation. Every key
/// starts with the <see cref="OwnerFetchMode"/>, so the two modes never read each other's entries.
/// <list type="bullet">
///   <item><b>by keys</b> (DESIGN §3.5.6): one <see cref="OwnerAnswer"/> per target entity, item,
///   target field, organisation, key and <b>plan hash</b>: the hash of the owner query as sent,
///   keys left out, so the select, the filter with its variables substituted, the owning row's
///   select, the query form and (later) the continued stages all take part. Two requests whose
///   variables differ are two plans and never answer for each other inside the TTL. The target
///   field is part of the key because two references may point at one entity through different
///   members of it. A positive answer lives <c>Cache:ResolveTtlSeconds</c>; a negative one
///   (<c>not_found</c>) <c>Cache:NegativeResolveTtlSeconds</c>, not at all when that is 0, and a
///   strict request never reads one: a record created a moment ago must not be refused as
///   missing.</item>
///   <item><b>by condition</b>: the owner's wire values of the target field, keyed by target entity,
///   organisation and the hash of the first-page query the owner is sent. A grid paging a filtered list
///   asks the same question on every block. How a parent stores its reference member is not part of
///   the answer, so each caller encodes the values for its own member.</item>
/// </list>
/// <para>
/// Each mode keeps its own budget of <c>Cache:OwnerFetchCacheMaxEntries</c>: an answer costs one unit per
/// row it holds (at least one), a list of ids one unit per id, and a semi-join answer of several thousand ids never evicts the rows
/// of the resolves beside it. An empty by-condition answer lives as long as a negative by-keys one.
/// </para>
/// <para>
/// Beside the answers, in a store of their own that the answers' budget never evicts, the paths an
/// owner said one target of a union lacks are kept per organisation, service and target
/// (<see cref="DropsKeyOf"/>), so a later request drops them before
/// it asks and reports them from the cache too: the answer does not depend on what is cached.
/// </para>
/// <para>
/// Entries are keyed by organisation, not by user: the cache assumes an owner answers every user of
/// one organisation alike, as the fleet's owners do (they scope by organisation). An owner that
/// filters rows per user must not be reached through a host that caches (set
/// <c>Cache:ResolveTtlSeconds</c> as low as it takes).
/// </para>
/// </summary>
public sealed class OwnerFetchCache : IDisposable
{
    private readonly MemoryCache rows;
    private readonly MemoryCache keys;
    private readonly MemoryCache drops;
    private readonly TimeSpan ttl;
    private readonly TimeSpan negativeTtl;
    private readonly TimeProvider time;

    /// <summary>An empty cache sized and timed by <paramref name="options"/>' <c>Cache</c> section; <paramref name="time"/> is the clock by-keys answers expire by.</summary>
    public OwnerFetchCache(OxQLOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var limit = Math.Max(1, options.Cache.OwnerFetchCacheMaxEntries);

        rows = new MemoryCache(new MemoryCacheOptions { SizeLimit = limit });
        keys = new MemoryCache(new MemoryCacheOptions { SizeLimit = limit });
        drops = new MemoryCache(new MemoryCacheOptions { SizeLimit = limit });
        ttl = TimeSpan.FromSeconds(Math.Max(1, options.Cache.ResolveTtlSeconds));
        negativeTtl = TimeSpan.FromSeconds(Math.Max(0, options.Cache.NegativeResolveTtlSeconds));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>The key of one by-keys answer; <paramref name="item"/> is the item collection of an item target, null for an entity.</summary>
    public static string KeyOf(string targetEntity, string? item, string targetField, Guid organisation, string key, string planHash) =>
        string.Join('|', nameof(OwnerFetchMode.ByKeys), targetEntity, item ?? "", targetField, organisation.ToString("D"), key, planHash);

    /// <summary>
    /// The plan hash of a by-keys owner query: its wire form built for no keys (DESIGN §3.5.6), so
    /// everything but the keys takes part, the variables already substituted by the binder.
    /// <paramref name="probed"/> marks a plan whose empty answers the existence probe told apart
    /// into <c>excluded</c> and <c>not_found</c>; without the probe every empty answer is
    /// <c>not_found</c>, which must not answer for a probed plan.
    /// </summary>
    /// <remarks>
    /// The paths of a projection are a set: they are hashed in ordinal order, so two requests that
    /// name the same paths under an alias in another order share one plan and its cached answers.
    /// </remarks>
    public static string PlanHashOf(QueryRequest template, bool probed)
    {
        ArgumentNullException.ThrowIfNull(template);

        var ordered = template with
        {
            Pipeline = template.Pipeline.Select(stage => stage.Project is { } project
                ? stage with { Project = project with { Fields = new SortedDictionary<string, int>(project.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal) } }
                : stage).ToList(),
        };
        var sent = JsonSerializer.Serialize(ordered, OxQLJson.Wire) + (probed ? "|probe" : "");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sent)))[..32];
    }

    /// <summary>The key of one by-condition answer: the organisation and the first-page query its owner is sent.</summary>
    public static string KeyOf(Guid organisation, QueryRequest ownerQuery)
    {
        ArgumentNullException.ThrowIfNull(ownerQuery);

        var sent = JsonSerializer.Serialize(ownerQuery, OxQLJson.Wire);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sent)));

        return string.Join('|', nameof(OwnerFetchMode.ByCondition), ownerQuery.EntityType, organisation.ToString("D"), hash);
    }

    /// <summary>
    /// The cached by-keys answer, or false. A <paramref name="strict"/> read skips a negative
    /// entry (DESIGN §3.5.6). The rows are clones: a node cannot have two parents.
    /// </summary>
    public bool TryGet(string key, bool strict, out OwnerAnswer? answer)
    {
        if (rows.TryGetValue(key, out Entry? cached) && cached is not null && time.GetUtcNow() < cached.Expires && !(strict && cached.Answer.IsNegative))
        {
            answer = Clone(cached.Answer);
            return true;
        }

        answer = null;
        return false;
    }

    /// <summary>
    /// Stores one by-keys answer: a positive one for <c>Cache:ResolveTtlSeconds</c>, a negative one
    /// for <c>Cache:NegativeResolveTtlSeconds</c>, and a negative one not at all when that is 0.
    /// </summary>
    public void Set(string key, OwnerAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var lifetime = answer.IsNegative ? negativeTtl : ttl;

        if (lifetime <= TimeSpan.Zero)
            return;

        rows.Set(key, new Entry(Clone(answer), time.GetUtcNow() + lifetime), new MemoryCacheEntryOptions { Size = Math.Max(1, answer.Rows.Count), AbsoluteExpirationRelativeToNow = lifetime });
    }

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

    /// <summary>
    /// Stores one by-condition answer at a size of one per id: for the TTL, or an empty one for the
    /// negative TTL (not at all when that is 0), since a row created a moment ago must not stay
    /// unselected for a minute.
    /// </summary>
    public void SetKeys(string key, IReadOnlyList<string> values)
    {
        var lifetime = values.Count == 0 ? negativeTtl : ttl;

        if (lifetime <= TimeSpan.Zero)
            return;

        keys.Set(key, values.ToArray(), new MemoryCacheEntryOptions { Size = Math.Max(1, values.Count), AbsoluteExpirationRelativeToNow = lifetime });
    }

    /// <summary>
    /// The key of the paths an owner said one union target lacks, per organisation, service and target
    /// (<c>entity</c> or <c>entity#item</c>): what a target lacks is a fact about the target, whatever
    /// query asked it.
    /// </summary>
    public static string DropsKeyOf(Guid organisation, string service, string target) =>
        string.Join('|', "Drops", organisation.ToString("D"), service, target);

    /// <summary>The paths under the alias (<c>Select</c>) and under the owning row (<c>Parent</c>) an owner said the target lacks, or false.</summary>
    public bool TryGetDrops(string key, out IReadOnlyList<string> select, out IReadOnlyList<string> parent)
    {
        if (drops.TryGetValue(key, out Drops? cached) && cached is not null && time.GetUtcNow() < cached.Expires)
        {
            select = cached.Select;
            parent = cached.Parent;
            return true;
        }

        select = [];
        parent = [];
        return false;
    }

    /// <summary>
    /// Keeps the paths an owner said the target lacks for the TTL, in a store of their own: the
    /// answers' size budget must never evict them while rows cached under the same plan live on,
    /// or a page served from the cache would lose its <c>SELECT_PATH_NOT_ON_TARGET</c> note.
    /// </summary>
    /// <remarks>What earlier requests learned of the target stays: the paths add up.</remarks>
    public void SetDrops(string key, IEnumerable<string> select, IEnumerable<string> parent)
    {
        TryGetDrops(key, out var knownSelect, out var knownParent);

        drops.Set(key,
            new Drops([.. knownSelect.Union(select, StringComparer.Ordinal)], [.. knownParent.Union(parent, StringComparer.Ordinal)], time.GetUtcNow() + ttl),
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ttl });
    }

    /// <summary>How many entries the cache holds, both modes together.</summary>
    public int Count => rows.Count + keys.Count + drops.Count;

    /// <inheritdoc/>
    public void Dispose()
    {
        rows.Dispose();
        keys.Dispose();
        drops.Dispose();
    }

    private static OwnerAnswer Clone(OwnerAnswer answer) =>
        answer with { Rows = answer.Rows.Select(row => (JsonObject)row.DeepClone()).ToList() };

    /// <summary>A stored answer with the instant it expires on this cache's clock.</summary>
    private sealed record Entry(OwnerAnswer Answer, DateTimeOffset Expires);

    /// <summary>The paths an owner said one union target lacks, with the instant they expire.</summary>
    private sealed record Drops(string[] Select, string[] Parent, DateTimeOffset Expires);
}
