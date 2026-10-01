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
    public sealed record Entity(EntityDef Def, string StoragePrefix) : ShapeNode(StoragePrefix)
    {
        /// <summary>
        /// The paths the row shows under a join's alias, relative to it; null for every member. While
        /// the stages bind it is null under contract 2, where every member of the target can be read
        /// and the join loads what is read (improvement plan §3.S); the final shape carries the output
        /// set. Under contract 1 it is the join's select, key included, which is all the join fetches.
        /// </summary>
        public IReadOnlyList<string>? Select { get; init; }

        /// <summary>The alias of the join whose rows the node holds; null for the entity row itself.</summary>
        public string? Join { get; init; }
    }

    /// <summary>An unwound element under its alias.</summary>
    public sealed record Element(EntityDef Def, PathDef Source, string StoragePrefix) : ShapeNode(StoragePrefix)
    {
        /// <summary>The alias of the join whose rows hold the collection; null for a collection of the entity row.</summary>
        public string? Join { get; init; }
    }

    /// <summary>A lookup alias: an array of the target entity.</summary>
    public sealed record Array(EntityDef Target, string StoragePrefix) : ShapeNode(StoragePrefix)
    {
        /// <summary>The paths the row shows of each child, as <see cref="Entity.Select"/>.</summary>
        public IReadOnlyList<string>? Select { get; init; }

        /// <summary>The alias of the lookup whose children the node holds.</summary>
        public string? Join { get; init; }
    }

    /// <summary>
    /// A resolved remote target under its alias: its shape lives on another host. A condition on
    /// a member of it is a semi-join on the owner only when <paramref name="SemiJoinable"/>: a plain
    /// remote resolve, whose key set is one <c>$in</c> on one local member. The alias of a typed,
    /// item, converted or element-wise resolve is not. <paramref name="TargetOpen"/> for the alias of a
    /// resolve continued under another alias without a <c>target</c>: the reference it follows lies
    /// in the model of the entities it continues on, which their owners hold, so
    /// <paramref name="TargetEntity"/> is the keyed stage's first target and not the alias's entity;
    /// explain asks the owners for that (DESIGN §4.3).
    /// </summary>
    public sealed record Remote(string TargetEntity, ResolvedPath Reference, string StoragePrefix, bool SemiJoinable = true, bool TargetOpen = false) : ShapeNode(StoragePrefix);

    /// <summary>
    /// The rows a keyed fetch joins after the page is taken, from local targets (DESIGN §3.4.1
    /// step 6): an entity, or an item of one, per target. Paths under it are checked against the
    /// targets here; they can be projected, never filtered or sorted. <paramref name="Many"/> for
    /// <c>elements: "all"</c>, where the alias holds an array of them.
    /// </summary>
    public sealed record Keyed(IReadOnlyList<KeyedTarget> Targets, bool Many, string StoragePrefix) : ShapeNode(StoragePrefix);

    /// <summary>
    /// The alias of a stage that failed to bind (DESIGN §3.8). A path under it fails without an
    /// error of its own, so one mistake yields one error rather than one per later use.
    /// </summary>
    public sealed record Poisoned(string StoragePrefix) : ShapeNode(StoragePrefix);

    /// <summary>A scalar root: an unwind index.</summary>
    public sealed record Scalar(Kind Kind, string StoragePrefix) : ShapeNode(StoragePrefix);

    /// <summary>
    /// The outcome of a join under the name its stage gave it (<c>outcomeAs</c>, improvement plan
    /// §3.O): a string on every row saying what became of the reference. <paramref name="Join"/> is the
    /// alias of the join it is the outcome of. It follows its join: with <paramref name="InAggregate"/>
    /// (an inline resolve) the aggregate writes it, so a condition, a group key and an aggregate may
    /// read it before the page; otherwise it is written after the page with the join's rows and can
    /// only be projected. It never orders a page.
    /// </summary>
    public sealed record Outcome(string Join, bool InAggregate, string StoragePrefix) : ShapeNode(StoragePrefix);

    /// <summary>
    /// A group output under its alias. A pushed array has no shape of its own — the alias is a
    /// collection nothing filters or sorts — so it carries its element's kind and shape
    /// instead, and the encoder renders each element the way a row renders the member.
    /// </summary>
    public sealed record GroupOutput(Kind Kind, ShapeDef? Shape, string StoragePrefix, Kind ElementKind = Kind.Unknown, ShapeDef? ElementShape = null) : ShapeNode(StoragePrefix);
}

