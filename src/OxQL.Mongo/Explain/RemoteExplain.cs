using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo.Resolve;

namespace OxQL.Mongo.Explain;

/// <summary>
/// What owners' internal explain answered (DESIGN §4.1): each answer kept 30 seconds by organisation,
/// user, owner service and the hash of the forwarded body with its variables substituted, so the
/// studio's debounced explains of one query do not ask the owner again and no user is answered from
/// another's call (an owner may refuse one user what it answers another). The owner's schema revision
/// is not part of the key, since the origin learns it only from the answer; the 30 seconds bound how
/// long a changed owner model is answered from before. Only answers are kept, never a failed call.
/// </summary>
public sealed class ExplainForwardCache : IDisposable
{
    /// <summary>How long an owner's answer is kept.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>The most answers kept.</summary>
    public const int MaxEntries = 1_000;

    private readonly MemoryCache answers;
    private readonly TimeProvider time;

    /// <summary>An empty cache; <paramref name="time"/> is the clock answers expire by.</summary>
    public ExplainForwardCache(TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        answers = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxEntries });
    }

    /// <summary>The key of one forwarded body, for no user in particular.</summary>
    public static string KeyOf(Guid organisation, string service, ExplainRequest request) => KeyOf(organisation, null, service, request);

    /// <summary>
    /// The key of one forwarded body for one user of an organisation. The budget an internal explain
    /// carries is not part of it: what is left of an origin's explain changes nothing an owner answers.
    /// </summary>
    public static string KeyOf(Guid organisation, string? user, string service, ExplainRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = JsonSerializer.SerializeToUtf8Bytes(request with { Budget = null }, OxQLJson.Wire);
        var who = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user ?? "")))[..16];

        return $"{organisation:N}|{who}|{service}|{Convert.ToHexString(SHA256.HashData(body))}";
    }

    /// <summary>A kept answer, as a copy of its own, or null.</summary>
    public JsonObject? Get(string key) =>
        answers.TryGetValue(key, out Entry? kept) && kept is not null && time.GetUtcNow() < kept.Expires ? (JsonObject)kept.Answer.DeepClone() : null;

    /// <summary>
    /// Keeps an answer for <see cref="Ttl"/>. It is kept as parsed text, which nothing reads: a copy of
    /// it costs nothing until its reader touches a part, and the parts it does not touch are written
    /// on as they stand.
    /// </summary>
    public void Set(string key, JsonObject answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        answers.Set(key, new Entry(JsonNode.Parse(answer.ToJsonString(Compact))!.AsObject(), time.GetUtcNow() + Ttl), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Ttl });
    }

    private static readonly JsonSerializerOptions Compact = new() { MaxDepth = OxQLJson.MaxDepth };

    /// <inheritdoc/>
    public void Dispose() => answers.Dispose();

    private sealed record Entry(JsonObject Answer, DateTimeOffset Expires);
}

/// <summary>
/// One owner an explain reached, directly or through another owner (<see cref="Via"/>): how many calls
/// it cost (one per round: the first ask, each ask-again), whether it answered, and what its answer
/// says of itself.
/// </summary>
public sealed class ExplainOwnerUse(string service, string? via)
{
    /// <summary>The owner's service key.</summary>
    public string Service { get; } = service;

    /// <summary>The owner this host reached it through, or null for one it asks itself.</summary>
    public string? Via { get; } = via;

    /// <summary>The calls it cost this explain: one per round it was asked in; none when every answer came from the cache.</summary>
    public int Calls { get; set; }

    /// <summary>Whether an answer came (kept or fresh); false when it was asked and did not answer; null when it was never asked.</summary>
    public bool? Answered { get; set; }

    /// <summary>Why it did not answer: <c>unsupported</c>, <c>unreachable</c>, <c>timeout</c> or <c>limit</c>.</summary>
    public string? Reason { get; set; }

    /// <summary>Whether every answer came from the cache.</summary>
    public bool Cached { get; set; } = true;

    /// <summary>The milliseconds its calls took together.</summary>
    public long Ms { get; set; }

    /// <summary>The schema revision its answer names.</summary>
    public string? Revision { get; set; }

    /// <summary>The engine block of its answer.</summary>
    public JsonNode? Engine { get; set; }

    internal HashSet<int> Rounds { get; } = [];
}

