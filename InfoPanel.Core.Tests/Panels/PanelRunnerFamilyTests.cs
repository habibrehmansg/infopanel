using System.Text.Json;
using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.Sdk;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels;

/// <summary>
/// The runner with what the other families' sessions declare: a format beside JPEG and little-endian RGB565 is written
/// (the session isn't refused), the session's own keep-alive interval is kept in place of the runner's second, and a
/// panel the engine dims gets a new frame at a new brightness.
/// </summary>
public sealed class PanelRunnerFamilyTests
{
    private readonly FakeUsbClock _clock = new();
    private readonly KeptLog _log = new();
    private readonly List<PanelSink> _refreshed = [];
    private PanelSink? _sink;
    private long _frameId;

    private PanelRunner Runner(PanelCapabilities capabilities, out Session session, PanelRunnerOptions? options = null)
    {
        var driver = new Driver(_clock, capabilities);
        session = driver.Session;
        var runner = new PanelRunner(driver, new PanelCandidate(0x1209, 0x0001, 1, 2, "1-2"), new PanelIdentity("m", capabilities.Width, capabilities.Height, null, null),
            "test panel", "p-1", 100, PanelRotation.None, sink => { _sink = sink; return true; }, _ => { }, _ => { },
            sink => { lock (_refreshed) _refreshed.Add(sink); }, () => 60, _clock, options ?? new PanelRunnerOptions(), _log.Logger);
        runner.Start();
        Until(() => runner.Running, "the panel never ran");
        return runner;
    }

