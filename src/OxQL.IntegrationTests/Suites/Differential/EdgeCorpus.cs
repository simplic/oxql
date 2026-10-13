using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Fleet.Models.Conformance;
using OxQL.IntegrationTests.Fleet.Models.Staff;
using OxQL.IntegrationTests.Harness;

namespace OxQL.IntegrationTests.Suites.Differential;

/// <summary>
/// The rows the differential suites read: a fleet of its own whose conformance and staff databases
/// hold generated rows made for the edges of an order, a comparison and a join. Every member takes
/// its values from a short list by the row's number under a modulus of its own, so the members vary
/// against each other: ties on every sort member, a member stored as null and one not stored at all,
/// collections that are absent, null, empty and filled, numbers stored as Int32, Int64, Double and
/// Decimal128 under one member, dates at both ends of the calendar and at the changes of the clock,
/// text that differs by case and accent only, text of regular-expression characters, the empty
/// string and a long one, references that hit, miss and are not there. The key of a row is no
/// function of the order the rows are written in, so an answer that is in order was ordered.
/// <para>
/// Nothing is expected by hand here: a suite computes what it expects from <see cref="Stored"/>
/// (<see cref="EdgeOracle"/>) or from one answer that holds every row.
/// </para>
/// </summary>
public static class EdgeCorpus
{
    public const string Entity = "conformance.entity";
    public const string Ref = "conformance.ref";
    public const string Child = "conformance.child";
    public const string Employee = "staff.employee";

    /// <summary>The rows of organisation A.</summary>
    public const int Count = 90;

    /// <summary>The rows of organisation B: the same generator over other numbers, so a scope that does not fire answers more rows.</summary>
    public const int CountB = 12;

    private static readonly Lazy<Task<CorpusFleet>> Shared = new(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyList<BsonDocument>> RowsA = new(() => [.. Enumerable.Range(1, Count).Select(n => Row(Org.A, n))]);

    private static readonly Lazy<IReadOnlyList<BsonDocument>> RowsB = new(() => [.. Enumerable.Range(1, CountB).Select(n => Row(Org.B, n))]);

    /// <summary>The stored rows of the entity in an organisation, in the order they were written.</summary>
    public static IReadOnlyList<BsonDocument> Stored(Org org = Org.A) => org == Org.A ? RowsA.Value : RowsB.Value;

    /// <summary>The stored children, every organisation.</summary>
    public static IReadOnlyList<BsonDocument> Children { get; } = [.. BuildChildren()];

    /// <summary>The stored reference targets, every organisation.</summary>
    public static IReadOnlyList<BsonDocument> Refs { get; } = [.. BuildRefs()];

    /// <summary>The stored employees another service owns, every organisation.</summary>
    public static IReadOnlyList<BsonDocument> Employees { get; } = [.. BuildEmployees()];

    /// <summary>The fleet, seeded on first use and kept for the run.</summary>
    public static Task<CorpusFleet> FleetAsync() => Shared.Value;

    /// <summary>A client of the conformance service of the fleet.</summary>
    public static async Task<LabClient> ClientAsync(Org org = Org.A, int? contract = 2) => (await FleetAsync()).Client(LabService.Conformance, org, contract);

    /// <summary>The key of row <paramref name="n"/>: scattered, so the order of the keys is not the order of the numbers.</summary>
    public static Guid IdOf(Org org, int n) => Ids.Of(Spaces.ConformanceEntity, org, 5_000 + (n * 37 % 1_009));

    public static Guid ChildIdOf(Org org, int n) => Ids.Of(Spaces.ConformanceChild, org, 5_000 + (n * 53 % 1_009));

    public static Guid EmployeeIdOf(Org org, int n) => Ids.Of(Spaces.Employee, org, 5_000 + (n * 29 % 1_009));

    /// <summary>The key of a stored row, as the wire writes it.</summary>
    public static string WireId(BsonDocument row) => row["_id"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard).ToString("D");

    private static async Task<CorpusFleet> StartAsync() =>
        await CorpusFleet.CreateAsync("edge", seed: [], extra: async fleet =>
        {
            var conformance = await fleet.DatabaseAsync(LabService.Conformance);
            var staff = await fleet.DatabaseAsync(LabService.Staff);
            var model = LabService.Conformance.Model;

            await conformance.GetCollection<BsonDocument>(model.Entities[Entity].Collection).InsertManyAsync(Stored(Org.A).Concat(Stored(Org.B)));
            await conformance.GetCollection<BsonDocument>(model.Entities[Ref].Collection).InsertManyAsync(Refs);
            await conformance.GetCollection<BsonDocument>(model.Entities[Child].Collection).InsertManyAsync(Children);
            await staff.GetCollection<BsonDocument>(LabService.Staff.Model.Entities[Employee].Collection).InsertManyAsync(Employees);
        });

    // ── the values ──────────────────────────────────────────────────────────────────────────

    /// <summary>Names that differ by case and accent only, names of pattern characters, the empty one.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "Alpha", "alpha", "ALPHA", "Älpha", "Bravo", "bravo", "Müller", "Muller", "MULLER", "Mueller", "Straße", "Strasse", "STRASSE",
        "a.b*c", "a+b(c)", "Zulu [x]", "x\\y", "50% off", "under_score", "", "Zebra", "çedille", "Cedille",
    ];

