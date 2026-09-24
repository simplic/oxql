using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;

namespace OxQL.IntegrationTests.Suites.EngineOnly;

/// <summary>
/// Area O: the request shapes the engine accepts and the typed client cannot build. A
/// hand-written caller, the client's <c>raw</c> escape hatch or a service migrating off v1 reaches
/// all of it, so each case asks whether the form works and does what a caller would assume. An
/// engine-only form that binds and then quietly answers nothing is worse than one that refuses.
/// Ported from the legacy <c>engine-only-o</c> battery; the client half of <c>raw</c> (bytes sent,
/// path checks degraded, stage counting) went to the client's specs.
/// </summary>
[Trait("Category", "Integration")]
public class EngineOnlyOTests
{
    private const string Counted = """{ "page": { "limit": 3, "includeTotalCount": true } }""";

    private static Task<LabClient> Transport() => Lab.ClientAsync(LabService.Transport);

    private static async Task<WireAnswer> Send(string pipeline, string entity = Corpus.Shipment, object? variables = null) =>
        await (await Lab.ClientForAsync(entity)).SendAsync(entity, pipeline, variables);

    [Fact]
    public async Task O06_an_implicit_eq_binds_exactly_as_the_explicit_operator_does()
    {
        var planned = Corpus.IdsOf(Corpus.ShipmentsWithStatus("Planned"));
        planned.Should().HaveCountGreaterThan(3);

        var implicitString = await Send($$"""[ { "match": { "status.name": "Planned" } }, { "sort": [ { "id": "asc" } ] }, {{Counted}} ]""");
        var explicitString = await Send($$"""[ { "match": { "status.name": { "eq": "Planned" } } }, { "sort": [ { "id": "asc" } ] }, {{Counted}} ]""");

        implicitString.ShouldHaveIds(Corpus.PageOf(planned, limit: 3)).ShouldHaveTotal(planned.Count);
        explicitString.ShouldHaveIds(implicitString.Ids()).ShouldHaveTotal(planned.Count);

        // …and on a numeric path, where an implicit form could have been read as an operand object.
        var edge = Corpus.IdsWhere(Corpus.Vehicle, row => Corpus.Number(row, "fuelTankCapacity") == int.MaxValue.ToString());
        edge.Should().Equal([Corpus.IdOf(Corpus.Vehicle, "int-edge")]);
        (await Send($$"""[ { "match": { "fuelTankCapacity": 2147483647 } }, {{Counted}} ]""", Corpus.Vehicle)).ShouldHaveIds(edge).ShouldHaveTotal(1);
    }

    [Fact]
    public async Task O03_O04_a_variable_referenced_only_by_a_hand_written_stage_binds_and_its_value_is_taken_as_written()
    {
        var planned = Corpus.IdsOf(Corpus.ShipmentsWithStatus("Planned"));

        var bound = await Send($$"""[ { "match": { "status.name": { "eq": { "$var": "status" } } } }, { "sort": [ { "id": "asc" } ] }, {{Counted}} ]""", variables: new { status = "Planned" });
        bound.ShouldHaveIds(Corpus.PageOf(planned, limit: 3)).ShouldHaveTotal(planned.Count);

        // A $var carrying a dateTime string is bound at the place of use, as a literal would be.
        var since = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var later = Corpus.Where(Corpus.Shipment, row => Corpus.Date(row, "loadStart") >= since).Count;
        later.Should().BeGreaterThan(0).And.BeLessThan(Corpus.Counts(Corpus.Shipment).A);

        var temporal = await Send($$"""[ { "match": { "loadStart": { "gte": { "$var": "since" } } } }, {{Counted}} ]""", variables: new { since = "2026-06-15T00:00:00Z" });
        temporal.ShouldHaveTotal(later);
    }

    [Fact]
    public async Task O07_the_nested_projection_syntax_produces_the_same_rows_as_the_dotted_form()
    {
        var first = Corpus.Rows(Corpus.Shipment).Take(2).ToList();

        var nested = await Send("""[ { "sort": [ { "id": "asc" } ] }, { "project": { "id": 1, "status": { "name": 1 } } }, { "page": { "limit": 2 } } ]""");
        var dotted = await Send("""[ { "sort": [ { "id": "asc" } ] }, { "project": { "id": 1, "status.name": 1 } }, { "page": { "limit": 2 } } ]""");

        nested.ShouldHaveIds(first.Select(row => row.Id));
        nested.Items.ToJsonString().Should().Be(dotted.Items.ToJsonString());
        nested.Items[0]!.ToJsonString().Should().Be(new JsonObject { ["id"] = first[0].WireId, ["status"] = new JsonObject { ["name"] = Corpus.Text(first[0], "status.name") } }.ToJsonString());
    }

