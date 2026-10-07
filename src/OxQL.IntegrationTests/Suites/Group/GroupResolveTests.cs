using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;
using static OxQL.IntegrationTests.Suites.Group.GroupKit;

namespace OxQL.IntegrationTests.Suites.Group;

/// <summary>
/// A <c>resolve</c> after a <c>group</c> (DG2): a key over a member that declares a reference keeps
/// that reference, so the group's row can show the record its key names ("revenue by customer" with
/// the customer's name). A target of this service joins in the aggregate, on the page's rows when
/// only the display reads it; another service's is fetched after the page, one owner query for the
/// page's keys. The groups, their counts and their order are those of the same request without the
/// join; what the alias holds is the record the corpus holds under the key.
/// </summary>
[Trait("Category", "Integration")]
public class GroupResolveTests
{
    private static Task<LabClient> Fleet() => Lab.ClientAsync(LabService.Fleet);

    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    /// <summary>The equipment per vehicle key: the key as the wire spells it (null for no vehicle), and the count.</summary>
    private static List<(string? Vehicle, int Count)> EquipmentPerVehicle() => Corpus.Rows(Corpus.Equipment)
        .GroupBy(row => Corpus.GuidAt(row, "vehicle.id"))
        .Select(group => (group.Key?.ToString("D"), group.Count()))
        .ToList();

    private static string? MatchCodeOf(string? vehicle) =>
        vehicle is null ? null : Corpus.Rows(Corpus.Vehicle).FirstOrDefault(row => row.Id == Guid.Parse(vehicle)) is { } found ? Corpus.Text(found, "matchCode") : null;

    private const string ByVehicle = """{ "group": { "by": [{ "path": "vehicle.id", "as": "vehicle" }], "fields": { "n": { "count": true } } } }""";

