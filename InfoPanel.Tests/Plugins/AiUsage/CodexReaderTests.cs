using InfoPanel.AiUsage;
using Xunit;

namespace InfoPanel.Tests.Plugins.AiUsage;

// Codex's limits and tokens from its session logs, on hand-written fixtures in the shapes Codex writes.
public sealed class CodexReaderTests : IDisposable
{
    private readonly FixtureHome _home = new();
    private static DateTimeOffset Now => Fixtures.Now;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _home.Dispose();

    private CodexReading Read(CodexReader? reader = null) => (reader ?? new CodexReader(_home.Environment)).Read(_home.Codex, Ct);

    [Fact]
    public void The_weekly_window_alone_reads_with_no_five_hour_window_and_says_so()
    {
        _home.CodexSession(0, "a", Fixtures.SessionMeta(Now.AddHours(-2)), Fixtures.WeeklyOnly(Now.AddMinutes(-90), 54, Now.AddDays(2), 12800));

        var reading = Read();

        Assert.Null(reading.Limits.FiveHour);
        Assert.Equal(new Window(54, Now.AddDays(2)), reading.Limits.Weekly);
        Assert.Equal(Now.AddMinutes(-90), reading.Limits.AsOf);
        Assert.Equal("Codex reports no five-hour window for this plan", reading.Limits.Status);
        Assert.Equal(("none", "prolite"), (reading.Credits, reading.Plan));

        var entries = new CodexEntries();
        entries.Show(reading, Now, Fixtures.Zone);
        Assert.True(float.IsNaN(entries.FiveHourUsed.Value));
        Assert.True(float.IsNaN(entries.FiveHourResetIn.Value));
        Assert.Equal(54, entries.WeeklyUsed.Value);
        Assert.Equal(2 * 24 * 60, entries.WeeklyResetIn.Value);
        Assert.Equal(90, entries.AsOfAge.Value);
        // Older than 30 minutes: "as of" its local time.
        Assert.Equal("as of Tue 08:30", entries.Caption.Value);
    }

    [Fact]
    public void Both_windows_are_told_apart_by_their_length_whichever_slot_they_are_in()
    {
        _home.CodexSession(0, "a", Fixtures.Both(Now.AddMinutes(-20), 24, Now.AddHours(4).AddMinutes(30), 45, Now.AddDays(2).AddHours(-1), 100));
        var reading = Read();
        Assert.Equal((new Window(24, Now.AddHours(4).AddMinutes(30)), new Window(45, Now.AddDays(2).AddHours(-1))), (reading.Limits.FiveHour, reading.Limits.Weekly));
        Assert.Equal(LimitsReading.Ok, reading.Limits.Status);

        var entries = new CodexEntries();
        entries.Show(reading, Now, Fixtures.Zone);
        Assert.Equal("resets 14:30 · wk Thu 09:00", entries.Caption.Value);
        Assert.Equal(270, entries.FiveHourResetIn.Value);

        _home.CodexSession(0, "b", Fixtures.BothSwapped(Now.AddMinutes(-10), 30, Now.AddHours(3), 50, Now.AddDays(3), 200));
        reading = Read();
        Assert.Equal((new Window(30, Now.AddHours(3)), new Window(50, Now.AddDays(3))), (reading.Limits.FiveHour, reading.Limits.Weekly));
    }

    [Fact]
    public void An_event_with_no_window_leaves_the_last_one_that_had_them()
    {
        _home.CodexSession(0, "a",
            Fixtures.WeeklyOnly(Now.AddMinutes(-20), 54, Now.AddDays(2), 1000),
            Fixtures.PrimaryNull(Now.AddMinutes(-10), 1200));

        var reading = Read();

        Assert.Equal(new Window(54, Now.AddDays(2)), reading.Limits.Weekly);
        Assert.Equal(Now.AddMinutes(-20), reading.Limits.AsOf);
        // Its tokens still count.
        Assert.Equal(1200, reading.TokensToday);
    }

