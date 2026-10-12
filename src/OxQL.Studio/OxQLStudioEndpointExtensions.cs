using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OxQL.Studio;

/// <summary>
/// Extension methods for mapping the OxQL Studio UI endpoints.
/// </summary>
public static class OxQLStudioEndpointExtensions
{
    private static readonly Assembly ThisAssembly = typeof(OxQLStudioEndpointExtensions).Assembly;

    /// <summary>
    /// The names the asset route never serves. The console usually sits beside the OxQL API
    /// (OxS maps both at <c>/oxql</c>), so an asset with one of these names would shadow an API
    /// route. The asset route only matches names with a file extension, which none of these has,
    /// and an asset whose base name is one of these (<c>health.js</c>) is refused as well.
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedAssetNames = ["health", "query", "batch", "explain"];

    /// <summary>
    /// An asset name: word characters with dashes, a dot and a known extension. Written without a
    /// character class, since route templates reserve brackets.
    /// </summary>
    private const string AssetConstraint = @"regex(^\w+(-\w+)*\.(js|css|svg)$)";

    /// <summary>
    /// Maps the OxQL Studio console at <see cref="OxQLStudioOptions.RoutePath"/> (default
    /// <c>/oxql</c>), relative to the request's path base: the shell at <c>{RoutePath}</c>, the
    /// assets at <c>{RoutePath}/{asset}</c>. Both are anonymous; the console asks for a
    /// credential in the page and sends it only to the API.
    /// Requires <see cref="ServiceCollectionExtensions.AddOxQLStudio"/> to have been called.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapOxQLStudio(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetService<OxQLStudioOptions>() ?? new OxQLStudioOptions();
        options.Normalize();

        WarnWhenExplainDisagrees(endpoints.ServiceProvider, options);

        // Shell: GET {pathBase}{RoutePath} → index.html with the runtime config injected.
        endpoints.MapGet(options.RoutePath, (HttpContext ctx) =>
        {
            var html = LoadTextResource("index.html");
            if (html is null)
                return Results.NotFound();

            var assetBasePath = WithPathBase(ctx, options.RoutePath);
            var config = new
            {
                apiBasePath    = WithPathBase(ctx, options.ApiBasePath),
                schemaBasePath = WithPathBase(ctx, options.SchemaBasePath),
                assetBasePath,
                title          = options.Title,
                monacoCdnBase  = options.MonacoCdnBase,
                enableExplain  = options.EnableExplain,
                studioAppUrl   = options.StudioAppUrl
            };

            // The default encoder escapes '<', so nothing in the config can close its <script>.
            var json = JsonSerializer.Serialize(config);
            html = html
                .Replace("__OXQL_CONFIG__", json)
                .Replace("__OXQL_TITLE__", System.Net.WebUtility.HtmlEncode(options.Title))
                .Replace("__OXQL_ASSET_BASE__", System.Net.WebUtility.HtmlEncode(assetBasePath));

            return Results.Content(html, "text/html; charset=utf-8");
        }).AllowAnonymous();

        // Assets: GET {pathBase}{RoutePath}/{asset}
        var assetRoute = options.RoutePath == "/"
            ? "{asset:" + AssetConstraint + "}"
            : options.RoutePath.TrimStart('/') + "/{asset:" + AssetConstraint + "}";

        endpoints.MapGet(assetRoute, (string asset) =>
        {
            var (bytes, contentType) = LoadAsset(asset);
            return bytes is null
                ? Results.NotFound()
                : Results.File(bytes, contentType);
        }).AllowAnonymous();

        return endpoints;
    }

    /// <summary>
    /// Warns when the Studio's Explain button and the engine's <c>OxQL:Explain:Enabled</c>
    /// disagree: the button would call an endpoint that answers 404, or the endpoint would be
    /// reachable without a button. The engine options are read by name so the Studio needs no
    /// reference to the engine.
    /// </summary>
    private static void WarnWhenExplainDisagrees(IServiceProvider provider, OxQLStudioOptions options)
    {
        var loggerFactory = provider.GetService<ILoggerFactory>();
        var optionsType = Type.GetType("OxQL.Core.Models.OxQLOptions, OxQL.Core", throwOnError: false);

        if (loggerFactory is null || optionsType is null)
            return;

        var engineOptions = provider.GetService(optionsType);
        if (engineOptions is null)
            return;

        var explain = optionsType.GetProperty("Explain")?.GetValue(engineOptions);
        var enabled = explain?.GetType().GetProperty("Enabled")?.GetValue(explain) as bool?;

        if (enabled.HasValue && enabled.Value != options.EnableExplain)
            loggerFactory.CreateLogger(nameof(OxQLStudioEndpointExtensions)).LogWarning(
                "OxQLStudioOptions.EnableExplain ({StudioValue}) and OxQL:Explain:Enabled ({EngineValue}) differ: the Studio button and the explain endpoint will not match.",
                options.EnableExplain,
                enabled.Value);
    }

    private static string WithPathBase(HttpContext ctx, string path)
    {
        // Honour a reverse-proxy path base if one is configured.
        var pathBase = ctx.Request.PathBase.HasValue ? ctx.Request.PathBase.Value : string.Empty;
        return $"{pathBase}{path}";
    }

    private static (byte[]? bytes, string contentType) LoadAsset(string asset)
    {
        if (ReservedAssetNames.Contains(Path.GetFileNameWithoutExtension(asset), StringComparer.OrdinalIgnoreCase))
            return (null, "application/octet-stream");

        var contentType = asset switch
        {
            _ when asset.EndsWith(".js",   StringComparison.OrdinalIgnoreCase) => "text/javascript; charset=utf-8",
            _ when asset.EndsWith(".css",  StringComparison.OrdinalIgnoreCase) => "text/css; charset=utf-8",
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
