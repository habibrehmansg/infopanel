using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using Point = System.Drawing.Point;

namespace InfoPanel.Utils
{
    public class ScreenHelper
    {
        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private const int CCHDEVICENAME = 32;
        private const uint MONITORINFOF_PRIMARY = 0x00000001;

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOZORDER = 0x0004;

        public static void MoveWindowPhysical(Window window, int x, int y)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        public static Point GetWindowPositionPhysical(Window window)
        {
            var hWnd = new WindowInteropHelper(window).Handle;
            GetWindowRect(hWnd, out var rect);
            return new Point(rect.Left, rect.Top);
        }

        public static MonitorInfo? GetWindowScreen(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (!GetWindowRect(hwnd, out var rect))
                return null;

            var windowPos = new Point(rect.Left, rect.Top);
            var monitors = GetAllMonitors();

            // Find the monitor whose bounds contain the window position
            foreach (var monitor in monitors)
            {
                if (monitor.Bounds.Contains(windowPos))
                {
                    return monitor;
                }
            }

            // If not contained (e.g., overlapping), return the closest by distance
            return monitors
                .OrderBy(m => DistanceSquared(windowPos, m.Bounds))
                .FirstOrDefault();
        }

        private static double DistanceSquared(Point point, Rectangle rect)
        {
            int centerX = rect.Left + rect.Width / 2;
            int centerY = rect.Top + rect.Height / 2;
            int dx = (int)(centerX - point.X);
            int dy = (int)(centerY - point.Y);
            return dx * dx + dy * dy;
        }

        public static Point GetWindowRelativePosition(MonitorInfo screen, Point absolutePosition)
        {
            var relativeX = absolutePosition.X - screen.Bounds.X;
            var relativeY = absolutePosition.Y - screen.Bounds.Y;

            return new Point(relativeX, relativeY);
        }

        // Get fresh monitor list using Win32 API
        public static List<MonitorInfo> GetAllMonitors()
        {
            var monitors = new List<MonitorInfo>();

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                var info = new MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(info);

                if (GetMonitorInfo(hMonitor, ref info))
                {
                    monitors.Add(new MonitorInfo
                    {
                        DeviceName = info.szDevice,
                        Bounds = new Rectangle(info.rcMonitor.Left, info.rcMonitor.Top,
                            info.rcMonitor.Right - info.rcMonitor.Left,
                            info.rcMonitor.Bottom - info.rcMonitor.Top),
                        WorkingArea = new Rectangle(info.rcWork.Left, info.rcWork.Top,
                            info.rcWork.Right - info.rcWork.Left,
                            info.rcWork.Bottom - info.rcWork.Top),
                        IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0
                    });
                }
                return true;
            }, IntPtr.Zero);

            return monitors;
        }
    }

    public class MonitorInfo
    {
        public string? DeviceName { get; set; }
        public Rectangle Bounds { get; set; }
        public Rectangle WorkingArea { get; set; }
        public bool IsPrimary { get; set; }

        public override string ToString()
        {
            return $"Monitor: {DeviceName}, Bounds={Bounds}, WorkingArea={WorkingArea}, Primary={IsPrimary}";
        }
    }
}
