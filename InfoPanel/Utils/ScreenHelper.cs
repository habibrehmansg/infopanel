using ControlzEx.Standard;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

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

        // Force refresh of screen information
        public static void RefreshScreens()
        {
            // This forces Windows Forms to refresh its cached screen data
            var dummy = Screen.PrimaryScreen;
            var allScreens = Screen.AllScreens;

            // Alternative: Use reflection to clear the cache (if available)
            try
            {
                var screenType = typeof(Screen);
                var screensField = screenType.GetField("screens", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (screensField != null)
                {
                    screensField.SetValue(null, null);
                }
            }
            catch
            {
                // Ignore if reflection fails
            }
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
        public string DeviceName { get; set; }
        public Rectangle Bounds { get; set; }
        public Rectangle WorkingArea { get; set; }
        public bool IsPrimary { get; set; }
    }
}
