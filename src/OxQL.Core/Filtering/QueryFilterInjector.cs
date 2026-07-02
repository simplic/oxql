using System.Text.Json;
using OxQL.Core.Models;

namespace OxQL.Core.Filtering;

/// <summary>
/// Injects additional root-level filter conditions into a <see cref="QueryRequest"/>.
/// <para>
/// Every injected condition is combined with the query's existing match using a
/// logical <c>AND</c> at the root of the pipeline. This is primarily intended for
/// multi-tenant scenarios where a host wants to force, for example, an
/// <c>OrganizationId</c> constraint onto every query so callers cannot read data
/// belonging to another tenant.
/// </para>
/// <para>
/// <b>Cache safety.</b> Injected conditions should reference their value through a
/// query variable (<see cref="InjectedFilter.VariableName"/>) rather than embedding a
/// literal. The query-plan cache keys plans by filter <i>shape</i> (variable names are
/// part of the shape, literal values are not) and resolves variable values per request.
/// Using a variable therefore keeps a single cached plan tenant-safe: the shape is stable
/// while each request supplies its own value.
/// </para>
/// </summary>
public static class QueryFilterInjector
{
    /// <summary>
    /// Returns a new <see cref="QueryRequest"/> whose pipeline is guaranteed to filter by
    /// all supplied <paramref name="filters"/> in addition to any conditions the caller
    /// already specified. Returns the original <paramref name="request"/> unchanged when
    /// there is nothing to inject.
    /// </summary>
    /// <param name="request">The incoming query request.</param>
    /// <param name="filters">The conditions to force onto the query.</param>
    /// <returns>A request with the injected conditions applied.</returns>
    public static QueryRequest Inject(QueryRequest request, IReadOnlyList<InjectedFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (filters is null || filters.Count == 0)
            return request;

        // Build the FilterConditions that will be AND-ed onto the query.
        var injectedConditions = filters.Select(ToCondition).ToList();

        var pipeline = new List<PipelineStage>(request.Pipeline.Count + 1);
        var injected = false;

        foreach (var stage in request.Pipeline)
        {
            if (!injected && stage.Match is not null)
            {
                pipeline.Add(stage with { Match = MergeMatch(stage.Match, injectedConditions) });
                injected = true;
            }
            else
            {
                pipeline.Add(stage);
            }
        }

        // No match stage existed – prepend one that only contains the injected conditions.
        if (!injected)
        {
            pipeline.Insert(0, new PipelineStage
            {
                Match = new MatchStage { And = injectedConditions }
            });
        }

        return request with
        {
            Pipeline = pipeline,
            Variables = MergeVariables(request.Variables, filters)
        };
    }

    /// <summary>
    /// Wraps an existing <see cref="MatchStage"/> together with the injected conditions
    /// inside a single root-level logical <c>AND</c>.
    /// </summary>
    private static MatchStage MergeMatch(MatchStage existing, IReadOnlyList<FilterCondition> injected)
    {
        var conditions = new List<FilterCondition>(injected.Count + 1);

        // Preserve the caller's existing match as the first AND operand.
        var existingCondition = ToCondition(existing);
        if (existingCondition is not null)
            conditions.Add(existingCondition);

        conditions.AddRange(injected);

        return new MatchStage { And = conditions };
    }

    /// <summary>
    /// Collapses a <see cref="MatchStage"/> into a single <see cref="FilterCondition"/> so it
    /// can participate as one operand of a logical group. Returns <c>null</c> for an empty match.
    /// </summary>
    private static FilterCondition? ToCondition(MatchStage match)
    {
        if (match.And is not null) return new FilterCondition { And = match.And };
        if (match.Or is not null) return new FilterCondition { Or = match.Or };
        if (match.Not is not null) return new FilterCondition { Not = match.Not };
        return match.Condition;
    }

    /// <summary>
    /// Builds a field <see cref="FilterCondition"/> for an injected filter, referencing the
    /// supplied value through a <c>$var</c> reference to keep query plans cache-safe.
    /// </summary>
    private static FilterCondition ToCondition(InjectedFilter filter)
    {
        var valueJson = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["$var"] = filter.VariableName
        });

        return new FilterCondition
        {
            Path = filter.Path,
            Op = filter.Op,
            Value = valueJson
        };
    }

    /// <summary>
    /// Produces a <see cref="QueryVariables"/> instance that contains the caller's variables
    /// plus one entry per injected filter. Injected values overwrite caller-supplied values of
    /// the same name so a client cannot override a forced tenant constraint.
    /// </summary>
    private static QueryVariables MergeVariables(QueryVariables? existing, IReadOnlyList<InjectedFilter> filters)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (existing is not null)
        {
            foreach (var (key, value) in existing.Values)
                values[key] = value;
        }

        foreach (var filter in filters)
            values[filter.VariableName] = filter.Value;

        return new QueryVariables { Values = values };
    }
}
