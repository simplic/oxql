using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Mongo;
using OxQL.Mongo.Resolve;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The outcome matrix of OxQL 2.1 (DESIGN §3.6, §3.4.3) against fakes: per-row join outcomes
/// become <c>RESOLVE_MISSING</c> under <c>onMissing: "report"</c>, a 422 under <c>"refuse"</c> or
/// <c>strict</c>; <c>RESOLVE_AMBIGUOUS</c> and <c>RESOLVE_TRUNCATED</c> always travel and refuse
/// under strict, as do owner failures and <c>PAGE_INCOMPLETE</c>; <c>MaxReportedRows</c> caps the
/// listed rows; an inline resolve detects <c>not_found</c> from the page rows.
/// </summary>
public class OutcomePolicyTests
{
    private const string Invoice = ResolveModel.Invoice;

    private static readonly Guid InvoiceId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Customer1 = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Missing = Guid.Parse("c0000000-0000-0000-0000-0000000000ff");
    private static readonly Guid Tour1 = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid Shipment1 = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid Line1 = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid Contact1 = Guid.Parse("e0000000-0000-0000-0000-000000000001");

    /// <summary>Answers each aggregate with the rows of its entity, whatever the pipeline, and records the stages.</summary>
    private sealed class EntityRunner : IAggregateRunner
    {
        public Dictionary<string, List<BsonDocument>> Rows { get; } = new(StringComparer.Ordinal);

        public List<(EntityDef Entity, IReadOnlyList<BsonDocument> Stages)> Calls { get; } = [];

        public Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            Calls.Add((entity, stages));

