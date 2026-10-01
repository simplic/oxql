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
/// user, owner service, the revision of that owner's schema document and the hash of the forwarded body
/// with its variables substituted, so the studio's debounced explains of one query do not ask the owner
/// again and no user is answered from another's call (an owner may refuse one user what it answers another).
/// <para>
/// The revision is the one the owner's last answer named (<see cref="RevisionOf"/>): the origin learns it
/// from answers only. An answer that names another revision of a service retires every answer kept for
/// the old one at once, also those of other owners that reached that service (an answer is kept with the
/// revision of every service it depended on, and is not handed out once one of them is known to have
/// changed). Without a fresh answer the 30 seconds bound how long a changed owner model is answered from before.
/// </para>
/// <para>
/// Only answers are kept, never a failed call, and never as more than they were: an answer that was not
/// complete stays not complete. The cache also remembers through which owner a service this host does not
/// know itself was reached (<see cref="RouteOf"/>), so a catalog lookup of such an entity can be routed.
/// Nothing an explain answers depends on what is kept: with an empty cache the same answer is asked for again.
/// </para>
/// </summary>
public sealed class ExplainForwardCache : IDisposable
{
    /// <summary>How long an owner's answer is kept.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>The most answers kept.</summary>
    public const int MaxEntries = 1_000;

    private readonly MemoryCache answers;
    private readonly TimeProvider time;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> revisions = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> routes = new(StringComparer.Ordinal);

