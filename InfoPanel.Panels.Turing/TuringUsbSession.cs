// The Turing variant's requests ported from TuringSmartScreenLib's LcdDriver.TuringSmartScreen/ScreenDevice.cs:
// Copyright (c) 2021 machi_pon (https://github.com/usausa/turing-smart-screen; the full notice is in
// LICENSES.md). The Lian Li variant's from InfoPanel 1.4's LianLiPanel/LianLiUsbScreenDevice.cs.

using System.Diagnostics;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Turing;

/// <summary>
/// A running Turing USB or Lian Li panel over its claimed interface 0 (endpoints 1 out and 1 in), as 1.4 drove each:
/// <list type="bullet">
/// <item>Turing (the library's <c>ScreenDevice</c>, 1.4's <c>Lib/LcdDriver.TuringSmartScreen.dll</c>; the steps of
/// <c>Services/TuringPanelUsbDeviceTask.cs</c>): each command's packet written, an image's bytes as a second write, and
/// one 512-byte answer read, each within 1500 ms; no answer is a failure, an image's too.</item>
/// <item>Lian Li (<c>LianLiPanel/LianLiUsbScreenDevice.cs</c>, the steps of <c>Services/LianLiPanelDeviceTask.cs</c>): the
/// packet and any image as one write within 2000 ms, then one answer read within 2000 ms (an image's within 200 ms,
/// whose absence is success); an image's answer reporting more than 3 frames buffered has the device asked
/// (QueryBlock) every 2 ms until it holds 2 or fewer, at most 1000 times; an image over 512,000 bytes is never sent.</item>
/// </list>
/// Before each command, what the device left on the in endpoint is read away, as LibUsbDotNet's <c>ReadFlush</c> did
/// (10 ms reads until one brings nothing, at most 128; inferred from LibUsbDotNet's source, not 1.4's); a flush whose
/// read fails, or that is still bringing packets at its 128th read, fails the command unwritten, so a command never takes
/// a stale packet for its answer; and fails what sent it (a start, a brightness change, a frame), a command whose answer
/// 1.4 didn't look at too. Only a Lian Li image's missing answer is success, as in 1.4.
/// </summary>
internal sealed class TuringUsbSession : IPanelSession
{
    public const byte OutEndpoint = 0x01;
    public const byte InEndpoint = 0x81;

    /// <summary>1.4's devices sent at most their Target FPS, 30 by default and its slider's top.</summary>
    public const int MaxFps = 30;

    /// <summary>1.4's JPEG quality for these panels (the devices' default, 90).</summary>
    public const int JpegQuality = 90;

    private const int FlushTimeoutMs = 10;
    private const int FlushReads = 128;
    private const int LianLiImageAckMs = 200;
    private const int DrainTries = 1000;

    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PrepSettle = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DrainPause = TimeSpan.FromMilliseconds(2);

    private readonly UsbVariant _variant;
    private readonly IPanelUsb _usb;
    private readonly Func<int> _timestamp;
    private readonly Func<DateTime> _localNow;
    private readonly Action<TimeSpan, CancellationToken> _wait;
    private readonly byte[] _answer = new byte[TuringUsbProtocol.PacketSize];
    private Func<int>? _left;
    private bool _synced;
    private bool _gone;
    private bool _closed;

    public TuringUsbSession(UsbVariant variant, IPanelUsb usb, int width, int height, Func<int> timestamp, Func<DateTime> localNow, Action<TimeSpan, CancellationToken> wait)
    {
        _variant = variant;
        _usb = usb;
        _timestamp = timestamp;
        _localNow = localNow;
        _wait = wait;
        Capabilities = new PanelCapabilities(width, height, [PanelFrameFormat.Jpeg], MaxFps,
            MaxFrameBytes: variant == UsbVariant.LianLi ? TuringUsbProtocol.MaxImagePayload : null,
            JpegQuality: JpegQuality, Brightness: new PanelRange(0, 100));
    }

    public PanelCapabilities Capabilities { get; }

    private int CommandTimeoutMs => _variant == UsbVariant.Turing ? 1500 : 2000;

