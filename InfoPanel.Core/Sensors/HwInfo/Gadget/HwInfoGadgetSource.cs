using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.HwInfo.Gadget;

/// <summary>
/// The HWiNFO Gadget source: a <see cref="PolledSensorSource"/> with the id <c>infopanel.hwinfo-gadget</c> over
/// <see cref="HwInfoGadgetSampler"/>, polled once a second, answering every key under
/// <c>infopanel:/hwinfo-gadget/</c> (<c>{sensor}/{label}</c>, <see cref="GadgetKeys"/>). A key the registry
/// doesn't have reads unavailable. Read as the normal user: a user reads their own hive.
/// </summary>
public static class HwInfoGadgetSource
{
    public const string SourceId = "infopanel.hwinfo-gadget";

    public const string KeyPrefix = "/" + SourceId + "/";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>No gadget readings this run: HWiNFO isn't reporting to gadgets, or no reading is ticked for them (the source is off).</summary>
    public static readonly SourceReason NotSetUp = new("notSetUp", "HWiNFO isn't reporting any reading to gadgets");

    /// <summary>Gadget readings stopped, and HWiNFO doesn't run in this session.</summary>
    public static readonly SourceReason NotRunning = new("notRunning", "HWiNFO isn't running");

    /// <summary>Gadget readings stopped while HWiNFO runs: its reporting to gadgets was turned off, or nothing is ticked any more.</summary>
    public static readonly SourceReason Stopped = new("stopped", "HWiNFO stopped reporting to gadgets: check Enable reporting to Gadget");

    /// <summary>The key of a reading by its id (<c>CPU/Temp</c> → <c>infopanel:/hwinfo-gadget/CPU/Temp</c>).</summary>
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

    /// <summary>Whether a <c>HWiNFO64</c> process runs in this process's session, never in session 0 (<see cref="HwInfoProcess.IsRunning"/>).</summary>
    public static bool IsHwInfoRunning() => HwInfoProcess.IsRunning();

    /// <summary>The process check over <paramref name="hwinfoSessions"/> (<see cref="HwInfoProcess.RunningIn"/>).</summary>
    public static Func<bool> HwInfoRunningIn(int session, Func<IReadOnlyList<int>> hwinfoSessions) => HwInfoProcess.RunningIn(session, hwinfoSessions);

    /// <summary>The session of each <c>HWiNFO64</c> process (<see cref="HwInfoProcess.Sessions"/>).</summary>
    public static IReadOnlyList<int> HwInfoSessions() => HwInfoProcess.Sessions();

    /// <summary>This process's session (<see cref="HwInfoProcess.CurrentSession"/>).</summary>
    public static int CurrentSession() => HwInfoProcess.CurrentSession();
}
