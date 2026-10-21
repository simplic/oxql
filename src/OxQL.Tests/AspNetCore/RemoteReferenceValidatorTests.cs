using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore.Health;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.TestCases;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The targets of a host's declared remote references are checked against the owners' schema
/// documents in the background: the check never blocks a caller and never fails anything, an owner
/// that does not answer leaves its references not validated, a target the owner does not publish
/// is listed on health (which it degrades) and noted by explain.
/// </summary>
public class RemoteReferenceValidatorTests
{
    /// <summary>A client that reads owners' schema documents from a script, and counts the reads.</summary>
    private sealed class SchemaClient : IRemoteQueryClient, IRemoteSchemaSource, IRemoteOwnerInfo
    {
        private int reads;

        /// <summary>What each owner publishes; an owner without an entry does not answer.</summary>
        public Dictionary<string, RemoteSchemaEntities> Published { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Unconfigured { get; } = new(StringComparer.Ordinal);

        /// <summary>Owners whose read never ends, and ignores its token.</summary>
        public HashSet<string> Hanging { get; } = new(StringComparer.Ordinal);

        public List<(string Service, string? KnownRevision)> Reads { get; } = [];

        public int ReadCount => Volatile.Read(ref reads);

        private readonly List<(int Count, TaskCompletionSource Reached)> waiting = [];

        /// <summary>Ends once <paramref name="count"/> schema documents have been asked for.</summary>
        public Task ReadAsync(int count)
        {
            lock (Reads)
            {
                if (ReadCount >= count)
                    return Task.CompletedTask;

                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                waiting.Add((count, reached));

                return reached.Task;
            }
        }

        public Task<RemoteSchemaEntities?> SchemaEntitiesAsync(string serviceKey, string? knownRevision, CancellationToken cancellationToken)
        {
            lock (Reads)
            {
                var now = Interlocked.Increment(ref reads);

                Reads.Add((serviceKey, knownRevision));

                foreach (var (count, reached) in waiting)
                    if (now >= count)
                        reached.TrySetResult();
            }

            if (Hanging.Contains(serviceKey))
                return new TaskCompletionSource<RemoteSchemaEntities?>().Task;

            if (!Published.TryGetValue(serviceKey, out var published))
                throw new HttpRequestException($"'{serviceKey}' is not reachable.");

            // The conditional read: an owner whose document is the one the caller holds says so.
            return Task.FromResult<RemoteSchemaEntities?>(knownRevision is not null && knownRevision == published.Revision ? RemoteSchemaEntities.NotModified(knownRevision) : published);
        }

        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool IsConfigured(string serviceKey) => !Unconfigured.Contains(serviceKey);

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => Task.FromResult(true);

        public RemoteOwnerInfo? OwnerOf(string serviceKey) => new("2.1.0", EngineCapabilities.Contract, 10);
    }

    /// <summary>The resolve graph declares references into <c>crm</c> (two: <c>crm.contact</c> from two entities) and <c>transport</c>.</summary>
    private static readonly OxQL.Model.EntityModel Model = ResolveModel.Model;

    private static async Task<IReadOnlyList<RemoteReferenceValidation>> CheckedAsync(RemoteReferenceValidator validator, IRemoteQueryClient client)
    {
        validator.State(Model, client);
        await validator.Refreshing;

        return validator.State(Model, client);
    }

    private static SchemaClient Publishing(params string[] crm) => new()
    {
        Published =
        {
            ["crm"] = new RemoteSchemaEntities("sha256:crm-1", crm),
            ["transport"] = new RemoteSchemaEntities("sha256:tr-1", ["transport.shipment", "transport.tour"]),
        },
    };

    // ---- the check ----------------------------------------------------------------------------------

    [Fact]
    public async Task Every_declared_target_the_owner_publishes_validates_the_service()
    {
        var states = await CheckedAsync(new RemoteReferenceValidator(), Publishing("crm.contact", "crm.company"));

        states.Select(state => state.Service).Should().Equal("crm", "transport");
        states.Should().OnlyContain(state => state.Validated && state.UnknownTargets.Count == 0 && state.Reason == null);
        states[0].Revision.Should().Be("sha256:crm-1");
        states[0].Declared.Should().Be(RemoteReferences.Of(Model).Count(reference => reference.Service == "crm"));
    }