/// <summary>
/// One explain's calls to owners (DESIGN §4.1, §4.3): the check of the parts of the query continued at
/// them, which also answers the types of their targets, and the catalog lookups of their entities, over
/// <see cref="IRemoteQueryClient.ExplainAsync"/>, answers read through the <see cref="ExplainForwardCache"/>.
/// <para>
/// The calls are bounded so that an explain is never a way to load a service (improvement plan §3.E
/// protection): together within <c>Explain.RemoteTimeoutMs</c> and what is left of the explain's wall
/// time, at most <c>Explain.MaxOwnerServices</c> distinct services and <c>Explain.MaxOwnerCalls</c>
/// calls in all, one per service and round. An internal explain carries what is left of both
/// (<see cref="ExplainBudget"/>), so an owner nests its own owners' answers within its origin's budget
/// and never starts rounds beyond it.
/// </para>
/// <para>
/// A call that does not answer (no client, a client that cannot explain, unreachable, timed out) is
/// a <c>REMOTE_UNCHECKED</c> note; one a limit left out is an <c>EXPLAIN_LIMIT</c> note. Neither is an
/// error, and the answer is then not complete (<see cref="Complete"/>).
/// </para>
/// </summary>
public sealed class RemoteExplain : IExplainOwners
{
    private readonly IRemoteQueryClient? client;
    private readonly Func<ExplainRequest, CancellationToken, Task<JsonObject?>>? self;
    private readonly ExplainForwardCache cache;
    private readonly RequestContext context;
    private readonly ExplainTypes? types;
    private readonly bool shape;
    private readonly bool docs;
    private readonly int? depth;
    private readonly TimeSpan budget;
    private readonly bool wallBound;
    private readonly int wallMs;
    private readonly int maxServices;
    private readonly int maxCalls;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<ExplainOwnerUse> uses = [];
    private readonly List<Diagnostic> limitNotes = [];
    private int callsLeft;

    /// <summary>The calls of one explain of <paramref name="request"/> under <paramref name="context"/>.</summary>
    public RemoteExplain(IRemoteQueryClient? client, ExplainForwardCache cache, RequestContext context, ExplainRequest request)
        : this(client, cache, context, request, null)
    {
    }

    /// <summary>
    /// The calls of one explain of <paramref name="request"/> under <paramref name="context"/>;
    /// <paramref name="self"/> explains an owner query at this host itself, as its SelfOwner runs
    /// one: the check of the stages continued under a local keyed stage (null: they are noted unchecked).
    /// <paramref name="elapsed"/> is what the explain already spent of its wall time;
    /// <paramref name="types"/> takes the types of the owners' answers.
    /// </summary>
    public RemoteExplain(IRemoteQueryClient? client, ExplainForwardCache cache, RequestContext context, ExplainRequest request,
        Func<ExplainRequest, CancellationToken, Task<JsonObject?>>? self, ExplainTypes? types = null, TimeSpan elapsed = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        this.client = client;
        this.self = self;
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.types = types;
        shape = request.IncludesShape && types is not null;
        docs = request.IncludesDocs;
        depth = request.ShapeDepth;

        var options = context.Options.Explain;
        // Only an internal call carries a budget of its origin; a public one has its own.
        var given = context.Internal ? request.Budget : null;
        var remote = TimeSpan.FromMilliseconds(Math.Max(1, options.RemoteTimeoutMs));
        var wall = TimeSpan.FromMilliseconds(Math.Max(1, options.TimeoutMs)) - elapsed;

        if (given is not null && TimeSpan.FromMilliseconds(given.Ms) < wall)
            wall = TimeSpan.FromMilliseconds(given.Ms);

        wallBound = wall < remote;
        budget = wallBound ? (wall > TimeSpan.Zero ? wall : TimeSpan.Zero) : remote;
        wallMs = (int)Math.Min(int.MaxValue, budget.TotalMilliseconds);
        // A nested explain whose outer one spent the budget asks no owner at all.
        spent = budget == TimeSpan.Zero;
        maxServices = Math.Max(0, options.MaxOwnerServices);
        maxCalls = Math.Max(0, options.MaxOwnerCalls);
        callsLeft = given is null ? maxCalls : Math.Min(maxCalls, given.Calls);
    }

    /// <summary>
    /// What is left of the owners' time: an explain this one runs at this host (the check of a local
    /// keyed stage's continued stages) gets it for its own owners, so they cannot stretch the explain
    /// past its budget, while this host's own binding is still checked (RL-8).
    /// </summary>
    public TimeSpan Remaining => spent ? TimeSpan.Zero : budget - clock.Elapsed is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;

