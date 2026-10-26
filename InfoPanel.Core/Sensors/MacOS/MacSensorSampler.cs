using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.MacOS;

/// <summary>
/// One group of macOS sensors read the same way (Mach CPU ticks, the SMC, an IOKit service...). Discovery adds the
/// sensors it finds; <see cref="Sample"/> fills exactly those, in order.
/// </summary>
internal abstract class MacReader : IDisposable
{
    private int _start;
    private int _count;

    /// <summary>Sample on every Nth poll only (1: every poll). Readings in between stay as they were.</summary>
    public virtual int Every => 1;

    public string Name => GetType().Name;

    public void Discover(List<SensorDefinition> definitions)
    {
        _start = definitions.Count;
        try
        {
            DiscoverCore(definitions);
        }
        catch
        {
            definitions.RemoveRange(_start, definitions.Count - _start);
            _count = 0;
            throw;
        }

        _count = definitions.Count - _start;
    }

    public void Sample(Span<SensorSample> all, TimeSpan elapsed)
    {
        if (_count > 0)
            SampleCore(all.Slice(_start, _count), elapsed);
    }

    /// <summary>Fails every sensor of this reader for the round (after an exception).</summary>
    public void Fail(Span<SensorSample> all)
    {
        foreach (ref var s in all.Slice(_start, _count))
            s.Outcome = SampleOutcome.Failed;
    }

    public int Count => _count;

    protected abstract void DiscoverCore(List<SensorDefinition> definitions);

    /// <param name="mine">This reader's sensors, indexed as discovered (0 = the first it added).</param>
    /// <param name="elapsed">Time since this reader's previous sample; zero on the first.</param>
    protected abstract void SampleCore(Span<SensorSample> mine, TimeSpan elapsed);

    /// <summary>Adds a sensor; returns its index in this reader's span.</summary>
    protected int Add(List<SensorDefinition> definitions, string id, string group, string name, string unit)
    {
        definitions.Add(new SensorDefinition(id, group, name, unit));
        return definitions.Count - 1 - _start;
    }

    public virtual void Dispose()
    {
    }
}

/// <summary>
/// The macOS sensors as one <see cref="ISensorSampler"/>: CPU load (Mach), CPU and GPU frequency and GPU power
/// (IOReport), memory (Mach, sysctl), GPU load and memory (IOAccelerator), temperatures and fans (SMC), battery and
/// system power (AppleSmartBattery), disk (IOBlockStorageDriver) and network (sysctl) throughput. A reader that fails
/// discovery adds no sensors; one that throws while sampling fails only its own sensors for that round.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacSensorSampler : ISensorSampler
{
    private readonly ILogger _log = Log.ForContext<MacSensorSampler>();
    private readonly List<MacReader> _readers = [];
    private readonly HashSet<MacReader> _failing = [];
    private readonly Dictionary<MacReader, TimeSpan> _sinceSample = [];
    private long _polls;

    public IReadOnlyList<SensorDefinition> Discover()
    {
        var definitions = new List<SensorDefinition>();
        var host = new MacHostInfo();
        MacReader[] readers =
        [
            new CpuLoadReader(host),
            new IOReportReader(host),
            new SmcReader(host),
            new GpuReader(host),
            new MemoryReader(),
            new BatteryReader(),
            new DiskReader(),
            new NetworkReader(),
        ];

        foreach (var reader in readers)
        {
            try
            {
                reader.Discover(definitions);
                _readers.Add(reader);
                _sinceSample[reader] = TimeSpan.Zero;
                _log.Debug("macOS sensors: {Reader} found {Count}", reader.Name, reader.Count);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "macOS sensors: {Reader} found nothing", reader.Name);
                reader.Dispose();
            }
        }

        return definitions;
    }

    public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
    {
        var poll = _polls++;
        foreach (var reader in _readers)
        {
            var since = _sinceSample[reader] + elapsed;
            if (poll % reader.Every != 0)
            {
                _sinceSample[reader] = since;
                continue;
            }

            _sinceSample[reader] = TimeSpan.Zero;
            try
            {
                reader.Sample(samples, poll == 0 ? TimeSpan.Zero : since);
                _failing.Remove(reader);
            }
            catch (Exception ex)
            {
                reader.Fail(samples);
                if (_failing.Add(reader))
                    _log.Warning(ex, "macOS sensors: {Reader} failed to sample", reader.Name);
            }
        }
    }

    public void Dispose()
    {
        foreach (var reader in _readers)
            reader.Dispose();
        _readers.Clear();
    }
}

