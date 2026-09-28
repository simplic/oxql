using OxQL.Core.Models;
using OxQL.Model.Addon;

namespace OxQL.Core.Binding;

/// <summary>What the host knows about the caller that the engine needs: the organisation scope, the contract, and the addon definitions.</summary>
public sealed record RequestContext
{
    /// <summary>The caller's organisation. Null refuses every request with 403 before binding; the engine never compares against null.</summary>
    public Guid? Organisation { get; init; }

    /// <summary>The contract the request was written for: 2, or 1 in compatibility mode.</summary>
    public int Contract { get; init; } = 2;

    /// <summary>The organisation's addon definitions, read per entity the pipeline enters.</summary>
    public IAddonDefinitionSource AddonSource { get; init; } = EmptyAddonDefinitionSource.Instance;

    /// <summary>The engine options in force.</summary>
    public required OxQLOptions Options { get; init; }

    /// <summary>The caller's user id, for the log line and for forwarding to a remote owner.</summary>
    public string? UserId { get; init; }

    /// <summary>The request's correlation id, for the log line and for forwarding.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>A per-request ceiling on the aggregate, under the configured one; a batch sets it.</summary>
    public int? MaxTimeMs { get; init; }

    /// <summary>
    /// Whether the request came over an internal call: the internal batch route
    /// (<c>IOxQLQueryService.BatchAsync(batch, internalCall: true, …)</c>) or this host's own keyed
    /// fetch in process. Only such a request may carry <c>keyedBy</c>; the route is the signal, no
    /// header carries it.
    /// </summary>
    public bool Internal { get; init; }
}
