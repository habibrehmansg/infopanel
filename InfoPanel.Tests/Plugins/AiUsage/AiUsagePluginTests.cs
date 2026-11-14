using InfoPanel.AiUsage;
using InfoPanel.Plugins;
using Xunit;

namespace InfoPanel.Tests.Plugins.AiUsage;

// The plugin as its host runs it: its sets, its settings, and its readers off until turned on.
public sealed class AiUsagePluginTests : IDisposable
{
    private readonly FixtureHome _home = new();
    private static DateTimeOffset Now => Fixtures.Now;

    public void Dispose() => _home.Dispose();

    /// <summary>A home with Codex's logs and two Claude folders, one connected.</summary>
    private void Populate()
    {
        _home.CodexSession(0, "a", Fixtures.WeeklyOnly(Now.AddMinutes(-90), 54, Now.AddDays(2), 12800));
        _home.Usage(_home.ClaudeFolder(".claude"), Fixtures.StatusLine(Fixtures.ClaudeBoth(24, Now.AddHours(4), 45, Now.AddDays(2))), Now.AddMinutes(-1));
        _home.ClaudeFolder(".claude-work");
    }

    private (AiUsagePlugin Plugin, List<IPluginContainer> Containers) Loaded()
    {
        var plugin = new AiUsagePlugin(_home.Environment);
        plugin.Initialize();
        var containers = new List<IPluginContainer>();
        plugin.Load(containers);
        return (plugin, containers);
    }

    [Fact]
    public void With_both_readers_off_it_opens_no_file_and_reads_off()
    {
        Populate();
        var (plugin, containers) = Loaded();
        for (var round = 0; round < 3; round++)
        {
            plugin.Update();
            _home.Clock.Now = _home.Clock.Now.AddSeconds(5);
        }

        // Folders were looked at when it loaded (which Claude folders there are); no file was opened, looked at or listed.
        Assert.Equal((0, 0, 0, 0L), (_home.Files.Opens, _home.Files.Stats, _home.Files.FileLists, _home.Files.BytesRead));
        Assert.Equal("off: turn on Read Codex in AI usage's settings", plugin.Codex.Status.Value);
        Assert.Equal("off: turn on Read Claude Code in AI usage's settings", plugin.Claude.Status.Value);
        Assert.Equal("off", plugin.Codex.Caption.Value);
        Assert.All(containers.SelectMany(c => c.Entries).OfType<PluginSensor>(), s => Assert.True(float.IsNaN(s.Value), s.Id));

        // Turned on, it reads (and so goes through the files the test counts).
        plugin.ApplyConfig(AiUsagePlugin.ReadCodex, true);
        plugin.Update();
        Assert.True(_home.Files.Opens > 0);
        Assert.Equal(54, plugin.Codex.WeeklyUsed.Value);
    }

    [Fact]
    public void It_lists_Codex_Claude_Code_and_each_Claude_folder_with_their_entries()
    {
        Populate();
        var (plugin, containers) = Loaded();

        Assert.Equal(["codex", "claude", "profile-.claude", "profile-.claude-work"], containers.Select(c => c.Id));
        Assert.Equal(["Codex", "Claude Code", "Claude Code · .claude", "Claude Code · .claude-work"], containers.Select(c => c.Name));
        Assert.Equal(
            ["five-hour-used", "weekly-used", "five-hour-reset-in", "weekly-reset-in", "caption", "as-of-age", "status", "credits", "plan", "tokens-today", "tokens-7d"],
            containers[0].Entries.Select(e => e.Id));
        Assert.Equal(["five-hour-used", "weekly-used", "five-hour-reset-in", "weekly-reset-in", "caption", "as-of-age", "status", "profile"], containers[1].Entries.Select(e => e.Id));
        Assert.Equal(["five-hour-used", "weekly-used", "five-hour-reset-in", "weekly-reset-in", "caption", "as-of-age", "status"], containers[2].Entries.Select(e => e.Id));
        Assert.Equal(("ai-usage", "AI usage", TimeSpan.FromSeconds(5)), (plugin.Id, plugin.Name, plugin.UpdateInterval));
    }

    [Fact]
    public void Both_readers_on_read_Codex_and_Claude_Code()
    {
        Populate();
        var (plugin, _) = Loaded();
        plugin.ApplyConfig(AiUsagePlugin.ReadCodex, true);
        plugin.ApplyConfig(AiUsagePlugin.ReadClaudeCode, true);
        plugin.Update();

        Assert.Equal(54, plugin.Codex.WeeklyUsed.Value);
        Assert.True(float.IsNaN(plugin.Codex.FiveHourUsed.Value));
        Assert.Equal("Codex reports no five-hour window for this plan", plugin.Codex.Status.Value);
        Assert.Equal("as of Tue 08:30", plugin.Codex.Caption.Value);
        Assert.Equal(("none", "prolite", 12800f), (plugin.Codex.Credits.Value, plugin.Codex.Plan.Value, plugin.Codex.TokensToday.Value));

        Assert.Equal((24f, 45f, ".claude"), (plugin.Claude.FiveHourUsed.Value, plugin.Claude.WeeklyUsed.Value, plugin.Claude.Profile.Value));
        Assert.Equal(24, plugin.ClaudeFolderEntries["profile-.claude"].FiveHourUsed.Value);
        Assert.Equal(LimitsReader.NotConnected, plugin.ClaudeFolderEntries["profile-.claude-work"].Status.Value);
        Assert.True(float.IsNaN(plugin.ClaudeFolderEntries["profile-.claude-work"].FiveHourUsed.Value));

        // Off again: nothing more is read, and the readings go.
        plugin.ApplyConfig(AiUsagePlugin.ReadCodex, false);
        plugin.ApplyConfig(AiUsagePlugin.ReadClaudeCode, false);
        var (opens, stats) = (_home.Files.Opens, _home.Files.Stats);
        plugin.Update();
        Assert.Equal((opens, stats), (_home.Files.Opens, _home.Files.Stats));
        Assert.True(float.IsNaN(plugin.Codex.WeeklyUsed.Value));
        Assert.True(float.IsNaN(plugin.Claude.FiveHourUsed.Value));
    }

