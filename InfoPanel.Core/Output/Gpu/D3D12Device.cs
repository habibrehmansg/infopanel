using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Serilog;
using SkiaSharp;

namespace InfoPanel.Output.Gpu;

/// <summary>
/// Direct3D 12 for Skia on Windows: the first hardware adapter in Windows' order that takes a device (DXGI's
/// <c>EnumAdapters1</c>; <see cref="D3D12Abi.Candidates"/> leaves out the adapters that draw on the CPU), a device on it
/// at feature level 11_0, a direct command queue, and Skia's context over the three (<see cref="GRContext.CreateDirect3D(GRD3DBackendContext)"/>).
/// SkiaSharp's Windows native links <c>d3d12.dll</c> but not DXGI, so the caller supplies all three; the COM calls go
/// through vtable slots (<see cref="D3D12Abi"/>, pinned by <c>D3D12AbiTests</c>), with no dependency beyond the OS.
/// The context is used by one thread at a time (the caller's lock); Direct3D 12 has no thread affinity.
/// <para>A removal (<c>GetDeviceRemovedReason</c>) seen by <see cref="CheckLost"/> or at dispose is kept, and Skia's
/// context is then abandoned without freeing anything on the device. The engine's own references (adapter, device,
/// queue: <see cref="D3D12References"/>) are released once each, after the context; Skia releases its own.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class D3D12Device : GrContextDevice
{
    private readonly D3D12References _references;

    internal D3D12Device(D3D12References references, GRContext context, D3D12AdapterInfo chosen, IReadOnlyList<D3D12AdapterInfo> adapters)
        : base(context, GpuRendering.Direct3D12, string.IsNullOrWhiteSpace(chosen.Name) ? "Direct3D 12 device" : chosen.Name)
    {
        _references = references;
        Adapter = chosen;
        Adapters = adapters;
    }

    /// <summary>The adapter the device is on.</summary>
    public D3D12AdapterInfo Adapter { get; }

    /// <summary>The engine's references to the adapter, the device and the queue.</summary>
    internal D3D12References References => _references;

    /// <summary>Every adapter DXGI listed, in its order, the software ones included.</summary>
    public IReadOnlyList<D3D12AdapterInfo> Adapters { get; }

    /// <summary>
    /// The device's COM reference count just before Skia's context was made and just after (Skia holds references of
    /// its own). A diagnostic for the probe: nothing is decided on it.
    /// </summary>
    internal (uint Before, uint After) DeviceReferencesAroundContext { get; private init; }

    /// <summary>
    /// Makes the device, its queue and Skia's context, or throws <see cref="D3D12DeviceException"/>: with
    /// <see cref="D3D12DeviceException.NoDevice"/> when DXGI lists no hardware adapter, else with what failed and its
    /// HRESULT. The device goes on the first of <see cref="D3D12Abi.Candidates"/> that Direct3D 12 accepts; the adapter
    /// chosen is logged.
    /// </summary>
    public static D3D12Device Create() => Create(ListAdapters);

    /// <summary><see cref="Create()"/> over the adapters <paramref name="list"/> gives (the tests' fakes).</summary>
    internal static D3D12Device Create(Func<DxgiAdapters> list)
    {
        using var adapters = list();
        var candidates = D3D12Abi.Candidates(adapters.Infos);
        if (candidates.Count == 0)
        {
            var names = adapters.Infos.Count == 0 ? "none" : string.Join(", ", adapters.Infos.Select(a => a.Name));
            throw new D3D12DeviceException($"No hardware graphics adapter (DXGI lists: {names})", noDevice: true);
        }

        nint device = 0;
        var chosen = -1;
        var refused = new List<string>();
        var lastHr = 0;
        var iid = D3D12Abi.IidID3D12Device;
        foreach (var i in candidates)
        {
            var hr = D3D12CreateDevice(adapters[i], D3D12Abi.FeatureLevel11_0, &iid, &device);
            if (hr >= 0)
            {
                chosen = i;
                break;
            }

            device = 0;
            lastHr = hr;
            refused.Add($"{adapters.Infos[i].Name} 0x{hr:X8}");
        }

        if (chosen < 0)
            throw new D3D12DeviceException($"D3D12CreateDevice failed on every hardware adapter ({string.Join(", ", refused)})", noDevice: false, lastHr);

        var references = new D3D12References(adapters.Take(chosen), device);
        GRContext? context = null;
        try
        {
            var desc = new D3D12CommandQueueDesc { Type = D3D12Abi.CommandListTypeDirect };
            iid = D3D12Abi.IidID3D12CommandQueue;
            nint queue = 0;
            var createQueue = (delegate* unmanaged[Stdcall]<nint, D3D12CommandQueueDesc*, Guid*, nint*, int>)D3D12Com.Slot(device, D3D12Abi.DeviceCreateCommandQueue);
            Check(createQueue(device, &desc, &iid, &queue), "ID3D12Device::CreateCommandQueue");
            references.Queue = queue;

            var before = references.DeviceReferences();
            using (var backend = new GRD3DBackendContext { Adapter = references.Adapter, Device = device, Queue = queue })
                context = GRContext.CreateDirect3D(backend);
            if (context is null)
                throw new D3D12DeviceException($"Skia couldn't make a Direct3D 12 context on {adapters.Infos[chosen].Name}", noDevice: false);

            var made = new D3D12Device(references, context, adapters.Infos[chosen], adapters.Infos)
            {
                DeviceReferencesAroundContext = (before, references.DeviceReferences()),
            };
            context = null;
            Log.ForContext<D3D12Device>().Information("Direct3D 12 adapter chosen: {Adapter}, number {Index} of the {Count} DXGI lists ({Adapters})",
                made.Adapter, chosen, adapters.Infos.Count, string.Join("; ", adapters.Infos));
            return made;
        }
        catch
        {
            context?.Dispose();
            references.Release();
            throw;
        }
    }

    /// <summary>
    /// The probe's look at every adapter: what DXGI says of it and the HRESULT of a Direct3D 12 device made on it at
    /// feature level 11_0 (released at once).
    /// </summary>
    internal static List<(D3D12AdapterInfo Adapter, int Hr)> TryEachAdapter()
    {
        using var adapters = ListAdapters();
        var tried = new List<(D3D12AdapterInfo, int)>();
        var iid = D3D12Abi.IidID3D12Device;
        for (var i = 0; i < adapters.Infos.Count; i++)
        {
            nint device = 0;
            var hr = D3D12CreateDevice(adapters[i], D3D12Abi.FeatureLevel11_0, &iid, &device);
            if (device != 0) D3D12Com.Release(device);
            tried.Add((adapters.Infos[i], hr));
        }

        return tried;
    }

    /// <summary>
    /// Null while the device works; else why it doesn't: the device was removed (its reason's HRESULT, kept, and Skia's
    /// context abandoned without freeing anything on it) or Skia abandoned its context.
    /// </summary>
    public override string? CheckLost()
    {
        var reason = _references.RemovedReason();
        if (reason == 0)
            return base.CheckLost();

        if (!Context.IsAbandoned)
            Abandon(releaseResources: false);
        return $"The Direct3D 12 device was removed (0x{reason:X8}).";
    }

    /// <summary>Removed: seen before, or now.</summary>
    protected override bool IsGone() => _references.RemovedReason() != 0;

    /// <summary>After Skia's context is gone: the queue, the device and the adapter, once each.</summary>
    protected override void ReleaseDevice() => _references.Release();

    /// <summary>Every adapter DXGI lists, in its order, each with its reference.</summary>
    private static DxgiAdapters ListAdapters()
    {
        nint factory = 0;
        var iid = D3D12Abi.IidIDXGIFactory1;
        Check(CreateDXGIFactory1(&iid, &factory), "CreateDXGIFactory1");
        var infos = new List<D3D12AdapterInfo>();
        var pointers = new List<nint>();
        try
        {
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)D3D12Com.Slot(factory, D3D12Abi.FactoryEnumAdapters1);
            for (uint i = 0; i < D3D12Abi.MaxAdapters; i++)
            {
                nint adapter = 0;
                var hr = enumAdapters(factory, i, &adapter);
                if (hr == D3D12Abi.DxgiErrorNotFound) break;
                Check(hr, $"IDXGIFactory1::EnumAdapters1({i})");
                pointers.Add(adapter);
                DxgiAdapterDesc1 desc;
                var getDesc = (delegate* unmanaged[Stdcall]<nint, DxgiAdapterDesc1*, int>)D3D12Com.Slot(adapter, D3D12Abi.AdapterGetDesc1);
                Check(getDesc(adapter, &desc), $"IDXGIAdapter1::GetDesc1({i})");
                infos.Add(D3D12AdapterInfo.Of(desc));
            }
        }
        catch
        {
            foreach (var p in pointers) D3D12Com.Release(p);
            D3D12Com.Release(factory);
            throw;
        }

        return new DxgiAdapters(infos, [.. pointers], factory);
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new D3D12DeviceException($"{what} failed (0x{hr:X8})", noDevice: false, hr);
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(Guid* riid, nint* factory);

    [DllImport("d3d12.dll", ExactSpelling = true)]
    private static extern int D3D12CreateDevice(nint adapter, int minimumFeatureLevel, Guid* riid, nint* device);
}