    [Fact]
    public async Task A_key_over_a_reference_resolves_after_the_group_to_the_record_it_names_and_the_groups_stay_what_they_were()
    {
        var client = await Fleet();
        var expected = EquipmentPerVehicle();

        expected.Should().Contain(group => group.Vehicle == null, "the corpus holds equipment without a vehicle");
        expected.Should().Contain(group => group.Vehicle != null && MatchCodeOf(group.Vehicle) == null, "and equipment whose vehicle does not exist");
        expected.Should().Contain(group => group.Count > 1);

        const string Tail = """{ "sort": [{ "n": "desc" }, { "vehicle": "asc" }] }, { "page": { "limit": 100, "count": true } }""";
        var plain = await client.SendAsync(Corpus.Equipment, $"[ {ByVehicle}, {Tail} ]");
        var joined = await client.SendAsync(Corpus.Equipment, $$"""[ {{ByVehicle}}, { "resolve": { "path": "vehicle", "as": "veh" } }, { "project": { "vehicle": 1, "n": 1, "veh.id": 1, "veh.matchCode": 1 } }, {{Tail}} ]""");

        plain.ShouldBeOk();
        joined.ShouldBeOk().ShouldHaveNoDiagnostics();
        joined.TotalCount.Should().Be(expected.Count);
        joined.Items.Select(row => (row!["vehicle"]?.GetValue<string>(), (int)Number(row["n"])!.Value)).Should().BeEquivalentTo(expected);
        joined.Items.Select(row => row!["vehicle"]?.GetValue<string>()).Should().Equal(plain.Items.Select(row => row!["vehicle"]?.GetValue<string>()), "the join decides no group and no order");

        foreach (var row in joined.Items)
        {
            var key = row!["vehicle"]?.GetValue<string>();

            if (MatchCodeOf(key) is { } code)
            {
                row["veh"]!["id"]!.GetValue<string>().Should().Be(key);
                row["veh"]!["matchCode"]!.GetValue<string>().Should().Be(code);
            }
            else
            {
                row["veh"].Should().BeNull($"the key {key ?? "null"} names no vehicle");
            }
        }

        // Kept whole, the alias shows the target's key and display member, as before a group.
        var whole = await client.SendAsync(Corpus.Equipment, $$"""[ {{ByVehicle}}, { "resolve": { "path": "vehicle", "as": "veh" } }, {{Tail}} ]""");

        whole.ShouldBeOk();
        whole.Items.Where(row => row!["veh"] is not null).Should().NotBeEmpty().And.OnlyContain(row => row!["veh"]!["id"]!.GetValue<string>() == row["vehicle"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_groups_page_by_value_with_the_join_taken_on_each_pages_rows_and_a_sort_or_a_condition_on_the_record_takes_it_before_the_page()
    {
        var client = await Fleet();
        var expected = EquipmentPerVehicle();

        // Display only: the join runs after the page is taken.
        var walk = await client.WalkAsync(Corpus.Equipment,
            $$"""[ {{ByVehicle}}, { "resolve": { "path": "vehicle", "as": "veh" } }, { "project": { "vehicle": 1, "n": 1, "veh.matchCode": 1 } }, { "sort": [{ "n": "desc" }, { "vehicle": "asc" }] } ]""", limit: 2);

        walk.Items.Should().HaveCount(expected.Count);
        walk.Pages.Should().BeGreaterThan(1);
        walk.Items.Select(row => row["vehicle"]?.GetValue<string>()).Should().OnlyHaveUniqueItems();
        walk.Items.Should().OnlyContain(row => (row["veh"] == null ? null : row["veh"]!["matchCode"]!.GetValue<string>()) == MatchCodeOf(row["vehicle"] == null ? null : row["vehicle"]!.GetValue<string>()));
        Cursors.Payload(walk.Cursors[0])["m"]!.GetValue<string>().Should().Be("k", "groups continue by value with the join after them");

        // Ordered by the record's member: the same groups, in the order of the names; a group without a record first.
        var sorted = await client.WalkAsync(Corpus.Equipment,
            $$"""[ {{ByVehicle}}, { "resolve": { "path": "vehicle", "as": "veh" } }, { "project": { "vehicle": 1, "n": 1, "veh.matchCode": 1 } }, { "sort": [{ "veh.matchCode": "asc" }, { "vehicle": "asc" }] } ]""", limit: 2);

        sorted.Items.Should().HaveCount(expected.Count);
        sorted.Items.Select(row => row["vehicle"]?.GetValue<string>()).Should().BeEquivalentTo(expected.Select(group => group.Vehicle));

        var codes = sorted.Items.Select(row => row["veh"] == null ? null : row["veh"]!["matchCode"]!.GetValue<string>()).ToList();

        codes.Should().Equal(codes.OrderBy(code => code, Comparer<string?>.Create((a, b) => a is null ? b is null ? 0 : -1 : b is null ? 1 : Order.CompareCollated(a, b))));

        // A condition on the record keeps the groups whose record meets it.
        var some = Corpus.Rows(Corpus.Vehicle).Select(row => Corpus.Text(row, "matchCode")!).First(code => expected.Any(group => MatchCodeOf(group.Vehicle) == code));
        var having = await client.SendAsync(Corpus.Equipment,
            $$"""[ {{ByVehicle}}, { "resolve": { "path": "vehicle", "as": "veh" } }, { "match": { "veh.matchCode": { "eq": "{{some}}" } } }, { "project": { "vehicle": 1, "n": 1 } }, { "page": { "limit": 100, "count": true } } ]""");

        having.ShouldBeOk();
        having.Items.Select(row => (row!["vehicle"]?.GetValue<string>(), (int)Number(row["n"])!.Value)).Should().BeEquivalentTo(expected.Where(group => MatchCodeOf(group.Vehicle) == some));
        having.TotalCount.Should().Be(having.Items.Count);
    }

    [Fact]
    public async Task Another_services_record_is_fetched_for_the_pages_keys_and_a_key_that_names_none_has_a_null_alias()
    {
        var client = await Transport();
        var shipments = Corpus.Rows(Corpus.Shipment);
        var departments = Corpus.Rows(Corpus.Department).ToDictionary(row => row.Id, row => Corpus.Text(row, "name"));
        var expected = shipments.GroupBy(row => Corpus.GuidAt(row, "department.id")).Select(group => (Key: group.Key, Count: group.Count())).ToList();

        expected.Should().Contain(group => group.Key == null);
        expected.Should().Contain(group => group.Key != null && !departments.ContainsKey(group.Key.Value), "a shipment names a department the fleet does not hold");
        expected.Count(group => group.Key != null && departments.ContainsKey(group.Key.Value)).Should().BeGreaterThan(1);

        var answer = await client.SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "department.id", "as": "department" }], "fields": { "n": { "count": true } } } },
              { "resolve": { "path": "department", "as": "dep" } },
              { "project": { "department": 1, "n": 1, "dep.id": 1, "dep.name": 1 } },
              { "sort": [{ "n": "desc" }, { "department": "asc" }] }, { "page": { "limit": 100, "count": true } } ]
            """);

        answer.ShouldBeOk();
        answer.Items.Should().HaveCount(expected.Count);
        answer.TotalCount.Should().Be(expected.Count);

        foreach (var row in answer.Items)
        {
            Guid? key = row!["department"] is { } written ? Guid.Parse(written.GetValue<string>()) : null;

            Number(row["n"]).Should().Be(expected.Single(group => group.Key == key).Count);

            if (key is { } id && departments.TryGetValue(id, out var name))
            {
                row["dep"]!["id"]!.GetValue<string>().Should().Be(id.ToString("D"));
                row["dep"]!["name"]?.GetValue<string>().Should().Be(name);
            }
            else
            {
                row["dep"].Should().BeNull($"the key {key?.ToString() ?? "null"} names no department");
            }
        }

        // Walked in pages of two, every page asks for its own keys and the rows are the same.
        var walk = await client.WalkAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "department.id", "as": "department" }], "fields": { "n": { "count": true } } } },
              { "resolve": { "path": "department", "as": "dep" } },
              { "project": { "department": 1, "n": 1, "dep.id": 1, "dep.name": 1 } },
              { "sort": [{ "n": "desc" }, { "department": "asc" }] } ]
            """, limit: 2);

        walk.Items.Select(row => row.ToJsonString()).Should().Equal(answer.Items.Select(row => row!.ToJsonString()));

        // A condition on the owner's member is a semi-join on the group's key: the groups whose record meets it.
        var workshop = await client.SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "department.id", "as": "department" }], "fields": { "n": { "count": true } } } },
              { "resolve": { "path": "department", "as": "dep" } }, { "match": { "dep.name": { "eq": "Workshop" } } },
              { "project": { "department": 1, "n": 1 } }, { "page": { "limit": 100, "count": true } } ]
            """);
        var named = expected.Where(group => group.Key is { } id && departments.TryGetValue(id, out var name) && name == "Workshop").ToList();

        named.Should().NotBeEmpty();
        workshop.ShouldBeOk();
        workshop.Items.Select(row => (Guid.Parse(row!["department"]!.GetValue<string>()), (int)Number(row["n"])!.Value)).Should().BeEquivalentTo(named.Select(group => (group.Key!.Value, group.Count)));
        workshop.TotalCount.Should().Be(named.Count);
    }

    [Fact]
    public async Task What_a_missing_record_is_follows_the_stage_as_before_a_group()
    {
        var client = await Transport();
        const string Grouped = """{ "group": { "by": [{ "path": "department.id", "as": "department" }], "fields": { "n": { "count": true } } } }""";

        var told = await client.SendAsync(Corpus.Shipment, $$"""
            [ {{Grouped}}, { "resolve": { "path": "department", "as": "dep", "outcomeAs": "depOutcome", "onMissing": "report" } },
              { "project": { "department": 1, "depOutcome": 1, "dep.name": 1 } }, { "sort": [{ "department": "asc" }] }, { "page": { "limit": 100 } } ]
            """);

        told.ShouldBeOk();
        told.ShouldHaveDiagnostic("RESOLVE_MISSING");
        told.Items.Where(row => row!["department"] is null).Should().OnlyContain(row => row!["depOutcome"]!.GetValue<string>() == "reference_null");
        told.Items.Where(row => row!["department"] is not null && row["dep"] is null).Should().NotBeEmpty().And.OnlyContain(row => row!["depOutcome"]!.GetValue<string>() == "not_found");
        told.Items.Where(row => row!["dep"] is not null).Should().NotBeEmpty().And.OnlyContain(row => row!["depOutcome"]!.GetValue<string>() == "resolved");

        var refused = await client.SendAsync(Corpus.Shipment, $$"""[ {{Grouped}}, { "resolve": { "path": "department", "as": "dep", "onMissing": "refuse" } }, { "page": { "limit": 100 } } ]""");

        refused.ShouldRefuse("RESOLVE_MISSING", 422);
    }

    [Fact]
    public async Task What_cannot_be_followed_after_a_group_is_refused_at_the_resolve()
    {
        var client = await Transport();

        // An aggregate and a key over a member without a reference declare none.
        var aggregate = await client.SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "dep": { "first": "department.id" } } } }, { "resolve": { "path": "dep", "as": "d" } } ]
            """);

        ShouldRefuseOnly(aggregate, "RESOLVE_NOT_DECLARED").Should().Be("'dep' declares no reference; it is a guid.");

        var plainKey = await client.SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "status.name", "as": "status" }], "fields": { "n": { "count": true } } } }, { "resolve": { "path": "status", "as": "s" } } ]
            """);

        ShouldRefuseOnly(plainKey, "RESOLVE_NOT_DECLARED").Should().Be("'status' declares no reference; it is a string.");

        // A lookup still has no key to join on: the group's row is no record.
        var lookup = await (await Fleet()).SendAsync(Corpus.Vehicle, """
            [ { "group": { "by": [{ "path": "id", "as": "id2" }], "fields": { "n": { "count": true } } } }, { "lookup": { "from": "fleet.equipment", "path": "vehicle.id", "as": "eq" } } ]
            """);

        lookup.ShouldRefuse("UNKNOWN_PATH", 400)["message"]!.GetValue<string>().Should().Be("The shape after a group has no key to join on.");

        // Contract 1 has no resolve stage of this form at all, after a group or before it.
        var legacy = await client.Contract(1).SendAsync(Corpus.Shipment, """
            [ { "group": { "by": [{ "path": "department.id", "as": "department" }], "fields": { "n": { "count": true } } } }, { "resolve": { "path": "department", "as": "dep" } } ]
            """);

        legacy.ShouldRefuse("LEGACY_STAGE_UNSUPPORTED", 400);
    }

    [Fact]
    public async Task Explain_says_the_key_can_be_followed_where_the_join_runs_and_what_the_row_then_holds()
    {
        var client = await Fleet();
        var answer = await client.ExplainHereAsync(new JsonObject
        {
            ["query"] = Json.Request(Corpus.Equipment, $$"""[ {{ByVehicle}}, { "resolve": { "path": "vehicle", "as": "veh" } }, { "project": { "vehicle": 1, "n": 1, "veh.matchCode": 1 } }, { "sort": [{ "n": "desc" }] } ]"""),
            ["include"] = new JsonArray("plan", "notes"),
        });
        var body = answer.Body!;

        body["valid"]!.GetValue<bool>().Should().BeTrue(answer.ToString());
        body["executable"]!.GetValue<string>().Should().Be("yes");

        // The key's alias says the reference it follows; the aggregate's says none.
        body["aliases"]!["vehicle"]!["reference"]!["cases"]![0]!["targets"]![0]!["entity"]!.GetValue<string>().Should().Be(Corpus.Vehicle);
        body["aliases"]!["vehicle"]!["keyOf"]!.GetValue<string>().Should().Be("vehicle.id");
        body["aliases"]!["n"]!.AsObject().ContainsKey("reference").Should().BeFalse();

        // The join is a stage like any other, placed after the page since only the display reads it.
        body["stages"]![1]!["kind"]!.GetValue<string>().Should().Be("resolve");
        body["stages"]![1]!["status"]!.GetValue<string>().Should().Be("ok");
        body["notes"]!.AsArray().Should().Contain(note => note!["code"]!.GetValue<string>() == "JOIN_AFTER_PAGE" && note["stage"]!.GetValue<int>() == 1);

        var stages = body["plan"]!["stages"]!.AsArray().Select(stage => stage!.AsObject().Single().Key).ToList();

        stages.IndexOf("$lookup").Should().BeGreaterThan(stages.IndexOf("$limit"), string.Join(",", stages));
        stages.IndexOf("$group").Should().BeLessThan(stages.IndexOf("$limit"));

        // The row is a bucket that holds one record under the alias.
        body["result"]!["identity"]!.ToJsonString().Should().Be("""{"kind":"bucket","bucket":{"keys":["vehicle"]}}""");
        body["result"]!["subjects"]!.ToJsonString().Should().Be($$"""[{"root":"veh","entity":"{{Corpus.Vehicle}}","key":"veh.id","nullable":true,"many":false}]""");
        body["result"]!["columns"]!.AsArray().Select(column => column!["path"]!.GetValue<string>()).Should().Equal("vehicle", "n", "veh.matchCode");

        // Without the resolve the row holds no record.
        var plain = await client.ExplainHereAsync(new JsonObject { ["query"] = Json.Request(Corpus.Equipment, $"[ {ByVehicle} ]") });

        plain.Body!["result"]!["subjects"]!.AsArray().Should().BeEmpty();
        plain.Body["aliases"]!["vehicle"]!["reference"].Should().NotBeNull();
    }
}
