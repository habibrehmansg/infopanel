using InfoPanel.Shell;
using Microsoft.Extensions.Hosting;

namespace InfoPanel.Tests.Shell;

/// <summary>
/// The shell's platform for tests: records the icon, hands out menus as the OS would each time one opens, invokes items
/// as a click does, answers prompts from a queue, and keeps what is copied and the notifications shown (each clickable).
/// Nothing is drawn.
/// </summary>
public sealed class FakeShellPlatform : IShellPlatform
{
    private readonly Lock _lock = new();
    private readonly Queue<int> _answers = new();
    private readonly List<ShellPrompt> _prompts = [];
    private Func<ShellMenu>? _menu;
    private Func<string, Task>? _invoked;

    /// <summary>The host's lifetime, set when the fake is registered: <see cref="Show"/> notes whether the host had started.</summary>
    public IHostApplicationLifetime? Lifetime { get; set; }

    /// <summary>True, unless a test makes the platform one without a menu-bar item (the shell off).</summary>
    public bool IsSupported { get; init; } = true;

    public event Action? TerminateRequested;

    private Action? _reopen;

    /// <summary>
    /// How many times the app is opened again (a link opened, on macOS) as the engine starts listening for it, before the
    /// server listens: the engine's services start before it does.
    /// </summary>
    public int ReopensAsTheEngineStarts { get; init; }

    public event Action? ReopenRequested
    {
        add
        {
            _reopen += value;
            for (var i = 0; i < ReopensAsTheEngineStarts; i++)
                value?.Invoke();
        }
        remove => _reopen -= value;
    }

    /// <summary>Whether the engine listens for the app being opened again.</summary>
    public bool ListensForReopen => _reopen != null;

    /// <summary>The icon as shown, or null before <see cref="Show"/>.</summary>
    public ShellIcon? Icon { get; private set; }

    /// <summary>Whether the host had started (the server listened) when the icon was shown.</summary>
    public bool? ShownAfterStart { get; private set; }

    public int ShowCount { get; private set; }

    public bool Removed { get; private set; }

    public IReadOnlyList<ShellPrompt> Prompts
    {
        get
        {
            lock (_lock)
                return [.. _prompts];
        }
    }

    public void Show(ShellIcon icon, Func<ShellMenu> menu, Func<string, Task> invoked)
    {
        lock (_lock)
        {
            Icon = icon;
            ShowCount++;
            ShownAfterStart = Lifetime?.ApplicationStarted.IsCancellationRequested;
            _menu = menu;
            _invoked = invoked;
        }
    }

    public void Remove()
    {
        lock (_lock)
            Removed = true;
    }

    private readonly List<string> _copied = [];

    /// <summary>What was put on the clipboard, in order.</summary>
    public IReadOnlyList<string> Copied
    {
        get
        {
            lock (_lock)
                return [.. _copied];
        }
    }

    public void CopyText(string text)
    {
        lock (_lock)
            _copied.Add(text);
    }

    /// <summary>The next prompt is answered with the button at <paramref name="index"/>.</summary>
    public void Answer(int index)
    {
        lock (_lock)
            _answers.Enqueue(index);
    }

    public Task<int> ConfirmAsync(ShellPrompt prompt)
    {
        lock (_lock)
        {
            _prompts.Add(prompt);
            return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : prompt.CancelButton);
        }
    }

    private readonly List<ShellNotification> _notifications = [];
    private Func<Task>? _notificationClicked;

    /// <summary>The notifications shown, in order.</summary>
    public IReadOnlyList<ShellNotification> Notifications
    {
        get
        {
            lock (_lock)
                return [.. _notifications];
        }
    }

    /// <summary>
    /// Records <paramref name="note"/>. As the platforms do (macOS's one identifier, Windows' one balloon), only the
    /// newest notification's callback is kept: the next <c>Notify</c> overwrites it.
    /// </summary>
    public void Notify(ShellNotification note, Func<Task> clicked)
    {
        lock (_lock)
        {
            _notifications.Add(note);
            _notificationClicked = clicked;
        }
    }

    /// <summary>Clicks the notification on screen (the newest shown) and waits until the engine has handled it.</summary>
    public Task ClickNotificationAsync()
    {
        Func<Task> clicked;
        lock (_lock)
            clicked = _notificationClicked ?? throw new InvalidOperationException("No notification is shown.");
        return clicked();
    }

    /// <summary>The menu as it is built when the user opens it.</summary>
    public ShellMenu OpenMenu()
    {
        lock (_lock)
            return (_menu ?? throw new InvalidOperationException("No icon is shown."))();
    }

    /// <summary>Clicks the item <paramref name="id"/> and waits until the engine has handled it.</summary>
    public Task ClickAsync(string id)
    {
        Func<string, Task> invoked;
        lock (_lock)
            invoked = _invoked ?? throw new InvalidOperationException("No icon is shown.");
        return invoked(id);
    }

    /// <summary>The system asks the app to quit (logout, shutdown, Activity Monitor).</summary>
    public void RequestTerminate() => TerminateRequested?.Invoke();

    /// <summary>The app is opened again while it runs (Finder or the Dock on macOS).</summary>
    public void Reopen() => _reopen?.Invoke();
}
