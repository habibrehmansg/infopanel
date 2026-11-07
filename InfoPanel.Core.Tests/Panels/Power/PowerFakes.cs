using System.Diagnostics;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Panels.Power;

namespace InfoPanel.Core.Tests.Panels.Power;

/// <summary>
/// A power source the test drives: <see cref="Sleep"/> is a will-sleep as a source sends it (it waits for the handler,
/// bounded by <see cref="PowerEvents.SuspendBound"/>, then allows the sleep), <see cref="Wake"/> a powered-on.
/// </summary>
internal sealed class FakePowerEvents : IPowerEvents
{
    private PowerHandlers? _handlers;

    public string Name => "the test's power source";

    public bool Disposed { get; private set; }

    public bool Start(PowerHandlers handlers)
    {
        _handlers = handlers;
        return true;
    }

    /// <summary>Will-sleep: whether the handler finished before the sleep was allowed, and how long the sleep waited (real time).</summary>
    public (bool Finished, TimeSpan Waited) Sleep()
    {
        var watch = Stopwatch.StartNew();
        var finished = PowerEvents.WaitForSuspending(_handlers!.Suspending, PowerEvents.SuspendBound);
        return (finished, watch.Elapsed);
    }

    public void Wake() => _handlers!.Resumed();

    public void Dispose() => Disposed = true;
}

/// <summary>A logger that keeps what it is told, rendered (a test's failure shows them).</summary>
internal sealed class KeptLog : Serilog.Core.ILogEventSink
{
    private readonly List<string> _lines = [];

    public Serilog.ILogger Logger => new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(this).CreateLogger();

    public void Emit(Serilog.Events.LogEvent logEvent)
    {
        lock (_lines)
            _lines.Add(logEvent.RenderMessage());
    }

    public override string ToString()
    {
        lock (_lines)
            return string.Join("\n", _lines);
    }
}

/// <summary>A clock whose timers fire only when the test moves it (<see cref="Advance"/>), each once its due time is reached.</summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<Timer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<Timer> due;
        lock (_lock)
        {
            _ticks += by.Ticks;
            due = _timers.Where(t => t.DueAt <= _ticks).ToList();
            foreach (var timer in due)
                _timers.Remove(timer);
        }
        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long DueAt;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._lock)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return true;
                DueAt = clock._ticks + dueTime.Ticks;
                clock._timers.Add(this);
            }
            return true;
        }

        public void Dispose()
        {
            lock (clock._lock)
                clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// The USB tests' clock as a computer that sleeps sees it: its timestamps (what the manager measures waits with) stop
/// while asleep, as macOS's uptime clock does; <see cref="SinceBoot"/> (macOS's <c>CLOCK_MONOTONIC_RAW</c>) and the wall
/// time don't (<see cref="SleepFor"/>). The wall time can also be corrected (<see cref="ShiftWall"/>), which moves nothing else.
/// </summary>
internal sealed class SleepingClock(FakeUsbClock inner) : TimeProvider
{
    private long _sleptTicks;
    private long _wallShiftTicks;

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override long GetTimestamp() => inner.GetTimestamp();

    public override DateTimeOffset GetUtcNow() =>
        inner.GetUtcNow().AddTicks(Interlocked.Read(ref _sleptTicks) + Interlocked.Read(ref _wallShiftTicks));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        inner.CreateTimer(callback, state, dueTime, period);

    /// <summary>The monotonic clock that counts sleep: the timestamps' time, and the time asleep.</summary>
    public TimeSpan SinceBoot() => inner.GetElapsedTime(0) + TimeSpan.FromTicks(Interlocked.Read(ref _sleptTicks));

    public void SleepFor(TimeSpan asleep) => Interlocked.Add(ref _sleptTicks, asleep.Ticks);

    public void ShiftWall(TimeSpan by) => Interlocked.Add(ref _wallShiftTicks, by.Ticks);
}