    /// <summary>The owner calls this explain may still cause.</summary>
    public int CallsLeft => callsLeft;

    /// <summary>The <c>reason</c> of a part whose owner cannot be explained at (no client, or one without internal explain).</summary>
    public const string Unsupported = "unsupported";

    /// <summary>The <c>reason</c> of a part whose owner's call failed.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The <c>reason</c> of a part the remote budget ran out before.</summary>
    public const string Timeout = "timeout";

    /// <summary>The <c>reason</c> of a part an explain limit left out (<c>EXPLAIN_LIMIT</c>).</summary>
    public const string Limit = "limit";

    /// <inheritdoc/>
    public bool Knows(string service) => client?.IsConfigured(service) == true;

    private readonly Dictionary<string, List<string>> reached = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> aliasTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> unanswered = new(StringComparer.Ordinal);

    /// <summary>
    /// After <see cref="CheckAsync"/>: per alias a continued stage adds, the entities its owners' answers
    /// say it creates (DESIGN §4.3), in the order of the checks; an alias no owner answered for is absent.
    /// A resolve continued without a target follows a reference of the owner's model, so only the owner
    /// that binds it knows what it reaches.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Reached => reached.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal);

    /// <summary>After <see cref="CheckAsync"/>: per alias a continued stage adds, the types its owners' answers point it to, in the order of the checks.</summary>
    public IReadOnlyList<string> TypesOf(string alias) => aliasTypes.TryGetValue(alias, out var list) ? list : [];

    private readonly Dictionary<int, List<JsonObject>> continuedReads = [];
    private readonly Dictionary<string, JsonObject> aliasLoads = new(StringComparer.Ordinal);

    /// <summary>
    /// After <see cref="CheckAsync"/>: what the stage at the caller's index <paramref name="stage"/>
    /// reads at the owners it is continued at, as they answered it, in the origin row's paths
    /// (<c>{ path, use, alias }</c>); null when no owner answered for the stage. The owner binds the
    /// stage, so only it knows every read: the member that picks a reference's case among them.
    /// </summary>
    public IReadOnlyList<JsonObject>? ReadsOf(int stage) => continuedReads.GetValueOrDefault(stage);

    /// <summary>
    /// After <see cref="CheckAsync"/>: what the join of a continued stage loads and shows under
    /// <paramref name="alias"/>, as its owner inferred it for the run's query (<c>{ loads, shows, hint }</c>); null when no owner answered.
    /// </summary>
    public JsonObject? LoadsOf(string alias) => aliasLoads.GetValueOrDefault(alias);

    /// <summary>After <see cref="CheckAsync"/>: whether the owner of <paramref name="target"/> of the keyed stage creating <paramref name="alias"/> did not answer its check.</summary>
    public bool Unanswered(string alias, string target) => unanswered.Contains(alias + "\n" + target);

    /// <summary>The owners this explain reached, in the order they were first asked; the ones reached through another owner after it.</summary>
    public IReadOnlyList<ExplainOwnerUse> Owners => uses;

    /// <summary>The <c>EXPLAIN_LIMIT</c> notes of the parts a limit left out.</summary>
    public IReadOnlyList<Diagnostic> LimitNotes => limitNotes;

    /// <summary>False once an owner did not answer or a limit left a part out.</summary>
    public bool Complete { get; private set; } = true;

    /// <inheritdoc/>
    public async Task<(JsonObject? Answer, string? Reason)> CatalogAsync(string service, JsonObject entry, int depth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var entity = entry["entity"]?.GetValue<string>() ?? "";
        var hash = entity.IndexOf('#', StringComparison.Ordinal);
        var request = new ExplainRequest
        {
            Query = new QueryRequest { EntityType = hash < 0 ? entity : entity[..hash], Pipeline = [] },
            Catalog = [(JsonObject)entry.DeepClone()],
            Include = docs ? [ExplainRequest.IncludeDocs] : [],
            ShapeDepth = depth,
            IsEnvelope = true,
        };

        var (answer, reason) = await CallAsync(service, request, 0, entity, null, cancellationToken).ConfigureAwait(false);

        if (answer is null)
            Complete = false;

        return (answer, reason ?? (answer is null ? Unsupported : null));
    }