    public static readonly IReadOnlyList<string> Codes = ["A-1", "a-1", "B-2", "Ä-1", "C.3", "c.3", "D*4"];

    /// <summary>Instants at both ends of the calendar, at the changes of the clock in Berlin, at the turn of a week, a month and a year.</summary>
    public static readonly IReadOnlyList<DateTime> Moments =
    [
        Utc("0001-01-01T00:00:00Z"), Utc("9999-12-31T23:59:59.999Z"), Utc("2026-03-28T23:30:00Z"), Utc("2026-03-29T00:30:00Z"), Utc("2026-03-29T01:30:00Z"),
        Utc("2026-10-24T22:30:00Z"), Utc("2026-10-25T00:30:00Z"), Utc("2026-10-25T01:30:00Z"), Utc("2025-12-31T23:30:00Z"), Utc("2026-01-04T23:30:00Z"),
        Utc("2026-01-05T00:00:00Z"), Utc("2026-06-15T12:00:00Z"), Utc("2026-06-15T12:00:00Z"), Utc("1969-12-31T23:59:59.999Z"), Utc("1970-01-01T00:00:00Z"),
    ];

    private static DateTime Utc(string iso) => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static readonly long[] Magnitudes = [9007199254740993L, 9007199254740992L, 9007199254740991L, -9007199254740993L, 0L, 42L, long.MaxValue, long.MinValue];

    private static readonly decimal[] Amounts = [0m, 1234.50m, 99.99m, -5m, 0.1m, 1234.5m, 79228162514264337593543950335m, -0.0000000000000000000000000001m];

    private static readonly double[] Ratios = [0d, 0.25d, -1.5d, 1e300d, -1e300d, 0.1d, 2.5d];

    private static readonly string[] Durations = ["00:00:00", "01:30:00", "10:00:00", "00:00:01", "23:59:59"];

    private static readonly ConformanceState[] States = [ConformanceState.Draft, ConformanceState.Active, ConformanceState.Huge];

    private static readonly string?[] RefCodes = ["REF-A", "REF-B", "ref-a", "REF-MISSING", "REF-C", null];

    // ── the rows ────────────────────────────────────────────────────────────────────────────

