using InfoPanel.Shell;
using InfoPanel.Shell.Windows;

namespace InfoPanel.Tests.Shell.Windows;

/// <summary>
/// The notification-area icon's Win32 side for tests: records each icon added, changed and deleted, shows menus by
/// answering the command a test picks, and prompts by answering the button a test picks. Nothing is drawn.
/// </summary>
internal sealed class FakeTrayNative : IWinTrayNative
{
    /// <summary>Each add: the tooltip and whether the icon was drawn for a light taskbar.</summary>
    public List<(string Tooltip, bool Light)> Adds { get; } = [];

    /// <summary>Each change of the icon's image: whether it was drawn for a light taskbar.</summary>
    public List<bool> Modifies { get; } = [];

    public int Deletes { get; private set; }

    /// <summary>Each menu shown, as its entries, and where.</summary>
    public List<(IReadOnlyList<WinMenuEntry> Entries, int X, int Y)> Menus { get; } = [];

    /// <summary>Each prompt shown, in order.</summary>
    public List<WinPrompt> Prompts { get; } = [];

    public int Dismissals { get; private set; }

    /// <summary>Whether Explorer takes the icon (false: no taskbar yet, as at a sign-in start).</summary>
    public bool AddSucceeds { get; set; } = true;

    /// <summary>The taskbar's theme as the system reports it.</summary>
    public bool Light { get; set; } = true;

    /// <summary>Picks the command a shown menu returns (0: the menu was dismissed).</summary>
    public Func<IReadOnlyList<WinMenuEntry>, uint> PickCommand { get; set; } = _ => 0;

    /// <summary>Picks a shown prompt's answer; runs while the prompt is "open" (a test may nest work here).</summary>
    public Func<WinPrompt, int> PickAnswer { get; set; } = p => p.CancelAnswer;

    public uint DoubleClickMilliseconds { get; set; } = 500;

    /// <summary>The icon's size in pixels at the taskbar's DPI now.</summary>
    public int Pixels { get; set; } = 16;

    /// <summary>Each hold of a session's end until the UI loop is told to quit: the longest it may last from its start.</summary>
    public List<TimeSpan> Holds { get; } = [];

    /// <summary>Each hold's deadline, on the clock the tray passed.</summary>
    public List<long> Deadlines { get; } = [];

    /// <summary>Runs during a hold (a test checks what happened before it).</summary>
    public Action OnHold { get; set; } = () => { };

    public bool AddIcon(string tooltip, bool lightTaskbar)
    {
        Adds.Add((tooltip, lightTaskbar));
        return AddSucceeds;
    }

    public bool ModifyIcon(bool lightTaskbar)
    {
        Modifies.Add(lightTaskbar);
        return true;
    }

    public void DeleteIcon() => Deletes++;

    public uint ShowMenu(IReadOnlyList<WinMenuEntry> entries, int x, int y)
    {
        Menus.Add((entries, x, y));
        return PickCommand(entries);
    }

    public int ShowPrompt(WinPrompt prompt)
    {
        Prompts.Add(prompt);
        return PickAnswer(prompt);
    }

    public void DismissPrompt() => Dismissals++;

    public bool LightTaskbar() => Light;

    public int IconPixels() => Pixels;

    public void HoldUntilQuit(long deadline, Func<long> clock)
    {
        Holds.Add(TimeSpan.FromMilliseconds(deadline - clock()));
        Deadlines.Add(deadline);
        OnHold();
    }

    /// <summary>The command of the entry <paramref name="id"/> in <paramref name="entries"/>.</summary>
    public static uint CommandOf(IReadOnlyList<WinMenuEntry> entries, string id) => entries.Single(e => e.Id == id).Command;
}

/// <summary>The registry for tests: values by key and name, as the Run and StartupApproved keys hold them.</summary>
internal sealed class FakeRegistry : IWinRegistry
{
    public Dictionary<(string Key, string Name), object> Values { get; } = [];

    /// <summary>Set to make every read throw (a registry that can't be read).</summary>
    public bool Broken { get; set; }

    public object? Get(string key, string name)
    {
        if (Broken)
            throw new UnauthorizedAccessException("denied");
        return Values.TryGetValue((key, name), out var value) ? value : null;
    }

    public void SetString(string key, string name, string value) => Values[(key, name)] = value;

    public void Delete(string key, string name) => Values.Remove((key, name));
}
