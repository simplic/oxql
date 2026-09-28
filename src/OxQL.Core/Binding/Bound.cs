using System.Text.Json;
using MongoDB.Bson;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Model.Addon;

namespace OxQL.Core.Binding;

/// <summary>A wire path resolved against the shape at its stage: where it is stored and what it can do there.</summary>
public sealed record ResolvedPath
{
    /// <summary>The wire path as written.</summary>
    public required string Wire { get; init; }

    /// <summary>The storage path, alias roots verbatim; null for an unstored member or a remote path.</summary>
    public required string? Storage { get; init; }

    /// <summary>The kind at the path (after unwinds).</summary>
    public required Kind Kind { get; init; }

    /// <summary>The shape at the path; null for a remote path.</summary>
    public ShapeDef? Shape { get; init; }

    /// <summary>The model path, when the path is a member of an entity in this model.</summary>
    public PathDef? Path { get; init; }

    /// <summary>The entity whose index resolved the path.</summary>
    public EntityDef? Entity { get; init; }

    /// <summary>Arrays above the path that the pipeline has not unwound; a match through one means "some element".</summary>
    public required int CollectionAncestors { get; init; }

    /// <summary>Whether a filter operand can be encoded for the path at this stage.</summary>
    public required bool Filterable { get; init; }

    /// <summary>Whether a sort can use the path at this stage.</summary>
    public required bool Sortable { get; init; }

    /// <summary>The root the path resolved under.</summary>
    public required ShapeNode Root { get; init; }

    /// <summary>True under a remote resolve alias: the owner binds it.</summary>
    public bool IsRemote { get; init; }

    /// <summary>The addon definition the path binds through, when it is a defined addon key.</summary>
    public AddonDefinition? Addon { get; init; }

    /// <summary>The kind once array traversal is accounted for.</summary>
    public Kind LeafKind => Shape?.LeafKind ?? Kind;

    /// <summary>The leaf shape.</summary>
    public ShapeDef? Leaf => Shape?.Leaf;
}

/// <summary>A node of the shape: what a root name stands for.</summary>
public abstract record ShapeNode(string StoragePrefix)
{
    /// <summary>An entity: the implicit root, or a resolved local target under its alias.</summary>
    public sealed record Entity(EntityDef Def, string StoragePrefix) : ShapeNode(StoragePrefix);

    /// <summary>An unwound element under its alias.</summary>
    public sealed record Element(EntityDef Def, PathDef Source, string StoragePrefix) : ShapeNode(StoragePrefix);

    /// <summary>A lookup alias: an array of the target entity.</summary>
    public sealed record Array(EntityDef Target, string StoragePrefix) : ShapeNode(StoragePrefix);

    /// <summary>A resolved remote target under its alias: its shape lives on another host.</summary>
    public sealed record Remote(string TargetEntity, ResolvedPath Reference, string StoragePrefix) : ShapeNode(StoragePrefix);

    /// <summary>A scalar root: an unwind index.</summary>
    public sealed record Scalar(Kind Kind, string StoragePrefix) : ShapeNode(StoragePrefix);

    /// <summary>
    /// A group output under its alias. A pushed array has no shape of its own — the alias is a
    /// collection nothing filters or sorts — so it carries its element's kind and shape
    /// instead, and the encoder renders each element the way a row renders the member.
    /// </summary>
    public sealed record GroupOutput(Kind Kind, ShapeDef? Shape, string StoragePrefix, Kind ElementKind = Kind.Unknown, ShapeDef? ElementShape = null) : ShapeNode(StoragePrefix);
}

/// <summary>A coerced operand: one value, a set, alternatives to match tolerantly, null, or a raw operand for a remote owner.</summary>
public abstract record BoundOperand
{
    /// <summary>One typed value.</summary>
    public sealed record Single(BsonValue Value) : BoundOperand;

    /// <summary>A set of typed values, tolerant alternatives already unioned.</summary>
    public sealed record Set(IReadOnlyList<BsonValue> Values) : BoundOperand;

