using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace OxQL.Studio;

/// <summary>
/// Extension methods for mapping the OxQL Studio UI endpoints.
/// </summary>
public static class OxQLStudioEndpointExtensions
{
    private static readonly Assembly ThisAssembly = typeof(OxQLStudioEndpointExtensions).Assembly;

    /// <summary>
    /// Maps the OxQL Studio UI at the configured route path (default <c>/oxql</c>).
    /// Requires <see cref="ServiceCollectionExtensions.AddOxQLStudio"/> to have been called.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapOxQLStudio(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetService<OxQLStudioOptions>() ?? new OxQLStudioOptions();
        options.Normalize();

        var basePath = options.RoutePath;

        // Shell: GET {routePath}  → index.html with injected runtime config
        endpoints.MapGet(basePath, (HttpContext ctx) =>
        {
            var html = LoadTextResource("index.html");
            if (html is null)
                return Results.NotFound();

            var config = new
            {
                apiBasePath   = ResolveApiBasePath(ctx, options.ApiBasePath),
                assetBasePath = basePath,
                title         = options.Title,
                monacoCdnBase = options.MonacoCdnBase
            };

            var json = JsonSerializer.Serialize(config);
            html = html
                .Replace("__OXQL_CONFIG__", json)
                .Replace("__OXQL_TITLE__", System.Net.WebUtility.HtmlEncode(options.Title))
                .Replace("__OXQL_ASSET_BASE__", basePath);

            return Results.Content(html, "text/html; charset=utf-8");
        });

        // Static assets: GET {routePath}/{asset}
        endpoints.MapGet($"{basePath}/{{asset}}", (string asset) =>
        {
            var (bytes, contentType) = LoadAsset(asset);
            return bytes is null
                ? Results.NotFound()
                : Results.File(bytes, contentType);
        });

        return endpoints;
    }

    private static string ResolveApiBasePath(HttpContext ctx, string apiBasePath)
    {
        // Honour a reverse-proxy path base if one is configured.
        var pathBase = ctx.Request.PathBase.HasValue ? ctx.Request.PathBase.Value : string.Empty;
        return $"{pathBase}{apiBasePath}";
    }

    private static (byte[]? bytes, string contentType) LoadAsset(string asset)
    {
        var contentType = asset switch
        {
            _ when asset.EndsWith(".js",   StringComparison.OrdinalIgnoreCase) => "text/javascript; charset=utf-8",
            _ when asset.EndsWith(".css",  StringComparison.OrdinalIgnoreCase) => "text/css; charset=utf-8",
            _ when asset.EndsWith(".html", StringComparison.OrdinalIgnoreCase) => "text/html; charset=utf-8",
            _ when asset.EndsWith(".svg",  StringComparison.OrdinalIgnoreCase) => "image/svg+xml",
            _ => "application/octet-stream"
        };

        var stream = OpenResource(asset);
        if (stream is null)
            return (null, contentType);

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return (ms.ToArray(), contentType);
    }

    private static string? LoadTextResource(string fileName)
    {
        using var stream = OpenResource(fileName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Opens an embedded wwwroot resource by file name using a case-insensitive
    /// suffix match so callers don't need to know the exact manifest namespace.
    /// </summary>
    private static Stream? OpenResource(string fileName)
    {
        var suffix = ".wwwroot." + fileName.Replace('/', '.');

        var name = Array.Find(
            ThisAssembly.GetManifestResourceNames(),
            n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        return name is null ? null : ThisAssembly.GetManifestResourceStream(name);
    }
}
