using System.Text;
using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Thermaltake;
using InfoPanel.Panels.Usb;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermaltake;

/// <summary>
/// The Thermaltake driver run by the engine in process (§2.2 (7)): the manager lists the device (the engine's fake USB
/// backend), offers it to the driver, which probes and opens it over the kit's fake devices, and the runner turns a
/// rendered frame into the engine's JPEG at the family's quality and writes it in 1000-byte chunks; stopping turns
/// realtime display off and lets the device go. Brightness stays at 100: the engine's dimming of the frame for
/// <c>DimInSoftware</c> comes with the engine side of the families.
/// </summary>
public sealed class ThermaltakeRunnerTests : IAsyncDisposable
{
    private const long Date = 1_731_400_000_456;

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

    private static readonly byte[] Ok = ThermaltakeProtocol.Report(ThermaltakeProtocol.Frame(Encoding.ASCII.GetBytes("200 OK\r\n\r\n")));

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

    private (FakePanelDevice Device, ThermaltakeDriver Driver) Plug(ushort vendor, ushort product)
    {
        _backend.Add(new FakeUsbDevice(new UsbDeviceId(vendor, product, 0, 7, "0-7")));
        var device = _devices.Add(new PanelCandidate(vendor, product, 0, 7, "0-7"), PanelTransport.Hid);
        device.Hid.MaxOutputReportLength = ThermaltakeProtocol.ReportSize;
        device.Hid.MaxInputReportLength = ThermaltakeProtocol.ReportSize;
        device.Hid.Respond(_ => Ok);
        return (device, new ThermaltakeDriver(_devices) { Now = () => Date, Wait = (_, stop) => stop.ThrowIfCancellationRequested() });
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

    public static TheoryData<ushort, ushort, string, int, int> Models() => new()
    {
        { 0x264A, 0x2347, "toughliquid6inch", 1480, 720 },
        { 0x26CE, 0x0A10, "asrockphantomgaming360lcd", 480, 480 },
    };

    [Theory]
    [MemberData(nameof(Models))]
    public async Task APanel_GetsTheEnginesJpegAtQuality85_InChunks_AndItsStopTurnsRealtimeDisplayOff(ushort vendor, ushort product, string model, int width, int height)
    {
        var (device, driver) = Plug(vendor, product);
        var manager = Manager(driver);
        await manager.RunPassAsync();
        var found = Assert.Single(await manager.ListAsync());
        Assert.Equal((ThermaltakeDriver.FamilyId, model), (found.Saved.Family, found.Saved.Model));
        await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1", Brightness = 100 });
        await Until(async () => (await manager.ListAsync())[0].State == "running", "the panel never ran");

        // The first whole frame: its chunk 0 says how many there are; chunks 0 to n - 1 in order, after the handshake.
        List<byte[]>? chunks = null;
        await Until(() =>
        {
            Deliver();
            var images = device.Trace.Entries.Where(e => e.Op == PanelTraceOp.HidWrite && e.Data[1] == 0x5C).Select(e => e.Data).ToList();
            if (images.Count > 0)
            {
                var total = images[0][5] << 8 | images[0][6];
                if (images.Count >= total)
                    chunks = images.Take(total).ToList();
            }
            return Task.FromResult(chunks != null);
        }, "no whole frame was written");
        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));

        var count = chunks!.Count;
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(new byte[] { 0x00, 0x5C, 0x03, 0xFD, 0x00, (byte)(count >> 8), (byte)count, (byte)(i >> 8), (byte)i, 0x01 }, chunks[i][..10]);
            Assert.Equal(ThermaltakeProtocol.ReportSize, chunks[i].Length);
        }
        var jpeg = chunks.SelectMany(c => c.Skip(1 + ThermaltakeProtocol.ImageHeaderSize)).ToArray(); // the last chunk's zeros after the image's end
        using (var decoded = SKBitmap.Decode(jpeg))
        {
            Assert.Equal((width, height), (decoded.Width, decoded.Height));
            var centre = decoded.GetPixel(width / 2, height / 2);
            Assert.True(Math.Abs(centre.Red - SKColors.SteelBlue.Red) <= 6 && Math.Abs(centre.Blue - SKColors.SteelBlue.Blue) <= 6, $"the centre is {centre}");
        }
        Assert.Contains(_log.Lines, l => l.Contains("q85", StringComparison.Ordinal));

        // The stop: realtime display off (SeqNumber 102: conn 100, enable 101), its reply read, the device let go.
        var last = device.Trace.Entries.TakeLast(3).Select(PanelTrace.FormatLine).ToList();
        var off = Convert.ToHexStringLower(ThermaltakeProtocol.Report(ThermaltakeProtocol.RealtimeDisplay(false, 102, Date)));
        Assert.Equal([$"h1 hid> {off}", $"h1 hid< {Convert.ToHexStringLower(Ok)}", "h1 close hid"], last);
        Assert.Equal(0, device.CallsAfterDispose);
    }
}
