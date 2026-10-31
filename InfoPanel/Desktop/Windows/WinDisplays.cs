using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace InfoPanel.Desktop.Windows;

/// <summary>A monitor as Windows lists it: its GDI device name and its rectangle in physical pixels of the virtual screen.</summary>
internal readonly record struct MonitorRecord(string Device, int Left, int Top, int Right, int Bottom, bool Primary);

/// <summary>
/// The displays on Windows. On a per-monitor DPI aware (v2) thread every monitor's rectangle is in physical pixels of
/// one virtual screen whose origin is the primary's top-left, y down: exactly <see cref="PointRect"/>'s space, so
/// desktop coordinates on Windows are pixels and every display's scale is 1 (1.4.x's coordinates too).
/// <list type="bullet">
/// <item><c>Name</c>: the GDI device name (<c>\\.\DISPLAY2</c>), what 1.4.x matched a window's monitor on;</item>
/// <item><c>Label</c>: the monitor's friendly name, as Windows Settings shows it ("LG ULTRAWIDE"), from the display
/// configuration; null when it has none (built-in panels often don't) or the query failed. With several paths on one
/// source (mirrored displays), the first friendly name.</item>
/// <item>Order: the primary first, then by device name (ordinal), as 1.4.x's picker listed them.</item>
/// </list>
/// </summary>
internal static class WinDisplays
{
    /// <summary>
    /// <paramref name="monitors"/> as displays: the primary first, then by device name; each at scale 1 with its friendly
    /// name from <paramref name="friendlyNames"/> (by device name, any case) as its label when it has a non-empty one.
    /// </summary>
    public static IReadOnlyList<DisplayInfo> Map(IEnumerable<MonitorRecord> monitors, IReadOnlyDictionary<string, string> friendlyNames) =>
        monitors
            .OrderByDescending(m => m.Primary)
            .ThenBy(m => m.Device, StringComparer.Ordinal)
            .Select(m => new DisplayInfo(
                m.Device,
                new PointRect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top),
                1,
                friendlyNames.TryGetValue(m.Device, out var label) && !string.IsNullOrWhiteSpace(label) ? label.Trim() : null))
            .ToList();

    /// <summary>
    /// The friendly name of each source's GDI device name from (source name, friendly name) pairs, one per display path:
    /// the first non-empty name a device has wins (mirrored displays have several paths on one source).
    /// </summary>
    public static Dictionary<string, string> FriendlyNames(IEnumerable<(string? Device, string? Friendly)> paths)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (device, friendly) in paths)
        {
            var name = friendly?.Trim();
            if (!string.IsNullOrEmpty(device) && !string.IsNullOrEmpty(name))
                names.TryAdd(device.Trim(), name);
        }

        return names;
    }

    private static readonly Serilog.ILogger Logger = Log.ForContext(typeof(WinDisplays));

    /// <summary>Friendly names by device name, read again when a listed device isn't in it or after <see cref="Invalidate"/> (UI thread).</summary>
    private static Dictionary<string, string>? _friendly;
    private static HashSet<string>? _friendlyDevices;

    /// <summary>The display configuration changed: the friendly names are read again next time (UI thread).</summary>
    public static void Invalidate()
    {
        _friendly = null;
        _friendlyDevices = null;
    }

    /// <summary>The displays now (a per-monitor DPI aware v2 thread: the desktop thread).</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<DisplayInfo> Read()
    {
        var monitors = Monitors();
        if (_friendly == null || _friendlyDevices == null || monitors.Any(m => !_friendlyDevices.Contains(m.Device)))
        {
            _friendly = ReadFriendlyNames();
            _friendlyDevices = monitors.Select(m => m.Device).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return Map(monitors, _friendly);
    }

    /// <summary>Every monitor, through <c>EnumDisplayMonitors</c> and <c>GetMonitorInfoW</c>.</summary>
    [SupportedOSPlatform("windows")]
    public static unsafe List<MonitorRecord> Monitors()
    {
        var handles = new List<nint>();
        var handle = GCHandle.Alloc(handles);
        try
        {
            Win32.EnumDisplayMonitors(0, null, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        var monitors = new List<MonitorRecord>(handles.Count);
        foreach (var monitor in handles)
        {
            var info = new MONITORINFOEXW { CbSize = (uint)sizeof(MONITORINFOEXW) };
            if (Win32.GetMonitorInfoW(monitor, &info) == 0)
                continue;
            var device = FixedString(info.SzDevice, 32);
            var r = info.RcMonitor;
            monitors.Add(new MonitorRecord(device, r.Left, r.Top, r.Right, r.Bottom, (info.DwFlags & Win32.MONITORINFOF_PRIMARY) != 0));
        }

        return monitors;
    }

    /// <summary>A fixed-size character field: up to its first NUL, never past its <paramref name="length"/>.</summary>
    internal static unsafe string FixedString(char* chars, int length)
    {
        var span = new ReadOnlySpan<char>(chars, length);
        var end = span.IndexOf('\0');
        return new string(end < 0 ? span : span[..end]);
    }

    [UnmanagedCallersOnly]
    private static unsafe int OnMonitor(nint monitor, nint hdc, RECT* rect, nint data)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is List<nint> list)
                list.Add(monitor);
        }
        catch
        {
            // Never unwind into user32.
        }

        return 1;
    }

    /// <summary>
    /// Each source's GDI device name and its monitor's friendly name, through <c>QueryDisplayConfig</c> and
    /// <c>DisplayConfigGetDeviceInfo</c> (1.4.x's <c>DisplayNameHelper</c>); empty on any failure, never throws.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static unsafe Dictionary<string, string> ReadFriendlyNames()
    {
        try
        {
            DISPLAYCONFIG_PATH_INFO[] paths;
            uint pathCount, modeCount;
            var attempt = 0;
            while (true)
            {
                var status = Win32.GetDisplayConfigBufferSizes(Win32.QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount);
                if (status != Win32.ERROR_SUCCESS)
                {
                    Logger.Debug("The display configuration's sizes couldn't be read ({Error})", status);
                    return FriendlyNames([]);
                }

                paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                fixed (DISPLAYCONFIG_PATH_INFO* p = paths)
                fixed (DISPLAYCONFIG_MODE_INFO* m = modes)
                    status = Win32.QueryDisplayConfig(Win32.QDC_ONLY_ACTIVE_PATHS, &pathCount, p, &modeCount, m, 0);

                // The configuration can change between the two calls: ask again for the new sizes.
                if (status == Win32.ERROR_INSUFFICIENT_BUFFER && ++attempt < 3)
                    continue;
                if (status != Win32.ERROR_SUCCESS)
                {
                    Logger.Debug("The display configuration couldn't be read ({Error})", status);
                    return FriendlyNames([]);
                }

                break;
            }

            var pairs = new List<(string?, string?)>((int)pathCount);
            for (var i = 0; i < pathCount; i++)
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    Type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    Size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME),
                    AdapterId = paths[i].SourceInfo.AdapterId,
                    Id = paths[i].SourceInfo.Id,
                };
                if (Win32.DisplayConfigGetDeviceInfo(&source.Header) != Win32.ERROR_SUCCESS)
                    continue;

                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    Type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    Size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME),
                    AdapterId = paths[i].TargetInfo.AdapterId,
                    Id = paths[i].TargetInfo.Id,
                };
                if (Win32.DisplayConfigGetDeviceInfo(&target.Header) != Win32.ERROR_SUCCESS)
                    continue;

                pairs.Add((FixedString(source.ViewGdiDeviceName, 32), FixedString(target.MonitorFriendlyDeviceName, 64)));
            }

            return FriendlyNames(pairs);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "The displays' friendly names couldn't be read");
            return FriendlyNames([]);
        }
    }
}
