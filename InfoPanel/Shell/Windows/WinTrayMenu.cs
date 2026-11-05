namespace InfoPanel.Shell.Windows;

/// <summary>How a Win32 menu line is drawn.</summary>
[Flags]
internal enum WinMenuFlags
{
    None = 0,
    Separator = 1,
    Disabled = 2,
    Checked = 4,

    /// <summary>The default item, drawn bold: what a left click on the icon does.</summary>
    Default = 8,
}

/// <summary>
/// One line of the Win32 menu: <paramref name="Command"/> is what <c>TrackPopupMenuEx</c> returns for it (0 for a
/// separator), <paramref name="Id"/> the model's item id (or the platform's own), <paramref name="Label"/> the text as
/// Win32 takes it (an ampersand doubled, so it shows as one).
/// </summary>
internal sealed record WinMenuEntry(uint Command, string Id, string Label, WinMenuFlags Flags);

/// <summary>
/// The model's menu (<see cref="ShellMenu.Build"/>) as the Win32 menu's lines, built each time the menu opens: in
/// Windows' words (<see cref="WinShellText"/>), Open editor the default item, and the platform's Start when I sign in
/// in a group of its own above Exit.
/// </summary>
internal static class WinTrayMenu
{
    /// <summary>The id of Start when I sign in: the platform's own item, not the model's.</summary>
    public const string StartAtSignInId = "windows.start-at-sign-in";

    /// <summary>
    /// The lines for <paramref name="menu"/>, with Start when I sign in ticked when <paramref name="startAtSignIn"/>
    /// (greyed when it couldn't be read: null).
    /// </summary>
    public static IReadOnlyList<WinMenuEntry> Flatten(ShellMenu menu, bool? startAtSignIn)
    {
        var items = menu.Items.ToList();
        var exit = items.FindIndex(i => i.Id == ShellMenu.Quit && i.Kind != ShellItemKind.Separator);
        var head = exit < 0 ? items : items[..exit];
        var tail = exit < 0 ? [] : items[exit..];
        while (head.Count > 0 && head[^1].Kind == ShellItemKind.Separator)
            head = head[..^1];

        var entries = new List<WinMenuEntry>();
        uint command = 0;
        foreach (var item in head)
            entries.Add(Entry(item, ref command));

        if (entries.Count > 0)
            entries.Add(Separator());
        var startFlags = startAtSignIn switch
        {
            true => WinMenuFlags.Checked,
            false => WinMenuFlags.None,
            null => WinMenuFlags.Disabled,
        };
        entries.Add(new WinMenuEntry(++command, StartAtSignInId, Escape(WinShellText.StartAtSignIn), startFlags));
        if (tail.Count > 0)
            entries.Add(Separator());

        foreach (var item in tail)
            entries.Add(Entry(item, ref command));
        return entries;
    }

    private static WinMenuEntry Entry(ShellMenuItem item, ref uint command)
    {
        if (item.Kind == ShellItemKind.Separator)
            return Separator();
        var flags = item.Enabled ? WinMenuFlags.None : WinMenuFlags.Disabled;
        if (item.Id == ShellMenu.OpenEditor && item.Enabled)
            flags |= WinMenuFlags.Default;
        return new WinMenuEntry(++command, item.Id, Escape(WinShellText.Label(item)), flags);
    }

    private static WinMenuEntry Separator() => new(0, "", "", WinMenuFlags.Separator);

    /// <summary>Win32 reads <c>&amp;</c> as the next letter's access key; doubled, it shows as itself.</summary>
    private static string Escape(string label) => label.Replace("&", "&&", StringComparison.Ordinal);
}
