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
    private CompatBinder? compat;
    private EntityModel? compatModel;

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
    }

    /// <inheritdoc/>
    public Task<QueryOutcome> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, null, cancellationToken);

    /// <inheritdoc/>
    public async Task<QueryOutcome> ExecuteAsync(QueryRequest request, int? maxTimeMs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
    public async Task<BatchOutcome> BatchAsync(BatchRequest batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

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
    public async Task<ExplainOutcome> ExplainAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
