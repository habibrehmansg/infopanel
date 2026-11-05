using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InfoPanel.Desktop.Windows;
using Microsoft.Win32;
using Serilog;
using static InfoPanel.Shell.Windows.WinShellInterop;

namespace InfoPanel.Shell.Windows;

/// <summary>
/// The notification-area icon's Win32 side, on the UI thread, owned by its hidden top-level window (which hears
/// Explorer's <c>TaskbarCreated</c> broadcast; a message-only window wouldn't): <c>Shell_NotifyIconW</c> with version 4
/// callbacks, the glyph at the small-icon size for the taskbar's DPI, an <c>HMENU</c> shown with <c>TrackPopupMenuEx</c>
/// (light, as Win32 menus are), and <c>TaskDialogIndirect</c> for prompts (Common Controls 6, activated for them:
/// <see cref="WinCommonControls"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WinTrayNative(nint hwnd) : IWinTrayNative, IWinMessageSource
{
    private const uint IconId = 1;

    /// <summary><c>PM_NOREMOVE</c>: a look at the queue that leaves the message there.</summary>
    private const uint PM_NOREMOVE = 0x0000;

    /// <summary>The button ids of a prompt's buttons other than Cancel (which is <c>IDCANCEL</c>, so Escape chooses it).</summary>
    private const int FirstButtonId = 100;

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Serilog.ILogger Logger = Log.ForContext<WinTrayNative>();

    /// <summary>The open task dialog's window (one prompt at a time, on the one UI thread), or 0.</summary>
    private static nint _dialog;

    private nint _icon;

    public uint DoubleClickMilliseconds => GetDoubleClickTime();

    public bool AddIcon(string tooltip, bool lightTaskbar)
    {
        var old = _icon;
        _icon = CreateIcon(lightTaskbar);
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        var length = Math.Min(tooltip.Length, 127);
        for (var i = 0; i < length; i++)
            data.SzTip[i] = tooltip[i];

        // Already there (a TaskbarCreated without a restart): changed in place.
        var added = Shell_NotifyIconW(NIM_ADD, &data) != 0 || Shell_NotifyIconW(NIM_MODIFY, &data) != 0;
        if (added)
        {
            data.UVersion = NOTIFYICON_VERSION_4;
            if (Shell_NotifyIconW(NIM_SETVERSION, &data) == 0)
                Logger.Warning("The notification area icon's version couldn't be set; its clicks may not be heard");
        }

        Destroy(old);
        return added;
    }

    public bool ModifyIcon(bool lightTaskbar)
    {
        var old = _icon;
        _icon = CreateIcon(lightTaskbar);
        var data = Data(NIF_ICON);
        var changed = Shell_NotifyIconW(NIM_MODIFY, &data) != 0;
        Destroy(old);
        return changed;
    }

    public void DeleteIcon()
    {
        var data = Data(0);
        if (Shell_NotifyIconW(NIM_DELETE, &data) == 0)
            Logger.Warning("The notification area icon couldn't be removed");
        Destroy(_icon);
        _icon = 0;
    }

    public uint ShowMenu(IReadOnlyList<WinMenuEntry> entries, int x, int y)
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            Logger.Error("The notification area icon's menu couldn't be made ({Error})", Marshal.GetLastPInvokeError());
            return 0;
        }

        try
        {
            foreach (var entry in entries)
            {
                if (entry.Flags.HasFlag(WinMenuFlags.Separator))
                {
                    AppendMenuW(menu, MF_SEPARATOR, 0, null);
                    continue;
                }

                var flags = MF_STRING | (entry.Flags.HasFlag(WinMenuFlags.Disabled) ? MF_GRAYED : 0) | (entry.Flags.HasFlag(WinMenuFlags.Checked) ? MF_CHECKED : 0);
                fixed (char* text = entry.Label)
                    AppendMenuW(menu, flags, entry.Command, text);
                if (entry.Flags.HasFlag(WinMenuFlags.Default))
                    SetMenuDefaultItem(menu, entry.Command, 0);
            }

            // The menu closes when the user clicks elsewhere only if its window is in the foreground; the null message
            // after it lets the next right click open it again (the documented TrackPopupMenu pattern).
            SetForegroundWindow(hwnd);
            var align = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            var command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | align, x, y, hwnd, 0);
            Win32.PostMessageW(hwnd, WM_NULL, 0, 0);
            return (uint)command;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public int ShowPrompt(WinPrompt prompt)
    {
        var strings = new List<nint>();
        char* Text(string s)
        {
            var p = Marshal.StringToHGlobalUni(s);
            strings.Add(p);
            return (char*)p;
        }

        try
        {
            var buttons = stackalloc TASKDIALOG_BUTTON[prompt.Buttons.Count];
            for (var i = 0; i < prompt.Buttons.Count; i++)
                buttons[i] = new TASKDIALOG_BUTTON { NButtonID = IdOf(prompt, prompt.Buttons[i].Answer), PszButtonText = Text(prompt.Buttons[i].Label) };

            var config = new TASKDIALOGCONFIG
            {
                CbSize = (uint)sizeof(TASKDIALOGCONFIG),
                DwFlags = TDF_ALLOW_DIALOG_CANCELLATION,
                PszWindowTitle = Text("InfoPanel"),
                PszMainInstruction = Text(prompt.Title),
                PszContent = Text(prompt.Text),
                CButtons = (uint)prompt.Buttons.Count,
                PButtons = buttons,
                NDefaultButton = IdOf(prompt, prompt.DefaultAnswer),
                PfCallback = &DialogCallback,
            };

            int chosen;
            int result;
            try
            {
                Logger.Information("The notification area icon's prompt is open");
                using (WinCommonControls.Enter())
                    result = TaskDialogIndirect(&config, &chosen, null, null);
            }
            catch (EntryPointNotFoundException ex)
            {
                Logger.Error(ex, "The prompt needs Common Controls 6, which couldn't be activated; answered Cancel");
                return prompt.CancelAnswer;
            }

            if (result != 0)
            {
                Logger.Error("The notification area icon's prompt couldn't be shown (0x{Result:X8}); answered Cancel", result);
                return prompt.CancelAnswer;
            }

            var answer = chosen - FirstButtonId;
            return answer != prompt.CancelAnswer && prompt.Buttons.Any(b => b.Answer == answer) ? answer : prompt.CancelAnswer;
        }
        finally
        {
            foreach (var p in strings)
                Marshal.FreeHGlobal(p);
        }
    }

    public void DismissPrompt()
    {
        if (_dialog == 0)
            return;
        SendMessageW(_dialog, TDM_CLICK_BUTTON, IDCANCEL, 0);
        Logger.Information("The notification area icon's prompt was dismissed: the engine is stopping");
    }

    /// <summary>The small-icon size at the DPI of the taskbar's display (the system's when there is no taskbar).</summary>
    public int IconPixels()
    {
        var size = GetSystemMetricsForDpi(SM_CXSMICON, TaskbarDpi());
        return size > 0 ? size : 16;
    }

    public void HoldUntilQuit(long deadline, Func<long> clock) => WinMessageHold.Run(this, clock, deadline);

    bool IWinMessageSource.Peek()
    {
        MSG msg;
        return Win32.PeekMessageW(&msg, 0, 0, 0, PM_NOREMOVE) != 0;
    }

    WinMessageTaken IWinMessageSource.Take()
    {
        MSG msg;
        if (Win32.PeekMessageW(&msg, 0, 0, 0, Win32.PM_REMOVE) == 0)
            return WinMessageTaken.None;
        if (msg.Message == Win32.WM_QUIT)
        {
            Win32.PostQuitMessage((int)msg.WParam); // for the loop this hold runs in
            return WinMessageTaken.Quit;
        }

        Win32.TranslateMessage(&msg);
        Win32.DispatchMessageW(&msg);
        return WinMessageTaken.Dispatched;
    }

    void IWinMessageSource.Wait(long milliseconds) =>
        MsgWaitForMultipleObjectsEx(0, null, (uint)Math.Clamp(milliseconds, 0, uint.MaxValue - 1), QS_ALLINPUT, MWMO_INPUTAVAILABLE);

    private static uint TaskbarDpi()
    {
        nint taskbar;
        fixed (char* name = "Shell_TrayWnd")
            taskbar = FindWindowW(name, null);
        var monitor = taskbar != 0 ? MonitorFromWindow(taskbar, MONITOR_DEFAULTTOPRIMARY) : 0;
        uint x, y;
        return monitor != 0 && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &x, &y) == 0 && x > 0 ? x : GetDpiForSystem();
    }

    public bool LightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int light && light != 0;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "The taskbar's theme couldn't be read; drawing the icon for a dark one");
            return false;
        }
    }

    private static int IdOf(WinPrompt prompt, int answer) => answer == prompt.CancelAnswer ? IDCANCEL : FirstButtonId + answer;

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        CbSize = (uint)sizeof(NOTIFYICONDATAW),
        HWnd = hwnd,
        UId = IconId,
        UFlags = flags,
        UCallbackMessage = WinTray.CallbackMessage,
        HIcon = _icon,
    };

    /// <summary>The glyph at the taskbar's small-icon size (16 pixels at 100 %, 24 at 150 %, 32 at 200 %).</summary>
    private nint CreateIcon(bool lightTaskbar)
    {
        var size = IconPixels();
        var png = WinTrayGlyph.Png(size, lightTaskbar);
        nint icon;
        fixed (byte* bits = png)
            icon = CreateIconFromResourceEx(bits, (uint)png.Length, 1, 0x00030000, size, size, 0);
        if (icon != 0)
            return icon;

        Logger.Warning("The notification area icon's glyph couldn't be made ({Error}); showing the system's", Marshal.GetLastPInvokeError());
        return SystemIcon.Value;
    }

    /// <summary>The system's application icon: shared, so never destroyed.</summary>
    private static readonly Lazy<nint> SystemIcon = new(() => LoadIconW(0, IDI_APPLICATION));

    private static void Destroy(nint icon)
    {
        if (icon != 0 && !(SystemIcon.IsValueCreated && icon == SystemIcon.Value))
            DestroyIcon(icon);
    }

    [UnmanagedCallersOnly]
    private static int DialogCallback(nint dialog, uint notification, nint wParam, nint lParam, nint data)
    {
        try
        {
            switch (notification)
            {
                case TDN_CREATED:
                    _dialog = dialog;
                    SetForegroundWindow(dialog);
                    break;
                case TDN_DESTROYED:
                    _dialog = 0;
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "The prompt's callback failed");
        }

        return 0;
    }
}
