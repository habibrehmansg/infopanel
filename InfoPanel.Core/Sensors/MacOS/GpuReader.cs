using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.MacOS;

/// <summary>
/// GPU load and memory from the first <c>IOAccelerator</c> service's <c>PerformanceStatistics</c> dictionary (a public
/// IOKit call; the keys are undocumented): <c>Device Utilization %</c>, <c>Renderer Utilization %</c>,
/// <c>Tiler Utilization %</c>, <c>In use system memory</c> and <c>Alloc system memory</c> (bytes; Apple Silicon has
/// unified memory). Sensors whose key the first read lacks (other GPUs) are not listed.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class GpuReader(MacHostInfo host) : MacReader
{
    private static readonly IntPtr StatisticsKey = CF.Str("PerformanceStatistics");

    private static readonly (string Id, string Name, string Unit, string Key, double Scale)[] Catalogue =
    [
        ("gpu/load", "GPU Load", "%", "Device Utilization %", 1),
        ("gpu/renderer-load", "GPU Renderer Load", "%", "Renderer Utilization %", 1),
        ("gpu/tiler-load", "GPU Tiler Load", "%", "Tiler Utilization %", 1),
        ("gpu/memory-used", "GPU Memory In Use", "MB", "In use system memory", 1.0 / (1024 * 1024)),
        ("gpu/memory-allocated", "GPU Memory Allocated", "MB", "Alloc system memory", 1.0 / (1024 * 1024)),
    ];

    private uint _service;
    private readonly List<(int Index, IntPtr Key, double Scale)> _sensors = [];

    protected override void DiscoverCore(List<SensorDefinition> definitions)
    {
        _service = IOKit.FirstService("IOAccelerator");
        if (_service == 0)
            return;

        var stats = IOKit.IORegistryEntryCreateCFProperty(_service, StatisticsKey, IntPtr.Zero, 0);
        try
        {
            foreach (var (id, name, unit, key, scale) in Catalogue)
            {
                var cfKey = CF.Str(key);
                if (CF.TryGetDouble(stats, cfKey, out _))
                    _sensors.Add((Add(definitions, id, host.GpuGroup, name, unit), cfKey, scale));
                else
                    CF.CFRelease(cfKey);
            }
        }
        finally
        {
            if (stats != IntPtr.Zero)
                CF.CFRelease(stats);
        }
    }

    protected override void SampleCore(Span<SensorSample> mine, TimeSpan elapsed)
    {
        var stats = IOKit.IORegistryEntryCreateCFProperty(_service, StatisticsKey, IntPtr.Zero, 0);
        if (stats == IntPtr.Zero)
        {
            foreach (ref var s in mine)
                s.Outcome = SampleOutcome.Failed;
            return;
        }

        try
        {
            foreach (var (index, key, scale) in _sensors)
            {
                if (CF.TryGetDouble(stats, key, out var value))
                    mine[index].Set(value * scale);
                else
                    mine[index].Outcome = SampleOutcome.Failed;
            }
        }
        finally
        {
            CF.CFRelease(stats);
        }
    }

    public override void Dispose()
    {
        foreach (var (_, key, _) in _sensors)
            CF.CFRelease(key);
        _sensors.Clear();
        if (_service != 0)
        {
            IOKit.IOObjectRelease(_service);
            _service = 0;
        }
    }
}
