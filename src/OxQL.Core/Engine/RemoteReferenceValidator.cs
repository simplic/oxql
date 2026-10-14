using System.Text.Json.Serialization;
using OxQL.Model;

namespace OxQL.Core.Engine;

/// <summary>
/// What a remote client read of an owner's schema document: the document's revision, and every
/// entity id the owner answers a query under (the ids of its entities and the ids it has retired
/// in favour of them). <see cref="EntityIds"/> is null for the answer "not modified": the owner's
/// document is still the one of the revision the caller named.
/// </summary>
public sealed record RemoteSchemaEntities(string? Revision, IReadOnlyCollection<string>? EntityIds)
{
    /// <summary>The answer of an owner whose document is still the one of <paramref name="revision"/>: nothing was read again.</summary>
    public static RemoteSchemaEntities NotModified(string revision) => new(revision, null);

    /// <summary>Whether the owner said its document has not changed since the revision the caller named.</summary>
    [JsonIgnore]
    public bool IsNotModified => EntityIds is null;
}

/// <summary>
/// An optional capability of an <see cref="IRemoteQueryClient"/>: reading which entities an owner
/// publishes, so the host can check the targets of its declared remote references against the
/// owner's own schema document (<see cref="RemoteReferenceValidator"/>). One conditional
/// <c>GET</c> of the owner's schema document serves it: the client sends the revision it is handed
/// as the validator of the request and answers <see cref="RemoteSchemaEntities.NotModified"/> on 304.
/// A client without the capability leaves the references not validated.
/// </summary>
public interface IRemoteSchemaSource
{
    /// <summary>
    /// The entities the owner behind <paramref name="serviceKey"/> publishes in its schema document.
    /// <paramref name="knownRevision"/> is the revision of the document the caller read last, or null:
    /// an owner whose document is still that one may be answered as
    /// <see cref="RemoteSchemaEntities.NotModified"/>. Null when the owner publishes no document the
    /// client can read (it serves none, or not under the address this host calls); a call that fails
    /// throws. Either leaves the owner's references not validated, never wrong.
    /// </summary>
    Task<RemoteSchemaEntities?> SchemaEntitiesAsync(string serviceKey, string? knownRevision, CancellationToken cancellationToken);
}

/// <summary>
/// The targets of the references a host declares into one service, checked against that owner's
/// schema document. <see cref="Validated"/> is false while the check could not be made, with the
/// <see cref="Reason"/>; a reference is then neither right nor wrong. Validated,
/// <see cref="UnknownTargets"/> lists the declared target entity ids the owner does not publish
/// (ordinally sorted, each once), read of the document of <see cref="Revision"/>.
/// </summary>
public sealed record RemoteReferenceValidation(
    [property: JsonIgnore] string Service,
    int Declared,
    bool Validated,
    string? Revision,
    IReadOnlyList<string> UnknownTargets,
    string? Reason = null)
{
    /// <summary><see cref="Reason"/> of a host whose remote client cannot read schema documents (<see cref="IRemoteSchemaSource"/>).</summary>
    public const string Unsupported = "unsupported";

    /// <summary><see cref="Reason"/> of a service the host has no <c>InternalHosts</c> entry for.</summary>
    public const string NotConfigured = "notConfigured";

    /// <summary><see cref="Reason"/> of an owner that did not answer, or serves no schema document the client can read.</summary>
    public const string Unavailable = "unavailable";

    /// <summary><see cref="Reason"/> of a check that has not run yet.</summary>
    public const string Pending = "pending";
}

/// <summary>
/// Checks the targets of a host's declared remote references against the owners' schema documents,
/// in the background: a declaration that names an entity its owner does not publish (a wrong id, an
/// entity the owner renamed) builds without a finding, since the host cannot see another service's
/// model when it builds its own.
/// <para>
/// It never blocks and never fails anything. <see cref="State"/> answers from the last check at
/// once and starts the next one in the background when it is due (one at a time, under its own
/// timeout, never under a caller's token): every <see cref="RetryInterval"/> while a service is not
/// validated, every <see cref="RefreshInterval"/> once all are, the read conditional on the revision
/// last seen. An owner that is down, serves no schema document, or is reached by a client without
/// <see cref="IRemoteSchemaSource"/> leaves its references not validated; what was validated before
/// an owner stopped answering stays, since it is still true of the revision it names.
/// </para>
/// </summary>
public sealed class RemoteReferenceValidator(TimeProvider? time = null)
{
    private readonly object gate = new();
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private IReadOnlyList<RemoteReferenceValidation>? checkedLast;
    private long? takenAt;
    private Task? refresh;

    /// <summary>How long one owner may take to answer before its references stay not validated.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long after a check that left a service not validated the next one starts.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long after a check that validated every service the next one starts.</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Called after a check with each service whose result is new or has changed; a host logs from it.
    /// It runs on the checking thread, and what it throws is swallowed.
    /// </summary>
    public Action<RemoteReferenceValidation>? Changed { get; set; }

