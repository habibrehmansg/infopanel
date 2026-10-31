using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using InfoPanel.Desktop.Windows;
using Xunit;

namespace InfoPanel.Tests.Desktop.Windows;

/// <summary>
/// The Win32 structures' sizes and the offsets the code reads, as the 64-bit Windows headers lay them out (x64 and
/// arm64 alike). A wrong or missing field shifts every field after it without any error, so they are pinned here, on
/// any 64-bit OS.
/// </summary>
public sealed class Win32LayoutTests
{
    [Fact]
    public void TheStructures_HaveTheirWindowsSizes()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(48, Unsafe.SizeOf<MSG>());
        Assert.Equal(80, Unsafe.SizeOf<WNDCLASSEXW>());
        Assert.Equal(104, Unsafe.SizeOf<MONITORINFOEXW>());
        Assert.Equal(40, Unsafe.SizeOf<MINMAXINFO>());
        Assert.Equal(40, Unsafe.SizeOf<BITMAPINFOHEADER>());
        Assert.Equal(4, Unsafe.SizeOf<BLENDFUNCTION>());
        Assert.Equal(12, Unsafe.SizeOf<USEROBJECTFLAGS>());
        Assert.Equal(8, Unsafe.SizeOf<LUID>());
        Assert.Equal(20, Unsafe.SizeOf<DISPLAYCONFIG_PATH_SOURCE_INFO>());
        Assert.Equal(8, Unsafe.SizeOf<DISPLAYCONFIG_RATIONAL>());
        Assert.Equal(48, Unsafe.SizeOf<DISPLAYCONFIG_PATH_TARGET_INFO>());
        Assert.Equal(72, Unsafe.SizeOf<DISPLAYCONFIG_PATH_INFO>());
        Assert.Equal(64, Unsafe.SizeOf<DISPLAYCONFIG_MODE_INFO>());
        Assert.Equal(20, Unsafe.SizeOf<DISPLAYCONFIG_DEVICE_INFO_HEADER>());
        Assert.Equal(84, Unsafe.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>());
        Assert.Equal(420, Unsafe.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>());
        Assert.Equal(16, Unsafe.SizeOf<RECT>());
        Assert.Equal(8, Unsafe.SizeOf<POINT>());
        Assert.Equal(8, Unsafe.SizeOf<SIZE>());
    }

    [Fact]
    public void TheFieldsTheCodeReads_AreAtTheirWindowsOffsets()
    {
        Assert.Equal(8, Offset<MSG>(nameof(MSG.Message)));
        Assert.Equal(16, Offset<MSG>(nameof(MSG.WParam)));
        Assert.Equal(24, Offset<MSG>(nameof(MSG.LParam)));
        Assert.Equal(36, Offset<MSG>(nameof(MSG.Pt)));

        Assert.Equal(8, Offset<WNDCLASSEXW>(nameof(WNDCLASSEXW.LpfnWndProc)));
        Assert.Equal(24, Offset<WNDCLASSEXW>(nameof(WNDCLASSEXW.HInstance)));
        Assert.Equal(64, Offset<WNDCLASSEXW>(nameof(WNDCLASSEXW.LpszClassName)));

        Assert.Equal(4, Offset<MONITORINFOEXW>(nameof(MONITORINFOEXW.RcMonitor)));
        Assert.Equal(36, Offset<MONITORINFOEXW>(nameof(MONITORINFOEXW.DwFlags)));
        Assert.Equal(40, Offset<MONITORINFOEXW>(nameof(MONITORINFOEXW.SzDevice)));

        Assert.Equal(24, Offset<MINMAXINFO>(nameof(MINMAXINFO.PtMinTrackSize)));
        Assert.Equal(32, Offset<MINMAXINFO>(nameof(MINMAXINFO.PtMaxTrackSize)));

        Assert.Equal(3, Offset<BLENDFUNCTION>(nameof(BLENDFUNCTION.AlphaFormat)));
        Assert.Equal(8, Offset<USEROBJECTFLAGS>(nameof(USEROBJECTFLAGS.DwFlags)));

        Assert.Equal(20, Offset<DISPLAYCONFIG_PATH_INFO>(nameof(DISPLAYCONFIG_PATH_INFO.TargetInfo)));
        Assert.Equal(68, Offset<DISPLAYCONFIG_PATH_INFO>(nameof(DISPLAYCONFIG_PATH_INFO.Flags)));
        Assert.Equal(8, Offset<DISPLAYCONFIG_PATH_SOURCE_INFO>(nameof(DISPLAYCONFIG_PATH_SOURCE_INFO.Id)));
        Assert.Equal(8, Offset<DISPLAYCONFIG_PATH_TARGET_INFO>(nameof(DISPLAYCONFIG_PATH_TARGET_INFO.Id)));

        Assert.Equal(20, Offset<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(nameof(DISPLAYCONFIG_SOURCE_DEVICE_NAME.ViewGdiDeviceName)));
        Assert.Equal(36, Offset<DISPLAYCONFIG_TARGET_DEVICE_NAME>(nameof(DISPLAYCONFIG_TARGET_DEVICE_NAME.MonitorFriendlyDeviceName)));
        Assert.Equal(164, Offset<DISPLAYCONFIG_TARGET_DEVICE_NAME>(nameof(DISPLAYCONFIG_TARGET_DEVICE_NAME.MonitorDevicePath)));
    }

    private static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);
}
