using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Threading;

namespace InfoPanel.Sensors.Volume;

/// <summary>
/// Windows: the output devices through Core Audio (the multimedia device API, <c>mmdeviceapi.h</c>, and
/// <c>IAudioEndpointVolume</c>, <c>endpointvolume.h</c>; no permission prompt), as the 1.x Volume Info plugin read them
/// through NAudio: the active render endpoints (<c>EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE)</c>) and the default
/// one for multimedia (<c>GetDefaultAudioEndpoint(eRender, eMultimedia)</c>; none, null). Each device's id is its
/// endpoint id (<c>{0.0.0.00000000}.{guid}</c>); its Name <c>PKEY_Device_FriendlyName</c> (<c>Speakers (Realtek(R)
/// Audio)</c>) and its Device Name <c>PKEY_DeviceInterface_FriendlyName</c> (<c>Realtek(R) Audio</c>), <c>Unknown</c>
/// when the endpoint has none (as NAudio reads them), read at each round so a renamed device follows; its volume the
/// endpoint's master volume scalar and its mute the endpoint's mute (an endpoint without a volume control has
/// neither). Each endpoint's objects are kept while it is listed and its reads succeed, so a round asks for its names,
/// volume and mute only; one whose read failed is replaced at the next listing. Nothing native is asked for until the
/// first listing: an enumerator that can't be made fails the listing (Volume unavailable, the engine runs on) and is
/// asked for again at most every 5 s.
/// <para>
/// COM: the process keeps the multithreaded apartment alive (<c>CoIncrementMTAUsage</c>, once, at the first activation), so
/// the thread-pool threads the source polls on use it without initialising COM themselves; the objects are made there
/// and are free-threaded. Source-generated COM (<see cref="GeneratedComInterfaceAttribute"/>) and
/// <see cref="LibraryImportAttribute"/>, no package; each interface whole up to the last slot used, in the SDK headers'
/// vtable order, every method PreserveSig with an int HRESULT (<c>WinAudioListerTests</c> pins the IIDs and slots).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinAudioLister : IAudioLister
{
    /// <summary>A failed activation is tried again at the first listing this long after it, not before.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private readonly Func<IWinAudioEndpoints> _activate;
    private readonly TimeProvider _time;
    private IWinAudioEndpoints? _endpoints;
    private long? _failedAt;
    private Exception? _failure;
    // The listed endpoints' objects by endpoint id, kept while they are listed.
    private Dictionary<string, Kept> _devices = new(StringComparer.Ordinal);

    /// <summary>A lister that asks for Core Audio's objects at its first listing, not before.</summary>
    public WinAudioLister()
        : this(WinAudioEndpoints.Activate)
    {
    }

    /// <summary>A lister over <paramref name="activate"/>'s endpoints (a test's).</summary>
    internal WinAudioLister(Func<IWinAudioEndpoints> activate, TimeProvider? time = null)
    {
        _activate = activate;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<IAudioDevice> List()
    {
        lock (_lock)
        {
            var endpoints = Endpoints();
            var listed = new List<IAudioDevice>();
            var kept = new Dictionary<string, Kept>(StringComparer.Ordinal);
            try
            {
                endpoints.EachActive(endpoint => listed.Add(Keep(endpoint, kept)));
            }
            catch
            {
                // The round failed: the objects made for it alone let go; the kept ones stay for the next.
                foreach (var (id, device) in kept)
                {
                    if (!_devices.TryGetValue(id, out var before) || before != device)
                        device.Dispose();
                }

                throw;
            }

            // The endpoints no longer listed, and those replaced, let go of their objects.
            foreach (var (id, device) in _devices)
            {
                if (!kept.TryGetValue(id, out var now) || now != device)
                    device.Dispose();
            }

            _devices = kept;
            return listed;
        }
    }

    public IAudioDevice? Default()
    {
        lock (_lock)
        {
            if (Endpoints().Default() is not { } endpoint)
                return null;
            var id = endpoint.Id;
            if (_devices.TryGetValue(id, out var device) && !device.Failed)
            {
                endpoint.Dispose();
                return device;
            }

            device?.Dispose();
            return _devices[id] = new Kept(endpoint);
        }
    }

    /// <summary>
    /// The enumerator, asked for at the first listing: one that fails (the audio service missing, say) leaves the
    /// listing failing until it is asked for again, at the first listing <see cref="RetryInterval"/> after it.
    /// </summary>
    private IWinAudioEndpoints Endpoints()
    {
        if (_endpoints != null)
            return _endpoints;
        var now = _time.GetTimestamp();
        if (_failedAt is { } at && _time.GetElapsedTime(at, now) < RetryInterval)
            throw new COMException($"Core Audio's device enumerator isn't available; asked again at most every {RetryInterval.TotalSeconds:0} s: {_failure!.Message}", _failure);
        try
        {
            return _endpoints = _activate();
        }
        catch (Exception ex)
        {
            _failedAt = now;
            _failure = ex;
            throw;
        }
    }

    /// <summary>
    /// The device kept for <paramref name="endpoint"/>'s id this round, or from the last while no read of it failed (the
    /// new one let go of); else the new one, kept in <paramref name="into"/>.
    /// </summary>
    private Kept Keep(IWinAudioEndpoint endpoint, Dictionary<string, Kept> into)
    {
        var id = endpoint.Id;
        if (into.TryGetValue(id, out var device) || _devices.TryGetValue(id, out device) && !device.Failed)
            endpoint.Dispose();
        else
            device = new Kept(endpoint);
        into[id] = device;
        return device;
    }

    /// <summary>
    /// A kept endpoint as the sampler reads it: a read that throws (its objects invalidated when the device went or
    /// changed, <c>AUDCLNT_E_DEVICE_INVALIDATED</c>, or any other failure) marks it, and the next listing or ask for the
    /// default takes a new endpoint for its id and lets go of this one's objects.
    /// </summary>
    private sealed class Kept(IWinAudioEndpoint endpoint) : IAudioDevice, IDisposable
    {
        private bool _disposed;

        public bool Failed { get; private set; }

        public string Id => endpoint.Id;

        public string Name => Read(static e => e.Name);

        public string DeviceName => Read(static e => e.DeviceName);

        public float? Volume => Read(static e => e.Volume);

        public bool? Mute => Read(static e => e.Mute);

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            endpoint.Dispose();
        }

        private T Read<T>(Func<IWinAudioEndpoint, T> read)
        {
            try
            {
                return read(endpoint);
            }
            catch
            {
                Failed = true;
                throw;
            }
        }
    }
}

