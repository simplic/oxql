using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Cursor;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// What a contract 2 refusal says, read as its caller reads it: through the host, as bytes. No
/// message and no parameter names how a member is stored (a storage name, the discriminator
/// <c>_t</c>, the key <c>_id</c>) or a type of the runtime; a caller sees the wire's names and
/// kinds and nothing else. And the requests the engine reads rather than refuses are read the way
/// the documentation says.
/// </summary>
public class RefusalTextTests
{
    private const string Order = "probe.order";

    private static string Query(string pipeline, string entity = Order) => $$"""{ "entityType": "{{entity}}", "pipeline": {{pipeline}} }""";

    private static string Match(string condition) => Query($$"""[{ "match": {{condition}} }]""");

    /// <summary>A request of every family of refusal a caller can provoke by what it writes.</summary>
    public static TheoryData<string, string> Refusals => new()
    {
        { "an unknown entity", Query("[]", "probe.nowhere") },
        { "an entity in the storage's spelling", Query("[]", "OrderModel") },
        { "an unknown path", Match("""{ "nothing": 1 }""") },
        { "a storage name as a path", Match("""{ "CreateDateTime": { "gt": "2026-01-01T00:00:00Z" } }""") },
        { "the discriminator as a path", Match("""{ "_t": "OrderModel" }""") },
        { "the stored key as a path", Match("""{ "_id": "x" }""") },
        { "a path under a storage name", Match("""{ "ShipTo.City": "x" }""") },
        { "an operand of another kind", Match("""{ "count": { "eq": "many" } }""") },
        { "a date that is no date", Match("""{ "when": { "gte": "2026-02-30T00:00:00Z" } }""") },
        { "an object as an operand", Match("""{ "count": { "eq": { "a": 1 } } }""") },
        { "a number that is no member of the enum", Match("""{ "state": 99 }""") },
        { "a name that is no member of the enum", Match("""{ "state": "draft" }""") },
        { "an operator that does not apply", Match("""{ "flag": { "contains": "x" } }""") },
        { "an unknown operator", Match("""{ "count": { "dateTrunc": "month" } }""") },
        { "an empty group", Match("""{ "and": [] }""") },
        { "an empty any", Match("""{ "items": { "any": {} } }""") },
        { "any on a scalar", Match("""{ "number": { "any": { "x": 1 } } }""") },
        { "an option that is no boolean", Match("""{ "number": { "eq": "a", "options": { "caseSensitive": "true" } } }""") },
        { "an unknown option", Match("""{ "number": { "eq": "a", "options": { "foo": 1 } } }""") },
        { "is on a member without variants", Match("""{ "shipTo": { "is": "Address" } }""") },
        { "an invalid pattern", Match("""{ "number": { "regex": "(" } }""") },
        { "a pattern too long", Match($$"""{ "number": { "regex": "^{{new string('a', 300)}}" } }""") },
        { "an unbound variable", Match("""{ "count": { "eq": { "$var": "missing" } } }""") },
        { "a condition on an object", Match("""{ "shipTo": 1 }""") },
        { "a condition on a collection", Match("""{ "items": 1 }""") },
        { "a member nothing stores", Match("""{ "hidden": 1 }""") },
        { "an unknown stage", Query("""[{ "filter": {} }]""") },
        { "a stage with two keys", Query("""[{ "match": {}, "sort": [] }]""") },
        { "a null stage", Query("[null]") },
        { "an unknown stage member", Query("""[{ "unwind": { "path": "items", "as": "item", "preserveNulls": true } }]""") },
        { "an unwind of a scalar", Query("""[{ "unwind": { "path": "number", "as": "n" } }]""") },
        { "an invalid alias", Query("""[{ "unwind": { "path": "items", "as": "9 lives" } }]""") },
        { "an alias that is taken", Query("""[{ "unwind": { "path": "items", "as": "number" } }]""") },
        { "a sort without a direction", Query("""[{ "sort": [{ "number": "up" }] }]""") },
        { "a sort on an object", Query("""[{ "sort": [{ "shipTo": "asc" }] }]""") },
        { "a mixed projection", Query("""[{ "project": { "number": 1, "count": 0 } }]""") },
        { "a projection of a string", Query("""[{ "project": { "number": "yes" } }]""") },
        { "an empty projection", Query("""[{ "project": {} }]""") },
        { "an unknown aggregate", Query("""[{ "group": { "by": [], "fields": { "n": { "median": "count" } } } }]""") },
        { "a sum over text", Query("""[{ "group": { "by": [], "fields": { "n": { "sum": "number" } } } }]""") },
        { "a group key under a collection", Query("""[{ "group": { "by": [{ "path": "items.quantity", "as": "q" }], "fields": {} } }]""") },
        { "a dateTrunc over text", Query("""[{ "group": { "by": [{ "dateTrunc": { "path": "number", "unit": "month" }, "as": "m" }], "fields": {} } }]""") },
        { "a dateTrunc unit", Query("""[{ "group": { "by": [{ "dateTrunc": { "path": "when", "unit": "fortnight" }, "as": "m" }], "fields": {} } }]""") },
        { "a timezone", Query("""[{ "group": { "by": [{ "dateTrunc": { "path": "when", "unit": "day", "timezone": "Mars/Olympus" }, "as": "m" }], "fields": {} } }]""") },
        { "a lookup that is not declared", Query("""[{ "lookup": { "from": "probe.customer", "path": "number", "as": "c" } }]""") },
        { "a lookup in the earlier form", Query("""[{ "lookup": { "from": "CustomerModel", "localPath": "CustomerId", "foreignPath": "_id", "as": "c" } }]""") },
        { "a resolve that is not declared", Query("""[{ "resolve": { "path": "number", "as": "c" } }]""") },
        { "a page limit of zero", Query("""[{ "page": { "limit": 0 } }]""") },
        { "a page over the host's size", Query("""[{ "page": { "limit": 100000 } }]""") },
        { "an offset on the public route", Query("""[{ "page": { "limit": 1, "offset": 5 } }]""") },
        { "a stage after the page", Query("""[{ "page": { "limit": 1 } }, { "match": {} }]""") },
        { "two pages", Query("""[{ "page": { "limit": 1 } }, { "page": { "limit": 1 } }]""") },
        { "a cursor of another query", Query($$"""[{ "page": { "limit": 1, "cursor": "{{BindHost.Cursors.Encode(new CursorPayload("another", PagingMode.Offset, [], 5))}}" } }]""") },
        { "a cursor that is none", Query("""[{ "page": { "limit": 1, "cursor": "garbage" } }]""") },
        { "a values stage over a collection of objects", Query("""[{ "values": { "path": "items" } }]""") },
        { "a values limit", Query("""[{ "values": { "path": "number", "limit": 100000 } }]""") },
        { "a member a request does not have", """{ "entityType": "probe.order", "pipeline": [], "page": { "limit": 1 } }""" },
        { "a filter that is no list", """{ "entityType": "probe.order", "pipeline": [], "filter": {} }""" },
        { "a body that is no JSON", """{ "entityType": "probe.order", "pipeline": [ }""" },
        { "a stage that is text", Query("""["page"]""") },
        { "a sort that is an object", Query("""[{ "sort": { "number": "asc" } }]""") },
    };