/// <summary>One target a keyed alias may hold: the entity, or the element of <paramref name="Item"/> on it.</summary>
public sealed record KeyedTarget(EntityDef Entity, PathDef? Item);

/// <summary>Which executor runs a resolve (DESIGN §3.4.1 step 5).</summary>
public enum ResolveExecutor
{
    /// <summary>The in-aggregate <c>$lookup</c>: one unconditional case, one local entity target, no item, no conversion.</summary>
    Inline,

    /// <summary>The keyed fetch after the page: every other resolve, remote ones included.</summary>
    Keyed,
}

/// <summary>How a resolve through a collection that is not unwound picks its elements.</summary>
public enum ResolveElements
{
    /// <summary>The first element, in stored order, whose case is selected and whose key resolves.</summary>
    First,

    /// <summary>Every resolved target, as an array.</summary>
    All,
}

/// <summary>What a resolve does with data loss (DESIGN §3.6).</summary>
public enum ResolveOnMissing
{
    /// <summary>The alias is null; only owner failures are reported.</summary>
    Null,

    /// <summary>One <c>RESOLVE_MISSING</c> diagnostic per stage.</summary>
    Report,

    /// <summary>A 422 refusal.</summary>
    Refuse,
}

/// <summary>
/// One case of a resolve's reference as bound: the declaration, the stored value that selects it
/// (null for an unconditional case), and its targets after <c>target</c> narrowed them, in
/// declaration order.
/// </summary>
public sealed record BoundResolveCase(ReferenceDef Declared, BoundCaseCondition? When, IReadOnlyList<BoundResolveTarget> Targets)
{
    /// <summary>How the stored value becomes the targets' key.</summary>
    public KeyAs KeyAs => Declared.KeyAs;
}

/// <summary>
/// What selects a case in a row: the stored value at <paramref name="Storage"/> is one of
/// <paramref name="Values"/>. For a sibling condition <paramref name="Path"/> is the sibling and the
/// values are its stored form; for a variant condition it is the holding object and
/// <paramref name="Storage"/> its discriminator element, the values the discriminators of the
/// named variants and their descendants (<see cref="BsonNull"/> for a value stored without one).
/// Storage is absolute in the row; under <c>elements</c> it is relative to one element of the
/// collection crossed (<see cref="BoundStage.Resolve.CollectionStorage"/>), where the keyed fetch
/// reads it element by element.
/// </summary>
public sealed record BoundCaseCondition(ResolvedPath Path, string Storage, IReadOnlyList<BsonValue> Values, bool IsVariant);

/// <summary>
/// One target of a case as bound. A local target carries its entity, the storage of the matched
/// field (relative to the element for an item target) and of the item collection, what it loads
/// (<paramref name="Select"/>), its filter, its scope and what its owning row loads
/// (<paramref name="ParentSelect"/>); a remote target carries what the owner binds, as paths.
/// A local target also carries the filter as written and the owning row's paths
/// (<paramref name="RemoteFilter"/>, <paramref name="RemoteParentSelect"/>): the keyed fetch sends
/// them to this host's own <c>SelfOwner</c> as an ordinary owner query.
/// <para>
/// Under contract 2 the loads are inferred once every stage is bound (improvement plan §3.S): the
/// key and the output set of the alias, which is the paths the projection names under it, or kept
/// whole the <c>select</c> hint, else the target's key and display (a null
/// <paramref name="RemoteSelect"/>: the owner's own). Under contract 1 they are the select as written.
/// </para>
/// <paramref name="DroppedSelect"/> lists the paths this target does not have, which the target
/// leaves out (the paths of a union are flat); <paramref name="DroppedParentSelect"/> the same for
/// the owning row. A remote target's paths are only known to its owner: the keyed fetch drops them
/// per target at run time (<c>ResolveResult.Dropped</c>).
/// </summary>
public sealed record BoundResolveTarget(
    ReferenceTarget Declared,
    EntityDef? Entity,
    string? FieldStorage,
    string? ItemStorage,
    IReadOnlyList<ResolvedPath>? Select,
    IReadOnlyList<string>? RemoteSelect,
    BoundCondition? Filter,
    JsonElement? RemoteFilter,
    BoundStage.Scope? Scope,
    IReadOnlyList<ResolvedPath>? ParentSelect,
    IReadOnlyList<string>? RemoteParentSelect,
    IReadOnlyList<string> DroppedSelect,
    IReadOnlyList<string>? DroppedParentSelect = null)
{
    /// <summary>True when the target lives on another host.</summary>
    public bool IsRemote => Declared.IsRemote;
}

