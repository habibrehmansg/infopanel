using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.HwInfo.Gadget;

/// <summary>
/// The HWiNFO Gadget source: a <see cref="PolledSensorSource"/> with the id <c>infopanel.hwinfo-gadget</c> over
/// <see cref="HwInfoGadgetSampler"/>, polled once a second, answering every key under
/// <c>plugin:/infopanel.hwinfo-gadget/</c> (<c>{sensor}/{label}</c>, <see cref="GadgetKeys"/>). A key the registry
/// doesn't have reads unavailable. Read as the normal user: a user reads their own hive.
/// </summary>
public static class HwInfoGadgetSource
{
    public const string SourceId = "infopanel.hwinfo-gadget";

    public const string KeyPrefix = "/" + SourceId + "/";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>The key of a reading by its id (<c>CPU/Temp</c> → <c>plugin:/infopanel.hwinfo-gadget/CPU/Temp</c>).</summary>
    public static SensorKey KeyOf(string id) => SensorKey.Plugin(KeyPrefix + id);

    /// <summary>A source over <paramref name="createSampler"/>'s sampler (a test's).</summary>
    public static PolledSensorSource Create(Func<ISensorSampler> createSampler, TimeProvider? time = null, PolledSensorSourceOptions? options = null) =>
        new(SourceId, createSampler, options ?? new PolledSensorSourceOptions { Interval = Interval }, time);

    /// <summary>A source over <paramref name="registry"/> (a test's), with <paramref name="hwinfoRunning"/> as the process check.</summary>
    public static PolledSensorSource Create(IGadgetRegistry registry, TimeProvider? time = null, PolledSensorSourceOptions? options = null, ILogger? log = null, Func<bool>? hwinfoRunning = null) =>
        Create(() => new HwInfoGadgetSampler(registry, time, log, hwinfoRunning), time, options);

    /// <summary>The source over this PC's registry and HWiNFO's process.</summary>
    [SupportedOSPlatform("windows")]
    public static PolledSensorSource CreateWindows(TimeProvider? time = null) => Create(WinGadgetRegistry.Reader(), time, hwinfoRunning: IsHwInfoRunning);

    /// <summary>Whether a <c>HWiNFO64</c> process runs in this process's session, never in session 0.</summary>
    public static bool IsHwInfoRunning() => HwInfoRunningIn(CurrentSession(), HwInfoSessions)();

    /// <summary>The process check over <paramref name="hwinfoSessions"/>, the sessions <c>HWiNFO64</c> processes run in (a test's).</summary>
    /// <remarks>
    /// Only a HWiNFO in <paramref name="session"/> counts: another user's, in another session, writes their own hive,
    /// and would otherwise keep this user's abandoned values live. In session 0 none does: every user's services
    /// share it, so a HWiNFO there may be another user's, and freshness is the key's last-write time alone (a
    /// user's engine runs in their own session). Matching the process's owner isn't attempted: a normal user may not
    /// read the token of HWiNFO, which runs elevated.
    /// </remarks>
    public static Func<bool> HwInfoRunningIn(int session, Func<IReadOnlyList<int>> hwinfoSessions) =>
        session == 0 ? () => false : () => hwinfoSessions().Contains(session);

    /// <summary>
    /// The session of each <c>HWiNFO64</c> process: names and sessions are listed to the normal user, elevated ones and
    /// other users' too. One that exits meanwhile is left out.
    /// </summary>
    public static IReadOnlyList<int> HwInfoSessions()
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
