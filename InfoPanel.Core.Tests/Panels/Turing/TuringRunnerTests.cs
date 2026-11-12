using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Turing;
using InfoPanel.Panels.Usb;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Turing;

/// <summary>
/// The drivers run by the engine in process (§2.2 (7)): the manager lists the device (the engine's fake USB backend),
/// offers it to the driver, which probes and opens it over the kit's fake devices, and the runner turns a rendered frame
/// into the family's format (the engine's JPEG at quality 90 for the USB panels; BGR for a serial revision C) and the
/// driver writes it; stopping turns the screen off and lets the device go.
/// </summary>
public sealed class TuringRunnerTests : IAsyncDisposable
{
    private readonly FakeUsbBackend _backend = new();
    private readonly FakePanelDevices _devices = new();
    private readonly KeptLog _log = new();
    private PanelManager? _manager;
    private volatile PanelSink? _sink;
    private long _frameId;

    public async ValueTask DisposeAsync()
    {
        if (_manager != null)
            await _manager.DisposeAsync();
    }

    private PanelManager Manager(IPanelDriver driver)
    {
        var host = new PanelHost(_ => true, sink =>
        {
            _sink = (PanelSink)sink;
            return true;
        }, _ => { }, _ => { }, _ => { }, () => 60);
        _manager = new PanelManager(_backend, PanelStore.InMemory(), host, _backend.Clock, _log.Logger,
            new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan }, drivers: [driver]);
        _manager.Start(passes: false);
        return _manager;
    }

    private static byte[] Answer() => new byte[512];

    private FakePanelDevice Plug(ushort vendor, ushort product, PanelTransport transport)
    {
        _backend.Add(new FakeUsbDevice(new UsbDeviceId(vendor, product, 0, 7, "0-7")));
        var device = _devices.Add(new PanelCandidate(vendor, product, 0, 7, "0-7"), transport);
        PanelTraceEntry? answered = null;
        device.Usb.Respond(last =>
        {
            if (last == null || ReferenceEquals(last, answered))
                return null;
            answered = last;
            return Answer();
        });
        device.Serial.Respond(w => w.Data[0] == 0x01 && w.Data[1] == 0xef ? [.. "chs_5inch.dev1_rom1.88"u8, 0]
            : w.Data[0] == 0xcf ? [.. "needReSend:0"u8] : null);
        return device;
    }

    private async Task Until(Func<Task<bool>> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await done())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{what}\nThe log:\n{_log}");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A new 8 × 2 frame, steel blue, to the panel's sink, as the render loop hands one over.</summary>
    private void Deliver()
    {
        if (_sink is not { } sink)
            return;
        var frameId = Interlocked.Increment(ref _frameId);
        using var pixels = new SKBitmap(new SKImageInfo(8, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        pixels.Erase(SKColors.SteelBlue);
        using var image = SKImage.FromBitmap(pixels);
        var frame = new RenderedFrame(image)
        {
            ProfileId = "p-1",
            Rev = 1,
            FrameId = frameId,
            Tick = frameId,
            Scale = 1,
            PixelWidth = pixels.Width,
            PixelHeight = pixels.Height,
            ProfileWidth = pixels.Width,
            ProfileHeight = pixels.Height,
            Bounds = [],
            ContentHash = (ulong)frameId,
        };
        sink.OnFrame(frame);
        frame.Close();
    }

    private async Task<PanelManager> Running(IPanelDriver driver, string family, string model)
    {
        var manager = Manager(driver);
        await manager.RunPassAsync();
        var found = Assert.Single(await manager.ListAsync());
        Assert.Equal((family, model), (found.Saved.Family, found.Saved.Model));
        await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1", Brightness = 100 });
        await Until(async () => (await manager.ListAsync())[0].State == "running", "the panel never ran");
        return manager;
    }

    [Theory]
    [InlineData("turing", (ushort)0x0021, "rev21inchusb")]
    [InlineData("lianli", (ushort)0xA034, "hydroshiftiilcd")]
    public async Task AUsbPanel_GetsTheEnginesJpeg_AndItsStopTurnsItDown(string family, ushort product, string model)
    {
        var device = Plug(0x1CBE, product, PanelTransport.Usb);
        IPanelDriver driver = family == "turing"
            ? new TuringDriver(_devices) { Wait = (_, stop) => stop.ThrowIfCancellationRequested(), Timestamp = () => 7 }
            : new LianLiDriver(_devices) { Wait = (_, stop) => stop.ThrowIfCancellationRequested(), Timestamp = () => 7 };
        var manager = await Running(driver, family, model);

        // The first JPEG: Turing's after its 101 packet as a write of its own, Lian Li's in the packet's write.
        byte[]? jpeg = null;
        await Until(() =>
        {
            Deliver();
            var writes = device.Trace.Entries.Where(e => e.Op == PanelTraceOp.UsbWrite).Select(e => e.Data).ToList();
            jpeg = family == "turing"
                ? writes.FirstOrDefault(w => w.Length > 2 && w[0] == 0xFF && w[1] == 0xD8)
                : writes.Where(w => w.Length > 514 && w[512] == 0xFF && w[513] == 0xD8).Select(w => w[512..]).Skip(1).FirstOrDefault(); // the first is the black clear
            return Task.FromResult(jpeg != null);
        }, "no JPEG was written");
        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));

        using (var decoded = SKBitmap.Decode(jpeg))
        {
            Assert.Equal((480, 480), (decoded.Width, decoded.Height));
            var centre = decoded.GetPixel(240, 240);
            Assert.True(Math.Abs(centre.Red - SKColors.SteelBlue.Red) <= 6 && Math.Abs(centre.Blue - SKColors.SteelBlue.Blue) <= 6, $"the centre is {centre}");
        }
        Assert.Contains(_log.Lines, l => l.Contains("q90", StringComparison.Ordinal));

        // The stop: brightness 0 (Lian Li's StopMedia 123 before it), each its packet, then the device let go.
        var lastWrites = device.Trace.Entries.Where(e => e.Op == PanelTraceOp.UsbWrite).TakeLast(family == "turing" ? 1 : 2).Select(e => Convert.ToHexStringLower(e.Data)).ToList();
        var variant = family == "turing" ? UsbVariant.Turing : UsbVariant.LianLi;
        string Packet(byte[] command) => Convert.ToHexStringLower(TuringUsbProtocol.Packet(variant, command));
        List<string> expected = family == "turing"
            ? [Packet(TuringUsbProtocol.WithValue(14, 0, 7))]
            : [Packet(TuringUsbProtocol.Command(123, 7)), Packet(TuringUsbProtocol.WithValue(14, 0, 7))];
        Assert.Equal(expected, lastWrites);
        Assert.Equal("h1 close usb if0", PanelTrace.FormatLine(device.Trace.Entries[^1]));
        Assert.Equal(0, device.CallsAfterDispose);
    }

    [Fact]
    public async Task ASerialRevisionC_GetsTheEnginesBgr_AsAWholeFrame_AndItsStopTurnsItOff()
    {
        var device = Plug(0x1D6B, 0x0106, PanelTransport.Serial);
        var manager = await Running(new TuringDriver(_devices) { Wait = (_, stop) => stop.ThrowIfCancellationRequested() }, "turing", "rev5inch");

        // The whole frame: the 5"'s 60 blocks of 80 x 80, each its UpdateBitmap.
        List<byte[]>? frame = null;
        await Until(() =>
        {
            Deliver();
            var writes = device.Trace.Entries.Where(e => e.Op == PanelTraceOp.SerialWrite).Select(e => e.Data).ToList();
            if (writes.Count(w => w is [0xcc, 0xef, 0x69, ..]) >= 60)
                frame = writes;
            return Task.FromResult(frame != null);
        }, "no frame was written");
        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));

        // The blocks' rows put at their places, three bytes a pixel (B, G, R): the 8 x 2 frame fitted to 800 x 480, so
        // its centre is steel blue.
        var screen = TuringSerialProtocolTests.ScreenCe(frame!, 800, 800, 480, 3);
        var centre = ((240 * 800) + 400) * 3;
        Assert.Equal([SKColors.SteelBlue.Blue, SKColors.SteelBlue.Green, SKColors.SteelBlue.Red], screen[centre..(centre + 3)]);
        var lines = device.Trace.Entries.TakeLast(2).Select(PanelTrace.FormatLine).ToList();
        Assert.Equal("h2 serial> 7bef6900000001000000" + "00" + new string('0', 239 * 2), lines[0]); // brightness 0
        Assert.Equal("h2 close serial", lines[1]);
        Assert.Equal(0, device.CallsAfterDispose);
    }
}