    [Fact]
    public async Task A_declared_target_the_owner_does_not_publish_is_listed_once()
    {
        var states = await CheckedAsync(new RemoteReferenceValidator(), Publishing("crm.person", "crm.company"));
        var crm = states.Single(state => state.Service == "crm");

        crm.Validated.Should().BeTrue("the owner answered: the check was made");
        crm.UnknownTargets.Should().Equal(["crm.contact"], "two references name it, and it is one target");
        states.Single(state => state.Service == "transport").UnknownTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task An_id_the_owner_has_retired_in_favour_of_another_and_an_id_in_another_case_validate()
    {
        // The owner lists every id it answers a query under: its entities' ids and the ones it retired.
        var states = await CheckedAsync(new RemoteReferenceValidator(), Publishing("crm.person", "CRM.Contact"));

        states.Single(state => state.Service == "crm").UnknownTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task An_owner_that_does_not_answer_leaves_its_references_not_validated_and_never_wrong()
    {
        var client = Publishing("crm.contact");

        client.Published.Remove("transport");

        var states = await CheckedAsync(new RemoteReferenceValidator(), client);
        var transport = states.Single(state => state.Service == "transport");

        transport.Validated.Should().BeFalse();
        transport.Reason.Should().Be(RemoteReferenceValidation.Unavailable);
        transport.UnknownTargets.Should().BeEmpty("nothing was checked, so nothing is unknown");
        states.Single(state => state.Service == "crm").Validated.Should().BeTrue("one owner's silence is its own");
    }

    [Fact]
    public async Task An_owner_without_a_schema_document_a_service_that_is_not_configured_and_a_client_that_reads_none_are_not_validated()
    {
        var client = Publishing("crm.contact");

        client.Unconfigured.Add("transport");

        (await CheckedAsync(new RemoteReferenceValidator(), client)).Single(state => state.Service == "transport")
            .Should().Match<RemoteReferenceValidation>(state => !state.Validated && state.Reason == RemoteReferenceValidation.NotConfigured);
        client.Reads.Select(read => read.Service).Should().Equal(["crm"], "a service the host cannot reach is not asked");

        var plain = new FakeRemoteClient();
        var unsupported = await CheckedAsync(new RemoteReferenceValidator(), plain);

        unsupported.Should().OnlyContain(state => !state.Validated && state.Reason == RemoteReferenceValidation.Unsupported && state.Declared > 0);
    }

    [Fact]
    public async Task What_was_validated_stays_when_the_owner_stops_answering()
    {
        var time = new ManualTime();
        var client = Publishing("crm.person");
        var validator = new RemoteReferenceValidator(time);

        await CheckedAsync(validator, client);
        client.Published.Remove("crm");
        time.Advance(validator.RefreshInterval);

        var states = await CheckedAsync(validator, client);
        var crm = states.Single(state => state.Service == "crm");

        crm.Validated.Should().BeTrue("it is still true of the revision it names");
        crm.Revision.Should().Be("sha256:crm-1");
        crm.UnknownTargets.Should().Equal("crm.contact");
    }

    // ---- when it runs -------------------------------------------------------------------------------

    [Fact]
    public async Task A_caller_is_answered_at_once_and_never_waits_for_an_owner()
    {
        var client = Publishing("crm.contact");

        client.Hanging.Add("crm");

        var time = new ManualTime();
        var validator = new RemoteReferenceValidator(time) { ReadTimeout = TimeSpan.FromSeconds(2) };
        var first = validator.State(Model, client);

        first.Should().OnlyContain(state => !state.Validated && state.Reason == RemoteReferenceValidation.Pending, "nothing is checked yet and the caller did not wait for it");
        validator.Of("crm").Should().BeNull();

        var refreshing = validator.Refreshing;

        // Both owners are asked; the one that never answers is given up once its time is over.
        await client.ReadAsync(2).WaitAsync(ManualTime.Ceiling);
        refreshing.IsCompleted.Should().BeFalse("the read that never ends holds the check until its timeout");
        time.Advance(TimeSpan.FromSeconds(2));

        await refreshing.WaitAsync(ManualTime.Ceiling);
        validator.Of("crm")!.Reason.Should().Be(RemoteReferenceValidation.Unavailable);
        validator.Of("transport")!.Validated.Should().BeTrue();
    }

    [Fact]
    public async Task The_next_check_starts_after_the_retry_interval_while_a_service_is_not_validated_and_after_the_refresh_interval_once_all_are()
    {
        var time = new ManualTime();
        var client = Publishing("crm.contact");

        client.Published.Remove("transport");

        var validator = new RemoteReferenceValidator(time);

        await CheckedAsync(validator, client);
        client.ReadCount.Should().Be(2);

        for (var call = 0; call < 5; call++)
            validator.State(Model, client);

        await validator.Refreshing;
        client.ReadCount.Should().Be(2, "nothing is read again before the interval is over, however often the state is asked");

        // The owner is back; the retry interval passes.
        client.Published["transport"] = new RemoteSchemaEntities("sha256:tr-1", ["transport.shipment"]);
        time.Advance(validator.RetryInterval);

        (await CheckedAsync(validator, client)).Should().OnlyContain(state => state.Validated);
        client.ReadCount.Should().Be(4);

        time.Advance(validator.RetryInterval);
        validator.State(Model, client);
        await validator.Refreshing;
        client.ReadCount.Should().Be(4, "every service is validated: the next check waits for the longer interval");

        time.Advance(validator.RefreshInterval);
        await CheckedAsync(validator, client);
        client.ReadCount.Should().Be(6);
    }

    [Fact]
    public async Task A_later_read_names_the_revision_it_holds_and_an_unchanged_document_is_not_read_again()
    {
        var time = new ManualTime();
        var client = Publishing("crm.person");
        var validator = new RemoteReferenceValidator(time);

        await CheckedAsync(validator, client);
        time.Advance(validator.RefreshInterval);

        var unchanged = await CheckedAsync(validator, client);

        client.Reads.Where(read => read.Service == "crm").Select(read => read.KnownRevision).Should().Equal(null, "sha256:crm-1");
        unchanged.Single(state => state.Service == "crm").UnknownTargets.Should().Equal(["crm.contact"], "the owner said its document is the one already read");

        // The owner publishes the entity: a new revision, read in full.
        client.Published["crm"] = new RemoteSchemaEntities("sha256:crm-2", ["crm.person", "crm.contact"]);
        time.Advance(validator.RefreshInterval);

        var corrected = (await CheckedAsync(validator, client)).Single(state => state.Service == "crm");

        corrected.Revision.Should().Be("sha256:crm-2");
        corrected.UnknownTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task A_listener_hears_of_each_result_once_and_again_only_when_it_changes()
    {
        var time = new ManualTime();
        var client = Publishing("crm.person");
        var heard = new List<RemoteReferenceValidation>();
        var validator = new RemoteReferenceValidator(time) { Changed = result => { lock (heard) heard.Add(result); } };

        await CheckedAsync(validator, client);
        heard.Select(result => result.Service).Should().BeEquivalentTo(["crm", "transport"]);

        time.Advance(validator.RefreshInterval);
        await CheckedAsync(validator, client);
        heard.Should().HaveCount(2, "nothing changed");

        client.Published["crm"] = new RemoteSchemaEntities("sha256:crm-2", ["crm.contact"]);
        time.Advance(validator.RefreshInterval);
        await CheckedAsync(validator, client);

        heard.Should().HaveCount(3);
        heard[2].Should().Match<RemoteReferenceValidation>(result => result.Service == "crm" && result.UnknownTargets.Count == 0 && result.Revision == "sha256:crm-2");
    }

    [Fact]
    public void A_model_without_remote_references_asks_nobody()
    {
        var client = Publishing("crm.contact");
        var validator = new RemoteReferenceValidator();

        validator.State(OwnerFleet.Transport, client).Should().BeEmpty();
        validator.Refreshing.IsCompleted.Should().BeTrue();
        client.ReadCount.Should().Be(0);
    }

    // ---- health -------------------------------------------------------------------------------------

    private static async Task<(JsonObject Body, SampleHost Host)> HealthAsync(IRemoteQueryClient client, int? contract = 2)
    {
        var host = new SampleHost(configure: services => services.AddSingleton(client)) { Model = Model, Environment = "Production" };

        await host.Client(contract).GetAsync("/OxQL/health");
        await host.Services.GetRequiredService<RemoteHealthProbe>().Refreshing;
        await host.Services.GetRequiredService<RemoteReferenceValidator>().Refreshing;

        return ((await SampleHost.Body(await host.Client(contract).GetAsync("/OxQL/health")))!.AsObject(), host);
    }

    [Fact]
    public async Task Health_lists_per_service_the_targets_its_owner_does_not_publish_and_is_degraded_by_them()
    {
        var (body, host) = await HealthAsync(Publishing("crm.person"));

        using (host)
        {
            var remote = body["remote"]!.AsArray().Select(node => node!.AsObject()).ToList();
            var declared = RemoteReferences.Of(Model).Count(reference => reference.Service == "crm");

            body["status"]!.GetValue<string>().Should().Be("degraded");
            remote.Single(entry => entry["service"]!.GetValue<string>() == "crm")["references"]!.ToJsonString()
                .Should().Be($$"""{"declared":{{declared}},"validated":true,"revision":"sha256:crm-1","unknownTargets":["crm.contact"]}""");
            remote.Single(entry => entry["service"]!.GetValue<string>() == "transport")["references"]!.ToJsonString()
                .Should().Be("""{"declared":1,"validated":true,"revision":"sha256:tr-1","unknownTargets":[]}""");
        }
    }

    [Fact]
    public async Task Health_says_not_validated_for_an_owner_that_cannot_be_read_and_stays_healthy()
    {
        var client = Publishing("crm.contact");

        client.Published.Remove("transport");

        var (body, host) = await HealthAsync(client);

        using (host)
        {
            body["status"]!.GetValue<string>().Should().Be("healthy", "a check that could not be made is not a finding");
            body["remote"]!.AsArray().Single(entry => entry!["service"]!.GetValue<string>() == "transport")!["references"]!.ToJsonString()
                .Should().Be("""{"declared":1,"validated":false,"unknownTargets":[],"reason":"unavailable"}""");
        }
    }

    [Fact]
    public async Task Health_without_the_contract_header_has_no_references_member()
    {
        var (body, host) = await HealthAsync(Publishing("crm.person"), contract: null);

        using (host)
        {
            body["remote"]!.AsArray().Should().OnlyContain(entry => !entry!.AsObject().ContainsKey("references"));
            body["remote"]!.ToJsonString().Should().Be("""[{"service":"crm","configured":true,"reachable":true},{"service":"transport","configured":true,"reachable":true}]""");
        }
    }

    [Fact]
    public async Task The_check_starts_with_the_host_and_does_not_keep_it_from_starting()
    {
        var client = Publishing("crm.person");

        client.Hanging.Add("transport");

        using var host = new SampleHost(configure: services => services.AddSingleton<IRemoteQueryClient>(client)) { Model = Model, Environment = "Production" };

        // The host answers although an owner's schema read never ends.
        (await host.Client().GetAsync("/OxQL/health?shallow=true")).StatusCode.Should().Be(HttpStatusCode.OK);
        // The check starts once the host has started, without a request asking for it.
        await client.ReadAsync(2).WaitAsync(TimeSpan.FromSeconds(20));
    }

    // ---- explain ------------------------------------------------------------------------------------

    [Fact]
    public async Task Explain_notes_a_join_whose_target_the_owner_does_not_publish_and_does_not_say_it_runs()
    {
        var client = Publishing("crm.person");
        var validator = new RemoteReferenceValidator();
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(Model), new FakeAggregateRunner(), BindHost.Cursors, options, client) { ReferenceValidator = validator };
        const string body = """
            {
              "query": { "entityType": "rc.invoice", "pipeline": [{ "resolve": { "path": "contactId", "as": "c" } }, { "project": { "number": 1, "c": 1 } }] },
              "remote": "check",
              "include": ["notes"]
            }
            """;

        // The first explain starts the check and does not wait for it.
        await engine.ExplainAsync(ExplainAnswer.Envelope(body), BindHost.Context(options));
        await validator.Refreshing;

        client.ReadCount.Should().Be(2, "an explain starts the check that is due");

        var result = ((ExplainOutcome.Success)await engine.ExplainAsync(ExplainAnswer.Envelope(body), BindHost.Context(options))).Result;
        var note = result.Notes.Should().ContainSingle(note => note.Code == Notes.ReferenceTargetUnknown).Subject;

        note.Stage.Should().Be(0);
        note.Basis.Should().Be(Basis.Model);
        note.Message.Should().Contain("'crm' does not publish the entity 'crm.contact'");
        note.Params.Should().Contain("service", "crm").And.Contain("target", "crm.contact").And.Contain("revision", "sha256:crm-1").And.Contain("alias", "c");
        result.Valid.Should().BeTrue("the check may be behind the owner's document: it is a note, not an error");
        result.Executable.Should().Be(Basis.Unknown);
        result.Cache.Complete.Should().BeFalse();
    }
}
