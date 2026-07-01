namespace OxQL.Studio;

/// <summary>
/// Configuration options for the OxQL Studio query-builder UI.
/// </summary>
public sealed class OxQLStudioOptions
{
    /// <summary>
    /// The route path where the Studio UI is served. Default: <c>/oxql</c>.
    /// Must start with a leading slash and must not end with a trailing slash.
    /// </summary>
    public string RoutePath { get; set; } = "/oxql";

    /// <summary>
    /// The base path of the OxQL HTTP API used by the UI to execute queries
    /// (<c>{ApiBasePath}/query</c>) and to load the type explorer
    /// (<c>{ApiBasePath}/types</c>). Default: <c>/OxQL</c> to match the MVC
    /// <c>OxQLController</c> route.
    /// </summary>
    public string ApiBasePath { get; set; } = "/OxQL";

    /// <summary>
    /// The document title / heading shown in the UI. Default: <c>OxQL Studio</c>.
    /// </summary>
    public string Title { get; set; } = "OxQL Studio";

    /// <summary>
    /// The CDN base URL used to load the Monaco editor. Default: jsDelivr.
    /// Override to self-host Monaco in air-gapped environments.
    /// </summary>
    public string MonacoCdnBase { get; set; } = "https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min";

    /// <summary>
    /// Normalizes <see cref="RoutePath"/> and <see cref="ApiBasePath"/> to a
    /// leading-slash, no-trailing-slash form.
    /// </summary>
    internal void Normalize()
    {
        RoutePath   = NormalizePath(RoutePath,   "/oxql");
        ApiBasePath = NormalizePath(ApiBasePath, "/OxQL");
    }

    private static string NormalizePath(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var path = value.Trim();
        if (!path.StartsWith('/'))
            path = "/" + path;

        if (path.Length > 1)
            path = path.TrimEnd('/');

        return path;
    }
}
