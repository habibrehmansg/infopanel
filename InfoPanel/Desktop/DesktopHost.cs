using InfoPanel.Desktop.MacOS;
using InfoPanel.Hosting;

namespace InfoPanel.Desktop;

/// <summary>
/// Chooses the desktop platform at start-up and, on macOS, runs the host off the main thread so the main thread can
/// run AppKit's event loop.
/// <list type="bullet">
/// <item>macOS, windows not disabled (<c>--no-windows</c>) and the entry point on the process main thread: AppKit
/// (<see cref="MacDesktopPlatform"/>); <see cref="RunAsync"/> runs the host on a worker thread and the AppKit loop
/// on the calling (main) thread until the host has stopped.</item>
/// <item>Otherwise (Windows and Linux for now, <c>--no-windows</c>, or an entry point run off the main thread by a
/// test host): <see cref="NullDesktopPlatform"/>, and the host runs on the calling thread as before.</item>
/// </list>
/// </summary>
public sealed class DesktopHost
{
    private readonly object? _mainLoop;

    private DesktopHost(IDesktopPlatform platform, object? mainLoop)
    {
        Platform = platform;
        _mainLoop = mainLoop;
    }

    public IDesktopPlatform Platform { get; }

    /// <summary>Call on the entry point's thread, before the host is built.</summary>
    public static DesktopHost Create(EngineOptions options)
    {
        if (options.NoWindows)
            return new DesktopHost(new NullDesktopPlatform(null), null);

        if (OperatingSystem.IsMacOS())
        {
            if (MacMainLoop.TryCreate() is { } loop)
                return new DesktopHost(new MacDesktopPlatform(loop), loop);
            return new DesktopHost(new NullDesktopPlatform("Desktop windows need the engine's entry point on the main thread; not showing them"), null);
        }

        return new DesktopHost(new NullDesktopPlatform("Desktop windows aren't supported on this OS yet"), null);
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
