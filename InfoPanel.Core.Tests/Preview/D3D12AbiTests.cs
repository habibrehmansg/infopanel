using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Core.Tests.Scenes;
using InfoPanel.Output.Gpu;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Preview;

/// <summary>
/// The Direct3D 12 device's ABI as <see cref="D3D12Abi"/> declares it, on any 64-bit OS: each vtable slot against the
/// SDK headers' method order, the IIDs and values against the headers, the two structures' layouts, and the adapter
/// choice. Nothing here calls Windows.
/// </summary>
public sealed class D3D12AbiTests
{
    // ---- the headers' method order, each interface after its base (unknwn.h, dxgi.h, d3d12.h) ----

    private static readonly string[] IUnknown = ["QueryInterface", "AddRef", "Release"];
    private static readonly string[] IDXGIObject = ["SetPrivateData", "SetPrivateDataInterface", "GetPrivateData", "GetParent"];
    private static readonly string[] IDXGIFactory = ["EnumAdapters", "MakeWindowAssociation", "GetWindowAssociation", "CreateSwapChain", "CreateSoftwareAdapter"];
    private static readonly string[] IDXGIFactory1 = ["EnumAdapters1", "IsCurrent"];
    private static readonly string[] IDXGIAdapter = ["EnumOutputs", "GetDesc", "CheckInterfaceSupport"];
    private static readonly string[] IDXGIAdapter1 = ["GetDesc1"];
    private static readonly string[] ID3D12Object = ["GetPrivateData", "SetPrivateData", "SetPrivateDataInterface", "SetName"];

    private static readonly string[] ID3D12Device =
    [
        "GetNodeCount", "CreateCommandQueue", "CreateCommandAllocator", "CreateGraphicsPipelineState", "CreateComputePipelineState",
        "CreateCommandList", "CheckFeatureSupport", "CreateDescriptorHeap", "GetDescriptorHandleIncrementSize", "CreateRootSignature",
        "CreateConstantBufferView", "CreateShaderResourceView", "CreateUnorderedAccessView", "CreateRenderTargetView",
        "CreateDepthStencilView", "CreateSampler", "CopyDescriptors", "CopyDescriptorsSimple", "GetResourceAllocationInfo",
        "GetCustomHeapProperties", "CreateCommittedResource", "CreateHeap", "CreatePlacedResource", "CreateReservedResource",
        "CreateSharedHandle", "OpenSharedHandle", "OpenSharedHandleByName", "MakeResident", "Evict", "CreateFence",
        "GetDeviceRemovedReason", "GetCopyableFootprints", "CreateQueryHeap", "SetStablePowerState", "CreateCommandSignature",
        "GetResourceTiling", "GetAdapterLuid",
    ];

    private static readonly Dictionary<string, string[][]> Chains = new()
    {
        ["IUnknown"] = [IUnknown],
        ["IDXGIFactory1"] = [IUnknown, IDXGIObject, IDXGIFactory, IDXGIFactory1],
        ["IDXGIAdapter1"] = [IUnknown, IDXGIObject, IDXGIAdapter, IDXGIAdapter1],
        ["ID3D12Device"] = [IUnknown, ID3D12Object, ID3D12Device],
    };

    public static TheoryData<string, string, int> Slots => new()
    {
        { "IUnknown", "AddRef", D3D12Abi.UnknownAddRef },
        { "IUnknown", "Release", D3D12Abi.UnknownRelease },
        { "IDXGIFactory1", "EnumAdapters1", D3D12Abi.FactoryEnumAdapters1 },
        { "IDXGIAdapter1", "GetDesc1", D3D12Abi.AdapterGetDesc1 },
        { "ID3D12Device", "CreateCommandQueue", D3D12Abi.DeviceCreateCommandQueue },
        { "ID3D12Device", "GetDeviceRemovedReason", D3D12Abi.DeviceGetDeviceRemovedReason },
    };

