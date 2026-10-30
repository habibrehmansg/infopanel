using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using InfoPanel.Plugins.Host;
using InfoPanel.Plugins.Ipc;
using StreamJsonRpc;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The built engine started with --plugin-host is a plugin host: it connects to the pipe it is given, loads the plugin
// DLL, answers the host service and calls back, and writes its log and the plugin's settings only in the folders it is
// given (never in 1.x's per-user InfoPanel folder). Each test is the engine as a child process against a pipe the test
// serves, with the test plugin, and a home folder of its own, so "nothing written there" is checked on a clean folder.
public sealed class PluginHostModeTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = PluginFixtures.NewTempRoot("ip-hm-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    [Fact]
    public async Task The_engine_in_host_mode_connects_initialises_and_reports_the_plugins_entries()
    {
        await using var run = await HostRun.StartAsync(_root, []);

        var metadata = await run.InitializeAsync();

        Assert.Contains(metadata, m => m.Id == PluginFixtures.FixturePluginId && m.IsConfigurable);
        var (_, containers) = await run.Callback.Loaded.Task.WaitAsync(Bound, Ct);
        var fixture = containers[PluginFixtures.FixturePluginId];
        var readings = Assert.Single(fixture, c => c.Id == "readings");
        Assert.Equal(["counter", "status", "processes"], readings.Entries.Select(e => e.Id).ToArray());
        Assert.Equal(["sensor", "text", "table"], readings.Entries.Select(e => e.Type).ToArray());
        var ephemeral = Assert.Single(fixture, c => c.Id == "eph");
        Assert.True(ephemeral.IsEphemeralPath);
        Assert.Equal("eph-value", Assert.Single(ephemeral.Entries).Id);
    }

    [Fact]
    public async Task The_host_writes_its_log_and_the_plugins_settings_only_in_the_folders_it_is_given()
    {
        await using var run = await HostRun.StartAsync(_root, []);
        await run.InitializeAsync();

        await run.Rpc.InvokeWithCancellationAsync<List<PluginConfigPropertyDto>>(
            nameof(IPluginHostService.ApplyConfigAsync), [PluginFixtures.FixturePluginId, "label", "changed"], Ct);
        await run.StopAsync();

        var settings = Path.Combine(run.SettingsDir, PluginFixtures.FixturePluginId + ".json");
        Assert.Contains("changed", await File.ReadAllTextAsync(settings, Ct));
        Assert.Empty(Directory.GetFiles(run.SettingsDir, "*.tmp"));
        Assert.Single(Directory.GetFiles(run.LogDir, PluginFixtures.FixtureFolder + "-*.log"));

        // No InfoPanel folder anywhere under the home folder: not 1.x's (LocalApplicationData/InfoPanel on every OS) nor any.
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(run.Home, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains("InfoPanel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_settings_change_logs_its_key_and_never_its_value()
    {
        var secret = "secret-" + RandomNumberGenerator.GetHexString(24, lowercase: true);
        await using var run = await HostRun.StartAsync(_root, [], logLevel: "debug");
        await run.InitializeAsync();

        await run.Rpc.InvokeWithCancellationAsync<List<PluginConfigPropertyDto>>(
            nameof(IPluginHostService.ApplyConfigAsync), [PluginFixtures.FixturePluginId, "apiKey", secret], Ct);
        await run.StopAsync();

        var log = string.Concat(Directory.GetFiles(run.LogDir).Select(File.ReadAllText));
        Assert.Contains("apiKey", log);
        Assert.DoesNotContain(secret, log);
        // The settings file holds it: that is where it belongs.
        Assert.Contains(secret, await File.ReadAllTextAsync(Path.Combine(run.SettingsDir, PluginFixtures.FixturePluginId + ".json"), Ct));
    }

    [Fact]
    public async Task When_its_pipe_ends_the_host_exits_within_5_seconds_even_with_a_hanging_plugin()
    {
        await using var run = await HostRun.StartAsync(_root, ["hang"]);
        await run.InitializeAsync();

        var clock = Stopwatch.StartNew();
        run.ClosePipe();
        var exited = await run.WaitForExitAsync(TimeSpan.FromSeconds(5));

        Assert.True(exited, $"the host was still running {clock.Elapsed} after its pipe ended; it said: {run.Output}");
    }

    [Fact]
    public async Task When_its_pipe_ends_the_host_exits_even_when_a_plugins_exit_handler_hangs()
    {
        await using var run = await HostRun.StartAsync(_root, ["hang-exit-handler"]);
        await run.InitializeAsync();

        var clock = Stopwatch.StartNew();
        run.ClosePipe();
        // The deadline is 5 s after the pipe ended; the margin is for a loaded machine, not for the exit handler.
        var exited = await run.WaitForExitAsync(PluginHostEntry.ExitDeadline + TimeSpan.FromSeconds(3));

        Assert.True(exited, $"the host was still running {clock.Elapsed} after its pipe ended; it said: {run.Output}");
    }

    [Fact]
    public void The_host_library_has_no_top_level_Program()
    {
        var assembly = typeof(HostService).Assembly;

        Assert.Null(assembly.GetType("Program"));
        Assert.NotNull(assembly.GetType("InfoPanel.Plugins.Host.PluginHostEntry"));
    }

    /// <summary>The engine as a plugin host, connected to a pipe this test serves, with its own home, log and settings folders.</summary>
    private sealed class HostRun : IAsyncDisposable
    {
        private readonly StringBuilder _output = new();

        private HostRun(Process process, NamedPipeServerStream pipe, string root)
        {
            Process = process;
            Pipe = pipe;
            Home = Path.Combine(root, "home");
            LogDir = Path.Combine(root, "logs");
            SettingsDir = Path.Combine(root, "settings");
        }

        public Process Process { get; }
        public NamedPipeServerStream Pipe { get; }
        public JsonRpc Rpc { get; private set; } = null!;
        public RecordingCallback Callback { get; } = new();
        public string Home { get; }
        public string LogDir { get; }
        public string SettingsDir { get; }

        public string Output
        {
            get
            {
                lock (_output)
                    return _output.ToString();
            }
        }

        public static async Task<HostRun> StartAsync(string root, string[] mode, string logLevel = "information")
        {
            var package = PluginFixtures.CopyFixture(Path.Combine(root, "plugins"), mode);
            var pipeName = "ipt-" + RandomNumberGenerator.GetHexString(16, lowercase: true);
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var home = Path.Combine(root, "home");
            Directory.CreateDirectory(home);
            var start = new ProcessStartInfo(PluginFixtures.EnginePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[]
            {
                "--plugin-host", "--pipe", pipeName, "--plugin", PluginFixtures.DllOf(package),
                "--log-dir", Path.Combine(root, "logs"), "--settings-dir", Path.Combine(root, "settings"), "--log-level", logLevel,
            })
            {
                start.ArgumentList.Add(arg);
            }
            // 1.x's folder is LocalApplicationData/InfoPanel: under HOME on macOS and Linux, LOCALAPPDATA on Windows.
            start.Environment["HOME"] = home;
            start.Environment["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local");
            start.Environment["XDG_DATA_HOME"] = Path.Combine(home, ".local", "share");

            var process = Process.Start(start)!;
            var run = new HostRun(process, pipe, root);
            process.OutputDataReceived += (_, e) => run.Said(e.Data);
            process.ErrorDataReceived += (_, e) => run.Said(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var connected = pipe.WaitForConnectionAsync(Ct);
            var first = await Task.WhenAny(connected, process.WaitForExitAsync(Ct)).WaitAsync(Bound, Ct);
            Assert.True(first == connected, $"the host didn't connect (exit {(process.HasExited ? process.ExitCode : -1)}): {run.Output}");
            await connected;

            run.Rpc = new JsonRpc(pipe);
            run.Rpc.AddLocalRpcTarget<IPluginClientCallback>(run.Callback, null);
            run.Rpc.StartListening();
            return run;
        }

        private void Said(string? line)
        {
            if (line == null) return;
            lock (_output)
                _output.AppendLine(line);
        }

        public Task<List<PluginMetadataDto>> InitializeAsync() =>
            Rpc.InvokeWithCancellationAsync<List<PluginMetadataDto>>(nameof(IPluginHostService.InitializeAsync), null, Ct).WaitAsync(Bound, Ct);

        /// <summary>Asks the host to shut down, as the engine does, and waits for it to exit.</summary>
        public async Task StopAsync()
        {
            try
            {
                await Rpc.InvokeWithCancellationAsync(nameof(IPluginHostService.ShutdownAsync), null, Ct).WaitAsync(Bound, Ct);
            }
            catch (ConnectionLostException)
            {
                // The host may close the pipe before its answer is read.
            }

            Assert.True(await WaitForExitAsync(Bound), $"the host didn't exit after its shutdown: {Output}");
        }

        public void ClosePipe()
        {
            Rpc.Dispose();
            Pipe.Dispose();
        }

        public async Task<bool> WaitForExitAsync(TimeSpan within)
        {
            try
            {
                await Process.WaitForExitAsync(Ct).WaitAsync(within, Ct);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Rpc?.Dispose();
            await Pipe.DisposeAsync();
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync(CancellationToken.None);
            }
            Process.Dispose();
        }
    }

    /// <summary>The engine's side of the callbacks, recorded.</summary>
    internal sealed class RecordingCallback : IPluginClientCallback
    {
        public TaskCompletionSource<(List<PluginMetadataDto> Plugins, Dictionary<string, List<ContainerDto>> Containers)> Loaded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(string PluginId, string Message)> Errors { get; } = [];

        public void OnPluginsLoaded(List<PluginMetadataDto> plugins, Dictionary<string, List<ContainerDto>> containersByPluginId) =>
            Loaded.TrySetResult((plugins, containersByPluginId));

        public void OnSensorUpdate(List<SensorUpdateBatchDto> updates) { }

        public void OnPluginError(string pluginId, string errorMessage)
        {
            lock (Errors)
                Errors.Add((pluginId, errorMessage));
        }

        public void OnPerformanceUpdate(List<PluginPerformanceDto> performances) { }

        public void OnImageResize(string pluginId, ImageDescriptorDto descriptor) { }
    }
}
