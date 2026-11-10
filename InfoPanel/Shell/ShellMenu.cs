namespace InfoPanel.Shell;

/// <summary>
/// What a menu line is: an action the user can choose, a line that only says something, a separator, a submenu
/// (<see cref="ShellMenuItem.Children"/>), or the menu's header (the app's name, at its top).
/// </summary>
public enum ShellItemKind
{
    Action,
    Status,
    Separator,
    Submenu,
    Header,
}

/// <summary>
/// One line of the shell's menu: <paramref name="Id"/> is what a click reports (stable, so a platform can map its own
/// words to it), <paramref name="KeyEquivalent"/> the key that chooses it with Command while the menu is open (macOS).
/// <see cref="Checked"/> draws a check mark beside it; <see cref="Detail"/> is a second line beneath its label, and
/// <see cref="Initial"/> the letter of a round badge before it (macOS draws both; Windows puts the detail on the same
/// line); a <see cref="ShellItemKind.Submenu"/> holds its lines in <see cref="Children"/>. Two items are equal when every
/// member is, their children's lines included.
/// </summary>
public sealed record ShellMenuItem(string Id, string Label, ShellItemKind Kind = ShellItemKind.Action, string? KeyEquivalent = null)
{
    public bool Enabled => Kind is ShellItemKind.Action or ShellItemKind.Submenu;

    /// <summary>A check mark beside the line (Show on Desktop's profiles that are shown, Click-Through's windows that let clicks through).</summary>
    public bool Checked { get; init; }

    /// <summary>
    /// A second line beneath the label (who is signed in: the email, in the secondary colour; or Sign In…, in the label
    /// colour, <see cref="DetailIsAction"/>).
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The second line is what choosing the line does (Sign In…): macOS draws it in the label colour and the label above
    /// it in the secondary one, so the row doesn't read as disabled.
    /// </summary>
    public bool DetailIsAction { get; init; }

    /// <summary>The letter a round badge before the line shows (who is signed in); null for none.</summary>
    public string? Initial { get; init; }

    /// <summary>A submenu's lines; empty for any other kind.</summary>
    public IReadOnlyList<ShellMenuItem> Children { get; init; } = [];

    public bool Equals(ShellMenuItem? other) =>
        other is not null && Id == other.Id && Label == other.Label && Kind == other.Kind && KeyEquivalent == other.KeyEquivalent
        && Checked == other.Checked && Detail == other.Detail && DetailIsAction == other.DetailIsAction && Initial == other.Initial && Children.SequenceEqual(other.Children);

    public override int GetHashCode() => HashCode.Combine(Id, Label, Kind, KeyEquivalent, Checked, Detail, Initial, Children.Count);
}

/// <summary>
/// A profile as Show on Desktop and Click-Through list it: its id, its name, whether its desktop window is on, and whether
/// clicks pass through that window (<c>window.clickThrough</c>).
/// </summary>
public sealed record ShellProfile(string Id, string Name, bool Shown, bool ClickThrough = false);

/// <summary>
/// What the menu is built from: the engine's state when the user opens it. <see cref="HasCredential"/>: the engine holds
/// a credential for the configured service, and the service hasn't refused it; <see cref="SignedInAs"/>: its holder's
/// name (else email), as the service last gave it for that same credential, ready to show (<c>ServiceHolder</c>), or null;
/// <see cref="SignedInEmail"/>: the holder's email shown beneath that name, or null (no name, or none known);
/// <see cref="Profiles"/>: the profiles that can be opened, in the library's order; <see cref="DesktopWindows"/>: this
/// engine shows desktop windows (not <c>--no-windows</c>, and a platform that has them); <see cref="Update"/>: a newer
/// version the update check found, and its release page; <see cref="ChecksForUpdates"/>: this engine checks for updates
/// (not a development build).
/// </summary>
public sealed record ShellState(string Version)
{
    public (string Version, string Url)? Update { get; init; }

    public bool ChecksForUpdates { get; init; }

    public bool HasCredential { get; init; }

    public string? SignedInAs { get; init; }

    public string? SignedInEmail { get; init; }

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
    public const string Header = "header";
    public const string Status = "status";
    public const string UpdateAvailable = "update-available";
    public const string CheckForUpdates = "check-for-updates";
    public const string SignedIn = "signed-in";
    public const string OpenEditor = "open-editor";
    public const string EditFromAnotherComputer = "edit-from-another-computer";
    public const string ShowOnDesktop = "show-on-desktop";
    public const string NoProfiles = "no-profiles";
    public const string MoreProfiles = "more-profiles";
    public const string ClickThrough = "click-through";
    public const string NoWindowsShown = "no-windows-shown";
    public const string ShowDataFolder = "show-data-folder";
    public const string OpenLogsFolder = "open-logs-folder";
    public const string CopyEngineDetails = "copy-engine-details";
    public const string Quit = "quit";

    /// <summary>The most characters of a version the update line shows; past it, its first and last ones around "…".</summary>
    public const int UpdateVersionLength = 32;

    /// <summary>The most profiles Show on Desktop lists; past it, "More in the Editor…" opens the editor.</summary>
    public const int DesktopProfiles = 20;

