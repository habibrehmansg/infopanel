using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Linux;
using Serilog.Events;
using System.Text.RegularExpressions;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Linux;

/// <summary>
/// The Linux source's system readers on trees made by <see cref="SysfsKit"/> (a sysfs and a <c>/proc</c>) and a fake
/// NVML: the CPUs' load from <c>/proc/stat</c> deltas and their clocks from cpufreq, memory from <c>/proc/meminfo</c>, the
/// disks' rates from <c>/proc/diskstats</c>, an amdgpu card from sysfs and NVIDIA GPUs through NVML, under the Linux
/// fork's <c>system/…</c> ids; each reader read only while one of its sensors is in use.
/// </summary>
public sealed class LinuxSystemReadersTests : IDisposable
{
    // A made-up GPU UUID in NVML's form; the VFD templates' grammar, never their values.
    private const string Uuid = "GPU-00000000-1111-2222-3333-444444444444";
    private const string Gpu = "system/gpu/gpu-uuid+" + Uuid + "~pci+0000-01-00.0/";

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly List<LogEvent> _events = [];
    private readonly TempDirectory _temp = new();
    private LinuxSensorSource? _source;

    public void Dispose()
    {
        _source?.Dispose();
        _temp.Dispose();
    }

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    private Serilog.ILogger Log() =>
        new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();

    /// <summary>The source, discovered and polled once with nothing in use.</summary>
    private LinuxSensorSource OpenIdle(Sysfs? sysfs = null, Sysfs? proc = null, Func<INvml?>? nvml = null)
    {
        _source = LinuxSensorSource.Create(sysfs ?? new SysfsKit(), proc, nvml, _time, log: Log());
        _source.Open();
        return _source;
    }

    /// <summary>The source with every sensor it lists put in use and polled again at the same moment, so each is read.</summary>
    private LinuxSensorSource Open(Sysfs? sysfs = null, Sysfs? proc = null, Func<INvml?>? nvml = null)
    {
        OpenIdle(sysfs, proc, nvml);
        foreach (var sensor in _source!.GetSensors())
            _source.NoteUse(sensor.Key);
        _source.Poll();
        return _source;
    }

    private void Use(string id) => _source!.NoteUse(LinuxSensorSource.KeyOf(id));

