using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Ms9132Jonsbo;

/// <summary>
/// The MS9132's control plane over its opened HID collection: one report at a time as 1.4 sent each (set, then got back
/// into a zeroed 9-byte buffer, whatever the reply's use; JonsboMs9132Device.cs:272-287, VmaxUsbDevice.cs:258-270), and
/// the pause after it, through <see cref="Wait"/>. A reply must be the whole report; only the probe's identity reads
/// must echo theirs (<see cref="Get"/>).
/// </summary>
internal sealed class Ms9132Link(IPanelHid hid, Action<TimeSpan, CancellationToken> wait)
{
    private readonly byte[] _reply = new byte[Ms9132Protocol.FeatureLength];

    public IPanelHid Hid => hid;

    /// <summary>The last reply got: the report id, the echoed opcode and address, then the data (<see cref="Ms9132Protocol.ReplyData"/>).</summary>
    public ReadOnlySpan<byte> Reply => _reply;

    public Action<TimeSpan, CancellationToken> Wait => wait;

    /// <summary>Sets <paramref name="payload"/> as a feature report within <paramref name="timeoutMs"/>.</summary>
    public PanelDeviceStatus Set(ReadOnlySpan<byte> payload, int timeoutMs, CancellationToken stop) =>
        hid.SetFeature(Ms9132Protocol.Feature(payload), timeoutMs, stop);

    /// <summary>
    /// Gets the reply to <paramref name="payload"/> (report id 0) within <paramref name="timeoutMs"/> into
    /// <see cref="Reply"/>, zeroed first. A reply that isn't the whole report isn't the bridge's answer:
    /// <see cref="PanelDeviceStatus.Io"/>. With <paramref name="echoed"/> (the probe's identity reads) it must also echo
    /// the payload's opcode and address (JonsboMs9132Device.cs:285); any other reply is taken as 1.4 took it, unread.
    /// </summary>
    public PanelDeviceStatus Get(ReadOnlySpan<byte> payload, int timeoutMs, CancellationToken stop, bool echoed = false)
    {
        Array.Clear(_reply);
        var status = hid.GetFeature(_reply, timeoutMs, out var read, stop);
        if (status == PanelDeviceStatus.Ok && !(echoed ? Ms9132Protocol.Answers(payload, _reply, read) : Ms9132Protocol.IsWhole(read)))
            return PanelDeviceStatus.Io;
        return status;
    }

    /// <summary>
    /// One report: set, got back, then the pause of <paramref name="delayMs"/> (none when it is 0); the first status that
    /// isn't <see cref="PanelDeviceStatus.Ok"/> ends it there, no pause after.
    /// </summary>
    public PanelDeviceStatus Send(ReadOnlySpan<byte> payload, int timeoutMs, int delayMs, CancellationToken stop)
    {
        var status = Set(payload, timeoutMs, stop);
        if (status != PanelDeviceStatus.Ok)
            return status;
        status = Get(payload, timeoutMs, stop);
        if (status != PanelDeviceStatus.Ok)
            return status;
        if (delayMs > 0)
            wait(TimeSpan.FromMilliseconds(delayMs), stop);
        return PanelDeviceStatus.Ok;
    }

    /// <summary>
    /// One report within what <paramref name="left"/> answers at each step (its set, its get and its pause, none started
    /// with nothing left), on <paramref name="stop"/> (none: a close's). With <paramref name="wholePause"/> (a power-off,
    /// whose pause lets the panel settle before it is next powered up) the pause is kept whole: the get is given what is
    /// left less the pause, and when that leaves the get nothing, or the get leaves less than the pause, the report
    /// answers <see cref="PanelDeviceStatus.Timeout"/> and no part of the pause is waited.
    /// </summary>
    public PanelDeviceStatus SendWithin(ReadOnlySpan<byte> payload, Func<int> left, int delayMs, CancellationToken stop = default, bool wholePause = false)
    {
        var ms = left();
        if (ms < 1)
            return PanelDeviceStatus.Timeout;
        var status = Set(payload, Math.Min(Ms9132Protocol.HidTimeoutMs, ms), stop);
        if (status != PanelDeviceStatus.Ok)
            return status;
        var kept = wholePause ? Math.Max(0, delayMs) : 0;
        if ((ms = left() - kept) < 1)
            return PanelDeviceStatus.Timeout;
        status = Get(payload, Math.Min(Ms9132Protocol.HidTimeoutMs, ms), stop);
        if (status != PanelDeviceStatus.Ok)
            return status;
        if (delayMs < 1)
            return PanelDeviceStatus.Ok;
        ms = left();
        if (wholePause && ms < delayMs)
            return PanelDeviceStatus.Timeout;
        if (ms >= 1)
            wait(TimeSpan.FromMilliseconds(Math.Min(delayMs, ms)), stop);
        return PanelDeviceStatus.Ok;
    }

    /// <summary>A pause of <paramref name="time"/> that ends early, throwing, when <paramref name="stop"/> is cancelled.</summary>
    public static void Pause(TimeSpan time, CancellationToken stop)
    {
        if (stop.CanBeCanceled)
            stop.WaitHandle.WaitOne(time);
        else
            Thread.Sleep(time);
        stop.ThrowIfCancellationRequested();
    }

    /// <summary>A clock's "what is left of <paramref name="budget"/>", in whole milliseconds from 0 to the seam's longest timeout.</summary>
    public static Func<int> Left(TimeProvider time, long started, TimeSpan budget) =>
        () => (int)Math.Clamp(Math.Floor((budget - time.GetElapsedTime(started)).TotalMilliseconds), 0, IPanelDevices.MaxTimeoutMs);
}
