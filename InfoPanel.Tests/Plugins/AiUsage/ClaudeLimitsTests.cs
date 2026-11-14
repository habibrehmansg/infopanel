using InfoPanel.AiUsage;
using Xunit;

namespace InfoPanel.Tests.Plugins.AiUsage;

// Claude Code's limits from the usage file its connected status line writes in each Claude folder, on hand-written
// fixtures of its status line input.
public sealed class ClaudeLimitsTests : IDisposable
{
    private readonly FixtureHome _home = new();
    private static DateTimeOffset Now => Fixtures.Now;

    public void Dispose() => _home.Dispose();

    private ClaudeFolder Folder(string name) =>
        Assert.Single(Folders.FindClaude(_home.Environment), f => f.Path == Path.GetFullPath(Path.Combine(_home.Path, name)));

    [Fact]
    public void Both_windows_read_with_their_resets_and_age()
    {
        var path = _home.ClaudeFolder(".claude");
        _home.Usage(path, Fixtures.StatusLine(Fixtures.ClaudeBoth(24, Now.AddHours(4).AddMinutes(30), 45, Now.AddDays(2).AddHours(-1))), Now.AddMinutes(-2));
        var folder = Folder(".claude");

        var reading = new LimitsReader(_home.Environment).Read([folder]);

        Assert.Equal((new Window(24, Now.AddHours(4).AddMinutes(30)), new Window(45, Now.AddDays(2).AddHours(-1))), (reading.Combined.FiveHour, reading.Combined.Weekly));
        Assert.Equal(LimitsReading.Ok, reading.Combined.Status);
        Assert.Equal(".claude", reading.Profile);
        Assert.Same(reading.Combined, reading.Folders[folder.Path]);

        var entries = new ClaudeEntries();
        entries.Show(reading.Combined, Now, Fixtures.Zone);
        Assert.Equal((24f, 45f, 270f, 2f), (entries.FiveHourUsed.Value, entries.WeeklyUsed.Value, entries.FiveHourResetIn.Value, entries.AsOfAge.Value));
        Assert.Equal("resets 14:30 · wk Thu 09:00", entries.Caption.Value);
    }

    [Fact]
    public void No_plan_limits_read_nothing_and_say_so()
    {
        var path = _home.ClaudeFolder(".claude");
        _home.Usage(path, Fixtures.StatusLine("null"), Now.AddMinutes(-1));

        var reading = new LimitsReader(_home.Environment).Read([Folder(".claude")]);

        Assert.Equal((null, null), (reading.Combined.FiveHour, reading.Combined.Weekly));
        Assert.Equal("no plan limits (an API key, Bedrock or Vertex)", reading.Combined.Status);
    }

    [Fact]
    public void A_window_left_out_reads_nothing_and_says_which()
    {
        var path = _home.ClaudeFolder(".claude");
        _home.Usage(path, Fixtures.StatusLine(Fixtures.ClaudeFiveHourOnly(30, Now.AddHours(2))), Now.AddMinutes(-1));

        var reading = new LimitsReader(_home.Environment).Read([Folder(".claude")]);

        Assert.Equal(new Window(30, Now.AddHours(2)), reading.Combined.FiveHour);
        Assert.Null(reading.Combined.Weekly);
        Assert.Equal("Claude Code's status line gave no weekly window", reading.Combined.Status);
        var entries = new LimitEntries();
        entries.Show(reading.Combined, Now, Fixtures.Zone);
        Assert.True(float.IsNaN(entries.WeeklyUsed.Value));
        Assert.Equal("resets 12:00", entries.Caption.Value);
    }

    [Fact]
    public void A_session_without_limits_yet_keeps_the_last_ones()
    {
        var path = _home.ClaudeFolder(".claude");
        var reader = new LimitsReader(_home.Environment);
        var folder = Folder(".claude");
        _home.Usage(path, Fixtures.StatusLine(null), Now.AddMinutes(-3));
        Assert.Equal("no limits yet: Claude Code gives them after a session's first answer", reader.Read([folder]).Combined.Status);

        _home.Usage(path, Fixtures.StatusLine(Fixtures.ClaudeBoth(24, Now.AddHours(4), 45, Now.AddDays(2))), Now.AddMinutes(-2));
        Assert.Equal(24, reader.Read([folder]).Combined.FiveHour!.UsedPercent);
        _home.Usage(path, Fixtures.StatusLine(null), Now.AddMinutes(-1));
        var reading = reader.Read([folder]);
        Assert.Equal(24, reading.Combined.FiveHour!.UsedPercent);
        Assert.Equal(Now.AddMinutes(-2), reading.Combined.AsOf);
    }