    /// <summary>
    /// The remote check of a bound request (DESIGN §4.3): every keyed stage's owner queries are explained
    /// at their owners, as the run's plan builds them. An owner error at a continued stage is this
    /// request's error at the caller's stage, and an owner's own <c>REMOTE_UNCHECKED</c> notes are passed
    /// on at the first continued stage. The answers also say what the stages continued there create and
    /// describe the owners' types. What was not checked is noted.
    /// </summary>
    public async Task<(List<QueryValidationError> Errors, List<Diagnostic> Notes)> CheckAsync(BoundPipeline bound, bool strict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bound);

        var errors = new List<QueryValidationError>();
        var notes = new List<Diagnostic>();

        // A local keyed stage is checked too when stages continue under it: a run sends them to this
        // host's own SelfOwner, which binds them with its model and sends what continues further
        // (a union's every target, a third service) to those owners, whose refusals come back here.
        foreach (var resolve in bound.Stages.OfType<BoundStage.Resolve>().Where(stage => stage.IsRemote || Continuation.Of(bound, stage).Count > 0))
        {
            var checks = KeyedFetch.Checks(bound, resolve, strict, client, everyRemoteTarget: shape);
            var misses = new List<Miss>();
            var answered = new HashSet<OwnerCheck>(ReferenceEqualityComparer.Instance);

            foreach (var check in checks)
            {
                var local = check.Bound is { IsRemote: false };
                var request = new ExplainRequest
                {
                    Query = check.Query,
                    Remote = ExplainRequest.RemoteCheck,
                    Include = !shape ? [ExplainRequest.IncludeNotes]
                        : docs ? [ExplainRequest.IncludeShape, ExplainRequest.IncludeNotes, ExplainRequest.IncludeDocs]
                        : ExplainRequest.DefaultIncludes,
                    ShapeDepth = shape ? depth : null,
                    IsEnvelope = true,
                };
                var (answer, reason) = local
                    ? await SelfAsync(request, cancellationToken).ConfigureAwait(false)
                    : await CallAsync(check.Service, request, 0, check.Target, check.FirstContinued, cancellationToken).ConfigureAwait(false);

                if (answer is null)
                {
                    Complete = false;
                    unanswered.Add(resolve.As + "\n" + check.Target);

                    if (reason != Limit)
                        notes.Add(Unchecked(check, reason ?? Unsupported));

                    continue;
                }

                answered.Add(check);

                OwnerFaults.Scrubbed(answer);

                // A remote union target whose owner refused only paths the target lacks is asked again
                // without them, as a run asks it (DESIGN §3.4.1 flat paths): the owner then binds the
                // stages continued at it, and checks theirs, exactly as the run's query has them bound.
                // What it lacked stays a miss; an answer that does not come leaves the first.
                var sent = check.Query;

                for (var round = 0; !local && round < KeyedFetch.MaxDropRounds && check.Again(answer, sent) is { } again; round++)
                {
                    var (asked, _) = await CallAsync(check.Service, request with { Query = again }, round + 1, check.Target, check.FirstContinued, cancellationToken).ConfigureAwait(false);

                    if (asked is null)
                        break;

                    foreach (var error in answer["errors"]!.AsArray().OfType<JsonObject>())
                        if (MissOf(check, error) is { } miss)
                            misses.Add(miss);

                    OwnerFaults.Scrubbed(asked);
                    answer = asked;
                    sent = again;
                }

                Remember(check, answer);

                if (answer["errors"] is JsonArray owned)
                    foreach (var error in owned.OfType<JsonObject>())
                    {
                        if (check.Map(error) is { } mapped)
                        {
                            if (!errors.Any(other => other.Code == mapped.Code && other.Stage == mapped.Stage && other.Path == mapped.Path))
                                errors.Add(mapped);
                        }
                        else if (!local && MissOf(check, error) is { } miss)
                        {
                            misses.Add(miss);
                        }
                    }

                if (answer["notes"] is JsonArray ownerNotes)
                    foreach (var note in ownerNotes.OfType<JsonObject>().Where(note => note["code"]?.GetValue<string>() is Notes.RemoteUnchecked or Notes.ExplainLimit))
                    {
                        Complete = false;
                        notes.Add(new Diagnostic
                        {
                            Code = note["code"]!.GetValue<string>(),
                            Message = note["message"]?.GetValue<string>() ?? "",
                            Stage = check.FirstContinued,
                            Params = note["params"] is JsonObject parameters
                                ? parameters.ToDictionary(pair => pair.Key, pair => (object?)pair.Value?.DeepClone(), StringComparer.Ordinal)
                                : null,
                        });
                    }

                if (answer["cache"]?["complete"] is JsonValue complete && complete.TryGetValue<bool>(out var whole) && !whole)
                    Complete = false;
            }

            Judge(resolve, checks, answered, misses, errors, notes);
        }

