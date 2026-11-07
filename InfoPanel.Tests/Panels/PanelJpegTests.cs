using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Usb;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests.Panels;

/// <summary>Bytes the JPEG tests compare against: the manual's start tag (Panel-Link Rev 1.0 p. 7, table 2-5) and a JPEG's markers.</summary>
internal static class Jpeg
{
    /// <summary>Type 5, <c>image/jpeg</c>, no size fields, checksum <c>79 d6</c> (worked by hand and by an independent script).</summary>
    public static byte[] StartTag
    {
        get
        {
            var tag = new byte[270];
            "PANEL-LINK"u8.CopyTo(tag);
            tag[10] = 1;
            tag[11] = 5;
            "image/jpeg"u8.CopyTo(tag.AsSpan(12));
            (tag[268], tag[269]) = (0x79, 0xD6);
            return tag;
        }
    }

    public static bool Is(byte[] data) => data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8 && data[^2] == 0xFF && data[^1] == 0xD9;

    /// <summary>A JPEG of 1920 × 480 that decodes to black.</summary>
    public static bool IsBlack(byte[] data)
    {
        if (!Is(data))
            return false;
        using var bitmap = SKBitmap.Decode(data);
        return bitmap is { Width: 1920, Height: 480 } && bitmap.Pixels.All(p => p.Red <= 2 && p.Green <= 2 && p.Blue <= 2);
    }

    /// <summary>The screen-off of a JPEG stream: the backlight at raw 0, a black JPEG, the end tag (type 6).</summary>
    public static bool EndsWithScreenOff(FakeUsbDevice device)
    {
        var t = device.Transfers;
        return t.Length >= 3 && t[^3].Data.AsSpan().SequenceEqual(BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [0]))
            && t[^2].Endpoint == BeadaPanelProtocol.PanelOut && IsBlack(t[^2].Data)
            && t[^1].Data.AsSpan().SequenceEqual(BeadaPanelProtocol.PanelLink(PanelLinkType.EndMediaStream));
    }
}

/// <summary><c>devices.json</c>'s <c>encoding</c>: <c>auto</c>, <c>jpeg</c> or <c>raw</c>, <c>auto</c> when absent or unknown.</summary>
public sealed class PanelStoreEncodingTests : IDisposable
{
    private const string Id1 = "0f8e4a52-6c1b-4f38-9a57-2d1c3b4e5f60";

    private readonly string _folder = Directory.CreateTempSubdirectory("panels-encoding-").FullName;

    private string FilePath => Path.Combine(_folder, PanelStore.FileName);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private PanelStore Read(string device)
    {
        File.WriteAllText(FilePath, "{\"format\": \"infopanel.devices/1\", \"devices\": [" + device + "]}");
        var store = PanelStore.Read(FilePath, PanelStoreTests.Logged().Logger);
        Assert.Null(store.Problem);
        return store;
    }

    private static string Device(string encoding) =>
        $$"""{"id": "{{Id1}}", "family": "beadapanel", "model": "8", "serial": null, "port": "0-1", "enabled": true, "profileId": "p-1", "rotation": 0, "brightness": 100{{encoding}}}""";

    [Theory]
    [InlineData("auto", PanelEncoding.Auto)]
    [InlineData("jpeg", PanelEncoding.Jpeg)]
    [InlineData("raw", PanelEncoding.Raw)]
    public void Each_encoding_round_trips(string written, PanelEncoding read)
    {
        var store = Read(Device($", \"encoding\": \"{written}\""));
        Assert.Equal(read, Assert.Single(store.Devices).Encoding);

        store.Save(store.Devices);
        Assert.Contains($"\"brightness\":100,\"encoding\":\"{written}\"}}", File.ReadAllText(FilePath));
        Assert.Equal(read, Assert.Single(PanelStore.Read(FilePath, PanelStoreTests.Logged().Logger).Devices).Encoding);
    }