    [Theory]
    [MemberData(nameof(Slots))]
    public void EachSlot_IsTheMethodsPlaceInItsInterfacesVtable(string face, string method, int declared)
    {
        // The vtable: each base's methods in order, then the interface's own; the last interface's method is the one meant.
        var chain = Chains[face];
        var before = chain[..^1].Sum(c => c.Length);
        var own = Array.IndexOf(chain[^1], method);
        Assert.True(own >= 0, $"{face} declares no {method}");
        Assert.Equal(before + own, declared);
    }

    public static TheoryData<string, string, string> Values => new()
    {
        { "IID_IDXGIFactory1", "dxgi.h", $"{D3D12Abi.IidIDXGIFactory1}=770aae78-f26f-4dba-a829-253c83d1b387" },
        { "IID_IDXGIAdapter1", "dxgi.h", $"{D3D12Abi.IidIDXGIAdapter1}=29038f61-3839-4626-91fd-086879011a05" },
        { "IID_ID3D12Device", "d3d12.h", $"{D3D12Abi.IidID3D12Device}=189819f1-1db6-4b57-be54-1821339b85f7" },
        { "IID_ID3D12CommandQueue", "d3d12.h", $"{D3D12Abi.IidID3D12CommandQueue}=0ec870a6-5d7e-4c22-8cfc-5baae07616ed" },
        { "DXGI_ERROR_NOT_FOUND", "winerror.h", $"{unchecked((uint)D3D12Abi.DxgiErrorNotFound):X8}=887A0002" },
        { "DXGI_ADAPTER_FLAG_SOFTWARE", "dxgi.h", $"{D3D12Abi.AdapterFlagSoftware}=2" },
        { "D3D_FEATURE_LEVEL_11_0", "d3dcommon.h", $"{D3D12Abi.FeatureLevel11_0:X}=B000" },
        { "D3D12_COMMAND_LIST_TYPE_DIRECT", "d3d12.h", $"{D3D12Abi.CommandListTypeDirect}=0" },
        { "DXGI_ADAPTER_DESC1.Description", "dxgi.h", $"{D3D12Abi.DescriptionChars}=128" },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void EachValue_IsTheHeaders(string name, string header, string declaredEqualsDocumented)
    {
        var parts = declaredEqualsDocumented.Split('=');
        Assert.True(string.Equals(parts[0], parts[1], StringComparison.OrdinalIgnoreCase), $"{name} ({header}): declared {parts[0]}, documented {parts[1]}");
    }

    [Fact]
    public void TheStructures_HaveTheir64BitWindowsLayouts()
    {
        Assert.Equal(8, IntPtr.Size);

        // DXGI_ADAPTER_DESC1: WCHAR[128], four UINTs, three SIZE_Ts, a LUID (DWORD, LONG), UINT Flags; 8-aligned.
        Assert.Equal(312, Unsafe.SizeOf<DxgiAdapterDesc1>());
        Assert.Equal(312, Marshal.SizeOf<DxgiAdapterDesc1>());
        Assert.Equal(0, Offset<DxgiAdapterDesc1>(nameof(DxgiAdapterDesc1.Description)));
        Assert.Equal(256, Offset<DxgiAdapterDesc1>(nameof(DxgiAdapterDesc1.VendorId)));
        Assert.Equal(260, Offset<DxgiAdapterDesc1>(nameof(DxgiAdapterDesc1.DeviceId)));
        Assert.Equal(264, Offset<DxgiAdapterDesc1>(nameof(DxgiAdapterDesc1.SubSysId)));
        Assert.Equal(268, Offset<DxgiAdapterDesc1>(nameof(DxgiAdapterDesc1.Revision)));
        Assert.Equal(272, Offset<DxgiAdapterDesc1>(nameof(DxgiAdapterDesc1.DedicatedVideoMemory)));
        Assert.Equal(280, Offset<DxgiAdapterDesc1>(nameof(DxgiAdapterDesc1.DedicatedSystemMemory)));
        Assert.Equal(288, Offset<DxgiAdapterDesc1>(nameof(DxgiAdapterDesc1.SharedSystemMemory)));
        Assert.Equal(296, Offset<DxgiAdapterDesc1>(nameof(DxgiAdapterDesc1.AdapterLuidLowPart)));
        Assert.Equal(300, Offset<DxgiAdapterDesc1>(nameof(DxgiAdapterDesc1.AdapterLuidHighPart)));
        Assert.Equal(304, Offset<DxgiAdapterDesc1>(nameof(DxgiAdapterDesc1.Flags)));
        // Each field's width too: padding before an 8-aligned field would hide a SIZE_T declared as 4 bytes.
        Assert.Equal(
            [256, 4, 4, 4, 4, 8, 8, 8, 4, 4, 4],
            typeof(DxgiAdapterDesc1).GetFields().OrderBy(f => f.MetadataToken).Select(f => Marshal.SizeOf(f.FieldType)));

        // D3D12_COMMAND_QUEUE_DESC: Type, Priority, Flags, NodeMask.
        Assert.Equal(16, Unsafe.SizeOf<D3D12CommandQueueDesc>());
        Assert.Equal(16, Marshal.SizeOf<D3D12CommandQueueDesc>());
        Assert.Equal(0, Offset<D3D12CommandQueueDesc>(nameof(D3D12CommandQueueDesc.Type)));
        Assert.Equal(4, Offset<D3D12CommandQueueDesc>(nameof(D3D12CommandQueueDesc.Priority)));
        Assert.Equal(8, Offset<D3D12CommandQueueDesc>(nameof(D3D12CommandQueueDesc.Flags)));
        Assert.Equal(12, Offset<D3D12CommandQueueDesc>(nameof(D3D12CommandQueueDesc.NodeMask)));
        Assert.Equal([4, 4, 4, 4], typeof(D3D12CommandQueueDesc).GetFields().OrderBy(f => f.MetadataToken).Select(f => Marshal.SizeOf(f.FieldType)));
    }

    private static int Offset<T>(string field) where T : struct => (int)Marshal.OffsetOf<T>(field);

    // ---- the adapter ----

    private static D3D12AdapterInfo Hardware(string name) => new(name, 0x1002, 0x1900, 0, 512UL << 20);

    private static D3D12AdapterInfo Software(string name) => new(name, 0x1414, 0x8C, D3D12Abi.AdapterFlagSoftware, 0);

    private static D3D12AdapterInfo BasicRenderUnflagged => new("Microsoft Basic Render Driver", 0x1414, 0x8C, 0, 0);

    [Fact]
    public void TheCandidates_AreEveryAdapterButTheSoftwareOnesAndMicrosofts_InDxgisOrder()
    {
        Assert.Equal([0, 1], D3D12Abi.Candidates([Hardware("a"), Hardware("b"), Software("Microsoft Basic Render Driver")]));
        Assert.Equal([1, 2], D3D12Abi.Candidates([Software("Microsoft Basic Render Driver"), Hardware("a"), Hardware("b")]));
        Assert.Empty(D3D12Abi.Candidates([Software("Microsoft Basic Render Driver")]));
        Assert.Empty(D3D12Abi.Candidates([]));
        // As DXGI lists them in a signed-in session with no monitor on the GPU: a Basic Render Driver without the software
        // flag first (Direct3D 12 accepts a device there), the GPU after it, the flagged one last.
        Assert.Equal([1], D3D12Abi.Candidates([BasicRenderUnflagged, Hardware("gpu"), Software("Microsoft Basic Render Driver")]));
        // As in session 0 (a service, or ssh): the GPU first.
        Assert.Equal([0], D3D12Abi.Candidates([Hardware("gpu"), BasicRenderUnflagged, Software("Microsoft Basic Render Driver")]));
        Assert.Empty(D3D12Abi.Candidates([BasicRenderUnflagged, Software("Microsoft Basic Render Driver")]));
        // A remote adapter (DXGI_ADAPTER_FLAG_REMOTE, 1) of another vendor is tried.
        Assert.Equal([0], D3D12Abi.Candidates([new D3D12AdapterInfo("remote", 0x10DE, 1, 1, 0)]));
    }

    [Theory]
    [InlineData("AMD Radeon 780M Graphics")]
    [InlineData("")]
    public void TheAdaptersName_IsTheDescriptionUpToItsNul(string name)
    {
        var desc = new DxgiAdapterDesc1 { VendorId = 0x1002, DeviceId = 0x15BF, Flags = 0, DedicatedVideoMemory = 512u << 20 };
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref desc, 1));
        // Garbage after the NUL is never part of the name.
        Encoding.Unicode.GetBytes(name + "\0garbage", bytes);
        var info = D3D12AdapterInfo.Of(desc);
        Assert.Equal(name, info.Name);
        Assert.Equal((0x1002u, 0x15BFu, false, 512UL << 20), (info.VendorId, info.DeviceId, info.IsSoftware, info.DedicatedVideoMemory));
    }

    [Fact]
    public void ADescriptionWithNoNul_IsTakenWhole()
    {
        var desc = new DxgiAdapterDesc1 { Flags = D3D12Abi.AdapterFlagSoftware };
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref desc, 1));
        Encoding.Unicode.GetBytes(new string('x', D3D12Abi.DescriptionChars), bytes);
        var info = D3D12AdapterInfo.Of(desc);
        Assert.Equal(D3D12Abi.DescriptionChars, info.Name.Length);
        Assert.True(info.IsSoftware);
    }
}

