using System.Diagnostics;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Thermaltake;

/// <summary>
/// A running Thermaltake or ASRock panel over its opened HID collection: the handshake, JPEG frames in 1000-byte chunks,
/// and the stop that turns realtime display off. Each command takes the next <c>SeqNumber</c> from 100, as 1.4's device
/// did (a new one each start).
/// </summary>
internal sealed class ThermaltakeSession : IPanelSession
{
    /// <summary>1.4 sent frames at most at its Target FPS slider's top, 30 (15 by default).</summary>
    public const int MaxFps = 30;

    /// <summary>1.4's JPEG quality for these panels (its device's default, 85).</summary>
    public const int JpegQuality = 85;

    private readonly IPanelHid _hid;
    private readonly Func<long> _now;
    private readonly Action<TimeSpan, CancellationToken> _wait;
    private readonly byte[] _report = new byte[ThermaltakeProtocol.ReportSize];
    private readonly byte[] _reply;
    private int _seqNumber = ThermaltakeProtocol.FirstSeqNumber;
    private bool _gone;
    private bool _closed;

    public ThermaltakeSession(IPanelHid hid, int width, int height, Func<long> now, Action<TimeSpan, CancellationToken> wait)
    {
        _hid = hid;
        _now = now;
        _wait = wait;
        _reply = new byte[Math.Max(ThermaltakeProtocol.ReportSize, hid.MaxInputReportLength)];
        Capabilities = new PanelCapabilities(width, height, [PanelFrameFormat.Jpeg], MaxFps, JpegQuality: JpegQuality) { DimInSoftware = true };
    }

    public PanelCapabilities Capabilities { get; }

    /// <summary>
    /// <c>POST conn 1</c>, then <c>POST realtimeDisplay 1</c> on, each answered <c>200</c>: <see cref="PanelDeviceStatus.Ok"/>,
    /// else the status and <paramref name="why"/>.
    /// </summary>
    public PanelDeviceStatus Handshake(CancellationToken stop, out string why)
    {
        var status = Exchange(ThermaltakeProtocol.Conn(_seqNumber++, _now()), ThermaltakeProtocol.WriteTimeoutMs, ThermaltakeProtocol.ReadTimeoutMs, stop, out var answer);
        if (status != PanelDeviceStatus.Ok || !ThermaltakeProtocol.Accepted(answer))
            return Refused("conn", status, answer, out why);
        status = Exchange(ThermaltakeProtocol.RealtimeDisplay(true, _seqNumber++, _now()), ThermaltakeProtocol.WriteTimeoutMs, ThermaltakeProtocol.ReadTimeoutMs, stop, out answer);
        if (status != PanelDeviceStatus.Ok || !ThermaltakeProtocol.Accepted(answer))
            return Refused("realtimeDisplay", status, answer, out why);
        why = "";
        return PanelDeviceStatus.Ok;
    }

    private static PanelDeviceStatus Refused(string command, PanelDeviceStatus status, string? answer, out string why)
    {
        if (status != PanelDeviceStatus.Ok)
        {
            why = $"the panel didn't answer {command} ({status})";
            return status;
        }
        why = answer == null ? $"the panel's answer to {command} had no text" : $"the panel answered {command} without 200: {Clip(answer)}";
        return answer == null ? PanelDeviceStatus.Timeout : PanelDeviceStatus.Io;
    }

    private static string Clip(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 64 ? line : line[..64] + "…";
    }

    /// <summary>
    /// A command and its reply, as 1.4 sent one: the report written, a pause of 50 ms, one input report read; the reply's
    /// text in <paramref name="answer"/> (null: none, or one with no text). <see cref="PanelDeviceStatus.Ok"/> once the
    /// write went, a read that timed out included (1.4 took it as no answer); the write's or the read's other status.
    /// </summary>
    private PanelDeviceStatus Exchange(byte[] packet, int writeTimeoutMs, int readTimeoutMs, CancellationToken stop, out string? answer)
    {
        answer = null;
        var status = Remember(_hid.Write(ThermaltakeProtocol.Report(packet), writeTimeoutMs, stop));
        if (status != PanelDeviceStatus.Ok)
            return status;
        _wait(ThermaltakeProtocol.ReplyWait, stop);
        status = Remember(_hid.Read(_reply, readTimeoutMs, out var read, stop));
        if (status == PanelDeviceStatus.Ok)
            answer = ThermaltakeProtocol.ResponseText(_reply, read);
        return status == PanelDeviceStatus.Timeout ? PanelDeviceStatus.Ok : status;
    }

    private PanelDeviceStatus Remember(PanelDeviceStatus status)
    {
        if (status == PanelDeviceStatus.Gone)
            _gone = true;
        return status;
    }

    public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
    {
        var jpeg = frame.Bytes;
        var total = ThermaltakeProtocol.ChunkCount(jpeg.Length);
        if (total > ThermaltakeProtocol.MaxChunks)
            return new(PanelDeviceStatus.NotSupported, TimeSpan.Zero); // its chunk count wouldn't fit its 16 bits: never sent
        long started = 0;
        for (var chunk = 0; chunk < total; chunk++)
        {
            ThermaltakeProtocol.ImageReport(jpeg, chunk, total, _report);
            if (chunk == 0)
                started = Stopwatch.GetTimestamp();
            var status = Remember(_hid.Write(_report, ThermaltakeProtocol.WriteTimeoutMs, stop));
            if (status != PanelDeviceStatus.Ok)
                return new(status, Stopwatch.GetElapsedTime(started));
        }
        return new(PanelDeviceStatus.Ok, total == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(started));
    }

    /// <summary>Nothing to send: the engine dims the frame (<see cref="PanelCapabilities.DimInSoftware"/>), as 1.4 did.</summary>
    public PanelDeviceStatus SetBrightness(int percent) => PanelDeviceStatus.Ok;

    /// <summary>
    /// With <paramref name="screenOff"/>, <c>POST realtimeDisplay 1</c> off and its reply read, as 1.4's dispose of the
    /// device did, each call within what is left of <paramref name="budget"/> and none once the device is gone; then the
    /// collection let go.
    /// </summary>
    public void Close(bool screenOff, TimeSpan budget)
    {
        if (_closed)
            return;
        _closed = true;
        try
        {
            if (screenOff && !_gone)
                ScreenOff(budget);
        }
        finally
        {
            _hid.Dispose();
        }
    }

    private void ScreenOff(TimeSpan budget)
    {
        var clock = Stopwatch.StartNew();
        int Left() => (int)Math.Clamp(Math.Floor((budget - clock.Elapsed).TotalMilliseconds), 0, IPanelDevices.MaxTimeoutMs);

        var left = Left();
        if (left < 1)
            return;
        var packet = ThermaltakeProtocol.RealtimeDisplay(false, _seqNumber++, _now());
        if (Remember(_hid.Write(ThermaltakeProtocol.Report(packet), Math.Min(ThermaltakeProtocol.WriteTimeoutMs, left))) != PanelDeviceStatus.Ok)
            return;
        left = Left();
        if (left < 1)
            return;
        _wait(TimeSpan.FromMilliseconds(Math.Min(ThermaltakeProtocol.ReplyWait.TotalMilliseconds, left)), CancellationToken.None);
        left = Left();
        if (left < 1)
            return;
        Remember(_hid.Read(_reply, Math.Min(ThermaltakeProtocol.ReadTimeoutMs, left), out _));
    }

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        _hid.Dispose();
    }
}
