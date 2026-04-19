using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace InfoPanel.Utils
{
    /// <summary>Helpers for detecting the foreground window and its process (for program-specific panels).</summary>
    public static class ForegroundWindowHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(ForegroundWindowHelper));

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint MAX_PATH_EXTENDED = 32767;

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
            IntPtr hwnd;
            uint processId;
            try
            {
                hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                    return null;

                _ = GetWindowThreadProcessId(hwnd, out processId);
                if (processId == 0)
                    return null;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Foreground window lookup failed");
                return null;
            }

            var name = GetProcessNameByLimitedQuery(processId);
            if (name != null)
                return name;

            try
            {
                using var process = Process.GetProcessById((int)processId);
                return process.ProcessName;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Process.GetProcessById fallback failed for pid {ProcessId}", processId);
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
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == ERROR_INSUFFICIENT_BUFFER)
                    {
                        capacity = MAX_PATH_EXTENDED;
                        buffer = new char[capacity];
                        if (!QueryFullProcessImageNameW(hProcess, PROCESS_NAME_WIN32, buffer, ref capacity))
                        {
                            Logger.Debug("QueryFullProcessImageNameW retry failed for pid {ProcessId}, win32 error {Error}", processId, Marshal.GetLastWin32Error());
                            return null;
                        }
                    }
                    else
                    {
                        Logger.Debug("QueryFullProcessImageNameW failed for pid {ProcessId}, win32 error {Error}", processId, err);
                        return null;
                    }
                }

                var path = new string(buffer, 0, (int)capacity);
                return Path.GetFileNameWithoutExtension(path);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "GetProcessNameByLimitedQuery failed for pid {ProcessId}", processId);
                return null;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }
    }
}
