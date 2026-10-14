using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using OxQL.AspNetCore.Legacy;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// What a host says of the requests it answers, for whoever has to know which callers still send
/// contract 1: the meter <c>OxQL</c> counts every query by contract, route, entity and outcome and
/// records how long it took, and the line of a contract 1 request the legacy pipeline answered
/// carries the route, the status, the rows and the time, the database's share of it included.
/// </summary>
/// <remarks>
/// The meter is one for the whole process, so the class runs in a collection of its own that runs
/// alone, after the tests that run side by side: no other test's host measures while it listens.
/// </remarks>
[Collection(MeterCollection.Name)]
public class RequestMetricsTests
{
    private const string Query = "/OxQL/query";
    private const string Batch = "/OxQL/batch";
    private const string Explain = "/OxQL/explain";

    private const string Customers = """{ "entityType": "probe.customer", "pipeline": [{ "page": { "limit": 1 } }] }""";

    /// <summary>Every measurement of the engine's meter while it listens, by instrument, with its tags.</summary>
    private sealed class Listening : IDisposable
    {
        private readonly MeterListener listener = new();

        public ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)> Seen { get; } = new();

        public Listening()
        {
            listener.InstrumentPublished = (instrument, own) =>
            {
                if (instrument.Meter.Name == OxQLMetrics.MeterName)
                    own.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Seen.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Seen.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
            listener.Start();
        }

        /// <summary>The measurements of one instrument that carry exactly these four tags.</summary>
        public List<double> Of(string instrument, string contract, string route, string entity, string outcome) =>
            Seen.Where(seen => seen.Instrument == instrument
                    && seen.Tags.Count == 4
                    && Equals(seen.Tags[OxQLMetrics.ContractTag], contract)
                    && Equals(seen.Tags[OxQLMetrics.RouteTag], route)
                    && Equals(seen.Tags[OxQLMetrics.EntityTag], entity)
                    && Equals(seen.Tags[OxQLMetrics.OutcomeTag], outcome))
                .Select(seen => seen.Value).ToList();

        public void Dispose() => listener.Dispose();
    }

    private static void ShouldHaveCounted(Listening listening, string contract, string route, string entity, string outcome, int atLeast = 1)
    {
        var counted = listening.Of(OxQLMetrics.RequestsName, contract, route, entity, outcome);
        var timed = listening.Of(OxQLMetrics.DurationName, contract, route, entity, outcome);

        counted.Should().HaveCountGreaterThanOrEqualTo(atLeast, $"{contract} {route} {entity} {outcome} among: {string.Join(" | ", listening.Seen.Where(seen => seen.Instrument == OxQLMetrics.RequestsName).Select(seen => string.Join(",", seen.Tags.Values)))}");
        counted.Should().OnlyContain(value => value == 1);
        timed.Should().HaveCount(counted.Count, "every counted query has its time").And.OnlyContain(seconds => seconds >= 0 && seconds < 60);
    }

