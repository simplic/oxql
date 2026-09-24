using FluentAssertions;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Harness;
using OxQL.IntegrationTests.Suites.Group;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Seed;

/// <summary>
/// The oracle agrees with the database before any suite leans on it: <see cref="Corpus.SortedIds"/>
/// predicts the engine's order on the paths that carry the corpus's order hazards (null against
/// missing, a decimal stored as a string, an empty array, case and accents and CJK under the
/// collation, dates, booleans, enums, longs above 2^53), and <see cref="Corpus.ValueAt"/> reads what
/// the engine renders. A disagreement here is an oracle defect or an engine defect, and either
/// has to be settled before a wave ports a case over the path.
/// </summary>
[Trait("Category", "Integration")]
public class OracleAgreementTests
{
    public static TheoryData<string, string> SortPaths => new()
    {
        { Corpus.Employee, "matchCode" },
        { Corpus.Employee, "address.lastName" },
        { Corpus.Employee, "address.city" },
        { Corpus.Employee, "birthday" },
        { Corpus.Employee, "children" },
        { Corpus.Employee, "group.displayName" },
        { Corpus.Employee, "employment.exitDate" },
        { Corpus.Employee, "isDeleted" },
        { Corpus.Employee, "addon.shiftModel" },
        { Corpus.Vehicle, "mileage" },
        { Corpus.Vehicle, "operatingHours" },
        { Corpus.Vehicle, "fuelTankCapacity" },
        { Corpus.Vehicle, "status.name" },
        { Corpus.Vehicle, "additionalTechnicalData.payload" },
        { Corpus.Vehicle, "qrCode" },
        { Corpus.Vehicle, "location" },
        { Corpus.Shipment, "shipmentNumber" },
        { Corpus.Shipment, "loadStart" },
        { Corpus.Shipment, "loadingTimeType" },
        { Corpus.Shipment, "status.name" },
        { Corpus.Shipment, "loadAddress.city" },
        { Corpus.Shipment, "actualWeight.value" },
        { Corpus.Shipment, "department.id" },
        { Corpus.Transaction, "totalPrice" },
        { Corpus.Transaction, "balance" },
        { Corpus.Transaction, "convertState" },
        { Corpus.Transaction, "number" },
        { Corpus.Transaction, "reference" },
        { Corpus.Transaction, "date" },
        { Corpus.Conformance, "magnitude" },
        { Corpus.Conformance, "optionalMagnitude" },
        { Corpus.Conformance, "amount" },
        { Corpus.Conformance, "grade" },
        { Corpus.Conformance, "day" },
        { Corpus.Conformance, "duration" },
        { Corpus.Conformance, "state" },
        { Corpus.Conformance, "marker" },
        { Corpus.Status, "name" },
        { Corpus.Department, "name" },
    };

    [Theory]
    [MemberData(nameof(SortPaths))]
    public async Task The_oracle_predicts_the_engine_order_in_both_directions(string entityId, string path)
    {
        var client = await Lab.ClientForAsync(entityId);

        foreach (var descending in new[] { false, true })
        {
            var direction = descending ? "desc" : "asc";
            var expected = Corpus.SortedIds(entityId, path, descending);

            var answer = await client.SendAsync(entityId, $$"""[{ "sort": [{ "{{path}}": "{{direction}}" }] }, { "project": { "id": 1 } }, { "page": { "limit": 500 } }]""");

            answer.ShouldHaveIds(expected, $"sort {path} {direction}");
        }
    }

    [Fact]
    public async Task The_oracle_predicts_the_volume_order_through_nulls_missing_and_duplicates()
    {
        var client = await Lab.ClientAsync(Fleet.LabService.Transport);

        var walk = await client.WalkAsync(Corpus.Template, """[{ "sort": [{ "templateName": "desc" }] }, { "project": { "id": 1 } }]""", 500);

        walk.Ids.Should().Equal(Corpus.SortedIds(Corpus.Template, "templateName", descending: true));
    }

    [Fact]
    public async Task The_oracle_reads_the_members_the_engine_renders()
    {
        var client = await Lab.ClientForAsync(Corpus.Employee);
        var rows = await client.PullAsync(Corpus.Employee);
        var byId = rows.ToDictionary(row => Guid.Parse(row["id"]!.GetValue<string>()));

        foreach (var row in Corpus.Rows(Corpus.Employee))
        {
            var wire = byId[row.Id];

            Json.Has(wire, "matchCode").Should().Be(!Corpus.Missing(row, "matchCode"), row.Key);
            wire["matchCode"]?.GetValue<string>().Should().Be(Corpus.Text(row, "matchCode"), row.Key);
            Json.Has(wire, "group").Should().Be(!Corpus.Missing(row, "group"), row.Key);
            Json.Has(wire, "addon").Should().Be(!Corpus.Missing(row, "addon"), row.Key);
        }
    }

