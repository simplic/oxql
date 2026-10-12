using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.Core.Binding;

/// <summary>
/// What would have to change for a request to run, said of every error, diagnostic and note of an
/// explain answer (<c>basis</c>), and the verdict derived from it (<c>executable</c>). A reader that
/// decides whether a stored query may be saved reads the basis, not the code:
/// <list type="bullet">
/// <item><see cref="Request"/>: wrong as written, on any service and any data; the request changes.</item>
/// <item><see cref="Model"/>: contradicts the model of the service that binds it, at the revision the
/// answer names; a deployment of that service changes it.</item>
/// <item><see cref="Limits"/>: exceeds a configured limit of the binding host; host configuration changes it.</item>
/// <item><see cref="Organisation"/>: depends on the organisation's addon definitions; a definition (data) changes it.</item>
/// <item><see cref="Owner"/>: a part could not be checked: an owner did not answer, is not reachable or was not asked.</item>
/// <item><see cref="Data"/>: only a run can tell.</item>
/// <item><see cref="Advisory"/>: the query runs; behaviour worth knowing.</item>
/// </list>
/// Every code of <see cref="Codes"/> and every note of <see cref="Notes"/> has exactly one entry; a test
/// enumerates both against the tables, so a code cannot be added without its basis.
/// </summary>
public static class Basis
{
    /// <summary>Wrong as written: the request changes.</summary>
    public const string Request = "request";
    /// <summary>Against the model of the service that binds it: a deployment changes it.</summary>
    public const string Model = "model";
    /// <summary>Above a configured limit of the binding host.</summary>
    public const string Limits = "limits";
    /// <summary>Depends on an addon definition of the organisation.</summary>
    public const string Organisation = "organisation";
    /// <summary>A part could not be checked at its owner.</summary>
    public const string Owner = "owner";
    /// <summary>Only a run can tell.</summary>
    public const string Data = "data";
    /// <summary>The query runs; worth knowing.</summary>
    public const string Advisory = "advisory";

    /// <summary>The bases, strongest first: the order a wrapper takes the basis of what it wraps in.</summary>
    public static readonly IReadOnlyList<string> All = [Request, Model, Limits, Organisation, Owner, Data, Advisory];

    /// <summary>The verdict: the request can never run as it is.</summary>
    public const string Never = "never";

    /// <summary>The verdict: nothing final speaks against it, and a part is not known.</summary>
    public const string Unknown = "unknown";

    /// <summary>The verdict: the request binds and every part was checked.</summary>
    public const string Yes = "yes";

    /// <summary>The basis of every error and diagnostic code (<see cref="Codes"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> OfCode = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // wrong as written
        [Codes.InvalidPath] = Request, [Codes.InvalidAlias] = Request, [Codes.OperandNotArray] = Request, [Codes.UnknownOperator] = Request,
        [Codes.EmptyLogicalGroup] = Request, [Codes.InvalidRegex] = Request, [Codes.UnknownRequestMember] = Request, [Codes.UnknownStage] = Request,
        [Codes.UnknownStageMember] = Request, [Codes.StageAfterPage] = Request, [Codes.MultiplePageStages] = Request, [Codes.MixedProjection] = Request,
        [Codes.UnknownAggFunction] = Request, [Codes.InvalidDateTruncUnit] = Request, [Codes.InvalidTimezone] = Request, [Codes.InvalidSortDirection] = Request,
        [Codes.InvalidPageLimit] = Request, [Codes.UnboundVariable] = Request, [Codes.InvalidVariable] = Request, [Codes.LegacyStageUnsupported] = Request,
        [Codes.FilterNotPlaceable] = Request, [Codes.InvalidFilter] = Request, [Codes.InvalidRequestBody] = Request,

