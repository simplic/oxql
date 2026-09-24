using OxQL.Core.Attributes;

namespace OxQL.IntegrationTests.Fleet.Models.Ledger;

/// <summary>
/// The second enum and decimal entity: a four-member byte enum and a single-member one, prices
/// as Decimal128 and as strings, a scalar collection, a dictionary whose key holds a dot, and
/// an extendable entity with no addon bag member at all.
/// </summary>
[OxQLType("ledger.transaction", "transaction", Extendable = true)]
public class Transaction
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? Number { get; set; }

    public string? Reference { get; set; }

    public TransactionType? Type { get; set; }

    public DateTime? Date { get; set; }

    public DateTime? DeliveryDate { get; set; }

    public string? TermsOfPayment { get; set; }

    public string? PaymentMethod { get; set; }

    public Currency? Currency { get; set; }

    public string? Description { get; set; }

    public Party? FinancialPartner { get; set; }

    public Party? DeliveryAddress { get; set; }

    public Party? InvoiceRecipient { get; set; }

    public Party? Payer { get; set; }

    public Party? Creator { get; set; }

    public Party? Responsible { get; set; }

    public Party? Representative { get; set; }

    public List<TransactionItem> Items { get; set; } = [];

    public ConvertState ConvertState { get; set; }

    public DateTime? AlternativePaymentDeadline { get; set; }

    public decimal? Balance { get; set; }

    public string? Barcode { get; set; }

    public string? BillToText { get; set; }

    public decimal? CashDiscountPercentValue { get; set; }

    public decimal? CashDiscountTotal { get; set; }

    public string? Document { get; set; }

    public List<string> AttachedDocuments { get; set; } = [];

    public string? FinancialAccountingPeriod { get; set; }

    public string? Period { get; set; }

    public List<string> States { get; set; } = [];

    public decimal? ManualVat { get; set; }

    public string? Notes { get; set; }

    public DateTime? DueDate { get; set; }

    public string? ReferenceNumber { get; set; }

    public string? TaxGroup { get; set; }

    public decimal TotalPrice { get; set; }

    public decimal TotalPriceGross { get; set; }

    public decimal TotalPriceNet { get; set; }

    public decimal TotalPriceTax { get; set; }

    public int TransactionYear { get; set; }

    public UserReference? UpdateUser { get; set; }

    public decimal SignedTotalPriceNet { get; set; }

    public decimal SignedTotalPriceGross { get; set; }

    public decimal SignedTotalPrice { get; set; }

    public decimal SignedTotalPriceTax { get; set; }

    public List<TaxKeyTotalPrice> TaxKeyTotalPrices { get; set; } = [];

    /// <summary>1 or -1; a signed int here, where a real ledger may store it narrower.</summary>
    public int Sign { get; set; }

    public OperationItemCombinationMode OperationItemCombinationMode { get; set; }

    public bool IsGross { get; set; }

    public bool FinancialExportDisabled { get; set; }

    public ValidationResult? ValidationResult { get; set; }

    public List<CostAssignment> DefaultCostCenters { get; set; } = [];

    public List<CostAssignment> DefaultCostObjects { get; set; } = [];

    public DateTime? CashDiscountDate { get; set; }

    public DateTime CreateDateTime { get; set; }

    public DateTime UpdateDateTime { get; set; }

    public Guid CreateUserId { get; set; }

    public Guid? UpdateUserId { get; set; }

    public string? CreateUserName { get; set; }

    public string? UpdateUserName { get; set; }
}

public enum ConvertState : byte
{
    NotConverted = 0,
    PartiallyConverted = 1,
    QuantitiesConverted = 2,
    Converted = 3,
}

/// <summary>A single-member enum: absent and its only member are the only two states it has.</summary>
public enum OperationItemCombinationMode : byte
{
    DeepestFirst = 0,
}

public class TransactionType
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public int Number { get; set; }

    public string? ReportName { get; set; }

    public string? ShortName { get; set; }

    public List<string> Functions { get; set; } = [];

    public string? Subtype { get; set; }
}

public class Currency
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public int Number { get; set; }

    public string? Name { get; set; }

    public string? ShortName { get; set; }

    public string? Symbol { get; set; }

    public DateTime CreateDateTime { get; set; }

    public DateTime UpdateDateTime { get; set; }

    public Guid CreateUserId { get; set; }

    public Guid? UpdateUserId { get; set; }

    public string? CreateUserName { get; set; }

    public string? UpdateUserName { get; set; }
}

public class Party
{
    public Guid Id { get; set; }

    public string? Name { get; set; }
}

public class UserReference
{
    public Guid Id { get; set; }

    public string? Name { get; set; }
}

public class TransactionItem
{
    public Guid Id { get; set; }

    public TransactionItemType? Type { get; set; }

    public Guid? BookedFromTransactionId { get; set; }

    public Guid? TransactionItemCollectionId { get; set; }

    public int SortNumber { get; set; }

    public string? DeserializationType { get; set; }

    public string? Text { get; set; }
}

public class TransactionItemType
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? DeserializationType { get; set; }

    public string? Name { get; set; }

    public int Number { get; set; }

    public string? Code { get; set; }

    public bool HasPositionNumber { get; set; }

    public bool IsSelectable { get; set; }

    public string? DetailHtml { get; set; }

    public string? DataTemplate { get; set; }

    public string? ArticleGLAResolver { get; set; }

    public DateTime CreateDateTime { get; set; }

    public DateTime UpdateDateTime { get; set; }

    public Guid CreateUserId { get; set; }

    public Guid? UpdateUserId { get; set; }

    public string? CreateUserName { get; set; }

    public string? UpdateUserName { get; set; }
}

public class TaxKeyTotalPrice
{
    public string? TaxKey { get; set; }

    public decimal TotalPrice { get; set; }
}

public class ValidationResult
{
    /// <summary>A string-keyed dictionary, stored as a document; a key may hold a dot.</summary>
    public Dictionary<string, bool> InputFieldValidationResults { get; set; } = [];

    public List<string> Errors { get; set; } = [];

    public bool IsValid { get; set; }
}

public class CostAssignment
{
    public Guid Id { get; set; }

    public decimal Percentage { get; set; }
}
