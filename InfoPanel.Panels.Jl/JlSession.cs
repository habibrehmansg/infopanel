using System.Diagnostics;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Jl;

/// <summary>
/// A JL panel over its opened serial port, as InfoPanel 1.4 drove it (<c>JlSerialDevice</c>, <c>JlPanelDeviceTask</c>): the
/// handshake (200 ms, the pipeline reset, 200 ms, the input dropped, the device info, status 200), the light at the start
/// and whenever it changes, each frame as the keep-alive then the JPEG's envelope and its terminator, a repeated frame as the
/// keep-alive alone, and the stop's close.
/// </summary>
internal sealed class JlSession : IPanelSession
{
    /// <summary>The most frames a second: the top of 1.4's Target FPS slider for these panels (15 was its default).</summary>
    public const int MaxFps = 60;

    /// <summary>1.4's JPEG quality for these panels (its device's default, 80).</summary>
    public const int JpegQuality = 80;

    /// <summary>The floor 1.4 lowered the quality to for a frame over the panel's cap.</summary>
    public const int MinJpegQuality = 30;

    /// <summary>How much lower each try at a frame over the cap went in 1.4.</summary>
    public const int JpegQualityStep = 15;

    /// <summary>A frame's cap when the model doesn't give one: the table's 80 KB.</summary>
    public const int DefaultMaxFrameBytes = 80 * 1024;

    /// <summary>1.4 sent a keep-alive after 1 s without a frame (the firmware wants one within 1.5 s).</summary>
    public static readonly TimeSpan KeepAliveEvery = TimeSpan.FromSeconds(1);

    /// <summary>The most a reply may take before it is given up on: a whole reply and some bytes before it.</summary>
    internal const int MaxReplyBytes = 2 * JlProtocol.MaxReplyLength;

    private readonly IPanelSerial _serial;
    private readonly Action<TimeSpan, CancellationToken> _wait;
    private readonly TimeProvider _time;
    private readonly byte[] _read = new byte[4096];
    private byte[] _envelope = [];
    private PanelCapabilities? _capabilities;
    private int? _brightness;
    private bool _gone;
    private bool _closed;

    public JlSession(IPanelSerial serial, Action<TimeSpan, CancellationToken> wait, TimeProvider time)
    {
        _serial = serial;
        _wait = wait;
        _time = time;
    }

    public PanelCapabilities Capabilities => _capabilities ?? throw new InvalidOperationException("The session hasn't started.");

    /// <summary>No bound but each call's own (a start's).</summary>
    private static int Unbounded() => IPanelDevices.MaxTimeoutMs;

    /// <summary>
    /// 1.4's open after the port, then its device info: 200 ms, the pipeline reset, 200 ms, the input dropped, the query,
    /// and its reply read within 2 s. <see cref="PanelDeviceStatus.Ok"/> with the <paramref name="info"/> when the panel
    /// answered status 200; else the status (<see cref="PanelDeviceStatus.Timeout"/>: no answer;
    /// <see cref="PanelDeviceStatus.Io"/>: one 1.4 wouldn't take) and <paramref name="why"/>.
    /// </summary>
    public PanelDeviceStatus Hello(CancellationToken stop, out JlDeviceInfo? info, out string why)
    {
        info = null;
        _wait(JlProtocol.SettleWait, stop);
        var status = Remember(_serial.Write(JlProtocol.PipelineReset, JlProtocol.WriteTimeoutMs, stop));
        if (status != PanelDeviceStatus.Ok)
        {
            why = $"the panel didn't take the pipeline reset ({status})";
            return status;
        }
        _wait(JlProtocol.SettleWait, stop);
        return Ask(Unbounded, JlProtocol.ReadTimeoutMs, stop, out info, out why, out _);
    }

    /// <summary>
    /// The probe's questions, each within what <paramref name="left"/> answers of its budget: 1.4's 200 ms after the open,
    /// the input dropped and the device info query alone, its answer waited for <paramref name="replyMs"/>; only when
    /// none came in all that time, 1.4's pipeline reset (which flushes a parser left inside a frame), 200 ms, and the query
    /// once more. <see cref="PanelDeviceStatus.Ok"/> with the <paramref name="info"/> when the panel answered status 200;
    /// <see cref="PanelDeviceStatus.Ok"/> without it when neither query was answered in its whole wait (not one of the
    /// family's); else the status that kept it from asking (a call failed, an answer 1.4 wouldn't take, the budget run
    /// out before an answer could come) and <paramref name="why"/>. Nothing is written but the query and the reset.
    /// </summary>
    public PanelDeviceStatus Probe(Func<int> left, int replyMs, CancellationToken stop, out JlDeviceInfo? info, out string why)
    {
        info = null;
        if (!Settle(left, stop, out why))
            return PanelDeviceStatus.Timeout;
        var status = Ask(left, replyMs, stop, out info, out why, out var unanswered);
        if (!unanswered)
            return status;

        var writeMs = Math.Min(JlProtocol.WriteTimeoutMs, left());
        status = writeMs < 1 ? PanelDeviceStatus.Timeout : Remember(_serial.Write(JlProtocol.PipelineReset, writeMs, stop));
        if (status != PanelDeviceStatus.Ok)
        {
            why = $"the panel didn't take the pipeline reset ({status})";
            return status;
        }
        if (!Settle(left, stop, out why))
            return PanelDeviceStatus.Timeout;
        status = Ask(left, replyMs, stop, out info, out why, out unanswered);
        if (!unanswered)
            return status;
        why = "the device answered getDeviceInfo neither alone nor after the pipeline reset";
        return PanelDeviceStatus.Ok;
    }