/// <summary>
/// The Direct3D 12 probe, on Windows only: makes the device and Skia's context on it, draws the 1.x all-kinds reference
/// document there and on the CPU, reads the GPU's frame back and compares the two (the numbers in the test's output).
/// With no hardware adapter it skips with <c>GPU: …</c>, unless <c>INFOPANEL_REQUIRE_GPU=1</c>, which makes that a
/// failure. In the <c>Golden</c> collection: it uses the process-wide font state.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class D3D12ProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void AllKinds_DrawnOnDirect3D12_AndReadBack_MatchesTheCpuFrame()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OperatingSystem.IsLinux() ? "Linux: no Direct3D 12" : "Direct3D 12 is Windows only");
        if (OperatingSystem.IsWindows())
            Probe();
    }

    [SupportedOSPlatform("windows")]
    private void Probe()
    {
        var requireGpu = Environment.GetEnvironmentVariable("INFOPANEL_REQUIRE_GPU") == "1";
        output.WriteLine($"skia {typeof(SKSurface).Assembly.GetName().Version}; session {Process.GetCurrentProcess().SessionId}; INFOPANEL_REQUIRE_GPU={(requireGpu ? "1" : "unset")}");
        foreach (var (adapter, hr) in D3D12Device.TryEachAdapter())
            output.WriteLine($"D3D12CreateDevice at 11_0 on {adapter}: 0x{hr:X8}");
        var made = Stopwatch.StartNew();
        D3D12Device? created = null;
        try
        {
            created = D3D12Device.Create();
        }
        catch (D3D12DeviceException e) when (e.NoDevice && !requireGpu)
        {
            Assert.Skip($"GPU: {e.Message}");
        }

        using var device = created!;
        output.WriteLine($"device, queue and context in {made.Elapsed.TotalMilliseconds:F1} ms");
        for (var i = 0; i < device.Adapters.Count; i++)
            output.WriteLine($"adapter {i}: {device.Adapters[i]}{(ReferenceEquals(device.Adapters[i], device.Adapter) ? "  <- chosen" : "")}");
        output.WriteLine($"backend {device.Backend}, device '{device.Name}'; device references before/after the context {device.DeviceReferencesAroundContext}");
        Assert.Equal("direct3d12", device.Backend);
        Assert.False(device.Adapter.IsSoftware);

        using var media = V1ParityTests.FixtureMedia();
        var document = SceneKit.Load(Path.Combine(V1ParityTests.ReferenceDir, "all-kinds.document.json"));
        var options = new SceneRuntimeOptions { Inputs = new EveryKeyInputs(), Media = media, LocalZone = TimeZoneInfo.Utc, Locale = RegionalFormat.Invariant };
        using var snapshot = SceneRuntime.RenderOnce(document, GoldenScene.UtcNow, options);
        var info = new SKImageInfo(snapshot.Width, snapshot.Height, SKColorType.Rgba8888, SKAlphaType.Premul);

        // Today's path: a canvas over an Rgba8888 premultiplied bitmap.
        using var cpu = new SKBitmap(info);
        var cpuTimes = new List<double>();
        using (var canvas = new SKCanvas(cpu))
        {
            for (var i = 0; i < 6; i++)
            {
                var t = Stopwatch.GetTimestamp();
                canvas.Clear(SKColors.Transparent);
                snapshot.Draw(canvas, 1);
                canvas.Flush();
                cpuTimes.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }
        }

        // The GPU path: a surface on the device, flushed and waited on, read back into a bitmap of the same type.
        using var gpu = new SKBitmap(info);
        var drawTimes = new List<double>();
        var readTimes = new List<double>();
        using (var surface = SKSurface.Create(device.Context, true, info))
        {
            Assert.NotNull(surface);
            for (var i = 0; i < 6; i++)
            {
                var t = Stopwatch.GetTimestamp();
                surface.Canvas.Clear(SKColors.Transparent);
                snapshot.Draw(surface.Canvas, 1);
                device.Context.Flush(submit: true, synchronous: true);
                drawTimes.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                t = Stopwatch.GetTimestamp();
                Assert.True(surface.ReadPixels(info, gpu.GetPixels(), info.RowBytes, 0, 0), "ReadPixels failed");
                readTimes.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }
        }

        Assert.Null(device.CheckLost());
        device.Context.GetResourceCacheUsage(out var resources, out var bytes);

        var (max, over1, over4) = Compare(cpu, gpu);
        output.WriteLine($"all-kinds {info.Width}x{info.Height}: max delta R{max[0]} G{max[1]} B{max[2]} A{max[3]}; >1: {over1:P3}; >4: {over4:P3}");
        output.WriteLine($"times (first, then median of the other 5), ms: cpu draw {cpuTimes[0]:F2} / {Median(cpuTimes)}; gpu draw+flush {drawTimes[0]:F2} / {Median(drawTimes)}; read-back {readTimes[0]:F2} / {Median(readTimes)}");
        output.WriteLine($"skia gpu cache: {resources} resources, {bytes / 1048576.0:F1} MiB");

        var dump = Environment.GetEnvironmentVariable("INFOPANEL_GPU_PROBE_OUT");
        if (!string.IsNullOrEmpty(dump))
        {
            Directory.CreateDirectory(dump);
            Save(cpu, Path.Combine(dump, "all-kinds-cpu.png"));
            Save(gpu, Path.Combine(dump, "all-kinds-d3d12.png"));
        }

        // The plan's starting bound (the first runs set the one the frame-match tests keep): alpha within 1, and at most
        // 1 % of pixels off by more than 4 in any channel.
        Assert.True(max[3] <= 1, $"alpha differs by {max[3]}");
        Assert.True(over4 <= 0.01, $"{over4:P3} of pixels differ by more than 4");
    }

    private static string Median(List<double> times)
    {
        var rest = times.Skip(1).Order().ToList();
        return rest[rest.Count / 2].ToString("F2");
    }

    private static (int[] Max, double Over1, double Over4) Compare(SKBitmap a, SKBitmap b)
    {
        var x = a.GetPixelSpan();
        var y = b.GetPixelSpan();
        Assert.Equal(x.Length, y.Length);
        var max = new int[4];
        long over1 = 0, over4 = 0;
        var n = x.Length / 4;
        for (var i = 0; i < n; i++)
        {
            var m = 0;
            for (var c = 0; c < 4; c++)
            {
                var d = Math.Abs(x[i * 4 + c] - y[i * 4 + c]);
                if (d > max[c]) max[c] = d;
                if (d > m) m = d;
            }

            if (m > 1) over1++;
            if (m > 4) over4++;
        }

        return (max, (double)over1 / n, (double)over4 / n);
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}