/// <summary>
/// The engine's own references to the adapter, the device and its queue. Skia keeps references of its own to what it is
/// given, so these are released once each (<see cref="Release"/>), whatever the counts say, and a second call releases
/// nothing. A removal the device reports is kept.
/// </summary>
internal sealed unsafe class D3D12References(nint adapter, nint device)
{
    private nint _adapter = adapter;
    private nint _device = device;
    private int _removed;
    private int _released;

    public nint Adapter => _adapter;

    public nint Device => _device;

    /// <summary>The direct command queue, once made.</summary>
    public nint Queue { get; set; }

    /// <summary>
    /// 0 while the device works; else why it was removed (<c>ID3D12Device::GetDeviceRemovedReason</c>), kept once seen,
    /// so it is answered again after the device's release too.
    /// </summary>
    public int RemovedReason()
    {
        if (_removed != 0 || _device == 0)
            return _removed;
        var reason = ((delegate* unmanaged[Stdcall]<nint, int>)D3D12Com.Slot(_device, D3D12Abi.DeviceGetDeviceRemovedReason))(_device);
        if (reason != 0)
            _removed = reason;
        return reason;
    }

    /// <summary>The device's COM reference count (a diagnostic).</summary>
    public uint DeviceReferences() => _device == 0 ? 0 : D3D12Com.References(_device);

