using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace InfoPanel.Utils
{
    /// <summary>Helpers for detecting the foreground window and its process (for program-specific panels).</summary>
    public static class ForegroundWindowHelper
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);

        private const uint PROCESS_NAME_WIN32 = 0;

        /// <summary>Gets the process name of the current foreground window (e.g. "chrome", "Cyberpunk2077"). Returns null if unavailable.
        /// Uses PROCESS_QUERY_LIMITED_INFORMATION so elevated (admin) foreground apps can be detected without running InfoPanel as admin.</summary>
        public static string? GetForegroundProcessName()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                    return null;

                _ = GetWindowThreadProcessId(hwnd, out uint processId);
                if (processId == 0)
                    return null;

                // Prefer limited query so we can read elevated processes without admin
                var name = GetProcessNameByLimitedQuery(processId);
                if (name != null)
                {
                    return name;
                }

                // Fallback for same-process or when limited query isn't allowed (e.g. different user)
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    var fallbackName = process.ProcessName;
                    return fallbackName;
                }
                catch
                {
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string? GetProcessNameByLimitedQuery(uint processId)
        {
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess == IntPtr.Zero)
                return null;

            try
            {
                uint capacity = 260;
                var buffer = new char[capacity];
                if (!QueryFullProcessImageNameW(hProcess, PROCESS_NAME_WIN32, buffer, ref capacity))
                    return null;

                var path = new string(buffer, 0, (int)capacity);
                return Path.GetFileNameWithoutExtension(path);
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }
    }
}
