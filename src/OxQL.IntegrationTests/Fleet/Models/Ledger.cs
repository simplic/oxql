using OxQL.Core.Attributes;
using OxQL.Model.Attributes;

namespace OxQL.IntegrationTests.Fleet.Models.Ledger;

/// <summary>
/// The second enum and decimal entity: a four-member byte enum and a single-member one, prices
/// as Decimal128 and as strings, a scalar collection, a dictionary whose key holds a dot, and
/// an extendable entity with no addon bag member at all. It mirrors the ERP transaction: the
/// items are polymorphic (seven concrete variants, groups nesting items), the contacts carry a
/// contact-service address id, and the creating user is an employee's user id.
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

    public TermsOfPayment? TermsOfPayment { get; set; }

    public string? PaymentMethod { get; set; }

    public Currency? Currency { get; set; }

    public string? Description { get; set; }

    public TransactionContact? FinancialPartner { get; set; }

    public TransactionContact? DeliveryAddress { get; set; }

    public TransactionContact? InvoiceRecipient { get; set; }

    public TransactionContact? Payer { get; set; }

    public TransactionContact? Creator { get; set; }

    public TransactionContact? Responsible { get; set; }

    public TransactionContact? Representative { get; set; }

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

    /// <summary>The user who created the transaction; the employee whose <c>userId</c> it is.</summary>
    [OxQLReference("staff.employee", "userId")]
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

/// <summary>A contact as the transaction snapshots it: the personal account's id and names, and the contact-service address.</summary>
public class TransactionContact
{
    public Guid Id { get; set; }

    public string? AccountNumber { get; set; }

    public string? CompanyName { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public Guid? TaxGroupId { get; set; }

    public string? VatId { get; set; }

    public TransactionAddress? Address { get; set; }
}

/// <summary>The address of a transaction contact; its id is the contact of the directory service.</summary>
public class TransactionAddress
{
    [OxQLReference("directory.contact", "id")]
    public Guid Id { get; set; }

    public string? Number { get; set; }

    public string? Name { get; set; }

    public string? Street { get; set; }

    public string? Zipcode { get; set; }

    public string? City { get; set; }

    public string? Country { get; set; }

    public string? CountryIso { get; set; }
}

/// <summary>The terms of payment, snapshotted whole.</summary>
public class TermsOfPayment
{
    public Guid Id { get; set; }

    public int Number { get; set; }

    public string? Name { get; set; }

    public decimal CashDiscount { get; set; }

    public int CashDiscountDays { get; set; }

    public int PaymentDeadlineDays { get; set; }

    public string? FormattedText { get; set; }
}

public class UserReference
{
    public Guid Id { get; set; }

    public string? Name { get; set; }
}

/// <summary>
/// An item of a transaction. The ERP's base is abstract; here it stays concrete so the corpus
/// rows written before the hierarchy existed (stored as the base, without a discriminator) keep
/// their meaning. Every variant below is registered (<see cref="FleetClassMaps"/>) and stored
/// with its class name as <c>_t</c>; the report organisation's rows use variants only.
/// </summary>
public class TransactionItem
{
    public Guid Id { get; set; }

    public TransactionItemType? Type { get; set; }

    [OxQLReference("ledger.transaction")]
    public Guid? BookedFromTransactionId { get; set; }

    public Guid? TransactionItemCollectionId { get; set; }

    public int SortNumber { get; set; }

    public string? DeserializationType { get; set; }

    public string? Text { get; set; }
}

/// <summary>An item with a quantity.</summary>
public abstract class QuantityTransactionItem : TransactionItem
{
    public Quantity? Quantity { get; set; }
}

/// <summary>An item with a price: the input price and the calculated single and total prices.</summary>
public abstract class PriceTransactionItem : QuantityTransactionItem
{
    public decimal InputPrice { get; set; }

    public TransactionTaxRate? TaxRate { get; set; }

    public List<CostAssignment> CostCenters { get; set; } = [];

    public List<CostAssignment> CostObjects { get; set; } = [];

    public decimal SinglePrice { get; set; }

    public decimal TotalPrice { get; set; }

    public decimal SinglePriceNet { get; set; }

    public decimal TotalPriceNet { get; set; }

    public decimal SinglePriceGross { get; set; }

    public decimal TotalPriceGross { get; set; }

    public decimal SinglePriceVat { get; set; }

    public decimal TotalPriceVat { get; set; }
}

/// <summary>An operation (discount, surcharge) over other items, which it holds as copies.</summary>
public abstract class OperationTransactionItem : TransactionItem
{
    public List<TransactionItem> AssignedTransactionItems { get; set; } = [];

