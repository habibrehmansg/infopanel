using System;
using System.Threading;

namespace InfoPanel.Service;

/// <summary>
/// One reading of the relay's clock (docs/2.0/relay.md, "Never late"): the relay's <c>now</c> less the wall clock's
/// midpoint between the question and the answer, and where both of the engine's clocks stood when it came back.
/// </summary>
/// <param name="OffsetMs">The relay's clock less the wall clock, in ms.</param>
/// <param name="WallAt">The wall clock when the answer came.</param>
/// <param name="MonotonicAt">The monotonic clock (<see cref="TimeProvider.GetTimestamp"/>) when the answer came.</param>
public sealed record RelayReading(double OffsetMs, DateTimeOffset WallAt, long MonotonicAt);

/// <summary>
/// The engine's two clocks, read once (<see cref="Take"/>): what is decided from it (whether a reading may be used, how
/// late a call is, its deadline) is decided against the same instant, and a step of the wall clock after it reaches none of it.
/// </summary>
/// <param name="Wall">The wall clock (<see cref="TimeProvider.GetUtcNow"/>).</param>
/// <param name="Monotonic">The monotonic clock (<see cref="TimeProvider.GetTimestamp"/>).</param>
public readonly record struct ClockSnapshot(DateTimeOffset Wall, long Monotonic)
{
    public static ClockSnapshot Take(TimeProvider time) => new(time.GetUtcNow(), time.GetTimestamp());
}

/// <summary>
/// The engine's two clocks, read together through one <see cref="TimeProvider"/>: the wall clock
/// (<see cref="TimeProvider.GetUtcNow"/>), which a sleep moves on and the user or the network can step, and the monotonic
/// clock (<see cref="TimeProvider.GetTimestamp"/>, <c>Stopwatch</c>'s), which no one steps and which on macOS stops in
/// sleep. What must not be late or run long is measured on both, and the stricter wins: a sleep or a step can at worst
/// refuse a call, never let a late one through.
/// </summary>
public static class RelayClock
{
    /// <summary>How long a reading's question and answer may take, on both clocks.</summary>
    public static readonly TimeSpan ReadingRoundTrip = TimeSpan.FromSeconds(2);

    /// <summary>How old a reading may be, on either clock, before a call asks for a fresh one.</summary>
    public static readonly TimeSpan ReadingLife = TimeSpan.FromSeconds(30);

    /// <summary>How far the two clocks' elapsed times since a reading may disagree before it is void (a step of the wall clock).</summary>
    public static readonly TimeSpan ClocksAgree = TimeSpan.FromSeconds(1);

    /// <summary>A call sent longer ago than this on the relay's clock is refused unrun (<c>late</c>).</summary>
    public static readonly TimeSpan LateAfter = TimeSpan.FromSeconds(10);

    /// <summary>How long after it was sent, on the relay's clock, a call may still make its change.</summary>
    public static readonly TimeSpan CommitWithin = TimeSpan.FromSeconds(60);

    /// <summary>How far a good reading can be off: half its round trip, the most its midpoint can miss the relay's answer by.</summary>
    public static readonly TimeSpan ReadingError = ReadingRoundTrip / 2;

    /// <summary>
    /// The reading made from a <c>time</c> answer, or null when it doesn't count: it took longer than
    /// <see cref="ReadingRoundTrip"/> on either clock (an answer held across a sleep fails the wall clock, or Stopwatch
    /// where it runs in sleep), or a clock went back meanwhile.
    /// </summary>
    public static RelayReading? Reading(long relayNowMs, DateTimeOffset wallAsked, long monotonicAsked, DateTimeOffset wallAnswered,
        long monotonicAnswered, TimeProvider time)
    {
        var wall = wallAnswered - wallAsked;
        var monotonic = time.GetElapsedTime(monotonicAsked, monotonicAnswered);
        if (wall < TimeSpan.Zero || monotonic < TimeSpan.Zero || wall > ReadingRoundTrip || monotonic > ReadingRoundTrip)
            return null;
        var midpoint = Ms(wallAsked + wall / 2);
        return new RelayReading(relayNowMs - midpoint, wallAnswered, monotonicAnswered);
    }

    /// <summary>
    /// Whether <paramref name="reading"/> may still be used now: the wall clock's time since it isn't negative and agrees
    /// with the monotonic clock's within <see cref="ClocksAgree"/> (else the wall clock was stepped, and the reading is
    /// void), and neither is over <see cref="ReadingLife"/>.
    /// </summary>
    public static bool IsUsable(RelayReading? reading, TimeProvider time) => IsUsable(reading, ClockSnapshot.Take(time), time);

