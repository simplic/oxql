using OxQL.Core.Binding;
using OxQL.Core.Models;
using OxQL.Model;

namespace OxQL.Core.Engine;

/// <summary>The engine: binds a request against the model, compiles and executes it, encodes the rows.</summary>
public interface IQueryEngine
{
    /// <summary>Executes one request. Never throws for a caller error; a refusal is an outcome.</summary>
    Task<QueryOutcome> ExecuteAsync(QueryRequest request, RequestContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything about one request without executing it (DESIGN §4): binds, compiles when it binds,
    /// and answers the errors, each stage with the shape of the row after it, the aliases, the types
    /// and, on request, the compiled form. A request that does not bind is an answer with
    /// <c>valid: false</c>. Reads Mongo only for the addon definitions and, when
    /// <see cref="ExplainRequest.IncludesIndexes"/>, the index lists. A plain
    /// <see cref="QueryRequest"/> converts to a request with the default includes.
    /// </summary>
    Task<ExplainOutcome> ExplainAsync(ExplainRequest request, RequestContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Explains the checks of one internal explain batch (improvement plan §3.E), each as
    /// <see cref="ExplainAsync"/> explains it, within <paramref name="budget"/> together: the time the
    /// origin has left and the owner calls it may still cause. One outcome per request, in order; a
    /// request the time ran out before is refused. The default explains them one after another, each
    /// with what the ones before it left; an engine that asks owners of its own asks them once per
    /// round for all of the requests together.
    /// </summary>
    async Task<IReadOnlyList<ExplainOutcome>> ExplainBatchAsync(IReadOnlyList<ExplainRequest> requests, RequestContext context, ExplainBudget? budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var outcomes = new List<ExplainOutcome>(requests.Count);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var calls = budget?.Calls ?? 0;

        foreach (var request in requests)
        {
            var left = budget is null ? null : new ExplainBudget(budget.Ms - (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds), Math.Max(0, calls));

            if (left is { Ms: <= 0 })
            {
                outcomes.Add(new ExplainOutcome.Refused(Refusal.Timeout("The time of the explain this check belongs to ran out before it.")));
                continue;
            }

            var outcome = await ExplainAsync(request with { Budget = left }, context, cancellationToken).ConfigureAwait(false);

            if (outcome is ExplainOutcome.Success { Result.Owners: { } owners })
                calls -= owners.OfType<System.Text.Json.Nodes.JsonObject>().Sum(ExplainBatchRequest.CallsOfOwner);

            outcomes.Add(outcome);
        }

        return outcomes;
    }
}

/// <summary>Where the engine gets the host's entity model: built once, after every service registration, before the first request.</summary>
public interface IEntityModelProvider
{
    /// <summary>The model. Throws when the host has not built one yet.</summary>
    EntityModel Model { get; }

    /// <summary>
    /// The revision of the schema document the host publishes for the model, which explain answers
    /// in <c>revision.schema</c> (DESIGN §4.3); null when the host publishes none (the default).
    /// </summary>
    string? SchemaRevision => null;
}

/// <summary>A provider over a model the host built and handed over.</summary>
public sealed class StaticEntityModelProvider(EntityModel model) : IEntityModelProvider
{
    /// <inheritdoc/>
    public EntityModel Model { get; } = model ?? throw new ArgumentNullException(nameof(model));
}

/// <summary>
/// A provider that builds the model on first use, which is after startup and so after every
/// registration; for hosts without a startup filter of their own.
/// </summary>
public sealed class LazyEntityModelProvider(Func<EntityModel> build) : IEntityModelProvider
{
    private readonly Lazy<EntityModel> model = new(build ?? throw new ArgumentNullException(nameof(build)), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <inheritdoc/>
    public EntityModel Model => model.Value;
}
