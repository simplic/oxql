using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OxQL.AspNetCore.Models;
using OxQL.Core.Models;

namespace OxQL.AspNetCore;

/// <summary>
/// Extension methods for mapping OxQL endpoints using minimal APIs.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the OxQL query endpoint as a minimal-API route group.
    /// Use after calling <see cref="ServiceCollectionExtensions.AddOxQLEndpoint{T}"/> in DI setup.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>A <see cref="RouteGroupBuilder"/> for further customization (e.g. adding authorization).</returns>
    public static RouteGroupBuilder MapOxQL(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider
            .GetService<IOptions<OxQLEndpointOptions>>()?.Value ?? new OxQLEndpointOptions();

        var group = endpoints.MapGroup(options.RoutePrefix)
            .WithTags("OxQL");

        var queryEndpoint = group.MapPost("/query", async (
            QueryRequest request,
            IOxQLQueryService queryService,
            ILogger<OxQLQueryService<object>> logger,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await queryService.ExecuteAsync(request, cancellationToken);
                return Results.Ok(result);
            }
            catch (QueryValidationException ex)
            {
                logger.LogWarning("Query validation failed with {ErrorCount} error(s): {FirstError}",
                    ex.Errors.Count, ex.Errors[0].Message);

                return Results.BadRequest(new OxQLErrorResponse
                {
                    Type = "validation_error",
                    Title = "Query validation failed.",
                    Status = StatusCodes.Status400BadRequest,
                    Errors = ex.Errors.Select(OxQLFieldError.FromValidationError).ToList()
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error executing OxQL query for entity type '{EntityType}'",
                    request.EntityType);

                var response = new OxQLErrorResponse
                {
                    Type = "internal_error",
                    Title = "An unexpected error occurred while executing the query.",
                    Status = StatusCodes.Status500InternalServerError
                };

                if (options.IncludeErrorDetails)
                {
                    response = response with
                    {
                        Errors =
                        [
                            new OxQLFieldError
                            {
                                Code = "INTERNAL_ERROR",
                                Message = ex.Message
                            }
                        ]
                    };
                }

                return Results.Json(response, statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("OxQLQuery")
        .WithSummary("Execute an OxQL query")
        .Produces<OxQLQueryResult>(StatusCodes.Status200OK)
        .Produces<OxQLErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<OxQLErrorResponse>(StatusCodes.Status500InternalServerError);

        // Optionally protect the query endpoint. Configurable via OxQLEndpointOptions;
        // the /health probe below is intentionally left anonymous.
        if (options.RequireAuthorization)
        {
            if (string.IsNullOrWhiteSpace(options.AuthorizationPolicy))
                queryEndpoint.RequireAuthorization();
            else
                queryEndpoint.RequireAuthorization(options.AuthorizationPolicy);
        }

        group.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "oxql" }))
            .WithName("OxQLHealth")
            .WithSummary("OxQL service health check");

        return group;
    }
}
