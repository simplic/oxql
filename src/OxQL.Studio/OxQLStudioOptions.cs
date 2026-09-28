namespace OxQL.Studio;

/// <summary>
/// Configuration options for the OxQL Studio developer console: one service, one scratch query.
/// </summary>
public sealed class OxQLStudioOptions
{
    /// <summary>
    /// The route path where the console is served, relative to the request's path base
    /// (<c>UsePathBase</c>): the shell at <c>{RoutePath}</c>, the assets at
    /// <c>{RoutePath}/{asset}</c>. Default: <c>/oxql</c>. It may equal <see cref="ApiBasePath"/>;
    /// the asset route never matches <c>health</c>, <c>query</c>, <c>batch</c> or <c>explain</c>.
    /// </summary>
    public string RoutePath { get; set; } = "/oxql";

    /// <summary>
    /// The base path of the OxQL HTTP API the UI executes against: <c>{ApiBasePath}/query</c>,
    /// <c>{ApiBasePath}/batch</c>, <c>{ApiBasePath}/explain</c> and <c>{ApiBasePath}/health</c>.
    /// Default: <c>/OxQL</c> to match the MVC <c>OxQLController</c> route.
    /// </summary>
    public string ApiBasePath { get; set; } = "/OxQL";

    /// <summary>
    /// The base path of the Ox Schema document, relative to the path base. The console reads
    /// only the entity names from it, to complete <c>entityType</c>. Default: <c>/schema</c>,
    /// the base package's schema controller beside the query controller. A host without a
    /// schema endpoint simply offers no completion.
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
    /// Shows the <c>Explain</c> and <c>Indexes</c> buttons, which call <c>POST /explain</c>. Should
    /// match the engine's <c>OxQL:Explain:Enabled</c>, which is on by default; a difference is
    /// logged as a warning when the console is mapped. The console also hides the buttons when
    /// <c>/health</c> does not list the <c>explain</c> capability. Default: <c>true</c>.
    /// </summary>
    public bool EnableExplain { get; set; } = true;

    /// <summary>
    /// An optional absolute or relative URL of the Angular OxQL Studio. When set, the console's
    /// top bar links to it. Default: none.
    /// </summary>
    public string? StudioAppUrl { get; set; }

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