    private void Round(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _source!.Poll();
        }
    }

    private void Rescan() => Round((int)LinuxSensorSampler.RescanInterval.TotalSeconds);

    private string[] Ids(string prefix) =>
        _source!.GetSensors().Select(s => s.Key.Id[LinuxSensorSource.KeyPrefix.Length..]).Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    private SensorInfo Info(string id) => Assert.Single(_source!.GetSensors(), s => s.Key == LinuxSensorSource.KeyOf(id));

    private double Value(string id)
    {
        Assert.Equal(SensorStatus.Live, _source!.GetStatus(LinuxSensorSource.KeyOf(id)));
        Assert.True(_source.TryRead(LinuxSensorSource.KeyOf(id), out var reading));
        return reading.ValueNow;
    }

    private bool HasReading(string id) => _source!.TryRead(LinuxSensorSource.KeyOf(id), out _);

    // ---- /proc/stat ----

    /// <summary>A <c>cpu</c> line: user nice system idle iowait irq softirq steal (guest and guest_nice 0).</summary>
    private static string Cpu(string name, long user, long system, long idle, long iowait, long irq = 0, long nice = 0, long softirq = 0) =>
        $"{name} {user} {nice} {system} {idle} {iowait} {irq} {softirq} 0 0 0\n";

    private static string Stat(params string[] lines) => string.Concat(lines) + "intr 123456 0 0 0\nctxt 999\nbtime 1762750000\n";

    [Fact]
    public void The_CPUs_total_and_each_cores_load_are_read_from_proc_stat_deltas_the_first_sample_skipped()
    {
        var proc = new SysfsKit().File("stat", Stat(
            Cpu("cpu ", 100, 100, 700, 100), Cpu("cpu0", 50, 50, 350, 50), Cpu("cpu1", 50, 50, 350, 50)));
        Open(proc: proc);

        Assert.Equal(["system/cpu/total", "system/cpu/core0", "system/cpu/core1"], Ids("system/cpu/"));
        Assert.Equal("%", Info("system/cpu/total").Unit);
        // The first read has no interval: no reading yet, never 0.
        Assert.False(HasReading("system/cpu/total"));
        Assert.False(HasReading("system/cpu/core0"));

        // busy is user+nice+system+irq+softirq+steal; idle is idle+iowait.
        proc.File("stat", Stat(
            Cpu("cpu ", 160, 140, 750, 150), Cpu("cpu0", 75, 75, 375, 75), Cpu("cpu1", 110, 70, 360, 60)));
        Round();

        Assert.Equal(50, Value("system/cpu/total"), 6);
        Assert.Equal(50, Value("system/cpu/core0"), 6);
        Assert.Equal(80, Value("system/cpu/core1"), 6);
    }

    [Fact]
    public void A_cpu_counter_that_went_back_gives_no_reading_that_round_and_the_next_reads_from_it()
    {
        var proc = new SysfsKit().File("stat", Stat(Cpu("cpu ", 1000, 1000, 7000, 1000)));
        Open(proc: proc);
        proc.File("stat", Stat(Cpu("cpu ", 1100, 1100, 7700, 1100)));
        Round();
        Assert.Equal(20, Value("system/cpu/total"), 6);

        // A wrap (or a counter reset): every counter is lower. No reading from it; the last one stands, never a bogus jump.
        proc.File("stat", Stat(Cpu("cpu ", 10, 10, 70, 10)));
        Round();
        Assert.Equal(20, Value("system/cpu/total"), 6);

        // The next round reads from the counters after the wrap.
        proc.File("stat", Stat(Cpu("cpu ", 40, 40, 100, 20)));
        Round();
        Assert.Equal(60, Value("system/cpu/total"), 6);
    }

    [Fact]
    public void Iowait_going_back_gives_no_reading_that_round()
    {
        var proc = new SysfsKit().File("stat", Stat(Cpu("cpu ", 1000, 1000, 7000, 1000), Cpu("cpu0", 500, 500, 3500, 500)));
        Open(proc: proc);
        proc.File("stat", Stat(Cpu("cpu ", 1100, 1100, 7700, 1100), Cpu("cpu0", 550, 550, 3850, 550)));
        Round();
        Assert.Equal(20, Value("system/cpu/total"), 6);
        Assert.Equal(20, Value("system/cpu/core0"), 6);

        // iowait falls (the kernel's docs say it can) while busy and the total still rise: no reading from that round.
        proc.File("stat", Stat(Cpu("cpu ", 1400, 1400, 8500, 900), Cpu("cpu0", 600, 600, 4000, 400)));
        Round();
        Assert.Equal(20, Value("system/cpu/total"), 6);
        Assert.Equal(20, Value("system/cpu/core0"), 6);

        proc.File("stat", Stat(Cpu("cpu ", 1450, 1450, 8800, 1000), Cpu("cpu0", 625, 625, 4100, 450)));
        Round();
        Assert.Equal(100 / 500.0 * 100, Value("system/cpu/total"), 6);
        Assert.Equal(50 / 200.0 * 100, Value("system/cpu/core0"), 6);
    }

    [Fact]
    public void One_counter_going_back_gives_no_reading_though_busy_and_the_total_still_rise()
    {
        var proc = new SysfsKit().File("stat", Stat(Cpu("cpu ", 100, 0, 1000, 0)));
        Open(proc: proc);

        // Idle falls (1000 -> 990) while user rises: busy and the total both go up, yet it is no valid interval (it was 111 %).
        proc.File("stat", Stat(Cpu("cpu ", 200, 0, 990, 0)));
        Round();
        Assert.False(HasReading("system/cpu/total"));

        // The next round reads from the counters it left: 100 busy of 200.
        proc.File("stat", Stat(Cpu("cpu ", 300, 0, 1090, 0)));
        Round();
        Assert.Equal(50, Value("system/cpu/total"), 6);

        // Any other counter too: steal (the eighth field) falls while the rest rise.
        proc.File("stat", Stat("cpu  400 0 0 1190 0 0 0 50 0 0\n"));
        Round();
        Assert.Equal(60, Value("system/cpu/total"), 6);
        proc.File("stat", Stat("cpu  500 0 0 1290 0 0 0 40 0 0\n"));
        Round();
        Assert.Equal(60, Value("system/cpu/total"), 6);
    }

    [Fact]
    public void Each_cores_clock_is_read_from_cpufreq_in_mhz()
    {
        var sysfs = new SysfsKit()
            .File("devices/system/cpu/cpu0/cpufreq/scaling_cur_freq", "3600000\n")
            .File("devices/system/cpu/cpu1/cpufreq/scaling_cur_freq", "4250000\n")
            .Dir("devices/system/cpu/cpu2")
            .Dir("devices/system/cpu/cpufreq");
        Open(sysfs);

        Assert.Equal(["system/cpufreq/core0", "system/cpufreq/core1"], Ids("system/cpufreq/"));
        Assert.Equal("MHz", Info("system/cpufreq/core0").Unit);
        Assert.Equal(3600, Value("system/cpufreq/core0"), 6);
        Assert.Equal(4250, Value("system/cpufreq/core1"), 6);
    }

    // ---- /proc/meminfo ----

    private static string MemInfo(long total, long available, long swapTotal, long swapFree) =>
        $"MemTotal:       {total} kB\nMemFree:         1000000 kB\nMemAvailable:    {available} kB\nBuffers:          100000 kB\n" +
        $"Cached:          2000000 kB\nSwapCached:            0 kB\nSwapTotal:       {swapTotal} kB\nSwapFree:        {swapFree} kB\n";

    [Fact]
    public void Memory_is_read_from_proc_meminfo_in_gb_and_percent_swap_too()
    {
        var proc = new SysfsKit().File("meminfo", MemInfo(16_384_000, 4_096_000, 2_097_152, 1_048_576));
        Open(proc: proc);

        Assert.Equal(
            ["system/memory/total", "system/memory/used", "system/memory/available", "system/memory/used_percent",
             "system/memory/swap_used", "system/memory/swap_percent"],
            Ids("system/memory/"));
        Assert.Equal("GB", Info("system/memory/used").Unit);
        Assert.Equal(15.625, Value("system/memory/total"), 6);
        Assert.Equal(11.71875, Value("system/memory/used"), 6);
        Assert.Equal(3.90625, Value("system/memory/available"), 6);
        Assert.Equal(75, Value("system/memory/used_percent"), 6);
        Assert.Equal(1, Value("system/memory/swap_used"), 6);
        Assert.Equal(50, Value("system/memory/swap_percent"), 6);

        proc.File("meminfo", MemInfo(16_384_000, 8_192_000, 2_097_152, 2_097_152));
        Round();
        Assert.Equal(50, Value("system/memory/used_percent"), 6);
        Assert.Equal(0, Value("system/memory/swap_used"), 6);
    }

    [Fact]
    public void Without_swap_memory_lists_no_swap()
    {
        Open(proc: new SysfsKit().File("meminfo", MemInfo(8_192_000, 4_096_000, 0, 0)));
        Assert.Equal(["system/memory/total", "system/memory/used", "system/memory/available", "system/memory/used_percent"], Ids("system/memory/"));
    }

    // ---- /proc/diskstats ----

    /// <summary>A <c>/proc/diskstats</c> line with its sectors read and written (each 512 bytes).</summary>
    private static string Disk(int major, int minor, string name, long sectorsRead, long sectorsWritten) =>
        $"{major,4} {minor,7} {name} 100 0 {sectorsRead} 50 200 0 {sectorsWritten} 80 0 120 130 0 0 0 0 0 0\n";

    /// <summary>
    /// An NVMe drive (its controller's serial; a partition), a SATA disk (its WWID; a partition) and a loop device in
    /// sysfs; <c>/proc/diskstats</c> also has a <c>vda</c> sysfs doesn't know (a container's), with its partition.
    /// </summary>
    private static SysfsKit Disks()
    {
        var kit = new SysfsKit();
        var function = kit.BusDevice("devices/pci0000:00/0000:00:01.1/0000:01:00.0", "pci");
        var nvme = kit.ClassDevice($"{function}/nvme/nvme0", "nvme", ("serial", "FIXTURE-NVME-0001      "));
        kit.Link("class/block/nvme0n1", kit.ClassDevice($"{nvme}/nvme0n1", "block", ("wwid", "eui.0000000000000001")));
        kit.Link("class/block/nvme0n1p1", kit.ClassDevice($"{nvme}/nvme0n1/nvme0n1p1", "block", ("partition", "1")));
        var scsi = kit.BusDevice("devices/pci0000:00/0000:00:17.0/ata1/host0/target0:0:0/0:0:0:0", "scsi", ("wwid", "naa.5000c500a1b2c3d4"));
        kit.Link("class/block/sda", kit.ClassDevice($"{scsi}/block/sda", "block"));
        kit.Link("class/block/sda1", kit.ClassDevice($"{scsi}/block/sda/sda1", "block", ("partition", "1")));
        kit.Link("class/block/loop0", kit.ClassDevice("devices/virtual/block/loop0", "block"));
        return kit;
    }

    private static string DiskStats(long nvmeRead, long nvmeWritten, long sdaRead = 0, long vdaRead = 0, bool sdb = false) =>
        Disk(7, 0, "loop0", 10, 0) + Disk(259, 0, "nvme0n1", nvmeRead, nvmeWritten) + Disk(259, 1, "nvme0n1p1", nvmeRead, nvmeWritten)
        + Disk(8, 0, "sda", sdaRead, 0) + Disk(8, 1, "sda1", sdaRead, 0) + Disk(253, 0, "vda", vdaRead, 0) + Disk(253, 1, "vda1", vdaRead, 0)
        + (sdb ? Disk(8, 16, "sdb", 0, 0) : "");

    [Fact]
    public void Each_disks_read_and_write_rates_are_read_from_proc_diskstats_deltas_keyed_by_what_identifies_it()
    {
        var proc = new SysfsKit().File("diskstats", DiskStats(10_000, 20_000));
        Open(Disks(), proc);

        // Whole disks only, no partition or loop device: the NVMe controller's serial, the SATA disk's WWID, else the name.
        Assert.Equal(
            ["system/disk/block-wwid+naa.5000c500a1b2c3d4/read_speed", "system/disk/block-wwid+naa.5000c500a1b2c3d4/write_speed",
             "system/disk/name+vda/read_speed", "system/disk/name+vda/write_speed",
             "system/disk/nvme-serial+FIXTURE-NVME-0001/read_speed", "system/disk/nvme-serial+FIXTURE-NVME-0001/write_speed"],
            Ids("system/disk/"));
        Assert.Equal("MB/s", Info("system/disk/name+vda/read_speed").Unit);
        Assert.False(HasReading("system/disk/nvme-serial+FIXTURE-NVME-0001/read_speed"));

        // 2048 sectors of 512 bytes a second is 1 MB/s.
        proc.File("diskstats", DiskStats(10_000 + 2048, 20_000 + 4096, sdaRead: 1024, vdaRead: 4096));
        Round();
        Assert.Equal(1, Value("system/disk/nvme-serial+FIXTURE-NVME-0001/read_speed"), 6);
        Assert.Equal(2, Value("system/disk/nvme-serial+FIXTURE-NVME-0001/write_speed"), 6);
        Assert.Equal(0.5, Value("system/disk/block-wwid+naa.5000c500a1b2c3d4/read_speed"), 6);
        Assert.Equal(0, Value("system/disk/block-wwid+naa.5000c500a1b2c3d4/write_speed"), 6);
        Assert.Equal(2, Value("system/disk/name+vda/read_speed"), 6);

        // A counter that went back gives no reading that round.
        proc.File("diskstats", DiskStats(5, 5, sdaRead: 1024, vdaRead: 4096));
        Round();
        Assert.Equal(1, Value("system/disk/nvme-serial+FIXTURE-NVME-0001/read_speed"), 6);
    }

    [Fact]
    public void A_disk_added_or_removed_between_scans_changes_the_catalogue()
    {
        var proc = new SysfsKit().File("diskstats", DiskStats(0, 0));
        Open(Disks(), proc);
        var version = Ids("system/disk/").Length;
        Assert.Equal(6, version);

        proc.File("diskstats", DiskStats(0, 0, sdb: true));
        Rescan();
        Assert.Contains("system/disk/name+sdb/read_speed", Ids("system/disk/"));

        // Gone: unavailable at once, unlisted at the next rescan.
        Use("system/disk/name+vda/read_speed");
        proc.File("diskstats", Disk(259, 0, "nvme0n1", 0, 0) + Disk(8, 0, "sda", 0, 0) + Disk(8, 16, "sdb", 0, 0));
        Round();
        Assert.Equal(SensorStatus.Unavailable, _source!.GetStatus(LinuxSensorSource.KeyOf("system/disk/name+vda/read_speed")));
        Rescan();
        Assert.DoesNotContain("system/disk/name+vda/read_speed", Ids("system/disk/"));
    }

    /// <summary>A SCSI disk (its WWID) with its block device under <paramref name="name"/>'s link; returns the SCSI device's path.</summary>
    private static string ScsiDisk(SysfsKit kit, string name, int target, string wwid)
    {
        var scsi = kit.BusDevice($"devices/pci0000:00/0000:00:17.0/ata{target}/host{target}/target{target}:0:0/{target}:0:0:0", "scsi", ("wwid", wwid));
        kit.Link("class/block/" + name, kit.ClassDevice($"{scsi}/block/{name}", "block"));
        return scsi;
    }

    [Fact]
    public void A_disk_replaced_under_the_same_name_gives_no_rate_from_the_old_disks_counters()
    {
        var kit = new SysfsKit();
        var scsi = ScsiDisk(kit, "sda", 1, "naa.5000c500000000a1");
        var proc = new SysfsKit().File("diskstats", Disk(8, 0, "sda", 2048, 0));
        Open(kit, proc);
        const string Old = "system/disk/block-wwid+naa.5000c500000000a1/read_speed";
        const string New = "system/disk/block-wwid+naa.5000c500000000b2/read_speed";
        Assert.Contains(Old, Ids("system/disk/"));

        // The disk is swapped: another WWID under the name, and its counters (not the old disk's continued).
        kit.File(scsi + "/wwid", "naa.5000c500000000b2\n");
        proc.File("diskstats", Disk(8, 0, "sda", 2_099_200, 0));
        Round();
        Assert.False(HasReading(Old));

        // Found again at the rescan: the new disk is listed under its own id and reads from its own counters.
        Rescan();
        Assert.DoesNotContain(Old, Ids("system/disk/"));
        Use(New);
        Round();
        Assert.False(HasReading(New));
        proc.File("diskstats", Disk(8, 0, "sda", 2_099_200 + 2048, 0));
        Round();
        Assert.Equal(1, Value(New), 6);
    }

    [Fact]
    public void Two_disks_whose_names_are_swapped_across_a_rescan_keep_their_own_counters()
    {
        var kit = new SysfsKit();
        var a = ScsiDisk(kit, "sda", 1, "naa.5000c500000000a1");
        var b = ScsiDisk(kit, "sdb", 2, "naa.5000c500000000b2");
        var proc = new SysfsKit().File("diskstats", Disk(8, 0, "sda", 10_000, 0) + Disk(8, 16, "sdb", 50_000, 0));
        Open(kit, proc);
        const string A = "system/disk/block-wwid+naa.5000c500000000a1/read_speed";
        const string B = "system/disk/block-wwid+naa.5000c500000000b2/read_speed";

        // The kernel names them the other way round after a rescan (a reboot, a re-plug): the counters follow the disks.
        kit.Link("class/block/sda", $"{b}/block/sdb");
        kit.Link("class/block/sdb", $"{a}/block/sda");
        proc.File("diskstats", Disk(8, 0, "sda", 50_000, 0) + Disk(8, 16, "sdb", 10_000, 0));
        Round();
        Assert.False(HasReading(A));
        Assert.False(HasReading(B));

        Rescan();
        proc.File("diskstats", Disk(8, 0, "sda", 50_000 + 4096, 0) + Disk(8, 16, "sdb", 10_000 + 2048, 0));
        Round();
        Assert.Equal(1, Value(A), 6);
        Assert.Equal(2, Value(B), 6);
    }

    [Fact]
    public void The_namespaces_of_one_nvme_controller_are_one_disk_whose_rates_are_their_sum()
    {
        var kit = new SysfsKit();
        var function = kit.BusDevice("devices/pci0000:00/0000:00:01.1/0000:01:00.0", "pci");
        var nvme = kit.ClassDevice($"{function}/nvme/nvme0", "nvme", ("serial", "FIXTURE-NVME-0001      "));
        kit.Link("class/block/nvme0n1", kit.ClassDevice($"{nvme}/nvme0n1", "block", ("wwid", "eui.0000000000000001")));
        kit.Link("class/block/nvme0n2", kit.ClassDevice($"{nvme}/nvme0n2", "block", ("wwid", "eui.0000000000000002")));
        kit.Link("class/block/nvme0n2p1", kit.ClassDevice($"{nvme}/nvme0n2/nvme0n2p1", "block", ("partition", "1")));
        string Stats(long n1Read, long n1Written, long n2Read, long n2Written) =>
            Disk(259, 0, "nvme0n1", n1Read, n1Written) + Disk(259, 1, "nvme0n2", n2Read, n2Written) + Disk(259, 2, "nvme0n2p1", n2Read, n2Written);
        var proc = new SysfsKit().File("diskstats", Stats(10_000, 20_000, 30_000, 40_000));
        Open(kit, proc);

        // One id pair under the controller's serial, as with one namespace: neither is left out as ambiguous.
        const string Id = "system/disk/nvme-serial+FIXTURE-NVME-0001/";
        Assert.Equal([Id + "read_speed", Id + "write_speed"], Ids("system/disk/"));
        Assert.DoesNotContain(_events, e => e.Level == LogEventLevel.Warning);

        proc.File("diskstats", Stats(10_000 + 2048, 20_000 + 1024, 30_000 + 4096, 40_000 + 1024));
        Round();
        Assert.Equal(3, Value(Id + "read_speed"), 6);
        Assert.Equal(1, Value(Id + "write_speed"), 6);

        // A counter of either going back gives no reading that round.
        proc.File("diskstats", Stats(10_000, 20_000 + 2048, 30_000 + 8192, 40_000 + 2048));
        Round();
        Assert.Equal(3, Value(Id + "read_speed"), 6);
    }

    // ---- tables longer than the read limit ----

    /// <summary>A folder with <paramref name="name"/> holding <paramref name="text"/>, read as /proc is: at most <see cref="Sysfs.MaxProcRead"/> bytes.</summary>
    private Sysfs ProcFolder(string name, string text)
    {
        var folder = _temp.Combine("proc");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), text);
        return Sysfs.Disk(folder, Sysfs.MaxProcRead);
    }

    [Fact]
    public void A_proc_stat_longer_than_the_read_limit_lists_only_whole_cpu_rows_and_says_so_once()
    {
        const int Cores = 8192;
        string Build(int tick, int pad)
        {
            var text = new System.Text.StringBuilder("cpu  1000 0 500 8000 10 0 5 0 0 0" + new string(' ', pad) + "\n");
            for (var n = 0; n < Cores; n++)
                text.Append(Cpu($"cpu{n}", 1000 + tick, 500 + tick, 8000 + 3 * tick, 10, softirq: 5));
            return text.ToString() + "intr 123456 0 0 0\nctxt 999\nbtime 1762750000\n";
        }

        // Padding on the total's row puts the limit inside a core's row, past its fifth field (a row that would read as
        // data with zeros for what is missing).
        var pad = Enumerable.Range(0, 40).First(p =>
        {
            var text = Build(0, p);
            var start = text.LastIndexOf('\n', Sysfs.MaxProcRead - 1) + 1;
            return text[Sysfs.MaxProcRead - 1] != '\n' && text[start..Sysfs.MaxProcRead].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 6;
        });
        var full = Build(0, pad);
        var whole = full[..(full.LastIndexOf('\n', Sysfs.MaxProcRead - 1) + 1)].Count(c => c == '\n') - 1;
        Assert.InRange(whole, 1, Cores - 1);

        var proc = ProcFolder("stat", full);
        Open(proc: proc);
        var cores = Ids("system/cpu/core");
        Assert.Equal(whole, cores.Length);
        Assert.Contains($"system/cpu/core{whole - 1}", cores);
        Assert.DoesNotContain($"system/cpu/core{whole}", cores);

        File.WriteAllText(_temp.Combine("proc", "stat"), Build(100, pad));
        Round();
        // busy +200 of +500 in all.
        Assert.Equal(40, Value($"system/cpu/core{whole - 1}"), 6);
        Round(3);
        Assert.Single(_events, e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("/proc/stat", StringComparison.Ordinal));
    }

    [Fact]
    public void A_proc_diskstats_longer_than_the_read_limit_leaves_out_the_row_it_cut_and_says_so_once()
    {
        // sda first, then loop devices, then sdz, whose row the limit cuts inside its sectors written (its first digits only
        // would read as a number).
        var sdz = Disk(8, 32, "sdz", 5000, 9_999_999_999);
        var cut = sdz.IndexOf("9999999999", StringComparison.Ordinal) + 3;
        string Build(long sdaRead)
        {
            var text = new System.Text.StringBuilder(Disk(8, 0, "sda", sdaRead, 0));
            for (var n = 0; text.Length < Sysfs.MaxProcRead - cut - 200; n++)
                text.Append(Disk(7, n, "loop" + n, 0, 0));
            text.Append(new string(' ', Sysfs.MaxProcRead - cut - text.Length - 1) + "\n");
            Assert.Equal(Sysfs.MaxProcRead - cut, text.Length);
            return text.ToString() + sdz;
        }

        var proc = ProcFolder("diskstats", Build(1000));
        Open(proc: proc);
        Assert.Equal(["system/disk/name+sda/read_speed", "system/disk/name+sda/write_speed"], Ids("system/disk/"));

        File.WriteAllText(_temp.Combine("proc", "diskstats"), Build(1000 + 2048));
        Round();
        Assert.Equal(1, Value("system/disk/name+sda/read_speed"), 6);
        Round(3);
        Assert.Single(_events, e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("/proc/diskstats", StringComparison.Ordinal));
    }

    // ---- amdgpu ----

    /// <summary>An amdgpu card at PCI 0000:03:00.0 (<c>class/drm/card1</c>, a connector beside it) with its hwmon device.</summary>
    private static SysfsKit AmdGpu(SysfsKit? kit = null)
    {
        kit ??= new SysfsKit();
        var bridge = kit.BusDevice("devices/pci0000:00/0000:00:03.1", "pci");
        var gpu = kit.BusDevice($"{bridge}/0000:03:00.0", "pci",
            ("gpu_busy_percent", "37"), ("mem_busy_percent", "12"), ("mem_info_vram_used", "2147483648"), ("mem_info_vram_total", "8589934592"));
        kit.Link("class/drm/card1", kit.ClassDevice($"{gpu}/drm/card1", "drm"));
        kit.Link($"{gpu}/drm/card1/device", gpu);
        kit.Link("class/drm/card1-DP-1", kit.ClassDevice($"{gpu}/drm/card1/card1-DP-1", "drm"));
        kit.Hwmon(5, gpu, "amdgpu",
            ("freq1_input", "2400000000"), ("freq1_label", "sclk"), ("freq2_input", "1000000000"), ("freq2_label", "mclk"),
            ("power1_average", "150000000"), ("temp1_input", "55000"), ("temp1_label", "edge"), ("temp2_input", "70000"),
            ("temp2_label", "junction"), ("temp3_input", "60000"), ("temp3_label", "mem"), ("fan1_input", "1500"));
        return kit;
    }

    [Fact]
    public void An_amdgpu_card_is_read_from_sysfs_under_its_pci_address_with_no_library()
    {
        Open(AmdGpu());

        const string Amd = "system/amdgpu/pci+0000-03-00.0/";
        Assert.Equal(
            [Amd + "utilization", Amd + "memory_utilization", Amd + "memory_used", Amd + "memory_total", Amd + "memory_percent",
             Amd + "clock_graphics", Amd + "clock_memory", Amd + "power", Amd + "temperature", Amd + "temperature_junction",
             Amd + "temperature_memory", Amd + "fan_rpm"],
            Ids("system/amdgpu/"));
        Assert.Equal(37, Value(Amd + "utilization"), 6);
        Assert.Equal(12, Value(Amd + "memory_utilization"), 6);
        Assert.Equal(2048, Value(Amd + "memory_used"), 6);
        Assert.Equal("MB", Info(Amd + "memory_used").Unit);
        Assert.Equal(8192, Value(Amd + "memory_total"), 6);
        Assert.Equal(25, Value(Amd + "memory_percent"), 6);
        Assert.Equal(2400, Value(Amd + "clock_graphics"), 6);
        Assert.Equal(1000, Value(Amd + "clock_memory"), 6);
        Assert.Equal(150, Value(Amd + "power"), 6);
        Assert.Equal("W", Info(Amd + "power").Unit);
        Assert.Equal(55, Value(Amd + "temperature"), 6);
        Assert.Equal(70, Value(Amd + "temperature_junction"), 6);
        Assert.Equal(60, Value(Amd + "temperature_memory"), 6);
        Assert.Equal(1500, Value(Amd + "fan_rpm"), 6);
    }

    // ---- NVIDIA through NVML ----

    [Fact]
    public void NVIDIA_gpus_are_listed_through_nvml_under_the_VFD_templates_id_grammar_and_nothing_without_nvml()
    {
        var nvml = new FakeNvml(new FakeGpu(Uuid, "00000000:01:00.0", "NVIDIA Fixture GPU") { Fans = [1100, 1250] });
        Open(nvml: () => nvml);

        // The VFD templates' four metrics, exactly as they spell them.
        string[] vfd = [Gpu + "utilization", Gpu + "fan0_rpm", Gpu + "temperature", Gpu + "temperature_vram"];
        var ids = Ids("system/gpu/");
        foreach (var id in vfd)
        {
            Assert.Contains(id, ids);
            Assert.Matches(new Regex(@"^system/gpu/gpu-uuid\+GPU-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}~pci\+[0-9a-f]{4}-[0-9a-f]{2}-[0-9a-f]{2}\.[0-7]/(utilization|fan0_rpm|temperature|temperature_vram)$"), id);
        }

        Assert.Equal(
            [Gpu + "utilization", Gpu + "memory_utilization", Gpu + "temperature", Gpu + "temperature_vram", Gpu + "memory_used",
             Gpu + "memory_total", Gpu + "memory_percent", Gpu + "power", Gpu + "power_limit", Gpu + "clock_graphics",
             Gpu + "clock_memory", Gpu + "fan_speed", Gpu + "fan0_rpm", Gpu + "fan1_rpm"],
            ids);
        Assert.Equal(63, Value(Gpu + "utilization"), 6);
        Assert.Equal(1100, Value(Gpu + "fan0_rpm"), 6);
        Assert.Equal("RPM", Info(Gpu + "fan0_rpm").Unit);
        Assert.Equal(1250, Value(Gpu + "fan1_rpm"), 6);
        Assert.Equal(61, Value(Gpu + "temperature"), 6);
        Assert.Equal(70, Value(Gpu + "temperature_vram"), 6);
        Assert.Equal(4096, Value(Gpu + "memory_used"), 6);
        Assert.Equal(25, Value(Gpu + "memory_percent"), 6);
        Assert.Equal(215.5, Value(Gpu + "power"), 6);
        Assert.Equal(450, Value(Gpu + "power_limit"), 6);
        Assert.Equal(2520, Value(Gpu + "clock_graphics"), 6);
        Assert.Equal("NVIDIA Fixture GPU", Info(Gpu + "utilization").Group);

        // No NVML (the library isn't there, or it doesn't start): no GPU listed, nothing else changed.
        _source!.Dispose();
        Open(nvml: () => null);
        Assert.Empty(Ids("system/gpu/"));
        _source!.Dispose();
        Open();
        Assert.Empty(Ids("system/gpu/"));
    }

    [Fact]
    public void An_nvml_metric_the_gpu_lacks_is_not_listed_one_refused_reads_denied_and_the_pci_address_is_written_as_sysfs_does()
    {
        var gpu = new FakeGpu("GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "00000000:0A:00.0", "NVIDIA Fixture GPU")
        {
            MemoryTemperature = NvmlStatus.NotSupported,
            PowerStatus = NvmlStatus.NoPermission,
        };
        Open(nvml: () => new FakeNvml(gpu));

        const string Id = "system/gpu/gpu-uuid+GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee~pci+0000-0a-00.0/";
        var ids = Ids("system/gpu/");
        Assert.Contains(Id + "utilization", ids);
        Assert.DoesNotContain(Id + "temperature_vram", ids);
        Assert.Contains(Id + "power", ids);
        Assert.Equal(SensorStatus.PermissionDenied, _source!.GetStatus(LinuxSensorSource.KeyOf(Id + "power")));
    }

    [Fact]
    public void The_nvml_loader_gives_none_when_the_library_is_not_there()
    {
        Assert.Null(NvmlLibrary.TryOpen(Log(), "libnvidia-ml-not-installed.so.1"));
        Assert.Contains(_events, e => e.RenderMessage().Contains("NVML", StringComparison.Ordinal));
    }

    // ---- demand ----

    [Fact]
    public void A_reader_none_of_whose_sensors_is_in_use_is_not_read()
    {
        var proc = new SysfsKit()
            .File("stat", Stat(Cpu("cpu ", 100, 100, 700, 100)))
            .File("meminfo", MemInfo(8_192_000, 4_096_000, 0, 0))
            .File("diskstats", DiskStats(0, 0));
        var nvml = new FakeNvml(new FakeGpu(Uuid, "00000000:01:00.0", "NVIDIA Fixture GPU"));
        OpenIdle(Disks(), proc, () => nvml);
        Assert.NotEmpty(Ids("system/cpu/"));
        Assert.NotEmpty(Ids("system/memory/"));
        Assert.NotEmpty(Ids("system/disk/"));
        Assert.NotEmpty(Ids("system/gpu/"));

        var (stat, meminfo, diskstats, calls) = (proc.ReadsOf("stat"), proc.ReadsOf("meminfo"), proc.ReadsOf("diskstats"), nvml.MetricCalls);
        Round(3);
        Assert.Equal(stat, proc.ReadsOf("stat"));
        Assert.Equal(meminfo, proc.ReadsOf("meminfo"));
        Assert.Equal(diskstats, proc.ReadsOf("diskstats"));
        Assert.Equal(calls, nvml.MetricCalls);

        // One memory sensor in use: memory is read from the next poll, once a round; the others still aren't.
        Use("system/memory/used_percent");
        Round(2);
        Assert.Equal(meminfo + 2, proc.ReadsOf("meminfo"));
        Assert.Equal(stat, proc.ReadsOf("stat"));
        Assert.Equal(diskstats, proc.ReadsOf("diskstats"));
        Assert.Equal(calls, nvml.MetricCalls);

        // One GPU metric in use: only its call is made.
        Use(Gpu + "temperature");
        Round();
        Assert.Equal(calls + 1, nvml.MetricCalls);
    }

    [Fact]
    public void An_nvidia_gpu_is_not_asked_for_its_metrics_at_the_rescans_while_none_is_in_use()
    {
        // The second GPU supports none of the metrics (and has no fans): what it offers, nothing, is kept as well.
        var nvml = new FakeNvml(
            new FakeGpu(Uuid, "00000000:01:00.0", "NVIDIA Fixture GPU") { Fans = [1100] },
            new FakeGpu("GPU-99999999-8888-7777-6666-555555555555", "00000000:02:00.0", "NVIDIA Fixture GPU") { Unsupported = true });
        OpenIdle(nvml: () => nvml);
        var listed = Ids("system/gpu/");
        Assert.NotEmpty(listed);
        Assert.Empty(Ids("system/gpu/gpu-uuid+GPU-99999999-8888-7777-6666-555555555555"));

        // Three rescans with nothing in use: each GPU is listed from what the first scan found, no metric is read.
        var calls = nvml.MetricCalls;
        Round(35);
        Assert.Equal(calls, nvml.MetricCalls);
        Assert.Equal(listed, Ids("system/gpu/"));

        // In use, a metric is read once a round, and a rescan adds no call of its own.
        Use(Gpu + "temperature");
        Round(12);
        Assert.Equal(calls + 12, nvml.MetricCalls);
    }

    [Fact]
    public void An_nvml_metric_that_failed_at_the_first_scan_is_asked_again_at_the_rescans_until_nvml_gives_it()
    {
        var gpu = new FakeGpu(Uuid, "00000000:01:00.0", "NVIDIA Fixture GPU") { PowerStatus = NvmlStatus.Failed };
        var nvml = new FakeNvml(gpu);
        OpenIdle(nvml: () => nvml);
        Assert.Contains(Gpu + "utilization", Ids("system/gpu/"));
        Assert.DoesNotContain(Gpu + "power", Ids("system/gpu/"));

        // Still failing: asked again at the rescan, its call alone, and still not listed.
        var calls = nvml.MetricCalls;
        Rescan();
        Assert.Equal(calls + 1, nvml.MetricCalls);
        Assert.DoesNotContain(Gpu + "power", Ids("system/gpu/"));

        // NVML gives it now: listed from the next rescan.
        gpu.PowerStatus = NvmlStatus.Ok;
        Rescan();
        Assert.Contains(Gpu + "power", Ids("system/gpu/"));

        // Given once: no longer asked at the rescans while nothing is in use; read when in use.
        calls = nvml.MetricCalls;
        Rescan();
        Assert.Equal(calls, nvml.MetricCalls);
        Use(Gpu + "power");
        Round();
        Assert.Equal(215.5, Value(Gpu + "power"), 6);
    }

    [Fact]
    public void A_failed_nvml_open_is_tried_again_at_the_next_rescan()
    {
        var nvml = new FakeNvml(new FakeGpu(Uuid, "00000000:01:00.0", "NVIDIA Fixture GPU"));
        var opens = 0;
        OpenIdle(nvml: () => ++opens == 1 ? null : nvml);
        Assert.Empty(Ids("system/gpu/"));
        Assert.Equal(1, opens);

        Rescan();
        Assert.Equal(2, opens);
        Assert.Contains(Gpu + "utilization", Ids("system/gpu/"));

        // Open now: not asked again.
        Rescan();
        Assert.Equal(2, opens);
    }

    [Fact]
    public void A_failed_nvml_open_is_logged_once_however_often_it_is_tried_and_the_late_success_is_said()
    {
        var nvml = new FakeNvml(new FakeGpu(Uuid, "00000000:01:00.0", "NVIDIA Fixture GPU"));
        var opens = 0;
        // As NvmlLibrary.TryOpen does: it logs its failure through the logger it is given.
        Func<Serilog.ILogger, INvml?> open = log =>
        {
            if (++opens <= 3)
            {
                log.Information("Linux sensors: NVML (fixture) isn't there, so no NVIDIA GPU is listed");
                return null;
            }

            return nvml;
        };
        OpenIdle(nvml: NvidiaGpuReader.Retrying(open, Log()));
        Rescan();
        Rescan();
        Assert.Equal(3, opens);
        Assert.Empty(Ids("system/gpu/"));
        Assert.Single(_events, e => e.RenderMessage().Contains("isn't there", StringComparison.Ordinal));

        Rescan();
        Assert.Equal(4, opens);
        Assert.Contains(Gpu + "utilization", Ids("system/gpu/"));
        Assert.Single(_events, e => e.RenderMessage().Contains("isn't there", StringComparison.Ordinal));
        Assert.Single(_events, e => e.RenderMessage().Contains("is there now", StringComparison.Ordinal));
    }

    /// <summary>A GPU as the fake NVML gives it.</summary>
    private sealed record FakeGpu(string Uuid, string BusId, string Name)
    {
        public uint[] Fans { get; init; } = [];
        public NvmlStatus MemoryTemperature { get; init; } = NvmlStatus.Ok;
        public NvmlStatus PowerStatus { get; set; } = NvmlStatus.Ok;

        /// <summary>Every metric NVML is asked for is "not supported" (and it has no fans).</summary>
        public bool Unsupported { get; init; }
    }

    /// <summary>NVML over <see cref="FakeGpu"/>s; counts the calls that read a metric.</summary>
    private sealed class FakeNvml(params FakeGpu[] gpus) : INvml
    {
        public int MetricCalls { get; private set; }

        public bool Disposed { get; private set; }

        private FakeGpu Gpu(nint device) => gpus[(int)device - 1];

        private NvmlStatus Metric(nint device, NvmlStatus status = NvmlStatus.Ok)
        {
            MetricCalls++;
            return Gpu(device).Unsupported ? NvmlStatus.NotSupported : status;
        }

        public NvmlStatus DeviceCount(out uint count)
        {
            count = (uint)gpus.Length;
            return NvmlStatus.Ok;
        }

        public NvmlStatus Device(uint index, out nint device)
        {
            device = (nint)index + 1;
            return index < gpus.Length ? NvmlStatus.Ok : NvmlStatus.Failed;
        }

        public NvmlStatus Uuid(nint device, out string uuid)
        {
            uuid = Gpu(device).Uuid;
            return NvmlStatus.Ok;
        }

        public NvmlStatus PciBusId(nint device, out string busId)
        {
            busId = Gpu(device).BusId;
            return NvmlStatus.Ok;
        }

        public NvmlStatus Name(nint device, out string name)
        {
            name = Gpu(device).Name;
            return NvmlStatus.Ok;
        }

        public NvmlStatus Utilization(nint device, out uint gpu, out uint memory)
        {
            (gpu, memory) = (63, 21);
            return Metric(device);
        }

        public NvmlStatus Temperature(nint device, out uint celsius)
        {
            celsius = 61;
            return Metric(device);
        }

        public NvmlStatus MemoryTemperature(nint device, out uint celsius)
        {
            celsius = 70;
            return Metric(device, Gpu(device).MemoryTemperature);
        }

        public NvmlStatus Memory(nint device, out ulong used, out ulong total)
        {
            (used, total) = (4096UL << 20, 16384UL << 20);
            return Metric(device);
        }

        public NvmlStatus Power(nint device, out uint milliwatts)
        {
            milliwatts = 215_500;
            return Metric(device, Gpu(device).PowerStatus);
        }

        public NvmlStatus PowerLimit(nint device, out uint milliwatts)
        {
            milliwatts = 450_000;
            return Metric(device);
        }

        public NvmlStatus Clock(nint device, NvmlClock clock, out uint mhz)
        {
            mhz = clock == NvmlClock.Graphics ? 2520u : 10501u;
            return Metric(device, clock is NvmlClock.Graphics or NvmlClock.Memory ? NvmlStatus.Ok : NvmlStatus.NotSupported);
        }

        public NvmlStatus FanCount(nint device, out uint count)
        {
            count = (uint)Gpu(device).Fans.Length;
            return NvmlStatus.Ok;
        }

        public NvmlStatus FanSpeed(nint device, out uint percent)
        {
            percent = 40;
            return Metric(device, Gpu(device).Fans.Length > 0 ? NvmlStatus.Ok : NvmlStatus.NotSupported);
        }

        public NvmlStatus FanRpm(nint device, uint fan, out uint rpm)
        {
            var fans = Gpu(device).Fans;
            rpm = fan < fans.Length ? fans[fan] : 0;
            return Metric(device, fan < fans.Length ? NvmlStatus.Ok : NvmlStatus.NotSupported);
        }

        public void Dispose() => Disposed = true;
    }
}
