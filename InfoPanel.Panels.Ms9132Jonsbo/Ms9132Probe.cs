using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Ms9132Jonsbo;

/// <summary>
/// What an MS9132's probe came to: a status other than <see cref="PanelDeviceStatus.Ok"/> when it couldn't ask now (the
/// device gone, busy, its time run out); else whether one of its HID collections answered, and the EDID that answer
/// gave (null: it answered without one that reads), with <see cref="Message"/> saying what it read.
/// </summary>
internal sealed record Ms9132ProbeResult(PanelDeviceStatus Status, bool Answered, Edid? Edid, string Message);

/// <summary>
/// The MS9132's probe, shared by both families on <c>345f:9132</c>: it reads the EDID the bridge holds of its panel, the
/// way 1.4's startup read it (32 reads of 4 bytes, <c>B5 C0 00</c> to <c>B5 C0 7C</c>, JonsboMs9132Device.cs:211-229).
/// For each HID collection in turn (1.4 tried each, JonsboMs9132Device.cs:150-173): first the EDID reads alone. A
/// collection that refuses a read, doesn't answer it in its whole wait, or answers with what isn't the bridge's reply
/// (short, or not echoing the read's opcode and address) didn't answer, and is sent nothing more (no command that changes
/// a device goes to one that didn't answer); when none answered, the device isn't one of the families'. One whose 32
/// replies were the bridge's, without a block that reads, gets 1.4's whole startup block (the power-up, registers and the
/// same reads), kept to the budget less <see cref="PowerOffReserveMs"/>, then 1.4's power-off within that reserve, its
/// pause kept whole, as its open and dispose sent them. In the block the EDID and status reads must echo as the first 32
/// did; a write's reply need only be the whole report, as 1.4 never read it. That bridge answered: a failure after it (of
/// the block, out of its time, or of the power-off) answers a status, asked again on a later pass, never "not mine"; and
/// the power-off is sent after a failed block too, unless the device went (after a report that timed out the engine has
/// closed the collection's stream, so it is attempted but doesn't reach the panel). The whole probe keeps to <see cref="Budget"/>, its clock read once, inside the
/// engine's probe time: each call is given what is left, at most 1.4's 1000 ms, and a call cut short by it answers
/// <see cref="PanelDeviceStatus.Timeout"/> (asked again on a later pass), never "unanswered". A cancel throws, with what
/// it opened let go and nothing more sent.
/// </summary>
internal static class Ms9132Probe
{
    /// <summary>The probe's whole time, inside the engine's 5 s with room for the dispose and the answer's way back.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The time kept back from the startup block for the power-off after it: its set's whole wait and its whole pause; its
    /// get takes what is left after the pause (a bridge that answers answers in milliseconds), and a power-off whose pause
    /// no longer fits answers <see cref="PanelDeviceStatus.Timeout"/> (asked again on a later pass).
    /// </summary>
    public const int PowerOffReserveMs = Ms9132Protocol.HidTimeoutMs + 105;

    private enum Asked { Answered, Unanswered, Stopped }