/// <summary>Core Audio's device enumerator as <see cref="WinAudioLister"/> asks it (the seam its tests replace).</summary>
internal interface IWinAudioEndpoints
{
    /// <summary>Hands each active render endpoint to <paramref name="take"/>, in the system's order; each is the taker's.</summary>
    void EachActive(Action<IWinAudioEndpoint> take);

    /// <summary>The default render endpoint for multimedia, the caller's; null when there is none.</summary>
    IWinAudioEndpoint? Default();
}

/// <summary>One endpoint's objects, its id read when it was made; disposing lets go of them.</summary>
internal interface IWinAudioEndpoint : IAudioDevice, IDisposable
{
}

/// <summary>The enumerator over Core Audio itself.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WinAudioEndpoints : IWinAudioEndpoints
{
    private const int ERender = 0;
    private const int EMultimedia = 1;
    private const uint DeviceStateActive = 1;
    private const int ENotFound = unchecked((int)0x80070490); // HRESULT_FROM_WIN32(ERROR_NOT_FOUND): no default endpoint

    private static readonly Guid ClsidMMDeviceEnumerator = new("bcde0395-e52f-467c-8e3d-c4579291692e");
    private static readonly Lock MtaLock = new();
    private static bool _mta;

    private readonly IMMDeviceEnumerator _enumerator;

    private WinAudioEndpoints(IMMDeviceEnumerator enumerator) => _enumerator = enumerator;

    /// <summary>The multithreaded apartment kept (once a process) and the enumerator made; throws when either fails.</summary>
    public static IWinAudioEndpoints Activate()
    {
        KeepMta();
        Check(WinAudioNative.CoCreateInstance(ClsidMMDeviceEnumerator, 0, WinAudioDevice.ClsctxAll, typeof(IMMDeviceEnumerator).GUID, out var p), "CoCreateInstance(MMDeviceEnumerator)");
        return new WinAudioEndpoints(WinAudioWrappers.Wrap<IMMDeviceEnumerator>(p));
    }

    public void EachActive(Action<IWinAudioEndpoint> take)
    {
        Check(_enumerator.EnumAudioEndpoints(ERender, DeviceStateActive, out var pc), "EnumAudioEndpoints");
        var collection = WinAudioWrappers.Wrap<IMMDeviceCollection>(pc);
        try
        {
            Check(collection.GetCount(out var count), "IMMDeviceCollection.GetCount");
            for (uint i = 0; i < count; i++)
            {
                Check(collection.Item(i, out var pd), "IMMDeviceCollection.Item");
                take(WinAudioDevice.Of(pd));
            }
        }
        finally
        {
            WinAudioWrappers.Free(collection);
        }
    }

    public IWinAudioEndpoint? Default()
    {
        var hr = _enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var p);
        if (hr == ENotFound)
            return null;
        Check(hr, "GetDefaultAudioEndpoint");
        return WinAudioDevice.Of(p);
    }

    private static void KeepMta()
    {
        lock (MtaLock)
        {
            if (_mta)
                return;
            Check(WinAudioNative.CoIncrementMTAUsage(out _), "CoIncrementMTAUsage");
            _mta = true;
        }
    }

    internal static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }
}

