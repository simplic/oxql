using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OxQL.AspNetCore.Batch;
using OxQL.AspNetCore.Compat;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model.Addon;

namespace OxQL.AspNetCore;

/// <summary>
/// The host's face of the engine: builds the request context from the scope provider and the
/// contract header, rewrites a contract 1 request through the <see cref="CompatBinder"/> and
/// logs it, runs the engine, and hands the outcome to the controller.
/// <para>
/// It is also the boundary that guarantees the shape of an answer. Everything below it — the
/// scope provider, the rewrite, the binder, the compiler, the row encoding — is written to
/// refuse rather than throw, but a caller must get an envelope even where one of them throws:
/// a bare HTTP 500 carries no code, and a client reports it as "the service could not be
/// reached". <see cref="Guarded"/> turns any escaped exception into a coded
/// <c>INTERNAL_ERROR</c> refusal and logs it with the request's correlation id, which the
/// refusal's message names so the log line can be found from the response.
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

    /// <summary>Creates the service for one request; the optional collaborators default to none.</summary>
    public OxQLQueryService(
        IQueryEngine engine,
        IOxQLScopeProvider scope,
        OxQLOptions options,
        IEntityModelProvider models,
        IAddonDefinitionSource? addons = null,
        IHttpContextAccessor? httpContextAccessor = null,
        ILoggerFactory? loggerFactory = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.models = models ?? throw new ArgumentNullException(nameof(models));
        this.addons = addons ?? EmptyAddonDefinitionSource.Instance;
        this.httpContextAccessor = httpContextAccessor;
        compatLog = loggerFactory?.CreateLogger(CompatLogCategory) ?? NullLogger.Instance;
        faultLog = loggerFactory?.CreateLogger(typeof(OxQLQueryService).FullName!) ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public Task<QueryOutcome> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, null, cancellationToken);

    /// <inheritdoc/>
    public Task<QueryOutcome> ExecuteAsync(QueryRequest request, int? maxTimeMs, CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, maxTimeMs, internalCall: false, cancellationToken);

    private Task<QueryOutcome> ExecuteAsync(QueryRequest request, int? maxTimeMs, bool internalCall, CancellationToken cancellationToken) =>
        Guarded(QueryOutcome.Of, () => RunAsync(request, maxTimeMs, internalCall, cancellationToken), cancellationToken);

    private async Task<QueryOutcome> RunAsync(QueryRequest request, int? maxTimeMs, bool internalCall, CancellationToken cancellationToken)
    {
        // A null query is a caller error, not a fault: the batch route can carry one
        // ({"queries":[null]}) and the body of the query route can be the literal `null`.
        if (request is null)
            return QueryOutcome.Of(Refusal.Validation([new QueryValidationError
            {
                Code = Codes.UnknownStage,
                Message = "The request is empty; a query carries an entityType and a pipeline.",
            }]));

        var context = await ContextAsync(maxTimeMs, internalCall, cancellationToken);

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
        BatchAsync(batch, internalCall: false, cancellationToken);

    /// <inheritdoc/>
    public Task<BatchOutcome> BatchAsync(BatchRequest batch, bool internalCall, CancellationToken cancellationToken = default) =>
        Guarded(refusal => (BatchOutcome)new BatchOutcome.Refused(refusal), () => RunBatchAsync(batch, internalCall, cancellationToken), cancellationToken);

    private async Task<BatchOutcome> RunBatchAsync(BatchRequest batch, bool internalCall, CancellationToken cancellationToken)
    {
        if (batch is null || batch.Queries is null)
            return new BatchOutcome.Refused(Refusal.Validation([new QueryValidationError
            {
                Code = Codes.UnknownStage,
                Message = "The batch is empty; a batch carries a queries array.",
            }]));

        // A member the batch does not have is refused rather than dropped: a batch-level strict
        // would otherwise run every query without it. Contract 1 ignores it, as it always has.
        if (batch.Unknown is { Count: > 0 } unknown && ContractOf(httpContextAccessor?.HttpContext) != 1)
            return new BatchOutcome.Refused(Refusal.Validation([new QueryValidationError
            {
                Code = Codes.UnknownRequestMember,
                Message = $"'{string.Join(", ", unknown.Keys)}' is not a member of a batch; a batch carries queries and maxTimeMs, and each query its own strict.",
            }]));

        if (batch.Queries.Count > options.Limits.MaxBatchQueries)
            return new BatchOutcome.Refused(Refusal.Validation([new QueryValidationError
            {
                Code = Codes.BatchTooLarge,
                Message = $"The batch carries {batch.Queries.Count} queries; the limit is {options.Limits.MaxBatchQueries}.",
            }]));

        var results = new List<System.Text.Json.Nodes.JsonNode?>(batch.Queries.Count);

        // A batch's maxTimeMs bounds the whole batch, not each query: the queries run one after
        // another, each under what is left, so an owner answers within the time its caller waits.
        var deadline = batch.MaxTimeMs is { } ceiling ? DateTime.UtcNow.AddMilliseconds(ceiling) : (DateTime?)null;

        // Sequential per host: the parallelism of a batch is across services, not within one.
        foreach (var query in batch.Queries)
        {
            var left = deadline is { } until ? Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalMilliseconds)) : (int?)null;
            var outcome = await ExecuteAsync(query, left, internalCall, cancellationToken);

            results.Add(outcome switch
            {
                QueryOutcome.Success success => System.Text.Json.JsonSerializer.SerializeToNode(success.Result, OxQLJson.Wire),
                QueryOutcome.Refused refused => System.Text.Json.JsonSerializer.SerializeToNode(refused.Refusal, OxQLJson.Wire),
                _ => null,
            });
        }

        return new BatchOutcome.Success(new BatchResponse { Results = results });
    }

    /// <inheritdoc/>
    public Task<ExplainOutcome> ExplainAsync(ExplainRequest request, CancellationToken cancellationToken = default) =>
        ExplainAsync(request, internalCall: false, cancellationToken);

    /// <inheritdoc/>
    public Task<ExplainOutcome> ExplainAsync(ExplainRequest request, bool internalCall, CancellationToken cancellationToken = default) =>
        Guarded(refusal => (ExplainOutcome)new ExplainOutcome.Refused(refusal), () => RunExplainAsync(request, internalCall, cancellationToken), cancellationToken);

    private async Task<ExplainOutcome> RunExplainAsync(ExplainRequest request, bool internalCall, CancellationToken cancellationToken)
    {
        if (request?.Query is null)
            return new ExplainOutcome.Refused(Refusal.Validation([new QueryValidationError
            {
                Code = Codes.UnknownStage,
                Message = "The request is empty; a query carries an entityType and a pipeline.",
            }]));

        var context = await ContextAsync(null, internalCall, cancellationToken);

        if (context.Contract == 1)
        {
            var rewrite = Compat(request.Query, context);

            // A contract 1 request that does not rewrite is an answer too (DESIGN §4.1): valid false with the errors.
            if (rewrite.Refusal is not null)
            {
                if (rewrite.Refusal.Status != 400)
                    return new ExplainOutcome.Refused(rewrite.Refusal);

                // Each describe entry says why nothing is described, rather than no answer at all.
                var first = rewrite.Refusal.Errors is [var head, ..] ? head : null;
                var invalid = ExplainResult.Invalid(context.Contract, EngineOf(), rewrite.Refusal.Errors ?? []);

                return new ExplainOutcome.Success(invalid with
                {
                    Describe = Describe.Refused(request, first?.Code ?? Codes.LegacyStageUnsupported, first?.Message ?? "The contract 1 request does not rewrite."),
                });
            }

            request = request with { Query = rewrite.Request! };
        }

        return await engine.ExplainAsync(request, context, cancellationToken);
    }

    /// <summary>The engine block of an explain answer: the version and this host's capabilities.</summary>
    private ExplainEngine EngineOf() => new()
    {
        Version = EngineCapabilities.Version,
        Capabilities = EngineCapabilities.Of(engine is IEngineFeatures features && features.RemoteResolve, options.Compat.Enabled, options.Explain.Enabled),
    };

    /// <summary>
    /// Runs <paramref name="work"/> and turns anything it throws into a coded refusal of the
    /// right outcome shape. A cancellation the caller asked for is not a fault and travels on;
    /// any other cancellation (a collaborator's own timeout) is a fault like the rest.
    /// </summary>
    private async Task<T> Guarded<T>(Func<Refusal, T> refused, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var correlation = CorrelationOrTrace();

            faultLog.LogError(exception, "OxQL unhandled fault on the query path; answered INTERNAL_ERROR correlation={CorrelationId}", correlation);

            return refused(Refusal.Internal($"The engine could not answer this request; the detail is in the service log under the correlation id '{correlation}'."));
        }
    }

    /// <summary>The correlation id of the current request for the fault line; the scope provider may be what failed, so it is not relied on.</summary>
    private string CorrelationOrTrace()
    {
        var httpContext = httpContextAccessor?.HttpContext;
        string? correlation = null;

        try
        {
            correlation = scope.CorrelationId(httpContext);
        }
        catch (Exception)
        {
            // The trace identifier below stands in.
        }

        return LogText.Of(correlation ?? httpContext?.TraceIdentifier) ?? "unknown";
    }

    /// <summary>The context of the current request.</summary>
    public ValueTask<RequestContext> ContextAsync(int? maxTimeMs, CancellationToken cancellationToken) =>
        ContextAsync(maxTimeMs, internalCall: false, cancellationToken);

    /// <summary>The context of the current request; <paramref name="internalCall"/> marks it as arriving over the internal route.</summary>
    public async ValueTask<RequestContext> ContextAsync(int? maxTimeMs, bool internalCall, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor?.HttpContext;

        return new RequestContext
        {
            Organisation = await scope.OrganisationAsync(httpContext, cancellationToken),
            Contract = ContractOf(httpContext),
            AddonSource = addons,
            Options = options,
            UserId = scope.UserId(httpContext),
            // The scope provider may read the correlation off a caller's header; every log line reads it from here.
            CorrelationId = LogText.Of(scope.CorrelationId(httpContext)),
            MaxTimeMs = maxTimeMs,
            Internal = internalCall,
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
        var rewrite = new CompatBinder(models.Model).Rewrite(request);

        compatLog.LogInformation(
            "OxQL.Compat contract 1 request for {Entity}: firstLegacyPath={FirstLegacyPath} legacyPaths={LegacyPaths} typeHints={TypeHints} refused={Refused} user={UserId} org={OrganisationId} correlation={CorrelationId}",
            LogText.Of(request.EntityType),
            LogText.Of(rewrite.FirstLegacyPath),
            rewrite.LegacyPaths,
            rewrite.TypeHints,
            rewrite.Refusal?.Errors?[0].Code,
            context.UserId,
            context.Organisation,
            context.CorrelationId);

        return rewrite;
    }
}