    /// <summary>Alternatives one of which matches: the decimal and guid tolerant forms, the addon forms.</summary>
    public sealed record Tolerant(IReadOnlyList<BsonValue> Alternatives) : BoundOperand;

    /// <summary>The <c>null</c> operand: absent or null.</summary>
    public sealed record Null : BoundOperand;

    /// <summary>The operand as written, for a condition the remote owner binds.</summary>
    public sealed record Raw(JsonElement Value) : BoundOperand;

    /// <summary>The shared null.</summary>
    public static readonly Null NullValue = new();
}

/// <summary>A bound condition tree.</summary>
public abstract record BoundCondition
{
    public sealed record And(IReadOnlyList<BoundCondition> Conditions) : BoundCondition;

    public sealed record Or(IReadOnlyList<BoundCondition> Conditions) : BoundCondition;

    public sealed record Not(BoundCondition Condition) : BoundCondition;

    /// <summary>
    /// One comparison. <paramref name="IgnoreCase"/> is whether it folds case and accents: true
    /// or false once bound against a member of this host, null only under a remote alias when
    /// the caller wrote no option, so the owner applies its own default.
    /// </summary>
    public sealed record Leaf(ResolvedPath Path, string Op, BoundOperand Operand, bool? IgnoreCase, bool IsSemiJoin) : BoundCondition;

    /// <summary>A correlated condition on one element of a collection of objects; inner paths are relative to the element.</summary>
    public sealed record Any(ResolvedPath Path, BoundCondition Inner) : BoundCondition;
}

/// <summary>A bound expression of a group aggregate.</summary>
public abstract record BoundExpression
{
    public sealed record Path(ResolvedPath Resolved) : BoundExpression;

    public sealed record Literal(BsonValue Value) : BoundExpression;

    public sealed record Arithmetic(string Operator, IReadOnlyList<BoundExpression> Operands) : BoundExpression;
}

/// <summary>A bound stage.</summary>
public abstract record BoundStage
{
    /// <summary>The engine-owned organisation scope: one equality on one indexed member, at every entry into an entity.</summary>
    public sealed record Scope(EntityDef Entity, string OrganisationStorage, Guid Organisation, GuidRepresentation Representation) : BoundStage;

    public sealed record Match(BoundCondition Condition) : BoundStage;

    /// <summary>
    /// A lookup. <paramref name="ChildSort"/> is the caller's order of the children (the child's key
    /// completes it at compile time), empty for the key order; <paramref name="First"/> places
    /// the first child or null instead of the array; <paramref name="On"/> is the alias of the
    /// parent row, null for the implicit root, and <paramref name="ParentKeyStorage"/> already
    /// lies under it. <paramref name="Stage"/> is the request's stage index, for the diagnostic;
    /// it is bound-only and never rendered.
    /// </summary>
    public sealed record Lookup(EntityDef From, ResolvedPath ChildReference, string As, IReadOnlyList<ResolvedPath> Select, BoundCondition? Filter, int Limit, Scope ChildScope, string ParentKeyStorage, string ChildKeyStorage,
        IReadOnlyList<BoundSortField>? ChildSort = null, bool First = false, string? On = null, int Stage = -1) : BoundStage;

    public sealed record Resolve(ResolvedPath Reference, string As, string TargetEntity, string TargetField, bool IsRemote,
        EntityDef? Target, string? TargetFieldStorage, IReadOnlyList<ResolvedPath>? Select, BoundCondition? Filter, Scope? TargetScope,
        IReadOnlyList<string>? RemoteSelect, JsonElement? RemoteFilter) : BoundStage;

    /// <summary>An unwind; <paramref name="Flatten"/> when it also descends a nested collection of the same items.</summary>
    public sealed record Unwind(ResolvedPath Path, string? As, bool PreserveNull, string? IncludeIndex, BoundFlatten? Flatten = null) : BoundStage;

    public sealed record Group(IReadOnlyList<GroupKey> Keys, IReadOnlyList<Aggregate> Fields) : BoundStage;

    public sealed record Project(bool Inclusion, IReadOnlyList<ResolvedPath> Paths, bool IncludeId) : BoundStage;

