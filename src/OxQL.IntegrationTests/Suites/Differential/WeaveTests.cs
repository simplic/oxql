using System.Text.Json.Nodes;
using FluentAssertions;
using OxQL.IntegrationTests.Harness;
using Xunit;
using L = OxQL.IntegrationTests.Fleet.Models.Ledger;
using static OxQL.IntegrationTests.Suites.Differential.WeaveOracle;

namespace OxQL.IntegrationTests.Suites.Differential;

/// <summary>
/// The joins that cross services, over generated rows and against an oracle that follows the same
/// references in memory: a tree flattened into rows, stages continued at the owner of a record a
/// join reached, a union join, references typed by a sibling member and by the variant, references
/// inside a collection, and a report page of more rows than a list page holds. Every query is asked
/// as one page and walked by cursor in pages of several sizes; the rows are the same, and they are
/// the oracle's.
/// </summary>
[Trait("Category", "Integration")]
public class WeaveTests
{
    private const string Entity = WeaveCorpus.Transaction;

    /// <summary>The invoices that nest deeper than a flatten reads: a strict request refuses them, so a report page leaves them out.</summary>
    private static readonly string[] Deep = ["WX-0040", "WX-0041"];

    private static readonly string NotDeep = $$"""{ "match": { "number": { "nin": [{{string.Join(", ", Deep.Select(number => $"\"{number}\""))}}] } } }""";

    private static string Body(string stages, JsonObject? page, bool strict = false, string? onMissing = null)
    {
        var body = new JsonObject { ["entityType"] = Entity };

        if (strict)
            body["strict"] = true;

        if (onMissing is not null)
            body["onMissing"] = onMissing;

        var pipeline = (JsonArray)JsonNode.Parse(stages)!;

        if (page is not null)
            pipeline.Add(new JsonObject { ["page"] = page });

        body["pipeline"] = pipeline;

        return body.ToJsonString();
    }

    /// <summary>The rows of a walk by cursor in pages of <paramref name="limit"/>.</summary>
    private static async Task<List<JsonObject>> WalkAsync(LabClient client, string stages, int limit, string? onMissing = null)
    {
        var rows = new List<JsonObject>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = new JsonObject { ["limit"] = limit };

            if (cursor is not null)
                page["cursor"] = cursor;

            var answer = await client.QueryAsync(Body(stages, page, onMissing: onMissing));

            answer.ShouldBeOk($"page {pages + 1} in pages of {limit}");
            rows.AddRange(answer.Items.OfType<JsonObject>());
            cursor = answer.HasNextPage ? answer.NextCursor : null;

            (++pages).Should().BeLessThan(2_000, "the walk ends");
        }
        while (cursor is not null);

