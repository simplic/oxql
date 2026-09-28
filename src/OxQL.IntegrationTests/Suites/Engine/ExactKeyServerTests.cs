using FluentAssertions;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Harness;
using OxQL.Model.Build;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Engine;

/// <summary>
/// A grouped owner query (<c>keyedBy</c>) keeps its string keys exact inside a collated aggregate
/// (RE-12): a filter that folds case collates the whole aggregate, and the key <c>$in</c> would then
/// also take a record whose key differs only in case, which the caller would read as a second record
/// of the key. The same holds through this host's own plain key match of a local string target.
/// </summary>
[Trait("Category", "Integration")]
public class ExactKeyServerTests
{
    public const string Customer = "ek.customer";

    public sealed class CustomerModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public string Name { get; set; } = "";

        public string Code { get; set; } = "";
    }

    private static readonly EngineDirect Direct = new(ClrModelBuilder.Build(
    [
        new EntityDeclaration(Customer, Customer, typeof(CustomerModel), "tmp_ek_customers", null, false),
    ]));

    private static BsonDocument Row(int n, string code) => new()
    {
        ["_id"] = new BsonBinaryData(Guid.Parse($"00000000-0000-0000-0000-{n:D12}"), GuidRepresentation.Standard),
        ["OrganizationId"] = EngineDirect.OrganisationValue,
        ["Name"] = "Same Name",
        ["Code"] = code,
    };

    [Fact]
    public async Task K01_a_grouped_query_under_a_folding_filter_answers_only_the_key_as_written()
    {
        await using var owned = await OxQL.IntegrationTests.Fleet.MongoFixture.CreateDatabaseAsync("exact_key");
        await owned.Database.GetCollection<BsonDocument>("tmp_ek_customers").InsertManyAsync([Row(1, "abc"), Row(2, "ABC"), Row(3, "xyz")]);
        var engine = Direct.Engine(await OxQL.IntegrationTests.Fleet.MongoFixture.ClientAsync(), owned.Name);

        var request = EngineDirect.Request(Customer, """[{ "match": { "name": { "eq": "same name" } } }, { "project": { "code": 1 } }]""") with
        {
            KeyedBy = new KeyedByMember { Path = "code", Keys = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "abc" }), PerKey = 2 },
        };

        var outcome = await engine.ExecuteAsync(request, Direct.Context() with { Internal = true });

        var result = outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? EngineDirect.Describe(refused.Refusal) : "").Subject.Result;
        result.Items.Select(item => item!["code"]!.GetValue<string>()).Should().Equal(["abc"], "the filter folds, the key does not");
    }
}
