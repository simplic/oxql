using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OxQL.AspNetCore.Batch;
using OxQL.AspNetCore.Compat;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Addon;

namespace OxQL.AspNetCore;

/// <summary>
/// The host's face of the engine: builds the request context from the scope provider and the
/// contract header, rewrites a contract 1 request through the <see cref="CompatBinder"/> and
/// logs it, runs the engine, and hands the outcome to the controller.
/// <para>
/// It is also the boundary that guarantees the shape of an answer. Everything below it — the
/// binder, the compiler, the operand coercion, the row encoding — is written to refuse rather
/// than throw, and four separate live findings showed that where one of them throws anyway
/// the caller gets a bare HTTP 500: no code, no path, no stage, no envelope, and a client that
/// reports "the service could not be reached" for what is a caller error or an engine defect.
/// <see cref="Guarded"/> turns any escaped exception into a coded <c>INTERNAL_ERROR</c>
/// refusal, logged with its detail and its correlation id. The four causes are fixed at their
/// sites; this is what covers the fifth.
/// </para>
/// </summary>
public sealed class OxQLQueryService : IOxQLQueryService
{
    /// <summary>The contract header.</summary>
    public const string ContractHeader = "X-OxQL-Contract";

    /// <summary>The log category every contract 1 request is written under.</summary>
    public const string CompatLogCategory = "OxQL.Compat";

    private readonly IQueryEngine engine;
    private readonly IOxQLScopeProvider scope;
    private readonly OxQLOptions options;
    private readonly IEntityModelProvider models;
    private readonly IAddonDefinitionSource addons;
    private readonly IHttpContextAccessor? httpContextAccessor;
    private readonly ILogger compatLog;
    private readonly ILogger faultLog;
    private readonly bool includeErrorDetails;
    private CompatBinder? compat;
    private EntityModel? compatModel;

