using InfoPanel.Panels.Devices;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Usb;
using InfoPanel.Panels.Thermalright;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermalright;

/// <summary>
/// A tester's run on a real Thermalright panel (§2.2 (9)), skipped unless <c>INFOPANEL_PANEL_HW=thermalright</c>: each
/// matched device is probed and opened through the engine's device seam, sent a black frame in its format, and let go;
/// every transfer is recorded to <c>INFOPANEL_PANEL_TRACE</c> (a file) through the kit's recorder, so the trace can
/// become a vector. The seam is the one the panel host gives a package's drivers: the engine's
/// <see cref="EnginePanelDevices"/> over libusb (when it loads), HID and serial.
/// </summary>
public sealed class ThermalrightHardwareTests
{
    /// <summary>The engine's own device seam, built as the panel host builds it (libusb only when <paramref name="usb"/>).</summary>
    internal static IPanelDevices EngineDevices(Serilog.ILogger logger, bool usb = true)
    {
        IUsbBackend? backend = null;
        if (usb && LibUsb.LoadAndLog(LibUsb.DefaultCandidates(), logger) is { } library)
            backend = LibUsbBackend.Create(library, out _);
        return new EnginePanelDevices(backend, new HidSharpSystem(logger), new SerialPorts(logger), logger);
    }

    [Fact]
    public void TheHardwareTest_DrivesThePanelThroughTheEnginesOwnSeam()
    {
        // No device is touched: the seam is only built (without libusb), as the hardware run builds it.
        Assert.IsType<EnginePanelDevices>(EngineDevices(Serilog.Core.Logger.None, usb: false));
    }

    [Fact]
    public void AConnectedPanel_StartsTakesABlackFrameAndIsLetGo_Recorded()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("INFOPANEL_PANEL_HW") == ThermalrightCatalog.FamilyId,
            "A real Thermalright panel: set INFOPANEL_PANEL_HW=thermalright (and INFOPANEL_PANEL_TRACE=<file> to record it).");
        var engine = EngineDevices(Serilog.Log.Logger);
        var trace = Environment.GetEnvironmentVariable("INFOPANEL_PANEL_TRACE");
        var devices = string.IsNullOrEmpty(trace) ? engine : new RecordingPanelDevices(engine, trace);
        var driver = new ThermalrightDriver(devices);
        var bus = Environment.GetEnvironmentVariable("INFOPANEL_PANEL_CANDIDATE"); // vvvv:pppp@bus:address:port
        Assert.SkipWhen(string.IsNullOrEmpty(bus), "Name the panel: INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port (as the engine's log lists it).");
        var parts = bus!.Split('@', ':');
        var candidate = new PanelCandidate(Convert.ToUInt16(parts[0], 16), Convert.ToUInt16(parts[1], 16), byte.Parse(parts[2]), byte.Parse(parts[3]), parts[4]);

        var probe = driver.Probe(candidate, TestContext.Current.CancellationToken);
        Assert.True(probe.Mine, $"{probe.Status}: {probe.Message}");
        var open = driver.Open(candidate, probe.Identity!, new PanelOpenOptions(100), TestContext.Current.CancellationToken);
        Assert.True(open.Session != null, $"{open.Status}: {open.Message}");
        using var session = open.Session!;
        var capabilities = session.Capabilities;
        var format = capabilities.Formats[0];
        var (width, height) = capabilities.Crop is { } crop ? (crop.Width, crop.Height) : (capabilities.Width, capabilities.Height);
        byte[] black;
        if (format == PanelFrameFormat.Jpeg)
        {
            using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            bitmap.Erase(SKColors.Black);
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, capabilities.JpegQuality);
            black = data.ToArray();
        }
        else
        {
            black = new byte[width * height * 2];
        }
        Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(black, format, 1, false), TestContext.Current.CancellationToken).Status);
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
    }
}
