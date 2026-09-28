using MongoDB.Bson.Serialization.Attributes;
using OxQL.Core.Attributes;
using OxQL.Model.Attributes;

namespace OxQL.IntegrationTests.Fleet.Models.Transport;

/// <summary>
/// The temporal, enum and collection entity: loading and delivery times across DST and the
/// UTC day boundary, an Int32 enum, items with a nested collection, billing lines whose
/// references are a collection under a collection, tags, the addon bag, and a department that
/// references another service.
/// </summary>
[OxQLType("transport.shipment", "shipment", Extendable = true)]
public class Shipment
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime CreateDateTime { get; set; }

    public DateTime UpdateDateTime { get; set; }

    public Guid CreateUserId { get; set; }

    public Guid? UpdateUserId { get; set; }

    public ContactAddress? LoadAddress { get; set; }

    public ContactAddress? DeliveryAddress { get; set; }

    public BillableContact? Carrier { get; set; }

    public BillableContact? FreightPayer { get; set; }

    public BillableContact? Customer { get; set; }

    public ContactAddress? RecipientAddress { get; set; }

    public BillableContact? InvoiceRecipient { get; set; }

    public ContactAddress? SenderAddress { get; set; }

    public BillableContact? Supplier { get; set; }

    public DateTime? LoadStart { get; set; }

    public DateTime? LoadEnd { get; set; }

    public DateTime? PlannedLoadStart { get; set; }

    public DateTime? PlannedLoadEnd { get; set; }

    public DateTime? CalculatedLoadStart { get; set; }

    public DateTime? CalculatedLoadEnd { get; set; }

    public DateTime? ActualLoadStart { get; set; }

    public DateTime? ActualLoadEnd { get; set; }

    public LoadingDateTimeType LoadingTimeType { get; set; }

    public DateTime? DeliveryStart { get; set; }

    public DateTime? DeliveryEnd { get; set; }

    public DateTime? PlannedDeliveryStart { get; set; }

    public DateTime? PlannedDeliveryEnd { get; set; }

    public DateTime? CalculatedDeliveryStart { get; set; }

    public DateTime? CalculatedDeliveryEnd { get; set; }

    public DateTime? ActualDeliveryStart { get; set; }

    public DateTime? ActualDeliveryEnd { get; set; }

    public DateTime? EffectiveLoadStart { get; set; }

    public DateTime? EffectiveLoadEnd { get; set; }

    public DateTime? EffectiveDeliveryStart { get; set; }

    public DateTime? EffectiveDeliveryEnd { get; set; }

    public DateTime? ActualStartDateTime { get; set; }

    public DateTime? ActualDeliveryStartDateTime { get; set; }

    public LoadingDateTimeType DeliveryTimeType { get; set; }

    public DateTime? OrderDate { get; set; }

    public string? ShipmentNumber { get; set; }

    public string? ReferenceNumber { get; set; }

    public string? LoadNumber { get; set; }

    public string? DeliveryNumber { get; set; }

    public string? DeliveryNoteNumber { get; set; }

    public Quantity? ActualWeight { get; set; }

    public bool IsTemplate { get; set; }

    public string? TemplateName { get; set; }

    public ShipmentStatus? Status { get; set; }

    public string? TransportOrder { get; set; }

    public List<ShipmentItem> Items { get; set; } = [];

    public List<BillingLine> BillingLines { get; set; } = [];

    public List<ShipmentDocument> Documents { get; set; } = [];

    public List<ShipmentTag>? Tags { get; set; } = [];

    public string? Notes { get; set; }

    public string? ExternalNotes { get; set; }

    public string? LoadWorkflow { get; set; }

    public string? DeliveryWorkflow { get; set; }

    public List<ShipmentTour> Tours { get; set; } = [];

    public string? ConstructionSite { get; set; }

    public ShipmentDepartment? Department { get; set; }

    public string? Incoterm { get; set; }

    public Dictionary<string, object>? Addon { get; set; }

    public string? Tariff { get; set; }

    public string? CarrierTariff { get; set; }
}

/// <summary>Stored as Int32, as the driver stores an enum by default.</summary>
public enum LoadingDateTimeType
{
    None = 0,
    Fixed = 1,
    FixedWithBooking = 2,
}

