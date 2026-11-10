using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Linux;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Linux;

/// <summary>
/// The disk reader (<see cref="Sysfs.Disk"/>, what the engine reads <c>/sys</c> or <c>INFOPANEL_LINUX_SYSFS</c> with) on
/// a tree <see cref="SysfsKit"/> writes with real links: it lists the same sensors as the tree in memory, reads them,
/// keeps to its root, and tells a missing file from one it may not read. Unix only (links and file modes).
/// </summary>
public sealed class DiskSysfsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _temp.Dispose();

    /// <summary>The source over <paramref name="sysfs"/>, discovered, every sensor it lists put in use, and polled again so each is read.</summary>
    private LinuxSensorSource Opened(Sysfs sysfs)
    {
        var source = LinuxSensorSource.Create(sysfs, _time);
        source.Open();
        foreach (var sensor in source.GetSensors())
            source.NoteUse(sensor.Key);
        source.Poll();
        return source;
    }

    private static SensorKey Key(string id) => LinuxSensorSource.KeyOf(id);

    [Fact]
    public void A_link_out_of_the_root_is_neither_read_nor_listed_a_files_or_a_folders()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix: the tree's links.");
        var outside = _temp.Combine("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret"), "4242\n");
        var root = _temp.Combine("sys");
        Directory.CreateDirectory(root);
        var kit = new SysfsKit();
        var dir = kit.AddIt8696(0);
        // A label linked to a file outside, by a full path and by a relative one; a channel file linked out; a folder linked out.
        kit.RawLink(dir + "/fan1_label", Path.Combine(outside, "secret"));
        kit.RawLink(dir + "/fan2_label", "../../../../../../outside/secret");
        kit.RawLink(dir + "/fan3_input", Path.Combine(outside, "secret"));
        kit.RawLink("devices/platform/escape", outside);
        kit.RawLink("devices/platform/escape-up", "../../../outside");
        kit.WriteTo(root);
        Assert.Equal("4242\n", File.ReadAllText(Path.Combine(root, dir, "fan2_label")));
        var sysfs = Sysfs.Disk(root);

        foreach (var path in new[] { dir + "/fan1_label", dir + "/fan2_label", dir + "/fan3_input", "devices/platform/escape/secret", "devices/platform/escape-up/secret" })
        {
            Assert.Equal(SysfsRead.Missing, sysfs.Read(path, out var text));
            Assert.Equal("", text);
        }

        Assert.Empty(sysfs.List("devices/platform/escape"));
        Assert.Empty(sysfs.List("devices/platform/escape-up"));
        Assert.Contains("fan1_input", sysfs.List(dir));

        using var source = Opened(sysfs);
        var ids = source.GetSensors().Select(s => s.Key.Id[LinuxSensorSource.KeyPrefix.Length..]).ToArray();
        Assert.DoesNotContain("hwmon/v1/it8696/platform+it87.2624/fan3", ids);
        var names = source.GetSensors().Select(s => s.Name).ToArray();
        Assert.DoesNotContain(names, name => name.Contains("4242", StringComparison.Ordinal));
        Assert.Equal("fan1", Assert.Single(source.GetSensors(), s => s.Key == Key("hwmon/v1/it8696/platform+it87.2624/fan1")).Name);
        Assert.Equal("fan2", Assert.Single(source.GetSensors(), s => s.Key == Key("hwmon/v1/it8696/platform+it87.2624/fan2")).Name);
        Assert.True(source.TryRead(Key("hwmon/v1/it8696/platform+it87.2624/fan1"), out var fan));
        Assert.Equal(1250, fan.ValueNow);
    }

    [Fact]
    public void A_FIFO_in_place_of_a_channel_or_its_label_is_never_waited_on()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix: FIFOs.");
        var root = _temp.Combine("sys");
        Directory.CreateDirectory(root);
        var kit = new SysfsKit();
        var dir = kit.AddIt8696(0);
        kit.WriteTo(root);
        var fifos = new[] { Path.Combine(root, dir, "fan2_input"), Path.Combine(root, dir, "fan1_label") };
        File.Delete(fifos[0]);
        foreach (var fifo in fifos)
            Assert.Equal(0, Fifo.Make(fifo));
        var sysfs = Sysfs.Disk(root);

        // Discovery reads the label, the poll the channel: neither may wait for a writer that never comes.
        var opened = Task.Run(() => Opened(sysfs));
        try
        {
            Assert.True(opened.Wait(TimeSpan.FromSeconds(20)), "the source waited on a FIFO");
        }
        finally
        {
            // Without the fix the open waits on a FIFO: a writer coming and going lets it go, so nothing is left waiting.
            for (var i = 0; i < 20 && !opened.IsCompleted; i++)
            {
                foreach (var fifo in fifos)
                    Fifo.Release(fifo);
                opened.Wait(TimeSpan.FromMilliseconds(250));
            }
        }

        using var source = opened.Result;
        Assert.Equal(SysfsRead.Failed, sysfs.Read(dir + "/fan2_input", out _));
        Assert.Equal(SysfsRead.Failed, sysfs.Read(dir + "/fan1_label", out _));
        Assert.Equal("fan1", Assert.Single(source.GetSensors(), s => s.Key == Key("hwmon/v1/it8696/platform+it87.2624/fan1")).Name);
        Assert.True(source.TryRead(Key("hwmon/v1/it8696/platform+it87.2624/fan1"), out var fan));
        Assert.Equal(1250, fan.ValueNow);
        Assert.Contains(source.GetStatus(Key("hwmon/v1/it8696/platform+it87.2624/fan2")), new[] { SensorStatus.Unavailable, SensorStatus.Stale });
    }

    /// <summary>Makes and releases FIFOs (Unix).</summary>
    private static class Fifo
    {
        public static int Make(string path) => mkfifo(path, 0b110_100_100);

        /// <summary>Opens the FIFO to write without waiting and closes it: a reader waiting in its open goes on, reading nothing.</summary>
        public static void Release(string path)
        {
            var fd = open(path, 1 | (OperatingSystem.IsMacOS() ? 0x0004 : 0x0800)); // O_WRONLY | O_NONBLOCK
            if (fd >= 0)
                close(fd);
        }

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int mkfifo([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, uint mode);

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int open([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags);

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);
    }

    [Fact]
    public void A_tree_on_disk_reads_as_the_same_tree_in_memory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix: the tree's links and file modes.");
        var kit = SysfsKit.VfdMachine();
        kit.File("devices/platform/it87.2624/hwmon/hwmon0/fan2_input", "980\n", denied: true);
        kit.WriteTo(_temp.Path);

        using var memory = Opened(kit);
        using var disk = Opened(Sysfs.Disk(_temp.Path));

        Assert.Equal(memory.GetSensors(), disk.GetSensors());
        var fan1 = LinuxSensorSource.KeyOf("hwmon/v1/it8696/platform+it87.2624/fan1");
        var curr1 = LinuxSensorSource.KeyOf("hwmon/v1/wireview/platform+wireview_hwmon/curr1");
        Assert.Equal(SensorStatus.Live, disk.GetStatus(fan1));
        Assert.True(disk.TryRead(fan1, out var fan));
        Assert.Equal(1250, fan.ValueNow);
        Assert.True(disk.TryRead(curr1, out var current));
        Assert.Equal(8.25, current.ValueNow);
        // Root reads every file, whatever its mode.
        if (!Environment.IsPrivilegedProcess)
            Assert.Equal(SensorStatus.PermissionDenied, disk.GetStatus(LinuxSensorSource.KeyOf("hwmon/v1/it8696/platform+it87.2624/fan2")));
    }

    [Fact]
    public void The_disk_reader_keeps_to_its_root_and_tells_missing_from_denied()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix: the tree's links and file modes.");
        var kit = new SysfsKit();
        kit.AddIt8696(0);
        kit.File("devices/platform/it87.2624/secret", "1\n", denied: true);
        kit.RawLink("class/hwmon/hwmon9", "../../../outside");
        kit.RawLink("class/hwmon/hwmon10", "/etc");
        var root = _temp.Combine("sys");
        Directory.CreateDirectory(root);
        File.WriteAllText(_temp.Combine("outside"), "not sysfs");
        kit.WriteTo(root);
        var sysfs = Sysfs.Disk(root);

        Assert.Equal("devices/platform/it87.2624/hwmon/hwmon0", sysfs.RealPath("class/hwmon/hwmon0"));
        Assert.Equal("devices/platform/it87.2624", sysfs.RealPath("class/hwmon/hwmon0/device"));
        Assert.Null(sysfs.RealPath("class/hwmon/hwmon9"));
        Assert.Null(sysfs.RealPath("class/hwmon/hwmon10"));
        Assert.Contains("hwmon0", sysfs.List("class/hwmon"));
        Assert.Equal("it8696", sysfs.ReadAttribute("class/hwmon/hwmon0/name"));
        Assert.Equal(SysfsRead.Ok, sysfs.Read("devices/platform/it87.2624/hwmon/hwmon0/fan1_input", out var text));
        Assert.Equal("1250\n", text);
        Assert.Equal(SysfsRead.Missing, sysfs.Read("devices/platform/it87.2624/hwmon/hwmon0/fan9_input", out _));
        Assert.Equal(SysfsRead.Missing, sysfs.Read("devices/platform/none/name", out _));
        if (!Environment.IsPrivilegedProcess)
            Assert.Equal(SysfsRead.Denied, sysfs.Read("devices/platform/it87.2624/secret", out _));
        Assert.Empty(sysfs.List("devices/none"));
    }
}

/// <summary>
/// Writes the VFD machine's sysfs tree (<see cref="SysfsKit.VfdMachine"/>) to the folder <see cref="Variable"/> names,
/// for the Docker live check: the engine reads it with <c>INFOPANEL_LINUX_SYSFS</c> pointing at it. Opt-in; skipped otherwise.
/// </summary>
public sealed class LinuxFixtureWriter
{
    public const string Variable = "INFOPANEL_LINUX_FIXTURE_OUT";

    [Fact]
    public void Write()
    {
        var folder = Environment.GetEnvironmentVariable(Variable);
        Assert.SkipWhen(string.IsNullOrEmpty(folder), $"Set {Variable} to a new folder's full path to write the VFD machine's sysfs tree there.");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix: the tree's links.");
        Assert.True(Path.IsPathFullyQualified(folder), $"{Variable} is a full path");
        Assert.False(Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any(), $"{folder} is a new or empty folder");

        Directory.CreateDirectory(folder);
        SysfsKit.VfdMachine().WriteTo(folder);
    }
}
