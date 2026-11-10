using InfoPanel.Shell;
using InfoPanel.Shell.Windows;
using Xunit;

namespace InfoPanel.Tests.Shell.Windows;

/// <summary>
/// The notification-area icon's logic on any OS, through <see cref="FakeTrayNative"/> (the Win32 side) and
/// <see cref="FakeRegistry"/> (the Run value): the menu in Windows' words with its ids, the default item, Start when I
/// sign in, a left click, a right click, Explorer's restart (<c>TaskbarCreated</c>), Exit's
/// prompt, and the icon's removal.
/// </summary>
public sealed class WinTrayTests
{
    private const uint TaskbarCreated = 0xC0DE;
    private const string Command = "\"C:\\Program Files\\InfoPanel\\InfoPanel.exe\"";

    private readonly FakeTrayNative _native = new();
    private readonly FakeRegistry _registry = new();
    private readonly List<string> _invoked = [];
    private long _now = 10_000;
    private int _menusBuilt;

    private WinTray Tray() => new(_native, new WinStartup(_registry, Command), TaskbarCreated, () => _now);

    private WinTray Shown(string version = "2.0.0", ShellState? state = null)
    {
        var tray = Tray();
        tray.Show(WinShellText.Tooltip(version), () =>
        {
            _menusBuilt++;
            return ShellMenu.Build(state ?? new ShellState(version));
        }, id =>
        {
            _invoked.Add(id);
            return Task.CompletedTask;
        });
        return tray;
    }

    /// <summary>The callback message's <c>lParam</c> (version 4): the event in the low word, the icon's id in the high one.</summary>
    private static nint Event(uint e) => (nint)((1u << 16) | e);

    /// <summary>The callback message's <c>wParam</c> (version 4): the anchor point, x in the low word, y in the high one.</summary>
    private static nint Anchor(int x, int y) => (nint)(((uint)(ushort)(short)y << 16) | (ushort)(short)x);

    private static string[] Labels(IReadOnlyList<WinMenuEntry> entries) => [.. entries.Select(e => e.Label)];

    /// <summary>Every line, the submenus' included, depth first.</summary>
    private static IEnumerable<WinMenuEntry> All(IReadOnlyList<WinMenuEntry> entries) => entries.SelectMany(e => new[] { e }.Concat(All(e.Children)));

    /// <summary>
    /// The engine's state with every line the model has: signed in, desktop windows on, two profiles (the second shown, and
    /// with <paramref name="clickThrough"/> its clicks passing through).
    /// </summary>
    private static ShellState Full(int profiles = 2, bool clickThrough = false) => new("2.0.0")
    {
        HasCredential = true,
        SignedInAs = "Ada Lovelace",
        SignedInEmail = "ada@example.com",
        DesktopWindows = true,
        Profiles = [.. Enumerable.Range(1, profiles).Select(i => new ShellProfile($"p{i}", i == 2 ? "Fans & pumps" : $"Profile {i}", Shown: i == 2, ClickThrough: clickThrough && i == 2))],
    };

