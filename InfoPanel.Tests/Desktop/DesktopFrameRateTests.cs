using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Desktop;
using InfoPanel.Output;
using InfoPanel.Settings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// A desktop window's frames come at the frame rate setting (1.4.x: <c>TargetFrameRate</c>, 15 by default, every
/// display window re-timed at once when it changes, <c>Views/DisplayWindow.xaml.cs</c>), on the engine host with a
/// manual clock and a <see cref="FakeDesktopPlatform"/>. The profile's bar grows with elapsed time, so every tick draws
/// new pixels, and every tick the window is due gives it a frame.
/// </summary>
public sealed class DesktopFrameRateTests : IAsyncLifetime
{
    private static readonly DisplayInfo Display = new("Built-in", new PointRect(0, 0, 1512, 982), 2);

    /// <summary>A sixtieth of a second, rounded up to a whole tick.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromTicks((TimeSpan.TicksPerSecond + 59) / 60);

    private readonly EngineFactory _base = new();
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeDesktopPlatform _platform = new(Display);
    private readonly string _id = Guid.NewGuid().ToString();
    private WebApplicationFactory<Program>? _factory;

    public ValueTask InitializeAsync()
    {
        var growing = JsonNode.Parse("""
            {"format": "infopanel.scene/1",
             "state": {"t": {"init": 0, "next": {"op": "add", "args": [{"op": "state", "name": "t"}, {"op": "dt"}]}}},
             "nodes": [{"kind": "rect", "w": {"op": "mul", "args": [{"op": "state", "name": "t"}, 100]}, "h": 20, "fill": "#ffffff"}]}
            """)!.AsObject();
        var doc = TestDocs.Doc("Growing", 1000, 100, TestDocs.Scene("grow", 0, 40, growing));
        doc["id"] = _id;
        doc["profile"]!["window"] = new JsonObject { ["enabled"] = true };
        var folder = Path.Combine(_base.DataDir, "profiles", _id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "document.json"), doc.ToJsonString());
        File.WriteAllText(Path.Combine(_base.DataDir, "profiles.json"), $$"""{"order":["{{_id}}"]}""");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory != null)
            await _factory.DisposeAsync();
        await _base.DisposeAsync();
        _platform.Dispose();
    }

    /// <summary>Starts the host, render loops ticking on their own timers or by hand, and returns the window's sink.</summary>
    private async Task<DesktopWindowSink> Start(bool autoTick)
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IDesktopPlatform>(_platform);
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton(new RenderServiceOptions(AutoTick: autoTick));
        }));
        _ = _factory.Services;
        await _platform.IdleAsync();
        return await _platform.InvokeAsync(() => Manager.SinkOf(_id)) ?? throw new Xunit.Sdk.XunitException("No window sink");
    }

    private DisplayWindowManager Manager => _factory!.Services.GetRequiredService<DisplayWindowManager>();

    private SettingsStore Settings => _factory!.Services.GetRequiredService<SettingsStore>();

    private RenderService Render => _factory!.Services.GetRequiredService<RenderService>();

    private static long Frames(DesktopWindowSink sink) => Interlocked.Read(ref sink.FramesReceived);

    private static JsonElement Change(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task Until(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail("Timed out waiting for the condition.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AWindowsFrames_ComeAtTheFrameRateSetting_15ByDefault_AndFollowAChange()
    {
        var sink = await Start(autoTick: false);
        Assert.Equal(new FrameRequest(1, 15, IncludeBounds: false), sink.Request);
        // A 60 fps sink on the same loop: the loop ticks every sixtieth of a second, and the window takes the ticks its
        // own rate is due at.
        Assert.True(Render.Attach(new Probe(_id)));

        // Ticks at 60 Hz for a second: the window's frames are a fifteenth of a second (4 ticks) apart.
        var windowTicks = new List<int>();
        for (var tick = 0; tick <= 60; tick++)
        {
            if (tick > 0)
                _clock.Advance(Step);
            var before = Frames(sink);
            Render.Tick(_id);
            if (Frames(sink) > before)
                windowTicks.Add(tick);
        }

        Assert.Equal(Enumerable.Range(0, 16).Select(i => i * 4), windowTicks);

        // A change: the same window asks for the new rate, gets a frame at the next tick, then one every 200 ms (12 ticks).
        Settings.Apply(Change("""{"frameRate":5}"""));
        await _platform.IdleAsync();
        Assert.Same(sink, await _platform.InvokeAsync(() => Manager.SinkOf(_id)));
        Assert.Equal(new FrameRequest(1, 5, IncludeBounds: false), sink.Request);
        windowTicks.Clear();
        for (var tick = 61; tick <= 61 + 36; tick++)
        {
            _clock.Advance(Step);
            var before = Frames(sink);
            Render.Tick(_id);
            if (Frames(sink) > before)
                windowTicks.Add(tick);
        }

        Assert.Equal([61, 73, 85, 97], windowTicks);
    }

    [Fact]
    public async Task AChange_ReTimesTheWindowsLoopAtOnce()
    {
        var sink = await Start(autoTick: true);

        // The first frame when the window attaches. The attach also woke the loop, which waits out its shortest interval:
        // past it, a tick is due for the window (at least half a period after its frame).
        await Until(() => Frames(sink) == 1);
        await Until(() => _clock.HasTimerDueAt(RenderLoop.MinInterval));
        _clock.Advance(TimeSpan.FromMilliseconds(40));
        await Until(() => Frames(sink) == 2);

        // Before the loop's timer (a fifteenth of a second after the start), a change: the window's loop ticks at once,
        // with the clock where it is, and the window gets a frame at its new rate.
        _clock.Advance(TimeSpan.FromMilliseconds(20));
        Settings.Apply(Change("""{"frameRate":5}"""));
        await Until(() => Frames(sink) == 3);
        Assert.Equal(5, sink.Request.MaxFps);
    }

    [Fact]
    public async Task AStoredFrameRate_IsWhatAWindowAsksFor()
    {
        Directory.CreateDirectory(_base.DataDir);
        File.WriteAllText(Path.Combine(_base.DataDir, "settings.json"), """{"format":"infopanel.settings/1","frameRate":24}""");

        var sink = await Start(autoTick: false);

        Assert.Equal(24, sink.Request.MaxFps);
        Assert.Equal(24, RenderLoop.EffectiveFps(sink.Request));
    }

    /// <summary>Asks for 60 frames a second at a small scale, and ignores them.</summary>
    private sealed class Probe(string profileId) : IFrameSink
    {
        public string ProfileId { get; } = profileId;

        public FrameRequest Request { get; } = new(0.25f, 60, IncludeBounds: false);

        public void OnFrame(RenderedFrame frame)
        {
        }
    }
}