    [Theory]
    [InlineData("""{ "shipmentNumber": 1, "loadStart": 0 }""")]
    [InlineData("""{ "loadStart": 0, "shipmentNumber": 1 }""")]
    public async Task O08_a_genuinely_mixed_projection_is_refused_and_excluding_only_the_key_from_an_inclusion_is_not_mixed(string projection)
    {
        var mixed = await Send($$"""[ { "project": {{projection}} }, { "page": { "limit": 1 } } ]""");
        mixed.ShouldRefuse("MIXED_PROJECTION", 400);
        mixed.ErrorCodes.Should().Equal("MIXED_PROJECTION");

        var keyOut = await Send("""[ { "project": { "id": 0, "shipmentNumber": 1 } }, { "page": { "limit": 1 } } ]""");
        keyOut.ShouldBeOk();
        keyOut.Items[0]!.AsObject().ContainsKey("id").Should().BeFalse();
    }

    [Fact]
    public async Task O09_exists_binds_on_an_object_on_an_array_and_on_a_dictionary_and_each_answers_its_own_row_set()
    {
        var withAddress = Corpus.IdsWhere(Corpus.Shipment, row => !Corpus.Missing(row, "loadAddress"));
        var withoutItems = Corpus.IdsWhere(Corpus.Shipment, row => Corpus.Missing(row, "items"));
        var withBag = Corpus.IdsWhere(Corpus.Shipment, row => !Corpus.Missing(row, "addon"));
        withoutItems.Should().Equal([Corpus.IdOf(Corpus.Shipment, "items-missing")]);
        withBag.Should().HaveCount(Corpus.Counts(Corpus.Shipment).A - 1, "addon-missing is the one row with no bag");

        (await Send($$"""[ { "match": { "loadAddress": { "exists": true } } }, {{Counted}} ]""")).ShouldHaveTotal(withAddress.Count);
        (await Send($$"""[ { "match": { "items": { "exists": false } } }, {{Counted}} ]""")).ShouldHaveIds(withoutItems).ShouldHaveTotal(1);
        (await Send($$"""[ { "match": { "addon": { "exists": true } } }, {{Counted}} ]""")).ShouldHaveTotal(withBag.Count);
    }

    [Fact]
    public async Task O10_several_sort_stages_bind_and_the_last_one_wins_outright()
    {
        // If the first sort survived at all, the page would open on the Closed rows.
        var expected = Corpus.AllIds(Corpus.Shipment).Reverse().Take(4).ToList();
        Corpus.Rows(Corpus.Shipment).TakeLast(4).Select(row => Corpus.Text(row, "status.name")).Should().NotContain("Closed", "the last four rows by id sort after Closed under a status sort");

        var two = await Send("""[ { "sort": [ { "status.name": "asc" } ] }, { "sort": [ { "id": "desc" } ] }, { "project": { "id": 1, "status.name": 1 } }, { "page": { "limit": 4 } } ]""");

        two.ShouldHaveIds(expected, "the first sort left no trace");
    }

    [Theory]
    [InlineData("monday", "monday")]
    [InlineData("mon", "monday")]
    [InlineData("MONDAY", "monday")]
    [InlineData("Mon", "monday")]
    [InlineData("sunday", "sunday")]
    [InlineData("sun", "sunday")]
    public async Task O12_date_trunc_accepts_the_short_weekday_spellings_in_any_case_and_each_moves_the_bucket(string spelling, string weekStart)
    {
        var buckets = Corpus.DateTruncBuckets(Corpus.Shipment, "loadStart", "week", Temporal.Berlin, weekStart).Take(3).Select(bucket => bucket.Bucket).ToList();
        Corpus.DateTruncBuckets(Corpus.Shipment, "loadStart", "week", Temporal.Berlin, "monday")[0].Bucket
            .Should().NotBe(Corpus.DateTruncBuckets(Corpus.Shipment, "loadStart", "week", Temporal.Berlin, "sunday")[0].Bucket, "a different day is a different boundary");

        var answer = await Send($$"""[ { "group": { "by": [ { "dateTrunc": { "path": "loadStart", "unit": "week", "timezone": "Europe/Berlin", "weekStart": "{{spelling}}" }, "as": "w" } ], "fields": { "c": { "count": true } } } }, { "sort": [ { "w": "asc" } ] }, { "page": { "limit": 3 } } ]""");

        answer.ShouldBeOk(spelling);
        answer.Strings("w").Should().Equal(buckets, spelling);
    }

