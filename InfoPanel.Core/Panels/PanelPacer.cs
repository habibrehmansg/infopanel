using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using InfoPanel.Panels.BeadaPanel;

namespace InfoPanel.Panels;

/// <summary>
/// How many frames a second a panel is sent, so the device's own buffer stays nearly empty and a change is shown a frame
/// or two after it is written. The panel's thread keeps to it at every write: no sooner than one interval of the lower of
/// the frame rate setting and the pace after its last write began, whatever the render loop delivers.
/// <para>
/// A write returns when the device takes the frame, not when it is shown, and writes only lengthen once the device's
/// buffer is full; so the pace starts under what the panel is known to show and the writes only ever lower it:
/// </para>
/// <list type="bullet">
/// <item><b>The ceiling</b> (<see cref="CeilingOf"/>): a T113 is sent at most 15 frames a second as JPEG and 12 raw, under
/// its measured rates (about 17–20 and 16), kept at the write from the first frame and asked for from the first check;
/// other panels, the frame rate setting.</item>
/// <item><b>The thread's own rate:</b> at most <see cref="Headroom"/> × 1000 / the mean frame time (its encode included), so a
/// stream whose writes are long by nature (raw) stays under what the thread can write.</item>
/// <item><b>Lowering:</b> in <see cref="Sustained"/> of the last <see cref="Within"/> checks the median write is over
/// <see cref="LongRatio"/> × the unloaded write (the least of the last <see cref="Kept"/> writes the thread came to idle)
/// plus <see cref="SlackMs"/> while the writes keep the thread busy (<see cref="BusyShare"/>). The learned pace becomes
/// <see cref="Headroom"/> × the writes a second of that check, after one check at half of them so the buffer drains.</item>
/// <item><b>Recovery:</b> after <see cref="QuietChecks"/> checks of writes near the unloaded time, the learned pace rises one
/// frame a second each such check, up to the highest pace that kept up (sent for <see cref="KeptUpAfter"/> such checks in a
/// row with writes after an idle wait, a T113's ceiling no exception) and never past the ceiling, and only while the writes
/// after an idle wait are no longer than when that pace kept up (content no heavier). Lowered again after it rose, the
/// learned pace stays where it is lowered to.</item>
/// </list>
/// Each session, and each change of the frame rate setting, starts a new pacer.
/// </summary>
internal sealed class PanelPacer(int? ceiling)
{
    /// <summary>Idle writes kept for the unloaded time.</summary>
    public const int Kept = 60;

    /// <summary>Fewest writes since the last check that can show back-pressure.</summary>
    public const int MinWindow = 3;

    /// <summary>The share of what the thread can write, and of what the device was seen to take, that it is sent at most.</summary>
    public const double Headroom = 0.85;

    public const double LongRatio = 1.15;

    public const double SlackMs = 1;

    /// <summary>The share of the thread's time the writes take when it is waiting on the device.</summary>
    public const double BusyShare = 0.8;

    /// <summary>Checks with held-back writes, among the last <see cref="Within"/> and this one among them, before the pace drops.</summary>
    public const int Sustained = 2;

    public const int Within = 5;

    /// <summary>Quiet checks before the learned pace rises a step.</summary>
    public const int QuietChecks = 3;

    /// <summary>Quiet checks in a row at one pace for it to count as one that kept up.</summary>
    public const int KeptUpAfter = 8;

    /// <summary>The least time the thread waited for a frame for its write to count as unloaded.</summary>
    public const double IdleMs = 1;

    private const int MaxWindow = 256;

    private readonly double[] _idle = new double[Kept];
    private readonly List<(double WriteMs, double FrameMs, double IntervalMs, bool Idle)> _window = [];
    private int _idleCount;
    private long _lastEnd = -1;
    private int _heldChecks;
    private int _quiet;
    private int _steady;
    private int _steadyAt;
    private bool _rose;
    /// <summary>The highest pace that kept up (none yet: 0), on any platform; a T113's ceiling only bounds it.</summary>
    private int _keptUp;

    /// <summary>The workload tag: the median idle write when <see cref="_keptUp"/> kept up (the heaviest at it); none yet: NaN.</summary>
    private double _keptUpMs = double.NaN;

    /// <summary>A T113 (platform 5)'s ceiling, 15 frames a second as JPEG and 12 raw; null (none) for any other panel.</summary>
    public static int? CeilingOf(BeadaPanelInfo info, bool jpeg) => info.Platform == 5 ? (jpeg ? 15 : 12) : null;

    /// <summary>The pace (1–60): the most frames a second the panel is sent; 60 until the first check.</summary>
    public int Paced { get; private set; } = 60;

    /// <summary>The most frames a second the device is sent since its writes were seen held back; null until they have.</summary>
    public int? Learned { get; private set; }

