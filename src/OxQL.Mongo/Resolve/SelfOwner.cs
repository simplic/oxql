using System.Text.Json;
using System.Text.Json.Nodes;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.Mongo.Resolve;

/// <summary>
/// This host as the owner of its own entities (DESIGN §3.5.2 step 4, §3.5.8): the keyed fetch's
/// owner client for a local target. It runs every owner query of a batch through the engine's own
/// <see cref="IQueryEngine.ExecuteAsync"/> under the caller's request context, marked internal (so
/// the query may carry <c>keyedBy</c>) and contract 2 (the owner query's vocabulary), with the
/// batch's <c>maxTimeMs</c> as its ceiling, and answers in the batch's wire form, exactly as a
/// remote owner's internal batch route does. The local-owner case so adds no second fetch path.
/// <para>
/// The queries of a batch run side by side, at most <c>Execution:BatchConcurrency</c> at once, and
/// answer in the order they were asked. The batch's <c>maxTimeMs</c> is one deadline for all of
/// them: each runs under what is left of it when it starts. The host's addon definitions are read
/// one at a time (<see cref="SerialAddonSource"/>), and an owner query two of them would send is
/// sent once (<see cref="BatchFlights"/>).
/// </para>
/// <para>
/// An owner query holds only the key match, the target's filter and projection (and, later, the
/// stages continued under the alias), never the resolve that produced it, so a query this host
/// sends itself carries strictly fewer join stages than the one it came from and recursion ends by
/// construction.
/// </para>
/// </summary>
public sealed class SelfOwner(IQueryEngine engine, RequestContext context) : IRemoteQueryClient
{
    private readonly IQueryEngine engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly RequestContext context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc/>
    public async Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var owner = context with
        {
            Internal = true,
            Contract = 2,
            AddonSource = SerialAddonSource.Of(context.AddonSource),
            BatchShare = context.BatchShare ?? (request.Queries.Count > 1 ? new BatchFlights() : null),
        };
        var deadline = request.MaxTimeMs is { } ceiling and > 0 ? DateTime.UtcNow.AddMilliseconds(ceiling) : (DateTime?)null;

        var results = await BatchRun.RunAsync(request.Queries, context.Options.Execution.EffectiveBatchConcurrency, async (query, token) =>
        {
            // One deadline for the batch: a query that starts later has less of it, never a budget of its own.
            var left = deadline is { } until ? Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalMilliseconds)) : (int?)null;
            var outcome = await engine.ExecuteAsync(query, owner with { MaxTimeMs = MinPositive(context.MaxTimeMs, left) }, token).ConfigureAwait(false);

            return outcome switch
            {
                QueryOutcome.Success success => JsonSerializer.SerializeToNode(success.Result, OxQLJson.Wire),
                QueryOutcome.Refused refused => JsonSerializer.SerializeToNode(refused.Refusal, OxQLJson.Wire),
                _ => null,
            };
        }, cancellationToken).ConfigureAwait(false);

        return new BatchResponse { Results = [.. results] };
    }

    /// <summary>This host always knows itself.</summary>
    public bool IsConfigured(string serviceKey) => true;

    /// <summary>This host always reaches itself.</summary>
    public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => Task.FromResult(true);

    private static int? MinPositive(int? left, int? right) => (left, right) switch
    {
        ({ } a and > 0, { } b and > 0) => Math.Min(a, b),
        ({ } a and > 0, _) => a,
        (_, { } b and > 0) => b,
        _ => null,
    };
}
