using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Desktop;
using InfoPanel.Documents;
using InfoPanel.Output;
using InfoPanel.Spec;
using InfoPanel.Tests.Preview;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// <see cref="DisplayWindowManager"/> on the engine host with a <see cref="FakeDesktopPlatform"/>, a manual clock and
/// hand-driven render loops, and the manager's log captured. The data folder starts with "Wide bar" (1920×480, its
/// desktop window enabled). Displays: "Built-in" (1512×982 points at 2×, the primary) and "External" (2560×1440 at
/// 1×, above and to the left).
/// </summary>
public sealed class DisplayWindowManagerTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DisplayInfo BuiltIn = new("Built-in", new PointRect(0, 0, 1512, 982), 2);
    private static readonly DisplayInfo External = new("External", new PointRect(-408, -1440, 2560, 1440), 1);

    private readonly EngineFactory _base = new();
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 26, 9, 41, 7, TimeSpan.Zero));
    private readonly CapturedLog _log = new();
    private FakeDesktopPlatform _platform = new(BuiltIn, External);
    private WebApplicationFactory<Program> _factory = null!;

    private const string WideBarId = "7d3e2a10-5b4c-4e8f-9a1b-2c3d4e5f6a70";

    public ValueTask InitializeAsync()
    {
        var wideBar = TestDocs.Doc("Wide bar", 1920, 480, TestDocs.Scene("bar", 10, 10, TestDocs.Rect("#ff0000", 400, 40)));
        wideBar["id"] = WideBarId;
        wideBar["profile"]!["window"] = new JsonObject { ["enabled"] = true };
        Seed(WideBarId, wideBar.ToJsonString());
        _factory = Create(_platform);
        return ValueTask.CompletedTask;
    }

    /// <summary>Stores a document in the data folder before the host starts (the store lists every profile folder).</summary>
    private void Seed(string id, string json)
    {
        var folder = Path.Combine(_base.DataDir, "profiles", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "document.json"), json);
        if (!File.Exists(Path.Combine(_base.DataDir, "profiles.json")))
            File.WriteAllText(Path.Combine(_base.DataDir, "profiles.json"), $$"""{"order":["{{id}}"]}""");
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _base.DisposeAsync();
        _platform.Dispose();
    }

    private WebApplicationFactory<Program> Create(FakeDesktopPlatform platform, bool noWindows = false) =>
        _base.WithWebHostBuilder(builder =>
        {
            if (noWindows)
                builder.UseSetting("Engine:NoWindows", "true");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IDesktopPlatform>(platform);
                services.AddSingleton<TimeProvider>(_clock);
                services.AddSingleton(new RenderServiceOptions(AutoTick: false));
                services.AddSingleton<ILogger<DisplayWindowManager>>(_log);
            });
        });

    private Workspace Workspace => _factory.Services.GetRequiredService<Workspace>();

    private DisplayWindowManager Manager => _factory.Services.GetRequiredService<DisplayWindowManager>();

    private RenderService Render => _factory.Services.GetRequiredService<RenderService>();

    /// <summary>Starts the host and waits for the manager's first pass; returns "Wide bar".</summary>
    private async Task<ProfileDocument> Start()
    {
        _ = _factory.Services;
        await _platform.IdleAsync();
        return Workspace.Open(WideBarId)!;
    }

    private Task<FakeWindow?> WindowOf(string id) => _platform.InvokeAsync(() => Manager.WindowOf(id) as FakeWindow);

    private async Task<FakeWindow> RequireWindow(string id) => await WindowOf(id) ?? throw new Xunit.Sdk.XunitException($"No window for {id}");

    private static DocumentProfile P(ProfileDocument doc) => doc.Current.Document.Profile;

    private static WindowSettings W(ProfileDocument doc) => P(doc).Window!;

    /// <summary>
    /// A client's <c>profile</c> commit: <c>width</c> and <c>height</c> set on the profile, every other member of
    /// <paramref name="set"/> on its <c>window</c> (the rest of the window kept).
    /// </summary>
    private async Task Patch(ProfileDocument doc, JsonObject set)
    {
        var window = JsonNode.Parse(JsonSerializer.Serialize(P(doc).Window ?? new WindowSettings { Enabled = true }, SpecJson.Options))!.AsObject();
        var profile = new JsonObject();
        foreach (var (key, value) in set)
        {
            if (key is "width" or "height")
                profile[key] = value?.DeepClone();
            else
                window[key] = value?.DeepClone();
        }

        profile["window"] = window;
        TestDocs.Commit(doc, TestDocs.Profile(profile));
        await _platform.IdleAsync();
    }

    /// <summary>A client's commit of the window's place on <paramref name="display"/> (as a drag writes it).</summary>
    private static void PlaceOn(ProfileDocument doc, int x, int y, DisplayInfo display)
    {
        var window = (P(doc).Window ?? new WindowSettings { Enabled = true }) with { X = x, Y = y, Display = DisplayMatcher.TargetOf(display) };
        var json = JsonNode.Parse(JsonSerializer.Serialize(window, SpecJson.Options))!;
        TestDocs.Commit(doc, TestDocs.Profile(new JsonObject { ["window"] = json }));
    }

    private static void Undo(ProfileDocument doc) => Assert.NotNull(doc.Undo(new HistoryRequest { Instance = doc.Instance, BaseRev = doc.Rev }).Result);

    private static void Redo(ProfileDocument doc) => Assert.NotNull(doc.Redo(new HistoryRequest { Instance = doc.Instance, BaseRev = doc.Rev }).Result);

    /// <summary>A new profile with its desktop window enabled; returns its id.</summary>
    private string CreateActive(string name, int width, int height)
    {
        var doc = TestDocs.Doc(name, width, height);
        doc["profile"]!["window"] = new JsonObject { ["enabled"] = true };
        return TestDocs.Create(Workspace, doc);
    }

    private static PointRect Rect(double x, double y, double w, double h) => new(x, y, w, h);

    private void AssertNoErrors() => Assert.Empty(_platform.Errors);

    // Monitors that report their EDID's id, for a window's places on several displays (window.spots).
    private static readonly DisplayInfo Laptop = BuiltIn with { Id = "APPA050" };                                              // 3024×1964 px at 2×
    private static readonly DisplayInfo Work = new("DELL U2720Q", new PointRect(-408, -1440, 2560, 1440), 1, Id: "DEL40F4");  // 2560×1440 px at 1×
    private static readonly DisplayInfo Home = new("LG HDR 4K", new PointRect(1512, 0, 1920, 1080), 2, Id: "GSM5B7F");        // 3840×2160 px at 2×

    /// <summary>Starts the host on a desktop of <paramref name="displays"/> instead; returns "Wide bar".</summary>
    private async Task<ProfileDocument> StartOn(params DisplayInfo[] displays)
    {
        await _factory.DisposeAsync();
        _platform.Dispose();
        _platform = new FakeDesktopPlatform(displays);
        _factory = Create(_platform);
        return await Start();
    }

    private static WindowSpot Spot(DisplayInfo display, int x, int y) => new() { Display = DisplayMatcher.TargetOf(display), X = x, Y = y };

    /// <summary>A client's commit of the window on <paramref name="display"/> at <paramref name="x"/>, <paramref name="y"/>, remembering <paramref name="spots"/>.</summary>
    private async Task SetPlaces(ProfileDocument doc, DisplayInfo display, int x, int y, bool? strict, params WindowSpot[] spots)
    {
        var window = (P(doc).Window ?? new WindowSettings { Enabled = true }) with
        {
            X = x,
            Y = y,
            Display = DisplayMatcher.TargetOf(display),
            Spots = spots.Length == 0 ? null : spots,
            Strict = strict,
        };
        TestDocs.Commit(doc, TestDocs.Profile(new JsonObject { ["window"] = JsonNode.Parse(JsonSerializer.Serialize(window, SpecJson.Options)) }));
        await _platform.IdleAsync();
    }

    [Fact]
    public async Task UnpluggedAndPluggedBackIn_TheWindowGoesToItsMostRecentConnectedPlace_WithoutAnEdit()
    {
        var doc = await StartOn(Laptop, Work);
        var window = await RequireWindow(doc.Id);
        await SetPlaces(doc, Work, 100, 50, null, Spot(Laptop, 20, 10));
        Assert.Equal(Rect(-308, -1390, 1920, 480), window.Frame);
        var revision = doc.Rev;

        await _platform.ChangeDisplaysAsync(Laptop); // unplugged: its spot on the laptop
        Assert.True(window.Visible);
        Assert.Equal(Rect(10, 5, 960, 240), window.Frame);

        await _platform.ChangeDisplaysAsync(Laptop, Work); // plugged back in
        Assert.True(window.Visible);
        Assert.Equal(Rect(-308, -1390, 1920, 480), window.Frame);
        Assert.Equal(revision, doc.Rev); // display changes never edit the profile
        AssertNoErrors();
    }

    [Fact]
    public async Task TwoOfItsDisplaysConnected_TheMostRecentWins_AndWithoutIt_TheOther()
    {
        var doc = await StartOn(Laptop, Work, Home);
        var window = await RequireWindow(doc.Id);
        await SetPlaces(doc, Home, 200, 100, null, Spot(Work, 100, 50));
        Assert.Equal(Rect(1612, 50, 960, 240), window.Frame);

        await _platform.ChangeDisplaysAsync(Laptop, Work);
        Assert.Equal(Rect(-308, -1390, 1920, 480), window.Frame);

        await _platform.ChangeDisplaysAsync(Laptop, Work, Home);
        Assert.Equal(Rect(1612, 50, 960, 240), window.Frame);
        Assert.True(window.Visible);
        AssertNoErrors();
    }

    [Fact]
    public async Task NoneOfItsDisplaysConnected_HidesTheWindow_ByDefault_UntilOneIs()
    {
        var doc = await StartOn(Laptop, Work, Home);
        var window = await RequireWindow(doc.Id);
        await SetPlaces(doc, Work, 100, 50, null, Spot(Home, 300, 60));
        Assert.Null(W(doc).Strict);

        await _platform.ChangeDisplaysAsync(Laptop);
        Assert.False(window.Visible);
        Assert.Same(window, await WindowOf(doc.Id)); // still open

        await _platform.ChangeDisplaysAsync(Laptop, Home);
        Assert.True(window.Visible);
        Assert.Equal(Rect(1662, 30, 960, 240), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task NoneOfItsDisplaysConnected_NotStrict_ShowsItOnTheMainDisplay_PulledIn_WithoutAnEdit()
    {
        var doc = await StartOn(Laptop, Work, Home);
        var window = await RequireWindow(doc.Id);
        await SetPlaces(doc, Work, 2000, 1200, false, Spot(Home, 300, 60));
        var revision = doc.Rev;

        // On the laptop's 3024×1964 pixels the 1920×480-pixel window at 2000, 1200 would stick out: it moves to 1104, 1200.
        await _platform.ChangeDisplaysAsync(Laptop);
        Assert.True(window.Visible);
        Assert.Equal(Rect(552, 600, 960, 240), window.Frame);
        Assert.Equal((revision, 2000, 1200), (doc.Rev, W(doc).X, W(doc).Y));
        AssertNoErrors();
    }

    [Fact]
    public async Task ADragOntoAnotherDisplay_RemembersThePlaceItLeft_AndADragBack_TheOther()
    {
        var doc = await StartOn(Laptop, Work);
        var window = await RequireWindow(doc.Id);
        await SetPlaces(doc, Laptop, 40, 20, null);

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();
        Assert.Equal((120, 64, DisplayMatcher.TargetOf(Work)), (W(doc).X, W(doc).Y, W(doc).Display));
        Assert.Equal([Spot(Laptop, 40, 20)], W(doc).Spots);

        await _platform.InvokeAsync(() => window.UserMove(Rect(100, 50, 1920, 480)));
        await _platform.IdleAsync();
        Assert.Equal((200, 100, DisplayMatcher.TargetOf(Laptop)), (W(doc).X, W(doc).Y, W(doc).Display));
        Assert.Equal([Spot(Work, 120, 64)], W(doc).Spots);
        Assert.Equal(Rect(100, 50, 960, 240), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task AResize_OnTheMainDisplayByTheFallback_MakesItTheWindowsPlace_AndRemembersItsOwn()
    {
        var doc = await StartOn(Laptop, Work);
        var window = await RequireWindow(doc.Id);
        await SetPlaces(doc, Work, 2000, 1200, false);
        await _platform.ChangeDisplaysAsync(Laptop);
        Assert.Equal(Rect(552, 600, 960, 240), window.Frame);

        await _platform.InvokeAsync(() => window.UserResize(Rect(552, 600, 900, 230)));
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();

        Assert.Equal((1800, 460), (P(doc).Width, P(doc).Height));
        Assert.Equal((1104, 1200, DisplayMatcher.TargetOf(Laptop)), (W(doc).X, W(doc).Y, W(doc).Display));
        Assert.Equal([Spot(Work, 2000, 1200)], W(doc).Spots);
        AssertNoErrors();
    }

    [Fact]
    public async Task AResizeWhileAtASpot_MakesThatDisplayThePlace_AndKeepsTheOtherDisplaysOwn()
    {
        var doc = await StartOn(Laptop, Work);
        var window = await RequireWindow(doc.Id);
        await SetPlaces(doc, Work, 100, 50, null, Spot(Laptop, 20, 10));
        await _platform.ChangeDisplaysAsync(Laptop);
        Assert.Equal(Rect(10, 5, 960, 240), window.Frame);

        // From the top-left edge: the window's place on the laptop moves too, the work monitor's stays its own.
        await _platform.InvokeAsync(() => window.UserResize(Rect(15, 8, 900, 230)));
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();

        Assert.Equal((1800, 460), (P(doc).Width, P(doc).Height));
        Assert.Equal((30, 16, DisplayMatcher.TargetOf(Laptop)), (W(doc).X, W(doc).Y, W(doc).Display));
        Assert.Equal([Spot(Work, 100, 50)], W(doc).Spots);
        Assert.Equal(Rect(15, 8, 900, 230), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task UndoOfADrag_BringsBackTheDisplayAndThePlacesItHad()
    {
        var doc = await StartOn(Laptop, Work, Home);
        var window = await RequireWindow(doc.Id);
        await SetPlaces(doc, Home, 200, 100, null, Spot(Laptop, 40, 20));
        var before = W(doc);

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();
        var after = W(doc);
        Assert.Equal(DisplayMatcher.TargetOf(Work), after.Display);
        Assert.Equal([Spot(Home, 200, 100), Spot(Laptop, 40, 20)], after.Spots);

        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal(before, W(doc));
        Assert.Equal(Rect(1612, 50, 960, 240), window.Frame);

        Redo(doc);
        await _platform.IdleAsync();
        Assert.Equal(after, W(doc));
        Assert.Equal(Rect(-288, -1376, 1920, 480), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task ADragAcrossADisplayChange_IsNoMove_AndTheWindowGoesToItsNextConnectedPlace()
    {
        var doc = await StartOn(Laptop, Work);
        var window = await RequireWindow(doc.Id);
        await SetPlaces(doc, Work, 100, 50, null, Spot(Laptop, 20, 10));
        var (revision, places) = (doc.Rev, W(doc));

        // The button goes down on the window, its monitor is unplugged, the window is dragged on the laptop and let go.
        await _platform.InvokeAsync(window.BeginUserMove);
        await _platform.ChangeDisplaysAsync(Laptop);
        await _platform.InvokeAsync(() => window.DragTo(Rect(300, 200, 1920, 480)));
        await _platform.InvokeAsync(window.EndUserMove);
        await _platform.IdleAsync();

        Assert.Equal((revision, places), (doc.Rev, W(doc)));
        Assert.True(window.Visible);
        Assert.Equal(Rect(10, 5, 960, 240), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task ADrag_WhileAnotherClientChangesTheWindowsPlaces_KeepsThem()
    {
        var doc = await StartOn(Laptop, Work, Home);
        var window = await RequireWindow(doc.Id);
        await SetPlaces(doc, Laptop, 40, 20, null);
        // After the drop read the profile, before its commit: another client gives the window a place on the home monitor.
        Manager.BeforeCommit = _ =>
        {
            Manager.BeforeCommit = null;
            var changed = JsonNode.Parse(JsonSerializer.Serialize(W(doc) with { Spots = [Spot(Home, 300, 60)] }, SpecJson.Options))!;
            TestDocs.Commit(doc, TestDocs.Profile(new JsonObject { ["window"] = changed }));
        };
        var revision = doc.Rev;

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal(revision + 2, doc.Rev);
        Assert.Equal((120, 64, DisplayMatcher.TargetOf(Work)), (W(doc).X, W(doc).Y, W(doc).Display));
        Assert.Equal([Spot(Laptop, 40, 20), Spot(Home, 300, 60)], W(doc).Spots);
        AssertNoErrors();
    }

    [Fact]
    public async Task ADragOntoTheSamePlace_WithPlacesOnOtherDisplays_IsNoEdit()
    {
        var doc = await StartOn(Laptop, Work);
        var window = await RequireWindow(doc.Id);
        await SetPlaces(doc, Laptop, 0, 0, null, Spot(Work, 100, 50));
        var revision = doc.Rev;

        await _platform.InvokeAsync(() => window.UserMove(Rect(0.2, 0.2, 960, 240))); // less than half a pixel away
        await _platform.IdleAsync();

        Assert.Equal(revision, doc.Rev);
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task AKnownMonitorBackAtASmallerSize_PullsTheWindowOnToIt_WithoutAnEdit_AndAtItsSize_ItsSavedPlace()
    {
        // The home monitor at 3840×2160 pixels at 1×, then the same monitor (its id) at 1920×1080.
        var wide = new DisplayInfo("LG HDR 4K", new PointRect(1512, 0, 3840, 2160), 1, Id: "GSM5B7F");
        var narrow = wide with { Frame = new PointRect(1512, 0, 1920, 1080) };
        var doc = await StartOn(Laptop, wide);
        var window = await RequireWindow(doc.Id);
        await Patch(doc, new JsonObject { ["width"] = 800, ["height"] = 480 });
        await SetPlaces(doc, wide, 3040, 100, null);
        Assert.Equal(Rect(1512 + 3040, 100, 800, 480), window.Frame);
        var (revision, places) = (doc.Rev, W(doc));

        await _platform.ChangeDisplaysAsync(Laptop, narrow);
        Assert.True(window.Visible);
        Assert.Equal(Rect(1512 + 1120, 100, 800, 480), window.Frame);

        await _platform.ChangeDisplaysAsync(Laptop, wide);
        Assert.Equal(Rect(1512 + 3040, 100, 800, 480), window.Frame);
        Assert.Equal((revision, places), (doc.Rev, W(doc))); // the pull-in is never written

        // As a spot, and at its recorded size a saved place half off the monitor stays exactly where it was saved.
        await SetPlaces(doc, Work, 100, 50, null, Spot(wide, 3040, 1900));
        await _platform.ChangeDisplaysAsync(Laptop, narrow);
        Assert.Equal(Rect(1512 + 1120, 600, 800, 480), window.Frame);
        await SetPlaces(doc, wide, 3400, 1900, null);
        await _platform.ChangeDisplaysAsync(Laptop, wide);
        Assert.Equal(Rect(1512 + 3400, 1900, 800, 480), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task AResizeFromTheLeftEdge_OntoTheDisplayBeside_MakesThatDisplayThePlace_AndRemembersTheOneItLeft()
    {
        // Two monitors side by side at 1×: A at x 0…1919, B at 1920…3839.
        var a = new DisplayInfo("DELL U2720Q", new PointRect(0, 0, 1920, 1080), 1, Id: "DEL40F4");
        var b = new DisplayInfo("LG HDR 4K", new PointRect(1920, 0, 1920, 1080), 1, Id: "GSM5B7F");
        var doc = await StartOn(a, b);
        var window = await RequireWindow(doc.Id);
        await Patch(doc, new JsonObject { ["width"] = 800, ["height"] = 480 });
        await SetPlaces(doc, b, 10, 100, null);
        Assert.Equal(Rect(1930, 100, 800, 480), window.Frame);

        // The left edge dragged from 1930 to 1925 (still on B), then on to 1900, on A: the last one counts.
        await _platform.InvokeAsync(() => window.UserResize(Rect(1925, 100, 805, 480)));
        await _platform.InvokeAsync(() => window.UserResize(Rect(1900, 100, 830, 480)));
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();

        Assert.Equal((830, 480), (P(doc).Width, P(doc).Height));
        Assert.Equal((1900, 100, DisplayMatcher.TargetOf(a)), (W(doc).X, W(doc).Y, W(doc).Display));
        Assert.Equal([Spot(b, 10, 100)], W(doc).Spots);

        await _platform.ChangeDisplaysAsync(a); // B unplugged: the window stays on A
        Assert.True(window.Visible);
        Assert.Equal(Rect(1900, 100, 830, 480), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task AResizeWhoseDisplayIsUnpluggedBeforeItIsQuiet_IsNoEdit_AndNeverMakesThatDisplayThePlace()
    {
        // Two monitors side by side at 1×: A (the main one) at x 0…1919, B at 1920…3839. The window has no display
        // of its own (the main one, A), at 1800, 100.
        var a = new DisplayInfo("DELL U2720Q", new PointRect(0, 0, 1920, 1080), 1, Id: "DEL40F4");
        var b = new DisplayInfo("LG HDR 4K", new PointRect(1920, 0, 1920, 1080), 1, Id: "GSM5B7F");
        var doc = await StartOn(a, b);
        var window = await RequireWindow(doc.Id);
        await Patch(doc, new JsonObject { ["width"] = 800, ["height"] = 480, ["x"] = 1800, ["y"] = 100 });
        Assert.Equal(Rect(1800, 100, 800, 480), window.Frame);
        var revision = doc.Rev;

        // The left edge dragged to 2000, on B, the button still down; B is unplugged before the size is quiet.
        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.UserResize(Rect(2000, 100, 600, 480));
        });
        await _platform.ChangeDisplaysAsync(a);
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        await _platform.InvokeAsync(window.EndUserMove);
        await _platform.IdleAsync();

        // Nothing is written, B least of all, and the window is shown on A at its place.
        Assert.Equal(revision, doc.Rev);
        Assert.Null(W(doc).Display);
        Assert.Null(W(doc).Spots);
        Assert.True(window.Visible);
        Assert.Equal(Rect(1800, 100, 800, 480), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task AResizeWhoseCommitIsAlreadyQueuedWhenItsDisplayIsUnplugged_IsNoEdit()
    {
        // As above, but the resize's quiet timer fires while the display change waits on the UI thread, so its commit
        // is queued ahead of anything the display change posts.
        var a = new DisplayInfo("DELL U2720Q", new PointRect(0, 0, 1920, 1080), 1, Id: "DEL40F4");
        var b = new DisplayInfo("LG HDR 4K", new PointRect(1920, 0, 1920, 1080), 1, Id: "GSM5B7F");
        var doc = await StartOn(a, b);
        var window = await RequireWindow(doc.Id);
        await Patch(doc, new JsonObject { ["width"] = 800, ["height"] = 480, ["x"] = 1800, ["y"] = 100 });
        Assert.Equal(Rect(1800, 100, 800, 480), window.Frame);
        var revision = doc.Rev;

        await _platform.InvokeAsync(() => window.UserResize(Rect(2000, 100, 600, 480)));
        using var gate = new ManualResetEventSlim();
        _platform.Post(() => gate.Wait(TimeSpan.FromSeconds(10)));
        var change = _platform.ChangeDisplaysAsync(a);
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        gate.Set();
        await change;
        await _platform.IdleAsync();
        await _platform.IdleAsync();

        Assert.Equal(revision, doc.Rev);
        Assert.Null(W(doc).Display);
        Assert.Null(W(doc).Spots);
        Assert.Equal((800, 480), (P(doc).Width, P(doc).Height));
        Assert.True(window.Visible);
        Assert.Equal(Rect(1800, 100, 800, 480), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task AResizeWhoseCommitIsAlreadyQueuedWhenItsDisplayChangesResolution_IsNoEdit()
    {
        var a = new DisplayInfo("DELL U2720Q", new PointRect(0, 0, 1920, 1080), 1, Id: "DEL40F4");
        var b = new DisplayInfo("LG HDR 4K", new PointRect(1920, 0, 1920, 1080), 1, Id: "GSM5B7F");
        var doc = await StartOn(a, b);
        var window = await RequireWindow(doc.Id);
        await Patch(doc, new JsonObject { ["width"] = 800, ["height"] = 480, ["x"] = 1800, ["y"] = 100 });
        var revision = doc.Rev;

        await _platform.InvokeAsync(() => window.UserResize(Rect(2000, 100, 600, 480)));
        using var gate = new ManualResetEventSlim();
        _platform.Post(() => gate.Wait(TimeSpan.FromSeconds(10)));
        var change = _platform.ChangeDisplaysAsync(a, b with { Frame = new PointRect(1920, 0, 2560, 1440) });
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        gate.Set();
        await change;
        await _platform.IdleAsync();
        await _platform.IdleAsync();

        Assert.Equal(revision, doc.Rev);
        Assert.Null(W(doc).Display);
        Assert.Equal((800, 480), (P(doc).Width, P(doc).Height));
        AssertNoErrors();
    }

    /// <summary>
    /// A window as stored before spots and ids (one place, by name, size and top-left; strict not set) loads and is placed
    /// as before while its display is connected, with nothing written; strict's default is now true, so unplugged it hides;
    /// the first drag records the new display's id and starts its spots with the old place.
    /// </summary>
    [Fact]
    public async Task TodaysSinglePlacement_LoadsAndPlacesAsBefore_HidesWithoutItsDisplay_AndADragStartsItsSpots()
    {
        var stored = TestDocs.Doc("Wide bar", 1920, 480, TestDocs.Scene("bar", 10, 10, TestDocs.Rect("#ff0000", 400, 40)));
        stored["id"] = WideBarId;
        stored["profile"]!["window"] = JsonNode.Parse("""
            {"enabled": true, "x": 100, "y": 50, "display": {"name": "DELL U2720Q", "width": 2560, "height": 1440, "x": -408, "y": -1440}}
            """);
        Seed(WideBarId, stored.ToJsonString());
        var doc = await StartOn(Laptop, Work);
        var window = await RequireWindow(doc.Id);
        var old = W(doc).Display!;
        Assert.Null(old.Id);

        Assert.True(window.Visible);
        Assert.Equal(Rect(-308, -1390, 1920, 480), window.Frame);
        Assert.Equal((1L, false), (doc.Rev, doc.Dirty));

        await _platform.ChangeDisplaysAsync(Laptop);
        Assert.False(window.Visible);

        await _platform.ChangeDisplaysAsync(Laptop, Work);
        Assert.Equal(Rect(-308, -1390, 1920, 480), window.Frame);
        await _platform.InvokeAsync(() => window.UserMove(Rect(20, 10, 1920, 480)));
        await _platform.IdleAsync();
        Assert.Equal((40, 20, DisplayMatcher.TargetOf(Laptop)), (W(doc).X, W(doc).Y, W(doc).Display));
        Assert.Equal("APPA050", W(doc).Display!.Id);
        Assert.Equal([new WindowSpot { Display = old, X = 100, Y = 50 }], W(doc).Spots);
        AssertNoErrors();
    }

    [Fact]
    public async Task AtStart_AnActiveProfile_GetsAVisibleWindow_AtItsPixelSize_OnTheFirstDisplay()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        Assert.True(window.Visible);
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame); // 1920×480 pixels at 2×
        Assert.Equal(new DesktopWindowStyle(WindowLevel.Normal, Movable: true, Resizable: true), window.Style);
        Assert.Equal(1, doc.Rev);
        Assert.True(Render.IsRendering(doc.Id));
        AssertNoErrors();
    }

    [Fact]
    public async Task Active_TurnedOffAndOn_ClosesAndReopensTheWindow()
    {
        var doc = await Start();
        var first = await RequireWindow(doc.Id);

        await Patch(doc, new JsonObject { ["enabled"] = false });
        Assert.True(first.Disposed);
        Assert.Null(await WindowOf(doc.Id));
        Assert.False(Render.IsRendering(doc.Id));

        await Patch(doc, new JsonObject { ["enabled"] = true });
        var second = await RequireWindow(doc.Id);
        Assert.NotSame(first, second);
        Assert.True(second.Visible);

        // Undo turns it off again; redo on.
        Undo(doc);
        await _platform.IdleAsync();
        Assert.Null(await WindowOf(doc.Id));
        Redo(doc);
        await _platform.IdleAsync();
        Assert.NotNull(await WindowOf(doc.Id));
        AssertNoErrors();
    }

    [Fact]
    public async Task AProfileCreatedActive_GetsAWindow_AndLosesItWhenDeleted()
    {
        await Start();
        var created = CreateActive("New", 100, 50);
        await _platform.IdleAsync();
        var window = await RequireWindow(created);
        Assert.Equal(Rect(0, 0, 50, 25), window.Frame);

        Workspace.DeleteWithItsTag(created);
        await _platform.IdleAsync();
        Assert.True(window.Disposed);
        Assert.Null(await WindowOf(created));
        AssertNoErrors();
    }

    [Fact]
    public async Task Shutdown_ClosesEveryWindow()
    {
        await Start();
        CreateActive("Second", 100, 50);
        await _platform.IdleAsync();
        var windows = await _platform.InvokeAsync(() => _platform.Created.ToList());
        Assert.Equal(2, windows.Count);

        await _factory.DisposeAsync();
        Assert.All(windows, w => Assert.True(w.Disposed));
        AssertNoErrors();
    }

    [Fact]
    public async Task LevelDragResizeAndSize_AreAppliedLive()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        await Patch(doc, new JsonObject { ["level"] = "top", ["draggable"] = false, ["resizable"] = false });
        Assert.Equal(new DesktopWindowStyle(WindowLevel.Top, Movable: false, Resizable: false), window.Style);

        await Patch(doc, new JsonObject { ["width"] = 1000, ["height"] = 300 });
        Assert.Equal(Rect(0, 0, 500, 150), window.Frame);
        Assert.Same(window, await WindowOf(doc.Id));
        AssertNoErrors();
    }

    public static TheoryData<string, string> LevelChanges => new()
    {
        { "normal", "top" }, { "normal", "desktop" }, { "top", "normal" }, { "top", "desktop" }, { "desktop", "normal" }, { "desktop", "top" },
    };

    /// <summary>Each of the six changes between the three levels reaches the window at once, the same window, its other settings kept.</summary>
    [Theory]
    [MemberData(nameof(LevelChanges))]
    public async Task EachLevelChange_IsAppliedLive_ToTheSameWindow(string from, string to)
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        await Patch(doc, new JsonObject { ["level"] = from, ["resizable"] = false });
        Assert.Equal(new DesktopWindowStyle(LevelOf(from), Movable: true, Resizable: false), window.Style);

        await Patch(doc, new JsonObject { ["level"] = to });

        Assert.Equal(new DesktopWindowStyle(LevelOf(to), Movable: true, Resizable: false), window.Style);
        Assert.Same(window, await WindowOf(doc.Id));
        Assert.True(window.Visible);
        AssertNoErrors();
    }

    private static WindowLevel LevelOf(string level) => level switch
    {
        "top" => WindowLevel.Top,
        "desktop" => WindowLevel.Desktop,
        _ => WindowLevel.Normal,
    };

    [Fact]
    public async Task ALevelChange_IsAppliedLive_ThroughUndoAndRedo()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        await Patch(doc, new JsonObject { ["level"] = "top" });
        await Patch(doc, new JsonObject { ["level"] = "desktop" });
        Assert.Equal(WindowLevel.Desktop, window.Style.Level);

        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal((WindowLevel.Top, "top"), (window.Style.Level, W(doc).Level));
        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal((WindowLevel.Normal, null), (window.Style.Level, W(doc).Level));

        Redo(doc);
        await _platform.IdleAsync();
        Assert.Equal(WindowLevel.Top, window.Style.Level);
        Redo(doc);
        await _platform.IdleAsync();
        Assert.Equal((WindowLevel.Desktop, "desktop"), (window.Style.Level, W(doc).Level));
        Assert.Same(window, await WindowOf(doc.Id));
        AssertNoErrors();
    }

    [Fact]
    public async Task AProfileWithClickThrough_OpensItsWindowWithClicksPassingThrough()
    {
        await Start();
        var created = TestDocs.Doc("Overlay", 100, 50);
        created["profile"]!["window"] = new JsonObject { ["enabled"] = true, ["level"] = "top", ["clickThrough"] = true };
        var id = TestDocs.Create(Workspace, created);
        await _platform.IdleAsync();

        var window = await RequireWindow(id);
        var style = new DesktopWindowStyle(WindowLevel.Top, Movable: true, Resizable: true, ClickThrough: true);
        Assert.Equal((style, style), (window.CreatedStyle, window.Style));
        Assert.Contains(_log.Messages, m => m == $"Showing profile {id} as a desktop window, clicks passing through it");
        AssertNoErrors();
    }

    [Fact]
    public async Task ClickThrough_IsAppliedLive_ToTheSameWindow_ThroughUndoAndRedo_AndLogged()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        Assert.False(window.CreatedStyle.ClickThrough);
        Assert.Contains(_log.Messages, m => m == $"Showing profile {doc.Id} as a desktop window");

        await Patch(doc, new JsonObject { ["clickThrough"] = true, ["resizable"] = false });
        Assert.Equal(new DesktopWindowStyle(WindowLevel.Normal, Movable: true, Resizable: false, ClickThrough: true), window.Style);
        Assert.Single(_log.Messages, m => m == $"The desktop window of profile {doc.Id} lets clicks through");

        await Patch(doc, new JsonObject { ["clickThrough"] = false });
        Assert.Equal(new DesktopWindowStyle(WindowLevel.Normal, Movable: true, Resizable: false), window.Style);
        Assert.Single(_log.Messages, m => m == $"The desktop window of profile {doc.Id} takes clicks again");

        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal((true, true), (window.Style.ClickThrough, W(doc).ClickThrough));
        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal((false, null), (window.Style.ClickThrough, W(doc).ClickThrough));
        Redo(doc);
        await _platform.IdleAsync();
        Assert.True(window.Style.ClickThrough);

        // One window throughout: nothing was recreated.
        Assert.Same(window, await WindowOf(doc.Id));
        Assert.Single(await _platform.InvokeAsync(() => _platform.Created.ToList()));
        Assert.True(window.Visible);
        AssertNoErrors();
    }

    /// <summary>Click-through combines with every level, the level and the other settings kept.</summary>
    [Theory]
    [InlineData("normal")]
    [InlineData("top")]
    [InlineData("desktop")]
    public async Task ClickThrough_CombinesWithEachLevel(string level)
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        await Patch(doc, new JsonObject { ["level"] = level, ["draggable"] = false });

        await Patch(doc, new JsonObject { ["clickThrough"] = true });
        Assert.Equal(new DesktopWindowStyle(LevelOf(level), Movable: false, Resizable: true, ClickThrough: true), window.Style);

        await Patch(doc, new JsonObject { ["clickThrough"] = false });
        Assert.Equal(new DesktopWindowStyle(LevelOf(level), Movable: false, Resizable: true), window.Style);
        Assert.Same(window, await WindowOf(doc.Id));
        AssertNoErrors();
    }

    [Fact]
    public async Task ADrag_WhileAnotherClientTurnsClickThroughOn_KeepsIt()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        Manager.BeforeCommit = _ =>
        {
            Manager.BeforeCommit = null;
            var changed = JsonNode.Parse(JsonSerializer.Serialize(W(doc) with { ClickThrough = true }, SpecJson.Options))!;
            TestDocs.Commit(doc, TestDocs.Profile(new JsonObject { ["window"] = changed }));
        };

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal((3L, 120, 64, true), (doc.Rev, W(doc).X, W(doc).Y, W(doc).ClickThrough));
        Assert.True(window.Style.ClickThrough);
        AssertNoErrors();
    }

    [Fact]
    public async Task WindowXAndY_AndTargetWindow_Reposition_ThroughUndoAndRedo()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        await Patch(doc, new JsonObject { ["x"] = 200, ["y"] = 100 });
        Assert.Equal(Rect(100, 50, 960, 240), window.Frame); // pixels of the 2× display

        PlaceOn(doc, 30, 40, External);
        await _platform.IdleAsync();
        Assert.Equal(Rect(-378, -1400, 1920, 480), window.Frame); // the 1× display: one point per pixel

        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal(Rect(100, 50, 960, 240), window.Frame);
        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame);
        Redo(doc);
        Redo(doc);
        await _platform.IdleAsync();
        Assert.Equal(Rect(-378, -1400, 1920, 480), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task StrictMatching_HidesTheWindowWithoutItsDisplay_AndShowsItWhenItIsBack()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        PlaceOn(doc, 10, 20, External);
        await Patch(doc, new JsonObject { ["strict"] = true });
        Assert.True(window.Visible);
        Assert.Equal(Rect(-398, -1420, 1920, 480), window.Frame);
        var revision = doc.Rev;

        await _platform.ChangeDisplaysAsync(BuiltIn); // unplugged
        Assert.False(window.Visible);
        Assert.Same(window, await WindowOf(doc.Id)); // still open

        await _platform.ChangeDisplaysAsync(BuiltIn, External); // plugged back in
        Assert.True(window.Visible);
        Assert.Equal(Rect(-398, -1420, 1920, 480), window.Frame);

        // Not strict: without its display the window goes to the first one, at the same offset.
        await Patch(doc, new JsonObject { ["strict"] = false });
        await _platform.ChangeDisplaysAsync(BuiltIn);
        Assert.True(window.Visible);
        Assert.Equal(Rect(5, 10, 960, 240), window.Frame);
        Assert.Equal(revision + 1, doc.Rev); // display changes never edit the profile
        AssertNoErrors();
    }

    [Fact]
    public async Task AUserMove_IsOneUndoableEdit_WithTheDisplayUnderTheTopLeft_SavedAtOnceOnACleanProfile()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var changes = window.FrameChanges;

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal(2, doc.Rev);
        // The only change since the profile was saved: saved straight after, as a client's placement change is.
        Assert.False(doc.Dirty);
        Assert.True(doc.CanUndo);
        Assert.Equal((120, 64), (W(doc).X, W(doc).Y));
        var target = W(doc).Display!;
        Assert.Equal((-408, -1440, 2560, 1440, "External"), (target.X, target.Y, target.Width, target.Height, target.Name));

        // On the 1× display the window is resized to keep its pixels (the manager's own change, not an edit).
        Assert.Equal(Rect(-288, -1376, 1920, 480), window.Frame);
        Assert.Equal(2, doc.Rev);
        Assert.True(window.FrameChanges > changes);

        var saved = JsonNode.Parse(File.ReadAllText(DocumentFile(doc)))!["profile"]!["window"]!;
        Assert.Equal((120, 64, "External", 2560), (saved["x"]!.GetValue<int>(), saved["y"]!.GetValue<int>(), saved["display"]!["name"]!.GetValue<string>(), saved["display"]!["width"]!.GetValue<int>()));

        // Undo is kept by the save, and leaves the profile with an unsaved change again.
        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame);
        Assert.True(doc.Dirty);
        AssertNoErrors();
    }

    /// <summary>The profile's saved document.</summary>
    private string DocumentFile(ProfileDocument doc) => Path.Combine(_base.DataDir, "profiles", doc.Id, "document.json");

    /// <summary>The profile as its saved document has it.</summary>
    private DocumentProfile SavedProfile(ProfileDocument doc) =>
        JsonNode.Parse(File.ReadAllText(DocumentFile(doc)))!["profile"].Deserialize<DocumentProfile>(SpecJson.Options)!;

    /// <summary>Counts the document's saves from now on.</summary>
    private static Func<int> CountSaves(ProfileDocument doc)
    {
        var saves = 0;
        doc.Changed += (_, e) =>
        {
            if (e.Change == DocumentChange.Saved)
                Interlocked.Increment(ref saves);
        };
        return () => Volatile.Read(ref saves);
    }

    /// <summary>Stops the engine as a quit does (an orderly stop), then starts it again on the same data folder; returns "Wide bar".</summary>
    private async Task<ProfileDocument> Restart()
    {
        await _factory.DisposeAsync();
        _platform.Dispose();
        await _base.WaitForTheFolderAsync(Ct);
        _platform = new FakeDesktopPlatform(BuiltIn, External);
        _factory = Create(_platform);
        return await Start();
    }

    /// <summary>A resize to <paramref name="frame"/>, committed after its quiet time, or at the release of the button.</summary>
    private async Task Resize(FakeWindow window, PointRect frame, bool atRelease)
    {
        if (atRelease)
        {
            await _platform.InvokeAsync(() =>
            {
                window.BeginUserMove();
                window.UserResize(frame);
                window.EndUserMove();
            });
        }
        else
        {
            await _platform.InvokeAsync(() => window.UserResize(frame));
            _clock.Advance(DisplayWindowManager.ResizeQuiet);
            await _platform.IdleAsync();
        }

        await _platform.IdleAsync();
    }

    /// <summary>
    /// A drag on a profile with no unsaved changes is saved at once: the place is in <c>document.json</c> before anything
    /// else happens, the profile is clean, and a start after a quit (or a crash, which has nothing unsaved to lose) finds
    /// it there, not in a recovery file.
    /// </summary>
    [Fact]
    public async Task ADragOnACleanProfile_IsSavedAtOnce_AndAStartAfterAQuitFindsItThere()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var saves = CountSaves(doc);

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal((2L, false, 1), (doc.Rev, doc.Dirty, saves()));
        Assert.Equal(W(doc), SavedProfile(doc).Window);
        Assert.Equal((120, 64, "External"), (W(doc).X, W(doc).Y, W(doc).Display!.Name));
        Assert.False(File.Exists(Path.Combine(_base.DataDir, "profiles", doc.Id, "recovery.json")));

        var again = await Restart();
        Assert.Equal((1L, false, null), (again.Rev, again.Dirty, again.Status.Recovered));
        Assert.Equal((120, 64, "External"), (W(again).X, W(again).Y, W(again).Display!.Name));
        Assert.Equal(Rect(-288, -1376, 1920, 480), (await RequireWindow(again.Id)).Frame);
        AssertNoErrors();
    }

    /// <summary>A drag on a profile with other unsaved changes saves nothing: they and the drag stay unsaved together, as before.</summary>
    [Fact]
    public async Task ADragWithOtherUnsavedChanges_SavesNothing_AndLeavesThemAllUnsaved()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        await Patch(doc, new JsonObject { ["level"] = "top" });
        var stored = File.ReadAllBytes(DocumentFile(doc));
        var saves = CountSaves(doc);

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal((3L, true, 0), (doc.Rev, doc.Dirty, saves()));
        Assert.Equal((120, 64, "External", "top"), (W(doc).X, W(doc).Y, W(doc).Display!.Name, W(doc).Level));
        Assert.Equal(stored, File.ReadAllBytes(DocumentFile(doc)));

        // Undo takes back the drag alone; the other change stays unsaved.
        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal((null, "top", true), (W(doc).Display, W(doc).Level, doc.Dirty));
        AssertNoErrors();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResizeOnACleanProfile_IsSavedAtOnce_AfterItsQuietOrAtItsRelease(bool atRelease)
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var saves = CountSaves(doc);

        await Resize(window, Rect(0, 0, 900, 240), atRelease);

        Assert.Equal((2L, false, 1), (doc.Rev, doc.Dirty, saves()));
        var saved = SavedProfile(doc);
        Assert.Equal((1800, 480, 0, 0), (saved.Width, saved.Height, saved.Window!.X, saved.Window.Y));
        Assert.Equal(Rect(0, 0, 900, 240), window.Frame);
        AssertNoErrors();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResizeWithOtherUnsavedChanges_SavesNothing_AfterItsQuietOrAtItsRelease(bool atRelease)
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        await Patch(doc, new JsonObject { ["level"] = "top" });
        var stored = File.ReadAllBytes(DocumentFile(doc));
        var saves = CountSaves(doc);

        await Resize(window, Rect(0, 0, 900, 240), atRelease);

        Assert.Equal((3L, true, 0), (doc.Rev, doc.Dirty, saves()));
        Assert.Equal((1800, 480, "top"), (P(doc).Width, P(doc).Height, W(doc).Level));
        Assert.Equal(stored, File.ReadAllBytes(DocumentFile(doc)));
        AssertNoErrors();
    }

    /// <summary>A drag that leaves the window's place as it is commits nothing, and so saves nothing.</summary>
    [Fact]
    public async Task ADragThatChangesNothing_OnACleanProfile_SavesNothing()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        PlaceOn(doc, 0, 0, BuiltIn);
        doc.Save();
        await _platform.IdleAsync();
        var revision = doc.Rev;
        var saves = CountSaves(doc);

        await _platform.InvokeAsync(() => window.UserMove(Rect(0.2, 0.2, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal((revision, false, 0), (doc.Rev, doc.Dirty, saves()));
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame);
        AssertNoErrors();
    }

    /// <summary>
    /// Another client's change between the drag's read and its commit: the commit is read again and retried, and the
    /// profile then holds that client's unsaved change, so nothing is saved: a drag never writes another client's change.
    /// </summary>
    [Fact]
    public async Task ADragRetriedAfterAnotherClientsChange_SavesNothing()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var stored = File.ReadAllBytes(DocumentFile(doc));
        var saves = CountSaves(doc);
        Manager.BeforeCommit = _ =>
        {
            Manager.BeforeCommit = null;
            var changed = JsonNode.Parse(JsonSerializer.Serialize(W(doc) with { Level = "desktop" }, SpecJson.Options))!;
            TestDocs.Commit(doc, TestDocs.Profile(new JsonObject { ["window"] = changed }));
        };

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal((3L, true, 0), (doc.Rev, doc.Dirty, saves()));
        Assert.Equal((120, 64, "External", "desktop"), (W(doc).X, W(doc).Y, W(doc).Display!.Name, W(doc).Level));
        Assert.Equal(stored, File.ReadAllBytes(DocumentFile(doc)));
        AssertNoErrors();
    }

    /// <summary>
    /// Another client's change between the drag's commit and its save: the save names the drag's own revision, so it
    /// is refused and nothing is written; both changes stay unsaved.
    /// </summary>
    [Fact]
    public async Task AnotherClientsChange_BetweenTheDragsCommitAndItsSave_IsNeverSaved()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var stored = File.ReadAllBytes(DocumentFile(doc));
        var saves = CountSaves(doc);
        var first = true;
        doc.Changed += (_, e) =>
        {
            if (e.Change != DocumentChange.Commit || !first)
                return;
            first = false;
            var changed = JsonNode.Parse(JsonSerializer.Serialize(W(doc) with { Level = "desktop" }, SpecJson.Options))!;
            TestDocs.Commit(doc, TestDocs.Profile(new JsonObject { ["window"] = changed }));
        };

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal((3L, true, 0), (doc.Rev, doc.Dirty, saves()));
        Assert.Equal((120, 64, "External", "desktop"), (W(doc).X, W(doc).Y, W(doc).Display!.Name, W(doc).Level));
        Assert.Equal(stored, File.ReadAllBytes(DocumentFile(doc)));
        AssertNoErrors();
    }

    /// <summary>
    /// A save the folder refuses leaves the drag committed and unsaved, the window where the user put it, a warning in
    /// the log, and the change kept as any unsaved change is: an orderly stop keeps it, and the next start restores it.
    /// </summary>
    [Fact]
    public async Task ADragWhoseSaveFails_StaysCommittedAndUnsaved_WhereTheUserPutIt_AndIsKeptAsAnyUnsavedChange()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Refuses the save through Unix file modes.");
        if (OperatingSystem.IsWindows())
            return; // for the platform analyzer: SkipWhen has already ended the test
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var stored = File.ReadAllBytes(DocumentFile(doc));
        var folder = Path.GetDirectoryName(DocumentFile(doc))!;
        var mode = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
            await _platform.IdleAsync();

            Assert.Equal((2L, true), (doc.Rev, doc.Dirty));
            Assert.Equal((120, 64, "External"), (W(doc).X, W(doc).Y, W(doc).Display!.Name));
            Assert.Equal(Rect(-288, -1376, 1920, 480), window.Frame);
            Assert.Equal(stored, File.ReadAllBytes(DocumentFile(doc)));
            Assert.Single(_log.Messages, m => m.Contains("couldn't be saved"));
        }
        finally
        {
            File.SetUnixFileMode(folder, mode);
        }

        var again = await Restart();
        Assert.Equal((2L, true), (again.Rev, again.Dirty));
        Assert.NotNull(again.Status.Recovered);
        Assert.Equal((120, 64, "External"), (W(again).X, W(again).Y, W(again).Display!.Name));
        AssertNoErrors();
    }

    /// <summary>
    /// A save that replaced <c>document.json</c> but couldn't flush the profile's folder: the drag is saved (the file holds
    /// it, the profile is clean), and the warning says so and that it couldn't be confirmed on disk, never that it stays
    /// unsaved; nothing reaches the platform. The folder is made unreadable (write and search kept), so the new document
    /// goes in and the folder can't be opened to flush it.
    /// </summary>
    [Fact]
    public async Task ADragWhoseSaveReplacesTheDocumentButCantFlushItsFolder_IsSaved_AndLoggedAsNotConfirmedOnDisk()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Fails the folder's flush through Unix file modes.");
        if (OperatingSystem.IsWindows())
            return; // for the platform analyzer: SkipWhen has already ended the test
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var saves = CountSaves(doc);
        var folder = Path.GetDirectoryName(DocumentFile(doc))!;
        var mode = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
            await _platform.IdleAsync();
        }
        finally
        {
            File.SetUnixFileMode(folder, mode);
        }

        Assert.Equal((2L, false, 1), (doc.Rev, doc.Dirty, saves()));
        Assert.Equal(W(doc), SavedProfile(doc).Window);
        Assert.Equal((120, 64, "External"), (W(doc).X, W(doc).Y, W(doc).Display!.Name));
        Assert.Equal(Rect(-288, -1376, 1920, 480), window.Frame);
        Assert.Single(_log.Messages, m => m.Contains("is saved, but") && m.Contains("on disk"));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("stays unsaved"));
        AssertNoErrors();
    }

    /// <summary>
    /// Sets the workspace's test hook that runs as a save is about to write a document, under the document's save lock
    /// (<c>Workspace.Saving</c>, internal to the core library, so set by reflection).
    /// </summary>
    private void OnSaving(Action<string>? saving) =>
        (typeof(Workspace).GetField("Saving", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(Workspace), "Saving")).SetValue(Workspace, saving);

    /// <summary>
    /// The profile deleted after the drag's save has begun, before it writes: the store no longer lists it, so the
    /// save meets a profile that is gone (<see cref="KeyNotFoundException"/>, not a disposed document). That is the
    /// deletion it is: nothing saved, no failure logged, and the release's handling goes on to its end (the window put
    /// at the profile's place); then the deletion closes the window.
    /// </summary>
    [Fact]
    public async Task ADragWhoseProfileIsDeletedAsItsSaveBegins_SavesNothing_LogsNoFailure_AndItsReleaseFinishes()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var instance = doc.Instance;
        DeleteOutcome? deleted = null;
        OnSaving(id =>
        {
            OnSaving(null);
            deleted = Workspace.Delete(id, instance);
        });

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal(new DeleteOutcome(true, null), deleted);
        Assert.Equal(Rect(-288, -1376, 1920, 480), window.Frame);
        Assert.True(window.Disposed);
        Assert.Null(await WindowOf(WideBarId));
        Assert.Null(Workspace.Open(WideBarId));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("saved"));
        AssertNoErrors();
    }

    /// <summary>
    /// A resize held past its quiet time, then gone on with, then released: the quiet commit waits for the release
    /// (nothing saved while the button is down, the profile unsaved meanwhile), and the release saves once, the final
    /// size: one save, one kept version, for one gesture.
    /// </summary>
    [Fact]
    public async Task AResizeHeldPastItsQuiet_ThenGoneOn_IsSavedOnce_AtItsRelease_WithItsFinalSize()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var saves = CountSaves(doc);

        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.UserResize(Rect(0, 0, 900, 240));
        });
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal((2L, true, 0), (doc.Rev, doc.Dirty, saves()));

        await _platform.InvokeAsync(() =>
        {
            window.UserResize(Rect(0, 0, 800, 240));
            window.EndUserMove();
        });
        await _platform.IdleAsync();

        Assert.Equal((3L, false, 1), (doc.Rev, doc.Dirty, saves()));
        var saved = SavedProfile(doc);
        Assert.Equal((1600, 480, 0, 0), (saved.Width, saved.Height, saved.Window!.X, saved.Window.Y));
        Assert.Equal(Rect(0, 0, 800, 240), window.Frame);
        AssertNoErrors();
    }

    /// <summary>
    /// A held resize on a clean profile with another client's change between its quiet commit and its release's: the
    /// gesture's commits are no longer the profile's only changes, so nothing is saved, that client's change included.
    /// </summary>
    [Fact]
    public async Task AHeldResize_WithAnotherClientsChangeBetweenItsCommits_SavesNothing()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var stored = File.ReadAllBytes(DocumentFile(doc));
        var saves = CountSaves(doc);

        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.UserResize(Rect(0, 0, 900, 240));
        });
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        await Patch(doc, new JsonObject { ["level"] = "top" });

        await _platform.InvokeAsync(() =>
        {
            window.UserResize(Rect(0, 0, 800, 240));
            window.EndUserMove();
        });
        await _platform.IdleAsync();

        Assert.Equal((4L, true, 0), (doc.Rev, doc.Dirty, saves()));
        Assert.Equal((1600, 480, "top"), (P(doc).Width, P(doc).Height, W(doc).Level));
        Assert.Equal(stored, File.ReadAllBytes(DocumentFile(doc)));
        AssertNoErrors();
    }

    /// <summary>
    /// A held resize on a clean profile whose quiet commit is waiting for the release, and another client's change the
    /// release's commit doesn't touch (the profile's name) committed after that commit read the profile and before it
    /// applied: the release's commit applies on top of it, so the press's commits are no longer the profile's only
    /// changes, and nothing is saved; both stay unsaved.
    /// </summary>
    [Fact]
    public async Task AHeldResize_WithAnotherClientsChangeBetweenItsReleasesReadAndItsCommit_SavesNothing()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var stored = File.ReadAllBytes(DocumentFile(doc));
        var saves = CountSaves(doc);

        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.UserResize(Rect(0, 0, 900, 240));
        });
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal((2L, true, 0), (doc.Rev, doc.Dirty, saves()));
        RenameBeforeTheNextCommit(doc);

        await _platform.InvokeAsync(() =>
        {
            window.UserResize(Rect(0, 0, 800, 240));
            window.EndUserMove();
        });
        await _platform.IdleAsync();

        Assert.Equal((4L, true, 0), (doc.Rev, doc.Dirty, saves()));
        Assert.Equal((1600, 480, "Renamed by a client"), (P(doc).Width, P(doc).Height, P(doc).Name));
        Assert.Equal(stored, File.ReadAllBytes(DocumentFile(doc)));
        AssertNoErrors();
    }

    /// <summary>
    /// The same for a drag after a resize's quiet commit in the same press: another client's rename between the drag's
    /// read and its commit, at the release, means the press saves nothing; both stay unsaved.
    /// </summary>
    [Fact]
    public async Task ADragAfterAHeldResizesQuietCommit_WithAnotherClientsChangeBetweenItsReadAndItsCommit_SavesNothing()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var stored = File.ReadAllBytes(DocumentFile(doc));
        var saves = CountSaves(doc);

        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.UserResize(Rect(0, 0, 900, 240));
        });
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal((2L, true, 0), (doc.Rev, doc.Dirty, saves()));
        RenameBeforeTheNextCommit(doc);

        await _platform.InvokeAsync(() =>
        {
            window.DragTo(Rect(100, 50, 900, 240));
            window.EndUserMove();
        });
        await _platform.IdleAsync();

        Assert.Equal((4L, true, 0), (doc.Rev, doc.Dirty, saves()));
        Assert.Equal((1800, 480, 200, 100, "Renamed by a client"), (P(doc).Width, P(doc).Height, W(doc).X, W(doc).Y, P(doc).Name));
        Assert.Equal(stored, File.ReadAllBytes(DocumentFile(doc)));
        AssertNoErrors();
    }

    /// <summary>
    /// Another client's rename between a held resize's first read and its quiet commit: the profile isn't clean when the
    /// press's first commit applies, so the press saves nothing, at its quiet or its release; both stay unsaved.
    /// </summary>
    [Fact]
    public async Task AHeldResize_WithAnotherClientsChangeBeforeItsFirstCommit_SavesNothing()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var stored = File.ReadAllBytes(DocumentFile(doc));
        var saves = CountSaves(doc);
        RenameBeforeTheNextCommit(doc);

        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.UserResize(Rect(0, 0, 900, 240));
        });
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal((3L, true, 0), (doc.Rev, doc.Dirty, saves()));

        await _platform.InvokeAsync(() =>
        {
            window.UserResize(Rect(0, 0, 800, 240));
            window.EndUserMove();
        });
        await _platform.IdleAsync();

        Assert.Equal((4L, true, 0), (doc.Rev, doc.Dirty, saves()));
        Assert.Equal((1600, 480, "Renamed by a client"), (P(doc).Width, P(doc).Height, P(doc).Name));
        Assert.Equal(stored, File.ReadAllBytes(DocumentFile(doc)));
        AssertNoErrors();
    }

    /// <summary>
    /// Another client's rename between a drag's read and its commit, which it doesn't conflict with: the profile isn't
    /// clean when the drag's commit applies, so nothing is saved; both stay unsaved.
    /// </summary>
    [Fact]
    public async Task ADrag_WithAnotherClientsChangeBetweenItsReadAndItsCommit_SavesNothing()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var stored = File.ReadAllBytes(DocumentFile(doc));
        var saves = CountSaves(doc);
        RenameBeforeTheNextCommit(doc);

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal((3L, true, 0), (doc.Rev, doc.Dirty, saves()));
        Assert.Equal((120, 64, "External", "Renamed by a client"), (W(doc).X, W(doc).Y, W(doc).Display!.Name, P(doc).Name));
        Assert.Equal(stored, File.ReadAllBytes(DocumentFile(doc)));
        AssertNoErrors();
    }

    /// <summary>Another client renames the profile once, between the window's next commit's read of the profile and that commit.</summary>
    private void RenameBeforeTheNextCommit(ProfileDocument doc) =>
        Manager.BeforeCommit = _ =>
        {
            Manager.BeforeCommit = null;
            TestDocs.Commit(doc, TestDocs.Profile(new JsonObject { ["name"] = "Renamed by a client" }));
        };

    /// <summary>A resize held past its quiet time and released where it is: the release commits nothing more and saves the quiet commit, once.</summary>
    [Fact]
    public async Task AHeldResize_WhoseReleaseChangesNothingMore_SavesItsQuietCommitOnce_AtTheRelease()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var saves = CountSaves(doc);

        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.UserResize(Rect(0, 0, 900, 240));
        });
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal((2L, true, 0), (doc.Rev, doc.Dirty, saves()));

        await _platform.InvokeAsync(window.EndUserMove);
        await _platform.IdleAsync();

        Assert.Equal((2L, false, 1), (doc.Rev, doc.Dirty, saves()));
        var saved = SavedProfile(doc);
        Assert.Equal((1800, 480, 0, 0), (saved.Width, saved.Height, saved.Window!.X, saved.Window.Y));
        Assert.Equal(Rect(0, 0, 900, 240), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task ADrag_WhileAnotherClientChangesTheWindow_KeepsThatChange_AndIsOneUndoableStep()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        // After the drop read the profile, before its commit: another client puts the window on the desktop, fixed-size.
        Manager.BeforeCommit = _ =>
        {
            Manager.BeforeCommit = null;
            var changed = JsonNode.Parse(JsonSerializer.Serialize(W(doc) with { Level = "desktop", Resizable = false }, SpecJson.Options))!;
            TestDocs.Commit(doc, TestDocs.Profile(new JsonObject { ["window"] = changed }));
        };

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal((3L, 120, 64, "External", "desktop", false), (doc.Rev, W(doc).X, W(doc).Y, W(doc).Display!.Name, W(doc).Level, W(doc).Resizable));
        Assert.Equal(new DesktopWindowStyle(WindowLevel.Desktop, Movable: true, Resizable: false), window.Style);

        // Undoing the drag leaves the other client's change.
        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal((null, "desktop", false), (W(doc).Display, W(doc).Level, W(doc).Resizable));
        Assert.Equal(WindowLevel.Desktop, window.Style.Level);
        AssertNoErrors();
    }

    [Fact]
    public async Task AUserMove_OntoTheSameSpot_IsNoEdit_AndTheWindowGoesBackToItsPlace()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        PlaceOn(doc, 0, 0, BuiltIn);
        await _platform.IdleAsync();
        var revision = doc.Rev;

        // Less than half a pixel away: the same WindowX and WindowY.
        await _platform.InvokeAsync(() => window.UserMove(Rect(0.2, 0.2, 960, 240)));
        await _platform.IdleAsync();
        Assert.Equal(revision, doc.Rev);
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task AUserMove_RaisesTheWorkspacesEntriesChangedOnceForItsCommitAndOnceForItsSave_AndAPlainClick_None()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var entries = new ConcurrentQueue<long>();
        Workspace.EntriesChanged += (_, e) => entries.Enqueue(e.Version);
        var before = Workspace.Version;

        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.EndUserMove();
        });
        await _platform.IdleAsync();
        Assert.Equal(1, doc.Rev);
        Assert.Empty(entries);

        await _platform.InvokeAsync(() => window.UserMove(Rect(120, 64, 960, 240)));
        await _platform.IdleAsync();
        Assert.Equal(2, doc.Rev);
        // The drag's commit, then its save (the profile had no unsaved changes): one each.
        Assert.Equal([before + 1, before + 2], entries);
        Assert.Equal(before + 2, Workspace.Version);
        AssertNoErrors();
    }

    [Fact]
    public async Task ADragOntoADisplayOfAnotherScale_StaysWhereTheUserHasIt_UntilTheDrop()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        // The top-left crosses onto the 1× display with the button still down: the window's scale changes mid-drag.
        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.DragTo(Rect(-408 + 120, -1440 + 64, 960, 240));
        });
        await _platform.IdleAsync();
        Assert.Equal(Rect(-288, -1376, 960, 240), window.Frame);
        Assert.Equal(1, doc.Rev);

        // Still mid-drag, another profile is created and a display is plugged in: both reconcile every window.
        CreateActive("Second", 100, 50);
        await _platform.IdleAsync();
        await _platform.ChangeDisplaysAsync(BuiltIn, External, new DisplayInfo("Projector", Rect(1512, 0, 1920, 1080), 1));
        Assert.Equal(Rect(-288, -1376, 960, 240), window.Frame);
        Assert.Equal(1, doc.Rev);

        await _platform.InvokeAsync(window.EndUserMove);
        await _platform.IdleAsync();
        Assert.Equal(2, doc.Rev);
                Assert.Equal((120, 64, "External"), (W(doc).X, W(doc).Y, W(doc).Display?.Name));
        Assert.Equal(Rect(-288, -1376, 1920, 480), window.Frame);
        Assert.Single(_log.Messages, m => m.Contains(" moved "));
        AssertNoErrors();
    }

    [Fact]
    public async Task APressWithoutAMove_DuringWhichTheScaleChanged_IsReconciledAtTheRelease_WithoutAnEdit()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var changes = await _platform.InvokeAsync(() => window.FrameChanges);

        // The button goes down on the window and the built-in display is switched to 1× (same pixels) before it is up.
        await _platform.InvokeAsync(window.BeginUserMove);
        await _platform.ChangeDisplaysAsync(BuiltIn with { Frame = Rect(0, 0, 3024, 1964), Scale = 1 }, External);
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame);
        Assert.Equal(changes, await _platform.InvokeAsync(() => window.FrameChanges));

        await _platform.InvokeAsync(window.EndUserMove);
        await _platform.IdleAsync();
        Assert.Equal(Rect(0, 0, 1920, 480), window.Frame); // 1920×480 pixels at 1×
        Assert.Equal(1, doc.Rev);
        Assert.False(doc.CanUndo);
        Assert.DoesNotContain(_log.Messages, m => m.Contains(" moved "));

        // A plain click, nothing changed meanwhile: nothing at all.
        changes = await _platform.InvokeAsync(() => window.FrameChanges);
        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.EndUserMove();
        });
        await _platform.IdleAsync();
        Assert.Equal(changes, await _platform.InvokeAsync(() => window.FrameChanges));
        Assert.Equal(1, doc.Rev);
        Assert.DoesNotContain(_log.Messages, m => m.Contains(" moved "));
        AssertNoErrors();
    }

    [Fact]
    public async Task AUserResize_IsOneEdit_AfterItsSizeStaysQuiet()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        await _platform.InvokeAsync(() => window.UserResize(Rect(0, 0, 900, 240)));
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        await _platform.InvokeAsync(() => window.UserResize(Rect(0, 0, 800, 250)));
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        await _platform.InvokeAsync(() => window.UserResize(Rect(0, 0, 700.5, 260)));
        await _platform.IdleAsync();
        Assert.Equal(1, doc.Rev);

        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal(2, doc.Rev);
        Assert.False(doc.Dirty); // the only change since the profile was saved: saved straight after
                Assert.Equal((1401, 520, 0, 0), (P(doc).Width, P(doc).Height, W(doc).X, W(doc).Y));
        Assert.Equal(Rect(0, 0, 700.5, 260), window.Frame);

        // A resize from the top-left edge also moves the position.
        await _platform.InvokeAsync(() => window.UserResize(Rect(10, 5, 690.5, 255)));
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal(3, doc.Rev);
                Assert.Equal((1381, 510, 20, 10), (P(doc).Width, P(doc).Height, W(doc).X, W(doc).Y));

        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal(Rect(0, 0, 700.5, 260), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task AResizeFromAnEdge_CommitsAtTheRelease_AsOneEdit()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        // The button goes down on the window's left edge, the resize moves its top-left, and the button is up again
        // before the size is quiet: the release commits the resize at once, and is no move of its own.
        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.UserResize(Rect(10, 0, 950, 240));
            window.EndUserMove();
        });
        await _platform.IdleAsync();
        Assert.Equal(2, doc.Rev);
                Assert.Equal((1900, 480, 20, 0), (P(doc).Width, P(doc).Height, W(doc).X, W(doc).Y));

        // The resize's timer has nothing left to commit.
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal(2, doc.Rev);
                Assert.Equal((1900, 480, 20, 0), (P(doc).Width, P(doc).Height, W(doc).X, W(doc).Y));
        Assert.DoesNotContain(_log.Messages, m => m.Contains(" moved "));
        AssertNoErrors();
    }

    [Fact]
    public async Task AResizeTimerThatFiredBeforeTheNextChange_DoesNotSplitTheResize()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        await _platform.InvokeAsync(() => window.UserResize(Rect(0, 0, 900, 240)));

        // The quiet time runs out while the UI thread is busy and the resize's next change is already queued: the
        // timer's commit is posted behind that change.
        using var gate = new ManualResetEventSlim();
        _platform.Post(() => gate.Wait(TimeSpan.FromSeconds(10)));
        var next = _platform.InvokeAsync(() => window.UserResize(Rect(0, 0, 800, 250)));
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        gate.Set();
        await next;
        await _platform.IdleAsync();
        Assert.Equal(1, doc.Rev);

        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal(2, doc.Rev);
                Assert.Equal((1600, 500), (P(doc).Width, P(doc).Height));
        AssertNoErrors();
    }

    [Fact]
    public async Task AResizeHeldPastItsCommit_ThenReleased_IsStillOneEdit()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        // The size is quiet with the button still down on the window's edge: the resize commits before the release.
        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.UserResize(Rect(0, 0, 900, 240));
        });
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal(2, doc.Rev);

        await _platform.InvokeAsync(window.EndUserMove);
        await _platform.IdleAsync();
        Assert.Equal(2, doc.Rev);
        Assert.Equal(Rect(0, 0, 900, 240), window.Frame);
        Assert.DoesNotContain(_log.Messages, m => m.Contains(" moved "));

        Undo(doc);
        await _platform.IdleAsync();
        Assert.False(doc.CanUndo);
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task ADragReleasedBeforeTheResizeAheadOfItIsQuiet_IsAnEditOfItsOwn()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        // A resize from the right edge is released, then the window is dragged and released before the size is quiet.
        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.UserResize(Rect(0, 0, 900, 240));
            window.EndUserMove();
        });
        await _platform.InvokeAsync(() => window.UserMove(Rect(100, 50, 900, 240)));
        await _platform.IdleAsync();
        Assert.Equal(3, doc.Rev);
                Assert.Equal((1800, 480, 200, 100), (P(doc).Width, P(doc).Height, W(doc).X, W(doc).Y));
        Assert.Equal(Rect(100, 50, 900, 240), window.Frame);

        // The resize's timer has nothing left to commit.
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal(3, doc.Rev);

        // Undo takes back the drag alone, then the resize.
        Undo(doc);
        await _platform.IdleAsync();
                Assert.Equal((1800, 480, 0, 0), (P(doc).Width, P(doc).Height, W(doc).X, W(doc).Y));
        Assert.Equal(Rect(0, 0, 900, 240), window.Frame);
        Assert.True(doc.CanUndo);

        Undo(doc);
        await _platform.IdleAsync();
        Assert.False(doc.CanUndo);
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task APressDuringWhichItsDisplayWentAway_IsNoMove_AndTheWindowFallsBackToTheFirstDisplay()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        PlaceOn(doc, 10, 20, External);
        await Patch(doc, new JsonObject { ["strict"] = false }); // strict is true by default
        Assert.Equal(Rect(-398, -1420, 1920, 480), window.Frame);
        var revision = doc.Rev;

        // The button goes down on the window and its display is unplugged before it is up: the system moves the window
        // onto the built-in display, under the still pressed button.
        await _platform.InvokeAsync(window.BeginUserMove);
        await _platform.ChangeDisplaysAsync(BuiltIn);
        Assert.Equal(Rect(0, 0, 1920, 480), window.Frame);

        await _platform.InvokeAsync(window.EndUserMove);
        await _platform.IdleAsync();
        Assert.Equal(revision, doc.Rev);
                Assert.Equal((10, 20, "External"), (W(doc).X, W(doc).Y, W(doc).Display?.Name));

        // Not strict: without its display the window goes to the first one, at the same offset.
        Assert.True(window.Visible);
        Assert.Equal(Rect(5, 10, 960, 240), window.Frame);
        Assert.DoesNotContain(_log.Messages, m => m.Contains(" moved "));
        AssertNoErrors();
    }

    [Fact]
    public async Task TheEditLogLines_NameThePlacementAndTheDisplay()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();
        var moved = Assert.Single(_log.Messages, m => m.Contains(" moved "));
        Assert.Contains("(120, 64) on External", moved);
        Assert.DoesNotContain("InfoPanel.Models", moved);

        await _platform.InvokeAsync(() => window.UserResize(Rect(-288, -1376, 1000, 300)));
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        var resized = Assert.Single(_log.Messages, m => m.Contains(" resized "));
        Assert.Contains("1000×300 at (120, 64) on External", resized);
        Assert.DoesNotContain("InfoPanel.Models", resized);
        AssertNoErrors();
    }

    [Fact]
    public async Task AnUnreadableProfile_DoesNotKeepTheOthersWindowsFromOpening()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Makes a profile unreadable through Unix file modes.");

        var unreadable = TestDocs.NewId();
        var readable = TestDocs.Doc("Readable", 200, 100);
        readable["profile"]!["window"] = new JsonObject { ["enabled"] = true };
        Seed(unreadable, TestDocs.Doc("Unreadable", 100, 50).ToJsonString());
        Seed(readable["id"]!.GetValue<string>(), readable.ToJsonString());
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(_base.DataDir, "profiles", unreadable, "document.json"), UnixFileMode.None);

        _ = _factory.Services;
        await _platform.IdleAsync();

        var window = await RequireWindow(readable["id"]!.GetValue<string>());
        Assert.True(window.Visible);
        Assert.Null(await WindowOf(unreadable));
        Assert.Equal(("unreadable", "io"), (Workspace.Entry(unreadable)!.Status, Workspace.Entry(unreadable)!.Problem!.Code));
        AssertNoErrors();
    }

    [Fact]
    public async Task TheManagersOwnMoves_NeverEditTheProfile()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        await Patch(doc, new JsonObject { ["x"] = 50 });
        await _platform.ChangeDisplaysAsync(External, BuiltIn);
        await _platform.ChangeDisplaysAsync(BuiltIn, External);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _platform.IdleAsync();

        Assert.Equal(2, doc.Rev);
        Assert.True(window.FrameChanges >= 2);
        AssertNoErrors();
    }

    [Fact]
    public async Task NoWindows_ShowsNoWindows()
    {
        await _factory.DisposeAsync();
        _platform.Dispose();
        _platform = new FakeDesktopPlatform(BuiltIn, External);
        _factory = Create(_platform, noWindows: true);

        var doc = await Start();
        Assert.Empty(await _platform.InvokeAsync(() => _platform.Created.ToList()));
        Assert.False(Render.IsRendering(doc.Id));
        Assert.False(Manager.Enabled);
    }

    [Fact]
    public async Task TheWindowsFramePixels_EqualTheScale1PreviewFrame_AtTheSameTick()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        await using var client = await PreviewClient.ConnectAsync(_factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = doc.Id, scale = 1, maxFps = 30 }, Ct);
        await client.ReceiveTextAsync("doc", Ct);

        // Both sinks are new, so one tick hands both the same frame.
        Assert.Equal(2, Render.Tick(doc.Id));
        var (header, png) = await client.ReceiveFrameAsync(Ct);
        await _platform.IdleAsync();

        var shown = await _platform.InvokeAsync(() => window.LastFrame);
        Assert.NotNull(shown);
        Assert.Equal((1.0, 1920, 480), (header.Scale, header.PixelWidth, header.PixelHeight));
        Assert.Equal((1920, 480), (shown.Width, shown.Height));

        using var decoded = SKBitmap.Decode(png.ToArray(), new SKImageInfo(1920, 480, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var expected = decoded.GetPixelSpan().ToArray();
        var actual = Unpremultiplied(shown);
        var firstDifference = expected.AsSpan().CommonPrefixLength(actual);
        Assert.True(firstDifference == expected.Length && expected.Length == actual.Length,
            $"pixels differ from byte {firstDifference} (pixel {firstDifference / 4 % 1920}, {firstDifference / 4 / 1920})");
        AssertNoErrors();
    }

    [Fact]
    public async Task AWindowShowingItsEdgeHandle_GetsThePreviewsFramePixels_TheHandleInNoFrame()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var (edge, opacity) = await _platform.InvokeAsync(() =>
        {
            window.HoverAt(window.Frame.Width - 1, window.Frame.Height / 2, TimeSpan.Zero);
            return (window.Handles.Edge, window.Handles.Opacity(EdgeHandles.FadeIn));
        });
        Assert.Equal((ResizeEdge.Right, 1.0), (edge, opacity));

        await using var client = await PreviewClient.ConnectAsync(_factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = doc.Id, scale = 1, maxFps = 30 }, Ct);
        await client.ReceiveTextAsync("doc", Ct);

        // The window's frame and the preview's, from one tick, while the handle shows: the same pixels.
        Assert.Equal(2, Render.Tick(doc.Id));
        var (_, png) = await client.ReceiveFrameAsync(Ct);
        await _platform.IdleAsync();
        var shown = await _platform.InvokeAsync(() => window.LastFrame);
        Assert.NotNull(shown);

        using var decoded = SKBitmap.Decode(png.ToArray(), new SKImageInfo(shown.Width, shown.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var expected = decoded.GetPixelSpan().ToArray();
        var actual = Unpremultiplied(shown);
        Assert.True(expected.AsSpan().SequenceEqual(actual), "the window's frame differs from the preview's");
        AssertNoErrors();
    }

    [Fact]
    public async Task AFrameWithUnchangedPixels_IsNotCopiedAgain()
    {
        await Start();
        var created = CreateActive("Plain", 64, 32); // nothing on it moves
        await _platform.IdleAsync();
        var doc = Workspace.Open(created)!;
        var window = await RequireWindow(doc.Id);
        var sink = await _platform.InvokeAsync(() => Manager.SinkOf(doc.Id)!);
        Assert.Equal(1, Render.Tick(doc.Id));
        await _platform.IdleAsync();
        Assert.Equal(1, await _platform.InvokeAsync(() => window.FramesShown));
        Assert.Equal(1, Interlocked.Read(ref sink.FramesReceived));

        // An edit that leaves the pixels alone: the new revision reaches the sink, which skips the copy.
        await Patch(doc, new JsonObject { ["level"] = "top" });
        _clock.Advance(TimeSpan.FromMilliseconds(40));
        Assert.Equal(1, Render.Tick(doc.Id));
        await _platform.IdleAsync();
        Assert.Equal(1, Interlocked.Read(ref sink.FramesReceived));
        Assert.Equal(1, await _platform.InvokeAsync(() => window.FramesShown));
    }

    [Fact]
    public async Task AProfileOverTheFramePixelCap_IsRenderedSmaller_AndStretchedToFillItsWindow()
    {
        await Start();
        var created = CreateActive("Huge", 4097, 4096); // 16 781 312 pixels: over FrameScale.MaxPixels at scale 1
        await _platform.IdleAsync();
        var window = await RequireWindow(created);
        Assert.Equal(1, Render.Tick(created));
        await _platform.IdleAsync();

        var (shown, backing, frame) = await _platform.InvokeAsync(() => (window.LastFrame, window.BackingScale, window.Frame));
        Assert.NotNull(shown);
        Assert.Equal((3073, 3072, 0.75f), (shown.Width, shown.Height, shown.Scale));

        // Shown at Scale × BackingScale frame pixels per point, the frame covers the window to within one frame pixel.
        var point = 1 / (shown.Scale * backing);
        Assert.InRange(shown.Width * point - frame.Width, -point, point);
        Assert.InRange(shown.Height * point - frame.Height, -point, point);
        Assert.Single(_log.Messages, m => m.Contains("reduced resolution"));
        AssertNoErrors();
    }

    [Fact]
    public async Task AResizeAcrossTheFramePixelCap_WithUnchangedPixels_ReachesTheWindowAtItsNewScale()
    {
        await Start();
        var created = CreateActive("Square", 4096, 4096); // exactly FrameScale.MaxPixels: scale 1
        await _platform.IdleAsync();
        var doc = Workspace.Open(created)!;
        var window = await RequireWindow(doc.Id);

        async Task<float> ShownScale()
        {
            _clock.Advance(TimeSpan.FromMilliseconds(40));
            Render.Tick(doc.Id);
            await _platform.IdleAsync();
            var shown = await _platform.InvokeAsync(() => window.LastFrame);
            Assert.NotNull(shown);
            Assert.Equal((4096, 4096), (shown.Width, shown.Height)); // the same pixels every time
            return shown.Scale;
        }

        Assert.Equal(1f, await ShownScale());

        // Twice the size is drawn at half the scale: the same 4096×4096 pixels, stretched by the window.
        await Patch(doc, new JsonObject { ["width"] = 8192, ["height"] = 8192 });
        Assert.Equal(0.5f, await ShownScale());
        await Patch(doc, new JsonObject { ["width"] = 4096, ["height"] = 4096 });
        Assert.Equal(1f, await ShownScale());

        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal(0.5f, await ShownScale());
        Redo(doc);
        await _platform.IdleAsync();
        Assert.Equal(1f, await ShownScale());
        AssertNoErrors();
    }

    [Fact]
    public async Task AWindowHiddenWithoutItsDisplay_GetsNoFrames_AndGetsOneAtOnceWhenItShowsAgain()
    {
        await Start();
        var growing = JsonNode.Parse("""
            {"format": "infopanel.scene/1",
             "state": {"t": {"init": 0, "next": {"op": "add", "args": [{"op": "state", "name": "t"}, {"op": "dt"}]}}},
             "nodes": [{"kind": "rect", "w": {"op": "mul", "args": [{"op": "state", "name": "t"}, 100]}, "h": 20, "fill": "#ffffff"}]}
            """)!.AsObject();
        var id = TestDocs.Create(Workspace, TestDocs.Doc("Growing", 1000, 100, TestDocs.Scene("grow", 0, 40, growing)));
        var doc = Workspace.Open(id)!;
        await SetPlaces(doc, External, 10, 20, strict: true);
        var window = await RequireWindow(id);
        Assert.True(window.Visible);
        Assert.Equal(1, Render.Tick(id));
        await _platform.IdleAsync();
        Assert.Equal(1, await _platform.InvokeAsync(() => window.FramesShown));

        await _platform.ChangeDisplaysAsync(BuiltIn); // its only display unplugged: hidden, still open
        Assert.False(window.Visible);
        Assert.Same(window, await WindowOf(id));

        // 100 ticks, each a fifteenth of a second on, each drawing new pixels: none reaches the hidden window.
        var delivered = 0;
        for (var tick = 0; tick < 100; tick++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(70));
            delivered += Render.Tick(id);
        }

        await _platform.IdleAsync();
        Assert.Equal(0, delivered);
        Assert.Equal(1, await _platform.InvokeAsync(() => window.FramesShown));
        Assert.Null(await _platform.InvokeAsync(() => Manager.SinkOf(id)));
        Assert.False(Render.IsRendering(id)); // the window was the profile's only output

        await _platform.ChangeDisplaysAsync(BuiltIn, External); // plugged back in: shown, and drawn at the next tick
        Assert.True(window.Visible);
        _clock.Advance(TimeSpan.FromMilliseconds(70));
        Assert.Equal(1, Render.Tick(id));
        await _platform.IdleAsync();
        Assert.Equal(2, await _platform.InvokeAsync(() => window.FramesShown));
        AssertNoErrors();
    }

    [Fact]
    public async Task EditsThatLeaveTheWindowAlone_DoNotPlaceItAgain()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var listed = _platform.DisplaysListed;
        var changes = window.FrameChanges;

        // An item dragged in the editor: 100 placement commits, none touching the profile's size or window.
        for (var i = 0; i < 100; i++)
            TestDocs.Commit(doc, TestDocs.Place("bar", 10 + i, 10));
        await _platform.IdleAsync();

        Assert.InRange(_platform.DisplaysListed - listed, 0, 2);
        Assert.Equal(changes, window.FrameChanges);

        // A window change is still applied.
        await Patch(doc, new JsonObject { ["x"] = 50 });
        Assert.Equal(changes + 1, window.FrameChanges);
        AssertNoErrors();
    }

    [Fact]
    public async Task ALevelChangedAndUndone_WhileAResizeWaits_IsAppliedLive()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        await _platform.InvokeAsync(() => window.UserResize(Rect(0, 0, 700.5, 260)));
        await _platform.IdleAsync();
        Assert.Equal(1, doc.Rev);

        // Before the size is quiet: the level goes to top and back, the user's frame untouched.
        await Patch(doc, new JsonObject { ["level"] = "top" });
        Assert.Equal(WindowLevel.Top, window.Style.Level);
        Assert.Equal(Rect(0, 0, 700.5, 260), window.Frame);
        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal(WindowLevel.Normal, window.Style.Level);
        Assert.Equal(Rect(0, 0, 700.5, 260), window.Frame);

        // The resize still commits at the user's frame.
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal((1401, 520, 0, 0), (P(doc).Width, P(doc).Height, W(doc).X, W(doc).Y));
        Assert.Equal(Rect(0, 0, 700.5, 260), window.Frame);
        Assert.Equal(WindowLevel.Normal, window.Style.Level);
        AssertNoErrors();
    }

    [Fact]
    public async Task DeletedProfiles_AreForgottenByTheOncePerProfileWarnings()
    {
        // 50 profiles this engine can't open (from a newer engine), each warned about once at start.
        var unsupported = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            var id = TestDocs.NewId();
            Seed(id, new JsonObject { ["format"] = "infopanel.document/2", ["id"] = id }.ToJsonString());
            unsupported.Add(id);
        }

        await Start();
        Assert.Equal(50, (await _platform.InvokeAsync(() => Manager.Warned)).Unopened);
        foreach (var id in unsupported)
            Assert.True(Workspace.DeleteWithItsTag(id));
        await _platform.IdleAsync();

        // 50 profiles over the frame pixel cap, each shown at reduced resolution once, then deleted.
        for (var i = 0; i < 50; i++)
        {
            var created = CreateActive($"Huge {i}", 4097, 4096);
            await _platform.IdleAsync();
            Assert.Equal(1, Render.Tick(created));
            await _platform.IdleAsync();
            Assert.True(Workspace.DeleteWithItsTag(created));
            await _platform.IdleAsync();
        }

        Assert.Equal(50, _log.Messages.Count(m => m.Contains("reduced resolution")));
        Assert.Equal((0, 0), await _platform.InvokeAsync(() => Manager.Warned));
        AssertNoErrors();
    }

    private static byte[] Unpremultiplied(DesktopFrame frame)
    {
        using var source = new SKBitmap();
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(frame.Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), frame.RowBytes);
            using var converted = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using var target = converted.PeekPixels();
            Assert.True(pixmap.ReadPixels(target));
            return converted.GetPixelSpan().ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>The manager's log messages, formatted.</summary>
    private sealed class CapturedLog : ILogger<DisplayWindowManager>
    {
        public ConcurrentQueue<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }
}
