// LEGACY(compat.v1): contract 1 legacy support. Remove with contract 1 compatibility; see src/docs/LEGACY.md.
using Microsoft.AspNetCore.Http;
using OxQL.Core.Legacy;
using OxQL.Core.Models;

namespace OxQL.AspNetCore.Legacy;

/// <summary>
/// What <c>GET /oxql/health</c> lists for a caller whose requests the legacy pipeline answers: a
/// request without the contract header on a host with <c>Compat:Enabled</c>,
/// <c>Compat:Legacy:Enabled</c> and <c>Compat:Legacy:Always</c> (the default) that has a legacy
/// pipeline. Health says of such a caller's requests what holds for them, so the capabilities and
/// the limits are the ones that pipeline and its two routes honour, under the names health uses
/// for every caller. Nothing is listed that a request of that caller would not get: no paging by
/// offset (the page is read by cursor only), no <c>any</c>, no remote join, and no explain, since an
/// explain describes the rewrite's reading of a request and not this pipeline's.
/// <para>
/// The other members of the answer (<c>status</c>, <c>service</c>, <c>engine</c>, <c>remote</c>) say
/// what the host is and are the same for every caller; a host that reads another host's shallow
/// health without the header finds the engine's version and contract and the two limits it sizes
/// its batches by (<c>maxBatchQueries</c>, <c>maxPageSize</c>) here as well.
/// </para>
/// </summary>
internal sealed record LegacyHealth(IReadOnlyList<string> Capabilities, object Limits)
{
    /// <summary>
    /// The capability names that hold for a request the legacy pipeline answers: the batch route
    /// answers each entry as the query route does, and the host answers contract 1.
    /// </summary>
    internal static IReadOnlyList<string> Honoured { get; } = ["batch", "compat.v1"];

    /// <summary>
    /// The list for the caller of <paramref name="http"/>, or null when the legacy pipeline does not
    /// answer that caller's requests first: the request names a contract, compatibility or the
    /// legacy support is off, <c>Always</c> is off (the rewrite answers first), or the host has no
    /// legacy pipeline.
    /// </summary>
    internal static LegacyHealth? For(OxQLOptions options, HttpContext? http)
    {
        ArgumentNullException.ThrowIfNull(options);

        var legacy = options.Compat.Legacy;

        if (http is null || !options.Compat.Enabled || !legacy.Enabled || !legacy.Always || OxQLQueryService.ContractOf(options, http) != 1)
            return null;

        if (http.RequestServices?.GetService(typeof(ILegacyPipeline)) is not ILegacyPipeline)
            return null;

        var limits = options.Limits;

        // The limits the legacy validation and the two routes check a request against, in health's order.
        return new LegacyHealth(Honoured, new
        {
            maxPageSize = limits.MaxPageSize,
            defaultPageSize = limits.DefaultPageSize,
            maxPipelineStages = limits.MaxPipelineStages,
            maxLookupStages = limits.MaxLookupStages,
            maxUnwindStages = limits.MaxUnwindStages,
            maxGroupFields = limits.MaxGroupFields,
            maxProjectionFields = limits.MaxProjectionFields,
            maxRequestBytes = limits.MaxRequestBytes,
            maxBatchQueries = limits.MaxBatchQueries,
            regexMaxLength = limits.RegexMaxLength,
        });
    }
}