    [Fact]
    public void TheMenu_IsInWindowsWords_WithStartWhenISignIn_AboveExit()
    {
        var entries = WinTrayMenu.Lines(ShellMenu.Build(Full(clickThrough: true)), startAtSignIn: false);

        Assert.Equal(
        [
            "InfoPanel · Running · 2.0.0", "",
            "Ada Lovelace (ada@example.com)", "",
            "Open editor", "Edit from another computer…", "",
            "Show on desktop", "Click-through", "",
            "Open data folder", "Open logs folder", "Copy engine details", "",
            "Start when I sign in", "",
            "Exit",
        ], Labels(entries));
        Assert.Equal(
        [
            WinMenuFlags.Disabled, WinMenuFlags.Separator,
            WinMenuFlags.None, WinMenuFlags.Separator,
            WinMenuFlags.Default, WinMenuFlags.None, WinMenuFlags.Separator,
            WinMenuFlags.Submenu, WinMenuFlags.Submenu, WinMenuFlags.Separator,
            WinMenuFlags.None, WinMenuFlags.None, WinMenuFlags.None, WinMenuFlags.Separator,
            WinMenuFlags.None, WinMenuFlags.Separator,
            WinMenuFlags.None,
        ], entries.Select(e => e.Flags));
        Assert.Equal(
        [
            ShellMenu.Status, "",
            ShellMenu.SignedIn, "",
            ShellMenu.OpenEditor, ShellMenu.EditFromAnotherComputer, "",
            ShellMenu.ShowOnDesktop, ShellMenu.ClickThrough, "",
            ShellMenu.ShowDataFolder, ShellMenu.OpenLogsFolder, ShellMenu.CopyEngineDetails, "",
            WinTrayMenu.StartAtSignInId, "",
            ShellMenu.Quit,
        ], entries.Select(e => e.Id));

        var desktop = entries[7].Children;
        Assert.Equal(["Profile 1", "Fans && pumps"], Labels(desktop));
        Assert.Equal([WinMenuFlags.None, WinMenuFlags.Checked], desktop.Select(e => e.Flags));
        Assert.Equal([ShellMenu.DesktopItem("p1", show: true), ShellMenu.DesktopItem("p2", show: false)], desktop.Select(e => e.Id));

        // Click-Through: only the shown window, checked while its clicks pass through; choosing it lets it take clicks again.
        var through = entries[8].Children;
        Assert.Equal(["Fans && pumps"], Labels(through));
        Assert.Equal([WinMenuFlags.Checked], through.Select(e => e.Flags));
        Assert.Equal([ShellMenu.ClickThroughItem("p2", on: false)], through.Select(e => e.Id));
        Assert.All(entries.Where(e => e.Id is not (ShellMenu.ShowOnDesktop or ShellMenu.ClickThrough)), e => Assert.Empty(e.Children));
    }

    [Fact]
    public void WhoIsSignedIn_IsOneClickableLine_TheNameAndEmail_TheEmailAlone_OrNotSignedIn_AndIsAbsent_WhileTheNameIsUnknown()
    {
        WinMenuEntry Row(ShellState state) => WinTrayMenu.Lines(ShellMenu.Build(state), startAtSignIn: false).Single(e => e.Id == ShellMenu.SignedIn);

        Assert.Equal(("Ada Lovelace (ada@example.com)", WinMenuFlags.None), (Row(Full()).Label, Row(Full()).Flags));
        var emailAlone = Row(new ShellState("2.0.0") { HasCredential = true, SignedInAs = "ada@example.com" });
        Assert.Equal(("ada@example.com", WinMenuFlags.None), (emailAlone.Label, emailAlone.Flags));
        var signedOut = Row(new ShellState("2.0.0"));
        Assert.Equal(("Not signed in · Sign in…", WinMenuFlags.None), (signedOut.Label, signedOut.Flags));
        Assert.Equal("Fans && Co (f&&c@example.com)", Row(new ShellState("2.0.0") { HasCredential = true, SignedInAs = "Fans & Co", SignedInEmail = "f&c@example.com" }).Label);

        var unknown = WinTrayMenu.Lines(ShellMenu.Build(new ShellState("2.0.0") { HasCredential = true }), startAtSignIn: false);
        Assert.Equal(["InfoPanel · Running · 2.0.0", "", "Open editor", "Edit from another computer…", "", "Open data folder", "Open logs folder", "Copy engine details", "", "Start when I sign in", "", "Exit"], Labels(unknown));
    }

    [Fact]
    public void ShowOnDesktop_WithNoProfiles_SaysNoProfilesYet_Greyed()
    {
        var entries = WinTrayMenu.Lines(ShellMenu.Build(Full(profiles: 0)), startAtSignIn: false);

        var line = Assert.Single(entries.Single(e => e.Id == ShellMenu.ShowOnDesktop).Children);
        Assert.Equal((ShellMenu.NoProfiles, "No profiles yet", WinMenuFlags.Disabled), (line.Id, line.Label, line.Flags));
    }

