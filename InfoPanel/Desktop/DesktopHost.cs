using InfoPanel.Desktop.MacOS;
using InfoPanel.Desktop.Windows;
using InfoPanel.Hosting;
using InfoPanel.Shell;
using InfoPanel.Shell.MacOS;

namespace InfoPanel.Desktop;

/// <summary>
/// Chooses the desktop platform and the shell (the menu-bar item) at start-up and runs the host with the platform's UI
/// loop.
/// <list type="bullet">
/// <item>macOS with the entry point on the process main thread: desktop windows (<see cref="MacDesktopPlatform"/>) unless
/// <c>--no-windows</c>, and the menu-bar item (<see cref="MacShellPlatform"/>) unless <c>--no-tray</c>. When either is on,
/// <see cref="RunAsync"/> runs the host on a worker thread and the AppKit loop on the calling (main) thread until the host
/// has stopped.</item>
/// <item>Windows: desktop windows (<see cref="WinDesktopPlatform"/>) unless <c>--no-windows</c>, on the engine's own UI
/// thread (<see cref="WinUiThread"/>), started on first use; the host runs on the calling thread, and once it has stopped
/// (its windows closed) the UI thread is stopped too. No notification-area icon yet: <see cref="NullShellPlatform"/>.</item>
/// <item>Otherwise (Linux, both flags, or an entry point run off the main thread on macOS by a test host):
/// <see cref="NullDesktopPlatform"/> and <see cref="NullShellPlatform"/>, and the host runs on the calling thread.</item>
/// </list>
/// Test engines replace the platform, so the Windows UI thread never starts in them.
/// </summary>
public sealed class DesktopHost
{
    private readonly object? _mainLoop;

    private DesktopHost(IDesktopPlatform platform, IShellPlatform shell, object? mainLoop)
    {
        Platform = platform;
        Shell = shell;
        _mainLoop = mainLoop;
    }

    public IDesktopPlatform Platform { get; }

    /// <summary>The menu-bar item's platform, or <see cref="NullShellPlatform"/>.</summary>
    public IShellPlatform Shell { get; }

    /// <summary>What runs: desktop windows, the menu-bar item, and the UI loop whenever either does.</summary>
    public readonly record struct Choice(bool Windows, bool Tray)
    {
        public bool Loop => Windows || Tray;
    }

    /// <summary>The operating systems the desktop tells apart.</summary>
    internal enum DesktopOS
    {
        MacOS,
        Windows,
        Other,
    }

    /// <summary>This process's operating system.</summary>
    internal static DesktopOS CurrentOS =>
        OperatingSystem.IsMacOS() ? DesktopOS.MacOS : OperatingSystem.IsWindows() ? DesktopOS.Windows : DesktopOS.Other;

    /// <summary>The choice for <paramref name="options"/> on macOS (<paramref name="macOS"/>) or another OS without desktop windows.</summary>
    internal static Choice Choose(EngineOptions options, bool macOS, bool mainThread) =>
        Choose(options, macOS ? DesktopOS.MacOS : DesktopOS.Other, mainThread);

    /// <summary>
    /// The choice for <paramref name="options"/> on <paramref name="os"/> and this thread: on macOS only the main thread
    /// runs either; on Windows windows run on the engine's own UI thread, whichever thread this is (no tray yet); nothing
    /// elsewhere.
    /// </summary>
    internal static Choice Choose(EngineOptions options, DesktopOS os, bool mainThread) => os switch
    {
        DesktopOS.MacOS => mainThread ? new Choice(!options.NoWindows, !options.NoTray) : default,
        DesktopOS.Windows => new Choice(!options.NoWindows, false),
        _ => default,
    };

    /// <summary>Call on the entry point's thread, before the host is built.</summary>
    public static DesktopHost Create(EngineOptions options)
    {
        if (OperatingSystem.IsMacOS())
        {
            var choice = Choose(options, DesktopOS.MacOS, MacMainLoop.IsMainThread);
            if (choice.Loop && MacMainLoop.TryCreate() is { } loop)
            {
                IDesktopPlatform windows = choice.Windows ? new MacDesktopPlatform(loop) : new NullDesktopPlatform(null);
                IShellPlatform shell = choice.Tray ? new MacShellPlatform(loop) : new NullShellPlatform();
                return new DesktopHost(windows, shell, loop);
            }
        }

        if (OperatingSystem.IsWindows() && Choose(options, DesktopOS.Windows, mainThread: true).Windows)
        {
            var thread = new WinUiThread();
            return new DesktopHost(new WinDesktopPlatform(thread), new NullShellPlatform(), thread);
        }

        return new DesktopHost(new NullDesktopPlatform(UnsupportedMessage(options, CurrentOS)), new NullShellPlatform(), null);
    }

    /// <summary>What an engine without desktop windows logs when a profile would have had one; null with <c>--no-windows</c>.</summary>
    internal static string? UnsupportedMessage(EngineOptions options, DesktopOS os)
    {
        if (options.NoWindows)
            return null;
        // On Windows only --no-windows leaves windows off.
        return os == DesktopOS.MacOS
            ? "Desktop windows need the engine's entry point on the main thread; not showing them"
            : "Desktop windows aren't supported on Linux yet";
    }

    /// <summary>How long a stopped host waits for the Windows UI thread to end.</summary>
    private static readonly TimeSpan UiThreadStop = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <paramref name="host"/> to completion: on macOS on a worker thread under the AppKit loop when there is one;
    /// otherwise here, and on Windows the UI thread is stopped once the host has (its windows are closed by then).
    /// </summary>
    public async Task RunAsync(Func<Task> host)
    {
        if (OperatingSystem.IsMacOS() && _mainLoop is MacMainLoop loop)
        {
            var running = Task.Run(host);
            loop.RunUntil(running);
            await running;
            return;
        }

        if (OperatingSystem.IsWindows() && _mainLoop is WinUiThread thread)
        {
            try
            {
                await host();
            }
            finally
            {
                thread.Stop(UiThreadStop);
            }

            return;
        }

        await host();
    }
}
