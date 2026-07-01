using OxQL.Core.Interfaces;
using OxQL.Core.Models;
using System.Text.Json;

namespace OxQL.Core.Validation;

/// <summary>
/// Validates query requests against configured rules and security limits.
/// </summary>
public sealed class QueryValidator : IQueryValidator
{
    private readonly OxQLOptions _options;

    private static readonly HashSet<string> AllowedOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "neq", "gt", "gte", "lt", "lte",
        "in", "nin", "contains", "startsWith", "endsWith",
        "exists", "regex"
    };

    private static readonly HashSet<string> AllowedSortDirections = new(StringComparer.OrdinalIgnoreCase)
    {
        "asc", "desc"
    };

    private static readonly HashSet<string> AllowedDateTruncUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "year", "quarter", "month", "week", "day", "hour", "minute", "second"
    };

    private static readonly HashSet<string> AllowedAggFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "count", "countDistinct", "sum", "avg", "min", "max", "first", "last", "push"
    };

    public QueryValidator(OxQLOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ValidationResult Validate(QueryRequest request)
    {
        var errors = new List<QueryValidationError>();

        ValidateEntityType(request, errors);
        ValidatePipelineLimits(request, errors);
        ValidatePipelineStages(request, errors);

        return errors.Count == 0 ? ValidationResult.Success() : ValidationResult.Failure(errors);
    }

    private static void ValidateEntityType(QueryRequest request, List<QueryValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(request.EntityType))
        {
            errors.Add(new QueryValidationError
            {
                Code = "INVALID_ENTITY_TYPE",
                Message = "EntityType is required."
            });
        }
    }

    private void ValidatePipelineLimits(QueryRequest request, List<QueryValidationError> errors)
    {
        if (request.Pipeline.Count > _options.MaxPipelineStages)
        {
            errors.Add(new QueryValidationError
            {
                Code = "MAX_PIPELINE_STAGES_EXCEEDED",
                Message = $"Pipeline exceeds maximum of {_options.MaxPipelineStages} stages."
            });
        }

        var lookupCount = request.Pipeline.Count(s => s.Lookup is not null);
        if (lookupCount > _options.MaxLookupStages)
        {
            errors.Add(new QueryValidationError
            {
                Code = "MAX_LOOKUP_STAGES_EXCEEDED",
                Message = $"Pipeline exceeds maximum of {_options.MaxLookupStages} lookup stages."
            });
        }

        var unwindCount = request.Pipeline.Count(s => s.Unwind is not null);
        if (unwindCount > _options.MaxUnwindStages)
        {
            errors.Add(new QueryValidationError
            {
                Code = "MAX_UNWIND_STAGES_EXCEEDED",
                Message = $"Pipeline exceeds maximum of {_options.MaxUnwindStages} unwind stages."
            });
        }
    }

    private void ValidatePipelineStages(QueryRequest request, List<QueryValidationError> errors)
    {
        foreach (var stage in request.Pipeline)
        {
            if (stage.Match is not null) ValidateMatch(stage.Match, errors);
            if (stage.Lookup is not null) ValidateLookup(stage.Lookup, errors);
            if (stage.Resolve is not null) ValidateResolve(stage.Resolve, errors);
            if (stage.Unwind is not null) ValidateUnwind(stage.Unwind, errors);
            if (stage.Group is not null) ValidateGroup(stage.Group, errors);
            if (stage.Project is not null) ValidateProject(stage.Project, errors);
            if (stage.Sort is not null) ValidateSort(stage.Sort, errors);
            if (stage.Page is not null) ValidatePage(stage.Page, errors);
        }
    }

    private void ValidateMatch(MatchStage match, List<QueryValidationError> errors)
    {
        if (match.And is not null)
        {
            foreach (var condition in match.And)
                ValidateFilterCondition(condition, errors);
        }

        if (match.Or is not null)
        {
            foreach (var condition in match.Or)
                ValidateFilterCondition(condition, errors);
        }

        if (match.Not is not null)
            ValidateFilterCondition(match.Not, errors);

        if (match.Condition is not null)
            ValidateFilterCondition(match.Condition, errors);
    }

    private void ValidateFilterCondition(FilterCondition condition, List<QueryValidationError> errors)
    {
        if (condition.IsLogical)
        {
            if (condition.And is not null)
                foreach (var c in condition.And) ValidateFilterCondition(c, errors);
            if (condition.Or is not null)
                foreach (var c in condition.Or) ValidateFilterCondition(c, errors);
            if (condition.Not is not null)
                ValidateFilterCondition(condition.Not, errors);
            return;
        }

        if (string.IsNullOrWhiteSpace(condition.Path))
        {
            errors.Add(new QueryValidationError
            {
                Code = "INVALID_FILTER_PATH",
                Message = "Filter condition requires a path."
            });
            return;
        }

        ValidatePath(condition.Path, "filter", errors);

        if (string.IsNullOrWhiteSpace(condition.Op))
        {
            errors.Add(new QueryValidationError
            {
                Code = "MISSING_OPERATOR",
                Message = $"Filter condition on path '{condition.Path}' requires an operator.",
                Path = condition.Path
            });
            return;
        }

        if (!AllowedOperators.Contains(condition.Op))
        {
            errors.Add(new QueryValidationError
            {
                Code = "UNKNOWN_OPERATOR",
                Message = $"Unknown operator '{condition.Op}'.",
                Path = condition.Path
            });
        }

        if (string.Equals(condition.Op, "regex", StringComparison.OrdinalIgnoreCase) && condition.Value.HasValue)
        {
            ValidateRegexValue(condition.Value.Value, condition.Path, errors);
        }
    }

    private void ValidateRegexValue(JsonElement value, string path, List<QueryValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var pattern = value.GetString();
            if (pattern is not null && pattern.Length > _options.RegexMaxLength)
            {
                errors.Add(new QueryValidationError
                {
                    Code = "REGEX_TOO_LONG",
                    Message = $"Regex pattern exceeds maximum length of {_options.RegexMaxLength}.",
                    Path = path
                });
            }
        }
    }

    private void ValidateLookup(LookupStage lookup, List<QueryValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(lookup.From))
        {
            errors.Add(new QueryValidationError
            {
                Code = "INVALID_LOOKUP_SOURCE",
                Message = "Lookup 'from' is required."
            });
        }
        else if (_options.AllowedLookupSources.Count > 0 && !_options.AllowedLookupSources.Contains(lookup.From))
        {
            errors.Add(new QueryValidationError
            {
                Code = "DISALLOWED_LOOKUP_SOURCE",
                Message = $"Lookup source '{lookup.From}' is not in the allowed list.",
                Path = lookup.From
            });
        }

        ValidatePath(lookup.LocalPath, "lookup.localPath", errors);
        ValidatePath(lookup.ForeignPath, "lookup.foreignPath", errors);

        if (string.IsNullOrWhiteSpace(lookup.As))
        {
            errors.Add(new QueryValidationError
            {
                Code = "INVALID_LOOKUP_ALIAS",
                Message = "Lookup 'as' alias is required."
            });
        }
    }

    private void ValidateResolve(ResolveStage resolve, List<QueryValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(resolve.Source))
        {
            errors.Add(new QueryValidationError
            {
                Code = "INVALID_RESOLVE_SOURCE",
                Message = "Resolve 'source' is required."
            });
        }
        else if (_options.AllowedResolveSources.Count > 0 && !_options.AllowedResolveSources.Contains(resolve.Source))
        {
            errors.Add(new QueryValidationError
            {
                Code = "DISALLOWED_RESOLVE_SOURCE",
                Message = $"Resolve source '{resolve.Source}' is not in the allowed list.",
                Path = resolve.Source
            });
        }

        ValidatePath(resolve.LocalPath, "resolve.localPath", errors);

        if (string.IsNullOrWhiteSpace(resolve.As))
        {
            errors.Add(new QueryValidationError
            {
                Code = "INVALID_RESOLVE_ALIAS",
                Message = "Resolve 'as' alias is required."
            });
        }
    }

    private void ValidateUnwind(UnwindStage unwind, List<QueryValidationError> errors)
    {
        ValidatePath(unwind.Path, "unwind.path", errors);
    }

    private void ValidateGroup(GroupStage group, List<QueryValidationError> errors)
    {
        if (group.Fields.Count > _options.MaxGroupFields)
        {
            errors.Add(new QueryValidationError
            {
                Code = "MAX_GROUP_FIELDS_EXCEEDED",
                Message = $"Group exceeds maximum of {_options.MaxGroupFields} fields."
            });
        }

        foreach (var byField in group.By)
        {
            if (byField.Path is not null)
                ValidatePath(byField.Path, "group.by", errors);
            if (byField.DateTrunc is not null)
            {
                ValidatePath(byField.DateTrunc.Path, "group.by.dateTrunc", errors);
                if (!AllowedDateTruncUnits.Contains(byField.DateTrunc.Unit))
                {
                    errors.Add(new QueryValidationError
                    {
                        Code = "INVALID_DATE_TRUNC_UNIT",
                        Message = $"Invalid dateTrunc unit '{byField.DateTrunc.Unit}'.",
                        Path = byField.DateTrunc.Path
                    });
                }
            }
        }

        foreach (var (fieldName, aggExpr) in group.Fields)
        {
            if (aggExpr.Function is null || !AllowedAggFunctions.Contains(aggExpr.Function))
            {
                errors.Add(new QueryValidationError
                {
                    Code = "UNKNOWN_AGG_FUNCTION",
                    Message = $"Unknown aggregation function for field '{fieldName}'.",
                    Path = fieldName
                });
            }
        }
    }

    private void ValidateProject(ProjectStage project, List<QueryValidationError> errors)
    {
        if (project.Include.Count > _options.MaxProjectionFields)
        {
            errors.Add(new QueryValidationError
            {
                Code = "MAX_PROJECTION_FIELDS_EXCEEDED",
                Message = $"Projection exceeds maximum of {_options.MaxProjectionFields} fields."
            });
        }

        foreach (var path in project.Include)
        {
            ValidatePath(path, "project.include", errors);
        }
    }

    private static void ValidateSort(IReadOnlyList<SortField> sortFields, List<QueryValidationError> errors)
    {
        foreach (var field in sortFields)
        {
            ValidatePath(field.Path, "sort", errors);

            if (!AllowedSortDirections.Contains(field.Direction))
            {
                errors.Add(new QueryValidationError
                {
                    Code = "INVALID_SORT_DIRECTION",
                    Message = $"Invalid sort direction '{field.Direction}'. Must be 'asc' or 'desc'.",
                    Path = field.Path
                });
            }
        }
    }

    private void ValidatePage(PageStage page, List<QueryValidationError> errors)
    {
        if (page.Limit <= 0)
        {
            errors.Add(new QueryValidationError
            {
                Code = "INVALID_PAGE_LIMIT",
                Message = "Page limit must be greater than 0."
            });
        }
        else if (page.Limit > _options.MaxPageSize)
        {
            errors.Add(new QueryValidationError
            {
                Code = "PAGE_SIZE_EXCEEDED",
                Message = $"Page limit {page.Limit} exceeds maximum of {_options.MaxPageSize}."
            });
        }
    }

    private static void ValidatePath(string? path, string context, List<QueryValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add(new QueryValidationError
            {
                Code = "EMPTY_PATH",
                Message = $"Path is required in {context}."
            });
            return;
        }

        if (path.Contains('$'))
        {
            errors.Add(new QueryValidationError
            {
                Code = "INVALID_PATH_DOLLAR",
                Message = $"Path '{path}' contains '$' which is not allowed.",
                Path = path
            });
        }

        if (path.Contains(".."))
        {
            errors.Add(new QueryValidationError
            {
                Code = "INVALID_PATH_TRAVERSAL",
                Message = $"Path '{path}' contains '..' which is not allowed.",
                Path = path
            });
        }

        var segments = path.Split('.');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                errors.Add(new QueryValidationError
                {
                    Code = "EMPTY_PATH_SEGMENT",
                    Message = $"Path '{path}' contains an empty segment.",
                    Path = path
                });
                break;
            }
        }
    }
}