    public OxQLQueryService(
        IQueryEngine engine,
        IOxQLScopeProvider scope,
        OxQLOptions options,
        IEntityModelProvider models,
        IAddonDefinitionSource? addons = null,
        IHttpContextAccessor? httpContextAccessor = null,
        ILoggerFactory? loggerFactory = null,
        bool includeErrorDetails = false)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.models = models ?? throw new ArgumentNullException(nameof(models));
        this.addons = addons ?? EmptyAddonDefinitionSource.Instance;
        this.httpContextAccessor = httpContextAccessor;
        compatLog = loggerFactory?.CreateLogger(CompatLogCategory) ?? NullLogger.Instance;
        faultLog = loggerFactory?.CreateLogger(typeof(OxQLQueryService).FullName!) ?? NullLogger.Instance;
        this.includeErrorDetails = includeErrorDetails;
    }

    /// <inheritdoc/>
    public Task<QueryOutcome> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, null, cancellationToken);

    /// <inheritdoc/>
    public Task<QueryOutcome> ExecuteAsync(QueryRequest request, int? maxTimeMs, CancellationToken cancellationToken = default) =>
        Guarded(QueryOutcome.Of, () => RunAsync(request, maxTimeMs, cancellationToken));

    private async Task<QueryOutcome> RunAsync(QueryRequest request, int? maxTimeMs, CancellationToken cancellationToken)
    {
        // A null query is a caller error, not a fault: the batch route can carry one
        // ({"queries":[null]}) and the body of the query route can be the literal `null`.
        if (request is null)
            return QueryOutcome.Of(Refusal.Validation([new QueryValidationError
            {
                Code = Codes.UnknownStage,
                Message = "The request is empty; a query carries an entityType and a pipeline.",
            }]));

        var context = await ContextAsync(maxTimeMs, cancellationToken);

        if (context.Contract == 1)
        {
            var rewrite = Compat(request, context);

            if (rewrite.Refusal is not null)
                return QueryOutcome.Of(rewrite.Refusal);

            request = rewrite.Request;
        }

        return await engine.ExecuteAsync(request, context, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<BatchOutcome> BatchAsync(BatchRequest batch, CancellationToken cancellationToken = default) =>
        Guarded(refusal => (BatchOutcome)new BatchOutcome.Refused(refusal), () => RunBatchAsync(batch, cancellationToken));

    private async Task<BatchOutcome> RunBatchAsync(BatchRequest batch, CancellationToken cancellationToken)
    {
        if (batch is null || batch.Queries is null)
            return new BatchOutcome.Refused(Refusal.Validation([new QueryValidationError
            {
                Code = Codes.UnknownStage,
                Message = "The batch is empty; a batch carries a queries array.",
            }]));

        if (batch.Queries.Count > options.Limits.MaxBatchQueries)
            return new BatchOutcome.Refused(Refusal.Validation([new QueryValidationError
            {
                Code = Codes.BatchTooLarge,
                Message = $"The batch carries {batch.Queries.Count} queries; the limit is {options.Limits.MaxBatchQueries}.",
            }]));

        var results = new List<System.Text.Json.Nodes.JsonNode?>(batch.Queries.Count);

        // Sequential per host: the parallelism of a batch is across services, not within one.
        foreach (var query in batch.Queries)
        {
            var outcome = await ExecuteAsync(query, batch.MaxTimeMs, cancellationToken);

            results.Add(outcome switch
            {
                QueryOutcome.Success success => System.Text.Json.JsonSerializer.SerializeToNode(success.Result, Controllers.JsonOptions.Wire),
                QueryOutcome.Refused refused => System.Text.Json.JsonSerializer.SerializeToNode(refused.Refusal, Controllers.JsonOptions.Wire),
                _ => null,
            });
        }

        return new BatchOutcome.Success(new BatchResponse { Results = results });
    }

    /// <inheritdoc/>
    public Task<ExplainOutcome> ExplainAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
        Guarded(refusal => (ExplainOutcome)new ExplainOutcome.Refused(refusal), () => RunExplainAsync(request, cancellationToken));

    private async Task<ExplainOutcome> RunExplainAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
            return new ExplainOutcome.Refused(Refusal.Validation([new QueryValidationError
            {
                Code = Codes.UnknownStage,
                Message = "The request is empty; a query carries an entityType and a pipeline.",
            }]));

        var context = await ContextAsync(null, cancellationToken);

        if (context.Contract == 1)
        {
            var rewrite = Compat(request, context);

            if (rewrite.Refusal is not null)
                return new ExplainOutcome.Refused(rewrite.Refusal);

            request = rewrite.Request;
        }

        return await engine.ExplainAsync(request, context, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="work"/> and turns anything it throws into a coded refusal of the
    /// right outcome shape. A cancellation the caller asked for is not a fault and travels on.
    /// </summary>
    private async Task<T> Guarded<T>(Func<Refusal, T> refused, Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            faultLog.LogError(exception, "OxQL unhandled fault on the query path; answered INTERNAL_ERROR");

            return refused(Refusal.Internal(includeErrorDetails ? exception.Message : null));
        }
    }

    /// <summary>The context of the current request.</summary>
    public async ValueTask<RequestContext> ContextAsync(int? maxTimeMs, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor?.HttpContext;

        return new RequestContext
        {
            Organisation = await scope.OrganisationAsync(httpContext, cancellationToken),
            Contract = ContractOf(httpContext),
            AddonSource = addons,
            Options = options,
            UserId = scope.UserId(httpContext),
            CorrelationId = scope.CorrelationId(httpContext),
            MaxTimeMs = maxTimeMs,
        };
    }

    /// <summary>
    /// The contract a request was written for: while <c>Compat:Enabled</c>, the header decides
    /// and a missing header means contract 1; once compatibility is switched off every request
    /// is contract 2, header or not.
    /// </summary>
    public int ContractOf(HttpContext? httpContext)
    {
        if (!options.Compat.Enabled)
            return 2;

        if (httpContext is not null && httpContext.Request.Headers.TryGetValue(ContractHeader, out var values) && int.TryParse(values.FirstOrDefault(), out var contract))
            return contract == 1 ? 1 : 2;

        return 1;
    }

    /// <summary>Rewrites a contract 1 request and writes the compat log line.</summary>
    private CompatRewrite Compat(QueryRequest request, RequestContext context)
    {
        var model = models.Model;

        if (compat is null || !ReferenceEquals(compatModel, model))
        {
            compat = new CompatBinder(model);
            compatModel = model;
        }

        var rewrite = compat.Rewrite(request);

        compatLog.LogInformation(
            "OxQL.Compat contract 1 request for {Entity}: firstLegacyPath={FirstLegacyPath} legacyPaths={LegacyPaths} typeHints={TypeHints} refused={Refused} user={UserId} org={OrganisationId} correlation={CorrelationId}",
            request.EntityType,
            rewrite.FirstLegacyPath,
            rewrite.LegacyPaths,
            rewrite.TypeHints,
            rewrite.Refusal?.Errors?[0].Code,
            context.UserId,
            context.Organisation,
            context.CorrelationId);

        return rewrite;
    }
}