    [Fact]
    public void A_torn_file_keeps_the_last_good_reading_and_reads_format_changed_only_after_failing_for_2_minutes_as_it_changes()
    {
        var path = _home.ClaudeFolder(".claude");
        var reader = new LimitsReader(_home.Environment);
        var folder = Folder(".claude");
        var good = Fixtures.StatusLine(Fixtures.ClaudeBoth(24, Now.AddHours(4), 45, Now.AddDays(2)));
        _home.Usage(path, good, Now.AddMinutes(-1));
        Assert.Equal(LimitsReading.Ok, reader.Read([folder]).Combined.Status);

        // Caught mid-write: the last good reading stays.
        _home.Usage(path, good[..(good.Length / 2)], Now);
        var torn = reader.Read([folder]).Combined;
        Assert.Equal((LimitsReading.Ok, 24d), (torn.Status, torn.FiveHour!.UsedPercent));

        // Still failing a minute on: the same; past 2 minutes, still failing and still changing: format changed.
        _home.Clock.Now = Now.AddMinutes(1);
        _home.Usage(path, good[..(good.Length / 3)], Now.AddMinutes(1));
        Assert.Equal(LimitsReading.Ok, reader.Read([folder]).Combined.Status);
        _home.Clock.Now = Now.AddMinutes(3);
        _home.Usage(path, good[..(good.Length / 4)], Now.AddMinutes(3));
        var changed = reader.Read([folder]).Combined;
        Assert.Equal("Claude Code's usage file format changed (it doesn't parse); InfoPanel may need an update", changed.Status);
        Assert.Null(changed.FiveHour);

        // Whole again: it reads.
        _home.Usage(path, good, Now.AddMinutes(3).AddSeconds(5));
        Assert.Equal(LimitsReading.Ok, reader.Read([folder]).Combined.Status);
    }

    [Fact]
    public void A_mistyped_window_reads_format_changed_naming_the_field()
    {
        var path = _home.ClaudeFolder(".claude");
        _home.Usage(path, Fixtures.StatusLine($$$"""{"five_hour":{"used_percentage":"24","resets_at":{{{Fixtures.Epoch(Now.AddHours(1))}}}}}"""), Now);

        var reading = new LimitsReader(_home.Environment).Read([Folder(".claude")]);

        Assert.Equal("Claude Code's usage file format changed (rate_limits.five_hour.used_percentage missing); InfoPanel may need an update", reading.Combined.Status);
    }

    [Fact]
    public void A_passed_reset_reads_0_with_no_reset_time()
    {
        var path = _home.ClaudeFolder(".claude");
        _home.Usage(path, Fixtures.StatusLine(Fixtures.ClaudeBoth(90, Now.AddMinutes(-5), 45, Now.AddDays(2))), Now.AddMinutes(-20));

        var reading = new LimitsReader(_home.Environment).Read([Folder(".claude")]);
        var entries = new LimitEntries();
        entries.Show(reading.Combined, Now, Fixtures.Zone);

        Assert.Equal(0, entries.FiveHourUsed.Value);
        Assert.True(float.IsNaN(entries.FiveHourResetIn.Value));
        Assert.Equal(45, entries.WeeklyUsed.Value);
        Assert.Equal("wk resets Thu 10:00", entries.Caption.Value);
    }

