using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace InfoPanel.Panels.Power;

/// <summary>
/// What a power source calls, on its own thread as each event arrives (one at a time, in order): <see cref="SleepArriving"/>
/// as the computer is about to sleep, which returns at once the sleep's work (the source runs it off its thread through
/// <see cref="PowerEvents.WaitForSuspending(PowerHandlers, TimeSpan, ILogger)"/> and waits for its task, at most
/// <see cref="PowerEvents.SuspendBound"/>, before letting it sleep; true when everything it stopped has stopped), and
/// <see cref="Resumed"/> once it is awake.
/// </summary>
public sealed record PowerHandlers(Func<Func<Task<bool>>> SleepArriving, Action Resumed);

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
    /// <summary>How long a source waits for the sleep's work (<see cref="PowerHandlers.SleepArriving"/>) before it lets the computer sleep anyway.</summary>
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
    /// A will-sleep, the one way a source delivers it (before it allows the sleep): tells <paramref name="handlers"/> it
    /// arrived, on the caller's thread (the source's, so it is numbered before any later event), then runs the sleep's
    /// work off that thread and waits for it, at most <paramref name="bound"/> from the call, its synchronous work
    /// included. True when it finished in time and says so; a handler that throws doesn't. The work's fault is caught
    /// where it runs and logged through <paramref name="logger"/>, even once the bound has passed, so none is left unobserved.
    /// </summary>
    public static bool WaitForSuspending(PowerHandlers handlers, TimeSpan bound, ILogger logger) =>
        WaitForSuspending(handlers, bound, logger, out _);

    /// <summary>As <see cref="WaitForSuspending(PowerHandlers, TimeSpan, ILogger)"/>; <paramref name="work"/> is the work's own task (tests).</summary>
    internal static bool WaitForSuspending(PowerHandlers handlers, TimeSpan bound, ILogger logger, out Task<bool> work)
    {
        Func<Task<bool>> suspending;
        try
        {
            suspending = handlers.SleepArriving();
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "USB panels: the sleep's handler failed");
            work = Task.FromResult(false);
            return false;
        }
        var allowed = false; // the source let the computer sleep without it
        work = Task.Run(async () =>
        {
            try
            {
                return await suspending().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, Volatile.Read(ref allowed)
                    ? "USB panels: the sleep's handler failed after the sleep was allowed"
                    : "USB panels: the sleep's handler failed");
                return false;
            }
        });
        if (work.Wait(bound))
            return work.Result;
        Volatile.Write(ref allowed, true);
        return false;
    }
}
