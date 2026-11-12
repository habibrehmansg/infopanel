// The sample driver of docs/2.0/panel-drivers.md ("A whole driver"): the text between the two marker lines is the
// guide's code block word for word (ConformanceTests.The_guides_sample_is_this_file checks it), and ConformanceTests runs
// the conformance suite against it over the kit's fakes.
// guide:begin
using System.Diagnostics;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Example;

/// <summary>A 320 × 240 panel on USB 1209:000a: raw RGB565 frames and a two-byte backlight command, both on endpoint 0x01.</summary>
public sealed class ExampleDriver(IPanelDevices devices) : IPanelDriver
{
    public string Family => "example";

    public IReadOnlyList<PanelMatch> Matches { get; } = [new(0x1209, 0x000a)];

    public PanelProbe Probe(PanelCandidate device, CancellationToken stop) =>
        new(true, new PanelIdentity("320x240", 320, 240, Serial: null, Firmware: null), PanelDeviceStatus.Ok);

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        var status = devices.OpenUsb(device, 0, 1000, out var usb, stop);
        if (usb == null)
            return new(null, status, "the panel couldn't be opened");
        var session = new Session(usb);
        try
        {
            if ((status = session.Backlight(options.Brightness, stop)) == PanelDeviceStatus.Ok)
                return new(session, status, "");
        }
        catch (OperationCanceledException) // a cancelled start: the screen off again, the device let go
        {
            session.Close(screenOff: true, TimeSpan.FromSeconds(1));
            throw;
        }
        session.Dispose();
        return new(null, status, "the panel didn't take its backlight");
    }

    private sealed class Session(IPanelUsb usb) : IPanelSession
    {
        public PanelCapabilities Capabilities { get; } =
            new(320, 240, [PanelFrameFormat.Rgb565Le], MaxFps: 30, Brightness: new PanelRange(0, 255));

        public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
        {
            var started = Stopwatch.GetTimestamp();
            var status = usb.BulkWrite(0x01, frame.Bytes, 1000, cancellationToken: stop);
            return new(status, Stopwatch.GetElapsedTime(started));
        }

        public PanelDeviceStatus SetBrightness(int percent) => Backlight(percent, CancellationToken.None);

        public PanelDeviceStatus Backlight(int percent, CancellationToken stop) =>
            usb.BulkWrite(0x01, [0xB1, (byte)(percent * 255 / 100)], 1000, cancellationToken: stop);

        public void Close(bool screenOff, TimeSpan budget)
        {
            if (screenOff && budget.TotalMilliseconds >= 1) // the backlight off, within what is left of the budget
                usb.BulkWrite(0x01, [0xB1, 0], (int)Math.Min(budget.TotalMilliseconds, 1000));
            usb.Dispose();
        }

        public void Dispose() => usb.Dispose();
    }
}
// guide:end
