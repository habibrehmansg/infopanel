using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.AiUsage;
using InfoPanel.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Plugins.AiUsage;

// AI usage end to end: its build, as the engine bundles it, in a real plugin host whose home is a fixture home (HOME and
// USERPROFILE set for the host process alone, CLAUDE_CONFIG_DIR and CODEX_HOME taken away), with its readers turned on
// in the settings the engine keeps for it.
public sealed class AiUsageEngineTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Folder = "InfoPanel.AiUsage";

    private readonly string _root = PluginFixtures.NewTempRoot("ip-aiu-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    [Fact]
    public async Task The_bundled_plugin_runs_in_its_host_and_reads_a_fixture_home()
    {
        var home = Path.Combine(_root, "home");
        var now = DateTimeOffset.Now;
        var day = now.Date;
        var sessions = Path.Combine(home, ".codex", "sessions", day.ToString("yyyy", CultureInfo.InvariantCulture), day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, $"rollout-{day:yyyy-MM-dd}T00-00-00-fixture.jsonl"),
            Fixtures.SessionMeta(now.AddMinutes(-91)) + "\n" + Fixtures.WeeklyOnly(now.AddMinutes(-90), 54, now.AddDays(2), 12800) + "\n");
        var claude = Path.Combine(home, ".claude");
        Directory.CreateDirectory(Path.Combine(claude, "projects"));
        File.WriteAllText(Path.Combine(claude, LimitsReader.FileName), Fixtures.StatusLine(Fixtures.ClaudeBoth(24, now.AddHours(4), 45, now.AddDays(2))));

        var package = PluginFixtures.Copy(Folder, Path.Combine(_root, "bundled"));
        using var factory = new EngineFactory();
        var settings = Path.Combine(factory.DataDir, "plugin-settings", Folder);
        Directory.CreateDirectory(settings);
        await File.WriteAllTextAsync(Path.Combine(settings, AiUsagePlugin.PluginId + ".json"),
            JsonSerializer.Serialize(new Dictionary<string, bool> { [AiUsagePlugin.ReadClaudeCode] = true, [AiUsagePlugin.ReadCodex] = true }), Ct);
        using var engine = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:NoPlugins", "false");
            builder.UseSetting("Engine:BundledPlugins", Path.GetDirectoryName(package)!);
            builder.ConfigureTestServices(s => s.AddSingleton<IPluginProcessLauncher>(new HomeLauncher(home)));
        });
        using var client = engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);

        await Until(async () =>
        {
            var list = await client.GetFromJsonAsync<JsonElement>("/api/plugins", Ct);
            return list.GetProperty("packages").EnumerateArray().Any(p => p.GetProperty("folder").GetString() == Folder
                && p.GetProperty("state").GetString() == "running" && p.GetProperty("bundled").GetBoolean()
                && p.GetProperty("name").GetString() == "AI usage"
                && p.GetProperty("plugins").EnumerateArray().Any(q => q.GetProperty("id").GetString() == AiUsagePlugin.PluginId && q.GetProperty("configurable").GetBoolean()));
        }, "AI usage runs, bundled");

        await Until(async () =>
        {
            var values = await Values(client);
            return values.TryGetValue("plugin:/ai-usage/codex/weekly-used", out var weekly) && weekly.GetProperty("now").GetDouble() == 54
                && values.TryGetValue("plugin:/ai-usage/claude/five-hour-used", out var fiveHour) && fiveHour.GetProperty("now").GetDouble() == 24;
        }, "the fixture home's limits read");
        var read = await Values(client);
        Assert.Equal(45, read["plugin:/ai-usage/profile-.claude/weekly-used"].GetProperty("now").GetDouble());
        Assert.Equal(".claude", read["plugin:/ai-usage/claude/profile"].GetProperty("text").GetString());
        Assert.Equal("Codex reports no five-hour window for this plan", read["plugin:/ai-usage/codex/status"].GetProperty("text").GetString());
        Assert.StartsWith("as of ", read["plugin:/ai-usage/codex/caption"].GetProperty("text").GetString());
        // Codex has no five-hour window: no reading, so "–".
        var sensors = (await client.GetFromJsonAsync<JsonElement[]>("/api/sensors", Ct))!.ToDictionary(s => s.GetProperty("key").GetString()!);
        Assert.Equal("unavailable", sensors["plugin:/ai-usage/codex/five-hour-used"].GetProperty("status").GetString());
        Assert.Equal("AI usage › Codex", sensors["plugin:/ai-usage/codex/weekly-used"].GetProperty("group").GetString());
    }

    private static async Task<Dictionary<string, JsonElement>> Values(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement[]>("/api/sensors/values", Ct))!.ToDictionary(v => v.GetProperty("key").GetString()!);

    /// <summary>A sanity limit, as PluginEngineTests' (a host's start can stall for tens of seconds under full runs).</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);

    private static async Task Until(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"timed out waiting until {what}");
            await Task.Delay(100, Ct);
        }
    }

    /// <summary>
    /// Starts hosts as the engine does (<see cref="PluginProcessLauncher.EngineCommand"/>), with <paramref name="home"/> as
    /// the host's home and no Claude or Codex folder named by the environment, so it never sees the real ones.
    /// </summary>
    private sealed class HomeLauncher(string home) : IPluginProcessLauncher
    {
        public IPluginProcess Start(PluginHostCommand command)
        {
            var (file, prefix) = PluginProcessLauncher.EngineCommand();
            var start = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in prefix.Concat(command.Arguments))
                start.ArgumentList.Add(arg);
            start.Environment["HOME"] = home;
            start.Environment["USERPROFILE"] = home;
            start.Environment.Remove("CLAUDE_CONFIG_DIR");
            start.Environment.Remove("CODEX_HOME");
            return new HomeProcess(Process.Start(start)!);
        }
    }

    private sealed class HomeProcess : IPluginProcess
    {
        private readonly Process _process;

        public HomeProcess(Process process)
        {
            _process = process;
            Id = process.Id;
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            Exited = process.WaitForExitAsync();
        }

        public int Id { get; }

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public int? ExitCode => HasExited ? _process.ExitCode : null;

        public Task Exited { get; }

        public string ErrorTail => "";

        public long? MemoryBytes => null;

        public void Kill()
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }

        public void Dispose() => _process.Dispose();
    }
}
