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
// LEGACY(compat.v1): keeps a contract 1 body readable for the legacy pipeline; remove with src/OxQL.AspNetCore/Legacy.
[TypeFilter(typeof(Legacy.LegacyBodyFilter))]
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

    /// <summary>
    /// Executes one query. The rows are written in the content coding the caller accepts
    /// (<c>Accept-Encoding</c>: Brotli, else gzip) when the body is at least
    /// <see cref="WireCompression.MinBytes"/>, with <c>Vary: Accept-Encoding</c> either way: a page of
    /// rows repeats its member names in every row and shrinks to about a tenth. A refusal is not coded.
    /// <para>
    /// A contract 1 request is answered as it always was (<see cref="Contract1"/>): through the host's
    /// own JSON formatter, with no content coding, no length, and no header the engine adds.
    /// </para>
    /// </summary>
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
            QueryOutcome.Success success when Contract1 => Ok(success.Result),
            QueryOutcome.Success success => Timed(success.Result.Measured is { } measured ? [measured] : [], new CompressedJsonResult(success.Result)),
            QueryOutcome.Refused refused => Log(refused.Refusal).ToActionResult(),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>
    /// Executes several queries in order under one time ceiling; always 200, each entry carries
    /// its own outcome. More queries than <c>Limits:MaxBatchQueries</c> is <c>BATCH_TOO_LARGE</c>.
    /// The answer is content-coded as a query's is, and a contract 1 batch is answered as it always was.
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
            BatchOutcome.Success success when Contract1 => Ok(success.Response),
            BatchOutcome.Success success => Timed(success.Response.Measured, new CompressedJsonResult(success.Response)),
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
    /// measurement has finished. Under contract 2 each service also says what its owner's engine
    /// can do for this host (<c>contract</c>, <c>engineVersion</c>, <c>usable</c>, and a
    /// <c>reason</c> when it is not usable), and the answer says how many entities the host's model
    /// has and whether it is what a failed build left (<c>model { entities, degraded, reason }</c>).
    /// The status is <c>degraded</c> while a service is not configured, does not answer or is not
    /// usable, and while the model is degraded. With <c>?shallow=true</c> the answer leaves <c>remote</c> out and
    /// starts no measurement; that is the form one host asks of another, so a probe never sets off
    /// the probed service's own probes.
    /// <para>
    /// <c>capabilities</c> and <c>limits</c> are read per caller and say what holds for that caller's
    /// requests. A request without the contract header on a host that answers contract 1 by the
    /// compatibility rewrite gets the names the published engine listed before contract 2 (batches,
    /// paging after a group and by offset, <c>any</c>) ahead of the host's own, since that host does all
    /// of them for that caller. Where the legacy pipeline answers such a caller (the default), the two
    /// members list what that pipeline honours: no paging by offset, and its limits only.
    /// </para>
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
        var model = ModelHealth.Of(models);
        var degraded = model.Degraded || (services is not null && services.Any(state => state.Degrades));

        // LEGACY(compat.v1): a caller the legacy pipeline answers reads what that pipeline honours; remove with src/OxQL.AspNetCore/Legacy.
        var legacy = Legacy.LegacyHealth.For(options, HttpContext);

        return Ok(new
        {
            status = degraded ? "degraded" : "healthy",
            service = "oxql",
            engine = new
            {
                version = EngineCapabilities.Version,
                contract = EngineCapabilities.Contract,
            },
            capabilities = legacy?.Capabilities ?? EngineCapabilities.Of(remote, options.Compat.Enabled, options.Explain.Enabled, Contract1),
            limits = legacy?.Limits ?? (Contract1 ? Limits(options) : Limits2(options)),
            // What a caller without the header reads ends with remote; the members after it are a contract 2 answer's.
            remote = Contract1 ? services?.Select(Contract1State).ToList() : (object?)services,
            model = Contract1 ? null : model,
        });
    }

    /// <summary>
    /// The state of the host's entity model on health: how many entities it has, and whether it is
    /// what a failed build left, with the reason: <c>build</c> (the provider says the build failed),
    /// <c>empty</c> (no entity, and the provider does not say that is meant) or <c>unavailable</c>
    /// (the provider has no model to hand out). A degraded model degrades the status: no query binds
    /// against an entity the model lacks.
    /// </summary>
    private sealed record ModelHealth(int Entities, bool Degraded, string? Reason)
    {
        public static ModelHealth Of(IEntityModelProvider models)
        {
            if (ModelOrNull(models) is not { } model)
                return new ModelHealth(0, true, "unavailable");

            var entities = model.Entities.Count;

            return models.Degraded switch
            {
                true => new ModelHealth(entities, true, "build"),
                null when entities == 0 => new ModelHealth(0, true, "empty"),
                _ => new ModelHealth(entities, false, null),
            };
        }
    }

    /// <summary>
    /// A remote service as a contract 1 caller reads it: the three members that form has always had.
    /// What the owner's engine can do (<c>contract</c>, <c>engineVersion</c>, <c>usable</c>,
    /// <c>reason</c>) is a contract 2 answer's.
    /// </summary>
    private static object Contract1State(RemoteServiceState state) =>
        new { service = state.Service, configured = state.Configured, reachable = state.Reachable };

    /// <summary>
    /// Every limit the engine enforces. The schema document publishes the ones a caller checks a
    /// request against before sending it; this is the whole set, for diagnosis, the chain
    /// budget and the negative cache lifetime among them.
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
            maxFilterEntries = limits.MaxFilterEntries,
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
            maxValuesLimit = limits.MaxValuesLimit,
            valuesScanRows = options.Values.EffectiveScanRows,
            valuesDefaultLimit = Math.Min(options.Values.DefaultLimit, limits.MaxValuesLimit),
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
            explainOwnerRatePerMinute = options.Explain.OwnerRatePerMinute,
            explainOwnerRateBurst = options.Explain.OwnerRateBurst,
            explainMaxConcurrentPerUser = options.Explain.MaxConcurrentPerUser,
            explainMaxConcurrentPerHost = options.Explain.MaxConcurrentPerHost,
            explainMaxConcurrentPerCaller = options.Explain.MaxConcurrentPerCaller,
        };
    }

    /// <summary>
    /// The limits a contract 2 caller reads: every limit, and what was added to the list since a
    /// caller without the header first read it, which that caller's list does not grow by.
    /// </summary>
    private static JsonObject Limits2(Core.Models.OxQLOptions options)
    {
        var limits = System.Text.Json.JsonSerializer.SerializeToNode(Limits(options))!.AsObject();

        limits["ownerUnavailableTtlSeconds"] = Math.Max(0, options.Cache.OwnerUnavailableTtlSeconds);

        return limits;
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
    /// Everything about a query without running it: the body is a plain query or the
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

    /// <summary>
    /// Whether the request was written for contract 1 (<see cref="OxQLQueryService.ContractOf(OxQLOptions, HttpContext?)"/>).
    /// What such a caller receives is frozen: the status, the headers and the body bytes of the
    /// engine that first answered it. Everything the engine has since added to an answer on the wire
    /// (the content coding and with it <c>Vary</c> and <c>Content-Length</c>, <c>X-Content-Type-Options</c>,
    /// <c>Server-Timing</c>, <c>Access-Control-Expose-Headers</c>, the <c>timing</c> member) is a
    /// contract 2 answer's alone, and so is whatever is added next: it goes behind this.
    /// </summary>
    private bool Contract1 => OxQLQueryService.ContractOf(options, HttpContext) == 1;

    /// <summary>
    /// The answer under its <c>Server-Timing</c> header: where the time went up to here, the first
    /// byte of the body (total, the database with its commands and round trips, this host's own
    /// share and its parts). A browser on another origin reads the header only when it is exposed:
    /// the header is named in <c>Access-Control-Expose-Headers</c> for a request that carries an
    /// <c>Origin</c>, beside what the host's CORS policy exposes. Contract 2 only.
    /// </summary>
    private IActionResult Timed(IReadOnlyList<QueryTiming> measured, IActionResult answer)
    {
        if (measured.Count == 0)
            return answer;

        foreach (var timing in measured)
            timing.Stop();

        Response.Headers[QueryTiming.ServerTimingHeader] = QueryTiming.ServerTiming(measured);

        if (Request.Headers.ContainsKey(Microsoft.Net.Http.Headers.HeaderNames.Origin))
            Response.Headers.Append(Microsoft.Net.Http.Headers.HeaderNames.AccessControlExposeHeaders, QueryTiming.ServerTimingHeader);

        return answer;
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
