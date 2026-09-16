namespace OxQL.AspNetCore;

/// <summary>
/// The host's choices about the controller: how it is protected. Everything the engine
/// itself does (limits, explain, compat, timeouts) is configured through <c>OxQLOptions</c>,
/// bound from the <c>OxQL</c> section.
/// </summary>
public sealed class OxQLEndpointOptions
{
    /// <summary>
    /// Whether to require authentication for the query endpoints. Default: false.
    /// When true the controller is decorated with [Authorize]; the health probe stays anonymous.
    /// Authentication middleware must be configured separately.
    /// </summary>
    public bool RequireAuthorization { get; set; }

    /// <summary>
    /// The name of the authorization policy to enforce when <see cref="RequireAuthorization"/>
    /// is <c>true</c>. When <c>null</c> or empty the default authorization policy is used
    /// (any authenticated user). Ignored when <see cref="RequireAuthorization"/> is <c>false</c>.
    /// </summary>
    public string? AuthorizationPolicy { get; set; }
}
