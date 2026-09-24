using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Shape;

/// <summary>
/// Area H: collections, <c>any</c> and correlation. A path through a collection means "some
/// element"; <c>any</c> means "one element satisfies all of it". A case that cannot show the two
/// returning different ids proves nothing, so every correlation case is anchored on a row built
/// to split them (<c>items-split</c>, <c>appt-split</c>, <c>arrays-many</c>) and asserts that the
/// row is in one answer and not the other. Ported from the legacy <c>shape-h-collections</c>
/// battery, engine half only: the client's uncorrelated-array guard and its compiled bytes went
/// to the client's specs.
/// </summary>
[Trait("Category", "Integration")]
public class ShapeHCollectionsTests
{
    private const string FullPage = """{ "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500, "includeTotalCount": true } }""";

    /// <summary>One stored <c>items</c> element of a shipment, read by storage name.</summary>
    private sealed record Item(string? Status, double? Quantity, string? Article, string? Text);

    private static IReadOnlyList<Item> Items(CorpusRow row) => Corpus.Elements(row, "items").OfType<BsonDocument>().Select(item => new Item(
        item.GetValue("Status", BsonNull.Value) is BsonDocument status && status.GetValue("Name", BsonNull.Value) is BsonString name ? name.Value : null,
        item.GetValue("Quantity", BsonNull.Value) is BsonDocument quantity && quantity.GetValue("Value", BsonNull.Value) is { IsNumeric: true } value ? value.ToDouble() : null,
        item.GetValue("Article", BsonNull.Value) is BsonDocument article && article.GetValue("Name", BsonNull.Value) is BsonString articleName ? articleName.Value : null,
        item.GetValue("Text", BsonNull.Value) is BsonString text ? text.Value : null)).ToList();

    private static IReadOnlyList<Guid> ShipmentsWhere(Func<IReadOnlyList<Item>, bool> predicate) =>
        Corpus.IdsWhere(Corpus.Shipment, row => predicate(Items(row)));

    private static Guid Split => Corpus.IdOf(Corpus.Shipment, "items-split");

    private static Task<WireAnswer> Match(object condition, string entity = Corpus.Shipment) =>
        Send(entity, $$"""[ { "match": {{Json.Text(condition)}} }, { "project": { "id": 1 } }, {{FullPage}} ]""");

    private static async Task<WireAnswer> Send(string entity, string pipeline) =>
        await (await Lab.ClientForAsync(entity)).SendAsync(entity, pipeline);

    private static JsonObject Refused(WireAnswer answer, string code, string? path = null, int? stage = null)
    {
        var error = answer.ShouldRefuse(code, 400);
        answer.ErrorCodes.Should().Equal([code], answer.ToString());

        if (path is not null)
            error["path"]!.GetValue<string>().Should().Be(path, answer.ToString());

        if (stage is not null)
            error["stage"]!.GetValue<int>().Should().Be(stage.Value, answer.ToString());

        return error;
    }

    [Fact]
    public async Task H00_the_split_row_holds_one_open_item_of_quantity_0_and_one_closed_item_of_quantity_5()
    {
        Items(Corpus.Row(Corpus.Shipment, "items-split")).Select(item => (item.Status, item.Quantity)).Should().Equal(("Open", 0d), ("Closed", 5d));
    }

    [Fact]
    public async Task H01_a_path_through_a_collection_in_match_means_some_element()
    {
        var expected = ShipmentsWhere(items => items.Any(item => item.Quantity == 7));
        expected.Should().NotBeEmpty().And.HaveCountLessThan(Corpus.Counts(Corpus.Shipment).A);

        var answer = await Send(Corpus.Shipment, $$"""[ { "match": { "items.quantity.value": { "eq": 7 } } }, { "project": { "id": 1, "items.quantity.value": 1 } }, {{FullPage}} ]""");

        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);

