using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InfoPanel.Desktop;
using InfoPanel.Hosting;
using Xunit;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// Real AppKit windows: a real engine process (the built engine next to the tests, temp data folder, own port and
/// token), checked through the window server's list (bounds, level, owner). Opens windows on the screen, so it runs
/// only on macOS with <c>INFOPANEL_WINDOW_TESTS=1</c>; skipped otherwise. A process because AppKit needs the main
/// thread, which the test runner's is not.
/// </summary>
public sealed class MacDesktopWindowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Enabled => OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("INFOPANEL_WINDOW_TESTS") == "1";

    [Fact]
    public async Task TheEngine_ShowsProfilesWithAWindowAsWindows_AndFollowsCommits()
    {
        Assert.SkipUnless(Enabled, "Opens real windows: set INFOPANEL_WINDOW_TESTS=1 on macOS to run it.");
        if (OperatingSystem.IsMacOS())
            await RunEngineAndCheckWindows();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static async Task RunEngineAndCheckWindows()
    {
        var root = Path.Combine(Path.GetTempPath(), "infopanel-window-tests-" + Guid.NewGuid().ToString("N"));
        var port = EngineRuntime.FindFreePort();
        var engine = Path.Combine(AppContext.BaseDirectory, "InfoPanel");
        var start = new ProcessStartInfo(engine)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "--port", port.ToString(), "--data-dir", Path.Combine(root, "data"), "--v1-dir", Path.Combine(root, "v1"), "--token", "wt", "--no-browser", "--no-tray", "--no-panels", "--mock-seed", "42" })
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
            var scale = MacWindowList.MainDisplayScale();

            // The first run's example profile has no desktop window.
            var example = (await api.GetFromJsonAsync<JsonElement>("/api/profiles", Ct)).EnumerateArray().Single().GetProperty("id").GetString()!;

            // A profile created with its window enabled gets one: 400×200 pixels at the main display's top-left, normal level.
            var id = await CreateWithWindow(api, "Window test", 400, 200);
            await Until(() => Task.FromResult(OnScreen(process.Id).Any(w => w.Bounds == new PointRect(0, 0, 400 / scale, 200 / scale) && w.Layer == 0)),
                "the new profile's window");
            Assert.Single(OnScreen(process.Id));

            // Moving it and putting it on top through a commit (a client's path).
            await CommitWindow(api, id, new JsonObject { ["x"] = 200, ["y"] = 120, ["level"] = "top" });
            await Until(() => Task.FromResult(OnScreen(process.Id).Any(w =>
                w.Bounds == new PointRect(200 / scale, 120 / scale, 400 / scale, 200 / scale) && w.Layer == 3)), "the moved, floating window");

            // On the desktop: one level above the desktop icons; then on top again, then normal, each applied live.
            var desktopLayer = MacWindowLevelTests.CGWindowLevelForKey(MacWindowLevelTests.DesktopIconKey) + 1;
            foreach (var (level, layer) in new[] { ("desktop", desktopLayer), ("top", 3), ("normal", 0) })
            {
                await CommitWindow(api, id, new JsonObject { ["level"] = level });
                await Until(() => Task.FromResult(OnScreen(process.Id).Any(w =>
                    w.Bounds == new PointRect(200 / scale, 120 / scale, 400 / scale, 200 / scale) && w.Layer == layer)), $"the window at level {level}");
            }

            // Its window disabled: the window closes.
            await CommitWindow(api, id, new JsonObject { ["enabled"] = false });
            await Until(() => Task.FromResult(OnScreen(process.Id).Count == 0), "the window to close");

            // Moves made by the engine are no edits: the profile has the five commits' revisions only, the example is clean.
            Assert.Equal(6, (await api.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct)).GetProperty("rev").GetInt64());
            Assert.False((await api.GetFromJsonAsync<JsonElement>($"/api/profiles/{example}", Ct)).GetProperty("dirty").GetBoolean());

            // Over the frame pixel cap: drawn at half the scale, the layer stretches it (contents scale halved, linear
            // filter). Back under it: one frame pixel per backing pixel, nearest. Read back from the real layer.
            var hugeId = await CreateWithWindow(api, "Over the cap", 8192, 8192);
            Assert.Equal((scale / 2, "linear"), await LayerScaling(output, 0.5));
            await Commit(api, hugeId, new JsonObject { ["width"] = 400, ["height"] = 200 });
            Assert.Equal((scale, "nearest"), await LayerScaling(output, 1));

            // A clean stop (SIGTERM: a test runner started from a background shell may ignore SIGINT; the
            // manual check covers Ctrl+C) closes the windows and exits 0.
            Assert.Equal(0, kill(process.Id, 15));
            using var exit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            exit.CancelAfter(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(exit.Token);
            Assert.Equal(0, process.ExitCode);
        }
        catch
        {
            lock (output)
                TestContext.Current.TestOutputHelper?.WriteLine(output.ToString());
            throw;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The layer's contents scale and filter that the engine logs once a window shows frames rendered at <paramref name="frameScale"/>.</summary>
    private static async Task<(double ContentsScale, string Filter)> LayerScaling(System.Text.StringBuilder output, double frameScale)
    {
        var pattern = new Regex(
            $@"frames rendered at {frameScale.ToString(CultureInfo.InvariantCulture)}× at layer contents scale ([\d.]+), (\w+) magnification");
        Match? match = null;
        await Until(() =>
        {
            lock (output)
                match = pattern.Match(output.ToString());
            return Task.FromResult(match.Success);
        }, $"a window to show frames rendered at {frameScale}×");
        return (double.Parse(match!.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static List<MacWindowList.Window> OnScreen(int pid) => MacWindowList.Of(pid).Where(w => w.OnScreen).ToList();

    /// <summary>Creates a profile under a new id from a document with no items and its desktop window enabled; returns the id.</summary>
    private static async Task<string> CreateWithWindow(HttpClient api, string name, int width, int height)
    {
        var id = Guid.NewGuid().ToString();
        var document = new JsonObject
        {
            ["format"] = "infopanel.document/1",
            ["id"] = id,
            ["profile"] = new JsonObject { ["name"] = name, ["width"] = width, ["height"] = height, ["window"] = new JsonObject { ["enabled"] = true } },
            ["items"] = new JsonArray(),
        };
        using var response = await api.PostAsJsonAsync("/api/profiles", new JsonObject { ["id"] = id, ["document"] = document }, Ct);
        response.EnsureSuccessStatusCode();
        return id;
    }

    /// <summary>A <c>profile</c> commit of the window with the members of <paramref name="set"/> changed, the rest as read.</summary>
    private static async Task CommitWindow(HttpClient api, string id, JsonObject set)
    {
        var doc = await api.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
        var window = JsonNode.Parse(doc.GetProperty("document").GetProperty("profile").GetProperty("window").GetRawText())!.AsObject();
        foreach (var (key, value) in set)
            window[key] = value?.DeepClone();
        await Commit(api, id, new JsonObject { ["window"] = window });
    }

    /// <summary>One <c>profile</c> op setting <paramref name="set"/>, based on the revision just read.</summary>
    private static async Task Commit(HttpClient api, string id, JsonObject set)
    {
        var session = await api.GetFromJsonAsync<JsonElement>("/api/session", Ct);
        var doc = await api.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
        var body = new JsonObject
        {
            ["opId"] = Guid.NewGuid().ToString("N"),
            ["instance"] = session.GetProperty("instance").GetString(),
            ["baseRev"] = doc.GetProperty("rev").GetInt64(),
            ["ops"] = new JsonArray(new JsonObject { ["op"] = "profile", ["set"] = set }),
        };
        using var response = await api.PostAsJsonAsync($"/api/profiles/{id}/commit", body, Ct);
        response.EnsureSuccessStatusCode();
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

    private static async Task Until(Func<Task<bool>> condition, string what)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(100, Ct);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [Fact]
    public async Task TheBuiltInDisplay_IsListedWithItsMonitorId()
    {
        Assert.SkipUnless(Enabled, "Reads the real displays: set INFOPANEL_WINDOW_TESTS=1 on macOS to run it.");
        if (OperatingSystem.IsMacOS())
            await ListTheBuiltInDisplay();
    }

    /// <summary>
    /// A real engine with no window enabled, so nothing appears on the screen, lists the built-in display (found by its
    /// top-left in CoreGraphics' desktop points, <c>CGDisplayIsBuiltin</c> and <c>CGDisplayBounds</c>) with its monitor id,
    /// its EDID's maker and product code; the listing goes to the test's output.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static async Task ListTheBuiltInDisplay()
    {
        var builtIn = BuiltInDisplayOrigin();
        Assert.SkipWhen(builtIn == null, "No built-in display is active (a desktop Mac, or the lid closed).");

        var root = Path.Combine(Path.GetTempPath(), "infopanel-display-id-" + Guid.NewGuid().ToString("N"));
        var port = EngineRuntime.FindFreePort();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "InfoPanel"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "--port", port.ToString(), "--data-dir", Path.Combine(root, "data"), "--v1-dir", Path.Combine(root, "v1"), "--token", "wt", "--no-browser", "--no-tray", "--no-panels", "--mock-seed", "42" })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var api = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wt");
        try
        {
            await Until(async () => (await Try(() => api.GetStringAsync("/healthz", Ct))) == "ok", "the engine to listen");
            var list = await api.GetFromJsonAsync<JsonElement>("/api/displays", Ct);
            TestContext.Current.TestOutputHelper?.WriteLine($"GET /api/displays: {list}");

            var (x, y) = builtIn!.Value;
            var entry = list.GetProperty("displays").EnumerateArray()
                .Single(d => d.GetProperty("x").GetInt32() == (int)Math.Round(x) && d.GetProperty("y").GetInt32() == (int)Math.Round(y));
            var id = entry.TryGetProperty("id", out var value) ? value.GetString() : null;
            TestContext.Current.TestOutputHelper?.WriteLine($"The built-in display, {entry.GetProperty("label").GetString()}: id {id ?? "(none)"}");
            Assert.Matches("^[A-Z]{3}[0-9A-F]{4}$", id);

            Assert.Equal(0, kill(process.Id, 15));
            using var exit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            exit.CancelAfter(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(exit.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The built-in display's top-left in desktop points (CoreGraphics' global space: the main display's top-left at 0, 0, y down), or null.</summary>
    private static (double X, double Y)? BuiltInDisplayOrigin()
    {
        var displays = new uint[16];
        if (CGGetActiveDisplayList((uint)displays.Length, displays, out var count) != 0)
            return null;
        foreach (var display in displays.AsSpan(0, (int)count))
        {
            if (CGDisplayIsBuiltin(display) != 0)
            {
                var bounds = CGDisplayBounds(display);
                return (bounds.X, bounds.Y);
            }
        }

        return null;
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CoreGraphics)]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[] activeDisplays, out uint displayCount);

    [DllImport(CoreGraphics)]
    private static extern int CGDisplayIsBuiltin(uint display);

    [DllImport(CoreGraphics)]
    private static extern CGRect CGDisplayBounds(uint display);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }
}
