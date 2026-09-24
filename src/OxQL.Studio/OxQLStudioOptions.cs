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
    /// The base path of the OxQL HTTP API the UI executes against: <c>{ApiBasePath}/query</c>,
    /// <c>{ApiBasePath}/batch</c>, <c>{ApiBasePath}/explain</c> and <c>{ApiBasePath}/health</c>.
    /// Default: <c>/OxQL</c> to match the MVC <c>OxQLController</c> route.
    /// </summary>
    public string ApiBasePath { get; set; } = "/OxQL";

    /// <summary>
    /// The base path of the schema the UI reads the entities from: <c>{SchemaBasePath}</c> is
    /// the Ox Schema document, <c>{SchemaBasePath}/addons</c> the organisation's addon
    /// definitions. Default: <c>/schema</c>, the base package's schema controller beside the
    /// query controller. A host without a schema endpoint shows an empty explorer.
    /// </summary>
    public string SchemaBasePath { get; set; } = "/schema";

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
    /// Shows the <c>Explain</c> button in the Studio UI, which calls <c>POST /explain</c> and
    /// visualises the bound pipeline, the emitted stages and the index advisory. Should match
    /// the engine's <c>OxQL:Explain:Enabled</c>; the endpoint answers 404 otherwise.
    /// Default: <c>false</c>.
    /// </summary>
    public bool EnableExplain { get; set; }

    /// <summary>
    /// Normalizes <see cref="RoutePath"/>, <see cref="ApiBasePath"/> and <see cref="SchemaBasePath"/>
    /// to a leading-slash, no-trailing-slash form.
    /// </summary>
    internal void Normalize()
    {
        RoutePath      = NormalizePath(RoutePath,      "/oxql");
        ApiBasePath    = NormalizePath(ApiBasePath,    "/OxQL");
        SchemaBasePath = NormalizePath(SchemaBasePath, "/schema");
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
