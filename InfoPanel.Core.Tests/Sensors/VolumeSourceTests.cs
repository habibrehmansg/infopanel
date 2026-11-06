using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Volume;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// The volume source (<c>infopanel.volume</c>) on a fake lister: the 1.x Volume Info plugin's entries for the default
/// output device and for each output device under keys of its own (the device id the plugin SDK's id of the system's),
/// polled every 250 ms, the default followed as it changes, devices followed as they come and go, the volume rounded as
/// 1.x rounded it, the mute as 1.x wrote it, a device without a volume or a mute reading it unavailable, bounds, and
/// failures kept to the device that failed. The plugin's keys are never answered.
/// </summary>
public sealed class VolumeSourceTests : IDisposable
{
    private static readonly string[] Ids = ["device_friendly_name", "friendly_name", "short_name", "volume", "mute"];

    // A Windows endpoint id, as the 1.x plugin's containers had it.
    private const string SpeakersId = "{0.0.0.00000000}.{a1b2c3d4-0000-4000-8000-0123456789ab}";
    private const string Speakers = "00000000000a1b2c3d40000400080000123456789ab";

    private sealed class FakeDevice(string id, string name, string? deviceName = null, float? volume = 0.5f, bool? mute = false) : IAudioDevice
    {
        public string DeviceId = id;
        public string FriendlyName = name;
        public string InterfaceName = deviceName ?? name;
        public float? Level = volume;
        public bool? Muted = mute;

        /// <summary>When set, reading the volume throws it (a device that went while it was read).</summary>
        public Exception? Throws;

        public int Reads;

        public string Id => DeviceId;
        public string Name => FriendlyName;
        public string DeviceName => InterfaceName;
        public float? Volume
        {
            get
            {
                Reads++;
                return Throws is { } e ? throw e : Level;
            }
        }

        public bool? Mute => Muted;
    }

    private sealed class FakeLister : IAudioLister
    {
        public readonly List<FakeDevice> Devices = [];
        public FakeDevice? DefaultDevice;
        public Exception? Throws;
        public Exception? DefaultThrows;

        public IReadOnlyList<IAudioDevice> List() => Throws is { } e ? throw e : Devices.ToArray();

        public IAudioDevice? Default() => DefaultThrows is { } e ? throw e : DefaultDevice;
    }

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeLister _lister = new();
    private readonly List<LogEvent> _events = [];
    private PolledSensorSource? _source;

    public void Dispose() => _source?.Dispose();

    /// <summary>The source over the fake lister, discovered and polled once.</summary>
    private PolledSensorSource Open()
    {
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
        _source = VolumeSource.Create(_lister, _time, log: log);
        _source.Open();
        return _source;
    }

    private FakeDevice Add(string id, string name, string? deviceName = null, float? volume = 0.5f, bool? mute = false)
    {
        var device = new FakeDevice(id, name, deviceName, volume, mute);
        _lister.Devices.Add(device);
        return device;
    }

    /// <summary>A device, listed and the default.</summary>
    private FakeDevice AddDefault(string id, string name, string? deviceName = null, float? volume = 0.5f, bool? mute = false) =>
        _lister.DefaultDevice = Add(id, name, deviceName, volume, mute);

    /// <summary>One poll, 250 ms after the last (the source's interval); or <paramref name="after"/>.</summary>
    private void Round(int count = 1, TimeSpan? after = null)
    {
        for (var i = 0; i < count; i++)
        {
            _time.Advance(after ?? VolumeSource.Interval);
            _source!.Poll();
        }
    }

    private static SensorKey Key(string container, string entry) => VolumeSource.KeyOf(container, entry);

    private string[] Listed() => _source!.GetSensors().Select(s => s.Key.ToString()).ToArray();

    private string[] Containers() => _source!.GetSensors().Select(s => s.Key.Id.Split('/')[2]).Distinct().ToArray();

    private double Number(string container, string entry = "volume")
    {
        Assert.True(_source!.TryRead(Key(container, entry), out var reading), $"{container}/{entry}");
        return reading.ValueNow;
    }

    private string Text(string container, string entry)
    {
        var state = _source!.ReadState(Key(container, entry));
        Assert.True(state.HasReading, $"{container}/{entry}");
        Assert.Equal(SensorKind.Text, state.Type);
        return state.Reading.ValueText!;
    }

    private string[] Names(string container) => Ids.Take(3).Select(id => Text(container, id)).ToArray();

