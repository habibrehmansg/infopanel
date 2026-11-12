using System.Buffers.Binary;
using System.Text;
using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.Jl;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Usb;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Jl;

/// <summary>
/// The JL driver run by the engine in process (the runner test): the manager lists the device (the engine's fake USB backend),
/// offers it to the driver, which probes and opens it over the kit's fake devices, and the runner turns a rendered frame
/// into the engine's JPEG at the family's quality, encoded again 15 lower a try until it fits the panel's 80 KB (a frame
/// still over it at q30 isn't sent), which the driver writes after a keep-alive in its envelope; without a new frame the
/// runner's keep-alive, a second later, is the driver's bare <c>0x11</c>; stopping sends close and lets the port go.
/// </summary>
public sealed class JlRunnerTests : IAsyncDisposable
{
    private const int Width = 480;
    private const int Height = 960;
    private const int Cap = 80 * 1024;

    private static readonly PanelCandidate Candidate = new(0x33C3, 0x7788, 0, 7, "0-7");

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

    private static readonly byte[] Info = JlProtocol.Frame(JlProtocol.GetDeviceInfo, Encoding.UTF8.GetBytes(
        "{\"status\":200,\"uid\":\"R1\",\"version\":\"Ver1.0\",\"width\":480,\"height\":960,\"angle\":0}"));

    private static JlDriver Driver(FakePanelDevices devices) => new(devices) { Wait = (_, stop) => stop.ThrowIfCancellationRequested() };

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

    private static FakePanelDevice Add(FakePanelDevices devices)
    {
        var device = devices.Add(Candidate, PanelTransport.Serial);
        device.Serial.Respond(seen => seen.Data.AsSpan().SequenceEqual(JlProtocol.DeviceInfoQuery) ? Info : null);
        return device;
    }

    private async Task Until(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{what}\nThe log:\n{_log}");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// A 480 × 960 frame: its top <paramref name="noiseRows"/> rows noise (seeded), the rest steel blue; as RGBA, opaque.
    /// </summary>
    private static SKBitmap Picture(int noiseRows)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.SteelBlue);
        var random = new Random(noiseRows);
        var row = new byte[Width * 4];
        for (var y = 0; y < noiseRows; y++)
        {
            random.NextBytes(row);
            for (var x = 3; x < row.Length; x += 4)
                row[x] = 255;
            System.Runtime.InteropServices.Marshal.Copy(row, 0, bitmap.GetPixels() + y * bitmap.RowBytes, row.Length);
        }
        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    /// <summary>The picture to the panel's sink, as the render loop hands one over.</summary>
    private void Deliver(SKBitmap pixels)
    {
        if (_sink is not { } sink)
            return;
        var frameId = Interlocked.Increment(ref _frameId);
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

    /// <summary>
    /// 1.4.x's loop (JlPanelDeviceTask.cs:240-249, verbatim but for the encoder, the engine's): at <paramref name="quality"/>,
    /// then 15 lower a try down to 30, the first JPEG within <paramref name="maxBytes"/> (or the one at 30), and its quality.
    /// </summary>
    private static (byte[] Jpeg, int Quality) V1FirstFitting(SKBitmap picture, int quality, int maxBytes)
    {
        byte[] Encode(int q) => PanelJpeg.Encode(picture.GetPixelSpan(), picture.Width, picture.Height, q);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var data = Encode(quality);
            if (data.Length <= maxBytes || quality <= 30)
                return (data, quality);
            quality = Math.Max(30, quality - 15);
        }
        return (Encode(30), 30);
    }

    /// <summary>The JPEG in each envelope the device got, in order (a write whose first 4 bytes give its length less 6).</summary>
    private static List<byte[]> Envelopes(FakePanelDevice device) =>
        device.Trace.Entries.Where(e => e.Op == PanelTraceOp.SerialWrite && e.Data.Length >= 6 && BinaryPrimitives.ReadInt32LittleEndian(e.Data) == e.Data.Length - 6)
            .Select(e => e.Data[4..^2]).ToList();