    /// <summary>The median write of the last check's window (or the last write's), in milliseconds.</summary>
    public double RecentMs { get; private set; }

    /// <summary>The unloaded write, in milliseconds; 0 until a write followed an idle wait.</summary>
    public double UnloadedMs { get; private set; }

    /// <summary>
    /// One frame written: <paramref name="writeMs"/> the write alone, <paramref name="frameMs"/> the thread's time on it
    /// (the write, and the encode on a JPEG stream), <paramref name="idleMs"/> how long the thread waited for the frame
    /// since its last write returned, <paramref name="end"/> the timestamp the write returned at.
    /// </summary>
    public void Record(double writeMs, double frameMs, double idleMs, long end, TimeProvider time)
    {
        var interval = _lastEnd >= 0 ? time.GetElapsedTime(_lastEnd, end).TotalMilliseconds : double.NaN;
        var idle = _lastEnd < 0 || idleMs >= IdleMs;
        if (idle)
        {
            _idle[_idleCount++ % Kept] = writeMs;
            UnloadedMs = _idle.Take(Math.Min(_idleCount, Kept)).Min();
        }
        _lastEnd = end;
        if (_window.Count < MaxWindow)
            _window.Add((writeMs, frameMs, interval, idle));
        RecentMs = writeMs;
    }

    /// <summary>
    /// The check, once a second: the new pace from the writes since the last one. <paramref name="frameTimeMs"/>: the mean
    /// frame time (the thread's time a frame) of the last writes; <paramref name="current"/>: what the sink asks for now
    /// (the lower of the frame rate setting and the pace).
    /// </summary>
    public int Evaluate(double frameTimeMs, int current)
    {
        var own = frameTimeMs > 0 ? (int)Math.Min(60, Math.Floor(Headroom * 1000 / frameTimeMs)) : 60;
        if (_window.Count > 0)
            RecentMs = Median(_window.Select(w => w.WriteMs));
        var limit = LongRatio * UnloadedMs + SlackMs;
        var held = HeldBack(limit);
        _heldChecks = ((_heldChecks << 1) | (held != null ? 1 : 0)) & ((1 << Within) - 1);
        var idleMs = _window.Any(w => w.Idle) ? Median(_window.Where(w => w.Idle).Select(w => w.WriteMs)) : double.PositiveInfinity;
        _window.Clear();

        if (held is { } rate && BitOperations.PopCount((uint)_heldChecks) >= Sustained && rate < current)
        {
            // The device's buffer is full, or the frames are large: under what it took, after a check at half of it.
            Learned = Math.Max(1, (int)Math.Floor(Headroom * rate));
            if (_rose)
                _keptUp = Learned.Value; // lowered again after rising: it stays there
            _rose = false;
            _heldChecks = _quiet = _steady = 0;
            return Paced = Math.Max(1, (int)Math.Floor(Math.Min(current, rate) / 2));
        }

        if (held == null && RecentMs <= limit)
        {
            _quiet++;
            _steady = current == _steadyAt ? _steady + 1 : 1;
            _steadyAt = current;
            if (_steady >= KeptUpAfter && current >= _keptUp && double.IsFinite(idleMs))
                (_keptUp, _keptUpMs) = (current, current > _keptUp ? idleMs : Math.Max(_keptUpMs, idleMs));
            var top = Math.Min(Math.Min(own, _keptUp), ceiling ?? 60);
            if (Learned is { } learned && learned < top && _quiet >= QuietChecks && double.IsFinite(_keptUpMs)
                && idleMs <= LongRatio * _keptUpMs + SlackMs)
            {
                Learned = learned + 1;
                _rose = true;
            }
        }
        else
            _quiet = _steady = 0;

        return Paced = Math.Clamp(Math.Min(own, Learned ?? ceiling ?? 60), 1, 60);
    }

    /// <summary>The writes a second of this window when they were held back by the device; null when they weren't.</summary>
    private double? HeldBack(double limit)
    {
        if (_window.Count < MinWindow || UnloadedMs <= 0 || RecentMs <= limit)
            return null;
        double busy = 0, elapsed = 0;
        var n = 0;
        foreach (var w in _window)
        {
            if (double.IsNaN(w.IntervalMs) || w.IntervalMs <= 0)
                continue;
            busy += w.FrameMs;
            elapsed += w.IntervalMs;
            n++;
        }
        if (n == 0 || busy < BusyShare * elapsed)
            return null; // long writes with the thread idle between them: larger frames, not a full device
        return 1000 * n / elapsed;
    }

    private static double Median(IEnumerable<double> items)
    {
        var values = items.ToArray();
        Array.Sort(values);
        var mid = values.Length / 2;
        return values.Length % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
