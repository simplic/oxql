using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OxQL.Core.Engine;

namespace OxQL.AspNetCore.Resolve;

/// <summary>
/// Starts the host's check of its declared remote references against the owners' schema documents
/// (<see cref="RemoteReferenceValidator"/>) once the host has started, and logs what it finds: one
/// warning per service whose owner does not publish a declared target. It never delays the start
/// and never stops it: the check runs in the background after the application has started, an
/// owner that does not answer leaves its references not validated, and nothing it throws leaves
/// this class. Health and explain read the same result and start the next check when one is due.
/// </summary>
internal sealed class RemoteReferenceValidationStarter(IServiceProvider provider, IHostApplicationLifetime lifetime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => Task.Run(Check));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Check()
    {
        var logger = provider.GetService<ILoggerFactory>()?.CreateLogger("OxQL.Startup") ?? NullLogger.Instance;

        try
        {
            if (provider.GetService<RemoteReferenceValidator>() is not { } validator || provider.GetService<IRemoteQueryClient>() is not { } client)
                return;

            validator.Changed ??= result => Log(logger, result);
            validator.State(provider.GetRequiredService<IEntityModelProvider>().Model, client);
        }
        catch (Exception exception)
        {
            // The model may not be built yet, or the host is stopping: health and explain start the check later.
            logger.LogDebug(exception, "OxQL remote reference validation did not start with the host; it starts with the first health or explain request");
        }
    }

    private static void Log(ILogger logger, RemoteReferenceValidation result)
    {
        if (result is { Validated: true, UnknownTargets.Count: > 0 })
            logger.LogWarning(
                "OxQL remote reference finding: service '{Service}' does not publish {Count} target(s) this host declares references to: {Targets} (its schema revision {Revision}). A join over such a reference is refused when it runs. Correct the declared target ids, or have the owner publish the entities.",
                result.Service,
                result.UnknownTargets.Count,
                string.Join(", ", result.UnknownTargets),
                result.Revision);
        else if (result.Validated)
            logger.LogInformation("OxQL remote references into '{Service}' validated: the owner publishes every declared target ({Count} reference(s), schema revision {Revision}).", result.Service, result.Declared, result.Revision);
        else
            logger.LogDebug("OxQL remote references into '{Service}' are not validated ({Reason}).", result.Service, result.Reason);
    }
}