    [Fact]
    public void Its_settings_are_two_switches_off_and_two_folders_and_a_wrong_value_is_refused()
    {
        var (plugin, _) = Loaded();

        Assert.Equal(
            [(AiUsagePlugin.ReadClaudeCode, PluginConfigType.Boolean, (object?)false), (AiUsagePlugin.ReadCodex, PluginConfigType.Boolean, false),
             (AiUsagePlugin.OtherClaudeFolders, PluginConfigType.String, ""), (AiUsagePlugin.CodexFolder, PluginConfigType.String, "")],
            plugin.ConfigProperties.Select(p => (p.Key, p.Type, p.Value)));
        Assert.Throws<ArgumentException>(() => plugin.ApplyConfig(AiUsagePlugin.ReadCodex, "yes"));
        Assert.Throws<ArgumentException>(() => plugin.ApplyConfig(AiUsagePlugin.CodexFolder, 3));
        Assert.Throws<ArgumentException>(() => plugin.ApplyConfig(AiUsagePlugin.CodexFolder, new string('a', AiUsagePlugin.MaxFolderSettingChars + 1)));
        Assert.Throws<ArgumentException>(() => plugin.ApplyConfig("nothing", true));
        Assert.Equal(false, plugin.ConfigProperties[1].Value);
    }

    [Fact]
    public void The_folder_settings_point_the_readers_elsewhere()
    {
        var (plugin, _) = Loaded();
        var codex = Path.Combine(_home.Path, "elsewhere", "codex");
        var claude = Path.Combine(_home.Path, "elsewhere", "claude");
        Directory.CreateDirectory(Path.Combine(claude, "projects"));
        _home.Usage(claude, Fixtures.StatusLine(Fixtures.ClaudeBoth(12, Now.AddHours(1), 34, Now.AddDays(1))), Now);
        var day = Path.Combine(codex, "sessions", "2026", "11", "10");
        Directory.CreateDirectory(day);
        File.WriteAllText(Path.Combine(day, "rollout-2026-11-10T09-00-00-x.jsonl"), Fixtures.WeeklyOnly(Now.AddMinutes(-1), 77, Now.AddDays(1), 10) + "\n");

        plugin.ApplyConfig(AiUsagePlugin.ReadCodex, true);
        plugin.ApplyConfig(AiUsagePlugin.ReadClaudeCode, true);
        plugin.ApplyConfig(AiUsagePlugin.CodexFolder, "~/elsewhere/codex");
        plugin.ApplyConfig(AiUsagePlugin.OtherClaudeFolders, " ; ~/elsewhere/claude ;");
        plugin.Update();

        Assert.Equal(77, plugin.Codex.WeeklyUsed.Value);
        Assert.Equal((12f, "elsewhere/claude"), (plugin.Claude.FiveHourUsed.Value, plugin.Claude.Profile.Value));
    }

    [Fact]
    public void A_found_folder_and_another_folder_of_the_same_name_keep_their_own_readings()
    {
        // ~/.claude, found, and a .claude outside the home that Other Claude folders names: both are ".claude".
        _home.Usage(_home.ClaudeFolder(".claude"), Fixtures.StatusLine(Fixtures.ClaudeBoth(24, Now.AddHours(4), 45, Now.AddDays(2))), Now.AddMinutes(-10));
        var outside = PluginFixtures.NewTempRoot("ip-aiusage-backup-");
        try
        {
            var backup = Path.Combine(outside, ".claude");
            Directory.CreateDirectory(Path.Combine(backup, "projects"));
            _home.Usage(backup, Fixtures.StatusLine(Fixtures.ClaudeBoth(88, Now.AddHours(4), 99, Now.AddDays(2))), Now.AddMinutes(-1));
            var (plugin, containers) = Loaded();
            plugin.ApplyConfig(AiUsagePlugin.ReadClaudeCode, true);
            plugin.ApplyConfig(AiUsagePlugin.OtherClaudeFolders, backup);
            plugin.Update();

            Assert.Equal(["codex", "claude", "profile-.claude"], containers.Select(c => c.Id));
            Assert.Equal(24, plugin.ClaudeFolderEntries["profile-.claude"].FiveHourUsed.Value);
            // The combined set follows the one written last, the other folder, by its own reading.
            Assert.Equal(88, plugin.Claude.FiveHourUsed.Value);
            var ids = Folders.OtherClaude(_home.Environment, backup, plugin.ClaudeFolders).Select(f => f.ContainerId);
            Assert.DoesNotContain("profile-.claude", ids);
        }
        finally
        {
            PluginFixtures.DeleteQuietly(outside);
        }
    }
}
