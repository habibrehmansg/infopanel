using Serilog;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace InfoPanel.Sensors.Linux;

/// <summary>
/// NVML from the NVIDIA driver's own <c>libnvidia-ml.so.1</c>, loaded at run time (<see cref="NativeLibrary.TryLoad(string, out IntPtr)"/>;
/// no package): the few calls <see cref="INvml"/> names, each looked up by name, a call the driver's NVML lacks reading
/// as not supported. <see cref="TryOpen"/> gives none when the library isn't there or NVML doesn't start (no NVIDIA
/// driver, or no GPU), logged once; disposing shuts NVML down and lets the library go.
/// </summary>
internal sealed unsafe class NvmlLibrary : INvml
{
    // nvmlReturn_t values the reader tells apart.
    private const int Success = 0;
    private const int ErrorNotSupported = 3;
    private const int ErrorNoPermission = 4;
    private const int ErrorNotFound = 6;
    private const int ErrorGpuIsLost = 15;
    private const int ErrorFunctionNotFound = 13;

    // NVML_DEVICE_UUID_V2_BUFFER_SIZE, NVML_DEVICE_NAME_V2_BUFFER_SIZE, NVML_DEVICE_PCI_BUS_ID_BUFFER_SIZE.
    private const int UuidBuffer = 96;
    private const int NameBuffer = 96;
    private const int BusIdBuffer = 32;

    // NVML_TEMPERATURE_GPU; NVML_FI_DEV_MEMORY_TEMP.
    private const int TemperatureGpu = 0;
    private const uint FieldMemoryTemperature = 82;

    [StructLayout(LayoutKind.Sequential)]
    private struct UtilizationRates
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    // nvmlFanSpeedInfo_v1_t, its version NVML_STRUCT_VERSION(FanSpeedInfo, 1).
    [StructLayout(LayoutKind.Sequential)]
    private struct FanSpeedInfo
    {
        public uint Version;
        public uint Fan;
        public uint Speed;
    }

    // nvmlFieldValue_t: the value is a union of 8 bytes, read by its type.
    [StructLayout(LayoutKind.Sequential)]
    private struct FieldValue
    {
        public uint FieldId;
        public uint ScopeId;
        public long Timestamp;
        public long LatencyUsec;
        public int ValueType;
        public int Return;
        public ulong Value;
    }

    private readonly IntPtr _library;
    private readonly delegate* unmanaged<int> _shutdown;
    private readonly delegate* unmanaged<uint*, int> _deviceCount;
    private readonly delegate* unmanaged<uint, nint*, int> _deviceByIndex;
    private readonly delegate* unmanaged<nint, byte*, uint, int> _uuid;
    private readonly delegate* unmanaged<nint, byte*, uint, int> _name;
    private readonly delegate* unmanaged<nint, byte*, int> _busId;
    private readonly delegate* unmanaged<nint, UtilizationRates*, int> _utilization;
    private readonly delegate* unmanaged<nint, int, uint*, int> _temperature;
    private readonly delegate* unmanaged<nint, int, FieldValue*, int> _fieldValues;
    private readonly delegate* unmanaged<nint, MemoryInfo*, int> _memory;
    private readonly delegate* unmanaged<nint, uint*, int> _power;
    private readonly delegate* unmanaged<nint, uint*, int> _powerLimit;
    private readonly delegate* unmanaged<nint, uint, uint*, int> _clock;
    private readonly delegate* unmanaged<nint, uint*, int> _fanCount;
    private readonly delegate* unmanaged<nint, uint*, int> _fanSpeed;
    private readonly delegate* unmanaged<nint, FanSpeedInfo*, int> _fanRpm;
    private bool _disposed;

