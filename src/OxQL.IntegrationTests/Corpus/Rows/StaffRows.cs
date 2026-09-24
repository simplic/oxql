using OxQL.IntegrationTests.Fleet.Models.Staff;
using static OxQL.IntegrationTests.Fixtures.Rows.RowKit;

namespace OxQL.IntegrationTests.Fixtures.Rows;

/// <summary>
/// <c>staff.employee</c>, the string entity: null, missing and empty; duplicate and null sort
/// keys; case, accents, CJK, an astral code point and the Turkish dotted I; collections of zero,
/// one and several, and absent; a nullable object null and absent; the full addon bag and none.
/// Flat, no enum, no nested collection. 26 rows in A, 3 clones in B.
/// </summary>
internal static class StaffRows
{
    public const string Entity = "staff.employee";

    public static IReadOnlyList<CorpusRow> For(Org org) => org switch
    {
        Org.A => A,
        Org.B => B,
        _ => [],
    };

    private static readonly IReadOnlyList<CorpusRow> A =
    [
        R(1, "plain", "the baseline: every member present and non-empty"),
        R(2, "matchcode-null", "matchCode is null: a null in a sort key", (e, _) => { e.MatchCode = null; e.Address.LastName = "Becker"; }),
        R(3, "matchcode-missing", "matchCode is absent: missing, which is not null", (e, raw) => { e.MatchCode = null; e.Address.LastName = "Becker"; raw.Unset("matchCode", "missing, which is not null"); }),
        R(4, "matchcode-empty", "matchCode is the empty string: neither null nor missing", (e, _) => { e.MatchCode = ""; e.Address.LastName = "Becker"; }),
        R(5, "dup-a", "duplicate sort key, first by id", (e, _) => { e.MatchCode = "DUP"; e.Address.LastName = "Duplicat"; }),
        R(6, "dup-b", "duplicate sort key, second by id", (e, _) => { e.MatchCode = "DUP"; e.Address.LastName = "Duplicat"; }),
        R(7, "dup-c", "duplicate sort key, third by id: three rows straddle a page of two", (e, _) => { e.MatchCode = "DUP"; e.Address.LastName = "Duplicat"; }),
        R(8, "case-lower", "differs from case-upper only in case", (e, _) => { e.MatchCode = "müller"; e.Address.LastName = "müller"; }),
        R(9, "case-upper", "differs from case-lower only in case", (e, _) => { e.MatchCode = "MÜLLER"; e.Address.LastName = "MÜLLER"; }),
        R(10, "accent-plain", "differs from accent-mark only in the accent", (e, _) => { e.MatchCode = "Muller"; e.Address.LastName = "Muller"; }),
        R(11, "accent-mark", "differs from accent-plain only in the accent", (e, _) => { e.MatchCode = "Müller"; e.Address.LastName = "Müller"; }),
        R(12, "unicode-cjk", "CJK above the Latin range, so byte order is observable", (e, _) => { e.MatchCode = "東京"; e.Address.LastName = "東京"; e.Address.City = "東京"; }),
        R(13, "unicode-emoji", "an astral code point: UTF-8 byte order and UTF-16 code-unit order disagree here", (e, _) => { e.MatchCode = "🚚 Fleet"; e.Address.LastName = "🚚"; }),
        R(14, "turkish-lower", "the dotted-I pair: a naive upper-casing disagrees with the collation", (e, _) => { e.MatchCode = "istanbul"; e.Address.LastName = "istanbul"; }),
        R(15, "turkish-upper", "the dotted-I pair, upper half", (e, _) => { e.MatchCode = "İSTANBUL"; e.Address.LastName = "İSTANBUL"; }),
        R(16, "arrays-empty", "every collection is the empty array", (e, _) => { e.MatchCode = "ARR-EMPTY"; e.Functions = []; e.EmailAddresses = []; e.PhoneNumbers = []; }),
        R(17, "arrays-one", "every collection holds exactly one element", (e, _) =>
        {
            e.MatchCode = "ARR-ONE";
            e.Functions = ["dispatch"];
            e.EmailAddresses = [new EmailAddress { Email = "one@lab.invalid", Type = "work" }];
            e.PhoneNumbers = [new PhoneNumber { Number = "+49 30 1", Type = "work" }];
        }),
        R(18, "arrays-many", "several elements, two of which each satisfy one half of a condition: emailAddresses.email eq split-a AND emailAddresses.type eq private matches uncorrelated, any() does not", (e, _) =>
        {
            e.MatchCode = "ARR-MANY";
            e.Functions = ["dispatch", "billing", "workshop"];
            e.EmailAddresses = [new EmailAddress { Email = "split-a@lab.invalid", Type = "work" }, new EmailAddress { Email = "split-b@lab.invalid", Type = "private" }];
            e.PhoneNumbers = [new PhoneNumber { Number = "+49 30 1", Type = "work" }, new PhoneNumber { Number = "+49 30 2", Type = "private" }, new PhoneNumber { Number = "+49 30 3", Type = "mobile" }];
        }),
        R(19, "arrays-missing", "the collections are absent, not empty", (e, raw) =>
        {
            e.MatchCode = "ARR-MISSING";
            raw.Unset("functions", "a non-nullable collection absent from storage")
               .Unset("emailAddresses", "a non-nullable collection absent from storage")
               .Unset("phoneNumbers", "a non-nullable collection absent from storage");
        }),
        R(20, "group-null", "a nullable object member is null", (e, _) => { e.MatchCode = "GRP-NULL"; e.Group = null; }),
        R(21, "group-missing", "the same object member is absent", (e, raw) => { e.MatchCode = "GRP-MISSING"; e.Group = null; raw.Unset("group", "a nullable object absent, not null"); }),
        R(22, "deleted", "isDeleted is true: the engine injects no soft-delete filter, so this row comes back", (e, _) => { e.MatchCode = "DELETED-22"; e.IsDeleted = true; }),
        R(23, "addon-rich", "the full addon bag: defined scalars, a key with a space, a wrapped decimal, a closed value list, a long above 2^53, a date, an undefined key, a null and an object-valued key", (e, _) => { e.MatchCode = "ADDON-RICH"; e.Addon = Addons.Rich(); }),
        R(24, "addon-missing", "the bag is absent", (e, raw) => { e.MatchCode = "ADDON-MISSING"; e.Addon = null; raw.Unset("addon", "the bag absent, not empty"); }),
        R(25, "employment-exited", "employment.exitDate is set and isActive false: a second temporal path for a range", (e, _) =>
        {
            e.MatchCode = "EXITED-25";
            e.Employment = new Employment { Number = "E-025", IsActive = false, EntryDate = Dt("2019-03-01T00:00:00Z"), ExitDate = Dt("2026-05-31T00:00:00Z") };
        }),
        R(26, "birthday-null", "a nullable temporal member is null", (e, _) => { e.MatchCode = "BDAY-NULL"; e.Birthday = null; }),
    ];

