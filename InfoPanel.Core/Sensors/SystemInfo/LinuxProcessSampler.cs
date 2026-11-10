using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace InfoPanel.Sensors.SystemInfo;

/// <summary>
/// Linux, from <c>/proc</c> as the normal user: the uptime from <c>/proc/uptime</c> (the boot clock, suspend included, as
/// Windows counts it); the CPUs' busy share from the first line of <c>/proc/stat</c>; app memory as the total less the
/// available memory of <c>/proc/meminfo</c>, and the compressed memory as its <c>Zswap</c> line (0 where there is none);
/// and the processes from the numbered folders of <c>/proc</c>. Like the macOS reader it lists this user's processes
/// (another user's are left out of the tables, though their folders are counted and, being readable, their threads
/// summed; where a <c>hidepid</c> mount refuses their <c>status</c> they are counted still, and the thread total, being
/// only a part, is not given): each one's name is its executable's whole file name (the last part of the <c>exe</c> link, else
/// <c>comm</c>, which the kernel cuts at 15 bytes); its CPU time the user and system ticks of its <c>stat</c> (at the
/// 100 a second Linux counts them); its start the <c>stat</c>'s start tick, so a pid used again isn't taken for the old
/// process; and its memory the anonymous and shared-memory pages it holds (<c>RssAnon</c> and <c>RssShmem</c> of its
/// <c>status</c>, its private footprint as the Mac's, without the files it maps; the whole <c>VmRSS</c> where the kernel
/// has no split). A process that exits while it is read is left out. The tables show a process's CPU per CPU
/// (a busy thread 100 %, as <c>top</c> does). Linux has no handle count and no CPU utility counter. A round's readings come
/// from one read of <c>/proc</c>, made at the round's first process reading and kept until <see cref="BeginRound"/>.
/// </summary>
public sealed class LinuxProcessSampler : IProcessSampler
{
    /// <summary>The ticks a second of CPU time holds in <c>stat</c> (the kernel's USER_HZ, 100 on x64 and arm64 Linux).</summary>
    public const int ClockTicks = 100;

    private readonly string _root;
    private readonly string? _uid;
    private readonly int _processors;

    // This round's read of the process folders, null until a reading asks for it.
    private Snapshot? _round;
    // The CPUs' busy, total and iowait ticks at the last CpuUsage reading, once there was one.
    private (ulong Busy, ulong Total, ulong IoWait)? _cpu;

    // Threads is null where a refused status made the total partial.
    private sealed record Snapshot(int Count, int? Threads, IReadOnlyList<ProcessReading> Own);

    /// <summary>The processes of this computer, from <c>/proc</c>, of the user running the engine.</summary>
    public LinuxProcessSampler() : this("/proc", null, null)
    {
    }

    /// <summary>
    /// The seams a test sets: <paramref name="root"/> is the folder read for <c>/proc</c>; <paramref name="uid"/> the user
    /// whose processes are listed (null: the engine's, from <c>{root}/self/status</c>); <paramref name="processors"/> the
    /// logical CPUs (null: <see cref="Environment.ProcessorCount"/>).
    /// </summary>
    internal LinuxProcessSampler(string root, string? uid, int? processors)
    {
        _root = root;
        _uid = uid;
        _processors = Math.Max(1, processors ?? Environment.ProcessorCount);
    }

    public int ProcessorCount => _processors;

    /// <summary><c>top</c>'s: a busy thread reads 100 %.</summary>
    public bool ProcessCpuPerCpu => true;

    public void BeginRound() => _round = null;

    public TimeSpan Uptime()
    {
        var text = File.ReadAllText(Path.Combine(_root, "uptime"));
        var first = text.AsSpan().TrimStart();
        var end = first.IndexOfAny(' ', '\t', '\n');
        if (end >= 0)
            first = first[..end];
        if (!double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            throw new InvalidOperationException("/proc/uptime can't be read");
        return TimeSpan.FromSeconds(seconds);
    }

    public int ProcessCount() => Round().Count;

    public int? ThreadCount() => Round().Threads;

    public int? HandleCount() => null;

    public double? CpuUsage()
    {
        // "cpu  user nice system idle iowait irq softirq steal guest guest_nice": guest time is in user's already.
        var line = ReadFirstLine(Path.Combine(_root, "stat"));
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 || fields[0] != "cpu")
            throw new InvalidOperationException("/proc/stat has no cpu line");
        ulong Field(int i) => i < fields.Length && ulong.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var idle = Field(4) + Field(5);
        var busy = Field(1) + Field(2) + Field(3) + Field(6) + Field(7) + Field(8);
        var now = (Busy: busy, Total: busy + idle, IoWait: Field(5));
        var before = _cpu;
        _cpu = now;
        // The first reading has no interval; neither has one after a counter that went back (a wrap, a restart), iowait too,
        // which the kernel's docs say can fall while the total, which holds it, rises less than busy.
        if (before is not { } last || now.Total < last.Total || now.Busy < last.Busy || now.IoWait < last.IoWait)
            return null;
        var total = now.Total - last.Total;
        return total > 0 ? (now.Busy - last.Busy) / (double)total * 100 : 0;
    }

    public double? CpuUtility() => null;

    public long MemoryUsage()
    {
        var meminfo = Meminfo();
        if (!meminfo.TryGetValue("MemTotal", out var total) || !meminfo.TryGetValue("MemAvailable", out var available))
            throw new InvalidOperationException("/proc/meminfo has no MemTotal and MemAvailable");
        return Math.Max(0, total - available);
    }

    public long MemoryCompression() => Meminfo().GetValueOrDefault("Zswap");