    [Fact]
    public async Task O12b_a_week_start_that_names_no_day_is_refused_under_the_unit_code()
    {
        var answer = await Send("""[ { "group": { "by": [ { "dateTrunc": { "path": "loadStart", "unit": "week", "weekStart": "bogus" }, "as": "w" } ], "fields": { "c": { "count": true } } } }, { "page": { "limit": 1 } } ]""");

        answer.ShouldRefuse("INVALID_DATE_TRUNC_UNIT", 400)["message"]!.GetValue<string>().Should().Contain("is not a day of the week");
    }

    [Fact]
    public async Task O14_a_star_key_under_the_bag_is_a_literal_key_it_matches_nothing_and_projects_an_empty_bag()
    {
        // * is not a wildcard (C17): exists on a key no bag holds matches nothing. The legacy battery
        // recorded this as "binds and silently answers nothing"; under the literal-key rule that
        // is the correct answer, and the whole-member forms beside it are the control.
        var withBag = Corpus.IdsWhere(Corpus.Shipment, row => !Corpus.Missing(row, "addon"));
        Corpus.Rows(Corpus.Shipment).Should().NotContain(row => Corpus.Present(row, "addon.*"));

        var star = await Send($$"""[ { "match": { "addon.*": { "exists": true } } }, {{Counted}} ]""");
        star.ShouldHaveTotal(0).ShouldHaveNoDiagnostics();
        (await Send($$"""[ { "match": { "addon": { "exists": true } } }, {{Counted}} ]""")).ShouldHaveTotal(withBag.Count);

        var first = Corpus.IdsWhere(Corpus.Shipment, row => Corpus.Present(row, "addon.shiftModel"))[0];
        var starProject = await Send($$"""[ { "match": { "id": { "eq": "{{first}}" } } }, { "project": { "id": 1, "addon.*": 1 } }, { "page": { "limit": 1 } } ]""");
        var wholeProject = await Send($$"""[ { "match": { "id": { "eq": "{{first}}" } } }, { "project": { "id": 1, "addon": 1 } }, { "page": { "limit": 1 } } ]""");

        starProject.Items[0]!["addon"]!.ToJsonString().Should().Be("{}", "the bag holds no key named *");
        wholeProject.Items[0]!["addon"]!.AsObject().Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task O15_count_false_binds_as_a_count_exactly_as_count_true_does()
    {
        var expected = Corpus.Rows(Corpus.Shipment).GroupBy(row => Corpus.Text(row, "status.name")!)
            .OrderBy(group => group.Key, Comparer<string>.Create(Order.CompareCollated))
            .Select(group => new JsonObject { ["s"] = group.Key, ["c"] = group.Count().ToString() }).ToList();

        var falsey = await Send("""[ { "group": { "by": [ { "path": "status.name", "as": "s" } ], "fields": { "c": { "count": false } } } }, { "sort": [ { "s": "asc" } ] }, { "page": { "limit": 10 } } ]""");
        var truthy = await Send("""[ { "group": { "by": [ { "path": "status.name", "as": "s" } ], "fields": { "c": { "count": true } } } }, { "sort": [ { "s": "asc" } ] }, { "page": { "limit": 10 } } ]""");

        falsey.ShouldBeOk();
        falsey.Items.ToJsonString().Should().Be(new JsonArray(expected.ToArray<JsonNode?>()).ToJsonString());
        truthy.Items.ToJsonString().Should().Be(falsey.Items.ToJsonString());
    }

    [Fact]
    public async Task O18_an_empty_match_is_a_no_op_stage()
    {
        (await Send($$"""[ { "match": {} }, {{Counted}} ]""")).ShouldHaveTotal(Corpus.Counts(Corpus.Shipment).A);
    }

    [Fact]
    public async Task O19_a_match_before_and_after_a_group_binds_the_first_against_rows_and_the_second_against_aliases()
    {
        var planned = Corpus.ShipmentsWithStatus("Planned").Count;

        var answer = await Send("""
            [ { "match": { "status.name": { "eq": "Planned" } } },
              { "group": { "by": [ { "path": "status.name", "as": "s" } ], "fields": { "c": { "count": true } } } },
              { "match": { "s": { "eq": "Planned" } } },
              { "page": { "limit": 10 } } ]
            """);

        answer.ShouldBeOk();
        answer.Items.ToJsonString().Should().Be(new JsonArray(new JsonObject { ["s"] = "Planned", ["c"] = planned.ToString() }).ToJsonString());

        // The post-group match addresses the alias, not the source path.
        var wrong = await Send("""[ { "group": { "by": [ { "path": "status.name", "as": "s" } ], "fields": { "c": { "count": true } } } }, { "match": { "status.name": { "eq": "Planned" } } }, { "page": { "limit": 10 } } ]""");
        wrong.ShouldRefuse("UNKNOWN_PATH", 400)["stage"]!.GetValue<int>().Should().Be(1);
        wrong.ErrorCodes.Should().Equal("UNKNOWN_PATH");
    }

    [Fact]
    public async Task O20_explain_answers_404_while_disabled_and_200_on_a_host_that_enables_it()
    {
        var body = Json.Request(Corpus.Shipment, """[ { "page": { "limit": 1 } } ]""");
        var transport = await Transport();

        (await transport.ExplainHereAsync(body)).StatusCode.Should().Be(404, "Explain:Enabled is off by default");

        var enabled = await transport.ExplainAsync(body);
        enabled.StatusCode.Should().Be(200, enabled.ToString());
        enabled.Body!["bound"]!["entity"]!.GetValue<string>().Should().Be(Corpus.Shipment);
    }

    [Fact]
    public async Task O21_the_batch_endpoint_accepts_max_time_ms_and_answers_each_entry_on_its_own()
    {
        var all = Corpus.AllIds(Corpus.Shipment);

        var answer = await (await Transport()).BatchAsync(
        [
            Json.Request(Corpus.Shipment, """[ { "page": { "limit": 2, "includeTotalCount": true } } ]"""),
            Json.Request(Corpus.Shipment, """[ { "match": { "nope": { "eq": 1 } } }, { "page": { "limit": 1 } } ]"""),
        ], maxTimeMs: 5000);

        answer.StatusCode.Should().Be(200, "a batch answers 200 even when an entry was refused: " + answer);
        answer.Results.Should().HaveCount(2);
        answer.Results[0].ShouldHaveIds(Corpus.PageOf(all, limit: 2)).ShouldHaveTotal(all.Count);
        answer.Results[1].ShouldRefuse("UNKNOWN_PATH");
        answer.Results[1].ErrorCodes.Should().Equal("UNKNOWN_PATH");
    }

    [Fact]
    public async Task O21b_two_good_batch_entries_both_answer_their_page()
    {
        var all = Corpus.AllIds(Corpus.Shipment);

        var answer = await (await Transport()).BatchAsync(
        [
            Json.Request(Corpus.Shipment, """[ { "page": { "limit": 2, "includeTotalCount": true } } ]"""),
            Json.Request(Corpus.Shipment, """[ { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 1 } } ]"""),
        ]);

        answer.StatusCode.Should().Be(200, answer.ToString());
        answer.Results[0].ShouldHaveIds(Corpus.PageOf(all, limit: 2)).ShouldHaveTotal(all.Count);
        answer.Results[1].ShouldHaveIds(Corpus.PageOf(all, limit: 1));
    }

    [Theory]
    [InlineData("two keys", """{ "match": {}, "sort": [ { "id": "asc" } ] }""")]
    [InlineData("no key", "{}")]
    [InlineData("unknown key filter", """{ "filter": { "id": { "eq": 1 } } }""")]
    [InlineData("unknown key limitTo", """{ "limitTo": 5 }""")]
    public async Task O22_O23_O24_a_stage_with_two_keys_no_key_or_an_unknown_key_is_an_unknown_stage_naming_the_eight(string label, string stage)
    {
        var answer = await Send($$"""[ {{stage}}, { "page": { "limit": 1 } } ]""");

        answer.ShouldRefuse("UNKNOWN_STAGE", 400, label)["stage"]!.GetValue<int>().Should().Be(0);
        answer.ErrorCodes.Should().Equal(["UNKNOWN_STAGE"], label);

        if (label.StartsWith("unknown key", StringComparison.Ordinal))
            answer.Errors[0]["message"]!.GetValue<string>().Should().Contain("match, lookup, resolve, unwind, group, project, sort, page", label);
    }

    [Fact]
    public async Task O25_a_null_pipeline_element_is_a_coded_refusal_and_a_non_object_element_is_a_ProblemDetails_400()
    {
        var notAnObject = await Send("""[ "match", { "page": { "limit": 1 } } ]""");
        notAnObject.StatusCode.Should().Be(400, notAnObject.ToString());
        notAnObject.IsProblemDetails.Should().BeTrue("the converter rejects a non-object before the body is a request: " + notAnObject);
        notAnObject.ErrorCodes.Should().BeEmpty();

        // Legacy R5 (closed): a null element used to fall out of the host as an unhandled 500.
        var nullElement = await Send("""[ null, { "page": { "limit": 1 } } ]""");
        nullElement.Type.Should().Be("validation_error", nullElement.ToString());
        var error = nullElement.ShouldRefuse("UNKNOWN_STAGE", 400);
        nullElement.ErrorCodes.Should().Equal("UNKNOWN_STAGE");
        error["message"]!.GetValue<string>().Should().Be("A stage is an object carrying exactly one stage member; this one is null.");
        error["stage"]!.GetValue<int>().Should().Be(0);
    }

    // ── O13, O16, O17: the engine-only join forms. Legacy: unreachable (no declared reference);
    // the lab model declares references, so each now runs.

    [Fact]
    public async Task O13_a_lookup_select_given_as_a_bare_string_selects_that_one_path()
    {
        var parents = Corpus.Rows(Corpus.Conformance);
        var children = Corpus.Rows(Corpus.ConformanceChild);
        var expected = parents.Select(parent => children.Where(child => Corpus.GuidAt(child, "parentId") == parent.Id)
            .Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).Select(child => (child.WireId, Corpus.Text(child, "name"))).ToList()).ToList();
        expected.Should().Contain(list => list.Count > 0);

        var answer = await Send("""[ { "lookup": { "from": "conformance.child", "path": "parentId", "as": "children", "select": "name" } }, { "project": { "id": 1, "children": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 50 } } ]""", Corpus.Conformance);

        answer.ShouldHaveIds(parents.Select(parent => parent.Id));
        answer.Items.Select(row => row!["children"]!.AsArray().Select(child => (child!["id"]!.GetValue<string>(), child["name"]?.GetValue<string>())).ToList())
            .Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        answer.Items.SelectMany(row => row!["children"]!.AsArray()).Should().OnlyContain(child => child!.AsObject().Select(member => member.Key).Order(StringComparer.Ordinal).SequenceEqual(new[] { "id", "name" }));
    }