    [Fact]
    public void A_new_panel_is_auto_and_written_so()
    {
        var store = PanelStore.Read(FilePath, PanelStoreTests.Logged().Logger);
        store.Save([new SavedPanel(Id1, SavedPanel.BeadaPanelFamily, "8", null, "0-1")]);
        Assert.Equal(PanelEncoding.Auto, store.Devices[0].Encoding);
        Assert.EndsWith("\"rotation\":0,\"brightness\":100,\"encoding\":\"auto\"}]}", File.ReadAllText(FilePath));
    }

    /// <summary>
    /// A file written before the setting existed, and the 1.x import's devices (1.4.x had no such setting: the editor's
    /// import adds devices without it), read as <c>auto</c>.
    /// </summary>
    [Fact]
    public void A_device_saved_without_an_encoding_is_auto()
    {
        var store = Read(Device(""));
        Assert.Equal(PanelEncoding.Auto, Assert.Single(store.Devices).Encoding);
        Assert.Equal(PanelEncoding.Auto, new SavedPanel(Id1, SavedPanel.BeadaPanelFamily, "8", null, "0-1").Encoding);
    }

    [Theory]
    [InlineData("\"h264\"")]
    [InlineData("\"JPEG\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("{\"kind\": \"jpeg\"}")]
    public void An_unknown_encoding_reads_as_auto_and_the_panels_still_run(string value)
    {
        var store = Read(Device($", \"encoding\": {value}"));
        var device = Assert.Single(store.Devices);
        Assert.Equal(PanelEncoding.Auto, device.Encoding);
        Assert.True(device.Enabled);

        // An unrelated change (the brightness, a new port) writes the value back as it was (compacted, as every kept member).
        store.Save([device with { Brightness = 40, Port = "0-2" }]);
        Assert.Contains($"\"brightness\":40,\"encoding\":{value.Replace(" ", "")}}}", File.ReadAllText(FilePath));
        var again = Assert.Single(PanelStore.Read(FilePath, PanelStoreTests.Logged().Logger).Devices);
        Assert.Equal(PanelEncoding.Auto, again.Encoding);

        // Setting the encoding replaces it, auto included.
        store.Save([again with { Encoding = PanelEncoding.Auto }]);
        Assert.Contains("\"brightness\":40,\"encoding\":\"auto\"}", File.ReadAllText(FilePath));
    }

    [Fact]
    public void A_later_engines_encoding_survives_and_setting_one_replaces_it()
    {
        var store = Read(Device(", \"encoding\": \"h264\""));
        store.Save([store.Devices[0] with { Enabled = false }]);
        Assert.Contains("\"encoding\":\"h264\"}", File.ReadAllText(FilePath));

        store.Save([store.Devices[0] with { Encoding = PanelEncoding.Raw }]);
        Assert.Contains("\"encoding\":\"raw\"}", File.ReadAllText(FilePath));
        Assert.DoesNotContain("h264", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Encoding_twice_is_a_file_the_engine_cant_hold()
    {
        File.WriteAllText(FilePath, "{\"format\": \"infopanel.devices/1\", \"devices\": [" + Device(", \"encoding\": \"raw\", \"encoding\": \"jpeg\"") + "]}");
        Assert.NotNull(PanelStore.Read(FilePath, PanelStoreTests.Logged().Logger).Problem);
    }
}

/// <summary>
/// A panel's thread streaming JPEG on a real render loop, ticked by hand, over the fake backend: frames encoded once
/// and written as one JPEG each, an unchanged frame neither encoded nor sent again but for the keep-alive, pacing by the
/// shorter writes, and the stop's black JPEG.
/// </summary>
public sealed class PanelJpegRunnerTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly EngineFactory _base = new();
    private readonly FrozenClock _clock = new();
    private readonly FakeUsbBackend _backend = new();
    private readonly (Serilog.ILogger Logger, List<LogEvent> Events) _log = PanelStoreTests.Logged();
    private WebApplicationFactory<Program> _factory = null!;
    private FakeUsbDevice _device = null!;

    public ValueTask InitializeAsync()
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton(new RenderServiceOptions(AutoTick: false));
        }));
        _device = _backend.Add(new FakeUsbDevice(new UsbDeviceId(BeadaPanelProtocol.VendorId, BeadaPanelProtocol.ProductId, 0, 4, "0-1"))
        {
            Reply = PanelReplies.Info(2, 5, 21, 1920, 480, "934072005c"),
        });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _base.DisposeAsync();
    }

    private RenderService Render => _factory.Services.GetRequiredService<RenderService>();

    private async Task<string> WideBar()
    {
        using var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
        var profiles = await api.GetFromJsonAsync<JsonElement>("/api/profiles", Ct);
        return profiles.EnumerateArray().First(p => p.GetProperty("name").GetString() == "Wide bar").GetProperty("id").GetString()!;
    }

    private PanelRunner Runner(string profile, TimeProvider time, TimeSpan keepAlive, PanelEncoding encoding = PanelEncoding.Auto, int frameRate = 30,
        Action? afterEncode = null) =>
        new(_backend, _device.Id, "test panel", profile, 100, PanelRotation.None, Render.Attach, Render.Detach, Render.RequestChanged, Render.Refresh,
            () => frameRate, time, new PanelRunnerOptions { KeepAlive = keepAlive, AfterEncode = afterEncode }, _log.Logger, encoding);

    private static async Task Until(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, what);
            await Task.Delay(5, Ct);
        }
    }

    /// <summary>JPEG frames written after the start tag (the stop's black one included once it is sent).</summary>
    private List<byte[]> JpegWrites => [.. _device.Transfers.Where(t => t.Endpoint == BeadaPanelProtocol.PanelOut && Jpeg.Is(t.Data)).Select(t => t.Data)];

    [Fact]
    public async Task Auto_on_a_t113_writes_each_frame_as_one_jpeg_of_the_panels_size_and_stops_with_a_black_one()
    {
        var id = await WideBar();
        var runner = Runner(id, TimeProvider.System, Timeout.InfiniteTimeSpan);
        runner.Start();
        await Until(() => runner.Running, "the panel never ran");
        Assert.True(runner.Jpeg);

        Render.Tick(id);
        await Until(() => JpegWrites.Count == 1, "the first frame wasn't written");
        var transfers = _device.Transfers;
        var tag = Array.FindIndex(transfers, t => t.Data.AsSpan().SequenceEqual(Jpeg.StartTag));
        Assert.Equal(4, tag); // after the info, its reply, the reset and the backlight
        Assert.True(Jpeg.Is(transfers[tag + 1].Data), "the frame after the start tag isn't one JPEG");
        Assert.DoesNotContain(transfers, t => t.Data.Length == 1920 * 480 * 2);
        using (var frame = SKBitmap.Decode(transfers[tag + 1].Data))
        {
            Assert.Equal((1920, 480), (frame.Width, frame.Height));
            Assert.Contains(frame.Pixels, p => p.Red > 32 || p.Green > 32 || p.Blue > 32); // Wide bar's picture, not black
        }
        Assert.Contains(_log.Events, e => e.RenderMessage().Contains("running, 1920 x 480, JPEG q90"));

        runner.RequestStop();
        await runner.Ended;
        Assert.True(Jpeg.EndsWithScreenOff(_device), "the stop isn't the backlight at 0, a black JPEG and the end tag");
        Assert.Equal(0, _device.OpenHandles);
    }

    [Fact]
    public async Task An_unchanged_frame_is_neither_encoded_nor_sent_again_but_for_the_keep_alive()
    {
        var id = await WideBar();
        var runner = Runner(id, TimeProvider.System, TimeSpan.FromMilliseconds(200));
        runner.Start();
        await Until(() => runner.Running, "the panel never ran");

        Render.Tick(id);
        await Until(() => runner.Encodes == 1, "the first frame wasn't encoded");
        // The same picture again (the loop is told to send it): not copied, so not encoded.
        Render.Refresh(runner.Sink!);
        Render.Tick(id);
        await Until(() => JpegWrites.Count >= 3, "the frame wasn't sent again");

        var writes = JpegWrites;
        Assert.All(writes, w => Assert.Equal(writes[0], w));
        Assert.Equal(1, runner.Sink!.FramesCopied);
        Assert.Equal(1, runner.Encodes);
        runner.RequestStop();
        await runner.Ended;
    }

    [Fact]
    public async Task Pacing_follows_the_shorter_jpeg_writes_with_their_encode()
    {
        var id = await WideBar();
        _device.WriteDelay = TimeSpan.FromMilliseconds(22); // the demo's mean JPEG q90 write, on the fake clock the runner times by
        // The encode takes 4 ms on that clock (the demo measured 3.6 ms): a frame is 26 ms of the panel's thread.
        var runner = Runner(id, _backend.Clock, Timeout.InfiniteTimeSpan, frameRate: 60, afterEncode: () => _backend.Clock.Advance(TimeSpan.FromMilliseconds(4)));
        runner.Start();
        await Until(() => runner.Running, "the panel never ran");
        Assert.Equal(60, runner.Sink!.Request.MaxFps);

        for (var i = 0; i < 100 && runner.Sink.Request.MaxFps == 60; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            Render.Tick(id);
            await Until(() => runner.Writes >= runner.Sink.FramesCopied, "a frame wasn't written");
        }

        // 1000 / (0.9 × 26 ms) = 42 frames a second (the write alone would give 50; raw's 60 ms writes give 18).
        Assert.True(runner.Jpeg);
        Assert.NotEmpty(JpegWrites);
        Assert.Equal(42, runner.Sink.Request.MaxFps);
        Assert.Equal(26, runner.FrameTimeMs, 1);
        runner.RequestStop();
        await runner.Ended;
    }

    [Fact]
    public async Task A_refusal_is_kept_when_the_stop_comes_during_the_start()
    {
        var id = await WideBar();
        PanelRunner? runner = null;
        _device.OnWrite = write =>
        {
            if (!write.Data.AsSpan().SequenceEqual(Jpeg.StartTag))
                return null;
            runner!.RequestStop(); // the panel is turned off as its JPEG start is refused
            return UsbStatus.Timeout;
        };
        runner = Runner(id, TimeProvider.System, Timeout.InfiniteTimeSpan);
        runner.Start();
        var end = await runner.Ended;

        Assert.False(end.Failed);
        Assert.Equal(UsbStatus.Timeout, runner.JpegRefused);
        Assert.Single(_log.Events, e => e.RenderMessage().Contains("JPEG start wasn't taken"));
    }

    [Fact]
    public async Task Raw_chosen_writes_raw_frames()
    {
        var id = await WideBar();
        var runner = Runner(id, TimeProvider.System, Timeout.InfiniteTimeSpan, PanelEncoding.Raw);
        runner.Start();
        await Until(() => runner.Running, "the panel never ran");
        Assert.False(runner.Jpeg);
        Render.Tick(id);
        await Until(() => _device.Transfers.Any(t => t.Data.Length == 1920 * 480 * 2), "the raw frame wasn't written");
        Assert.Empty(JpegWrites);
        Assert.Equal(0, runner.Encodes);
        runner.RequestStop();
        await runner.Ended;
    }

    /// <summary>A clock frozen except when the test advances it (timers still run on real time).</summary>
    private sealed class FrozenClock : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero).AddTicks(Interlocked.Read(ref _ticks));

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}