    /// <summary>An empty cache; <paramref name="time"/> is the clock answers expire by.</summary>
    public ExplainForwardCache(TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        answers = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxEntries });
    }

    /// <summary>The key of one forwarded body, for no user in particular and no revision.</summary>
    public static string KeyOf(Guid organisation, string service, ExplainRequest request) => KeyOf(organisation, null, service, request);

    /// <summary>
    /// The key of one forwarded body for one user of an organisation, before the owner's revision. The
    /// budget an internal explain carries is not part of it: what is left of an origin's explain changes
    /// nothing an owner answers. Neither is the tier (<c>remote</c>): the cached tier reads what a check kept.
    /// </summary>
    public static string KeyOf(Guid organisation, string? user, string service, ExplainRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = JsonSerializer.SerializeToUtf8Bytes(request with { Budget = null, Remote = ExplainRequest.RemoteCheck }, OxQLJson.Wire);
        var who = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user ?? "")))[..16];

        return $"{organisation:N}|{who}|{service}|{Convert.ToHexString(SHA256.HashData(body))}";
    }

    /// <summary>The revision of <paramref name="service"/>'s schema document as its last answer named it, or null while none did.</summary>
    public string? RevisionOf(string service) => revisions.GetValueOrDefault(service);

    /// <summary>The owner this host reached <paramref name="service"/> through, or null when it never was reached through another.</summary>
    public string? RouteOf(string service) => routes.GetValueOrDefault(service);

    /// <summary>Remembers that <paramref name="service"/> was reached through the owner <paramref name="via"/>.</summary>
    public void Route(string service, string via)
    {
        if (service != via)
            routes[service] = via;
    }

    /// <summary>A kept answer of <paramref name="service"/> for the key, as a copy of its own, or null.</summary>
    public JsonObject? Get(string service, string key)
    {
        if (!answers.TryGetValue(Keyed(service, key), out Entry? kept) || kept is null || time.GetUtcNow() >= kept.Expires)
            return null;

        // An answer that depended on a revision a later answer replaced is no answer any more.
        foreach (var (reached, revision) in kept.Revisions)
            if (revisions.TryGetValue(reached, out var known) && known != revision)
                return null;

        return (JsonObject)kept.Answer.DeepClone();
    }

    /// <summary>A kept answer by its key alone (no revision), or null.</summary>
    public JsonObject? Get(string key) => Get(ServiceOf(key), key);

    /// <summary>
    /// Keeps an answer of <paramref name="service"/> for <see cref="Ttl"/>, under the revisions it names.
    /// It is kept as parsed text, which nothing reads: a copy of it costs nothing until its reader touches
    /// a part, and the parts it does not touch are written on as they stand.
    /// </summary>
    public void Set(string service, string key, JsonObject answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var named = new Dictionary<string, string>(StringComparer.Ordinal);

        if (answer["revision"]?["schema"] is JsonObject schema)
            foreach (var (reached, value) in schema)
                if (value is JsonValue stated && stated.TryGetValue<string>(out var revision))
                    named[reached] = revisions[reached] = revision;

        answers.Set(Keyed(service, key), new Entry(JsonNode.Parse(answer.ToJsonString(Compact))!.AsObject(), time.GetUtcNow() + Ttl, named), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Ttl });
    }

    /// <summary>Keeps an answer by its key alone.</summary>
    public void Set(string key, JsonObject answer) => Set(ServiceOf(key), key, answer);

    /// <summary>The key with the revision of the owner's schema document as last named: an answer of another revision is another entry.</summary>
    private string Keyed(string service, string key) => key + "|" + (RevisionOf(service) ?? "");

    private static string ServiceOf(string key) => key.Split('|') is { Length: >= 3 } parts ? parts[2] : "";

    private static readonly JsonSerializerOptions Compact = new() { MaxDepth = OxQLJson.MaxDepth };

    /// <inheritdoc/>
    public void Dispose() => answers.Dispose();

    private sealed record Entry(JsonObject Answer, DateTimeOffset Expires, IReadOnlyDictionary<string, string> Revisions);
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
    private readonly OwnerFetchCache? drops;
    private readonly bool cachedOnly;
    private readonly int maxChecks;
    private readonly RequestContext context;
    private readonly ExplainTypes? types;
    private readonly bool shape;
    private readonly bool tables;
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
    private readonly ExplainOwnerPool pool;

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
        Func<ExplainRequest, CancellationToken, Task<JsonObject?>>? self, ExplainTypes? types = null, TimeSpan elapsed = default, OwnerFetchCache? drops = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        this.client = client;
        this.self = self;
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.types = types;
        this.drops = drops;
        cachedOnly = request.Remote == ExplainRequest.RemoteCached;
        maxChecks = Math.Max(1, context.Options.Explain.MaxBatchChecks);
        shape = request.IncludesShape && types is not null;
        tables = shape && request.IncludesTypes;
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
        // The explains that belong together share what is left of the calls; an explain alone has its own.
        pool = context.ExplainOwners as ExplainOwnerPool ?? new ExplainOwnerPool(1, given is null ? maxCalls : Math.Min(maxCalls, given.Calls));
    }

    /// <summary>
    /// What is left of the owners' time: an explain this one runs at this host (the check of a local
    /// keyed stage's continued stages) gets it for its own owners, so they cannot stretch the explain
    /// past its budget, while this host's own binding is still checked (RL-8).
    /// </summary>
    public TimeSpan Remaining => spent ? TimeSpan.Zero : budget - clock.Elapsed is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;

    /// <summary>The owner calls this explain may still cause.</summary>
    public int CallsLeft => pool.CallsLeft;

    /// <summary>The pool this explain's owner calls go out through: an explain this one runs at this host shares it.</summary>
    public ExplainOwnerPool Pool => pool;

    /// <summary>The <c>reason</c> of a part whose owner cannot be explained at (no client, or one without internal explain).</summary>
    public const string Unsupported = "unsupported";

    /// <summary>The <c>reason</c> of a part whose owner's call failed.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The <c>reason</c> of a part the remote budget ran out before.</summary>
    public const string Timeout = "timeout";

    /// <summary>The <c>reason</c> of a part an explain limit left out (<c>EXPLAIN_LIMIT</c>).</summary>
    public const string Limit = "limit";

    /// <summary>The <c>reason</c> of a part the cached tier (<c>remote: "cached"</c>) holds no kept owner answer for: no owner was asked.</summary>
    public const string Cached = "cached";

    /// <inheritdoc/>
    public bool Knows(string service) => Reaching(service) is not null;

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

    private readonly Dictionary<(string Alias, string Target), (List<string> Entities, List<string> Types)> branchAnswers = [];
    private readonly HashSet<(int Stage, string Target)> refusedAt = [];
    private readonly Dictionary<string, JsonArray> ownerBranches = new(StringComparer.Ordinal);

    /// <summary>
    /// After <see cref="CheckAsync"/>: what the branch of a union join (<c>byTarget</c>) for the anchor's
    /// target <paramref name="target"/> reaches under <paramref name="alias"/>, as that target's owner
    /// answered: the entities and the types; null when no owner answered for the branch.
    /// </summary>
    public (IReadOnlyList<string> Entities, IReadOnlyList<string> Types)? BranchOf(string alias, string target) =>
        branchAnswers.TryGetValue((alias, target), out var answered) ? (answered.Entities, answered.Types) : null;

    /// <summary>After <see cref="CheckAsync"/>: whether the owner of the anchor's <paramref name="target"/> refused the stage at the caller's index <paramref name="stage"/>.</summary>
    public bool RefusedAt(int stage, string target) => refusedAt.Contains((stage, target));

    /// <summary>
    /// After <see cref="CheckAsync"/>: the branches of a union join an owner split itself (one under an
    /// alias a continued stage added), as that owner answered them; null when none did.
    /// </summary>
    public JsonArray? OwnerBranchesOf(string alias) => ownerBranches.GetValueOrDefault(alias);

    /// <summary>After <see cref="CheckAsync"/>: whether the owner of <paramref name="target"/> of the keyed stage creating <paramref name="alias"/> did not answer its check.</summary>
    public bool Unanswered(string alias, string target) => unanswered.Contains(alias + "\n" + target);

    /// <summary>The owners this explain reached, in the order they were first asked; the ones reached through another owner after it.</summary>
    public IReadOnlyList<ExplainOwnerUse> Owners => uses;

    private readonly Dictionary<string, string?> revisions = new(StringComparer.Ordinal);

    /// <summary>
    /// The revision of each service's schema document as the answers named it (<c>revision.schema</c>): an
    /// owner's own and those of the owners it asked. Null for a service that publishes none.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Revisions => revisions;

    /// <summary>The <c>EXPLAIN_LIMIT</c> notes of the parts a limit left out.</summary>
    public IReadOnlyList<Diagnostic> LimitNotes => limitNotes;

    /// <summary>False once an owner did not answer or a limit left a part out.</summary>
    public bool Complete { get; private set; } = true;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(JsonObject? Answer, string? Reason)>> CatalogAsync(IReadOnlyList<(string Service, JsonObject Entry, int Depth)> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var asks = entries.Select(entry =>
        {
            var entity = entry.Entry["entity"]?.GetValue<string>() ?? "";
            var hash = entity.IndexOf('#', StringComparison.Ordinal);
            var owner = Reaching(entry.Service) ?? entry.Service;

            return new Ask(owner, new ExplainRequest
            {
                Query = new QueryRequest { EntityType = hash < 0 ? entity : entity[..hash], Pipeline = [] },
                Remote = cachedOnly ? ExplainRequest.RemoteCached : ExplainRequest.RemoteCheck,
                Catalog = [(JsonObject)entry.Entry.DeepClone()],
                Include = [.. tables ? [ExplainRequest.IncludeTypes] : Array.Empty<string>(), .. docs ? [ExplainRequest.IncludeDocs] : Array.Empty<string>()],
                ShapeDepth = entry.Depth,
                IsEnvelope = true,
                Slim = true,
            }, entity, null, owner != entry.Service);
        }).ToList();

        // Every lookup of one explain rides in the one call its owner is asked in.
        var answers = await RoundAsync(asks, CatalogRound, cancellationToken).ConfigureAwait(false);

        return answers.Select(answer =>
        {
            if (answer.Answer is null)
                Complete = false;

            return (answer.Answer, answer.Reason ?? (answer.Answer is null ? Unsupported : null));
        }).ToList();
    }

    /// <summary>The round the catalog lookups of an explain are asked in: one of their own, beside the checks'.</summary>
    private const int CatalogRound = -1;

    /// <summary>
    /// The owner this host asks for <paramref name="service"/>: the service itself when the host knows it,
    /// else the owner that reached it, in this explain or an earlier one (a transitive entity is answered
    /// by the owner that knows its service); null when there is no way to it.
    /// </summary>
    private string? Reaching(string service)
    {
        if (client is null)
            return null;

        if (client.IsConfigured(service))
            return service;

        var via = uses.FirstOrDefault(use => use.Service == service && use.Via is not null)?.Via?.Split('>')[0] ?? cache.RouteOf(service);

        return via is not null && client.IsConfigured(via) ? via : null;
    }

    /// <summary>One owner query of a keyed stage on its way through the rounds: what was sent last, and what came back for it.</summary>
    private sealed class Asked(BoundStage.Resolve resolve, OwnerCheck check)
    {
        public BoundStage.Resolve Resolve { get; } = resolve;

        public OwnerCheck Check { get; } = check;

        public bool Local { get; } = check.Bound is { IsRemote: false };

        /// <summary>The query the answer held is the answer to.</summary>
        public QueryRequest Sent { get; set; } = check.Query;

        public JsonObject? Answer { get; set; }

        public string? Reason { get; set; }

        /// <summary>The query a round asks again with, once the paths the target lacks left it.</summary>
        public QueryRequest? Again { get; set; }

        /// <summary>Whether an ask-again went unanswered: the answer before it stands, and nothing more is asked.</summary>
        public bool Stopped { get; set; }

        /// <summary>The paths earlier answers refused and a later query no longer asks.</summary>
        public List<Miss> Misses { get; } = [];
    }

    /// <summary>
    /// The remote check of a bound request (DESIGN §4.3): every keyed stage's owner queries are explained
    /// at their owners, as the run's plan builds them. An owner error at a continued stage is this
    /// request's error at the caller's stage, and an owner's own <c>REMOTE_UNCHECKED</c> notes are passed
    /// on at the first continued stage. The answers also say what the stages continued there create and
    /// describe the owners' types. What was not checked is noted.
    /// <para>
    /// The checks go out in rounds (improvement plan §3.E): the first carries every owner query of every
    /// keyed stage, one call per owner service, the services asked at once. A further round asks again
    /// what a run asks again (<see cref="KeyedFetch.MaxDropRounds"/>): a target whose owner refused only
    /// paths it lacks, without them. What an earlier request learned a target lacks is dropped before the
    /// first round, as a run drops it, so a warm explain needs one round.
    /// </para>
    /// </summary>
    public async Task<(List<QueryValidationError> Errors, List<Diagnostic> Notes)> CheckAsync(BoundPipeline bound, bool strict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bound);

        var errors = new List<QueryValidationError>();
        var notes = new List<Diagnostic>();

        // A local keyed stage is checked too when stages continue under it: a run sends them to this
        // host's own SelfOwner, which binds them with its model and sends what continues further
        // (a union's every target, a third service) to those owners, whose refusals come back here.
        var stages = bound.Stages.OfType<BoundStage.Resolve>().Where(stage => stage.IsRemote || Continuation.Of(bound, stage).Count > 0)
            .Select(resolve => (Resolve: resolve, Checks: KeyedFetch.Checks(bound, resolve, strict, client, everyRemoteTarget: shape, drops)))
            .ToList();
        var asked = stages.SelectMany(stage => stage.Checks.Select(check => new Asked(stage.Resolve, check))).ToList();

        var first = await RoundAsync(asked.Select(each => AskOf(each, each.Sent)).ToList(), 0, cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < asked.Count; index++)
        {
            (asked[index].Answer, asked[index].Reason) = first[index];

            if (asked[index].Answer is { } answer)
                OwnerFaults.Scrubbed(answer);
        }

        // A remote union target whose owner refused only paths the target lacks is asked again
        // without them, as a run asks it (DESIGN §3.4.1 flat paths): the owner then binds the
        // stages continued at it, and checks theirs, exactly as the run's query has them bound.
        // What it lacked stays a miss; an answer that does not come leaves the first. A local target
        // is asked again as well: what a branch of a union join does not reach is its SelfOwner's to
        // say, and a run drops it the same way.
        for (var round = 1; round <= KeyedFetch.MaxDropRounds; round++)
        {
            var again = new List<Asked>();

            foreach (var each in asked)
            {
                each.Again = each is { Stopped: false, Answer: { } answer } ? each.Check.Again(answer, each.Sent) : null;

                if (each.Again is not null)
                    again.Add(each);
            }

            if (again.Count == 0)
                break;

            var answers = await RoundAsync(again.Select(each => AskOf(each, each.Again!)).ToList(), round, cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < again.Count; index++)
            {
                var each = again[index];

                // An ask-again that is not answered leaves the answer before it.
                if (answers[index].Answer is not { } next)
                {
                    each.Stopped = true;
                    continue;
                }

                foreach (var error in each.Answer!["errors"]!.AsArray().OfType<JsonObject>())
                    if (MissOf(each.Check, error) is { } miss)
                        each.Misses.Add(miss);

                OwnerFaults.Scrubbed(next);
                each.Answer = next;
                each.Sent = each.Again!;
            }
        }

        foreach (var (resolve, checks) in stages)
        {
            var misses = new List<Miss>();
            var answered = new HashSet<OwnerCheck>(ReferenceEqualityComparer.Instance);

            foreach (var each in asked.Where(each => ReferenceEquals(each.Resolve, resolve)))
            {
                var check = each.Check;

                // What an earlier request learned this target lacks was not asked at all: a miss all the same.
                // The misses of one target are said in the order its query asks the paths, so the answer
                // does not depend on which of them was learned before and which an owner refused now.
                var own = check.Learned.Select(learned => new Miss(check, learned.Path, learned.Parent, learned.Path)).ToList();

                void Said() => misses.AddRange(own.OrderBy(miss => check.Rank(miss.Path, miss.Parent)));

                if (each.Answer is not { } answer)
                {
                    Complete = false;
                    unanswered.Add(resolve.As + "\n" + check.Target);

                    if (each.Reason != Limit)
                        notes.Add(Unchecked(check, each.Reason ?? Unsupported));

                    Said();
                    continue;
                }

                answered.Add(check);
                own.AddRange(each.Misses);

                Remember(check, answer);

                if (answer["errors"] is JsonArray owned)
                    foreach (var error in owned.OfType<JsonObject>())
                    {
                        if (check.Map(error) is { } mapped)
                        {
                            if (mapped.Stage is { } at && check.Bound is { } refusing)
                                refusedAt.Add((at, refusing.Declared.Entity));

                            if (!errors.Any(other => other.Code == mapped.Code && other.Stage == mapped.Stage && other.Path == mapped.Path))
                                errors.Add(mapped);
                        }
                        else if (MissOf(check, error) is { } miss && (!each.Local || miss.Continued))
                        {
                            own.Add(miss);
                        }
                    }

                Said();

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

            var before = errors.Count;

            Judge(resolve, checks, answered, misses, errors, notes);

            // What the owners said a target lacks is kept as a run keeps it, once it is known to be a
            // drop and not a refusal: the next explain, and the next run, ask without it.
            if (errors.Count == before && drops is not null)
                foreach (var check in checks.Where(answered.Contains))
                    check.Keep(drops);
        }

        return (errors, notes);
    }

    /// <summary>The explain request one owner query travels in: the run's query, asked for what this explain was asked for.</summary>
    private Ask AskOf(Asked asked, QueryRequest query) => new(asked.Local ? "" : asked.Check.Service, new ExplainRequest
    {
        Query = query,
        Remote = cachedOnly ? ExplainRequest.RemoteCached : ExplainRequest.RemoteCheck,
        // The owner writes its member rows out only for an origin that was asked to.
        Include = !shape ? [ExplainRequest.IncludeNotes]
            : !tables && !docs ? ExplainRequest.DefaultIncludes
            : [ExplainRequest.IncludeShape, ExplainRequest.IncludeNotes, .. tables ? [ExplainRequest.IncludeTypes] : Array.Empty<string>(), .. docs ? [ExplainRequest.IncludeDocs] : Array.Empty<string>()],
        ShapeDepth = shape ? depth : null,
        IsEnvelope = true,
        Slim = true,
    }, asked.Check.Target, asked.Check.FirstContinued, asked.Check.Continued.Count > 0);

    /// <summary>
    /// What an owner's answer says beyond its errors: its types go into this explain's table, and for
    /// each alias of a stage continued at it, the entities and the type the owner bound it to.
    /// </summary>
    private void Remember(OwnerCheck check, JsonObject answer)
    {
        // What each union of the answer is made of, read before the table takes the answer's types: two
        // owners may each name a union after the one alias, and the alias here is the union of both.
        var unions = (answer["types"] as JsonObject)?
            .Where(pair => pair.Key.StartsWith("u:", StringComparison.Ordinal) && pair.Value?["of"] is JsonArray)
            .ToDictionary(pair => pair.Key, pair => pair.Value!["of"]!.AsArray().Select(each => each!.GetValue<string>()).ToList(), StringComparer.Ordinal) ?? [];

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

                // What this target's owner said the alias reaches: of a union join, what its branch reaches.
                var branch = (alias, check.Bound?.Declared.Entity ?? check.Target);

                if (!branchAnswers.TryGetValue(branch, out var reachedHere))
                    branchAnswers[branch] = reachedHere = ([], []);

                foreach (var entity in described["entities"]?.AsArray().OfType<JsonValue>().Select(value => value.ToString()) ?? [])
                {
                    if (!list.Contains(entity, StringComparer.Ordinal))
                        list.Add(entity);

                    if (!reachedHere.Entities.Contains(entity, StringComparer.Ordinal))
                        reachedHere.Entities.Add(entity);
                }

                if (described["type"] is JsonValue pointer && pointer.TryGetValue<string>(out var named))
                {
                    if (!aliasTypes.TryGetValue(alias, out var pointers))
                        aliasTypes[alias] = pointers = [];

                    foreach (var type in unions.TryGetValue(named, out var of) ? of : [named])
                    {
                        if (!pointers.Contains(type, StringComparer.Ordinal))
                            pointers.Add(type);

                        if (!reachedHere.Types.Contains(type, StringComparer.Ordinal))
                            reachedHere.Types.Add(type);
                    }
                }

                // A union join the owner split itself: its branches are the owner's to say.
                if (origin.Branches is null && described["branches"] is JsonArray split && !ownerBranches.ContainsKey(alias))
                    ownerBranches[alias] = (JsonArray)split.DeepClone();
            }
        }
    }

    /// <summary>A path a remote target's owner said the target lacks, at the check query's projection: relative to the alias, or to the owning row.</summary>
    private sealed record Miss(OwnerCheck Check, string Path, bool Parent, string OwnerPath)
    {
        /// <summary>Whether the path lies under an alias a stage continued at the owner adds: that stage's join lacks it, not the target.</summary>
        public bool Continued => !Parent && Check.ContinuedAliases.Contains(Path.Split('.')[0], StringComparer.Ordinal);
    }

    /// <summary>An owner's <c>UNKNOWN_PATH</c> at the check query's projection as a path of the alias or of the owning row; null for any other error.</summary>
    private static Miss? MissOf(OwnerCheck check, JsonObject error)
    {
        if (error["code"]?.ToString() != Codes.UnknownPath || error["stage"] is not JsonValue at || !at.TryGetValue<int>(out var stage)
            || error["path"]?.ToString() is not { Length: > 0 } path)
            return null;

        // A path of a union join's select hint that this target's branch does not reach: the owner says it at the branch's stage.
        if (stage != check.ProjectAt)
            return check.OriginOf(stage) is { Branches: not null } union
                ? new Miss(check, path.StartsWith(union.Aliases[0] + ".", StringComparison.Ordinal) ? path : union.Aliases[0] + "." + path, Parent: false, path)
                : null;

        var element = BoundKeyedBy.Element + ".";

        // A path under an alias a continued stage added is that stage's, whatever the target is.
        if (!check.Item || check.ContinuedAliases.Contains(path.Split('.')[0], StringComparer.Ordinal))
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
                var hinted = first.Check.Continued.FirstOrDefault(stage => stage.Branches is not null && stage.Aliases.Contains(continued, StringComparer.Ordinal)) is { } hinting
                    && hinting.Stage.Resolve?.Select?.Contains(path[(continued.Length + 1)..], StringComparer.Ordinal) == true ? hinting : null;
                // The hint is written at the union join; every other path asked under its alias is the projection's.
                var at = hinted?.OriginIndex ?? first.Check.ProjectStage ?? first.Check.Stage;

                if (hinted is not null)
                    path = path[(continued.Length + 1)..];

                // Under a union join's alias the path is dropped for the branches that do not reach it, as
                // a run drops it; only one no branch reaches is unknown. A branch whose owner did not
                // answer is taken to reach it.
                if (first.Check.Continued.FirstOrDefault(stage => stage.Branches is not null && stage.Aliases.Contains(continued, StringComparer.Ordinal)) is { } union
                    && checks.Where(check => check.Continued.Contains(union)).Any(check => !answered.Contains(check) || !lacking.Contains(check)))
                {
                    foreach (var check in lacking.DistinctBy(check => check.Bound?.Declared.Entity ?? check.Target))
                        notes.Add(Notes.BranchPathDropped(union.OriginIndex, continued, check.Bound?.Declared.Entity ?? check.Target, hinted is null ? path[(continued.Length + 1)..] : path));

                    continue;
                }

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

        var answer = await self(request with { Budget = new ExplainBudget((int)Math.Min(int.MaxValue, Remaining.TotalMilliseconds), pool.CallsLeft) }, cancellationToken).ConfigureAwait(false);

        if (answer is null)
            return (null, Unsupported);

        Revise(answer);

        // The owners this host's own explain asked are this explain's: their calls are spent here.
        foreach (var nested in answer["owners"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (nested["remote"]?.GetValue<bool>() != true || nested["service"]?.GetValue<string>() is not { } service)
                continue;

            // It asked them through this explain's pool, so they are taken off what is left already.
            Nest(nested, service, nested["via"]?.GetValue<string>(), spend: false);
        }

        return (answer, null);
    }

    /// <summary>One explain request of a round: the owner it goes to (empty: this host itself), the target it speaks of and the caller's stage a note about it lies at.</summary>
    /// <param name="Service">The owner asked; empty for this host itself.</param>
    /// <param name="Request">The explain request.</param>
    /// <param name="Target">The target the request speaks of.</param>
    /// <param name="Stage">The caller's stage a note about it lies at.</param>
    /// <param name="Nests">Whether the owner may have to ask owners of its own for it: a query that carries continued stages, or a lookup of an entity the owner reaches for this host.</param>
    private sealed record Ask(string Service, ExplainRequest Request, string Target, int? Stage, bool Nests);

    /// <summary>
    /// One round of an explain (improvement plan §3.E): the requests this host explains itself, in order;
    /// then, of the others, what is kept is read from the cache, and what is left goes out through the
    /// pool (<see cref="ExplainOwnerPool"/>) as one call per owner service
    /// (<see cref="IRemoteQueryClient.ExplainBatchAsync"/>), the services asked at once, each within
    /// what is left of the time and with its share of the calls left. A request the
    /// cached tier finds no kept answer for is not asked. The answers come back in the order asked.
    /// </summary>
    private async Task<(JsonObject? Answer, string? Reason)[]> RoundAsync(IReadOnlyList<Ask> asks, int round, CancellationToken cancellationToken)
    {
        var results = new (JsonObject? Answer, string? Reason)[asks.Count];
        var keys = new string?[asks.Count];
        var open = new List<(string Service, List<int> Indexes)>();

        for (var index = 0; index < asks.Count; index++)
            if (asks[index].Service.Length == 0)
                results[index] = await SelfAsync(asks[index].Request, cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < asks.Count; index++)
        {
            var ask = asks[index];

            if (ask.Service.Length == 0)
                continue;

            if (client is null)
            {
                results[index] = (null, Unsupported);
                continue;
            }

            var use = uses.FirstOrDefault(each => each.Service == ask.Service && each.Via is null);
            var key = keys[index] = ExplainForwardCache.KeyOf(context.Organisation ?? Guid.Empty, context.UserId, ask.Service, ask.Request);

            if (cache.Get(ask.Service, key) is { } kept)
            {
                use ??= Use(ask.Service, null);
                use.Answered = true;
                ReadFacts(use, kept);

                // What the kept answer depended on is depended on still; nothing was called for it now.
                foreach (var nested in kept["owners"]?.AsArray().OfType<JsonObject>() ?? [])
                    if (nested["remote"]?.GetValue<bool>() == true && nested["service"]?.GetValue<string>() is { } reachedService)
                        Nest(nested, reachedService, nested["via"]?.GetValue<string>() is { } further ? ask.Service + ">" + further : ask.Service, called: false);

                results[index] = (kept, null);
                continue;
            }

            // The cached tier asks no owner: what is not kept is left out, and said to be.
            if (cachedOnly)
            {
                use ??= Use(ask.Service, null);
                use.Reason ??= Cached;
                results[index] = (null, Cached);
                continue;
            }

            if (open.FirstOrDefault(group => group.Service == ask.Service) is { Indexes: { } indexes })
                indexes.Add(index);
            else
                open.Add((ask.Service, [index]));
        }

        if (open.Count == 0)
            return results;

        // What may go out: within the time, the services and the calls an explain has, each service's
        // checks as one call. A limit leaves a part out and notes it; nothing is retried.
        var sending = new List<(ExplainOwnerUse Use, List<int> Indexes)>();
        var remaining = budget - clock.Elapsed;

        foreach (var (service, indexes) in open)
        {
            var use = uses.FirstOrDefault(each => each.Service == service && each.Via is null);
            var head = asks[indexes[0]];

            // A call cut by the budget spent it, whatever the clock reads a moment later (the timer may
            // fire a hair before the elapsed time reaches the budget).
            if (spent || remaining <= TimeSpan.Zero)
            {
                var late = OutOfTime(use ?? Use(service, null), service, head.Target, head.Stage);

                foreach (var index in indexes)
                    results[index] = late;

                continue;
            }

            // A service is counted once it is called: one the cache answered, or the cached tier left out, takes no place.
            var refused = use is not { Calls: > 0 } && uses.Count(each => each.Via is null && each.Calls > 0) + sending.Count(group => group.Use.Calls == 0) >= maxServices
                ? Limited("ownerServices", maxServices, service, head.Target, head.Stage)
                : pool.CallsLeft <= 0 ? Limited("ownerCalls", maxCalls, service, head.Target, head.Stage)
                : ((JsonObject?, string?)?)null;

            if (refused is { } limited)
            {
                foreach (var index in indexes)
                    results[index] = limited;

                continue;
            }

            // What exceeds a batch is left out: one call per service and round, never a second.
            foreach (var index in indexes.Skip(maxChecks).ToList())
            {
                results[index] = Limited("checks", maxChecks, service, asks[index].Target, asks[index].Stage);
                indexes.Remove(index);
            }

            sending.Add((use ?? Use(service, null), indexes));
        }

        if (sending.Count == 0)
            return results;

        // The asks go out through the pool: with those of the explains beside this one, one call per owner.
        var called = await pool.AskAsync(client,
            sending.Select(group => new ExplainOwnerPool.Group(group.Use.Service, group.Indexes.Select(index => asks[index].Request).ToList(), group.Indexes.Any(index => asks[index].Nests))).ToList(),
            remaining, maxChecks, cancellationToken).ConfigureAwait(false);

        // What came back is taken in the order asked, so the answer does not depend on who answered first.
        for (var position = 0; position < sending.Count; position++)
        {
            var (use, indexes) = sending[position];
            var outcome = called[position];
            var head = asks[indexes[0]];

            use.Ms += outcome.Ms;

            // One call per service and round, counted for the explain that asked first.
            if (outcome.Paid)
            {
                use.Rounds.Add(round);
                use.Calls++;
            }

            if (outcome.Answers is not { } answers)
            {
                (JsonObject?, string?) failed;

                if (outcome.Failure is ExplainOwnerPool.NoCalls or ExplainOwnerPool.TooManyChecks)
                    failed = Limited(outcome.Failure == ExplainOwnerPool.NoCalls ? "ownerCalls" : "checks", outcome.Failure == ExplainOwnerPool.NoCalls ? maxCalls : maxChecks, use.Service, head.Target, head.Stage);
                else if (outcome.Failure == Timeout)
                {
                    spent = true;
                    failed = OutOfTime(use, use.Service, head.Target, head.Stage);
                }
                else
                {
                    use.Answered ??= false;
                    use.Reason = outcome.Failure ?? Unsupported;
                    failed = (null, use.Reason);
                }

                foreach (var index in indexes)
                    results[index] = failed;

                continue;
            }

            for (var at = 0; at < indexes.Count; at++)
            {
                var index = indexes[at];

                // A check the owner left out of its answer (refused before binding, or past the batch's budget).
                if (at >= answers.Count || answers[at] is not { } answer)
                {
                    use.Answered ??= false;
                    use.Reason ??= Unreachable;
                    results[index] = (null, Unreachable);
                    continue;
                }

                cache.Set(use.Service, keys[index]!, answer);
                use.Answered = true;
                use.Cached = false;
                ReadFacts(use, answer);

                // What the owner asked its own owners is spent of this explain's calls.
                foreach (var nested in answer["owners"]?.AsArray().OfType<JsonObject>() ?? [])
                    if (nested["remote"]?.GetValue<bool>() == true && nested["service"]?.GetValue<string>() is { } reachedService)
                        Nest(nested, reachedService, nested["via"]?.GetValue<string>() is { } further ? use.Service + ">" + further : use.Service);

                results[index] = (answer, null);
            }
        }

        return results;
    }

    private ExplainOwnerUse Use(string service, string? via)
    {
        var use = new ExplainOwnerUse(service, via);

        uses.Add(use);

        return use;
    }

    /// <summary>An owner another owner's answer names: its calls are counted here, and it is listed as reached through that owner.</summary>
    private void Nest(JsonObject nested, string service, string? via, bool called = true, bool spend = true)
    {
        var use = uses.FirstOrDefault(each => each.Service == service && each.Via == via) ?? Use(service, via);

        // A service this host does not know itself is reached again through the owner that reached it.
        if (via is not null && client?.IsConfigured(service) != true)
            cache.Route(service, via.Split('>')[0]);

        var calls = called && nested["calls"] is JsonValue count && count.TryGetValue<int>(out var number) ? number : 0;

        use.Calls += calls;

        if (spend)
            pool.Spend(calls);

        // An owner asked twice answered when one of the asks was answered; the first reason stays.
        if (nested["answered"] is JsonValue answered && answered.TryGetValue<bool>(out var flag))
            use.Answered = flag || use.Answered == true;

        use.Reason ??= nested["reason"]?.GetValue<string>();
        use.Cached &= calls == 0;
        use.Engine = nested["engine"]?.DeepClone() ?? use.Engine;
    }

    private void ReadFacts(ExplainOwnerUse use, JsonObject answer)
    {
        Revise(answer);

        if (answer["engine"] is JsonObject engine)
            use.Engine = new JsonObject { ["version"] = engine["version"]?.DeepClone(), ["contract"] = engine["contract"]?.DeepClone() };
    }

    /// <summary>Takes the schema revisions an answer names; a revision once named is not replaced by none.</summary>
    private void Revise(JsonObject answer)
    {
        if (answer["revision"]?["schema"] is not JsonObject named)
            return;

        foreach (var (service, value) in named)
        {
            var text = value is JsonValue stated && stated.TryGetValue<string>(out var read) ? read : null;

            if (!revisions.TryGetValue(service, out var known) || known is null)
                revisions[service] = text;
        }
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
                    "checks" => $"One call to an owner carries at most {max} checks; what '{service}' binds for '{target}' was not checked.",
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
        Message = reason == Cached && check.Service.Length > 0
            ? $"What '{check.Service}' binds for '{check.Target}' was not asked of its owner: this explain answers from the owner answers already kept (remote \"cached\"), and none is kept for it. Explain with remote \"check\" to ask the owner."
            : check.Service.Length == 0
            ? $"The stages continued under '{check.Target}' at this host were not checked ({reason}); this host binds them when the query runs."
            : $"What '{check.Service}' binds for '{check.Target}' (its select, the paths under its alias, the stages continued there) could not be checked at its owner ({reason}); the owner binds it when the query runs.",
        Stage = check.FirstContinued,
        Params = new Dictionary<string, object?> { ["service"] = check.Service.Length == 0 ? null : check.Service, ["target"] = check.Target, ["reason"] = reason },
    };
}