    /// <summary>1.4's 200 ms pause, when <paramref name="left"/> has room for it; else false, with <paramref name="why"/>.</summary>
    private bool Settle(Func<int> left, CancellationToken stop, out string why)
    {
        if (left() < JlProtocol.SettleWait.TotalMilliseconds)
        {
            why = "the probe's time ran out before the panel could be asked";
            return false;
        }
        _wait(JlProtocol.SettleWait, stop);
        why = "";
        return true;
    }

    /// <summary>
    /// The device info: the input dropped, the query, its answer read within <paramref name="replyMs"/>; each call given
    /// no more than <paramref name="left"/> answers. <paramref name="unanswered"/>: no answer came in the whole
    /// <paramref name="replyMs"/> (an answer cut short by <paramref name="left"/> is a <see cref="PanelDeviceStatus.Timeout"/>
    /// that isn't).
    /// </summary>
    private PanelDeviceStatus Ask(Func<int> left, int replyMs, CancellationToken stop, out JlDeviceInfo? info, out string why, out bool unanswered)
    {
        info = null;
        unanswered = false;
        var status = Remember(_serial.DiscardInput());
        if (status != PanelDeviceStatus.Ok)
        {
            why = $"the port's input couldn't be dropped ({status})";
            return status;
        }
        var writeMs = Math.Min(JlProtocol.WriteTimeoutMs, left());
        status = writeMs < 1 ? PanelDeviceStatus.Timeout : Remember(_serial.Write(JlProtocol.DeviceInfoQuery, writeMs, stop));
        if (status != PanelDeviceStatus.Ok)
        {
            why = $"the panel didn't take getDeviceInfo ({status})";
            return status;
        }
        var readMs = Math.Min(replyMs, left());
        status = ReadReply(readMs, stop, out var payload);
        if (status != PanelDeviceStatus.Ok)
        {
            unanswered = status == PanelDeviceStatus.Timeout && readMs >= replyMs;
            why = status != PanelDeviceStatus.Timeout ? $"the panel's answer to getDeviceInfo couldn't be read ({status})"
                : unanswered ? "the panel didn't answer getDeviceInfo"
                : "the probe's time ran out before the panel could answer getDeviceInfo";
            return status;
        }
        info = JlProtocol.ParseDeviceInfo(payload);
        if (info == null)
        {
            why = "the panel's answer to getDeviceInfo isn't its JSON";
            return PanelDeviceStatus.Io;
        }
        if (info.Status != JlProtocol.OkStatus)
        {
            why = $"the panel answered getDeviceInfo with status {info.Status}";
            return PanelDeviceStatus.Io;
        }
        why = "";
        return PanelDeviceStatus.Ok;
    }

    /// <summary>
    /// One reply, as 1.4's <c>ReadFrame</c> read it, within <paramref name="windowMs"/> (1.4's 2 s at a start): what comes
    /// is read until it holds a whole reply (<see cref="PanelDeviceStatus.Ok"/>, its <paramref name="payload"/>), one 1.4
    /// would refuse (<see cref="PanelDeviceStatus.Io"/>), or nothing more comes in time (<see cref="PanelDeviceStatus.Timeout"/>).
    /// No more than <see cref="MaxReplyBytes"/> is read or held: a reply not whole within them is refused.
    /// </summary>
    private PanelDeviceStatus ReadReply(int windowMs, CancellationToken stop, out byte[]? payload)
    {
        payload = null;
        var received = new byte[_read.Length];
        var count = 0;
        var started = _time.GetTimestamp();
        while (true)
        {
            var room = MaxReplyBytes - count;
            if (room < 1)
                return PanelDeviceStatus.Io;
            var left = windowMs - (int)_time.GetElapsedTime(started).TotalMilliseconds;
            if (left < 1)
                return PanelDeviceStatus.Timeout;
            var status = Remember(_serial.Read(_read.AsSpan(0, Math.Min(_read.Length, room)), left, out var read, stop));
            if (status != PanelDeviceStatus.Ok)
                return status;
            if (count + read > received.Length)
                Array.Resize(ref received, Math.Min(MaxReplyBytes, Math.Max(received.Length * 2, count + read)));
            _read.AsSpan(0, read).CopyTo(received.AsSpan(count));
            count += read;
            switch (JlProtocol.ParseReply(received.AsSpan(0, count), out payload))
            {
                case JlReply.Complete:
                    return PanelDeviceStatus.Ok;
                case JlReply.Invalid:
                    return PanelDeviceStatus.Io;
            }
        }
    }

