using Serilog;

namespace InfoPanel.Shell.Windows;

/// <summary>The notification-area icon's Win32 side (<see cref="WinTrayNative"/>; a fake in tests). UI thread only.</summary>
internal interface IWinTrayNative
{
    /// <summary>Adds the icon (version 4 callbacks) with its tooltip, the logo at <see cref="IconPixels"/>; false when Explorer refused.</summary>
    bool AddIcon(string tooltip);

    /// <summary>Redraws the icon at <see cref="IconPixels"/>.</summary>
    bool ModifyIcon();

    /// <summary>Removes the icon.</summary>
    void DeleteIcon();

    /// <summary>Shows <paramref name="balloon"/> on the icon (Windows 10 and 11 show it as a toast); false when Explorer refused.</summary>
    bool ShowBalloon(WinBalloon balloon);

    /// <summary>Shows the menu at the point (physical pixels) until it closes; the command chosen, or 0.</summary>
    uint ShowMenu(IReadOnlyList<WinMenuEntry> entries, int x, int y);

    /// <summary>Asks <paramref name="prompt"/> until it is answered or dismissed; the answer (a model's button index).</summary>
    int ShowPrompt(WinPrompt prompt);

    /// <summary>Closes the open prompt as Cancel.</summary>
    void DismissPrompt();

    /// <summary>The longest gap between the clicks of a double-click.</summary>
    uint DoubleClickMilliseconds { get; }

    /// <summary>The icon's size in pixels at the taskbar's DPI (the small-icon size: 16 at 100 %, 32 at 200 %).</summary>
    int IconPixels();

    /// <summary>
    /// Runs the UI thread's messages until its loop is told to quit (the host has stopped) or <paramref name="clock"/>
    /// (milliseconds) reaches <paramref name="deadline"/>; the quit is left for the loop (<see cref="WinMessageHold"/>).
    /// </summary>
    void HoldUntilQuit(long deadline, Func<long> clock);
}

/// <summary>
/// A balloon on the icon: its title (at most <see cref="WinTray.BalloonTitleLength"/> characters) and text (at most
/// <see cref="WinTray.BalloonTextLength"/>), and its <c>NIIF_</c> flags.
/// </summary>
internal sealed record WinBalloon(string Title, string Text, uint Flags);

/// <summary>What <see cref="IWinMessageSource.Take"/> found.</summary>
internal enum WinMessageTaken
{
    /// <summary>No message was waiting.</summary>
    None,

    /// <summary>A message was dispatched (its handler may hold again, nested).</summary>
    Dispatched,

    /// <summary>The loop's quit, posted again for the loop.</summary>
    Quit,
}

/// <summary>The UI thread's message queue as a session end's hold drains it (<see cref="WinTrayNative"/>; a fake in tests).</summary>
internal interface IWinMessageSource
{
    /// <summary>
    /// Whether a message waits, leaving it queued. Messages sent to the thread run here (their handlers may hold again,
    /// nested), as they do in any look at the queue.
    /// </summary>
    bool Peek();

    /// <summary>Takes the next waiting message: dispatches it, or posts the loop's quit again.</summary>
    WinMessageTaken Take();

    /// <summary>Waits until a message comes or <paramref name="milliseconds"/> have passed.</summary>
    void Wait(long milliseconds);
}

/// <summary>A session end's hold over the UI thread's messages, with no Win32 in it (tested on any OS).</summary>
internal static class WinMessageHold
{
    private static readonly Serilog.ILogger Logger = Log.ForContext(typeof(WinMessageHold));

    /// <summary>
    /// Runs <paramref name="source"/>'s messages until the loop's quit (true) or until <paramref name="clock"/>
    /// (milliseconds) reaches <paramref name="deadline"/> (false). The one deadline is checked after anything that can
    /// run code (a dispatch, a look that runs sent messages) and before a message is taken, so neither a queue that never
    /// empties nor a nested hold that reached it holds the session's end past it.
    /// </summary>
    public static bool Run(IWinMessageSource source, Func<long> clock, long deadline)
    {
        while (true)
        {
            if (deadline - clock() <= 0)
                return Late();

            var waiting = source.Peek();
            var left = deadline - clock();
            if (left <= 0)
                return Late();

            if (!waiting)
                source.Wait(left);
            else if (source.Take() == WinMessageTaken.Quit)
            {
                Logger.Information("The engine stopped before the session's end");
                return true;
            }
        }
    }

    private static bool Late()
    {
        Logger.Warning("The engine didn't stop by the session end's deadline");
        return false;
    }
}

