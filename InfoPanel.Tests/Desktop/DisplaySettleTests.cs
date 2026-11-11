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
/// <see cref="DisplayWindowManager"/> while displays come and go: the system moves windows then, and macOS reports such
/// a move as a resize that keeps the window's size. Neither that nor a frame left on no display is the user's edit; the
/// window keeps its saved places and goes back to them once the displays settle. Displays: a built-in panel (3024×1964
/// pixels at 2×, the primary) and an external monitor (5120×2880 pixels at 2×, above and to the left), both reporting
/// their ids; the profile is 800×480.
/// </summary>
public sealed class DisplaySettleTests : IAsyncLifetime
{
    private static readonly DisplayInfo BuiltIn = new("Built-in Display", new PointRect(0, 0, 1512, 982), 2, Id: "APPA061");
    private static readonly DisplayInfo External = new("External 5K", new PointRect(-408, -1440, 2560, 1440), 2, Id: "AUSAA08");

    /// <summary>Longer than any settle window the manager waits for after the displays last changed.</summary>
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(10);

    private const string PanelId = "5e2b7c41-9d3a-4f60-b8e1-7a2c4d6f8e90";

    private readonly EngineFactory _base = new();
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 11, 11, 19, 25, 0, TimeSpan.Zero));
    private readonly FakeDesktopPlatform _platform = new(BuiltIn, External);
    private WebApplicationFactory<Program> _factory = null!;

    public ValueTask InitializeAsync()
    {
        var panel = TestDocs.Doc("Panel", 800, 480, TestDocs.Scene("bar", 10, 10, TestDocs.Rect("#ff0000", 400, 40)));
        panel["id"] = PanelId;
        panel["profile"]!["window"] = new JsonObject { ["enabled"] = true };
        var folder = Path.Combine(_base.DataDir, "profiles", PanelId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "document.json"), panel.ToJsonString());
        File.WriteAllText(Path.Combine(_base.DataDir, "profiles.json"), $$"""{"order":["{{PanelId}}"]}""");
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
        var doc = Workspace.Open(PanelId)!;
        var window = await _platform.InvokeAsync(() => Manager.WindowOf(doc.Id) as FakeWindow) ?? throw new Xunit.Sdk.XunitException("No window");
        return (doc, window);
    }

    private static WindowSettings W(ProfileDocument doc) => doc.Current.Document.Profile.Window!;

    private static WindowSpot Spot(DisplayInfo display, int x, int y) => new() { Display = DisplayMatcher.TargetOf(display), X = x, Y = y };

    /// <summary>A client's commit of the window on <paramref name="display"/> at <paramref name="x"/>, <paramref name="y"/>.</summary>
    private async Task SetPlace(ProfileDocument doc, DisplayInfo display, int x, int y, params WindowSpot[] spots)
    {
        var window = W(doc) with { X = x, Y = y, Display = DisplayMatcher.TargetOf(display), Spots = spots.Length == 0 ? null : spots };
        var json = JsonNode.Parse(JsonSerializer.Serialize(window, SpecJson.Options))!;
        TestDocs.Commit(doc, TestDocs.Profile(new JsonObject { ["window"] = json }));
        await _platform.IdleAsync();
    }

    /// <summary>Lets a resize's quiet time pass, and what it posts run.</summary>
    private async Task Quiet()
    {
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
    }

    private static PointRect Rect(double x, double y, double w, double h) => new(x, y, w, h);

    // The window on the external monitor at 4320, 62 pixels: 1752, -1409 points, 400×240 points at 2×.
    private static readonly PointRect OnExternal = Rect(1752, -1409, 400, 240);

    [Fact]
    public async Task ADisplayRemoved_AndTheWindowMovedOnToAnotherByTheSystem_ReportedAsAResize_IsNoEdit_AndTheStrictWindowStaysHidden()
    {
        var (doc, window) = await Start();
        await SetPlace(doc, External, 4320, 62);
        Assert.Equal(OnExternal, window.Frame);
        var (revision, places) = (doc.Rev, W(doc));

        await _platform.ChangeDisplaysAsync(BuiltIn); // unplugged: strict by default, so hidden
        Assert.False(window.Visible);

        // The system moves the window on to the built-in display, its size unchanged, and macOS reports that as a resize.
        await _platform.InvokeAsync(() => window.UserResize(Rect(200, 100, 400, 240)));
        await Quiet();

        Assert.Equal((revision, places), (doc.Rev, W(doc)));
        Assert.Equal((800, 480), (doc.Current.Document.Profile.Width, doc.Current.Document.Profile.Height));
        Assert.False(window.Visible);
        Assert.Empty(_platform.Errors);
    }

    [Fact]
    public async Task TheSystemsMoveReportedBeforeTheDisplayChange_IsNoEdit()
    {
        var (doc, window) = await Start();
        await SetPlace(doc, External, 4320, 62);
        var (revision, places) = (doc.Rev, W(doc));

        // The external monitor is already gone from the list when the system's move of the window is reported, before
        // the platform says the displays changed.
        await _platform.InvokeAsync(() =>
        {
            _platform.Displays = [BuiltIn];
            window.UserResize(Rect(200, 100, 400, 240));
        });
        await _platform.ChangeDisplaysAsync(BuiltIn);
        await Quiet();

        Assert.Equal((revision, places), (doc.Rev, W(doc)));
        Assert.False(window.Visible);
        Assert.Empty(_platform.Errors);
    }

    [Fact]
    public async Task AResizeReportWhoseFrameLiesOnNoDisplay_IsNoEdit_AndTheWindowComesBackAtItsSavedPlaceWithItsDisplay()
    {
        var (doc, window) = await Start();
        await SetPlace(doc, External, 4320, 62);
        var (revision, places) = (doc.Rev, W(doc));
        await _platform.ChangeDisplaysAsync(BuiltIn);
        _clock.Advance(Settled);
        await _platform.IdleAsync();

        // Where the external monitor was: 2224, -854 pixels from the built-in display's top-left, above it, on no display.
        await _platform.InvokeAsync(() => window.UserResize(Rect(1112, -427, 400, 240)));
        await Quiet();

        Assert.Equal((revision, places), (doc.Rev, W(doc)));
        Assert.False(window.Visible);

        await _platform.ChangeDisplaysAsync(BuiltIn, External); // plugged back in
        Assert.True(window.Visible);
        Assert.Equal(OnExternal, window.Frame);
        Assert.Equal((revision, places), (doc.Rev, W(doc)));
        Assert.Empty(_platform.Errors);
    }

    [Fact]
    public async Task ADragWhoseFrameLiesOnNoDisplay_IsNoMove_AndTheWindowGoesBackToItsPlace()
    {
        var (doc, window) = await Start();
        await SetPlace(doc, BuiltIn, 40, 20);
        Assert.Equal(Rect(20, 10, 400, 240), window.Frame);
        var (revision, places) = (doc.Rev, W(doc));

        // Right of and below the built-in display, below the external one: on neither.
        await _platform.InvokeAsync(() => window.UserMove(Rect(1600, 1000, 400, 240)));
        await _platform.IdleAsync();

        Assert.Equal((revision, places), (doc.Rev, W(doc)));
        Assert.True(window.Visible);
        Assert.Equal(Rect(20, 10, 400, 240), window.Frame);
        Assert.Empty(_platform.Errors);
    }

    [Fact]
    public async Task AUserDragOntoAnotherListedDisplay_JustAfterADisplayChange_IsSavedAsBefore()
    {
        var (doc, window) = await Start();
        await SetPlace(doc, BuiltIn, 40, 20);
        await _platform.ChangeDisplaysAsync(BuiltIn);
        await _platform.ChangeDisplaysAsync(BuiltIn, External);

        await _platform.InvokeAsync(() => window.UserMove(OnExternal));
        await _platform.IdleAsync();

        Assert.Equal((4320, 62, DisplayMatcher.TargetOf(External)), (W(doc).X, W(doc).Y, W(doc).Display));
        Assert.Equal([Spot(BuiltIn, 40, 20)], W(doc).Spots);
        Assert.Equal(OnExternal, window.Frame);
        Assert.Empty(_platform.Errors);
    }

    [Fact]
    public async Task OnceTheDisplaysSettle_AWindowTheSystemMovedSilently_GoesBackToItsSavedPlace()
    {
        var (doc, window) = await Start();
        await SetPlace(doc, External, 4320, 62, Spot(BuiltIn, 40, 20));
        var (revision, places) = (doc.Rev, W(doc));

        await _platform.ChangeDisplaysAsync(BuiltIn); // unplugged: its spot on the built-in display
        Assert.True(window.Visible);
        Assert.Equal(Rect(20, 10, 400, 240), window.Frame);

        // After the engine placed it, the system moves it once more and says nothing.
        await _platform.InvokeAsync(() => window.Relocate(Rect(1112, -427, 400, 240)));
        _clock.Advance(Settled);
        await _platform.IdleAsync();
        await _platform.IdleAsync();

        Assert.True(window.Visible);
        Assert.Equal(Rect(20, 10, 400, 240), window.Frame);
        Assert.Equal((revision, places), (doc.Rev, W(doc)));

        await _platform.ChangeDisplaysAsync(BuiltIn, External);
        Assert.Equal(OnExternal, window.Frame);
        Assert.Empty(_platform.Errors);
    }

    [Fact]
    public async Task AResizeStillPending_WhenThePlatformSaysTheDisplaysChangedWithTheListUnchanged_IsSaved()
    {
        var (doc, window) = await Start();
        await SetPlace(doc, BuiltIn, 40, 20);
        await _platform.ChangeDisplaysAsync(BuiltIn); // the external monitor unplugged; the window stays where it is

        // 0.85 s later the user resizes the window and lets go; its commit is due 0.3 s after that.
        _clock.Advance(TimeSpan.FromMilliseconds(850));
        await _platform.InvokeAsync(() => window.UserResize(Rect(20, 10, 450, 240)));

        // At 1 s the platform says the displays changed once more (Windows does, a second after a change), the list as it was.
        _clock.Advance(TimeSpan.FromMilliseconds(150));
        await _platform.ChangeDisplaysAsync(BuiltIn);
        await Quiet();

        Assert.Equal((900, 480), (doc.Current.Document.Profile.Width, doc.Current.Document.Profile.Height));
        Assert.Equal((40, 20, DisplayMatcher.TargetOf(BuiltIn)), (W(doc).X, W(doc).Y, W(doc).Display));
        Assert.Equal(Rect(20, 10, 450, 240), window.Frame);
        Assert.Empty(_platform.Errors);
    }

    [Fact]
    public async Task WhileTheDisplaysSettle_TheSystemMovingAWindowMidResize_ReportedAsAResize_KeepsTheUsersResize()
    {
        var (doc, window) = await Start();
        await SetPlace(doc, BuiltIn, 40, 20);
        await _platform.ChangeDisplaysAsync(BuiltIn); // the external monitor unplugged; the window stays where it is

        await _platform.InvokeAsync(() => window.UserResize(Rect(20, 10, 450, 240)));
        // Before the resize is quiet the system moves the window, its new size kept, and macOS reports that as a resize.
        await _platform.InvokeAsync(() => window.UserResize(Rect(200, 100, 450, 240)));
        await Quiet();

        Assert.Equal((900, 480), (doc.Current.Document.Profile.Width, doc.Current.Document.Profile.Height));
        Assert.Equal((40, 20, DisplayMatcher.TargetOf(BuiltIn)), (W(doc).X, W(doc).Y, W(doc).Display));
        Assert.Equal(Rect(20, 10, 450, 240), window.Frame);
        Assert.Empty(_platform.Errors);
    }

    [Fact]
    public async Task WhileTheDisplaysSettle_TheSystemMovingAWindowMidResize_ThenTheButtonUpBeforeTheResizeIsQuiet_SavesTheUsersResizeAndNoDrag()
    {
        var (doc, window) = await Start();
        await SetPlace(doc, BuiltIn, 40, 20);
        await _platform.ChangeDisplaysAsync(BuiltIn); // the external monitor unplugged; the window stays where it is

        // The user presses on the window's edge and resizes it; before the resize is quiet the system moves the window,
        // its new size kept, and macOS reports that as a resize; then the user lets go.
        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.UserResize(Rect(20, 10, 450, 240));
            window.UserResize(Rect(200, 100, 450, 240));
            window.EndUserMove();
        });
        await _platform.IdleAsync();
        _clock.Advance(Settled);
        await _platform.IdleAsync();
        await _platform.IdleAsync();

        Assert.Equal((900, 480), (doc.Current.Document.Profile.Width, doc.Current.Document.Profile.Height));
        Assert.Equal((40, 20, DisplayMatcher.TargetOf(BuiltIn)), (W(doc).X, W(doc).Y, W(doc).Display));
        Assert.Null(W(doc).Spots);
        Assert.Equal(Rect(20, 10, 450, 240), window.Frame);
        Assert.Empty(_platform.Errors);
    }

    [Fact]
    public async Task WhileTheDisplaysSettle_TheSystemsMoveReportedAsAResize_ThenAClick_IsNoDrag()
    {
        var (doc, window) = await Start();
        await SetPlace(doc, BuiltIn, 40, 20);
        var (revision, places) = (doc.Rev, W(doc));
        await _platform.ChangeDisplaysAsync(BuiltIn); // the external monitor unplugged; the window stays where it is

        // The system moves the window, its size kept, and macOS reports that as a resize; then the user clicks it.
        await _platform.InvokeAsync(() => window.UserResize(Rect(200, 100, 400, 240)));
        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.EndUserMove();
        });
        await _platform.IdleAsync();
        _clock.Advance(Settled);
        await _platform.IdleAsync();
        await _platform.IdleAsync();

        Assert.Equal((revision, places), (doc.Rev, W(doc)));
        Assert.Equal(Rect(20, 10, 400, 240), window.Frame);
        Assert.Empty(_platform.Errors);
    }

    [Fact]
    public async Task ThePlatformSayingTheDisplaysChangedWithTheListUnchanged_DoesNotPutOffWhenTheyHaveSettled()
    {
        var (doc, window) = await Start();
        await SetPlace(doc, BuiltIn, 40, 20);
        var (revision, places) = (doc.Rev, W(doc));
        await _platform.ChangeDisplaysAsync(BuiltIn); // the external monitor unplugged; the window stays where it is

        // At 1 s the platform says the displays changed once more, the list as it was; then the system moves the window
        // and says nothing.
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _platform.ChangeDisplaysAsync(BuiltIn);
        await _platform.InvokeAsync(() => window.Relocate(Rect(200, 100, 400, 240)));

        // 2 s after the change itself, not after the second notification, the displays have settled: the window is placed again.
        _clock.Advance(DisplayWindowManager.DisplaySettle - TimeSpan.FromSeconds(1));
        await _platform.IdleAsync();
        await _platform.IdleAsync();

        Assert.Equal(Rect(20, 10, 400, 240), window.Frame);
        Assert.Equal((revision, places), (doc.Rev, W(doc)));
        Assert.Empty(_platform.Errors);
    }
}