    /// <summary>The names the probe model stores its members under, where they differ from the wire's.</summary>
    private static readonly IReadOnlyList<string> StorageNames = BindHost.Probe.Entities.Values
        .SelectMany(entity => entity.Paths)
        .Where(path => path.Storage is not null && !string.Equals(path.Storage, path.Wire, StringComparison.Ordinal))
        .SelectMany(path => path.Storage!.Split('.'))
        .Where(name => name.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_refusal_names_no_storage_name_and_no_type_of_the_runtime(string what, string body)
    {
        using var host = new SampleHost();

        var response = await host.Client().PostAsync("/OxQL/query", SampleHost.Json(body));
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{what}: {text}");

        var answer = (await SampleHost.Body(response))!;

        answer["type"]!.GetValue<string>().Should().Be("validation_error", what);

        // What the caller wrote is the caller's to read back; everything else in the answer is the engine's.
        var said = string.Join("\n", answer["errors"]!.AsArray().Select(error => error!["message"]!.GetValue<string>() + "\n" + (error["params"]?.ToJsonString() ?? "")));
        var written = Regex.Matches(body, "\"([^\"]+)\"").Select(match => match.Groups[1].Value).SelectMany(value => value.Split('.')).ToHashSet(StringComparer.Ordinal);

        foreach (var name in StorageNames.Where(name => !written.Contains(name)))
            Regex.IsMatch(said, $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])").Should().BeFalse($"{what}: the storage name '{name}' is in: {said}");

        foreach (var internalName in new[] { "_t", "_id", "_v" }.Where(name => !written.Contains(name)))
            Regex.IsMatch(said, $@"(?<![A-Za-z0-9_]){internalName}(?![A-Za-z0-9_])").Should().BeFalse($"{what}: '{internalName}' is in: {said}");

        foreach (var runtime in new[] { "System.", "OxQL.", "Nullable", "IReadOnly", "List`", "Dictionary`", "Exception", "Bson", "Mongo", "JsonElement", "LineNumber", "BytePositionInLine", " at OxQL" })
            text.Should().NotContain(runtime, $"{what}: {text}");
    }

