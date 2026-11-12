using System.Diagnostics;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Virtual;

/// <summary>
/// A virtual panel, 480 × 320, on the virtual USB device 1209:0001: every probe of it says "mine", every start opens a
/// session, every frame is taken. It talks to no device. <c>virtual-mode.txt</c> beside the package's DLL asks for more,
/// a line each: <c>log {file}</c> (each start, frame, brightness and close as a line in that file, beside the DLL),
/// <c>hang-close</c> (a close never returns), <c>hang-constructor</c> (the driver's constructor logs <c>constructing</c>
/// and never returns), <c>unknown-model</c> (its probe names the model <see cref="UnknownModel"/>, which its panel.json
/// doesn't declare, and a start answers <c>notSupported</c> until the family setting <c>model</c> names <c>480x320</c>:
/// "Try it as…") and <c>log-settings</c> (each start's family settings as a <c>settings</c> line). Its panel.json declares a
/// boolean, an enum and a model setting, so a spare engine shows a family's settings.
/// </summary>
public sealed class VirtualPanelDriver : IPanelDriver
{
    public const ushort VendorId = 0x1209;
    public const ushort ProductId = 0x0001;

    /// <summary>The model id its probe names under <c>unknown-model</c>: one the family's panel.json doesn't declare.</summary>
    public const string UnknownModel = "pm-47";

    private readonly string? _log;
    private readonly bool _hangClose;
    private readonly bool _unknownModel;
    private readonly bool _logSettings;

    /// <summary>A driver as the panel host makes it: given the host's device seam (which the virtual panel never uses).</summary>
    public VirtualPanelDriver(IPanelDevices devices)
        : this()
    {
        Devices = devices;
    }

    /// <summary>The device seam it was given (null when made without one).</summary>
    public IPanelDevices? Devices { get; }

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
            else if (line == "unknown-model")
                _unknownModel = true;
            else if (line == "log-settings")
                _logSettings = true;
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
        new(true, new PanelIdentity(_unknownModel ? UnknownModel : "480x320", 480, 320, "VIRTUAL" + string.Concat(device.PortPath.Where(char.IsAsciiDigit)), "1"), PanelDeviceStatus.Ok);

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        Log($"open brightness {options.Brightness} rotation {options.Rotation} format {options.Format?.ToString() ?? "any"}");
        if (_logSettings)
            Log($"settings {(options.FamilySettings.ValueKind == System.Text.Json.JsonValueKind.Object ? options.FamilySettings.GetRawText() : "none")}");
        // "Try it as…": a model the family doesn't know runs only once the model setting names one it does.
        var tryAs = options.FamilySettings.ValueKind == System.Text.Json.JsonValueKind.Object
            && options.FamilySettings.TryGetProperty("model", out var model) && model.ValueKind == System.Text.Json.JsonValueKind.String ? model.GetString() : null;
        if (identity.Model != "480x320" && tryAs != "480x320")
            return new PanelOpen(null, PanelDeviceStatus.NotSupported, $"the panel's model {identity.Model} isn't one the family knows: try it as one of its models");
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
