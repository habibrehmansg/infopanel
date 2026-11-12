using System.Diagnostics;
using System.Text.Json;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Ms9132Jonsbo;

/// <summary>
/// A running Jonsbo DS916 on its USB CDC serial port, as 1.4 drove it (JonsboSerialDevice.cs and
/// JonsboPanelDeviceTask.cs:28-291): the port opened at the nominal 9600 baud with DTR and RTS raised, the handshake asked
/// (again after 1 s of silence), then one raw JPEG a frame at the size the panel reported, at quality 80, 15 lower each
/// try down to 30 to fit 256 KiB; the last frame written again after 1 s without a new one; the engine dims the frame.
/// The firmware knows no stop command: the stop lets the port go and the panel returns to its own animation, as in 1.4.
/// </summary>
internal sealed class Ds916Session : IPanelSession
{
    /// <summary>1.4's frame-rate slider's top for a Jonsbo panel (Views/Pages/UsbPanelsPage.xaml's 60).</summary>
    public const int MaxFps = 60;

    /// <summary>How long the port's open may take.</summary>
    internal const int OpenTimeoutMs = 5000;

    /// <summary>The probe's whole time, inside the engine's 5 s with room for the dispose and the answer's way back.</summary>
    internal static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(4);

    /// <summary>How long the probe waits for a reply's first byte (1.4's start waited 2 s: two of those and its 1 s gap wouldn't fit the engine's probe time).</summary>
    internal const int ProbeReplyMs = 1000;

    /// <summary>The port as 1.4 opened it (JonsboSerialDevice.cs:53-60).</summary>
    internal static readonly PanelSerialOptions Options = new(Ds916Protocol.Baud, Dtr: true, Rts: true);

    private readonly IPanelSerial _serial;
    private readonly TimeSpan _writeBudget;
    private bool _gone;
    private bool _closed;

    private Ds916Session(IPanelSerial serial, int width, int height, int maxFrameBytes, TimeSpan writeBudget)
    {
        _serial = serial;
        _writeBudget = writeBudget;
        Capabilities = new PanelCapabilities(width, height, [PanelFrameFormat.Jpeg], MaxFps, MaxFrameBytes: maxFrameBytes,
            JpegQuality: Ds916Protocol.JpegQuality, MinJpegQuality: Ds916Protocol.MinJpegQuality, KeepAliveEvery: TimeSpan.FromSeconds(1))
        {
            DimInSoftware = true,
            JpegQualityStep = Ds916Protocol.JpegQualityStep,
        };
    }

    public PanelCapabilities Capabilities { get; }

    private enum Asked { Answered, Unanswered, Stopped }

