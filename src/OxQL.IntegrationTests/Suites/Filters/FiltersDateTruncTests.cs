using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Group;
using OxQL.Mongo;
using Xunit;
using static OxQL.IntegrationTests.Suites.Filters.FilterKit;

namespace OxQL.IntegrationTests.Suites.Filters;

/// <summary>
/// The bucket test <c>{ path: { "dateTrunc": { unit, timezone?, weekStart?, eq } } }</c> on the fleet
/// (DG2): the rows it matches are the rows of the group whose key is <c>eq</c>, for every unit, in
/// UTC and in a zone with daylight saving, on a date-time and on a date without a time. So a grid
/// that groups by a bucket and opens one group asks for exactly that group's rows, and the counts
/// of the groups add up to the rows. The expected rows are the corpus's, bucketed by the suites' own
/// oracle (<see cref="Truncation"/>), never read off the engine.
/// </summary>
[Trait("Category", "Integration")]
public class FiltersDateTruncTests
{
    private const string Berlin = "Europe/Berlin";

    private static string Operand(string unit, string? timezone, string? weekStart, string? eq)
    {
        var node = new JsonObject { ["unit"] = unit };

        if (timezone is not null)
            node["timezone"] = timezone;

        if (weekStart is not null)
            node["weekStart"] = weekStart;

        node["eq"] = eq;

        return node.ToJsonString();
    }