    /// <summary><see cref="IsUsable(RelayReading?, TimeProvider)"/> at <paramref name="now"/>, the clocks as read once.</summary>
    public static bool IsUsable(RelayReading? reading, ClockSnapshot now, TimeProvider time)
    {
        if (reading is null)
            return false;
        var wall = now.Wall - reading.WallAt;
        var monotonic = time.GetElapsedTime(reading.MonotonicAt, now.Monotonic);
        return wall >= TimeSpan.Zero && (wall - monotonic).Duration() <= ClocksAgree && wall <= ReadingLife && monotonic <= ReadingLife;
    }

    /// <summary>
    /// Whether a call sent at <paramref name="sentAtMs"/> on the relay's clock contradicts <paramref name="reading"/> at
    /// <paramref name="now"/>: a call arrives after it was sent, so the relay's clock is at least its <c>sentAt</c>; a reading
    /// that puts it further behind than <see cref="ReadingError"/> missed a step of the relay's clock, and is read again.
    /// </summary>
    public static bool Contradicts(RelayReading reading, ClockSnapshot now, double sentAtMs) =>
        sentAtMs - RelayNowMs(reading, now) > ReadingError.TotalMilliseconds;

    /// <summary>The relay's clock at <paramref name="now"/>, in ms since 1970 UTC, by <paramref name="reading"/>.</summary>
    public static double RelayNowMs(RelayReading reading, ClockSnapshot now) => Ms(now.Wall) + reading.OffsetMs;

    /// <summary><paramref name="instant"/> in ms since 1970 UTC, with its fraction.</summary>
    public static double Ms(DateTimeOffset instant) => (instant - DateTimeOffset.UnixEpoch).TotalMilliseconds;
}

/// <summary>
/// A relay call's cutoff for changes (docs/2.0/relay.md): tripped by the relay's <c>cancel</c>, by the connection's close,
/// or by its commit deadline, which is set once, as the time left on the relay's clock, and then runs on both of the
/// engine's clocks, expiring when either has run it out, so neither a sleep nor a step of the wall clock extends it.
/// Every place a tool from elsewhere writes asks <see cref="IsTripped"/> under the write's lock, just before it writes, and
/// stops unmade when it is. The deadline is the time left on the relay's clock, from 0 to 60 s.
/// </summary>
public sealed class RelayCutoff : IDisposable
{
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _tripped = new();
    private TimeSpan? _left;
    private DateTimeOffset _wallFrom;
    private long _monotonicFrom;
    private ITimer? _timer;
    private string? _reason;

    public RelayCutoff(TimeProvider time) => _time = time;

    /// <summary>Cancelled once the cutoff is tripped (the deadline trips it by a timer on the monotonic clock too).</summary>
    public CancellationToken Token => _tripped.Token;

    /// <summary>Why it was tripped (<c>cancel</c>, <c>closed</c>, <c>deadline</c>), or null.</summary>
    public string? Reason
    {
        get
        {
            lock (_lock)
                return _reason;
        }
    }

    /// <summary>Trips the cutoff: from now on <see cref="IsTripped"/> is true.</summary>
    public void Trip(string reason)
    {
        lock (_lock)
            _reason ??= reason;
        try
        {
            _tripped.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Sets the commit deadline <paramref name="left"/> from <paramref name="from"/> (the clocks as read when it was worked
    /// out, not read again), on both clocks.
    /// </summary>
    public void SetDeadline(TimeSpan left, ClockSnapshot from)
    {
        lock (_lock)
        {
            _left = left < TimeSpan.Zero ? TimeSpan.Zero : left;
            _wallFrom = from.Wall;
            _monotonicFrom = from.Monotonic;
            _timer?.Dispose();
            // The timer is on the monotonic clock, which no one steps: what is left of the deadline on it now.
            var due = _left.Value - _time.GetElapsedTime(from.Monotonic);
            _timer = _time.CreateTimer(_ => Trip("deadline"), null, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Whether the change must stop unmade: tripped, or past the deadline on either clock (the wall clock's time since it
    /// was set, or the monotonic clock's).
    /// </summary>
    public bool IsTripped
    {
        get
        {
            if (_tripped.IsCancellationRequested)
                return true;
            TimeSpan? left;
            DateTimeOffset wallFrom;
            long monotonicFrom;
            lock (_lock)
                (left, wallFrom, monotonicFrom) = (_left, _wallFrom, _monotonicFrom);
            if (left is not { } deadline)
                return false;
            if (_time.GetUtcNow() - wallFrom >= deadline || _time.GetElapsedTime(monotonicFrom) >= deadline)
            {
                Trip("deadline");
                return true;
            }

            return false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
