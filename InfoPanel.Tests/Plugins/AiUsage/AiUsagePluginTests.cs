using System.Collections.Concurrent;
using InfoPanel.AiUsage;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Host;
using InfoPanel.Plugins.Ipc;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;

namespace InfoPanel.Tests.Plugins.AiUsage;

// The plugin as its host runs it: its sets, its settings, and its readers on until a saved setting turns one off.
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

    /// <param name="saved">
    /// The settings the person saved, applied in the host's order: after Initialize and Load, before the first update
    /// (HostService.StartPluginsAsync; the host's own order is driven by the host-order tests below).
    /// </param>
    private (AiUsagePlugin Plugin, List<IPluginContainer> Containers) Loaded(Action<AiUsagePlugin>? saved = null)
    {
        var plugin = new AiUsagePlugin(_home.Environment);
        plugin.Initialize();
        var containers = new List<IPluginContainer>();
        plugin.Load(containers);
        saved?.Invoke(plugin);
        return (plugin, containers);
    }

    [Fact]
    public void A_fresh_plugin_with_no_saved_setting_reads_with_both_readers_on()
    {
        Populate();
        var (plugin, _) = Loaded();
        // Load reads nothing: the first read is the first update's, once the host has applied the saved settings.
        Assert.Equal((0, 0, 0, 0L), (_home.Files.Opens, _home.Files.Stats, _home.Files.FileLists, _home.Files.BytesRead));
        plugin.Update();

        Assert.True(_home.Files.Opens > 0);
        Assert.Equal(54, plugin.Codex.WeeklyUsed.Value);
        Assert.Equal("Codex reports no five-hour window for this plan", plugin.Codex.Status.Value);
        Assert.Equal((24f, 45f), (plugin.Claude.FiveHourUsed.Value, plugin.Claude.WeeklyUsed.Value));
    }

    // Deliberately changed: this test used to rely on the readers' default being off; it now saves both off, as the host
    // does for a setting the person saved (it applies the stored values after Load, before the first update).
    [Fact]
    public void With_both_readers_saved_off_it_opens_no_file_and_reads_off()
    {
        Populate();
        var (plugin, containers) = Loaded(p =>
        {
            p.ApplyConfig(AiUsagePlugin.ReadCodex, false);
            p.ApplyConfig(AiUsagePlugin.ReadClaudeCode, false);
        });
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
    public void A_saved_off_for_one_reader_keeps_that_reader_off_and_the_other_on()
    {
        Populate();
        var (codexOff, _) = Loaded(p => p.ApplyConfig(AiUsagePlugin.ReadCodex, false));
        codexOff.Update();
        Assert.True(float.IsNaN(codexOff.Codex.WeeklyUsed.Value));
        Assert.Equal("off", codexOff.Codex.Caption.Value);
        Assert.Equal(24, codexOff.Claude.FiveHourUsed.Value);

        var (claudeOff, _) = Loaded(p => p.ApplyConfig(AiUsagePlugin.ReadClaudeCode, false));
        claudeOff.Update();
        Assert.True(float.IsNaN(claudeOff.Claude.FiveHourUsed.Value));
        Assert.Equal("off: turn on Read Claude Code in AI usage's settings", claudeOff.Claude.Status.Value);
        Assert.Equal(54, claudeOff.Codex.WeeklyUsed.Value);
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
    public void Its_settings_are_two_switches_on_and_two_folders_and_a_wrong_value_is_refused()
    {
        var (plugin, _) = Loaded();

        Assert.Equal(
            [(AiUsagePlugin.ReadClaudeCode, PluginConfigType.Boolean, (object?)true), (AiUsagePlugin.ReadCodex, PluginConfigType.Boolean, true),
             (AiUsagePlugin.OtherClaudeFolders, PluginConfigType.String, ""), (AiUsagePlugin.CodexFolder, PluginConfigType.String, "")],
            plugin.ConfigProperties.Select(p => (p.Key, p.Type, p.Value)));
        Assert.Throws<ArgumentException>(() => plugin.ApplyConfig(AiUsagePlugin.ReadCodex, "yes"));
        Assert.Throws<ArgumentException>(() => plugin.ApplyConfig(AiUsagePlugin.CodexFolder, 3));
        Assert.Throws<ArgumentException>(() => plugin.ApplyConfig(AiUsagePlugin.CodexFolder, new string('a', AiUsagePlugin.MaxFolderSettingChars + 1)));
        Assert.Throws<ArgumentException>(() => plugin.ApplyConfig("nothing", true));
        Assert.Equal(true, plugin.ConfigProperties[1].Value);
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

    // The host's own order, with its saved settings file: the host builds the plugin, runs its Initialize and Load, applies
    // the saved settings, lists it, and only then starts its own task, whose first update is the first read. A reader saved
    // off opens, looks at and lists no file of its own from the start on, and no reading of it reaches the catalogue or an
    // update; the other reader reads on that first update. No timing is involved: the test waits for the first update's
    // read of Claude Code's file, which Codex's part of the same update comes before.
    [Fact]
    public async Task In_the_hosts_order_a_reader_saved_off_opens_no_file_and_publishes_no_reading_from_the_start()
    {
        var ct = TestContext.Current.CancellationToken;
        Populate();
        var settings = Path.Combine(_home.Path, "plugin-settings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, AiUsagePlugin.PluginId + ".json"), $$"""{"{{AiUsagePlugin.ReadCodex}}": false}""");
        var opened = new ConcurrentQueue<string>();
        var claudeRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _home.Files.BeforeOpen = opened.Enqueue;
        _home.Files.AfterOpen = path =>
        {
            if (Path.GetFileName(path) == LimitsReader.FileName)
                claudeRead.TrySetResult();
        };
        var host = new HostService(Path.Combine(_home.Path, "InfoPanel.AiUsage.dll"), settings);
        var transport = new AiUsageTransport();
        using var jsonRpc = new JsonRpc(transport);
        jsonRpc.StartListening();
        host.SetJsonRpc(jsonRpc);

        try
        {
            var listed = await host.StartPluginsAsync([new AiUsagePlugin(_home.Environment)], null);
            Assert.Equal([AiUsagePlugin.PluginId], listed.Select(p => p.Id));

            // The plugin's first update, on its own task, has read Claude Code (and so has done with Codex); the host's
            // cycle then sends what it shows.
            await claudeRead.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await host.RunUpdateCycleAsync([], ct);

            Assert.DoesNotContain(opened, p => p.StartsWith(_home.Codex, StringComparison.Ordinal));
            Assert.Equal(0, _home.Files.FileLists);
            Assert.All(transport.Catalogue.Where(e => e.Container == "codex" && e.Number.HasValue), e => Assert.True(float.IsNaN(e.Number!.Value), e.Entry));
            Assert.All(transport.Updates.Where(e => e.Container == "codex" && e.Number.HasValue), e => Assert.True(float.IsNaN(e.Number!.Value), e.Entry));
            Assert.Contains(transport.Updates, e => e is { Container: "codex", Entry: "status", Text: "off: turn on Read Codex in AI usage's settings" });
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // A fresh plugin in the host's order (no saved settings file): both readers on, both read on its first update.
    [Fact]
    public async Task In_the_hosts_order_a_fresh_plugin_reads_both_on_its_first_update()
    {
        var ct = TestContext.Current.CancellationToken;
        Populate();
        var settings = Path.Combine(_home.Path, "plugin-settings");
        var opened = new ConcurrentQueue<string>();
        var claudeRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _home.Files.BeforeOpen = opened.Enqueue;
        _home.Files.AfterOpen = path =>
        {
            if (Path.GetFileName(path) == LimitsReader.FileName)
                claudeRead.TrySetResult();
        };
        var host = new HostService(Path.Combine(_home.Path, "InfoPanel.AiUsage.dll"), settings);
        var plugin = new AiUsagePlugin(_home.Environment);

        try
        {
            await host.StartPluginsAsync([plugin], null);
            await claudeRead.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            Assert.Contains(opened, p => p.StartsWith(_home.Codex, StringComparison.Ordinal));
            Assert.Equal(54, plugin.Codex.WeeklyUsed.Value);
            Assert.Equal([true, true], plugin.ConfigProperties.Take(2).Select(p => p.Value));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    /// <summary>
    /// A JsonRpc transport that records AI usage's entries as the host lists them (OnPluginsLoaded) and as it updates them
    /// (OnSensorUpdate): container, entry, a sensor's number, a text's value. It ignores the rest.
    /// </summary>
    private sealed class AiUsageTransport : IJsonRpcMessageHandler
    {
        private readonly List<(string Container, string Entry, float? Number, string? Text)> _catalogue = [];
        private readonly List<(string Container, string Entry, float? Number, string? Text)> _updates = [];

        public bool CanRead => true;
        public bool CanWrite => true;
        public IJsonRpcMessageFormatter Formatter { get; } = new JsonMessageFormatter();

        public List<(string Container, string Entry, float? Number, string? Text)> Catalogue
        {
            get { lock (_catalogue) return [.. _catalogue]; }
        }

        public List<(string Container, string Entry, float? Number, string? Text)> Updates
        {
            get { lock (_updates) return [.. _updates]; }
        }

        public async ValueTask<JsonRpcMessage?> ReadAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }

        public ValueTask WriteAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            // The arguments are read as the host passed them, before anything serialises them.
            if (message is JsonRpcRequest { Method: nameof(IPluginClientCallback.OnPluginsLoaded) } loaded)
            {
                var containers = (Dictionary<string, List<ContainerDto>>)loaded.ArgumentsList![1]!;
                lock (_catalogue)
                {
                    foreach (var container in containers.GetValueOrDefault(AiUsagePlugin.PluginId) ?? [])
                    {
                        foreach (var entry in container.Entries)
                            _catalogue.Add((container.Id, entry.Id, entry.SensorValue?.Value, entry.TextValue));
                    }
                }
            }
            if (message is JsonRpcRequest { Method: nameof(IPluginClientCallback.OnSensorUpdate) } request)
            {
                lock (_updates)
                {
                    foreach (var batch in (IEnumerable<SensorUpdateBatchDto>)request.ArgumentsList![0]!)
                    {
                        if (batch.PluginId != AiUsagePlugin.PluginId)
                            continue;
                        foreach (var update in batch.Updates)
                            _updates.Add((update.ContainerId, update.EntryId, update.SensorValue?.Value, update.TextValue));
                    }
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}