    private NvmlLibrary(IntPtr library)
    {
        _library = library;
        _shutdown = (delegate* unmanaged<int>)Export("nvmlShutdown");
        _deviceCount = (delegate* unmanaged<uint*, int>)Export("nvmlDeviceGetCount_v2");
        _deviceByIndex = (delegate* unmanaged<uint, nint*, int>)Export("nvmlDeviceGetHandleByIndex_v2");
        _uuid = (delegate* unmanaged<nint, byte*, uint, int>)Export("nvmlDeviceGetUUID");
        _name = (delegate* unmanaged<nint, byte*, uint, int>)Export("nvmlDeviceGetName");
        _busId = (delegate* unmanaged<nint, byte*, int>)Export("nvmlDeviceGetPciInfo_v3");
        _utilization = (delegate* unmanaged<nint, UtilizationRates*, int>)Export("nvmlDeviceGetUtilizationRates");
        _temperature = (delegate* unmanaged<nint, int, uint*, int>)Export("nvmlDeviceGetTemperature");
        _fieldValues = (delegate* unmanaged<nint, int, FieldValue*, int>)Export("nvmlDeviceGetFieldValues");
        _memory = (delegate* unmanaged<nint, MemoryInfo*, int>)Export("nvmlDeviceGetMemoryInfo");
        _power = (delegate* unmanaged<nint, uint*, int>)Export("nvmlDeviceGetPowerUsage");
        _powerLimit = (delegate* unmanaged<nint, uint*, int>)Export("nvmlDeviceGetEnforcedPowerLimit");
        _clock = (delegate* unmanaged<nint, uint, uint*, int>)Export("nvmlDeviceGetClockInfo");
        _fanCount = (delegate* unmanaged<nint, uint*, int>)Export("nvmlDeviceGetNumFans");
        _fanSpeed = (delegate* unmanaged<nint, uint*, int>)Export("nvmlDeviceGetFanSpeed");
        _fanRpm = (delegate* unmanaged<nint, FanSpeedInfo*, int>)Export("nvmlDeviceGetFanSpeedRPM");
    }

    private IntPtr Export(string name) => NativeLibrary.TryGetExport(_library, name, out var address) ? address : IntPtr.Zero;

    /// <summary>NVML started from <paramref name="library"/>, or null (logged) when it isn't there or doesn't start.</summary>
    public static INvml? TryOpen(ILogger log, string library = "libnvidia-ml.so.1")
    {
        log = log.ForContext<NvmlLibrary>();
        if (!NativeLibrary.TryLoad(library, out var handle))
        {
            log.Information("Linux sensors: NVML ({Library}) isn't there, so no NVIDIA GPU is listed", library);
            return null;
        }

        if (!NativeLibrary.TryGetExport(handle, "nvmlInit_v2", out var init)
            || ((delegate* unmanaged<int>)init)() != Success)
        {
            log.Information("Linux sensors: NVML ({Library}) didn't start, so no NVIDIA GPU is listed", library);
            NativeLibrary.Free(handle);
            return null;
        }

        log.Information("Linux sensors: NVML ({Library}) started", library);
        return new NvmlLibrary(handle);
    }

    private static NvmlStatus Status(int result) => result switch
    {
        Success => NvmlStatus.Ok,
        ErrorNotSupported or ErrorFunctionNotFound => NvmlStatus.NotSupported,
        ErrorNoPermission => NvmlStatus.NoPermission,
        ErrorGpuIsLost or ErrorNotFound => NvmlStatus.Lost,
        _ => NvmlStatus.Failed,
    };

    private bool Ready(bool present) => !_disposed && present;

    public NvmlStatus DeviceCount(out uint count)
    {
        count = 0;
        if (!Ready(_deviceCount != null))
            return NvmlStatus.NotSupported;
        fixed (uint* c = &count)
            return Status(_deviceCount(c));
    }

    public NvmlStatus Device(uint index, out nint device)
    {
        device = 0;
        if (!Ready(_deviceByIndex != null))
            return NvmlStatus.NotSupported;
        fixed (nint* d = &device)
            return Status(_deviceByIndex(index, d));
    }