// The device is Windows-only for the product. Over fake COM objects and a context on this computer's own GPU it makes no
// Windows call, so the tests below run wherever a GPU context can be made.
#pragma warning disable CA1416

/// <summary>
/// The Direct3D 12 device's lifetime over fake COM objects (adapter, device, queue): the engine's references released
/// once each whatever the counts say; a removal kept, and Skia's context then abandoned without freeing anything on the
/// device, whether a draw's check or the release sees it. The context is on this computer's own GPU
/// (<see cref="GpuContextForTests"/>).
/// </summary>
public sealed class D3D12DeviceTests
{
    /// <summary><c>DXGI_ERROR_DEVICE_REMOVED</c> (winerror.h).</summary>
    internal const int DeviceRemoved = unchecked((int)0x887A0005);

    internal const string RemovedMessage = "The Direct3D 12 device was removed (0x887A0005).";

    [Fact]
    public void TheReferences_AreReleasedOnceEach_AndARemovalIsKept()
    {
        using var com = new FakeD3D12();
        var references = com.References();
        Assert.Equal(0, references.RemovedReason());

        com.DeviceObject.RemovedReason = DeviceRemoved;
        Assert.Equal(DeviceRemoved, references.RemovedReason());
        com.DeviceObject.RemovedReason = 0;
        Assert.Equal(DeviceRemoved, references.RemovedReason());

        references.Release();
        references.Release();

        Assert.Equal((1, 1, 1), com.Releases);
        Assert.Equal(DeviceRemoved, references.RemovedReason());
        Assert.Equal(2, com.DeviceObject.ReasonsAsked);
    }

