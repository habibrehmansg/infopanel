using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace InfoPanel.Sensors.HwInfo;

/// <summary>
/// Whether HWiNFO runs, for the HWiNFO sources' freshness and states: a <c>HWiNFO64</c> process in this process's
/// session, never in session 0.
/// </summary>
public static class HwInfoProcess
{
    /// <summary>Whether a <c>HWiNFO64</c> process runs in this process's session, never in session 0.</summary>
    public static bool IsRunning() => RunningIn(CurrentSession(), Sessions)();

    /// <summary>The process check over <paramref name="hwinfoSessions"/>, the sessions <c>HWiNFO64</c> processes run in (a test's).</summary>
    /// <remarks>
    /// Only a HWiNFO in <paramref name="session"/> counts: another user's, in another session, writes their own hive,
    /// and would otherwise keep this user's abandoned values live. In session 0 none does: every user's services
    /// share it, so a HWiNFO there may be another user's (a user's engine runs in their own session). Matching the
    /// process's owner isn't attempted: a normal user may not read the token of HWiNFO, which runs elevated.
    /// </remarks>
    public static Func<bool> RunningIn(int session, Func<IReadOnlyList<int>> hwinfoSessions) =>
        session == 0 ? () => false : () => hwinfoSessions().Contains(session);

    /// <summary>
    /// The session of each <c>HWiNFO64</c> process: names and sessions are listed to the normal user, elevated ones and
    /// other users' too. One that exits meanwhile is left out.
    /// </summary>
    public static IReadOnlyList<int> Sessions()
    {
        var processes = Process.GetProcessesByName("HWiNFO64");
        var sessions = new List<int>(processes.Length);
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    sessions.Add(process.SessionId);
                }
                catch (InvalidOperationException)
                {
                    // Exited.
                }
            }
        }

        return sessions;
    }

    /// <summary>This process's session.</summary>
    public static int CurrentSession()
    {
        using var self = Process.GetCurrentProcess();
        return self.SessionId;
    }
}

/// <summary>
/// A sampler's process check: <see cref="HwInfoProcess.IsRunning"/> (or a test's) asked at most every
/// <see cref="Every"/>, the last answer kept between; a check that throws counts as not running, logged once until one
/// answers. Called on the sampler's thread only.
/// </summary>
internal sealed class HwInfoProcessCheck(Func<bool>? check, TimeProvider time, ILogger log, string logPrefix)
{
    /// <summary>Whether HWiNFO runs is asked at most this often.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    // The source's own words before the message ("HWiNFO Gadget: …"), part of the template rather than a property.
    private readonly string _template = logPrefix + ": checking whether HWiNFO runs failed: {Message}";
    private bool _known;
    private bool _running;
    private long _at;
    private bool _failed;

    /// <summary>Whether HWiNFO runs at <paramref name="now"/> (a timestamp of the sampler's clock); false without a check.</summary>
    public bool IsRunning(long now)
    {
        if (check == null)
            return false;
        if (_known && time.GetElapsedTime(_at, now) < Every)
            return _running;

        try
        {
            _running = check();
            _failed = false;
        }
        catch (Exception ex)
        {
            if (!_failed)
                log.Warning(ex, _template, ex.Message);
            _failed = true;
            _running = false;
        }

        _known = true;
        _at = now;
        return _running;
    }
}
