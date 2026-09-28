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

    /// <summary>An <c>is</c> names a type that is neither a variant of the member's type nor its concrete base.</summary>
    public const string UnknownVariant = "UNKNOWN_VARIANT";

    // request

    /// <summary>A contract 2 request carries a top-level member a request does not have.</summary>
    public const string UnknownRequestMember = "UNKNOWN_REQUEST_MEMBER";

    // stage
    public const string UnknownStage = "UNKNOWN_STAGE";
    public const string UnknownStageMember = "UNKNOWN_STAGE_MEMBER";

    /// <summary>An unwind's <c>flatten</c> names a member that is not a collection of the same items as the unwound collection.</summary>
    public const string FlattenNotRecursive = "FLATTEN_NOT_RECURSIVE";
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

    /// <summary>A lookup's <c>on</c> names an alias that is not one entity row: a lookup array, an unwound element, a scalar or a group output.</summary>
    public const string LookupOnNotEntity = "LOOKUP_ON_NOT_ENTITY";

    /// <summary>
    /// A stage cannot run on an alias whose rows come from an owner after the page: anything but a
    /// resolve or a lookup (which continue at the owner), a continuation under an <c>elements: "all"</c>
    /// alias, or a projection that keeps a continued alias but drops the alias it continues under.
    /// </summary>
    public const string NotContinuable = "NOT_CONTINUABLE";
    public const string ResolveNotDeclared = "RESOLVE_NOT_DECLARED";

    /// <summary>A resolve's path lies under a collection that is not unwound, and the stage does not say which elements it resolves (<c>elements</c>).</summary>
    public const string ResolveOnCollection = "RESOLVE_ON_COLLECTION";

    /// <summary>A resolve's <c>target</c> names an entity that is not a target of any case of the reference.</summary>
    public const string ResolveTargetNotDeclared = "RESOLVE_TARGET_NOT_DECLARED";

    /// <summary>A resolve's <c>parentAs</c> asks for the owning row of a target that is an entity, not an item of one.</summary>
    public const string ResolveParentNotItem = "RESOLVE_PARENT_NOT_ITEM";

    /// <summary>
    /// A condition on the alias of a remote resolve itself (<c>{"veh":{"eq":null}}</c>,
    /// <c>{"veh":{"exists":true}}</c>): the alias is the owner's row, not a path of the owner, so
    /// no semi-join can express it and it is refused at bind time, before any owner call. A
    /// condition on a member of the alias (<c>veh.matchCode</c>) is a semi-join and is admitted.
    /// </summary>
    public const string ResolveNotFilterable = "RESOLVE_NOT_FILTERABLE";
    public const string ResolveNotSortable = "RESOLVE_NOT_SORTABLE";

    // limits
    public const string MaxPipelineStagesExceeded = "MAX_PIPELINE_STAGES_EXCEEDED";
    public const string MaxLookupStagesExceeded = "MAX_LOOKUP_STAGES_EXCEEDED";
    public const string MaxUnwindStagesExceeded = "MAX_UNWIND_STAGES_EXCEEDED";
    public const string MaxResolveStagesExceeded = "MAX_RESOLVE_STAGES_EXCEEDED";

    /// <summary>More stages continue under one keyed or remote alias than <c>MaxContinuedStages</c>.</summary>
    public const string MaxContinuedStagesExceeded = "MAX_CONTINUED_STAGES_EXCEEDED";
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

    /// <summary>A stage needs OxQL 2.1 at a remote owner (continued stages, typed or item targets, <c>keyedBy</c>) whose health reports an older engine (422).</summary>
    public const string OwnerNotCapable = "OWNER_NOT_CAPABLE";
    public const string SemiJoinTooLarge = "SEMI_JOIN_TOO_LARGE";
    public const string QueryTooExpensive = "QUERY_TOO_EXPENSIVE";
    public const string QueryTimeout = "QUERY_TIMEOUT";
    public const string InternalError = "INTERNAL_ERROR";

    /// <summary>A strict request without <c>cursor</c> or <c>offset</c> matches more rows than its page holds (422).</summary>
    public const string PageIncomplete = "PAGE_INCOMPLETE";

    // compat
    public const string LegacyStageUnsupported = "LEGACY_STAGE_UNSUPPORTED";

    // diagnostics
    public const string EntityIdRetired = "ENTITY_ID_RETIRED";
    public const string TotalCountCapped = "TOTAL_COUNT_CAPPED";
    public const string ResolveTimeout = "RESOLVE_TIMEOUT";
    public const string ResolveUnreachable = "RESOLVE_UNREACHABLE";
    /// <summary>
    /// Dormant at the shipped defaults rather than dead: one resolve stage cannot need more
    /// distinct keys than the page has rows, and <c>MaxPageSize</c> (500) and
    /// <c>MaxReportPageSize</c> (5 000) are below <c>MaxResolveKeys</c> (10 000), so it cannot
    /// fire until a host raises a page size past the resolve cap. <see cref="Models.LimitOptions.MaxResolveKeys"/> says so where an
    /// operator will read it.
    /// </summary>
    public const string ResolvePartial = "RESOLVE_PARTIAL";
    public const string SortOnAddon = "SORT_ON_ADDON";
    public const string RegexUnanchored = "REGEX_UNANCHORED";

    /// <summary>An ordered comparison on a decimal member covered the numerically stored rows only; rows still stored as text are outside it.</summary>
    public const string DecimalTextExcluded = "DECIMAL_TEXT_EXCLUDED";

    /// <summary>An unwind with <c>flatten</c> met items nested deeper than <see cref="Models.LimitOptions.MaxFlattenDepth"/>; they are not in the rows.</summary>
    public const string UnwindDepthTruncated = "UNWIND_DEPTH_TRUNCATED";

    /// <summary>A lookup had more children for some parent than its limit; only the first <c>limit</c> are in the rows.</summary>
    public const string LookupTruncated = "LOOKUP_TRUNCATED";

    /// <summary>
    /// A resolve with <c>onMissing: "report"</c> or <c>"refuse"</c> lost rows: the referenced record
    /// does not exist (<c>not_found</c>), the key does not convert (<c>invalid_key</c>), or the owner
    /// did not answer (<c>owner_unanswered</c>). An error under <c>onMissing: "refuse"</c>.
    /// </summary>
    public const string ResolveMissing = "RESOLVE_MISSING";

    /// <summary>A resolve met a key more than one record holds; the alias holds the first by target order, then key.</summary>
    public const string ResolveAmbiguous = "RESOLVE_AMBIGUOUS";

    /// <summary>An <c>elements: "all"</c> resolve met a row with more targets than <c>MaxLookupLimit</c>; only the first are in the alias.</summary>
    public const string ResolveTruncated = "RESOLVE_TRUNCATED";
}
