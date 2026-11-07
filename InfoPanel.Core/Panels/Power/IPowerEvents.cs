using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace InfoPanel.Panels.Power;

/// <summary>
/// What a power source calls: <see cref="Suspending"/> before the computer sleeps (the source waits for its task, at
/// most <see cref="PowerEvents.SuspendBound"/>, before letting it sleep; true when everything it stopped has stopped)
/// and <see cref="Resumed"/> once it is awake.
/// </summary>
public sealed record PowerHandlers(Func<Task<bool>> Suspending, Action Resumed);

/// <summary>
/// The computer's sleep and wake, from the platform (macOS: <see cref="MacPowerEvents"/>; Windows and Linux add theirs).
/// <see cref="Start"/> registers and delivers to the handlers until <see cref="IDisposable.Dispose"/>, which unregisters.
/// </summary>
public interface IPowerEvents : IDisposable
{
    /// <summary>What the source listens to, for the log.</summary>
    string Name { get; }

    /// <summary>Registers for sleep and wake; false when the platform refused (nothing is delivered then).</summary>
    bool Start(PowerHandlers handlers);
}

/// <summary>What every power source shares.</summary>
public static class PowerEvents
{
    /// <summary>How long a source waits for <see cref="PowerHandlers.Suspending"/> before it lets the computer sleep anyway.</summary>
    public static readonly TimeSpan SuspendBound = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A monotonic clock that keeps counting while the computer sleeps, where the platform's timestamps don't (macOS's
    /// <c>CLOCK_MONOTONIC_RAW</c>, beside the uptime clock .NET's timestamps read); null elsewhere. Clock corrections
    /// don't move it.
    /// </summary>
    public static Func<TimeSpan>? SinceBoot => OperatingSystem.IsMacOS() ? MacMonotonicRaw : null;

    [SupportedOSPlatform("macos")]
    private static TimeSpan MacMonotonicRaw() => TimeSpan.FromTicks((long)(clock_gettime_nsec_np(ClockMonotonicRaw) / 100));

    private const int ClockMonotonicRaw = 4; // _CLOCK_MONOTONIC_RAW, <time.h>

    [SupportedOSPlatform("macos")]
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern ulong clock_gettime_nsec_np(int clockId);

    /// <summary>
    /// Runs <paramref name="suspending"/> off the caller's thread and waits for it, at most <paramref name="bound"/> from
    /// the call, its synchronous work included (a source calls this before it allows the sleep). True when it finished in
    /// time and says so; a handler that throws doesn't.
    /// </summary>
    public static bool WaitForSuspending(Func<Task<bool>> suspending, TimeSpan bound)
    {
        try
        {
            var task = Task.Run(suspending);
            return task.Wait(bound) && task.Result;
        }
        catch (Exception)
        {
            return false; // it failed: nothing more to wait for
        }
    }
}
