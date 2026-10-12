// LEGACY(compat.v1): contract 1 legacy support. Remove with contract 1 compatibility; see src/docs/LEGACY.md.
using System.Text.Json;
using OxQL.Core.Legacy;
using OxQL.Core.Models;

namespace OxQL.Mongo.Legacy;

/// <summary>
/// The 1.1 engine's validation, code for code and message for message: the limits, the operators,
/// the shape of a path. It knows no model, so no path is unknown to it. The limits are this host's
/// (<c>OxQL:Limits</c>), which carry the names the 1.1 options had.
/// </summary>
internal sealed class LegacyValidator(LimitOptions limits, LegacyOptions legacy)
{
    private static readonly HashSet<string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "contains", "startsWith", "endsWith", "exists", "regex",
    };

    private static readonly HashSet<string> DateTruncUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "year", "quarter", "month", "week", "day", "hour", "minute", "second",
    };

    public List<QueryValidationError> Validate(LegacyRequest request)
    {
        var errors = new List<QueryValidationError>();

        if (request.Pipeline.Count > limits.MaxPipelineStages)
            errors.Add(Error("MAX_PIPELINE_STAGES_EXCEEDED", $"Pipeline exceeds maximum of {limits.MaxPipelineStages} stages."));

        if (request.Pipeline.Count(stage => stage.Lookup is not null) > limits.MaxLookupStages)
            errors.Add(Error("MAX_LOOKUP_STAGES_EXCEEDED", $"Pipeline exceeds maximum of {limits.MaxLookupStages} lookup stages."));

        if (request.Pipeline.Count(stage => stage.Unwind is not null) > limits.MaxUnwindStages)
            errors.Add(Error("MAX_UNWIND_STAGES_EXCEEDED", $"Pipeline exceeds maximum of {limits.MaxUnwindStages} unwind stages."));

        foreach (var stage in request.Pipeline)
        {
            if (stage.Match is { } match) Match(match, errors);
            if (stage.Lookup is { } lookup) Lookup(lookup, errors);
            if (stage.Resolve is { } resolve) Resolve(resolve, errors);
            if (stage.Unwind is { } unwind) Path(unwind.Path, "unwind.path", errors);
            if (stage.Group is { } group) Group(group, errors);
            if (stage.Project is { } project) Project(project, errors);
            if (stage.Sort is { } sort) Sort(sort, errors);
            if (stage.Page is { } page) Page(page, errors);
        }

        return errors;
    }

    private void Match(LegacyMatch match, List<QueryValidationError> errors)
    {
        foreach (var condition in match.And ?? [])
            Condition(condition, errors);

        foreach (var condition in match.Or ?? [])
            Condition(condition, errors);

        if (match.Not is not null)
            Condition(match.Not, errors);

        if (match.Condition is not null)
            Condition(match.Condition, errors);
    }

    private void Condition(LegacyCondition condition, List<QueryValidationError> errors)
    {
        if (condition.IsLogical)
        {
            foreach (var inner in condition.And ?? [])
                Condition(inner, errors);

            foreach (var inner in condition.Or ?? [])
                Condition(inner, errors);

            if (condition.Not is not null)
                Condition(condition.Not, errors);

            return;
        }

        if (string.IsNullOrWhiteSpace(condition.Path))
        {
            errors.Add(Error("INVALID_FILTER_PATH", "Filter condition requires a path."));
            return;
        }

        Path(condition.Path, "filter", errors);

        if (string.IsNullOrWhiteSpace(condition.Op))
        {
            errors.Add(Error("MISSING_OPERATOR", $"Filter condition on path '{condition.Path}' requires an operator.", condition.Path));
            return;
        }

        if (!Operators.Contains(condition.Op))
            errors.Add(Error("UNKNOWN_OPERATOR", $"Unknown operator '{condition.Op}'.", condition.Path));

        if (string.Equals(condition.Op, "regex", StringComparison.OrdinalIgnoreCase)
            && condition.Value is { ValueKind: JsonValueKind.String } pattern
            && pattern.GetString() is { } text
            && text.Length > limits.RegexMaxLength)
            errors.Add(Error("REGEX_TOO_LONG", $"Regex pattern exceeds maximum length of {limits.RegexMaxLength}.", condition.Path));
    }

    private void Lookup(LegacyLookup lookup, List<QueryValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(lookup.From))
            errors.Add(Error("INVALID_LOOKUP_SOURCE", "Lookup 'from' is required."));
        else if (legacy.AllowedLookupSources.Count > 0 && !legacy.AllowedLookupSources.Contains(lookup.From, StringComparer.OrdinalIgnoreCase))
            errors.Add(Error("DISALLOWED_LOOKUP_SOURCE", $"Lookup source '{lookup.From}' is not in the allowed list.", lookup.From));

        Path(lookup.LocalPath, "lookup.localPath", errors);
        Path(lookup.ForeignPath, "lookup.foreignPath", errors);

        if (string.IsNullOrWhiteSpace(lookup.As))
            errors.Add(Error("INVALID_LOOKUP_ALIAS", "Lookup 'as' alias is required."));

        if (!string.IsNullOrWhiteSpace(lookup.Convert)
            && !string.Equals(lookup.Convert, "stringToUuid", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(lookup.Convert, "uuidToString", StringComparison.OrdinalIgnoreCase))
            errors.Add(Error("INVALID_LOOKUP_CONVERT", "Lookup 'convert' must be 'stringToUuid' or 'uuidToString'.", lookup.Convert));

        if (lookup.Filter is { } filter)
            Match(filter, errors);
    }

    private static void Resolve(LegacyResolve resolve, List<QueryValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(resolve.Source))
            errors.Add(Error("INVALID_RESOLVE_SOURCE", "Resolve 'source' is required."));

        Path(resolve.LocalPath, "resolve.localPath", errors);

        if (string.IsNullOrWhiteSpace(resolve.As))
            errors.Add(Error("INVALID_RESOLVE_ALIAS", "Resolve 'as' alias is required."));
    }

    private void Group(LegacyGroup group, List<QueryValidationError> errors)
    {
        if (group.Fields.Count > limits.MaxGroupFields)
            errors.Add(Error("MAX_GROUP_FIELDS_EXCEEDED", $"Group exceeds maximum of {limits.MaxGroupFields} fields."));

        foreach (var key in group.By)
        {
            if (key.Path is not null)
                Path(key.Path, "group.by", errors);

            if (key.DateTruncPath is not null || key.DateTruncUnit is not null)
            {
                Path(key.DateTruncPath, "group.by.dateTrunc", errors);

                if (key.DateTruncUnit is null || !DateTruncUnits.Contains(key.DateTruncUnit))
                    errors.Add(Error("INVALID_DATE_TRUNC_UNIT", $"Invalid dateTrunc unit '{key.DateTruncUnit}'.", key.DateTruncPath));
            }
        }
    }

    private void Project(LegacyProject project, List<QueryValidationError> errors)
    {
        if (project.Include.Count > limits.MaxProjectionFields)
            errors.Add(Error("MAX_PROJECTION_FIELDS_EXCEEDED", $"Projection exceeds maximum of {limits.MaxProjectionFields} fields."));

        foreach (var path in project.Include)
            Path(path, "project.include", errors);
    }

    private static void Sort(IReadOnlyList<LegacySortField> fields, List<QueryValidationError> errors)
    {
        foreach (var field in fields)
        {
            Path(field.Path, "sort", errors);

            if (!string.Equals(field.Direction, "asc", StringComparison.OrdinalIgnoreCase) && !string.Equals(field.Direction, "desc", StringComparison.OrdinalIgnoreCase))
                errors.Add(Error("INVALID_SORT_DIRECTION", $"Invalid sort direction '{field.Direction}'. Must be 'asc' or 'desc'.", field.Path));
        }
    }

    private void Page(LegacyPage page, List<QueryValidationError> errors)
    {
        if (page.Limit <= 0)
            errors.Add(Error("INVALID_PAGE_LIMIT", "Page limit must be greater than 0."));
        else if (page.Limit > limits.MaxPageSize)
            errors.Add(Error("PAGE_SIZE_EXCEEDED", $"Page limit {page.Limit} exceeds maximum of {limits.MaxPageSize}."));
    }

    /// <summary>A path is some text without <c>$</c>, without <c>..</c> and without an empty segment; what it names is the database's to say.</summary>
    private static void Path(string? path, string context, List<QueryValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add(Error("EMPTY_PATH", $"Path is required in {context}."));
            return;
        }

        if (path.Contains('$'))
            errors.Add(Error("INVALID_PATH_DOLLAR", $"Path '{path}' contains '$' which is not allowed.", path));

        if (path.Contains(".."))
            errors.Add(Error("INVALID_PATH_TRAVERSAL", $"Path '{path}' contains '..' which is not allowed.", path));

        if (path.Split('.').Any(string.IsNullOrWhiteSpace))
            errors.Add(Error("EMPTY_PATH_SEGMENT", $"Path '{path}' contains an empty segment.", path));
    }

    internal static QueryValidationError Error(string code, string message, string? path = null) => new() { Code = code, Message = message, Path = path };
}
