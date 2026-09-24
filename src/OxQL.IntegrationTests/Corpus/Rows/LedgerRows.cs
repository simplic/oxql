using MongoDB.Bson;
using OxQL.IntegrationTests.Fleet.Models.Ledger;
using static OxQL.IntegrationTests.Fixtures.Rows.RowKit;

namespace OxQL.IntegrationTests.Fixtures.Rows;

/// <summary>
/// <c>ledger.transaction</c>: a four-member byte enum and a single-member one (each also absent),
/// prices as Decimal128 and as strings, negative and beyond <see cref="decimal"/>'s precision, a
/// nullable decimal null and absent, a scalar collection empty and with three values, a
/// dictionary key holding a dot, duplicate and null sort keys, and an extendable entity with no
/// addon bag member at all. 25 rows in A, 2 clones in B.
/// </summary>
internal static class LedgerRows
{
    public const string Entity = "ledger.transaction";

    public static IReadOnlyList<CorpusRow> For(Org org) => org switch
    {
        Org.A => A,
        Org.B => B,
        _ => [],
    };

    private static readonly IReadOnlyList<CorpusRow> A =
    [
        T(1, "cs-not-converted", "convertState = NotConverted (0)", (t, _) => t.ConvertState = ConvertState.NotConverted),
        T(2, "cs-partially", "convertState = PartiallyConverted (1)", (t, _) => t.ConvertState = ConvertState.PartiallyConverted),
        T(3, "cs-quantities", "convertState = QuantitiesConverted (2)", (t, _) => t.ConvertState = ConvertState.QuantitiesConverted),
        T(4, "cs-converted", "convertState = Converted (3)", (t, _) => t.ConvertState = ConvertState.Converted),
        T(5, "cs-absent", "convertState is absent", (_, raw) => raw.Unset("convertState", "a non-nullable enum missing from storage")),
        T(6, "mode-deepest", "operationItemCombinationMode = DeepestFirst, the enum's only member", (t, _) => t.OperationItemCombinationMode = OperationItemCombinationMode.DeepestFirst),
        T(7, "mode-absent", "the single-member enum is absent: the only way its two states differ", (_, raw) => raw.Unset("operationItemCombinationMode", "absent is the second state a single-member enum has")),
        T(8, "dec-decimal128", "every price as Decimal128"),
        T(9, "dec-string", "the same prices held as strings: the ledger half of the decimal duality", (_, raw) => raw
            .DecimalAsString("totalPrice", "1000.00")
            .DecimalAsString("totalPriceNet", "1000.00")
            .DecimalAsString("totalPriceGross", "1190.00")
            .DecimalAsString("totalPriceTax", "190.00")
            .DecimalAsString("balance", "0.00")),
        T(10, "dec-negative", "a credit note: negative prices and sign -1", (t, _) =>
        {
            t.Sign = -1;
            t.TotalPrice = t.TotalPriceNet = t.SignedTotalPrice = t.SignedTotalPriceNet = -250.75m;
            t.TotalPriceGross = t.SignedTotalPriceGross = -298.39m;
            t.TotalPriceTax = t.SignedTotalPriceTax = -47.64m;
        }),
        T(11, "dec-many-places", "a decimal with thirty fractional digits, beyond what a CLR decimal holds", (t, raw) =>
        {
            t.TotalPrice = 0m;
            raw.Set("totalPrice", new BsonDecimal128(Decimal128.Parse("0.000000000000000000000000000001")), "thirty fractional digits: Decimal128 holds it, System.Decimal does not");
        }),
        T(12, "balance-null", "a nullable decimal is null", (t, _) => t.Balance = null),
        T(13, "balance-missing", "the same nullable decimal is absent", (t, raw) => { t.Balance = null; raw.Unset("balance", "a nullable decimal absent, not null"); }),
        T(14, "states-empty", "states is the empty array", (t, _) => t.States = []),
        T(15, "states-many", "three states, so an in over a scalar collection has something to match", (t, _) => t.States = ["draft", "review", "posted"]),
        T(16, "items-none", "items is the empty array", (t, _) => t.Items = []),
        T(17, "items-many", "three items", (t, _) => t.Items = [ItemOf(Org.A, 1), ItemOf(Org.A, 2), ItemOf(Org.A, 3)]),
        T(18, "dict-dotted-key", "the validation dictionary carries a key with a dot in it, which the database stores but no path grammar reaches", (t, _) =>
            t.ValidationResult = new ValidationResult { InputFieldValidationResults = new() { ["number"] = true, ["type.subtype"] = false }, Errors = [], IsValid = false }),
        T(19, "num-dup-a", "number T-DUP, first by id", (t, _) => t.Number = "T-DUP"),
        T(20, "num-dup-b", "number T-DUP, second by id", (t, _) => t.Number = "T-DUP"),
        T(21, "ref-null", "reference is null", (t, _) => t.Reference = null),
        T(22, "ref-missing", "reference is absent", (t, raw) => { t.Reference = null; raw.Unset("reference", "missing, which is not null"); }),
        T(23, "deleted", "isDeleted is true", (t, _) => t.IsDeleted = true),
        T(24, "year-prev", "transactionYear 2025 and a 2025 date, so a year grouping has two buckets", (t, _) =>
        {
            t.TransactionYear = 2025;
            t.Date = Dt("2025-11-20T00:00:00Z");
            t.DueDate = Dt("2025-12-20T00:00:00Z");
        }),
        T(25, "date-null", "the nullable date member is null while dueDate is set", (t, _) => t.Date = null),
    ];