/// <summary>One endpoint: its names read at each ask, its volume control activated once.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WinAudioDevice : IWinAudioEndpoint
{
    internal const uint ClsctxAll = 0x17;
    private const int ENoInterface = unchecked((int)0x80004002);

    // PKEY_Device_FriendlyName and PKEY_DeviceInterface_FriendlyName (functiondiscoverykeys_devpkey.h).
    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PropertyKey InterfaceFriendlyName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);

    private readonly IMMDevice _endpoint;
    private IAudioEndpointVolume? _volume;
    private bool _noVolume;

    private WinAudioDevice(string id, IMMDevice endpoint)
    {
        Id = id;
        _endpoint = endpoint;
    }

    /// <summary>The endpoint <paramref name="p"/> (its reference taken over) with its id; let go of when that can't be read.</summary>
    public static WinAudioDevice Of(nint p)
    {
        var endpoint = WinAudioWrappers.Wrap<IMMDevice>(p);
        try
        {
            return new WinAudioDevice(IdOf(endpoint), endpoint);
        }
        catch
        {
            WinAudioWrappers.Free(endpoint);
            throw;
        }
    }

    public string Id { get; }

    public string Name => Text(FriendlyName);

    public string DeviceName => Text(InterfaceFriendlyName);

    public float? Volume
    {
        get
        {
            if (Control() is not { } control)
                return null;
            Check(control.GetMasterVolumeLevelScalar(out var scalar), "IAudioEndpointVolume.GetMasterVolumeLevelScalar");
            return scalar;
        }
    }

    public bool? Mute
    {
        get
        {
            if (Control() is not { } control)
                return null;
            Check(control.GetMute(out var mute), "IAudioEndpointVolume.GetMute");
            return mute != 0;
        }
    }

    public void Dispose()
    {
        WinAudioWrappers.Free(_volume);
        _volume = null;
        WinAudioWrappers.Free(_endpoint);
    }

    private static void Check(int hr, string what) => WinAudioEndpoints.Check(hr, what);

    private static string IdOf(IMMDevice endpoint)
    {
        Check(endpoint.GetId(out var p), "IMMDevice.GetId");
        try
        {
            return Marshal.PtrToStringUni(p) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>The endpoint's volume control, activated at the first ask; null when it has none.</summary>
    private IAudioEndpointVolume? Control()
    {
        if (_volume != null || _noVolume)
            return _volume;
        var hr = _endpoint.Activate(typeof(IAudioEndpointVolume).GUID, ClsctxAll, 0, out var p);
        if (hr == ENoInterface)
        {
            _noVolume = true;
            return null;
        }

        Check(hr, "IMMDevice.Activate(IAudioEndpointVolume)");
        _volume = WinAudioWrappers.Wrap<IAudioEndpointVolume>(p);
        return _volume;
    }

    /// <summary>A text property of the endpoint, <c>Unknown</c> when it has none (as NAudio reads it).</summary>
    private string Text(PropertyKey key)
    {
        Check(_endpoint.OpenPropertyStore(0 /* STGM_READ */, out var ps), "IMMDevice.OpenPropertyStore");
        var store = WinAudioWrappers.Wrap<IPropertyStore>(ps);
        PropVariant value = default;
        try
        {
            Check(store.GetValue(key, &value), "IPropertyStore.GetValue");
            return value.Vt == PropVariant.VtLpwstr && value.Pointer != 0 ? Marshal.PtrToStringUni(value.Pointer) ?? "" : "Unknown";
        }
        finally
        {
            WinAudioNative.PropVariantClear(&value);
            WinAudioWrappers.Free(store);
        }
    }
}

/// <summary><c>PROPERTYKEY</c> (wtypes.h): a property set and an id in it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey(Guid set, uint id)
{
    public readonly Guid Set = set;
    public readonly uint Id = id;
}

/// <summary><c>PROPVARIANT</c> (propidlbase.h), as far as a text property reads it: its type, and its value's first pointer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    /// <summary><c>VT_LPWSTR</c> (wtypes.h).</summary>
    public const ushort VtLpwstr = 31;

    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Pointer;
    public nint Pointer2;
}