    private const string DesktopPrefix = "desktop:";
    private const string ClickThroughPrefix = "click-through:";

    /// <summary>Who is signed in, without a credential (or with one the service refused): the second line beneath "Not signed in".</summary>
    public const string SignIn = "Sign In…";

    /// <summary>
    /// The header "InfoPanel" and "Running · {version}"; beneath it "Update Available: InfoPanel {version}…" while the
    /// update check knows a newer version (its version shortened in the middle past <see cref="UpdateVersionLength"/>
    /// characters), else "Check for Updates…" when this engine checks for updates; who is signed in, one line that opens the editor at its sign-in
    /// settings: the name with the email beneath and the name's initial while a credential is held and its holder known
    /// (the email alone without a name), "Not signed in" with "Sign In…" beneath without one or when the service refused it,
    /// none while the holder isn't known; Open Editor; Edit from Another Computer… (the editor at the same sign-in settings,
    /// where another computer is set up to edit this one); Show on Desktop with each profile, then Click-Through with each shown window (when desktop windows are on);
    /// Show Data Folder in Finder, Open Logs Folder, Copy Engine Details; Quit InfoPanel (⌘Q).
    /// </summary>
    public static ShellMenu Build(ShellState state)
    {
        var items = new List<ShellMenuItem>
        {
            new(Header, "InfoPanel", ShellItemKind.Header),
            new(Status, $"Running · {state.Version}", ShellItemKind.Status),
        };
        if (state.Update is { } update)
            items.Add(new(UpdateAvailable, $"Update Available: InfoPanel {MiddleShortened(update.Version, UpdateVersionLength)}…"));
        else if (state.ChecksForUpdates)
            items.Add(new(CheckForUpdates, "Check for Updates…"));
        items.Add(Separator());
        if (!state.HasCredential)
            items.Add(new(SignedIn, "Not signed in") { Detail = SignIn, DetailIsAction = true });
        else if (state.SignedInAs is { } name)
            items.Add(new(SignedIn, name) { Detail = state.SignedInEmail, Initial = InitialOf(name) });
        if (items[^1].Id == SignedIn)
            items.Add(Separator());
        items.Add(new(OpenEditor, "Open Editor"));
        items.Add(new(EditFromAnotherComputer, "Edit from Another Computer…"));
        items.Add(Separator());
        if (state.DesktopWindows)
        {
            items.Add(new(ShowOnDesktop, "Show on Desktop", ShellItemKind.Submenu) { Children = DesktopLines(state.Profiles) });
            items.Add(new(ClickThrough, "Click-Through", ShellItemKind.Submenu) { Children = ClickThroughLines(state.Profiles) });
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

    /// <summary>
    /// The id of a shown window's line in Click-Through: what choosing it asks for, clicks passing through
    /// (<paramref name="on"/>) or taken again, so a choice made on a menu that has gone stale never undoes itself.
    /// </summary>
    public static string ClickThroughItem(string profileId, bool on) => $"{ClickThroughPrefix}{(on ? "on" : "off")}:{profileId}";

    /// <summary>Reads a line's id made by <see cref="ClickThroughItem"/>; false for any other id.</summary>
    public static bool TryReadClickThroughItem(string id, out string profileId, out bool on)
    {
        profileId = "";
        on = false;
        if (!id.StartsWith(ClickThroughPrefix, StringComparison.Ordinal))
            return false;
        var rest = id[ClickThroughPrefix.Length..];
        var colon = rest.IndexOf(':');
        if (colon < 0 || colon == rest.Length - 1 || rest[..colon] is not ("on" or "off"))
            return false;
        on = rest[..colon] == "on";
        profileId = rest[(colon + 1)..];
        return true;
    }

    /// <summary>
    /// The letter of <paramref name="name"/>'s round badge: its first letter or digit (the whole character, an accent
    /// included) in capitals; null when it has none.
    /// </summary>
    public static string? InitialOf(string name)
    {
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(name);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (char.IsLetterOrDigit(element, 0))
                return element.ToUpperInvariant();
        }

        return null;
    }

    /// <summary><paramref name="text"/>, past <paramref name="length"/> characters its first ones, "…" and its last ones.</summary>
    internal static string MiddleShortened(string text, int length)
    {
        if (text.Length <= length)
            return text;
        var head = (length - 1) / 2;
        return text[..head] + "…" + text[^(length - 1 - head)..];
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

    /// <summary>
    /// Click-Through's lines: each profile Show on Desktop lists whose window is on, in its order and by its name, checked
    /// while clicks pass through it; "No Windows Shown" (greyed) when none is.
    /// </summary>
    private static List<ShellMenuItem> ClickThroughLines(IReadOnlyList<ShellProfile> profiles)
    {
        var lines = profiles.Take(DesktopProfiles)
            .Where(p => p.Shown)
            .Select(p => new ShellMenuItem(ClickThroughItem(p.Id, !p.ClickThrough), p.Name) { Checked = p.ClickThrough })
            .ToList();
        return lines.Count > 0 ? lines : [new(NoWindowsShown, "No Windows Shown", ShellItemKind.Status)];
    }
}
