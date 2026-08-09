# Cross-Service Resolver for OxQL

Enable an OxQL-powered microservice to resolve data from **other** microservices at query time — with no static knowledge of peers required. Callers supply the target services dynamically via a `?services=` query parameter.

---

## How it works

```
GET /oxql/query
  ?q=match id in ["abc"] | resolve vehicle-api/v1.vehicleId
  &services=vehicle-api/v1=https://vehicle-api.internal/,erp-api/v2=https://erp-api.internal/
```

1. The receiving service parses `?services=` into `(source, baseAddress)` pairs.
2. `DynamicResolverFactory` creates one `ExternalServiceResolver` per pair — no startup registration needed.
3. Each resolver forwards the OxQL sub-query to the target service as `POST /oxql/query?q=<encoded-query>`.
4. Results are merged back into the parent query result.

---

## Files

Place everything in a single file, e.g. `ExternalServiceResolver.cs`, inside your project.

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OxQL.Core.Interfaces;

namespace YourService.CrossService; // <-- replace with your actual namespace

// ---------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------

public sealed class OxQLCrossServiceOptions
{
    /// <summary>Maximum number of external services a caller may pass per request.</summary>
    public int MaxServicesPerRequest { get; set; } = 20;

    /// <summary>Allow plain HTTP base addresses. Keep false in production.</summary>
    public bool AllowHttp { get; set; } = false;
}

// ---------------------------------------------------------------------------
// ExternalServiceResolver  (implements IExternalResolver)
// ---------------------------------------------------------------------------

/// <summary>
/// Resolves data from a remote OxQL service.
/// Source format: "{service-name}/{version}"  e.g. "vehicle-api/v1"
/// The sub-query is sent as the ?q= query parameter.
/// </summary>
public sealed class ExternalServiceResolver : IExternalResolver
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ExternalServiceResolver> _logger;

    public ExternalServiceResolver(
        HttpClient httpClient,
        string source,
        ILogger<ExternalServiceResolver> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Source      = source      ?? throw new ArgumentNullException(nameof(source));
        _logger     = logger      ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Source { get; }

    public async Task<IReadOnlyDictionary<string, object?>> ResolveAsync(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        if (keys.Count == 0)
            return new Dictionary<string, object?>();

        // Build:  match id in ["key1","key2"]
        var inList  = string.Join(", ", keys.Select(k => $"\"{k}\""));
        var query   = $"match id in [{inList}]";
        var url     = $"/oxql/query?q={HttpUtility.UrlEncode(query)}";

        try
        {
            var response = await _httpClient.PostAsync(url, content: null, cancellationToken);
            response.EnsureSuccessStatusCode();

            var items = await response.Content
                .ReadFromJsonAsync<List<JsonElement>>(cancellationToken: cancellationToken) ?? [];

            var result = new Dictionary<string, object?>(items.Count);
            foreach (var item in items)
            {
                if (item.TryGetProperty("id", out var idProp) && idProp.GetString() is { } id)
                    result[id] = item;
            }

            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to resolve keys from external service '{Source}'", Source);
            return new Dictionary<string, object?>();
        }
    }
}

// ---------------------------------------------------------------------------
// ServiceRegistryParser
// ---------------------------------------------------------------------------

/// <summary>
/// Parses the ?services= query parameter.
/// Format:  name/version=https://base-url/,name2/version2=https://...
/// </summary>
public static class ServiceRegistryParser
{
    public static IReadOnlyList<(string Source, Uri BaseAddress)> Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        var entries = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result  = new List<(string, Uri)>(entries.Length);

        foreach (var entry in entries)
        {
            var idx = entry.IndexOf('=');
            if (idx <= 0)
                throw new ArgumentException($"Malformed services entry (missing '='): '{entry}'");

            var source = entry[..idx].Trim();
            var rawUrl = entry[(idx + 1)..].Trim();

            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
                throw new ArgumentException($"Invalid base address for '{source}': '{rawUrl}'");

            result.Add((source, uri));
        }

        return result;
    }
}

// ---------------------------------------------------------------------------
// DynamicResolverFactory
// ---------------------------------------------------------------------------

