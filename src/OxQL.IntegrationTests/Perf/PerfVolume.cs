using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Transport;

namespace OxQL.IntegrationTests.Perf;

/// <summary>
/// The volume the performance bench measures over: shipments of organisation C as a service stores
/// them (about 7 KB each: addresses, a status, items, a tour entry, notes), the tours they name and
/// the vehicles those name. No index beside <c>_id</c>, on purpose: what is measured is what the
/// engine's command costs the database when nothing bounds it.
/// </summary>
public static class PerfVolume
{
    /// <summary>How many tours the shipments name.</summary>
    public const int Tours = 500;

    /// <summary>How many vehicles the tours name.</summary>
    public const int Vehicles = 100;

    /// <summary>How many delivery attempts name a shipment: what a lookup from a shipment scans, having no index.</summary>
    public const int Attempts = 20_000;

    /// <summary>The statuses a shipment is in, by ordinal.</summary>
    public static readonly string[] Statuses = ["Open", "Planned", "Loaded", "On tour", "Delivered", "Billed", "Cancelled", "Blocked", "Draft", "Returned", "Archived"];

    private static readonly string[] Companies =
    [
        "Müller Logistik", "Muller Transporte", "Schäfer & Söhne", "Schaefer Cargo", "Straßenbau Weiß", "Strassenbau Weiss", "Özdemir Spedition",
        "Alpha Fracht", "Beta Fracht", "Gamma Fracht", "Delta Kühlung", "Epsilon Schüttgut", "Zeta Baustoffe", "Eta Getränke", "Theta Stahl",
    ];

    private static readonly string Filler = string.Concat(Enumerable.Repeat("Lieferung nur werktags zwischen 7 und 16 Uhr, Rampe 3, Avis 30 Minuten vorher. ", 48));

    /// <summary>The id of shipment <paramref name="n"/>.</summary>
    public static Guid ShipmentId(int n) => Ids.Of(Spaces.Shipment, Org.C, n);

    /// <summary>Writes the volume into the fleet's transport and fleet databases, and says how long it took.</summary>
    public static async Task<TimeSpan> SeedAsync(LabFleet fleet, int shipments)
    {
        var watch = Stopwatch.StartNew();
        var transport = await fleet.DatabaseAsync(LabService.Transport);
        var organisation = Org.C.Id();
        var at = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await (await fleet.DatabaseAsync(LabService.Fleet))
            .GetCollection<BsonDocument>(LabService.Fleet.Model.Entities["fleet.vehicle"].Collection)
            .InsertManyAsync(Enumerable.Range(1, Vehicles).Select(n => new Vehicle
            {
                Id = Ids.Of("20000001", Org.C, n),
                OrganizationId = organisation,
                MatchCode = $"V-{n:D4}",
                Location = "Depot " + (n % 7),
            }.ToBsonDocument()));

        await transport
            .GetCollection<BsonDocument>(LabService.Transport.Model.Entities["transport.tour"].Collection)
            .InsertManyAsync(Enumerable.Range(1, Tours).Select(n => new Tour
            {
                Id = Ids.Of(Spaces.Tour, Org.C, n),
                OrganizationId = organisation,
                Number = $"T-{n:D5}",
                Resource = new TractorUnitResource { Id = Ids.Of("20000001", Org.C, 1 + (n % Vehicles)), OrganizationId = organisation, MatchCode = $"V-{1 + (n % Vehicles):D4}" },
                StartDateTime = at.AddHours(n),
                CreateDateTime = at,
                UpdateDateTime = at,
            }.ToBsonDocument()));

        // Three attempts for each of the first shipments, the rest spread over the others.
        await transport
            .GetCollection<BsonDocument>(LabService.Transport.Model.Entities["transport.delivery_attempt"].Collection)
            .InsertManyAsync(Enumerable.Range(1, Attempts).Select(n => new DeliveryAttempt
            {
                Id = Ids.Of("30000002", Org.C, n),
                OrganizationId = organisation,
                Text = $"Zustellversuch {n}",
                DateTime = at.AddMinutes(n * 7 % 50_000),
                ShipmentId = ShipmentId(1 + (n % Math.Max(1, shipments / 3))),
            }.ToBsonDocument()), new InsertManyOptions { IsOrdered = false });

        var collection = transport.GetCollection<BsonDocument>(Corpus.Entity(Corpus.Shipment).Collection);

        const int Batch = 2_000;

        for (var from = 1; from <= shipments; from += Batch)
            await collection.InsertManyAsync(
                Enumerable.Range(from, Math.Min(Batch, shipments - from + 1)).Select(n => Shipment(n, shipments, organisation, at).ToBsonDocument()),
                new InsertManyOptions { IsOrdered = false });

        return watch.Elapsed;
    }

