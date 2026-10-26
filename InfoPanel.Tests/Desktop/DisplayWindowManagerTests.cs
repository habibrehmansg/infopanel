using System.Net.Http.Json;
using System.Text.Json.Nodes;
using InfoPanel.Desktop;
using InfoPanel.Editing;
using InfoPanel.Models;
using InfoPanel.Output;
using InfoPanel.Tests.Preview;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// <see cref="DisplayWindowManager"/> on the engine host with a <see cref="FakeDesktopPlatform"/>, a manual clock and
/// hand-driven render loops. Displays: "Built-in" (1512×982 points at 2×, the primary) and "External" (2560×1440 at
/// 1×, above and to the left).
/// </summary>
public sealed class DisplayWindowManagerTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DisplayInfo BuiltIn = new("Built-in", new PointRect(0, 0, 1512, 982), 2);
    private static readonly DisplayInfo External = new("External", new PointRect(-408, -1440, 2560, 1440), 1);

    private readonly EngineFactory _base = new();
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 26, 9, 41, 7, TimeSpan.Zero));
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
    public async Task AUserMove_OntoTheSameSpot_IsNoEdit()
    {
        var doc = await Start();
        var window = await RequireWindow(doc.Id);
        doc.PlaceWindow(new WindowPlacement(0, 0, new TargetWindow(0, 0, 3024, 1964, "Built-in")));
        await _platform.IdleAsync();
        var revision = doc.Revision;

        await _platform.InvokeAsync(() => window.UserMove(Rect(0, 0, 960, 240)));
        await _platform.IdleAsync();
        Assert.Equal(revision, doc.Revision);
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
}