/// <summary>The manager's part: the saved encoding reaches the session, a refused JPEG start is remembered, a change restarts the stream.</summary>
public sealed class PanelJpegManagerTests : IAsyncDisposable
{
    private const string Serial = "934072005c00481401044f2420751fd6";

    private readonly string _folder = Directory.CreateTempSubdirectory("panel-jpeg-manager-").FullName;
    private readonly FakeUsbBackend _backend = new();
    private readonly List<LogEvent> _events;
    private readonly Serilog.ILogger _logger;
    private PanelManager? _manager;

    public PanelJpegManagerTests() => (_logger, _events) = PanelStoreTests.Logged();

    public async ValueTask DisposeAsync()
    {
        if (_manager != null)
            await _manager.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private FakeUsbDevice Plug() =>
        _backend.Add(new FakeUsbDevice(new UsbDeviceId(BeadaPanelProtocol.VendorId, BeadaPanelProtocol.ProductId, 0, 4, "0-1"))
        {
            Reply = PanelReplies.Info(2, 5, 21, 1920, 480, Serial),
            UsbSerial = Serial,
        });

    private PanelManager Manager(PanelEncoding encoding)
    {
        var store = PanelStore.Read(Path.Combine(_folder, PanelStore.FileName), _logger);
        store.Save([new SavedPanel("0f8e4a52-6c1b-4f38-9a57-2d1c3b4e5f60", SavedPanel.BeadaPanelFamily, "8", Serial, "0-1", true, "p-1") { Encoding = encoding }]);
        var host = new PanelHost(_ => true, _ => true, _ => { }, _ => { }, _ => { }, () => 15);
        _manager = new PanelManager(_backend, store, host, _backend.Clock, _logger, new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan });
        _manager.Start(passes: false);
        return _manager;
    }