    /// <summary>Releases the queue, the device and the adapter, each once; later calls release nothing.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;

        var (queue, device, adapter) = (Queue, _device, _adapter);
        Queue = _device = _adapter = 0;
        if (queue != 0) D3D12Com.Release(queue);
        if (device != 0) D3D12Com.Release(device);
        if (adapter != 0) D3D12Com.Release(adapter);
    }
}

/// <summary>
/// The adapters DXGI listed, in its order, with a reference to each and to the factory: <see cref="Take"/> hands one's
/// reference on; <see cref="Dispose"/> releases the rest.
/// </summary>
internal sealed class DxgiAdapters(IReadOnlyList<D3D12AdapterInfo> infos, nint[]? pointers = null, nint factory = 0) : IDisposable
{
    private readonly nint[] _pointers = pointers ?? new nint[infos.Count];
    private nint _factory = factory;

    public IReadOnlyList<D3D12AdapterInfo> Infos { get; } = infos;

    public nint this[int index] => _pointers[index];

    /// <summary>Adapter <paramref name="index"/>'s reference, now the caller's.</summary>
    public nint Take(int index)
    {
        var pointer = _pointers[index];
        _pointers[index] = 0;
        return pointer;
    }

    public void Dispose()
    {
        for (var i = 0; i < _pointers.Length; i++)
            if (Take(i) is var p && p != 0) D3D12Com.Release(p);
        if (_factory != 0) D3D12Com.Release(_factory);
        _factory = 0;
    }
}

/// <summary>COM through vtable slots.</summary>
internal static unsafe class D3D12Com
{
    public static nint Slot(nint obj, int slot) => (*(nint**)obj)[slot];

    public static uint Release(nint obj) => ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, D3D12Abi.UnknownRelease))(obj);

    /// <summary>An object's reference count: AddRef, then the count Release answers.</summary>
    public static uint References(nint obj)
    {
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, D3D12Abi.UnknownAddRef))(obj);
        return Release(obj);
    }
}

/// <summary>What DXGI says of an adapter (<c>DXGI_ADAPTER_DESC1</c>, the parts the choice and the log use).</summary>
internal sealed record D3D12AdapterInfo(string Name, uint VendorId, uint DeviceId, uint Flags, ulong DedicatedVideoMemory)
{
    public bool IsSoftware => (Flags & D3D12Abi.AdapterFlagSoftware) != 0;

    /// <summary>
    /// One of Microsoft's own adapters (PCI vendor 0x1414): the Basic Render Driver (DEV_008C), with or without the
    /// software flag, and Microsoft's display-only and virtual adapters. They draw on the CPU, if at all.
    /// </summary>
    public bool IsMicrosofts => VendorId == D3D12Abi.MicrosoftVendorId;

    internal static unsafe D3D12AdapterInfo Of(DxgiAdapterDesc1 desc)
    {
        var chars = new ReadOnlySpan<char>(desc.Description, D3D12Abi.DescriptionChars);
        var end = chars.IndexOf('\0');
        var name = new string(end >= 0 ? chars[..end] : chars);
        return new D3D12AdapterInfo(name, desc.VendorId, desc.DeviceId, desc.Flags, (ulong)desc.DedicatedVideoMemory);
    }

