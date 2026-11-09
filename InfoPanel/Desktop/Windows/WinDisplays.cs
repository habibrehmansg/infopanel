using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace InfoPanel.Desktop.Windows;

/// <summary>A monitor as Windows lists it: its GDI device name and its rectangle in physical pixels of the virtual screen.</summary>
internal readonly record struct MonitorRecord(string Device, int Left, int Top, int Right, int Bottom, bool Primary);

/// <summary>
/// One display path as the display configuration lists it: its source's GDI device name, its target monitor's friendly
/// name, the monitor's id (<see cref="WinDisplays.IdOf"/>, null without valid EDID ids) and its device path.
/// </summary>
internal readonly record struct TargetName(string Device, string Friendly, string? Id, string DevicePath);

/// <summary>
/// The displays on Windows. On a per-monitor DPI aware (v2) thread every monitor's rectangle is in physical pixels of
/// one virtual screen whose origin is the primary's top-left, y down: exactly <see cref="PointRect"/>'s space, so
/// desktop coordinates on Windows are pixels and every display's scale is 1 (1.4.x's coordinates too).
/// <list type="bullet">
/// <item><c>Name</c>: the GDI device name (<c>\\.\DISPLAY2</c>), what 1.4.x matched a window's monitor on;</item>
/// <item><c>Label</c>: the monitor's friendly name, as Windows Settings shows it ("LG ULTRAWIDE"), from the display
/// configuration; null when it has none (built-in panels often don't) or the query failed.</item>
/// <item><c>Id</c>: the same monitor's EDID maker and product code (<see cref="IdOf"/>); null when it reports no valid
/// EDID ids (a virtual display, Remote Desktop's) or the query failed.</item>
/// <item>With several paths on one source (mirrored displays), one of them gives both: the first with a friendly name,
/// else the first (<see cref="Sources"/>), so the label and the id are always one monitor's.</item>
/// <item>Order: the primary first, then by device name (ordinal), as 1.4.x's picker listed them.</item>
/// </list>
/// </summary>
internal static class WinDisplays
{
    /// <summary><c>DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS</c>' <c>edidIdsValid</c>: the EDID maker and product code are set.</summary>
    internal const uint EdidIdsValid = 0x4;

    /// <summary>
    /// <paramref name="monitors"/> as displays: the primary first, then by device name; each at scale 1 with its source's
    /// target from <paramref name="sources"/> (by device name, any case; <see cref="Sources"/>): its friendly name as the
    /// label when it has a non-empty one, and its monitor id when it has one.
    /// </summary>
    public static IReadOnlyList<DisplayInfo> Map(
        IEnumerable<MonitorRecord> monitors,
        IReadOnlyDictionary<string, TargetName> sources) =>
        monitors
            .OrderByDescending(m => m.Primary)
            .ThenBy(m => m.Device, StringComparer.Ordinal)
            .Select(m =>
            {
                var found = sources.TryGetValue(m.Device, out var target);
                return new DisplayInfo(
                    m.Device,
                    new PointRect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top),
                    1,
                    found && !string.IsNullOrWhiteSpace(target.Friendly) ? target.Friendly.Trim() : null,
                    found ? target.Id : null);
            })
            .ToList();

    /// <summary>
    /// A monitor's id (<see cref="DisplayMatcher.IdOf"/>, <c>DEL40F4</c>, the string macOS gives for the same monitor)
    /// from a <c>DISPLAYCONFIG_TARGET_DEVICE_NAME</c>'s flags and EDID words: null unless <see cref="EdidIdsValid"/> is
    /// set. Windows gives the maker word as the EDID's bytes 8 and 9 read in memory order (Dell's 10 AC as 0xAC10), so
    /// its bytes are swapped into the big-endian PNP word first; the product code is the number itself.
    /// </summary>
    public static string? IdOf(uint flags, ushort maker, ushort product) =>
        (flags & EdidIdsValid) == 0 ? null : DisplayMatcher.IdOf(BinaryPrimitives.ReverseEndianness(maker), product);

    /// <summary>
    /// One target for each source's GDI device name (any case) from the display paths: the first path with a non-empty
    /// friendly name, else the first path (mirrored displays have several paths on one source). The label and the id are
    /// both taken from it, so they always describe one monitor; its id may be null.
    /// </summary>
    public static Dictionary<string, TargetName> Sources(IEnumerable<TargetName> paths)
    {
        var sources = new Dictionary<string, TargetName>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path.Device))
                continue;
            var device = path.Device.Trim();
            if (!sources.TryGetValue(device, out var kept) || (string.IsNullOrWhiteSpace(kept.Friendly) && !string.IsNullOrWhiteSpace(path.Friendly)))
                sources[device] = path;
        }

        return sources;
    }

    private static readonly Serilog.ILogger Logger = Log.ForContext(typeof(WinDisplays));

    /// <summary>
    /// Each source's target (its friendly name and monitor id) by device name, read again when a listed device isn't in
    /// them or after <see cref="Invalidate"/> (UI thread).
    /// </summary>
    private static Dictionary<string, TargetName>? _sources;
    private static HashSet<string>? _sourceDevices;

    /// <summary>The display configuration changed: the friendly names and ids are read again next time (UI thread).</summary>
    public static void Invalidate()
    {
        _sources = null;
        _sourceDevices = null;
    }

    /// <summary>The displays now (a per-monitor DPI aware v2 thread: the desktop thread).</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<DisplayInfo> Read()
    {
        var monitors = Monitors();
        if (_sources == null || _sourceDevices == null || monitors.Any(m => !_sourceDevices.Contains(m.Device)))
        {
            _sources = Sources(ReadTargetNames());
            _sourceDevices = monitors.Select(m => m.Device).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return Map(monitors, _sources);
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
    /// Each active path's source GDI device name, its monitor's friendly name and id, through <c>QueryDisplayConfig</c>
    /// and <c>DisplayConfigGetDeviceInfo</c> (1.4.x's <c>DisplayNameHelper</c>); empty on any failure, never throws.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static unsafe List<TargetName> ReadTargetNames()
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
                    return [];
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
                    return [];
                }

                break;
            }

            var targets = new List<TargetName>((int)pathCount);
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

                targets.Add(new TargetName(
                    FixedString(source.ViewGdiDeviceName, 32),
                    FixedString(target.MonitorFriendlyDeviceName, 64),
                    IdOf(target.Flags, target.EdidManufactureId, target.EdidProductCodeId),
                    FixedString(target.MonitorDevicePath, 128)));
            }

            return targets;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "The displays' friendly names and ids couldn't be read");
            return [];
        }
    }
}