    [Fact]
    public void The_scan_knows_the_names_it_looks_for()
    {
        StorageNames.Should().Contain(["CreateDateTime", "OrganizationId", "Number", "ShipTo", "_id"]);
        Refusals.Should().HaveCountGreaterThan(55);
    }

    // ---- read, not refused: what the documentation says of each ------------------------------------

    [Fact]
    public async Task Id_kept_beside_removed_paths_is_the_inclusion_of_id_alone()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "project": { "id": 1, "note": 0 } }]""");
        var project = bound.Stages.OfType<BoundStage.Project>().Single();

        project.Inclusion.Should().BeTrue();
        bound.FinalShape.IsVisible("id").Should().BeTrue();
        bound.FinalShape.IsVisible("note").Should().BeFalse();
        bound.FinalShape.IsVisible("number").Should().BeFalse("the row holds id and nothing else");

        // Everything but one member is the exclusion on its own.
        var exclusion = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "project": { "note": 0 } }]""");

        exclusion.FinalShape.IsVisible("number").Should().BeTrue();
        exclusion.FinalShape.IsVisible("note").Should().BeFalse();
    }

    [Fact]
    public async Task A_whole_number_other_than_0_keeps_a_path_as_1_does()
    {
        var two = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "project": { "note": 2 } }]""");
        var one = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "project": { "note": 1 } }]""");

        two.Fingerprint.Should().Be(one.Fingerprint);
        two.FinalShape.IsVisible("note").Should().BeTrue();
        two.FinalShape.IsVisible("number").Should().BeFalse();
    }

    [Fact]
    public async Task A_group_without_keys_and_fields_binds_as_the_totals_without_aggregates()
    {
        var bound = await BindHost.BoundAsync(BindHost.Probe, Order, """[{ "group": {} }]""");
        var group = bound.Stages.OfType<BoundStage.Group>().Single();

        group.Keys.Should().BeEmpty();
        group.Fields.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{ "count": { "eq": 99999999999 } }""")]
    [InlineData("""{ "ratio": { "eq": "NaN" } }""")]
    [InlineData("""{ "flag": { "eq": "TRUE" } }""")]
    public async Task An_operand_of_the_right_kind_that_no_stored_value_equals_binds(string condition)
    {
        var outcome = await BindHost.BindAsync(BindHost.Probe, BindHost.Request(Order, $$"""[{ "match": {{condition}} }]"""));

        outcome.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(outcome));
    }

    [Fact]
    public void A_member_written_twice_is_read_once_the_last_one()
    {
        var request = BindHost.Parse("""{ "entityType": "probe.order", "pipeline": [{ "page": { "limit": 1 } }], "pipeline": [{ "page": { "limit": 7 } }, { "match": {} }] }""");

        request.Pipeline.Should().HaveCount(2);
        request.Pipeline[0].Page!.Limit.Should().Be(7);
    }
}
