namespace OxQL.AspNetCore;

/// <summary>
/// The host's choices about the controller: how it is protected, and whether the host counts
/// as running under continuous integration. Everything the engine itself does (limits, explain,
/// compat, timeouts) is configured through <c>OxQLOptions</c>, bound from the <c>OxQL</c> section.
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

    /// <summary>
    /// Whether the host runs under continuous integration, where a declared remote reference
    /// without a configured service stops the host as it does in <c>Development</c> and
    /// <c>Local</c>. Read once, when <c>AddOxQLAspNetCore</c> is called, from the <c>CI</c>
    /// variable (GitHub Actions, GitLab) and the <c>TF_BUILD</c> variable (Azure Pipelines); set
    /// it to decide for the host instead of the machine, as a test host does.
    /// </summary>
    public bool ContinuousIntegration { get; set; }
}
