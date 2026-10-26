using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.MacOS;

/// <summary>
/// Memory from public calls: Mach <c>host_statistics64(HOST_VM_INFO64)</c> page counts, <c>hw.memsize</c>,
/// <c>hw.pagesize</c>, <c>kern.memorystatus_level</c> and <c>vm.swapusage</c>. "Used" is what Activity Monitor calls
/// Memory Used: app memory (internal minus purgeable pages) plus wired plus compressed. Pressure is
/// <c>100 - kern.memorystatus_level</c> (the kernel's % of memory available).
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MemoryReader : MacReader
{
    private const double MB = 1024 * 1024;
    private const string Group = "Memory";

    private double _total;
    private double _pageSize;
    private int _used, _available, _load, _wired, _compressed, _pressure = -1, _swap = -1;

    protected override void DiscoverCore(List<SensorDefinition> definitions)
    {
        if (!LibSystem.TryGetInt64("hw.memsize\0"u8, out var total) || !LibSystem.TryGetInt64("hw.pagesize\0"u8, out var page))
            return;

        _total = total;
        _pageSize = page;
        _used = Add(definitions, "memory/used", Group, "Memory Used", "MB");
        _available = Add(definitions, "memory/available", Group, "Memory Available", "MB");
        _load = Add(definitions, "memory/load", Group, "Memory Load", "%");
        _wired = Add(definitions, "memory/wired", Group, "Memory Wired", "MB");
        _compressed = Add(definitions, "memory/compressed", Group, "Memory Compressed", "MB");
        if (LibSystem.TryGetInt64("kern.memorystatus_level\0"u8, out _))
            _pressure = Add(definitions, "memory/pressure", Group, "Memory Pressure", "%");
        if (LibSystem.TryGet<XswUsage>("vm.swapusage\0"u8, out _))
            _swap = Add(definitions, "memory/swap-used", Group, "Swap Used", "MB");
    }

    protected override void SampleCore(Span<SensorSample> mine, TimeSpan elapsed)
    {
        VmStatistics64 vm;
        var count = (uint)(sizeof(VmStatistics64) / sizeof(int));
        if (LibSystem.host_statistics64(LibSystem.HostSelf, LibSystem.HostVmInfo64, &vm, ref count) == 0)
        {
            var app = (double)Math.Max(0, (long)vm.InternalPageCount - vm.PurgeableCount);
            var used = (app + vm.WireCount + vm.CompressorPageCount) * _pageSize;
            mine[_used].Set(used / MB);
            mine[_available].Set(Math.Max(0, _total - used) / MB);
            mine[_load].Set(Math.Clamp(used / _total * 100, 0, 100));
            mine[_wired].Set(vm.WireCount * _pageSize / MB);
            mine[_compressed].Set(vm.CompressorPageCount * _pageSize / MB);
        }
        else
        {
            mine[_used].Outcome = mine[_available].Outcome = mine[_load].Outcome =
                mine[_wired].Outcome = mine[_compressed].Outcome = SampleOutcome.Failed;
        }

        if (_pressure >= 0)
        {
            if (LibSystem.TryGetInt64("kern.memorystatus_level\0"u8, out var level))
                mine[_pressure].Set(Math.Clamp(100 - level, 0, 100));
            else
                mine[_pressure].Outcome = SampleOutcome.Failed;
        }

        if (_swap >= 0)
        {
            if (LibSystem.TryGet<XswUsage>("vm.swapusage\0"u8, out var swap))
                mine[_swap].Set(swap.Used / MB);
            else
                mine[_swap].Outcome = SampleOutcome.Failed;
        }
    }
}
