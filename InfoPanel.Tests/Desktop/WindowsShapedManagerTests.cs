using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Desktop;
using InfoPanel.Documents;
using InfoPanel.Output;
using InfoPanel.Spec;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// <see cref="DisplayWindowManager"/> on a desktop shaped as Windows gives it: every display at scale 1 with
/// coordinates in physical pixels, named by its GDI device name. <c>\\.\DISPLAY1</c> (2560×1440 at 0, 0, the primary)
/// and <c>\\.\DISPLAY2</c> (3840×2160 at 2560, −360, which Windows scales at 150 %: nothing in the coordinates shows
/// it). These pin the manager rather than fail first: it already handles any scale.
/// </summary>
public sealed class WindowsShapedManagerTests : IAsyncLifetime
{
    private static readonly DisplayInfo Display1 = new(@"\\.\DISPLAY1", new PointRect(0, 0, 2560, 1440), 1);
    private static readonly DisplayInfo Display2 = new(@"\\.\DISPLAY2", new PointRect(2560, -360, 3840, 2160), 1, "DELL U2723QE");

    private const string WideBarId = "3c1f0e7a-8b9d-4c2e-a5f6-0718293a4b5c";

    private readonly EngineFactory _base = new();
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 31, 9, 41, 7, TimeSpan.Zero));
    private readonly FakeDesktopPlatform _platform = new(Display1, Display2);
    private WebApplicationFactory<Program> _factory = null!;

    public ValueTask InitializeAsync()
    {
        var wideBar = TestDocs.Doc("Wide bar", 1920, 480, TestDocs.Scene("bar", 10, 10, TestDocs.Rect("#ff0000", 400, 40)));
        wideBar["id"] = WideBarId;
        wideBar["profile"]!["window"] = new JsonObject { ["enabled"] = true };
        var folder = Path.Combine(_base.DataDir, "profiles", WideBarId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "document.json"), wideBar.ToJsonString());
        File.WriteAllText(Path.Combine(_base.DataDir, "profiles.json"), $$"""{"order":["{{WideBarId}}"]}""");
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IDesktopPlatform>(_platform);
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton(new RenderServiceOptions(AutoTick: false));
        }));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _base.DisposeAsync();
        _platform.Dispose();
    }

    private Workspace Workspace => _factory.Services.GetRequiredService<Workspace>();

    private DisplayWindowManager Manager => _factory.Services.GetRequiredService<DisplayWindowManager>();

    private async Task<(ProfileDocument Doc, FakeWindow Window)> Start()
    {
        _ = _factory.Services;
        await _platform.IdleAsync();
        var doc = Workspace.Open(WideBarId)!;
        var window = await _platform.InvokeAsync(() => Manager.WindowOf(doc.Id) as FakeWindow) ?? throw new Xunit.Sdk.XunitException("No window");
        return (doc, window);
    }

    private static WindowSettings W(ProfileDocument doc) => doc.Current.Document.Profile.Window!;

    private async Task SetWindow(ProfileDocument doc, WindowSettings window)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(window, SpecJson.Options))!;
        TestDocs.Commit(doc, TestDocs.Profile(new JsonObject { ["window"] = json }));
        await _platform.IdleAsync();
    }

    private static PointRect Rect(double x, double y, double w, double h) => new(x, y, w, h);

    [Fact]
    public async Task AProfileOnTheSecondDisplay_LandsAtItsPixelPlace_AtItsOwnPixelSize()
    {
        var (doc, window) = await Start();
        var scaleChanges = 0;
        await _platform.InvokeAsync(() => window.BackingScaleChanged += () => scaleChanges++);
        Assert.Equal(Rect(0, 0, 1920, 480), window.Frame);

        await SetWindow(doc, W(doc) with { X = 100, Y = 50, Display = DisplayMatcher.TargetOf(Display2) });

        Assert.Equal(Rect(2660, -310, 1920, 480), window.Frame);
        Assert.True(window.Visible);
        Assert.Equal(0, scaleChanges);
        Assert.Empty(_platform.Errors);
    }

    [Fact]
    public async Task A1xImportedTargetMonitor_MatchesExactly()
    {
        // 1.4.x's TargetWindow for that monitor: its device name, its physical size and its physical top-left.
        var (doc, window) = await Start();

        await SetWindow(doc, W(doc) with
        {
            X = 0,
            Y = 0,
            Strict = true,
            Display = new DisplayMatch { Name = @"\\.\DISPLAY2", Width = 3840, Height = 2160, X = 2560, Y = -360 },
        });

        Assert.True(window.Visible);
        Assert.Equal(Rect(2560, -360, 1920, 480), window.Frame);
    }

    [Fact]
    public async Task ADragAcrossTheBoundary_CommitsTheSecondDisplay_AndThePositionOnIt()
    {
        var (doc, window) = await Start();

        await _platform.InvokeAsync(() => window.UserMove(Rect(2560 + 300, -360 + 40, 1920, 480)));
        await _platform.IdleAsync();

        Assert.Equal(2, doc.Rev);
        Assert.Equal((300, 40), (W(doc).X, W(doc).Y));
        var target = W(doc).Display!;
        Assert.Equal((@"\\.\DISPLAY2", 3840, 2160, 2560, -360), (target.Name, target.Width, target.Height, target.X, target.Y));
        Assert.Equal(Rect(2860, -320, 1920, 480), window.Frame);
        Assert.Equal((1920, 480), (doc.Current.Document.Profile.Width, doc.Current.Document.Profile.Height));
    }

    [Fact]
    public async Task AResize_CommitsThePixelSize()
    {
        var (doc, window) = await Start();
        await SetWindow(doc, W(doc) with { X = 100, Y = 50, Display = DisplayMatcher.TargetOf(Display2) });

        await _platform.InvokeAsync(() => window.UserResize(Rect(2660, -310, 1000, 300)));
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();

        Assert.Equal((1000, 300), (doc.Current.Document.Profile.Width, doc.Current.Document.Profile.Height));
        Assert.Equal((100, 50, @"\\.\DISPLAY2"), (W(doc).X, W(doc).Y, W(doc).Display!.Name));
        Assert.Equal(Rect(2660, -310, 1000, 300), window.Frame);
    }

    [Fact]
    public async Task TheSecondDisplayRemoved_HidesAStrictWindow_AndMovesAnotherToThePrimary()
    {
        var (doc, window) = await Start();
        await SetWindow(doc, W(doc) with { X = 100, Y = 50, Strict = true, Display = DisplayMatcher.TargetOf(Display2) });
        var revision = doc.Rev;

        await _platform.ChangeDisplaysAsync(Display1);
        Assert.False(window.Visible);

        await _platform.ChangeDisplaysAsync(Display1, Display2);
        Assert.True(window.Visible);
        Assert.Equal(Rect(2660, -310, 1920, 480), window.Frame);

        await SetWindow(doc, W(doc) with { Strict = false });
        await _platform.ChangeDisplaysAsync(Display1);
        Assert.True(window.Visible);
        Assert.Equal(Rect(100, 50, 1920, 480), window.Frame);
        Assert.Equal(revision + 1, doc.Rev); // display changes never edit the profile
        Assert.Empty(_platform.Errors);
    }
}
