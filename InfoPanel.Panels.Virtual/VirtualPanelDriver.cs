using System.Diagnostics;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Virtual;

/// <summary>
/// A virtual panel, 480 × 320, on the virtual USB device 1209:0001: every probe of it says "mine", every start opens a
/// session, every frame is taken. It talks to no device. <c>virtual-mode.txt</c> beside the package's DLL asks for more,
/// a line each: <c>log {file}</c> (each start, frame, brightness and close as a line in that file, beside the DLL),
/// <c>hang-close</c> (a close never returns) and <c>hang-constructor</c> (the driver's constructor logs <c>constructing</c>
/// and never returns).
/// </summary>
public sealed class VirtualPanelDriver : IPanelDriver
{
    public const ushort VendorId = 0x1209;
    public const ushort ProductId = 0x0001;

    private readonly string? _log;
    private readonly bool _hangClose;

    public VirtualPanelDriver()
    {
        var folder = Path.GetDirectoryName(typeof(VirtualPanelDriver).Assembly.Location) ?? ".";
        var mode = Path.Combine(folder, "virtual-mode.txt");
        if (!File.Exists(mode))
            return;
        var hang = false;
        foreach (var line in File.ReadAllLines(mode).Select(l => l.Trim()))
        {
            if (line.StartsWith("log ", StringComparison.Ordinal))
                _log = Path.Combine(folder, line[4..].Trim());
            else if (line == "hang-close")
                _hangClose = true;
            else if (line == "hang-constructor")
                hang = true;
        }
        if (hang)
        {
            Log("constructing");
            Thread.Sleep(Timeout.Infinite);
        }
    }

    public string Family => "virtual";

    public IReadOnlyList<PanelMatch> Matches { get; } = [new PanelMatch(VendorId, ProductId)];

    public PanelProbe Probe(PanelCandidate device, CancellationToken stop) =>
        new(true, new PanelIdentity("480x320", 480, 320, "VIRTUAL" + string.Concat(device.PortPath.Where(char.IsAsciiDigit)), "1"), PanelDeviceStatus.Ok);

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        Log($"open brightness {options.Brightness} rotation {options.Rotation} format {options.Format?.ToString() ?? "any"}");
        PanelFrameFormat[] formats = options.Format == PanelFrameFormat.Jpeg ? [PanelFrameFormat.Jpeg, PanelFrameFormat.Rgb565Le] : [PanelFrameFormat.Rgb565Le, PanelFrameFormat.Jpeg];
        return new PanelOpen(new Session(this, new PanelCapabilities(480, 320, formats, MaxFps: 30, Brightness: new PanelRange(0, 100))), PanelDeviceStatus.Ok, "");
    }

    private void Log(string line)
    {
        if (_log == null)
            return;
        lock (this)
            File.AppendAllLines(_log, [line]);
    }

    private sealed class Session(VirtualPanelDriver driver, PanelCapabilities capabilities) : IPanelSession
    {
        public PanelCapabilities Capabilities => capabilities;

        public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
        {
            var started = Stopwatch.GetTimestamp();
            if (frame.Format == PanelFrameFormat.Rgb565Le && frame.Bytes.Length != capabilities.Width * capabilities.Height * 2)
                return new PanelWrite(PanelDeviceStatus.Io, Stopwatch.GetElapsedTime(started));
            driver.Log($"write {frame.FrameId} {frame.Format} {frame.Bytes.Length}{(frame.Repeat ? " repeat" : "")}");
            return new PanelWrite(PanelDeviceStatus.Ok, Stopwatch.GetElapsedTime(started));
        }

        public PanelDeviceStatus SetBrightness(int percent)
        {
            driver.Log($"brightness {percent}");
            return PanelDeviceStatus.Ok;
        }

        public void Close(bool screenOff, TimeSpan budget)
        {
            driver.Log($"close screenOff {screenOff}");
            if (driver._hangClose)
                Thread.Sleep(Timeout.Infinite);
        }

        public void Dispose()
        {
        }
    }
}
