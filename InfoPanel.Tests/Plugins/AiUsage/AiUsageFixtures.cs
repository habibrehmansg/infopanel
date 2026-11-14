using System.Globalization;
using InfoPanel.AiUsage;

namespace InfoPanel.Tests.Plugins.AiUsage;

/// <summary>
/// Hand-written fixtures in the shapes Claude Code and Codex write (docs/2.0/ai-usage.md), never a copy of a real file:
/// Codex's <c>token_count</c> events and other lines, and Claude Code's status line input as the usage file holds it.
/// Times and totals are filled in per test; every other value is made up.
/// </summary>
internal static class Fixtures
{
    /// <summary>The fixtures' local time zone: UTC+8, with no daylight saving.</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Fixture+8", TimeSpan.FromHours(8), "Fixture+8", "Fixture+8");

    /// <summary>The fixtures' now: Tuesday 10 November 2026, 10:00 local (02:00 UTC).</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 10, 10, 0, 0, TimeSpan.FromHours(8));

    public static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static long Epoch(DateTimeOffset time) => time.ToUnixTimeSeconds();

    private static string Number(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Info(long total) =>
        $$"""{"total_token_usage":{"input_tokens":{{total - total / 10}},"cached_input_tokens":{{total / 2}},"cache_write_input_tokens":0,"output_tokens":{{total / 10}},"reasoning_output_tokens":{{total / 20}},"total_tokens":{{total}}},"last_token_usage":{"input_tokens":900,"cached_input_tokens":400,"cache_write_input_tokens":0,"output_tokens":100,"reasoning_output_tokens":50,"total_tokens":1000},"last_usage_includes_reasoning":true,"model_context_window":272000}""";

    private static string Window(double used, int minutes, DateTimeOffset resets) =>
        $$"""{"used_percent":{{Number(used)}},"window_minutes":{{minutes}},"resets_at":{{Epoch(resets)}}}""";

    private static string Limits(string primary, string secondary) =>
        $$"""{"limit_id":"codex","limit_name":null,"primary":{{primary}},"secondary":{{secondary}},"credits":{"has_credits":false,"unlimited":false,"balance":"0"},"individual_limit":null,"spend_control_reached":false,"plan_type":"prolite","rate_limit_reached_type":null}""";

    private static string TokenCount(DateTimeOffset at, string info, string limits) =>
        $$$"""{"timestamp":"{{{Iso(at)}}}","type":"event_msg","payload":{"type":"token_count","info":{{{info}}},"rate_limits":{{{limits}}}}}""";

    /// <summary>The shape every event on the Mac this was checked on has: the weekly window alone, as primary.</summary>
    public static string WeeklyOnly(DateTimeOffset at, double weekly, DateTimeOffset resets, long total) =>
        TokenCount(at, Info(total), Limits(Window(weekly, 10080, resets), "null"));

    /// <summary>A plan with both windows: the five-hour as primary, the weekly as secondary.</summary>
    public static string Both(DateTimeOffset at, double fiveHour, DateTimeOffset fiveHourResets, double weekly, DateTimeOffset weeklyResets, long total) =>
        TokenCount(at, Info(total), Limits(Window(fiveHour, 300, fiveHourResets), Window(weekly, 10080, weeklyResets)));

    /// <summary>Both windows the other way round: the weekly as primary, the five-hour as secondary.</summary>
    public static string BothSwapped(DateTimeOffset at, double fiveHour, DateTimeOffset fiveHourResets, double weekly, DateTimeOffset weeklyResets, long total) =>
        TokenCount(at, Info(total), Limits(Window(weekly, 10080, weeklyResets), Window(fiveHour, 300, fiveHourResets)));

    /// <summary>An event whose limits have no window at all (primary null).</summary>
    public static string PrimaryNull(DateTimeOffset at, long total) => TokenCount(at, Info(total), Limits("null", "null"));

    /// <summary>An event with limits and no token counts (info null).</summary>
    public static string InfoNull(DateTimeOffset at, double weekly, DateTimeOffset resets) =>
        TokenCount(at, "null", Limits(Window(weekly, 10080, resets), "null"));

    /// <summary>An event with tokens and no limits (rate_limits null).</summary>
    public static string NoLimits(DateTimeOffset at, long total) => TokenCount(at, Info(total), "null");

    /// <summary>A window of a length Codex never used (a day).</summary>
    public static string UnknownWindow(DateTimeOffset at, long total) =>
        TokenCount(at, Info(total), Limits(Window(20, 1440, at.AddHours(10)), "null"));

    /// <summary>The weekly window with <c>used_percent</c> renamed.</summary>
    public static string Renamed(DateTimeOffset at, DateTimeOffset resets, long total) =>
        TokenCount(at, Info(total), Limits($$$"""{"used_pct":54.0,"window_minutes":10080,"resets_at":{{{Epoch(resets)}}}}""", "null"));

    /// <summary>A session's first line.</summary>
    public static string SessionMeta(DateTimeOffset at) =>
        $$$"""{"timestamp":"{{{Iso(at)}}}","type":"session_meta","payload":{"id":"00000000-0000-4000-8000-000000000000","timestamp":"{{{Iso(at)}}}","cwd":"/fixture/project","originator":"codex_cli_rs","cli_version":"0.0.0"}}""";

    /// <summary>
    /// A model's answer of about <paramref name="bytes"/> bytes, whose text mentions "token_count" (escaped, as JSON
    /// writes quotes inside a string, so it never looks like an event's type).
    /// </summary>
    public static string ResponseItem(DateTimeOffset at, int bytes)
    {
        var head = $$"""{"timestamp":"{{Iso(at)}}","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"see the \"token_count\" event: """;
        const string tail = "\"}]}}";
        return head + new string('x', Math.Max(0, bytes - head.Length - tail.Length)) + tail;
    }

    /// <summary>Claude Code's status line input as the usage file holds it, with <paramref name="rateLimits"/> (a JSON value, or null to leave the member out).</summary>
    public static string StatusLine(string? rateLimits) =>
        """{"hook_event_name":"Status","session_id":"00000000-0000-4000-8000-000000000001","transcript_path":"/fixture/project/session.jsonl","cwd":"/fixture/project","model":{"id":"claude-fixture","display_name":"Fixture"},"workspace":{"current_dir":"/fixture/project","project_dir":"/fixture/project"},"version":"0.0.0","output_style":{"name":"default"},"cost":{"total_cost_usd":0.5,"total_duration_ms":1000,"total_api_duration_ms":500,"total_lines_added":1,"total_lines_removed":0},"exceeds_200k_tokens":false"""
        + (rateLimits == null ? "" : $",\"rate_limits\":{rateLimits}") + "}";

    /// <summary>Claude Code's two windows.</summary>
    public static string ClaudeBoth(double fiveHour, DateTimeOffset fiveHourResets, double weekly, DateTimeOffset weeklyResets) =>
        $$$"""{"five_hour":{"used_percentage":{{{Number(fiveHour)}}},"resets_at":{{{Epoch(fiveHourResets)}}}},"seven_day":{"used_percentage":{{{Number(weekly)}}},"resets_at":{{{Epoch(weeklyResets)}}}}}""";

    /// <summary>Claude Code's five-hour window alone.</summary>
    public static string ClaudeFiveHourOnly(double fiveHour, DateTimeOffset resets) =>
        $$$"""{"five_hour":{"used_percentage":{{{Number(fiveHour)}}},"resets_at":{{{Epoch(resets)}}}}}""";
}

/// <summary>A clock the test moves, in <see cref="Fixtures.Zone"/>.</summary>
internal sealed class FixtureClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => Fixtures.Zone;

