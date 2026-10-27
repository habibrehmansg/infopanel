using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using InfoPanel.Desktop;
using InfoPanel.Editing;
using InfoPanel.Models;
using InfoPanel.Output;
using InfoPanel.Persistence;
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
/// hand-driven render loops, and the manager's log captured. Displays: "Built-in" (1512×982 points at 2×, the primary) and "External" (2560×1440 at
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

    public ValueTask InitializeAsync()
    {
        _factory = Create(_platform);
        return ValueTask.CompletedTask;
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

    /// <summary>Starts the host and waits for the manager's first pass; returns the example profile.</summary>
    private async Task<ProfileDocument> Start()
    {
        _ = _factory.Services;
        await _platform.IdleAsync();
        var id = Workspace.ListProfiles().Single(p => p.Name == "Wide bar").Id;
        return Workspace.Open(id)!;
    }

    private Task<FakeWindow?> WindowOf(Guid id) => _platform.InvokeAsync(() => Manager.WindowOf(id) as FakeWindow);

    private async Task<FakeWindow> RequireWindow(Guid id) => await WindowOf(id) ?? throw new Xunit.Sdk.XunitException($"No window for {id}");

    private async Task Patch(ProfileDocument doc, JsonObject set)
    {
        doc.PatchProfile(set);
        await _platform.IdleAsync();
    }

    private static PointRect Rect(double x, double y, double w, double h) => new(x, y, w, h);

    private void AssertNoErrors() => Assert.Empty(_platform.Errors);

    [Fact]
    public async Task AtStart_AnActiveProfile_GetsAVisibleWindow_AtItsPixelSize_OnTheFirstDisplay()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        Assert.True(window.Visible);
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame); // 1920×480 pixels at 2×
        Assert.Equal(new DesktopWindowStyle(Topmost: false, Movable: true, Resizable: true), window.Style);
        Assert.Equal(1, doc.Revision);
        Assert.True(Render.IsRendering(doc.Id));
        AssertNoErrors();
    }

    [Fact]
    public async Task Active_TurnedOffAndOn_ClosesAndReopensTheWindow()
    {
        var doc = await Start();
        var first = await RequireWindow(doc.Id);

        await Patch(doc, new JsonObject { ["active"] = false });
        Assert.True(first.Disposed);
        Assert.Null(await WindowOf(doc.Id));
        Assert.False(Render.IsRendering(doc.Id));

        await Patch(doc, new JsonObject { ["active"] = true });
        var second = await RequireWindow(doc.Id);
        Assert.NotSame(first, second);
        Assert.True(second.Visible);

        // Undo turns it off again; redo on.
        doc.Undo();
        await _platform.IdleAsync();
        Assert.Null(await WindowOf(doc.Id));
        doc.Redo();
        await _platform.IdleAsync();
        Assert.NotNull(await WindowOf(doc.Id));
        AssertNoErrors();
    }

    [Fact]
    public async Task AProfileCreatedActive_GetsAWindow_AndLosesItWhenDeleted()
    {
        await Start();
        var created = Workspace.Create("New", 100, 50);
        await _platform.IdleAsync();
        var window = await RequireWindow(created.Id);
        Assert.Equal(Rect(0, 0, 50, 25), window.Frame);

        Workspace.Delete(created.Id);
        await _platform.IdleAsync();
        Assert.True(window.Disposed);
        Assert.Null(await WindowOf(created.Id));
        AssertNoErrors();
    }

    [Fact]
    public async Task Shutdown_ClosesEveryWindow()
    {
        await Start();
        Workspace.Create("Second", 100, 50);
        await _platform.IdleAsync();
        var windows = await _platform.InvokeAsync(() => _platform.Created.ToList());
        Assert.Equal(2, windows.Count);

        await _factory.DisposeAsync();
        Assert.All(windows, w => Assert.True(w.Disposed));
        AssertNoErrors();
    }

    [Fact]
    public async Task TopmostDragResizeAndSize_AreAppliedLive()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        await Patch(doc, new JsonObject { ["topmost"] = true, ["drag"] = false, ["resize"] = false });
        Assert.Equal(new DesktopWindowStyle(Topmost: true, Movable: false, Resizable: false), window.Style);

        await Patch(doc, new JsonObject { ["width"] = 1000, ["height"] = 300 });
        Assert.Equal(Rect(0, 0, 500, 150), window.Frame);
        Assert.Same(window, await WindowOf(doc.Id));
        AssertNoErrors();
    }

    [Fact]
    public async Task WindowXAndY_AndTargetWindow_Reposition_ThroughUndoAndRedo()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        await Patch(doc, new JsonObject { ["windowX"] = 200, ["windowY"] = 100 });
        Assert.Equal(Rect(100, 50, 960, 240), window.Frame); // pixels of the 2× display

        doc.PlaceWindow(new WindowPlacement(30, 40, new TargetWindow(-408, -1440, 2560, 1440, "External")));
        await _platform.IdleAsync();
        Assert.Equal(Rect(-378, -1400, 1920, 480), window.Frame); // the 1× display: one point per pixel

        doc.Undo();
        await _platform.IdleAsync();
        Assert.Equal(Rect(100, 50, 960, 240), window.Frame);
        doc.Undo();
        await _platform.IdleAsync();
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame);
        doc.Redo();
        doc.Redo();
        await _platform.IdleAsync();
        Assert.Equal(Rect(-378, -1400, 1920, 480), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task StrictMatching_HidesTheWindowWithoutItsDisplay_AndShowsItWhenItIsBack()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        doc.PlaceWindow(new WindowPlacement(10, 20, new TargetWindow(-408, -1440, 2560, 1440, "External")));
        await Patch(doc, new JsonObject { ["strictWindowMatching"] = true });
        Assert.True(window.Visible);
        Assert.Equal(Rect(-398, -1420, 1920, 480), window.Frame);
        var revision = doc.Revision;

        await _platform.ChangeDisplaysAsync(BuiltIn); // unplugged
        Assert.False(window.Visible);
        Assert.Same(window, await WindowOf(doc.Id)); // still open

        await _platform.ChangeDisplaysAsync(BuiltIn, External); // plugged back in
        Assert.True(window.Visible);
        Assert.Equal(Rect(-398, -1420, 1920, 480), window.Frame);

        // Not strict: without its display the window goes to the first one, at the same offset.
        await Patch(doc, new JsonObject { ["strictWindowMatching"] = false });
        await _platform.ChangeDisplaysAsync(BuiltIn);
        Assert.True(window.Visible);
        Assert.Equal(Rect(5, 10, 960, 240), window.Frame);
        Assert.Equal(revision + 1, doc.Revision); // display changes never edit the profile
        AssertNoErrors();
    }

    [Fact]
    public async Task AUserMove_IsOneUndoableEdit_WithTheDisplayUnderTheTopLeft_AndSaveKeepsIt()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var changes = window.FrameChanges;

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal(2, doc.Revision);
        Assert.True(doc.Dirty);
        Assert.True(doc.CanUndo);
        TargetWindow target;
        lock (doc.SyncRoot)
        {
            Assert.Equal((120, 64), (doc.Profile.WindowX, doc.Profile.WindowY));
            target = doc.Profile.TargetWindow!;
        }

        Assert.Equal((-408, -1440, 2560, 1440, "External"), (target.X, target.Y, target.Width, target.Height, target.DeviceName));

        // On the 1× display the window is resized to keep its pixels (the manager's own change, not an edit).
        Assert.Equal(Rect(-288, -1376, 1920, 480), window.Frame);
        Assert.Equal(2, doc.Revision);
        Assert.True(window.FrameChanges > changes);

        Assert.NotNull(Workspace.Save(doc.Id));
        var saved = Workspace.Store.LoadProfiles().Single(p => p.Guid == doc.Id);
        Assert.Equal((120, 64, "External", 2560), (saved.WindowX, saved.WindowY, saved.TargetWindow?.DeviceName, saved.TargetWindow?.Width));

        doc.Undo();
        await _platform.IdleAsync();
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task AUserMove_OntoTheSameSpot_IsNoEdit_AndTheWindowGoesBackToItsPlace()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        doc.PlaceWindow(new WindowPlacement(0, 0, new TargetWindow(0, 0, 3024, 1964, "Built-in")));
        await _platform.IdleAsync();
        var revision = doc.Revision;

        // Less than half a pixel away: the same WindowX and WindowY.
        await _platform.InvokeAsync(() => window.UserMove(Rect(0.2, 0.2, 960, 240)));
        await _platform.IdleAsync();
        Assert.Equal(revision, doc.Revision);
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame);
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
        Assert.Equal(1, doc.Revision);

        // Still mid-drag, another profile is created and a display is plugged in: both reconcile every window.
        Workspace.Create("Second", 100, 50);
        await _platform.IdleAsync();
        await _platform.ChangeDisplaysAsync(BuiltIn, External, new DisplayInfo("Projector", Rect(1512, 0, 1920, 1080), 1));
        Assert.Equal(Rect(-288, -1376, 960, 240), window.Frame);
        Assert.Equal(1, doc.Revision);

        await _platform.InvokeAsync(window.EndUserMove);
        await _platform.IdleAsync();
        Assert.Equal(2, doc.Revision);
        lock (doc.SyncRoot)
            Assert.Equal((120, 64, "External"), (doc.Profile.WindowX, doc.Profile.WindowY, doc.Profile.TargetWindow?.DeviceName));
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
        Assert.Equal(1, doc.Revision);
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
        Assert.Equal(1, doc.Revision);
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
        Assert.Equal(1, doc.Revision);

        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal(2, doc.Revision);
        Assert.True(doc.Dirty);
        lock (doc.SyncRoot)
            Assert.Equal((1401, 520, 0, 0), (doc.Profile.Width, doc.Profile.Height, doc.Profile.WindowX, doc.Profile.WindowY));
        Assert.Equal(Rect(0, 0, 700.5, 260), window.Frame);

        // A resize from the top-left edge also moves the position.
        await _platform.InvokeAsync(() => window.UserResize(Rect(10, 5, 690.5, 255)));
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal(3, doc.Revision);
        lock (doc.SyncRoot)
            Assert.Equal((1381, 510, 20, 10), (doc.Profile.Width, doc.Profile.Height, doc.Profile.WindowX, doc.Profile.WindowY));

        doc.Undo();
        await _platform.IdleAsync();
        Assert.Equal(Rect(0, 0, 700.5, 260), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task AResizeFromAnEdge_ReleasedBeforeItCommits_IsStillOneEdit()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        // The button goes down on the window's left edge, the resize moves its top-left, and the button is up again
        // before the size is quiet: the release is no move of its own.
        await _platform.InvokeAsync(() =>
        {
            window.BeginUserMove();
            window.UserResize(Rect(10, 0, 950, 240));
            window.EndUserMove();
        });
        await _platform.IdleAsync();
        Assert.Equal(1, doc.Revision);

        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal(2, doc.Revision);
        lock (doc.SyncRoot)
            Assert.Equal((1900, 480, 20, 0), (doc.Profile.Width, doc.Profile.Height, doc.Profile.WindowX, doc.Profile.WindowY));
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
        Assert.Equal(1, doc.Revision);

        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal(2, doc.Revision);
        lock (doc.SyncRoot)
            Assert.Equal((1600, 500), (doc.Profile.Width, doc.Profile.Height));
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
        Assert.Equal(2, doc.Revision);

        await _platform.InvokeAsync(window.EndUserMove);
        await _platform.IdleAsync();
        Assert.Equal(2, doc.Revision);
        Assert.Equal(Rect(0, 0, 900, 240), window.Frame);
        Assert.DoesNotContain(_log.Messages, m => m.Contains(" moved "));

        doc.Undo();
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
        Assert.Equal(3, doc.Revision);
        lock (doc.SyncRoot)
            Assert.Equal((1800, 480, 200, 100), (doc.Profile.Width, doc.Profile.Height, doc.Profile.WindowX, doc.Profile.WindowY));
        Assert.Equal(Rect(100, 50, 900, 240), window.Frame);

        // The resize's timer has nothing left to commit.
        _clock.Advance(DisplayWindowManager.ResizeQuiet);
        await _platform.IdleAsync();
        await _platform.IdleAsync();
        Assert.Equal(3, doc.Revision);

        // Undo takes back the drag alone, then the resize.
        doc.Undo();
        await _platform.IdleAsync();
        lock (doc.SyncRoot)
            Assert.Equal((1800, 480, 0, 0), (doc.Profile.Width, doc.Profile.Height, doc.Profile.WindowX, doc.Profile.WindowY));
        Assert.Equal(Rect(0, 0, 900, 240), window.Frame);
        Assert.True(doc.CanUndo);

        doc.Undo();
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
        doc.PlaceWindow(new WindowPlacement(10, 20, new TargetWindow(-408, -1440, 2560, 1440, "External")));
        await _platform.IdleAsync();
        Assert.Equal(Rect(-398, -1420, 1920, 480), window.Frame);
        var revision = doc.Revision;

        // The button goes down on the window and its display is unplugged before it is up: the system moves the window
        // onto the built-in display, under the still pressed button.
        await _platform.InvokeAsync(window.BeginUserMove);
        await _platform.ChangeDisplaysAsync(BuiltIn);
        Assert.Equal(Rect(0, 0, 1920, 480), window.Frame);

        await _platform.InvokeAsync(window.EndUserMove);
        await _platform.IdleAsync();
        Assert.Equal(revision, doc.Revision);
        lock (doc.SyncRoot)
            Assert.Equal((10, 20, "External"), (doc.Profile.WindowX, doc.Profile.WindowY, doc.Profile.TargetWindow?.DeviceName));

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

        // Listed first, so that today's pass reaches it before the readable one.
        var store = new ProfileStore(new DataPaths(_base.DataDir));
        var unreadable = new Profile("Unreadable", 100, 50);
        var readable = new Profile("Readable", 200, 100);
        store.AddProfile(unreadable, []);
        store.AddProfile(readable, []);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(store.Paths.ProfileItemsFile(unreadable.Guid), UnixFileMode.None);

        _ = _factory.Services;
        await _platform.IdleAsync();

        var window = await RequireWindow(readable.Guid);
        Assert.True(window.Visible);
        Assert.Null(await WindowOf(unreadable.Guid));
        AssertNoErrors();
    }

    [Fact]
    public async Task TheManagersOwnMoves_NeverEditTheProfile()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        await Patch(doc, new JsonObject { ["windowX"] = 50 });
        await _platform.ChangeDisplaysAsync(External, BuiltIn);
        await _platform.ChangeDisplaysAsync(BuiltIn, External);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _platform.IdleAsync();

        Assert.Equal(2, doc.Revision);
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
        Assert.Equal((1f, 1920, 480), (header.Scale, header.PixelWidth, header.PixelHeight));
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
    public async Task AFrameWithUnchangedPixels_IsNotCopiedAgain()
    {
        await Start();
        var created = Workspace.Create("Plain", 64, 32); // nothing on it moves
        await _platform.IdleAsync();
        var doc = Workspace.Open(created.Id)!;
        var window = await RequireWindow(doc.Id);
        var sink = await _platform.InvokeAsync(() => Manager.SinkOf(doc.Id)!);
        Assert.Equal(1, Render.Tick(doc.Id));
        await _platform.IdleAsync();
        Assert.Equal(1, await _platform.InvokeAsync(() => window.FramesShown));
        Assert.Equal(1, Interlocked.Read(ref sink.FramesReceived));

        // An edit that leaves the pixels alone: the new revision reaches the sink, which skips the copy.
        await Patch(doc, new JsonObject { ["topmost"] = true });
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
        var created = Workspace.Create("Huge", 4097, 4096); // 16 781 312 pixels: over FrameScale.MaxPixels at scale 1
        await _platform.IdleAsync();
        var window = await RequireWindow(created.Id);
        Assert.Equal(1, Render.Tick(created.Id));
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
        var created = Workspace.Create("Square", 4096, 4096); // exactly FrameScale.MaxPixels: scale 1
        await _platform.IdleAsync();
        var doc = Workspace.Open(created.Id)!;
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

        doc.Undo();
        await _platform.IdleAsync();
        Assert.Equal(0.5f, await ShownScale());
        doc.Redo();
        await _platform.IdleAsync();
        Assert.Equal(1f, await ShownScale());
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
