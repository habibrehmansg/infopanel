using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.MacOS;

/// <summary>
/// CPU cluster and GPU frequency and GPU power from IOReport (private, Apple Silicon): one subscription to
/// <c>CPU Stats / CPU Complex Performance States</c> (channels <c>ECPU*</c>, <c>PCPU*</c>), <c>GPU Stats / GPU
/// Performance States</c> (<c>GPUPH</c>) and <c>Energy Model / GPU Energy</c>. A cluster's frequency is the
/// residency-weighted average over its active performance states (every state but <c>DOWN</c>, <c>IDLE</c>, <c>OFF</c>),
/// state i being entry i of the DVFS table in the device tree's <c>pmgr</c> node (<c>voltage-states1-sram</c> E,
/// <c>voltage-states5-sram</c> P, <c>voltage-states9</c> GPU; zero entries skipped; Hz or kHz by magnitude); 0 MHz
/// when the cluster was never active in the interval. GPU power is the energy delta over the interval. IOReport's CPU,
/// ANE and DRAM energy counters don't move for an unprivileged process on macOS 27, so there is no CPU power here.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class IOReportReader(MacHostInfo host) : MacReader
{
    private enum Role : byte
    {
        None,
        Efficiency,
        Performance,
        Gpu,
        GpuEnergy,
    }

    private IOReportSubscription? _subscription;
    private double[] _eTable = [];
    private double[] _pTable = [];
    private double[] _gpuTable = [];
    private int _eFrequency = -1;
    private int _pFrequency = -1;
    private int _gpuFrequency = -1;
    private int _gpuPower = -1;

    // Per delta-array index, filled from the first delta: the channel's role, active-state offset and energy scale.
    private Role[]? _roles;
    private int[] _activeOffset = [];
    private int[] _stateCount = [];
    private double[] _joulesPerUnit = [];

    private static bool IsCpuCluster(string name) =>
        (name.StartsWith("ECPU", StringComparison.Ordinal) || name.StartsWith("PCPU", StringComparison.Ordinal))
        && name.AsSpan(4).IndexOfAnyExceptInRange('0', '9') < 0;

    protected override void DiscoverCore(List<SensorDefinition> definitions)
    {
        if (!IOReportApi.IsAvailable)
            return;

        ReadTables();
        _subscription = IOReportSubscription.Create(
        [
            ("CPU Stats", "CPU Complex Performance States", IsCpuCluster),
            ("GPU Stats", "GPU Performance States", n => n == "GPUPH"),
            ("Energy Model", null, n => n == "GPU Energy"),
        ]);
        if (_subscription == null)
            return;

        // Only what this machine's subscription and device tree can answer (an Intel Mac has neither).
        var channels = _subscription.SubscribedChannelNames();
        if (_eTable.Length > 0 && channels.Exists(c => c.StartsWith("ECPU", StringComparison.Ordinal)))
            _eFrequency = Add(definitions, "cpu/frequency-e", host.CpuGroup, "CPU Frequency (E-cores)", "MHz");
        if (_pTable.Length > 0 && channels.Exists(c => c.StartsWith("PCPU", StringComparison.Ordinal)))
            _pFrequency = Add(definitions, "cpu/frequency-p", host.CpuGroup, "CPU Frequency (P-cores)", "MHz");
        if (_gpuTable.Length > 0 && channels.Contains("GPUPH"))
            _gpuFrequency = Add(definitions, "gpu/frequency", host.GpuGroup, "GPU Frequency", "MHz");
        if (channels.Contains("GPU Energy"))
            _gpuPower = Add(definitions, "gpu/power", host.GpuGroup, "GPU Power", "W");
        _subscription.Sample(out _);
    }

    /// <summary>The DVFS tables from <c>pmgr</c>: pairs of 32-bit (frequency, voltage), frequency in Hz or kHz.</summary>
    private void ReadTables()
    {
        fixed (byte* name = "AppleARMIODevice\0"u8)
        {
            var iterator = IOKit.Services(name);
            if (iterator == 0)
                return;

            var e = CF.Str("voltage-states1-sram");
            var p = CF.Str("voltage-states5-sram");
            var g = CF.Str("voltage-states9");
            try
            {
                uint entry;
                while ((entry = IOKit.IOIteratorNext(iterator)) != 0)
                {
                    if (IOKit.NameOf(entry) == "pmgr")
                    {
                        _eTable = ToMHz(IOKit.DataProperty(entry, e));
                        _pTable = ToMHz(IOKit.DataProperty(entry, p));
                        _gpuTable = ToMHz(IOKit.DataProperty(entry, g));
                    }

                    IOKit.IOObjectRelease(entry);
                }
            }
            finally
            {
                CF.CFRelease(e);
                CF.CFRelease(p);
                CF.CFRelease(g);
                IOKit.IOObjectRelease(iterator);
            }
        }
    }

    /// <summary>
    /// Non-zero frequencies of a DVFS table in MHz. Units differ by chip (Hz on M1–M3, kHz for the M4's CPU tables),
    /// so they are taken from the largest entry: over 10^8 is Hz, over 10^5 kHz, else MHz.
    /// </summary>
    internal static double[] ToMHz(byte[]? data)
    {
        if (data == null)
            return [];

        var raw = new List<uint>();
        for (var i = 0; i + 8 <= data.Length; i += 8)
        {
            var f = BitConverter.ToUInt32(data, i);
            if (f != 0)
                raw.Add(f);
        }

        if (raw.Count == 0)
            return [];
        var max = raw.Max();
        var divisor = max > 100_000_000 ? 1_000_000.0 : max > 100_000 ? 1_000.0 : 1.0;
        return raw.Select(f => f / divisor).ToArray();
    }

    private void Map(IntPtr channels)
    {
        var list = IOReportSubscription.Channels(channels);
        _roles = new Role[list.Count];
        _activeOffset = new int[list.Count];
        _stateCount = new int[list.Count];
        _joulesPerUnit = new double[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            var (group, _, name, unit) = list[i];
            var channel = CF.CFArrayGetValueAtIndex(channels, i);
            var role = group switch
            {
                "CPU Stats" when name.StartsWith("ECPU", StringComparison.Ordinal) => Role.Efficiency,
                "CPU Stats" when name.StartsWith("PCPU", StringComparison.Ordinal) => Role.Performance,
                "GPU Stats" => Role.Gpu,
                "Energy Model" => Role.GpuEnergy,
                _ => Role.None,
            };

            if (role is Role.Efficiency or Role.Performance or Role.Gpu)
            {
                var states = IOReportSubscription.StateNames(channel);
                var offset = Array.FindIndex(states, s => s is not ("DOWN" or "IDLE" or "OFF"));
                if (offset < 0)
                    role = Role.None;
                _activeOffset[i] = offset;
                _stateCount[i] = states.Length;
            }
            else if (role == Role.GpuEnergy)
            {
                _joulesPerUnit[i] = unit switch
                {
                    "nJ" => 1e-9,
                    "uJ" or "µJ" => 1e-6,
                    "mJ" => 1e-3,
                    "J" => 1,
                    _ => 0,
                };
                if (_joulesPerUnit[i] == 0)
                    role = Role.None;
            }

            _roles[i] = role;
        }
    }

    protected override void SampleCore(Span<SensorSample> mine, TimeSpan elapsed)
    {
        var result = _subscription!.Sample(out var channels);
        if (result == IOReportSubscription.SampleResult.Failed)
        {
            foreach (ref var s in mine)
                s.Outcome = SampleOutcome.Failed;
            return;
        }

        // Primed: a baseline only. A delta's interval is the poll interval: a failure drops the baseline, so a delta
        // never spans a missed poll.
        if (result == IOReportSubscription.SampleResult.Primed || elapsed <= TimeSpan.Zero)
            return;

        var count = (int)CF.CFArrayGetCount(channels);
        if (_roles == null || _roles.Length != count)
            Map(channels);

        double eWeighted = 0, eActive = 0, pWeighted = 0, pActive = 0, gWeighted = 0, gActive = 0, joules = 0;
        var haveE = false;
        var haveP = false;
        var haveG = false;
        var haveEnergy = false;
        for (var i = 0; i < count; i++)
        {
            var role = _roles![i];
            if (role == Role.None)
                continue;

            var channel = CF.CFArrayGetValueAtIndex(channels, i);
            if (role == Role.GpuEnergy)
            {
                joules += IOReportApi.SimpleGetIntegerValue(channel, 0) * _joulesPerUnit[i];
                haveEnergy = true;
                continue;
            }

            var table = role switch { Role.Efficiency => _eTable, Role.Performance => _pTable, _ => _gpuTable };
            var offset = _activeOffset[i];
            var states = Math.Min(_stateCount[i], IOReportApi.StateGetCount(channel));
            double weighted = 0, active = 0;
            for (var s = offset; s < states && s - offset < table.Length; s++)
            {
                double residency = IOReportApi.StateGetResidency(channel, s);
                weighted += residency * table[s - offset];
                active += residency;
            }

            switch (role)
            {
                case Role.Efficiency: eWeighted += weighted; eActive += active; haveE = true; break;
                case Role.Performance: pWeighted += weighted; pActive += active; haveP = true; break;
                default: gWeighted += weighted; gActive += active; haveG = true; break;
            }
        }

        if (_eFrequency >= 0 && haveE)
            mine[_eFrequency].Set(eActive > 0 ? eWeighted / eActive : 0);
        if (_pFrequency >= 0 && haveP)
            mine[_pFrequency].Set(pActive > 0 ? pWeighted / pActive : 0);
        if (_gpuFrequency >= 0 && haveG)
            mine[_gpuFrequency].Set(gActive > 0 ? gWeighted / gActive : 0);
        if (_gpuPower >= 0 && haveEnergy)
            mine[_gpuPower].Set(joules / elapsed.TotalSeconds);
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