public class ContactAddress
{
    public Guid? ContactId { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? CompanyName { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? Street { get; set; }

    public string? HouseNumber { get; set; }

    public string? Additional01 { get; set; }

    public string? Additional02 { get; set; }

    public string? Zipcode { get; set; }

    public string? District { get; set; }

    public string? FederalState { get; set; }

    public string? Country { get; set; }

    public string? CountryIso { get; set; }

    public string? City { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public string? MatchCode { get; set; }
}

public class BillableContact
{
    public ContactAddress? Address { get; set; }

    public string? AccountNumber { get; set; }

    public Guid? PersonalAccountId { get; set; }
}

public class ShipmentStatus
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? Name { get; set; }

    public string? Number { get; set; }

    public List<string> Roles { get; set; } = [];

    public string? HexColor { get; set; }

    public int OrderNr { get; set; }

    public string? Resolver { get; set; }
}

public class ShipmentItemStatus
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? Name { get; set; }

    public string? Number { get; set; }

    public List<string> Roles { get; set; } = [];

    public string? HexColor { get; set; }
}

public class Quantity
{
    public double Value { get; set; }

    public QuantityUnit? QuantityUnit { get; set; }
}

public class QuantityUnit
{
    public Guid Guid { get; set; }

    public string? Name { get; set; }

    public string? ShortName { get; set; }

    public int Digits { get; set; }
}

public class ShipmentItem
{
    public Guid Id { get; set; }

    public ShipmentItemStatus? Status { get; set; }

    public string? Text { get; set; }

    public Article? Article { get; set; }

    public List<WeightNote> WeightNotes { get; set; } = [];

    public double? LoadingMeters { get; set; }

    public LoadingAidType? LoadingAidType { get; set; }

    public Quantity? Quantity { get; set; }

    public Quantity? Weight { get; set; }

    public string? ShippingUnit { get; set; }

    public string? Reference { get; set; }

    public int OrderNumber { get; set; }

    /// <summary>Not the bag: only a root member named <c>addon</c> is one.</summary>
    public Dictionary<string, object>? Addon { get; set; }
}

public class Article
{
    public Guid Id { get; set; }

    public string? Number { get; set; }

    public string? Name { get; set; }
}

public class WeightNote
{
    public Guid Id { get; set; }

    public string? Number { get; set; }

    public double? Weight { get; set; }

    /// <summary>The weighed quantity; left out when null, so the corpus rows are stored as before.</summary>
    [BsonIgnoreIfNull]
    public Quantity? Quantity { get; set; }
}

public class LoadingAidType
{
    public Guid Id { get; set; }

    public int Number { get; set; }

    public string? DisplayName { get; set; }

    public double? Weight { get; set; }

    public string? ShortText { get; set; }

    public int? Width { get; set; }

    public int? Length { get; set; }

    public double? StoragePosition { get; set; }
}

public class BillingLine
{
    public Guid Id { get; set; }

    public BillableContact? FinancialPartner { get; set; }

    public DateTime? Date { get; set; }

    public DateTime? DeliveryDate { get; set; }

    public BillingLineType Type { get; set; }

    public string? Status { get; set; }

    public string? Text { get; set; }

    public double? SinglePrice { get; set; }

    public double? TotalPrice { get; set; }

    public bool IsGross { get; set; }

    public List<CostCenterAssignment> CostCenters { get; set; } = [];

    public List<CostCenterAssignment> CostObjects { get; set; } = [];

    public string? GeneralLedgerAccountGroup { get; set; }

    public double? TaxRate { get; set; }

    public Quantity? Quantity { get; set; }

    public BillingLineReference? Reference { get; set; }

    public List<BillingLineReference> References { get; set; } = [];

    public bool IsManualBillingLine { get; set; }

    public Dictionary<string, object>? Addon { get; set; }

    public Guid? AssignedTransactionId { get; set; }
}

public enum BillingLineType
{
    Customer = 0,
    Carrier = 1,
}

public class CostCenterAssignment
{
    public Guid Id { get; set; }

    public CostCenter? CostCenter { get; set; }

    public decimal Percentage { get; set; }
}

public class CostCenter
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public int Number { get; set; }

    public DateTime? ValidFrom { get; set; }

    public DateTime? ValidTo { get; set; }
}

public class BillingLineReference
{
    public Guid Id { get; set; }

    public string? DataType { get; set; }

    public string? ReferenceId { get; set; }
}

public class ShipmentDocument
{
    public Guid Id { get; set; }

    public string? Name { get; set; }
}

public class ShipmentTag
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? Name { get; set; }

    public string? GroupName { get; set; }

    public string? HexColor { get; set; }
}

