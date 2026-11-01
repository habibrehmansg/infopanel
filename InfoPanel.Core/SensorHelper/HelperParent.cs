using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace InfoPanel.SensorHelper;

/// <summary>
/// The engine's process as the helper holds it: opened once by its id, its start time read from that same opening, and
/// a task that completes when that process ends. The helper compares the start time with the one the engine passed
/// (<see cref="HelperCommandLine.ParentStart"/>), so a process that took the engine's id after the engine ended is
/// never mistaken for it.
/// </summary>
public interface IHelperParent : IDisposable
{
    /// <summary>When the process started, as <see cref="HelperParent.CurrentStartTime"/> gives it.</summary>
    long StartTime { get; }

    /// <summary>Completes when the process ends.</summary>
    Task Exited { get; }
}

/// <summary>
/// Opens the engine's process for the helper (<see cref="IHelperParent"/>). On Windows one handle does all of it
/// (<c>OpenProcess</c> for synchronize and limited query, which a normal engine grants an elevated helper and the
/// other way round; <c>GetProcessTimes</c> for the start time, in UTC; a wait on that handle for the end); elsewhere,
/// where the helper itself never runs and only the tests' stand-in does, <see cref="Process"/> does.
/// </summary>
public static class HelperParent
{
    /// <summary>The process <paramref name="pid"/>, or null when it isn't running or can't be read.</summary>
    public static IHelperParent? Open(int pid) =>
        OperatingSystem.IsWindows() ? WindowsParent.Open(pid) : ProcessParent.Open(pid);

    /// <summary>This process's start time, read as <see cref="Open"/> reads another's: what the engine passes the helper.</summary>
    public static long CurrentStartTime()
    {
        using var self = Open(Environment.ProcessId) ?? throw new InvalidOperationException("This process's start time can't be read.");
        return self.StartTime;
    }

    private sealed class ProcessParent : IHelperParent
    {
        private readonly Process _process;

        private ProcessParent(Process process, long startTime)
        {
            _process = process;
            StartTime = startTime;
            Exited = Wait(process);
        }

        public long StartTime { get; }

        public Task Exited { get; }

        public static ProcessParent? Open(int pid)
        {
            Process process;
            try
            {
                process = Process.GetProcessById(pid);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return null;
            }

            try
            {
                return new ProcessParent(process, process.StartTime.ToUniversalTime().Ticks);
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                process.Dispose();
                return null;
            }
        }

        private static async Task Wait(Process process)
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // It ended before it could be waited on.
            }
        }

        public void Dispose() => _process.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsParent : IHelperParent
    {
        private const uint Synchronize = 0x0010_0000;
        private const uint QueryLimitedInformation = 0x1000;
        private const uint WaitObject0 = 0;
        private const uint StillActive = 259;

        private readonly SafeProcessHandle _handle;
        private readonly ProcessWaitHandle _waitHandle;
        private readonly RegisteredWaitHandle _wait;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private WindowsParent(SafeProcessHandle handle, long startTime)
        {
            _handle = handle;
            StartTime = startTime;
            _waitHandle = new ProcessWaitHandle(handle);

            // The wait holds this object as its state, so the handle's owner stays reachable for as long as the wait
            // is registered, whoever keeps only Exited: the wait handle doesn't own the process handle, and a collected
            // owner would close it under the thread pool's wait (its value then free for another handle to take).
            _wait = ThreadPool.RegisterWaitForSingleObject(_waitHandle, static (state, _) => ((WindowsParent)state!)._exited.TrySetResult(), this, Timeout.Infinite, executeOnlyOnce: true);
        }

        public long StartTime { get; }

        public Task Exited => _exited.Task;

        public static WindowsParent? Open(int pid)
        {
            var handle = new SafeProcessHandle(OpenProcess(Synchronize | QueryLimitedInformation, false, pid), ownsHandle: true);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }

            if (!GetProcessTimes(handle, out var created, out _, out _, out _) || HasEnded(handle))
            {
                handle.Dispose();
                return null;
            }

            return new WindowsParent(handle, created);
        }

        /// <summary>
        /// Whether the process ended or is ending: one that ended stays openable while anything holds a handle to it, and
        /// one being terminated has its exit code before its handle is signalled (as <see cref="Process.HasExited"/> reads it).
        /// </summary>
        private static bool HasEnded(SafeProcessHandle handle) =>
            WaitForSingleObject(handle, 0) == WaitObject0 || (GetExitCodeProcess(handle, out var code) && code != StillActive);

        public void Dispose()
        {
            _wait.Unregister(null);
            _waitHandle.Dispose();
            _handle.Dispose();
        }

        /// <summary>The process handle as something to wait on; it doesn't own the handle.</summary>
        private sealed class ProcessWaitHandle : WaitHandle
        {
            public ProcessWaitHandle(SafeProcessHandle process) =>
                SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    }
}
