using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// <item>The cost of showing a frame (<c>ShowFrame</c>) at 800×480, 1920×1080 and 3840×2160, at 15 and 60 frames a
/// second: measured and written to the test's output, not asserted.</item>
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
                     "--no-browser", "--no-tray", "--no-plugins", "--offline", "--mock-seed", "42" })
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
            await CommitWindow(api, id, new JsonObject { ["x"] = 200, ["y"] = 120, ["topmost"] = true });
            window = await OneWindow(process.Id, w => w.Bounds == new PointRect(200, 120, 400, 200) && (w.ExStyle & Topmost) != 0, "the moved, topmost window");

            // A fully transparent pixel of it is still the window's (the floor): a click there lands on the panel.
            await Until(() => Task.FromResult(WinTestInterop.TopLevelAt(400, 220) == window.Handle), "the window at its transparent pixel");

            await CommitWindow(api, id, new JsonObject { ["topmost"] = false });
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
                var w = new WinWindow(new DesktopWindowStyle(true, true, true), new PointRect(100, 100, 200, 100));
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
                    var w = new WinWindow(new DesktopWindowStyle(false, true, true), new PointRect(-30_000, -30_000, width, height));
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