/// <summary>
/// The notification-area icon's logic, on the UI thread, with no Win32 in it (tested on any OS through a fake
/// <see cref="IWinTrayNative"/>; <see cref="WinShellPlatform"/> wires it to the real one):
/// <list type="bullet">
/// <item>a left click (or Enter on the focused icon) runs the menu's Open editor; the second click of a double-click
/// doesn't open a second tab;</item>
/// <item>a right click (or the menu key) shows the menu built afresh from the model (<see cref="WinTrayMenu"/>) with
/// Start when I sign in read from the Run value then; the item chosen runs: the model's through the engine, Start when I
/// sign in here;</item>
/// <item>Explorer's <c>TaskbarCreated</c> (after it restarts, or once the taskbar exists at a sign-in start) adds the
/// icon again while it is shown; a settings or display change that turns the taskbar light or dark, or changes the
/// icon's size in pixels (the display's scale), redraws it;</item>
/// <item>the session ending (sign-out, shutdown) raises <see cref="SessionEnding"/> once (the engine stops without
/// asking) and holds the end until the host has stopped, at most <see cref="SessionEndWait"/> from the first end, however
/// many come;</item>
/// <item>prompts are asked one at a time, in Windows' words; the engine's stop closes the open one as Cancel and answers
/// every waiting one with Cancel;</item>
/// <item>a notification is a balloon on the icon (<see cref="Notify"/>), its title and text cut to what the structure
/// holds; a click on it (<c>NIN_BALLOONUSERCLICK</c>) runs its callback.</item>
/// </list>
/// </summary>
internal sealed class WinTray(IWinTrayNative native, WinStartup startup, uint taskbarCreated, Func<long> clock)
{
    /// <summary>The icon's callback message to the hidden window (clear of the UI thread's own <c>WM_APP</c> messages).</summary>
    public const uint CallbackMessage = 0x8000 + 0x100;

    public const uint WM_QUERYENDSESSION = 0x0011;
    public const uint WM_ENDSESSION = 0x0016;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint NIN_SELECT = 0x0400;
    public const uint NIN_KEYSELECT = 0x0401;
    public const uint NIN_BALLOONUSERCLICK = 0x0405;

    /// <summary><c>NIIF_USER</c>: the balloon shows the icon's own image.</summary>
    public const uint NIIF_USER = 0x04;

    /// <summary><c>NIIF_RESPECT_QUIET_TIME</c>: not shown in the user's first hour on a new install, as the guidelines ask.</summary>
    public const uint NIIF_RESPECT_QUIET_TIME = 0x80;

    /// <summary>The longest balloon title <c>szInfoTitle</c> holds (64 with its terminator).</summary>
    public const int BalloonTitleLength = 63;

    /// <summary>The longest balloon text <c>szInfo</c> holds (256 with its terminator).</summary>
    public const int BalloonTextLength = 255;

    /// <summary>
    /// The longest a session's end waits for the host to stop: within the 5 s Windows gives an app before it offers to
    /// end it anyway.
    /// </summary>
    public static readonly TimeSpan SessionEndWait = TimeSpan.FromSeconds(4);

    private static readonly Serilog.ILogger Logger = Log.ForContext<WinTray>();

    private readonly Queue<(ShellPrompt Prompt, TaskCompletionSource<int> Answer)> _prompts = new();
    private string? _tooltip;
    private Func<ShellMenu>? _menu;
    private Func<string, Task>? _invoked;
    private int _pixels;
    private bool _promptOpen;
    private bool _closed;
    private long? _lastSelect;
    private long? _sessionEndDeadline;
    private Func<Task>? _balloonClicked;

    /// <summary>
    /// The session is ending (sign-out, shutdown, restart): raised once, on the UI thread, before it waits, at most
    /// <see cref="SessionEndWait"/>, for the host to stop.
    /// </summary>
    public event Action? SessionEnding;

    /// <summary>Whether the icon is in the notification area now.</summary>
    public bool Present { get; private set; }

    /// <summary>How many times the icon was added (once, then once per <c>TaskbarCreated</c>).</summary>
    public int Additions { get; private set; }

    /// <summary>Shows the icon; nothing after the first time or once removed.</summary>
    public void Show(string tooltip, Func<ShellMenu> menu, Func<string, Task> invoked)
    {
        if (_tooltip != null || _closed)
            return;
        (_tooltip, _menu, _invoked) = (tooltip, menu, invoked);
        Add();
    }

    /// <summary>The engine is stopping: closes the prompts and removes the icon, for good.</summary>
    public void Remove()
    {
        if (_closed)
            return;
        _closed = true;
        while (_prompts.TryDequeue(out var waiting))
            waiting.Answer.TrySetResult(waiting.Prompt.CancelButton);
        if (_promptOpen)
            native.DismissPrompt();
        if (Present)
        {
            native.DeleteIcon();
            Present = false;
            Logger.Information("Notification area icon removed");
        }
    }

    /// <summary>
    /// Shows <paramref name="note"/> as a balloon on the icon; a click on it runs <paramref name="clicked"/>. Nothing
    /// while the icon isn't there.
    /// </summary>
    public void Notify(ShellNotification note, Func<Task> clicked)
    {
        if (_closed || !Present)
        {
            Logger.Information("No notification area icon to show {Title} on", note.Title);
            return;
        }

        var balloon = new WinBalloon(Cut(note.Title, BalloonTitleLength), Cut(note.Text, BalloonTextLength), NIIF_USER | NIIF_RESPECT_QUIET_TIME);
        if (native.ShowBalloon(balloon))
        {
            _balloonClicked = clicked;
            Logger.Information("Notification shown");
        }
        else
            Logger.Warning("The notification couldn't be shown");
    }

