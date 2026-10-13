using L = OxQL.IntegrationTests.Fleet.Models.Ledger;
using T = OxQL.IntegrationTests.Fleet.Models.Transport;

namespace OxQL.IntegrationTests.Suites.Differential;

/// <summary>
/// What the joins over <see cref="WeaveCorpus"/> have to answer, followed through the objects the
/// corpus wrote: the items of an invoice in pre-order, and for each reference of the chain the
/// outcome and the record it reaches, by the rules the semantics guide gives (<i>References</i>,
/// <i>Outcomes and data loss</i>, <i>Chains across services</i>).
/// </summary>
internal static class WeaveOracle
{
    public const string Resolved = "resolved";
    public const string Ambiguous = "ambiguous";
    public const string ReferenceNull = "reference_null";
    public const string Excluded = "excluded";
    public const string NotApplicable = "not_applicable";
    public const string NotFound = "not_found";
    public const string InvalidKey = "invalid_key";

    /// <summary>The deepest level a flatten reads; the collection itself is level 1.</summary>
    public const int FlattenDepth = 5;

    /// <summary>One row of a flattened invoice.</summary>
    internal sealed record Flat(L.Transaction Transaction, int Position, L.TransactionItem Item, int Level);

    /// <summary>Where a reference ends: its outcome and, where it reached one, the record's key (for a line of a shipment or tour, the candidates when several hold it).</summary>
    internal sealed record Hop(string Outcome, Guid? Id = null, string? Entity = null, IReadOnlyList<Guid>? Candidates = null);

    // ── flatten ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Every item of an invoice and every descendant down to the flatten depth, in pre-order, numbered in that order.</summary>
    public static List<Flat> Flatten(L.Transaction transaction)
    {
        var rows = new List<Flat>();

        void Walk(IReadOnlyList<L.TransactionItem> items, int level)
        {
            if (level > FlattenDepth)
                return;

            foreach (var item in items)
            {
                rows.Add(new Flat(transaction, rows.Count, item, level));

                if (item is L.GroupTransactionItem group)
                    Walk(group.Items, level + 1);
            }
        }

        Walk(transaction.Items, 1);

        return rows;
    }

    public static List<Flat> FlattenAll() => [.. WeaveCorpus.Transactions.SelectMany(Flatten)];

    /// <summary>The variant of an item, as <c>is</c> names it.</summary>
    public static string VariantOf(L.TransactionItem item) => item.GetType().Name;

    // ── the chain ───────────────────────────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<Guid, L.BillingLine> Lines = WeaveCorpus.Lines.ToDictionary(line => line.Id);

    private static readonly IReadOnlyDictionary<Guid, T.Shipment> Shipments = WeaveCorpus.Shipments.ToDictionary(shipment => shipment.Id);

    private static readonly IReadOnlyDictionary<Guid, T.Tour> Tours = WeaveCorpus.Tours.ToDictionary(tour => tour.Id);

    private static readonly IReadOnlySet<Guid> Vehicles = WeaveCorpus.Vehicles.Select(vehicle => vehicle.Id).ToHashSet();

    private static readonly IReadOnlySet<Guid> Employees = WeaveCorpus.Employees.Select(employee => employee.Id).ToHashSet();

    /// <summary>The billing line an item names: a reference onto a key of the same service.</summary>
    public static Hop Line(L.BillingLineTransactionItem item) =>
        Lines.ContainsKey(item.BillingLineId) ? new Hop(Resolved, item.BillingLineId) : new Hop(NotFound);

    /// <summary>
    /// The line a billing line was copied from: an element of a shipment's billing lines, then of a
    /// tour's, where the reference is of the type that names one. The record is the shipment or tour
    /// holding it; two holders are ambiguous, the first target's taken.
    /// </summary>
    public static Hop Source(Hop line)
    {
        if (line.Id is not { } id)
            return new Hop(ReferenceNull);

        if (Lines[id].SourceBillingLineReference is not { } source)
            return new Hop(ReferenceNull);

        if (source.Type != "logistics")
            return new Hop(Excluded);

        var shipments = WeaveCorpus.Shipments.Where(shipment => shipment.BillingLines.Any(held => held.Id == source.Id)).Select(shipment => shipment.Id).ToList();
        var tours = WeaveCorpus.Tours.Where(tour => tour.BillingLines.Any(held => held.Id == source.Id)).Select(tour => tour.Id).ToList();

        if (shipments.Count + tours.Count == 0)
            return new Hop(NotFound);

        var (entity, candidates) = shipments.Count > 0 ? (WeaveCorpus.Shipment, shipments) : (WeaveCorpus.Tour, tours);

        return new Hop(shipments.Count + tours.Count > 1 ? Ambiguous : Resolved, candidates.Count == 1 ? candidates[0] : null, entity, candidates);
    }

