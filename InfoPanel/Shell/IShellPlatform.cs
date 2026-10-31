namespace InfoPanel.Shell;

/// <summary>The icon the shell shows: in P1 only its tooltip (the engine's address), the glyph being the platform's own.</summary>
public sealed record ShellIcon(string Tooltip);

/// <summary>
/// A question the shell asks in a window of its own (the unsaved-changes alert; plugin installs later): its title, its
/// text, its buttons in order, the one Return chooses (<paramref name="DefaultButton"/>) and the one Escape or closing it
/// chooses (<paramref name="CancelButton"/>), as indexes into <paramref name="Buttons"/>.
/// </summary>
public sealed record ShellPrompt(string Title, string Text, IReadOnlyList<string> Buttons, int DefaultButton, int CancelButton);

/// <summary>
/// The operating system's menu-bar item or tray icon, for <see cref="ShellService"/>. The platform calls back on its UI
/// thread; the engine's handlers leave that thread before they do any work.
/// </summary>
public interface IShellPlatform
{
    /// <summary>False where there is no menu-bar item (<see cref="NullShellPlatform"/>).</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Shows the icon. Each time the user opens its menu the platform asks <paramref name="menu"/> for a fresh one; a click
    /// on an item calls <paramref name="invoked"/> with its id. Any thread.
    /// </summary>
    void Show(ShellIcon icon, Func<ShellMenu> menu, Func<string, Task> invoked);

    /// <summary>Removes the icon. Any thread.</summary>
    void Remove();

    /// <summary>Asks <paramref name="prompt"/> and answers the index of the button chosen. Any thread.</summary>
    Task<int> ConfirmAsync(ShellPrompt prompt);

    /// <summary>The system asks the app to quit (logout, shutdown, the Dock or Activity Monitor): stop without asking.</summary>
    event Action? TerminateRequested;
}

/// <summary>No menu-bar item: <c>--no-tray</c>, off the main thread (every test host), or an OS without one yet.</summary>
public sealed class NullShellPlatform : IShellPlatform
{
    public bool IsSupported => false;

    public event Action? TerminateRequested
    {
        add { }
        remove { }
    }

    public void Show(ShellIcon icon, Func<ShellMenu> menu, Func<string, Task> invoked)
    {
    }

    public void Remove()
    {
    }

    public Task<int> ConfirmAsync(ShellPrompt prompt) => Task.FromResult(prompt.CancelButton);
}
