using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;

namespace OxQL.AspNetCore.Health;

/// <summary>
/// One service the model references remotely: known to this host, and answering when last
/// asked. <see cref="Reachable"/> is null for a service that is not configured, and for a
/// configured one that has not been measured yet.
/// <para>
/// What the owner can do for this host is said beside it, from what the remote client read of the
/// owner's shallow health (<see cref="IRemoteOwnerInfo"/>): the <see cref="Contract"/> it speaks and
/// its <see cref="EngineVersion"/>, and whether this host can join into it (<see cref="Usable"/>).
/// A client without that capability leaves the three unknown (null).
/// </para>
/// </summary>
public sealed record RemoteServiceState(string Service, bool Configured, bool? Reachable)
{
    /// <summary><see cref="Reason"/> of a service without an <c>InternalHosts</c> entry.</summary>
    public const string NotConfigured = "notConfigured";

    /// <summary><see cref="Reason"/> of a service whose health did not answer.</summary>
    public const string Unreachable = "unreachable";

    /// <summary><see cref="Reason"/> of a service that answered and does not speak this engine's contract.</summary>
    public const string Engine = "engine";

    /// <summary>
    /// The <see cref="Reason"/> of an owner that does not answer on the internal query route under the
    /// API version this host calls it with: its health answered 404, or a join of this host found the
    /// route missing while its health answers. A join into it says the same word
    /// (<c>RESOLVE_UNREACHABLE</c>, <c>params.reason: "route"</c>).
    /// </summary>
    public const string Route = "route";

    /// <summary>The <see cref="Reason"/> of a configured service nothing was measured of yet: its entry says neither that it answers nor that it does not.</summary>
    public const string Pending = "pending";

    /// <summary>The contract the owner's engine speaks, as its health named it; null when it named none or nothing is known.</summary>
    public int? Contract { get; init; }

    /// <summary>The owner's engine version, as its health named it; null when it named none or nothing is known.</summary>
    public string? EngineVersion { get; init; }

    /// <summary>
    /// Whether this host can send the owner its joins: true only for an owner that answered and
    /// speaks this engine's contract (<see cref="EngineCapabilities.Contract"/>); false for one that is
    /// not configured, did not answer, or answered without naming that contract; null while nothing
    /// is measured or the client does not say what the owner runs.
    /// </summary>
    public bool? Usable { get; init; }

    /// <summary>Why the owner is not usable (<see cref="NotConfigured"/>, <see cref="Unreachable"/>, <see cref="Engine"/>); null otherwise.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The targets of the references this host declares into the service, checked against the owner's
    /// schema document (<see cref="RemoteReferenceValidator"/>): how many are declared, whether the
    /// check could be made, and the target entities the owner does not publish. Null when the host
    /// runs no such check.
    /// </summary>
    public RemoteReferenceValidation? References { get; init; }

    /// <summary>
    /// Whether the state is a finding: the status a contract 2 caller reads on <c>/oxql/health</c> is
    /// <c>degraded</c> with one. A caller without the contract header reads the status by the first
    /// two conditions alone (not configured, not reachable).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Degrades => !Configured || Reachable == false || Usable == false || References is { UnknownTargets.Count: > 0 };

    /// <summary>The state of a service this host has no <c>InternalHosts</c> entry for.</summary>
    public static RemoteServiceState Unconfigured(string service) => new(service, false, null) { Usable = false, Reason = NotConfigured };

    /// <summary>A configured service before its first measurement.</summary>
    public static RemoteServiceState Unmeasured(string service) => new(service, true, null) { Reason = Pending };

    /// <summary>A service whose internal query route does not answer; <paramref name="reachable"/> is what its health said.</summary>
    public static RemoteServiceState WithoutRoute(string service, bool reachable, RemoteOwnerInfo? owner) =>
        new(service, true, reachable) { Contract = owner?.Contract, EngineVersion = owner?.EngineVersion, Usable = false, Reason = Route };

    /// <summary>
    /// The state of a configured service after one measurement: whether its health answered, and
    /// what the client then knows of it (<paramref name="owner"/>; null when it knows nothing).
    /// </summary>
    public static RemoteServiceState Measured(string service, bool reachable, RemoteOwnerInfo? owner)
    {
        if (!reachable)
            return new RemoteServiceState(service, true, false) { Usable = false, Reason = Unreachable };

        if (owner is null)
            return new RemoteServiceState(service, true, true);

        var speaks = owner.Contract == EngineCapabilities.Contract;

        return new RemoteServiceState(service, true, true)
        {
            Contract = owner.Contract,
            EngineVersion = owner.EngineVersion,
            Usable = speaks,
            Reason = speaks ? null : Engine,
        };
    }
}