    [Fact]
    public void ClickThrough_WithNoWindowShown_SaysNoWindowsShown_Greyed()
    {
        var entries = WinTrayMenu.Lines(ShellMenu.Build(Full(profiles: 1)), startAtSignIn: false);

        var line = Assert.Single(entries.Single(e => e.Id == ShellMenu.ClickThrough).Children);
        Assert.Equal((ShellMenu.NoWindowsShown, "No windows shown", WinMenuFlags.Disabled), (line.Id, line.Label, line.Flags));
    }

    [Fact]
    public void ClickThrough_AWindowTakingClicks_IsUnchecked_AndAsksForClicksToPassThrough()
    {
        var entries = WinTrayMenu.Lines(ShellMenu.Build(Full()), startAtSignIn: false);

        var line = Assert.Single(entries.Single(e => e.Id == ShellMenu.ClickThrough).Children);
        Assert.Equal(("Fans && pumps", WinMenuFlags.None, ShellMenu.ClickThroughItem("p2", on: true)), (line.Label, line.Flags, line.Id));
    }

    [Fact]
    public void ShowOnDesktop_Past20_ListsTheFirst20_ThenMoreInTheEditor()
    {
        var entries = WinTrayMenu.Lines(ShellMenu.Build(Full(profiles: 21)), startAtSignIn: false);

        var desktop = entries.Single(e => e.Id == ShellMenu.ShowOnDesktop).Children;
        Assert.Equal(22, desktop.Count);
        Assert.Equal([.. Enumerable.Range(1, 20).Select(i => i == 2 ? ShellMenu.DesktopItem("p2", show: false) : ShellMenu.DesktopItem($"p{i}", show: true))], desktop.Take(20).Select(e => e.Id));
        Assert.Equal(WinMenuFlags.Separator, desktop[20].Flags);
        Assert.Equal((ShellMenu.MoreProfiles, "More in the editor…", WinMenuFlags.None), (desktop[21].Id, desktop[21].Label, desktop[21].Flags));
        Assert.Single(All(entries), e => e.Flags.HasFlag(WinMenuFlags.Checked) && e.Id != WinTrayMenu.StartAtSignInId);
    }

    [Fact]
    public void WithoutDesktopWindows_ThereIsNoShowOnDesktop()
    {
        var entries = WinTrayMenu.Lines(ShellMenu.Build(Full() with { DesktopWindows = false }), startAtSignIn: false);

        Assert.DoesNotContain(All(entries), e => e.Id is ShellMenu.ShowOnDesktop or ShellMenu.ClickThrough || e.Flags.HasFlag(WinMenuFlags.Submenu));
        Assert.Equal(["InfoPanel · Running · 2.0.0", "", "Ada Lovelace (ada@example.com)", "", "Open editor", "Edit from another computer…", "", "Open data folder", "Open logs folder", "Copy engine details", "", "Start when I sign in", "", "Exit"], Labels(entries));
    }

    [Fact]
    public void EveryLineTheModelNames_HasWindowsWords()
    {
        // The model's own lines (not a profile's name, nor who is signed in, above): only the ones already in sentence case
        // keep the model's words (the header and the status, made one line by the menu's lines).
        var same = new HashSet<string> { ShellMenu.Header, ShellMenu.Status };
        var model = new[]
            {
                Full(profiles: 21), Full(profiles: 0), new ShellState("2.0.0"), new ShellState("2.0.0") { ChecksForUpdates = true },
                new ShellState("2.0.0") { Update = ("2.0.1", "https://example.test") },
            }
            .SelectMany(s => ShellMenu.Build(s).Items)
            .SelectMany(i => new[] { i }.Concat(i.Children))
            .Where(i => i.Kind != ShellItemKind.Separator && i.Id != ShellMenu.SignedIn && !ShellMenu.TryReadDesktopItem(i.Id, out _, out _)
                && !ShellMenu.TryReadClickThroughItem(i.Id, out _, out _))
            .ToList();

        Assert.Equal(15, model.Select(i => i.Id).Distinct().Count());
        Assert.All(model.Where(i => !same.Contains(i.Id)), i => Assert.NotEqual(i.Label, WinShellText.Label(i)));
        Assert.All(model.Where(i => same.Contains(i.Id)), i => Assert.Equal(i.Label, WinShellText.Label(i)));
    }