    private static BsonDocument Row(Org org, int n)
    {
        var name = Names[n * 7 % Names.Count];
        var moment = Moments[n * 4 % Moments.Count];
        var model = new ConformanceEntity
        {
            Id = IdOf(org, n),
            OrganizationId = org.Id(),
            IsDeleted = n % 11 == 0,
            Name = name,
            Note = (n % 5) switch { 0 => "Note-1", 1 => "note-1", 2 => "nöte-2", 3 => "", _ => "zeta" },
            Count = n % 5,
            OptionalCount = n % 4,
            Magnitude = Magnitudes[n % Magnitudes.Length],
            OptionalMagnitude = Magnitudes[n * 3 % Magnitudes.Length],
            Amount = Amounts[n % Amounts.Length],
            OptionalAmount = Amounts[n * 3 % Amounts.Length],
            Ratio = Ratios[n % Ratios.Length],
            OptionalRatio = Ratios[n * 2 % Ratios.Length],
            Active = n % 2 == 0,
            OptionalActive = n % 3 == 0,
            Marker = Ids.Of(Spaces.ConformanceEntity, Org.A, n % 6),
            OptionalMarker = Ids.Of(Spaces.ConformanceEntity, Org.A, n % 4),
            Moment = moment,
            OptionalMoment = Moments[n * 7 % Moments.Count],
            Day = DateOnly.FromDateTime(Moments[n * 2 % Moments.Count]),
            OptionalDay = DateOnly.FromDateTime(Moments[n * 11 % Moments.Count]),
            Duration = TimeSpan.Parse(Durations[n % Durations.Length], CultureInfo.InvariantCulture),
            OptionalDuration = TimeSpan.Parse(Durations[n * 2 % Durations.Length], CultureInfo.InvariantCulture),
            Grade = "AaBbZ"[n % 5],
            OptionalGrade = "AaBbZ"[n * 3 % 5],
            Payload = [(byte)(n % 3), 2],
            OptionalPayload = [(byte)(n % 2)],
            State = States[n % States.Length],
            OptionalState = States[n * 2 % States.Length],
            Address = new ConformanceAddress { City = n % 4 == 0 ? "Köln" : n % 4 == 1 ? "koln" : n % 4 == 2 ? "Berlin" : "", Zip = n % 3 == 0 ? null : "5" + (n % 3), Floor = n % 3 },
            OptionalAddress = new ConformanceAddress { City = "City " + (n % 3), Zip = n % 2 == 0 ? null : "Z" + (n % 4), Floor = n % 2 },
            Tags = (n % 6) switch { 0 => [], 1 => ["alpha"], 2 => ["alpha", "beta"], 3 => ["Beta", "ALPHA", "gamma"], 4 => ["älpha", "gamma"], _ => ["beta", "beta"] },
            Items = [.. Enumerable.Range(0, n % 4).Select(position => new ConformanceItem
            {
                Id = Ids.Of(Spaces.ConformanceItem, org, n * 10 + position),
                Code = Codes[(n + position * 3) % Codes.Count],
                Quantity = (n + position) % 4,
                Parts = [.. Enumerable.Range(0, (n + position) % 3).Select(part => new ConformancePart { Sku = $"SKU-{(n + part) % 4}", Weight = Amounts[(n + part) % 4] })],
            })],
            Labels = new() { ["colour"] = n % 2 == 0 ? "red" : "blue" },
            Quantities = [],
            Opaque = null,
            RefCode = RefCodes[n % RefCodes.Length],
            ChildId = n % 5 == 0 ? ChildIdOf(org, 900 + n) : ChildIdOf(org, 1 + (n % 7)),
            EmployeeId = n % 6 == 0 ? EmployeeIdOf(org, 900 + n) : EmployeeIdOf(org, 1 + (n % 5)),
            WidgetCode = null,
            WidgetCodeExplicit = null,
            Addon = [],
        };

        var row = model.ToBsonDocument();

        // What the model cannot say: a member stored as null where it is not nullable, one not stored
        // at all, a number of another type, a collection that is not one.
        Vary(row, "Note", n, 6, missing: 4, @null: 5);
        Vary(row, "OptionalCount", n, 7, missing: 5, @null: 6);
        Vary(row, "OptionalMagnitude", n, 9, missing: 7, @null: 8);
        Vary(row, "OptionalAmount", n, 8, missing: 6, @null: 7);
        Vary(row, "OptionalRatio", n, 9, missing: 7, @null: 8);
        Vary(row, "OptionalActive", n, 5, missing: 3, @null: 4);
        Vary(row, "OptionalMarker", n, 7, missing: 5, @null: 6);
        Vary(row, "OptionalMoment", n, 8, missing: 6, @null: 7);
        Vary(row, "OptionalDay", n, 7, missing: 5, @null: 6);
        Vary(row, "OptionalDuration", n, 6, missing: 4, @null: 5);
        Vary(row, "OptionalGrade", n, 6, missing: 4, @null: 5);
        Vary(row, "OptionalState", n, 7, missing: 5, @null: 6);
        Vary(row, "OptionalAddress", n, 5, missing: 3, @null: 4);
        Vary(row, "Tags", n, 9, missing: 7, @null: 8);
        Vary(row, "Items", n, 11, missing: 9, @null: 10);
        Vary(row, "RefCode", n, 13, missing: 11, @null: 12);
        Vary(row, "ChildId", n, 9, missing: 7, @null: 8);
        Vary(row, "EmployeeId", n, 8, missing: 6, @null: 7);

        // One member, four numeric types: a count written by four versions of a service.
        if (row.TryGetValue("OptionalCount", out var count) && count is BsonInt32 stored)
            row["OptionalCount"] = (n % 4) switch { 0 => stored, 1 => new BsonInt64(stored.Value), 2 => new BsonDouble(stored.Value), _ => new BsonDecimal128(stored.Value) };

        // An element without its members, and a member of an element stored as null.
        if (row.TryGetValue("Items", out var items) && items is BsonArray { Count: > 0 } elements)
        {
            if (n % 8 == 3)
                elements[0].AsBsonDocument.Remove("Code");

            if (n % 8 == 5)
                elements[^1].AsBsonDocument["Code"] = BsonNull.Value;

            if (n % 10 == 7)
                elements[0].AsBsonDocument.Remove("Parts");
        }

        return row;
    }