    private static Shipment Shipment(int n, int of, Guid organisation, DateTime at)
    {
        // A scrambled ordinal, so no sort key follows the insertion order.
        var scrambled = (int)((long)n * 7_919 % of);
        var company = Companies[scrambled % Companies.Length] + " " + (scrambled % 23);

        ContactAddress Address(string name, int salt) => new()
        {
            ContactId = Ids.Of(Spaces.Contact, Org.C, 1 + ((n + salt) % 900)),
            OrganizationId = organisation,
            CompanyName = name,
            Street = "Industriestraße",
            HouseNumber = (1 + ((n + salt) % 180)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Zipcode = (10_000 + ((n * 13 + salt) % 89_000)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            City = "Stadt " + ((n + salt) % 400),
            Country = "Deutschland",
            CountryIso = "DE",
            Latitude = 47 + ((n + salt) % 800) / 100.0,
            Longitude = 6 + ((n + salt) % 900) / 100.0,
            MatchCode = name.ToUpperInvariant(),
        };

        return new Shipment
        {
            Id = ShipmentId(n),
            OrganizationId = organisation,
            IsDeleted = n % 50 == 0,
            CreateDateTime = at.AddMinutes(n),
            UpdateDateTime = at.AddMinutes(n + 5),
            CreateUserId = Ids.Of("30000001", Org.C, 1 + (n % 40)),
            ShipmentNumber = n % 97 == 0 ? null : $"S-{scrambled:D7}",
            ReferenceNumber = $"REF-{n % 5_000:D5}",
            LoadNumber = $"L-{scrambled % 9_000:D5}",
            DeliveryNoteNumber = $"DN-{n:D7}",
            LoadStart = n % 61 == 0 ? null : at.AddMinutes((long)scrambled * 37 % (600 * 24 * 60)),
            LoadEnd = at.AddMinutes((long)scrambled * 37 % (600 * 24 * 60)).AddHours(2),
            DeliveryStart = at.AddMinutes((long)scrambled * 41 % (600 * 24 * 60)).AddDays(1),
            DeliveryEnd = at.AddMinutes((long)scrambled * 41 % (600 * 24 * 60)).AddDays(1).AddHours(3),
            OrderDate = at.AddDays(scrambled % 600),
            LoadAddress = Address(Companies[(n + 3) % Companies.Length], 1),
            DeliveryAddress = Address(Companies[(n + 7) % Companies.Length], 2),
            RecipientAddress = Address(Companies[(n + 9) % Companies.Length], 3),
            SenderAddress = Address(Companies[(n + 11) % Companies.Length], 4),
            Customer = new BillableContact { Address = Address(company, 5), AccountNumber = (10_000 + (scrambled % 3_000)).ToString(System.Globalization.CultureInfo.InvariantCulture) },
            Carrier = new BillableContact { Address = Address(Companies[(n + 5) % Companies.Length], 6) },
            FreightPayer = new BillableContact { Address = Address(company, 7) },
            InvoiceRecipient = new BillableContact { Address = Address(company, 8) },
            Status = new ShipmentStatus
            {
                Id = Ids.Of(Spaces.ShipmentStatus, Org.C, 1 + (scrambled % Statuses.Length)),
                OrganizationId = organisation,
                Name = Statuses[scrambled % Statuses.Length],
                Number = (1 + (scrambled % Statuses.Length)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                OrderNr = scrambled % Statuses.Length,
                HexColor = "#336699",
            },
            ActualWeight = new Quantity { Value = 100 + (scrambled % 24_000) + 0.1 * (n % 10) },
            Items = [.. Enumerable.Range(0, n % 6).Select(position => new ShipmentItem
            {
                Id = Ids.Of(Spaces.ShipmentItem, Org.C, (long)n * 10 + position),
                Text = $"Position {position} von {n}",
                OrderNumber = position,
                Reference = $"IT-{(n * 31 + position * 7) % 10_000:D5}",
                LoadingMeters = 0.4 * (1 + ((n + position) % 30)),
                ShippingUnit = position % 2 == 0 ? "Palette" : "Kiste",
                Quantity = new Quantity { Value = 1 + ((n + position) % 33) },
                Weight = new Quantity { Value = 20.5 * (1 + ((n + position) % 40)) },
                Article = new Article { Id = Ids.Of(Spaces.ShipmentArticle, Org.C, 1 + ((n + position) % 250)), Number = $"A-{(n + position) % 250:D4}", Name = "Artikel " + ((n + position) % 250) },
            })],
            Tours = n % 3 == 0 ? [] : [new ShipmentTour { Id = Ids.Of(Spaces.ShipmentTourEntry, Org.C, n), Number = $"T-{1 + (n % Tours):D5}", TourId = Ids.Of(Spaces.Tour, Org.C, 1 + (n % Tours)) }],
            Notes = Filler,
            ExternalNotes = Filler[..(200 + (n % 600))],
            ConstructionSite = "Baustelle " + (n % 120),
            Incoterm = n % 2 == 0 ? "DAP" : "EXW",
        };
    }
}
