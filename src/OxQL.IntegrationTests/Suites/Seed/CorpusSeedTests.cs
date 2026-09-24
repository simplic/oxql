using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.IntegrationTests.Suites.Seed;

/// <summary>
/// The seeder wrote what the corpus says, in the forms it says: every entity answers exactly its
/// corpus rows per organisation, and the deliberate storage forms are in the database as raw
/// BSON, read back past the engine.
/// </summary>
[Trait("Category", "Integration")]
public class CorpusSeedTests(ITestOutputHelper output)
{
    public static TheoryData<string> EntityIds => new(Corpus.Entities.Select(entity => entity.Id));

    [Theory]
    [MemberData(nameof(EntityIds))]
    public async Task Every_entity_answers_exactly_its_corpus_rows_in_each_organisation(string entityId)
    {
        foreach (var org in new[] { Org.A, Org.B })
        {
            var client = await Lab.ClientForAsync(entityId, org);
            var expected = Corpus.Rows(entityId, org);

            if (expected.Count > 500)
            {
                var counted = await client.SendAsync(entityId, """[{ "project": { "id": 1 } }, { "page": { "limit": 1, "includeTotalCount": true } }]""");

                counted.ShouldHaveTotal(expected.Count, capped: false);
                continue;
            }

            var key = Corpus.Entity(entityId).KeyPath;
            var answer = await client.SendAsync(entityId, $$"""[{ "sort": [{ "{{key}}": "asc" }] }, { "page": { "limit": 500, "includeTotalCount": true } }]""");

            answer.ShouldHaveTotal(expected.Count);
            answer.Strings(key)
                .Should().Equal(expected.Order(Comparer<CorpusRow>.Create(Corpus.CompareIds)).Select(row => row.WireId), $"{entityId} in organisation {org}");
        }
    }

    [Fact]
    public async Task The_shared_fleet_is_seeded_once_and_says_how_long_it_took()
    {
        var first = await CorpusFleet.SharedAsync();
        var second = await CorpusFleet.SharedAsync();

        second.Should().BeSameAs(first);
        first.Seeded.Should().BeEquivalentTo(LabService.All);
        output.WriteLine($"corpus seed: {first.SeedTime.TotalMilliseconds:F0} ms for {Corpus.Entities.Sum(entity => entity.Rows(Org.A).Count + entity.Rows(Org.B).Count)} rows");
    }

    [Fact]
    public async Task The_bulk_volume_is_seeded_on_demand_in_organisation_C_once()
    {
        var fleet = await CorpusFleet.SharedAsync();

        var took = await fleet.SeedBulkAsync();
        (await fleet.SeedBulkAsync()).Should().Be(took, "the second call waits for the first write instead of writing again");
        output.WriteLine($"bulk seed: {took.TotalMilliseconds:F0} ms for {BulkRows.Count} rows");

        // One count answers the cap, so two counts either side of the last name add up to the truth.
        var client = fleet.Client(LabService.Transport, Org.C);
        var last = BulkRows.NameOf(BulkRows.Count);
        var below = await client.MatchCountAsync(Corpus.Template, $$"""{ "templateName": { "lt": "{{last}}" } }""");
        var from = await client.MatchCountAsync(Corpus.Template, $$"""{ "templateName": { "gte": "{{last}}" } }""");

        (below + from).Should().Be(BulkRows.Count);
        (await fleet.Client(LabService.Transport).MatchCountAsync(Corpus.Template, """{ "templateName": { "startsWith": "ZZ-BULK" } }""")).Should().Be(0, "organisation A sees none of it");
    }

    [Fact]
    public async Task The_addon_bag_is_stored_in_the_forms_the_driver_writes()
    {
        var stored = await StoredAsync(LabService.Staff, "employee", Corpus.IdOf(Corpus.Employee, "addon-rich"));
        var bag = stored["Addon"].AsBsonDocument;

        bag["weight"]["_t"].AsString.Should().Be(Addons.DecimalDiscriminator);
        bag["weight"]["_v"].BsonType.Should().Be(BsonType.Decimal128);
        bag["tourCount"].BsonType.Should().Be(BsonType.Int64);
        bag["tourCount"].AsInt64.Should().Be(9007199254740993L);
        bag["probationEnd"].BsonType.Should().Be(BsonType.DateTime);
        bag["plainCount"].BsonType.Should().Be(BsonType.Int32);
        bag["ratio"].BsonType.Should().Be(BsonType.Double);
        bag["nullKey"].IsBsonNull.Should().BeTrue();
        bag["legacyRef"].BsonType.Should().Be(BsonType.String);
        bag["vincario"]["_t"].AsString.Should().Be(Addons.DictionaryDiscriminator);
        bag["vincario"]["_v"]["make"].AsString.Should().Be("MAN");
        bag["vincario"]["_v"]["ratio"]["_t"].AsString.Should().Be(Addons.DecimalDiscriminator);

        var missing = await StoredAsync(LabService.Staff, "employee", Corpus.IdOf(Corpus.Employee, "addon-missing"));

        missing.Contains("Addon").Should().BeFalse();
    }

