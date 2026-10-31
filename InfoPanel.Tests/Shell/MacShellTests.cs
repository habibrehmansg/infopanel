using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using InfoPanel.Hosting;
using InfoPanel.Shell;
using Xunit;

namespace InfoPanel.Tests.Shell;

/// <summary>
/// The real menu-bar item: the built engine started as a child process without <c>--no-tray</c> (and with
/// <c>--no-windows</c>, which keeps the item), its log saying the item was added, then stopped as the system stops it
/// (<c>SIGTERM</c>): the item goes and the engine exits 0. Puts an icon in the menu bar, so it runs only on macOS with
/// <c>INFOPANEL_SHELL_TESTS=1</c>; skipped otherwise. A process because AppKit needs the main thread.
/// </summary>
public sealed class MacShellTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Enabled => OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("INFOPANEL_SHELL_TESTS") == "1";

    [Fact]
    public async Task TheEngine_AddsItsMenuBarItem_WithWindowsOff_AndRemovesItWhenStopped()
    {
        Assert.SkipUnless(Enabled, "Puts an icon in the menu bar: set INFOPANEL_SHELL_TESTS=1 on macOS to run it.");

        var root = Path.Combine(Path.GetTempPath(), "infopanel-shell-tests-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "InfoPanel"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "--port", EngineRuntime.FindFreePort().ToString(), "--data-dir", Path.Combine(root, "data"), "--v1-dir", Path.Combine(root, "v1"),
                     "--no-browser", "--no-windows", "--mock-seed", "42" })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        string Said()
        {
            lock (output)
                return output.ToString();
        }

        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!Said().Contains("Menu bar item added") && !process.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(50, Ct);
            Assert.Contains("Menu bar item added", Said());

            Assert.Equal(0, kill(process.Id, 15));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(limit.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Menu bar item removed", Said());
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The engine's Quit alert open (asked at start through the test setting <c>Engine__ShellTestPrompt</c>, read only in
    /// the <c>Test</c> host environment), then
    /// <c>SIGTERM</c>: the alert must not hold the main thread's queue, so the engine stops without anyone answering it,
    /// the alert dismissed as Cancel, the item removed, exit 0.
    /// </summary>
    [Fact]
    public async Task TheEngine_StopsOnSigterm_WithTheQuitAlertOpen()
    {
        Assert.SkipUnless(Enabled, "Puts an icon in the menu bar and an alert on screen: set INFOPANEL_SHELL_TESTS=1 on macOS to run it.");

        var root = Path.Combine(Path.GetTempPath(), "infopanel-shell-tests-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "InfoPanel"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "--port", EngineRuntime.FindFreePort().ToString(), "--data-dir", Path.Combine(root, "data"), "--v1-dir", Path.Combine(root, "v1"),
                     "--no-browser", "--no-windows", "--mock-seed", "42" })
            start.ArgumentList.Add(arg);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = ShellService.TestEnvironment;
        start.Environment["Engine__ShellTestPrompt"] = "true";

        using var process = Process.Start(start)!;
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        string Said()
        {
            lock (output)
                return output.ToString();
        }

        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!Said().Contains("The menu bar item's prompt is open") && !process.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(50, Ct);
            Assert.Contains("The menu bar item's prompt is open", Said());
            // Time for the alert's own loop to be the one running.
            await Task.Delay(500, Ct);

            Assert.Equal(0, kill(process.Id, 15));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                Assert.Fail($"The engine didn't stop within 20 s of SIGTERM with the alert open:\n{Said()}");
            }

            var said = Said();
            Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}:\n{said}");
            Assert.True(said.Contains("The test prompt was answered 1"), said);
            Assert.True(said.Contains("Menu bar item removed"), said);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
