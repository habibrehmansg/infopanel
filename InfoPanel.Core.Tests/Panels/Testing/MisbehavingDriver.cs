using System.Diagnostics;
using InfoPanel.Panels.Sdk;
using SkiaSharp;

namespace InfoPanel.Core.Tests.Panels.Testing;

/// <summary>
/// What <see cref="MisbehavingDriver"/> gets wrong, one rule of the conformance suite each; none set, it behaves as the
/// guide's sample does. <see cref="Overlay"/> and <see cref="Crop"/> declare an overlay (at 0°) and a crop, right or
/// wrong as the test sets them; <see cref="NoBrightness"/> is a fixed-brightness panel, which the SDK allows, and
/// <see cref="StartNonce"/> a start that sends a value of its own each time (a nonce, a timestamp), which the SDK allows
/// too; <see cref="Small"/> is a 20 × 20 panel, <see cref="ProbeQueriesTwice"/> a probe of two 512-byte queries and
/// <see cref="StartOneCall"/> a start of one call (its open), all allowed.
/// </summary>
internal sealed record Misbehaviour
{
    public bool ProbeKeepsDevice { get; init; }
    public bool ProbeDraws { get; init; }
    public bool ProbeQueriesLong { get; init; }
    public bool ProbeQueriesTwice { get; init; }
    public bool Small { get; init; }
    public bool StartIgnoresCancel { get; init; }
    public bool StartOneCall { get; init; }
    public bool CancelledBeforeStartLeaksDevice { get; init; }
    public bool StartNonce { get; init; }
    public bool FixedStartFailsAtZero { get; init; }
    public bool FailsSecondFormat { get; init; }
    public bool NoBrightness { get; init; }
    public bool BrightnessOverflowsAtFull { get; init; }
    public bool BrightnessFailsAt25 { get; init; }
    public bool CloseIgnoresBudget { get; init; }
    public bool FixedCloseTimeout { get; init; }
    public bool WritesWhenSpent { get; init; }
    public bool SpendsBudgetThenWrites { get; init; }
    public bool GoneAsIo { get; init; }
    public bool ScreenOffIgnoresGone { get; init; }
    public bool WritesAfterClose { get; init; }
    public bool CloseLeavesTimer { get; init; }
    public bool ScreenOffWithoutAsk { get; init; }
    public bool NoDeviceTime { get; init; }
    public bool PadsDeviceTime { get; init; }
    public bool DeviceTimeIncludesPreparation { get; init; }
    public byte[]? Overlay { get; init; }
    public PanelCrop? Crop { get; init; }
}

/// <summary>
/// The guide's sample driver (a 320 × 240 panel, RGB565 and a backlight command on endpoint 0x01) with the faults
/// <see cref="Misbehaviour"/> names, to show that each of the conformance suite's checks catches the fault it is for.
/// </summary>
internal sealed class MisbehavingDriver(IPanelDevices devices, Misbehaviour faults) : IPanelDriver
{
    public const int Width = 320;
    public const int Height = 240;

    public string Family => "misbehaving";

    public IReadOnlyList<PanelMatch> Matches { get; } = [new(0x1209, 0x000b)];

    // The panel's size: 320 × 240, or 20 × 20 when small.
    private int W => faults.Small ? 20 : Width;

    private int H => faults.Small ? 20 : Height;