    /// <summary>
    /// The DS916's probe, within <see cref="ProbeBudget"/> on one clock: the port opened, the handshake asked; silent or
    /// answered with what doesn't read, asked again after 1.4's 1 s; still not, the device isn't a DS916 ("not mine", only
    /// after each reply's whole wait). A wait cut short by the budget answers <see cref="PanelDeviceStatus.Timeout"/>; a
    /// cancel throws, the port let go, nothing more sent. The model is the USB id's; its size and serial the reply's.
    /// </summary>
    public static PanelProbe Probe(IPanelDevices devices, PanelCandidate device, PanelManifestModel model, TimeProvider time,
        Action<TimeSpan, CancellationToken> wait, CancellationToken stop)
    {
        var left = Ms9132Link.Left(time, time.GetTimestamp(), ProbeBudget);
        var given = Math.Min(OpenTimeoutMs, left());
        if (given < 1)
            return new(false, null, PanelDeviceStatus.Timeout, "the probe's time ran out");
        var status = devices.OpenSerial(device, Options, given, out var serial, stop);
        if (serial == null)
            return new(false, null, status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status, "the panel's serial port couldn't be opened");
        try
        {
            if ((status = serial.DiscardInput()) != PanelDeviceStatus.Ok)
                return new(false, null, status, "the panel's serial port failed");
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                if (attempt == 2)
                {
                    if (left() < Ds916Protocol.RetryGap.TotalMilliseconds + ProbeReplyMs)
                        return new(false, null, PanelDeviceStatus.Timeout, "the probe's time ran out");
                    wait(Ds916Protocol.RetryGap, stop);
                }
                var asked = Ask(serial, ProbeReplyMs, left, time, stop, out status, out var identity);
                if (asked == Asked.Stopped)
                    return new(false, null, status, status == PanelDeviceStatus.Timeout ? "the probe's time ran out" : $"the handshake failed ({status})");
                if (asked == Asked.Answered)
                    return new(true, Identify(identity!, model), PanelDeviceStatus.Ok, identity!.Raw);
            }
            return new(false, null, PanelDeviceStatus.Ok, "it didn't answer the DS916's handshake, asked twice");
        }
        finally
        {
            serial.Dispose(); // the probe only asked
        }
    }

    private static PanelIdentity Identify(Ds916Identity identity, PanelManifestModel model)
    {
        // 1.4 trusted the size the panel reports over its table's (JonsboPanelDeviceTask.cs:87-92).
        var (width, height) = identity.Width > 0 && identity.Height > 0 ? (identity.Width, identity.Height) : (model.Width, model.Height);
        return new PanelIdentity(model.Id, width, height, string.IsNullOrWhiteSpace(identity.Serial) ? null : identity.Serial, Firmware: null);
    }

    /// <summary>
    /// 1.4's start (JonsboPanelDeviceTask.cs:56-97): the port opened (its input dropped, JonsboSerialDevice.cs:63), the
    /// handshake asked, again after 1 s of silence; the session at the size the panel reported.
    /// </summary>
    public static PanelOpen Start(IPanelDevices devices, PanelCandidate device, PanelManifestModel model, TimeProvider time,
        Action<TimeSpan, CancellationToken> wait, TimeSpan writeBudget, CancellationToken stop)
    {
        var status = devices.OpenSerial(device, Options, OpenTimeoutMs, out var serial, stop);
        if (serial == null)
            return new(null, status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status, "the panel's serial port couldn't be opened");
        try
        {
            if ((status = serial.DiscardInput()) != PanelDeviceStatus.Ok)
            {
                serial.Dispose();
                return new(null, status, "the panel's serial port failed");
            }
            Ds916Identity? identity = null;
            for (var attempt = 1; attempt <= 2 && identity == null; attempt++)
            {
                if (attempt == 2)
                    wait(Ds916Protocol.RetryGap, stop);
                var asked = Ask(serial, Ds916Protocol.ReadTimeoutMs, () => IPanelDevices.MaxTimeoutMs, time, stop, out status, out identity);
                if (asked == Asked.Stopped)
                {
                    serial.Dispose();
                    return new(null, status, $"the handshake failed ({status})");
                }
            }
            if (identity == null)
            {
                // 1.4 let the port go after no identity ("No identity reply"): the panel was sent nothing more.
                serial.Dispose();
                return new(null, PanelDeviceStatus.Timeout, "the panel didn't answer its handshake with a DS916's identity");
            }
            var size = Identify(identity, model);
            return new(new Ds916Session(serial, size.Width, size.Height, MaxFrameBytesOf(model), writeBudget), PanelDeviceStatus.Ok, "");
        }
        catch (OperationCanceledException)
        {
            serial.Dispose();
            throw;
        }
    }

    /// <summary>A model's JPEG cap: its <c>maxJpegBytes</c>, else 1.4's 256 KiB.</summary>
    private static int MaxFrameBytesOf(PanelManifestModel model) =>
        model.Json.TryGetProperty("maxJpegBytes", out var cap) && cap.ValueKind == JsonValueKind.Number && cap.TryGetInt32(out var bytes) && bytes > 0
            ? bytes : Ds916Protocol.MaxJpegBytes;

    /// <summary>
    /// The handshake asked once (JonsboSerialDevice.cs:80-136): the input dropped, <c>F0 A5 5A 0F</c> written, the reply's
    /// first byte waited for up to <paramref name="replyMs"/>, the rest gathered for 300 ms, at most 64 bytes, each wait
    /// within what <paramref name="left"/> answers. Answered: it reads as an identity. Unanswered: nothing came in the
    /// whole wait, or what came doesn't read. Stopped: a call failed, or a wait was cut short by <paramref name="left"/>.
    /// </summary>
    private static Asked Ask(IPanelSerial serial, int replyMs, Func<int> left, TimeProvider time, CancellationToken stop,
        out PanelDeviceStatus status, out Ds916Identity? identity)
    {
        identity = null;
        if ((status = serial.DiscardInput()) != PanelDeviceStatus.Ok)
            return Asked.Stopped;
        var given = Math.Min(Ds916Protocol.WriteTimeoutMs, left());
        if (given < 1)
        {
            status = PanelDeviceStatus.Timeout;
            return Asked.Stopped;
        }
        if ((status = serial.Write(Ds916Protocol.Handshake, given, stop)) != PanelDeviceStatus.Ok)
            return Asked.Stopped;

        var reply = new byte[Ds916Protocol.ReplyCap];
        given = Math.Min(replyMs, left());
        if (given < 1)
        {
            status = PanelDeviceStatus.Timeout;
            return Asked.Stopped;
        }
        status = serial.Read(reply, given, out var total, stop);
        if (status == PanelDeviceStatus.Timeout)
        {
            if (given < replyMs)
                return Asked.Stopped; // cut short by the budget: not an answer either way
            status = PanelDeviceStatus.Ok;
            return Asked.Unanswered;
        }
        if (status != PanelDeviceStatus.Ok)
            return Asked.Stopped;

        // The rest of the reply, for 300 ms after its first byte or until 64 bytes have come.
        var gathering = time.GetTimestamp();
        var cut = false;
        while (total < reply.Length)
        {
            var rest = Ds916Protocol.GatherMs - (int)time.GetElapsedTime(gathering).TotalMilliseconds;
            var budget = left();
            if (budget < rest)
                (rest, cut) = (budget, true);
            if (rest < 1)
                break;
            status = serial.Read(reply.AsSpan(total), rest, out var read, stop);
            if (status == PanelDeviceStatus.Timeout)
                break;
            if (status != PanelDeviceStatus.Ok)
                return Asked.Stopped;
            total += read;
        }
        if (cut && total < reply.Length)
        {
            // The budget ended the gathering before its 300 ms or 64 bytes: what came may be a part of the reply (a serial
            // cut short reads as another), so it is no answer either way.
            status = PanelDeviceStatus.Timeout;
            return Asked.Stopped;
        }
        status = PanelDeviceStatus.Ok;
        identity = Ds916Protocol.Parse(reply.AsSpan(0, total));
        return identity == null ? Asked.Unanswered : Asked.Answered;
    }

    /// <summary>One raw JPEG, nothing around it (JonsboSerialDevice.cs:142-146), within the write budget.</summary>
    public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
    {
        var timeout = (int)Math.Clamp(Math.Min(Ds916Protocol.WriteTimeoutMs, _writeBudget.TotalMilliseconds), 1, IPanelDevices.MaxTimeoutMs);
        var started = Stopwatch.GetTimestamp();
        var status = _serial.Write(frame.Bytes, timeout, stop);
        var deviceTime = Stopwatch.GetElapsedTime(started);
        if (status == PanelDeviceStatus.Gone)
            _gone = true;
        return new(status, deviceTime);
    }

    /// <summary>Nothing to send: the engine dims the frame (<see cref="PanelCapabilities.DimInSoftware"/>); 1.4 knew no brightness command for the DS916.</summary>
    public PanelDeviceStatus SetBrightness(int percent) => PanelDeviceStatus.Ok;

    /// <summary>Sends nothing (the firmware knows no stop command, JonsboSerialDevice.cs:156-157): the port let go.</summary>
    public void Close(bool screenOff, TimeSpan budget) => Dispose();

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        _serial.Dispose();
    }

    /// <summary>Whether the device went away during the session (tests).</summary>
    internal bool Gone => _gone;
}