    /// <summary>The check that is running, or a completed task when none is. A request path never awaits it.</summary>
    public Task Refreshing
    {
        get
        {
            lock (gate)
                return refresh ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// The result of the last check per service the model references remotely, without waiting; a
    /// check that is due is started in the background and answers a later call. Before the first
    /// check has finished every service is not validated (<see cref="RemoteReferenceValidation.Pending"/>).
    /// </summary>
    public IReadOnlyList<RemoteReferenceValidation> State(EntityModel? model, IRemoteQueryClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        lock (gate)
        {
            var interval = checkedLast is not null && checkedLast.All(service => service.Validated) ? RefreshInterval : RetryInterval;
            var due = takenAt is not { } taken || time.GetElapsedTime(taken) >= interval;

            if (!due && checkedLast is not null)
                return checkedLast;

            var declared = model is null ? [] : DeclaredOf(model);

            // A model without remote references has nothing to check, now or at the next call.
            if (model is not null && declared.Count == 0)
            {
                takenAt = time.GetTimestamp();

                return checkedLast = [];
            }

            if (due && refresh is null && declared.Count > 0)
            {
                var before = checkedLast ?? [];

                refresh = Task.Run(() => RefreshAsync(declared, before, client));
            }

            return checkedLast ?? declared.Select(service => new RemoteReferenceValidation(service.Service, service.References, false, null, [], RemoteReferenceValidation.Pending)).ToList();
        }
    }

    /// <summary>The result of the last check for <paramref name="service"/>, or null when none has finished; starts nothing.</summary>
    public RemoteReferenceValidation? Of(string service)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (gate)
            return checkedLast?.FirstOrDefault(each => each.Service == service);
    }

    /// <summary>Per service, ordinally: how many references the model declares into it, and the distinct target entities they name.</summary>
    private static List<(string Service, int References, IReadOnlyList<string> Targets)> DeclaredOf(EntityModel model) =>
        RemoteReferences.Of(model)
            .GroupBy(reference => reference.Service, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, group.Count(), (IReadOnlyList<string>)group.Select(reference => reference.TargetEntity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()))
            .ToList();

    private async Task RefreshAsync(IReadOnlyList<(string Service, int References, IReadOnlyList<string> Targets)> declared, IReadOnlyList<RemoteReferenceValidation> before, IRemoteQueryClient client)
    {
        IReadOnlyList<RemoteReferenceValidation>? results = null;

        try
        {
            results = await Task.WhenAll(declared.Select(service =>
                CheckAsync(service.Service, service.References, service.Targets, before.FirstOrDefault(each => each.Service == service.Service), client))).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A check never fails anything: what was known stays.
        }
        finally
        {
            lock (gate)
            {
                checkedLast = results ?? checkedLast;
                takenAt = time.GetTimestamp();
                refresh = null;
            }
        }

        if (results is null || Changed is not { } changed)
            return;

        foreach (var result in results)
        {
            if (before.FirstOrDefault(each => each.Service == result.Service) is { } known && Same(known, result))
                continue;

            try
            {
                changed(result);
            }
            catch (Exception)
            {
                // A listener that fails is the listener's.
            }
        }
    }

    private static bool Same(RemoteReferenceValidation one, RemoteReferenceValidation other) =>
        one.Validated == other.Validated && one.Revision == other.Revision && one.Reason == other.Reason && one.Declared == other.Declared
        && one.UnknownTargets.SequenceEqual(other.UnknownTargets, StringComparer.Ordinal);

    private async Task<RemoteReferenceValidation> CheckAsync(string service, int references, IReadOnlyList<string> targets, RemoteReferenceValidation? known, IRemoteQueryClient client)
    {
        // What was validated stays true of the revision it names, whatever the owner does now.
        RemoteReferenceValidation Unchecked(string reason) => known is { Validated: true }
            ? known with { Declared = references }
            : new RemoteReferenceValidation(service, references, false, null, [], reason);

        if (client is not IRemoteSchemaSource source)
            return Unchecked(RemoteReferenceValidation.Unsupported);

        if (!client.IsConfigured(service))
            return new RemoteReferenceValidation(service, references, false, null, [], RemoteReferenceValidation.NotConfigured);

        using var timeout = new CancellationTokenSource(ReadTimeout, time);

        try
        {
            // WaitAsync bounds a client that does not observe the token.
            var read = await source.SchemaEntitiesAsync(service, known is { Validated: true } ? known.Revision : null, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);

            if (read is null || read.EntityIds is not { } ids)
                return Unchecked(RemoteReferenceValidation.Unavailable);

            var published = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);

            return new RemoteReferenceValidation(service, references, true, read.Revision, targets.Where(target => !published.Contains(target)).ToList());
        }
        catch (Exception)
        {
            return Unchecked(RemoteReferenceValidation.Unavailable);
        }
    }
}