/// <summary>
/// A tour the shipment is on, as the shipment snapshots it: the tour's id and number and the
/// tour's resource. The new members are left out when null, so the corpus rows are stored as before.
/// </summary>
public class ShipmentTour
{
    public Guid Id { get; set; }

    public string? Number { get; set; }

    [BsonIgnoreIfNull]
    [OxQLReference("transport.tour")]
    public Guid? TourId { get; set; }

    [BsonIgnoreIfNull]
    public Resource? Resource { get; set; }

    [BsonIgnoreIfNull]
    public DateTime? StartDateTime { get; set; }

    [BsonIgnoreIfNull]
    public DateTime? EndDateTime { get; set; }
}

/// <summary>The dispatching department, embedded as a subset; its id names a department of the fleet service.</summary>
public class ShipmentDepartment
{
    [OxQLReference("fleet.department", "id")]
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public int OrderId { get; set; }

    public string? HexColor { get; set; }
}

/// <summary>
/// The volume entity: thin rows in the thousands, for the offset ceiling, the cursor walk and
/// the count cap, and a user id member that references a vehicle of the fleet service.
/// </summary>
[OxQLType("transport.shipment_template", "shipment_template", Extendable = true)]
public class ShipmentTemplate
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? TemplateName { get; set; }

    public string? ShipmentNumber { get; set; }

    public string? ReferenceNumber { get; set; }

    public LoadingDateTimeType LoadingTimeType { get; set; }

    public LoadingDateTimeType DeliveryTimeType { get; set; }

    public TemplateTimeMode TimeMode { get; set; }

    public TemplateTime? LoadStart { get; set; }

    public DateTime? OrderDate { get; set; }

    public bool IsShipmentConversionDisabled { get; set; }

    public List<ShipmentItem> Items { get; set; } = [];

    public List<ShipmentDocument> Documents { get; set; } = [];

    public List<ShipmentTag> Tags { get; set; } = [];

    public List<BillingLine> BillingLines { get; set; } = [];

    public DateTime CreateDateTime { get; set; }

    public DateTime UpdateDateTime { get; set; }

    /// <summary>A remote reference on a member that is not named like one.</summary>
    [OxQLReference("fleet.vehicle", "id")]
    public Guid CreateUserId { get; set; }

    public Guid? UpdateUserId { get; set; }

    public string? CreateUserName { get; set; }

    public string? UpdateUserName { get; set; }

    public Dictionary<string, object>? Addon { get; set; }
}

public enum TemplateTimeMode
{
    Absolute = 0,
    RelativeFromStart = 1,
}

/// <summary>A time either absolute or relative to the start.</summary>
public class TemplateTime
{
    public DateTime? AbsoluteTime { get; set; }

    public TimeSpan? RelativeTime { get; set; }
}

/// <summary>
/// A tour: a resource (the tractor unit, or a carrier) driving stops, the resources attached on
/// the way (driver, trailer), billing lines of the same item type as the shipment's, and the
/// stops themselves as interface-typed actions. Not extendable, as in logistics.
/// </summary>
[OxQLType("transport.tour", "tour")]
public class Tour
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? Number { get; set; }

    public string? Reference { get; set; }

    public Resource? Resource { get; set; }

    public ContactAddress? StartAddress { get; set; }

    public ContactAddress? EndAddress { get; set; }

    public DateTime? StartDateTime { get; set; }

    public DateTime? EndDateTime { get; set; }

    public DateTime? ActualStartDateTime { get; set; }

    public DateTime? ActualEndDateTime { get; set; }

    public List<ITourAction> Actions { get; set; } = [];

    public List<AttachedResource> AttachedResources { get; set; } = [];

    public List<BillingLine> BillingLines { get; set; } = [];

    public string? Notes { get; set; }

    public BillableContact? FinancialPartner { get; set; }

    public DateTime CreateDateTime { get; set; }

    public DateTime UpdateDateTime { get; set; }
}

/// <summary>A resource attached to a tour between two of its actions.</summary>
public class AttachedResource
{
    public Guid Id { get; set; }

    public Resource? Resource { get; set; }

    public ITourAction? AttachAction { get; set; }

    public ITourAction? DetachAction { get; set; }
}

/// <summary>A stop of a tour. Interface-typed wherever it is held; stored with its class name as <c>_t</c>.</summary>
public interface ITourAction
{
    Guid Id { get; set; }

