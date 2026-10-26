namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// A clock in a UTC local zone that moves only on <see cref="Advance"/>, with timers that fire as it passes their
/// due times (enough for <see cref="PeriodicTimer"/>).
/// </summary>
internal sealed class TimerTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<FakeTimer> _timers = [];
    private long _ticks;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
            return start + TimeSpan.FromTicks(_ticks);
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock)
            return _ticks;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        lock (_lock)
            _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Timers created and not yet disposed.</summary>
    public int TimerCount
    {
        get
        {
            lock (_lock)
                return _timers.Count;
        }
    }

    /// <summary>Moves the clock forward, firing each timer at each due time it passes, in time order.</summary>
    public void Advance(TimeSpan by)
    {
        long end;
        lock (_lock)
            end = _ticks + by.Ticks;

        while (true)
        {
            FakeTimer? next;
            lock (_lock)
            {
                next = _timers.Where(t => t.Due is { } due && due <= end).MinBy(t => t.Due!.Value);
                if (next == null)
                {
                    _ticks = end;
                    return;
                }

                _ticks = next.Due!.Value;
                next.Due = next.Period is { } period ? _ticks + period : null;
            }

            next.Fire();
        }
    }

    private sealed class FakeTimer(TimerTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long? Due;
        public long? Period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period.Ticks;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._lock)
                owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
