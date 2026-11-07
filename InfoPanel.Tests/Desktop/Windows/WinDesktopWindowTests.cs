using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Desktop;
using InfoPanel.Desktop.Windows;
using InfoPanel.Hosting;
using Xunit;

namespace InfoPanel.Tests.Desktop.Windows;

/// <summary>
/// Real windows on Windows, opt-in (<c>INFOPANEL_WINDOW_TESTS=1</c>, on Windows only; skipped otherwise): they show
/// windows, so they need the signed-in user's interactive desktop, which a shell over SSH doesn't have.
/// <list type="bullet">
/// <item>A real engine process (the built engine next to the tests, its own folder, port and token), its windows found
/// with <c>EnumWindows</c> and read in physical pixels: the style, never activated, its place and pixel size on every
/// display, on top and back, as large as 10,000 pixels, a transparent pixel still the window's, gone when turned off,
/// and the display list equal to Windows' own.</item>
/// <item>A window's frames on the screen: one frame, then another, read back from the screen's pixels.</item>
/// <item>A window on the desktop: not topmost, below the test's own normal window, still below after a click on it.</item>
/// <item>The cost of showing a frame (<c>ShowFrame</c>) at 800×480, 1920×1080 and 3840×2160, at 15 and 60 frames a
/// second: measured and written to the test's output, not asserted.</item>
/// <item>Drag and resize with the real mouse (<c>SendInput</c>, so the session must also be connected and unlocked): a
/// drag of the body moves the window live and is one commit, undone by undo; a press without a move is none; a pull of
/// the bottom-right corner resizes it live and is one commit of the size; a window that isn't movable stays.</item>
/// </list>
/// Every window a test opens is closed before it ends, and the engine is stopped.
/// </summary>
public sealed class WinDesktopWindowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("INFOPANEL_WINDOW_TESTS") == "1";

    private const string Skip = "Opens real windows: set INFOPANEL_WINDOW_TESTS=1 on Windows, in the signed-in session, to run it.";

    /// <summary><c>WS_POPUP</c>, and <c>WS_CLIPSIBLINGS</c>, which Windows adds to every top-level window.</summary>
    private const uint Popup = 0x80000000 | 0x04000000, Visible = 0x10000000;
    private const uint Layered = 0x00080000, ToolWindow = 0x00000080, NoActivate = 0x08000000, Topmost = 0x00000008;

    [Fact]
    public async Task TheEngine_ShowsProfilesWithAWindowAsWindows_InPixels_AndFollowsCommits()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            await RunEngineAndCheckWindows();
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunEngineAndCheckWindows()
    {
        var root = Path.Combine(Path.GetTempPath(), "infopanel-window-tests-" + Guid.NewGuid().ToString("N"));
        var port = EngineRuntime.FindFreePort();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "InfoPanel.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "--port", port.ToString(), "--data-dir", Path.Combine(root, "data"), "--v1-dir", Path.Combine(root, "v1"), "--token", "wt",
                     "--no-browser", "--no-tray", "--no-plugins", "--no-panels", "--offline", "--mock-seed", "42" })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var api = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wt");
        try
        {
            await Until(async () => (await Try(() => api.GetStringAsync("/healthz", Ct))) == "ok", "the engine to listen");

            // The display list is Windows' own: device names, physical rectangles, the primary first, scale 1.
            var listed = await api.GetFromJsonAsync<JsonElement>("/api/displays", Ct);
            Assert.True(listed.GetProperty("supported").GetBoolean(), "the engine in the signed-in session shows desktop windows");
            var displays = listed.GetProperty("displays").EnumerateArray().ToList();
            var monitors = WinTestInterop.InPixels(WinDisplays.Monitors);
            var expected = monitors.OrderByDescending(m => m.Primary).ThenBy(m => m.Device, StringComparer.Ordinal)
                .Select(m => (m.Device, m.Right - m.Left, m.Bottom - m.Top, m.Left, m.Top)).ToList();
            Assert.Equal(expected, displays.Select(d => (d.GetProperty("name").GetString()!, d.GetProperty("width").GetInt32(),
                d.GetProperty("height").GetInt32(), d.GetProperty("x").GetInt32(), d.GetProperty("y").GetInt32())));
            Assert.All(displays, d => Assert.Equal(1.0, d.GetProperty("scale").GetDouble()));
            Log($"Displays: {string.Join("; ", displays.Select(d => d.GetRawText()))}");

            // A profile with its window enabled and a transparent background: one window, at the primary's top-left,
            // 400×200 pixels, exactly the style, not activated.
            var foreground = WinTestInterop.GetForegroundWindow();
            var id = await CreateWithWindow(api, "Window test", 400, 200, "#00000000");
            var window = await OneWindow(process.Id, w => w.Bounds == new PointRect(0, 0, 400, 200), "the new profile's window");
            Assert.Equal(Popup | Visible, window.Style);
            Assert.Equal(Layered | ToolWindow | NoActivate, window.ExStyle);
            Assert.True(WinTestInterop.IsPerMonitorV2(window.Handle));
            Assert.Equal(foreground, WinTestInterop.GetForegroundWindow());

            // On top through a commit, and back.
            await CommitWindow(api, id, new JsonObject { ["x"] = 200, ["y"] = 120, ["level"] = "top" });
            window = await OneWindow(process.Id, w => w.Bounds == new PointRect(200, 120, 400, 200) && (w.ExStyle & Topmost) != 0, "the moved, topmost window");

            // A fully transparent pixel of it is still the window's (the floor): a click there lands on the panel.
            await Until(() => Task.FromResult(WinTestInterop.TopLevelAt(400, 220) == window.Handle), "the window at its transparent pixel");

            await CommitWindow(api, id, new JsonObject { ["level"] = "normal" });
            window = await OneWindow(process.Id, w => (w.ExStyle & Topmost) == 0, "the window back from the top");
            Assert.Equal(Layered | ToolWindow | NoActivate, window.ExStyle);

            // On every display: 10, 10 pixels from its top-left, at the profile's pixel size whatever the display's scaling.
            foreach (var display in displays)
            {
                var (dx, dy) = (display.GetProperty("x").GetInt32(), display.GetProperty("y").GetInt32());
                await CommitWindow(api, id, new JsonObject
                {
                    ["x"] = 10,
                    ["y"] = 10,
                    ["display"] = new JsonObject
                    {
                        ["name"] = display.GetProperty("name").GetString(),
                        ["width"] = display.GetProperty("width").GetInt32(),
                        ["height"] = display.GetProperty("height").GetInt32(),
                        ["x"] = dx,
                        ["y"] = dy,
                    },
                });
                await OneWindow(process.Id, w => w.Bounds == new PointRect(dx + 10, dy + 10, 400, 200), $"the window on {display.GetProperty("name").GetString()}");
            }

            // Turned off: the window is gone.
            await CommitWindow(api, id, new JsonObject { ["enabled"] = false });
            await Until(() => Task.FromResult(Windows(process.Id).Count == 0), "the window to close");

            // A window larger than every screen: 10,000 × 10,000 pixels (transparent, so it hides nothing), then off again.
            var huge = await CreateWithWindow(api, "Huge", 10_000, 10_000, "#00000000");
            await OneWindow(process.Id, w => w.Bounds == new PointRect(0, 0, 10_000, 10_000), "the 10,000-pixel window");
            await CommitWindow(api, huge, new JsonObject { ["enabled"] = false });
            await Until(() => Task.FromResult(Windows(process.Id).Count == 0), "the huge window to close");
            Assert.Equal(foreground, WinTestInterop.GetForegroundWindow());
        }
        catch
        {
            lock (output)
                Log(output.ToString());
            throw;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task AWindow_ShowsEachNewFrame_OnTheScreen()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            await ShowTwoFrames();
    }

    /// <summary>
    /// A window on top at (100, 100) on the primary display, 200×100 pixels: frame A (red), then frame B (blue), each read
    /// back from the screen's pixels at the window's centre.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task ShowTwoFrames()
    {
        var thread = new WinUiThread();
        try
        {
            var window = await WinUiThreadTests.On(thread, () =>
            {
                var w = new WinWindow(new DesktopWindowStyle(WindowLevel.Top, true, true), new PointRect(100, 100, 200, 100));
                w.SetVisible(true);
                return w;
            });
            try
            {
                foreach (var (r, g, b) in new (byte, byte, byte)[] { (255, 0, 0), (0, 0, 255) })
                {
                    await WinUiThreadTests.On(thread, () => window.ShowFrame(WinUiThreadTests.Solid(200, 100, r, g, b)));
                    await Until(() => Task.FromResult(WinTestInterop.ScreenPixel(200, 150) == (r, g, b)), $"the screen to show ({r}, {g}, {b})",
                        () => WinTestInterop.ScreenPixel(200, 150).ToString());
                }
            }
            finally
            {
                await WinUiThreadTests.On(thread, window.Dispose);
            }
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AWindowOnTheDesktop_IsntTopmost_AndStaysBelowANormalWindow_WhenClicked()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            await DesktopLevel();
    }

    /// <summary>
    /// A window on the desktop at (300, 300), 400×200, and the test's own normal window at (500, 350) over part of it:
    /// the desktop one has no <c>WS_EX_TOPMOST</c> and is below the normal one in the z-order, and still is after a click
    /// on its uncovered part at (350, 350) (which must show the desktop there: nothing else over it).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task DesktopLevel()
    {
        var thread = new WinUiThread();
        try
        {
            var level = new WinDesktopLevel(thread);
            var (desktop, normal) = await WinUiThreadTests.On(thread, () =>
            {
                var d = new WinWindow(new DesktopWindowStyle(WindowLevel.Desktop, true, true), new PointRect(300, 300, 400, 200), level);
                var n = new WinWindow(new DesktopWindowStyle(WindowLevel.Normal, true, true), new PointRect(500, 350, 400, 200));
                d.SetVisible(true);
                n.SetVisible(true);
                d.ShowFrame(WinUiThreadTests.Solid(400, 200, 0, 160, 0));
                n.ShowFrame(WinUiThreadTests.Solid(400, 200, 0, 0, 160));
                return (d, n);
            });
            try
            {
                Assert.Equal(0u, WinTestInterop.Read(desktop.Handle).ExStyle & Topmost);
                Assert.True(WinTestInterop.IsAbove(normal.Handle, desktop.Handle), "the normal window is above the desktop one");
                await Until(() => Task.FromResult(WinTestInterop.TopLevelAt(350, 350) == desktop.Handle),
                    "the desktop window at (350, 350): nothing else may cover it there");

                await Gesture(m => DragGesture.Drag(m, 350, 350, 350, 350));
                await Task.Delay(1000, Ct);

                Assert.Equal(0u, WinTestInterop.Read(desktop.Handle).ExStyle & Topmost);
                Assert.True(WinTestInterop.IsAbove(normal.Handle, desktop.Handle), "the normal window is still above the desktop one after the click");
            }
            finally
            {
                await WinUiThreadTests.On(thread, desktop.Dispose);
                await WinUiThreadTests.On(thread, normal.Dispose);
            }
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ShowingAFrame_IsMeasured_At3SizesAnd2Rates()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            await MeasureShowFrame();
    }

    /// <summary>
    /// <c>ShowFrame</c> (the blit into the window's pixels and <c>UpdateLayeredWindow</c>) timed on the UI thread, frames
    /// paced at 15 and 60 a second, for 4 s and 2 s. The window is shown far off every screen, so nothing appears.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task MeasureShowFrame()
    {
        var thread = new WinUiThread();
        try
        {
            foreach (var (width, height) in new[] { (800, 480), (1920, 1080), (3840, 2160) })
            {
                var frames = new[] { Frame(width, height, 0), Frame(width, height, 1) };
                var window = await WinUiThreadTests.On(thread, () =>
                {
                    var w = new WinWindow(new DesktopWindowStyle(WindowLevel.Normal, true, true), new PointRect(-30_000, -30_000, width, height));
                    w.SetVisible(true);
                    return w;
                });
                try
                {
                    foreach (var (fps, seconds) in new[] { (15, 4), (60, 2) })
                    {
                        var times = new List<double>();
                        var interval = TimeSpan.FromSeconds(1.0 / fps);
                        var pace = Stopwatch.StartNew();
                        for (var i = 0; i < fps * seconds; i++)
                        {
                            var frame = frames[i % 2];
                            times.Add(await WinUiThreadTests.On(thread, () =>
                            {
                                var clock = Stopwatch.StartNew();
                                window.ShowFrame(frame);
                                return clock.Elapsed.TotalMilliseconds;
                            }));
                            var next = interval * (i + 1) - pace.Elapsed;
                            if (next > TimeSpan.Zero)
                                await Task.Delay(next, Ct);
                        }

                        times.Sort();
                        Log($"ShowFrame {width}x{height} at {fps} fps: p50 {Percentile(times, 0.50):F2} ms, p95 {Percentile(times, 0.95):F2} ms, max {times[^1]:F2} ms ({times.Count} frames)");
                    }
                }
                finally
                {
                    await WinUiThreadTests.On(thread, window.Dispose);
                }
            }
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    private static double Percentile(List<double> sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>An opaque frame of horizontal colour bands, shifted by <paramref name="phase"/> so consecutive frames differ.</summary>
    private static DesktopFrame Frame(int width, int height, int phase)
    {
        var rowBytes = width * 4;
        var pixels = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var v = (byte)((y + phase * 64) & 0xFF);
            for (var x = 0; x < width; x++)
            {
                var i = y * rowBytes + x * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (v, (byte)(x & 0xFF), (byte)(255 - v), 255);
            }
        }

        return new DesktopFrame(pixels, width, height, rowBytes);
    }

    [SupportedOSPlatform("windows")]
    private static List<WinTestInterop.Window> Windows(int pid) =>
        WinTestInterop.WindowsOf(pid, WinWindow.ClassName).Where(w => w.Visible).ToList();

    /// <summary>Waits until the engine has exactly one visible desktop window and it satisfies <paramref name="check"/>.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<WinTestInterop.Window> OneWindow(int pid, Func<WinTestInterop.Window, bool> check, string what)
    {
        WinTestInterop.Window? found = null;
        await Until(() =>
        {
            var windows = Windows(pid);
            found = windows.Count == 1 && check(windows[0]) ? windows[0] : null;
            return Task.FromResult(found != null);
        }, what, () => string.Join("; ", Windows(pid)));
        return found!;
    }

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>Creates a profile under a new id from a document with no items, its window enabled; returns the id.</summary>
    private static async Task<string> CreateWithWindow(HttpClient api, string name, int width, int height, string background)
    {
        var id = Guid.NewGuid().ToString();
        var document = new JsonObject
        {
            ["format"] = "infopanel.document/1",
            ["id"] = id,
            ["profile"] = new JsonObject
            {
                ["name"] = name,
                ["width"] = width,
                ["height"] = height,
                ["background"] = background,
                ["window"] = new JsonObject { ["enabled"] = true },
            },
            ["items"] = new JsonArray(),
        };
        using var response = await api.PostAsJsonAsync("/api/profiles", new JsonObject { ["id"] = id, ["document"] = document }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return id;
    }

    /// <summary>A <c>profile</c> commit of the window with the members of <paramref name="set"/> changed, the rest as read.</summary>
    private static async Task CommitWindow(HttpClient api, string id, JsonObject set)
    {
        var doc = await api.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
        var window = JsonNode.Parse(doc.GetProperty("document").GetProperty("profile").GetProperty("window").GetRawText())!.AsObject();
        foreach (var (key, value) in set)
            window[key] = value?.DeepClone();
        var body = new JsonObject
        {
            ["opId"] = Guid.NewGuid().ToString("N"),
            ["instance"] = doc.GetProperty("instance").GetString(), // the document's tag, not the session's
            ["baseRev"] = doc.GetProperty("rev").GetInt64(),
            ["ops"] = new JsonArray(new JsonObject { ["op"] = "profile", ["set"] = new JsonObject { ["window"] = window } }),
        };
        using var response = await api.PostAsJsonAsync($"/api/profiles/{id}/commit", body, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    // Drag and resize with the real mouse (SendInput): the system's move and size loop on an engine's window, each edit
    // read back through the API. Each gesture runs through DragGesture, bounded by GestureGuard: cancelled, it injects
    // nothing more once its cleanup has returned (within DragGesture.CleanupBound twice over), and the cleanup releases a
    // button it left down; the engine is then stopped and the pointer put back where it was.

    private static readonly TimeSpan GestureGuard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DraggingAWindow_IsOneUndoableEdit_OfItsPlace()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            await WithEngineWindow(DragAndUndo);
    }

    /// <summary>Pressed on its body and dragged 200 pixels right: one commit, the window there; undone, it goes back.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task DragAndUndo(HttpClient api, string id, IntPtr window)
    {
        var before = await Read(api, id);
        // Live: while the button is still down, the window is already where the mouse took it.
        var held = new PointRect(500, 300, 400, 200);
        await Gesture(m => DragGesture.Drag(m, 500, 400, 700, 400, () => HeldAt(m, window, held)));

        await UntilWithin(async () => (await Read(api, id)).X == 500, "the drag's commit");
        await Task.Delay(800, Ct);
        var after = await Read(api, id);
        Assert.Equal((before.Rev + 1, 500, 300), (after.Rev, after.X, after.Y));
        Assert.Equal(new PointRect(500, 300, 400, 200), WinTestInterop.Read(window).Bounds);

        using var undo = await api.PostAsJsonAsync($"/api/profiles/{id}/undo", new JsonObject { ["instance"] = after.Instance, ["baseRev"] = after.Rev }, Ct);
        Assert.True(undo.IsSuccessStatusCode, await undo.Content.ReadAsStringAsync(Ct));
        await UntilWithin(() => Task.FromResult(WinTestInterop.Read(window).Bounds == new PointRect(300, 300, 400, 200)), "the window back after undo");
        Assert.Equal(300, (await Read(api, id)).X);
    }

    [Fact]
    public async Task APressAndReleaseWithoutMoving_IsNoEdit()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            await WithEngineWindow(Click);
    }

    [SupportedOSPlatform("windows")]
    private static async Task Click(HttpClient api, string id, IntPtr window)
    {
        var before = await Read(api, id);
        await Gesture(m => DragGesture.Drag(m, 500, 400, 500, 400));

        await Task.Delay(1000, Ct);
        Assert.Equal(before, await Read(api, id));
        Assert.Equal(new PointRect(300, 300, 400, 200), WinTestInterop.Read(window).Bounds);
    }

    [Fact]
    public async Task ResizingAWindowFromItsCorner_IsOneEdit_OfItsSize_AfterItsQuiet()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            await WithEngineWindow(ResizeFromTheCorner);
    }

    /// <summary>
    /// Pressed 2 pixels inside the bottom-right corner (699, 499), within the band at any scaling, and pulled 100 right
    /// and 50 down: one commit of the size, once the size has been quiet, the place unchanged.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task ResizeFromTheCorner(HttpClient api, string id, IntPtr window)
    {
        var before = await Read(api, id);
        var held = new PointRect(300, 300, 500, 250);
        await Gesture(m => DragGesture.Drag(m, 697, 497, 797, 547, () => HeldAt(m, window, held)));

        await UntilWithin(async () => (await Read(api, id)).Width == 500, "the resize's commit");
        await Task.Delay(800, Ct);
        var after = await Read(api, id);
        Assert.Equal((before.Rev + 1, 500, 250, 300, 300), (after.Rev, after.Width, after.Height, after.X, after.Y));
        Assert.Equal(new PointRect(300, 300, 500, 250), WinTestInterop.Read(window).Bounds);
    }

    [Fact]
    public async Task AWindowThatIsntMovable_DoesntMove_WhenDragged()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (OperatingSystem.IsWindows())
            await WithEngineWindow(DragWhenNotMovable);
    }

    [SupportedOSPlatform("windows")]
    private static async Task DragWhenNotMovable(HttpClient api, string id, IntPtr window)
    {
        await CommitWindow(api, id, new JsonObject { ["draggable"] = false });
        var before = await Read(api, id);
        await Gesture(m => DragGesture.Drag(m, 500, 400, 700, 400));

        await Task.Delay(1000, Ct);
        Assert.Equal(before, await Read(api, id));
        Assert.Equal(new PointRect(300, 300, 400, 200), WinTestInterop.Read(window).Bounds);
    }

    /// <summary>What a test reads of a profile: its revision and tag, its window's place and its size.</summary>
    private sealed record ProfileRead(long Rev, string Instance, int X, int Y, int Width, int Height);

    private static async Task<ProfileRead> Read(HttpClient api, string id)
    {
        var doc = await api.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
        var profile = doc.GetProperty("document").GetProperty("profile");
        var window = profile.GetProperty("window");
        return new ProfileRead(doc.GetProperty("rev").GetInt64(), doc.GetProperty("instance").GetString()!,
            window.TryGetProperty("x", out var x) ? x.GetInt32() : 0, window.TryGetProperty("y", out var y) ? y.GetInt32() : 0,
            profile.GetProperty("width").GetInt32(), profile.GetProperty("height").GetInt32());
    }

    /// <summary>
    /// A real engine with one opaque 400 × 200 profile's window, on top, at (300, 300) on the primary display (its
    /// <c>draggable</c> and <c>resizable</c> left at their defaults, on), found under the pointer; then
    /// <paramref name="test"/> with the window's handle. The engine is stopped and the pointer put back after.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task WithEngineWindow(Func<HttpClient, string, IntPtr, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "infopanel-window-tests-" + Guid.NewGuid().ToString("N"));
        var port = EngineRuntime.FindFreePort();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "InfoPanel.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "--port", port.ToString(), "--data-dir", Path.Combine(root, "data"), "--v1-dir", Path.Combine(root, "v1"), "--token", "wt",
                     "--no-browser", "--no-tray", "--no-plugins", "--no-panels", "--offline", "--mock-seed", "42" })
            start.ArgumentList.Add(arg);

        var home = WinMouse.Instance.Position;
        using var process = Process.Start(start)!;
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var api = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wt");
        try
        {
            await Until(async () => (await Try(() => api.GetStringAsync("/healthz", Ct))) == "ok", "the engine to listen");
            var id = await CreateWithWindow(api, "Drag test", 400, 200, "#336699ff");
            await OneWindow(process.Id, w => w.Bounds == new PointRect(0, 0, 400, 200), "the new profile's window");
            await CommitWindow(api, id, new JsonObject { ["x"] = 300, ["y"] = 300, ["level"] = "top" });
            var window = await OneWindow(process.Id, w => w.Bounds == new PointRect(300, 300, 400, 200) && (w.ExStyle & Topmost) != 0, "the window on top at (300, 300)");
            foreach (var (x, y) in new[] { (500, 400), (697, 497) })
                Assert.True(WinTestInterop.TopLevelAt(x, y) == window.Handle, $"the window is under ({x}, {y})");
            await test(api, id, window.Handle);
        }
        catch
        {
            lock (output)
                Log(output.ToString());
            throw;
        }
        finally
        {
            // The engine first: putting the mouse back can fail (a session that isn't connected) and must not keep it running.
            // Every gesture's cleanup has returned by now, its gate shut, so its worker injects nothing more; bar the one input
            // a stuck SendInput still held when the cleanup gave up on it, which failed the test (DragGesture.Run).
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            WinMouse.Instance.Release();
            WinMouse.Instance.TryMoveTo(home.X, home.Y);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Runs <paramref name="gesture"/> with the real mouse off the test's thread, failing past <see cref="GestureGuard"/>;
    /// cancelled or timed out, it returns within its cleanup's bound, with the gesture's gate shut (<see cref="DragGesture.Run"/>).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static Task Gesture(Action<GestureMouse> gesture) => DragGesture.Run(WinMouse.Instance, gesture, GestureGuard, Ct);

    /// <summary>While the button is held: the window reaches <paramref name="bounds"/> within a second (the system's loop moves it live).</summary>
    [SupportedOSPlatform("windows")]
    private static void HeldAt(GestureMouse mouse, IntPtr window, PointRect bounds)
    {
        var deadline = Environment.TickCount64 + 1000;
        while (WinTestInterop.Read(window).Bounds != bounds && Environment.TickCount64 < deadline)
            mouse.Pause(20);
        Assert.Equal(bounds, WinTestInterop.Read(window).Bounds);
    }

    private static async Task UntilWithin(Func<Task<bool>> condition, string what)
    {
        var deadline = Environment.TickCount64 + (long)GestureGuard.TotalMilliseconds;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>The real mouse, through <c>SendInput</c>, in physical pixels of the virtual screen.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class WinMouse : IMouseInput
    {
        public static readonly WinMouse Instance = new();

        private WinMouse()
        {
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int Dx;
            public int Dy;
            public uint Data;
            public uint Flags;
            public uint Time;
            public IntPtr Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public MouseInput Mouse;
        }

        private const uint Move = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, VirtualDesk = 0x4000, Absolute = 0x8000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out WinTestInterop.Point point);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        private volatile bool _down;

        public (int X, int Y) Position => WinTestInterop.InPixels(() =>
        {
            GetCursorPos(out var p);
            return (p.X, p.Y);
        });

        /// <summary>To the pixel: <c>MOUSEEVENTF_ABSOLUTE</c> over the virtual screen maps n to n × width / 65536, so n is rounded up.</summary>
        public bool TryMoveTo(int x, int y) => WinTestInterop.InPixels(() =>
        {
            var (left, top, width, height) = (GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
            return Send(Move | Absolute | VirtualDesk, (int)(((long)(x - left) * 65536 + width - 1) / width), (int)(((long)(y - top) * 65536 + height - 1) / height));
        });

        public bool TryDown()
        {
            _down = true;
            return Send(LeftDown, 0, 0);
        }

        public bool TryUp()
        {
            _down = false;
            return Send(LeftUp, 0, 0);
        }

        /// <summary>Releases the left button if this pressed it or it is down; never throws (it runs in finally blocks).</summary>
        public void Release()
        {
            if (_down || (GetAsyncKeyState(1) & 0x8000) != 0)
                Send(LeftUp, 0, 0);
            _down = false;
        }

        private static bool Send(uint flags, int dx, int dy)
        {
            var input = new Input { Type = 0, Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = flags } };
            return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
        }
    }

    private static async Task<T?> Try<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (HttpRequestException)
        {
            return default;
        }
    }

    private static async Task Until(Func<Task<bool>> condition, string what, Func<string>? state = null)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"Timed out waiting for {what}.{(state != null ? " Now: " + state() : "")}");
            await Task.Delay(100, Ct);
        }
    }
}