    private NvmlStatus Text(delegate* unmanaged<nint, byte*, uint, int> function, nint device, int size, out string text)
    {
        text = "";
        if (!Ready(function != null))
            return NvmlStatus.NotSupported;
        var buffer = stackalloc byte[size];
        var status = Status(function(device, buffer, (uint)size));
        if (status == NvmlStatus.Ok)
            text = Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(buffer));
        return status;
    }

    public NvmlStatus Uuid(nint device, out string uuid) => Text(_uuid, device, UuidBuffer, out uuid);

    public NvmlStatus Name(nint device, out string name) => Text(_name, device, NameBuffer, out name);

    public NvmlStatus PciBusId(nint device, out string busId)
    {
        busId = "";
        if (!Ready(_busId != null))
            return NvmlStatus.NotSupported;
        // nvmlPciInfo_t: busIdLegacy[16], domain, bus, device, pciDeviceId, pciSubSystemId (uint each), busId[32].
        const int offset = 16 + 5 * sizeof(uint);
        var info = stackalloc byte[offset + BusIdBuffer];
        var status = Status(_busId(device, info));
        if (status == NvmlStatus.Ok)
            busId = Encoding.ASCII.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(info + offset));
        return status;
    }

    public NvmlStatus Utilization(nint device, out uint gpu, out uint memory)
    {
        gpu = memory = 0;
        if (!Ready(_utilization != null))
            return NvmlStatus.NotSupported;
        UtilizationRates value;
        var status = Status(_utilization(device, &value));
        (gpu, memory) = (value.Gpu, value.Memory);
        return status;
    }

    public NvmlStatus Temperature(nint device, out uint celsius)
    {
        celsius = 0;
        if (!Ready(_temperature != null))
            return NvmlStatus.NotSupported;
        fixed (uint* c = &celsius)
            return Status(_temperature(device, TemperatureGpu, c));
    }

    public NvmlStatus MemoryTemperature(nint device, out uint celsius)
    {
        celsius = 0;
        if (!Ready(_fieldValues != null))
            return NvmlStatus.NotSupported;
        var field = new FieldValue { FieldId = FieldMemoryTemperature };
        var status = Status(_fieldValues(device, 1, &field));
        if (status != NvmlStatus.Ok)
            return status;
        if ((status = Status(field.Return)) != NvmlStatus.Ok)
            return status;
        // nvmlValueType_t: 0 double, 1 unsigned int, 2 unsigned long, 3 unsigned long long, 4 signed long long, 5 signed int.
        double value = field.ValueType switch
        {
            0 => BitConverter.UInt64BitsToDouble(field.Value),
            1 => (uint)field.Value,
            2 or 3 => field.Value,
            4 => (long)field.Value,
            5 => (int)(uint)field.Value,
            _ => double.NaN,
        };
        if (!double.IsFinite(value) || value < 0 || value > 1000)
            return NvmlStatus.Failed;
        celsius = (uint)value;
        return NvmlStatus.Ok;
    }

    public NvmlStatus Memory(nint device, out ulong used, out ulong total)
    {
        used = total = 0;
        if (!Ready(_memory != null))
            return NvmlStatus.NotSupported;
        MemoryInfo info;
        var status = Status(_memory(device, &info));
        (used, total) = (info.Used, info.Total);
        return status;
    }

    public NvmlStatus Power(nint device, out uint milliwatts) => Uint(_power, device, out milliwatts);

    public NvmlStatus PowerLimit(nint device, out uint milliwatts) => Uint(_powerLimit, device, out milliwatts);

    public NvmlStatus FanCount(nint device, out uint count) => Uint(_fanCount, device, out count);

    public NvmlStatus FanSpeed(nint device, out uint percent) => Uint(_fanSpeed, device, out percent);

    private NvmlStatus Uint(delegate* unmanaged<nint, uint*, int> function, nint device, out uint value)
    {
        value = 0;
        if (!Ready(function != null))
            return NvmlStatus.NotSupported;
        fixed (uint* v = &value)
            return Status(function(device, v));
    }

    public NvmlStatus Clock(nint device, NvmlClock clock, out uint mhz)
    {
        mhz = 0;
        if (!Ready(_clock != null))
            return NvmlStatus.NotSupported;
        fixed (uint* m = &mhz)
            return Status(_clock(device, (uint)clock, m));
    }

    public NvmlStatus FanRpm(nint device, uint fan, out uint rpm)
    {
        rpm = 0;
        if (!Ready(_fanRpm != null))
            return NvmlStatus.NotSupported;
        var info = new FanSpeedInfo { Version = (uint)sizeof(FanSpeedInfo) | (1u << 24), Fan = fan };
        var status = Status(_fanRpm(device, &info));
        rpm = info.Speed;
        return status;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_shutdown != null)
            _shutdown();
        NativeLibrary.Free(_library);
    }
}