    [Fact]
    public void An_event_with_no_token_counts_still_gives_its_limits()
    {
        _home.CodexSession(0, "a",
            Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 700),
            Fixtures.InfoNull(Now.AddMinutes(-5), 41, Now.AddDays(2)));

        var reading = Read();

        Assert.Equal(new Window(41, Now.AddDays(2)), reading.Limits.Weekly);
        Assert.Equal(Now.AddMinutes(-5), reading.Limits.AsOf);
        Assert.Equal(700, reading.TokensToday);
    }

    [Fact]
    public void Tokens_and_no_limits_say_Codex_reports_none()
    {
        _home.CodexSession(0, "a", Fixtures.NoLimits(Now.AddMinutes(-5), 900));

        var reading = Read();

        Assert.Equal((null, null), (reading.Limits.FiveHour, reading.Limits.Weekly));
        Assert.Equal("Codex reports no limits", reading.Limits.Status);
        Assert.Equal(900, reading.TokensToday);
    }

    [Fact]
    public void A_megabyte_line_before_the_last_event_and_after_it_is_read_through()
    {
        _home.CodexSession(0, "a",
            Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 1000),
            Fixtures.ResponseItem(Now.AddMinutes(-25), 1024 * 1024),
            Fixtures.WeeklyOnly(Now.AddMinutes(-20), 54, Now.AddDays(2), 2000),
            Fixtures.ResponseItem(Now.AddMinutes(-15), 1024 * 1024));

        var reading = Read();

        Assert.Equal(new Window(54, Now.AddDays(2)), reading.Limits.Weekly);
        Assert.Equal(2000, reading.TokensToday);
    }

    [Fact]
    public void A_line_past_the_limit_is_passed_over_and_the_events_after_it_read()
    {
        _home.CodexSession(0, "a",
            Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 1000),
            Fixtures.ResponseItem(Now.AddMinutes(-25), CodexReader.MaxLineBytes + 1024),
            Fixtures.WeeklyOnly(Now.AddMinutes(-20), 54, Now.AddDays(2), 2000));

        var reading = Read();

        Assert.Equal(new Window(54, Now.AddDays(2)), reading.Limits.Weekly);
        Assert.Equal(2000, reading.TokensToday);
    }

    [Fact]
    public void A_repeated_total_counts_once()
    {
        _home.CodexSession(0, "a",
            Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 1000),
            Fixtures.WeeklyOnly(Now.AddMinutes(-29), 40, Now.AddDays(2), 1000),
            Fixtures.WeeklyOnly(Now.AddMinutes(-20), 41, Now.AddDays(2), 1500));

        var reading = Read();

        Assert.Equal((1500L, 1500L), (reading.TokensToday, reading.Tokens7d));
    }

    [Fact]
    public void Tokens_today_start_at_local_midnight_and_7_days_at_now_less_a_week()
    {
        var midnight = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.FromHours(8));
        // A session begun yesterday that went on past midnight.
        _home.CodexSession(1, "a",
            Fixtures.WeeklyOnly(midnight.AddMinutes(-10), 40, Now.AddDays(2), 1000),
            Fixtures.WeeklyOnly(midnight.AddMinutes(10), 41, Now.AddDays(2), 1500),
            Fixtures.WeeklyOnly(Now.AddHours(-1), 42, Now.AddDays(2), 2100));
        // Six days ago: in the 7 days, not today.
        _home.CodexSession(6, "b", Fixtures.WeeklyOnly(Now.AddDays(-6), 10, Now.AddDays(1), 500));
        // Seven and a half days ago: in the eighth day folder, but before the 7 days.
        _home.CodexSession(7, "c", Fixtures.WeeklyOnly(Now.AddDays(-7).AddHours(-1), 5, Now.AddDays(1), 300));
        // Nine days ago: never read.
        _home.CodexSession(9, "d", Fixtures.WeeklyOnly(Now.AddDays(-9), 99, Now.AddDays(1), 50000));

        var reading = Read();

        Assert.Equal(1100, reading.TokensToday);
        Assert.Equal(2100 + 500, reading.Tokens7d);
        Assert.Equal(new Window(42, Now.AddDays(2)), reading.Limits.Weekly);
    }

    [Fact]
    public void No_event_in_8_days_reads_nothing_and_says_so()
    {
        _home.CodexSession(9, "d", Fixtures.WeeklyOnly(Now.AddDays(-9), 99, Now.AddDays(1), 50000));

        var reading = Read();

        Assert.Equal((null, null, null, null), (reading.Limits.FiveHour, reading.Limits.Weekly, reading.TokensToday, reading.Tokens7d));
        Assert.Equal("no Codex use seen in the last 8 days", reading.Limits.Status);
    }

    [Fact]
    public void A_missing_folder_reads_nothing_and_says_where_it_looked_without_the_users_name()
    {
        var reading = Read();

        Assert.Null(reading.Limits.Weekly);
        Assert.Equal("Codex's folder wasn't found (~/.codex)", reading.Limits.Status);
    }

    [Fact]
    public void A_window_of_unknown_length_reads_format_changed()
    {
        _home.CodexSession(0, "a",
            Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 1000),
            Fixtures.UnknownWindow(Now.AddMinutes(-5), 1100));

        var reading = Read();

        Assert.Equal((null, null), (reading.Limits.FiveHour, reading.Limits.Weekly));
        Assert.Equal("Codex's log format changed (rate_limits.primary.window_minutes unknown); InfoPanel may need an update", reading.Limits.Status);
    }

    [Fact]
    public void A_renamed_field_reads_format_changed_naming_the_field()
    {
        _home.CodexSession(0, "a", Fixtures.Renamed(Now.AddMinutes(-5), Now.AddDays(2), 1100));

        var reading = Read();

        Assert.Null(reading.Limits.Weekly);
        Assert.Equal("Codex's log format changed (rate_limits.primary.used_percent missing); InfoPanel may need an update", reading.Limits.Status);
    }

    [Fact]
    public void Only_appended_bytes_are_read_and_a_line_still_being_written_waits_for_its_end()
    {
        var path = _home.CodexSession(0, "a", Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 1000));
        var reader = new CodexReader(_home.Environment);
        Assert.Equal(new Window(40, Now.AddDays(2)), Read(reader).Limits.Weekly);
        var size = new FileInfo(path).Length;
        Assert.Equal(size, _home.Files.BytesRead);

        // Nothing new: nothing read.
        Read(reader);
        Assert.Equal(size, _home.Files.BytesRead);

        // Half a line: read, and not taken until it ends.
        var next = Fixtures.WeeklyOnly(Now.AddMinutes(-5), 54, Now.AddDays(2), 1600);
        File.AppendAllText(path, next[..(next.Length / 2)]);
        Assert.Equal(new Window(40, Now.AddDays(2)), Read(reader).Limits.Weekly);
        File.AppendAllText(path, next[(next.Length / 2)..] + "\n");
        var reading = Read(reader);
        Assert.Equal(new Window(54, Now.AddDays(2)), reading.Limits.Weekly);
        Assert.Equal(1600, reading.TokensToday);
        // Every byte was read once, the half line's too (kept until its end came), and at each growth the file's first
        // bytes again, to tell it is still the file read.
        var half = next.Length / 2;
        Assert.Equal(size + Math.Min(CodexReader.HeadBytes, size) + half + Math.Min(CodexReader.HeadBytes, size + half) + (next.Length - half + 1), _home.Files.BytesRead);
    }

    // ---- Files: only regular files below Codex's folder, never through a link, never waiting on one.

    [Fact]
    public void A_linked_folder_below_Codexs_folder_is_never_followed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Making a link on Windows needs a privilege the tests don't have.");
        _home.CodexSession(0, "a", Fixtures.WeeklyOnly(Now.AddMinutes(-5), 54, Now.AddDays(2), 900));
        // The year's folder moved elsewhere, a link to it left in its place.
        var year = Path.Combine(_home.Codex, "sessions", "2026");
        var elsewhere = Path.Combine(_home.Path, "elsewhere-2026");
        Directory.Move(year, elsewhere);
        Directory.CreateSymbolicLink(year, elsewhere);

        var reading = Read();

        Assert.Equal((null, null, null), (reading.Limits.Weekly, reading.TokensToday, reading.Tokens7d));
        Assert.Equal(0, _home.Files.BytesRead);
    }

    [Fact]
    public async Task A_log_swapped_for_a_FIFO_before_its_open_is_refused_without_waiting()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no FIFOs.");
        var path = _home.CodexSession(0, "a", Fixtures.WeeklyOnly(Now.AddMinutes(-5), 54, Now.AddDays(2), 900));
        _home.Files.BeforeOpen = p =>
        {
            if (p == path && File.Exists(p))
            {
                File.Delete(p);
                Fifo.Make(p);
            }
        };

        var read = Task.Run(() => Read(), Ct);
        try
        {
            Assert.True(await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10), Ct)) == read, "the read waited on the FIFO");
        }
        finally
        {
            Fifo.Release(path);
        }
        Assert.Null((await read).Limits.Weekly);
    }

    // ---- Budgets: files listed, bytes and time a round, totals kept; cancellation.

    [Fact]
    public void A_day_folder_is_listed_only_up_to_its_limit_and_the_tokens_say_why_they_are_left_out()
    {
        for (var i = 0; i < 8; i++)
            _home.CodexSession(0, $"f{i}", Fixtures.WeeklyOnly(Now.AddMinutes(-30 + i), 40 + i, Now.AddDays(2), 100 * (i + 1)));

        var reading = new CodexReader(_home.Environment, CodexBudget.Default with { FilesPerDay = 5 }).Read(_home.Codex, Ct);

        Assert.True(_home.Files.Stats <= 5, $"{_home.Files.Stats} files looked at");
        Assert.Equal((null, null), (reading.TokensToday, reading.Tokens7d));
        Assert.Equal("Codex's day folders hold more than 5 logs; tokens are left out", reading.Limits.Status);
    }

    /// <summary>A log in today's folder with <paramref name="folders"/> folders beside it; the log's path.</summary>
    private string DayWithFolders(int folders, long total)
    {
        var path = _home.CodexSession(0, "a", Fixtures.WeeklyOnly(Now.AddMinutes(-5), 54, Now.AddDays(2), total));
        for (var i = 0; i < folders; i++)
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(path)!, $"folder-{i:D3}"));
        return path;
    }

    [Fact]
    public void A_day_folder_of_many_folders_is_listed_within_its_entries_and_the_tokens_say_why_they_are_left_out()
    {
        DayWithFolders(40, 900);

        var reading = new CodexReader(_home.Environment, CodexBudget.Default with { FilesPerDay = 5, EntriesPerDay = 10 }).Read(_home.Codex, Ct);

        // Folders count as entries looked at, as files do.
        Assert.True(_home.Files.Entries <= 10, $"{_home.Files.Entries} entries looked at");
        // One log found, but the folder wasn't listed to its end: its tokens can't be told whole.
        Assert.Equal((null, null), (reading.TokensToday, reading.Tokens7d));
        Assert.Equal("a Codex day folder holds more than 10 entries; tokens are left out", reading.Limits.Status);
    }

    [Fact]
    public void Listing_the_day_folders_spends_the_rounds_time_and_stops_when_it_runs_out()
    {
        DayWithFolders(300, 900);
        var reader = new CodexReader(_home.Environment, CodexBudget.Default with { TimePerRound = TimeSpan.FromSeconds(2) });
        // Each entry looked at takes 10 ms: the folder would take 3 seconds to list.
        _home.Files.OnEntry = _ => _home.Clock.Now = _home.Clock.Now.AddMilliseconds(10);

        var reading = reader.Read(_home.Codex, Ct);
        Assert.True(_home.Files.Entries <= 202, $"{_home.Files.Entries} entries looked at");
        Assert.StartsWith("reading Codex's logs", reading.Limits.Status);
        Assert.Equal((null, null), (reading.TokensToday, reading.Tokens7d));

        // The next round, with time to spare, carries on.
        _home.Files.OnEntry = null;
        reading = reader.Read(_home.Codex, Ct);
        Assert.Equal((900L, 900L), (reading.TokensToday, reading.Tokens7d));
    }

    [Fact]
    public void A_listing_cancelled_part_way_stops_at_once()
    {
        DayWithFolders(300, 900);
        var reader = new CodexReader(_home.Environment);
        using var cancel = new CancellationTokenSource();
        _home.Files.OnEntry = _ => cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => reader.Read(_home.Codex, cancel.Token));
        Assert.Equal(1, _home.Files.Entries);
    }

    [Fact]
    public void The_first_scan_reads_at_most_its_bytes_a_round_and_carries_on_where_it_stopped()
    {
        const int budget = 64 * 1024;
        var old = _home.CodexSession(1, "old",
            Fixtures.WeeklyOnly(Now.AddDays(-1), 30, Now.AddDays(2), 1000),
            Fixtures.ResponseItem(Now.AddDays(-1).AddMinutes(1), 100 * 1024),
            Fixtures.WeeklyOnly(Now.AddDays(-1).AddMinutes(5), 31, Now.AddDays(2), 2000));
        var recent = _home.CodexSession(0, "new",
            Fixtures.WeeklyOnly(Now.AddMinutes(-5), 54, Now.AddDays(2), 500),
            Fixtures.ResponseItem(Now.AddMinutes(-4), 100 * 1024));
        var reader = new CodexReader(_home.Environment, CodexBudget.Default with { BytesPerRound = budget });

        var reading = reader.Read(_home.Codex, Ct);
        Assert.True(_home.Files.BytesRead <= budget, $"{_home.Files.BytesRead} bytes read in the first round");
        Assert.Equal((null, null), (reading.TokensToday, reading.Tokens7d));
        Assert.StartsWith("reading Codex's logs", reading.Limits.Status);
        // The newest day first: its limits already.
        Assert.Equal(new Window(54, Now.AddDays(2)), reading.Limits.Weekly);

        for (var round = 0; round < 10 && reading.TokensToday == null; round++)
            reading = reader.Read(_home.Codex, Ct);
        Assert.Equal((500L, 2500L), (reading.TokensToday, reading.Tokens7d));
        Assert.Equal("Codex reports no five-hour window for this plan", reading.Limits.Status);
        // Every byte read once.
        Assert.Equal(new FileInfo(old).Length + new FileInfo(recent).Length, _home.Files.BytesRead);
    }

    [Fact]
    public void A_round_past_its_time_stops_and_the_next_carries_on()
    {
        _home.CodexSession(1, "old", Fixtures.WeeklyOnly(Now.AddDays(-1), 30, Now.AddDays(2), 1000));
        _home.CodexSession(0, "new", Fixtures.WeeklyOnly(Now.AddMinutes(-5), 54, Now.AddDays(2), 500));
        var reader = new CodexReader(_home.Environment, CodexBudget.Default with { TimePerRound = TimeSpan.FromSeconds(2) });
        // Each open takes 3 seconds.
        _home.Files.BeforeOpen = _ => _home.Clock.Now = _home.Clock.Now.AddSeconds(3);

        var reading = reader.Read(_home.Codex, Ct);
        Assert.Equal(1, _home.Files.Opens);
        Assert.StartsWith("reading Codex's logs", reading.Limits.Status);

        reading = reader.Read(_home.Codex, Ct);
        Assert.Equal((500L, 1500L), (reading.TokensToday, reading.Tokens7d));
    }

    [Fact]
    public void A_cancelled_read_stops_and_the_next_carries_on()
    {
        var a = _home.CodexSession(1, "old", Fixtures.WeeklyOnly(Now.AddDays(-1), 30, Now.AddDays(2), 1000));
        var b = _home.CodexSession(0, "new", Fixtures.WeeklyOnly(Now.AddMinutes(-5), 54, Now.AddDays(2), 500));
        var reader = new CodexReader(_home.Environment);
        using var cancel = new CancellationTokenSource();
        _home.Files.BeforeOpen = _ => cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => reader.Read(_home.Codex, cancel.Token));

        _home.Files.BeforeOpen = null;
        var reading = reader.Read(_home.Codex, Ct);
        Assert.Equal((500L, 1500L), (reading.TokensToday, reading.Tokens7d));
        Assert.Equal(new FileInfo(a).Length + new FileInfo(b).Length, _home.Files.BytesRead);
    }

    [Fact]
    public void Token_totals_are_kept_once_a_minute_and_a_repeated_one_not_again()
    {
        var lines = new List<string>();
        // 300 answers a minute apart that used no tokens, then three in one minute that did.
        for (var i = 0; i < 300; i++)
            lines.Add(Fixtures.WeeklyOnly(Now.AddMinutes(-400 + i), 40, Now.AddDays(2), 1000));
        lines.Add(Fixtures.WeeklyOnly(Now.AddMinutes(-10), 41, Now.AddDays(2), 1100));
        lines.Add(Fixtures.WeeklyOnly(Now.AddMinutes(-10).AddSeconds(20), 41, Now.AddDays(2), 1200));
        lines.Add(Fixtures.WeeklyOnly(Now.AddMinutes(-10).AddSeconds(40), 41, Now.AddDays(2), 1300));
        _home.CodexSession(0, "a", [.. lines]);
        var reader = new CodexReader(_home.Environment);

        var reading = reader.Read(_home.Codex, Ct);

        Assert.Equal(2, reader.RetainedSamples);
        Assert.Equal((1300L, 1300L), (reading.TokensToday, reading.Tokens7d));
    }

    [Fact]
    public void Past_the_totals_it_keeps_the_tokens_are_left_out_and_say_why()
    {
        _home.CodexSession(0, "a", [.. Enumerable.Range(0, 5).Select(i => Fixtures.WeeklyOnly(Now.AddMinutes(-50 + 10 * i), 40, Now.AddDays(2), 1000 * (i + 1)))]);
        var reader = new CodexReader(_home.Environment, CodexBudget.Default with { Samples = 3 });

        var reading = reader.Read(_home.Codex, Ct);

        Assert.True(reader.RetainedSamples <= 3, $"{reader.RetainedSamples} totals kept");
        Assert.Equal((null, null), (reading.TokensToday, reading.Tokens7d));
        Assert.Equal("Codex's logs hold more token counts than AI usage keeps (3); tokens are left out", reading.Limits.Status);
        Assert.Equal(new Window(40, Now.AddDays(2)), reading.Limits.Weekly);
    }

    // ---- A file rewritten, replaced or truncated is read again from its start.

    [Fact]
    public void A_file_rewritten_to_the_same_length_is_read_again()
    {
        var path = _home.CodexSession(0, "a", Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 1000));
        var reader = new CodexReader(_home.Environment);
        Assert.Equal(40, Read(reader).Limits.Weekly!.UsedPercent);

        var rewritten = Fixtures.WeeklyOnly(Now.AddMinutes(-30), 41, Now.AddDays(2), 1000) + "\n";
        Assert.Equal(new FileInfo(path).Length, rewritten.Length);
        File.WriteAllText(path, rewritten);
        File.SetLastWriteTimeUtc(path, Now.AddMinutes(1).UtcDateTime);

        Assert.Equal(41, Read(reader).Limits.Weekly!.UsedPercent);
    }

    [Fact]
    public void A_file_rewritten_to_the_same_length_while_its_first_scan_is_part_way_is_read_again()
    {
        var path = _home.CodexSession(0, "a",
            Fixtures.SessionMeta(Now.AddMinutes(-40)),
            Fixtures.ResponseItem(Now.AddMinutes(-39), 2 * 1024),
            Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 5000),
            Fixtures.ResponseItem(Now.AddMinutes(-20), 125 * 1024));
        var reader = new CodexReader(_home.Environment, CodexBudget.Default with { BytesPerRound = 64 * 1024 });
        var reading = reader.Read(_home.Codex, Ct);
        Assert.StartsWith("reading Codex's logs", reading.Limits.Status);
        Assert.Equal(64 * 1024, _home.Files.BytesRead);

        // The event already read, past the first KiB, rewritten in place to the same length, at another time.
        var text = File.ReadAllText(path);
        var rewritten = text.Replace("\"used_percent\":40.0", "\"used_percent\":41.0").Replace("\"total_tokens\":5000}", "\"total_tokens\":5009}");
        Assert.Equal(text.Length, rewritten.Length);
        Assert.NotEqual(text, rewritten);
        File.WriteAllText(path, rewritten);
        File.SetLastWriteTimeUtc(path, Now.AddMinutes(1).UtcDateTime);

        for (var round = 0; round < 10 && reading.Limits.Status.StartsWith("reading", StringComparison.Ordinal); round++)
            reading = reader.Read(_home.Codex, Ct);
        Assert.Equal((41d, 5009L), (reading.Limits.Weekly!.UsedPercent, reading.TokensToday));
    }

    [Fact]
    public void A_file_replaced_by_a_longer_one_is_read_from_its_start()
    {
        var path = _home.CodexSession(0, "a", Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 1000));
        var reader = new CodexReader(_home.Environment);
        Assert.Equal(1000, Read(reader).TokensToday);

        var other = Path.Combine(_home.Path, "other.jsonl");
        File.WriteAllText(other, Fixtures.WeeklyOnly(Now.AddMinutes(-20), 60, Now.AddDays(2), 300) + "\n" + Fixtures.PrimaryNull(Now.AddMinutes(-10), 500) + "\n");
        File.Move(other, path, overwrite: true);

        var reading = Read(reader);
        Assert.Equal((60d, 500L), (reading.Limits.Weekly!.UsedPercent, reading.TokensToday));
    }

    [Fact]
    public void A_file_truncated_and_written_again_is_read_from_its_start()
    {
        var path = _home.CodexSession(0, "a", Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 1000));
        var reader = new CodexReader(_home.Environment);
        Assert.Equal(1000, Read(reader).TokensToday);

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(0);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(Fixtures.WeeklyOnly(Now.AddMinutes(-20), 60, Now.AddDays(2), 300) + "\n" + Fixtures.PrimaryNull(Now.AddMinutes(-10), 500) + "\n"));
        }

        var reading = Read(reader);
        Assert.Equal((60d, 500L), (reading.Limits.Weekly!.UsedPercent, reading.TokensToday));
    }

    // ---- A line still being written.

    [Fact]
    public void An_unfinished_line_costs_no_read_until_the_file_grows()
    {
        var path = _home.CodexSession(0, "a", Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 1000));
        File.AppendAllText(path, Fixtures.ResponseItem(Now.AddMinutes(-20), 100 * 1024));
        var reader = new CodexReader(_home.Environment);
        Read(reader);
        var size = new FileInfo(path).Length;
        Assert.Equal(size, _home.Files.BytesRead);

        Read(reader);
        Read(reader);
        Assert.Equal(size, _home.Files.BytesRead);

        var appended = "\n" + Fixtures.WeeklyOnly(Now.AddMinutes(-5), 54, Now.AddDays(2), 1600) + "\n";
        File.AppendAllText(path, appended);
        var reading = Read(reader);
        Assert.Equal((54d, 1600L), (reading.Limits.Weekly!.UsedPercent, reading.TokensToday));
        Assert.Equal(size + CodexReader.HeadBytes + appended.Length, _home.Files.BytesRead);
    }

    [Fact]
    public void An_unfinished_line_past_the_limit_is_passed_over_and_never_read_again()
    {
        var path = _home.CodexSession(0, "a", Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 1000));
        File.AppendAllText(path, new string('x', CodexReader.MaxLineBytes + 1024));
        var reader = new CodexReader(_home.Environment);
        Read(reader);
        var size = new FileInfo(path).Length;
        Read(reader);
        Assert.Equal(size, _home.Files.BytesRead);

        var appended = "xx\n" + Fixtures.WeeklyOnly(Now.AddMinutes(-5), 54, Now.AddDays(2), 1600) + "\n";
        File.AppendAllText(path, appended);
        var reading = Read(reader);
        Assert.Equal((54d, 1600L), (reading.Limits.Weekly!.UsedPercent, reading.TokensToday));
        Assert.Equal(size + CodexReader.HeadBytes + appended.Length, _home.Files.BytesRead);
    }

    // ---- Format changes reach the readings they make unknown.

    [Fact]
    public void A_missing_rate_limits_member_reads_format_changed_not_the_last_limits()
    {
        _home.CodexSession(0, "a",
            Fixtures.WeeklyOnly(Now.AddMinutes(-30), 54, Now.AddDays(2), 1000),
            Fixtures.WeeklyOnly(Now.AddMinutes(-5), 55, Now.AddDays(2), 1200).Replace("\"rate_limits\"", "\"rateLimits\""));

        var reading = Read();

        Assert.Equal((null, null), (reading.Limits.FiveHour, reading.Limits.Weekly));
        Assert.Equal("Codex's log format changed (rate_limits missing); InfoPanel may need an update", reading.Limits.Status);
    }

    [Fact]
    public void A_mistyped_total_leaves_the_token_readings_out()
    {
        _home.CodexSession(0, "a",
            Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 1000),
            Fixtures.WeeklyOnly(Now.AddMinutes(-5), 41, Now.AddDays(2), 1500).Replace("\"total_tokens\":1500", "\"total_tokens\":\"1500\""));

        var reading = Read();

        Assert.Equal((null, null), (reading.TokensToday, reading.Tokens7d));
        Assert.Equal("Codex's log format changed (info.total_token_usage.total_tokens missing); InfoPanel may need an update", reading.Limits.Status);
        var entries = new CodexEntries();
        entries.Show(reading, Now, Fixtures.Zone);
        Assert.True(float.IsNaN(entries.TokensToday.Value));
    }

    [Fact]
    public void A_missing_info_keeps_the_tokens_out_through_later_events_with_no_counts_until_a_total_comes()
    {
        var path = _home.CodexSession(0, "a",
            Fixtures.WeeklyOnly(Now.AddMinutes(-30), 40, Now.AddDays(2), 1000),
            Fixtures.WeeklyOnly(Now.AddMinutes(-20), 41, Now.AddDays(2), 1500).Replace("\"info\":", "\"infos\":"),
            Fixtures.InfoNull(Now.AddMinutes(-10), 42, Now.AddDays(2)));
        var reader = new CodexReader(_home.Environment);

        var reading = Read(reader);
        // The total before the missing one is no longer the current one.
        Assert.Equal((null, null), (reading.TokensToday, reading.Tokens7d));
        Assert.Equal("Codex's log format changed (info missing); InfoPanel may need an update", reading.Limits.Status);
        Assert.Equal(42, reading.Limits.Weekly!.UsedPercent);

        // A valid total clears it.
        File.AppendAllText(path, Fixtures.WeeklyOnly(Now.AddMinutes(-5), 43, Now.AddDays(2), 1800) + "\n");
        reading = Read(reader);
        Assert.Equal((1800L, 1800L), (reading.TokensToday, reading.Tokens7d));
        Assert.Equal("Codex reports no five-hour window for this plan", reading.Limits.Status);
    }

    [Fact]
    public void A_passed_reset_reads_0_with_no_reset_time_until_the_window_is_seen_again()
    {
        _home.CodexSession(0, "a", Fixtures.Both(Now.AddHours(-6), 80, Now.AddHours(-1), 60, Now.AddDays(1), 100));
        var reading = Read();

        var entries = new CodexEntries();
        entries.Show(reading, Now, Fixtures.Zone);

        Assert.Equal(0, entries.FiveHourUsed.Value);
        Assert.True(float.IsNaN(entries.FiveHourResetIn.Value));
        Assert.Equal(60, entries.WeeklyUsed.Value);
        Assert.Equal(360, entries.AsOfAge.Value);
    }
}
