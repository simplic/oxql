using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Mongo.Explain;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>The static index advisory: the leading match, the sort and every join field matched against index lists.</summary>
public class IndexAdvisorTests
{
    private static BsonDocument Index(string name, params (string Field, int Direction)[] keys)
    {
        var key = new BsonDocument();

        foreach (var (field, direction) in keys)
            key[field] = direction;

        return new BsonDocument { ["name"] = name, ["key"] = key };
    }

    private static readonly BsonBinaryData Org = new(Guid.NewGuid(), GuidRepresentation.Standard);

    private static JsonObject Entry(IReadOnlyList<JsonNode> advisory, string field) =>
        advisory.Select(node => node!.AsObject()).Single(node => node["field"]!.GetValue<string>() == field);

    [Fact]
    public void Leading_match_fields_are_matched_through_the_equality_prefix()
    {
        var stages = new[]
        {
            new BsonDocument("$match", new BsonDocument("OrganizationId", Org)),
            new BsonDocument("$match", new BsonDocument { ["Number"] = "a", ["Count"] = new BsonDocument("$gt", 1) }),
            new BsonDocument("$sort", new BsonDocument { ["Number"] = 1, ["_id"] = 1 }),
            new BsonDocument("$limit", 11),
        };
        var indexes = new[] { Index("_id_", ("_id", 1)), Index("org_number", ("OrganizationId", 1), ("Number", 1)) };
        var advisory = IndexAdvisor.Advise(stages, indexes, null);

        Entry(advisory, "OrganizationId")["used"]!.GetValue<bool>().Should().BeTrue();
        Entry(advisory, "OrganizationId")["index"]!.GetValue<string>().Should().Be("org_number");
        Entry(advisory, "Number")["used"]!.GetValue<bool>().Should().BeTrue("Number is reached through the OrganizationId equality");
        Entry(advisory, "Count")["used"]!.GetValue<bool>().Should().BeFalse();
        Entry(advisory, "Count")["note"]!.GetValue<string>().Should().Contain("range");

        var sort = Entry(advisory, "sort:Number,_id");

        sort["used"]!.GetValue<bool>().Should().BeFalse("the index does not carry the _id tie-breaker");
        sort["index"]!.GetValue<string>().Should().Be("org_number");
        sort["note"]!.GetValue<string>().Should().Contain("tie-breaker");
    }

    [Fact]
    public void A_sort_is_served_by_an_index_after_the_equality_prefix_in_either_direction()
    {
        var stages = new[]
        {
            new BsonDocument("$match", new BsonDocument("OrganizationId", Org)),
            new BsonDocument("$sort", new BsonDocument { ["When"] = -1, ["_id"] = -1 }),
        };
        var indexes = new[] { Index("org_when_id", ("OrganizationId", 1), ("When", 1), ("_id", 1)) };
        var sort = Entry(IndexAdvisor.Advise(stages, indexes, null), "sort:When,_id");

        sort["used"]!.GetValue<bool>().Should().BeTrue();
        sort["index"]!.GetValue<string>().Should().Be("org_when_id");
    }

    [Fact]
    public void Regex_and_negation_forms_are_named_in_the_note()
    {
        var stages = new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                ["Number"] = new BsonRegularExpression("^ab", "i"),
                ["Note"] = new BsonRegularExpression("ab", ""),
                ["State"] = new BsonDocument("$ne", 1),
                ["$or"] = new BsonArray { new BsonDocument("Count", 1), new BsonDocument("Big", 2) },
            }),
        };
        var advisory = IndexAdvisor.Advise(stages, [Index("number", ("Number", 1))], null);

        Entry(advisory, "Number")["note"]!.GetValue<string>().Should().Contain("case-insensitive");
        Entry(advisory, "Note")["note"]!.GetValue<string>().Should().Contain("unanchored");
        Entry(advisory, "State")["note"]!.GetValue<string>().Should().Contain("negation");
        Entry(advisory, "Count")["note"]!.GetValue<string>().Should().Contain("inside an or");
    }

    [Fact]
    public void A_join_is_served_by_an_index_of_the_joined_collection_reached_through_its_scope_equality()
    {
        var stages = new[]
        {
            new BsonDocument("$match", new BsonDocument("OrganizationId", Org)),
            Lookup("orders", "customers", "CustomerId", "orders"),
            Lookup("suppliers", "suppliers", "_id", "supplier"),
        };
        var joined = new Dictionary<string, IReadOnlyList<BsonDocument>>
        {
            ["orders"] = [Index("_id_", ("_id", 1)), Index("org_customer", ("OrganizationId", 1), ("CustomerId", 1))],
            ["suppliers"] = [Index("_id_", ("_id", 1))],
        };
        var advisory = IndexAdvisor.Advise(stages, [Index("_id_", ("_id", 1))], joined);

        var orders = Entry(advisory, "lookup:orders");

        orders["used"]!.GetValue<bool>().Should().BeTrue("CustomerId is reached through the join's own OrganizationId equality");
        orders["index"]!.GetValue<string>().Should().Be("org_customer");
        orders["note"]!.GetValue<string>().Should().Contain("orders.CustomerId");

        var supplier = Entry(advisory, "lookup:supplier");

        supplier["used"]!.GetValue<bool>().Should().BeTrue();
        supplier["index"]!.GetValue<string>().Should().Be("_id_");
    }

    [Fact]
    public void A_join_without_an_index_on_its_field_scans_and_one_whose_indexes_were_not_read_is_unknown()
    {
        var stages = new[]
        {
            new BsonDocument("$match", new BsonDocument("OrganizationId", Org)),
            Lookup("orders", "customers", "CustomerId", "orders"),
            Lookup("notes", "customers", "CustomerId", "notes"),
        };
        var joined = new Dictionary<string, IReadOnlyList<BsonDocument>> { ["orders"] = [Index("_id_", ("_id", 1)), Index("customer_org", ("CustomerId", 1), ("OrganizationId", 1))] };
        var advisory = IndexAdvisor.Advise(stages, [], joined);

        Entry(advisory, "lookup:orders")["used"]!.GetValue<bool>().Should().BeTrue("an index that starts with the join field serves it");

        var unknown = Entry(advisory, "lookup:notes");

        unknown["used"].Should().BeNull();
        unknown["note"]!.GetValue<string>().Should().Contain("not known");

        var scanned = Entry(IndexAdvisor.Advise(stages.Take(2).ToList(), [], new Dictionary<string, IReadOnlyList<BsonDocument>> { ["orders"] = [Index("_id_", ("_id", 1))] }), "lookup:orders");

        scanned["used"]!.GetValue<bool>().Should().BeFalse();
        scanned["note"]!.GetValue<string>().Should().Contain("scans 'orders'");
        IndexAdvisor.Advise(stages, [], null).Where(line => line["field"]!.GetValue<string>().StartsWith("lookup:", StringComparison.Ordinal))
            .Should().HaveCount(2).And.OnlyContain(line => line["used"] == null, "without index lists nothing about a join is known");
    }

    /// <summary>A <c>$lookup</c> as the compiler emits it: the scoped sub-pipeline, then the join on <paramref name="foreignField"/>.</summary>
    private static BsonDocument Lookup(string from, string localField, string foreignField, string alias) =>
        new("$lookup", new BsonDocument
        {
            ["from"] = from,
            ["localField"] = localField,
            ["foreignField"] = foreignField,
            ["pipeline"] = new BsonArray { new BsonDocument("$match", new BsonDocument("OrganizationId", Org)), new BsonDocument("$limit", 101) },
            ["as"] = alias,
        });
}
