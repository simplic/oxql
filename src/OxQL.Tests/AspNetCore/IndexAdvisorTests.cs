using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.Mongo.Explain;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>The index advisory over listIndexes and the two <c>$lookup</c> explain shapes.</summary>
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
    public void Lookups_are_read_from_both_explain_shapes()
    {
        var stages = new[]
        {
            new BsonDocument("$match", new BsonDocument("OrganizationId", Org)),
            new BsonDocument("$lookup", new BsonDocument { ["from"] = "customers", ["as"] = "customer__arr" }),
            new BsonDocument("$lookup", new BsonDocument { ["from"] = "suppliers", ["as"] = "supplier" }),
        };

        // The slot-based form reports strategy and indexName inside the winning plan; the pipelined form reports indexesUsed on the stage.
        var explain = new BsonDocument
        {
            ["queryPlanner"] = new BsonDocument("winningPlan", new BsonDocument
            {
                ["stage"] = "EQ_LOOKUP",
                ["strategy"] = "IndexedLoopJoin",
                ["indexName"] = "_id_",
                ["inputStage"] = new BsonDocument("stage", "IXSCAN"),
            }),
            ["stages"] = new BsonArray
            {
                new BsonDocument { ["$lookup"] = new BsonDocument("from", "suppliers"), ["indexesUsed"] = new BsonArray { "_id_" } },
            },
        };
        var advisory = IndexAdvisor.Advise(stages, [Index("_id_", ("_id", 1))], explain);

        var first = Entry(advisory, "lookup:customer__arr");

        first["used"]!.GetValue<bool>().Should().BeTrue();
        first["index"]!.GetValue<string>().Should().Be("_id_");
        first["note"]!.GetValue<string>().Should().Contain("IndexedLoopJoin");

        var second = Entry(advisory, "lookup:supplier");

        second["used"]!.GetValue<bool>().Should().BeTrue();
        second["note"]!.GetValue<string>().Should().Contain("pipelined");
    }

    [Fact]
    public void Without_a_server_explain_a_lookup_is_reported_as_unknown()
    {
        var stages = new[]
        {
            new BsonDocument("$match", new BsonDocument("OrganizationId", Org)),
            new BsonDocument("$lookup", new BsonDocument { ["from"] = "customers", ["as"] = "customer" }),
        };
        var entry = Entry(IndexAdvisor.Advise(stages, [], null), "lookup:customer");

        entry["used"].Should().BeNull();
        entry["note"]!.GetValue<string>().Should().Contain("unavailable");
    }
}