    public static Ms9132ProbeResult Read(IPanelDevices devices, PanelCandidate device, TimeProvider time, Action<TimeSpan, CancellationToken> wait, CancellationToken stop)
    {
        var left = Ms9132Link.Left(time, time.GetTimestamp(), Budget);
        var status = devices.ListHid(device, new PanelHidFilter(), out var collections);
        if (status != PanelDeviceStatus.Ok)
            return new(status, false, null, "its HID collections couldn't be listed");
        if (collections.Count == 0)
            return new(PanelDeviceStatus.Ok, false, null, "it has no HID collection, so no MS9132 control interface");

        PanelDeviceStatus? couldnt = null;
        foreach (var collection in collections)
        {
            var given = Math.Min(Ms9132Protocol.HidTimeoutMs, left());
            if (given < 1)
                return new(PanelDeviceStatus.Timeout, false, null, "the probe's time ran out");
            status = devices.OpenHid(device, collection, given, out var hid, stop);
            if (hid == null)
            {
                if (status == PanelDeviceStatus.Gone)
                    return new(status, false, null, "it went away");
                if (status == PanelDeviceStatus.Timeout && given < Ms9132Protocol.HidTimeoutMs)
                    return new(status, false, null, "the probe's time ran out");
                couldnt ??= status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status; // 1.4 went on to the next one
                continue;
            }
            try
            {
                var link = new Ms9132Link(hid, wait);
                var edid = new byte[Ms9132Protocol.EdidLength];
                // First the EDID reads alone: nothing but reads.
                var asked = Ask(link, Ms9132Protocol.EdidReads, edid, left, stop, out status);
                if (asked == Asked.Stopped)
                    return new(status, false, null, Why(status));
                if (asked == Asked.Unanswered)
                    continue; // it didn't answer a read: it is sent nothing that changes it
                if (Edid.Parse(edid) is { } read)
                    return new(PanelDeviceStatus.Ok, true, read, read.Describe());
                // It answered as the bridge, without an EDID that reads: 1.4's startup block, whose reads come after the
                // power-up, kept to what is left less the power-off's reserve; then its power-off within that reserve.
                int BlockLeft() => Math.Max(0, left() - PowerOffReserveMs);
                if (BlockLeft() < 1)
                    return new(PanelDeviceStatus.Timeout, false, null, "the probe's time ran out"); // nothing sent that changes it
                Array.Clear(edid);
                asked = Ask(link, JonsboMs9132.Startup, edid, BlockLeft, stop, out status);
                if (asked != Asked.Answered)
                {
                    // A bridge that answered failed later: asked again on a later pass, never "not mine". The block may
                    // have powered the panel up: it is powered off again, unless the device went.
                    var later = status switch
                    {
                        PanelDeviceStatus.Ok or PanelDeviceStatus.NotSupported => PanelDeviceStatus.Io,
                        _ => status,
                    };
                    if (later != PanelDeviceStatus.Gone)
                        link.SendWithin(JonsboMs9132.PowerOff, left, JonsboMs9132.DelayMs(JonsboMs9132.PowerOff), stop, wholePause: true);
                    return new(later, false, null, $"its startup block failed after it answered as an MS9132: {Why(later)}");
                }
                var off = link.SendWithin(JonsboMs9132.PowerOff, left, JonsboMs9132.DelayMs(JonsboMs9132.PowerOff), stop, wholePause: true);
                if (off != PanelDeviceStatus.Ok)
                    return new(off == PanelDeviceStatus.NotSupported ? PanelDeviceStatus.Io : off, false, null, $"its power-off after the startup block failed: {Why(off)}");
                var parsed = Edid.Parse(edid);
                return new(PanelDeviceStatus.Ok, true, parsed, parsed?.Describe() ?? $"it answered without an EDID that reads: {Convert.ToHexString(edid)}");
            }
            finally
            {
                hid.Dispose(); // the probe only asked
            }
        }
        if (couldnt is { } failed)
            return new(failed, false, null, "a HID collection couldn't be opened");
        return new(PanelDeviceStatus.Ok, false, null, "no HID collection answered the MS9132's reports");
    }

    // Each report set and got back, then its pause, each within what is left; the EDID reads' bytes kept. Unanswered: a
    // report was refused, its reply wasn't the bridge's answer to it, or didn't come in the whole wait. Stopped: the device went, or the time ran out (a
    // call cut short by it, or none left to start one), or a call failed in a way that says nothing of the device's answer.
    private static Asked Ask(Ms9132Link link, IReadOnlyList<byte[]> reports, byte[] edid, Func<int> left, CancellationToken stop, out PanelDeviceStatus status)
    {
        status = PanelDeviceStatus.Ok;
        foreach (var report in reports)
        {
            var given = Math.Min(Ms9132Protocol.HidTimeoutMs, left());
            if (given < 1)
            {
                status = PanelDeviceStatus.Timeout;
                return Asked.Stopped;
            }
            status = link.Set(report, given, stop);
            if (Outcome(status, given) is { } set)
                return set;
            given = Math.Min(Ms9132Protocol.HidTimeoutMs, left());
            if (given < 1)
            {
                status = PanelDeviceStatus.Timeout;
                return Asked.Stopped;
            }
            // Io too when the reply isn't the bridge's: an identity read's must echo it, any other need only be whole.
            status = link.Get(report, given, stop, echoed: Ms9132Protocol.IsIdentityRead(report));
            if (Outcome(status, given) is { } got)
                return got;
            if (Ms9132Protocol.IsEdidRead(report))
                Ms9132Protocol.TakeEdid(report, link.Reply, edid);
            var pause = Math.Min(JonsboMs9132.DelayMs(report), left());
            if (pause > 0)
                link.Wait(TimeSpan.FromMilliseconds(pause), stop);
        }
        return Asked.Answered;
    }

    private static string Why(PanelDeviceStatus status) => status switch
    {
        PanelDeviceStatus.Gone => "it went away",
        PanelDeviceStatus.Timeout => "no reply in time",
        _ => $"its reports failed ({status})",
    };

    private static Asked? Outcome(PanelDeviceStatus status, int given) => status switch
    {
        PanelDeviceStatus.Ok => null,
        PanelDeviceStatus.Io or PanelDeviceStatus.NotSupported => Asked.Unanswered, // refused
        PanelDeviceStatus.Timeout when given >= Ms9132Protocol.HidTimeoutMs => Asked.Unanswered, // no reply in its whole wait
        _ => Asked.Stopped, // gone, cut short by the budget, busy, access
    };
}
