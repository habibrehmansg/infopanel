using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InfoPanel.Scenes;

/// <summary>
/// The calling thread's processor time (user and system together), for the scene watchdog: how much of an evaluation's
/// or a draw's wall time its thread actually ran. macOS and Linux read the thread's <c>clock_gettime</c> clock, Windows
/// <c>GetThreadTimes</c> (which may count in scheduler quanta, about 15.6 ms). Null where the call fails.
/// </summary>
internal static class ThreadClock
{
    private static volatile bool _unavailable;

    /// <summary>The calling thread's processor time so far; null when it can't be read.</summary>
    public static TimeSpan? Now() => Now(IntPtr.Size);

    /// <summary>
    /// As <see cref="Now()"/> for a process whose pointers are <paramref name="pointerSize"/> bytes. The Linux
    /// <c>timespec</c> is read as two 64-bit fields, which is wrong in a 32-bit process (ARM32's are 32-bit): none then.
    /// </summary>
    internal static TimeSpan? Now(int pointerSize)
    {
        if (_unavailable || pointerSize == 4) return null;
        try
        {
            if (OperatingSystem.IsMacOS())
                return clock_gettime_nsec_np(MacThreadClock) is var ns and > 0 ? TimeSpan.FromTicks((long)(ns / 100)) : null;
            if (OperatingSystem.IsLinux())
                return clock_gettime(LinuxThreadClock, out var ts) == 0 ? TimeSpan.FromTicks(ts.Seconds * TimeSpan.TicksPerSecond + ts.Nanoseconds / 100) : null;
            if (OperatingSystem.IsWindows())
                return GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? TimeSpan.FromTicks(kernel + user) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not on this system: never asked again.
        }
        _unavailable = true;
        return null;
    }

    /// <summary>
    /// The thread's QoS class on macOS (<c>qos_class_self</c>): <c>userInteractive</c>, <c>userInitiated</c>,
    /// <c>default</c>, <c>utility</c>, <c>background</c>, <c>unspecified</c>, or the number in hex; null elsewhere.
    /// </summary>
    public static string? QosClass()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            return qos_class_self() switch
            {
                0x21 => "userInteractive",
                0x19 => "userInitiated",
                0x15 => "default",
                0x11 => "utility",
                0x09 => "background",
                0x00 => "unspecified",
                var other => $"0x{other:x}",
            };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private const int MacThreadClock = 16; // CLOCK_THREAD_CPUTIME_ID, <time.h>
    private const int LinuxThreadClock = 3; // CLOCK_THREAD_CPUTIME_ID, <linux/time.h>

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [SupportedOSPlatform("macos")]
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern ulong clock_gettime_nsec_np(int clockId);

    [SupportedOSPlatform("macos")]
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern uint qos_class_self();

    [SupportedOSPlatform("linux")]
    [DllImport("libc")]
    private static extern int clock_gettime(int clockId, out Timespec time);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentThread();

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
}

/// <summary>
/// A watchdog clock (<see cref="SceneRuntimeOptions.DrawTime"/>) that gives the thread's processor time too, in place of
/// <see cref="ThreadClock"/>: tests drive both.
/// </summary>
internal interface IThreadTime
{
    TimeSpan? ThreadTime();

    /// <summary>Who draws, in place of the stack walk (<c>TickSnapshot</c>); null to walk it.</summary>
    string? Drawer() => null;
}

/// <summary>
/// What a watched evaluation or draw started from: the thread's processor time, and the process's garbage-collection
/// pauses and collections. Read at every watched span; its figures are worked out only for a slow one (<see cref="Slow"/>).
/// </summary>
internal readonly struct WatchSpan
{
    private readonly IThreadTime? _clock;
    private readonly TimeSpan? _thread;
    private readonly TimeSpan _pause;
    private readonly int _gen0, _gen1, _gen2;

    private WatchSpan(IThreadTime? clock)
    {
        _clock = clock;
        _thread = clock is null ? ThreadClock.Now() : clock.ThreadTime();
        _pause = GC.GetTotalPauseDuration();
        _gen0 = GC.CollectionCount(0);
        _gen1 = GC.CollectionCount(1);
        _gen2 = GC.CollectionCount(2);
    }

    /// <summary>Starts a span on the calling thread, its processor time read from <paramref name="time"/> when it gives one.</summary>
    public static WatchSpan Start(TimeProvider time) => new(time as IThreadTime);

    /// <summary>A slow span's figures, read now, on the thread that started it.</summary>
    public SlowMark Slow(long tick, string stage, double wallMs, double? scale = null, string? drawer = null)
    {
        var thread = _clock is null ? ThreadClock.Now() : _clock.ThreadTime();
        return new SlowMark(tick, stage, wallMs, thread - _thread is { } t ? t.TotalMilliseconds : null,
            (GC.GetTotalPauseDuration() - _pause).TotalMilliseconds,
            GC.CollectionCount(0) - _gen0, GC.CollectionCount(1) - _gen1, GC.CollectionCount(2) - _gen2,
            scale, drawer, ThreadClock.QosClass());
    }
}

/// <summary>
/// One slow evaluation or draw, as the watchdog saw it (logged with the suspension it leads to): its tick, which it was
/// (<c>evaluate</c> or <c>draw</c>), its wall time, its thread's processor time (null where unknown), the garbage-collection
/// pause time and the collections of each generation in the process over it, and for a draw its scale and who drew it
/// (<c>loop</c>, <c>thumbnail</c> or <c>other</c>); on macOS the thread's QoS class. Times in milliseconds, uncounted
/// (a draw's wall time is not divided by its scale²).
/// </summary>
internal sealed record SlowMark(long Tick, string Stage, double WallMs, double? ThreadMs, double GcPauseMs, int Gen0, int Gen1, int Gen2,
    double? Scale, string? Drawer, string? Qos);