    [Fact]
    public void Several_folders_each_read_and_the_combined_set_follows_the_one_written_last()
    {
        var main = _home.ClaudeFolder(".claude");
        var work = _home.ClaudeFolder(".claude-work");
        _home.ClaudeFolder(".claude-idle");
        // Not a Claude folder: no projects/.
        Directory.CreateDirectory(Path.Combine(_home.Path, ".claude-settings"));
        Directory.CreateDirectory(Path.Combine(_home.Path, ".config", "claude", "projects"));
        var elsewhere = Path.Combine(_home.Path, "elsewhere", "claude-config");
        Directory.CreateDirectory(elsewhere);
        _home.Variables["CLAUDE_CONFIG_DIR"] = elsewhere;
        _home.Usage(main, Fixtures.StatusLine(Fixtures.ClaudeBoth(10, Now.AddHours(1), 20, Now.AddDays(1))), Now.AddMinutes(-10));
        _home.Usage(work, Fixtures.StatusLine(Fixtures.ClaudeBoth(30, Now.AddHours(2), 60, Now.AddDays(3))), Now.AddMinutes(-1));

        var folders = Folders.FindClaude(_home.Environment);

        Assert.Equal(
            [("profile-.claude", ".claude"), ("profile-.claude-idle", ".claude-idle"), ("profile-.claude-work", ".claude-work"), ("profile-.config-claude", ".config/claude"), ("profile-elsewhere-claude-config", "elsewhere/claude-config")],
            folders.Select(f => (f.ContainerId, f.Name)).Order());
        var reading = new LimitsReader(_home.Environment).Read(folders);
        Assert.Equal(".claude-work", reading.Profile);
        Assert.Equal(30, reading.Combined.FiveHour!.UsedPercent);
        Assert.Equal(10, reading.Folders[main].FiveHour!.UsedPercent);
        Assert.Equal(LimitsReader.NotConnected, reading.Folders[Path.Combine(_home.Path, ".claude-idle")].Status);

        // The other one writes: the combined set follows it.
        _home.Usage(main, Fixtures.StatusLine(Fixtures.ClaudeBoth(11, Now.AddHours(1), 21, Now.AddDays(1))), Now);
        reading = new LimitsReader(_home.Environment).Read(folders);
        Assert.Equal((".claude", 11d), (reading.Profile, reading.Combined.FiveHour!.UsedPercent));
    }

    [Fact]
    public void No_usage_file_anywhere_reads_not_connected()
    {
        _home.ClaudeFolder(".claude");

        var reading = new LimitsReader(_home.Environment).Read(Folders.FindClaude(_home.Environment));

        Assert.Equal((LimitsReader.NotConnected, ""), (reading.Combined.Status, reading.Profile));
        Assert.Equal("Not connected: Connect Claude Code limits in AI usage", reading.Combined.Status);
    }

    // ---- The usage file: a regular file in the folder, opened without following a link or waiting, read to 1 MiB.

    /// <summary>A Claude folder with a usage file; the file's path.</summary>
    private string Connected(double fiveHour)
    {
        var path = _home.ClaudeFolder(".claude");
        _home.Usage(path, Fixtures.StatusLine(Fixtures.ClaudeBoth(fiveHour, Now.AddHours(4), 45, Now.AddDays(2))), Now.AddMinutes(-1));
        return Path.Combine(path, LimitsReader.FileName);
    }

    [Fact]
    public void A_usage_file_swapped_for_a_link_between_its_check_and_its_open_is_not_followed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Making a link on Windows needs a privilege the tests don't have.");
        var file = Connected(24);
        var secret = Path.Combine(_home.Path, "secret.json");
        File.WriteAllText(secret, Fixtures.StatusLine(Fixtures.ClaudeBoth(99, Now.AddHours(4), 99, Now.AddDays(2))));
        var swapped = false;
        _home.Files.BeforeOpen = p =>
        {
            if (p == file && !swapped)
            {
                swapped = true;
                File.Delete(p);
                File.CreateSymbolicLink(p, secret);
            }
        };

        var reading = new LimitsReader(_home.Environment).Read([Folder(".claude")]);

