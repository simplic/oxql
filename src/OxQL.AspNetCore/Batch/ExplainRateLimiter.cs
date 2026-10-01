using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore.Models;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Engine;
using OxQL.Core.Models;

namespace OxQL.AspNetCore.Batch;

/// <summary>
/// What an explain was admitted with, held until it is answered; or why it was not admitted
/// (<see cref="Limit"/>, <see cref="Max"/>, <see cref="RetryAfter"/>). Disposing it frees the places in flight.
/// </summary>
public sealed class ExplainLease : IDisposable
{
    private readonly List<RateLimitLease> held;

    internal ExplainLease(List<RateLimitLease> held)
    {
        this.held = held;
        Acquired = true;
    }

    internal ExplainLease(string limit, int max, TimeSpan retryAfter)
    {
        held = [];
        Limit = limit;
        Max = max;
        RetryAfter = retryAfter;
    }

    /// <summary>Whether the explain may run.</summary>
    public bool Acquired { get; }

    /// <summary>The limit that refused it: <c>rate</c>, <c>concurrentPerUser</c>, <c>concurrentPerHost</c> or <c>concurrentPerCaller</c>.</summary>
    public string? Limit { get; }

    /// <summary>The value of that limit.</summary>
    public int Max { get; }

    /// <summary>When a retry may be admitted.</summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>The whole seconds of <see cref="RetryAfter"/>, at least one: the <c>Retry-After</c> header.</summary>
    public int RetryAfterSeconds => Math.Max(1, (int)Math.Ceiling(RetryAfter.TotalSeconds));

    /// <summary>The 429 of an explain that was not admitted.</summary>
    public Refusal Refusal() => OxQL.Core.Engine.Refusal.TooManyExplains(Limit ?? "rate", Max, RetryAfterSeconds);

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var lease in held)
            lease.Dispose();

        held.Clear();
    }
}

/// <summary>
/// The rate and concurrency limits of explain (improvement plan §3.E protection), built on the
/// ASP.NET Core rate limiters and separate from every other route, so explain traffic never queues
/// a run and nothing waits: an explain over a limit is refused at once.
/// <list type="bullet">
/// <item>The public route, per organisation and user: a token bucket of <c>Explain:RateBurst</c> refilled
/// evenly at <c>Explain:RatePerMinute</c>, at most <c>Explain:MaxConcurrentPerUser</c> in flight, and
/// at most <c>Explain:MaxConcurrentPerHost</c> in flight on the host whoever sends them.</item>
/// <item>The internal route, per calling service: at most <c>Explain:MaxConcurrentPerCaller</c> in flight.</item>
/// </list>
/// One instance per host (<see cref="For"/>); the limits are read once, when it is built.
/// </summary>
public sealed class ExplainRateLimiter : IDisposable
{
    private static readonly ConditionalWeakTable<OxQLOptions, ExplainRateLimiter> Hosts = [];

    private readonly PartitionedRateLimiter<string> tokens;
    private readonly PartitionedRateLimiter<string> perUser;
    private readonly PartitionedRateLimiter<string> perCaller;
    private readonly ConcurrencyLimiter perHost;
    private readonly ExplainOptions options;

