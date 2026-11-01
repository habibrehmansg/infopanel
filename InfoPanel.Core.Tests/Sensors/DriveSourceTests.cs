using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Drives;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// The drives source (<c>infopanel.drives</c>) on a fake lister: the 1.x Drive Info plugin's entries per volume under
/// keys of its own (the drive id the plugin SDK's id of the volume's name), sizes in whole MiB, texts read every round,
/// volumes followed as they come and go, the system's own volumes left out on macOS and Linux, bounds, and failures
/// kept to the volume that failed. The plugin's keys are never answered.
/// </summary>
public sealed class DriveSourceTests : IDisposable
{
    private static readonly string[] Ids = ["name", "type", "volume_label", "format", "total_size", "free_space", "available_space", "used_space"];

    private const long Total = 500_107_862_016;
    private const long Free = 123_456_789_012;
    private const long Available = 100_000_000_000;

    private sealed class FakeVolume(string name, DriveType type = DriveType.Fixed, string label = "", string format = "NTFS") : IVolume
    {
        public string VolumeName = name;
        public DriveType Type = type;
        public bool Ready = true;
        public string Label = label;
        public string Format = format;
        public long Size = Total;
        public long FreeBytes = Free;
        public long AvailableBytes = Available;

        /// <summary>Mounted not to be shown (macOS <c>nobrowse</c>).</summary>
        public bool Hidden;

        /// <summary>When set, reading the sizes throws it (a drive that went while it was read).</summary>
        public Exception? Throws;

        /// <summary>When set, every property throws, and each read is counted in <see cref="Touches"/> (a share that hangs).</summary>
        public bool Untouchable;

        public int Touches;

        private T Get<T>(T value)
        {
            if (!Untouchable)
                return value;
            Touches++;
            throw new InvalidOperationException($"{VolumeName} was read");
        }

        public string Name => Get(VolumeName);
        public DriveType DriveType => Get(Type);
        public bool IsReady => Get(Ready);
        public string VolumeLabel => Get(Label);
        public string DriveFormat => Get(Format);
        public long TotalSize => Throws is { } e ? throw e : Get(Size);
        public long TotalFreeSpace => Throws is { } e ? throw e : Get(FreeBytes);
        public long AvailableFreeSpace => Throws is { } e ? throw e : Get(AvailableBytes);
        public bool IsHidden => Get(Hidden);
    }

    private sealed class FakeLister : IVolumeLister
    {
        public readonly List<FakeVolume> Volumes = [];
        public Exception? Throws;
        public int Calls;

        public IReadOnlyList<IVolume> List()
        {
            Calls++;
            return Throws is { } e ? throw e : Volumes.ToArray();
        }
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

    /// <summary>The source over the fake lister, as on <paramref name="platform"/>, discovered and polled once.</summary>
    private PolledSensorSource Open(VolumePlatform platform = VolumePlatform.Windows)
    {
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
        _source = DriveSource.Create(_lister, platform, _time, log: log);
        _source.Open();
        return _source;
    }

    private FakeVolume Add(string name, DriveType type = DriveType.Fixed, string label = "", string format = "NTFS")
    {
        var volume = new FakeVolume(name, type, label, format);
        _lister.Volumes.Add(volume);
        return volume;
    }

    /// <summary>One poll, 2 s after the last (the source's interval).</summary>
    private void Round(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            _time.Advance(DriveSource.Interval);
            _source!.Poll();
        }
    }

    private static SensorKey Key(string drive, string entry) => DriveSource.KeyOf(drive, entry);

    private string[] Listed() => _source!.GetSensors().Select(s => s.Key.ToString()).ToArray();

    private string[] Drives() => _source!.GetSensors().Select(s => s.Key.Id.Split('/')[2]).Distinct().ToArray();

    private double Number(string drive, string entry)
    {
        Assert.True(_source!.TryRead(Key(drive, entry), out var reading), $"{drive}/{entry}");
        return reading.ValueNow;
    }