    private void Deliver(SKColor color, ulong hash)
    {
        using var pixels = new SKBitmap(new SKImageInfo(4, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        pixels.Erase(color);
        using var image = SKImage.FromBitmap(pixels);
        var frameId = Interlocked.Increment(ref _frameId);
        var frame = new RenderedFrame(image)
        {
            ProfileId = "p-1",
            Rev = 1,
            FrameId = frameId,
            Tick = frameId,
            Scale = 1,
            PixelWidth = 4,
            PixelHeight = 2,
            ProfileWidth = 4,
            ProfileHeight = 2,
            Bounds = [],
            ContentHash = hash,
        };
        _sink!.OnFrame(frame);
        frame.Close();
    }

    private static void Until(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException(what);
            Thread.Sleep(5);
        }
    }

    private static async Task Stop(PanelRunner runner)
    {
        runner.RequestStop();
        await runner.Ended.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_session_taking_RGB888_alone_is_written_RGB888()
    {
        var runner = Runner(new PanelCapabilities(4, 2, [PanelFrameFormat.Rgb888], MaxFps: 30), out var session);

        Deliver(new SKColor(10, 20, 30), 1);
        Until(() => session.Writes.Count >= 1, "no frame was written");
        await Stop(runner);

        var write = session.Writes[0];
        Assert.Equal(PanelFrameFormat.Rgb888, write.Format);
        Assert.Equal(4 * 2 * 3, write.Bytes.Length);
        Assert.Equal(new byte[] { 10, 20, 30 }, write.Bytes[..3]);
    }

    [Fact]
    public async Task A_jpeg_frame_over_its_cap_at_the_floor_isnt_written_and_the_next_that_fits_is()
    {
        var capabilities = new PanelCapabilities(4, 2, [PanelFrameFormat.Jpeg], MaxFps: 30, MaxFrameBytes: 10, MinJpegQuality: 80);
        var runner = Runner(capabilities, out var session, new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan });

        Deliver(SKColors.SteelBlue, 1); // no JPEG is 10 bytes
        Until(() => _log.Lines.Any(l => l.Contains("isn't sent")), "the frame over its cap wasn't refused");
        await Stop(runner);

        Assert.Empty(session.Writes);
    }

    private void DeliverPixels(int width, int height, Func<int, int, SKColor> color, ulong hash)
    {
        using var pixels = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels.SetPixel(x, y, color(x, y));
        using var image = SKImage.FromBitmap(pixels);
        var frameId = Interlocked.Increment(ref _frameId);
        var frame = new RenderedFrame(image)
        {
            ProfileId = "p-1",
            Rev = 1,
            FrameId = frameId,
            Tick = frameId,
            Scale = 1,
            PixelWidth = width,
            PixelHeight = height,
            ProfileWidth = width,
            ProfileHeight = height,
            Bounds = [],
            ContentHash = hash,
        };
        _sink!.OnFrame(frame);
        frame.Close();
    }

    [Fact]
    public async Task Frames_refused_over_their_cap_one_after_another_still_let_a_due_keep_alive_go_with_the_kept_frame()
    {
        // A flat frame fits in 3000 bytes; noise at q90 (some 20 KB) doesn't. Each encode (the flat frame's first) hands the
        // sink a new noise frame 250 ms later, so the panel's thread never waits: while refused frames keep coming, the
        // session's 1 s keep-alive still goes, with the frame it kept.
        var capabilities = new PanelCapabilities(128, 64, [PanelFrameFormat.Jpeg], MaxFps: 30, JpegQuality: 90, MaxFrameBytes: 3000, MinJpegQuality: 90,
            KeepAliveEvery: TimeSpan.FromSeconds(1));
        var noise = new Random(7);
        var flooding = 0;
        Session? watched = null;
        ulong hash = 100;
        var options = new PanelRunnerOptions
        {
            KeepAlive = Timeout.InfiniteTimeSpan,
            AfterEncode = () =>
            {
                if (Volatile.Read(ref flooding) is 0 or >= 60 || watched?.Writes.Count >= 2)
                    return;
                Interlocked.Increment(ref flooding);
                _clock.Advance(TimeSpan.FromMilliseconds(250));
                DeliverPixels(128, 64, (_, _) => new SKColor((byte)noise.Next(256), (byte)noise.Next(256), (byte)noise.Next(256)), ++hash);
            },
        };
        var runner = Runner(capabilities, out var session, options);
        watched = session;

        Volatile.Write(ref flooding, 1);
        DeliverPixels(128, 64, (_, _) => SKColors.SteelBlue, 1);
        Until(() => session.Writes.Count >= 2, "no keep-alive was written");
        await Stop(runner);

        var (first, again) = (session.Writes[0], session.Writes[1]);
        Assert.True(again.Repeat);
        Assert.Equal(first.Bytes, again.Bytes);
        Assert.InRange(again.AtMs - first.AtMs, 1000, 1250);
    }

    [Fact]
    public async Task The_last_frame_is_written_again_at_the_sessions_keep_alive_interval_not_the_runners()
    {
        var capabilities = new PanelCapabilities(4, 2, [PanelFrameFormat.Rgb565Le], MaxFps: 30, KeepAliveEvery: TimeSpan.FromSeconds(3));
        var runner = Runner(capabilities, out var session, new PanelRunnerOptions { KeepAlive = TimeSpan.FromSeconds(1) });

        Deliver(SKColors.SteelBlue, 1);
        Until(() => session.Writes.Count >= 2, "no keep-alive was written");
        await Stop(runner);

        var (first, again) = (session.Writes[0], session.Writes[1]);
        Assert.False(first.Repeat);
        Assert.True(again.Repeat);
        Assert.Equal(3000, again.AtMs - first.AtMs);
    }

    [Fact]
    public async Task A_panel_the_engine_dims_is_sent_its_frame_again_dimmed_when_the_brightness_changes()
    {
        var capabilities = new PanelCapabilities(4, 2, [PanelFrameFormat.Rgb888], MaxFps: 30) { DimInSoftware = true };
        var runner = Runner(capabilities, out var session, new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan });
        Deliver(new SKColor(200, 200, 200), 1);
        Until(() => session.Writes.Count >= 1, "no frame was written");

        runner.Brightness = 50;
        Until(() => { lock (_refreshed) return _refreshed.Count >= 1; }, "the runner asked for no new frame");
        Deliver(new SKColor(200, 200, 200), 1); // the same picture, as the render loop's refresh sends it
        Until(() => session.Writes.Count >= 2, "the frame wasn't sent again");
        await Stop(runner);

        Assert.Equal(200, session.Writes[0].Bytes[0]);
        Assert.InRange(session.Writes[1].Bytes[0], 99, 101);
    }