    [Fact]
    public void Collated_equality_and_order_agree_over_the_corpus_strings()
    {
        var strings = Corpus.Rows(Corpus.Employee).Select(row => Corpus.Text(row, "matchCode")).OfType<string>().Distinct().ToList();

        foreach (var a in strings)
        {
            foreach (var b in strings)
                (Order.CompareCollated(a, b) == 0).Should().Be(Order.EqualsCi(a, b), $"'{a}' against '{b}'");
        }
    }

    // ── dateTrunc: the two passes through the repeated autumn hour ──────────────────────────

    [Theory]
    [InlineData("hour", "2026-10-25T00:00:00Z", "2026-10-25T01:00:00Z")]
    [InlineData("minute", "2026-10-25T00:30:00Z", "2026-10-25T01:30:00Z")]
    [InlineData("second", "2026-10-25T00:30:00Z", "2026-10-25T01:30:00Z")]
    public void The_temporal_oracle_keeps_the_two_passes_through_the_repeated_hour_apart(string unit, string first, string second)
    {
        // Both rows read local 02:30 on 2026-10-25 in Berlin, once under CEST and once under CET.
        // Rebuilding the boundary from the wall clock named one instant for both.
        var firstPass = Corpus.Date(Corpus.Row(Corpus.Shipment, "dst-autumn-first"), "loadStart")!.Value;
        var secondPass = Corpus.Date(Corpus.Row(Corpus.Shipment, "dst-autumn-second"), "loadStart")!.Value;

        Temporal.Local(firstPass, Temporal.Berlin).Should().Be(Temporal.Local(secondPass, Temporal.Berlin), "the corpus built the pair on one wall clock");
        Temporal.DateTruncUtc(firstPass, unit, Temporal.Berlin).Should().Be(first);
        Temporal.DateTruncUtc(secondPass, unit, Temporal.Berlin).Should().Be(second);

        var buckets = Corpus.DateTruncBuckets(Corpus.Shipment, "loadStart", unit, Temporal.Berlin);
        var firstId = Corpus.Row(Corpus.Shipment, "dst-autumn-first").Id;
        var secondId = Corpus.Row(Corpus.Shipment, "dst-autumn-second").Id;

        buckets.Should().NotContain(bucket => bucket.Ids.Contains(firstId) && bucket.Ids.Contains(secondId), $"one {unit} bucket must not hold both passes");
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData(Temporal.Berlin)]
    public void The_two_date_trunc_oracles_agree_on_every_unit_over_the_shipments(string timeZone)
    {
        var starts = Corpus.Rows(Corpus.Shipment).Select(row => Corpus.Date(row, "loadStart")!.Value).ToList();

        foreach (var unit in Truncation.Units)
            foreach (var start in starts)
                Temporal.DateTruncUtc(start, unit, timeZone).Should().Be(Truncation.Truncate(start, unit, timeZone), $"{unit} {timeZone} {Temporal.Iso(start)}");
    }

    [Theory]
    [InlineData("hour")]
    [InlineData("minute")]
    [InlineData("second")]
    public async Task The_temporal_oracle_predicts_the_engine_sub_day_buckets_in_berlin(string unit)
    {
        var client = await Lab.ClientAsync(Fleet.LabService.Transport);
        var answer = await client.SendAsync(Corpus.Shipment, $$"""
            [ { "group": { "by": [{ "dateTrunc": { "path": "loadStart", "unit": "{{unit}}", "timezone": "{{Temporal.Berlin}}" }, "as": "bucket" }], "fields": { "total": { "count": true } } } },
              { "sort": [{ "bucket": "asc" }] },
              { "page": { "limit": 100 } } ]
            """);

        answer.ShouldBeOk(unit);
        answer.HasNextPage.Should().BeFalse();

        var observed = answer.Items.Select(item => (item!["bucket"]!.GetValue<string>(), (int)GroupKit.Number(item["total"])!.Value)).ToList();
        var expected = Corpus.DateTruncBuckets(Corpus.Shipment, "loadStart", unit, Temporal.Berlin).Select(bucket => (bucket.Bucket, bucket.Ids.Count)).ToList();

        observed.Should().Equal(expected, unit);
    }
}