    /// <summary>The limiter of a host with <paramref name="options"/>; <paramref name="time"/> is the clock the token bucket refills by.</summary>
    public ExplainRateLimiter(ExplainOptions options, TimeProvider? time = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));

        var burst = Math.Max(1, options.RateBurst);
        var period = TimeSpan.FromMinutes(1) / Math.Max(1, options.RatePerMinute);
        var users = Math.Max(1, options.MaxConcurrentPerUser);
        var callers = Math.Max(1, options.MaxConcurrentPerCaller);

        tokens = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.Get(key, _ => new ManualTokenBucket(burst, period, time ?? TimeProvider.System)));
        perUser = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions { PermitLimit = users, QueueLimit = 0 }));
        perCaller = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions { PermitLimit = callers, QueueLimit = 0 }));
        perHost = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = Math.Max(1, options.MaxConcurrentPerHost), QueueLimit = 0 });
    }

    /// <summary>The one limiter of the host that runs with <paramref name="options"/>.</summary>
    public static ExplainRateLimiter For(OxQLOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Hosts.GetValue(options, each => new ExplainRateLimiter(each.Explain));
    }

    /// <summary>
    /// Admits one explain of <paramref name="identity"/> (organisation and user) on the public route, or
    /// says which limit refused it. The places in flight are taken first, so an explain that would not
    /// run costs its sender no token.
    /// </summary>
    public ExplainLease Acquire(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var held = new List<RateLimitLease>();

        if (!Take(perHost.AttemptAcquire(), held))
            return Refused(held, "concurrentPerHost", options.MaxConcurrentPerHost, TimeSpan.FromSeconds(1));

        if (!Take(perUser.AttemptAcquire(identity), held))
            return Refused(held, "concurrentPerUser", options.MaxConcurrentPerUser, TimeSpan.FromSeconds(1));

        var token = tokens.AttemptAcquire(identity);

        if (!Take(token, held))
            return Refused(held, "rate", options.RatePerMinute, token.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? retry : TimeSpan.FromMinutes(1) / Math.Max(1, options.RatePerMinute));

        return new ExplainLease(held);
    }

    /// <summary>Admits one internal explain of the calling service <paramref name="caller"/>, or says that it has too many in flight.</summary>
    public ExplainLease AcquireCaller(string caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var held = new List<RateLimitLease>();

        return Take(perCaller.AttemptAcquire(caller), held)
            ? new ExplainLease(held)
            : Refused(held, "concurrentPerCaller", options.MaxConcurrentPerCaller, TimeSpan.FromSeconds(1));
    }

    private static bool Take(RateLimitLease lease, List<RateLimitLease> held)
    {
        if (lease.IsAcquired)
        {
            held.Add(lease);
            return true;
        }

        lease.Dispose();

        return false;
    }

    private static ExplainLease Refused(List<RateLimitLease> held, string limit, int max, TimeSpan retryAfter)
    {
        foreach (var lease in held)
            lease.Dispose();

        return new ExplainLease(limit, max, retryAfter);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        tokens.Dispose();
        perUser.Dispose();
        perCaller.Dispose();
        perHost.Dispose();
    }

    /// <summary>
    /// A token bucket that refills by a clock it reads when asked, one token per <c>period</c> up to
    /// <c>burst</c>: no timer, so an idle partition costs nothing and a test moves the clock itself.
    /// </summary>
    private sealed class ManualTokenBucket(int burst, TimeSpan period, TimeProvider time) : RateLimiter
    {
        private readonly object gate = new();
        private double available = burst;
        private long last = time.GetTimestamp();

        public override TimeSpan? IdleDuration => null;

        public override RateLimiterStatistics? GetStatistics() => null;

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            lock (gate)
            {
                var now = time.GetTimestamp();

                available = Math.Min(burst, available + time.GetElapsedTime(last, now) / period);
                last = now;

                if (available >= permitCount)
                {
                    available -= permitCount;

                    return new Lease(true, TimeSpan.Zero);
                }

                return new Lease(false, period * (permitCount - available));
            }
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AttemptAcquireCore(permitCount));

        private sealed class Lease(bool acquired, TimeSpan retryAfter) : RateLimitLease
        {
            public override bool IsAcquired => acquired;

            public override IEnumerable<string> MetadataNames => acquired ? [] : [MetadataName.RetryAfter.Name];

            public override bool TryGetMetadata(string metadataName, out object? metadata)
            {
                if (!acquired && metadataName == MetadataName.RetryAfter.Name)
                {
                    metadata = retryAfter;
                    return true;
                }

                metadata = null;
                return false;
            }
        }
    }
}

/// <summary>
/// Admits an explain on the public route before its body is read (<see cref="ExplainRateLimiter"/>):
/// over a limit it is 429 with <c>Retry-After</c> and the refusal envelope (<c>EXPLAIN_LIMIT</c>), and
/// nothing was parsed, bound or asked of an owner. The caller is its organisation and user, as the
/// scope provider reads them; a request with neither is limited by its remote address. A host whose
/// explain is switched off is not limited: the route answers 404.
/// </summary>
public sealed class ExplainRateFilter(OxQLOptions options) : IAsyncResourceFilter
{
    /// <inheritdoc/>
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!options.Explain.Enabled)
        {
            await next();
            return;
        }

        var httpContext = context.HttpContext;
        var scope = httpContext.RequestServices.GetService<IOxQLScopeProvider>();
        var organisation = scope is null ? null : await scope.OrganisationAsync(httpContext, httpContext.RequestAborted);
        var user = scope?.UserId(httpContext);
        var identity = organisation is null && user is null
            ? "address|" + httpContext.Connection.RemoteIpAddress
            : $"{organisation:N}|{user}";

        using var lease = ExplainRateLimiter.For(options).Acquire(identity);

        if (!lease.Acquired)
        {
            httpContext.Response.Headers.RetryAfter = lease.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Result = lease.Refusal().ToActionResult();
            return;
        }

        await next();
    }
}
