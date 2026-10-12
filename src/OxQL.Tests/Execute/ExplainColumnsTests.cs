using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Model.Build;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using OxQL.Tests.Model.Fixtures;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The result columns of a member that lies below an object: a projection that names members
/// of an object makes each of them a column of its own, at any depth, with what that member can; an
/// object the projection names, one no projection narrows and one an exclusion takes members out
/// of stay one column. On the vendored logistics document, whose shipment holds a customer with an
/// address.
/// </summary>
public class ExplainColumnsTests
{
    private const string Shipment = "logistics.shipment";

    private static readonly Lazy<EntityModel> Logistics = new(() => DocumentModelBuilder.Build(ProbeModel.ReadFixture("logistics.json")));

    private static async Task<ExplainResult> ExplainAsync(string pipeline)
    {
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(Logistics.Value), new FakeAggregateRunner(), BindHost.Cursors, BindHost.Options(), new FakeRemoteClient());
        var outcome = await engine.ExplainAsync(BindHost.Parse($$"""{ "entityType": "{{Shipment}}", "pipeline": {{pipeline}} }"""), BindHost.Context());
        var answer = outcome.Should().BeOfType<ExplainOutcome.Success>().Subject.Result;

        answer.Valid.Should().BeTrue(string.Join("; ", answer.Errors.Select(error => error.Code + " " + error.Message)));

        return answer;
    }

    private static ExplainColumn Column(ExplainResult answer, string path) => answer.Result!.Columns.Single(column => column.Path == path);

    private static JsonObject CanOf(ExplainResult answer, string path) => answer.Result!.Capabilities![Column(answer, path).Can!]!.AsObject();

    private static List<string> Paths(ExplainResult answer) => answer.Result!.Columns.Select(column => column.Path).ToList();

    [Fact]
    public async Task A_projected_member_two_objects_down_is_a_column_with_what_the_member_can()
    {
        const string pipeline = """[{ "project": { "customer.address.companyName": 1, "shipmentNumber": 1, "customer.address.city": 1, "status.name": 1 } }]""";
        var answer = await ExplainAsync(pipeline);

        Paths(answer).Should().BeEquivalentTo(["id", "shipmentNumber", "status.name", "customer.address.companyName", "customer.address.city"]);
        Paths(answer).Should().NotContain("customer.address").And.NotContain("customer");

        // In the order the row writes its members, not the order the projection names them.
        var type = Logistics.Value.Entities[Shipment].Root;
        var address = type.Member("customer")!.Type!.Member("address")!.Type!;
        var written = address.Members.Select(member => member.WireName).ToList();

        Paths(answer).IndexOf("customer.address.companyName").CompareTo(Paths(answer).IndexOf("customer.address.city"))
            .Should().Be(written.IndexOf("companyName").CompareTo(written.IndexOf("city")));

        var leaf = Column(answer, "customer.address.companyName");

        leaf.Kind.Should().Be("string");
        leaf.Root.Should().Be("customer", "the root is the object of the entity the member lies in");
        leaf.Nullable.Should().BeTrue("an object above it may be missing");
        leaf.Present.Should().NotBe(ExplainColumn.Always);

        var can = CanOf(answer, "customer.address.companyName");

        can["can"]!["values"]!.GetValue<bool>().Should().BeTrue();
        can["can"]!["sort"]!.GetValue<bool>().Should().BeTrue();
        can["can"]!["group"]!.GetValue<bool>().Should().BeTrue();
        can["can"]!["filter"]!.AsArray().Select(op => op!.GetValue<string>()).Should().Contain(["eq", "in", "contains"]);
        can.ContainsKey("why").Should().BeFalse("nothing is refused of a stored text");

        // What the column says is what the row's shape says of the path.
        var bound = await BindHost.BoundAsync(Logistics.Value, Shipment, pipeline);

        foreach (var column in answer.Result!.Columns)
            answer.Result.Capabilities![column.Can!]!["can"]!.ToJsonString().Should().Be(ExplainFacts.Can(bound.FinalShape, column.Path).Can.ToJsonString(), column.Path);
    }

    [Fact]
    public async Task One_projected_member_alone_is_the_only_column_of_its_object()
    {
        var answer = await ExplainAsync("""[{ "project": { "customer.address.companyName": 1 } }]""");

        Paths(answer).Should().BeEquivalentTo(["id", "customer.address.companyName"]);
    }

    [Fact]
    public async Task Without_a_projection_an_object_below_the_first_level_stays_one_column()
    {
        var answer = await ExplainAsync("[]");

        Paths(answer).Should().Contain("customer.address").And.Contain("customer.accountNumber").And.NotContain("customer.address.companyName");
        Column(answer, "customer.address").Kind.Should().Be("object");
        CanOf(answer, "customer.address")["can"]!["values"]!.GetValue<bool>().Should().BeFalse();
        CanOf(answer, "customer.address")["why"]!["values"]!.GetValue<string>().Should().Be("notScalar");
    }

    [Fact]
    public async Task An_object_the_projection_names_and_one_an_exclusion_narrows_stay_one_column()
    {
        var named = await ExplainAsync("""[{ "project": { "customer.address": 1 } }]""");

        Paths(named).Should().BeEquivalentTo(["id", "customer.address"]);
        Column(named, "customer.address").Kind.Should().Be("object");

        var excluded = await ExplainAsync("""[{ "project": { "customer.address.city": 0 } }]""");

        Paths(excluded).Should().Contain("customer.address").And.NotContain("customer.address.companyName").And.NotContain("customer.address.city");
    }

    [Fact]
    public async Task A_projected_member_of_an_object_of_an_unwound_element_is_a_column_under_the_alias()
    {
        var answer = await ExplainAsync("""
            [{ "unwind": { "path": "billingLines", "as": "line", "includeIndex": "position" } },
             { "project": { "id": 1, "position": 1, "line.financialPartner.companyName": 1, "line.financialPartner.address.city": 1, "line.totalPrice": 1 } }]
            """);

        Paths(answer).Should().BeEquivalentTo(["id", "position", "line.totalPrice", "line.financialPartner.companyName", "line.financialPartner.address.city"]);

        var leaf = Column(answer, "line.financialPartner.address.city");

        leaf.Kind.Should().Be("string");
        leaf.Root.Should().Be("line");
        CanOf(answer, "line.financialPartner.address.city")["can"]!["sort"]!.GetValue<bool>().Should().BeTrue();
        CanOf(answer, "line.financialPartner.address.city")["can"]!["values"]!.GetValue<bool>().Should().BeTrue();

        // Kept whole, the element's object is one column as before.
        var whole = await ExplainAsync("""[{ "unwind": { "path": "billingLines", "as": "line" } }]""");

        Paths(whole).Should().Contain("line.financialPartner").And.NotContain("line.financialPartner.companyName");
    }
}
