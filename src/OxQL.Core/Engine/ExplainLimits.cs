using OxQL.Core.Binding;
using OxQL.Core.Models;

namespace OxQL.Core.Engine;

/// <summary>An <c>include</c> or <c>remote</c> value of an explain envelope the engine does not know.</summary>
public sealed record ExplainUnknownValue(string Member, string Value);

/// <summary>
/// The cheap refusals of an explain (improvement plan §3.E protection): checked on the parsed body
/// alone, before any model, binder or owner work, so an explain past its bounds costs a JSON parse
/// and nothing else. More stages than <c>Explain.MaxStages</c>, more <c>catalog</c> entries than
/// <c>Explain.MaxCatalogEntries</c>, a <c>shape.depth</c> above <c>Explain.MaxShapeDepth</c>, or an
/// <c>include</c> or <c>remote</c> value the engine does not know is 400 <c>EXPLAIN_LIMIT</c>, one
/// error per bound crossed. The body size (<c>Explain.MaxRequestBytes</c>, 413) is the host's, which
/// refuses it before the body is read.
/// </summary>
public static class ExplainLimits
{
    /// <summary>The refusal of <paramref name="request"/> under <paramref name="options"/>, or null when it is within every bound.</summary>
    public static Refusal? Check(ExplainRequest request, ExplainOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<QueryValidationError>();
        var stages = request.Query?.Pipeline?.Count ?? 0;

        if (stages > options.MaxStages)
            errors.Add(Error($"The explained query has {stages} stages; explain takes at most {options.MaxStages}.", "stages", stages, options.MaxStages));

        if (request.Catalog.Count > options.MaxCatalogEntries)
            errors.Add(Error($"The explain asks for {request.Catalog.Count} catalog entries; it may ask for at most {options.MaxCatalogEntries}.", "catalog", request.Catalog.Count, options.MaxCatalogEntries));

        if (request.ShapeDepth is { } depth && depth > options.MaxShapeDepth)
            errors.Add(Error($"The explain asks for shape depth {depth}; it may ask for at most {options.MaxShapeDepth}.", "shape.depth", depth, options.MaxShapeDepth));

        foreach (var (member, value) in request.UnknownValues)
        {
            var known = member == "include" ? ExplainRequest.KnownIncludes : ExplainRequest.KnownRemotes;

            errors.Add(new QueryValidationError
            {
                Code = Codes.ExplainLimit,
                Message = $"'{value}' is not a value of '{member}'; it takes {string.Join(", ", known.Select(each => $"\"{each}\""))}.",
                Path = member,
                Params = new Dictionary<string, object?> { ["limit"] = member, ["value"] = value, ["known"] = known.ToList() },
            });
        }

        return errors.Count == 0 ? null : Refusal.Validation(errors);
    }

    private static QueryValidationError Error(string message, string limit, int value, int max) => new()
    {
        Code = Codes.ExplainLimit,
        Message = message,
        Path = limit,
        Params = new Dictionary<string, object?> { ["limit"] = limit, ["value"] = value, ["max"] = max },
    };
}