/// <summary>
/// What makes a keyed stage a remote lookup (DESIGN §3.4.4): a <c>lookup</c> whose <c>from</c> is
/// another service's entity, run by the keyed fetch like a remote resolve whose key is the parent
/// row's key and whose target member is the child's <see cref="Path"/>. The owner groups its rows per
/// key (<c>keyedBy</c>), at most <see cref="PerKey"/> per key, ranked by <see cref="Sort"/> and the
/// child key. <see cref="From"/> is the child entity as written, <see cref="Entity"/> without its
/// item; <see cref="Item"/> the item collection of an <c>entity#item</c> child, whose element is the
/// child and whose owning row goes under the stage's <c>ParentAs</c>. <see cref="Rows"/> is set on an
/// entity child whose <see cref="Path"/> crosses one of its collections: a row holding a key in some
/// element counts once for it. <see cref="Parent"/> is this host's entity whose key the keys are,
/// <see cref="On"/> the alias of the parent row (null for the implicit root). <see cref="Sort"/> is the
/// order as written, bound by the owner; <see cref="Limit"/> the bound limit (1 under
/// <see cref="First"/>). <see cref="Filter"/> is the filter as the owner is sent it, every variable
/// substituted.
/// </summary>
public sealed record BoundRemoteLookup(
    string From,
    string Entity,
    string? Item,
    string Path,
    string Parent,
    string? On,
    IReadOnlyList<Models.SortField> Sort,
    bool First,
    int Limit,
    bool Rows,
    IReadOnlyList<string>? Select,
    IReadOnlyList<string>? ParentSelect,
    JsonElement? Filter)
{
    /// <summary>The service that owns the child.</summary>
    public string Service => Entity.Split('.')[0];

    /// <summary>The rows the owner answers per key: one under <c>first</c>, else one more than the limit, which tells a truncated parent.</summary>
    public int PerKey => First ? 1 : Limit + 1;
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
        IReadOnlyList<BoundSortField>? ChildSort = null, bool First = false, string? On = null, int Stage = -1) : BoundStage
    {
        /// <summary>The <c>select</c> as the caller wrote it: the hint of what the alias shows kept whole; null when none was written. Bound-only, never rendered.</summary>
        public IReadOnlyList<string>? Hint { get; init; }
    }

    /// <summary>
    /// A resolve. The members up to <paramref name="RemoteFilter"/> describe the first target of
    /// the first case as 2.0 did, and are all an inline or a plain remote resolve needs;
    /// <paramref name="Cases"/> holds every selected case with its bound targets.
    /// <paramref name="IsRemote"/> is true when some target lives on another host.
    /// <paramref name="Elements"/> is set on a path through one collection that is not unwound,
    /// whose storage is <paramref name="CollectionStorage"/>. <paramref name="NarrowedTo"/> is the
    /// <c>target</c> that excluded other targets; <paramref name="ParentAs"/> the alias of an item
    /// target's owning row. <paramref name="OnMissing"/> is what the caller wrote,
    /// <paramref name="EffectiveOnMissing"/> what applies. <paramref name="Executor"/>,
    /// <paramref name="EffectiveOnMissing"/> and <paramref name="Stage"/> are bound-only and never
    /// rendered.
    /// </summary>
    public sealed record Resolve(ResolvedPath Reference, string As, string TargetEntity, string TargetField, bool IsRemote,
        EntityDef? Target, string? TargetFieldStorage, IReadOnlyList<ResolvedPath>? Select, BoundCondition? Filter, Scope? TargetScope,
        IReadOnlyList<string>? RemoteSelect, JsonElement? RemoteFilter,
        ResolveExecutor Executor = ResolveExecutor.Inline,
        IReadOnlyList<BoundResolveCase>? Cases = null,
        ResolveElements? Elements = null,
        string? CollectionStorage = null,
        string? NarrowedTo = null,
        string? ParentAs = null,
        ResolveOnMissing? OnMissing = null,
        ResolveOnMissing EffectiveOnMissing = ResolveOnMissing.Null,
        int Stage = -1,
        BoundRemoteLookup? RemoteLookup = null) : BoundStage
    {
        /// <summary>
        /// The 2.0 form: one simple case, no <c>elements</c>, no narrowing, no owning row. Such a
        /// resolve renders as 2.0 did. A remote lookup is never one.
        /// </summary>
        public bool IsPlain => RemoteLookup is null && (Cases is null or [{ Declared.IsSimple: true }]) && Elements is null && NarrowedTo is null && ParentAs is null;

        /// <summary>The caller's kind of the stage: <c>lookup</c> for a remote lookup, <c>resolve</c> otherwise.</summary>
        public string Kind => RemoteLookup is null ? "resolve" : "lookup";

        /// <summary>
        /// Whether the resolve needs the keyed fetch of OxQL 2.1 (DESIGN §3.5): a keyed resolve other
        /// than a plain remote one, which the remote resolver runs as it did under 2.0.
        /// </summary>
        public bool NeedsKeyedFetch => Executor == ResolveExecutor.Keyed && !(IsRemote && IsPlain);

        /// <summary>The <c>select</c> as the caller wrote it: the hint of what the alias shows kept whole; null when none was written. Bound-only, never rendered.</summary>
        public IReadOnlyList<string>? Hint { get; init; }

        /// <summary>
        /// The root that carries the join's outcome on every row (<c>outcomeAs</c>); null when the stage
        /// names none. A stage that names one reads its outcomes: a key two records hold is told
        /// (<c>ambiguous</c>) and a record its filter left out is told from a missing one
        /// (<c>excluded</c>), as under an <c>onMissing</c> other than <c>null</c>.
        /// </summary>
        public string? OutcomeAs { get; init; }

        /// <summary>The roots the stage fills: its alias and, when it names one, its outcome.</summary>
        public IEnumerable<string> Names => OutcomeAs is null ? [As] : [As, OutcomeAs];

        /// <summary>Whether something reads the stage's outcomes: an effective <c>onMissing</c> other than <c>null</c>, or the outcome under a name.</summary>
        public bool ReadsOutcomes => EffectiveOnMissing != ResolveOnMissing.Null || OutcomeAs is not null;
    }

    /// <summary>An unwind; <paramref name="Flatten"/> when it also descends a nested collection of the same items.</summary>
    public sealed record Unwind(ResolvedPath Path, string? As, bool PreserveNull, string? IncludeIndex, BoundFlatten? Flatten = null, bool KeepPath = true) : BoundStage;

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

    /// <summary>
    /// The caller's stage index of each bound stage, by position (null for a sort or page the binder
    /// supplied). A caller stage that binds to no stage, such as an empty <c>match</c>, leaves no
    /// position, so a position is not a caller index. Null when the pipeline was not built by the
    /// binder; <see cref="CallerIndexOf(int)"/> then takes the position.
    /// </summary>
    public IReadOnlyList<int?>? CallerIndexes { get; init; }

    /// <summary>The caller's index of the stage at <paramref name="position"/>, or null for a stage the binder supplied.</summary>
    public int? CallerIndexOf(int position) =>
        position < 0 || position >= Stages.Count ? null
        : CallerIndexes is { } indexes ? position < indexes.Count ? indexes[position] : null
        : position;

    /// <summary>The caller's index of a bound stage, or null when it is not one of <see cref="Stages"/> or the binder supplied it.</summary>
    public int? CallerIndexOf(BoundStage stage)
    {
        for (var position = 0; position < Stages.Count; position++)
            if (ReferenceEquals(Stages[position], stage))
                return CallerIndexOf(position);

        return null;
    }

    /// <summary>
    /// The shape the rows have after the last stage. Under contract 2 each join alias in it carries its
    /// output set (<see cref="ShapeNode.Entity.Select"/>): what the row shows under the alias, whatever
    /// the join loaded for the stages that read it.
    /// </summary>
    public required Shape FinalShape { get; init; }

    /// <summary>The read ledger: every path a stage reads, in the order the stages bound them (improvement plan §3.S).</summary>
    public IReadOnlyList<PathRead> Reads { get; init; } = [];

    /// <summary>What each join loads and shows, by alias (a resolve's or lookup's <c>as</c> and <c>parentAs</c>); empty under contract 1.</summary>
    public IReadOnlyDictionary<string, JoinLoad> Loads { get; init; } = new Dictionary<string, JoinLoad>(StringComparer.Ordinal);

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

    /// <summary>The internal per-key owner answer the request asked for, or null (DESIGN §3.5.2 step 3).</summary>
    public BoundKeyedBy? KeyedBy { get; init; }

    /// <summary>
    /// Whether the aggregate runs under the host's collation: some string comparison, sort
    /// field or group key folds case. A pipeline that folds nothing runs without one.
    /// </summary>
    public bool Collated { get; init; }

    /// <summary>
    /// Whether the request is strict (contract 2): what the executor detects to refuse, never what
    /// binds. It is not part of the canonical form or the fingerprint.
    /// </summary>
    public bool Strict { get; init; }
}

