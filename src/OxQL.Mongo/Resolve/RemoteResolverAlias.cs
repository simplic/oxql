using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.Mongo.Resolve;

/// <summary>
/// The former name of <see cref="KeyedFetch"/>, kept for one release: every member forwards to it.
/// Remote resolve is the keyed fetch <i>by keys</i>, the semi-join the keyed fetch <i>by condition</i>.
/// </summary>
[Obsolete("Use KeyedFetch; RemoteResolver is kept for one release.")]
public sealed class RemoteResolver
{
    private readonly KeyedFetch fetch;

    /// <summary>A forwarder to a new <see cref="KeyedFetch"/> over the same arguments.</summary>
    public RemoteResolver(IRemoteQueryClient client, OwnerFetchCache cache, OxQLOptions options) =>
        fetch = new KeyedFetch(client, cache, options);

    /// <inheritdoc cref="KeyedFetch.ServiceKeyOf"/>
    public static string ServiceKeyOf(string targetEntity) => KeyedFetch.ServiceKeyOf(targetEntity);

    /// <inheritdoc cref="KeyedFetch.ValueAt"/>
    public static BsonValue? ValueAt(BsonDocument document, string storage) => KeyedFetch.ValueAt(document, storage);

    /// <inheritdoc cref="KeyedFetch.ByConditionAsync"/>
    public Task<Refusal?> SemiJoinAsync(CompiledQuery compiled, RequestContext context, TimeSpan remaining, CancellationToken cancellationToken) =>
        fetch.ByConditionAsync(compiled, context, remaining, cancellationToken);

    /// <inheritdoc cref="KeyedFetch.ByKeysAsync"/>
    public Task<ResolveResult> ResolveAsync(CompiledQuery compiled, IReadOnlyList<BsonDocument> rows, RequestContext context, TimeSpan remaining, CancellationToken cancellationToken) =>
        fetch.ByKeysAsync(compiled, rows, context, remaining, strict: false, cancellationToken);
}