    [Fact]
    public async Task The_raw_overrides_are_in_storage_and_the_oracle_reads_them()
    {
        var vehicle = await StoredAsync(LabService.Fleet, "vehicle", Corpus.IdOf(Corpus.Vehicle, "dec-string"));

        vehicle["Mileage"].Should().Be(new BsonString("125000.00"));
        vehicle["QRCode"].IsBsonNull.Should().BeTrue("qrCode is stored under the name the attribute gives");
        Corpus.ValueAt(Corpus.Row(Corpus.Vehicle, "dec-string"), "mileage").Should().Be(new BsonString("125000.00"));

        var absentEnum = await StoredAsync(LabService.Transport, "shipment", Corpus.IdOf(Corpus.Shipment, "enum-absent"));
        var unknownEnum = await StoredAsync(LabService.Transport, "shipment", Corpus.IdOf(Corpus.Shipment, "enum-unknown-value"));
        var anyShipment = await StoredAsync(LabService.Transport, "shipment", Corpus.IdOf(Corpus.Shipment, "items-one"));

        absentEnum.Contains("LoadingTimeType").Should().BeFalse();
        unknownEnum["LoadingTimeType"].Should().Be(new BsonInt32(99));
        anyShipment["Items"][0].AsBsonDocument.Contains("Addon").Should().BeFalse("an item's own dictionary is left out");
        anyShipment["Items"][0]["_id"].BsonType.Should().Be(BsonType.Binary, "an id is _id at every depth");
        anyShipment["Department"]["_id"].Should().Be(new BsonBinaryData(Ids.Of(Spaces.Department, Org.A, 1), GuidRepresentation.Standard), "the shipment department names a fleet department");

        var tiny = await StoredAsync(LabService.Ledger, "transaction", Corpus.IdOf(Corpus.Transaction, "dec-many-places"));
        var dotted = await StoredAsync(LabService.Ledger, "transaction", Corpus.IdOf(Corpus.Transaction, "dict-dotted-key"));

        tiny["TotalPrice"].AsDecimal128.Should().Be(Decimal128.Parse("1E-30"));
        dotted["ValidationResult"]["InputFieldValidationResults"].AsBsonDocument.Names.Should().Contain("type.subtype");

        var template = await StoredAsync(LabService.Transport, "shipment_template", Ids.Of(Spaces.ShipmentTemplate, Org.A, 3001));

        template.Contains("TemplateName").Should().BeFalse();
        template.Contains("Addon").Should().BeFalse();
        (await StoredAsync(LabService.Transport, "shipment_template", Ids.Of(Spaces.ShipmentTemplate, Org.A, 1)))["LoadStart"]["RelativeTime"].Should().Be(new BsonString("01:30:00"));
    }

    [Fact]
    public async Task The_conformance_kinds_are_stored_in_the_driver_forms()
    {
        var alpha = await StoredAsync(LabService.Conformance, "conformance", Corpus.IdOf(Corpus.Conformance, "c-alpha"));

        alpha["Magnitude"].Should().Be(new BsonInt64(9007199254740993L));
        alpha["Grade"].Should().Be(new BsonInt32('A'));
        alpha["Duration"].Should().Be(new BsonString("01:30:00"));
        alpha["Day"].BsonType.Should().Be(BsonType.DateTime);
        alpha["Payload"].BsonType.Should().Be(BsonType.Binary);
        alpha["State"].Should().Be(new BsonInt64(1));
        alpha["Quantities"].BsonType.Should().Be(BsonType.Array, "a Guid-keyed dictionary is an array of key/value documents");
        alpha["Labels"]["with space"].AsString.Should().Be("yes");
        alpha.Contains("Scratch").Should().BeFalse("an ignored member is never stored");
        alpha.Contains("Computed").Should().BeFalse("a get-only member is never stored");

        var reference = await StoredAsync(LabService.Conformance, "conformance_ref", "REF-A");

        reference["Name"].AsString.Should().Be("Alpha reference");
    }

    [Fact]
    public async Task Every_organisation_declares_the_addon_definitions_the_corpus_lists()
    {
        var fleet = await CorpusFleet.SharedAsync();
        var transport = (await fleet.Fleet.DatabaseAsync(LabService.Transport)).GetCollection<BsonDocument>(TestAddonSource.Collection);
        var conformance = (await fleet.Fleet.DatabaseAsync(LabService.Conformance)).GetCollection<BsonDocument>(TestAddonSource.Collection);

        var shipment = await transport.Find(new BsonDocument("Entity", Corpus.Shipment)).ToListAsync();

        shipment.Should().HaveCount(12 + 12 + 1);
        shipment[0]["Path"].AsString.Should().Be("weight");
        shipment[0]["Retired"].AsBoolean.Should().BeTrue("the retired twin is stored first, on purpose");
        (await conformance.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()).Select(definition => definition["Path"].AsString)
            .Should().Equal("Ablieferbelege vorhanden", "contractNumber", "status", "vincario", "zuschlag");
    }

    private static async Task<BsonDocument> StoredAsync(LabService service, string collection, BsonValue id)
    {
        var fleet = await CorpusFleet.SharedAsync();
        var found = await (await fleet.Fleet.DatabaseAsync(service)).GetCollection<BsonDocument>(collection).Find(new BsonDocument("_id", id)).FirstOrDefaultAsync();

        return found ?? throw new InvalidOperationException($"No {collection} row {id}.");
    }

    private static Task<BsonDocument> StoredAsync(LabService service, string collection, Guid id) =>
        StoredAsync(service, collection, new BsonBinaryData(id, GuidRepresentation.Standard));
}