        // …and it is the document that matched: every row has such an item.
        foreach (var row in answer.Items)
            row!["items"]!.AsArray().Select(item => item!["quantity"]!["value"]!.GetValue<double>()).Should().Contain(7d, row.ToJsonString());
    }

    [Fact]
    public async Task H02_two_conditions_on_one_collection_outside_any_match_one_element_each()
    {
        var expected = ShipmentsWhere(items => items.Any(item => item.Status == "Open") && items.Any(item => item.Quantity > 0));
        expected.Should().Contain(Split);

        var answer = await Match("""{ "and": [ { "items.status.name": { "eq": "Open" } }, { "items.quantity.value": { "gt": 0 } } ] }""");

        answer.ShouldHaveIds(expected, "the server has no refusal for the uncorrelated shape");
    }

    [Fact]
    public async Task H07_any_evaluates_its_whole_condition_against_one_element_and_the_ids_differ_from_H02()
    {
        var correlated = ShipmentsWhere(items => items.Any(item => item.Status == "Open" && item.Quantity > 0));
        var uncorrelated = ShipmentsWhere(items => items.Any(item => item.Status == "Open") && items.Any(item => item.Quantity > 0));
        uncorrelated.Except(correlated).Should().Equal([Split], "the two forms are not the same query");

        // The inner paths are element-relative: the exact bytes the client compiles any() to (H8).
        var answer = await Match("""{ "items": { "any": { "and": [ { "status.name": { "eq": "Open" } }, { "quantity.value": { "gt": 0 } } ] } } }""");

        answer.ShouldHaveIds(correlated).ShouldHaveTotal(correlated.Count);
    }

    [Fact]
    public async Task H07b_the_same_split_on_the_vehicle_appointments()
    {
        static IReadOnlyList<(int? CheckType, int? NextValue)> Appointments(CorpusRow row) => Corpus.Elements(row, "appointments").OfType<BsonDocument>()
            .Select(a => (a.GetValue("CheckType", BsonNull.Value) is BsonInt32 check ? check.Value : (int?)null, a.GetValue("NextValue", BsonNull.Value) is BsonInt32 next ? next.Value : (int?)null)).ToList();

        var uncorrelated = Corpus.IdsWhere(Corpus.Vehicle, row => Appointments(row).Any(a => a.CheckType == 1) && Appointments(row).Any(a => a.NextValue > 0));
        var correlated = Corpus.IdsWhere(Corpus.Vehicle, row => Appointments(row).Any(a => a.CheckType == 1 && a.NextValue > 0));
        uncorrelated.Except(correlated).Should().Equal([Corpus.IdOf(Corpus.Vehicle, "appt-split")]);

        (await Match("""{ "and": [ { "appointments.checkType": { "eq": 1 } }, { "appointments.nextValue": { "gt": 0 } } ] }""", Corpus.Vehicle)).ShouldHaveIds(uncorrelated);
        (await Match("""{ "appointments": { "any": { "checkType": { "eq": 1 }, "nextValue": { "gt": 0 } } } }""", Corpus.Vehicle)).ShouldHaveIds(correlated);
    }

    [Fact]
    public async Task H07c_the_same_split_on_the_employee_email_addresses_where_the_correlated_form_matches_nothing()
    {
        static IReadOnlyList<(string? Email, string? Type)> Emails(CorpusRow row) => Corpus.Elements(row, "emailAddresses").OfType<BsonDocument>()
            .Select(e => (e.GetValue("Email", BsonNull.Value) is BsonString email ? email.Value : null, e.GetValue("Type", BsonNull.Value) is BsonString type ? type.Value : null)).ToList();

        var uncorrelated = Corpus.IdsWhere(Corpus.Employee, row => Emails(row).Any(e => e.Email == "split-a@lab.invalid") && Emails(row).Any(e => e.Type == "private"));
        var correlated = Corpus.IdsWhere(Corpus.Employee, row => Emails(row).Any(e => e.Email == "split-a@lab.invalid" && e.Type == "private"));
        uncorrelated.Should().Equal([Corpus.IdOf(Corpus.Employee, "arrays-many")]);
        correlated.Should().BeEmpty();

        (await Match("""{ "and": [ { "emailAddresses.email": { "eq": "split-a@lab.invalid" } }, { "emailAddresses.type": { "eq": "private" } } ] }""", Corpus.Employee)).ShouldHaveIds(uncorrelated);
        (await Match("""{ "emailAddresses": { "any": { "and": [ { "email": { "eq": "split-a@lab.invalid" } }, { "type": { "eq": "private" } } ] } } }""", Corpus.Employee)).ShouldHaveIds(correlated);
    }

    [Fact]
    public async Task H09_dotted_inner_paths_inside_any_resolve_against_the_element_shape()
    {
        var expected = ShipmentsWhere(items => items.Any(item => item.Article == "Gravel"));
        expected.Should().ContainSingle();

        (await Match("""{ "items": { "any": { "article.name": { "eq": "Gravel" } } } }""")).ShouldHaveIds(expected);
    }

    [Fact]
    public async Task H10_an_unknown_path_inside_any_is_refused_naming_the_element_relative_path()
    {
        Refused(await Match("""{ "items": { "any": { "nope": { "eq": 1 } } } }"""), "UNKNOWN_PATH", "nope");
    }

    [Fact]
    public async Task H11_any_on_a_non_collection_path_is_refused()
    {
        Refused(await Match("""{ "status": { "any": { "name": { "eq": "Open" } } } }"""), "ANY_NOT_APPLICABLE", "status");
    }

    [Fact]
    public async Task H12_any_on_a_collection_of_scalars_is_refused_and_the_plain_form_is_the_some_element_match()
    {
        Refused(await Match("""{ "states": { "any": { "x": { "eq": 1 } } } }""", Corpus.Transaction), "ANY_NOT_APPLICABLE", "states")["message"]!
            .GetValue<string>().ToLowerInvariant().Should().Contain("element");

        var expected = Corpus.IdsWhere(Corpus.Transaction, row => Corpus.Texts(row, "states").Contains("review"));
        expected.Should().ContainSingle();
        (await Match("""{ "states": { "eq": "review" } }""", Corpus.Transaction)).ShouldHaveIds(expected);

        // exists inside any on a scalar collection is refused the same way.
        Refused(await Match("""{ "states": { "any": { "x": { "exists": true } } } }""", Corpus.Transaction), "ANY_NOT_APPLICABLE", "states");

        // …and on the employee's collection of scalars.
        Refused(await Match("""{ "functions": { "any": { "x": { "eq": 1 } } } }""", Corpus.Employee), "ANY_NOT_APPLICABLE", "functions");
    }

    [Fact]
    public async Task H13_any_on_a_collection_reached_through_another_collection_is_refused()
    {
        Refused(await Match("""{ "billingLines.references": { "any": { "dataType": { "eq": "order" } } } }"""), "ANY_NOT_APPLICABLE", "billingLines.references");
    }

    [Fact]
    public async Task H14_any_on_a_collection_that_has_already_been_unwound_is_refused()
    {
        Refused(await Send(Corpus.Shipment, $$"""[ { "unwind": { "path": "items" } }, { "match": { "items": { "any": { "orderNumber": { "eq": 1 } } } } }, {{FullPage}} ]"""), "ANY_NOT_APPLICABLE", "items", 1);
    }

    [Fact]
    public async Task H15_any_under_a_remote_alias_is_refused_and_the_plain_some_element_form_is_the_semi_join()
    {
        // Legacy: unreachable (no fleet entity declared a reference). The lab model declares
        // transport.shipment_template#createUserId as a remote reference onto fleet.vehicle, so
        // the case now runs. The local host does not hold the remote entity's element shape, so
        // any() under the remote alias is refused by design (Binder.BindAny: path.IsRemote); the
        // owner evaluates the plain form, which means "some element", as a semi-join.
        Refused(await Send(Corpus.Template, $$"""
            [ { "resolve": { "path": "createUserId", "as": "vehicle" } },
              { "match": { "vehicle.appointments": { "any": { "checkType": { "eq": 1 } } } } },
              {{FullPage}} ]
            """), "ANY_NOT_APPLICABLE", "vehicle.appointments", 1);

        var vehicles = Corpus.Rows(Corpus.Vehicle).ToDictionary(row => row.Id);
        var expected = Corpus.IdsWhere(Corpus.Template, row =>
            Corpus.GuidAt(row, "createUserId") is { } id && vehicles.TryGetValue(id, out var vehicle) &&
            Corpus.Elements(vehicle, "appointments").OfType<BsonDocument>().Any(a => a["CheckType"].AsInt32 == 1));
        expected.Should().NotBeEmpty().And.HaveCountLessThan(20, "only the first twenty templates name a vehicle, and some of those name none");

        var answer = await Send(Corpus.Template, $$"""
            [ { "resolve": { "path": "createUserId", "as": "vehicle" } },
              { "match": { "vehicle.appointments.checkType": { "eq": 1 } } },
              { "project": { "id": 1 } },
              {{FullPage}} ]
            """);

        answer.ShouldHaveIds(expected).ShouldHaveTotal(expected.Count);
    }

    [Fact]
    public async Task H16_any_works_across_two_different_collections_in_one_condition()
    {
        var expected = Corpus.IdsWhere(Corpus.Shipment, row => Items(row).Any(item => item.Status == "Open") && Corpus.Texts(row, "tags.name").Contains("Urgent"));
        expected.Should().Equal([Corpus.IdOf(Corpus.Shipment, "tags-many")]);

        (await Match("""{ "and": [ { "items": { "any": { "status.name": { "eq": "Open" } } } }, { "tags": { "any": { "name": { "eq": "Urgent" } } } } ] }""")).ShouldHaveIds(expected);
    }

    [Fact]
    public async Task H17_a_path_through_a_collection_is_refused_in_sort()
    {
        Refused(await Send(Corpus.Shipment, """[ { "sort": [ { "items.orderNumber": "asc" } ] }, { "page": { "limit": 5 } } ]"""), "NOT_SORTABLE", "items.orderNumber", 0);
    }

    [Fact]
    public async Task H18_a_group_key_under_a_collection_is_refused()
    {
        Refused(await Send(Corpus.Shipment, """[ { "group": { "by": [ { "path": "items.status.name", "as": "st" } ], "fields": { "n": { "count": true } } } }, { "page": { "limit": 50 } } ]"""),
            "GROUP_ON_COLLECTION", "items.status.name", 0);
    }

    [Theory]
    [InlineData("sum")]
    [InlineData("avg")]
    [InlineData("min")]
    [InlineData("max")]
    [InlineData("first")]
    [InlineData("last")]
    [InlineData("countDistinct")]
    public async Task H19_an_aggregate_argument_under_a_collection_is_refused(string function)
    {
        Refused(await Send(Corpus.Shipment, $$"""[ { "group": { "by": [ { "path": "status.name", "as": "st" } ], "fields": { "a": { "{{function}}": "items.quantity.value" } } } }, { "page": { "limit": 50 } } ]"""),
            "GROUP_ON_COLLECTION", "items.quantity.value", 0);
    }

    [Fact]
    public async Task H19b_push_under_a_collection_is_allowed_and_nests_one_array_per_document_that_has_the_member()
    {
        var closed = Corpus.Where(Corpus.Shipment, row => Corpus.Text(row, "status.name") == "Closed");
        var withItems = closed.Where(row => !Corpus.Missing(row, "items")).ToList();
        withItems.Count.Should().BeLessThan(closed.Count, "the corpus must hold a Closed row with no items member for this to prove anything");

        var answer = await Send(Corpus.Shipment, """[ { "group": { "by": [ { "path": "status.name", "as": "st" } ], "fields": { "texts": { "push": "items.text" } } } }, { "sort": [ { "st": "asc" } ] }, { "page": { "limit": 50 } } ]""");

        answer.ShouldBeOk();
        var bucket = answer.Items.Single(row => row!["st"]!.GetValue<string>() == "Closed")!;
        var texts = bucket["texts"]!.AsArray();

        // One array per document that has the member; a document whose source path is missing
        // contributes nothing at all, so the entries cannot be aligned to documents (legacy SH4).
        texts.Should().OnlyContain(entry => entry is JsonArray, "push over a collection nests one array per document");
        texts.Count.Should().Be(withItems.Count);
        texts.Select(entry => entry!.AsArray().Count).Order().Should().Equal(withItems.Select(row => Items(row).Count).Order());
    }

    [Fact]
    public async Task H20_a_nested_collection_is_unwindable_only_after_its_parent()
    {
        Refused(await Send(Corpus.Shipment, $$"""[ { "unwind": { "path": "billingLines.references" } }, {{FullPage}} ]"""), "UNWIND_ORDER", "billingLines.references", 0);

        var expected = Corpus.Rows(Corpus.Shipment)
            .SelectMany(row => Corpus.Elements(row, "billingLines").OfType<BsonDocument>()
                .SelectMany(line => line.GetValue("References", new BsonArray()).AsBsonArray.Select(_ => row.Id))).ToList();
        expected.Should().HaveCount(3);

        var ordered = await Send(Corpus.Shipment, $$"""[ { "unwind": { "path": "billingLines" } }, { "unwind": { "path": "billingLines.references" } }, { "project": { "id": 1 } }, {{FullPage}} ]""");
        ordered.ShouldHaveIds(expected);
    }

    [Fact]
    public async Task H21_a_collection_root_is_not_filterable_with_eq_but_is_with_exists()
    {
        Refused(await Match("""{ "tags": { "eq": "x" } }"""), "NOT_FILTERABLE", "tags");

        // items is absent on exactly one row, so exists is a real filter and not a tautology.
        var expected = Corpus.IdsWhere(Corpus.Shipment, row => !Corpus.Missing(row, "items"));
        expected.Should().HaveCount(Corpus.Counts(Corpus.Shipment).A - 1);
        (await Match("""{ "items": { "exists": true } }""")).ShouldHaveIds(expected);
    }

    [Fact]
    public async Task H22_or_and_not_over_collection_paths_are_not_refused_and_mean_what_the_element_semantics_say()
    {
        var either = ShipmentsWhere(items => items.Any(item => item.Status == "Open") || items.Any(item => item.Quantity > 4));
        either.Should().NotBeEmpty().And.HaveCountLessThan(Corpus.Counts(Corpus.Shipment).A);
        (await Match("""{ "or": [ { "items.status.name": { "eq": "Open" } }, { "items.quantity.value": { "gt": 4 } } ] }""")).ShouldHaveIds(either);

        // not over a collection path: no element has the value, which is not "some element differs".
        var none = ShipmentsWhere(items => !items.Any(item => item.Status == "Open"));
        none.Should().Contain(Corpus.IdOf(Corpus.Shipment, "items-none")).And.Contain(Corpus.IdOf(Corpus.Shipment, "items-missing"));
        (await Match("""{ "not": { "items.status.name": { "eq": "Open" } } }""")).ShouldHaveIds(none);
    }

    [Theory]
    [InlineData(Corpus.Shipment, "tags.name", "asc")]
    [InlineData(Corpus.Transaction, "states", "asc")]
    [InlineData(Corpus.Transaction, "states", "desc")]
    public async Task H23_a_collection_path_is_unsortable_on_an_object_collection_and_on_a_collection_of_scalars(string entity, string path, string direction)
    {
        Refused(await Send(entity, $$"""[ { "sort": [ { "{{path}}": "{{direction}}" } ] }, { "page": { "limit": 5 } } ]"""), "NOT_SORTABLE", path, 0);
    }
}