    private static readonly IReadOnlyList<CorpusRow> B =
    [
        Row(Entity, Spaces.Employee, Org.B, 1, "b-plain", "an exact clone of plain in the other organisation", Base),
        Row(Entity, Spaces.Employee, Org.B, 2, "b-dup", "an exact clone of dup-a", Base, (e, _) => { e.MatchCode = "DUP"; e.Address.LastName = "Duplicat"; }),
        Row(Entity, Spaces.Employee, Org.B, 3, "b-case", "an exact clone of case-lower", Base, (e, _) => { e.MatchCode = "müller"; e.Address.LastName = "müller"; }),
    ];

    private static CorpusRow R(int n, string key, string purpose, Action<Employee, RawStorage>? change = null) =>
        Row(Entity, Spaces.Employee, Org.A, n, key, purpose, Base, change);

    private static (Employee, RawStorage) Base(Org org) => (new Employee
    {
        Address = new EmployeeAddress
        {
            FirstName = "Anna",
            LastName = "Schmidt",
            CompanyName = "Lab Transport GmbH",
            Additional01 = null,
            Street = "Hauptstrasse",
            HouseNumber = "1",
            Zipcode = "10115",
            City = "Berlin",
            District = "",
            FederalState = null,
            CountryIso = "DE",
            Country = "Germany",
            Latitude = 52.52,
            Longitude = 13.405,
        },
        Birthday = Dt("1985-04-12T00:00:00Z"),
        PlaceOfBirth = "Berlin",
        Children = 2,
        Citizenship = "DE",
        TaxOffice = "Berlin Mitte",
        HealthInsurance = "Lab BKK",
        Employment = new Employment { Number = "E-001", IsActive = true, EntryDate = Dt("2020-01-15T00:00:00Z"), ExitDate = null },
        Group = new EmployeeGroup
        {
            Id = Ids.Of(Spaces.EmployeeGroup, org, 1),
            DisplayName = "Drivers",
            DisplayKey = "drivers",
            InternalName = "drivers",
            HexColor = "#0ea5e9",
            DefaultFunctions = ["drive"],
        },
        CreateDateTime = Dt("2026-01-05T08:00:00Z"),
        UpdateDateTime = Dt("2026-02-05T08:00:00Z"),
        CreateUserId = LabUser,
        UpdateUserId = LabUser,
        CreateUserName = "lab",
        UpdateUserName = "lab",
        Functions = ["dispatch", "billing"],
        EmailAddresses = [new EmailAddress { Email = "anna@lab.invalid", Type = "work" }],
        PhoneNumbers = [new PhoneNumber { Number = "+49 30 111", Type = "work" }],
        PrimaryEmailAddress = new EmailAddress { Email = "anna@lab.invalid", Type = "work" },
        PrimaryPhoneNumber = new PhoneNumber { Number = "+49 30 111", Type = "work" },
        MatchCode = "ALPHA-01",
        ExternalReference = "EXT-01",
        Addon = Addons.OneKey(),
        IsDeleted = false,
    }, RawStorage.None.Unset("address.additional02", "a nullable string absent beside additional01, which is null"));
}
