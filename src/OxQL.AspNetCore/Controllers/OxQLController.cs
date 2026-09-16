using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using OxQL.AspNetCore.Batch;
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
    /// The engine version, contract and capabilities, and the state of every service the model
    /// references remotely (configured on this host, reachable right now). Always reachable.
    /// </summary>
    [HttpGet("health")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Health([FromServices] IQueryEngine engine, [FromServices] IEntityModelProvider models, [FromServices] IRemoteQueryClient? client, CancellationToken cancellationToken)
    {
        var remote = engine is IEngineFeatures features && features.RemoteResolve;
        List<RemoteServiceState>? services = null;

        if (client is not null)
        {
            services = [];

            foreach (var service in ModelOrNull(models) is { } model ? RemoteReferences.ServicesOf(model) : [])
            {
                var configured = client.IsConfigured(service);
                bool? reachable = null;

                if (configured)
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    probe.CancelAfter(TimeSpan.FromSeconds(2));

                    try
                    {
                        reachable = await client.IsReachableAsync(service, probe.Token);
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        reachable = false;
                    }
                }

                services.Add(new RemoteServiceState(service, configured, reachable));
            }
        }

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
            remote = services,
        });
    }

    /// <summary>One service the model references remotely: known to this host, and answering right now (null when not probed).</summary>
    public sealed record RemoteServiceState(string Service, bool Configured, bool? Reachable);

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

    /// <summary>The bound pipeline, the emitted stages, the count stages and the index advisory, without executing; 404 unless <c>Explain:Enabled</c>.</summary>
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

/// <summary>The serializer options the wire uses.</summary>
public static class JsonOptions
{
    /// <summary>camelCase, nulls omitted.</summary>
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
