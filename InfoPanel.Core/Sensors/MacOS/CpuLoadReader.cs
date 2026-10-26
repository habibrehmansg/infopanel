using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.MacOS;

/// <summary>
/// CPU load from Mach <c>host_processor_info(PROCESSOR_CPU_LOAD_INFO)</c> tick deltas (public API): total, per logical
/// CPU, and for E and P cores (cluster types from the device tree) where the device tree has them. % of the interval
/// spent outside idle.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class CpuLoadReader(MacHostInfo host) : MacReader
{
    private int _total;
    private int _efficiency = -1;
    private int _performance = -1;
    private int _firstCore;
    private uint[] _previous = [];
    private bool _primed;

    protected override void DiscoverCore(List<SensorDefinition> definitions)
    {
        var group = host.CpuGroup;
        _total = Add(definitions, "cpu/load", group, "CPU Load", "%");
        if (host.ClusterTypes is { } clusters && Array.IndexOf(clusters, 'E') >= 0)
            _efficiency = Add(definitions, "cpu/load-e", group, "CPU Load (E-cores)", "%");
        if (host.ClusterTypes is { } tiers && Array.IndexOf(tiers, 'P') >= 0)
            _performance = Add(definitions, "cpu/load-p", group, "CPU Load (P-cores)", "%");

        for (var i = 0; i < host.LogicalCpus; i++)
        {
            var type = host.ClusterTypes is { } t ? $" ({t[i]}-core)" : "";
            var index = Add(definitions, string.Create(CultureInfo.InvariantCulture, $"cpu/core-{i}/load"), group,
                string.Create(CultureInfo.InvariantCulture, $"Core {i} Load{type}"), "%");
            if (i == 0)
                _firstCore = index;
        }

        _previous = new uint[host.LogicalCpus * LibSystem.CpuStateMax];
    }

    protected override void SampleCore(Span<SensorSample> mine, TimeSpan elapsed)
    {
        if (LibSystem.host_processor_info(LibSystem.HostSelf, LibSystem.ProcessorCpuLoadInfo, out var count, out var info, out var infoCount) != 0)
        {
            foreach (ref var s in mine)
                s.Outcome = SampleOutcome.Failed;
            return;
        }

        try
        {
            var ticks = new ReadOnlySpan<uint>((void*)info, (int)infoCount);
            var cpus = Math.Min((int)count, _previous.Length / LibSystem.CpuStateMax);
            if (!_primed)
            {
                ticks[..(cpus * LibSystem.CpuStateMax)].CopyTo(_previous);
                _primed = true;
                return;
            }

            double busyAll = 0, totalAll = 0, busyE = 0, totalE = 0, busyP = 0, totalP = 0;
            for (var cpu = 0; cpu < cpus; cpu++)
            {
                var o = cpu * LibSystem.CpuStateMax;
                // Counters are 32-bit and wrap; unchecked subtraction gives the delta across a wrap.
                double user = unchecked(ticks[o + LibSystem.CpuStateUser] - _previous[o + LibSystem.CpuStateUser]);
                double system = unchecked(ticks[o + LibSystem.CpuStateSystem] - _previous[o + LibSystem.CpuStateSystem]);
                double idle = unchecked(ticks[o + LibSystem.CpuStateIdle] - _previous[o + LibSystem.CpuStateIdle]);
                double nice = unchecked(ticks[o + LibSystem.CpuStateNice] - _previous[o + LibSystem.CpuStateNice]);
                var busy = user + system + nice;
                var total = busy + idle;

                if (total > 0)
                    mine[_firstCore + cpu].Set(busy / total * 100);

                busyAll += busy;
                totalAll += total;
                // Other cluster tiers count in the total only.
                switch (host.ClusterTypes?[cpu])
                {
                    case 'E':
                        busyE += busy;
                        totalE += total;
                        break;
                    case 'P':
                        busyP += busy;
                        totalP += total;
                        break;
                }
            }

            ticks[..(cpus * LibSystem.CpuStateMax)].CopyTo(_previous);
            if (totalAll > 0)
                mine[_total].Set(busyAll / totalAll * 100);
            if (_efficiency >= 0 && totalE > 0)
                mine[_efficiency].Set(busyE / totalE * 100);
            if (_performance >= 0 && totalP > 0)
                mine[_performance].Set(busyP / totalP * 100);
        }
        finally
        {
            LibSystem.vm_deallocate(LibSystem.TaskSelf, (nuint)info, infoCount * sizeof(uint));
        }
    }
}