    [Fact]
    public async Task O16_an_operand_under_a_remote_alias_is_forwarded_to_the_owner_which_binds_and_refuses_it()
    {
        // The local host cannot type a remote path, so it forwards the operand whole; the owner's
        // refusal comes back wrapped.
        var answer = await Send("""[ { "resolve": { "path": "department.id", "as": "dep" } }, { "match": { "dep.name": { "eq": { "x": 1 } } } }, { "page": { "limit": 5 } } ]""");

        answer.ShouldRefuse("RESOLVE_REFUSED", 422);
        answer.Text.Should().Contain("INVALID_OPERAND", "the owner's error travels inside the wrapper: " + answer);
    }

    [Fact]
    public async Task O17_a_remote_resolve_filter_is_forwarded_whole_and_executed_by_the_owner()
    {
        var departments = Corpus.Rows(Corpus.Department).ToDictionary(row => row.Id);
        var expected = Corpus.Rows(Corpus.Shipment).Select(row => (row.Id,
            Name: Corpus.GuidAt(row, "department.id") is { } id && departments.TryGetValue(id, out var department) && Corpus.Text(department, "name") == "Workshop" ? "Workshop" : null)).ToList();
        expected.Count(entry => entry.Name is not null).Should().Be(1);

        var filtered = await Send("""[ { "resolve": { "path": "department.id", "as": "dep", "filter": { "name": { "eq": "Workshop" } } } }, { "project": { "id": 1, "dep": 1 } }, { "sort": [ { "id": "asc" } ] }, { "page": { "limit": 500 } } ]""");

        filtered.ShouldHaveIds(expected.Select(entry => entry.Id));
        filtered.Strings("dep.name").Should().Equal(expected.Select(entry => entry.Name), "a target that fails the filter resolves to null");

        // A filter the owner cannot bind is the owner's refusal, not a silent null.
        var unknown = await Send("""[ { "resolve": { "path": "department.id", "as": "dep", "filter": { "nope": { "eq": 1 } } } }, { "page": { "limit": 5 } } ]""");
        unknown.StatusCode.Should().BeGreaterThanOrEqualTo(400, unknown.ToString());
        unknown.Text.Should().Contain("UNKNOWN_PATH", unknown.ToString());
    }
}