        // against the model of the service that binds it
        [Codes.UnknownEntity] = Model, [Codes.UnknownPath] = Model, [Codes.NotStored] = Model, [Codes.NotFilterable] = Model, [Codes.NotSortable] = Model,
        [Codes.NotACollection] = Model, [Codes.UnwindOrder] = Model, [Codes.AliasCollision] = Model, [Codes.InvalidOperand] = Model,
        [Codes.UnknownEnumMember] = Model, [Codes.DecimalTextNotOrderable] = Model, [Codes.OptionNotApplicable] = Model, [Codes.AnyNotApplicable] = Model,
        [Codes.UnknownVariant] = Model, [Codes.FlattenNotRecursive] = Model, [Codes.GroupOnCollection] = Model, [Codes.InvalidAggregateArgument] = Model,
        [Codes.LookupNotDeclared] = Model, [Codes.LookupOnNotEntity] = Model, [Codes.NotContinuable] = Model, [Codes.UnionCardinalityMismatch] = Model,
        [Codes.ResolveNotDeclared] = Model, [Codes.ResolveOnCollection] = Model, [Codes.ResolveTargetNotDeclared] = Model, [Codes.ResolveParentNotItem] = Model,
        [Codes.ResolveNotFilterable] = Model, [Codes.ResolveNotSortable] = Model,
        // An owner answered and refused: alone it is the owner's model speaking; beside the errors it wraps, the strongest of them (Of).
        [Codes.ResolveRefused] = Model,

        // against a configured limit of the binding host
        [Codes.MaxPipelineStagesExceeded] = Limits, [Codes.MaxLookupStagesExceeded] = Limits, [Codes.MaxUnwindStagesExceeded] = Limits,
        [Codes.MaxResolveStagesExceeded] = Limits, [Codes.MaxContinuedStagesExceeded] = Limits, [Codes.MaxGroupFieldsExceeded] = Limits,
        [Codes.MaxProjectionFieldsExceeded] = Limits, [Codes.MaxConditionsExceeded] = Limits, [Codes.MaxFilterEntriesExceeded] = Limits, [Codes.MaxVariablesExceeded] = Limits,
        [Codes.PageSizeExceeded] = Limits, [Codes.LookupLimitExceeded] = Limits, [Codes.MaxOffsetExceeded] = Limits, [Codes.RegexTooLong] = Limits,
        [Codes.BatchTooLarge] = Limits, [Codes.RequestTooLarge] = Limits, [Codes.ExplainLimit] = Limits, [Codes.ValuesLimitExceeded] = Limits,

        // a part that could not be checked
        [Codes.ResolveUnavailable] = Owner,

        // only a run can tell
        [Codes.SemiJoinTooLarge] = Data, [Codes.QueryTooExpensive] = Data, [Codes.QueryTimeout] = Data, [Codes.PageIncomplete] = Data,
        [Codes.CursorInvalid] = Data, [Codes.InternalError] = Data, [Codes.AccessDenied] = Data, [Codes.TotalCountCapped] = Data,
        [Codes.ResolveTimeout] = Data, [Codes.ResolveUnreachable] = Data, [Codes.ResolvePartial] = Data, [Codes.ResolveMissing] = Data,
        [Codes.ResolveAmbiguous] = Data, [Codes.ResolveTruncated] = Data, [Codes.LookupTruncated] = Data, [Codes.UnwindDepthTruncated] = Data,
        [Codes.ValuesScanCapped] = Data,

