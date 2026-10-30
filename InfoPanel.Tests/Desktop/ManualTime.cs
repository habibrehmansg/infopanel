namespace InfoPanel.Tests.Desktop;

/// <summary>A clock that moves only when the test advances it, with one-shot and periodic timers that fire on <see cref="Advance"/>.</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    public override DateTimeOffset GetUtcNow() => start.AddTicks(Interlocked.Read(ref _ticks));

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Whether a timer is due exactly <paramref name="at"/> after the start: lets a test wait until a timer it is about to
    /// pass is armed, instead of advancing past a timer that hasn't been set yet.
    /// </summary>
    public bool HasTimerDueAt(TimeSpan at)
    {
        lock (_lock)
            return _timers.Any(t => t.DueAt == at.Ticks);
    }

    /// <summary>How many timers are due exactly <paramref name="at"/> after the start (as <see cref="HasTimerDueAt"/>).</summary>
    public int TimersDueAt(TimeSpan at)
    {
        lock (_lock)
            return _timers.Count(t => t.DueAt == at.Ticks);
    }

    public void Advance(TimeSpan by)
    {
        var now = Interlocked.Add(ref _ticks, by.Ticks);
        while (true)
        {
            ManualTimer? due;
            lock (_lock)
                due = _timers.Where(t => t.DueAt is long at && at <= now).OrderBy(t => t.DueAt).FirstOrDefault();
            if (due == null)
                return;
            due.Fire();
        }
    }

    private sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period;

        public long? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (time._lock)
            {
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    DueAt = null;
                    time._timers.Remove(this);
                }
                else
                {
                    DueAt = time.GetTimestamp() + dueTime.Ticks;
                    if (!time._timers.Contains(this))
                        time._timers.Add(this);
                }
            }

            return true;
        }

        public void Fire()
        {
            lock (time._lock)
            {
                if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
                {
                    DueAt += _period.Ticks;
                }
                else
                {
                    DueAt = null;
                    time._timers.Remove(this);
                }
            }

            callback(state);
        }

        public void Dispose() => Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