/// <summary><c>IMMDeviceEnumerator</c> (mmdeviceapi.h), up to <c>GetDefaultAudioEndpoint</c>.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("a95664d2-9614-4f35-a746-de8db63617e6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out nint endpoint);
}

/// <summary><c>IMMDeviceCollection</c> (mmdeviceapi.h), whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out nint device);
}

/// <summary><c>IMMDevice</c> (mmdeviceapi.h), whole.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("d666063f-1587-4e43-81f1-b948e807363f")]
internal partial interface IMMDevice
{
    [PreserveSig] int Activate(in Guid iid, uint clsCtx, nint activationParams, out nint iface);
    [PreserveSig] int OpenPropertyStore(uint access, out nint properties);
    [PreserveSig] int GetId(out nint id);
    [PreserveSig] int GetState(out uint state);
}

/// <summary><c>IPropertyStore</c> (propsys.h), up to <c>GetValue</c>.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
internal unsafe partial interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(in PropertyKey key, PropVariant* value);
}

/// <summary><c>IAudioEndpointVolume</c> (endpointvolume.h), up to <c>GetMute</c>.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("5cdf2c82-841e-4546-9722-0cf74078229a")]
internal unsafe partial interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(nint notify);
    [PreserveSig] int UnregisterControlChangeNotify(nint notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, Guid* context);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, Guid* context);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, Guid* context);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, Guid* context);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute(int mute, Guid* context);
    [PreserveSig] int GetMute(out int mute);
}

/// <summary>The ole32 exports <see cref="WinAudioLister"/> calls; each answers an HRESULT.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WinAudioNative
{
    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint clsCtx, in Guid iid, out nint obj);

    [LibraryImport("ole32.dll")]
    public static partial int CoIncrementMTAUsage(out nint cookie);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(PropVariant* value);
}

/// <summary>
/// The wrappers the lister makes: each from <see cref="StrategyBasedComWrappers"/> as a unique instance that takes over
/// the caller's reference, released deterministically (<see cref="Free"/>), never left to the collector.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WinAudioWrappers
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Wraps <paramref name="p"/>, taking the caller's reference over (released here: the wrapper holds its own).</summary>
    public static T Wrap<T>(nint p) where T : class
    {
        if (p == 0)
            throw new COMException($"a null {typeof(T).Name}");
        object wrapper;
        try
        {
            wrapper = Wrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(p);
        }

        if (wrapper is T typed)
            return typed;
        Free(wrapper);
        throw new InvalidCastException($"the object isn't an {typeof(T).Name}");
    }

    /// <summary>Releases a wrapper's references now; null does nothing.</summary>
    public static void Free(object? wrapper)
    {
        if (wrapper is ComObject com)
            com.FinalRelease();
    }
}