/// <summary>
/// The reachability behind <c>/oxql/health</c>, measured at most once per
/// <c>OxQL:Cache:HealthProbeTtlSeconds</c> and shared by every caller.
/// <para>
/// The endpoint is anonymous on purpose — it answers "why is my list not working" in a browser —
/// which is exactly why a request must neither wait for a measurement nor be able to cause more
/// of them. <see cref="State"/> answers from the last measurement at once, however old it is;
/// when that measurement is older than the interval it starts one refresh in the background,
/// and no second one while the first is running. The refresh runs under its own timeout and
/// never under a caller's token, so a caller that disconnects neither cancels it nor makes the
/// next caller start it again. Before the first measurement has finished a configured service
/// is reported with an unknown reachability, which does not degrade the status.
/// </para>
/// </summary>
public sealed class RemoteHealthProbe(OxQLOptions options, ILogger<RemoteHealthProbe>? logger = null, TimeProvider? time = null)
{
    private readonly object gate = new();
    private readonly ILogger logger = logger ?? (ILogger)NullLogger<RemoteHealthProbe>.Instance;
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private IReadOnlyList<RemoteServiceState>? measured;
    private long? takenAt;
    private Task? refresh;

    /// <summary>How long one service may take to answer before it counts as not reachable.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The host's record of owners that did not answer, which the keyed fetch reads before it calls.
    /// A measurement that finds an owner answering forgets it there, so a join asks the owner again
    /// as soon as health sees it back. A measurement writes an outage there only when no connection
    /// to the owner could be made (refused, no address for the name, the connect timed out): one that
    /// ran out of the probe's time, was answered false or failed in any other way says nothing a join
    /// should go by, since the owner's health route is not the route a join calls. Null: the probe
    /// only reports.
    /// </summary>
    public OwnerAvailability? Availability { get; init; }

    /// <summary>The measurement that is running, or a completed task when none is. The request path never awaits it.</summary>
    public Task Refreshing
    {
        get
        {
            lock (gate)
                return refresh ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// The state of every remotely referenced service as last measured, without waiting: a
    /// measurement that is due is started in the background and answers a later call.
    /// </summary>
    public IReadOnlyList<RemoteServiceState> State(EntityModel? model, IRemoteQueryClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var ttl = TimeSpan.FromSeconds(Math.Max(1, options.Cache.HealthProbeTtlSeconds));

        lock (gate)
        {
            var due = takenAt is not { } taken || time.GetElapsedTime(taken) >= ttl;

            if (!due && measured is not null)
                return measured;

            var services = model is null ? [] : RemoteReferences.ServicesOf(model);

            if (due && refresh is null)
                refresh = Task.Run(() => RefreshAsync(services, client));

            return measured ?? services.Select(service => client.IsConfigured(service) ? RemoteServiceState.Unmeasured(service) : RemoteServiceState.Unconfigured(service)).ToList();
        }
    }

    private async Task RefreshAsync(IReadOnlyList<string> services, IRemoteQueryClient client)
    {
        IReadOnlyList<RemoteServiceState>? states = null;

        try
        {
            states = await Task.WhenAll(services.Select(service => ProbeAsync(service, client))).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "OxQL health measurement failed; the last known state stays in place");
        }
        finally
        {
            // The interval restarts whether or not the measurement succeeded, so a client that
            // keeps failing is asked once per interval and not once per request.
            lock (gate)
            {
                measured = states ?? measured;
                takenAt = time.GetTimestamp();
                refresh = null;
            }
        }
    }

    private async Task<RemoteServiceState> ProbeAsync(string service, IRemoteQueryClient client)
    {
        if (!client.IsConfigured(service))
            return RemoteServiceState.Unconfigured(service);

        using var timeout = new CancellationTokenSource(ProbeTimeout);

        try
        {
            // WaitAsync bounds a client that does not observe the token.
            var reachable = await client.IsReachableAsync(service, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);

            // An answering health says the owner's host is up, not that it has the internal query route.
            // One that says false does not say why, and a join finds out for itself.
            if (reachable)
                Availability?.Succeeded(service, keepRoute: true);

            var owner = reachable ? (client as IRemoteOwnerInfo)?.OwnerOf(service) : null;

            // A join that found the owner's query route missing said so; health says the same of the owner
            // until a call finds the route, whether or not its health answers.
            if (Availability?.OutageOf(service) is { Reason: OwnerAvailability.Route })
                return RemoteServiceState.WithoutRoute(service, reachable, owner);

            // The client keeps what the owner's health said of it where it measures reachability.
            return RemoteServiceState.Measured(service, reachable, owner);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The owner's host answered and has no such route: the API version it is called with, not its reach.
            logger.LogDebug(exception, "OxQL health probe of {Service} found no route", service);

            return RemoteServiceState.WithoutRoute(service, reachable: false, null);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "OxQL health probe of {Service} failed", service);

            if (NoConnection(exception))
                Availability?.Failed(service, OwnerAvailability.Unreachable);

            return RemoteServiceState.Measured(service, false, null);
        }
    }

    /// <summary>
    /// Whether a probe failed because no connection to the owner could be made: the client's HTTP
    /// stack says so (a connection error, a name that does not resolve), or a socket error is what it
    /// failed on. A probe that was cancelled or timed out is none, whatever it was waiting for.
    /// </summary>
    private static bool NoConnection(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case OperationCanceledException or TimeoutException:
                    return false;

                case HttpRequestException { StatusCode: null, HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError }:
                case System.Net.Sockets.SocketException:
                    return true;
            }
        }

        return false;
    }
}
