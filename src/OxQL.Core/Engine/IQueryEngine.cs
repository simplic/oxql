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
    /// and answers the errors, the shape after each stage and the compiled form. A request that
    /// does not bind is an answer with <c>valid: false</c>. Reads Mongo only for the addon
    /// definitions and, when <see cref="ExplainRequest.IncludesIndexes"/>, the index lists.
    /// A plain <see cref="QueryRequest"/> converts to a request without describe or include.
    /// </summary>
    Task<ExplainOutcome> ExplainAsync(ExplainRequest request, RequestContext context, CancellationToken cancellationToken = default);
}

/// <summary>Where the engine gets the host's entity model: built once, after every service registration, before the first request.</summary>
public interface IEntityModelProvider
{
    /// <summary>The model. Throws when the host has not built one yet.</summary>
    EntityModel Model { get; }

    /// <summary>
    /// The revision of the schema document the host publishes for the model, which explain answers
    /// in <c>schemaRevision</c> (DESIGN §4.3); null when the host publishes none (the default).
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