    private static async Task<PanelStatus> Until(PanelManager manager, Func<PanelStatus, bool> wanted)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var status = Assert.Single(await manager.ListAsync());
            if (wanted(status))
                return status;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"the panel never got there: {status}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static int StartTags(FakeUsbDevice device, byte[] tag) => device.Transfers.Count(t => t.Data.AsSpan().SequenceEqual(tag));

    private static byte[] RawStart => BeadaPanelProtocol.PanelLink(PanelLinkType.StartMediaStream, "video/x-raw, format=RGB16, width=1920, height=480, framerate=0/1"u8);

    [Fact]
    public async Task A_saved_auto_panel_on_a_t113_runs_jpeg_and_goes_dark_with_a_black_jpeg()
    {
        var device = Plug();
        var manager = Manager(PanelEncoding.Auto);
        await manager.RunPassAsync();
        var running = await Until(manager, s => s.State == "running");
        Assert.True(running.Jpeg);
        Assert.Equal(1, StartTags(device, Jpeg.StartTag));

        await manager.UpdateAsync(running.Saved.Id, s => s with { Enabled = false });
        await Until(manager, s => s.State == "off");
        Assert.True(Jpeg.EndsWithScreenOff(device));
    }

    [Fact]
    public async Task A_refused_jpeg_start_runs_raw_and_the_panel_stays_raw_until_the_engine_restarts()
    {
        var device = Plug();
        device.OnWrite = write => write.Data.AsSpan().SequenceEqual(Jpeg.StartTag) ? UsbStatus.Timeout : null;
        var manager = Manager(PanelEncoding.Auto);

        await manager.RunPassAsync();
        var running = await Until(manager, s => s.State == "running");
        Assert.False(running.Jpeg);
        Assert.Equal((1, 1), (StartTags(device, Jpeg.StartTag), StartTags(device, RawStart)));

        // Off and on again: the same panel isn't offered JPEG again this run.
        await manager.UpdateAsync(running.Saved.Id, s => s with { Enabled = false });
        await Until(manager, s => s.State == "off");
        await manager.UpdateAsync(running.Saved.Id, s => s with { Enabled = true });
        await manager.RunPassAsync();
        Assert.False((await Until(manager, s => s.State == "running")).Jpeg);
        Assert.Equal((1, 2), (StartTags(device, Jpeg.StartTag), StartTags(device, RawStart)));
        Assert.Single(_events, e => e.RenderMessage().Contains("JPEG start wasn't taken"));
        Assert.Equal(PanelEncoding.Auto, (await manager.ListAsync())[0].Saved.Encoding); // the setting is kept
    }