    /// <summary>The first tour of a shipment that is there: a stage for the rows of shipments only.</summary>
    public static Hop DeliveringTour(Hop source, Guid? parent)
    {
        if (parent is not { } id)
            return new Hop(ReferenceNull);

        if (source.Entity != WeaveCorpus.Shipment)
            return new Hop(NotApplicable);

        return First(Shipments[id].Tours.Select(entry => entry.TourId is { } tour ? new Hop(Tours.ContainsKey(tour) ? Resolved : NotFound, tour) : null));
    }

    /// <summary>What <c>elements: "first"</c> makes of the elements' outcomes: the first that resolves, else the gravest of the rest.</summary>
    public static Hop First(IEnumerable<Hop?> elements)
    {
        var keyed = elements.Where(element => element is not null).Select(element => element!).ToList();

        if (keyed.FirstOrDefault(element => element.Outcome is Resolved or Ambiguous) is { } found)
            return found;

        foreach (var outcome in new[] { NotFound, InvalidKey, Excluded })
            if (keyed.Any(element => element.Outcome == outcome))
                return new Hop(outcome);

        return new Hop(ReferenceNull);
    }

    /// <summary>
    /// The record a resource stands for, by its variant: a vehicle of the fleet service, an employee
    /// of the staff service, nothing for a carrier. With <paramref name="vehiclesOnly"/> the stage is
    /// narrowed to vehicles, and a driver is a case narrowed away.
    /// </summary>
    public static Hop Resource(T.Resource? resource, bool vehiclesOnly)
    {
        switch (resource)
        {
            case null:
                return new Hop(ReferenceNull);

            case T.DriverResource when vehiclesOnly:
            case T.CarrierResource:
                return new Hop(Excluded);

            case T.DriverResource:
                return Employees.Contains(resource.Id) ? new Hop(Resolved, resource.Id, WeaveCorpus.Employee) : new Hop(NotFound);

            default:
                return Vehicles.Contains(resource.Id) ? new Hop(Resolved, resource.Id, WeaveCorpus.Vehicle) : new Hop(NotFound);
        }
    }

    /// <summary>The union join: a shipment's line through its delivering tour's resource, a tour's line through the tour's own.</summary>
    public static Hop Vehicle(Hop source, Guid? parent, Hop deliveringTour, bool vehiclesOnly)
    {
        if (parent is not { } id)
            return new Hop(ReferenceNull);

        if (source.Entity == WeaveCorpus.Tour)
            return Resource(Tours[id].Resource, vehiclesOnly);

        return deliveringTour.Outcome is Resolved && deliveringTour.Id is { } tour ? Resource(Tours[tour].Resource, vehiclesOnly) : new Hop(ReferenceNull);
    }

    /// <summary>A reference an item carries beside its source, typed by a sibling member and stored as text, narrowed to shipments.</summary>
    public static Hop ShipmentOf(L.BillingLineReference reference)
    {
        if (reference.ReferenceId is null)
            return new Hop(ReferenceNull);

        if (reference.DataType != "shipment")
            return new Hop(Excluded);

        if (!Guid.TryParse(reference.ReferenceId, out var id))
            return new Hop(InvalidKey);

        return Shipments.ContainsKey(id) ? new Hop(Resolved, id) : new Hop(NotFound);
    }

    /// <summary>The clerk of an invoice: the employee whose user id the invoice was created under; two employees may share one.</summary>
    public static Hop Clerk(L.Transaction transaction)
    {
        var found = WeaveCorpus.Employees.Where(employee => employee.UserId == transaction.CreateUserId).Select(employee => employee.Id).ToList();

        return found.Count switch
        {
            0 => new Hop(NotFound),
            1 => new Hop(Resolved, found[0]),
            _ => new Hop(Ambiguous, null, null, found),
        };
    }
}
