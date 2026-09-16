using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using OxQL.AspNetCore.Batch;
using OxQL.AspNetCore.Models;
using OxQL.Core.Binding;
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
        if (batch.Queries.Count > options.Limits.MaxBatchQueries)
            return Log(Refusal.Validation([new QueryValidationError
            {
                Code = Codes.BatchTooLarge,
                Message = $"The batch carries {batch.Queries.Count} queries; the limit is {options.Limits.MaxBatchQueries}.",
            }])).ToActionResult();

        var results = new List<JsonNode?>(batch.Queries.Count);

        foreach (var query in batch.Queries)
        {
            var outcome = await queryService.ExecuteAsync(query, batch.MaxTimeMs, cancellationToken);

            results.Add(outcome switch
            {
                QueryOutcome.Success success => JsonSerializer.SerializeToNode(success.Result, JsonOptions.Wire),
                QueryOutcome.Refused refused => JsonSerializer.SerializeToNode(Log(refused.Refusal), JsonOptions.Wire),
                _ => null,
            });
        }

        return Ok(new BatchResponse { Results = results });
    }

    /// <summary>The engine version, contract and capabilities. Always reachable.</summary>
    [HttpGet("health")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Health([FromServices] IQueryEngine engine)
    {
        var remote = engine is IEngineFeatures features && features.RemoteResolve;

        return Ok(new
        {
            status = "healthy",
            service = "oxql",
            engine = new
            {
                version = typeof(OxQLController).Assembly.GetName().Version?.ToString(),
                contract = EngineCapabilities.Contract,
            },
            capabilities = EngineCapabilities.Of(remote, options.Compat.Enabled, options.Explain.Enabled),
        });
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
