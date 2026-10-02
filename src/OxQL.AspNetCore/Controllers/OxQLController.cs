using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using OxQL.AspNetCore.Batch;
using OxQL.AspNetCore.Health;
using OxQL.AspNetCore.Models;
using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.AspNetCore.Controllers;

/// <summary>The query surface: <c>POST query</c>, <c>POST batch</c>, <c>GET health</c>, <c>POST explain</c>.</summary>
[ApiController]
[Route("[controller]")]
[TypeFilter(typeof(RequestSizeFilter))]
public class OxQLController : ControllerBase
{
    private readonly IOxQLQueryService queryService;
    private readonly OxQLOptions options;
    private readonly ILogger<OxQLController> logger;

    /// <summary>The public routes over <paramref name="queryService"/>, publishing <paramref name="options"/>' limits on health.</summary>
    public OxQLController(IOxQLQueryService queryService, OxQLOptions options, ILogger<OxQLController> logger)
    {
        this.queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Executes one query.</summary>
    [HttpPost("query")]
    [ProducesResponseType(typeof(QueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Refusal), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Refusal), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Refusal), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(Refusal), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(Refusal), StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Query([FromBody] QueryRequest request, CancellationToken cancellationToken)
    {
        var outcome = await queryService.ExecuteAsync(request, cancellationToken);

        return outcome switch
        {
            QueryOutcome.Success success => Ok(success.Result),
            QueryOutcome.Refused refused => Log(refused.Refusal).ToActionResult(),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>
    /// Executes several queries in order under one time ceiling; always 200, each entry carries
    /// its own outcome. More queries than <c>Limits:MaxBatchQueries</c> is <c>BATCH_TOO_LARGE</c>.
    /// </summary>
    [HttpPost("batch")]
    [ProducesResponseType(typeof(BatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Refusal), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Refusal), StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Batch([FromBody] BatchRequest batch, CancellationToken cancellationToken)
    {
        var outcome = await queryService.BatchAsync(batch, cancellationToken);

        return outcome switch
        {
            BatchOutcome.Success success => Ok(success.Response),
            BatchOutcome.Refused refused => Log(refused.Refusal).ToActionResult(),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>
    /// The engine version, contract, capabilities and limits, and the state of every service the
    /// model references remotely. Anonymous and always reachable, so it answers "why is my list
    /// not working" from a browser. It never waits for another service: the reachability is the
    /// last one measured, refreshed in the background at most once per
    /// <c>OxQL:Cache:HealthProbeTtlSeconds</c>, and <c>reachable</c> is null until the first
    /// measurement has finished. With <c>?shallow=true</c> the answer leaves <c>remote</c> out and
    /// starts no measurement; that is the form one host asks of another, so a probe never sets off
    /// the probed service's own probes.
    /// </summary>
    [HttpGet("health")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Health(
        [FromServices] IQueryEngine engine,
        [FromServices] IEntityModelProvider models,
        [FromServices] RemoteHealthProbe probe,
        [FromServices] IRemoteQueryClient? client,
        [FromQuery] bool shallow = false)
    {
        var remote = engine is IEngineFeatures features && features.RemoteResolve;
        var services = client is null || shallow ? null : probe.State(ModelOrNull(models), client);
        var degraded = services is not null && services.Any(state => !state.Configured || state.Reachable == false);

        return Ok(new
        {
            status = degraded ? "degraded" : "healthy",
            service = "oxql",
            engine = new
            {
                version = EngineCapabilities.Version,
                contract = EngineCapabilities.Contract,
            },
            capabilities = EngineCapabilities.Of(remote, options.Compat.Enabled, options.Explain.Enabled),
            limits = Limits(options),
            remote = services,
        });
    }

    /// <summary>
    /// Every limit the engine enforces. The schema document publishes the ones a caller checks a
    /// request against before sending it; this is the whole set, for diagnosis, the chain
    /// budget and the negative cache lifetime among them (DESIGN §3.7).
    /// </summary>
    private static object Limits(Core.Models.OxQLOptions options)
    {
        var limits = options.Limits;

        return new
        {
            maxPageSize = limits.MaxPageSize,
            defaultPageSize = limits.DefaultPageSize,
            maxPipelineStages = limits.MaxPipelineStages,
            maxLookupStages = limits.MaxLookupStages,
            maxUnwindStages = limits.MaxUnwindStages,
            maxResolveStages = limits.MaxResolveStages,
            maxGroupFields = limits.MaxGroupFields,
            maxProjectionFields = limits.MaxProjectionFields,
            maxConditions = limits.MaxConditions,
            maxVariables = limits.MaxVariables,
            maxOffset = limits.MaxOffset,
            countCap = limits.CountCap,
            maxSemiJoinIds = limits.MaxSemiJoinIds,
            resolveKeyChunk = limits.ResolveKeyChunk,
            maxResolveKeys = limits.MaxResolveKeys,
            maxRequestBytes = limits.MaxRequestBytes,
            maxBatchQueries = limits.MaxBatchQueries,
            regexMaxLength = limits.RegexMaxLength,
            maxLookupLimit = limits.MaxLookupLimit,
            maxFlattenDepth = limits.MaxFlattenDepth,
            maxContinuedStages = limits.MaxContinuedStages,
            maxReportPageSize = limits.MaxReportPageSize,
            maxReportedRows = limits.MaxReportedRows,
            chainTimeoutMs = options.Execution.EffectiveChainTimeoutMs,
            negativeResolveTtlSeconds = options.Cache.NegativeResolveTtlSeconds,
            explainRemoteTimeoutMs = options.Explain.RemoteTimeoutMs,
            explainTimeoutMs = options.Explain.TimeoutMs,
            explainMaxRequestBytes = Math.Min(limits.MaxRequestBytes, options.Explain.MaxRequestBytes),
            explainMaxStages = options.Explain.MaxStages,
            explainMaxCatalogEntries = options.Explain.MaxCatalogEntries,
            explainMaxShapeDepth = options.Explain.MaxShapeDepth,
            explainDefaultShapeDepth = options.Explain.DefaultShapeDepth,
            explainMaxTypeMembers = options.Explain.MaxTypeMembers,
            explainMaxAnswerBytes = options.Explain.MaxAnswerBytes,
            explainMaxOwnerServices = options.Explain.MaxOwnerServices,
            explainMaxOwnerCalls = options.Explain.MaxOwnerCalls,
            explainMaxBatchChecks = options.Explain.MaxBatchChecks,
            explainRatePerMinute = options.Explain.RatePerMinute,
            explainRateBurst = options.Explain.RateBurst,
            explainMaxConcurrentPerUser = options.Explain.MaxConcurrentPerUser,
            explainMaxConcurrentPerHost = options.Explain.MaxConcurrentPerHost,
            explainMaxConcurrentPerCaller = options.Explain.MaxConcurrentPerCaller,
        };
    }

    private static Model.EntityModel? ModelOrNull(IEntityModelProvider models)
    {
        try
        {
            return models.Model;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Everything about a query without running it (DESIGN §4): the body is a plain query or the
    /// envelope <c>{ query, include?, shape?, remote?, catalog? }</c>. A query that does not bind is 200
    /// with <c>valid: false</c> and every error; the plan (the bound form and the emitted stages) comes
    /// with a valid one that asks for it. Explain never executes the query; the index advisory reads only
    /// the index lists, and only with <c>include: ["indexes"]</c>. On by default; 404 while
    /// <c>Explain:Enabled</c> is off. A malformed body is 400, no organisation 403, a body over
    /// <c>Explain:MaxRequestBytes</c> 413, and a body past the other explain bounds 400
    /// <c>EXPLAIN_LIMIT</c>, both before anything is bound. More explains than one user may send
    /// (<c>Explain:RatePerMinute</c>, <c>RateBurst</c>) or have in flight (<c>MaxConcurrentPerUser</c>,
    /// <c>MaxConcurrentPerHost</c>) is 429 with <c>Retry-After</c>, before the body is read.
    /// <para>
    /// An answer carries its validator as <c>ETag</c> (the answer's <c>etag</c>). A caller that holds an
    /// answer sends that value as <c>If-None-Match</c> and gets 304 without a body while the answer
    /// still stands: the same request by the same organisation and user, answered the same in
    /// everything but what the owners cost this time. The body is written in
    /// the content coding the caller accepts (<c>Accept-Encoding</c>: Brotli, else gzip).
    /// </para>
    /// </summary>
    [HttpPost("explain")]
    [ExplainBody]
    [TypeFilter(typeof(ExplainRateFilter), Order = -1)]
    [ProducesResponseType(typeof(Refusal), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ExplainResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(Refusal), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Refusal), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(Refusal), StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Explain([FromBody] ExplainRequest request, CancellationToken cancellationToken)
    {
        if (!options.Explain.Enabled)
            return NotFound();

        var outcome = await queryService.ExplainAsync(request, cancellationToken);

        return outcome switch
        {
            ExplainOutcome.Success success => Answered(success.Result),
            ExplainOutcome.Refused refused => Log(refused.Refusal).ToActionResult(),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>
    /// The explain answer under its validator: 304 when the caller's <c>If-None-Match</c> names it (weak
    /// comparison, as the header requires; <c>*</c> matches any), else the answer in the coding the caller accepts.
    /// </summary>
    private IActionResult Answered(ExplainResult result)
    {
        if (result.Etag is { } etag && Microsoft.Net.Http.Headers.EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            Response.Headers.ETag = etag;
            // The answer is one identity's (its organisation's addons, what its owners answer it): never a shared cache's.
            Response.Headers.CacheControl = "private, no-cache";

            if (Request.GetTypedHeaders().IfNoneMatch.Any(candidate => candidate.Equals(Microsoft.Net.Http.Headers.EntityTagHeaderValue.Any) || candidate.Compare(tag, useStrongComparison: false)))
                return StatusCode(StatusCodes.Status304NotModified);
        }

        return new CompressedJsonResult(result);
    }

    private Refusal Log(Refusal refusal)
    {
        if (refusal.Status >= 500)
            logger.LogError("OxQL refusal {Type}: {Title}", refusal.Type, refusal.Title);
        else
            logger.LogInformation("OxQL refusal {Type} {Code}: {Message}", refusal.Type, LogText.Of(refusal.Errors?[0].Code), LogText.Of(refusal.Errors?[0].Message));

        return refusal;
    }
}