    public sealed record Sort(IReadOnlyList<BoundSortField> Fields) : BoundStage;

    /// <summary>The page; <paramref name="CountCap"/> is the request's own count cap, already under the host's, or null for the host's.</summary>
    public sealed record Page(int Limit, int Offset, CursorPayload? Cursor, bool IncludeTotalCount, int? CountCap = null) : BoundStage;
}

/// <summary>
/// The descent of an <c>unwind</c> with <c>flatten</c>: the nested collection's wire name and its
/// storage name on the element, and how many levels are taken (the unwound collection is level
/// 1). <paramref name="Stage"/> is the request's stage index, for the diagnostic; it is bound-only
/// and never rendered.
/// </summary>
public sealed record BoundFlatten(string Member, string Storage, int Depth, int Stage);

/// <summary>One group key.</summary>
public sealed record GroupKey(string As, ResolvedPath? Path, DateTrunc? Trunc, Kind OutputKind, ShapeDef? OutputShape);

/// <summary>A date truncation key.</summary>
public sealed record DateTrunc(ResolvedPath Path, string Unit, string Timezone, string? WeekStart);

/// <summary>One aggregate: its argument as bound, with the argument's kind, and the kind and shape of what it outputs.</summary>
public sealed record Aggregate(string As, string Function, BoundExpression? Argument, Kind ArgumentKind, Kind OutputKind, ShapeDef? OutputShape);

/// <summary>One sort field; <paramref name="IgnoreCase"/> when a string member orders under the collation rather than by its exact value.</summary>
public sealed record BoundSortField(ResolvedPath Path, bool Ascending, bool IgnoreCase = false);

/// <summary>How the next page is addressed.</summary>
public enum PagingMode
{
    /// <summary>By the sort fields' values of the last row, plus the key.</summary>
    Keyset,

    /// <summary>By skipping rows: after a group or an unwind, where the row has no stable key.</summary>
    Offset,
}

/// <summary>What a cursor carries.</summary>
public sealed record CursorPayload(string Fingerprint, PagingMode Mode, IReadOnlyList<CursorValue> Fields, int Offset);

/// <summary>One sort field's value at the last row.</summary>
public sealed record CursorValue(string Wire, bool Ascending, BsonValue Value);

/// <summary>The bound pipeline: what the compiler emits from and the cursor is bound to.</summary>
public sealed record BoundPipeline
{
    public required EntityDef Entity { get; init; }

    public required Guid Organisation { get; init; }

    public required BoundStage.Scope Scope { get; init; }

    /// <summary>The caller's stages in order, bound; the scope is not among them.</summary>
    public required IReadOnlyList<BoundStage> Stages { get; init; }

    /// <summary>The shape the rows have after the last stage.</summary>
    public required Shape FinalShape { get; init; }

    /// <summary>The sort, when the caller gave one.</summary>
    public BoundStage.Sort? Sort { get; init; }

    /// <summary>The page, given or defaulted.</summary>
    public required BoundStage.Page Page { get; init; }

    public required PagingMode PagingMode { get; init; }

    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>A SHA-256 over the canonical bound form without the page; the cursor is bound to it.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The canonical bound form, for explain.</summary>
    public required string Canonical { get; init; }

    /// <summary>Whether any stage resolves through a remote owner.</summary>
    public bool HasRemote => Stages.Any(stage => stage is BoundStage.Resolve { IsRemote: true }) || HasSemiJoin;

    /// <summary>Whether any condition is a semi-join on a remote alias.</summary>
    public bool HasSemiJoin { get; init; }

    /// <summary>
    /// Whether the aggregate runs under the host's collation: some string comparison, sort
    /// field or group key folds case. A pipeline that folds nothing runs without one.
    /// </summary>
    public bool Collated { get; init; }
}

/// <summary>The outcome of binding: a pipeline, or the errors.</summary>
public abstract record BindOutcome
{
    public sealed record Bound(BoundPipeline Pipeline) : BindOutcome;

    public sealed record Failed(Refusal Refusal) : BindOutcome;
}
