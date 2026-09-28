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

        var owner = context with { Internal = true, Contract = 2, MaxTimeMs = MinPositive(context.MaxTimeMs, request.MaxTimeMs) };
        var results = new List<JsonNode?>(request.Queries.Count);

        foreach (var query in request.Queries)
        {
            var outcome = await engine.ExecuteAsync(query, owner, cancellationToken).ConfigureAwait(false);

            results.Add(outcome switch
            {
                QueryOutcome.Success success => JsonSerializer.SerializeToNode(success.Result, OxQLJson.Wire),
                QueryOutcome.Refused refused => JsonSerializer.SerializeToNode(refused.Refusal, OxQLJson.Wire),
                _ => null,
            });
        }

        return new BatchResponse { Results = results };
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
