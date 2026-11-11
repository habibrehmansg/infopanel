using InfoPanel.Output;
using InfoPanel.Output.Gpu;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SkiaSharp;
using Xunit;
using ManualClock = InfoPanel.Core.Tests.Editing.ManualTimeProvider;

namespace InfoPanel.Core.Tests.Preview;

/// <summary>
/// Drawing with the GPU (<see cref="GpuRendering"/>) over a stand-in device whose "GPU" surfaces are raster ones, so it
/// runs on every OS: off draws on the bitmap and makes no device; on draws through the device and reads back; a device
/// that can't be made, is lost or refuses a size gives CPU frames, each as the setting's answer says.
/// </summary>
public sealed class GpuRenderingTests
{
    private readonly List<LogEvent> _log = [];
    private readonly ILogger _logger;

    public GpuRenderingTests() => _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_log)).CreateLogger();

    private List<LogEvent> Warnings
    {
        get { lock (_log) return _log.Where(e => e.Level == LogEventLevel.Warning).ToList(); }
    }

    /// <summary>A scene stand-in: a gradient, a blurred circle and text, on transparent.</summary>
    private static void Scene(SKCanvas canvas, SKColor color)
    {
        canvas.Clear(SKColors.Transparent);
        using var gradient = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(120, 0), [color, SKColors.Blue], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(new SKRect(0, 0, 120, 30), gradient);
        using var circle = new SKPaint { Color = SKColors.White, IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(3, 3) };
        canvas.DrawCircle(80, 40, 15, circle);
    }

    private static readonly SKImageInfo Info = new(160, 60, SKColorType.Rgba8888, SKAlphaType.Premul);

    private static byte[] Cpu(SKColor color, SKImageInfo? info = null)
    {
        using var bitmap = new SKBitmap(info ?? Info);
        GpuRendering.DrawOnCpu(bitmap, c => Scene(c, color));
        return bitmap.GetPixelSpan().ToArray();
    }

    private static byte[] Draw(GpuRendering gpu, SKColor color, SKImageInfo? info = null)
    {
        using var bitmap = new SKBitmap(info ?? Info);
        gpu.Draw(bitmap, c => Scene(c, color));
        return bitmap.GetPixelSpan().ToArray();
    }

    [Fact]
    public void Off_DrawsOnTheBitmap_AndMakesNoDevice()
    {
        var made = 0;
        using var gpu = new GpuRendering(() => { made++; return new FakeGpuDevice(); }, logger: _logger);

        Assert.Equal(Cpu(SKColors.Red), Draw(gpu, SKColors.Red));
        Assert.Equal(0, made);
        Assert.Equal(GpuStatus.Cpu, gpu.Status);
        Assert.Equal("CPU", gpu.Drawing);

        Assert.Equal(GpuStatus.Cpu, gpu.Apply(false));
        Assert.Equal(0, made);
    }

    [Fact]
    public void On_DrawsThroughTheDevice_AndReadsBack()
    {
        var device = new FakeGpuDevice { Name = "Fake GPU 9000" };
        using var gpu = new GpuRendering(() => device, logger: _logger);

        var status = gpu.Apply(true);

        Assert.Equal(new GpuStatus(GpuRendering.Metal, "Fake GPU 9000", null), status);
        Assert.Equal(status, gpu.Status);
        Assert.Equal("Metal", gpu.Drawing);
        Assert.Equal(Cpu(SKColors.Red), Draw(gpu, SKColors.Red));
        Assert.Equal((1, 1), (device.Surfaces, device.Submits));
        Assert.Equal(1, device.Scopes);
        Assert.Contains(_log, e => e.Level == LogEventLevel.Information && e.RenderMessage().Contains("Fake GPU 9000", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeviceThatCantBeMade_GivesCpuFrames_AndGpuFailedWithItsMessage_LoggedOnce()
    {
        var made = 0;
        using var gpu = new GpuRendering(() => { made++; throw new InvalidOperationException("Metal made no command queue."); }, logger: _logger);

        var status = gpu.Apply(true);

        Assert.Equal(new GpuStatus(GpuRendering.CpuBackend, null, new GpuFallback(GpuRendering.Failed, "Metal made no command queue.")), status);
        Assert.Equal("CPU", gpu.Drawing);
        Assert.Equal(Cpu(SKColors.Red), Draw(gpu, SKColors.Red));
        Assert.Equal(Cpu(SKColors.Green), Draw(gpu, SKColors.Green));
        Assert.Equal(1, made);
        Assert.Contains("Metal made no command queue.", Assert.Single(Warnings).RenderMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GpuRendering.NoDevice)]
    [InlineData(GpuRendering.Unsupported)]
    public void NoGpuHere_IsItsOwnCode(string code)
    {
        using var gpu = new GpuRendering(() => throw new GpuUnavailableException(code, "No GPU here."), logger: _logger);

        Assert.Equal(new GpuFallback(code, "No GPU here."), gpu.Apply(true).Fallback);
        Assert.Equal(Cpu(SKColors.Red), Draw(gpu, SKColors.Red));
    }

    [Fact]
    public void ALossWhileDrawing_DeliversThatFrameFromTheCpu_SetsGpuLost_AndReleasesTheDevice()
    {
        var device = new FakeGpuDevice();
        using var gpu = new GpuRendering(() => device, logger: _logger);
        gpu.Apply(true);
        Assert.Equal(Cpu(SKColors.Red), Draw(gpu, SKColors.Red));

        // The device is lost while this frame draws: what it read back is garbage, so the frame comes from the CPU.
        device.LoseAtSubmit = "The GPU stopped answering.";
        Assert.Equal(Cpu(SKColors.Green), Draw(gpu, SKColors.Green));

        Assert.Equal(new GpuStatus(GpuRendering.CpuBackend, null, new GpuFallback(GpuRendering.Lost, "The GPU stopped answering.")), gpu.Status);
        Assert.True(device.Disposed);
        Assert.Contains("The GPU stopped answering.", Assert.Single(Warnings).RenderMessage(), StringComparison.Ordinal);

        // Later frames: the CPU, without the device.
        var surfaces = device.Surfaces;
        Assert.Equal(Cpu(SKColors.Blue), Draw(gpu, SKColors.Blue));
        Assert.Equal(surfaces, device.Surfaces);
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("surface")]
    public void ADeviceFailingAtAnyStep_IsLost_AndTheFrameDrawnOnTheCpu(string step)
    {
        var device = new FakeGpuDevice();
        using var gpu = new GpuRendering(() => device, logger: _logger);
        gpu.Apply(true);
        device.Fail = step;

        Assert.Equal(Cpu(SKColors.Red), Draw(gpu, SKColors.Red));

        Assert.Equal(GpuRendering.Lost, gpu.Status.Fallback?.Code);
        Assert.True(device.Disposed);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("scopeDispose")]
    [InlineData("checkLost")]
    [InlineData("checkLostAfterARefusal")]
    [InlineData("purge")]
    public void ADeviceOperationThatThrows_IsALoss_WithAFixedMessage_LoggedOnce_AndThatFrameDrawnOnTheCpu(string step)
    {
        var clock = new ManualClock();
        var device = new FakeGpuDevice { Refuses = step == "checkLostAfterARefusal" ? _ => true : null };
        using var gpu = new GpuRendering(() => device, clock, _logger);
        gpu.Apply(true);
        device.Fail = step == "checkLostAfterARefusal" ? "checkLost" : step;
        if (step == "purge")
            clock.Advance(10_000);

        Assert.Equal(Cpu(SKColors.Red), Draw(gpu, SKColors.Red));

        // The exception's own text (here a path) stays in the log; the answer's message is fixed.
        Assert.Equal(new GpuStatus(GpuRendering.CpuBackend, null, new GpuFallback(GpuRendering.Lost, "The GPU failed while drawing.")), gpu.Status);
        Assert.True(device.Disposed);
        var warning = Assert.Single(Warnings);
        Assert.NotNull(warning.Exception);

        // Later frames: the CPU, without the device, and nothing more logged.
        var (surfaces, scopes) = (device.Surfaces, device.Scopes);
        Assert.Equal(Cpu(SKColors.Green), Draw(gpu, SKColors.Green));
        Assert.Equal((surfaces, scopes), (device.Surfaces, device.Scopes));
        Assert.Single(Warnings);
    }

    [Fact]
    public void AFrameReadBackWhole_BeforeFreeingResourcesFails_IsTheGpusFrame_ThoughItsDeviceIsReleased()
    {
        // A device that answers nothing once released: the frame's backend is taken before the release, never after.
        var clock = new ManualClock();
        var device = new FakeGpuDevice { Name = "Fake GPU 9000", RefusesOnceDisposed = true };
        using var gpu = new GpuRendering(() => device, clock, _logger);
        var drew = new List<string>();
        gpu.Drew += drew.Add;
        gpu.Apply(true);
        device.Fail = "purge";
        clock.Advance(10_000);
        using var bitmap = new SKBitmap(Info);

        var backend = gpu.Draw(bitmap, c => Scene(c, SKColors.Red));

        Assert.Equal(GpuRendering.Metal, backend);
        Assert.Equal([GpuRendering.Metal], drew);
        Assert.Equal(1, device.Surfaces);
        Assert.Equal(Cpu(SKColors.Red), bitmap.GetPixelSpan().ToArray());
        Assert.True(device.Disposed);
        Assert.Equal(GpuRendering.Lost, gpu.Status.Fallback?.Code);

        // The next frame: the CPU's.
        Assert.Equal(GpuRendering.CpuBackend, gpu.Draw(bitmap, c => Scene(c, SKColors.Green)));
        Assert.Equal(1, device.Surfaces);
    }

    [Fact]
    public void AnExceptionInTheDraw_IsThrownOn_EvenWhenTheScopeThenFailsToClose_WhichIsALoss()
    {
        var device = new FakeGpuDevice();
        using var gpu = new GpuRendering(() => device, logger: _logger);
        gpu.Apply(true);
        device.Fail = "scopeDispose";
        using var bitmap = new SKBitmap(Info);

        Assert.Throws<ArgumentException>(() => gpu.Draw(bitmap, _ => throw new ArgumentException("a scene's bug")));

        Assert.Equal(GpuRendering.Lost, gpu.Status.Fallback?.Code);
        Assert.True(device.Disposed);
    }

    [Fact]
    public void EachDraw_AnswersAndTellsTheBackendThatDrewIt_NotWhatTheStatusSays()
    {
        var device = new FakeGpuDevice { Refuses = info => info.Width > 1000 };
        using var gpu = new GpuRendering(() => device, logger: _logger);
        var drew = new List<string>();
        gpu.Drew += drew.Add;
        gpu.Apply(true);
        var wide = new SKImageInfo(1200, 40, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var small = new SKBitmap(Info);
        using var refused = new SKBitmap(wide);
        var told = new List<string>();
        void Record(SKCanvas canvas, string backend) => told.Add(backend);

        Assert.Equal(GpuRendering.Metal, gpu.Draw(small, Record));
        // A size the device refuses: the CPU draws it, though the status still says the GPU.
        Assert.Equal(GpuRendering.CpuBackend, gpu.Draw(refused, Record));
        Assert.Equal(GpuRendering.Metal, gpu.Status.Using);
        // A loss: the GPU's draw, then the CPU's redraw, which is the frame's.
        device.LoseAtSubmit = "Lost.";
        Assert.Equal(GpuRendering.CpuBackend, gpu.Draw(small, Record));

        Assert.Equal([GpuRendering.Metal, GpuRendering.CpuBackend, GpuRendering.Metal, GpuRendering.CpuBackend], told);
        Assert.Equal([GpuRendering.Metal, GpuRendering.CpuBackend, GpuRendering.CpuBackend], drew);
    }

    [Fact]
    public void AnExceptionInTheDrawItself_IsTheDrawsNotALoss()
    {
        var device = new FakeGpuDevice();
        using var gpu = new GpuRendering(() => device, logger: _logger);
        gpu.Apply(true);
        using var bitmap = new SKBitmap(Info);

        Assert.Throws<ArgumentException>(() => gpu.Draw(bitmap, _ => throw new ArgumentException("a scene's bug")));

        Assert.Equal(GpuRendering.Metal, gpu.Status.Using);
        Assert.False(device.Disposed);
        Assert.Equal(Cpu(SKColors.Red), Draw(gpu, SKColors.Red));
    }

    [Fact]
    public void Off_ReleasesTheDevice_AndOnAgainMakesANewOne()
    {
        var devices = new List<FakeGpuDevice>();
        using var gpu = new GpuRendering(() => { var d = new FakeGpuDevice(); devices.Add(d); return d; }, logger: _logger);
        gpu.Apply(true);
        Draw(gpu, SKColors.Red);

        Assert.Equal(GpuStatus.Cpu, gpu.Apply(false));

        Assert.True(Assert.Single(devices).Disposed);
        Assert.Equal(Cpu(SKColors.Red), Draw(gpu, SKColors.Red));
        Assert.Equal(1, devices[0].Surfaces);

        Assert.Equal(GpuRendering.Metal, gpu.Apply(true).Using);
        Assert.Equal(2, devices.Count);
    }

    [Fact]
    public void AfterALoss_OnStaysLost_UntilOffAndOnAgain_WhichMakesANewDevice()
    {
        var devices = new List<FakeGpuDevice>();
        using var gpu = new GpuRendering(() => { var d = new FakeGpuDevice(); devices.Add(d); return d; }, logger: _logger);
        gpu.Apply(true);
        devices[0].LoseAtSubmit = "Lost.";
        Draw(gpu, SKColors.Red);

        // On again while on: no retry within the run.
        Assert.Equal(GpuRendering.Lost, gpu.Apply(true).Fallback?.Code);
        Assert.Single(devices);

        gpu.Apply(false);
        var status = gpu.Apply(true);

        Assert.Equal((GpuRendering.Metal, (GpuFallback?)null), (status.Using, status.Fallback));
        Assert.Equal(2, devices.Count);
        Assert.Equal(Cpu(SKColors.Red), Draw(gpu, SKColors.Red));
        Assert.Equal(1, devices[1].Surfaces);
    }

    [Fact]
    public void TwoLoopsAndAThumbnailAtOnce_AllGetTheirFrames_DrawnOneAtATimeOnTheGpu()
    {
        var device = new FakeGpuDevice { Slow = true };
        using var gpu = new GpuRendering(() => device, logger: _logger);
        gpu.Apply(true);
        var colors = new[] { SKColors.Red, SKColors.Green, SKColors.Blue };
        var expected = colors.Select(c => Cpu(c)).ToArray();
        const int Frames = 20;

        var drawn = colors.Select((color, i) => Task.Run(() =>
        {
            var ok = 0;
            for (var f = 0; f < Frames; f++)
                ok += Draw(gpu, color).AsSpan().SequenceEqual(expected[i]) ? 1 : 0;
            return ok;
        }, TestContext.Current.CancellationToken)).ToArray();
        Task.WaitAll(drawn, TestContext.Current.CancellationToken);

        Assert.All(drawn, t => Assert.Equal(Frames, t.Result));
        Assert.Equal(3 * Frames, device.Surfaces);
        Assert.Equal(1, device.MostAtOnce);
    }

    [Fact]
    public void ASizeTheDeviceRefuses_IsDrawnOnTheCpu_LoggedOncePerSize_WithoutAFallback()
    {
        var device = new FakeGpuDevice { Refuses = info => info.Width > 1000 };
        using var gpu = new GpuRendering(() => device, logger: _logger);
        gpu.Apply(true);
        var wide = new SKImageInfo(1200, 40, SKColorType.Rgba8888, SKAlphaType.Premul);

        Assert.Equal(Cpu(SKColors.Red, wide), Draw(gpu, SKColors.Red, wide));
        Assert.Equal(Cpu(SKColors.Green, wide), Draw(gpu, SKColors.Green, wide));

        Assert.Equal(new GpuStatus(GpuRendering.Metal, device.Name, null), gpu.Status);
        Assert.Contains("1200", Assert.Single(Warnings).RenderMessage(), StringComparison.Ordinal);
        Assert.False(device.Disposed);
        Assert.Equal(0, device.Surfaces);

        Assert.Equal(Cpu(SKColors.Red), Draw(gpu, SKColors.Red));
        Assert.Equal(1, device.Surfaces);
    }

    [Fact]
    public void UnusedGpuResources_ArePurgedAtMostEvery10Seconds_After30SecondsUnused()
    {
        var clock = new ManualClock();
        var device = new FakeGpuDevice();
        using var gpu = new GpuRendering(() => device, clock, _logger);
        gpu.Apply(true);

        Draw(gpu, SKColors.Red);
        clock.Advance(9_000);
        Draw(gpu, SKColors.Red);
        Assert.Empty(device.Purged);

        clock.Advance(1_000);
        Draw(gpu, SKColors.Red);
        Draw(gpu, SKColors.Red);
        Assert.Equal([TimeSpan.FromSeconds(30)], device.Purged);
    }

    [Fact]
    public void Dispose_ReleasesTheDevice_AndLaterDrawsAreOnTheCpu()
    {
        var device = new FakeGpuDevice();
        var gpu = new GpuRendering(() => device, logger: _logger);
        gpu.Apply(true);

        gpu.Dispose();

        Assert.True(device.Disposed);
        Assert.Equal(Cpu(SKColors.Red), Draw(gpu, SKColors.Red));
        Assert.Equal(0, device.Surfaces);
    }

    [Fact]
    public void TheStatsOverlay_SaysWhatDraws()
    {
        var overlay = new StatsOverlay();

        Assert.StartsWith("Metal | FPS ", overlay.Text("Profile", "Metal").Right, StringComparison.Ordinal);
        Assert.StartsWith("CPU | FPS ", overlay.Text("Profile", "CPU").Right, StringComparison.Ordinal);
        Assert.Equal(("Metal", "Direct3D 12", "CPU", "CPU"),
            (GpuRendering.Label(GpuRendering.Metal), GpuRendering.Label(GpuRendering.Direct3D12), GpuRendering.Label(GpuRendering.CpuBackend), GpuRendering.Label("vulkan")));
    }

    private sealed class CollectingSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }
}

/// <summary>
/// A GPU stand-in whose surfaces are raster ones: it counts what it is asked, refuses sizes, fails a step or is lost on
/// demand, and records how many draws ran inside its scope at once.
/// </summary>
internal sealed class FakeGpuDevice : IGpuDevice
{
    private int _inScope;
    private int _mostAtOnce;
    private SKSurface? _last;
    private string? _lost;
    private readonly string _backend = GpuRendering.Metal;
    private readonly string _name = "Fake GPU";

    public string Backend
    {
        get => Answer(_backend);
        init => _backend = value;
    }

    public string Name
    {
        get => Answer(_name);
        init => _name = value;
    }

    /// <summary>Its <see cref="Backend"/> and <see cref="Name"/> throw once it is disposed, as a released device's may.</summary>
    public bool RefusesOnceDisposed { get; init; }

    private string Answer(string value) =>
        RefusesOnceDisposed && Disposed ? throw new ObjectDisposedException(nameof(FakeGpuDevice)) : value;

    /// <summary>Sizes it can't make a surface for.</summary>
    public Func<SKImageInfo, bool>? Refuses { get; init; }

    /// <summary>Holds each draw a moment, so draws that overlap would be seen.</summary>
    public bool Slow { get; init; }

    /// <summary>At the next submit, the device is lost with this reason, and the surface holds garbage.</summary>
    public string? LoseAtSubmit { get; set; }

    /// <summary>
    /// <c>surface</c>, <c>submit</c>, <c>scope</c> (making the draw's scope), <c>scopeDispose</c> (closing it),
    /// <c>checkLost</c> or <c>purge</c>: that step throws from now on.
    /// </summary>
    public string? Fail { get; set; }

    public int Surfaces;
    public int Submits;
    public int Scopes;
    public bool Disposed;
    public List<TimeSpan> Purged { get; } = [];

    public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

    public SKSurface? CreateSurface(SKImageInfo info)
    {
        if (Fail == "surface")
            throw new InvalidOperationException("The device can't make surfaces.");
        if (Refuses?.Invoke(info) == true)
            return null;

        Interlocked.Increment(ref Surfaces);
        _last = SKSurface.Create(info);
        return _last;
    }

    public void Submit()
    {
        Interlocked.Increment(ref Submits);
        if (Slow)
            Thread.Sleep(1);
        if (Fail == "submit")
            throw new InvalidOperationException("The device stopped answering.");
        if (LoseAtSubmit is { } reason)
        {
            _lost = reason;
            _last?.Canvas.Clear(SKColors.Magenta);
        }
    }

    public string? CheckLost() => Fail == "checkLost" ? throw new InvalidOperationException("/private/var/folders/x/device-state: unreadable") : _lost;

    public void PurgeUnused(TimeSpan unused)
    {
        if (Fail == "purge")
            throw new InvalidOperationException("The device can't free resources.");
        Purged.Add(unused);
    }

    public IDisposable? DrawScope()
    {
        if (Fail == "scope")
            throw new InvalidOperationException("The device can't open a scope.");
        Interlocked.Increment(ref Scopes);
        var now = Interlocked.Increment(ref _inScope);
        int most;
        do
        {
            most = Volatile.Read(ref _mostAtOnce);
        }
        while (now > most && Interlocked.CompareExchange(ref _mostAtOnce, now, most) != most);
        return new Scope(this);
    }

    public void Dispose() => Disposed = true;

    private sealed class Scope(FakeGpuDevice device) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Decrement(ref device._inScope);
            if (device.Fail == "scopeDispose")
                throw new InvalidOperationException("The device can't close its scope.");
        }
    }
}