    [Fact]
    public void TheUpdateLines_AreInSentenceCase_UnderTheStatus()
    {
        var check = WinTrayMenu.Lines(ShellMenu.Build(new ShellState("2.0.0") { ChecksForUpdates = true }), startAtSignIn: false);
        var update = WinTrayMenu.Lines(ShellMenu.Build(new ShellState("2.0.0") { Update = ("2.0.1", "https://example.test") }), startAtSignIn: false);

        Assert.Equal(["InfoPanel · Running · 2.0.0", "Check for updates…", ""], Labels(check).Take(3));
        Assert.Equal(["InfoPanel · Running · 2.0.0", "Update available: InfoPanel 2.0.1…", ""], Labels(update).Take(3));
        Assert.Equal(ShellMenu.UpdateAvailable, update[1].Id);
        Assert.NotEqual(0u, update[1].Command);
    }

    [Fact]
    public void TheUpdatePrompts_KeepTheirWords_WithOKOrLaterAsCancel()
    {
        var available = WinShellText.Prompt(ShellService.UpdatePrompt("2.0.1", "2.0.0"));
        var upToDate = WinShellText.Prompt(ShellService.UpToDatePrompt("2.0.0"));
        var failed = WinShellText.Prompt(ShellService.CheckFailedPrompt());

        Assert.Equal(("InfoPanel 2.0.1 is available", 0, 1), (available.Title, available.DefaultAnswer, available.CancelAnswer));
        Assert.Equal([(0, "Download"), (1, "Later")], available.Buttons.Select(b => (b.Answer, b.Label)));
        Assert.Equal(("You're up to date", "InfoPanel 2.0.0 is the newest version."), (upToDate.Title, upToDate.Text));
        Assert.Equal(("Couldn't check for updates", "Check your connection, then try again."), (failed.Title, failed.Text));
        Assert.Equal([(0, "OK")], failed.Buttons.Select(b => (b.Answer, b.Label)));
    }

    [Fact]
    public void OnlyOpenEditor_IsTheDefaultItem()
    {
        var entries = WinTrayMenu.Lines(ShellMenu.Build(Full()), startAtSignIn: true);

        var defaults = All(entries).Where(e => e.Flags.HasFlag(WinMenuFlags.Default)).ToList();
        Assert.Equal(ShellMenu.OpenEditor, Assert.Single(defaults).Id);
    }

    [Fact]
    public void EachItem_HasItsOwnCommand_TheSubmenusToo_AndSeparatorsAndSubmenusNone()
    {
        var entries = WinTrayMenu.Lines(ShellMenu.Build(Full(profiles: 21)), startAtSignIn: false);

        var all = All(entries).ToList();
        var items = all.Where(e => !e.Flags.HasFlag(WinMenuFlags.Separator) && !e.Flags.HasFlag(WinMenuFlags.Submenu)).ToList();
        Assert.Equal(9 + 21 + 1, items.Count); // the model's lines, Show on Desktop's 20 and More, Click-Through's one shown window
        Assert.All(items, e => Assert.NotEqual(0u, e.Command));
        Assert.Equal(items.Count, items.Select(e => e.Command).Distinct().Count());
        Assert.All(all.Where(e => e.Flags.HasFlag(WinMenuFlags.Separator) || e.Flags.HasFlag(WinMenuFlags.Submenu)), e => Assert.Equal(0u, e.Command));
    }