        // the query runs
        [Codes.EntityIdRetired] = Advisory, [Codes.SortOnAddon] = Advisory, [Codes.RegexUnanchored] = Advisory, [Codes.DecimalTextExcluded] = Advisory,
    };

    /// <summary>The basis of every explain note (<see cref="Notes"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> OfNote = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Notes.RemoteUnchecked] = Owner, [Notes.ExplainLimit] = Owner, [Notes.ExplainTrimmed] = Owner,

        [Notes.TextFolds] = Advisory, [Notes.PatternFoldsCaseOnly] = Advisory, [Notes.PatternFolds] = Advisory, [Notes.ExactForcesExact] = Advisory,
        [Notes.SomeElement] = Advisory, [Notes.NeqMatchesAbsent] = Advisory, [Notes.OnlyForVariants] = Advisory, [Notes.SnapshotCopy] = Advisory,
        [Notes.JoinBeforePage] = Advisory, [Notes.JoinAfterPage] = Advisory, [Notes.OwnerBinds] = Advisory, [Notes.SelectPathNotOnTarget] = Advisory,
        [Notes.CountCap] = Advisory, [Notes.LookupLimit] = Advisory, [Notes.OffsetPaging] = Advisory, [Notes.MissingPolicy] = Advisory,
        [Notes.ReportPage] = Advisory, [Notes.IndexAdvice] = Advisory, [Notes.RemoteLookup] = Advisory, [Notes.FilterBindsEarlier] = Advisory,
        [Notes.ProjectAfterPage] = Advisory, [Notes.SortBeforeUnwind] = Advisory, [Notes.UnwindPrefilter] = Advisory, [Notes.GroupScans] = Advisory, [Notes.ValuesScans] = Advisory,

        // the query binds; what an owner's rows hold decides whether a run answers
        [Notes.SemiJoinNotByKey] = Data,

        // the host's model declares a target the owner's does not have
        [Notes.ReferenceTargetUnknown] = Model,
    };

    /// <summary>The codes that are the organisation's to decide when their path lies under an addon bag.</summary>
    private static readonly IReadOnlySet<string> ByDefinition = new HashSet<string>(StringComparer.Ordinal)
    {
        Codes.NotFilterable, Codes.NotSortable, Codes.InvalidOperand, Codes.UnknownEnumMember,
    };

    /// <summary>Whether an error with this code depends on an addon definition when its path lies under an addon bag.</summary>
    public static bool DependsOnDefinition(string code) => ByDefinition.Contains(code);

    /// <summary>The basis of a note, by its code.</summary>
    public static string Of(Diagnostic note)
    {
        ArgumentNullException.ThrowIfNull(note);

        return OfNote.TryGetValue(note.Code, out var basis) ? basis : OfCode.TryGetValue(note.Code, out basis) ? basis : Advisory;
    }

    /// <summary>
    /// The errors of one answer with their basis: each its code's, an error under an addon bag the
    /// organisation's (<paramref name="addon"/> answers its key and the state of its definition, or
    /// null; an owner's error says it itself, with <c>params.addon</c> beside <c>params.owner</c>), and
    /// <c>RESOLVE_REFUSED</c> beside the errors it wraps the strongest of them.
    /// </summary>
    public static IReadOnlyList<QueryValidationError> Of(IReadOnlyList<QueryValidationError> errors, Func<QueryValidationError, (string Key, string State)?> addon)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(addon);

        var based = errors.Select(error =>
        {
            if (DependsOnDefinition(error.Code) && addon(error) is { } definition)
            {
                var parameters = new Dictionary<string, object?>(error.Params ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
                {
                    ["addon"] = new Dictionary<string, object?> { ["key"] = definition.Key, ["state"] = definition.State },
                };

                return error with { Basis = Organisation, Params = parameters };
            }

            // An owner's error under its own addon bag: the owner said whose it is to change (its
            // params.addon), and this host's row holds no member of the owner's to ask about.
            if (DependsOnDefinition(error.Code) && error.Params is { } said && said.ContainsKey("owner") && said.TryGetValue("addon", out var theirs) && theirs is not null)
                return error with { Basis = Organisation };

            return error with { Basis = OfCode.TryGetValue(error.Code, out var basis) ? basis : Data };
        }).ToList();

        var wrapped = based.Where(error => error.Code != Codes.ResolveRefused).Select(error => error.Basis!).OrderBy(basis => All.ToList().IndexOf(basis)).FirstOrDefault();

        return wrapped is null ? based : based.Select(error => error.Code == Codes.ResolveRefused ? error with { Basis = wrapped } : error).ToList();
    }

    /// <summary>
    /// The verdict of an answer: <see cref="Never"/> with an error whose basis is the request, the model
    /// or a limit; else <see cref="Unknown"/> with any other error or an answer that is not complete;
    /// else <see cref="Yes"/>.
    /// </summary>
    public static string Executable(IReadOnlyList<QueryValidationError> errors, bool complete)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Any(error => error.Basis is Request or Model or Limits))
            return Never;

        return errors.Count > 0 || !complete ? Unknown : Yes;
    }
}
