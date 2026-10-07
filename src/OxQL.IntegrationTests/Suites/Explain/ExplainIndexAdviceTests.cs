using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Explain;

/// <summary>
/// The index advisory on the fleet's hosts, over the wire (DG2): the requests a grid sends (its rows
/// with the filter entries and the sort, the list of values of a column, a level of groups, rows with
/// a join) each get the definition of the index that serves them, the part of the request it serves
/// by the caller's own paths and filter entry ids, and one <c>INDEX_ADVICE</c> note per index with
/// <c>basis: "advisory"</c>. The hosts read their index lists, so nothing is <c>unknown</c>.
/// </summary>
[Trait("Category", "Integration")]
public class ExplainIndexAdviceTests
{
    private const string GridFilter = """
        [{ "id": "removeDeleted", "where": { "isDeleted": { "neq": true } } },
         { "id": "col:status", "where": { "status.name": { "in": ["Open", "Planned"] } } },
         { "id": "col:loadStart", "where": { "loadStart": { "gte": "2026-06-01T00:00:00Z" } } }]
        """;

    private static async Task<JsonObject> ExplainAsync(LabService service, string entity, string pipeline, string filter = "[]")
    {
        var client = await Lab.ClientAsync(service);
        var answer = await client.ExplainHereAsync(JsonNode.Parse($$"""{ "query": { "entityType": "{{entity}}", "filter": {{filter}}, "pipeline": {{pipeline}} }, "include": ["indexes"] }""")!);

        answer.StatusCode.Should().Be(200, answer.Text);
        answer.Body!["valid"]!.GetValue<bool>().Should().BeTrue(answer.Text);

        var body = answer.Body!.AsObject();
        var indexes = body["advisory"]!["indexes"]!.AsArray();
        var notes = body["notes"]!.AsArray().Select(note => note!.AsObject()).Where(note => note["code"]!.GetValue<string>() == "INDEX_ADVICE").ToList();

        body["advisory"]!["existing"]!.GetValue<string>().Should().Be("known", "the host reads its index lists");
        notes.Should().HaveCount(indexes.Count, "one note per advised index").And.OnlyContain(note => note["basis"]!.GetValue<string>() == "advisory");
        notes.Select(note => note["params"]!["id"]!.GetValue<string>()).Should().BeEquivalentTo(indexes.Select(index => index!["id"]!.GetValue<string>()), "notes stand in stage order, request-wide ones last");
        body["executable"]!.GetValue<string>().Should().Be("yes", "advice never keeps a request from running");

        return body;
    }

    private static JsonObject Of(JsonObject body, string purpose) =>
        body["advisory"]!["indexes"]!.AsArray().Select(index => index!.AsObject()).Single(index => index["for"]!.GetValue<string>() == purpose);

    [Fact]
    public async Task The_rows_of_a_grid_get_one_index_in_the_order_equality_sort_range_with_the_collation_its_texts_need()
    {
        var body = await ExplainAsync(LabService.Transport, Corpus.Shipment,
            """[{ "project": { "shipmentNumber": 1, "status.name": 1, "loadStart": 1 } }, { "sort": [{ "shipmentNumber": "desc" }] }, { "page": { "limit": 50 } }]""", GridFilter);
        var index = Of(body, "rows");

        index["collection"]!.GetValue<string>().Should().Be(LabService.Transport.Model.Entities[Corpus.Shipment].Collection);
        index["key"]!.ToJsonString().Should().Be("""{"OrganizationId":1,"Status.Name":1,"ShipmentNumber":-1,"_id":1,"LoadStart":1}""");
        index["collation"]!.ToJsonString().Should().Be("""{"locale":"de","strength":1}""");
        index["command"]!.GetValue<string>().Should().StartWith("db.getCollection(").And.Contain("createIndex(").And.Contain("collation:");
        index["serves"]!.AsArray().Select(serve => serve!["filter"]?.GetValue<string>() ?? serve["use"]!.GetValue<string>()).Should().Equal("col:status", "sort", "col:loadStart");
        index["existing"]!.GetValue<string>().Should().NotBe("unknown").And.NotBe("served", "the fleet declares no such index");
        index["used"]!.GetValue<bool>().Should().BeFalse();

        body["advisory"]!["unbounded"]!.AsArray().Select(each => each!.AsObject()).Should().ContainSingle(each => each["filter"]!.GetValue<string>() == "removeDeleted")
            .Which["reason"]!.GetValue<string>().Should().Be("negation");

        var note = body["notes"]!.AsArray().Select(each => each!.AsObject()).Single(each => each["code"]!.GetValue<string>() == "INDEX_ADVICE");

        note["filter"]!.GetValue<string>().Should().Be("col:status", "the note stands at the filter entry the index serves first");
        note["path"]!.GetValue<string>().Should().Be("status.name");
    }