    Guid GlobalActionId { get; set; }

    int OrderId { get; set; }

    DateTime? DateTime { get; set; }

    DateTime? ActualDateTime { get; set; }

    DateTime? CalculatedDateTime { get; set; }

    string? Notes { get; set; }

    bool OutOfTour { get; set; }
}

public abstract class TourActionBase : ITourAction
{
    public Guid Id { get; set; }

    public Guid GlobalActionId { get; set; }

    public int OrderId { get; set; }

    public DateTime? DateTime { get; set; }

    public DateTime? ActualDateTime { get; set; }

    public DateTime? CalculatedDateTime { get; set; }

    public string? Notes { get; set; }

    public bool OutOfTour { get; set; }
}

/// <summary>Loads a shipment.</summary>
public class AttachShipmentAction : TourActionBase
{
    public ContactAddress? Address { get; set; }

    public Guid? ShipmentId { get; set; }
}

/// <summary>Unloads a shipment.</summary>
public class DetachShipmentAction : TourActionBase
{
    public ContactAddress? Address { get; set; }

    public Guid? ShipmentId { get; set; }
}

public class AttachResourceAction : TourActionBase
{
    public ContactAddress? Address { get; set; }

    public Resource? Resource { get; set; }
}

public class DetachResourceAction : TourActionBase
{
    public ContactAddress? Address { get; set; }

    public Resource? Resource { get; set; }
}

public class CheckVehicleAction : TourActionBase
{
}

public class CleaningAction : TourActionBase
{
    public ContactAddress? Address { get; set; }
}

public class TaskAction : TourActionBase
{
    public string? Title { get; set; }

    public string? Description { get; set; }

    public DateTime? StartDateTime { get; set; }

    public DateTime? EndDateTime { get; set; }

    public ContactAddress? Address { get; set; }
}

/// <summary>The organisation document every logistics entity derives from: the key and the scope are inherited.</summary>
public abstract class OrganizationDocumentBase
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }
}

/// <summary>
/// A resource a tour plans with. Abstract and polymorphic; its inherited <c>id</c> is the id of
/// the thing it stands for, decided by the stored variant: a driver is an employee of the staff
/// service, the vehicle-like variants are vehicles of the fleet service, a carrier is neither.
/// Those references are declared host-side (<see cref="FleetReferences"/>), since the member
/// is inherited.
/// </summary>
[OxQLType("transport.resource", "resource", Extendable = true)]
public abstract class Resource : OrganizationDocumentBase
{
    public string? MatchCode { get; set; }

    public string? DisplayName { get; set; }

    public List<Resource>? AttachedResource { get; set; }

    public DateTime CreateDateTime { get; set; }

    public DateTime UpdateDateTime { get; set; }

    public Guid? CreateUserId { get; set; }

    public string? CreateUserName { get; set; }

    public DateTime? UsableUntil { get; set; }

    public string? PlanningOrderKey { get; set; }

    public string? Notes { get; set; }
}

public abstract class HumanResource : Resource
{
}

public class DriverResource : HumanResource
{
}

public abstract class VehicleResource : Resource
{
}

public class TractorUnitResource : VehicleResource
{
}

public class TrailerResource : VehicleResource
{
    public List<ResourceLoadingSlot> LoadingSlots { get; set; } = [];

    public bool IsLoadable { get; set; }
}

public class CarResource : VehicleResource
{
}

public class ContainerResource : Resource
{
    public List<ResourceLoadingSlot> LoadingSlots { get; set; } = [];

    public bool IsLoadable { get; set; }
}

public class CarrierResource : Resource
{
}

public class EquipmentResource : Resource
{
}

public class ResourceLoadingSlot
{
    public Guid Id { get; set; }

    public string? Name { get; set; }
}

/// <summary>An attempt to deliver a shipment; the latest one by <c>dateTime</c> is the proof of delivery.</summary>
[OxQLType("transport.delivery_attempt", "deliveryAttempt", Extendable = true)]
public class DeliveryAttempt
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public bool IsDeleted { get; set; }

    public string? Text { get; set; }

    public DeliveryAttemptStatus? Status { get; set; }

    public DateTime? DateTime { get; set; }

    [OxQLReference("transport.shipment")]
    public Guid? ShipmentId { get; set; }
}

public class DeliveryAttemptStatus
{
    public Guid Id { get; set; }

    public string? DisplayName { get; set; }

    public string? DisplayKey { get; set; }
}
