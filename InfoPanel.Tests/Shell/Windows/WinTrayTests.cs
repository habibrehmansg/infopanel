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

    private WinTray Shown(string version = "2.0.0")
    {
        var tray = Tray();
        tray.Show(WinShellText.Tooltip(version), () =>
        {
            _menusBuilt++;
            return ShellMenu.Build(new ShellState(version));
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

    [Fact]
    public void TheMenu_IsInWindowsWords_WithStartWhenISignIn_AboveExit()
    {
        var entries = WinTrayMenu.Flatten(ShellMenu.Build(new ShellState("2.0.0")), startAtSignIn: false);

        Assert.Equal(["InfoPanel 2.0.0 is running", "Open editor", "", "Start when I sign in", "", "Exit"], Labels(entries));
        Assert.Equal(WinMenuFlags.Disabled, entries[0].Flags);
        Assert.Equal(WinMenuFlags.Default, entries[1].Flags);
        Assert.Equal(WinMenuFlags.Separator, entries[2].Flags);
        Assert.Equal(WinMenuFlags.None, entries[3].Flags);
        Assert.Equal(WinMenuFlags.Separator, entries[4].Flags);
        Assert.Equal(WinMenuFlags.None, entries[5].Flags);
        Assert.Equal([ShellMenu.Status, ShellMenu.OpenEditor, "", WinTrayMenu.StartAtSignInId, "", ShellMenu.Quit], entries.Select(e => e.Id));
    }

    [Fact]
    public void OnlyOpenEditor_IsTheDefaultItem()
    {
        var entries = WinTrayMenu.Flatten(ShellMenu.Build(new ShellState("2.0.0")), startAtSignIn: true);

        var defaults = entries.Where(e => e.Flags.HasFlag(WinMenuFlags.Default)).ToList();
        Assert.Equal(ShellMenu.OpenEditor, Assert.Single(defaults).Id);
    }

    [Fact]
    public void EachItem_HasItsOwnCommand_AndSeparatorsNone()
    {
        var entries = WinTrayMenu.Flatten(ShellMenu.Build(new ShellState("2.0.0")), startAtSignIn: false);

        var items = entries.Where(e => !e.Flags.HasFlag(WinMenuFlags.Separator)).ToList();
        Assert.All(items, e => Assert.NotEqual(0u, e.Command));
        Assert.Equal(items.Count, items.Select(e => e.Command).Distinct().Count());
        Assert.All(entries.Where(e => e.Flags.HasFlag(WinMenuFlags.Separator)), e => Assert.Equal(0u, e.Command));
    }

    [Fact]
    public void StartWhenISignIn_IsTicked_WhenOn_AndGreyed_WhenItCantBeRead()
    {
        var on = WinTrayMenu.Flatten(ShellMenu.Build(new ShellState("2.0.0")), startAtSignIn: true).Single(e => e.Id == WinTrayMenu.StartAtSignInId);
        var unknown = WinTrayMenu.Flatten(ShellMenu.Build(new ShellState("2.0.0")), startAtSignIn: null).Single(e => e.Id == WinTrayMenu.StartAtSignInId);

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

        var entries = WinTrayMenu.Flatten(menu, startAtSignIn: false);

        Assert.Equal(["Show on Desktop", "Fans && pumps", "", "Start when I sign in", "", "Exit"], Labels(entries));
    }

    [Fact]
    public void AMenuWithoutExit_EndsWithStartWhenISignIn()
    {
        var entries = WinTrayMenu.Flatten(new ShellMenu([new(ShellMenu.OpenEditor, "Open Editor")]), startAtSignIn: false);

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