    /// <summary><paramref name="text"/> in at most <paramref name="length"/> characters: cut with an ellipsis, never inside a surrogate pair.</summary>
    private static string Cut(string text, int length)
    {
        if (text.Length <= length)
            return text;
        var keep = length - 1;
        if (char.IsHighSurrogate(text[keep - 1]))
            keep--;
        return text[..keep] + "…";
    }

    /// <summary>The hidden window's messages: an answer for the icon's own, null for the rest (broadcasts go on).</summary>
    public nint? OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            if (!_closed && _tooltip != null)
                OnIconEvent((uint)(lParam & 0xFFFF), (short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
            return 0;
        }

        // The session may end: yes, without asking (a sign-out can't wait on a browser). It ends: the engine stops as
        // for a system quit, and this message is held until the host has stopped (bounded), since Windows may end the
        // process once it is answered. Console shutdown events don't come once user32 is loaded.
        if (msg == WM_QUERYENDSESSION)
            return 1;
        if (msg == WM_ENDSESSION)
        {
            if (wParam != 0)
                EndSession();
            return 0;
        }

        if (_closed || _tooltip == null)
            return null;

        if (msg == taskbarCreated)
        {
            Logger.Information("The taskbar was created: adding the notification area icon again");
            Add();
        }
        else if ((msg is WM_SETTINGCHANGE or WM_DISPLAYCHANGE or WM_DPICHANGED) && Present)
        {
            // The taskbar's display's scale (the icon's size in pixels) may have changed. Its theme doesn't matter: the
            // icon is the coloured logo on either.
            var pixels = native.IconPixels();
            if (pixels != _pixels)
            {
                _pixels = pixels;
                native.ModifyIcon();
            }
        }

        return null;
    }

    private void EndSession()
    {
        // Once: the first end asks the engine to stop and sets the one deadline. A later one (a nested one the hold's own
        // dispatch brings included) asks nothing again and holds only to that deadline, so the end is never held longer.
        if (_sessionEndDeadline is not { } deadline)
        {
            deadline = clock() + (long)SessionEndWait.TotalMilliseconds;
            _sessionEndDeadline = deadline;
            Logger.Information("The session is ending: stopping the engine");
            try
            {
                SessionEnding?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "The engine couldn't be asked to stop at the session's end");
            }
        }

        if (deadline - clock() > 0)
            native.HoldUntilQuit(deadline, clock);
    }

    /// <summary>Asks <paramref name="prompt"/> after the open one, if any; answered with Cancel once the icon is removed.</summary>
    public void Enqueue(ShellPrompt prompt, TaskCompletionSource<int> answer)
    {
        if (_closed)
        {
            answer.TrySetResult(prompt.CancelButton);
            return;
        }

        _prompts.Enqueue((prompt, answer));
        if (_promptOpen)
            return; // asked when the open one is answered

        while (!_closed && _prompts.TryDequeue(out var next))
        {
            _promptOpen = true;
            try
            {
                next.Answer.TrySetResult(native.ShowPrompt(WinShellText.Prompt(next.Prompt)));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "The notification area icon's prompt failed");
                next.Answer.TrySetResult(next.Prompt.CancelButton);
            }
            finally
            {
                _promptOpen = false;
            }
        }
    }

    private void Add()
    {
        _pixels = native.IconPixels();
        if (native.AddIcon(_tooltip!))
        {
            Present = true;
            Additions++;
            Logger.Information("Notification area icon added");
        }
        else
        {
            Present = false;
            Logger.Warning("The notification area icon couldn't be added; it is added when the taskbar says it is there");
        }
    }

    private void OnIconEvent(uint e, int x, int y)
    {
        switch (e)
        {
            case NIN_SELECT or NIN_KEYSELECT:
                var now = clock();
                if (_lastSelect is { } last && now - last < native.DoubleClickMilliseconds)
                    return;
                _lastSelect = now;
                _ = _invoked!(ShellMenu.OpenEditor);
                break;
            case WM_CONTEXTMENU:
                ShowMenu(x, y);
                break;
            case NIN_BALLOONUSERCLICK when _balloonClicked is { } clicked:
                Logger.Information("The notification was clicked");
                try
                {
                    _ = clicked();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "The notification's click failed");
                }

                break;
        }
    }

    private void ShowMenu(int x, int y)
    {
        bool? on;
        try
        {
            on = startup.IsOn();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Start when I sign in couldn't be read");
            on = null;
        }

        var entries = WinTrayMenu.Lines(_menu!(), on);
        var command = native.ShowMenu(entries, x, y);
        if (command == 0 || _closed)
            return;
        var chosen = WinTrayMenu.Find(entries, command);
        if (chosen == null)
            return;

        if (chosen.Id == WinTrayMenu.StartAtSignInId)
        {
            try
            {
                startup.Set(on != true);
                Logger.Information("Start when I sign in turned {State}", on != true ? "on" : "off");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Start when I sign in couldn't be changed");
            }

            return;
        }

        _ = _invoked!(chosen.Id);
    }
}