    [Fact]
    public void StartWhenISignIn_IsTicked_WhenOn_AndGreyed_WhenItCantBeRead()
    {
        var on = WinTrayMenu.Lines(ShellMenu.Build(new ShellState("2.0.0")), startAtSignIn: true).Single(e => e.Id == WinTrayMenu.StartAtSignInId);
        var unknown = WinTrayMenu.Lines(ShellMenu.Build(new ShellState("2.0.0")), startAtSignIn: null).Single(e => e.Id == WinTrayMenu.StartAtSignInId);

        Assert.Equal(WinMenuFlags.Checked, on.Flags);
        Assert.Equal(WinMenuFlags.Disabled, unknown.Flags);
    }

    [Fact]
    public void ItemsTheMapDoesntKnow_KeepTheModelsWords_AndAnAmpersandIsShownAsOne()
    {
        var menu = new ShellMenu(
        [
            new("show", "Show on Desktop"),
            new("profile", "Fans & pumps"),
            new("", "", ShellItemKind.Separator),
            new(ShellMenu.Quit, "Quit InfoPanel"),
        ]);

        var entries = WinTrayMenu.Lines(menu, startAtSignIn: false);

        Assert.Equal(["Show on Desktop", "Fans && pumps", "", "Start when I sign in", "", "Exit"], Labels(entries));
    }

    [Fact]
    public void AMenuWithoutExit_EndsWithStartWhenISignIn()
    {
        var entries = WinTrayMenu.Lines(new ShellMenu([new(ShellMenu.OpenEditor, "Open Editor")]), startAtSignIn: false);

        Assert.Equal(["Open editor", "", "Start when I sign in"], Labels(entries));
    }

    [Fact]
    public void TheTooltip_IsTheNameAndVersion() => Assert.Equal("InfoPanel 2.0.0", WinShellText.Tooltip("2.0.0"));

    [Fact]
    public void ExitsPrompt_IsInWindowsWords_WithCancelLast()
    {
        var mac = ShellService.QuitPrompt(["Desk overlay", "Wide bar"]);

        var prompt = WinShellText.Prompt(mac);

        Assert.Equal("Exit InfoPanel?", prompt.Title);
        Assert.Equal(mac.Text, prompt.Text);
        Assert.Equal([(0, "Open editor"), (2, "Exit without saving"), (1, "Cancel")], prompt.Buttons.Select(b => (b.Answer, b.Label)));
        Assert.Equal(0, prompt.DefaultAnswer);
        Assert.Equal(1, prompt.CancelAnswer);
    }

    [Fact]
    public void Show_AddsTheIcon_Once_WithTheTooltip_AtTheTaskbarsSize()
    {
        _native.Pixels = 24;
        var tray = Shown();
        tray.Show("again", () => throw new InvalidOperationException(), _ => Task.CompletedTask);

        Assert.Equal([("InfoPanel 2.0.0", 24)], _native.Adds);
        Assert.True(tray.Present);
    }

    [Fact]
    public void ALeftClick_OpensTheEditor_AndSoDoesTheKeyboard_ButADoubleClickOpensItOnce()
    {
        var tray = Shown();

        Assert.Equal(0, tray.OnMessage(WinTray.CallbackMessage, Anchor(10, 20), Event(WinTray.NIN_SELECT)));
        _now += 100; // the second click of a double-click
        tray.OnMessage(WinTray.CallbackMessage, Anchor(10, 20), Event(WinTray.NIN_SELECT));
        Assert.Equal([ShellMenu.OpenEditor], _invoked);

        _now += _native.DoubleClickMilliseconds;
        tray.OnMessage(WinTray.CallbackMessage, Anchor(10, 20), Event(WinTray.NIN_KEYSELECT));
        Assert.Equal([ShellMenu.OpenEditor, ShellMenu.OpenEditor], _invoked);
        Assert.Empty(_native.Menus);
    }