/// <summary>
/// The bound <c>keyedBy</c> of an internal owner query: the matched member
/// <paramref name="Path"/> (stored at <c>Path.Storage</c>), the keys in stored form, and at most
/// <paramref name="PerKey"/> rows per key. For an item target <paramref name="ItemStorage"/> is the
/// item collection, each row carries the matched element under <paramref name="ElementAlias"/>,
/// and <paramref name="ElementFieldStorage"/> is the member on that element.
/// <para>
/// With <paramref name="KeyAlias"/> (<c>rows: "entity"</c>) a path through the item collection answers
/// whole rows instead: one per key some element of the row holds, that key under
/// <see cref="Key"/>. <paramref name="References"/> is the entity of the asking host the path must
/// declare a reference to (a remote lookup's parent); null for a resolve's owner query.
/// </para>
/// </summary>
public sealed record BoundKeyedBy(ResolvedPath Path, IReadOnlyList<BsonValue> Keys, int PerKey, string? ItemStorage, string? ElementAlias, string? ElementFieldStorage,
    string? KeyAlias = null, string? References = null)
{
    /// <summary>The alias an item target's element travels under in the owner's rows.</summary>
    public const string Element = "oxEl";

    /// <summary>The member a whole row answered per key (<c>rows: "entity"</c>) carries that key under.</summary>
    public const string Key = "oxKey";

    /// <summary>
    /// The storage every row's key lies at after the prologue: the key alias of a whole-row answer, the
    /// element's member for an item, the entity's otherwise.
    /// </summary>
    public string PartitionStorage => KeyAlias ?? (ItemStorage is null ? Path.Storage! : ElementAlias + "." + ElementFieldStorage);
}