    private static IReadOnlyList<Guid> Sorted(IEnumerable<CorpusRow> rows) => rows.Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).Select(row => row.Id).ToList();

    public static TheoryData<string, string?, string?> Buckets()
    {
        var data = new TheoryData<string, string?, string?>();

        foreach (var unit in Truncation.Units)
        {
            data.Add(unit, null, null);
            data.Add(unit, Berlin, null);
        }

        data.Add("week", Berlin, "sunday");
        data.Add("week", Berlin, "thu");
        data.Add("day", "Pacific/Chatham", null);
        data.Add("hour", "Asia/Kolkata", null);

        return data;
    }

    [Theory]
    [MemberData(nameof(Buckets))]
    public async Task The_rows_of_a_bucket_are_the_rows_of_the_group_with_that_key_and_the_groups_add_up_to_the_rows(string unit, string? timezone, string? weekStart)
    {
        var client = await TransportClient();
        var rows = Corpus.Rows(Corpus.Shipment);
        var zone = timezone ?? "UTC";
        var first = weekStart switch { "thu" => "thursday", null => "monday", _ => weekStart };

        // Level: the groups, as the engine answers them.
        var by = new JsonObject { ["path"] = "loadStart", ["unit"] = unit };

        if (timezone is not null)
            by["timezone"] = timezone;

        if (weekStart is not null)
            by["weekStart"] = weekStart;

        var level = await client.SendAsync(Corpus.Shipment, $$"""
            [ { "group": { "by": [{ "dateTrunc": {{by.ToJsonString()}}, "as": "bucket" }], "fields": { "count": { "count": true } } } },
              { "sort": [{ "bucket": "asc" }] }, { "page": { "limit": 500, "count": true } } ]
            """);

        level.ShouldBeOk();
        level.HasNextPage.Should().BeFalse();
        level.Items.Should().NotBeEmpty();
        level.TotalCount.Should().Be(level.Items.Count, "the count of a level request counts groups");

        var total = 0;

        // Leaves: the group's key, sent back as it was answered, matches the group's rows and no other.
        foreach (var group in level.Items)
        {
            var key = group!["bucket"]!.GetValue<string>();
            var expected = Sorted(rows.Where(row => Truncation.Truncate(Corpus.Date(row, "loadStart")!.Value, unit, zone, first) == key));
            var leaves = await client.Ids(Corpus.Shipment, $$"""{ "loadStart": { "dateTrunc": {{Operand(unit, timezone, weekStart, key)}} } }""");

            leaves.Should().Equal(expected, $"{unit} {zone} {weekStart} bucket {key}");
            leaves.Should().HaveCount((int)GroupKit.Number(group["count"])!.Value, $"the group's count is its rows: {key}");
            total += leaves.Count;
        }

        total.Should().Be(rows.Count, "every row is in exactly one bucket");
    }

    [Fact]
    public async Task Any_instant_of_the_bucket_and_a_variable_name_it_and_a_bucket_nothing_lies_in_matches_nothing()
    {
        var client = await TransportClient();
        var row = Corpus.Row(Corpus.Shipment, "dst-spring-after");
        var at = Corpus.Date(row, "loadStart")!.Value;
        var key = Truncation.Truncate(at, "month", Berlin);
        var expected = Sorted(Corpus.Rows(Corpus.Shipment).Where(candidate => Truncation.Truncate(Corpus.Date(candidate, "loadStart")!.Value, "month", Berlin) == key));

        expected.Should().Contain(row.Id);

        (await client.Ids(Corpus.Shipment, $$"""{ "loadStart": { "dateTrunc": {{Operand("month", Berlin, null, at.ToString("O", CultureInfo.InvariantCulture))}} } }""")).Should().Equal(expected);
        (await client.Ids(Corpus.Shipment, $$"""{ "loadStart": { "dateTrunc": {{Operand("month", Berlin, null, at.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm:ss'+02:00'", CultureInfo.InvariantCulture))}} } }""")).Should().Equal(expected);

        var byVariable = await client.SendAsync(Corpus.Shipment,
            $$"""[{ "match": { "loadStart": { "dateTrunc": { "unit": "month", "timezone": "{{Berlin}}", "eq": { "$var": "at" } } } } }, { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500 } }]""",
            new { at = key });

        byVariable.ShouldHaveIds(expected);

        (await client.Ids(Corpus.Shipment, $$"""{ "loadStart": { "dateTrunc": {{Operand("year", Berlin, null, "1999-06-01T00:00:00Z")}} } }""")).Should().BeEmpty();
    }

    [Fact]
    public async Task It_composes_with_not_and_or_and_the_complement_of_a_bucket_is_every_other_row()
    {
        var client = await TransportClient();
        var rows = Corpus.Rows(Corpus.Shipment);
        var months = Truncation.Buckets(rows.Select(row => Corpus.Date(row, "loadStart")!.Value), "month", Berlin);
        var (first, second) = (months[0].Key, months[^1].Key);

        bool In(CorpusRow row, string key) => Truncation.Truncate(Corpus.Date(row, "loadStart")!.Value, "month", Berlin) == key;

        (await client.Ids(Corpus.Shipment, $$"""{ "not": { "loadStart": { "dateTrunc": {{Operand("month", Berlin, null, first)}} } } }"""))
            .Should().Equal(Sorted(rows.Where(row => !In(row, first))));
        (await client.Ids(Corpus.Shipment, $$"""{ "or": [ { "loadStart": { "dateTrunc": {{Operand("month", Berlin, null, first)}} } }, { "loadStart": { "dateTrunc": {{Operand("month", Berlin, null, second)}} } } ] }"""))
            .Should().Equal(Sorted(rows.Where(row => In(row, first) || In(row, second))));
    }

    // ---- a date without a time, and no date at all ---------------------------------------------

    [Theory]
    [InlineData("month", Berlin)]
    [InlineData("day", Berlin)]
    [InlineData("day", "America/New_York")]
    [InlineData("week", "America/New_York")]
    [InlineData("year", null)]
    [InlineData("hour", Berlin)]
    public async Task On_a_date_without_a_time_the_groups_key_and_the_day_itself_both_name_the_groups_rows(string unit, string? timezone)
    {
        var client = await ConformanceClient();
        var zone = timezone is null ? "" : $", \"timezone\": \"{timezone}\"";
        var level = await client.SendAsync(Corpus.Conformance, $$"""
            [ { "group": { "by": [{ "dateTrunc": { "path": "day", "unit": "{{unit}}"{{zone}} }, "as": "bucket" }], "fields": { "count": { "count": true }, "days": { "push": "day" } } } },
              { "sort": [{ "bucket": "asc" }] }, { "page": { "limit": 100 } } ]
            """);

        level.ShouldBeOk();
        level.Items.Should().NotBeEmpty();

        var seen = 0;

        foreach (var group in level.Items)
        {
            var key = group!["bucket"]!.GetValue<string>();
            var days = group["days"]!.AsArray().Select(day => day!.GetValue<string>()).ToList();
            var expected = Sorted(Corpus.Rows(Corpus.Conformance).Where(row => Corpus.ValueAt(row, "day") is BsonDateTime stored && days.Contains(stored.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));

            expected.Should().HaveCount((int)GroupKit.Number(group["count"])!.Value);

            (await client.Ids(Corpus.Conformance, $$"""{ "day": { "dateTrunc": {{Operand(unit, timezone, null, key)}} } }""")).Should().Equal(expected, $"{unit} {timezone}: the key {key}");
            (await client.Ids(Corpus.Conformance, $$"""{ "day": { "dateTrunc": {{Operand(unit, timezone, null, days[0])}} } }""")).Should().Equal(expected, $"{unit} {timezone}: the day {days[0]}");
            seen += expected.Count;
        }

        seen.Should().Be(Corpus.Rows(Corpus.Conformance).Count);
    }

    [Theory]
    [InlineData("optionalMoment")]
    [InlineData("optionalDay")]
    public async Task Null_names_the_group_of_the_rows_without_a_date_absent_or_null_and_no_bucket_holds_them(string member)
    {
        var client = await ConformanceClient();
        var rows = Corpus.Rows(Corpus.Conformance);
        var without = Sorted(rows.Where(row => !Corpus.Present(row, member)));

        without.Should().NotBeEmpty("the corpus holds a row without the member");

        var level = await client.SendAsync(Corpus.Conformance, $$"""
            [ { "group": { "by": [{ "dateTrunc": { "path": "{{member}}", "unit": "year" }, "as": "bucket" }], "fields": { "count": { "count": true } } } },
              { "sort": [{ "bucket": "asc" }] }, { "page": { "limit": 100 } } ]
            """);

        level.ShouldBeOk();
        level.Items.Should().Contain(group => group!["bucket"] == null, "null and absent are one group, keyed null");
        GroupKit.Number(level.Items.Single(group => group!["bucket"] == null)!["count"]).Should().Be(without.Count);

        (await client.Ids(Corpus.Conformance, $$"""{ "{{member}}": { "dateTrunc": { "unit": "year", "eq": null } } }""")).Should().Equal(without);

        var inSome = new List<Guid>();

        foreach (var group in level.Items.Where(group => group!["bucket"] is not null))
            inSome.AddRange(await client.Ids(Corpus.Conformance, $$"""{ "{{member}}": { "dateTrunc": {{Operand("year", null, null, group!["bucket"]!.GetValue<string>())}} } }"""));

        inSome.Should().BeEquivalentTo(Sorted(rows.Where(row => Corpus.Present(row, member))));
        inSome.Should().NotIntersectWith(without);
    }

    // ---- the request's filter, collections, joins ----------------------------------------------

    [Fact]
    public async Task In_the_requests_filter_it_narrows_the_rows_that_feed_a_group_and_on_the_groups_key_the_groups()
    {
        var client = await TransportClient();
        var rows = Corpus.Rows(Corpus.Shipment);
        var month = Truncation.Buckets(rows.Select(row => Corpus.Date(row, "loadStart")!.Value), "month", Berlin).MaxBy(bucket => bucket.Count).Key;
        var inMonth = rows.Where(row => Truncation.Truncate(Corpus.Date(row, "loadStart")!.Value, "month", Berlin) == month).ToList();
        var days = Truncation.Buckets(inMonth.Select(row => Corpus.Date(row, "loadStart")!.Value), "day", Berlin);

        // Level 1 under the month: its days. The parent's key is a filter entry on the member; the engine runs it before the group.
        var request = JsonNode.Parse($$"""
            { "entityType": "{{Corpus.Shipment}}",
              "filter": [ { "id": "group:0", "where": { "loadStart": { "dateTrunc": {{Operand("month", Berlin, null, month)}} } } } ],
              "pipeline": [ { "group": { "by": [{ "dateTrunc": { "path": "loadStart", "unit": "day", "timezone": "{{Berlin}}" }, "as": "day" }], "fields": { "count": { "count": true } } } },
                            { "sort": [{ "day": "asc" }] }, { "page": { "limit": 100, "count": true } } ] }
            """)!;
        var level = await client.QueryAsync(request);

        level.ShouldBeOk();
        level.Items.Select(item => (item!["day"]!.GetValue<string>(), (int)GroupKit.Number(item["count"])!.Value)).Should().Equal(days);
        level.TotalCount.Should().Be(days.Count);

        var explain = await client.ExplainHereAsync(new JsonObject { ["query"] = request.DeepClone(), ["include"] = new JsonArray("plan", "notes") });

        explain.Body!["valid"]!.GetValue<bool>().Should().BeTrue(explain.ToString());
        explain.Body["executable"]!.GetValue<string>().Should().Be("yes");
        explain.Body["filter"]![0]!["placed"]!.ToJsonString().Should().Be("""[{"before":0,"paths":["loadStart"]}]""");

        // What runs is a range on the stored date in the first $match, before the $group; no expression.
        var stages = explain.Body["plan"]!["stages"]!.AsArray();
        var match = stages.First(stage => stage!["$match"]?["LoadStart"] is not null)!;

        match["$match"]!["LoadStart"]!.AsObject().Select(pair => pair.Key).Should().Equal("$gte", "$lt");
        stages.ToJsonString().IndexOf("\"LoadStart\":{\"$gte\"", StringComparison.Ordinal).Should().BeLessThan(stages.ToJsonString().IndexOf("\"$group\"", StringComparison.Ordinal));
        stages.ToJsonString().Should().NotContain("$expr");

        // On the group's key the same operator is a condition on the groups (HAVING).
        var having = JsonNode.Parse($$"""
            { "entityType": "{{Corpus.Shipment}}",
              "filter": [ { "id": "having", "where": { "day": { "dateTrunc": {{Operand("month", Berlin, null, month)}} } } } ],
              "pipeline": [ { "group": { "by": [{ "dateTrunc": { "path": "loadStart", "unit": "day", "timezone": "{{Berlin}}" }, "as": "day" }], "fields": { "count": { "count": true } } } },
                            { "sort": [{ "day": "asc" }] }, { "page": { "limit": 100, "count": true } } ] }
            """)!;
        var groups = await client.QueryAsync(having);

        groups.ShouldBeOk();
        groups.Items.Select(item => (item!["day"]!.GetValue<string>(), (int)GroupKit.Number(item["count"])!.Value)).Should().Equal(days);

        var placed = (await client.ExplainHereAsync(new JsonObject { ["query"] = having.DeepClone() })).Body!["filter"]![0]!["placed"]!;

        placed.ToJsonString().Should().Be("""[{"after":0,"paths":["day"],"having":true}]""");
    }

    [Fact]
    public async Task Under_a_collection_it_is_refused_and_inside_any_and_after_the_unwind_it_is_one_elements()
    {
        var client = await TransportClient();
        var rows = Corpus.Rows(Corpus.Shipment);

        // The dates of every billing line, with the shipment that holds them.
        var lines = rows.SelectMany(row => Corpus.Elements(row, "billingLines").OfType<BsonDocument>()
            .Select(line => (Row: row, Date: line.TryGetValue("Date", out var date) && date is BsonDateTime stored ? stored.ToUniversalTime() : (DateTime?)null))).ToList();
        var dated = lines.Where(line => line.Date is not null).ToList();

        dated.Should().NotBeEmpty("the corpus dates billing lines");

        var key = Truncation.Truncate(dated[0].Date!.Value, "day", Berlin);
        var operand = Operand("day", Berlin, null, key);

        var refused = await client.MatchRawAsync(Corpus.Shipment, $$"""{ "billingLines.date": { "dateTrunc": {{operand}} } }""");

        refused.ShouldRefuse("INVALID_OPERAND", 400)["params"]!.ToJsonString().Should().Be("""{"op":"dateTrunc","reason":"underCollection"}""");

        var holders = Sorted(dated.Where(line => Truncation.Truncate(line.Date!.Value, "day", Berlin) == key).Select(line => line.Row).Distinct());

        (await client.Ids(Corpus.Shipment, $$"""{ "billingLines": { "any": { "date": { "dateTrunc": {{operand}} } } } }""")).Should().Equal(holders);

        var unwound = await client.SendAsync(Corpus.Shipment, $$"""
            [ { "unwind": { "path": "billingLines", "as": "line", "includeIndex": "at" } },
              { "match": { "line.date": { "dateTrunc": {{operand}} } } },
              { "project": { "id": 1, "at": 1 } }, { "sort": [{ "id": "asc" }, { "at": "asc" }] }, { "page": { "limit": 500, "count": true } } ]
            """);

        unwound.ShouldBeOk();
        unwound.Items.Should().HaveCount(dated.Count(line => Truncation.Truncate(line.Date!.Value, "day", Berlin) == key));
        unwound.Ids().Distinct().Should().BeEquivalentTo(holders);
    }

    [Fact]
    public async Task On_another_services_member_the_owner_binds_it_and_the_rows_are_those_of_the_range_written_out()
    {
        var client = await TransportClient();
        var vehicles = Corpus.Rows(Corpus.Vehicle);
        var month = Truncation.Truncate(Corpus.Date(vehicles[0], "createDateTime")!.Value, "month", Berlin);
        var inMonth = vehicles.Where(vehicle => Corpus.Date(vehicle, "createDateTime") is { } created && Truncation.Truncate(created, "month", Berlin) == month).Select(vehicle => vehicle.Id).ToHashSet();
        var expected = Sorted(Corpus.Rows(Corpus.Template).Where(row => Corpus.GuidAt(row, "createUserId") is { } key && inMonth.Contains(key)));

        expected.Should().NotBeEmpty("templates name vehicles of that month");

        string Pipeline(string condition) => $$"""
            [ { "resolve": { "path": "createUserId", "as": "veh" } }, { "match": {{condition}} },
              { "project": { "id": 1 } }, { "sort": [{ "id": "asc" }] }, { "page": { "limit": 500, "count": true } } ]
            """;

        var bucket = await client.SendAsync(Corpus.Template, Pipeline($$"""{ "veh.createDateTime": { "dateTrunc": { "unit": "month", "timezone": "{{Berlin}}", "eq": { "$var": "month" } } } }"""), new { month });

        bucket.ShouldHaveIds(expected);

        var other = await client.SendAsync(Corpus.Template, Pipeline($$"""{ "veh.createDateTime": { "dateTrunc": {{Operand("month", Berlin, null, "1999-06-01T00:00:00Z")}} } }"""));

        other.ShouldBeOk();
        other.Items.Should().BeEmpty();

        // The owner refuses what it would refuse of its own caller: the operator on a member that is no date.
        var wrong = await client.SendAsync(Corpus.Template, Pipeline($$"""{ "veh.matchCode": { "dateTrunc": {{Operand("month", Berlin, null, month)}} } }"""));

        wrong.StatusCode.Should().BeOneOf([400, 422], wrong.ToString());
        wrong.ToString().Should().Contain("dateTrunc");
    }

    // ---- refusals, contract 1, explain ---------------------------------------------------------

    [Theory]
    [InlineData("shipmentNumber", """{ "unit": "day", "eq": "2026-03-05T00:00:00Z" }""", "INVALID_OPERAND", "'dateTrunc' does not apply to a string.")]
    [InlineData("loadStart", """{ "unit": "fortnight", "eq": "2026-03-05T00:00:00Z" }""", "INVALID_DATE_TRUNC_UNIT", "'fortnight' is not a unit; one of year, quarter, month, week, day, hour, minute, second.")]
    [InlineData("loadStart", """{ "unit": "week", "weekStart": "funday", "eq": "2026-03-05T00:00:00Z" }""", "INVALID_DATE_TRUNC_UNIT", "'funday' is not a day of the week.")]
    [InlineData("loadStart", """{ "unit": "day", "timezone": "CET", "eq": "2026-03-05T00:00:00Z" }""", "INVALID_TIMEZONE", "'CET' is not an IANA timezone.")]
    [InlineData("loadStart", """{ "unit": "day" }""", "INVALID_OPERAND", "'dateTrunc' needs 'eq': the instant whose bucket the rows are matched by, or null for the rows without a date.")]
    [InlineData("loadStart", """{ "unit": "day", "eq": "2026-03-05" }""", "INVALID_OPERAND", "The 'eq' of the dateTrunc on 'loadStart' expects an ISO-8601 date-time with 'Z' or an offset, or null.")]
    [InlineData("loadStart", "\"2026-03-05T00:00:00Z\"", "INVALID_OPERAND", "'dateTrunc' takes an object of unit, timezone, weekStart and eq.")]
    public async Task What_it_cannot_bind_is_refused_with_one_error_at_the_stage(string path, string operand, string code, string message)
    {
        var answer = await (await TransportClient()).MatchRawAsync(Corpus.Shipment, $$"""{ "{{path}}": { "dateTrunc": {{operand}} } }""");

        answer.ShouldRefuseExactly(code).Should().Be(message);
        answer.Errors[0]["stage"]!.GetValue<int>().Should().Be(0);
        answer.Errors[0]["path"]!.GetValue<string>().Should().Be(path);
    }

    [Fact]
    public async Task Contract_1_does_not_have_the_operator()
    {
        var answer = await (await TransportClient()).Contract(1).MatchRawAsync(Corpus.Shipment, """{ "loadStart": { "dateTrunc": { "unit": "day", "eq": "2026-03-05T00:00:00Z" } } }""");

        answer.ShouldRefuseExactly("UNKNOWN_OPERATOR").Should().Be("'dateTrunc' is not an operator.");
    }

    [Fact]
    public async Task Explain_lists_it_for_a_date_column_and_says_a_refusal_with_its_basis()
    {
        var client = await TransportClient();
        var answer = await client.ExplainHereAsync(new JsonObject { ["query"] = Json.Request(Corpus.Shipment, """[{ "project": { "id": 1, "loadStart": 1, "shipmentNumber": 1 } }]""") });
        var columns = answer.Body!["result"]!["columns"]!.AsArray();

        JsonArray Filter(string path) => answer.Body["result"]!["capabilities"]![columns.Single(column => column!["path"]!.GetValue<string>() == path)!["can"]!.GetValue<string>()]!["can"]!["filter"]!.AsArray();

        Filter("loadStart").ToJsonString().Should().Be("""["eq","neq","gt","gte","lt","lte","in","nin","exists","dateTrunc"]""");
        Filter("shipmentNumber").Select(op => op!.GetValue<string>()).Should().NotContain("dateTrunc");

        foreach (var (condition, code, basis) in new[]
        {
            ("""{ "shipmentNumber": { "dateTrunc": { "unit": "day", "eq": "2026-03-05T00:00:00Z" } } }""", "INVALID_OPERAND", "model"),
            ("""{ "loadStart": { "dateTrunc": { "unit": "fortnight", "eq": "2026-03-05T00:00:00Z" } } }""", "INVALID_DATE_TRUNC_UNIT", "request"),
            ("""{ "loadStart": { "dateTrunc": { "unit": "day", "timezone": "Mars/Olympus", "eq": "2026-03-05T00:00:00Z" } } }""", "INVALID_TIMEZONE", "request"),
            ("""{ "loadStart": { "dateTrunc": { "unit": "day", "eq": { "$var": "at" } } } }""", "UNBOUND_VARIABLE", "request"),
        })
        {
            var refused = await client.ExplainHereAsync(new JsonObject { ["query"] = Json.Request(Corpus.Shipment, $$"""[{ "match": {{condition}} }]""") });

            refused.Body!["valid"]!.GetValue<bool>().Should().BeFalse(condition);
            refused.Body["executable"]!.GetValue<string>().Should().Be("never");
            refused.Body["errors"]![0]!["code"]!.GetValue<string>().Should().Be(code);
            refused.Body["errors"]![0]!["basis"]!.GetValue<string>().Should().Be(basis);
            refused.Body["errors"]![0]!["stage"]!.GetValue<int>().Should().Be(0);
        }
    }

    // ---- an index bounds it --------------------------------------------------------------------

    [Fact]
    public async Task An_index_on_the_date_bounds_the_bucket_to_its_range_where_the_expression_would_read_every_row()
    {
        await using var owned = await MongoFixture.CreateDatabaseAsync("date_trunc_index");
        var direct = new EngineDirect(LabService.Transport.Model);
        var collection = owned.Database.GetCollection<BsonDocument>("shipments");
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await collection.InsertManyAsync(Enumerable.Range(0, 4_000).Select(n => new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["OrganizationId"] = EngineDirect.OrganisationValue,
            ["LoadStart"] = start.AddHours(n * 2),
        }));
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { ["OrganizationId"] = 1, ["LoadStart"] = 1 }));

        var compiled = await direct.CompileAsync(Corpus.Shipment,
            $$"""[{ "match": { "loadStart": { "dateTrunc": { "unit": "month", "timezone": "{{Berlin}}", "eq": "2026-02-28T23:00:00Z" } } } }, { "sort": [{ "loadStart": "asc" }] }, { "page": { "limit": 50 } }]""");
        var rows = await EngineDirect.RunAsync(owned.Database, "shipments", compiled);

        rows.Should().HaveCount(51, "a page reads one row more than it answers, to know whether another follows");
        rows[0]["LoadStart"].ToUniversalTime().Should().Be(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), "the first even hour of March in Berlin");

        var explained = await owned.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            ["explain"] = new BsonDocument { ["aggregate"] = "shipments", ["pipeline"] = new BsonArray(compiled.PageStages), ["cursor"] = new BsonDocument() },
            ["verbosity"] = "executionStats",
        });
        var plan = explained.ToJson();

        plan.Should().Contain("IXSCAN").And.NotContain("COLLSCAN");

        // The bucket is the index's bounds on the date, its start included and its end not.
        var bounds = Find(explained, "indexBounds").OfType<BsonDocument>().Where(document => document.Contains("LoadStart")).Select(document => document["LoadStart"][0].AsString).ToList();
        var from = new DateTimeOffset(2026, 2, 28, 23, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var until = new DateTimeOffset(2026, 3, 31, 22, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        bounds.Should().NotBeEmpty(plan);
        bounds.Should().OnlyContain(bound => bound == $"[new Date({from}), new Date({until}))", "the range is the index's bounds, not a filter over it");

        var inBucket = Enumerable.Range(0, 4_000).Select(n => start.AddHours(n * 2)).Count(at => at >= DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime && at < DateTimeOffset.FromUnixTimeMilliseconds(until).UtcDateTime);

        inBucket.Should().Be(371, "March 2026 in Berlin is 743 hours long");
        Find(explained, "totalKeysExamined").Max(value => value.ToInt64()).Should().BeInRange(inBucket, inBucket + 1, "of 4 000 keys the bucket's are read and no other");
    }

    /// <summary>Every value under <paramref name="name"/> anywhere in a document.</summary>
    private static IEnumerable<BsonValue> Find(BsonValue node, string name) => node switch
    {
        BsonDocument document => document.Elements.SelectMany(element => element.Name == name ? [element.Value] : Find(element.Value, name)),
        BsonArray array => array.SelectMany(item => Find(item, name)),
        _ => [],
    };
}