/// <summary>
/// Creates ExternalServiceResolver instances on-the-fly from a caller-supplied
/// service map. No static peer registration is required at startup.
/// </summary>
public sealed class DynamicResolverFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExternalServiceResolver> _resolverLogger;
    private readonly OxQLCrossServiceOptions _options;

    public DynamicResolverFactory(
        IHttpClientFactory httpClientFactory,
        ILogger<ExternalServiceResolver> resolverLogger,
        IOptions<OxQLCrossServiceOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _resolverLogger    = resolverLogger;
        _options           = options.Value;
    }

    /// <exception cref="InvalidOperationException">
    /// Too many services, or HTTP used while AllowHttp is false.
    /// </exception>
    public IReadOnlyList<IExternalResolver> CreateResolvers(
        IReadOnlyList<(string Source, Uri BaseAddress)> serviceMap)
    {
        if (serviceMap.Count > _options.MaxServicesPerRequest)
            throw new InvalidOperationException(
                $"Request exceeds the maximum of {_options.MaxServicesPerRequest} external services.");

        var resolvers = new List<IExternalResolver>(serviceMap.Count);

        foreach (var (source, baseAddress) in serviceMap)
        {
            if (!_options.AllowHttp && baseAddress.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Plain HTTP is not allowed for '{source}'. Use HTTPS.");

            var client = _httpClientFactory.CreateClient($"oxql-external-{source}");
            client.BaseAddress = baseAddress;

            resolvers.Add(new ExternalServiceResolver(client, source, _resolverLogger));
        }

        return resolvers;
    }
}

// ---------------------------------------------------------------------------
// DI registration
// ---------------------------------------------------------------------------

public static class OxQLCrossServiceExtensions
{
    /// <summary>
    /// Registers the dynamic cross-service resolver infrastructure.
    /// No target services need to be known at startup.
    /// </summary>
    public static IServiceCollection AddOxQLCrossServiceQuerying(
        this IServiceCollection services,
        Action<OxQLCrossServiceOptions>? configure = null)
    {
        services.AddHttpClient();

        if (configure is not null)
            services.Configure(configure);
        else
            services.Configure<OxQLCrossServiceOptions>(_ => { });

        services.AddScoped<DynamicResolverFactory>();

        return services;
    }
}
```

---

## Registration — `Program.cs`

```csharp
builder.Services.AddOxQLCrossServiceQuerying(opts =>
{
    opts.AllowHttp = builder.Environment.IsDevelopment();
    opts.MaxServicesPerRequest = 10;
});
```

No peer services are registered here. The caller decides which services to include at request time.

---

## Endpoint

```csharp
app.MapGet("/oxql/query", async (
    [FromQuery] string q,
    [FromQuery] string? services,
    DynamicResolverFactory factory,
    IOxQLQueryService queryService,
    CancellationToken ct) =>
{
    var serviceMap = ServiceRegistryParser.Parse(services);
    var resolvers  = factory.CreateResolvers(serviceMap);

    // Register resolvers into your execution context, then run the query
    var request = BuildQueryRequest(q);
    var result  = await queryService.ExecuteAsync(request, ct);

    return Results.Ok(result);
});
```

---

## Example request

```
GET /oxql/query
  ?q=match id in ["abc123"] | resolve vehicle-api/v1.vehicleId
  &services=vehicle-api/v1=https://vehicle-api.internal/,erp-api/v2=https://erp-api.internal/
```

The resolver for `vehicle-api/v1` will call:

```
POST https://vehicle-api.internal/oxql/query?q=match%20id%20in%20%5B%22abc123%22%5D
```

---

## `?services=` format

```
{service-name}/{version}={https://base-url/}
```

Multiple services are comma-separated:

```
vehicle-api/v1=https://vehicle-api.internal/,erp-api/v2=https://erp-api.internal/
```

---

## Security

| Rule | Default |
|---|---|
| HTTPS required | `AllowHttp = false` |
| Max services per request | `MaxServicesPerRequest = 20` |
| Invalid base URI | throws `ArgumentException` |
| HTTP request failure | logs error, returns empty result (no crash) |