    private static readonly IReadOnlyList<CorpusRow> B =
    [
        Row(Entity, Spaces.Transaction, Org.B, 1, "b-cs-not-converted", "an exact clone of cs-not-converted in the other organisation", Base, (t, _) => t.Number = "T-0001"),
        Row(Entity, Spaces.Transaction, Org.B, 2, "b-num-dup", "an exact clone of num-dup-a", Base, (t, _) => t.Number = "T-DUP"),
    ];

    private static CorpusRow T(int n, string key, string purpose, Action<Transaction, RawStorage>? change = null) =>
        Row(Entity, Spaces.Transaction, Org.A, n, key, purpose, Base, (t, raw) =>
        {
            t.Number = $"T-{n:D4}";
            change?.Invoke(t, raw);
        });

    private static TransactionItem ItemOf(Org org, int n) => new()
    {
        Id = Ids.Of(Spaces.TransactionItem, org, n),
        Type = new TransactionItemType
        {
            Id = Ids.Of(Spaces.TransactionItemType, org, 1),
            OrganizationId = org.Id(),
            DeserializationType = "ArticleTransactionItem",
            Name = "Article",
            Number = 1,
            Code = "ART",
            HasPositionNumber = true,
            IsSelectable = true,
            CreateDateTime = Dt("2026-01-01T00:00:00Z"),
            UpdateDateTime = Dt("2026-01-01T00:00:00Z"),
            CreateUserId = LabUser,
            UpdateUserId = LabUser,
            CreateUserName = "lab",
            UpdateUserName = "lab",
        },
        SortNumber = n,
        DeserializationType = "ArticleTransactionItem",
        Text = $"item {n}",
    };

    private static (Transaction, RawStorage) Base(Org org) => (new Transaction
    {
        Number = "T-0001",
        Reference = "REF-1",
        Type = new TransactionType { Id = Ids.Of(Spaces.TransactionType, org, 1), Name = "Invoice", Number = 10, ReportName = "invoice", ShortName = "INV", Functions = ["sell"], Subtype = null },
        Date = Dt("2026-04-10T00:00:00Z"),
        DeliveryDate = Dt("2026-04-12T00:00:00Z"),
        Currency = new Currency
        {
            Id = Ids.Of(Spaces.Currency, org, 1),
            OrganizationId = org.Id(),
            Number = 978,
            Name = "Euro",
            ShortName = "EUR",
            Symbol = "€",
            CreateDateTime = Dt("2026-01-01T00:00:00Z"),
            UpdateDateTime = Dt("2026-01-01T00:00:00Z"),
            CreateUserId = LabUser,
            UpdateUserId = LabUser,
            CreateUserName = "lab",
            UpdateUserName = "lab",
        },
        Description = "lab transaction",
        Items = [],
        ConvertState = ConvertState.NotConverted,
        Balance = 0.00m,
        AttachedDocuments = [],
        States = ["draft"],
        DueDate = Dt("2026-05-10T00:00:00Z"),
        ReferenceNumber = "RN-1",
        TotalPrice = 1000.00m,
        TotalPriceGross = 1190.00m,
        TotalPriceNet = 1000.00m,
        TotalPriceTax = 190.00m,
        TransactionYear = 2026,
        UpdateUser = new UserReference { Id = LabUser, Name = "lab" },
        SignedTotalPriceNet = 1000.00m,
        SignedTotalPriceGross = 1190.00m,
        SignedTotalPrice = 1000.00m,
        SignedTotalPriceTax = 190.00m,
        TaxKeyTotalPrices = [],
        Sign = 1,
        OperationItemCombinationMode = OperationItemCombinationMode.DeepestFirst,
        IsGross = false,
        FinancialExportDisabled = false,
        ValidationResult = new ValidationResult { InputFieldValidationResults = new() { ["number"] = true }, Errors = [], IsValid = true },
        DefaultCostCenters = [],
        DefaultCostObjects = [],
        CreateDateTime = Dt("2026-04-01T08:00:00Z"),
        UpdateDateTime = Dt("2026-04-02T08:00:00Z"),
        CreateUserId = LabUser,
        UpdateUserId = LabUser,
        CreateUserName = "lab",
        UpdateUserName = "lab",
        IsDeleted = false,
    }, RawStorage.None);
}