    public IReadOnlyList<ProcessReading> Processes() => Round().Own;

    /// <summary><c>/proc/meminfo</c>'s lines in bytes (it counts kilobytes, "MemTotal:  16384 kB").</summary>
    private Dictionary<string, long> Meminfo()
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(Path.Combine(_root, "meminfo")))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            if (KiloBytes(line.AsSpan(colon + 1)) is { } bytes)
                result[line[..colon]] = bytes;
        }

        return result;
    }

    private Snapshot Round() => _round ??= Read();

    private Snapshot Read()
    {
        var uid = _uid ?? OwnUid();
        var own = new List<ProcessReading>();
        int count = 0, threads = 0;
        var partial = false;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var folder = Path.GetFileName(dir);
            if (!int.TryParse(folder, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
                continue;
            try
            {
                Status status;
                try
                {
                    status = ReadStatus(Path.Combine(dir, "status"));
                }
                catch (UnauthorizedAccessException)
                {
                    // hidepid=1: the folder is listed but another user's status is refused. The process is there, so it is
                    // counted; its threads are not, so the total is only a part.
                    count++;
                    partial = true;
                    continue;
                }

                count++;
                threads += status.Threads;
                if (status.Uid != uid)
                    continue;
                var (name, cpu, started) = ReadStat(Path.Combine(dir, "stat"));
                var processName = ExecutableName(Path.Combine(dir, "exe")) ?? name;
                if (processName.Length > 0)
                    own.Add(new ProcessReading(pid, processName, cpu, status.Memory, started));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                // Gone since it was listed, or refused: left out.
            }
        }

        return new Snapshot(count, partial ? null : threads, own);
    }

    /// <summary>The engine's own user: the first of the <c>Uid</c> line of <c>{root}/self/status</c>.</summary>
    private string OwnUid()
    {
        var status = ReadStatus(Path.Combine(_root, "self", "status"));
        return status.Uid ?? throw new InvalidOperationException("/proc/self/status has no Uid");
    }

    private readonly record struct Status(string? Uid, int Threads, long Memory);

    /// <summary>The <c>Uid</c> (real), <c>Threads</c> and memory lines of a <c>status</c> file.</summary>
    private static Status ReadStatus(string path)
    {
        string? uid = null;
        var threads = 0;
        long? anon = null, shmem = null, resident = null;
        foreach (var line in File.ReadLines(path))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var value = line.AsSpan(colon + 1);
            switch (line.AsSpan(0, colon))
            {
                case "Uid":
                    value = value.Trim();
                    var end = value.IndexOfAny(' ', '\t');
                    uid = (end >= 0 ? value[..end] : value).ToString();
                    break;
                case "Threads":
                    int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out threads);
                    break;
                case "RssAnon":
                    anon = KiloBytes(value);
                    break;
                case "RssShmem":
                    shmem = KiloBytes(value);
                    break;
                case "VmRSS":
                    resident = KiloBytes(value);
                    break;
            }
        }

        // The pages the process holds alone; the whole resident size where the kernel doesn't split it (before 4.5).
        var memory = anon is { } a ? a + (shmem ?? 0) : resident ?? 0;
        return new Status(uid, threads, memory);
    }

    /// <summary>A size of the form "  1234 kB" in bytes, or null where it isn't one.</summary>
    private static long? KiloBytes(ReadOnlySpan<char> value)
    {
        value = value.Trim();
        var space = value.IndexOf(' ');
        var digits = space >= 0 ? value[..space] : value;
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var kb) && kb <= long.MaxValue / 1024 ? kb * 1024 : null;
    }

    /// <summary>
    /// A <c>stat</c> line's name (between the first <c>(</c> and the last <c>)</c>, which may hold spaces and brackets),
    /// the CPU time of its user and system ticks and its start tick.
    /// </summary>
    private static (string Name, TimeSpan Cpu, ulong Started) ReadStat(string path)
    {
        var text = File.ReadAllText(path);
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close < open)
            throw new FormatException("stat has no name");
        // After the name: state (field 3), then ppid (4) ... utime (14), stime (15) ... starttime (22).
        var rest = text[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length < 20
            || !ulong.TryParse(rest[11], NumberStyles.None, CultureInfo.InvariantCulture, out var user)
            || !ulong.TryParse(rest[12], NumberStyles.None, CultureInfo.InvariantCulture, out var system)
            || !ulong.TryParse(rest[19], NumberStyles.None, CultureInfo.InvariantCulture, out var started))
            throw new FormatException("stat can't be read");
        // 100 ns units, in whole integers; ticks beyond the largest time are cut there.
        var ticks = (UInt128)user + system;
        var cpu = ticks * (UInt128)(TimeSpan.TicksPerSecond / ClockTicks);
        return (text[(open + 1)..close], TimeSpan.FromTicks((long)UInt128.Min(cpu, (UInt128)long.MaxValue)), started);
    }

    /// <summary>The file name the <c>exe</c> link names (a deleted file's, without the kernel's " (deleted)"), or null where it can't be read.</summary>
    private static string? ExecutableName(string link)
    {
        try
        {
            if (new FileInfo(link).LinkTarget is not { Length: > 0 } target)
                return null;
            const string Deleted = " (deleted)";
            if (target.EndsWith(Deleted, StringComparison.Ordinal))
                target = target[..^Deleted.Length];
            var name = target[(target.LastIndexOf('/') + 1)..];
            return name.Length > 0 ? name : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadFirstLine(string path)
    {
        using var reader = new StreamReader(path);
        return reader.ReadLine() ?? "";
    }
}
