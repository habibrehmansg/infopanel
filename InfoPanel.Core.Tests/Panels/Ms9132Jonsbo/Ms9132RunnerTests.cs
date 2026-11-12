using InfoPanel.Core.Tests.Panels.Ms9132Jonsbo.V1Oracle;
using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.Ms9132Jonsbo;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Usb;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo;

/// <summary>
/// The MS9132 families run by the engine in process: the manager lists the device (the engine's fake USB
/// backend) and offers it to the package's families in their order, jonsbo then vmax; the EDID tells which takes it; the
/// runner turns a rendered frame into the engine's BGR888 (dimmed by the engine for the brightness), and the session
/// writes it with its header and trailer in 64 KiB writes; the VMAX is paced at most 30 a second (its <c>MaxFps</c>),
/// the DS339 at the engine's rate; stopping turns the DS339 off and sends the VMAX nothing.
/// </summary>
public sealed class Ms9132RunnerTests : IAsyncDisposable
{
    private readonly FakeUsbBackend _backend = new();
    private readonly Ms9132Devices _devices = new();
    private readonly KeptLog _log = new();
    private PanelManager? _manager;
    private volatile PanelSink? _sink;
    private long _frameId;

    public async ValueTask DisposeAsync()
    {
        if (_manager != null)
            await _manager.DisposeAsync();
    }

    private static void NoWait(TimeSpan _, CancellationToken stop) => stop.ThrowIfCancellationRequested();

    private (PanelManager Manager, Ms9132Fake Fake) Plug(byte[] edid)
    {
        var at = Ms9132Fake.Candidate;
        _backend.Add(new FakeUsbDevice(new UsbDeviceId(at.VendorId, at.ProductId, at.Bus, at.Address, at.PortPath)));
        var fake = Ms9132Fake.Add(_devices, edid);
        var host = new PanelHost(_ => true, sink =>
        {
            _sink = (PanelSink)sink;
            return true;
        }, _ => { }, _ => { }, _ => { }, () => 60);
        IPanelDriver[] drivers = [new JonsboDriver(_devices) { Wait = NoWait }, new VmaxDriver(_devices) { Wait = NoWait }];
        _manager = new PanelManager(_backend, PanelStore.InMemory(), host, _backend.Clock, _log.Logger,
            new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan }, drivers: drivers);
        _manager.Start(passes: false);
        return (_manager, fake);
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

    private static readonly SKColor Steel = SKColors.SteelBlue;

