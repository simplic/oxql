using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Suites.Explain;
using OxQL.IntegrationTests.Suites.Report;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Bind.Fixtures.Resolve;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// Types by reference: the default explain answer names its types (entity, service, the revision of
/// the service's schema document) and says per root and stage where the members stand (a rule); it
/// writes no member out. A client that holds the schema documents rebuilds from the two everything
/// the answer with <c>include: "types"</c> writes out (<see cref="Reconstruction"/>), which these tests
/// prove over the resolve graph for every way a stage changes where a member stands: an unwound
/// collection, a lookup's array, rows joined after the page from local targets and from an owner, an
/// alias holding many, a projection, a collection an unwind took out, a group, a contract 1 select.
/// </summary>
public class ExplainByReferenceTests
{
    private const string Invoice = ResolveModel.Invoice;
    private const string Customer = "rc.customer";

    private static readonly Lazy<SchemaTypes> Schema = new(() => new SchemaTypes(
    [
        SchemaDocumentWriter.Of(ResolveModel.Model, "rc"),
        SchemaDocumentWriter.Of(OwnerFleet.Crm, "crm"),
        SchemaDocumentWriter.Of(OwnerFleet.Transport, "transport"),
    ]));

    /// <summary>The cases: an entity, a pipeline and the contract it is read as.</summary>
    public static TheoryData<string, string, string, int> Cases => new()
    {
        { "entry", Invoice, "[]", 2 },
        { "joins", Invoice, """
            [{ "unwind": { "path": "lines", "as": "line" } },
             { "resolve": { "path": "customerId", "as": "c" } },
             { "resolve": { "path": "billingLineId", "as": "bl", "parentAs": "sh" } },
             { "resolve": { "path": "contactId", "as": "ct", "select": ["name"] } }]
            """, 2 },
        { "lookup", Customer, """[{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices" } }]""", 2 },
        { "lookup unwound", Customer, """
            [{ "lookup": { "from": "rc.invoice", "path": "customerId", "as": "invoices" } },
             { "unwind": { "path": "invoices", "as": "invoice" } },
             { "unwind": { "path": "invoice.lines", "as": "line" } }]
            """, 2 },
        { "many", Invoice, """[{ "resolve": { "path": "customerIds", "as": "cs", "elements": "all" } }]""", 2 },
        { "first and a lookup on it", Invoice, """
            [{ "resolve": { "path": "customerIds", "as": "r", "elements": "first" } },
             { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "r", "first": true, "as": "invoice" } }]
            """, 2 },
        { "union of local and remote items", Invoice, """
            [{ "resolve": { "path": "source.id", "as": "line", "parentAs": "owner" } },
             { "lookup": { "from": "rc.invoice", "path": "shipmentKey", "on": "owner", "forTarget": "rc.shipment", "as": "invoices" } }]
            """, 2 },
        { "union of local items and an entity", Invoice, """[{ "resolve": { "path": "localSource.id", "as": "ls", "parentAs": "lo" } }]""", 2 },
        { "converted keys", Invoice, """[{ "resolve": { "path": "billing.referenceId", "as": "b" } }, { "resolve": { "path": "shipmentKey", "as": "s" } }]""", 2 },
        { "a variant's reference", Invoice, """[{ "resolve": { "path": "slot.holderId", "as": "holder" } }]""", 2 },
        { "nested unwinds without the path", Invoice, """
            [{ "unwind": { "path": "lines", "as": "line", "keepPath": false, "includeIndex": "at" } },
             { "unwind": { "path": "line.parts", "as": "part" } },
             { "resolve": { "path": "part.customerId", "as": "pc" } }]
            """, 2 },
        { "nested unwinds with the path", Invoice, """
            [{ "unwind": { "path": "lines" } },
             { "unwind": { "path": "lines.parts" } }]
            """, 2 },
        { "an inclusion", Invoice, """
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } },
             { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "c", "as": "others", "select": ["number"] } },
             { "unwind": { "path": "lines", "as": "line", "includeIndex": "at" } },
             { "project": { "number": 1, "customerCode": 1, "slot": 1, "c": 1, "others": 1, "line.id": 1, "at": 1 } },
             { "sort": [{ "number": "asc" }] }]
            """, 2 },
        { "an exclusion", Invoice, """
            [{ "resolve": { "path": "contactId", "as": "ct" } },
             { "project": { "lines": 0, "slot.name": 0, "customerIds": 0 } }]
            """, 2 },
        { "a group", Invoice, """
            [{ "unwind": { "path": "lines", "as": "line" } },
             { "group": { "by": [{ "path": "number", "as": "n" }], "fields": { "count": { "count": true }, "ids": { "push": "line.id" } } } },
             { "sort": [{ "n": "asc" }] }]
            """, 2 },
        { "contract 1 selects", Invoice, """
            [{ "resolve": { "path": "customerId", "as": "c", "select": ["name"] } },
             { "lookup": { "from": "rc.invoice", "path": "customerId", "on": "c", "as": "others", "select": ["number"] } }]
            """, 1 },
        { "does not bind", Invoice, """
            [{ "unwind": { "path": "lines", "as": "line" } },
             { "resolve": { "path": "contactId", "as": "ct" } },
             { "match": { "nope": { "eq": 1 } } },
             { "resolve": { "path": "line.customerId", "as": "c" } }]
            """, 2 },
    };