    [Fact]
    public async Task The_list_of_values_of_a_column_and_a_level_of_groups_get_an_index_of_their_own()
    {
        var values = Of(await ExplainAsync(LabService.Transport, Corpus.Shipment,
            """[{ "values": { "path": "customer.address.companyName", "counts": true } }]""", GridFilter), "values");

        values["key"]!.ToJsonString().Should().Be("""{"OrganizationId":1,"LoadStart":1,"Customer.Address.CompanyName":1,"IsDeleted":1,"Status.Name":1}""");
        values.ContainsKey("collation").Should().BeFalse();
        values["covers"]!.GetValue<bool>().Should().BeTrue();
        values["serves"]!.AsArray().Select(serve => serve!["use"]!.GetValue<string>()).Should().Contain("values");

        var group = Of(await ExplainAsync(LabService.Transport, Corpus.Shipment,
            """[{ "group": { "by": [{ "path": "status.name", "as": "statusName" }], "fields": { "count": { "count": true } } } }, { "sort": [{ "statusName": "asc" }] }, { "page": { "limit": 100 } }]""",
            """[{ "id": "removeDeleted", "where": { "isDeleted": { "neq": true } } }, { "id": "col:loadStart", "where": { "loadStart": { "gte": "2026-06-01T00:00:00Z" } } }]"""), "group");

        group["key"]!.ToJsonString().Should().Be("""{"OrganizationId":1,"LoadStart":1,"Status.Name":1,"IsDeleted":1}""");
        group["covers"]!.GetValue<bool>().Should().BeTrue();
        group["serves"]!.AsArray().Select(serve => serve!["use"]!.GetValue<string>()).Should().Equal("filter", "group", "filter");
    }

    [Fact]
    public async Task A_join_in_the_aggregate_gets_an_index_of_the_joined_collection_named_by_its_stage()
    {
        var body = await ExplainAsync(LabService.Fleet, Corpus.Vehicle,
            """[{ "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "equipment" } }, { "match": { "equipment.name": { "exists": true } } }, { "page": { "limit": 10 } }]""");
        var join = Of(body, "join");

        join["collection"]!.GetValue<string>().Should().Be(LabService.Fleet.Model.Entities[Corpus.Equipment].Collection);
        join["keys"]!.AsArray().Select(key => key!["role"]!.GetValue<string>()).Should().Equal("scope", "join");
        join["serves"]!.ToJsonString().Should().Be("""[{"use":"join","alias":"equipment","path":"vehicle.id","stage":0}]""");
        join["existing"]!.GetValue<string>().Should().NotBe("unknown", "the joined collection's index list is read as well");

        var note = body["notes"]!.AsArray().Select(each => each!.AsObject()).Single(each => each["code"]!.GetValue<string>() == "INDEX_ADVICE" && each["params"]!["for"]!.GetValue<string>() == "join");

        note["stage"]!.GetValue<int>().Should().Be(0);
    }
}
