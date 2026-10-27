using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    public async Task TheEngine_ShowsActiveProfilesAsWindows_AndFollowsApiEdits()
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
        foreach (var arg in new[] { "--port", port.ToString(), "--data-dir", Path.Combine(root, "data"), "--token", "wt", "--no-browser", "--mock-seed", "42" })
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

            // The example profile is active: 1920×480 pixels at the main display's top-left, normal level.
            var example = (await api.GetFromJsonAsync<JsonElement>("/api/profiles", Ct)).EnumerateArray().Single().GetProperty("id").GetString();
            await Until(() => Task.FromResult(OnScreen(process.Id).Any(w => w.Bounds == new PointRect(0, 0, 1920 / scale, 480 / scale) && w.Layer == 0)),
                "the example profile's window");

            // A new profile gets its own window.
            var created = await api.PostAsJsonAsync("/api/profiles", new { name = "Window test", width = 400, height = 200 }, Ct);
            created.EnsureSuccessStatusCode();
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString();
            await Until(() => Task.FromResult(OnScreen(process.Id).Count == 2), "two windows");

            // Moving it and putting it on top through the API (the editor's path).
            await PatchProfile(api, id!, new { windowX = 200, windowY = 120, topmost = true });
            await Until(() => Task.FromResult(OnScreen(process.Id).Any(w =>
                w.Bounds == new PointRect(200 / scale, 120 / scale, 400 / scale, 200 / scale) && w.Layer == 3)), "the moved, floating window");

            // Not active: its window closes; the example's stays.
            await PatchProfile(api, id!, new { active = false });
            await Until(() => Task.FromResult(OnScreen(process.Id).Count == 1), "the window to close");

            // Moves made by the engine are no edits: the example profile is still clean.
            var doc = await api.GetFromJsonAsync<JsonElement>($"/api/profiles/{example}", Ct);
            Assert.False(doc.GetProperty("dirty").GetBoolean());

            // Over the frame pixel cap: drawn at half the scale, the layer stretches it (contents scale halved, linear
            // filter). Back under it: one frame pixel per backing pixel, nearest. Read back from the real layer.
            var huge = await api.PostAsJsonAsync("/api/profiles", new { name = "Over the cap", width = 8192, height = 8192 }, Ct);
            huge.EnsureSuccessStatusCode();
            var hugeId = (await huge.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString();
            Assert.Equal((scale / 2, "linear"), await LayerScaling(output, 0.5));
            await PatchProfile(api, hugeId!, new { width = 400, height = 200 });
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

    private static async Task PatchProfile(HttpClient api, string id, object set)
    {
        using var response = await api.PatchAsJsonAsync($"/api/profiles/{id}", new { set }, Ct);
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
}