    /// <summary>
    /// 1.4's start: Sync, Sync, StopMedia, (Lian Li: its layers prepared), the brightness, Sync, each followed by its
    /// pause. A Sync unanswered fails the start; the other commands' answers aren't looked at, as 1.4 didn't, but one
    /// that didn't go (its flush or its write failed) or found the device gone fails it.
    /// </summary>
    public PanelDeviceStatus Start(int brightness, CancellationToken stop, out string why)
    {
        why = "";
        var status = Request(TuringUsbProtocol.Command(TuringUsbProtocol.Sync, _timestamp()), [], stop).Status;
        if (status != PanelDeviceStatus.Ok)
            return Failed("Sync", status, out why);
        _synced = true;
        _wait(Settle, stop);
        status = Request(TuringUsbProtocol.Command(TuringUsbProtocol.Sync, _timestamp()), [], stop).Status;
        if (status != PanelDeviceStatus.Ok)
            return Failed("the second Sync", status, out why);
        _wait(Settle, stop);
        status = Request(TuringUsbProtocol.Command(TuringUsbProtocol.StopMedia(_variant), _timestamp()), [], stop).AnswerIgnored;
        if (status != PanelDeviceStatus.Ok)
            return Failed("StopMedia", status, out why);
        _wait(Settle, stop);

        if (_variant == UsbVariant.LianLi)
        {
            // LianLiPanelDeviceTask.cs:97-132, each step's answer not looked at.
            status = Request(TuringUsbProtocol.Clock(_localNow(), _timestamp()), [], stop).AnswerIgnored;
            if (status != PanelDeviceStatus.Ok)
                return Failed("the clock", status, out why);
            _wait(PrepSettle, stop);
            status = Request(TuringUsbProtocol.WithValue(TuringUsbProtocol.StopClock, 0, _timestamp()), [], stop).AnswerIgnored;
            if (status != PanelDeviceStatus.Ok)
                return Failed("StopClock", status, out why);
            _wait(PrepSettle, stop);
            status = Image(TuringUsbProtocol.Png, SolidImages.TransparentPng(Capabilities.Width, Capabilities.Height), answerLooked: false, stop);
            if (status != PanelDeviceStatus.Ok)
                return Failed("the overlay's clearing", status, out why);
            _wait(PrepSettle, stop);
            status = Image(TuringUsbProtocol.Jpeg, SolidImages.BlackJpeg(Capabilities.Width, Capabilities.Height), answerLooked: false, stop);
            if (status != PanelDeviceStatus.Ok)
                return Failed("the background's clearing", status, out why);
            _wait(PrepSettle, stop);
            status = Request(TuringUsbProtocol.WithValue(TuringUsbProtocol.FrameRate, MaxFps, _timestamp()), [], stop).AnswerIgnored;
            if (status != PanelDeviceStatus.Ok)
                return Failed("the frame rate", status, out why);
            _wait(Settle, stop);
        }

        status = Request(TuringUsbProtocol.WithValue(TuringUsbProtocol.Brightness, (byte)Math.Clamp(brightness, 0, 100), _timestamp()), [], stop).AnswerIgnored;
        if (status != PanelDeviceStatus.Ok)
            return Failed("the brightness", status, out why);
        status = Request(TuringUsbProtocol.Command(TuringUsbProtocol.Sync, _timestamp()), [], stop).Status;
        if (status != PanelDeviceStatus.Ok)
            return Failed("Sync after the brightness", status, out why);
        _wait(Settle, stop);
        return PanelDeviceStatus.Ok;
    }

    private static PanelDeviceStatus Failed(string step, PanelDeviceStatus status, out string why)
    {
        why = status == PanelDeviceStatus.Gone ? $"the panel went away at {step}" : $"the panel didn't take {step} ({status})";
        return status;
    }

    /// <summary>
    /// A command's outcome: its status; whether it went (what the device left read away, then its bytes written: when
    /// not, the status is that step's failure); whether a whole 512-byte answer came (in <see cref="_answer"/>).
    /// </summary>
    private readonly record struct Sent(PanelDeviceStatus Status, bool Went, bool Answered)
    {
        /// <summary>For a command whose answer 1.4 didn't look at: its failure when it didn't go or found the device gone; else Ok.</summary>
        public PanelDeviceStatus AnswerIgnored => !Went || Status == PanelDeviceStatus.Gone ? Status : PanelDeviceStatus.Ok;
    }

