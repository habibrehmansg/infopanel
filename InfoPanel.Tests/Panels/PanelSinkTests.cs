using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Usb;
using InfoPanel.Sensors;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Panels;

/// <summary>
/// A panel's sink and its thread on a real render loop, ticked by hand, over the fake USB backend: the newest frame
/// wins, the render thread never waits for a slow panel and other outputs keep their rate, unchanged frames are skipped
/// but a rotation isn't, the last frame is re-sent when nothing changes, and pacing lowers what the sink asks for.
/// </summary>
public sealed class PanelSinkTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly EngineFactory _base = new();
    private readonly ManualClock _clock = new();
    private readonly FakeUsbBackend _backend = new();
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

    private PanelRunner Runner(string profile, IUsbBackend backend, TimeProvider time, TimeSpan keepAlive, PanelRotation rotation = PanelRotation.None) =>
        new(backend, _device.Id, "test panel", profile, 100, rotation, Render.Attach, Render.Detach, Render.RequestChanged, Render.Refresh,
            () => 30, time, new PanelRunnerOptions { KeepAlive = keepAlive }, PanelStoreTests.Logged().Logger);

    private static async Task Until(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, what);
            await Task.Delay(5, Ct);
        }
    }

    private int FrameWrites => _device.Transfers.Count(t => t.Data.Length == 1920 * 480 * 2);

    [Fact]
    public async Task A_slow_panel_never_holds_the_render_thread_or_other_outputs_and_the_newest_frame_wins()
    {
        var id = await WideBar();
        using var gate = new GatedBackend(_backend);
        var runner = Runner(id, gate, TimeProvider.System, Timeout.InfiniteTimeSpan);
        runner.Start();
        await Until(() => runner.Running, "the panel never ran");
        var other = new CountingSink(id);
        Assert.True(Render.Attach(other));

        // The first frame's write blocks (the panel is slow); five more ticks of new frames.
        Render.Tick(id);
        await Until(() => gate.Blocked, "the first write never started");
        var slowest = TimeSpan.Zero;
        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            var started = Stopwatch.GetTimestamp();
            Render.Tick(id);
            slowest = TimeSpan.FromTicks(Math.Max(slowest.Ticks, Stopwatch.GetElapsedTime(started).Ticks));
        }
        Assert.True(slowest < TimeSpan.FromMilliseconds(500), $"a tick took {slowest.TotalMilliseconds} ms while the panel was writing");
        Assert.Equal(6, other.Frames);
        Assert.Equal(6, runner.Sink!.FramesCopied);
        Assert.True(runner.Sink.BuffersHeld <= 2);

        // Let the writes go: the frame being written, then only the newest of the five.
        var newest = other.LastFrameId;
        gate.Open();
        await Until(() => FrameWrites == 2, "the newest frame wasn't written");
        await Until(() => runner.LastFrameId == newest, "the frame written second wasn't the newest");
        Render.Detach(other);
        runner.RequestStop();
        await runner.Ended;
        // The stop's black frame is the only other full frame.
        Assert.Equal(3, FrameWrites);
        Assert.True(_device.Transfers.Where(t => t.Data.Length == 1920 * 480 * 2).Last().Data.All(b => b == 0));
    }

    [Fact]
    public async Task An_unchanged_frame_is_skipped_but_a_new_rotation_is_drawn()
    {
        var id = await WideBar();
        var runner = Runner(id, _backend, TimeProvider.System, Timeout.InfiniteTimeSpan);
        runner.Start();
        await Until(() => runner.Running, "the panel never ran");

        Render.Tick(id);
        await Until(() => FrameWrites == 1, "the first frame wasn't written");
        var first = _device.Transfers.Last(t => t.Data.Length == 1920 * 480 * 2).Data;

        // The same picture again (the loop is told to send it): not copied, not written.
        Render.Refresh(runner.Sink!);
        Render.Tick(id);
        Assert.Equal(1, runner.Sink!.FramesCopied);

        // Turned half way: the same picture is drawn again, upside down.
        runner.Rotation = PanelRotation.Rotate180;
        Render.Tick(id);
        await Until(() => FrameWrites == 2, "the rotated frame wasn't written");
        var turned = _device.Transfers.Last(t => t.Data.Length == 1920 * 480 * 2).Data;
        Assert.Equal(first[0..2], turned[^2..]);
        Assert.NotEqual(first, turned);
        runner.RequestStop();
        await runner.Ended;
    }

    [Fact]
    public async Task With_nothing_new_the_last_frame_is_sent_again()
    {
        var id = await WideBar();
        var runner = Runner(id, _backend, TimeProvider.System, TimeSpan.FromMilliseconds(200));
        runner.Start();
        await Until(() => runner.Running, "the panel never ran");

        Render.Tick(id);
        await Until(() => FrameWrites >= 3, "the frame wasn't sent again");
        var frames = _device.Transfers.Where(t => t.Data.Length == 1920 * 480 * 2).Take(3).ToList();
        Assert.Equal(frames[0].Data, frames[1].Data);
        Assert.Equal(frames[0].Data, frames[2].Data);
        Assert.Equal(1, runner.Sink!.FramesCopied);
        runner.RequestStop();
        await runner.Ended;
    }

    [Fact]
    public async Task Without_a_keep_alive_the_thread_sleeps_until_a_frame_comes()
    {
        var id = await WideBar();
        var runner = Runner(id, _backend, TimeProvider.System, Timeout.InfiniteTimeSpan);
        runner.Start();
        await Until(() => runner.Running, "the panel never ran");
        Render.Tick(id);
        await Until(() => FrameWrites == 1, "the frame wasn't written");

        var turns = runner.Turns;
        await Task.Delay(300, Ct);
        Assert.True(runner.Turns - turns < 5, $"the thread went round {runner.Turns - turns} times with nothing to do");
        runner.RequestStop();
        await runner.Ended;
    }

    [Fact]
    public async Task Pacing_lowers_the_request_to_what_the_panel_takes()
    {
        var id = await WideBar();
        _device.WriteDelay = TimeSpan.FromMilliseconds(100); // on the fake clock, which the runner times its writes by
        var runner = Runner(id, _backend, _backend.Clock, Timeout.InfiniteTimeSpan);
        runner.Start();
        await Until(() => runner.Running, "the panel never ran");
        Assert.Equal(30, runner.Sink!.Request.MaxFps);

        // Paced once a second of writes (on the fake clock): new frames until a second's worth is written.
        for (var i = 0; i < 30 && runner.Sink.Request.MaxFps == 30; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            Render.Tick(id);
            await Until(() => runner.Writes >= runner.Sink.FramesCopied, "a frame wasn't written");
        }

        // 1000 / (0.9 × 100 ms) = 11 frames a second.
        Assert.Equal(11, runner.Sink.Request.MaxFps);
        Assert.Equal(100, runner.FrameTimeMs, 1);
        runner.RequestStop();
        await runner.Ended;
    }

    [Fact]
    public async Task A_stop_asked_during_a_brightness_write_writes_no_frame_before_the_screen_off()
    {
        var id = await WideBar();
        var runner = Runner(id, _backend, TimeProvider.System, TimeSpan.FromMilliseconds(50));
        runner.Start();
        await Until(() => runner.Running, "the panel never ran");
        Render.Tick(id);
        await Until(() => FrameWrites >= 1, "the first frame wasn't written");

        var dimmer = BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [40]);
        _device.OnWrite = write =>
        {
            if (write.Data.AsSpan().SequenceEqual(dimmer))
            {
                runner.RequestStop();
                Thread.Sleep(100); // the keep-alive is due by the time the write returns
            }
            return null;
        };
        runner.Brightness = 40;
        await runner.Ended.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var transfers = _device.Transfers;
        var dimmed = Array.FindIndex(transfers, t => t.Data.AsSpan().SequenceEqual(dimmer));
        Assert.True(dimmed >= 0, "the brightness was never sent");
        Assert.Equal(BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [0]), transfers[dimmed + 1].Data);
    }

    [Fact]
    public async Task A_rotation_set_while_the_sink_is_made_reaches_the_sink()
    {
        PanelRunner? runner = null;
        var asked = 0;
        int FrameRate()
        {
            // Called first from the sink's constructor, after the rotation was read for it and before the sink is
            // published: the rotation changes then, on the same thread, so the interleaving is fixed.
            if (Interlocked.Exchange(ref asked, 1) == 0)
                runner!.Rotation = PanelRotation.Rotate270;
            return 30;
        }
        runner = new PanelRunner(_backend, _device.Id, "test panel", "p-1", 100, PanelRotation.None, _ => true, _ => { }, _ => { }, _ => { },
            FrameRate, TimeProvider.System, new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan }, PanelStoreTests.Logged().Logger);
        runner.Start();
        await Until(() => runner.Running, "the panel never ran");

        await Until(() => runner.Sink!.Rotation == PanelRotation.Rotate270, $"the sink kept {runner.Sink!.Rotation}");
        runner.RequestStop();
        await runner.Ended;
    }

    /// <summary>The fake backend, with every full frame's write held until <see cref="Open"/>.</summary>
    private sealed class GatedBackend(FakeUsbBackend inner) : IUsbBackend
    {
        private readonly ManualResetEventSlim _open = new(false);
        private volatile bool _blocked;

        public bool Blocked => _blocked;

        public void Open() => _open.Set();

        public IReadOnlyList<UsbDeviceId> ListDevices() => inner.ListDevices();

        public UsbStatus Open(UsbDeviceId device, out IUsbDevice? opened)
        {
            var status = inner.Open(device, out var handle);
            opened = handle == null ? null : new Gated(handle, this);
            return status;
        }

        public void Dispose() => _open.Set();

        private sealed class Gated(IUsbDevice inner, GatedBackend gate) : IUsbDevice
        {
            public UsbStatus GetConfiguration(out int configuration) => inner.GetConfiguration(out configuration);

            public UsbStatus SetConfiguration(int configuration) => inner.SetConfiguration(configuration);

            public UsbStatus ClaimInterface(int interfaceNumber) => inner.ClaimInterface(interfaceNumber);

            public UsbStatus ReleaseInterface(int interfaceNumber) => inner.ReleaseInterface(interfaceNumber);

            public string? ReadSerialNumber() => inner.ReadSerialNumber();

            public UsbStatus BulkWrite(byte endpoint, ReadOnlySpan<byte> data, int timeoutMs)
            {
                if (data.Length > 1000 && data.ContainsAnyExcept((byte)0))
                {
                    gate._blocked = true;
                    gate._open.Wait();
                }
                return inner.BulkWrite(endpoint, data, timeoutMs);
            }

            public UsbStatus BulkRead(byte endpoint, Span<byte> buffer, int timeoutMs, out int read) => inner.BulkRead(endpoint, buffer, timeoutMs, out read);

            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class CountingSink(string profileId) : IFrameSink
    {
        private int _frames;
        private long _last;

        public string ProfileId { get; } = profileId;

        public FrameRequest Request { get; } = new(1, 30, IncludeBounds: false);

        public int Frames => Volatile.Read(ref _frames);

        public long LastFrameId => Interlocked.Read(ref _last);

        public void OnFrame(RenderedFrame frame)
        {
            Interlocked.Increment(ref _frames);
            Interlocked.Exchange(ref _last, frame.FrameId);
        }
    }

    /// <summary>A clock frozen except when the test advances it (timers still run on real time).</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero).AddTicks(Interlocked.Read(ref _ticks));

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