        return (errors, notes);
    }

    /// <summary>
    /// What an owner's answer says beyond its errors: its types go into this explain's table, and for
    /// each alias of a stage continued at it, the entities and the type the owner bound it to.
    /// </summary>
    private void Remember(OwnerCheck check, JsonObject answer)
    {
        if (shape)
            types!.Import(answer);

        var aliases = answer["aliases"] as JsonObject;

        foreach (var stage in answer["stages"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (stage["index"] is not JsonValue at || !at.TryGetValue<int>(out var index) || check.OriginOf(index) is not { } origin)
                continue;

            // The reads the owner bound the stage with, in the origin row's paths.
            if (check.Resolve is { } anchor && stage["reads"] is JsonArray ownerReads)
            {
                if (!continuedReads.TryGetValue(origin.OriginIndex, out var known))
                    continuedReads[origin.OriginIndex] = known = [];

                foreach (var read in ownerReads.OfType<JsonObject>())
                {
                    if (read["path"]?.GetValue<string>() is not { Length: > 0 } ownerPath || read["use"]?.GetValue<string>() is not { } use)
                        continue;

                    var path = Continuation.FromOwner(ownerPath, anchor, check.Item, check.ContinuedAliases);

                    if (!known.Any(other => other["path"]!.GetValue<string>() == path && other["use"]!.GetValue<string>() == use))
                        known.Add(new JsonObject { ["path"] = path, ["use"] = use, ["alias"] = path.Split('.')[0] });
                }
            }

            foreach (var created in stage["creates"]?.AsArray().OfType<JsonValue>() ?? [])
            {
                if (!created.TryGetValue<string>(out var alias) || !origin.Aliases.Contains(alias, StringComparer.Ordinal) || aliases?[alias] is not JsonObject described)
                    continue;

                if (!aliasLoads.ContainsKey(alias) && described.ContainsKey("loads"))
                    aliasLoads[alias] = new JsonObject { ["loads"] = described["loads"]?.DeepClone(), ["shows"] = described["shows"]?.DeepClone(), ["hint"] = described["hint"]?.DeepClone() };

                if (!reached.TryGetValue(alias, out var list))
                    reached[alias] = list = [];

                foreach (var entity in described["entities"]?.AsArray().OfType<JsonValue>().Select(value => value.ToString()) ?? [])
                    if (!list.Contains(entity, StringComparer.Ordinal))
                        list.Add(entity);

                if (described["type"] is JsonValue pointer && pointer.TryGetValue<string>(out var type))
                {
                    if (!aliasTypes.TryGetValue(alias, out var pointers))
                        aliasTypes[alias] = pointers = [];

                    if (!pointers.Contains(type, StringComparer.Ordinal))
                        pointers.Add(type);
                }
            }
        }
    }

    /// <summary>A path a remote target's owner said the target lacks, at the check query's projection: relative to the alias, or to the owning row.</summary>
    private sealed record Miss(OwnerCheck Check, string Path, bool Parent, string OwnerPath);

    /// <summary>An owner's <c>UNKNOWN_PATH</c> at the check query's projection as a path of the alias or of the owning row; null for any other error.</summary>
    private static Miss? MissOf(OwnerCheck check, JsonObject error)
    {
        if (error["code"]?.ToString() != Codes.UnknownPath || error["stage"] is not JsonValue at || !at.TryGetValue<int>(out var stage) || stage != check.ProjectAt
            || error["path"]?.ToString() is not { Length: > 0 } path)
            return null;

        var element = BoundKeyedBy.Element + ".";

        if (!check.Item)
            return new Miss(check, path, Parent: false, path);

        return path.StartsWith(element, StringComparison.Ordinal)
            ? new Miss(check, path[element.Length..], Parent: false, path)
            : new Miss(check, path, Parent: true, path);
    }

    /// <summary>
    /// What the owners' misses mean for one resolve (DESIGN §3.4.1 flat paths, §4.3): a path some
    /// target has is dropped for those that lack it (<c>SELECT_PATH_NOT_ON_TARGET</c>, as a run drops
    /// it); a path no target has is this request's <c>UNKNOWN_PATH</c> where the caller wrote it (the
    /// resolve's select hint, or the projection after it), with <c>params.owner</c> where the owner saw it.
    /// A local target has what its entity (or item) has; a remote target nobody asked has every path.
    /// A path under an alias a continued stage added is that stage's join's to have: its owner binds
    /// it, and one it lacks is <c>UNKNOWN_PATH</c> at the projection, as a run is refused for it.
    /// </summary>
    private static void Judge(BoundStage.Resolve resolve, IReadOnlyList<OwnerCheck> checks, HashSet<OwnerCheck> answered, List<Miss> misses, List<QueryValidationError> errors, List<Diagnostic> notes)
    {
        var targets = (resolve.Cases is { Count: > 0 } cases ? cases.SelectMany(selected => selected.Targets) : [])
            .DistinctBy(target => (target.Declared.Entity, target.Declared.Field, target.Declared.Item))
            .ToList();

        foreach (var group in misses.GroupBy(miss => (miss.Path, miss.Parent)))
        {
            var (path, parent) = group.Key;
            var lacking = group.Select(miss => miss.Check).ToList();
            var first = group.First();
            // The hint is written at the resolve; every other path asked of the owner is the projection's.
            var written = parent ? null : resolve.Hint;
            var stage = written?.Contains(path, StringComparer.Ordinal) == true ? first.Check.Stage : first.Check.ProjectStage ?? first.Check.Stage;
            var alias = parent ? resolve.ParentAs ?? resolve.As : resolve.As;

            if (!parent && first.Check.ContinuedAliases.Contains(path.Split('.')[0], StringComparer.Ordinal))
            {
                var continued = path.Split('.')[0];
                var at = first.Check.ProjectStage ?? first.Check.Stage;

                if (!errors.Any(other => other.Code == Codes.UnknownPath && other.Stage == at && other.Path == path))
                    errors.Add(new QueryValidationError
                    {
                        Code = Codes.UnknownPath,
                        Message = $"'{path}' is not a path of what '{continued}' joins ({string.Join(", ", lacking.Select(check => check.Target).Distinct(StringComparer.Ordinal))}).",
                        Stage = at,
                        Path = path,
                        Params = new Dictionary<string, object?>
                        {
                            ["reason"] = PathReasons.NoTarget,
                            ["alias"] = continued,
                            ["targets"] = lacking.Select(check => check.Target).Distinct(StringComparer.Ordinal).ToList(),
                            ["owner"] = new Dictionary<string, object?>
                            {
                                ["service"] = first.Check.Service,
                                ["entity"] = first.Check.Bound?.Declared.Entity,
                                ["target"] = first.Check.Target,
                                ["stage"] = first.Check.ProjectAt,
                                ["path"] = first.OwnerPath,
                            },
                        },
                    });

                continue;
            }

            bool Has(BoundResolveTarget target)
            {
                if (target.IsRemote)
                {
                    var check = checks.FirstOrDefault(each => each.Bound is { } other
                        && other.Declared.Entity == target.Declared.Entity && other.Declared.Item == target.Declared.Item && other.Declared.Field == target.Declared.Field);

                    return check is null || !answered.Contains(check) || !lacking.Contains(check);
                }

                if (target.Entity is not { } entity)
                    return false;

                var at = Shape.ForEntity(entity);

                if (!parent && target.Declared.Item is { } item && at.Resolve(item, PathUsage.Unwind) is { Succeeded: true } collection)
                    at = at.ForElement(collection.Path!);

                return at.Resolve(path, PathUsage.Project).Succeeded;
            }

            // What a target lacks is said at the resolve, as a run says it and as a local target's is.
            if (targets.Count > 1 && targets.Any(Has))
            {
                foreach (var check in lacking.DistinctBy(check => check.Target))
                    notes.Add(Notes.SelectPathDropped(first.Check.Stage, resolve.As, check.Target, path, parent));

                continue;
            }

            if (errors.Any(other => other.Code == Codes.UnknownPath && other.Stage == stage && other.Path == alias + "." + path))
                continue;

            errors.Add(new QueryValidationError
            {
                Code = Codes.UnknownPath,
                Message = $"'{alias}.{path}' is not a path of {(targets.Count > 1 ? "any target" : "the target")} of '{resolve.As}' ({string.Join(", ", lacking.Select(check => check.Target).Distinct(StringComparer.Ordinal))}).",
                Stage = stage,
                Path = alias + "." + path,
                Params = new Dictionary<string, object?>
                {
                    ["reason"] = PathReasons.NoTarget,
                    ["alias"] = alias,
                    ["targets"] = lacking.Select(check => check.Target).Distinct(StringComparer.Ordinal).ToList(),
                    ["owner"] = new Dictionary<string, object?>
                    {
                        ["service"] = first.Check.Service,
                        ["entity"] = first.Check.Bound?.Declared.Entity,
                        ["target"] = first.Check.Target,
                        ["stage"] = first.Check.ProjectAt,
                        ["path"] = first.OwnerPath,
                    },
                },
            });
        }
    }

    /// <summary>
    /// An owner query explained at this host itself, as its SelfOwner would run it: nothing is
    /// forwarded or cached, and what the explain asks further owners is spent of this explain's
    /// budget (time and calls). Without a way to explain here, the part is noted unchecked.
    /// </summary>
    private async Task<(JsonObject? Answer, string? Reason)> SelfAsync(ExplainRequest request, CancellationToken cancellationToken)
    {
        if (self is null)
            return (null, Unsupported);

        var answer = await self(request with { Budget = new ExplainBudget((int)Math.Min(int.MaxValue, Remaining.TotalMilliseconds), callsLeft) }, cancellationToken).ConfigureAwait(false);

        if (answer is null)
            return (null, Unsupported);

        // The owners this host's own explain asked are this explain's: their calls are spent here.
        foreach (var nested in answer["owners"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (nested["remote"]?.GetValue<bool>() != true || nested["service"]?.GetValue<string>() is not { } service)
                continue;

            Nest(nested, service, nested["via"]?.GetValue<string>());
        }

        return (answer, null);
    }

    /// <summary>
    /// One forwarded body in round <paramref name="round"/> of its service: from the cache, else from
    /// the owner within what is left of the time and the calls.
    /// </summary>
    private async Task<(JsonObject? Answer, string? Reason)> CallAsync(string service, ExplainRequest request, int round, string target, int? stage, CancellationToken cancellationToken)
    {
        if (client is null)
            return (null, Unsupported);

        var use = uses.FirstOrDefault(each => each.Service == service && each.Via is null);
        var key = ExplainForwardCache.KeyOf(context.Organisation ?? Guid.Empty, context.UserId, service, request);

        if (cache.Get(key) is { } kept)
        {
            use ??= Use(service, null);
            use.Answered = true;
            ReadFacts(use, kept);

            // What the kept answer depended on is depended on still; nothing was called for it now.
            foreach (var nested in kept["owners"]?.AsArray().OfType<JsonObject>() ?? [])
                if (nested["remote"]?.GetValue<bool>() == true && nested["service"]?.GetValue<string>() is { } reachedService)
                    Nest(nested, reachedService, nested["via"]?.GetValue<string>() is { } further ? service + ">" + further : service, called: false);

            return (kept, null);
        }

        var remaining = budget - clock.Elapsed;

        // A call cut by the budget spent it, whatever the clock reads a moment later (the timer may
        // fire a hair before the elapsed time reaches the budget).
        if (spent || remaining <= TimeSpan.Zero)
            return OutOfTime(use ?? Use(service, null), service, target, stage);

        // One call per service and round: the checks of one round ride together once owners take
        // them batched, so the round is what is counted, and what a limit leaves out.
        if (use is null || !use.Rounds.Contains(round))
        {
            if (use is null && uses.Count(each => each.Via is null) >= maxServices)
                return Limited("ownerServices", maxServices, service, target, stage);

            if (callsLeft <= 0)
                return Limited("ownerCalls", maxCalls, service, target, stage);

            use ??= Use(service, null);
            use.Rounds.Add(round);
            use.Calls++;
            callsLeft--;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(remaining);

        var started = clock.Elapsed;

        try
        {
            var sent = request with { Budget = new ExplainBudget((int)Math.Min(int.MaxValue, remaining.TotalMilliseconds), callsLeft) };
            var answer = await client.ExplainAsync(service, sent, remaining, timeout.Token).ConfigureAwait(false);

            use.Ms += (long)(clock.Elapsed - started).TotalMilliseconds;

            if (answer is null)
            {
                use.Answered ??= false;
                use.Reason = Unsupported;

                return (null, Unsupported);
            }

            cache.Set(key, answer);
            use.Answered = true;
            use.Cached = false;
            ReadFacts(use, answer);

            // What the owner asked its own owners is spent of this explain's calls.
            foreach (var nested in answer["owners"]?.AsArray().OfType<JsonObject>() ?? [])
                if (nested["remote"]?.GetValue<bool>() == true && nested["service"]?.GetValue<string>() is { } reachedService)
                    Nest(nested, reachedService, nested["via"]?.GetValue<string>() is { } further ? service + ">" + further : service);

            return (answer, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            use.Ms += (long)(clock.Elapsed - started).TotalMilliseconds;
            spent = true;

            return OutOfTime(use, service, target, stage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            use.Ms += (long)(clock.Elapsed - started).TotalMilliseconds;
            use.Answered ??= false;
            use.Reason = Unreachable;

            return (null, Unreachable);
        }
    }

    private ExplainOwnerUse Use(string service, string? via)
    {
        var use = new ExplainOwnerUse(service, via);

        uses.Add(use);

        return use;
    }

    /// <summary>An owner another owner's answer names: its calls are counted here, and it is listed as reached through that owner.</summary>
    private void Nest(JsonObject nested, string service, string? via, bool called = true)
    {
        var use = uses.FirstOrDefault(each => each.Service == service && each.Via == via) ?? Use(service, via);
        var calls = called && nested["calls"] is JsonValue count && count.TryGetValue<int>(out var number) ? number : 0;

        use.Calls += calls;
        callsLeft = Math.Max(0, callsLeft - calls);

        // An owner asked twice answered when one of the asks was answered; the first reason stays.
        if (nested["answered"] is JsonValue answered && answered.TryGetValue<bool>(out var flag))
            use.Answered = flag || use.Answered == true;

        use.Reason ??= nested["reason"]?.GetValue<string>();
        use.Cached &= calls == 0;
        use.Revision = nested["revision"]?.GetValue<string>() ?? use.Revision;
        use.Engine = nested["engine"]?.DeepClone() ?? use.Engine;
    }

    private static void ReadFacts(ExplainOwnerUse use, JsonObject answer)
    {
        use.Revision = answer["revision"]?["schema"]?.GetValue<string>() ?? use.Revision;

        if (answer["engine"] is JsonObject engine)
            use.Engine = new JsonObject { ["version"] = engine["version"]?.DeepClone(), ["contract"] = engine["contract"]?.DeepClone() };
    }

    /// <summary>A part the time ran out before: the explain's own wall time is a limit, the owners' budget an unchecked part.</summary>
    private (JsonObject? Answer, string? Reason) OutOfTime(ExplainOwnerUse use, string service, string target, int? stage)
    {
        use.Answered ??= false;

        if (!wallBound)
        {
            use.Reason = Timeout;

            return (null, Timeout);
        }

        use.Reason ??= Limit;

        return Limited("time", wallMs, service, target, stage);
    }

    /// <summary>A part a limit left out: noted once per limit and service, never retried.</summary>
    private (JsonObject? Answer, string? Reason) Limited(string limit, int max, string service, string target, int? stage)
    {
        Complete = false;

        if (!limitNotes.Any(note => Equals(note.Params?["limit"], limit) && Equals(note.Params?["service"], service)))
            limitNotes.Add(new Diagnostic
            {
                Code = Notes.ExplainLimit,
                Message = limit switch
                {
                    "ownerServices" => $"This explain asks at most {max} owner services; what '{service}' binds for '{target}' was not checked.",
                    "ownerCalls" => $"This explain causes at most {max} owner calls in all; what '{service}' binds for '{target}' was not checked.",
                    _ => $"This explain had {max} ms left of the time an explain may take; what '{service}' binds for '{target}' was not checked.",
                },
                Stage = stage,
                Params = new Dictionary<string, object?> { ["limit"] = limit, ["max"] = max, ["service"] = service, ["target"] = target },
            });

        return (null, Limit);
    }

    /// <summary>Whether a call already ran out of the shared budget.</summary>
    private bool spent;

    private static Diagnostic Unchecked(OwnerCheck check, string reason) => new()
    {
        Code = Notes.RemoteUnchecked,
        Message = check.Service.Length == 0
            ? $"The stages continued under '{check.Target}' at this host were not checked ({reason}); this host binds them when the query runs."
            : $"What '{check.Service}' binds for '{check.Target}' (its select, the paths under its alias, the stages continued there) could not be checked at its owner ({reason}); the owner binds it when the query runs.",
        Stage = check.FirstContinued,
        Params = new Dictionary<string, object?> { ["service"] = check.Service.Length == 0 ? null : check.Service, ["target"] = check.Target, ["reason"] = reason },
    };
}
