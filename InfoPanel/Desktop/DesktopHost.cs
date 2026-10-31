using InfoPanel.Desktop.MacOS;
using InfoPanel.Hosting;
using InfoPanel.Shell;
using InfoPanel.Shell.MacOS;

namespace InfoPanel.Desktop;

/// <summary>
/// Chooses the desktop platform and the shell (the menu-bar item) at start-up and, on macOS, runs the host off the main
/// thread so the main thread can run AppKit's event loop.
/// <list type="bullet">
/// <item>macOS with the entry point on the process main thread: desktop windows (<see cref="MacDesktopPlatform"/>) unless
/// <c>--no-windows</c>, and the menu-bar item (<see cref="MacShellPlatform"/>) unless <c>--no-tray</c>. When either is on,
/// <see cref="RunAsync"/> runs the host on a worker thread and the AppKit loop on the calling (main) thread until the host
/// has stopped.</item>
/// <item>Otherwise (Windows and Linux for now, both flags, or an entry point run off the main thread by a test host):
/// <see cref="NullDesktopPlatform"/> and <see cref="NullShellPlatform"/>, and the host runs on the calling thread as
/// before.</item>
/// </list>
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

    /// <summary>The choice for <paramref name="options"/> on this OS and thread: only the Mac's main thread runs either.</summary>
    internal static Choice Choose(EngineOptions options, bool macOS, bool mainThread) =>
        macOS && mainThread ? new Choice(!options.NoWindows, !options.NoTray) : default;

    /// <summary>Call on the entry point's thread, before the host is built.</summary>
    public static DesktopHost Create(EngineOptions options)
    {
        if (OperatingSystem.IsMacOS())
        {
            var choice = Choose(options, macOS: true, MacMainLoop.IsMainThread);
            if (choice.Loop && MacMainLoop.TryCreate() is { } loop)
            {
                IDesktopPlatform windows = choice.Windows ? new MacDesktopPlatform(loop) : new NullDesktopPlatform(null);
                IShellPlatform shell = choice.Tray ? new MacShellPlatform(loop) : new NullShellPlatform();
                return new DesktopHost(windows, shell, loop);
            }
        }

        return new DesktopHost(new NullDesktopPlatform(UnsupportedMessage(options)), new NullShellPlatform(), null);
    }

    private static string? UnsupportedMessage(EngineOptions options)
    {
        if (options.NoWindows)
            return null;
        return OperatingSystem.IsMacOS()
            ? "Desktop windows need the engine's entry point on the main thread; not showing them"
            : "Desktop windows aren't supported on this OS yet";
    }

    /// <summary>Runs <paramref name="host"/> to completion: on a worker thread under the AppKit loop when there is one, else here.</summary>
    public async Task RunAsync(Func<Task> host)
    {
        if (OperatingSystem.IsMacOS() && _mainLoop is MacMainLoop loop)
        {
            var running = Task.Run(host);
            loop.RunUntil(running);
            await running;
            return;
        }

        await host();
    }
}