    /// <summary>
    /// A BeadaPanel 8 took no transfer for 20 to 30 s after a JPEG stream ended (end tag or reset; a raw or a JPEG start
    /// after it failed, 4 of 4), so the next start waits 30 s from the end; after a raw stream it doesn't wait.
    /// </summary>
    [Fact]
    public async Task After_a_jpeg_session_the_next_start_waits_30_s_and_after_a_raw_one_it_doesnt()
    {
        var device = Plug();
        var manager = Manager(PanelEncoding.Auto);
        await manager.RunPassAsync();
        var running = await Until(manager, s => s.State == "running" && s.Jpeg == true);
        var id = running.Saved.Id;
        await manager.UpdateAsync(id, s => s with { Enabled = false });
        await Until(manager, s => s.State == "off");

        await manager.UpdateAsync(id, s => s with { Enabled = true });
        await manager.RunPassAsync();
        var waiting = await Until(manager, s => s.State == "waiting");
        Assert.Equal("the panel takes no new stream until 30 s after its JPEG stream ended", waiting.Reason);
        _backend.Clock.Advance(TimeSpan.FromSeconds(29));
        await manager.RunPassAsync();
        Assert.Equal("waiting", (await manager.ListAsync())[0].State);
        Assert.Equal(1, StartTags(device, Jpeg.StartTag));

        _backend.Clock.Advance(TimeSpan.FromSeconds(1));
        await manager.RunPassAsync();
        Assert.True((await Until(manager, s => s.State == "running")).Jpeg);
        Assert.Equal(2, StartTags(device, Jpeg.StartTag));
        Assert.Single(_events, e => e.RenderMessage().Contains("starting again in 30 s"));

        // Raw: off and on again starts at once.
        await manager.UpdateAsync(id, s => s with { Encoding = PanelEncoding.Raw });
        await Until(manager, s => s.State == "waiting");
        _backend.Clock.Advance(PanelManager.JpegSettle);
        await manager.RunPassAsync();
        await Until(manager, s => s.State == "running" && s.Jpeg == false);
        await manager.UpdateAsync(id, s => s with { Enabled = false });
        await Until(manager, s => s.State == "off");
        await manager.UpdateAsync(id, s => s with { Enabled = true });
        await manager.RunPassAsync();
        await Until(manager, s => s.State == "running" && s.Jpeg == false);
        Assert.Equal(2, StartTags(device, RawStart));
    }

