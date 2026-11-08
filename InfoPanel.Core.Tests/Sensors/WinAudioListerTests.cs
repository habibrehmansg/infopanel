using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Volume;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

// The declarations pinned below are Windows-only for the product; their IIDs, slot order and layouts are metadata that
// any OS reads. Nothing here calls Windows but the tests that skip elsewhere.
#pragma warning disable CA1416

/// <summary>
/// The volume source's Windows lister (<see cref="WinAudioLister"/>), on this PC's own output devices: on Windows only
/// (skipped elsewhere, and on a PC with no active output device), the default output device is one of those listed,
/// with its endpoint id, its two names, a volume from 0 to 1 and a mute; each endpoint's objects are kept from round to
/// round; and the source over it reads the default device's five entries live, the volume as the lister reads it. On
/// any OS, Core Audio's interfaces as the lister declares them against the SDK headers: IIDs, slot order, layouts.
/// </summary>
public sealed class WinAudioListerTests
{
    /// <summary>The lister, and this PC's output devices; skips off Windows and when the PC has none.</summary>
    private static (WinAudioLister Lister, IReadOnlyList<IAudioDevice> Devices) Listed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Core Audio's device API is Windows'.");
        var lister = new WinAudioLister();
        var devices = lister.List();
        Assert.SkipWhen(devices.Count == 0, "This PC has no active output device.");
        return (lister, devices);
    }

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact]
    public void OnWindows_TheDefaultOutputDevice_IsListed_WithItsEndpointId_ItsNames_AVolume_AndAMute()
    {
        var (lister, devices) = Listed();
        foreach (var d in devices)
            Log($"listed {d.Id} | {d.Name} | {d.DeviceName} | {d.Volume} | {d.Mute}");

        var def = lister.Default();

        Assert.NotNull(def);
        Log($"default {def.Id}");
        Assert.Contains(def.Id, devices.Select(d => d.Id));
        Assert.All(devices, d =>
        {
            Assert.Matches(@"^\{0\.0\.0\.00000000\}\.\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}$", d.Id);
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
            Assert.False(string.IsNullOrWhiteSpace(d.DeviceName));
            Assert.InRange(d.Volume!.Value, 0f, 1f);
            Assert.NotNull(d.Mute);
        });
    }

    [Fact]
    public void OnWindows_EachEndpointsObjects_AreKept_FromRoundToRound()
    {
        var (lister, first) = Listed();

        var second = lister.List();
        var def = lister.Default();

        Assert.Equal(first.Select(d => d.Id), second.Select(d => d.Id));
        Assert.All(first.Zip(second), pair => Assert.Same(pair.First, pair.Second));
        Assert.Same(second.Single(d => d.Id == def!.Id), def);
    }

    [Fact]
    public void OnWindows_TheSource_ReadsTheDefaultDevice_Live_AsTheListerReadsIt()
    {
        var (lister, _) = Listed();
        var def = lister.Default()!;
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);

        using var source = VolumeSource.CreateSystem(time);
        source.Open();

        string[] ids = ["device_friendly_name", "friendly_name", "short_name", "volume", "mute"];
        Assert.All(ids, id => Assert.Equal(SensorStatus.Live, source.GetStatus(VolumeSource.KeyOf(VolumeSource.DefaultContainer, id))));
        Assert.All(ids, id => Assert.Equal(SensorStatus.Live, source.GetStatus(VolumeSource.KeyOf(VolumeSource.ContainerOf(def.Id), id))));
        Assert.True(source.TryRead(VolumeSource.KeyOf(VolumeSource.DefaultContainer, "volume"), out var volume));
        Assert.Equal(VolumeSource.Percent(def.Volume!.Value), volume.ValueNow);
        Assert.Equal(def.Name, source.ReadState(VolumeSource.KeyOf(VolumeSource.DefaultContainer, "friendly_name")).Reading.ValueText);
        Assert.Equal(def.DeviceName, source.ReadState(VolumeSource.KeyOf(VolumeSource.DefaultContainer, "device_friendly_name")).Reading.ValueText);
        Assert.Equal(VolumeSource.MuteText(def.Mute!.Value), source.ReadState(VolumeSource.KeyOf(VolumeSource.DefaultContainer, "mute")).Reading.ValueText);
        Log($"default volume {volume.ValueNow} %, '{def.Name}'");
    }

    // ---- Activation and recovery, on any OS through the lister's seam ----

    // REGDB_E_CLASSNOTREG and AUDCLNT_E_DEVICE_INVALIDATED.
    private const int ClassNotRegistered = unchecked((int)0x80040154);
    private const int DeviceInvalidated = unchecked((int)0x88890004);

    private const string SpeakersId = "{0.0.0.00000000}.{a1b2c3d4-0000-4000-8000-0123456789ab}";
    private const string HeadphonesId = "{0.0.0.00000000}.{ffffffff-0000-4000-8000-0123456789ab}";

    /// <summary>One endpoint's objects, as Core Audio hands them out: a new one each time it is asked for.</summary>
    private sealed class FakeEndpoint(string id) : IWinAudioEndpoint
    {
        /// <summary>When set, every read throws it (the device's objects invalidated).</summary>
        public Exception? Throws;

        public int Disposed;

        public string Id => id;
        public string Name => Throws is { } e ? throw e : $"Speakers ({id[^4..]})";
        public string DeviceName => Throws is { } e ? throw e : id[^4..];
        public float? Volume => Throws is { } e ? throw e : 0.5f;
        public bool? Mute => Throws is { } e ? throw e : false;

        public void Dispose() => Disposed++;
    }

    /// <summary>The enumerator: the active endpoints by id and the default's, every object it made.</summary>
    private sealed class FakeEndpoints : IWinAudioEndpoints
    {
        public readonly List<string> Active = [];
        public string? DefaultId;
        public readonly List<FakeEndpoint> Made = [];

        public void EachActive(Action<IWinAudioEndpoint> take)
        {
            foreach (var id in Active)
                take(Make(id));
        }

        public IWinAudioEndpoint? Default() => DefaultId is { } id ? Make(id) : null;

        public IEnumerable<FakeEndpoint> Of(string id) => Made.Where(e => e.Id == id);

        private FakeEndpoint Make(string id)
        {
            var endpoint = new FakeEndpoint(id);
            Made.Add(endpoint);
            return endpoint;
        }
    }

    private sealed class CollectingSink(List<Serilog.Events.LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    private static FakeEndpoints SpeakersAndHeadphones()
    {
        var endpoints = new FakeEndpoints { DefaultId = SpeakersId };
        endpoints.Active.AddRange([SpeakersId, HeadphonesId]);
        return endpoints;
    }

    [Fact]
    public void AFailedActivation_LeavesTheSourceMade_AndVolumeUnavailable_IsTriedAgainAtMostEvery5s_AndALaterOneListsTheDevices()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
        var events = new List<Serilog.Events.LogEvent>();
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger();
        var endpoints = SpeakersAndHeadphones();
        var activations = 0;
        var registered = false;

        var lister = new WinAudioLister(() =>
        {
            activations++;
            return registered ? endpoints : throw new COMException("CoCreateInstance(MMDeviceEnumerator) failed", ClassNotRegistered);
        }, time);
        using var source = VolumeSource.Create(lister, time, log: log);
        source.Open();

        Assert.Equal(1, activations);
        Assert.Empty(source.GetSensors());
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(VolumeSource.KeyOf(VolumeSource.DefaultContainer, "volume")));

        // The polls within 5 s of the failure don't ask again.
        for (var i = 0; i < 19; i++)
        {
            time.Advance(VolumeSource.Interval);
            source.Poll();
        }

        Assert.Equal(1, activations);
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(VolumeSource.KeyOf(VolumeSource.DefaultContainer, "volume")));
        Assert.Single(events, e => e.Level == Serilog.Events.LogEventLevel.Warning);

        // 5 s after it, the next poll asks again, and lists the devices; the one after reads them.
        registered = true;
        time.Advance(VolumeSource.Interval);
        source.Poll();
        time.Advance(VolumeSource.Interval);
        source.Poll();

        Assert.Equal(2, activations);
        Assert.Equal(SensorStatus.Live, source.GetStatus(VolumeSource.KeyOf(VolumeSource.DefaultContainer, "volume")));
        Assert.Equal(SensorStatus.Live, source.GetStatus(VolumeSource.KeyOf(VolumeSource.ContainerOf(HeadphonesId), "volume")));
        Assert.True(source.TryRead(VolumeSource.KeyOf(VolumeSource.DefaultContainer, "volume"), out var volume));
        Assert.Equal(50, volume.ValueNow);
        Assert.Single(events, e => e.Level == Serilog.Events.LogEventLevel.Warning);
    }

    [Fact]
    public void AListedEndpointWhoseObjectsAreInvalidated_IsReplacedAtTheNextListing_ItsOldObjectsLetGoOnce()
    {
        var endpoints = SpeakersAndHeadphones();
        var lister = new WinAudioLister(() => endpoints);
        var first = lister.List();
        Assert.Same(first[0], lister.Default());
        var dead = endpoints.Of(SpeakersId).First();

        dead.Throws = new COMException("IAudioEndpointVolume.GetMasterVolumeLevelScalar failed", DeviceInvalidated);
        Assert.Throws<COMException>(() => first[0].Volume);
        var second = lister.List();
        var def = lister.Default();
        var third = lister.List();

        Assert.Equal([SpeakersId, HeadphonesId], second.Select(d => d.Id));
        Assert.NotSame(first[0], second[0]);
        Assert.Equal(0.5f, second[0].Volume);
        Assert.Same(second[0], def);
        Assert.Same(second[0], third[0]);
        Assert.Same(first[1], second[1]);
        Assert.Equal(1, dead.Disposed);
        // Every object made for Speakers but the one kept now was let go of, once.
        var kept = endpoints.Of(SpeakersId).Where(e => e.Disposed == 0).ToList();
        Assert.Single(kept);
        Assert.All(endpoints.Of(SpeakersId).Except(kept), e => Assert.Equal(1, e.Disposed));
    }

    [Fact]
    public void TheDefaultEndpoint_WhoseObjectsAreInvalidated_IsReplacedAtTheNextAsk_AndTheSourceReadsItAgain()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
        var endpoints = SpeakersAndHeadphones();
        var lister = new WinAudioLister(() => endpoints, time);

        // Asked alone, between listings.
        var def = lister.Default()!;
        var dead = endpoints.Of(SpeakersId).Single(e => e.Disposed == 0);
        dead.Throws = new COMException("IAudioEndpointVolume.GetMute failed", DeviceInvalidated);
        Assert.Throws<COMException>(() => def.Mute);
        var again = lister.Default()!;

        Assert.NotSame(def, again);
        Assert.Equal(false, again.Mute);
        Assert.Equal(1, dead.Disposed);

        // Through the source: the default fails for the round it went dead in, and reads live again at the next.
        using var source = VolumeSource.Create(lister, time);
        source.Open();
        var key = VolumeSource.KeyOf(VolumeSource.DefaultContainer, "mute");
        Assert.Equal(SensorStatus.Live, source.GetStatus(key));
        var live = endpoints.Of(SpeakersId).Single(e => e.Disposed == 0);
        live.Throws = new COMException("IMMDevice.OpenPropertyStore failed", DeviceInvalidated);
        time.Advance(VolumeSource.Interval);
        source.Poll();
        time.Advance(VolumeSource.Interval);
        source.Poll();

        Assert.Equal(SensorStatus.Live, source.GetStatus(key));
        Assert.Equal("False", source.ReadState(key).Reading.ValueText);
        Assert.Equal(1, live.Disposed);
    }

    // ---- The declarations, on any OS ----

    /// <summary>Each interface's IID and its methods in slot order (after IUnknown's three), from the SDK headers.</summary>
    public static TheoryData<Type, string, string[]> Interfaces() => new()
    {
        { typeof(IMMDeviceEnumerator), "a95664d2-9614-4f35-a746-de8db63617e6", ["EnumAudioEndpoints", "GetDefaultAudioEndpoint"] },
        { typeof(IMMDeviceCollection), "0bd7a1be-7a1a-44db-8397-cc5392387b5e", ["GetCount", "Item"] },
        { typeof(IMMDevice), "d666063f-1587-4e43-81f1-b948e807363f", ["Activate", "OpenPropertyStore", "GetId", "GetState"] },
        { typeof(IPropertyStore), "886d8eeb-8cf2-4446-8d02-cdba1dbdcf99", ["GetCount", "GetAt", "GetValue"] },
        {
            typeof(IAudioEndpointVolume), "5cdf2c82-841e-4546-9722-0cf74078229a",
            [
                "RegisterControlChangeNotify", "UnregisterControlChangeNotify", "GetChannelCount", "SetMasterVolumeLevel",
                "SetMasterVolumeLevelScalar", "GetMasterVolumeLevel", "GetMasterVolumeLevelScalar", "SetChannelVolumeLevel",
                "SetChannelVolumeLevelScalar", "GetChannelVolumeLevel", "GetChannelVolumeLevelScalar", "SetMute", "GetMute",
            ]
        },
    };

    [Theory]
    [MemberData(nameof(Interfaces))]
    public void TheInterfaces_HaveTheHeadersIids_AndSlots_EachPreserveSig_ReturningAnHresult(Type type, string iid, string[] slots)
    {
        Assert.Equal(Guid.Parse(iid), type.GUID);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(m => m.MetadataToken).ToArray();
        Assert.Equal(slots, methods.Select(m => m.Name));
        Assert.All(methods, m =>
        {
            Assert.Equal(typeof(int), m.ReturnType);
            Assert.True(m.MethodImplementationFlags.HasFlag(MethodImplAttributes.PreserveSig), $"{type.Name}.{m.Name} isn't PreserveSig");
        });
    }

    [Fact]
    public void TheStructures_HaveTheir64BitWindowsLayouts_AndTheKeysAndTypeAreTheHeaders()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(20, Unsafe.SizeOf<PropertyKey>());
        Assert.Equal(16, (int)Marshal.OffsetOf<PropertyKey>(nameof(PropertyKey.Id)));
        Assert.Equal(24, Unsafe.SizeOf<PropVariant>());
        Assert.Equal(0, (int)Marshal.OffsetOf<PropVariant>(nameof(PropVariant.Vt)));
        Assert.Equal(8, (int)Marshal.OffsetOf<PropVariant>(nameof(PropVariant.Pointer)));
        Assert.Equal(31, PropVariant.VtLpwstr);
    }
}