    public PanelProbe Probe(PanelCandidate device, CancellationToken stop)
    {
        if (faults.ProbeKeepsDevice || faults.ProbeDraws || faults.ProbeQueriesLong || faults.ProbeQueriesTwice)
        {
            var status = devices.OpenUsb(device, 0, 1000, out var usb, stop);
            if (status != PanelDeviceStatus.Ok)
                return new PanelProbe(false, null, status);
            if (faults.ProbeDraws)
                usb!.BulkWrite(0x01, new byte[W * H * 2], 1000, cancellationToken: stop);
            if (faults.ProbeQueriesLong) // a query longer than 4 KiB, far short of a frame
                usb!.BulkWrite(0x01, new byte[4097], 1000, cancellationToken: stop);
            if (faults.ProbeQueriesTwice) // two queries of 512 bytes each, each short of a 20 × 20 frame (800 bytes)
            {
                usb!.BulkWrite(0x01, new byte[512], 1000, cancellationToken: stop);
                usb!.BulkWrite(0x01, new byte[512], 1000, cancellationToken: stop);
            }
            if (!faults.ProbeKeepsDevice)
                usb!.Dispose();
        }
        return new PanelProbe(true, new PanelIdentity($"{W}x{H}", W, H, null, null), PanelDeviceStatus.Ok);
    }

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        if (faults.FixedStartFailsAtZero && options.Brightness == 0)
            return new PanelOpen(null, PanelDeviceStatus.NotSupported, "a fixed light refuses brightness 0");
        var status = devices.OpenUsb(device, 0, 1000, out var usb, faults.StartIgnoresCancel || faults.CancelledBeforeStartLeaksDevice ? CancellationToken.None : stop);
        if (usb == null)
            return new PanelOpen(null, status, "the panel couldn't be opened");
        if (faults.CancelledBeforeStartLeaksDevice)
            stop.ThrowIfCancellationRequested(); // opened without its token, then cancelled with the device kept: nothing lets it go
        var session = new Session(usb, faults, W, H);
        try
        {
            if (faults.StartOneCall)
                return new PanelOpen(session, PanelDeviceStatus.Ok, "");
            if (faults.StartNonce) // a start command that carries a value of its own each time: no brightness in it
            {
                status = usb.BulkWrite(0x01, [0xA0, .. BitConverter.GetBytes(Stopwatch.GetTimestamp())], 1000, cancellationToken: stop);
                if (status != PanelDeviceStatus.Ok)
                {
                    session.Dispose();
                    return new PanelOpen(null, status, "the panel didn't take its start");
                }
            }
            if (faults.NoBrightness)
                return new PanelOpen(session, PanelDeviceStatus.Ok, "");
            if ((status = session.Backlight(options.Brightness, faults.StartIgnoresCancel ? CancellationToken.None : stop)) == PanelDeviceStatus.Ok)
                return new PanelOpen(session, status, "");
        }
        catch (OperationCanceledException)
        {
            session.Close(screenOff: true, TimeSpan.FromSeconds(1));
            throw;
        }
        session.Dispose();
        return new PanelOpen(null, status, "the panel didn't take its backlight");
    }

    /// <summary>A whole PNG of <paramref name="width"/> × <paramref name="height"/>: transparent, with a black band round its edge (a mask's shape).</summary>
    public static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 8 })
            canvas.DrawRect(0, 0, width, height, paint);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class Session(IPanelUsb usb, Misbehaviour faults, int width, int height) : IPanelSession
    {
        private Timer? _timer;

        public PanelCapabilities Capabilities { get; } = new(width, height,
            faults.FailsSecondFormat ? [PanelFrameFormat.Rgb565Le, PanelFrameFormat.Rgb888] : [PanelFrameFormat.Rgb565Le],
            MaxFps: 30, Brightness: faults.NoBrightness ? null : new PanelRange(0, 255))
        {
            Overlays = faults.Overlay is { } png ? new Dictionary<int, byte[]> { [0] = png } : null,
            Crop = faults.Crop,
        };

        public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
        {
            var started = Stopwatch.GetTimestamp();
            if (faults.FailsSecondFormat && frame.Format == PanelFrameFormat.Rgb888)
                return new PanelWrite(PanelDeviceStatus.NotSupported, TimeSpan.Zero);
            if (faults.DeviceTimeIncludesPreparation)
                Thread.Sleep(200); // the frame's own preparation, inside the time it answers
            var status = usb.BulkWrite(0x01, frame.Bytes, 1000, cancellationToken: stop);
            if (faults.GoneAsIo && status == PanelDeviceStatus.Gone)
                status = PanelDeviceStatus.Io;
            var took = Stopwatch.GetElapsedTime(started);
            return new PanelWrite(status, faults.NoDeviceTime ? TimeSpan.Zero : faults.PadsDeviceTime ? took + TimeSpan.FromSeconds(1) : took);
        }

        public PanelDeviceStatus SetBrightness(int percent) => Backlight(percent, CancellationToken.None);

        public PanelDeviceStatus Backlight(int percent, CancellationToken stop)
        {
            if (faults.BrightnessFailsAt25 && percent == 25)
                return PanelDeviceStatus.Io;
            return usb.BulkWrite(0x01, [0xB1, faults.BrightnessOverflowsAtFull ? checked((byte)(percent * 255 / 99)) : (byte)(percent * 255 / 100)], 1000, cancellationToken: stop);
        }

        public void Close(bool screenOff, TimeSpan budget)
        {
            if (screenOff || faults.ScreenOffWithoutAsk)
            {
                if (faults.CloseIgnoresBudget)
                    usb.BulkWrite(0x01, [0xB1, 0], 2000);
                else if (faults.FixedCloseTimeout)
                {
                    if (budget.TotalMilliseconds >= 1) // no write once the budget is spent, but a fixed timeout
                        usb.BulkWrite(0x01, [0xB1, 0], 1000);
                }
                else if (faults.SpendsBudgetThenWrites && budget.TotalMilliseconds >= 1)
                {
                    // The first step takes the whole budget (rounded up), then the next starts anyway, with at least 1 ms.
                    var clock = Stopwatch.StartNew();
                    usb.BulkWrite(0x01, [0xB1, 0], (int)Math.Clamp(Math.Ceiling(budget.TotalMilliseconds), 1, 1000));
                    usb.BulkWrite(0x01, [0xB0, 0], (int)Math.Clamp((budget - clock.Elapsed).TotalMilliseconds, 1, 1000));
                }
                else if (faults.ScreenOffIgnoresGone)
                {
                    for (var step = 0; step < 3; step++) // every step tried, even once the device is gone
                        usb.BulkWrite(0x01, [0xB1, 0], 1000);
                }
                else if (budget.TotalMilliseconds >= 1 || faults.WritesWhenSpent)
                    usb.BulkWrite(0x01, [0xB1, 0], (int)Math.Clamp(budget.TotalMilliseconds, 1, 1000));
            }
            if (faults.CloseLeavesTimer)
            {
                // The backlight faded off on a timer that Close leaves running, with the device: only Dispose stops both.
                _timer = new Timer(_ =>
                {
                    try
                    {
                        usb.BulkWrite(0x01, [0xB1, 0], 1000);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }, null, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
                return;
            }
            if (!faults.WritesAfterClose)
                usb.Dispose();
        }

        public void Dispose()
        {
            if (_timer is { } timer)
            {
                using var stopped = new ManualResetEvent(false);
                if (timer.Dispose(stopped))
                    stopped.WaitOne();
            }
            if (faults.WritesAfterClose)
            {
                // The device kept past Close, and a last command sent as the session is let go: after Close returned.
                try
                {
                    usb.BulkWrite(0x01, [0xB1, 0x80], 1000);
                }
                catch (ObjectDisposedException)
                {
                }
            }
            usb.Dispose();
        }
    }
}
