using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InfoPanel.Shell.Windows;

// The structures the notification-area icon passes to shell32 and comctl32, laid out as the 64-bit Windows headers lay
// them out (x64 and arm64 alike; commctrl.h packs the task dialog's to 1 byte). Their sizes and the offsets the code
// writes are pinned by WinShellPlatformTests, on any 64-bit OS.

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NOTIFYICONDATAW
{
    public uint CbSize;
    public nint HWnd;
    public uint UId;
    public uint UFlags;
    public uint UCallbackMessage;
    public nint HIcon;
    public fixed char SzTip[128];
    public uint DwState;
    public uint DwStateMask;
    public fixed char SzInfo[256];

    /// <summary>A union with <c>uTimeout</c>: the callback version for <c>NIM_SETVERSION</c>.</summary>
    public uint UVersion;

    public fixed char SzInfoTitle[64];
    public uint DwInfoFlags;
    public Guid GuidItem;
    public nint HBalloonIcon;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct TASKDIALOG_BUTTON
{
    public int NButtonID;
    public char* PszButtonText;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct TASKDIALOGCONFIG
{
    public uint CbSize;
    public nint HwndParent;
    public nint HInstance;
    public uint DwFlags;
    public uint DwCommonButtons;
    public char* PszWindowTitle;
    public nint HMainIcon;
    public char* PszMainInstruction;
    public char* PszContent;
    public uint CButtons;
    public TASKDIALOG_BUTTON* PButtons;
    public int NDefaultButton;
    public uint CRadioButtons;
    public TASKDIALOG_BUTTON* PRadioButtons;
    public int NDefaultRadioButton;
    public char* PszVerificationText;
    public char* PszExpandedInformation;
    public char* PszExpandedControlText;
    public char* PszCollapsedControlText;
    public nint HFooterIcon;
    public char* PszFooter;
    public delegate* unmanaged<nint, uint, nint, nint, nint, int> PfCallback;
    public nint LpCallbackData;
    public uint CxWidth;
}

/// <summary>An activation context's description (<c>CreateActCtxW</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ACTCTXW
{
    public uint CbSize;
    public uint DwFlags;
    public char* LpSource;
    public ushort WProcessorArchitecture;
    public ushort WLangId;
    public char* LpAssemblyDirectory;
    public char* LpResourceName;
    public char* LpApplicationName;
    public nint HModule;
}

/// <summary>
/// The Win32 functions the notification-area icon calls (shell32, the menu and icon parts of user32, comctl32's task
/// dialog, the taskbar's DPI, kernel32's activation contexts): hand-written <c>LibraryImport</c>s with blittable arguments, each returning the raw <c>BOOL</c>, handle or
/// code.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WinShellInterop
{
    private const string Shell32 = "shell32.dll";
    private const string User32 = "user32.dll";
    private const string Comctl32 = "comctl32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Shcore = "shcore.dll";

    public const uint NIM_ADD = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;
    public const uint NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x01;
    public const uint NIF_ICON = 0x02;
    public const uint NIF_TIP = 0x04;
    public const uint NIF_SHOWTIP = 0x80;
    public const uint NOTIFYICON_VERSION_4 = 4;

    public const uint MF_STRING = 0x0000;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_SEPARATOR = 0x0800;

    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RIGHTALIGN = 0x0008;
    public const uint TPM_BOTTOMALIGN = 0x0020;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;

    public const int SM_MENUDROPALIGNMENT = 40;
    public const int SM_CXSMICON = 49;

    public const uint WM_NULL = 0x0000;
    public const uint MSGFLT_ALLOW = 1;

    /// <summary><c>IDI_APPLICATION</c>, the system's application icon (when the glyph can't be made).</summary>
    public const nint IDI_APPLICATION = 32512;

    public const uint TDF_ALLOW_DIALOG_CANCELLATION = 0x0008;
    public const uint TDN_CREATED = 0;
    public const uint TDN_DESTROYED = 5;
    public const uint TDM_CLICK_BUTTON = 0x0400 + 102;
    public const int IDCANCEL = 2;

    public const uint QS_ALLINPUT = 0x04FF;
    public const uint MWMO_INPUTAVAILABLE = 0x0004;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const int MDT_EFFECTIVE_DPI = 0;

    /// <summary><c>ACTCTX_FLAG_RESOURCE_NAME_VALID</c>: the manifest is a resource of <c>lpSource</c>.</summary>
    public const uint ACTCTX_FLAG_RESOURCE_NAME_VALID = 0x008;

    /// <summary><c>CREATEPROCESS_MANIFEST_RESOURCE_ID</c>: where the compiler puts an executable's manifest.</summary>
    public const nint CREATEPROCESS_MANIFEST_RESOURCE_ID = 1;

    public const nint INVALID_HANDLE_VALUE = -1;

    [LibraryImport(Shell32, SetLastError = true)]
    public static partial int Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint CreatePopupMenu();

    [LibraryImport(User32, SetLastError = true)]
    public static partial int AppendMenuW(nint menu, uint flags, nuint id, char* text);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int SetMenuDefaultItem(nint menu, uint item, uint byPosition);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int DestroyMenu(nint menu);

    [LibraryImport(User32)]
    public static partial int SetForegroundWindow(nint hwnd);

    [LibraryImport(User32, SetLastError = true)]
    public static partial uint RegisterWindowMessageW(char* name);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint filter);

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint CreateIconFromResourceEx(byte* bits, uint size, int icon, uint version, int cx, int cy, uint flags);

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint LoadIconW(nint instance, nint name);

    [LibraryImport(User32)]
    public static partial int DestroyIcon(nint icon);

    [LibraryImport(User32)]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport(User32)]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport(User32)]
    public static partial uint GetDpiForSystem();

    [LibraryImport(User32)]
    public static partial uint GetDoubleClickTime();

    [LibraryImport(User32)]
    public static partial nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport(User32, SetLastError = true)]
    public static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);

    [LibraryImport(User32)]
    public static partial nint FindWindowW(char* className, char* windowName);

    [LibraryImport(User32)]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport(Shcore)]
    public static partial int GetDpiForMonitor(nint monitor, int type, uint* dpiX, uint* dpiY);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial nint CreateActCtxW(ACTCTXW* context);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial int ActivateActCtx(nint context, nuint* cookie);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial int DeactivateActCtx(uint flags, nuint cookie);

    /// <summary>In Common Controls 6 only: called inside <see cref="WinCommonControls.Enter"/>.</summary>
    [LibraryImport(Comctl32)]
    public static partial int TaskDialogIndirect(TASKDIALOGCONFIG* config, int* button, int* radioButton, int* verificationChecked);
}
