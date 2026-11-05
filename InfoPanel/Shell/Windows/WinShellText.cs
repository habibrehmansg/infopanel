namespace InfoPanel.Shell.Windows;

/// <summary>One button of a prompt as Windows shows it: <paramref name="Answer"/> is its index in the model's prompt.</summary>
internal sealed record WinPromptButton(int Answer, string Label);

/// <summary>A <see cref="ShellPrompt"/> in Windows' words and button order; answers are the model's button indexes.</summary>
internal sealed record WinPrompt(string Title, string Text, IReadOnlyList<WinPromptButton> Buttons, int DefaultAnswer, int CancelAnswer);

/// <summary>
/// The shell's words on Windows (docs/2.0/desktop-shell.md, "Windows"): the model speaks macOS's title case; Windows
/// menus and dialogs use sentence case and say Exit for Quit. Items and words this map doesn't know keep the model's.
/// </summary>
internal static class WinShellText
{
    /// <summary>The item the Windows platform adds above Exit.</summary>
    public const string StartAtSignIn = "Start when I sign in";

    /// <summary>
    /// The menu's words by item id. The status line, the signed-in line and a profile's line (its name) are already the
    /// same on Windows, so they keep the model's.
    /// </summary>
    private static readonly Dictionary<string, string> MenuLabels = new()
    {
        [ShellMenu.OpenEditor] = "Open editor",
        [ShellMenu.ShowOnDesktop] = "Show on desktop",
        [ShellMenu.NoProfiles] = "No profiles yet",
        [ShellMenu.MoreProfiles] = "More in the editor…",
        [ShellMenu.ShowDataFolder] = "Open data folder",
        [ShellMenu.OpenLogsFolder] = "Open logs folder",
        [ShellMenu.CopyEngineDetails] = "Copy engine details",
        [ShellMenu.Quit] = "Exit",
    };

    /// <summary>A prompt's title and buttons, by the model's words.</summary>
    private static readonly Dictionary<string, string> PromptWords = new()
    {
        ["Quit InfoPanel?"] = "Exit InfoPanel?",
        ["Open Editor"] = "Open editor",
        ["Quit Without Saving"] = "Exit without saving",
    };

    /// <summary>The icon's tooltip: "InfoPanel 2.0.0".</summary>
    public static string Tooltip(string version) => $"InfoPanel {version}";

    /// <summary>An item's label on Windows.</summary>
    public static string Label(ShellMenuItem item) => MenuLabels.TryGetValue(item.Id, out var label) ? label : item.Label;

    /// <summary><paramref name="prompt"/> in Windows' words, its cancel button last (Windows puts Cancel at the end).</summary>
    public static WinPrompt Prompt(ShellPrompt prompt)
    {
        var buttons = prompt.Buttons.Select((label, i) => new WinPromptButton(i, Words(label)))
            .OrderBy(b => b.Answer == prompt.CancelButton)
            .ToList();
        return new WinPrompt(Words(prompt.Title), prompt.Text, buttons, prompt.DefaultButton, prompt.CancelButton);
    }

    private static string Words(string text) => PromptWords.TryGetValue(text, out var words) ? words : text;
}
