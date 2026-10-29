using System.Runtime.InteropServices;
using Xunit;

namespace InfoPanel.Core.Tests;

/// <summary>
/// FIFOs for the tests that hold the engine to reading only regular files (Unix only: Windows has no FIFOs in folders).
/// Opening a FIFO to read waits for a writer, so a reader that doesn't check first would block for good:
/// <see cref="Within{T}"/> bounds the wait in the test, and lets such a reader go when it fails.
/// </summary>
internal static class Fifo
{
    /// <summary>Whether this platform has FIFOs (every one but Windows).</summary>
    public static bool Supported => !OperatingSystem.IsWindows();

    /// <summary>Makes a FIFO at <paramref name="path"/>, owner read and write.</summary>
    public static void Create(string path)
    {
        if (mkfifo(path, 0x180) != 0)
            throw new IOException($"mkfifo {path} failed (errno {Marshal.GetLastPInvokeError()}).");
    }

    /// <summary>
    /// <paramref name="action"/>'s answer, or a failed test when it hasn't answered within <paramref name="seconds"/>
    /// (it is blocked, on a FIFO say); <paramref name="fifos"/> are then opened and closed at each end, so a reader or
    /// writer blocked on one goes on and doesn't hold the test run. An exception it throws is thrown as it is.
    /// </summary>
    public static T Within<T>(int seconds, Func<T> action, params string[] fifos)
    {
        var task = Task.Run(action);
        if (Task.WaitAny([task], TimeSpan.FromSeconds(seconds)) < 0)
        {
            for (var round = 0; round < 3 && !task.IsCompleted; round++)
            {
                foreach (var fifo in fifos)
                    Release(fifo);
                task.Wait(TimeSpan.FromMilliseconds(200));
            }
            Assert.Fail($"Still blocked after {seconds} s (on a FIFO?).");
        }

        return task.GetAwaiter().GetResult();
    }

    /// <inheritdoc cref="Within{T}"/>
    public static void Within(int seconds, Action action, params string[] fifos) =>
        Within(seconds, () => { action(); return true; }, fifos);

    /// <summary>
    /// Opens <paramref name="fifo"/> without waiting, for writing (only a waiting reader lets it open) and for reading (which
    /// lets a waiting writer's open go on), and closes it.
    /// </summary>
    private static void Release(string fifo)
    {
        foreach (var mode in new[] { O_WRONLY, O_RDONLY })
        {
            var fd = open(fifo, mode | (OperatingSystem.IsMacOS() ? 0x0004 : 0x0800));
            if (fd >= 0)
                close(fd);
        }
    }

    private const int O_RDONLY = 0, O_WRONLY = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc")]
    private static extern int close(int fd);
}