/// <summary>What several readers need: the CPU's name and its logical CPUs' cluster types (E or P).</summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacHostInfo
{
    public MacHostInfo()
    {
        CpuName = LibSystem.GetString("machdep.cpu.brand_string") ?? "CPU";
        LogicalCpus = LibSystem.TryGetInt64("hw.logicalcpu\0"u8, out var n) ? (int)n : Environment.ProcessorCount;
        ClusterTypes = ReadClusterTypes(LogicalCpus);
        GpuGroup = ReadGpuModel() is { Length: > 0 } gpu ? "GPU: " + gpu : "GPU";
    }

    /// <summary>The group of GPU sensors: "GPU: " and the first IOAccelerator's <c>model</c>.</summary>
    public string GpuGroup { get; }

    public string CpuName { get; }

    public int LogicalCpus { get; }

    /// <summary>
    /// The cluster type per logical CPU ('E', 'P', or another letter on chips with more tiers; Mach processor index =
    /// device-tree <c>logical-cpu-id</c>), from IODeviceTree <c>/cpus/cpuN</c> <c>cluster-type</c>; null when the device
    /// tree has no such entries (Intel).
    /// </summary>
    public char[]? ClusterTypes { get; }

    public string CpuGroup => "CPU: " + CpuName;

    private static string? ReadGpuModel()
    {
        var service = IOKit.FirstService("IOAccelerator");
        if (service == 0)
            return null;

        var key = CF.Str("model");
        try
        {
            var model = IOKit.IORegistryEntryCreateCFProperty(service, key, IntPtr.Zero, 0);
            if (model == IntPtr.Zero)
                return null;
            var name = CF.ToManaged(model);
            CF.CFRelease(model);
            return name;
        }
        finally
        {
            CF.CFRelease(key);
            IOKit.IOObjectRelease(service);
        }
    }

    private static char[]? ReadClusterTypes(int count)
    {
        uint cpus;
        fixed (byte* path = "IODeviceTree:/cpus\0"u8)
            cpus = IOKit.IORegistryEntryFromPath(0, path);
        if (cpus == 0)
            return null;

        var types = new char[count];
        var found = 0;
        var clusterKey = CF.Str("cluster-type");
        var idKey = CF.Str("logical-cpu-id");
        try
        {
            fixed (byte* plane = "IODeviceTree\0"u8)
            {
                if (IOKit.IORegistryEntryGetChildIterator(cpus, plane, out var iterator) != IOKit.Success)
                    return null;

                uint cpu;
                while ((cpu = IOKit.IOIteratorNext(iterator)) != 0)
                {
                    var cluster = IOKit.DataProperty(cpu, clusterKey);
                    var hasId = IOKit.TryIntProperty(cpu, idKey, out var index);
                    IOKit.IOObjectRelease(cpu);
                    if (cluster is not { Length: > 0 } || !hasId)
                        continue;

                    var type = (char)cluster[0];
                    if (index >= 0 && index < count && char.IsAsciiLetterUpper(type))
                    {
                        types[index] = type;
                        found++;
                    }
                }

                IOKit.IOObjectRelease(iterator);
            }
        }
        finally
        {
            CF.CFRelease(clusterKey);
            CF.CFRelease(idKey);
            IOKit.IOObjectRelease(cpus);
        }

        return found == count ? types : null;
    }
}
