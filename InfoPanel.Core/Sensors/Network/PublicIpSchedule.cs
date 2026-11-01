using System;
using System.Threading;

namespace InfoPanel.Sensors.Network;

/// <summary>
/// When <see cref="PublicIpLookup"/> asks for the public address: only while a public-address reading is in use (read
/// within <see cref="UseWindow"/>), never otherwise; once when it comes into use, again when the network changes while
/// it is in use, and otherwise <see cref="Every"/> after the last lookup; after a lookup that failed, after a back-off
/// that starts at <see cref="FirstBackoff"/> and doubles with each failure up to <see cref="MaxBackoff"/>, until one
/// answers. A back-off is never cut short: a cause during it (coming into use, a network change) is kept and served by
/// the lookup at its end, so a flapping network asks no more often than the back-off allows. Timestamps are the time
/// provider's. Thread-safe: <see cref="NoteUse"/> is called on every read of a
/// public-address reading and takes no lock while the reading stays in use.
/// </summary>
public sealed class PublicIpSchedule(TimeProvider time)
{
    /// <summary>A public-address reading read within this long is in use.</summary>
    public static readonly TimeSpan UseWindow = TimeSpan.FromMinutes(2);

    /// <summary>The time between lookups while in use and the network stays as it is.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    /// <summary>The wait after the first failed lookup, doubled after each further one, up to <see cref="MaxBackoff"/>.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private const long Never = long.MinValue;

    private readonly Lock _lock = new();
    private long _lastUse = Never;
    // A lookup asked for now: the reading came into use, or the network changed while it was in use.
    private bool _pending;
    // When the next lookup is due without a cause (after the last one), or Never before any.
    private long _due = Never;
    private TimeSpan _backoff = TimeSpan.Zero;

    /// <summary>The current back-off: zero after a lookup that answered (or before any), else the wait after the last failure.</summary>
    public TimeSpan Backoff
    {
        get
        {
            lock (_lock)
                return _backoff;
        }
    }

    /// <summary>
    /// A public-address reading was read at <paramref name="now"/>. True when that brought it into use (not read within
    /// <see cref="UseWindow"/> before): a lookup is due at once, or at the end of a back-off under way.
    /// </summary>
    public bool NoteUse(long now)
    {
        var last = Interlocked.Exchange(ref _lastUse, now);
        if (last != Never && time.GetElapsedTime(last, now) <= UseWindow)
            return false;
        lock (_lock)
            _pending = true;
        return true;
    }

    /// <summary>Whether a public-address reading was read within <see cref="UseWindow"/> of <paramref name="now"/>.</summary>
    public bool InUse(long now)
    {
        var last = Interlocked.Read(ref _lastUse);
        return last != Never && time.GetElapsedTime(last, now) <= UseWindow;
    }

    /// <summary>
    /// The network changed at <paramref name="now"/> (an interface up or down, a local address added or gone). True when
    /// that asks for a lookup: only while in use (one that comes into use later asks then anyway); at once, or at the end
    /// of a back-off under way.
    /// </summary>
    public bool NetworkChanged(long now)
    {
        if (!InUse(now))
            return false;
        lock (_lock)
            _pending = true;
        return true;
    }

    /// <summary>Whether a lookup is due at <paramref name="now"/>; when it is, it is taken (the caller looks up now).</summary>
    public bool TakeDue(long now)
    {
        if (!InUse(now))
            return false;
        lock (_lock)
        {
            if (InBackoff(now) || (!_pending && (_due == Never || now < _due)))
                return false;
            _pending = false;
            _due = Never;
            return true;
        }
    }

    /// <summary>
    /// How long from <paramref name="now"/> until a lookup falls due by time alone, or null when none will without a
    /// cause (not in use, or no lookup yet): the caller waits for <see cref="NoteUse"/> or <see cref="NetworkChanged"/>.
    /// </summary>
    public TimeSpan? WaitFrom(long now)
    {
        if (!InUse(now))
            return null;
        lock (_lock)
        {
            if (InBackoff(now))
                return time.GetElapsedTime(now, _due);
            if (_pending)
                return TimeSpan.Zero;
            if (_due == Never)
                return null;
            return now >= _due ? TimeSpan.Zero : time.GetElapsedTime(now, _due);
        }
    }

    // Under the lock: a failed lookup's back-off hasn't ended at now.
    private bool InBackoff(long now) => _backoff != TimeSpan.Zero && _due != Never && now < _due;

    /// <summary>A lookup ended at <paramref name="now"/>: the next is due <see cref="Every"/> later when it answered, else after the back-off.</summary>
    public void LookedUp(long now, bool answered)
    {
        lock (_lock)
        {
            _backoff = answered ? TimeSpan.Zero
                : _backoff == TimeSpan.Zero ? FirstBackoff
                : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
            var wait = answered ? Every : _backoff;
            _due = now + (long)(wait.Ticks * (double)time.TimestampFrequency / TimeSpan.TicksPerSecond);
        }
    }
}
