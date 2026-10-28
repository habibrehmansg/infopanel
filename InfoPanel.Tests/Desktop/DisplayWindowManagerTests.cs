using System.Collections.Concurrent;
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

    [Fact]
    public async Task AtStart_AnActiveProfile_GetsAVisibleWindow_AtItsPixelSize_OnTheFirstDisplay()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        Assert.True(window.Visible);
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame); // 1920×480 pixels at 2×
        Assert.Equal(new DesktopWindowStyle(Topmost: false, Movable: true, Resizable: true), window.Style);
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
    public async Task TopmostDragResizeAndSize_AreAppliedLive()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);

        await Patch(doc, new JsonObject { ["topmost"] = true, ["draggable"] = false, ["resizable"] = false });
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
    public async Task AUserMove_IsOneUndoableEdit_WithTheDisplayUnderTheTopLeft_AndSaveKeepsIt()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        var changes = window.FrameChanges;

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal(2, doc.Rev);
        Assert.True(doc.Dirty);
        Assert.True(doc.CanUndo);
        Assert.Equal((120, 64), (W(doc).X, W(doc).Y));
        var target = W(doc).Display!;
        Assert.Equal((-408, -1440, 2560, 1440, "External"), (target.X, target.Y, target.Width, target.Height, target.Name));

        // On the 1× display the window is resized to keep its pixels (the manager's own change, not an edit).
        Assert.Equal(Rect(-288, -1376, 1920, 480), window.Frame);
        Assert.Equal(2, doc.Rev);
        Assert.True(window.FrameChanges > changes);

        doc.Save();
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(_base.DataDir, "profiles", doc.Id, "document.json")))!["profile"]!["window"]!;
        Assert.Equal((120, 64, "External", 2560), (saved["x"]!.GetValue<int>(), saved["y"]!.GetValue<int>(), saved["display"]!["name"]!.GetValue<string>(), saved["display"]!["width"]!.GetValue<int>()));

        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal(Rect(0, 0, 960, 240), window.Frame);
        AssertNoErrors();
    }

    [Fact]
    public async Task ADrag_WhileAnotherClientChangesTheWindow_KeepsThatChange_AndIsOneUndoableStep()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        // After the drop read the profile, before its commit: another client makes the window floating and fixed-size.
        Manager.BeforeCommit = _ =>
        {
            Manager.BeforeCommit = null;
            var changed = JsonNode.Parse(JsonSerializer.Serialize(W(doc) with { Topmost = true, Resizable = false }, SpecJson.Options))!;
            TestDocs.Commit(doc, TestDocs.Profile(new JsonObject { ["window"] = changed }));
        };

        await _platform.InvokeAsync(() => window.UserMove(Rect(-408 + 120, -1440 + 64, 960, 240)));
        await _platform.IdleAsync();

        Assert.Equal((3L, 120, 64, "External", true, false), (doc.Rev, W(doc).X, W(doc).Y, W(doc).Display!.Name, W(doc).Topmost, W(doc).Resizable));

        // Undoing the drag leaves the other client's change.
        Undo(doc);
        await _platform.IdleAsync();
        Assert.Equal((null, true, false), (W(doc).Display, W(doc).Topmost, W(doc).Resizable));
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
        Assert.True(doc.Dirty);
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
        Assert.Equal(1, doc.Rev);

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
        await _platform.IdleAsync();
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