    private string Text(string drive, string entry)
    {
        var state = _source!.ReadState(Key(drive, entry));
        Assert.True(state.HasReading, $"{drive}/{entry}");
        Assert.Equal(SensorKind.Text, state.Type);
        return state.Reading.ValueText!;
    }

    private SensorStatus Status(string drive, string entry = "free_space") => _source!.GetStatus(Key(drive, entry));

    private int Warnings(string contains) => _events.Count(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains(contains, StringComparison.Ordinal));

    // ---- Keys and entries ----

    [Fact]
    public void Keys_WindowsLetters_AreTheirSlugs_EachWithThe1xPluginsEntries_InItsOrder()
    {
        Add(@"C:\", label: "Windows");
        Add(@"D:\", DriveType.Removable, format: "exFAT");
        var source = Open();

        Assert.Equal([.. Ids.Select(id => $"plugin:/infopanel.drives/c/{id}"), .. Ids.Select(id => $"plugin:/infopanel.drives/d/{id}")], Listed());
        var c = source.GetSensors().Take(8).ToList();
        Assert.Equal(["Name", "Type", "Volume Label", "Format", "Total Size", "Free Space", "Available Space", "Used Space"], c.Select(s => s.Name));
        Assert.Equal(["", "", "", "", "MB", "MB", "MB", "MB"], c.Select(s => s.Unit));
        Assert.All(source.GetSensors(), s => Assert.Equal("infopanel.drives", s.SourceId));
        Assert.Equal(["C: (Windows)", "D:"], source.GetSensors().Select(s => s.Group).Distinct());
        Assert.Equal("infopanel.drives", source.Id);
        Assert.All(source.GetSensors(), s => Assert.Equal(SensorStatus.Live, source.GetStatus(s.Key)));
    }

    [Fact]
    public void Keys_TheMacRoot_HasAnEmptyDriveId_AndAVolumeUnderVolumes_ItsSlug()
    {
        Add("/", label: "/", format: "apfs");
        Add("/Volumes/My Disk", label: "/Volumes/My Disk", format: "msdos");
        Open(VolumePlatform.MacOS);

        Assert.Equal(["", "volumesmy-disk"], Drives());
        Assert.Contains("plugin:/infopanel.drives//free_space", Listed());
        Assert.Contains("plugin:/infopanel.drives/volumesmy-disk/free_space", Listed());
        // A label that is only the mount point again isn't added to the group.
        Assert.Equal(["/", "/Volumes/My Disk"], _source!.GetSensors().Select(s => s.Group).Distinct());
        Assert.Equal("/Volumes/My Disk", Text("volumesmy-disk", "name"));
    }

    [Fact]
    public void Keys_TheDriveIdIsTheSlugOfTheName_NotOfTheLabel()
    {
        Add(@"E:\", DriveType.Removable, label: "My Stick");
        Open();

        Assert.Equal(["e"], Drives());
        Assert.Equal("E: (My Stick)", _source!.GetSensors()[0].Group);
    }

    [Theory]
    [InlineData("C:")]
    [InlineData("D:")]
    [InlineData("/")]
    [InlineData("/Volumes/My Disk")]
    [InlineData("/Volumes/Macintosh HD - Data")]
    [InlineData("/media/user/USB DRIVE")]
    [InlineData("Ünïcödé Drívé")]
    [InlineData("日本 Disk")]
    [InlineData("  spaced   out  ")]
    [InlineData("tab\tand\u00a0nbsp")]
    [InlineData("a-b_c.d")]
    [InlineData("\u212a Kelvin")]
    [InlineData("İstanbul")]
    [InlineData("")]
    public void TheSlug_IsThePluginSdks(string name)
    {
        Assert.Equal(new PluginContainer(name).Id, DriveSource.Slug(name));
    }

    [Theory]
    [InlineData(@"C:\", "c")]
    [InlineData(@"\\server\share\", "servershare")]
    [InlineData("/", "")]
    public void TheDriveId_IsTheSlugOfTheNameWithBackslashesTrimmed_As1xs(string name, string drive)
    {
        Assert.Equal(drive, DriveSource.ContainerOf(name));
        Assert.Equal(new PluginContainer(name.TrimEnd('\\')).Id, DriveSource.ContainerOf(name));
    }

    // ---- Values ----

    [Fact]
    public void Sizes_AreWholeMiB_AndUsedIsTheTotalLessTheFreeSpace_ThenInMiB()
    {
        Add(@"C:\");
        Open();

        // 500,107,862,016 B is 476,940.02 MiB; used is (total − free) ÷ 1 MiB, rounded down once (not 476,940 − 117,737).
        Assert.Equal(476_940, Number("c", "total_size"));
        Assert.Equal(117_737, Number("c", "free_space"));
        Assert.Equal(95_367, Number("c", "available_space"));
        Assert.Equal(359_202, Number("c", "used_space"));
        Assert.Equal("MB", _source!.ReadState(Key("c", "used_space")).Reading.Unit);
    }

    [Fact]
    public void Texts_AreTheName_Type_LabelAndFormat()
    {
        Add(@"C:\", label: "Windows", format: "NTFS");
        Add(@"Z:\", DriveType.Network, format: "SMB");
        Open();

        Assert.Equal(["C:", "Fixed", "Windows", "NTFS"], Ids.Take(4).Select(id => Text("c", id)));
        Assert.Equal(["Z:", "Network", "", "SMB"], Ids.Take(4).Select(id => Text("z", id)));
    }

    [Fact]
    public void Texts_AreReadEveryRound_ALabelChangeShows_InTheTextAndTheGroup()
    {
        var c = Add(@"C:\", label: "Windows");
        Open();
        c.Label = "System";
        Round();

        Assert.Equal("System", Text("c", "volume_label"));
        Assert.Equal("C: (System)", _source!.GetSensors()[0].Group);
        Assert.Equal(SensorStatus.Live, Status("c", "volume_label"));
    }

    [Fact]
    public void Sizes_FollowEveryRound()
    {
        var c = Add(@"C:\");
        Open();
        c.FreeBytes = 1024L * 1024 * 1000;
        Round();

        Assert.Equal(1000, Number("c", "free_space"));
    }

    // ---- Volumes coming and going ----

    [Fact]
    public void AStick_IsListedAfterTheRoundThatSawIt_LiveAtTheNext_StaleWhenRemoved_UnlistedAfter30s_AndBackUnderTheSameKeys()
    {
        Add(@"C:\");
        Open();
        Assert.Equal(["c"], Drives());

        var stick = Add(@"E:\", DriveType.Removable, label: "STICK", format: "FAT32");
        Round();
        Assert.Equal(["c", "e"], Drives());
        Assert.Equal(SensorStatus.Unavailable, Status("e"));
        Round();
        Assert.Equal(SensorStatus.Live, Status("e"));
        Assert.Equal("STICK", Text("e", "volume_label"));

        // Ejected: its readings fail, the last values kept, then it goes once unavailable.
        _lister.Volumes.Remove(stick);
        Round();
        Assert.Equal(SensorStatus.Stale, Status("e"));
        Assert.Equal(117_737, Number("e", "free_space"));
        Assert.Equal(SensorStatus.Live, Status("c"));
        Round(14);
        Assert.Equal(["c", "e"], Drives());
        Round(2);
        Assert.Equal(["c"], Drives());
        Assert.Equal(SensorStatus.Unavailable, Status("e"));

        // Plugged in again: the same keys, live again.
        _lister.Volumes.Add(stick);
        Round(2);
        Assert.Equal(["c", "e"], Drives());
        Assert.Equal(SensorStatus.Live, Status("e"));
    }

    [Fact]
    public void AStickBackBeforeItWasUnlisted_KeepsItsKeys_AndReadsLiveAgain()
    {
        Add(@"C:\");
        var stick = Add(@"E:\", DriveType.Removable);
        Open();
        _lister.Volumes.Remove(stick);
        Round(3);
        Assert.Equal(SensorStatus.Stale, Status("e"));

        _lister.Volumes.Add(stick);
        Round(2);
        Assert.Equal(["c", "e"], Drives());
        Assert.Equal(SensorStatus.Live, Status("e"));
    }

    [Fact]
    public void AVolumeNotReady_IsLeftOut_AndOneThatStopsBeingReady_Goes()
    {
        Add(@"C:\");
        var cd = Add(@"F:\", DriveType.CDRom);
        cd.Ready = false;
        Open();
        Assert.Equal(["c"], Drives());

        cd.Ready = true;
        Round(2);
        Assert.Equal(SensorStatus.Live, Status("f"));
        cd.Ready = false;
        Round();
        Assert.Equal(SensorStatus.Stale, Status("f"));
        Round(16);
        Assert.Equal(["c"], Drives());
    }

    [Fact]
    public void AVolumeWhoseReadThrows_FailsAlone_TheOthersRead_AndItIsLoggedOnce()
    {
        // The failing volume comes first: the ones after it are still read.
        var d = Add(@"D:\");
        Add(@"C:\");
        Open();
        d.Throws = new IOException("The device is not ready.");
        Round(3);

        Assert.Equal(SensorStatus.Stale, Status("d"));
        Assert.Equal(SensorStatus.Live, Status("c"));
        Assert.Equal(1, Warnings("reading the volume"));

        d.Throws = null;
        Round(2);
        Assert.Equal(SensorStatus.Live, Status("d"));
    }

    [Fact]
    public void AVolumeThatThrowsFromTheStart_IsLeftOut_AndTheOthersAreListed()
    {
        Add(@"D:\").Throws = new UnauthorizedAccessException("denied");
        Add(@"C:\");
        Open();

        Assert.Equal(["c"], Drives());
        Assert.Equal(SensorStatus.Live, Status("c"));
    }

    [Fact]
    public void AListingThatThrows_FailsTheRound_TheCatalogueStands_AndItIsLoggedOnce()
    {
        Add(@"C:\");
        Open();
        _lister.Throws = new IOException("getfsstat failed");
        Round(2);

        Assert.Equal(["c"], Drives());
        Assert.Equal(SensorStatus.Stale, Status("c"));
        Assert.Equal(1, Warnings("listing the volumes failed"));

        _lister.Throws = null;
        Round();
        Assert.Equal(SensorStatus.Live, Status("c"));
    }

    [Fact]
    public void TwoVolumesWithOneDriveId_TheFirstIsListed_AndTheClashIsLoggedOnce()
    {
        Add("/Volumes/AB", label: "first");
        Add("/Volumes/A.B", label: "second");
        Open(VolumePlatform.MacOS);
        Round(2);

        Assert.Equal(["volumesab"], Drives());
        Assert.Equal("first", Text("volumesab", "volume_label"));
        Assert.Equal(1, Warnings("drive id"));
    }

    // ---- Which volumes ----

    [Fact]
    public void OnMacOS_TheRootAndVolumesUnderVolumes_NotTheSystemsOwn()
    {
        foreach (var name in new[] { "/", "/dev", "/System/Volumes/VM", "/System/Volumes/Preboot", "/System/Volumes/Data", "/private/var/vm", "/Volumes/Backup", "/Volumes/.timemachine/abc/2026-11-01", "/Volumes/", "/Volumes/USB" })
            Add(name, name == "/dev" ? DriveType.Ram : DriveType.Fixed);
        Open(VolumePlatform.MacOS);

        Assert.Equal(["/", "/Volumes/Backup", "/Volumes/USB"], _source!.GetSensors().Select(s => s.Group).Distinct());
    }

    [Fact]
    public void OnMacOS_AVolumeMountedNobrowse_AsTheRecoveryVolume_IsLeftOut_TheRootKept()
    {
        // As this Mac mounts them: the Recovery volume nobrowse under /Volumes/, the root never.
        Add("/", format: "apfs");
        Add("/Volumes/Recovery", format: "apfs").Hidden = true;
        Add("/Volumes/USB", DriveType.Removable, format: "msdos");
        Add("/Volumes/Hidden Share", DriveType.Network, format: "smbfs").Hidden = true;
        Open(VolumePlatform.MacOS);
        Round(2);

        Assert.Equal(["/", "/Volumes/USB"], _source!.GetSensors().Select(s => s.Group).Distinct());
    }

    [Fact]
    public void OnMacOS_TheRoot_IsListedEvenWhenMarkedHidden()
    {
        Add("/", format: "apfs").Hidden = true;
        Open(VolumePlatform.MacOS);

        Assert.Equal([""], Drives());
    }

    [Fact]
    public void OnWindowsAndLinux_TheHiddenMark_IsIgnored()
    {
        Add(@"C:\").Hidden = true;
        Open();
        Assert.Equal(["c"], Drives());

        _source!.Dispose();
        _lister.Volumes.Clear();
        Add("/", format: "ext4").Hidden = true;
        Open(VolumePlatform.Linux);
        Assert.Equal([""], Drives());
    }

    [Fact]
    public void OnLinux_RealFileSystems_FixedRemovableNetworkAndOptical_NotProcSysfsOrTmpfs()
    {
        Add("/", DriveType.Fixed, format: "ext4");
        Add("/proc", DriveType.Ram, format: "proc");
        Add("/sys", DriveType.Ram, format: "sysfs");
        Add("/run", DriveType.Ram, format: "tmpfs");
        Add("/dev/pts", DriveType.Unknown, format: "devpts");
        Add("/boot/efi", DriveType.Removable, format: "vfat");
        Add("/media/user/USB", DriveType.Removable, format: "exfat");
        Add("/mnt/nas", DriveType.Network, format: "nfs4");
        Add("/media/cdrom", DriveType.CDRom, format: "iso9660");
        Add("/sys/fs/cgroup", DriveType.NoRootDirectory, format: "cgroup2");
        Open(VolumePlatform.Linux);

        Assert.Equal(["/", "/boot/efi", "/media/user/USB", "/mnt/nas", "/media/cdrom"], _source!.GetSensors().Select(s => s.Group).Distinct());
    }

    [Fact]
    public void OnLinux_PseudoFileSystemsThatDotNetTypesAsFixedOrNetwork_AreLeftOut()
    {
        // .NET 10 types each of these Fixed (autofs Network); none is a disk.
        Add("/", DriveType.Fixed, format: "ext4");
        Add("/sys/firmware/efi/efivars", DriveType.Fixed, format: "efivarfs");
        Add("/sys/kernel/tracing", DriveType.Fixed, format: "tracefs");
        Add("/run/netns/vpn", DriveType.Fixed, format: "nsfs");
        Add("/snap/core22/1380", DriveType.Fixed, format: "squashfs");
        Add("/var/lib/docker/overlay2/abc/merged", DriveType.Fixed, format: "overlay");
        Add("/run/rpc_pipefs", DriveType.Fixed, format: "rpc_pipefs");
        Add("/proc/sys/fs/binfmt_misc", DriveType.Network, format: "autofs");
        Add("/home", DriveType.Fixed, format: "btrfs");
        Open(VolumePlatform.Linux);
        Round(2);

        Assert.Equal(["/", "/home"], _source!.GetSensors().Select(s => s.Group).Distinct());
    }

    [Fact]
    public void OnLinux_RealFileSystemsThatDotNetDoesNotType_AreListed()
    {
        // .NET's table has no ntfs3 or smb3: their type is Unknown.
        Add("/mnt/windows", DriveType.Unknown, format: "ntfs3");
        Add("/mnt/share", DriveType.Unknown, format: "smb3");
        Add("/mnt/stick", DriveType.Fixed, format: "fuseblk");
        Open(VolumePlatform.Linux);

        Assert.Equal(["/mnt/windows", "/mnt/share", "/mnt/stick"], _source!.GetSensors().Select(s => s.Group).Distinct());
    }

    [Theory]
    [InlineData("ceph", DriveType.Network)]
    [InlineData("glusterfs", DriveType.Network)]
    [InlineData("apfs", DriveType.Fixed)]
    [InlineData("ntfs-3g", DriveType.Fixed)]
    [InlineData("fuse.sshfs", DriveType.Fixed)]
    [InlineData("fuse.rclone", DriveType.Fixed)]
    [InlineData("fuse.ntfs-3g", DriveType.Fixed)]
    [InlineData("fuse.s3fs", DriveType.Fixed)]
    [InlineData("fuse.mergerfs", DriveType.Fixed)]
    [InlineData("ceph", DriveType.Unknown)]
    [InlineData("fuse.sshfs", DriveType.Network)]
    public void OnLinux_RealNetworkClusterAndFuseStorage_IsListed(string format, DriveType type)
    {
        Add("/mnt/storage", type, format: format);
        Open(VolumePlatform.Linux);

        Assert.Equal(["/mnt/storage"], _source!.GetSensors().Select(s => s.Group).Distinct());
    }

    [Fact]
    public void OnLinux_9pAndPlainFuse_AreLeftOut()
    {
        // 9p is a virtual machine's channel to its host, "fuse" a FUSE file system that names no storage type.
        Add("/", DriveType.Fixed, format: "ext4");
        Add("/mnt/host", DriveType.Fixed, format: "9p");
        Add("/run/user/1000/doc", DriveType.Fixed, format: "fuse");
        Add("/run/user/1000/gvfs", DriveType.Fixed, format: "fuse.gvfsd-fuse");
        Add("/run/user/1000/doc2", DriveType.Fixed, format: "fuse.portal");
        Open(VolumePlatform.Linux);

        Assert.Equal(["/"], _source!.GetSensors().Select(s => s.Group).Distinct());
    }

    [Fact]
    public void OnWindows_EveryReadyDrive()
    {
        Add(@"C:\");
        Add(@"D:\", DriveType.CDRom);
        Add(@"R:\", DriveType.Ram);
        Add(@"Z:\", DriveType.Network);
        Open();

        Assert.Equal(["c", "d", "r", "z"], Drives());
    }

    // ---- Bounds ----

    [Fact]
    public void AtMost64Volumes_TheRestLeftOut_LoggedOnce()
    {
        for (var i = 0; i < 70; i++)
            Add($"/Volumes/V{i}");
        Open(VolumePlatform.MacOS);
        Round(2);

        Assert.Equal(64, Drives().Length);
        Assert.Equal("volumesv63", Drives()[^1]);
        Assert.Equal(1, Warnings("at most 64 volumes"));
    }

    [Fact]
    public void AtTheCap_TheVolumesAfterIt_AreNeverRead()
    {
        // A 65th volume that would hang (a share that doesn't answer): once 64 are listed, nothing of it is read.
        for (var i = 0; i < 64; i++)
            Add($"/Volumes/V{i}");
        var hung = Add("/Volumes/Hung", DriveType.Network);
        hung.Untouchable = true;
        Open(VolumePlatform.MacOS);
        Round(3);

        Assert.Equal(0, hung.Touches);
        Assert.Equal(64, Drives().Length);
        Assert.All(Drives(), d => Assert.Equal(SensorStatus.Live, Status(d)));
        Assert.Equal(0, Warnings("reading the volume"));
        Assert.Equal(1, Warnings("at most 64 volumes"));
    }

    [Fact]
    public void NamesAndLabels_AreCutAt256_AndAVolumeWhoseKeysWouldPass512_IsLeftOut()
    {
        var name = "/Volumes/" + new string('n', 300);
        Add(name, label: new string('l', 300));
        Add("/Volumes/" + new string('x', 600));
        Add("/");
        Open(VolumePlatform.MacOS);
        Round();

        var drive = "volumes" + new string('n', 300);
        Assert.Equal(["", drive], Drives().Order(StringComparer.Ordinal));
        Assert.Equal(name[..256], Text(drive, "name"));
        Assert.Equal(new string('l', 256), Text(drive, "volume_label"));
        Assert.All(Listed(), k => Assert.True(k.Length <= 512, k));
        Assert.Equal(1, Warnings("longer than 512"));
    }

    // ---- The 1.x plugin's keys ----

    [Fact]
    public void The1xPluginsKeys_AreNeverAnswered()
    {
        Add(@"C:\");
        var source = Open();
        var hub = new SensorHub([source]);

        foreach (var id in Ids)
        {
            var old = SensorKey.Plugin($"/drive-info-plugin/c/{id}");
            Assert.False(source.Handles(old), id);
            Assert.Equal(SensorStatus.Unavailable, hub.GetStatus(old));
            Assert.False(hub.TryRead(old, out _));
        }

        Assert.True(source.Handles(Key("c", "free_space")));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(Key("q", "free_space")));
    }

    // ---- This computer ----

    [Fact]
    public void OnMacOS_TheSystemLister_MarksTheNobrowseMountsHidden_NotTheRoot()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only: its mount flags.");
        var volumes = new SystemVolumeLister().List().ToDictionary(v => v.Name);

        // Every Mac mounts the root to be shown and /dev (devfs) nobrowse.
        Assert.False(volumes["/"].IsHidden);
        Assert.True(volumes["/dev"].IsHidden);
        Assert.All(volumes.Values.Where(v => v.Name.StartsWith("/System/Volumes/", StringComparison.Ordinal)), v => Assert.True(v.IsHidden, v.Name));
    }

    // ---- This computer (opt-in) ----

    [Fact]
    public void Real_TheBootVolumeIsListed_WithTotalAtLeastFreeAboveZero_AndEveryMountIsPrinted()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("INFOPANEL_SENSOR_TESTS") == "1", "Set INFOPANEL_SENSOR_TESTS=1 to read this computer's volumes.");
        var output = TestContext.Current.TestOutputHelper;
        foreach (var v in new SystemVolumeLister().List())
        {
            string Try(Func<object> read)
            {
                try
                {
                    return Convert.ToString(read(), System.Globalization.CultureInfo.InvariantCulture) ?? "null";
                }
                catch (Exception ex)
                {
                    return "!" + ex.GetType().Name;
                }
            }

            output?.WriteLine($"{v.Name}\ttype={Try(() => v.DriveType)}\tready={Try(() => v.IsReady)}\tlabel={Try(() => v.VolumeLabel)}\tformat={Try(() => v.DriveFormat)}\ttotal={Try(() => v.TotalSize)}\tfree={Try(() => v.TotalFreeSpace)}\tavailable={Try(() => v.AvailableFreeSpace)}\thidden={Try(() => v.IsHidden)}\tlisted={Try(() => DriveSource.Includes(DriveSource.CurrentPlatform, v.Name, v))}");
        }

        _source = DriveSource.CreateSystem();
        _source.Open();
        var boot = OperatingSystem.IsWindows() ? DriveSource.ContainerOf(Path.GetPathRoot(Environment.SystemDirectory)!) : "";
        foreach (var group in _source.GetSensors().Select(s => s.Group).Distinct())
            output?.WriteLine($"listed: {group}");
        Assert.True(_source.TryRead(Key(boot, "total_size"), out var total), $"the boot volume '{boot}' isn't listed");
        Assert.True(_source.TryRead(Key(boot, "free_space"), out var free));
        Assert.True(total.ValueNow >= free.ValueNow && free.ValueNow > 0, $"total {total.ValueNow} MB, free {free.ValueNow} MB");
    }
}
