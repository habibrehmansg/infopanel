using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;

namespace InfoPanel.Sensors.Linux;

/// <summary>
/// Which of the Linux source's listed sensors are in use: used (<see cref="Note"/>, on every consumer's read and graph
/// draw) within <see cref="UseWindow"/>. Only listed keys are kept (<see cref="List"/>, at each discovery), each one's
/// last use carried over, so nothing grows with the keys asked for. <see cref="Note"/> takes no lock and allocates
/// nothing; a use noted while the list is being replaced may be missed, and the consumer's next read notes it again.
/// </summary>
internal sealed class LinuxDemand
{
    /// <summary>A sensor used within this long is in use (the same 2 minutes as the weather's and the public address's).</summary>
    public static readonly TimeSpan UseWindow = TimeSpan.FromMinutes(2);

    private const long Never = long.MinValue;

    private sealed record Table(FrozenDictionary<SensorKey, int> Index, long[] LastUse);

    private readonly TimeProvider _time;
    private volatile Table _table = new(FrozenDictionary<SensorKey, int>.Empty, []);

    public LinuxDemand(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>A consumer used <paramref name="key"/> now; a key not listed is ignored.</summary>
    public void Note(SensorKey key)
    {
        var table = _table;
        if (table.Index.TryGetValue(key, out var i))
            Volatile.Write(ref table.LastUse[i], _time.GetTimestamp());
    }

    /// <summary>Whether <paramref name="key"/> was used within <see cref="UseWindow"/>.</summary>
    public bool InUse(SensorKey key)
    {
        var table = _table;
        if (!table.Index.TryGetValue(key, out var i))
            return false;
        var last = Volatile.Read(ref table.LastUse[i]);
        return last != Never && _time.GetElapsedTime(last) <= UseWindow;
    }

    /// <summary>The keys listed now; each one's last use is kept, a new one's is never.</summary>
    public void List(IReadOnlyList<SensorKey> keys)
    {
        var old = _table;
        var index = new Dictionary<SensorKey, int>(keys.Count);
        var lastUse = new long[keys.Count];
        foreach (var key in keys)
        {
            if (index.ContainsKey(key))
                continue;
            var i = index.Count;
            index[key] = i;
            lastUse[i] = old.Index.TryGetValue(key, out var was) ? Volatile.Read(ref old.LastUse[was]) : Never;
        }

        _table = new Table(index.ToFrozenDictionary(), lastUse);
    }
}
