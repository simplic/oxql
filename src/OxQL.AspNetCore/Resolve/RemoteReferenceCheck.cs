using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OxQL.Core.Engine;
using OxQL.Model;

namespace OxQL.AspNetCore.Resolve;

/// <summary>
/// The startup check of the model's remote references: every declared reference into another
/// service, which is every remote target of every case of a typed, item or converted reference
/// too (<see cref="RemoteReferences.Of"/>), needs that service configured on this host (an
/// <c>InternalHosts</c> entry, read through <see cref="IRemoteQueryClient.IsConfigured"/>). A reference without one is a
/// configuration problem of this service, never a silent runtime null: an error log on every
/// host, and a refusal to start in <c>Development</c>, <c>Local</c> and under continuous
/// integration (<see cref="OxQLEndpointOptions.ContinuousIntegration"/>), the same strict set
/// the schema build fails fast in. A host without a remote
/// query client refuses remote resolves at runtime (<c>RESOLVE_UNAVAILABLE</c>) and is not
/// checked here.
/// </summary>
public static class RemoteReferenceCheck
{
    private static readonly string[] StrictEnvironments = ["Development", "Local"];

    /// <summary>The remote references whose service the client does not know.</summary>
    public static IReadOnlyList<RemoteReference> Unconfigured(EntityModel model, IRemoteQueryClient client)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(client);

        return RemoteReferences.Of(model).Where(reference => !client.IsConfigured(reference.Service)).ToList();
    }

    /// <summary>Whether a finding stops the host: the schema build's strict set, plus continuous integration.</summary>
    public static bool IsStrict(string? environmentName, bool continuousIntegration) =>
        continuousIntegration
        || (environmentName is not null && StrictEnvironments.Contains(environmentName, StringComparer.OrdinalIgnoreCase));

    /// <summary>Whether a finding stops the host, with continuous integration given as the value of one of its variables.</summary>
    public static bool IsStrict(string? environmentName, string? ciVariable) =>
        IsStrict(environmentName, ReadContinuousIntegration(ciVariable));

    /// <summary>
    /// Whether any of the variables a build server defines says so: <c>CI</c> on GitHub Actions
    /// and GitLab, <c>TF_BUILD</c> on Azure Pipelines. Set, and neither <c>0</c> nor <c>false</c>.
    /// </summary>
    public static bool ReadContinuousIntegration(params string?[] variables) =>
        variables is not null && variables.Any(variable =>
            !string.IsNullOrWhiteSpace(variable) && variable.Trim() != "0" && !string.Equals(variable.Trim(), "false", StringComparison.OrdinalIgnoreCase));

    /// <summary>One line per finding.</summary>
    public static string Describe(RemoteReference reference) =>
        $"'{reference.Entity}#{reference.Path}' references '{reference.TargetEntity}', but this host has no InternalHosts entry for '{reference.Service}'; configure the service under that key or the reference resolves to nothing.";
}

/// <summary>
/// Runs <see cref="RemoteReferenceCheck"/> when the host starts. When the model is not built
/// yet at that point (the base package builds it in its own startup filter), the check runs
/// once on the first request instead; a strict host with a finding then answers that request and
/// every later one with 500 until it is restarted with the service configured.
/// </summary>
internal sealed class RemoteReferenceStartupFilter(IServiceProvider provider) : IStartupFilter
{
    private readonly object gate = new();
    private bool checkedOnce;
    private IReadOnlyList<RemoteReference> findings = [];

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var client = provider.GetService<IRemoteQueryClient>();

        if (client is not null && !TryCheck(client, throwWhenStrict: true))
        {
            // The model is not built yet: check on the first request.
            app.Use(async (context, pipeline) =>
            {
                if (!TryCheck(client, throwWhenStrict: false))
                {
                    await pipeline(context);
                    return;
                }

                if (findings.Count > 0 && Strict())
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsync("OxQL: a declared remote reference has no configured service on this host; see the log.");
                    return;
                }

                await pipeline(context);
            });
        }

        next(app);
    };

    /// <summary>True when the model was available and the check ran (once); false when the model is not built yet.</summary>
    private bool TryCheck(IRemoteQueryClient client, bool throwWhenStrict)
    {
        lock (gate)
        {
            if (checkedOnce)
                return true;

            EntityModel model;

            try
            {
                model = provider.GetRequiredService<IEntityModelProvider>().Model;
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            checkedOnce = true;
            findings = RemoteReferenceCheck.Unconfigured(model, client);

            if (findings.Count == 0)
                return true;

            var logger = provider.GetService<ILoggerFactory>()?.CreateLogger("OxQL.Startup") ?? NullLogger.Instance;

            foreach (var finding in findings)
                logger.LogError("OxQL remote reference finding: {Finding}", RemoteReferenceCheck.Describe(finding));

            if (Strict())
            {
                logger.LogCritical("OxQL refuses to start: {Count} declared remote reference(s) have no configured service on this host.", findings.Count);

                if (throwWhenStrict)
                    throw new InvalidOperationException(
                        "OxQL refuses to start: " + string.Join(" ", findings.Select(RemoteReferenceCheck.Describe)));
            }

            return true;
        }
    }

    private bool Strict() =>
        RemoteReferenceCheck.IsStrict(
            provider.GetService<IHostEnvironment>()?.EnvironmentName,
            provider.GetService<IOptions<OxQLEndpointOptions>>()?.Value.ContinuousIntegration ?? false);
}