    [Fact]
    public void TheEnginesReferences_AreReleasedOnceEach_WhateverTheCountsSay_AndASecondDisposeReleasesNothing()
    {
        using var gpu = GpuContextForTests.ForThisComputer();
        // Counts that mislead (two wrappers on one adapter): every AddRef and Release answers the same number.
        using var com = new FakeD3D12(answers: 13);
        var device = com.Device(gpu.Context);

        device.Dispose();
        device.Dispose();

        Assert.Equal((1, 1, 1), com.Releases);
        Assert.True(device.AbandonedReleasing);
    }

    [Fact]
    public void ARemovalADrawsCheckSees_IsKept_AndSkiasContextIsAbandonedWithoutFreeingAnythingOnTheDevice()
    {
        using var gpu = GpuContextForTests.ForThisComputer();
        using var com = new FakeD3D12();
        var device = com.Device(gpu.Context);
        Assert.Null(device.CheckLost());

        com.DeviceObject.RemovedReason = DeviceRemoved;

        Assert.Equal(RemovedMessage, device.CheckLost());
        Assert.True(device.Context.IsAbandoned);
        Assert.False(device.AbandonedReleasing);
        // Kept: answered again without asking the device.
        com.DeviceObject.RemovedReason = 0;
        var asked = com.DeviceObject.ReasonsAsked;
        Assert.Equal(RemovedMessage, device.CheckLost());
        Assert.Equal(asked, com.DeviceObject.ReasonsAsked);

        device.Dispose();

        Assert.False(device.AbandonedReleasing);
        Assert.Equal((1, 1, 1), com.Releases);
    }

