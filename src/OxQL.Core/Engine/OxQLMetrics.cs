using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OxQL.Core.Engine;

/// <summary>
/// The engine's meter: how many requests a host answers and how long they take, by the contract
/// they were written for, the route, the entity and how they ended. A host exports it by the
/// meter's name (<see cref="MeterName"/>); nothing is measured while nobody listens.
/// <para>
/// One measurement is one query: a request of the query route, each entry of a batch, an explain.
/// The tags are bounded on purpose: the entity is one the host's model has (anything else is
/// <see cref="UnknownEntity"/>), the outcome is <see cref="Ok"/> or a refusal code, and neither the
/// user nor the organisation is a tag; those are in the log.
/// </para>
/// </summary>
public static class OxQLMetrics
{
    /// <summary>The name of the meter.</summary>
    public const string MeterName = "OxQL";

    /// <summary>The counter of answered queries.</summary>
    public const string RequestsName = "oxql.requests";

    /// <summary>The histogram of the time a query took on this host, in seconds.</summary>
    public const string DurationName = "oxql.request.duration";

    /// <summary>The tag of the contract the request was written for: <c>1</c> or <c>2</c>.</summary>
    public const string ContractTag = "contract";

    /// <summary>The tag of the route: <see cref="RouteQuery"/>, <see cref="RouteBatch"/> or <see cref="RouteExplain"/>.</summary>
    public const string RouteTag = "route";

    /// <summary>The tag of the entity: an entity id of the host's model, <see cref="UnknownEntity"/> or <see cref="NoEntity"/>.</summary>
    public const string EntityTag = "entity";

    /// <summary>The tag of how the query ended: <see cref="Ok"/>, a refusal code, <see cref="Cancelled"/> or <see cref="Unbound"/>.</summary>
    public const string OutcomeTag = "outcome";

    /// <summary>The route of one query.</summary>
    public const string RouteQuery = "query";

    /// <summary>The route of a batch; each entry is measured as a query of this route.</summary>
    public const string RouteBatch = "batch";

    /// <summary>The route of an explain.</summary>
    public const string RouteExplain = "explain";

    /// <summary>The outcome of a query that was answered.</summary>
    public const string Ok = "ok";

    /// <summary>The outcome of a query its caller gave up on.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The outcome of a contract 1 request whose body the host could not bind and answered itself.</summary>
    public const string Unbound = "unbound";

    /// <summary>The entity of a request that names one the host's model does not have.</summary>
    public const string UnknownEntity = "(unknown)";

    /// <summary>The entity of a request that was refused before the host looked at the entity it names (a body that does not read, a batch refused whole, an explain past its bounds).</summary>
    public const string NoEntity = "(none)";

    private static readonly Meter Meter = new(MeterName, EngineCapabilities.Version);

    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(RequestsName, "{request}", "Queries answered: a request of the query route, each entry of a batch, an explain.");

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(DurationName, "s", "The time a query took on this host, from when the host had read it to its answer.");

    /// <summary>Whether anything listens to the meter; a caller that would compute a tag for nobody asks first.</summary>
    public static bool Enabled => Requests.Enabled || Duration.Enabled;

    /// <summary>Records one query.</summary>
    /// <param name="contract">The contract the request was written for.</param>
    /// <param name="route">One of the route constants.</param>
    /// <param name="entity">The entity as the tag carries it (<see cref="EntityOf"/>).</param>
    /// <param name="outcome"><see cref="Ok"/>, a refusal code, <see cref="Cancelled"/> or <see cref="Unbound"/>.</param>
    /// <param name="elapsed">The time the query took.</param>
    public static void Record(int contract, string route, string entity, string outcome, TimeSpan elapsed)
    {
        if (!Enabled)
            return;

        var tags = new TagList
        {
            { ContractTag, contract == 1 ? "1" : "2" },
            { RouteTag, route },
            { EntityTag, entity },
            { OutcomeTag, outcome },
        };

        Requests.Add(1, tags);
        Duration.Record(Math.Max(0, elapsed.TotalSeconds), tags);
    }

    /// <summary>
    /// The entity tag of what a request names: the id of the entity the model resolves it to (a
    /// retired id and, as a contract 1 caller may write it, another case included),
    /// <see cref="UnknownEntity"/> for a name the model does not have, <see cref="NoEntity"/> for none.
    /// The caller's text is never a tag.
    /// </summary>
    public static string EntityOf(Model.EntityModel? model, string? named)
    {
        if (string.IsNullOrWhiteSpace(named))
            return NoEntity;

        if (model is null)
            return UnknownEntity;

        return model.TryResolve(named, out var entity, out _) || model.TryResolve(named.Trim().ToLowerInvariant(), out entity, out _)
            ? entity.Id
            : UnknownEntity;
    }

    /// <summary>The outcome tag of an answer: <see cref="Ok"/>, or the first code of its refusal.</summary>
    public static string OutcomeOf(Refusal? refusal) => refusal is null ? Ok : refusal.Errors is { Count: > 0 } errors ? errors[0].Code : refusal.Type;
}
