using InfoPanel.Shell;
using Microsoft.Extensions.Hosting;

namespace InfoPanel.Tests.Shell;

/// <summary>
/// The shell's platform for tests: records the icon, hands out menus as the OS would each time one opens, invokes items
/// as a click does, and answers prompts from a queue. Nothing is drawn.
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

    public bool IsSupported => true;

    public event Action? TerminateRequested;

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
}