    private SensorStatus Status(string container, string entry = "volume") => _source!.GetStatus(Key(container, entry));

    private int Warnings(string contains) => _events.Count(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains(contains, StringComparison.Ordinal));

    // ---- Keys and entries ----

    [Fact]
    public void Keys_TheDefaultFirst_ThenEachDevice_EachWithThe1xPluginsEntries_InItsOrder()
    {
        AddDefault(SpeakersId, "Speakers (Realtek(R) Audio)", "Realtek(R) Audio");
        Add("{0.0.0.00000000}.{ffffffff-0000-4000-8000-0123456789ab}", "Headphones (USB Audio)", "USB Audio");
        var source = Open();

        Assert.Equal(["default", Speakers, "00000000000ffffffff0000400080000123456789ab"], Containers());
        Assert.Equal(Ids.Select(id => $"plugin:/infopanel.volume/default/{id}"), Listed().Take(5));
        Assert.Equal(Ids.Select(id => $"plugin:/infopanel.volume/{Speakers}/{id}"), Listed().Skip(5).Take(5));
        var first = source.GetSensors().Take(5).ToList();
        Assert.Equal(["Device Name", "Name", "Short Name", "Volume", "Mute"], first.Select(s => s.Name));
        Assert.Equal(["", "", "", "%", ""], first.Select(s => s.Unit));
        Assert.Equal([SensorKind.Text, SensorKind.Text, SensorKind.Text, SensorKind.Number, SensorKind.Text], first.Select(s => source.ReadState(s.Key).Type));
        Assert.Equal(["Default", "Speakers (Realtek(R) Audio)", "Headphones (USB Audio)"], source.GetSensors().Select(s => s.Group).Distinct());
        Assert.All(source.GetSensors(), s => Assert.Equal("infopanel.volume", s.SourceId));
        Assert.Equal("infopanel.volume", source.Id);
        Assert.All(source.GetSensors(), s => Assert.Equal(SensorStatus.Live, source.GetStatus(s.Key)));
    }

