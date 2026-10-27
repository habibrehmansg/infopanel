using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace InfoPanel.DataApi
{
    /// <summary>
    /// Overall CPU and RAM load from Win32 (GetSystemTimes /
    /// GlobalMemoryStatusEx), so the Data API has basic system values even
    /// when LibreHardwareMonitor and HWiNFO are off. Served as system.*.
    /// </summary>
    internal sealed class SystemMetrics
    {
        private long _lastIdle;
        private long _lastKernel;
        private long _lastUser;
        private bool _hasBaseline;
        private double? _cpuLoad;

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        /// <summary>
        /// Adds the system.* values. CPU load is the busy share since the
        /// previous call, so the first call after start reports no CPU value.
        /// </summary>
        public void AddTo(Dictionary<string, DataApiValue> sensors)
        {
            if (GetSystemTimes(out long idle, out long kernel, out long user))
            {
                if (_hasBaseline)
                {
                    // Kernel time includes idle time: busy = kernel + user - idle.
                    long total = (kernel - _lastKernel) + (user - _lastUser);
                    if (total > 0)
                    {
                        long busy = total - (idle - _lastIdle);
                        _cpuLoad = Math.Clamp(Math.Round((double)busy / total * 100, 1), 0, 100);
                    }
                }
                _lastIdle = idle;
                _lastKernel = kernel;
                _lastUser = user;
                _hasBaseline = true;
            }

            if (_cpuLoad is double cpu)
            {
                sensors["system.cpu.load"] = Percent(cpu, "CPU Load");
            }

            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
            {
                var ram = Math.Round((mem.ullTotalPhys - mem.ullAvailPhys) / (double)mem.ullTotalPhys * 100, 1);
                sensors["system.ram.load"] = Percent(ram, "RAM Load");
                sensors["system.ram.free"] = Percent(Math.Round(100 - ram, 1), "RAM Free");
            }

            // Kept for dashboards built against the DataBridge plugin.
            sensors["system.bridge.status"] = new DataApiValue { Text = "running", Name = "Data API Status", Source = "system" };
            sensors["system.bridge.time"] = new DataApiValue { Text = DateTime.Now.ToString("HH:mm:ss"), Name = "Data API Time", Source = "system" };
        }

        private static DataApiValue Percent(double value, string name) => new()
        {
            Value = value,
            Unit = "%",
            Min = 0,
            Max = 100,
            Name = name,
            Source = "system",
        };
    }
}