    [Fact]
    public async Task A_hosted_start_carries_the_family_settings_to_the_host_and_the_sessions_new_capabilities_back()
    {
        // The open request with the panel's family settings, and the answer with a crop, software dimming, a JPEG step and
        // four overlays of 1.4's mask size (about 10 KB each), within the pipe's 64 KiB a message.
        using var settings = JsonDocument.Parse("""{"mask":"roundedLeft","flickerFix":true}""");
        var overlay = new byte[10_400];
        new Random(3).NextBytes(overlay);
        var capabilities = new PanelCapabilities(480, 480, [PanelFrameFormat.Jpeg], MaxFps: 25, MaxFrameBytes: 450_000, KeepAliveEvery: TimeSpan.FromSeconds(3))
        {
            DimInSoftware = true,
            Crop = new PanelCrop(0, 0, 480, 462),
            JpegQualityStep = 15,
            Overlays = new Dictionary<int, byte[]> { [0] = overlay, [90] = overlay, [180] = overlay, [270] = overlay },
        };
        using var pipe = new MemoryStream();

        await InfoPanel.Panels.Hosted.PanelHostWire.WriteAsync(pipe, new InfoPanel.Panels.Hosted.PanelHostRequest { Op = "open", FamilySettings = settings.RootElement }, TestContext.Current.CancellationToken);
        await InfoPanel.Panels.Hosted.PanelHostWire.WriteAsync(pipe, new InfoPanel.Panels.Hosted.PanelHostReply { Capabilities = capabilities }, TestContext.Current.CancellationToken);
        pipe.Position = 0;
        var request = await InfoPanel.Panels.Hosted.PanelHostWire.ReadAsync<InfoPanel.Panels.Hosted.PanelHostRequest>(pipe, TestContext.Current.CancellationToken);
        var reply = await InfoPanel.Panels.Hosted.PanelHostWire.ReadAsync<InfoPanel.Panels.Hosted.PanelHostReply>(pipe, TestContext.Current.CancellationToken);

        Assert.Equal("roundedLeft", request!.FamilySettings!.Value.GetProperty("mask").GetString());
        var back = reply!.Capabilities!;
        Assert.Equal((true, new PanelCrop(0, 0, 480, 462), 15, TimeSpan.FromSeconds(3)), (back.DimInSoftware, back.Crop, back.JpegQualityStep, back.KeepAliveEvery));
        Assert.Equal([0, 90, 180, 270], back.Overlays!.Keys.Order());
        Assert.Equal(overlay, back.Overlays[270]);
    }

    private sealed record Written(PanelFrameFormat Format, byte[] Bytes, bool Repeat, long AtMs);

    private sealed class Driver(FakeUsbClock clock, PanelCapabilities capabilities) : IPanelDriver
    {
        public Session Session { get; } = new(clock, capabilities);

        public string Family => "test";

        public IReadOnlyList<PanelMatch> Matches => [new(0x1209, 0x0001)];

        public PanelProbe Probe(PanelCandidate device, CancellationToken stop) => new(true, null, PanelDeviceStatus.Ok);

        public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop) =>
            new(Session, PanelDeviceStatus.Ok, "");
    }

    private sealed class Session(FakeUsbClock clock, PanelCapabilities capabilities) : IPanelSession
    {
        private readonly List<Written> _writes = [];

        public PanelCapabilities Capabilities => capabilities;

        public IReadOnlyList<Written> Writes
        {
            get
            {
                lock (_writes)
                    return [.. _writes];
            }
        }

        public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
        {
            lock (_writes)
                _writes.Add(new Written(frame.Format, frame.Bytes.ToArray(), frame.Repeat, clock.NowMs));
            return new PanelWrite(PanelDeviceStatus.Ok, TimeSpan.FromMilliseconds(1));
        }

        public PanelDeviceStatus SetBrightness(int percent) => PanelDeviceStatus.Ok;

        public void Close(bool screenOff, TimeSpan budget)
        {
        }

        public void Dispose()
        {
        }
    }
}
