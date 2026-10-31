namespace InfoPanel.Shell;

/// <summary>What a menu line is: an action the user can choose, a line that only says something, or a separator.</summary>
public enum ShellItemKind
{
    Action,
    Status,
    Separator,
}

/// <summary>
/// One line of the shell's menu: <paramref name="Id"/> is what a click reports, <paramref name="KeyEquivalent"/> the key
/// that chooses it with Command while the menu is open (macOS).
/// </summary>
public sealed record ShellMenuItem(string Id, string Label, ShellItemKind Kind = ShellItemKind.Action, string? KeyEquivalent = null)
{
    public bool Enabled => Kind == ShellItemKind.Action;
}

/// <summary>What the menu is built from: the engine's state when the user opens it.</summary>
public sealed record ShellState(string Version);

/// <summary>The shell's menu, built afresh from <see cref="ShellState"/> each time the user opens it (no live copy to keep in sync).</summary>
public sealed record ShellMenu(IReadOnlyList<ShellMenuItem> Items)
{
    public const string Status = "status";
    public const string OpenEditor = "open-editor";
    public const string Quit = "quit";

    /// <summary>"InfoPanel {version} is running", Open Editor, a separator, Quit InfoPanel (⌘Q).</summary>
    public static ShellMenu Build(ShellState state) => new(
    [
        new(Status, $"InfoPanel {state.Version} is running", ShellItemKind.Status),
        new(OpenEditor, "Open Editor"),
        new("", "", ShellItemKind.Separator),
        new(Quit, "Quit InfoPanel", KeyEquivalent: "q"),
    ]);
}
