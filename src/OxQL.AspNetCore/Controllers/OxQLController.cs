using System.Collections;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OxQL.AspNetCore.Models;
using OxQL.Core.Models;
using OxQL.Core.Registration;

namespace OxQL.AspNetCore.Controllers;

/// <summary>
/// API controller that exposes OxQL query execution over HTTP.
/// </summary>
[ApiController]
[Route("[controller]")]
public class OxQLController : ControllerBase
{
    private readonly IOxQLQueryService _queryService;
    private readonly OxQLEndpointOptions _options;
    private readonly ILogger<OxQLController> _logger;
    private readonly OxQLTypeRegistry _typeRegistry;

    public OxQLController(
        IOxQLQueryService queryService,
        IOptions<OxQLEndpointOptions> options,
        ILogger<OxQLController> logger,
        OxQLTypeRegistry typeRegistry)
    {
        _queryService  = queryService  ?? throw new ArgumentNullException(nameof(queryService));
        _options       = options?.Value ?? new OxQLEndpointOptions();
        _logger        = logger        ?? throw new ArgumentNullException(nameof(logger));
        _typeRegistry  = typeRegistry  ?? throw new ArgumentNullException(nameof(typeRegistry));
    }

    /// <summary>
    /// Executes an OxQL query and returns paginated results.
    /// </summary>
    /// <param name="request">The query request body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// 200 OK with query results, 400 Bad Request for validation errors,
    /// or 500 Internal Server Error for unexpected failures.
    /// </returns>
    [HttpPost("query")]
    [ProducesResponseType(typeof(OxQLQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OxQLErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(OxQLErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Query(
        [FromBody] QueryRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _queryService.ExecuteAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (QueryValidationException ex)
        {
            _logger.LogWarning("Query validation failed with {ErrorCount} error(s): {FirstError}",
                ex.Errors.Count, ex.Errors[0].Message);

            return BadRequest(new OxQLErrorResponse
            {
                Type = "validation_error",
                Title = "Query validation failed.",
                Status = StatusCodes.Status400BadRequest,
                Errors = ex.Errors.Select(OxQLFieldError.FromValidationError).ToList()
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected – nothing to return
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing OxQL query for entity type '{EntityType}'",
                request.EntityType);

            var response = new OxQLErrorResponse
            {
                Type = "internal_error",
                Title = "An unexpected error occurred while executing the query.",
                Status = StatusCodes.Status500InternalServerError
            };

            if (_options.IncludeErrorDetails)
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

            return StatusCode(StatusCodes.Status500InternalServerError, response);
        }
    }

    /// <summary>
    /// Returns all registered OxQL entity types and their public property structure.
    /// </summary>
    [HttpGet("types")]
    [ProducesResponseType(typeof(IReadOnlyList<OxQLTypeDescriptor>), StatusCodes.Status200OK)]
    public IActionResult Types()
    {
        var descriptors = _typeRegistry.Registrations
            .OrderBy(r => r.TypeName)
            .Select(r => new OxQLTypeDescriptor
            {
                TypeName       = r.TypeName,
                CollectionName = r.CollectionName,
                DatabaseName   = r.DatabaseName,
                ClrType        = r.ClrType?.FullName,
                Properties     = BuildProperties(r.ClrType)
            })
            .ToList();

        return Ok(descriptors);
    }

    /// <summary>
    /// Health-check endpoint that confirms the OxQL query service is available.
    /// Always reachable, even when the endpoint is protected with authorization,
    /// so infrastructure probes do not require credentials.
    /// </summary>
    [HttpGet("health")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", service = "oxql" });
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a list of <see cref="OxQLPropertyDescriptor"/> entries for all
    /// public readable instance properties of <paramref name="clrType"/>.
    /// Recursion is guarded by <paramref name="visited"/> to break reference cycles.
    /// </summary>
    private static IReadOnlyList<OxQLPropertyDescriptor> BuildProperties(
        Type? clrType,
        HashSet<Type>? visited = null)
    {
        if (clrType is null) return [];

        visited ??= [];
        if (!visited.Add(clrType)) return [];   // cycle guard

        return clrType
            .GetProperties(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead)
            .Select(p => DescribeProperty(p.Name, p.PropertyType, new HashSet<Type>(visited)))
            .ToList();
    }

    private static OxQLPropertyDescriptor DescribeProperty(
        string name,
        Type type,
        HashSet<Type> visited)
    {
        // Strip Nullable<T>
        bool outerNullable = false;
        var inner = Nullable.GetUnderlyingType(type);
        if (inner is not null)
        {
            outerNullable = true;
            type = inner;
        }
        else if (!type.IsValueType)
        {
            outerNullable = true;   // reference types are nullable by convention
        }

        // ── Dictionary<K,V> ────────────────────────────────────────────────
        if (TryDictionaryTypes(type, out var keyType, out var valType))
        {
            var valueDesc = DescribeProperty("value", valType!, new HashSet<Type>(visited));
            return new OxQLPropertyDescriptor
            {
                Name     = name,
                Kind     = "dictionary",
                Nullable = outerNullable,
                KeyKind  = ScalarKind(keyType!) ?? keyType!.Name,
                Items    = valueDesc with { Name = "value" }
            };
        }

        // ── Array / collection ─────────────────────────────────────────────
        if (TryCollectionElement(type, out var elemType))
        {
            var itemDesc = DescribeProperty("item", elemType!, new HashSet<Type>(visited));
            return new OxQLPropertyDescriptor
            {
                Name     = name,
                Kind     = "array",
                Nullable = outerNullable,
                Items    = itemDesc with { Name = "item" }
            };
        }

        // ── Scalar ─────────────────────────────────────────────────────────
        var scalarKind = ScalarKind(type);
        if (scalarKind is not null)
        {
            return new OxQLPropertyDescriptor
            {
                Name     = name,
                Kind     = scalarKind,
                Nullable = outerNullable
            };
        }

        // ── Complex object — recurse ───────────────────────────────────────
        var childProps = BuildProperties(type, new HashSet<Type>(visited));
        return new OxQLPropertyDescriptor
        {
            Name       = name,
            Kind       = "object",
            Nullable   = outerNullable,
            Properties = childProps.Count > 0 ? childProps : null
        };
    }

    /// <summary>Returns a JSON-style scalar kind name, or <c>null</c> if the type is not scalar.</summary>
    private static string? ScalarKind(Type t)
    {
        if (t == typeof(string))             return "string";
        if (t == typeof(bool))               return "boolean";
        if (t == typeof(byte)   ||
            t == typeof(short)  ||
            t == typeof(int)    ||
            t == typeof(long)   ||
            t == typeof(float)  ||
            t == typeof(double) ||
            t == typeof(decimal))            return "number";
        if (t == typeof(Guid))               return "Guid";
        if (t == typeof(DateTime))           return "DateTime";
        if (t == typeof(DateTimeOffset))     return "DateTimeOffset";
        if (t == typeof(TimeSpan))           return "TimeSpan";
        if (t == typeof(object))             return "object";
        return null;
    }

    private static bool TryCollectionElement(Type type, out Type? elementType)
    {
        // T[]
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type.IsGenericType)
        {
            var def  = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();

            // Skip Dictionary — handled separately
            if (def == typeof(Dictionary<,>)         ||
                def == typeof(IDictionary<,>)         ||
                def == typeof(IReadOnlyDictionary<,>))
            {
                elementType = null;
                return false;
            }

            // IList<T>, List<T>, IEnumerable<T>, ICollection<T>, HashSet<T>, …
            if (args.Length == 1 &&
                typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            {
                elementType = args[0];
                return true;
            }
        }

        elementType = null;
        return false;
    }

    private static bool TryDictionaryTypes(Type type, out Type? keyType, out Type? valueType)
    {
        if (type.IsGenericType)
        {
            var def  = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();

            if ((def == typeof(Dictionary<,>)          ||
                 def == typeof(IDictionary<,>)          ||
                 def == typeof(IReadOnlyDictionary<,>)) &&
                args.Length == 2)
            {
                keyType   = args[0];
                valueType = args[1];
                return true;
            }
        }

        keyType = valueType = null;
        return false;
    }
}

