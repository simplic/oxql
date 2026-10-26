using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Mongo.Explain;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// An explain under <c>remote: "check"</c> never says more than it checked. A remote owner no check
/// asked (the query shows its row whole, continues nothing there, and the caller did not ask for the
/// shape) is judged by what the host knows of it: known to speak this engine's contract it is taken
/// to answer; unknown, on another contract, or remembered as not answering it is noted
/// <c>REMOTE_UNCHECKED</c> and the verdict is <c>unknown</c>. And the owners' budget is the owners':
/// it runs from the first round, and a part nothing was sent for says so.
/// </summary>
public class ExplainUncheckedOwnerTests
{
    private const string Invoice = ResolveModel.Invoice;

    /// <summary>The whole alias and no path under it: nothing for the owner to bind, so no check asks it.</summary>
    private const string WholeAlias = """[{ "resolve": { "path": "contactId", "as": "c" } }, { "project": { "number": 1, "c": 1 } }, { "page": { "limit": 5 } }]""";

    private static MongoQueryEngine Engine(FakeRemoteClient client, OxQLOptions options) =>
        new(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, options, client);

    private static async Task<ExplainResult> ExplainAsync(MongoQueryEngine engine, OxQLOptions options, string pipeline = WholeAlias, string envelope = """ "remote": "check", "include": ["notes"] """)
    {
        var body = $$"""{ "query": { "entityType": "{{Invoice}}", "pipeline": {{pipeline}} }, {{envelope}} }""";
        var outcome = await engine.ExplainAsync(ExplainAnswer.Envelope(body), BindHost.Context(options));

        return outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
    }

    private static Task<ExplainResult> ExplainAsync(FakeRemoteClient client, string pipeline = WholeAlias, string envelope = """ "remote": "check", "include": ["notes"] """)
    {
        var options = BindHost.Options();

        return ExplainAsync(Engine(client, options), options, pipeline, envelope);
    }

    [Fact]
    public async Task An_owner_nothing_is_known_of_is_noted_unchecked_and_the_verdict_is_unknown()
    {
        var client = new FakeRemoteClient();

        var result = await ExplainAsync(client);

        result.Valid.Should().BeTrue();
        client.ExplainCalls.Should().BeEmpty("there is nothing for the owner to bind, so it is not asked");
        result.Executable.Should().Be(Basis.Unknown, "whether the owner answers the join was not checked");
        result.Cache.Complete.Should().BeFalse();

        var note = result.Notes.Should().ContainSingle(note => note.Code == Notes.RemoteUnchecked).Subject;

        note.Stage.Should().Be(0);
        note.Params.Should().Contain("service", "crm").And.Contain("target", "crm.contact").And.Contain("reason", RemoteExplain.Unknown).And.Contain("alias", "c");

        var owner = result.OwnerOf("crm");

        owner["answered"].Should().BeNull("it was never asked");
        owner["calls"]!.GetValue<int>().Should().Be(0);
        owner["reason"]!.GetValue<string>().Should().Be(RemoteExplain.Unknown);
        owner.ContainsKey("engine").Should().BeFalse();
    }

    [Fact]
    public async Task An_owner_known_to_speak_this_engines_contract_is_taken_to_answer_and_named_with_its_engine()
    {
        var client = new FakeRemoteClient { Owners = { ["crm"] = new RemoteOwnerInfo("2.1.0", EngineCapabilities.Contract, 10) } };

        var result = await ExplainAsync(client);

        client.ExplainCalls.Should().BeEmpty();
        result.Executable.Should().Be(Basis.Yes);
        result.Cache.Complete.Should().BeTrue();
        result.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked);

        var owner = result.OwnerOf("crm");

