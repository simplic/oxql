namespace OxQL.Core.Binding;

/// <summary>
/// The closed list of error and diagnostic codes. Codes are the contract; messages may
/// change. An error refuses the request; a diagnostic travels with a successful response.
/// </summary>
public static class Codes
{
    // entity
    public const string UnknownEntity = "UNKNOWN_ENTITY";

    // path
    public const string InvalidPath = "INVALID_PATH";
    public const string UnknownPath = "UNKNOWN_PATH";
    public const string NotStored = "NOT_STORED";
    public const string NotFilterable = "NOT_FILTERABLE";
    public const string NotSortable = "NOT_SORTABLE";
    public const string NotACollection = "NOT_A_COLLECTION";
    public const string UnwindOrder = "UNWIND_ORDER";
    public const string AliasCollision = "ALIAS_COLLISION";
    public const string InvalidAlias = "INVALID_ALIAS";

    // operand
    public const string InvalidOperand = "INVALID_OPERAND";
    public const string UnknownEnumMember = "UNKNOWN_ENUM_MEMBER";
    public const string OperandNotArray = "OPERAND_NOT_ARRAY";

    /// <summary>An ordered comparison over a decimal member whose storage is text, which cannot be ordered numerically.</summary>
    public const string DecimalTextNotOrderable = "DECIMAL_TEXT_NOT_ORDERABLE";

    public const string UnboundVariable = "UNBOUND_VARIABLE";
    public const string InvalidVariable = "INVALID_VARIABLE";

    // condition
    public const string UnknownOperator = "UNKNOWN_OPERATOR";
    public const string EmptyLogicalGroup = "EMPTY_LOGICAL_GROUP";
    public const string OptionNotApplicable = "OPTION_NOT_APPLICABLE";
    public const string InvalidRegex = "INVALID_REGEX";
    public const string RegexTooLong = "REGEX_TOO_LONG";
    public const string AnyNotApplicable = "ANY_NOT_APPLICABLE";

    // stage
    public const string UnknownStage = "UNKNOWN_STAGE";
    public const string UnknownStageMember = "UNKNOWN_STAGE_MEMBER";
    public const string StageAfterPage = "STAGE_AFTER_PAGE";
    public const string MultiplePageStages = "MULTIPLE_PAGE_STAGES";
    public const string MixedProjection = "MIXED_PROJECTION";
    public const string GroupOnCollection = "GROUP_ON_COLLECTION";
    public const string UnknownAggFunction = "UNKNOWN_AGG_FUNCTION";
    public const string InvalidAggregateArgument = "INVALID_AGGREGATE_ARGUMENT";
    public const string InvalidDateTruncUnit = "INVALID_DATE_TRUNC_UNIT";
    public const string InvalidTimezone = "INVALID_TIMEZONE";
    public const string InvalidSortDirection = "INVALID_SORT_DIRECTION";
    public const string LookupNotDeclared = "LOOKUP_NOT_DECLARED";
    public const string ResolveNotDeclared = "RESOLVE_NOT_DECLARED";
    /// <summary>
    /// Reserved and currently unreachable: the shape it was written for — a condition on a
    /// remote resolve's alias — is accepted and pushed down to the owner as a semi-join, which
    /// answers 200 with the right rows. It stays in the closed list because the client
    /// declares and translates it too, and the two lists are one contract; removing it is a
    /// coordinated change on both sides, not a tidy-up here.
    /// </summary>
    public const string ResolveNotFilterable = "RESOLVE_NOT_FILTERABLE";
    public const string ResolveNotSortable = "RESOLVE_NOT_SORTABLE";

    // limits
    public const string MaxPipelineStagesExceeded = "MAX_PIPELINE_STAGES_EXCEEDED";
    public const string MaxLookupStagesExceeded = "MAX_LOOKUP_STAGES_EXCEEDED";
    public const string MaxUnwindStagesExceeded = "MAX_UNWIND_STAGES_EXCEEDED";
    public const string MaxResolveStagesExceeded = "MAX_RESOLVE_STAGES_EXCEEDED";
    public const string MaxGroupFieldsExceeded = "MAX_GROUP_FIELDS_EXCEEDED";
    public const string MaxProjectionFieldsExceeded = "MAX_PROJECTION_FIELDS_EXCEEDED";
    public const string MaxConditionsExceeded = "MAX_CONDITIONS_EXCEEDED";
    public const string MaxVariablesExceeded = "MAX_VARIABLES_EXCEEDED";
    public const string InvalidPageLimit = "INVALID_PAGE_LIMIT";
    public const string PageSizeExceeded = "PAGE_SIZE_EXCEEDED";
    public const string LookupLimitExceeded = "LOOKUP_LIMIT_EXCEEDED";
    public const string MaxOffsetExceeded = "MAX_OFFSET_EXCEEDED";
    public const string BatchTooLarge = "BATCH_TOO_LARGE";
    public const string RequestTooLarge = "REQUEST_TOO_LARGE";

    // cursor
    public const string CursorInvalid = "CURSOR_INVALID";

    // access
    public const string AccessDenied = "ACCESS_DENIED";

    // execution
    public const string ResolveUnavailable = "RESOLVE_UNAVAILABLE";
    public const string ResolveRefused = "RESOLVE_REFUSED";
    public const string SemiJoinTooLarge = "SEMI_JOIN_TOO_LARGE";
    public const string QueryTooExpensive = "QUERY_TOO_EXPENSIVE";
    public const string QueryTimeout = "QUERY_TIMEOUT";
    public const string InternalError = "INTERNAL_ERROR";

    // compat
    public const string LegacyStageUnsupported = "LEGACY_STAGE_UNSUPPORTED";

    // diagnostics
    public const string EntityIdRetired = "ENTITY_ID_RETIRED";
    public const string TotalCountCapped = "TOTAL_COUNT_CAPPED";
    public const string ResolveTimeout = "RESOLVE_TIMEOUT";
    public const string ResolveUnreachable = "RESOLVE_UNREACHABLE";
    /// <summary>
    /// Dormant at the shipped defaults rather than dead: one resolve stage cannot need more
    /// distinct keys than the page has rows, and <c>MaxPageSize</c> (500) is below
    /// <c>MaxResolveKeys</c> (2 000), so it cannot fire until a host raises the page size past
    /// the resolve cap. <see cref="Models.LimitOptions.MaxResolveKeys"/> says so where an
    /// operator will read it.
    /// </summary>
    public const string ResolvePartial = "RESOLVE_PARTIAL";
    public const string SortOnAddon = "SORT_ON_ADDON";
    public const string RegexUnanchored = "REGEX_UNANCHORED";

    /// <summary>An ordered comparison on a decimal member covered the numerically stored rows only; rows still stored as text are outside it.</summary>
    public const string DecimalTextExcluded = "DECIMAL_TEXT_EXCLUDED";
}
