using System.Reflection;
using System.Runtime.Versioning;
using InfoPanel.Api;
using InfoPanel.Desktop.Windows;
using static InfoPanel.Shell.Windows.WinShellInterop;

namespace InfoPanel.Shell.Windows;

/// <summary>
/// The notification-area icon on Windows (docs/2.0/desktop-shell.md, "Windows"): <see cref="WinTray"/> on the engine's
/// UI thread (<see cref="WinUiThread"/>, the one desktop windows use), owned by that thread's hidden top-level window,
/// whose messages it hears through a hook. Everything runs on that thread; <see cref="Show"/>, <see cref="Remove"/> and
/// <see cref="ConfirmAsync"/> post there from any thread. Its tooltip is "InfoPanel {version}" (the model's tooltip, the
/// engine's address, is macOS's). Not supported when the process's window station isn't interactive (a service, or a
/// shell started over SSH): no taskbar sees it there. The session ending (sign-out, shutdown) raises
/// <see cref="TerminateRequested"/> on the UI thread, which then holds the end until the host has stopped (bounded:
/// <see cref="WinTray.SessionEndWait"/>), as macOS's quit waits; console shutdown events don't come once user32 is
/// loaded.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinShellPlatform : IShellPlatform
{
    private static readonly Lazy<uint> TaskbarCreated = new(RegisterTaskbarCreated);
    private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<WinShellPlatform>();

    private readonly WinUiThread _thread;
    private readonly Lazy<bool> _supported = new(WinDesktopPlatform.IsInteractiveWindowStation);

    public WinShellPlatform(WinUiThread thread)
    {
        _thread = thread;
        thread.AddMessageHook((msg, wParam, lParam) => Tray?.OnMessage(msg, wParam, lParam));
    }

    /// <summary>Explorer's <c>TaskbarCreated</c> message, broadcast when the taskbar is (re)created.</summary>
    public static uint TaskbarCreatedMessage => TaskbarCreated.Value;

    /// <summary>The icon's logic once made (UI thread).</summary>
    internal WinTray? Tray { get; private set; }

    public bool IsSupported => _supported.Value;

    public event Action? TerminateRequested;

    public void Show(ShellIcon icon, Func<ShellMenu> menu, Func<string, Task> invoked) =>
        _thread.Post(() => EnsureTray()?.Show(WinShellText.Tooltip(ReadEndpoints.EngineVersion()), menu, invoked));

    public void Remove()
    {
        if (_thread.Started)
            _thread.Post(() => Tray?.Remove());
    }

    /// <summary>Puts <paramref name="text"/> on the clipboard on the UI thread, owned by its hidden window (Copy engine details).</summary>
    public void CopyText(string text) => _thread.Post(() =>
    {
        if (!WinClipboard.SetText(_thread.Hwnd, text))
            Logger.Warning("The engine's details couldn't be put on the clipboard");
    });

    public Task<int> ConfirmAsync(ShellPrompt prompt)
    {
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread.Post(() =>
        {
            if (EnsureTray() is { } tray)
                tray.Enqueue(prompt, answer);
            else
                answer.TrySetResult(prompt.CancelButton);
        });
        return answer.Task;
    }

    /// <summary>Makes the icon's logic on the UI thread, once; null when the thread has no window.</summary>
    private WinTray? EnsureTray()
    {
        if (Tray != null)
            return Tray;
        var hwnd = _thread.Hwnd;
        if (hwnd == 0)
            return null;

        // An engine run as administrator still hears Explorer (unelevated) and the icon's clicks.
        ChangeWindowMessageFilterEx(hwnd, TaskbarCreatedMessage, MSGFLT_ALLOW, 0);
        ChangeWindowMessageFilterEx(hwnd, WinTray.CallbackMessage, MSGFLT_ALLOW, 0);

        var command = WinStartup.CommandFor(Environment.ProcessPath ?? "InfoPanel.exe", Assembly.GetEntryAssembly()?.Location);
        Tray = new WinTray(new WinTrayNative(hwnd), new WinStartup(new WinRegistry(), command), TaskbarCreatedMessage, () => Environment.TickCount64);
        Tray.SessionEnding += () => TerminateRequested?.Invoke();
        return Tray;
    }

    private static unsafe uint RegisterTaskbarCreated()
    {
        fixed (char* name = "TaskbarCreated")
            return RegisterWindowMessageW(name);
    }
}