    [Fact]
    public void ARemovalAfterTheLastCheck_IsSeenByTheRelease_WhichAbandonsWithoutFreeing()
    {
        using var gpu = GpuContextForTests.ForThisComputer();
        using var com = new FakeD3D12();
        var device = com.Device(gpu.Context);
        Assert.Null(device.CheckLost());

        com.DeviceObject.RemovedReason = DeviceRemoved;
        device.Dispose();

        Assert.False(device.AbandonedReleasing);
        Assert.Equal((1, 1, 1), com.Releases);
    }
}

/// <summary>
/// A fake adapter, device and queue: native objects whose vtables count <c>AddRef</c> and <c>Release</c> and answer
/// <c>GetDeviceRemovedReason</c> as the test sets it.
/// </summary>
internal sealed class FakeD3D12(uint? answers = null) : IDisposable
{
    public FakeComObject Adapter { get; } = new(answers);

    public FakeComObject DeviceObject { get; } = new(answers);

    public FakeComObject Queue { get; } = new(answers);

    /// <summary>The releases of the adapter, the device and the queue.</summary>
    public (int Adapter, int Device, int Queue) Releases => (Adapter.Releases, DeviceObject.Releases, Queue.Releases);

    public D3D12References References() => new(Adapter.Pointer, DeviceObject.Pointer) { Queue = Queue.Pointer };