    private static async Task<JsonObject> ExplainAsync(string entity, string pipeline, int contract, string envelope, string? revision = null)
    {
        var client = OwnerFleet.Client();
        var engine = new MongoQueryEngine(new Revisioned(ResolveModel.Model, revision), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), client);
        var body = $$"""{ "query": { "entityType": "{{entity}}", "pipeline": {{pipeline}} }{{envelope}} }""";
        var outcome = await engine.ExplainAsync(ExplainAnswer.Plain(body), BindHost.Context(contract: contract));

        return JsonSerializer.SerializeToNode(outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result, OxQLJson.Wire)!.AsObject();
    }

    private sealed class Revisioned(OxQL.Model.EntityModel model, string? revision) : IEntityModelProvider
    {
        public OxQL.Model.EntityModel Model { get; } = model;

        public string? SchemaRevision { get; } = revision;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_schema_documents_and_the_default_answer_are_the_answer_with_the_types_written_out(string name, string entity, string pipeline, int contract)
    {
        var byReference = await ExplainAsync(entity, pipeline, contract, "");
        var tabled = await ExplainAsync(entity, pipeline, contract, """, "include": ["shape", "notes", "types"], "shape": { "depth": 3 } """);

        Reconstruction.Assert(Schema.Value, byReference, tabled, 3, published: false).Should().BeGreaterThan(0, name);
    }

    [Fact]
    public async Task The_cases_cover_every_member_of_a_rule_and_both_ways_a_path_leaves_the_row()
    {
        var rules = new HashSet<string>(StringComparer.Ordinal);
        var shapes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in Cases)
        {
            var answer = await ExplainAsync((string)row[1], (string)row[2], (int)row[3], "");

            foreach (var (_, rule) in answer["rules"]!.AsObject())
                foreach (var (member, value) in rule!.AsObject())
                    rules.Add(member == "under" ? "under:" + value!.GetValue<string>() : member);

            foreach (var (_, shape) in Reconstruction.Shapes(answer))
                foreach (var member in (string[])["projection", "removed"])
                    if (shape[member] is not null)
                        shapes.Add(member);
        }

        rules.Should().BeEquivalentTo(["self", "under:collection", "under:afterPage", "under:owner", "filter", "many", "unwound", "shows"]);
        shapes.Should().BeEquivalentTo(["projection", "removed"]);
    }

    [Fact]
    public async Task The_default_answer_names_each_type_by_entity_service_and_revision_and_writes_no_member()
    {
        var answer = await ExplainAsync(Invoice, """
            [{ "unwind": { "path": "lines", "as": "line" } },
             { "resolve": { "path": "source.id", "as": "src", "parentAs": "owner" } },
             { "resolve": { "path": "contactId", "as": "ct" } }]
            """, 2, "", "sha256:rc");

        answer.ContainsKey("flagSets").Should().BeFalse();
        answer["types"]!["t:rc.invoice"]!.ToJsonString().Should().Be("""{"entity":"rc.invoice","service":"rc","schemaRevision":"sha256:rc"}""");
        answer["types"]!["t:rc.invoice#lines"]!.ToJsonString().Should().Be("""{"entity":"rc.invoice","item":"lines","service":"rc","schemaRevision":"sha256:rc"}""");
        answer["types"]!["t:crm.contact"]!.ToJsonString().Should().Be("""{"entity":"crm.contact","service":"crm"}""", "its owner names it, and publishes no revision here");
        answer["types"]!["u:src"]!.ToJsonString().Should().Be("""{"of":["t:rc.shipment#billingLines","t:rc.tour#billingLines","t:transport.shipment#billingLines"]}""");
        answer["revision"]!["schema"]!.ToJsonString().Should().Be("""{"crm":null,"rc":"sha256:rc","transport":null}""", "this host's document and each owner's that answered");

        // The entity row once its lines are unwound, the element, and an owner's rows a condition may narrow.
        var shape = answer["stages"]![2]!["shape"]!;

        answer["rules"]![shape["rules"]![""]!.GetValue<string>()]!.ToJsonString().Should().Be("""{"unwound":["lines"]}""");
        answer["rules"]![shape["rules"]!["ct"]!.GetValue<string>()]!.AsObject().Select(pair => pair.Key).Should().Equal("self", "under", "filter");
        answer["entry"]!["shape"]!["rules"]!.AsObject().Count.Should().Be(0, "at the entry every member has its own flags");
        shape.AsObject().ContainsKey("flags").Should().BeFalse("the per-member overrides are written with the types only");
    }

    [Fact]
    public async Task The_depth_and_the_member_cap_do_not_change_the_default_answer()
    {
        var plain = await ExplainAsync(Invoice, "[]", 2, "");
        var deep = await ExplainAsync(Invoice, "[]", 2, """, "shape": { "depth": 3 } """);

        deep["types"]!.ToJsonString().Should().Be(plain["types"]!.ToJsonString());
        deep["rules"]!.ToJsonString().Should().Be(plain["rules"]!.ToJsonString());
    }

    [Fact]
    public async Task A_catalog_entry_answers_the_type_by_reference_and_checks_the_prefix_without_listing_members()
    {
        var answer = await ExplainAsync(Customer, "[]", 2, """
            , "catalog": [{ "id": "a", "entity": "rc.invoice", "prefix": "lines" }, { "id": "b", "entity": "crm.contact" }, { "id": "c", "entity": "rc.invoice", "prefix": "nope" }]
            """);

        answer["catalog"]![0]!.ToJsonString().Should().Be("""{"id":"a","entity":"rc.invoice","prefix":"lines","type":"t:rc.invoice","forwarded":false}""");
        answer["catalog"]![1]!["type"]!.GetValue<string>().Should().Be("t:crm.contact");
        answer["catalog"]![1]!["forwarded"]!.GetValue<bool>().Should().BeTrue();
        answer["catalog"]![2]!["error"]!["code"]!.GetValue<string>().Should().Be("UNKNOWN_PATH");
        answer["types"]!["t:crm.contact"]!.ToJsonString().Should().Be("""{"entity":"crm.contact","service":"crm"}""");

        var tabled = await ExplainAsync(Customer, "[]", 2, """, "include": ["types"], "catalog": [{ "id": "a", "entity": "rc.invoice", "prefix": "lines" }] """);

        tabled["catalog"]![0]!["members"]!.AsArray().Should().NotBeEmpty("with the types written out a prefix lists the members below it");
    }
}
