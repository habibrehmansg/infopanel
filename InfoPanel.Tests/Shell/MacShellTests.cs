using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using InfoPanel.Desktop.MacOS;
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
                     "--no-browser", "--no-windows", "--no-panels", "--mock-seed", "42" })
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
                     "--no-browser", "--no-windows", "--no-panels", "--mock-seed", "42" })
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

/// <summary>The non-parallel collection of <see cref="MacAppDelegateTests"/>: the delegate's callbacks reach the latest shell.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MacAppDelegateCollection
{
    public const string Name = "The Mac app delegate";
}

/// <summary>
/// The app delegate (the menu-bar item's target class) answering the system off screen, nothing shown: an
/// <c>infopanel:</c> link opened (<c>application:openURLs:</c>) raises the same request as opening the app again, before
/// anyone listens too (a link that starts the app arrives while AppKit finishes launching), and a system quit before the
/// item shows is let through at once, as AppKit's default does. macOS only. Its own collection, run alone: the callbacks
/// reach the shell that last made the target, which another test's shell would replace.
/// </summary>
[Collection(MacAppDelegateCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed unsafe class MacAppDelegateTests
{
    private static readonly IntPtr ObjCLibrary = NativeLibrary.Load("/usr/lib/libobjc.A.dylib");

    private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte> SendBoolSel =
        (delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)NativeLibrary.GetExport(ObjCLibrary, "objc_msgSend");

    private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void> SendVoidIdId =
        (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)NativeLibrary.GetExport(ObjCLibrary, "objc_msgSend");

    private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, nuint> SendUIntId =
        (delegate* unmanaged<IntPtr, IntPtr, IntPtr, nuint>)NativeLibrary.GetExport(ObjCLibrary, "objc_msgSend");

    /// <summary>A shell whose target class is made, and a new instance of that class (what AppKit holds as its delegate).</summary>
    private static (InfoPanel.Shell.MacOS.MacShellPlatform Shell, IntPtr Target) Made()
    {
        var shell = new InfoPanel.Shell.MacOS.MacShellPlatform(null!);
        var menu = ObjC.Send(ObjC.Send(ObjC.Class("NSMenu"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        shell.Fill(menu, new ShellMenu([]));
        ObjC.SendVoid(menu, ObjC.Sel("release"));
        var target = ObjC.Send(ObjC.Send(ObjC.Class("InfoPanelShellTarget"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        return (shell, target);
    }

    private static bool AnswersOpenUrls() =>
        SendBoolSel(ObjC.Class("InfoPanelShellTarget"), ObjC.Sel("instancesRespondToSelector:"), ObjC.Sel("application:openURLs:")) != 0;

    /// <summary>An array holding one <c>NSURL</c> of <paramref name="link"/>, as AppKit hands the delegate.</summary>
    private static IntPtr Urls(string link)
    {
        var url = ObjC.SendId(ObjC.Class("NSURL"), ObjC.Sel("URLWithString:"), ObjC.NSString(link));
        return ObjC.SendId(ObjC.Class("NSArray"), ObjC.Sel("arrayWithObject:"), url);
    }

    [Fact]
    public void ALinkOpened_RaisesTheRequestToOpenTheEditor_Once()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AppKit");
        var pool = ObjC.AutoreleasePoolPush();
        try
        {
            var (shell, target) = Made();
            var raised = 0;
            shell.ReopenRequested += () => raised++;

            Assert.True(AnswersOpenUrls());
            SendVoidIdId(target, ObjC.Sel("application:openURLs:"), IntPtr.Zero, Urls("infopanel://open?page=elsewhere"));

            Assert.Equal(1, raised);
            ObjC.SendVoid(target, ObjC.Sel("release"));
        }
        finally
        {
            ObjC.AutoreleasePoolPop(pool);
        }
    }

    /// <summary>A link that starts the app arrives before the engine listens for it: the first to listen gets it, once.</summary>
    [Fact]
    public void ALinkOpened_BeforeAnyoneListens_IsGivenToTheFirstListener_Once()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AppKit");
        var pool = ObjC.AutoreleasePoolPush();
        try
        {
            var (shell, target) = Made();
            Assert.True(AnswersOpenUrls());
            SendVoidIdId(target, ObjC.Sel("application:openURLs:"), IntPtr.Zero, Urls("infopanel://open"));
            SendVoidIdId(target, ObjC.Sel("application:openURLs:"), IntPtr.Zero, Urls("infopanel://open"));

            var first = 0;
            var second = 0;
            shell.ReopenRequested += () => first++;
            shell.ReopenRequested += () => second++;

            Assert.Equal(1, first);
            Assert.Equal(0, second);
            ObjC.SendVoid(target, ObjC.Sel("release"));
        }
        finally
        {
            ObjC.AutoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// The delegate is set before the AppKit loop runs, before the item shows: a system quit then (a logout in the first
    /// seconds) goes on at once (<c>NSTerminateNow</c>, AppKit's default), asking the engine nothing.
    /// </summary>
    [Fact]
    public void ASystemQuit_BeforeTheItemShows_GoesOnAtOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AppKit");
        var pool = ObjC.AutoreleasePoolPush();
        try
        {
            var (shell, target) = Made();
            var asked = 0;
            shell.TerminateRequested += () => asked++;

            var answer = SendUIntId(target, ObjC.Sel("applicationShouldTerminate:"), IntPtr.Zero);

            Assert.Equal(1u, answer);
            Assert.Equal(0, asked);
            ObjC.SendVoid(target, ObjC.Sel("release"));
        }
        finally
        {
            ObjC.AutoreleasePoolPop(pool);
        }
    }
}