    /// <summary>A device over the fakes and <paramref name="context"/>, which it then owns.</summary>
    public D3D12Device Device(GRContext context, string name = "Fake Radeon")
    {
        var adapter = new D3D12AdapterInfo(name, 0x1002, 0x1900, 0, 512UL << 20);
        return new D3D12Device(References(), context, adapter, [adapter]);
    }

    public void Dispose()
    {
        Adapter.Dispose();
        DeviceObject.Dispose();
        Queue.Dispose();
    }
}

/// <summary>
/// A native COM object made in the test: a vtable with <c>AddRef</c>, <c>Release</c> and the device's
/// <c>GetDeviceRemovedReason</c> (the slots <see cref="D3D12Abi"/> names), each counted. Its count starts at 1 (the
/// engine's reference); <c>AddRef</c> and <c>Release</c> answer it, or a fixed number when one is given.
/// </summary>
internal sealed class FakeComObject : IDisposable
{
    private delegate uint CountSlot(nint self);

    private delegate int ReasonSlot(nint self);

    private static readonly ConcurrentDictionary<nint, FakeComObject> Live = new();

    // Kept in static fields: the native vtables point at these delegates.
    private static readonly CountSlot AddRefFn = self => Live[self].Count(+1);
    private static readonly CountSlot ReleaseFn = self => Live[self].Count(-1);
    private static readonly ReasonSlot ReasonFn = self => Live[self].Reason();

    private readonly Lock _lock = new();
    private readonly uint? _answers;
    private readonly nint _vtable;
    private int _count = 1;

    public FakeComObject(uint? answers = null)
    {
        _answers = answers;
        var slots = D3D12Abi.DeviceGetDeviceRemovedReason + 1;
        _vtable = Marshal.AllocHGlobal(slots * IntPtr.Size);
        for (var i = 0; i < slots; i++)
            Marshal.WriteIntPtr(_vtable, i * IntPtr.Size, IntPtr.Zero);
        Marshal.WriteIntPtr(_vtable, D3D12Abi.UnknownAddRef * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(AddRefFn));
        Marshal.WriteIntPtr(_vtable, D3D12Abi.UnknownRelease * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(ReleaseFn));
        Marshal.WriteIntPtr(_vtable, D3D12Abi.DeviceGetDeviceRemovedReason * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(ReasonFn));
        Pointer = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(Pointer, _vtable);
        Live[Pointer] = this;
    }

    public nint Pointer { get; }

    public int AddRefs { get; private set; }

    public int Releases { get; private set; }

    /// <summary>What <c>GetDeviceRemovedReason</c> answers: 0 (<c>S_OK</c>) while it works.</summary>
    public int RemovedReason { get; set; }