    private static void Vary(BsonDocument row, string member, int n, int modulus, int missing, int @null)
    {
        if (n % modulus == missing)
            row.Remove(member);
        else if (n % modulus == @null)
            row[member] = BsonNull.Value;
    }

    private static IEnumerable<BsonDocument> BuildRefs()
    {
        foreach (var (org, suffix) in new[] { (Org.A, ""), (Org.B, "-B") })
        {
            yield return new ConformanceRef { Code = "REF-A" + suffix, Name = "Alpha reference", Rank = 2, OrganizationId = org.Id() }.ToBsonDocument();
            yield return new ConformanceRef { Code = "REF-B" + suffix, Name = "alpha reference", Rank = 1, OrganizationId = org.Id() }.ToBsonDocument();
            yield return new ConformanceRef { Code = "REF-C" + suffix, Name = "Beta", Rank = 1, OrganizationId = org.Id(), IsDeleted = true }.ToBsonDocument();
        }
    }

    /// <summary>The children: seven records a row's <c>childId</c> may name, and none to five children per parent by its number, with ties and nulls on what a lookup sorts by.</summary>
    private static IEnumerable<BsonDocument> BuildChildren()
    {
        foreach (var org in new[] { Org.A, Org.B })
        {
            var rows = org == Org.A ? Count : CountB;

            for (var k = 1; k <= 7; k++)
            {
                var named = new ConformanceChild { Id = ChildIdOf(org, k), OrganizationId = org.Id(), ParentId = IdOf(org, 2_000 + k), Name = k % 3 == 0 ? "child" : "Child " + (k % 3), Sequence = k % 3, IsDeleted = k == 7 }.ToBsonDocument();

                if (k == 4)
                    named["Name"] = BsonNull.Value;

                yield return named;
            }

            for (var n = 1; n <= rows; n++)
            {
                for (var at = 0; at < n % 6; at++)
                {
                    var child = new ConformanceChild { Id = ChildIdOf(org, 100 + n * 6 + at), OrganizationId = org.Id(), ParentId = IdOf(org, n), Name = "kid " + ((n + at) % 3), Sequence = (n + at) % 3, IsDeleted = (n + at) % 7 == 0 }.ToBsonDocument();

                    if ((n + at) % 5 == 0)
                        child["Name"] = BsonNull.Value;

                    yield return child;
                }
            }

            // A child of organisation B under a parent of organisation A: the scope of a lookup hides it.
            if (org == Org.B)
                yield return new ConformanceChild { Id = ChildIdOf(org, 990), OrganizationId = org.Id(), ParentId = IdOf(Org.A, 1), Name = "of another organisation", Sequence = 0 }.ToBsonDocument();
        }
    }

    private static IEnumerable<BsonDocument> BuildEmployees()
    {
        foreach (var org in new[] { Org.A, Org.B })
        {
            for (var k = 1; k <= 5; k++)
            {
                var employee = new Employee
                {
                    Id = EmployeeIdOf(org, k),
                    OrganizationId = org.Id(),
                    // Two spellings of one code, so a comparison that folds case and one that does not select other rows.
                    MatchCode = k == 2 ? "EMP-EVEN" : k == 4 ? "Emp-Even" : "emp-" + k,
                    IsDeleted = k == 5,
                    Children = k % 3,
                }.ToBsonDocument();

                if (k == 3)
                    employee["MatchCode"] = BsonNull.Value;

                yield return employee;
            }
        }
    }
}