    [Fact]
    public void ARightClick_ShowsTheMenuBuiltAfresh_AtTheClick_AndRunsTheItemChosen()
    {
        var tray = Shown();
        _native.PickCommand = entries => FakeTrayNative.CommandOf(entries, ShellMenu.Quit);

        tray.OnMessage(WinTray.CallbackMessage, Anchor(1800, -30), Event(WinTray.WM_CONTEXTMENU));
        _native.PickCommand = entries => FakeTrayNative.CommandOf(entries, ShellMenu.OpenEditor);
        tray.OnMessage(WinTray.CallbackMessage, Anchor(5, 6), Event(WinTray.WM_CONTEXTMENU));
        _native.PickCommand = _ => 0; // dismissed
        tray.OnMessage(WinTray.CallbackMessage, Anchor(5, 6), Event(WinTray.WM_CONTEXTMENU));

        Assert.Equal(3, _menusBuilt);
        Assert.Equal((1800, -30), (_native.Menus[0].X, _native.Menus[0].Y));
        Assert.Equal([ShellMenu.Quit, ShellMenu.OpenEditor], _invoked);
    }

    [Fact]
    public void AChoiceInShowOnDesktop_OrAnyNewLine_RunsTheModelsId()
    {
        var tray = Shown(state: Full(profiles: 21));
        string[] picks =
        [
            ShellMenu.DesktopItem("p1", show: true), ShellMenu.DesktopItem("p2", show: false), ShellMenu.MoreProfiles, ShellMenu.ClickThroughItem("p2", on: true),
            ShellMenu.ShowDataFolder, ShellMenu.OpenLogsFolder, ShellMenu.CopyEngineDetails, ShellMenu.SignedIn,
        ];

        foreach (var pick in picks)
        {
            _native.PickCommand = entries => FakeTrayNative.CommandOf(entries, pick);
            tray.OnMessage(WinTray.CallbackMessage, Anchor(5, 6), Event(WinTray.WM_CONTEXTMENU));
        }

        Assert.Equal(picks.Length, _native.Menus.Count);
        Assert.Equal(picks, _invoked);
        Assert.Empty(_registry.Values);
    }

    [Fact]
    public void StartWhenISignIn_ReadsTheRunValue_AsTheMenuOpens_AndTurnsItOnAndOff()
    {
        var tray = Shown();
        uint Toggle(IReadOnlyList<WinMenuEntry> entries) => FakeTrayNative.CommandOf(entries, WinTrayMenu.StartAtSignInId);
        WinMenuEntry ItemIn(int menu) => _native.Menus[menu].Entries.Single(e => e.Id == WinTrayMenu.StartAtSignInId);
        _native.PickCommand = Toggle;

        tray.OnMessage(WinTray.CallbackMessage, Anchor(0, 0), Event(WinTray.WM_CONTEXTMENU));
        Assert.Equal(WinMenuFlags.None, ItemIn(0).Flags);
        Assert.Equal(Command, _registry.Values[(WinStartup.RunKey, WinStartup.ValueName)]);

        tray.OnMessage(WinTray.CallbackMessage, Anchor(0, 0), Event(WinTray.WM_CONTEXTMENU));
        Assert.Equal(WinMenuFlags.Checked, ItemIn(1).Flags);
        Assert.Empty(_registry.Values);

        // Turned on in Task Manager meanwhile: the menu shows what it shows.
        _registry.SetString(WinStartup.RunKey, WinStartup.ValueName, Command);
        _native.PickCommand = _ => 0;
        tray.OnMessage(WinTray.CallbackMessage, Anchor(0, 0), Event(WinTray.WM_CONTEXTMENU));
        Assert.Equal(WinMenuFlags.Checked, ItemIn(2).Flags);
        Assert.Empty(_invoked);
    }