        return rows;
    }

    /// <summary>The rows of the one page a strict request without a cursor may ask for.</summary>
    private static async Task<List<JsonObject>> ReportAsync(LabClient client, string stages, string? onMissing = null)
    {
        var answer = await client.QueryAsync(Body(stages, new JsonObject { ["limit"] = 5_000 }, strict: true, onMissing));

        answer.ShouldBeOk("the report page");
        answer.HasNextPage.Should().BeFalse();

        return [.. answer.Items.OfType<JsonObject>()];
    }

    private static Guid? IdUnder(JsonObject row, string alias) =>
        row[alias] is JsonObject record && record["id"] is JsonValue id ? Guid.Parse(id.GetValue<string>()) : null;

    private static string? Text(JsonNode? node) => node is JsonValue value ? value.GetValue<string>() : null;

    // ── a tree flattened into rows ──────────────────────────────────────────────────────────

    private static readonly (string Name, string Stage, Func<Flat, bool> Holds)[] Filters =
    [
        ("all", "", _ => true),
        ("billing lines", """{ "match": { "item": { "is": "BillingLineTransactionItem" } } }""", flat => flat.Item is L.BillingLineTransactionItem),
        ("groups", """{ "match": { "item": { "is": "GroupTransactionItem" } } }""", flat => flat.Item is L.GroupTransactionItem),
        ("a text", """{ "match": { "item.text": { "eq": "billing 3" } } }""", flat => flat.Item.Text == "billing 3"),
        ("a number of the item", """{ "match": { "item.sortNumber": { "gte": 2 } } }""", flat => flat.Item.SortNumber >= 2),
        ("a position", """{ "match": { "position": { "gt": 3 } } }""", flat => flat.Position > 3),
        ("either", """{ "match": { "or": [{ "item.sortNumber": { "eq": 0 } }, { "item": { "is": "TextTransactionItem" } }] } }""", flat => flat.Item.SortNumber == 0 || flat.Item is L.TextTransactionItem),
    ];

    private static readonly (string Name, string Sort, Comparison<Flat> Order)[] Sorts =
    [
        ("number, position", """[{ "number": "asc" }, { "position": "asc" }]""", (a, b) => Then(Number(a, b), a.Position.CompareTo(b.Position))),
        ("number down, position down", """[{ "number": "desc" }, { "position": "desc" }]""", (a, b) => Then(Number(b, a), b.Position.CompareTo(a.Position))),
        ("a number of the item", """[{ "item.sortNumber": "desc" }, { "number": "asc" }, { "position": "asc" }]""", (a, b) => Then(b.Item.SortNumber.CompareTo(a.Item.SortNumber), Number(a, b), a.Position.CompareTo(b.Position))),
        ("a text of the item", """[{ "item.text": "asc" }, { "number": "desc" }, { "position": "asc" }]""", (a, b) => Then(string.CompareOrdinal(a.Item.Text, b.Item.Text), Number(b, a), a.Position.CompareTo(b.Position))),
        ("position, number", """[{ "position": "desc" }, { "number": "asc" }]""", (a, b) => Then(b.Position.CompareTo(a.Position), Number(a, b))),

        // What the sort leaves open the key and the position close: the invoice's key, then the item's place in it.
        ("the tie-break", """[{ "item.sortNumber": "asc" }]""", (a, b) => Then(a.Item.SortNumber.CompareTo(b.Item.SortNumber), string.CompareOrdinal(a.Transaction.Id.ToString("D"), b.Transaction.Id.ToString("D")), a.Position.CompareTo(b.Position))),
    ];

    private static int Number(Flat a, Flat b) => string.CompareOrdinal(a.Transaction.Number, b.Transaction.Number);

    private static int Then(params int[] comparisons) => comparisons.FirstOrDefault(comparison => comparison != 0);

    public static TheoryData<int, int> Flattened()
    {
        var data = new TheoryData<int, int>();

        for (var filter = 0; filter < Filters.Length; filter++)
            for (var sort = 0; sort < Sorts.Length; sort++)
                data.Add(filter, sort);

        return data;
    }

    private static string Tuple(Flat flat) => $"{flat.Transaction.Number}#{flat.Position} {flat.Item.Id:D}";

    private static string Tuple(JsonObject row) => $"{Text(row["number"])}#{row["position"]!.GetValue<int>()} {IdUnder(row, "item"):D}";

    [Theory]
    [MemberData(nameof(Flattened))]
    public async Task A_flattened_tree_is_every_item_in_pre_order_on_a_report_page_and_on_every_walk(int filter, int sort)
    {
        var client = await WeaveCorpus.LedgerAsync();
        var (name, stage, holds) = Filters[filter];
        var (sorted, order, comparison) = Sorts[sort];
        var what = $"{name}, by {sorted}";

        var stages = $$"""[{{NotDeep}}, { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } }{{(stage.Length > 0 ? ", " + stage : "")}}, { "sort": {{order}} }, { "project": { "number": 1, "position": 1, "item.id": 1 } }]""";

        var expected = FlattenAll().Where(flat => !Deep.Contains(flat.Transaction.Number)).Where(holds).ToList();

        expected.Sort(comparison);

        var tuples = expected.Select(Tuple).ToList();
        var report = await ReportAsync(client, stages);

        report.Select(Tuple).Should().Equal(tuples, $"the report page: {what}");

        foreach (var limit in new[] { 500, 211 })
            (await WalkAsync(client, stages, limit)).Select(Tuple).Should().Equal(tuples, $"pages of {limit}: {what}");
    }

    [Fact]
    public void The_corpus_holds_more_rows_than_a_list_page()
    {
        FlattenAll().Count(flat => !Deep.Contains(flat.Transaction.Number)).Should().BeGreaterThan(500, "a report page above the list page's size is what the flatten cases ask for");
        LineRows(Unambiguous).Count.Should().BeGreaterThan(500, "and so is a report page of the chain");
    }

    [Fact]
    public async Task An_invoice_without_items_is_kept_once_where_the_unwind_preserves_it_and_a_tree_below_the_depth_is_cut_and_said()
    {
        var client = await WeaveCorpus.LedgerAsync();
        const string Stages = """[{ "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position", "preserveNull": true } }, { "sort": [{ "number": "asc" }, { "position": "asc" }] }, { "project": { "number": 1, "position": 1, "item.id": 1 } }]""";

        var expected = WeaveCorpus.Transactions.OrderBy(transaction => transaction.Number, StringComparer.Ordinal)
            .SelectMany(transaction => Flatten(transaction) is { Count: > 0 } rows ? rows.Select(Tuple) : [$"{transaction.Number}# "])
            .ToList();

        string Kept(JsonObject row) => row["position"] is null ? $"{Text(row["number"])}# " : Tuple(row);

        foreach (var limit in new[] { 500, 61 })
            (await WalkAsync(client, Stages, limit)).Select(Kept).Should().Equal(expected, $"pages of {limit}");

        // The invoices that nest deeper say so on the page that holds them, and a strict request refuses.
        var deep = await client.QueryAsync(Body($$"""[{ "match": { "number": { "in": ["{{Deep[0]}}", "{{Deep[1]}}"] } } }, { "unwind": { "path": "items", "flatten": "items", "as": "item" } }]""", new JsonObject { ["limit"] = 100 }));

        deep.ShouldBeOk().Items.Should().HaveCount(2 * FlattenDepth);
        deep.DiagnosticCodes.Should().Contain("UNWIND_DEPTH_TRUNCATED");

        (await client.QueryAsync(Body("""[{ "unwind": { "path": "items", "flatten": "items", "as": "item" } }]""", new JsonObject { ["limit"] = 5_000 }, strict: true)))
            .ShouldRefuse("UNWIND_DEPTH_TRUNCATED", 422);
    }

    // ── the chain: continued stages, a union join, typed references ─────────────────────────

    private static string Chain(string before, bool vehiclesOnly, string project = "", string more = "") => $$"""
        [ {{before}}
          { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
          { "match": { "item": { "is": "BillingLineTransactionItem" } } },
          { "resolve": { "path": "item.billingLineId", "as": "erpLine", "outcomeAs": "erpLineOutcome"{{more}} } },
          { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent", "outcomeAs": "sourceLineOutcome"{{more}} } },
          { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "forTarget": "{{WeaveCorpus.Shipment}}", "elements": "first", "outcomeAs": "deliveringTourOutcome"{{more}} } },
          { "resolve": { "as": "vehicle", "outcomeAs": "vehicleOutcome"{{(vehiclesOnly ? """, "target": "fleet.vehicle" """ : "")}}{{more}},
                         "byTarget": { "{{WeaveCorpus.Shipment}}": "deliveringTour.resource.id", "{{WeaveCorpus.Tour}}": "sourceParent.resource.id" } } },
          { "sort": [ { "number": "asc" }, { "position": "asc" } ] },
          { "project": { "number": 1, "position": 1, "item.id": 1, "erpLine.id": 1, "erpLineOutcome": 1, "sourceLineOutcome": 1, "sourceParent.id": 1,
                         "deliveringTour.id": 1, "deliveringTourOutcome": 1, "vehicle.id": 1, "vehicleOutcome": 1{{project}} } } ]
        """;

    /// <summary>What the oracle says of a row, given the holder the row took where two hold the line; empty when the row is what it says.</summary>
    private static string? Difference(Flat flat, JsonObject row, bool vehiclesOnly)
    {
        var line = Line((L.BillingLineTransactionItem)flat.Item);
        var source = Source(line);
        var parent = IdUnder(row, "sourceParent");
        var differences = new List<string>();

        void Hold(string what, object? expected, object? actual)
        {
            if (!Equals(expected, actual))
                differences.Add($"{what}: expected {expected ?? "(none)"}, got {actual ?? "(none)"}");
        }

        Hold("erpLine", line.Id, IdUnder(row, "erpLine"));
        Hold("erpLineOutcome", line.Outcome, Text(row["erpLineOutcome"]));
        Hold("sourceLineOutcome", source.Outcome, Text(row["sourceLineOutcome"]));

        if (source.Candidates is { } candidates)
        {
            if (parent is not { } held || !candidates.Contains(held))
                differences.Add($"sourceParent: expected one of {string.Join(", ", candidates)}, got {parent?.ToString() ?? "(none)"}");

            Hold("sourceParent.entity", source.Entity, Text(row["sourceParent"]?["entity"]));
        }
        else
        {
            Hold("sourceParent", null, parent);
        }

        if (differences.Count == 0)
        {
            var tour = DeliveringTour(source, parent);
            var vehicle = Vehicle(source, parent, tour, vehiclesOnly);

            Hold("deliveringTourOutcome", tour.Outcome, Text(row["deliveringTourOutcome"]));
            Hold("deliveringTour", tour.Outcome == Resolved ? tour.Id : null, IdUnder(row, "deliveringTour"));
            Hold("vehicleOutcome", vehicle.Outcome, Text(row["vehicleOutcome"]));
            Hold("vehicle", vehicle.Id, IdUnder(row, "vehicle"));
        }

        return differences.Count == 0 ? null : $"{Tuple(flat)}: {string.Join("; ", differences)} in {row.ToJsonString()}";
    }

    private static List<Flat> LineRows(Func<L.Transaction, bool> keep) =>
        [.. WeaveCorpus.Transactions.Where(keep).OrderBy(transaction => transaction.Number, StringComparer.Ordinal).SelectMany(Flatten).Where(flat => flat.Item is L.BillingLineTransactionItem)];

    private static void ShouldBeTheOracles(List<Flat> expected, List<JsonObject> rows, bool vehiclesOnly, string because)
    {
        rows.Select(Tuple).Should().Equal(expected.Select(Tuple), because);

        var differences = expected.Zip(rows, (flat, row) => Difference(flat, row, vehiclesOnly)).Where(difference => difference is not null).ToList();

        differences.Should().BeEmpty($"{because}: {differences.Count} rows differ, the first {differences.FirstOrDefault()}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_hop_of_a_chain_ends_where_the_references_lead_on_every_walk(bool vehiclesOnly)
    {
        var client = await WeaveCorpus.LedgerAsync();
        var stages = Chain("", vehiclesOnly);
        var expected = LineRows(_ => true);

        var outcomes = new HashSet<string>();
        List<string>? first = null;

        foreach (var limit in new[] { 500, 40, 11 })
        {
            var rows = await WalkAsync(client, stages, limit);

            ShouldBeTheOracles(expected, rows, vehiclesOnly, $"pages of {limit}");

            // Where two hold a line, the one taken is the same on every page size.
            var taken = rows.Select(row => row.ToJsonString()).ToList();

            first ??= taken;
            taken.Should().Equal(first, $"pages of {limit} answer the rows of the first walk");

            foreach (var row in rows)
                foreach (var member in new[] { "erpLineOutcome", "sourceLineOutcome", "deliveringTourOutcome", "vehicleOutcome" })
                    outcomes.Add($"{member}:{Text(row[member])}");
        }

        // The corpus reaches every outcome of every hop, so nothing above held for want of a case.
        outcomes.Should().Contain(
        [
            "erpLineOutcome:resolved", "erpLineOutcome:not_found",
            "sourceLineOutcome:resolved", "sourceLineOutcome:ambiguous", "sourceLineOutcome:not_found", "sourceLineOutcome:excluded", "sourceLineOutcome:reference_null",
            "deliveringTourOutcome:resolved", "deliveringTourOutcome:not_applicable", "deliveringTourOutcome:reference_null", "deliveringTourOutcome:not_found",
            "vehicleOutcome:resolved", "vehicleOutcome:not_found", "vehicleOutcome:excluded", "vehicleOutcome:reference_null",
        ]);
    }

    /// <summary>Whether no line of an invoice is held by two: a strict request refuses a row that is ambiguous.</summary>
    private static bool Unambiguous(L.Transaction transaction) =>
        !Deep.Contains(transaction.Number)
        && Flatten(transaction).Select(flat => flat.Item).OfType<L.BillingLineTransactionItem>().All(item => Source(Line(item)).Outcome != Ambiguous);

    private static string Numbered(Func<L.Transaction, bool> keep) =>
        $$"""{ "match": { "number": { "in": [{{string.Join(", ", WeaveCorpus.Transactions.Where(keep).Select(transaction => $"\"{transaction.Number}\""))}}] } } },""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_report_page_of_the_chain_holds_the_rows_of_the_walk(bool vehiclesOnly)
    {
        var client = await WeaveCorpus.LedgerAsync();
        var stages = Chain(Numbered(Unambiguous), vehiclesOnly);
        var expected = LineRows(Unambiguous);

        var report = await ReportAsync(client, stages, onMissing: "null");

        ShouldBeTheOracles(expected, report, vehiclesOnly, "the report page");

        var walked = await WalkAsync(client, stages, 200, onMissing: "null");

        walked.Select(row => row.ToJsonString()).Should().Equal(report.Select(row => row.ToJsonString()), "the walk in pages of 200 answers the rows of the report page");

        // A strict request that reads its missing references refuses the page, and one that reports them answers it whole.
        (await client.QueryAsync(Body(stages, new JsonObject { ["limit"] = 5_000 }, strict: true))).ShouldRefuse("RESOLVE_MISSING", 422);

        var reported = await client.QueryAsync(Body(stages, new JsonObject { ["limit"] = 5_000 }, strict: true, onMissing: "report"));

        reported.ShouldBeOk().Items.Select(row => row!.ToJsonString()).Should().Equal(report.Select(row => row.ToJsonString()));
        reported.DiagnosticCodes.Should().Contain("RESOLVE_MISSING");
    }

    public static TheoryData<string, string> Narrowed() => new()
    {
        { "a member of the invoice", """{ "match": { "reference": { "eq": "batch 1" } } },""" },
        { "a date of the invoice", """{ "match": { "date": { "gte": "2026-05-08T00:00:00Z" } } },""" },
        { "one invoice", """{ "match": { "number": { "eq": "WX-0017" } } },""" },
        { "none", """{ "match": { "number": { "eq": "no such invoice" } } },""" },
    };

    [Theory]
    [MemberData(nameof(Narrowed))]
    public async Task A_chain_over_a_part_of_the_invoices_is_that_part_of_the_chain(string name, string before)
    {
        var client = await WeaveCorpus.LedgerAsync();

        Func<L.Transaction, bool> keep = name switch
        {
            "a member of the invoice" => transaction => transaction.Reference == "batch 1",
            "a date of the invoice" => transaction => transaction.Date >= new DateTime(2026, 5, 8, 0, 0, 0, DateTimeKind.Utc),
            "one invoice" => transaction => transaction.Number == "WX-0017",
            _ => _ => false,
        };

        foreach (var limit in new[] { 500, 13 })
            ShouldBeTheOracles(LineRows(keep), await WalkAsync(client, Chain(before, vehiclesOnly: true), limit), vehiclesOnly: true, $"{name}, pages of {limit}");
    }

    /// <summary>One alias for both kinds of holder answers what two stages, one per kind, answer under two.</summary>
    [Fact]
    public async Task A_union_join_holds_what_a_stage_per_target_holds()
    {
        var client = await WeaveCorpus.LedgerAsync();

        var pair = $$"""
            [ { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
              { "match": { "item": { "is": "BillingLineTransactionItem" } } },
              { "resolve": { "path": "item.billingLineId", "as": "erpLine" } },
              { "resolve": { "path": "erpLine.sourceBillingLineReference.id", "as": "sourceLine", "parentAs": "sourceParent" } },
              { "resolve": { "path": "sourceParent.tours.tourId", "as": "deliveringTour", "forTarget": "{{WeaveCorpus.Shipment}}", "elements": "first" } },
              { "resolve": { "path": "deliveringTour.resource.id", "as": "shipmentVehicle", "target": "fleet.vehicle", "outcomeAs": "shipmentOutcome" } },
              { "resolve": { "path": "sourceParent.resource.id", "as": "tourVehicle", "forTarget": "{{WeaveCorpus.Tour}}", "target": "fleet.vehicle", "outcomeAs": "tourOutcome" } },
              { "sort": [ { "number": "asc" }, { "position": "asc" } ] },
              { "project": { "number": 1, "position": 1, "item.id": 1, "sourceParent.id": 1, "shipmentVehicle.id": 1, "shipmentVehicle.matchCode": 1, "shipmentOutcome": 1, "tourVehicle.id": 1, "tourVehicle.matchCode": 1, "tourOutcome": 1 } } ]
            """;

        var two = await WalkAsync(client, pair, 500);
        var one = await WalkAsync(client, Chain("", vehiclesOnly: true, project: """, "vehicle.matchCode": 1"""), 500);

        one.Select(Tuple).Should().Equal(two.Select(Tuple));

        for (var index = 0; index < one.Count; index++)
        {
            var tour = Text(two[index]["sourceParent"]?["entity"]) == WeaveCorpus.Tour;
            var expected = tour ? two[index]["tourVehicle"] : two[index]["shipmentVehicle"];

            (one[index]["vehicle"]?.ToJsonString()).Should().Be(expected?.ToJsonString(), $"row {index} ({Tuple(one[index])})");

            // The stage of the other kind says that it was not this row's.
            if (two[index]["sourceParent"] is JsonObject)
                Text(two[index][tour ? "shipmentOutcome" : "tourOutcome"]).Should().BeOneOf([NotApplicable, ReferenceNull], $"row {index}");

            Text(one[index]["vehicleOutcome"]).Should().Be(Text(two[index][tour ? "tourOutcome" : "shipmentOutcome"]), $"row {index} ({Tuple(one[index])})");
        }
    }

    // ── references inside a collection, typed by a sibling member and stored as text ─────────

    [Theory]
    [InlineData(500)]
    [InlineData(29)]
    public async Task A_reference_inside_a_collection_ends_where_its_elements_lead(int limit)
    {
        var client = await WeaveCorpus.LedgerAsync();

        const string Stages = """
            [ { "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } },
              { "match": { "item": { "is": "BillingLineTransactionItem" } } },
              { "resolve": { "path": "item.references.referenceId", "as": "firstShipment", "elements": "first", "target": "transport.shipment", "outcomeAs": "firstOutcome" } },
              { "resolve": { "path": "item.references.referenceId", "as": "shipments", "elements": "all", "target": "transport.shipment" } },
              { "sort": [ { "number": "asc" }, { "position": "asc" } ] },
              { "project": { "number": 1, "position": 1, "item.id": 1, "firstShipment.id": 1, "firstOutcome": 1, "shipments.id": 1 } } ]
            """;

        var expected = LineRows(_ => true);
        var rows = await WalkAsync(client, Stages, limit);

        rows.Select(Tuple).Should().Equal(expected.Select(Tuple));

        var outcomes = new HashSet<string?>();

        for (var index = 0; index < rows.Count; index++)
        {
            var references = ((L.BillingLineTransactionItem)expected[index].Item).References.Select(ShipmentOf).ToList();
            var first = First(references);
            var what = $"row {index} ({Tuple(rows[index])}): {rows[index].ToJsonString()}";

            Text(rows[index]["firstOutcome"]).Should().Be(first.Outcome, what);
            IdUnder(rows[index], "firstShipment").Should().Be(first.Id, what);

            var all = (rows[index]["shipments"] as JsonArray ?? []).Select(element => element is JsonObject record ? Guid.Parse(record["id"]!.GetValue<string>()) : (Guid?)null).Where(id => id is not null).ToList();

            all.Should().Equal(references.Where(reference => reference.Outcome == Resolved).Select(reference => reference.Id), what);
            outcomes.Add(first.Outcome);
        }

        outcomes.Should().Contain([Resolved, NotFound, InvalidKey, Excluded, ReferenceNull]);
    }

    // ── a reference onto a member that is not the key ───────────────────────────────────────

    [Theory]
    [InlineData(500)]
    [InlineData(17)]
    public async Task The_clerk_of_an_invoice_is_the_employee_of_its_user_and_two_of_them_are_said(int limit)
    {
        var client = await WeaveCorpus.LedgerAsync();

        const string Stages = """
            [ { "resolve": { "path": "createUserId", "as": "clerk", "outcomeAs": "clerkOutcome" } },
              { "sort": [ { "number": "desc" } ] },
              { "project": { "number": 1, "clerk.id": 1, "clerkOutcome": 1 } } ]
            """;

        var expected = WeaveCorpus.Transactions.OrderByDescending(transaction => transaction.Number, StringComparer.Ordinal).ToList();
        var rows = await WalkAsync(client, Stages, limit);

        rows.Select(row => Text(row["number"])).Should().Equal(expected.Select(transaction => transaction.Number));

        var taken = new Dictionary<Guid, Guid>();

        for (var index = 0; index < rows.Count; index++)
        {
            var clerk = Clerk(expected[index]);
            var what = $"{expected[index].Number}: {rows[index].ToJsonString()}";

            Text(rows[index]["clerkOutcome"]).Should().Be(clerk.Outcome, what);

            if (clerk.Candidates is { } candidates)
            {
                candidates.Should().Contain(IdUnder(rows[index], "clerk")!.Value, what);

                // The one taken of two is the same for every invoice of that user, on every page.
                taken.TryAdd(expected[index].CreateUserId, IdUnder(rows[index], "clerk")!.Value);
                IdUnder(rows[index], "clerk").Should().Be(taken[expected[index].CreateUserId], what);
            }
            else
            {
                IdUnder(rows[index], "clerk").Should().Be(clerk.Id, what);
            }
        }
    }

    // ── the row is what the projection names ────────────────────────────────────────────────

    public static TheoryData<string, string, string> Projected()
    {
        var data = new TheoryData<string, string, string>();

        foreach (var (unwind, how) in new[] { ("""{ "unwind": { "path": "items", "as": "item" } }""", "an unwind"), ("""{ "unwind": { "path": "items", "flatten": "items", "as": "item", "includeIndex": "position" } }""", "a flatten") })
            foreach (var (policy, word) in new[] { ("", "no word"), (""", "onMissing": "report" """, "report"), (""", "outcomeAs": "said" """, "an outcome") })
                foreach (var (join, what) in new[]
                {
                    ($$"""{ "resolve": { "path": "item.billingLineId", "as": "joined"{{policy}} } }""", "a record of this service"),
                    ($$"""{ "resolve": { "path": "item.references.referenceId", "as": "joined", "elements": "first", "target": "transport.shipment"{{policy}} } }""", "a record of another service, through a collection"),
                })
                    data.Add($"{how}, {what}, {word}", unwind, join);

        return data;
    }

    /// <summary>
    /// A join reads its reference off the row, so the reference outlives the projection in storage;
    /// the row shows what the projection names and nothing a join kept for itself, at the row's own
    /// members and under the alias of an unwind alike.
    /// </summary>
    [Theory]
    [MemberData(nameof(Projected))]
    public async Task A_row_shows_what_the_projection_names_under_an_unwound_alias_whatever_a_join_reads_there(string name, string unwind, string join)
    {
        var client = await WeaveCorpus.LedgerAsync();

        foreach (var (sorted, order) in new[] { ("before the projection", true), ("after it", false) })
        {
            const string Sort = """{ "sort": [{ "number": "asc" }] }""";
            const string Project = """{ "project": { "number": 1, "item.id": 1, "joined.id": 1 } }""";

            var stages = $$"""[{ "match": { "item": { "is": "BillingLineTransactionItem" } } }, {{join}}, {{(order ? Sort + ", " + Project : Project + ", " + Sort)}}]""";
            var rows = await WalkAsync(client, $"[{unwind}, {stages[1..]}", 250);

            rows.Should().NotBeEmpty();

            foreach (var row in rows)
            {
                var what = $"{name}, sorted {sorted}: {row.ToJsonString()}";

                row.Select(member => member.Key).Should().BeSubsetOf(["id", "number", "item", "joined"], what);
                row["item"]!.AsObject().Select(member => member.Key).Should().Equal(["id"], what);

                if (row["joined"] is JsonObject joined)
                    joined.Select(member => member.Key).Should().Equal(["id"], what);
            }
        }
    }
}