    public override string ToString() =>
        $"{Name} (VEN_{VendorId:X4}&DEV_{DeviceId:X4}, flags 0x{Flags:X}{(IsSoftware ? " software" : "")}, {DedicatedVideoMemory / (1024 * 1024)} MiB dedicated)";
}

/// <summary>Why <see cref="D3D12Device.Create()"/> made no device.</summary>
internal sealed class D3D12DeviceException : Exception
{
    public D3D12DeviceException(string message, bool noDevice, int hresult = 0) : base(message)
    {
        NoDevice = noDevice;
        HResult = hresult;
    }

    /// <summary>DXGI lists no hardware adapter (only software ones, or none).</summary>
    public bool NoDevice { get; }
}

/// <summary>
/// The ABI the device is made through, from the Windows SDK headers (each value names its header; pinned by
/// <c>D3D12AbiTests</c>): vtable slots counted from <c>IUnknown</c>'s three, IIDs, constants and the two structures.
/// </summary>
internal static class D3D12Abi
{
    // ---- vtable slots ----
    public const int UnknownAddRef = 1; // IUnknown::AddRef (unknwn.h)
    public const int UnknownRelease = 2; // IUnknown::Release (unknwn.h)
    public const int FactoryEnumAdapters1 = 12; // IDXGIFactory1::EnumAdapters1 (dxgi.h)
    public const int AdapterGetDesc1 = 10; // IDXGIAdapter1::GetDesc1 (dxgi.h)
    public const int DeviceCreateCommandQueue = 8; // ID3D12Device::CreateCommandQueue (d3d12.h)
    public const int DeviceGetDeviceRemovedReason = 37; // ID3D12Device::GetDeviceRemovedReason (d3d12.h)

    // ---- IIDs ----
    public static readonly Guid IidIDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387"); // dxgi.h
    public static readonly Guid IidIDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05"); // dxgi.h
    public static readonly Guid IidID3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7"); // d3d12.h
    public static readonly Guid IidID3D12CommandQueue = new("0ec870a6-5d7e-4c22-8cfc-5baae07616ed"); // d3d12.h

    // ---- values ----
    public const int DxgiErrorNotFound = unchecked((int)0x887A0002); // DXGI_ERROR_NOT_FOUND (winerror.h)
    public const uint AdapterFlagSoftware = 2; // DXGI_ADAPTER_FLAG_SOFTWARE (dxgi.h)
    public const int FeatureLevel11_0 = 0xB000; // D3D_FEATURE_LEVEL_11_0 (d3dcommon.h)
    public const int CommandListTypeDirect = 0; // D3D12_COMMAND_LIST_TYPE_DIRECT (d3d12.h)
    public const int DescriptionChars = 128; // DXGI_ADAPTER_DESC1.Description's WCHARs (dxgi.h)

    /// <summary>The adapters DXGI is asked for at most (a PC has a few).</summary>
    public const uint MaxAdapters = 64;

    /// <summary>Microsoft's PCI vendor id: the Basic Render Driver is VEN_1414&amp;DEV_008C.</summary>
    public const uint MicrosoftVendorId = 0x1414;

    /// <summary>
    /// The adapters a device is tried on, in DXGI's order: every one but the software ones and Microsoft's. Both checks
    /// are needed: with no monitor on the GPU, Windows lists a Basic Render Driver without the software flag first, and
    /// Direct3D 12 accepts a device on it (drawn on the CPU).
    /// </summary>
    public static List<int> Candidates(IReadOnlyList<D3D12AdapterInfo> adapters)
    {
        var candidates = new List<int>();
        for (var i = 0; i < adapters.Count; i++)
            if (!adapters[i].IsSoftware && !adapters[i].IsMicrosofts) candidates.Add(i);
        return candidates;
    }
}

/// <summary><c>DXGI_ADAPTER_DESC1</c> (dxgi.h).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct DxgiAdapterDesc1
{
    public fixed char Description[D3D12Abi.DescriptionChars];
    public uint VendorId;
    public uint DeviceId;
    public uint SubSysId;
    public uint Revision;
    public nuint DedicatedVideoMemory;
    public nuint DedicatedSystemMemory;
    public nuint SharedSystemMemory;
    public uint AdapterLuidLowPart;
    public int AdapterLuidHighPart;
    public uint Flags;
}

/// <summary><c>D3D12_COMMAND_QUEUE_DESC</c> (d3d12.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D12CommandQueueDesc
{
    public int Type;
    public int Priority;
    public int Flags;
    public uint NodeMask;
}