/// <summary>
/// The outcome of binding: a pipeline, or the errors. Either carries the <see cref="BindTrace"/> of
/// the stage loop once the entity bound (DESIGN §4.6), so explain answers the shapes of the part
/// that binds; a refusal before the stage loop (no organisation, unknown entity) carries none.
/// </summary>
public abstract record BindOutcome
{
    /// <summary>The shape after each stage, when the stage loop ran.</summary>
    public BindTrace? Trace { get; init; }

    /// <summary>
    /// The diagnostics binding produced up to where it stopped, also for a request that did not bind:
    /// explain answers them beside the errors. A bound pipeline carries the same list.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>The request bound: <paramref name="Pipeline"/> is what the compiler reads.</summary>
    public sealed record Bound(BoundPipeline Pipeline) : BindOutcome;

    /// <summary>The request did not bind: <paramref name="Refusal"/> carries every error.</summary>
    public sealed record Failed(Refusal Refusal) : BindOutcome;
}

/// <summary>How a caller stage bound (DESIGN §4.3 <c>steps[].status</c>).</summary>
public enum StageStatus
{
    /// <summary>It bound.</summary>
    Ok,

    /// <summary>It carries an error of its own.</summary>
    Error,

    /// <summary>It failed only under an alias an earlier stage failed to create (DESIGN §3.8).</summary>
    Skipped,
}

/// <summary>
/// One caller stage of the stage loop: its kind (null when it names none), its status, and the
/// shape before and after it. Shapes are immutable, so these are the very instances the binder
/// folded through; after a failed stage, <see cref="After"/> holds its aliases poisoned.
/// </summary>
public sealed record StageTrace(int Index, string? Kind, StageStatus Status, Shape Before, Shape After);

/// <summary>
/// The stage loop as it ran: the entry shape, every caller stage in order, and the final shape.
/// The shape before pipeline index <c>at</c> is <see cref="ShapeAt"/> (0 the entry, the pipeline
/// length the final shape), which explain reads for the row at each stage.
/// </summary>
public sealed record BindTrace(Shape Entry, IReadOnlyList<StageTrace> Stages, Shape Final)
{
    /// <summary>The read ledger as far as the stages bound: every path a stage reads, with its use and the join it loads from.</summary>
    public IReadOnlyList<PathRead> Reads { get; init; } = [];

    /// <summary>The shape before pipeline index <paramref name="at"/>; past the last stage, the final shape.</summary>
    public Shape ShapeAt(int at) =>
        at <= 0 ? Entry
        : at <= Stages.Count ? Stages[at - 1].After
        : Final;
}