    public ValueOperator ValueOperator { get; set; }

    public decimal Amount { get; set; }
}

public class ArticleTransactionItem : PriceTransactionItem
{
    public Guid ArticleId { get; set; }
}

public class GeneralLedgerAccountTransactionItem : PriceTransactionItem
{
    public GeneralLedgerAccount? GeneralLedgerAccount { get; set; }
}

/// <summary>An item billed from a billing line: the ERP billing line it came from and the logistics self-references the line carries.</summary>
public class BillingLineTransactionItem : PriceTransactionItem
{
    [OxQLReference("ledger.billing_line")]
    public Guid BillingLineId { get; set; }

    public QuantityUnit? PriceUnit { get; set; }

    public string? Reference { get; set; }

    public List<BillingLineReference> References { get; set; } = [];

    public bool IsManualBillingLine { get; set; }
}

public class BasicDiscountSurchargeOperationItem : OperationTransactionItem
{
    public decimal DeltaValue { get; set; }
}

public class CashDiscountOperationItem : BasicDiscountSurchargeOperationItem
{
}

/// <summary>A group: items nested at any depth, the tree a flattening unwind walks.</summary>
public class GroupTransactionItem : TransactionItem
{
    public List<TransactionItem> Items { get; set; } = [];
}

public class TextTransactionItem : TransactionItem
{
}

public enum ValueOperator
{
    Percentage = 0,
    Absolute = 1,
}

/// <summary>A quantity as the ERP stores it: the value and its unit.</summary>
public class Quantity
{
    public decimal Value { get; set; }

    public QuantityUnit? Unit { get; set; }
}

public class QuantityUnit
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public string? ShortName { get; set; }
}

public class TransactionTaxRate
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public decimal Rate { get; set; }
}

public class GeneralLedgerAccount
{
    public Guid Id { get; set; }

    public string? Number { get; set; }

    public string? Name { get; set; }
}

/// <summary>
/// A self-reference a logistics billing line carries onto every copy of it: the shipment or tour
/// it was billed on (<c>dataType</c> <c>shipment</c> or <c>tour</c>), or a tariff (<c>Tariff</c>),
/// with the id held as a string.
/// </summary>
public class BillingLineReference
{
    public Guid Id { get; set; }

    public string? DataType { get; set; }

    [OxQLReferenceWhen("dataType", "shipment", "transport.shipment", Field = "id", KeyAs = OxQLKeyAs.Guid)]
    [OxQLReferenceWhen("dataType", "tour", "transport.tour", Field = "id", KeyAs = OxQLKeyAs.Guid)]
    public string? ReferenceId { get; set; }
}

/// <summary>
/// The ERP billing line: a copy of a logistics billing line (or a manual one) waiting to be
/// invoiced; <c>sourceBillingLineReference</c> names the logistics line it was copied from, an
/// element of a shipment's or a tour's billing lines.
/// </summary>
[OxQLType("ledger.billing_line", "billing_line", Extendable = true)]
public class BillingLine
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public TransactionContact? FinancialPartner { get; set; }

    public DateTime Date { get; set; }

    public DateTime? DeliveryDate { get; set; }

    public string? Text { get; set; }

    public string? SourceText { get; set; }

    public Quantity? Quantity { get; set; }

    public decimal? SinglePrice { get; set; }

    public decimal? TotalPrice { get; set; }

    public bool IsGross { get; set; }

    public string? Reference { get; set; }

    public List<BillingLineReference> References { get; set; } = [];

    public bool IsManualBillingLine { get; set; }

    public BillingLineState State { get; set; }

    [OxQLReference("ledger.transaction")]
    public Guid? AssignedTransactionId { get; set; }

    public SourceBillingLineReference? SourceBillingLineReference { get; set; }

    public DateTime CreateDateTime { get; set; }

    public DateTime UpdateDateTime { get; set; }

    public Guid? CreateUserId { get; set; }

    public string? CreateUserName { get; set; }
}

public enum BillingLineState
{
    Open = 0,
    Assigned = 1,
    Invoiced = 2,
}

/// <summary>
/// Where an ERP billing line came from. <c>type</c> <c>logistics</c> does not say shipment or
/// tour: the id is an element of either one's billing lines, tried in that order.
/// </summary>
public class SourceBillingLineReference
{
    public string? Type { get; set; }

    [OxQLReferenceWhen("type", "logistics", "transport.shipment#billingLines", "transport.tour#billingLines", Field = "id")]
    public Guid Id { get; set; }
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
