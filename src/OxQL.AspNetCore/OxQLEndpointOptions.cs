namespace OxQL.AspNetCore;

/// <summary>
/// Configuration options for the OxQL API endpoint.
/// </summary>
public sealed class OxQLEndpointOptions
{
    /// <summary>
    /// The route prefix for the OxQL controller. Default: "api/oxql".
    /// </summary>
    public string RoutePrefix { get; set; } = "api/oxql";

    /// <summary>
    /// Whether to include detailed error information in responses.
    /// Should be false in production. Default: false.
    /// </summary>
    public bool IncludeErrorDetails { get; set; }

    /// <summary>
    /// Whether to require authentication for the query endpoint. Default: false.
    /// When true the controller is decorated with [Authorize].
    /// Authentication middleware must be configured separately.
    /// </summary>
    public bool RequireAuthorization { get; set; }
}