    /// <summary>
    /// The session's capabilities at <paramref name="width"/> × <paramref name="height"/> with the model's
    /// <paramref name="maxFrameBytes"/>, then the light at <paramref name="brightness"/>, as 1.4's start pushed it.
    /// </summary>
    public PanelDeviceStatus Begin(int width, int height, int maxFrameBytes, int brightness, CancellationToken stop, out string why)
    {
        _capabilities = new PanelCapabilities(width, height, [PanelFrameFormat.Jpeg], MaxFps, MaxFrameBytes: maxFrameBytes,
            JpegQuality: JpegQuality, MinJpegQuality: MinJpegQuality, Brightness: new PanelRange(0, 100), KeepAliveEvery: KeepAliveEvery)
        {
            // 1.4 dimmed the frame below 100 as well as setting the light, "to preserve the look of dim profiles below
            // the backlight floor" (JlPanelDeviceTask.cs:231-236).
            DimInSoftware = true,
            JpegQualityStep = JpegQualityStep,
        };
        _brightness = brightness;
        var status = Remember(_serial.Write(JlProtocol.Light(brightness), JlProtocol.WriteTimeoutMs, stop));
        why = status == PanelDeviceStatus.Ok ? "" : $"the panel didn't take its light ({status})";
        return status;
    }

    private PanelDeviceStatus Remember(PanelDeviceStatus status)
    {
        if (status == PanelDeviceStatus.Gone)
            _gone = true;
        return status;
    }

    /// <summary>
    /// A new frame: the keep-alive, the JPEG's envelope, the terminator. A repeated one (the engine's keep-alive): the
    /// keep-alive alone, as 1.4 sent after 1 s without a frame.
    /// </summary>
    public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
    {
        if (frame.Repeat)
        {
            var at = Stopwatch.GetTimestamp();
            var alive = Remember(_serial.Write(JlProtocol.KeepAliveFrame, JlProtocol.WriteTimeoutMs, stop));
            return new(alive, Stopwatch.GetElapsedTime(at));
        }

        var length = JlProtocol.EnvelopeLength(frame.Bytes.Length);
        if (_envelope.Length < length)
            _envelope = new byte[length];
        JlProtocol.Envelope(frame.Bytes, _envelope);
        var started = Stopwatch.GetTimestamp();
        var status = Remember(_serial.Write(JlProtocol.KeepAliveFrame, JlProtocol.WriteTimeoutMs, stop));
        if (status == PanelDeviceStatus.Ok)
            status = Remember(_serial.Write(_envelope.AsSpan(0, length), JlProtocol.WriteTimeoutMs, stop));
        if (status == PanelDeviceStatus.Ok)
            status = Remember(_serial.Write(JlProtocol.FrameTerminator, JlProtocol.WriteTimeoutMs, stop));
        return new(status, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>The light, 0–100, sent only when it differs from the last sent, as 1.4's send loop did.</summary>
    public PanelDeviceStatus SetBrightness(int percent)
    {
        if (percent == _brightness)
            return PanelDeviceStatus.Ok;
        _brightness = percent;
        return Remember(_serial.Write(JlProtocol.Light(percent), JlProtocol.WriteTimeoutMs));
    }

    /// <summary>
    /// With <paramref name="screenOff"/>, close (<c>0x21</c>), as 1.4's dispose of the device sent it, within what is left
    /// of <paramref name="budget"/> and not once the device is gone; then the port let go.
    /// </summary>
    public void Close(bool screenOff, TimeSpan budget)
    {
        if (_closed)
            return;
        _closed = true;
        try
        {
            var left = (int)Math.Clamp(Math.Floor(budget.TotalMilliseconds), 0, IPanelDevices.MaxTimeoutMs);
            if (screenOff && !_gone && left >= 1)
                _serial.Write(JlProtocol.CloseFrame, Math.Min(JlProtocol.WriteTimeoutMs, left));
        }
        finally
        {
            _serial.Dispose();
        }
    }

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        _serial.Dispose();
    }
}
