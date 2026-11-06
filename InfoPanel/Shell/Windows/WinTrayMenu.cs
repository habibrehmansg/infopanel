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

    /// <summary>A submenu (<c>MF_POPUP</c>): its lines are the entry's <see cref="WinMenuEntry.Children"/>.</summary>
    Submenu = 16,
}

/// <summary>
/// One line of the Win32 menu: <paramref name="Command"/> is what <c>TrackPopupMenuEx</c> returns for it (0 for a
/// separator or a submenu, which Win32 never returns), <paramref name="Id"/> the model's item id (or the platform's own),
/// <paramref name="Label"/> the text as Win32 takes it (an ampersand doubled, so it shows as one).
/// </summary>
internal sealed record WinMenuEntry(uint Command, string Id, string Label, WinMenuFlags Flags)
{
    /// <summary>A submenu's lines, numbered with the rest of the menu; empty for any other line.</summary>
    public IReadOnlyList<WinMenuEntry> Children { get; init; } = [];
}

/// <summary>
/// The model's menu (<see cref="ShellMenu.Build"/>) as the Win32 menu's lines, built each time the menu opens: in
/// Windows' words (<see cref="WinShellText"/>), the header and the status line one grey line ("InfoPanel · Running ·
/// 2.0.0": a Win32 menu has no header), Open editor the default item, a submenu of the model's (Show on desktop)
/// as a submenu with its check marks, and the platform's Start when I sign in in a group of its own above Exit. Commands
/// are numbered across the whole menu, the submenus' lines included, so the one <c>TrackPopupMenuEx</c> returns names
/// one line.
/// </summary>
internal static class WinTrayMenu
{
    /// <summary>The id of Start when I sign in: the platform's own item, not the model's.</summary>
    public const string StartAtSignInId = "windows.start-at-sign-in";

    /// <summary>
    /// The lines for <paramref name="menu"/>, with Start when I sign in ticked when <paramref name="startAtSignIn"/>
    /// (greyed when it couldn't be read: null).
    /// </summary>
    public static IReadOnlyList<WinMenuEntry> Lines(ShellMenu menu, bool? startAtSignIn)
    {
        var items = WithTheHeaderInTheStatus(menu.Items);
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

    /// <summary>The line chosen: the one with <paramref name="command"/>, in <paramref name="entries"/> or a submenu; null for none.</summary>
    public static WinMenuEntry? Find(IReadOnlyList<WinMenuEntry> entries, uint command)
    {
        if (command == 0)
            return null;
        foreach (var entry in entries)
        {
            if (entry.Command == command)
                return entry;
            if (Find(entry.Children, command) is { } child)
                return child;
        }

        return null;
    }

    /// <summary>
    /// <paramref name="items"/> with the header put into the status line after it ("InfoPanel · Running · 2.0.0"); a header
    /// without one stays a grey line of its own.
    /// </summary>
    private static List<ShellMenuItem> WithTheHeaderInTheStatus(IReadOnlyList<ShellMenuItem> items)
    {
        var lines = new List<ShellMenuItem>();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Kind == ShellItemKind.Header && i + 1 < items.Count && items[i + 1].Kind == ShellItemKind.Status)
            {
                lines.Add(items[i + 1] with { Label = $"{items[i].Label} · {items[i + 1].Label}" });
                i++;
            }
            else
            {
                lines.Add(items[i]);
            }
        }

        return lines;
    }

    private static WinMenuEntry Entry(ShellMenuItem item, ref uint command)
    {
        if (item.Kind == ShellItemKind.Separator)
            return Separator();
        var label = Escape(WinShellText.Label(item));
        if (item.Kind == ShellItemKind.Submenu)
        {
            var children = new List<WinMenuEntry>();
            foreach (var child in item.Children)
                children.Add(Entry(child, ref command));
            return new WinMenuEntry(0, item.Id, label, WinMenuFlags.Submenu) { Children = children };
        }

        var flags = item.Enabled ? WinMenuFlags.None : WinMenuFlags.Disabled;
        if (item.Checked)
            flags |= WinMenuFlags.Checked;
        if (item.Id == ShellMenu.OpenEditor && item.Enabled)
            flags |= WinMenuFlags.Default;
        return new WinMenuEntry(++command, item.Id, label, flags);
    }

    private static WinMenuEntry Separator() => new(0, "", "", WinMenuFlags.Separator);

    /// <summary>Win32 reads <c>&amp;</c> as the next letter's access key; doubled, it shows as itself.</summary>
    private static string Escape(string label) => label.Replace("&", "&&", StringComparison.Ordinal);
}