            return Task.FromResult<IReadOnlyList<BsonDocument>>(Rows.TryGetValue(entity.Id, out var rows) ? rows.Select(row => row.DeepClone().AsBsonDocument).ToList() : []);
        }
    }

    private static (MongoQueryEngine Engine, EntityRunner Runner, FakeRemoteClient Client) Host(Action<OxQLOptions>? configure = null)
    {
        var runner = new EntityRunner();
        var client = new FakeRemoteClient();
        var options = BindHost.Options(configure);
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(ResolveModel.Model), runner, BindHost.Cursors, options, client, cache: new OwnerFetchCache(options));

        return (engine, runner, client);
    }

    private static BsonBinaryData Id(Guid id) => new(id, GuidRepresentation.Standard);

    private static BsonDocument InvoiceRow(Action<BsonDocument> fill)
    {
        var row = new BsonDocument { ["_id"] = Id(InvoiceId), ["OrganizationId"] = Id(BindHost.Organisation), ["Number"] = "RE-1" };

        fill(row);

        return row;
    }

    private static BsonDocument CustomerRow(Guid id, string name) =>
        new() { ["_id"] = Id(id), ["OrganizationId"] = Id(BindHost.Organisation), ["Name"] = name, ["Code"] = name.ToUpperInvariant() };

    private static BsonDocument ItemRow(Guid parent, string number, Guid line, string code, string display = "Number") => new()
    {
        ["_id"] = Id(parent),
        ["OrganizationId"] = Id(BindHost.Organisation),
        [display] = number,
        ["oxEl"] = new BsonDocument { ["_id"] = Id(line), ["Code"] = code, ["Amount"] = new BsonDecimal128(5m) },
    };

    private static Task<QueryOutcome> ExecuteAsync(MongoQueryEngine engine, string pipeline, bool strict = false) =>
        engine.ExecuteAsync(BindHost.Request(Invoice, pipeline) with { Strict = strict ? true : null }, BindHost.Context());

    private static async Task<QueryResult> SuccessAsync(MongoQueryEngine engine, string pipeline, bool strict = false)
    {
        var outcome = await ExecuteAsync(engine, pipeline, strict);

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return ((QueryOutcome.Success)outcome).Result;
    }

    private static async Task<Refusal> RefusedAsync(MongoQueryEngine engine, string pipeline, bool strict = false)
    {
        var outcome = await ExecuteAsync(engine, pipeline, strict);

        outcome.Should().BeOfType<QueryOutcome.Refused>();

        var refusal = ((QueryOutcome.Refused)outcome).Refusal;

        refusal.Status.Should().Be(422);
        refusal.Type.Should().Be("not_executable");

        return refusal;
    }

    /// <summary>The params as the wire writes them.</summary>
    private static JsonNode Wire(IReadOnlyDictionary<string, object?>? parameters) =>
        JsonNode.Parse(JsonSerializer.Serialize(parameters, OxQLJson.Wire))!;

    private static Diagnostic Single(QueryResult result, string code) =>
        (result.Diagnostics ?? []).Should().ContainSingle(diagnostic => diagnostic.Code == code).Subject;

    private const string AllCustomers = """{ "resolve": { "path": "customerIds", "as": "customers", "elements": "all"{0} } }""";

    private static string All(string onMissing = "") => "[" + AllCustomers.Replace("{0}", onMissing.Length == 0 ? "" : $", \"onMissing\": \"{onMissing}\"") + "]";

    // ---- onMissing ------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_reference_under_the_default_onMissing_is_null_and_reported_nowhere()
    {
        var (engine, runner, _) = Host();
        runner.Rows[Invoice] = [InvoiceRow(row => row["CustomerIds"] = new BsonArray { Id(Customer1), Id(Missing) })];
        runner.Rows["rc.customer"] = [CustomerRow(Customer1, "Alice")];

        var result = await SuccessAsync(engine, All());

        result.Items[0]!["customers"]!.AsArray().Should().HaveCount(1);
        result.Diagnostics.Should().BeNull("onMissing null reports only owner failures");
    }

    [Fact]
    public async Task OnMissing_report_adds_one_RESOLVE_MISSING_per_stage_listing_row_element_key_and_outcome()
    {
        var (engine, runner, _) = Host();
        runner.Rows[Invoice] = [InvoiceRow(row => row["CustomerIds"] = new BsonArray { Id(Customer1), Id(Missing) })];
        runner.Rows["rc.customer"] = [CustomerRow(Customer1, "Alice")];

        var diagnostic = Single(await SuccessAsync(engine, All("report")), Codes.ResolveMissing);

        diagnostic.Stage.Should().Be(0);
        diagnostic.Path.Should().Be("customerIds");
        Wire(diagnostic.Params).ToJsonString().Should().Be(
            $$"""{"alias":"customers","count":1,"truncated":false,"rows":[{"row":0,"element":1,"key":"{{Missing:D}}","outcome":"not_found"}]}""");
    }

    [Fact]
    public async Task OnMissing_refuse_turns_the_missing_rows_into_a_422_carrying_the_diagnostic_with_its_params()
    {
        var (engine, runner, _) = Host();
        runner.Rows[Invoice] = [InvoiceRow(row => row["CustomerIds"] = new BsonArray { Id(Missing) })];

        var refusal = await RefusedAsync(engine, All("refuse"));

        var error = refusal.Errors.Should().ContainSingle().Subject;

        error.Code.Should().Be(Codes.ResolveMissing);
        error.Stage.Should().Be(0);
        error.Path.Should().Be("customerIds");

        var body = JsonNode.Parse(JsonSerializer.Serialize(refusal, OxQLJson.Wire))!;

        body["type"]!.GetValue<string>().Should().Be("not_executable");
        body["errors"]![0]!["params"]!["rows"]![0]!["outcome"]!.GetValue<string>().Should().Be("not_found");
        body["errors"]![0]!["params"]!["rows"]![0]!["row"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Strict_makes_refuse_the_default_and_an_explicit_report_keeps_the_rows()
    {
        var (engine, runner, _) = Host();
        runner.Rows[Invoice] = [InvoiceRow(row => row["CustomerIds"] = new BsonArray { Id(Missing) })];

        (await RefusedAsync(engine, All(), strict: true)).Errors!.Select(error => error.Code).Should().Equal(Codes.ResolveMissing);

        var reported = await SuccessAsync(engine, All("report"), strict: true);

        Single(reported, Codes.ResolveMissing).Params!["count"].Should().Be(1);
    }

    [Fact]
    public async Task A_key_that_does_not_convert_is_missing_as_invalid_key_and_a_null_reference_is_not()
    {
        var (engine, runner, _) = Host();
        runner.Rows[Invoice] =
        [
            InvoiceRow(row => row["ShipmentKey"] = "SHIP-12"),
            InvoiceRow(row => row["ShipmentKey"] = BsonNull.Value),
        ];

        var diagnostic = Single(await SuccessAsync(engine, """[{ "resolve": { "path": "shipmentKey", "as": "shipment", "onMissing": "report" } }]"""), Codes.ResolveMissing);

        Wire(diagnostic.Params)["rows"]!.ToJsonString().Should().Be("""[{"row":0,"key":"SHIP-12","outcome":"invalid_key"}]""");
    }

    [Fact]
    public async Task MaxReportedRows_caps_the_listed_rows_and_count_keeps_every_one()
    {
        var (engine, runner, _) = Host(configure => configure.Limits.MaxReportedRows = 2);
        runner.Rows[Invoice] = Enumerable.Range(0, 3).Select(_ => InvoiceRow(row => row["CustomerIds"] = new BsonArray { Id(Missing) })).ToList();

        var parameters = Wire(Single(await SuccessAsync(engine, All("report")), Codes.ResolveMissing).Params);

        parameters["count"]!.GetValue<int>().Should().Be(3);
        parameters["truncated"]!.GetValue<bool>().Should().BeTrue();
        parameters["rows"]!.AsArray().Select(row => row!["row"]!.GetValue<int>()).Should().Equal(0, 1);
    }

    // ---- owner failures -------------------------------------------------------------------------

    [Fact]
    public async Task An_owner_that_does_not_answer_is_owner_unanswered_under_report_and_refuses_under_strict()
    {
        var (engine, runner, client) = Host();
        runner.Rows[Invoice] = [InvoiceRow(row => row["ContactId"] = Id(Contact1))];
        client.Unreachable.Add("crm");

        var plain = await SuccessAsync(engine, """[{ "resolve": { "path": "contactId", "as": "contact" } }]""");

        plain.Diagnostics!.Select(diagnostic => diagnostic.Code).Should().Equal(Codes.ResolveUnreachable);

        var reported = await SuccessAsync(engine, """[{ "resolve": { "path": "contactId", "as": "contact", "onMissing": "report" } }]""");

        reported.Diagnostics!.Select(diagnostic => diagnostic.Code).Should().Equal(Codes.ResolveUnreachable, Codes.ResolveMissing);
        Wire(Single(reported, Codes.ResolveMissing).Params)["rows"]![0]!["outcome"]!.GetValue<string>().Should().Be("owner_unanswered");

        var strict = await RefusedAsync(engine, """[{ "resolve": { "path": "contactId", "as": "contact", "onMissing": "report" } }]""", strict: true);

        strict.Errors!.Select(error => error.Code).Should().Equal([Codes.ResolveUnreachable], "strict refuses on an owner failure whatever onMissing says");
    }

    // ---- ambiguity and truncation -----------------------------------------------------------------

    [Fact]
    public async Task An_ambiguous_key_is_reported_always_and_refused_under_strict()
    {
        var (engine, runner, _) = Host();
        runner.Rows[Invoice] = [InvoiceRow(row => row["LocalSource"] = new BsonDocument { ["Type"] = "logistics", ["_id"] = Id(Line1) })];
        runner.Rows["rc.shipment"] = [ItemRow(Shipment1, "SN-1", Line1, "S1")];
        runner.Rows["rc.tour"] = [ItemRow(Tour1, "Tour 1", Line1, "T1", display: "Name")];
        const string pipeline = """[{ "resolve": { "path": "localSource.id", "as": "line", "select": ["code"] } }]""";

        var result = await SuccessAsync(engine, pipeline);

        result.Items[0]!["line"]!["code"]!.GetValue<string>().Should().Be("S1");

        var diagnostic = Single(result, Codes.ResolveAmbiguous);

        diagnostic.Path.Should().Be("localSource.id");
        Wire(diagnostic.Params).ToJsonString().Should().Be(
            $$"""{"alias":"line","count":1,"truncated":false,"rows":[{"row":0,"key":"{{Line1:D}}","outcome":"ambiguous"}]}""");

        (await RefusedAsync(engine, pipeline, strict: true)).Errors!.Select(error => error.Code).Should().Equal(Codes.ResolveAmbiguous);
    }

    [Fact]
    public async Task OnMissing_refuse_does_not_refuse_an_ambiguous_key_outside_strict()
    {
        var (engine, runner, _) = Host();
        runner.Rows[Invoice] = [InvoiceRow(row => row["LocalSource"] = new BsonDocument { ["Type"] = "logistics", ["_id"] = Id(Line1) })];
        runner.Rows["rc.shipment"] = [ItemRow(Shipment1, "SN-1", Line1, "S1")];
        runner.Rows["rc.tour"] = [ItemRow(Tour1, "Tour 1", Line1, "T1", display: "Name")];

        var result = await SuccessAsync(engine, """[{ "resolve": { "path": "localSource.id", "as": "line", "onMissing": "refuse" } }]""");

        result.Diagnostics!.Select(diagnostic => diagnostic.Code).Should().Equal(Codes.ResolveAmbiguous);
    }

    [Fact]
    public async Task Elements_all_over_MaxLookupLimit_is_RESOLVE_TRUNCATED_and_refused_under_strict()
    {
        var (engine, runner, _) = Host(configure => configure.Limits.MaxLookupLimit = 1);
        runner.Rows[Invoice] = [InvoiceRow(row => row["CustomerIds"] = new BsonArray { Id(Customer1), Id(Customer1) })];
        runner.Rows["rc.customer"] = [CustomerRow(Customer1, "Alice")];

        var diagnostic = Single(await SuccessAsync(engine, All()), Codes.ResolveTruncated);

        Wire(diagnostic.Params).ToJsonString().Should().Be("""{"alias":"customers","limit":1,"rows":1}""");
        (await RefusedAsync(engine, All("report"), strict: true)).Errors!.Select(error => error.Code).Should().Equal(Codes.ResolveTruncated);
    }

    // ---- inline resolves ----------------------------------------------------------------------------

    [Fact]
    public async Task An_inline_resolve_reads_its_missing_rows_off_the_page()
    {
        var (engine, runner, _) = Host();
        runner.Rows[Invoice] =
        [
            InvoiceRow(row => { row["CustomerId"] = Id(Customer1); row["customer"] = new BsonDocument { ["_id"] = Id(Customer1), ["Name"] = "Alice" }; }),
            InvoiceRow(row => row["CustomerId"] = Id(Missing)),
            InvoiceRow(row => { row["CustomerId"] = Id(Missing); row["customer"] = BsonNull.Value; }),
            InvoiceRow(_ => { }),
        ];
        const string pipeline = """[{ "resolve": { "path": "customerId", "as": "customer", "onMissing": "report" } }]""";

        var diagnostic = Single(await SuccessAsync(engine, pipeline), Codes.ResolveMissing);

        Wire(diagnostic.Params).ToJsonString().Should().Be(
            $$"""{"alias":"customer","count":2,"truncated":false,"rows":[{"row":1,"key":"{{Missing:D}}","outcome":"not_found"},{"row":2,"key":"{{Missing:D}}","outcome":"not_found"}]}""");
        runner.Calls.Should().ContainSingle("an inline resolve joins in the aggregate, no owner query");

        (await RefusedAsync(engine, """[{ "resolve": { "path": "customerId", "as": "customer" } }]""", strict: true))
            .Errors!.Select(error => error.Code).Should().Equal(Codes.ResolveMissing);
    }

    [Fact]
    public async Task An_inline_resolve_that_reports_keeps_its_reference_against_a_projection_and_one_that_does_not_is_compiled_as_before()
    {
        var reporting = MongoCompiler.Compile(await BindHost.BoundAsync(ResolveModel.Model, Invoice,
            """[{ "resolve": { "path": "customerId", "as": "customer", "onMissing": "report" } }, { "project": { "number": 1, "customer.name": 1 } }]"""), new CompileOptions(5_000, null, 10_000));
        var plain = MongoCompiler.Compile(await BindHost.BoundAsync(ResolveModel.Model, Invoice,
            """[{ "resolve": { "path": "customerId", "as": "customer" } }, { "project": { "number": 1, "customer.name": 1 } }]"""), new CompileOptions(5_000, null, 10_000));

        reporting.InlineProbes.Should().ContainSingle().Which.ReferenceStorage.Should().Be("CustomerId");
        reporting.PageStages.Single(stage => stage.Contains("$project"))["$project"].AsBsonDocument.Contains("CustomerId").Should().BeTrue();
        plain.InlineProbes.Should().BeEmpty();
        plain.PageStages.Single(stage => stage.Contains("$project"))["$project"].AsBsonDocument.Contains("CustomerId").Should().BeFalse();
    }

    [Fact]
    public async Task The_kept_reference_of_an_inline_probe_does_not_reach_the_wire_row()
    {
        var (engine, runner, _) = Host();
        runner.Rows[Invoice] = [new BsonDocument { ["_id"] = Id(InvoiceId), ["Number"] = "RE-1", ["CustomerId"] = Id(Missing) }];

        var result = await SuccessAsync(engine, """[{ "resolve": { "path": "customerId", "as": "customer", "onMissing": "report" } }, { "project": { "number": 1, "customer.name": 1 } }]""");

        result.Items[0]!.AsObject().Select(member => member.Key).Should().NotContain("customerId");
        Single(result, Codes.ResolveMissing).Params!["count"].Should().Be(1);
    }

    // ---- the page -------------------------------------------------------------------------------

    [Fact]
    public async Task A_strict_page_without_cursor_or_offset_that_holds_fewer_rows_than_match_is_PAGE_INCOMPLETE()
    {
        var (engine, runner, _) = Host();
        runner.Rows[Invoice] = [InvoiceRow(_ => { }), InvoiceRow(_ => { })];

        var refusal = await RefusedAsync(engine, """[{ "page": { "limit": 1 } }]""", strict: true);

        var error = refusal.Errors.Should().ContainSingle().Subject;

        error.Code.Should().Be(Codes.PageIncomplete);
        Wire(error.Params).ToJsonString().Should().Be("""{"limit":1,"max":5000}""");

        (await SuccessAsync(engine, """[{ "page": { "limit": 1 } }]""")).PageInfo.HasNextPage.Should().BeTrue("outside strict a page is a page");
        (await SuccessAsync(engine, """[{ "page": { "limit": 1, "offset": 0 } }]""", strict: true)).PageInfo.HasNextPage.Should().BeTrue("a request that pages is not a report");
        (await SuccessAsync(engine, """[{ "page": { "limit": 2 } }]""", strict: true)).Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_page_level_loss_refuses_before_any_owner_is_asked()
    {
        var (engine, runner, client) = Host();
        runner.Rows[Invoice] = [InvoiceRow(row => row["ContactId"] = Id(Contact1)), InvoiceRow(row => row["ContactId"] = Id(Contact1))];

        var refusal = await RefusedAsync(engine, """[{ "resolve": { "path": "contactId", "as": "contact" } }, { "page": { "limit": 1 } }]""", strict: true);

        refusal.Errors!.Select(error => error.Code).Should().Equal(Codes.PageIncomplete);
        client.Calls.Should().BeEmpty();
    }
}