    /// <summary>A new 8 × 2 frame, steel blue, to the panel's sink, as the render loop hands one over.</summary>
    private void Deliver()
    {
        if (_sink is not { } sink)
            return;
        var frameId = Interlocked.Increment(ref _frameId);
        using var pixels = new SKBitmap(new SKImageInfo(8, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        pixels.Erase(Steel);
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

    /// <summary>The first whole frame written after the trace's entry <paramref name="from"/>: its writes joined.</summary>
    private async Task<byte[]> FirstFrame(Ms9132Fake fake, int from, int width, int height)
    {
        var length = Ms9132Protocol.FrameLength(width, height);
        byte[]? frame = null;
        await Until(() =>
        {
            Deliver();
            var writes = fake.Writes(from);
            var joined = writes.SelectMany(w => w).Take(length).ToArray();
            if (joined.Length == length)
                frame = joined;
            return Task.FromResult(frame != null);
        }, "no whole frame was written");
        return frame!;
    }

    public static TheoryData<string, string, string, int, int, int> Panels() => new()
    {
        { "ds339", "jonsbo", "ds339", 376, 960, 60 },
        { "vmax", "vmax", "vmax46inch", 320, 960, 30 },
    };

    [Theory]
    [MemberData(nameof(Panels))]
    public async Task APanel_IsTakenByItsFamily_AndGetsTheEnginesBgr888_InItsFrame_AtItsPace(string edid, string family, string model, int width, int height, int fps)
    {
        var (manager, fake) = Plug(edid == "ds339" ? EdidSamples.Ds339 : EdidSamples.Vmax);
        await manager.RunPassAsync();
        var found = Assert.Single(await manager.ListAsync());
        Assert.Equal((family, model), (found.Saved.Family, found.Saved.Model));
        var mark = fake.Device.Trace.Entries.Count;
        await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1", Brightness = 100 });
        await Until(async () => (await manager.ListAsync())[0].State == "running", "the panel never ran");

        var frame = await FirstFrame(fake, mark, width, height);
        // The engine's frame rate is 60: the VMAX's own ceiling holds it to 30 (VmaxPanelDeviceTask.cs:143), the DS339 none.
        // The runner keeps the ceiling at every write, and the render loop is asked for it from the pace's first check.
        await Until(() =>
        {
            Deliver();
            return Task.FromResult(_sink!.Request.MaxFps == fps);
        }, $"the render loop was never asked for {fps} fps");
        Assert.Equal(edid == "ds339" ? FrameHeader(width, height) : VmaxMs9132.Prefix.ToArray(), frame[..8]);
        Assert.Equal(Ms9132Protocol.Trailer.ToArray(), frame[^8..]);
        // The centre pixel (the 8 × 2 frame fitted across the panel's width), in BGR order: 1.4.x's conversion of it.
        var expected = new byte[3];
        V1JonsboPanelDeviceTask.GenerateBgrFrame([Steel.Red, Steel.Green, Steel.Blue, 255], 4, 1, 1, 100, expected);
        var centre = 8 + ((height / 2) * width + width / 2) * 3;
        Assert.Equal(expected, frame[centre..(centre + 3)]);
        Assert.Contains(_log.Lines, l => l.Contains("raw Bgr888", StringComparison.Ordinal));

        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));
        // The stop: the DS339 powered off (1.4.x's dispose), the VMAX sent nothing (1.4.x's dispose); both let go.
        var tail = fake.Device.Trace.Entries.TakeLast(4).ToList();
        Assert.Equal(PanelTraceOp.Close, tail[2].Op);
        Assert.Equal(PanelTraceOp.Close, tail[3].Op);
        if (edid == "ds339")
            Assert.Equal(Ms9132Protocol.Feature(JonsboMs9132.PowerOff), tail[0].Data);
        else
            Assert.DoesNotContain(fake.Reports(), r => r is [0xA6, 0x07, 0x00, ..]);
        Assert.Equal(0, fake.Device.CallsAfterDispose);
    }

    private static byte[] FrameHeader(int width, int height)
    {
        var header = new byte[8];
        JonsboMs9132.Header(width, height, header);
        return header;
    }

    [Fact]
    public async Task ADs916_GetsTheEnginesJpegAtQuality80_AsOneRawWrite_AtTheSizeItReports()
    {
        var at = new PanelCandidate(0x33C3, 0xF101, 1, 6, "1-2");
        _backend.Add(new FakeUsbDevice(new UsbDeviceId(at.VendorId, at.ProductId, at.Bus, at.Address, at.PortPath)));
        var device = _devices.Add(at, PanelTransport.Serial);
        device.Serial.Respond(write => write.Data.AsSpan().SequenceEqual(V1JonsboSerialDevice.HandshakeCommand)
            ? System.Text.Encoding.ASCII.GetBytes("VMAXB160462*1920S261301227") : null);
        var host = new PanelHost(_ => true, sink =>
        {
            _sink = (PanelSink)sink;
            return true;
        }, _ => { }, _ => { }, _ => { }, () => 60);
        _manager = new PanelManager(_backend, PanelStore.InMemory(), host, _backend.Clock, _log.Logger,
            new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan }, drivers: [new JonsboDriver(_devices) { Wait = NoWait }]);
        _manager.Start(passes: false);
        await _manager.RunPassAsync();
        var found = Assert.Single(await _manager.ListAsync());
        Assert.Equal(("jonsbo", "ds916", "S261301227"), (found.Saved.Family, found.Saved.Model, found.Saved.Serial));
        var mark = device.Trace.Entries.Count;
        await _manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1", Brightness = 100 });
        await Until(async () => (await _manager.ListAsync())[0].State == "running", "the panel never ran");

        byte[]? jpeg = null;
        await Until(() =>
        {
            Deliver();
            // A frame's write: after the start's handshake, a write that isn't the handshake.
            jpeg = device.Trace.Entries.Skip(mark).Where(e => e.Op == PanelTraceOp.SerialWrite && e.Data.Length > 4).Select(e => e.Data).FirstOrDefault();
            return Task.FromResult(jpeg != null);
        }, "no frame was written");
        Assert.True(await _manager.StopAsync(TimeSpan.FromSeconds(3)));

        using (var decoded = SKBitmap.Decode(jpeg))
            Assert.Equal((462, 1920), (decoded.Width, decoded.Height));
        Assert.Contains(_log.Lines, l => l.Contains("JPEG q80", StringComparison.Ordinal));
        Assert.Equal(PanelTraceOp.Close, device.Trace.Entries[^1].Op); // the stop sends nothing
    }

    [Fact]
    public async Task ADs339AtHalfBrightness_GetsItsFrameDimmedByTheEngine_As1_4sConversion()
    {
        var (manager, fake) = Plug(EdidSamples.Ds339);
        await manager.RunPassAsync();
        var found = Assert.Single(await manager.ListAsync());
        var mark = fake.Device.Trace.Entries.Count;
        await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1", Brightness = 50 });
        await Until(async () => (await manager.ListAsync())[0].State == "running", "the panel never ran");

        var frame = await FirstFrame(fake, mark, 376, 960);
        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));

        // JonsboPanelDeviceTask.cs:430-458: each channel × 0.5, truncated; the engine's colour matrix within 1.
        var expected = new byte[3];
        V1JonsboPanelDeviceTask.GenerateBgrFrame([Steel.Red, Steel.Green, Steel.Blue, 255], 4, 1, 1, 50, expected);
        var centre = 8 + (480 * 376 + 188) * 3;
        for (var i = 0; i < 3; i++)
            Assert.InRange(frame[centre + i], expected[i] - 1, expected[i] + 1);
    }
}