    public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
    {
        var jpeg = frame.Bytes;
        if (jpeg.IsEmpty)
            return new(PanelDeviceStatus.Ok, TimeSpan.Zero);
        if (_variant == UsbVariant.LianLi && jpeg.Length > TuringUsbProtocol.MaxImagePayload)
            return new(PanelDeviceStatus.NotSupported, TimeSpan.Zero); // LianLiUsbScreenDevice.cs:176-183: never sent
        var started = Stopwatch.GetTimestamp();
        var status = Image(TuringUsbProtocol.Jpeg, jpeg, answerLooked: true, stop);
        return new(status, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// 1.4's brightness change (<c>TuringPanelUsbDeviceTask.cs:209-220</c>, <c>LianLiPanelDeviceTask.cs:271-282</c>): the
    /// brightness, 0–100 as it is, then Sync, whose failure ends the session, then a pause of 200 ms. The brightness's
    /// answer isn't looked at, but a brightness that didn't go fails the change there: the Sync after it could still be
    /// answered, and the engine would take a brightness the panel never got.
    /// </summary>
    public PanelDeviceStatus SetBrightness(int percent)
    {
        var status = Request(TuringUsbProtocol.WithValue(TuringUsbProtocol.Brightness, (byte)Math.Clamp(percent, 0, 100), _timestamp()), [], CancellationToken.None).AnswerIgnored;
        if (status != PanelDeviceStatus.Ok)
            return status;
        status = Request(TuringUsbProtocol.Command(TuringUsbProtocol.Sync, _timestamp()), [], CancellationToken.None).Status;
        if (status == PanelDeviceStatus.Ok)
            _wait(Settle, CancellationToken.None);
        return status;
    }

    /// <summary>
    /// An image: its command with its length, its bytes, and (Lian Li) the buffer drained when its answer says so. With
    /// <paramref name="answerLooked"/> false (the start's layers), its answer's absence isn't a failure, as for any of the
    /// start's commands.
    /// </summary>
    private PanelDeviceStatus Image(byte command, ReadOnlySpan<byte> image, bool answerLooked, CancellationToken stop)
    {
        var sent = Request(TuringUsbProtocol.WithLength(command, image.Length, _timestamp()), image, stop);
        var status = answerLooked ? sent.Status : sent.AnswerIgnored;
        if (_variant != UsbVariant.LianLi || sent.Status != PanelDeviceStatus.Ok || !sent.Answered || _answer[8] <= 3)
            return status;
        // LianLiUsbScreenDevice.cs:158-172: QueryBlock until the level is 2 or less, or no answer comes in its time; any
        // other failure (its flush's, its write's, its read's) fails the image.
        for (var i = 0; i < DrainTries; i++)
        {
            var query = Request(TuringUsbProtocol.Command(TuringUsbProtocol.QueryBlock, _timestamp()), [], stop);
            if (query.Went && query.Status == PanelDeviceStatus.Timeout)
                return PanelDeviceStatus.Ok;
            if (query.Status != PanelDeviceStatus.Ok)
                return query.Status;
            if (_answer[8] <= 2)
                return PanelDeviceStatus.Ok;
            _wait(DrainPause, stop);
        }
        return PanelDeviceStatus.Ok;
    }

    /// <summary>One command: the in endpoint read away, the packet (and any image) written, one answer read.</summary>
    private Sent Request(byte[] command, ReadOnlySpan<byte> image, CancellationToken stop)
    {
        var packet = TuringUsbProtocol.Packet(_variant, command);
        var flushed = Flush(stop);
        if (flushed != PanelDeviceStatus.Ok)
            return new(flushed, false, false);

        PanelDeviceStatus status;
        if (_variant == UsbVariant.Turing)
        {
            status = Write(packet, stop);
            if (status == PanelDeviceStatus.Ok && !image.IsEmpty)
                status = Write(image, stop);
        }
        else if (image.IsEmpty)
        {
            status = Write(packet, stop);
        }
        else
        {
            var both = new byte[packet.Length + image.Length];
            packet.CopyTo(both, 0);
            image.CopyTo(both.AsSpan(packet.Length));
            status = Write(both, stop);
        }
        if (status != PanelDeviceStatus.Ok)
            return new(status, false, false);

        Array.Clear(_answer);
        var image200 = _variant == UsbVariant.LianLi && !image.IsEmpty;
        status = Read(image200 ? LianLiImageAckMs : CommandTimeoutMs, stop, out var read);
        if (image200 && status == PanelDeviceStatus.Timeout)
            return new(PanelDeviceStatus.Ok, true, false); // LianLiUsbScreenDevice.cs:264-270: an image's missing answer is success
        if (status == PanelDeviceStatus.Ok && read == TuringUsbProtocol.PacketSize)
            return new(PanelDeviceStatus.Ok, true, true);
        return new(status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status, true, false);
    }

    /// <summary>
    /// The in endpoint read away: Ok once a read brings nothing (its timeout, or no bytes); a read's failure as it is; no
    /// time left, <see cref="PanelDeviceStatus.Timeout"/>; still bringing packets at the last read, <see cref="PanelDeviceStatus.Io"/>.
    /// </summary>
    private PanelDeviceStatus Flush(CancellationToken stop)
    {
        if (_gone)
            return PanelDeviceStatus.Gone;
        var buffer = new byte[TuringUsbProtocol.PacketSize];
        for (var i = 0; i < FlushReads; i++)
        {
            var timeoutMs = FlushTimeoutMs;
            if (!Within(ref timeoutMs))
                return PanelDeviceStatus.Timeout;
            var status = Remember(_usb.BulkRead(InEndpoint, buffer, timeoutMs, out var read, stop));
            if (status == PanelDeviceStatus.Timeout || (status == PanelDeviceStatus.Ok && read == 0))
                return PanelDeviceStatus.Ok;
            if (status != PanelDeviceStatus.Ok)
                return status;
        }
        return PanelDeviceStatus.Io;
    }

    private PanelDeviceStatus Write(ReadOnlySpan<byte> data, CancellationToken stop)
    {
        var timeoutMs = CommandTimeoutMs;
        if (!Within(ref timeoutMs))
            return PanelDeviceStatus.Timeout;
        return Remember(_usb.BulkWrite(OutEndpoint, data, timeoutMs, cancellationToken: stop));
    }

    private PanelDeviceStatus Read(int timeoutMs, CancellationToken stop, out int read)
    {
        read = 0;
        if (!Within(ref timeoutMs))
            return PanelDeviceStatus.Timeout;
        return Remember(_usb.BulkRead(InEndpoint, _answer, timeoutMs, out read, stop));
    }

    private bool Within(ref int timeoutMs)
    {
        if (_left == null)
            return true;
        var left = _left();
        if (left < 1)
            return false;
        timeoutMs = Math.Min(timeoutMs, left);
        return true;
    }

    private PanelDeviceStatus Remember(PanelDeviceStatus status)
    {
        if (status == PanelDeviceStatus.Gone)
            _gone = true;
        return status;
    }

    /// <summary>
    /// With <paramref name="screenOff"/>, 1.4's stop (the loop's finally): Turing's brightness 0
    /// (<c>TuringPanelUsbDeviceTask.cs:262-272</c>); Lian Li's StopMedia, then brightness 0
    /// (<c>LianLiPanelDeviceTask.cs:324-343</c>); each transfer within what is left of <paramref name="budget"/>, none
    /// once the device is gone; then the interface let go. A device that never answered a Sync (a start that failed or
    /// was cancelled at its first) gets nothing: it may not be a panel at all, though 1.4's finally sent it the stop too.
    /// </summary>
    public void Close(bool screenOff, TimeSpan budget)
    {
        if (_closed)
            return;
        _closed = true;
        try
        {
            if (!screenOff || _gone || !_synced)
                return;
            var clock = Stopwatch.StartNew();
            _left = () => (int)Math.Clamp(Math.Floor((budget - clock.Elapsed).TotalMilliseconds), 0, IPanelDevices.MaxTimeoutMs);
            if (_variant == UsbVariant.LianLi)
                Request(TuringUsbProtocol.Command(TuringUsbProtocol.StopMedia(_variant), _timestamp()), [], CancellationToken.None);
            if (!_gone)
                Request(TuringUsbProtocol.WithValue(TuringUsbProtocol.Brightness, 0, _timestamp()), [], CancellationToken.None);
        }
        finally
        {
            _usb.Dispose();
        }
    }

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        _usb.Dispose();
    }
}
