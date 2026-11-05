namespace InfoPanel.Shell;

/// <summary>
/// What a menu line is: an action the user can choose, a line that only says something, a separator, or a submenu
/// (<see cref="ShellMenuItem.Children"/>).
/// </summary>
public enum ShellItemKind
{
    Action,
    Status,
    Separator,
    Submenu,
}

/// <summary>
/// One line of the shell's menu: <paramref name="Id"/> is what a click reports (stable, so a platform can map its own
/// words to it), <paramref name="KeyEquivalent"/> the key that chooses it with Command while the menu is open (macOS).
/// <see cref="Checked"/> draws a check mark beside it; a <see cref="ShellItemKind.Submenu"/> holds its lines in
/// <see cref="Children"/>. Two items are equal when every member is, their children's lines included.
/// </summary>
public sealed record ShellMenuItem(string Id, string Label, ShellItemKind Kind = ShellItemKind.Action, string? KeyEquivalent = null)
{
    public bool Enabled => Kind is ShellItemKind.Action or ShellItemKind.Submenu;

    /// <summary>A check mark beside the line (Show on Desktop's profiles that are shown).</summary>
    public bool Checked { get; init; }

    /// <summary>A submenu's lines; empty for any other kind.</summary>
    public IReadOnlyList<ShellMenuItem> Children { get; init; } = [];

    public bool Equals(ShellMenuItem? other) =>
        other is not null && Id == other.Id && Label == other.Label && Kind == other.Kind && KeyEquivalent == other.KeyEquivalent
        && Checked == other.Checked && Children.SequenceEqual(other.Children);

    public override int GetHashCode() => HashCode.Combine(Id, Label, Kind, KeyEquivalent, Checked, Children.Count);
}

/// <summary>A profile as Show on Desktop lists it: its id, its name, and whether its desktop window is on.</summary>
public sealed record ShellProfile(string Id, string Name, bool Shown);

/// <summary>
/// What the menu is built from: the engine's state when the user opens it. <see cref="HasCredential"/>: the engine holds
/// a credential for the configured service, and the service hasn't refused it; <see cref="SignedInAs"/>: its holder's
/// name (else email), as the service last gave it for that same credential, ready to show (<c>ServiceHolder</c>), or null;
/// <see cref="Profiles"/>: the profiles that can be opened, in the library's order; <see cref="DesktopWindows"/>: this
/// engine shows desktop windows (not <c>--no-windows</c>, and a platform that has them).
/// </summary>
public sealed record ShellState(string Version)
{
    public bool HasCredential { get; init; }

    public string? SignedInAs { get; init; }

    public IReadOnlyList<ShellProfile> Profiles { get; init; } = [];

    public bool DesktopWindows { get; init; }
}

/// <summary>
/// The shell's menu, built afresh from <see cref="ShellState"/> each time the user opens it (no live copy to keep in sync).
/// The labels are macOS's (title case, Apple's guidelines); every line has a stable id, so another platform maps its own
/// words to the same lines.
/// </summary>
public sealed record ShellMenu(IReadOnlyList<ShellMenuItem> Items)
{
    public const string Status = "status";
    public const string SignedIn = "signed-in";
    public const string OpenEditor = "open-editor";
    public const string ShowOnDesktop = "show-on-desktop";
    public const string NoProfiles = "no-profiles";
    public const string MoreProfiles = "more-profiles";
    public const string ShowDataFolder = "show-data-folder";
    public const string OpenLogsFolder = "open-logs-folder";
    public const string CopyEngineDetails = "copy-engine-details";
    public const string Quit = "quit";

    /// <summary>The most profiles Show on Desktop lists; past it, "More in the Editor…" opens the editor.</summary>
    public const int DesktopProfiles = 20;

    private const string DesktopPrefix = "desktop:";

    /// <summary>
    /// "InfoPanel {version} is running"; the signed-in line ("Signed in as {name}" while a credential is held and its name is
    /// known, "Not signed in" without one or when the service refused it, none while the name isn't known); Open Editor; Show on Desktop with each profile
    /// (when desktop windows are on); Show Data Folder in Finder, Open Logs Folder, Copy Engine Details; Quit InfoPanel (⌘Q).
    /// </summary>
    public static ShellMenu Build(ShellState state)
    {
        var items = new List<ShellMenuItem> { new(Status, $"InfoPanel {state.Version} is running", ShellItemKind.Status) };
        if (!state.HasCredential)
            items.Add(new(SignedIn, "Not signed in", ShellItemKind.Status));
        else if (state.SignedInAs is { } name)
            items.Add(new(SignedIn, $"Signed in as {name}", ShellItemKind.Status));
        items.Add(new(OpenEditor, "Open Editor"));
        items.Add(Separator());
        if (state.DesktopWindows)
        {
            items.Add(new(ShowOnDesktop, "Show on Desktop", ShellItemKind.Submenu) { Children = DesktopLines(state.Profiles) });
            items.Add(Separator());
        }

        items.Add(new(ShowDataFolder, "Show Data Folder in Finder"));
        items.Add(new(OpenLogsFolder, "Open Logs Folder"));
        items.Add(new(CopyEngineDetails, "Copy Engine Details"));
        items.Add(Separator());
        items.Add(new(Quit, "Quit InfoPanel", KeyEquivalent: "q"));
        return new ShellMenu(items);
    }

    /// <summary>
    /// The id of a profile's line in Show on Desktop: what choosing it asks for, the window shown (<paramref name="show"/>)
    /// or hidden, so a choice made on a menu that has gone stale never undoes what it showed.
    /// </summary>
    public static string DesktopItem(string profileId, bool show) => $"{DesktopPrefix}{(show ? "show" : "hide")}:{profileId}";

    /// <summary>Reads a line's id made by <see cref="DesktopItem"/>; false for any other id.</summary>
    public static bool TryReadDesktopItem(string id, out string profileId, out bool show)
    {
        profileId = "";
        show = false;
        if (!id.StartsWith(DesktopPrefix, StringComparison.Ordinal))
            return false;
        var rest = id[DesktopPrefix.Length..];
        var colon = rest.IndexOf(':');
        if (colon < 0 || colon == rest.Length - 1 || rest[..colon] is not ("show" or "hide"))
            return false;
        show = rest[..colon] == "show";
        profileId = rest[(colon + 1)..];
        return true;
    }

    public bool Equals(ShellMenu? other) => other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => Items.Count;

    private static ShellMenuItem Separator() => new("", "", ShellItemKind.Separator);

    private static List<ShellMenuItem> DesktopLines(IReadOnlyList<ShellProfile> profiles)
    {
        if (profiles.Count == 0)
            return [new(NoProfiles, "No Profiles Yet", ShellItemKind.Status)];

        var lines = profiles.Take(DesktopProfiles)
            .Select(p => new ShellMenuItem(DesktopItem(p.Id, !p.Shown), p.Name) { Checked = p.Shown })
            .ToList();
        if (profiles.Count > DesktopProfiles)
        {
            lines.Add(Separator());
            lines.Add(new(MoreProfiles, "More in the Editor…"));
        }

        return lines;
    }
}