    // Elapsed time is the test's too: it moves only when the test moves the clock, so a round's time budget is spent
    // only when a test spends it.
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Now.UtcTicks;
}

/// <summary>FIFOs for the tests that put one where a file is read (macOS and Linux).</summary>
internal static class Fifo
{
    /// <summary>Makes a FIFO at <paramref name="path"/>.</summary>
    public static void Make(string path)
    {
        if (mkfifo(path, 0x180) != 0) // 0600
            throw new IOException($"mkfifo failed: errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
    }

    /// <summary>
    /// Lets go of a reader waiting to open the FIFO at <paramref name="path"/>, if one waits: a writer opened without
    /// waiting (which fails when no reader waits) and closed at once.
    /// </summary>
    public static void Release(string path)
    {
        var flags = 1 | (OperatingSystem.IsMacOS() ? 0x0004 : 0x0800); // O_WRONLY | O_NONBLOCK
        var fd = open(path, flags);
        if (fd >= 0)
            close(fd);
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int mkfifo([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, uint mode);

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int open([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags);

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int close(int fd);
}

/// <summary>The disk, with every touch counted: what the plugin opened, looked at and listed.</summary>
internal sealed class CountingFiles : IUsageFiles
{
    private readonly DiskFiles _disk;

    public CountingFiles() => _disk = new DiskFiles(path =>
    {
        Interlocked.Increment(ref Entries);
        OnEntry?.Invoke(path);
    });

    public int Opens;
    public int Stats;
    public int FileLists;
    public int DirectoryLooks;
    public long BytesRead;

    /// <summary>The entries a listing looked at, files and folders alike.</summary>
    public int Entries;

    /// <summary>Run as a listing looks at an entry, with its path: the seam a test spends a round's time through.</summary>
    public Action<string>? OnEntry;

    public bool DirectoryExists(string path)
    {
        Interlocked.Increment(ref DirectoryLooks);
        return _disk.DirectoryExists(path);
    }

    public Listing Directories(string path, string pattern, int maxEntries, Func<bool>? stop = null)
    {
        Interlocked.Increment(ref DirectoryLooks);
        return _disk.Directories(path, pattern, maxEntries, stop);
    }

    public Listing Files(string path, string pattern, int max, int maxEntries, Func<bool>? stop = null)
    {
        Interlocked.Increment(ref FileLists);
        return _disk.Files(path, pattern, max, maxEntries, stop);
    }

    public FileStamp? Stat(string root, string path)
    {
        Interlocked.Increment(ref Stats);
        return _disk.Stat(root, path);
    }

    /// <summary>Run just before a file is opened, with its path: the seam a test changes a file through between its check and its open.</summary>
    public Action<string>? BeforeOpen;

    /// <summary>Run just after a file is opened (its stamp taken), with its path: the seam a test changes an open file through.</summary>
    public Action<string>? AfterOpen;

    public UsageFile Open(string root, string path)
    {
        Interlocked.Increment(ref Opens);
        BeforeOpen?.Invoke(path);
        var file = _disk.Open(root, path);
        AfterOpen?.Invoke(path);
        return new UsageFile(new Counted(file.Stream, this), file.Stamp);
    }

    private sealed class Counted(Stream inner, CountingFiles files) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Interlocked.Add(ref files.BytesRead, read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>A home folder for one test, under the temp folder, with Codex's and Claude Code's files written into it.</summary>
internal sealed class FixtureHome : IDisposable
{
    public string Path { get; } = PluginFixtures.NewTempRoot("ip-aiusage-");
    public FixtureClock Clock { get; } = new(Fixtures.Now);
    public CountingFiles Files { get; } = new();
    public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

    public UsageEnvironment Environment => new(Path, name => Variables.GetValueOrDefault(name), Clock, Files);

    public string Codex => System.IO.Path.Combine(Path, ".codex");

    /// <summary>
    /// A Codex session file in the day folder <paramref name="daysAgo"/> days before the fixtures' today, holding
    /// <paramref name="lines"/>, each ended by a newline; its path.
    /// </summary>
    public string CodexSession(int daysAgo, string name, params string[] lines)
    {
        var day = TimeZoneInfo.ConvertTime(Clock.Now, Fixtures.Zone).Date.AddDays(-daysAgo);
        var dir = System.IO.Path.Combine(Codex, "sessions", day.ToString("yyyy", CultureInfo.InvariantCulture), day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"rollout-{day:yyyy-MM-dd}T09-00-00-{name}.jsonl");
        File.WriteAllText(path, string.Concat(lines.Select(l => l + "\n")));
        return path;
    }

    /// <summary>A Claude folder <paramref name="name"/> in the home (with <c>projects/</c>); its path.</summary>
    public string ClaudeFolder(string name)
    {
        var folder = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.Combine(folder, "projects"));
        return folder;
    }

    /// <summary>Writes <paramref name="text"/> as <paramref name="folder"/>'s usage file, written at <paramref name="written"/>.</summary>
    public void Usage(string folder, string text, DateTimeOffset written)
    {
        var path = System.IO.Path.Combine(folder, LimitsReader.FileName);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
    }

    public void Dispose() => PluginFixtures.DeleteQuietly(Path);
}
