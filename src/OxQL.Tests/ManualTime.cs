namespace OxQL.TestCases;

/// <summary>
/// A clock that stands still until a test moves it, with timers that fire when it passes them: a
/// timeout, a delay or a deadline taken from it ends when the test says so, and never by itself.
/// Its timestamps start at zero and its wall-clock time at <see cref="Start"/>; both move together.
/// </summary>
/// <remarks>Self-contained, so another test project can link the file.</remarks>
public sealed class ManualTime : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private readonly SemaphoreSlim created = new(0);
    private long ticks;

    public ManualTime()
        : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public ManualTime(DateTimeOffset start) => Start = start;

    /// <summary>The wall-clock time the clock started at.</summary>
    public DateTimeOffset Start { get; }

    /// <summary>The longest <see cref="Armed"/> waits for a timer that its passing path always starts.</summary>
    public static TimeSpan Ceiling { get; } = TimeSpan.FromSeconds(30);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(GetTimestamp());

    /// <summary>The wall-clock time; setting it moves the clock to it, and fires the timers it passes.</summary>
    public DateTimeOffset Now
    {
        get => GetUtcNow();
        set => Advance(value - GetUtcNow());
    }

    /// <summary>The due times of the timers that were started, as offsets from the clock's start, in milliseconds.</summary>
    public List<long> Due
    {
        get
        {
            lock (timers)
                return timers.Select(timer => timer.Due / TimeSpan.TicksPerMillisecond).ToList();
        }
    }

    /// <summary>Completes when one more timer has been started; fails after <see cref="Ceiling"/> when none is.</summary>
    public async Task Armed()
    {
        if (!await created.WaitAsync(Ceiling).ConfigureAwait(false))
            throw new TimeoutException("No timer was started.");
    }

    /// <summary>Moves the clock on and fires, in the order they are due, the timers it passes.</summary>
    public void Advance(TimeSpan by)
    {
        var now = Interlocked.Add(ref ticks, by.Ticks);
        List<ManualTimer> due;

        lock (timers)
            due = timers.Where(timer => timer.Fires(now)).OrderBy(timer => timer.Due).ToList();

        foreach (var timer in due)
            timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);

        timer.Change(dueTime, period);

        lock (timers)
            timers.Add(timer);

        created.Release();

        return timer;
    }

    private sealed class ManualTimer(ManualTime clock, TimerCallback callback, object? state) : ITimer
    {
        private readonly object gate = new();
        private long due = long.MaxValue;
        private long period;
        private bool ended;
        private bool disposed;

        /// <summary>When the timer is or was due, in ticks of the clock; <see cref="long.MaxValue"/> when it was never set.</summary>
        public long Due
        {
            get
            {
                lock (gate)
                    return due;
            }
        }

        public bool Fires(long now)
        {
            lock (gate)
                return !ended && due != long.MaxValue && now >= due;
        }

        public void Fire()
        {
            lock (gate)
            {
                if (ended || due == long.MaxValue)
                    return;

                if (period > 0)
                    due += period;
                else
                    ended = true;
            }

            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (gate)
            {
                if (disposed)
                    return false;

                due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
                this.period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
                ended = false;

                return true;
            }
        }

        public void Dispose()
        {
            lock (gate)
                disposed = ended = true;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
