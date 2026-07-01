using OxQL.Core.Interfaces;
using OxQL.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OxQL.Core.Normalization;

/// <summary>
/// Normalizes query requests for caching and comparison.
/// </summary>
public sealed class QueryRequestNormalizer : IQueryRequestNormalizer
{
    private readonly OxQLOptions _options;

    public QueryRequestNormalizer(OxQLOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public QueryRequest Normalize(QueryRequest request)
    {
        var pipeline = new List<PipelineStage>(request.Pipeline.Count);

        IReadOnlyList<SortField>? sortFields = null;
        PageStage? pageStage = null;

        foreach (var stage in request.Pipeline)
        {
            if (stage.Sort is not null)
            {
                sortFields = NormalizeSortWithTieBreaker(stage.Sort);
                pipeline.Add(stage with { Sort = sortFields });
            }
            else if (stage.Page is not null)
            {
                pageStage = NormalizePage(stage.Page);
                pipeline.Add(stage with { Page = pageStage });
            }
            else
            {
                pipeline.Add(stage);
            }
        }

        // If no sort was found, add a default sort by id
        if (sortFields is null)
        {
            sortFields = [new SortField { Path = "id", Direction = "asc" }];
            // Insert sort before page stage if exists
            var pageIndex = pipeline.FindIndex(s => s.Page is not null);
            var sortStage = new PipelineStage { Sort = sortFields };
            if (pageIndex >= 0)
                pipeline.Insert(pageIndex, sortStage);
            else
                pipeline.Add(sortStage);
        }

        // If no page was found, add a default page
        if (pageStage is null)
        {
            pageStage = new PageStage { Limit = _options.DefaultPageSize };
            pipeline.Add(new PipelineStage { Page = pageStage });
        }

        return request with { Pipeline = pipeline };
    }

    public string GenerateCacheKey(QueryRequest request)
    {
        var keyBuilder = new StringBuilder();
        keyBuilder.Append(request.EntityType.ToLowerInvariant());
        keyBuilder.Append('|');

        // Build shape from pipeline stages (excluding variable values)
        foreach (var stage in request.Pipeline)
        {
            AppendStageShape(keyBuilder, stage);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyBuilder.ToString()));
        return Convert.ToBase64String(hash)[..22]; // Truncate for readability
    }

    private IReadOnlyList<SortField> NormalizeSortWithTieBreaker(IReadOnlyList<SortField> sortFields)
    {
        var hasIdSort = sortFields.Any(f => string.Equals(f.Path, "id", StringComparison.OrdinalIgnoreCase));
        if (hasIdSort) return sortFields;

        var normalized = new List<SortField>(sortFields)
        {
            new() { Path = "id", Direction = "asc" }
        };
        return normalized;
    }

    private PageStage NormalizePage(PageStage page)
    {
        var limit = page.Limit;
        if (limit <= 0) limit = _options.DefaultPageSize;
        if (limit > _options.MaxPageSize) limit = _options.MaxPageSize;

        return page with { Limit = limit };
    }

    private static void AppendStageShape(StringBuilder sb, PipelineStage stage)
    {
        if (stage.Match is not null)
        {
            sb.Append("M:");
            AppendMatchShape(sb, stage.Match);
            sb.Append(';');
        }

        if (stage.Lookup is not null)
        {
            sb.Append("L:");
            sb.Append(stage.Lookup.From);
            sb.Append(',');
            sb.Append(stage.Lookup.LocalPath);
            sb.Append(',');
            sb.Append(stage.Lookup.ForeignPath);
            sb.Append(',');
            sb.Append(stage.Lookup.As);
            sb.Append(';');
        }

        if (stage.Resolve is not null)
        {
            sb.Append("R:");
            sb.Append(stage.Resolve.Source);
            sb.Append(',');
            sb.Append(stage.Resolve.LocalPath);
            sb.Append(',');
            sb.Append(stage.Resolve.As);
            sb.Append(';');
        }

        if (stage.Unwind is not null)
        {
            sb.Append("U:");
            sb.Append(stage.Unwind.Path);
            if (stage.Unwind.As is not null) sb.Append($",as={stage.Unwind.As}");
            sb.Append($",pn={stage.Unwind.PreserveNull}");
            sb.Append(';');
        }

        if (stage.Group is not null)
        {
            sb.Append("G:");
            foreach (var by in stage.Group.By)
            {
                sb.Append(by.Path ?? by.DateTrunc?.Path ?? "?");
                sb.Append(':');
                sb.Append(by.As);
                sb.Append(',');
            }
            sb.Append('|');
            foreach (var (name, expr) in stage.Group.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                sb.Append(name);
                sb.Append(':');
                sb.Append(expr.Function);
                sb.Append(',');
            }
            sb.Append(';');
        }

        if (stage.Project is not null)
        {
            sb.Append("P:");
            foreach (var path in stage.Project.Include.OrderBy(p => p, StringComparer.Ordinal))
            {
                sb.Append(path);
                sb.Append(',');
            }
            sb.Append(';');
        }

        if (stage.Sort is not null)
        {
            sb.Append("S:");
            foreach (var field in stage.Sort)
            {
                sb.Append(field.Path);
                sb.Append(':');
                sb.Append(field.Direction);
                sb.Append(',');
            }
            sb.Append(';');
        }

        if (stage.Page is not null)
        {
            sb.Append("PG:");
            sb.Append(stage.Page.Limit);
            sb.Append(',');
            sb.Append(stage.Page.IncludeTotalCount);
            sb.Append(';');
        }
    }

    private static void AppendMatchShape(StringBuilder sb, MatchStage match)
    {
        if (match.And is not null)
        {
            sb.Append("AND[");
            foreach (var c in match.And) AppendConditionShape(sb, c);
            sb.Append(']');
        }
        else if (match.Or is not null)
        {
            sb.Append("OR[");
            foreach (var c in match.Or) AppendConditionShape(sb, c);
            sb.Append(']');
        }
        else if (match.Not is not null)
        {
            sb.Append("NOT[");
            AppendConditionShape(sb, match.Not);
            sb.Append(']');
        }
        else if (match.Condition is not null)
        {
            AppendConditionShape(sb, match.Condition);
        }
    }

    private static void AppendConditionShape(StringBuilder sb, FilterCondition condition)
    {
        if (condition.IsLogical)
        {
            if (condition.And is not null)
            {
                sb.Append("AND[");
                foreach (var c in condition.And) AppendConditionShape(sb, c);
                sb.Append(']');
            }
            if (condition.Or is not null)
            {
                sb.Append("OR[");
                foreach (var c in condition.Or) AppendConditionShape(sb, c);
                sb.Append(']');
            }
            if (condition.Not is not null)
            {
                sb.Append("NOT[");
                AppendConditionShape(sb, condition.Not);
                sb.Append(']');
            }
            return;
        }

        sb.Append(condition.Path);
        sb.Append(':');
        sb.Append(condition.Op);

        // For cache key, include whether value is a $var reference (shape) but not the actual value
        if (condition.Value.HasValue)
        {
            var val = condition.Value.Value;
            if (val.ValueKind == JsonValueKind.Object && val.TryGetProperty("$var", out var varName))
            {
                sb.Append(":$var(");
                sb.Append(varName.GetString());
                sb.Append(')');
            }
            else
            {
                sb.Append(":literal");
            }
        }
        sb.Append(',');
    }
}