    public int ReasonsAsked { get; private set; }

    private uint Count(int delta)
    {
        lock (_lock)
        {
            if (delta > 0)
                AddRefs++;
            else
                Releases++;
            _count += delta;
            return _answers ?? (uint)Math.Max(_count, 0);
        }
    }

    private int Reason()
    {
        lock (_lock)
        {
            ReasonsAsked++;
            return RemovedReason;
        }
    }

    public void Dispose()
    {
        if (!Live.TryRemove(Pointer, out _))
            return;
        Marshal.FreeHGlobal(Pointer);
        Marshal.FreeHGlobal(_vtable);
    }
}

/// <summary>
/// A Skia context on this computer's own GPU for a device under test to own (its release abandons and disposes it):
/// Metal on macOS, Direct3D 12 on Windows (over a device of the engine's making). Linux skips
/// (<c>Linux: no GPU backend</c>); no GPU skips with <c>GPU: …</c>, unless <c>INFOPANEL_REQUIRE_GPU=1</c>, which makes it a
/// failure. Dispose, after the device under test's, releases the platform's objects.
/// </summary>
internal sealed class GpuContextForTests : IDisposable
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private readonly IDisposable? _owner;
    private nint _metalDevice;
    private nint _metalQueue;

    private GpuContextForTests(GRContext context, IDisposable? owner, nint metalDevice = 0, nint metalQueue = 0)
    {
        Context = context;
        _owner = owner;
        _metalDevice = metalDevice;
        _metalQueue = metalQueue;
    }

    public GRContext Context { get; }

    public static GpuContextForTests ForThisComputer()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Linux: no GPU backend");
        if (OperatingSystem.IsWindows())
        {
            D3D12Device real;
            try
            {
                real = D3D12Device.Create();
            }
            catch (D3D12DeviceException e) when (e.NoDevice)
            {
                throw NoGpu(e.Message);
            }

            var references = real.References;
            GRContext? context;
            using (var backend = new GRD3DBackendContext { Adapter = references.Adapter, Device = references.Device, Queue = references.Queue })
                context = GRContext.CreateDirect3D(backend);
            if (context is null)
            {
                real.Dispose();
                throw new InvalidOperationException("Skia made no second Direct3D 12 context.");
            }

            return new GpuContextForTests(context, real);
        }

        if (OperatingSystem.IsMacOS())
        {
            var device = MTLCreateSystemDefaultDevice();
            if (device == 0)
                throw NoGpu("This Mac has no Metal device.");
            var queue = Send(device, sel_registerName("newCommandQueue"));
            var context = GRContext.CreateMetal(new GRMtlBackendContext { DeviceHandle = device, QueueHandle = queue });
            if (context is null)
            {
                Send(queue, sel_registerName("release"));
                Send(device, sel_registerName("release"));
                throw new InvalidOperationException("Skia made no Metal context.");
            }

            return new GpuContextForTests(context, null, device, queue);
        }

        throw NoGpu("No GPU backend on this OS.");
    }

    private static Exception NoGpu(string why)
    {
        Assert.False(Environment.GetEnvironmentVariable("INFOPANEL_REQUIRE_GPU") == "1", $"GPU: {why} (INFOPANEL_REQUIRE_GPU=1)");
        Assert.Skip($"GPU: {why}");
        return new UnreachableException();
    }

    public void Dispose()
    {
        _owner?.Dispose();
        if (_metalQueue != 0)
            Send(_metalQueue, sel_registerName("release"));
        if (_metalDevice != 0)
            Send(_metalDevice, sel_registerName("release"));
        _metalQueue = _metalDevice = 0;
    }

    [DllImport("/System/Library/Frameworks/Metal.framework/Metal")]
    private static extern nint MTLCreateSystemDefaultDevice();

    [DllImport(Objc)]
    private static extern nint sel_registerName(string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);
}