    [Fact]
    public void TheSourceIsPolledEvery250ms_As1xs()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), VolumeSource.Interval);
        AddDefault("BuiltInSpeakerDevice", "MacBook Pro Speakers", volume: 0.25f);
        Open();
        _lister.DefaultDevice!.Level = 0.75f;
        Round();

        Assert.Equal(75, Number("default"));
    }

    [Theory]
    [InlineData(SpeakersId)]
    [InlineData("BuiltInSpeakerDevice")]
    [InlineData("AppleUSBAudioEngine:Apple Inc.:EarPods:1234:1")]
    [InlineData("BlackHole2ch_UID")]
    public void TheDeviceId_IsThePluginSdksIdOfTheSystemsId(string id)
    {
        Assert.Equal(new PluginContainer(id).Id, VolumeSource.ContainerOf(id));
    }

    [Fact]
    public void TheDefaultsDeviceId_IsThePluginSdksIdOfDefault()
    {
        Assert.Equal("default", new PluginContainer("Default").Id);
        Assert.Equal("default", VolumeSource.DefaultContainer);
    }

    // ---- Values ----

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 100)]
    [InlineData(0.5f, 50)]
    [InlineData(0.004f, 0)]
    [InlineData(0.996f, 100)]
    // Halves round to even, as 1.x's Math.Round: 12.5 → 12, 37.5 → 38.
    [InlineData(0.125f, 12)]
    [InlineData(0.375f, 38)]
    public void TheVolume_IsTheScalarTimes100_RoundedAs1xRoundedIt(float scalar, double percent)
    {
        Assert.Equal(percent, VolumeSource.Percent(scalar));
        AddDefault("BuiltInSpeakerDevice", "MacBook Pro Speakers", volume: scalar);
        Open();
        Assert.Equal(percent, Number("default"));
        Assert.Equal(percent, Number("builtinspeakerdevice"));
    }

    [Fact]
    public void TheMute_ReadsTrueOrFalse_As1xWroteIt_AndFollowsEveryRound()
    {
        var speakers = AddDefault("BuiltInSpeakerDevice", "MacBook Pro Speakers");
        Open();
        Assert.Equal("False", Text("default", "mute"));

        speakers.Muted = true;
        Round();
        Assert.Equal("True", Text("default", "mute"));
        Assert.Equal("True", Text("builtinspeakerdevice", "mute"));
        Assert.Equal(SensorStatus.Live, Status("default", "mute"));
    }

    [Fact]
    public void TheNames_AreTheDevicesNames_TheShortNameWithoutTheDevicesNameInBrackets()
    {
        AddDefault(SpeakersId, "Speakers (Realtek(R) Audio)", "Realtek(R) Audio");
        Open();

        Assert.Equal(["Realtek(R) Audio", "Speakers (Realtek(R) Audio)", "Speakers"], Names("default"));
        Assert.Equal(["Realtek(R) Audio", "Speakers (Realtek(R) Audio)", "Speakers"], Names(Speakers));
    }

    [Fact]
    public void TheShortName_OfADeviceNotTheDefault_LeavesOutItsOwnDevicesName_Trimmed_Not1xsDefaultOne()
    {
        // 1.x made every device's Short Name with the default device's name and didn't trim it, so a USB headset read
        // "Headphones (USB Audio)" in full; each device's own is used here.
        AddDefault(SpeakersId, "Speakers (Realtek(R) Audio)", "Realtek(R) Audio");
        Add("usb", "Headphones (USB Audio)", "USB Audio");
        Open();

        Assert.Equal("Headphones", Text("usb", "short_name"));
        Assert.Equal("Speakers", Text(Speakers, "short_name"));
        Assert.Equal("Speakers", VolumeSource.ShortName("  Speakers (Realtek(R) Audio) ", "Realtek(R) Audio"));
    }

    [Fact]
    public void OnMacOS_EveryNameIsTheDevicesName()
    {
        // Core Audio has one name a device: the lister gives it as both, and the Short Name keeps it whole.
        AddDefault("BuiltInSpeakerDevice", "MacBook Pro Speakers");
        Open();

        Assert.Equal(["MacBook Pro Speakers", "MacBook Pro Speakers", "MacBook Pro Speakers"], Names("default"));
    }

    [Fact]
    public void ADeviceWithoutAVolume_ReadsItsVolumeUnavailable_AndOneWithoutAMute_ItsMute_TheirNamesRead()
    {
        // An HDMI display has no volume macOS can set (its slider greys); some devices have no mute control.
        AddDefault("hdmi", "LG UltraFine", volume: null, mute: null);
        Add("BuiltInSpeakerDevice", "MacBook Pro Speakers", mute: null);
        Open();
        Round();

        Assert.Equal(SensorStatus.Unavailable, Status("default"));
        Assert.Equal(SensorStatus.Unavailable, Status("default", "mute"));
        Assert.Equal(SensorStatus.Unavailable, Status("hdmi"));
        Assert.False(_source!.TryRead(Key("hdmi", "volume"), out _));
        Assert.Equal("LG UltraFine", Text("hdmi", "friendly_name"));
        Assert.Equal(SensorStatus.Live, Status("builtinspeakerdevice"));
        Assert.Equal(SensorStatus.Unavailable, Status("builtinspeakerdevice", "mute"));
        Assert.Contains("plugin:/infopanel.volume/hdmi/volume", Listed());

        // A volume it gains reads at once.
        _lister.Devices[0].Level = 0.3f;
        Round();
        Assert.Equal(30, Number("hdmi"));
        Assert.Equal(SensorStatus.Live, Status("default"));
    }

    // ---- The default device ----

    [Fact]
    public void TheDefault_FollowsAChangedDefaultDevice_ItsNamesAndVolume()
    {
        var speakers = AddDefault("BuiltInSpeakerDevice", "MacBook Pro Speakers", volume: 0.4f);
        var headphones = Add("headphones", "External Headphones", volume: 0.9f, mute: true);
        Open();
        Assert.Equal("MacBook Pro Speakers", Text("default", "friendly_name"));
        Assert.Equal(40, Number("default"));

        // Headphones plugged in: macOS makes them the default.
        _lister.DefaultDevice = headphones;
        Round();
        Assert.Equal(["External Headphones", "External Headphones", "External Headphones"], Names("default"));
        Assert.Equal(90, Number("default"));
        Assert.Equal("True", Text("default", "mute"));
        Assert.Equal("Default", _source!.GetSensors()[0].Group);
        // Each device still reads under its own id.
        Assert.Equal(40, Number("builtinspeakerdevice"));
        Assert.Same(speakers, _lister.Devices[0]);
    }

    [Fact]
    public void NoDefaultDevice_ReadsTheDefaultsReadingsUnavailable_TheDevicesStillRead_AndOneThatComesReadsLive()
    {
        Add("BuiltInSpeakerDevice", "MacBook Pro Speakers");
        Open();

        Assert.Equal(["default", "builtinspeakerdevice"], Containers());
        Assert.All(Ids, id => Assert.Equal(SensorStatus.Unavailable, Status("default", id)));
        Assert.Equal(SensorStatus.Live, Status("builtinspeakerdevice"));

        _lister.DefaultDevice = _lister.Devices[0];
        Round();
        Assert.Equal(SensorStatus.Live, Status("default"));
        Assert.Equal(50, Number("default"));
    }

    [Fact]
    public void ADefaultDeviceWhoseReadThrows_FailsTheDefaultsReadings_TheDevicesRead_AndItIsLoggedOnce()
    {
        AddDefault("BuiltInSpeakerDevice", "MacBook Pro Speakers");
        Open();
        _lister.DefaultThrows = new IOException("OSStatus 560947818");
        Round(3);

        Assert.Equal(SensorStatus.Stale, Status("default"));
        Assert.Equal(50, Number("default"));
        Assert.Equal(SensorStatus.Live, Status("builtinspeakerdevice"));
        Assert.Equal(1, Warnings("reading the default output device"));

        _lister.DefaultThrows = null;
        Round();
        Assert.Equal(SensorStatus.Live, Status("default"));
    }

    // ---- Devices coming and going ----

    [Fact]
    public void ADevice_IsListedAfterTheRoundThatSawIt_LiveAtTheNext_StaleWhenUnplugged_UnlistedAfter30s_AndBackUnderTheSameKeys()
    {
        AddDefault("BuiltInSpeakerDevice", "MacBook Pro Speakers");
        Open();
        Assert.Equal(["default", "builtinspeakerdevice"], Containers());

        var usb = Add("usb-dac", "USB DAC", volume: 0.2f);
        Round();
        Assert.Equal(["default", "builtinspeakerdevice", "usbdac"], Containers());
        Assert.Equal(SensorStatus.Unavailable, Status("usbdac"));
        Round();
        Assert.Equal(SensorStatus.Live, Status("usbdac"));
        Assert.Equal(20, Number("usbdac"));
        Assert.Equal("USB DAC", _source!.GetSensors().Last().Group);

        // Unplugged: its readings fail, the last values kept, then it goes once unavailable.
        _lister.Devices.Remove(usb);
        Round();
        Assert.Equal(SensorStatus.Stale, Status("usbdac"));
        Assert.Equal(20, Number("usbdac"));
        Assert.Equal(SensorStatus.Live, Status("builtinspeakerdevice"));
        Round(after: TimeSpan.FromSeconds(29));
        Assert.Equal(["default", "builtinspeakerdevice", "usbdac"], Containers());
        Round(2, after: TimeSpan.FromSeconds(2));
        Assert.Equal(["default", "builtinspeakerdevice"], Containers());
        Assert.Equal(SensorStatus.Unavailable, Status("usbdac"));

        // Plugged in again: the same keys, live again.
        _lister.Devices.Add(usb);
        Round(2);
        Assert.Equal(["default", "builtinspeakerdevice", "usbdac"], Containers());
        Assert.Equal(SensorStatus.Live, Status("usbdac"));
    }

    [Fact]
    public void ADeviceWhoseReadThrows_FailsAlone_TheOthersRead_AndItIsLoggedOnce()
    {
        var usb = Add("usb-dac", "USB DAC");
        AddDefault("BuiltInSpeakerDevice", "MacBook Pro Speakers");
        Open();
        usb.Throws = new IOException("OSStatus 560947818");
        Round(3);

        Assert.Equal(SensorStatus.Stale, Status("usbdac"));
        Assert.Equal(SensorStatus.Live, Status("builtinspeakerdevice"));
        Assert.Equal(SensorStatus.Live, Status("default"));
        Assert.Equal(1, Warnings("reading the output device"));

        usb.Throws = null;
        Round(2);
        Assert.Equal(SensorStatus.Live, Status("usbdac"));
    }

    [Fact]
    public void AListingThatThrows_FailsTheRound_TheCatalogueStands_AndItIsLoggedOnce()
    {
        AddDefault("BuiltInSpeakerDevice", "MacBook Pro Speakers");
        Open();
        _lister.Throws = new IOException("OSStatus 2003332927");
        Round(2);

        Assert.Equal(["default", "builtinspeakerdevice"], Containers());
        Assert.Equal(SensorStatus.Stale, Status("default"));
        Assert.Equal(SensorStatus.Stale, Status("builtinspeakerdevice"));
        Assert.Equal(1, Warnings("listing the output devices failed"));

        _lister.Throws = null;
        Round();
        Assert.Equal(SensorStatus.Live, Status("default"));
    }

    // ---- Which devices ----

    [Theory]
    [InlineData("Default")]
    [InlineData("DEFAULT")]
    [InlineData("!!!")]
    [InlineData("")]
    public void ADeviceWhoseIdMakesTheDefaultsDeviceIdOrNone_IsLeftOut_AndLoggedOnce(string id)
    {
        AddDefault("BuiltInSpeakerDevice", "MacBook Pro Speakers", volume: 0.1f);
        Add(id, "Odd Device", volume: 0.9f);
        Open();
        Round(2);

        Assert.Equal(["default", "builtinspeakerdevice"], Containers());
        Assert.Equal(10, Number("default"));
        Assert.Equal(1, Warnings("makes no device id of its own"));
    }

    [Fact]
    public void TwoDevicesWithOneDeviceId_TheFirstIsListed_AndTheClashIsLoggedOnce()
    {
        Add("usb-dac", "first");
        Add("usb.dac", "second");
        Open();
        Round(2);

        Assert.Equal(["default", "usbdac"], Containers());
        Assert.Equal("first", Text("usbdac", "friendly_name"));
        Assert.Equal(1, Warnings("device id"));
    }

    [Fact]
    public void AtMost64Devices_TheRestLeftOutUnread_LoggedOnce()
    {
        for (var i = 0; i < 66; i++)
            Add($"device-{i}", $"Device {i}");
        Open();
        Round(2);

        Assert.Equal(65, Containers().Length);
        Assert.Equal(0, _lister.Devices[64].Reads + _lister.Devices[65].Reads);
        Assert.Equal(1, Warnings("at most 64"));
    }

    [Fact]
    public void NamesAreCutAt256_AndADeviceWhoseKeysWouldPass512_IsLeftOut()
    {
        Add("long-name", new string('n', 300), new string('d', 300));
        Add(new string('x', 600), "Long Id");
        Open();

        Assert.Equal(["default", "longname"], Containers());
        Assert.Equal(256, Text("longname", "friendly_name").Length);
        Assert.Equal(256, Text("longname", "device_friendly_name").Length);
        Assert.Equal(1, Warnings("keys longer than"));
    }

    [Fact]
    public void The1xPluginsKeys_AreNeverAnswered()
    {
        AddDefault("BuiltInSpeakerDevice", "MacBook Pro Speakers");
        var source = Open();
        var hub = new SensorHub([source]);

        foreach (var id in Ids)
        {
            var old = SensorKey.Plugin($"/volume-plugin/default/{id}");
            Assert.False(source.Handles(old), id);
            Assert.Equal(SensorStatus.Unavailable, hub.GetStatus(old));
            Assert.False(hub.TryRead(old, out _));
        }

        Assert.True(source.Handles(Key("default", "volume")));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(Key("q", "volume")));
    }

    [Fact]
    public void EntryNames_AreThe1xPluginsNames()
    {
        Assert.Equal(["Device Name", "Name", "Short Name", "Volume", "Mute"], Ids.Select(VolumeSource.EntryName));
        Assert.Null(VolumeSource.EntryName("balance"));
    }

    // ---- The macOS lister's rules ----

    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCharacters(IntPtr alloc, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string chars, nint numChars);

    [Theory]
    // 171 CJK characters: 513 bytes in UTF-8, past a 512-byte buffer, within the 256-character text limit.
    [InlineData(171, "音")]
    [InlineData(300, "音")]
    // Characters outside the BMP (surrogate pairs) and a long ASCII UID, kept whole.
    [InlineData(200, "🎧")]
    [InlineData(600, "x")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void OnMacOS_ADevicesTextOfAnyLength_IsReadWhole(int count, string unit)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Core Foundation is macOS's.");
        var text = string.Concat(Enumerable.Repeat(unit, count));
        var cf = CFStringCreateWithCharacters(IntPtr.Zero, text, text.Length);
        Assert.NotEqual(IntPtr.Zero, cf);
        try
        {
            Assert.Equal(text, MacAudioLister.Text(cf));
        }
        finally
        {
            InfoPanel.Sensors.MacOS.CF.CFRelease(cf);
        }
    }

    [Theory]
    // Channel 1 alone (no stereo pair): its volume.
    [InlineData(new[] { 0.5f }, 0.5f)]
    // Every output channel's readable volume averaged, not only the first pair's.
    [InlineData(new[] { 0.2f, float.NaN, 0.6f, 1f }, 0.6f)]
    [InlineData(new[] { float.NaN, 0.4f }, 0.4f)]
    [InlineData(new[] { float.NaN, float.NaN }, null)]
    [InlineData(new float[0], null)]
    public void MacOS_WithoutAMainVolume_TheVolumeIsTheOutputChannelsAverage(float[] channels, float? volume)
    {
        // NaN stands for a channel without a volume control.
        var read = channels.Select(c => float.IsNaN(c) ? (float?)null : c).ToArray();
        var average = MacAudioRules.ChannelVolume(read);
        if (volume is null)
            Assert.Null(average);
        else
            Assert.Equal(volume.Value, average!.Value, 0.0001f);
    }

    [Fact]
    public void MacOS_WithoutAMainVolume_SixEqualChannelsReadAsTheScalarDoes()
    {
        // A float sum of six 0.505f averages 0.50500005 (51 %); the scalar 0.505f rounds to 50 %.
        var read = Enumerable.Repeat<float?>(0.505f, 6).ToArray();
        var average = MacAudioRules.ChannelVolume(read);
        Assert.Equal(50, VolumeSource.Percent(0.505f));
        Assert.Equal(VolumeSource.Percent(0.505f), VolumeSource.Percent(average!.Value));
    }

    [Theory]
    // True only when every output channel is muted; a channel without a mute control is heard.
    [InlineData("TT", true)]
    [InlineData("TTTT", true)]
    [InlineData("TF", false)]
    [InlineData("FF", false)]
    [InlineData("T-", false)]
    [InlineData("-T", false)]
    [InlineData("--", null)]
    [InlineData("", null)]
    public void MacOS_WithoutAMainMute_TheMuteIsTheOutputChannels(string channels, bool? mute)
    {
        var read = channels.Select(c => c switch { 'T' => true, 'F' => false, _ => (bool?)null }).ToArray();
        Assert.Equal(mute, MacAudioRules.ChannelMute(read));
    }

    // ---- This computer (opt-in) ----

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Real_TheMacsDefaultOutputDevice_IsRead_AndEveryDeviceIsPrinted()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("INFOPANEL_SENSOR_TESTS") == "1", "Set INFOPANEL_SENSOR_TESTS=1 on a Mac to read its output devices.");
        var output = TestContext.Current.TestOutputHelper;
        var lister = new MacAudioLister();

        static string Try(Func<object?> read)
        {
            try
            {
                return Convert.ToString(read(), System.Globalization.CultureInfo.InvariantCulture) ?? "none";
            }
            catch (Exception ex)
            {
                return "!" + ex.GetType().Name + ": " + ex.Message;
            }
        }

        foreach (var d in lister.List())
            output?.WriteLine($"device\tid={Try(() => d.Id)}\tname={Try(() => d.Name)}\tdeviceName={Try(() => d.DeviceName)}\tvolume={Try(() => d.Volume)}\tmute={Try(() => d.Mute)}\tcontainer={Try(() => VolumeSource.ContainerOf(d.Id))}");
        var def = lister.Default();
        Assert.NotNull(def);
        output?.WriteLine($"default\tid={Try(() => def.Id)}\tname={Try(() => def.Name)}\tvolume={Try(() => def.Volume)}\tmute={Try(() => def.Mute)}");
        Assert.False(string.IsNullOrEmpty(def.Name));
        Assert.Contains(lister.List(), d => d.Id == def.Id);

        _source = VolumeSource.CreateSystem();
        _source.Open();
        foreach (var s in _source.GetSensors())
        {
            var state = _source.ReadState(s.Key);
            output?.WriteLine($"listed\t{s.Key}\t{s.Group}\t{_source.GetStatus(s.Key)}\t{(state.HasReading ? state.Reading.ValueText ?? state.Reading.ValueNow.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")}");
        }

        Assert.Equal(def.Name, Text("default", "friendly_name"));
        Assert.Equal(SensorStatus.Live, Status("default", "friendly_name"));
        if (def.Volume is { } volume)
            Assert.Equal(VolumeSource.Percent(volume), Number("default"));
    }
}
