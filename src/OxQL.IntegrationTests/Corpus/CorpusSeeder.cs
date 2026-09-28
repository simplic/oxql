using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures.Rows;
using OxQL.IntegrationTests.Fleet;

namespace OxQL.IntegrationTests.Fixtures;

/// <summary>
/// Writes the corpus into a fleet's databases: every row of every entity of the services asked
/// for, as <see cref="CorpusRow.Stored"/> (the driver's serialization of the model, then the raw
/// overrides), the report scenario rows of organisation R (<see cref="ReportSeed"/>), and the
/// addon definitions each organisation declares, into the collection the host's addon source reads. Callers go through <see cref="CorpusFleet"/>, which seeds a fleet
/// once; the seeder itself writes whatever it is told.
/// </summary>
public static class CorpusSeeder
{
    /// <summary>Writes the rows and definitions of <paramref name="services"/> into <paramref name="fleet"/>, and says how long it took.</summary>
    public static async Task<TimeSpan> SeedAsync(LabFleet fleet, IEnumerable<LabService> services, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var wanted = services.ToHashSet();

        await Task.WhenAll(Corpus.Entities.Where(entity => wanted.Contains(entity.Service)).Select(async entity =>
        {
            var database = await fleet.DatabaseAsync(entity.Service);
            var rows = entity.Rows(Org.A).Concat(entity.Rows(Org.B)).Select(row => row.Stored).ToList();

            if (rows.Count > 0)
                await database.GetCollection<BsonDocument>(entity.Collection).InsertManyAsync(rows, new InsertManyOptions { IsOrdered = false }, cancellationToken);
        }));

        // The report scenario rows, all in organisation R: nothing a case over A or B reads.
        await ReportSeed.SeedAsync(fleet, wanted, cancellationToken);

        foreach (var service in wanted)
        {
            var definitions = Definitions(service).ToList();

            if (definitions.Count > 0)
                await (await fleet.DatabaseAsync(service)).GetCollection<BsonDocument>(TestAddonSource.Collection).InsertManyAsync(definitions, cancellationToken: cancellationToken);
        }

        return watch.Elapsed;
    }

    /// <summary>
    /// The stored addon definitions of a service, in storage order. Ordinals count per service
    /// across its entities and organisations; the retired shadow of a live key takes ordinal 900.
    /// </summary>
    public static IEnumerable<BsonDocument> Definitions(LabService service)
    {
        var ordinal = 0;

        foreach (var entity in Corpus.Entities.Where(entity => entity.Service == service))
        {
            foreach (var (org, definitions) in entity.AddonDefinitions.OrderBy(pair => pair.Key))
            {
                foreach (var definition in definitions)
                {
                    var n = ReferenceEquals(definition, Addons.RetiredShadow) ? 900 : ++ordinal;

                    yield return TestAddonSource.Document(
                        Ids.Of(Spaces.AddonDefinition, org, n), org.Id(), entity.Id, definition.Path, definition.Kind,
                        definition.DisplayName, definition.Retired, definition.Values);
                }
            }
        }
    }
}

/// <summary>
/// The bulk volume: 100 001 thin <c>transport.shipment_template</c> rows in organisation C, one
/// above the default count cap, so a capped total, a truncated walk and a count the cap does not
/// reach are observable. Organisation C holds nothing else, so no other test's counts move.
/// Written raw, as thin documents carrying only what a count or a page walk reads.
/// </summary>
public static class BulkRows
{
    /// <summary>One above the default <c>CountCap</c> of 100 000.</summary>
    public const int Count = 100_001;

    /// <summary>The id of bulk ordinal <paramref name="n"/>: the template space under organisation C's tag.</summary>
    public static Guid IdOf(int n) => Ids.Of(Spaces.ShipmentTemplate, Org.C, n);

    /// <summary>The templateName of bulk ordinal <paramref name="n"/>.</summary>
    public static string NameOf(int n) => $"ZZ-BULK-{n:D6}";

    /// <summary>Writes the bulk rows into the fleet's transport database, and says how long it took.</summary>
    public static async Task<TimeSpan> SeedAsync(LabFleet fleet, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var collection = (await fleet.DatabaseAsync(LabService.Transport)).GetCollection<BsonDocument>(Corpus.Entity(Corpus.Template).Collection);
        var at = new BsonDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var organisation = new BsonBinaryData(Org.C.Id(), GuidRepresentation.Standard);

        const int Batch = 10_000;

        for (var from = 1; from <= Count; from += Batch)
        {
            var documents = Enumerable.Range(from, Math.Min(Batch, Count - from + 1)).Select(n => new BsonDocument
            {
                ["_id"] = new BsonBinaryData(IdOf(n), GuidRepresentation.Standard),
                ["OrganizationId"] = organisation,
                ["TemplateName"] = NameOf(n),
                ["ShipmentNumber"] = $"ZB-{n:D6}",
                ["IsShipmentConversionDisabled"] = false,
                ["Items"] = new BsonArray(),
                ["Documents"] = new BsonArray(),
                ["Tags"] = new BsonArray(),
                ["BillingLines"] = new BsonArray(),
                ["CreateDateTime"] = at,
                ["UpdateDateTime"] = at,
                ["CreateUserName"] = "bulk",
                ["UpdateUserName"] = "bulk",
                ["IsDeleted"] = false,
            });

            await collection.InsertManyAsync(documents, new InsertManyOptions { IsOrdered = false }, cancellationToken);
        }

        return watch.Elapsed;
    }
}