    [Fact]
    public async Task A_refusal_is_remembered_when_the_raw_start_fails_too()
    {
        var device = Plug();
        var rawRefused = 0;
        device.OnWrite = write =>
            write.Data.AsSpan().SequenceEqual(Jpeg.StartTag) ? UsbStatus.Timeout
            : write.Data.AsSpan().SequenceEqual(RawStart) && Interlocked.Increment(ref rawRefused) == 1 ? UsbStatus.Timeout
            : null;
        var manager = Manager(PanelEncoding.Auto);

        await manager.RunPassAsync();
        var failed = await Until(manager, s => s.State == "failed");
        Assert.Equal("the stream's start wasn't taken (Timeout)", failed.Reason);
        Assert.Equal((1, 1), (StartTags(device, Jpeg.StartTag), StartTags(device, RawStart)));

        // The next pass tries raw, not JPEG.
        await manager.RunPassAsync();
        Assert.False((await Until(manager, s => s.State == "running")).Jpeg);
        Assert.Equal((1, 2), (StartTags(device, Jpeg.StartTag), StartTags(device, RawStart)));
        Assert.Single(_events, e => e.RenderMessage().Contains("JPEG start wasn't taken"));
    }

    [Fact]
    public async Task Changing_the_encoding_restarts_the_stream_in_the_new_one()
    {
        var device = Plug();
        var manager = Manager(PanelEncoding.Auto);
        await manager.RunPassAsync();
        var running = await Until(manager, s => s.State == "running" && s.Jpeg == true);

        await manager.UpdateAsync(running.Saved.Id, s => s with { Encoding = PanelEncoding.Raw });
        await Until(manager, s => s.State == "waiting"); // the session ended; a pass 30 s after it starts the new one
        _backend.Clock.Advance(PanelManager.JpegSettle);
        await manager.RunPassAsync();
        var raw = await Until(manager, s => s.State == "running");
        Assert.False(raw.Jpeg);
        Assert.Equal((1, 1), (StartTags(device, Jpeg.StartTag), StartTags(device, RawStart)));
        Assert.Contains("\"encoding\":\"raw\"", File.ReadAllText(Path.Combine(_folder, PanelStore.FileName)));
    }
}