    [Fact]
    public void ExplorerRestarted_AddsTheIconAgain_OnlyWhileItIsShown()
    {
        var tray = Tray();
        tray.OnMessage(TaskbarCreated, 0, 0);
        Assert.Empty(_native.Adds);

        tray = Shown();
        Assert.Null(tray.OnMessage(TaskbarCreated, 0, 0));
        Assert.Equal(2, _native.Adds.Count);
        Assert.Equal(2, tray.Additions);

        tray.Remove();
        tray.OnMessage(TaskbarCreated, 0, 0);
        Assert.Equal(2, _native.Adds.Count);
    }

    [Fact]
    public void NoTaskbarYet_AtShow_TheIconIsAddedWhenExplorerSaysItIsThere()
    {
        _native.AddSucceeds = false;
        var tray = Shown();
        Assert.False(tray.Present);

        _native.AddSucceeds = true;
        tray.OnMessage(TaskbarCreated, 0, 0);

        Assert.True(tray.Present);
        Assert.Equal(1, tray.Additions);
    }

    [Fact]
    public void Remove_DeletesTheIconOnce_AndClicksAfterItDoNothing()
    {
        var tray = Shown();

        tray.Remove();
        tray.Remove();
        tray.OnMessage(WinTray.CallbackMessage, Anchor(0, 0), Event(WinTray.NIN_SELECT));
        tray.OnMessage(WinTray.CallbackMessage, Anchor(0, 0), Event(WinTray.WM_CONTEXTMENU));

        Assert.Equal(1, _native.Deletes);
        Assert.False(tray.Present);
        Assert.Empty(_invoked);
        Assert.Empty(_native.Menus);
    }

    [Fact]
    public async Task APrompt_IsAskedInWindowsWords_AndAnswersTheModelsButton()
    {
        var tray = Shown();
        _native.PickAnswer = p => p.Buttons.Single(b => b.Label == "Exit without saving").Answer;
        var answer = new TaskCompletionSource<int>();

        tray.Enqueue(ShellService.QuitPrompt(["Wide bar"]), answer);

        Assert.Equal(2, await answer.Task);
        Assert.Equal("Exit InfoPanel?", Assert.Single(_native.Prompts).Title);
    }

    [Fact]
    public async Task APromptAskedWhileOneIsOpen_WaitsForItsAnswer()
    {
        var tray = Shown();
        var second = new TaskCompletionSource<int>();
        _native.PickAnswer = p =>
        {
            if (_native.Prompts.Count == 1)
            {
                tray.Enqueue(ShellService.QuitPrompt(["B"]), second);
                Assert.False(second.Task.IsCompleted);
                Assert.Single(_native.Prompts);
            }

            return p.DefaultAnswer;
        };
        var first = new TaskCompletionSource<int>();

        tray.Enqueue(ShellService.QuitPrompt(["A"]), first);

        Assert.Equal(0, await first.Task);
        Assert.Equal(0, await second.Task);
        Assert.Equal(2, _native.Prompts.Count);
    }

    [Fact]
    public async Task Remove_ClosesTheOpenPrompt_AnswersTheWaitingOnesWithCancel_AndLaterOnesAreNotAsked()
    {
        var tray = Shown();
        var waiting = new TaskCompletionSource<int>();
        _native.PickAnswer = p =>
        {
            tray.Enqueue(ShellService.QuitPrompt(["B"]), waiting);
            tray.Remove(); // the engine stops while the prompt is open
            return p.CancelAnswer;
        };
        var open = new TaskCompletionSource<int>();

        tray.Enqueue(ShellService.QuitPrompt(["A"]), open);
        var later = new TaskCompletionSource<int>();
        tray.Enqueue(ShellService.QuitPrompt(["C"]), later);

        Assert.Equal(1, await open.Task);
        Assert.Equal(1, await waiting.Task);
        Assert.Equal(1, await later.Task);
        Assert.Equal(1, _native.Dismissals);
        Assert.Single(_native.Prompts);
    }
}
