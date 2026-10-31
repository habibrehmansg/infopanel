using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace InfoPanel.Desktop.Windows;

/// <summary>
/// Desktop windows on Windows (Win32 through <see cref="Win32"/>, no UI framework). Everything but <see cref="Post"/>
/// runs on the <see cref="WinUiThread"/>, which this starts on first use.
/// <list type="bullet">
/// <item>Coordinates are physical pixels of the virtual screen and every display's scale is 1 (<see cref="WinDisplays"/>).</item>
/// <item>Windows are <see cref="WinWindow"/>s.</item>
/// <item><see cref="DisplaysChanged"/> is raised for <c>WM_DISPLAYCHANGE</c>, a resume from sleep and a session
/// connected or disconnected (Remote Desktop swaps the monitors), at once and once more 1 s after the last of them:
/// Windows moves windows off vanished monitors around the same time, and a wake can bring monitors back in steps.</item>
/// <item>Not supported when the process's window station isn't interactive (a service, or a shell started over SSH):
/// windows there are never seen.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinDesktopPlatform : IDesktopPlatform
{
    /// <summary>How long after the last display change the second pass is raised.</summary>
    internal static readonly TimeSpan SecondPass = TimeSpan.FromSeconds(1);

    private static readonly Serilog.ILogger Logger = Log.ForContext<WinDesktopPlatform>();

    private readonly WinUiThread _thread;
    private readonly Lazy<bool> _interactive = new(IsInteractiveWindowStation);
    private readonly Timer _secondPass;

    public WinDesktopPlatform(WinUiThread thread)
    {
        _thread = thread;
        _secondPass = new Timer(_ => _thread.Post(RaiseDisplaysChanged));
        _thread.AddMessageHook(OnMessage);
    }

    public bool IsSupported => _interactive.Value;

    public string? UnsupportedMessage => IsSupported ? null : "Desktop windows need an interactive desktop; not showing them";

    public event Action? DisplaysChanged;

    public void Post(Action action) => _thread.Post(action);

    public IReadOnlyList<DisplayInfo> GetDisplays() => WinDisplays.Read();

    public IDesktopWindow CreateWindow(DesktopWindowStyle style, PointRect frame) => new WinWindow(style, frame);

    /// <summary>The hidden window's broadcasts (UI thread): display changes, resume, sessions connected or disconnected.</summary>
    private nint? OnMessage(uint msg, nint wParam, nint lParam)
    {
        var changed = msg switch
        {
            Win32.WM_DISPLAYCHANGE => true,
            Win32.WM_POWERBROADCAST => wParam is Win32.PBT_APMRESUMEAUTOMATIC or Win32.PBT_APMRESUMESUSPEND,
            Win32.WM_WTSSESSION_CHANGE => wParam is Win32.WTS_CONSOLE_CONNECT or Win32.WTS_CONSOLE_DISCONNECT
                or Win32.WTS_REMOTE_CONNECT or Win32.WTS_REMOTE_DISCONNECT,
            _ => false,
        };
        if (changed)
        {
            Logger.Debug("The displays may have changed (message 0x{Message:X4}, {WParam})", msg, wParam);
            RaiseDisplaysChanged();
            _secondPass.Change(SecondPass, Timeout.InfiniteTimeSpan);
        }

        return null;
    }

    private void RaiseDisplaysChanged()
    {
        WinDisplays.Invalidate();
        DisplaysChanged?.Invoke();
    }

    /// <summary>Whether this process's window station is the interactive one (<c>WSF_VISIBLE</c>).</summary>
    private static unsafe bool IsInteractiveWindowStation()
    {
        var station = Win32.GetProcessWindowStation();
        if (station == 0)
        {
            Logger.Warning("The window station couldn't be read ({Error}); desktop windows are off", Marshal.GetLastPInvokeError());
            return false;
        }

        USEROBJECTFLAGS flags;
        uint needed;
        if (Win32.GetUserObjectInformationW(station, Win32.UOI_FLAGS, &flags, (uint)sizeof(USEROBJECTFLAGS), &needed) == 0)
        {
            Logger.Warning("The window station's flags couldn't be read ({Error}); desktop windows are off", Marshal.GetLastPInvokeError());
            return false;
        }

        return (flags.DwFlags & Win32.WSF_VISIBLE) != 0;
    }
}
