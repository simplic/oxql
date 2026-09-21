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
                version = typeof(OxQLController).Assembly.GetName().Version?.ToString(),
                contract = EngineCapabilities.Contract,
            },
            capabilities = EngineCapabilities.Of(remote, options.Compat.Enabled, options.Explain.Enabled),
            limits = Limits(options.Limits),
            remote = services,
        });
    }

    /// <summary>
    /// Every limit the engine enforces. The schema document publishes the ones a caller checks a
    /// request against before sending it; this is the whole set, for diagnosis.
    /// </summary>
    private static object Limits(Core.Models.LimitOptions limits) => new
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
    };

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
    /// The bound pipeline, the emitted stages, the count stages and the index advisory; 404 unless
    /// <c>Explain:Enabled</c>. No rows are returned and the count never runs, but for a pipeline
    /// with a lookup the advisory reads the server's own explain, which executes the page pipeline
    /// once under the query's time ceiling.
    /// </summary>
    [HttpPost("explain")]
    [ProducesResponseType(typeof(ExplainResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Explain([FromBody] QueryRequest request, CancellationToken cancellationToken)
    {
        if (!options.Explain.Enabled)
            return NotFound();

        var outcome = await queryService.ExplainAsync(request, cancellationToken);

        return outcome switch
        {
            ExplainOutcome.Success success => Ok(success.Result),
            ExplainOutcome.Refused refused => Log(refused.Refusal).ToActionResult(),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private Refusal Log(Refusal refusal)
    {
        if (refusal.Status >= 500)
            logger.LogError("OxQL refusal {Type}: {Title}", refusal.Type, refusal.Title);
        else
            logger.LogInformation("OxQL refusal {Type} {Code}: {Message}", refusal.Type, refusal.Errors?[0].Code, refusal.Errors?[0].Message);

        return refusal;
    }
}
