// LEGACY(compat.v1): contract 1 legacy support. Remove with contract 1 compatibility; see src/docs/LEGACY.md.
using System.Text.Json;
using OxQL.Core.Binding;
using OxQL.Core.Engine;

namespace OxQL.Core.Legacy;

/// <summary>
/// The pipeline ported from the 1.1 query path, kept for contract 1 callers while compatibility is on: validate,
/// normalise, build the aggregate from the request as written, run it, answer the stored documents.
/// It knows no model beyond the entity's collection; the organisation filter, the limits, the time
/// ceiling and the cancellation are this engine's.
/// </summary>
public interface ILegacyPipeline
{
    /// <summary>Answers <paramref name="request"/>, the request body as the caller wrote it, under <paramref name="context"/>.</summary>
    Task<QueryOutcome> ExecuteAsync(JsonElement request, RequestContext context, CancellationToken cancellationToken);
}