    [Fact]
    public async Task APanel_GetsTheEnginesJpeg_StepsDownToFit_NothingOverItAtTheFloor_KeepAlivesBetween_AndItsStopSendsClose()
    {
        // The session's capabilities, from a start of its own over another fake.
        var probing = new FakePanelDevices();
        Add(probing);
        var opened = Driver(probing).Open(Candidate, new PanelIdentity("chillarc360", Width, Height, null, null), new PanelOpenOptions(100), TestContext.Current.CancellationToken);
        var caps = opened.Session!.Capabilities;
        opened.Session.Close(screenOff: false, TimeSpan.Zero);

        // A picture over 80 KB at q80 that fits at a lower quality above the floor, and one over it even at q30; the first's
        // JPEG found by 1.4.x's own loop (JlPanelDeviceTask.cs:240-249: 80, 65, 50, 35, 30) over the engine's encoder.
        var rows = 0;
        int Encoded(int noiseRows, int quality)
        {
            using var picture = Picture(noiseRows);
            return PanelJpeg.Encode(picture.GetPixelSpan(), Width, Height, quality).Length;
        }
        do
            rows += 40;
        while (rows < Height && Encoded(rows, 80) <= Cap);
        Assert.True(Encoded(rows, 80) > Cap && Encoded(rows, 30) <= Cap, $"no band of noise between the cap at q80 and at q30 ({rows} rows)");
        Assert.True(Encoded(Height, 30) > Cap, "a picture of noise fits the cap at q30");
        using var fits = Picture(rows);
        using var over = Picture(Height);
        var (expected, quality) = V1FirstFitting(fits, caps.JpegQuality, Cap);
        Assert.InRange(quality, 35, 65); // a step between the first try and the floor: a jump to the floor shows
        Assert.InRange(expected.Length, 1, Cap);

        var device = Add(_devices);
        _backend.Add(new FakeUsbDevice(new UsbDeviceId(Candidate.VendorId, Candidate.ProductId, Candidate.Bus, Candidate.Address, Candidate.PortPath)));
        var manager = Manager(Driver(_devices));
        await manager.RunPassAsync();
        var found = Assert.Single(await manager.ListAsync());
        Assert.Equal((JlDriver.FamilyId, "chillarc360"), (found.Saved.Family, found.Saved.Model));
        await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1", Brightness = 100 });
        await Until(() => manager.ListAsync().GetAwaiter().GetResult()[0].State == "running", "the panel never ran");

        // The first frame: the engine's JPEG at the quality that fits, in its envelope after a keep-alive, then FF D9 FF D9.
        await Until(() =>
        {
            Deliver(fits);
            return Envelopes(device).Count > 0;
        }, "no frame was written");
        Assert.Equal(Convert.ToHexStringLower(expected), Convert.ToHexStringLower(Envelopes(device)[0]));
        var lines = device.Trace.Entries.Select(PanelTrace.FormatLine).ToList();
        var at = lines.FindIndex(l => l.StartsWith("h2 serial> " + Convert.ToHexStringLower(BitConverter.GetBytes(expected.Length)), StringComparison.Ordinal));
        Assert.True(at > 0, string.Join("\n", lines.Take(20)));
        Assert.Equal($"h2 serial> {Convert.ToHexStringLower(JlProtocol.KeepAliveFrame)}", lines[at - 1]);
        Assert.Equal($"h2 serial> {Convert.ToHexStringLower(JlProtocol.FrameTerminator.ToArray())}", lines[at + 1]);
        Assert.Contains(_log.Lines, l => l.Contains("JPEG q80", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains($"encoded again at q{quality}", StringComparison.Ordinal));

        // No new frame: the keep-alive the session asks for each second is the bare 0x11 (JlPanelDeviceTask.cs:183-186).
        var keepAlive = $"h2 serial> {Convert.ToHexStringLower(JlProtocol.KeepAliveFrame)}";
        await Until(() => device.Trace.Entries.Skip(at + 2).Select(PanelTrace.FormatLine).Any(l => l == keepAlive), "no keep-alive came after the frame");

        // A frame over the cap at q30 isn't sent: the panel keeps the last, and the keep-alive still goes.
        await Until(() =>
        {
            Deliver(over);
            return _log.Lines.Any(l => l.Contains("its floor", StringComparison.Ordinal));
        }, "the frame over the cap was never refused");
        var refusedAt = device.Trace.Entries.Count;
        await Until(() => device.Trace.Entries.Skip(refusedAt).Select(PanelTrace.FormatLine).Any(l => l == keepAlive), "no keep-alive came after the refused frame");
        Assert.All(Envelopes(device), jpeg => Assert.Equal(expected, jpeg));

        // The stop: close, then the port let go.
        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal([$"h2 serial> {Convert.ToHexStringLower(JlProtocol.CloseFrame)}", "h2 close serial"], device.Trace.Entries.TakeLast(2).Select(PanelTrace.FormatLine));
        Assert.Equal(0, device.CallsAfterDispose);
    }
}