    [Fact]
    public void The_meter_and_its_instruments_are_named_as_a_host_exports_them()
    {
        OxQLMetrics.MeterName.Should().Be("OxQL");
        OxQLMetrics.RequestsName.Should().Be("oxql.requests");
        OxQLMetrics.DurationName.Should().Be("oxql.request.duration");

        using var listener = new MeterListener();
        var published = new List<(string Name, string? Unit, string Kind)>();

        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == "OxQL")
                published.Add((instrument.Name, instrument.Unit, instrument.GetType().Name.Split('`')[0]));
        };
        listener.Start();

        published.Should().BeEquivalentTo(new[] { ("oxql.requests", "{request}", "Counter"), ("oxql.request.duration", "s", "Histogram") });
    }

    [Fact]
    public async Task A_query_is_counted_under_its_contract_route_entity_and_outcome_whichever_contract_it_was_written_for()
    {
        using var host = new SampleHost();
        using var listening = new Listening();

        (await host.Client().PostAsync(Query, SampleHost.Json(Customers))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.Client(contract: null).PostAsync(Query, SampleHost.Json("""{ "entityType": "probe.customer", "pipeline": [{ "page": { "limit": 1 } }] }"""))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.Client(contract: 1).PostAsync(Query, SampleHost.Json("""{ "entityType": "PROBE.customer", "pipeline": [] }"""))).StatusCode.Should().Be(HttpStatusCode.OK);

        ShouldHaveCounted(listening, "2", "query", "probe.customer", "ok");
        ShouldHaveCounted(listening, "1", "query", "probe.customer", "ok", atLeast: 2);
    }

    [Fact]
    public async Task A_refusal_is_counted_under_its_code_and_an_entity_the_model_does_not_have_under_one_name()
    {
        using var host = new SampleHost();
        using var listening = new Listening();

        await host.Client().PostAsync(Query, SampleHost.Json("""{ "entityType": "probe.customer", "pipeline": [{ "match": { "nothing": 1 } }] }"""));
        await host.Client().PostAsync(Query, SampleHost.Json("""{ "entityType": "whatever-a-caller-types-17", "pipeline": [] }"""));
        await host.Client().PostAsync(Query, SampleHost.Json("""{ "entityType": "whatever-a-caller-types-18", "pipeline": [] }"""));

        ShouldHaveCounted(listening, "2", "query", "probe.customer", Codes.UnknownPath);
        ShouldHaveCounted(listening, "2", "query", OxQLMetrics.UnknownEntity, Codes.UnknownEntity, atLeast: 2);
        listening.Seen.SelectMany(seen => seen.Tags.Values).Should().NotContain(value => value!.ToString()!.StartsWith("whatever", StringComparison.Ordinal), "what a caller types is never a tag");
    }

    [Fact]
    public async Task Each_entry_of_a_batch_is_a_query_of_the_batch_route_and_a_batch_refused_whole_is_counted_once()
    {
        using var host = new SampleHost();
        using var listening = new Listening();

        var answered = await host.Client().PostAsync(Batch, SampleHost.Json($$"""{ "queries": [{{Customers}}, {{Customers}}, null, { "entityType": "probe.customer", "pipeline": [{ "sort": {} }] }] }"""));

        answered.StatusCode.Should().Be(HttpStatusCode.OK, await answered.Content.ReadAsStringAsync());
        ShouldHaveCounted(listening, "2", "batch", "probe.customer", "ok", atLeast: 2);
        ShouldHaveCounted(listening, "2", "batch", OxQLMetrics.NoEntity, Codes.InvalidRequestBody, atLeast: 2);

        await host.Client().PostAsync(Batch, SampleHost.Json($$"""{ "queries": [{{string.Join(",", Enumerable.Repeat(Customers, 11))}}] }"""));

        ShouldHaveCounted(listening, "2", "batch", OxQLMetrics.NoEntity, Codes.BatchTooLarge);

        await host.Client(contract: null).PostAsync(Batch, SampleHost.Json($$"""{ "queries": [{{Customers}}] }"""));

        ShouldHaveCounted(listening, "1", "batch", "probe.customer", "ok");
    }

    [Fact]
    public async Task An_explain_and_a_body_that_does_not_read_are_counted_too()
    {
        using var host = new SampleHost();
        using var listening = new Listening();

        (await host.Client().PostAsync(Explain, SampleHost.Json(Customers))).StatusCode.Should().Be(HttpStatusCode.OK);
        await host.Client().PostAsync(Query, SampleHost.Json("not json"));
        await host.Client(contract: null).PostAsync(Query, SampleHost.Json("not json"));

        ShouldHaveCounted(listening, "2", "explain", "probe.customer", "ok");
        ShouldHaveCounted(listening, "2", "query", OxQLMetrics.NoEntity, Codes.InvalidRequestBody);
        ShouldHaveCounted(listening, "1", "query", OxQLMetrics.NoEntity, OxQLMetrics.Unbound);
    }

    [Fact]
    public void The_entity_tag_is_an_id_of_the_model_or_one_of_two_fixed_names()
    {
        OxQLMetrics.EntityOf(BindHost.Probe, "probe.order").Should().Be("probe.order");
        OxQLMetrics.EntityOf(BindHost.Probe, " Probe.Order ").Should().Be("probe.order", "a contract 1 caller's spelling is the entity it is read as");
        OxQLMetrics.EntityOf(BindHost.Probe, "probe.nowhere").Should().Be("(unknown)");
        OxQLMetrics.EntityOf(BindHost.Probe, null).Should().Be("(none)");
        OxQLMetrics.EntityOf(BindHost.Probe, "  ").Should().Be("(none)");
        OxQLMetrics.EntityOf(null, "probe.order").Should().Be("(unknown)");
        OxQLMetrics.OutcomeOf(null).Should().Be("ok");
        OxQLMetrics.OutcomeOf(Refusal.Timeout("late")).Should().Be(Codes.QueryTimeout);
    }

    // ---- the line of a contract 1 request -----------------------------------------------------------

    [Fact]
    public async Task The_line_of_a_contract_1_request_says_the_route_the_status_the_rows_and_the_time_with_the_databases_share()
    {
        using var host = new SampleHost();
        var first = Guid.NewGuid();

        host.Runner.PageRows = [SampleHost.Row(first, "A-1"), SampleHost.Row(Guid.NewGuid(), "A-2")];

        (await host.Client(contract: null).PostAsync(Query, SampleHost.Json("""{ "entityType": "probe.order", "pipeline": [{ "page": { "limit": 5 } }] }"""))).StatusCode.Should().Be(HttpStatusCode.OK);
        await host.Client(contract: null).PostAsync(Batch, SampleHost.Json("""{ "queries": [{ "entityType": "probe.order", "pipeline": [] }] }"""));
        await host.Client(contract: null).PostAsync(Query, SampleHost.Json("""{ "entityType": "probe.nowhere", "pipeline": [] }"""));

        var lines = host.Logs.Of(LegacyRoute.LogCategory).ToList();

        lines.Should().HaveCount(3);
        lines[0].Should().StartWith("OxQL.Compat.Legacy contract 1 request for probe.order answered by the legacy pipeline: reason=always refused=(null) user=");
        lines[0].Should().Contain(" org=" + BindHost.Organisation).And.Contain(" correlation=");
        lines[0].Should().Contain(" route=query status=200 rows=2 elapsedMs=").And.Contain(" dbMs=").And.EndWith(" dbCommands=1");
        lines[1].Should().Contain(" route=batch status=200 rows=2 ");
        lines[2].Should().Contain(" refused=UNKNOWN_TYPE ").And.Contain(" route=query status=400 rows=0 ").And.EndWith(" dbCommands=0");
    }
}

/// <summary>The tests that listen to the engine's meter: they run one after another and alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MeterCollection
{
    public const string Name = "Meter";
}