        owner["answered"].Should().BeNull();
        owner["calls"]!.GetValue<int>().Should().Be(0);
        owner["engine"]!.ToJsonString().Should().Be("""{"version":"2.1.0","contract":2}""");
        owner.ContainsKey("reason").Should().BeFalse();
    }

    [Fact]
    public async Task The_owners_facts_are_read_for_the_explain_as_a_run_reads_them()
    {
        var client = new FakeRemoteClient { Probe = _ => new RemoteOwnerInfo("2.1.0", EngineCapabilities.Contract, 10) };

        var result = await ExplainAsync(client);

        client.Probed.Should().Equal("crm");
        result.Executable.Should().Be(Basis.Yes);
    }

    [Theory]
    [InlineData(null, "it names none")]
    [InlineData(1, "it names contract 1")]
    public async Task An_owner_whose_health_names_another_contract_or_none_is_noted_unchecked(int? contract, string said)
    {
        var client = new FakeRemoteClient { Owners = { ["crm"] = new RemoteOwnerInfo(contract is null ? null : "1.0.0", contract, null) } };

        var result = await ExplainAsync(client);

        result.Executable.Should().Be(Basis.Unknown);

        var note = result.Notes.Should().ContainSingle(note => note.Code == Notes.RemoteUnchecked).Subject;

        note.Message.Should().Contain(said).And.Contain("the join 'c' is null");
        note.Params.Should().Contain("reason", RemoteExplain.Engine);
        result.OwnerOf("crm")["reason"]!.GetValue<string>().Should().Be(RemoteExplain.Engine);

        if (contract is not null)
            result.OwnerOf("crm")["engine"]!.ToJsonString().Should().Be("""{"version":"1.0.0","contract":1}""");
    }

    [Fact]
    public async Task An_owner_the_host_remembers_as_not_answering_is_noted_unchecked_whatever_its_facts_say()
    {
        var client = new FakeRemoteClient { Owners = { ["crm"] = new RemoteOwnerInfo("2.1.0", EngineCapabilities.Contract, 10) } };
        var options = BindHost.Options();
        var engine = Engine(client, options);

        engine.Availability.Failed("crm", OwnerAvailability.Unreachable);

        var result = await ExplainAsync(engine, options);

        result.Executable.Should().Be(Basis.Unknown);
        result.Notes.Should().ContainSingle(note => note.Code == Notes.RemoteUnchecked).Which.Params.Should().Contain("reason", OwnerAvailability.Unreachable);
        result.OwnerOf("crm")["reason"]!.GetValue<string>().Should().Be(OwnerAvailability.Unreachable);
    }

    /// <summary>A client that reaches owners and keeps nothing of what their health says.</summary>
    private sealed class FactlessClient : IRemoteQueryClient
    {
        public int Calls;

        public Task<BatchResponse> BatchAsync(string serviceKey, BatchRequest request, TimeSpan budget, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);

            throw new NotSupportedException();
        }

        public bool IsConfigured(string serviceKey) => true;

        public Task<bool> IsReachableAsync(string serviceKey, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static async Task<ExplainResult> ExplainAsync(IRemoteQueryClient client, Action<MongoQueryEngine>? before = null, string tier = "check")
    {
        var options = BindHost.Options();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), new FakeAggregateRunner(), BindHost.Cursors, options, client);

        before?.Invoke(engine);

        var body = $$"""{ "query": { "entityType": "{{Invoice}}", "pipeline": {{WholeAlias}} }, "remote": "{{tier}}", "include": ["notes"] }""";

        return (await engine.ExplainAsync(ExplainAnswer.Envelope(body), BindHost.Context(options))).Should().BeOfType<ExplainOutcome.Success>().Subject.Result;
    }

    [Fact]
    public async Task A_host_whose_client_keeps_no_owner_facts_answers_a_join_nobody_asked_without_a_note()
    {
        var client = new FactlessClient();

        var result = await ExplainAsync(client);

        result.Valid.Should().BeTrue();
        result.Executable.Should().Be(Basis.Yes, "there is nothing such a host could ever judge the owner by");
        result.Cache.Complete.Should().BeTrue();
        result.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked);
        client.Calls.Should().Be(0);

        var owner = result.OwnerOf("crm");

        owner["answered"].Should().BeNull("it was never asked");
        owner["calls"]!.GetValue<int>().Should().Be(0);
        owner.ContainsKey("reason").Should().BeFalse();
        owner.ContainsKey("engine").Should().BeFalse();
    }

    [Fact]
    public async Task A_host_whose_client_keeps_no_owner_facts_does_not_judge_an_owner_by_what_it_remembers_either()
    {
        var result = await ExplainAsync(new FactlessClient(), engine => engine.Availability.Failed("crm", OwnerAvailability.Unreachable));

        result.Executable.Should().Be(Basis.Yes);
        result.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked);
        result.OwnerOf("crm").ContainsKey("reason").Should().BeFalse();
    }

    [Fact]
    public async Task Two_answers_that_say_the_same_of_the_request_have_the_same_validator()
    {
        var facts = new RemoteOwnerInfo("2.1.0", EngineCapabilities.Contract, 10);

        // A client without facts: the check and the tier that asks nobody say the same.
        var plain = await ExplainAsync(new FactlessClient());
        var plainCached = await ExplainAsync(new FactlessClient(), tier: "cached");

        plain.Etag.Should().NotBeNull();
        plainCached.Etag.Should().Be(plain.Etag);

        // An owner known to speak the contract: the check lists what the host knows of its engine, the
        // cached tier does not, and both say that the query runs.
        var known = await ExplainAsync(new FakeRemoteClient { Owners = { ["crm"] = facts } });
        var knownCached = await ExplainAsync(new FakeRemoteClient { Owners = { ["crm"] = facts } }, tier: "cached");

        known.Executable.Should().Be(Basis.Yes);
        knownCached.Executable.Should().Be(Basis.Yes);
        known.OwnerOf("crm").ContainsKey("engine").Should().BeTrue();
        knownCached.Etag.Should().Be(known.Etag);
        known.Etag.Should().Be(plain.Etag, "the same request, the same verdict, nothing else a caller reads of the request");

        // An owner remembered as not answering: the same note and verdict with and without its facts.
        var down = await ExplainAsync(new FakeRemoteClient { Owners = { ["crm"] = facts } }, engine => engine.Availability.Failed("crm", OwnerAvailability.Unreachable));
        var downUnknown = await ExplainAsync(new FakeRemoteClient(), engine => engine.Availability.Failed("crm", OwnerAvailability.Unreachable));

        down.Executable.Should().Be(Basis.Unknown);
        downUnknown.Executable.Should().Be(Basis.Unknown);
        downUnknown.Etag.Should().Be(down.Etag);

        // Another verdict is another validator.
        var unknown = await ExplainAsync(new FakeRemoteClient());

        unknown.Executable.Should().Be(Basis.Unknown);
        unknown.Etag.Should().NotBe(known.Etag).And.NotBe(down.Etag, "its note says another reason");
    }

    [Fact]
    public async Task The_cached_tier_asks_nobody_and_is_answered_as_before()
    {
        var client = new FakeRemoteClient();

        var result = await ExplainAsync(client, envelope: """ "remote": "cached", "include": ["notes"] """);

        client.Probed.Should().BeEmpty();
        result.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked);
        result.OwnerOf("crm").ContainsKey("reason").Should().BeFalse();
    }

    [Fact]
    public async Task An_owner_a_check_asked_is_judged_by_its_answer_alone()
    {
        // With the shape asked for, every remote target is checked: the owner's answer says what holds.
        var client = OwnerFleet.Client();

        var result = await ExplainAsync(client, envelope: """ "remote": "check", "include": ["shape", "notes"] """);

        client.ExplainCalls.Should().ContainSingle();
        result.Executable.Should().Be(Basis.Yes);
        result.Notes.Should().NotContain(note => note.Code == Notes.RemoteUnchecked);
        result.OwnerOf("crm")["answered"]!.GetValue<bool>().Should().BeTrue();
    }

    // ---- the owners' budget -------------------------------------------------------------------------

    [Fact]
    public async Task What_comes_before_the_first_round_is_not_the_owners_time()
    {
        // Reading the owner's facts takes longer than the whole of the owners' budget: the read is cut
        // at its slice, and the owner is asked with the budget it has.
        var client = OwnerFleet.Client();

        client.ProbeDelay = TimeSpan.FromMilliseconds(600);

        var options = BindHost.Options(configure =>
        {
            configure.Explain.RemoteTimeoutMs = 400;
            configure.Explain.TimeoutMs = 10_000;
        });

        var result = await ExplainAsync(Engine(client, options), options, envelope: """ "remote": "check", "include": ["shape", "notes"] """);

        client.ExplainCalls.Should().ContainSingle("the owner is asked: the time before the first round was not its");
        result.OwnerOf("crm")["answered"]!.GetValue<bool>().Should().BeTrue();
        result.OwnerOf("crm")["calls"]!.GetValue<int>().Should().Be(1);
        result.Executable.Should().Be(Basis.Yes);
    }

    [Fact]
    public async Task A_part_nothing_was_sent_for_says_budget_and_only_an_owner_that_was_asked_says_timeout()
    {
        var client = OwnerFleet.Client();

        client.Silent.Add("crm");

        var options = BindHost.Options(configure => configure.Explain.RemoteTimeoutMs = 150);

        // The check of the resolve is the first round, which the silent owner spends; the lookup of the
        // transport entity comes after it and is not sent.
        var result = await ExplainAsync(Engine(client, options), options,
            """[{ "resolve": { "path": "contactId", "as": "ct" } }]""",
            """ "remote": "check", "catalog": [{ "id": "t", "entity": "transport.shipment" }] """);

        client.ExplainCalls.Select(call => call.Service).Should().Equal("crm");

        var asked = result.OwnerOf("crm");

        asked["answered"]!.GetValue<bool>().Should().BeFalse();
        asked["calls"]!.GetValue<int>().Should().Be(1);
        asked["reason"]!.GetValue<string>().Should().Be(RemoteExplain.Timeout);

        var unsent = result.OwnerOf("transport");

        unsent["answered"]!.GetValue<bool>().Should().BeFalse();
        unsent["calls"]!.GetValue<int>().Should().Be(0);
        unsent["reason"]!.GetValue<string>().Should().Be(RemoteExplain.Budget, "nothing was sent, so nothing is said of whether it answers");
        result.Catalog.Single()["error"]!["params"]!["reason"]!.GetValue<string>().Should().Be(RemoteExplain.Budget);
        result.Notes.Should().NotContain(note => note.Code == Notes.ExplainLimit, "the explain's own time is not what ran out");
        result.Cache.Complete.Should().BeFalse();
    }
}