        Assert.NotEqual(99d, reading.Combined.FiveHour?.UsedPercent);
        Assert.Equal(0, _home.Files.BytesRead);
    }

    [Fact]
    public async Task A_usage_file_swapped_for_a_FIFO_between_its_check_and_its_open_is_refused_without_waiting()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no FIFOs.");
        var file = Connected(24);
        var swapped = false;
        _home.Files.BeforeOpen = p =>
        {
            if (p == file && !swapped)
            {
                swapped = true;
                File.Delete(p);
                Fifo.Make(p);
            }
        };
        var folder = Folder(".claude");

        var ct = TestContext.Current.CancellationToken;
        var read = Task.Run(() => new LimitsReader(_home.Environment).Read([folder]), ct);
        try
        {
            Assert.True(await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10), ct)) == read, "the read waited on the FIFO");
        }
        finally
        {
            Fifo.Release(file);
        }
        Assert.Null((await read).Combined.FiveHour);
    }

    [Fact]
    public void A_usage_file_that_grows_past_1_MiB_after_its_open_is_refused_without_reading_past_the_limit()
    {
        var file = Connected(24);
        var big = "{\"padding\":\"" + new string('x', 2 * LimitsReader.MaxFileBytes) + "\","
            + Fixtures.StatusLine(Fixtures.ClaudeBoth(77, Now.AddHours(4), 45, Now.AddDays(2)))[1..];
        long? stampLength = null;
        // It grows once its handle has told its size (small), so only the limit on the read itself holds it.
        _home.Files.AfterOpen = p =>
        {
            if (p == file && stampLength == null)
            {
                stampLength = new FileInfo(p).Length;
                File.WriteAllText(p, big);
            }
        };

        var reading = new LimitsReader(_home.Environment).Read([Folder(".claude")]);

        Assert.True(stampLength < LimitsReader.MaxFileBytes, $"the handle's size was {stampLength}");
        Assert.NotEqual(77d, reading.Combined.FiveHour?.UsedPercent);
        Assert.True(_home.Files.BytesRead <= LimitsReader.MaxFileBytes + 1, $"{_home.Files.BytesRead} bytes read");
        Assert.Equal("Claude Code's usage file format changed (the file is larger than 1 MiB); InfoPanel may need an update", reading.Combined.Status);
    }

    // ---- Finding the Claude folders: bounded however many entries the home folder holds.

    [Fact]
    public void A_home_of_many_Claude_folders_is_listed_within_its_entries_and_says_so()
    {
        for (var i = 0; i < 120; i++)
            _home.ClaudeFolder($".claude-{i:D3}");

        var folders = Folders.FindClaude(_home.Environment, 50, out var truncated);

        Assert.True(_home.Files.Entries <= 50, $"{_home.Files.Entries} entries looked at");
        // The listing, projects/ in no more folders than are given readings, and ~/.config/claude.
        Assert.True(_home.Files.DirectoryLooks <= Folders.MaxClaudeFolders + 2, $"{_home.Files.DirectoryLooks} folders looked at");
        Assert.Equal(Folders.MaxClaudeFolders, folders.Count);
        Assert.True(truncated);
    }

    [Fact]
    public void A_home_that_takes_too_long_to_list_stops_and_says_so()
    {
        for (var i = 0; i < 120; i++)
            _home.ClaudeFolder($".claude-{i:D3}");
        // Each entry looked at takes 100 ms.
        _home.Files.OnEntry = _ => _home.Clock.Now = _home.Clock.Now.AddMilliseconds(100);

        var folders = Folders.FindClaude(_home.Environment, Folders.MaxHomeEntries, out var truncated);

        Assert.True(_home.Clock.Now - Now <= Folders.MaxFindTime + TimeSpan.FromMilliseconds(100), $"{(_home.Clock.Now - Now).TotalSeconds} s spent");
        Assert.True(truncated);
        Assert.True(folders.Count <= Folders.MaxClaudeFolders);
    }

    [Fact]
    public void A_home_listed_whole_is_not_truncated()
    {
        _home.ClaudeFolder(".claude");
        _home.ClaudeFolder(".claude-work");

        var folders = Folders.FindClaude(_home.Environment, Folders.MaxHomeEntries, out var truncated);

        Assert.Equal(2, folders.Count);
        Assert.False(truncated);
    }

    // ---- Container ids: bounded, and unique however long or alike the folders' names.

    [Fact]
    public void A_long_folder_name_gives_an_id_within_the_limit_and_two_such_names_stay_apart()
    {
        // At the limit: kept as it is.
        var fits = ".claude-" + new string('a', Folders.MaxContainerIdChars - "profile-.claude-".Length - 4);
        // Past it (a name of 250 characters, as a file system allows): cut, with a hash of the whole name.
        var longA = ".claude-" + new string('b', 241) + "1";
        var longB = ".claude-" + new string('b', 241) + "2";
        foreach (var name in new[] { fits, longA, longB })
            _home.ClaudeFolder(name);

        var folders = Folders.FindClaude(_home.Environment);

        Assert.Equal(3, folders.Count);
        Assert.All(folders, f => Assert.True(f.ContainerId.Length <= Folders.MaxContainerIdChars, $"{f.ContainerId.Length} characters"));
        Assert.Equal(3, folders.Select(f => f.ContainerId).Distinct().Count());
        Assert.Contains(folders, f => f.ContainerId == "profile-" + fits);
        // Stable: the same folders, the same ids.
        Assert.Equal(folders.Select(f => f.ContainerId), Folders.FindClaude(_home.Environment).Select(f => f.ContainerId));
    }
}
