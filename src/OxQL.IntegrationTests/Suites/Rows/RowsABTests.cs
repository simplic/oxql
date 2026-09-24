using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using MongoDB.Bson;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Model;
using Xunit;

namespace OxQL.IntegrationTests.Suites.Rows;

/// <summary>
/// Area AB: the row encoding on the wire. Every row a caller reads passes through it and a defect
/// here is silent, so the cases walk every kind the corpus reaches, every leaf of every row of
/// every entity, against the kind the service's model declares for its path. Ported from the
/// legacy <c>rows-ab</c> battery, engine half only: what the typed client decodes a row into
/// (enum names, the no-copy fast path) went to the client's specs. The legacy battery read the
/// kinds from the vendored schema documents; the lab reads them from each service's model, which
/// is what the schema document is generated from.
/// </summary>
[Trait("Category", "Integration")]
public partial class RowsABTests
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$")]
    private static partial Regex IsoInstant();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DateOnlyText();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex GuidText();

    [GeneratedRegex(@"^-?P(?=[\dT])(\d+Y)?(\d+M)?(\d+D)?(T(\d+H)?(\d+M)?(\d+(\.\d+)?S)?)?$")]
    private static partial Regex IsoDuration();

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex Digits();

    /// <summary>Every entity's rows as the engine renders them, walked in id order; the volume entity a 60-row slice.</summary>
    private static async Task<IReadOnlyList<JsonObject>> RowsOf(string entityId)
    {
        var entity = Corpus.Entity(entityId);
        var client = await Lab.ClientForAsync(entityId);
        var limit = entityId == Corpus.Template ? 60 : 500;
        var answer = await client.SendAsync(entityId, $$"""[ { "sort": [ { "{{entity.KeyPath}}": "asc" } ] }, { "page": { "limit": {{limit}} } } ]""");

        answer.ShouldBeOk();

        if (entityId != Corpus.Template)
            answer.HasNextPage.Should().BeFalse($"{entityId}: the whole entity must fit one page");

        answer.Items.Should().HaveCount(Math.Min(limit, Corpus.Counts(entityId).A));

        return answer.Items.OfType<JsonObject>().ToList();
    }

    private static EntityDef Model(string entityId) => Corpus.Entity(entityId).Service.Model.Entities[entityId];

    public static TheoryData<string> Entities() => new(Corpus.Entities.Select(entity => entity.Id));

    /// <summary>Walks every member of a row: the declared path (or null), the wire path and the value.</summary>
    private static IEnumerable<(PathDef? Path, string Wire, JsonNode? Value)> Walk(EntityDef entity, JsonObject node, string prefix = "")
    {
        foreach (var (key, value) in node)
        {
            var wire = prefix.Length == 0 ? key : $"{prefix}.{key}";
            var path = entity.Path(wire);

            yield return (path, wire, value);

            if (path is null || value is null)
                continue;

            if (path.Kind == Kind.Object && value is JsonObject inner)
            {
                foreach (var member in Walk(entity, inner, wire))
                    yield return member;
            }
            else if (path.Kind == Kind.Array && value is JsonArray array)
            {
                foreach (var element in array.OfType<JsonObject>())
                    foreach (var member in Walk(entity, element, wire))
                        yield return member;
            }
        }
    }

    /// <summary>Why a scalar value does not fit the kind declared for its path, or null when it does.</summary>
    private static string? Misfit(Kind kind, JsonNode? value)
    {
        if (value is null)
            return null;

        var json = value.GetValueKind();
        var text = json == JsonValueKind.String ? value.GetValue<string>() : null;

        return kind switch
        {
            Kind.String => json == JsonValueKind.String ? null : "declared string",
            Kind.Int => json == JsonValueKind.Number && value.GetValue<decimal>() % 1 == 0 ? null : "declared int",
            Kind.Double => json == JsonValueKind.Number ? null : "declared double",
            Kind.Long => text is not null && Digits().IsMatch(text) ? null : "declared long, wants a string of digits",
            Kind.Decimal => json == JsonValueKind.String ? null : "declared decimal, wants a string",
            Kind.Bool => json is JsonValueKind.True or JsonValueKind.False ? null : "declared bool",
            Kind.Guid => text is not null && GuidText().IsMatch(text) ? null : "declared guid, wants a canonical uuid",
            Kind.Date => text is not null && DateOnlyText().IsMatch(text) ? null : "declared date, wants YYYY-MM-DD",
            Kind.DateTime => text is not null && IsoInstant().IsMatch(text) ? null : "declared dateTime, wants an ISO instant with Z",
            Kind.TimeSpan => text is not null && IsoDuration().IsMatch(text) ? null : "declared timeSpan, wants an ISO duration",
            Kind.Binary => json == JsonValueKind.String ? null : "declared binary, wants base64",
            // An enum travels as its number; above the Int32 range as a string of digits.
            Kind.Enum => json == JsonValueKind.Number || (text is not null && Digits().IsMatch(text) && !int.TryParse(text, out _)) ? null : "declared enum, wants a number",
            Kind.Object => json == JsonValueKind.Object ? null : "declared object",
            Kind.Array => json == JsonValueKind.Array ? null : "declared array",
            _ => null,
        };
    }

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task AB02_AB03_no_key_at_any_depth_of_any_row_is_underscore_id_or_begins_with_an_upper_case_letter(string entityId)
    {
        var offenders = new List<string>();

        void Check(JsonNode? node, string prefix)
        {
            if (node is JsonArray array)
            {
                foreach (var element in array)
                    Check(element, prefix);

                return;
            }

            if (node is not JsonObject item)
                return;

            foreach (var (key, value) in item)
            {
                var wire = prefix.Length == 0 ? key : $"{prefix}.{key}";

                if (key == "_id")
                    offenders.Add($"{wire} is _id");

                if (char.IsUpper(key[0]))
                    offenders.Add($"{wire} is not camelCase");

                // The bag and the dictionaries are verbatim by contract: their keys are data.
                if (Model(entityId).Path(wire) is { Kind: Kind.Dictionary or Kind.Unknown })
                    continue;

                Check(value, wire);
            }
        }

        foreach (var row in await RowsOf(entityId))
            Check(row, "");

        offenders.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task AB14_every_key_of_every_row_names_a_member_the_model_declares(string entityId)
    {
        var entity = Model(entityId);

        var undeclared = (await RowsOf(entityId)).SelectMany(row => Walk(entity, row)).Where(member => member.Path is null).Select(member => member.Wire).Distinct().ToList();

        undeclared.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Entities))]
    public async Task AB01_AB03_every_value_in_every_row_matches_the_kind_the_model_declares_for_its_path(string entityId)
    {
        var entity = Model(entityId);
        var leaves = 0;
        var misfits = new List<string>();

        foreach (var row in await RowsOf(entityId))
        {
            foreach (var (path, wire, value) in Walk(entity, row))
            {
                if (path is null)
                    continue;

                if (path.Kind == Kind.Array && value is JsonArray array)
                {
                    foreach (var element in array.Where(element => element is not JsonObject))
                    {
                        leaves++;

                        if (Misfit(path.LeafKind, element) is { } why)
                            misfits.Add($"{wire}[]: {why}, carries {element?.ToJsonString()}");
                    }

                    continue;
                }

                if (path.Kind is Kind.Dictionary or Kind.Unknown)
                    continue;

                leaves++;

                if (Misfit(path.Kind, value) is { } reason)
                    misfits.Add($"{wire}: {reason}, carries {value?.ToJsonString()}");
            }
        }

        leaves.Should().BeGreaterThanOrEqualTo(Math.Min(60, Corpus.Counts(entityId).A) * 5, "the walk must reach values");
        misfits.Distinct().Should().BeEmpty();
    }

    [Fact]
    public async Task AB05_a_decimal_travels_as_a_string_on_both_halves_of_the_decimal_duality_keeping_every_digit()
    {
        var rows = await RowsOf(Corpus.Vehicle);

        rows.Should().OnlyContain(row => row["mileage"]!.GetValueKind() == JsonValueKind.String && row["operatingHours"]!.GetValueKind() == JsonValueKind.String);

        // The stored text rows come back as strings too.
        var stringRows = Corpus.IdsOf(Corpus.VehiclesWithStringMileage());
        stringRows.Should().HaveCount(2);

        // The many-places row survives verbatim: 25 fractional digits, which no double holds.
        var wide = Corpus.Row(Corpus.Vehicle, "dec-many-places");
        var stored = Corpus.Number(wide, "mileage")!;
        stored.Split('.')[1].Should().HaveLength(25);
        var onWire = rows.Single(row => row["id"]!.GetValue<string>() == wide.WireId)["mileage"]!.GetValue<string>();
        onWire.Should().Be(stored);
        double.Parse(onWire, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture).Should().NotBe(onWire, "a number JavaScript cannot round-trip");
    }

    [Fact]
    public async Task AB06_every_dateTime_is_an_ISO_instant_in_UTC_with_Z_and_no_milliseconds()
    {
        var rows = await RowsOf(Corpus.Shipment);
        var expected = Corpus.Rows(Corpus.Shipment).Select(row => Corpus.Date(row, "loadStart")).ToList();

        var instants = rows.SelectMany(row => new[] { row["createDateTime"], row["loadStart"] }).OfType<JsonValue>().Select(value => value.GetValue<string>()).ToList();
        instants.Should().HaveCountGreaterThan(Corpus.Counts(Corpus.Shipment).A);
        instants.Should().OnlyContain(value => IsoInstant().IsMatch(value) && !value.Contains('.'));

        rows.Select(row => row["loadStart"]?.GetValue<string>()).Should().Equal(expected.Select(date => date?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task AB09_every_guid_is_a_canonical_uuid_string()
    {
        var rows = await RowsOf(Corpus.Shipment);

        rows.Select(row => row["id"]!.GetValue<string>()).Should().Equal(Corpus.Rows(Corpus.Shipment).Select(row => row.WireId));
        rows.Select(row => row["createUserId"]!.GetValue<string>()).Should().OnlyContain(text => GuidText().IsMatch(text));
        rows.Select(row => row["createUserId"]!.GetValue<string>()).Should().OnlyContain(text => Guid.Parse(text) == LabIdentity.User);
    }

    [Fact]
    public async Task AB08_a_timeSpan_travels_as_an_ISO_8601_duration()
    {
        var expected = Corpus.Rows(Corpus.Template).Take(20).Select(row => Corpus.ValueAt(row, "loadStart.relativeTime") is BsonString span
            ? System.Xml.XmlConvert.ToString(TimeSpan.Parse(span.Value, CultureInfo.InvariantCulture)) : null).ToList();
        expected.Should().Contain(span => span != null).And.Contain(span => span == null);

        var answer = await (await Lab.ClientAsync(LabService.Transport)).SendAsync(Corpus.Template, """[ { "sort": [ { "id": "asc" } ] }, { "project": { "id": 1, "loadStart.relativeTime": 1 } }, { "page": { "limit": 20 } } ]""");

        answer.ShouldBeOk();
        var spans = answer.Values("loadStart.relativeTime").Select(value => value?.GetValue<string>()).ToList();
        spans.Should().Equal(expected);
        spans.OfType<string>().Should().OnlyContain(span => IsoDuration().IsMatch(span));
    }

    [Fact]
    public async Task AB15_an_explicit_null_is_present_as_null_and_a_missing_member_is_absent()
    {
        var nulls = Corpus.IdsOf(Corpus.RowsNull(Corpus.Employee, "matchCode"));
        var missing = Corpus.IdsOf(Corpus.RowsMissing(Corpus.Employee, "matchCode"));
        var empty = Corpus.IdsWhere(Corpus.Employee, row => Corpus.Text(row, "matchCode") == "");
        new[] { nulls, missing, empty }.Should().OnlyContain(set => set.Count == 1);

        var rows = (await RowsOf(Corpus.Employee)).ToDictionary(row => Guid.Parse(row["id"]!.GetValue<string>()));

        rows[nulls[0]].ContainsKey("matchCode").Should().BeTrue();
        rows[nulls[0]]["matchCode"].Should().BeNull();
        rows[missing[0]].ContainsKey("matchCode").Should().BeFalse();
        rows[empty[0]]["matchCode"]!.GetValue<string>().Should().BeEmpty();
    }

    [Fact]
    public async Task AB16_a_dictionary_renders_as_an_object_keyed_by_its_keys_a_dotted_key_included()
    {
        var rows = (await RowsOf(Corpus.Transaction)).ToDictionary(row => Guid.Parse(row["id"]!.GetValue<string>()));
        var dictionaries = 0;

        foreach (var row in Corpus.Rows(Corpus.Transaction))
        {
            if (Corpus.ValueAt(row, "validationResult.inputFieldValidationResults") is not BsonDocument stored)
                continue;

            dictionaries++;
            var wire = rows[row.Id]["validationResult"]!["inputFieldValidationResults"];
            wire.Should().BeOfType<JsonObject>("an object, never the array-of-documents form");
            wire!.AsObject().Select(entry => (entry.Key, entry.Value!.GetValue<bool>())).Should().Equal(stored.Elements.Select(element => (element.Name, element.Value.AsBoolean)), row.Key);
        }

        dictionaries.Should().Be(Corpus.Counts(Corpus.Transaction).A);
        rows[Corpus.IdOf(Corpus.Transaction, "dict-dotted-key")]["validationResult"]!["inputFieldValidationResults"]!.AsObject().ContainsKey("type.subtype").Should().BeTrue();
    }

    [Fact]
    public async Task AB17_an_unwound_row_carries_the_element_under_the_alias_plus_the_index_and_the_element_replaces_the_array()
    {
        var expected = Corpus.Rows(Corpus.Shipment).SelectMany(row => Corpus.Elements(row, "items").Select((_, index) => (row.Id, index))).ToList();

        var aliased = await (await Lab.ClientAsync(LabService.Transport)).SendAsync(Corpus.Shipment, """
            [ { "match": { "items.orderNumber": { "exists": true } } },
              { "unwind": { "path": "items", "as": "it", "includeIndex": "ix" } },
              { "sort": [ { "id": "asc" } ] },
              { "project": { "id": 1, "it.orderNumber": 1, "ix": 1 } },
              { "page": { "limit": 500 } } ]
            """);

        aliased.ShouldHaveIds(expected.Select(entry => entry.Id));
        aliased.Items.Should().OnlyContain(row => row!.AsObject().Select(member => member.Key).Order(StringComparer.Ordinal).SequenceEqual(new[] { "id", "it", "ix" }));
        aliased.Items.Should().OnlyContain(row => row!["it"] is JsonObject);
        aliased.Values("ix").Select(value => value!.GetValue<int>()).Should().Equal(expected.Select(entry => entry.index), "the index is the element's position, not a counter");

        var plain = await (await Lab.ClientAsync(LabService.Transport)).SendAsync(Corpus.Shipment, """[ { "unwind": { "path": "items" } }, { "sort": [ { "id": "asc" } ] }, { "project": { "id": 1, "items.orderNumber": 1 } }, { "page": { "limit": 500 } } ]""");
        plain.ShouldHaveIds(expected.Select(entry => entry.Id));
        plain.Items.Should().OnlyContain(row => row!["items"] is JsonObject, "unaliased, the element still replaces the array at the original path");
    }

    [Fact]
    public async Task AB18_a_grouped_row_carries_exactly_the_by_and_fields_aliases()
    {
        var names = Corpus.Rows(Corpus.Shipment).Select(row => Corpus.Text(row, "status.name")!).Distinct().Order(Comparer<string>.Create(Order.CompareCollated)).ToList();

        var answer = await (await Lab.ClientAsync(LabService.Transport)).SendAsync(Corpus.Shipment, """[ { "group": { "by": [ { "path": "status.name", "as": "k" } ], "fields": { "c": { "count": true } } } }, { "sort": [ { "k": "asc" } ] }, { "page": { "limit": 100 } } ]""");

        answer.ShouldBeOk();
        answer.Strings("k").Should().Equal(names);
        answer.Items.Should().OnlyContain(row => row!.AsObject().Select(member => member.Key).Order(StringComparer.Ordinal).SequenceEqual(new[] { "c", "k" }));
    }

    [Fact]
    public async Task AB19_aggregate_outputs_are_typed_by_the_source_kind_counts_and_decimal_sums_as_strings()
    {
        var buckets = Corpus.Rows(Corpus.Vehicle).GroupBy(row => Corpus.ValueAt(row, "isDeleted")!.AsBoolean).OrderBy(group => group.Key).ToList();
        buckets.Should().HaveCount(2);

        var answer = await (await Lab.ClientAsync(LabService.Fleet)).SendAsync(Corpus.Vehicle, """
            [ { "group": { "by": [ { "path": "isDeleted", "as": "k" } ], "fields": {
                  "c": { "count": true }, "cd": { "countDistinct": "id" },
                  "sumDecimal": { "sum": "mileage" }, "avgDecimal": { "avg": "mileage" },
                  "sumInt": { "sum": "fuelTankCapacity" }, "avgInt": { "avg": "fuelTankCapacity" },
                  "minDecimal": { "min": "mileage" }, "minDate": { "min": "createDateTime" },
                  "firstGuid": { "first": "id" }, "pushString": { "push": "matchCode" } } } },
              { "sort": [ { "k": "asc" } ] },
              { "page": { "limit": 10 } } ]
            """);

        answer.ShouldBeOk();
        answer.Items.Should().HaveCount(2);

        foreach (var (bucket, rows) in answer.Items.Zip(buckets))
        {
            bucket!["k"]!.GetValue<bool>().Should().Be(rows.Key);
            bucket["c"]!.GetValue<string>().Should().Be(rows.Count().ToString(CultureInfo.InvariantCulture), "a count travels as a string of digits");
            bucket["cd"]!.GetValue<string>().Should().Be(rows.Count().ToString(CultureInfo.InvariantCulture));

            // sum and avg over a decimal are decimal strings; over an int, JSON numbers.
            bucket["sumDecimal"]!.GetValueKind().Should().Be(JsonValueKind.String);
            bucket["avgDecimal"]!.GetValueKind().Should().Be(JsonValueKind.String);
            bucket["sumInt"]!.GetValueKind().Should().Be(JsonValueKind.Number);
            bucket["sumInt"]!.GetValue<long>().Should().Be(rows.Sum(row => Corpus.ValueAt(row, "fuelTankCapacity")!.ToInt64()));
            bucket["avgInt"]!.GetValueKind().Should().Be(JsonValueKind.Number);
            bucket["minDecimal"]!.GetValueKind().Should().Be(JsonValueKind.String);
            bucket["minDate"]!.GetValue<string>().Should().Be(rows.Min(row => Corpus.Date(row, "createDateTime"))!.Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            bucket["firstGuid"]!.GetValue<string>().Should().MatchRegex(GuidText().ToString());
            bucket["pushString"]!.AsArray().Select(value => value?.GetValue<string>()).Should().BeEquivalentTo(rows.Select(row => Corpus.Text(row, "matchCode")));
        }

        // The cost, stated: the decimal sum of the live rows is a value no double holds.
        var live = answer.Items.Single(bucket => !bucket!["k"]!.GetValue<bool>())!["sumDecimal"]!.GetValue<string>();
        double.Parse(live, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture).Should().NotBe(live);
    }

    [Fact]
    public async Task AB04_a_long_addon_value_is_written_as_a_string_and_the_value_read_back_finds_its_row()
    {
        var rich = Corpus.IdOf(Corpus.Employee, "addon-rich");
        Corpus.ValueAt(Corpus.Row(Corpus.Employee, "addon-rich"), "addon.tourCount").Should().Be(new BsonInt64(9007199254740993L));
        var client = await Lab.ClientAsync(LabService.Staff);

        var answer = await client.SendAsync(Corpus.Employee, """[ { "match": { "addon.tourCount": { "exists": true } } }, { "project": { "id": 1, "addon": 1 } }, { "page": { "limit": 10 } } ]""");

        answer.ShouldHaveIds([rich]);
        var bag = answer.Items[0]!["addon"]!;
        bag["tourCount"]!.GetValueKind().Should().Be(JsonValueKind.String, "a defined long travels as a string, like every other long");
        bag["tourCount"]!.GetValue<string>().Should().Be("9007199254740993");

        // The round trip closes: the value read back selects the row it came from.
        (await client.SendAsync(Corpus.Employee, $$"""[ { "match": { "addon.tourCount": { "eq": "{{bag["tourCount"]!.GetValue<string>()}}" } } }, { "page": { "limit": 1, "includeTotalCount": true } } ]""")).ShouldHaveTotal(1);

        // A key with no definition has no kind to encode by: it stays what the bag holds.
        bag["plainCount"]!.GetValueKind().Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task AB07_a_date_addon_value_is_written_as_YYYY_MM_DD_and_the_value_read_back_finds_its_row()
    {
        var rich = Corpus.IdOf(Corpus.Employee, "addon-rich");
        var stored = Corpus.Date(Corpus.Row(Corpus.Employee, "addon-rich"), "addon.probationEnd")!.Value;
        var expected = stored.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var client = await Lab.ClientAsync(LabService.Staff);

        var answer = await client.SendAsync(Corpus.Employee, """[ { "match": { "addon.probationEnd": { "exists": true } } }, { "project": { "id": 1, "addon.probationEnd": 1 } }, { "page": { "limit": 10 } } ]""");

        answer.ShouldHaveIds([rich]);
        var read = answer.Items[0]!["addon"]!["probationEnd"]!.GetValue<string>();
        read.Should().Be(expected, "a defined date key travels as YYYY-MM-DD, as every date does");

        // Read a value, filter on it: the commonest round trip there is.
        (await client.SendAsync(Corpus.Employee, $$"""[ { "match": { "addon.probationEnd": { "eq": "{{read}}" } } }, { "page": { "limit": 1, "includeTotalCount": true } } ]""")).ShouldHaveTotal(1);
    }

    [Fact]
    public async Task AB07b_a_date_addon_key_filters_on_YYYY_MM_DD()
    {
        var stored = Corpus.Date(Corpus.Row(Corpus.Employee, "addon-rich"), "addon.probationEnd")!.Value;

        var answer = await (await Lab.ClientAsync(LabService.Staff)).SendAsync(Corpus.Employee,
            $$"""[ { "match": { "addon.probationEnd": { "eq": "{{stored:yyyy-MM-dd}}" } } }, { "page": { "limit": 1, "includeTotalCount": true } } ]""");

        answer.ShouldHaveIds([Corpus.IdOf(Corpus.Employee, "addon-rich")]).ShouldHaveTotal(1);
    }

    [Theory]
    [InlineData(Corpus.Shipment, "loadingTimeType")]
    [InlineData(Corpus.Transaction, "convertState")]
    [InlineData(Corpus.Transaction, "operationItemCombinationMode")]
    public async Task AB10_an_enum_travels_as_its_stored_number_and_an_absent_one_not_at_all(string entityId, string path)
    {
        var rows = (await RowsOf(entityId)).ToDictionary(row => Guid.Parse(row["id"]!.GetValue<string>()));
        var stored = Corpus.Rows(entityId).Select(row => (row.Id, Value: Corpus.ValueAt(row, path))).ToList();
        stored.Should().Contain(entry => entry.Value == null, "the corpus carries an absent enum");

        foreach (var (id, value) in stored)
        {
            if (value is null)
                rows[id].ContainsKey(path).Should().BeFalse($"{id}: the engine invents no value for a member storage does not have");
            else
                rows[id][path]!.GetValue<long>().Should().Be(value.ToInt64(), id.ToString());
        }

        // Every member the enum declares that the corpus stores comes back distinct.
        rows.Values.Where(row => row.ContainsKey(path)).Select(row => row[path]!.GetValue<long>()).Distinct().Should()
            .BeEquivalentTo(stored.Where(entry => entry.Value is not null).Select(entry => entry.Value!.ToInt64()).Distinct());
    }

    [Fact]
    public async Task AB13_the_addon_bag_reaches_the_caller_verbatim_wrappers_spaced_keys_nulls_and_undefined_keys_included()
    {
        var row = Corpus.Row(Corpus.Employee, "addon-rich");
        var stored = Corpus.ValueAt(row, "addon")!.AsBsonDocument;

        var answer = await (await Lab.ClientAsync(LabService.Staff)).SendAsync(Corpus.Employee, """[ { "match": { "addon.tourCount": { "exists": true } } }, { "project": { "id": 1, "addon": 1 } }, { "page": { "limit": 10 } } ]""");

        answer.ShouldHaveIds([row.Id]);
        var bag = answer.Items[0]!["addon"]!.AsObject();
        bag.Select(member => member.Key).Should().Equal(stored.Names, "every stored key, in stored order");
        bag["Ablieferbelege vorhanden"]!.GetValue<string>().Should().Be("ja", "a key with a space");
        bag.ContainsKey("nullKey").Should().BeTrue();
        bag["nullKey"].Should().BeNull("a null inside a bag");
        bag["weight"]!["_t"]!.GetValue<string>().Should().Be(Addons.DecimalDiscriminator, "the driver's decimal wrapper is passed through");
        bag["vincario"]!["_t"]!.GetValue<string>().Should().Be(Addons.DictionaryDiscriminator);
        bag["undefinedKey"]!.GetValue<string>().Should().Be("opaque", "a key with no definition is still projected");
        bag.ContainsKey("retiredKey").Should().BeFalse("a retired definition invents no key");
    }

    [Fact]
    public async Task AB24_a_success_carries_items_and_page_info_and_nothing_else()
    {
        var transport = await Lab.ClientAsync(LabService.Transport);

        var answer = await transport.SendAsync(Corpus.Shipment, """[ { "page": { "limit": 1 } } ]""");
        answer.ShouldBeOk();
        answer.Body!.AsObject().Select(member => member.Key).Order(StringComparer.Ordinal).Should().Equal("items", "pageInfo");

        var counted = await transport.SendAsync(Corpus.Shipment, """[ { "page": { "limit": 1, "includeTotalCount": true } } ]""");
        counted.Body!.AsObject().Select(member => member.Key).Order(StringComparer.Ordinal).Should().Equal("items", "pageInfo");
        counted.PageInfo.Select(member => member.Key).Order(StringComparer.Ordinal).Should().Equal("hasNextPage", "nextCursor", "totalCount", "totalCountCapped");
    }
}
