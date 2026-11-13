using System.IO.Pipes;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using StreamJsonRpc;
using Xunit;

namespace InfoPanel.Tests.Plugins;

/// <summary>
/// Plugin hosts without processes, for the manager's tests: <see cref="FakeLauncher.Start"/> gives a
/// <see cref="FakeProcess"/> that, as its <see cref="FakeHost"/> says, connects to the engine's real pipe from this
/// process and serves the host service over JSON-RPC, or never connects, hangs, ignores a shutdown, or exits when told.
/// </summary>
internal sealed class FakeLauncher(Func<PluginHostCommand, FakeHost> hosts) : IPluginProcessLauncher
{
    private readonly List<FakeProcess> _started = [];

    public FakeLauncher(Func<FakeHost> hosts) : this(_ => hosts())
    {
    }

    public IReadOnlyList<FakeProcess> Started
    {
        get
        {
            lock (_started)
                return [.. _started];
        }
    }

    public IPluginProcess Start(PluginHostCommand command)
    {
        var process = new FakeProcess(command, hosts(command));
        lock (_started)
            _started.Add(process);
        process.Run();
        return process;
    }
}

/// <summary>What a fake host does.</summary>
internal sealed class FakeHost : IPluginHostService
{
    /// <summary>Never connects to the pipe.</summary>
    public bool NeverConnects { get; init; }

    /// <summary>Its InitializeAsync never answers.</summary>
    public bool InitializeHangs { get; init; }

    /// <summary>It never calls OnPluginsLoaded.</summary>
    public bool NoLoaded { get; init; }

    /// <summary>Its ShutdownAsync never answers and it doesn't exit.</summary>
    public bool IgnoresShutdown { get; init; }

    public string PluginId { get; init; } = "fake";

    /// <summary>Its InitializeAsync waits for this first, when set.</summary>
    public Task? InitializeGate { get; init; }

    /// <summary>Its ShutdownAsync waits for this first (once counted), when set.</summary>
    public Task? ShutdownGate { get; init; }

    /// <summary>Its plugin's actions (method names; each shown as "Do {method}").</summary>
    public string[] Actions { get; init; } = [];

    /// <summary>Its plugin's settings, when it has any; their values change as they are applied.</summary>
    public List<PluginConfigPropertyDto>? Settings { get; init; }

    /// <summary>Its settings calls never answer.</summary>
    public bool SettingsHang { get; init; }

    /// <summary>The actions it was asked to run, in order: (plugin id, method).</summary>
    public List<(string Plugin, string Method)> ActionsRun { get; } = [];

    /// <summary>The settings it was asked to apply, in order: (plugin id, key, value).</summary>
    public List<(string Plugin, string Key, object? Value)> Applied { get; } = [];

    internal FakeProcess Process { get; set; } = null!;
    internal JsonRpc? Rpc { get; set; }

    /// <summary>Its plugin's name, as it lists it.</summary>
    public string Name { get; init; } = "Fake";

    public PluginMetadataDto Metadata => new()
    {
        Id = PluginId,
        Name = Name,
        Description = "A fake plugin.",
        Actions = [.. Actions.Select(a => new PluginActionDto { MethodName = a, DisplayName = "Do " + a })],
        IsConfigurable = Settings != null,
        ConfigProperties = Settings ?? [],
    };

    /// <summary>The entries its plugin lists in its one container, <c>c</c>: a number, <c>v</c>, unless a test gives others.</summary>
    public Func<List<EntryDto>> Entries { get; init; } =
        () => [new EntryDto { Id = "v", Name = "Value", Type = "sensor", SensorValue = new SensorValueDto { Value = 1, Unit = "u" } }];

    public Dictionary<string, List<ContainerDto>> Containers => new()
    {
        [PluginId] =
        [
            new ContainerDto
            {
                Id = "c",
                Name = "Container",
                Entries = Entries(),
            },
        ],
    };

    public async Task<List<PluginMetadataDto>> InitializeAsync()
    {
        if (InitializeGate != null)
            await InitializeGate.WaitAsync(Process.Dead);
        if (InitializeHangs)
            await Task.Delay(Timeout.Infinite, Process.Dead);
        if (!NoLoaded)
            await Rpc!.NotifyAsync(nameof(IPluginClientCallback.OnPluginsLoaded), new List<PluginMetadataDto> { Metadata }, Containers);
        return [Metadata];
    }

    public Task InvokeActionAsync(string pluginId, string methodName)
    {
        lock (ActionsRun)
            ActionsRun.Add((pluginId, methodName));
        return Task.CompletedTask;
    }

    public async Task<List<PluginConfigPropertyDto>> GetConfigPropertiesAsync(string pluginId)
    {
        if (SettingsHang)
            await Task.Delay(Timeout.Infinite, Process.Dead);
        return pluginId == PluginId ? Settings ?? [] : [];
    }

    public async Task<List<PluginConfigPropertyDto>> ApplyConfigAsync(string pluginId, string key, object? value)
    {
        if (SettingsHang)
            await Task.Delay(Timeout.Infinite, Process.Dead);
        lock (Applied)
            Applied.Add((pluginId, key, value));
        if (Settings?.FirstOrDefault(p => p.Key == key) is { } setting)
            setting.Value = value;
        return Settings ?? [];
    }

    /// <summary>Reports its processor use, as a host does every 2 s.</summary>
    public Task SendPerformanceAsync(double cpu) =>
        Rpc!.NotifyAsync(nameof(IPluginClientCallback.OnPerformanceUpdate), new List<PluginPerformanceDto> { new() { PluginId = PluginId, CpuPercent = cpu } });

    public async Task ShutdownAsync()
    {
        Process.ShutdownsAsked++;
        if (ShutdownGate != null)
            await ShutdownGate.WaitAsync(Process.Dead);
        if (IgnoresShutdown)
        {
            await Task.Delay(Timeout.Infinite, Process.Dead);
            return;
        }
        // As the real host: it answers, then ends its connection and exits.
        _ = Task.Run(async () =>
        {
            await Task.Delay(20);
            Process.Exit(0);
        });
    }

    /// <summary>Sends the engine a notification <paramref name="chars"/> characters long.</summary>
    public Task SendHugeErrorAsync(int chars) =>
        Rpc!.NotifyAsync(nameof(IPluginClientCallback.OnPluginError), PluginId, new string('x', chars));

    /// <summary>Sends the engine a plugin error, a valid message.</summary>
    public Task SendErrorAsync(string message) =>
        Rpc!.NotifyAsync(nameof(IPluginClientCallback.OnPluginError), PluginId, message);

    /// <summary>Calls <paramref name="method"/> with an id, so the engine would owe it a reply; completes with that reply.</summary>
    public Task AskAsync(string method, params object?[] arguments) => Rpc!.InvokeAsync(method, arguments);

    /// <summary>Sends the engine a notification of <paramref name="method"/> with these arguments, whatever they are.</summary>
    public Task NotifyAsync(string method, params object?[] arguments) => Rpc!.NotifyAsync(method, arguments);

    /// <summary>Sends the engine a notification of <paramref name="method"/> with named arguments.</summary>
    public Task NotifyNamedAsync(string method, object arguments) => Rpc!.NotifyWithParameterObjectAsync(method, arguments);
}

internal sealed class FakeProcess(PluginHostCommand command, FakeHost host) : IPluginProcess
{
    private static int _ids = 100_000;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _dead = new();
    private NamedPipeClientStream? _pipe;

    public PluginHostCommand Command { get; } = command;
    public FakeHost Host { get; } = host;
    public int Id { get; } = Interlocked.Increment(ref _ids);
    public bool HasExited => _exited.Task.IsCompleted;
    public int? ExitCode { get; private set; }
    public Task Exited => _exited.Task;
    public string ErrorTail { get; set; } = "";
    public bool Killed { get; private set; }
    public long? MemoryBytes { get; set; } = 42 * 1024 * 1024;
    public int ShutdownsAsked { get; set; }
    public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Cancelled when it exits.</summary>
    public CancellationToken Dead => _dead.Token;

    public void Run()
    {
        Host.Process = this;
        if (Host.NeverConnects)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", Command.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await _pipe.ConnectAsync(Dead);
                var rpc = new JsonRpc(_pipe);
                rpc.AddLocalRpcTarget<IPluginHostService>(Host, null);
                Host.Rpc = rpc;
                rpc.StartListening();
                Connected.TrySetResult();
            }
            catch (Exception ex)
            {
                Connected.TrySetException(ex);
            }
        });
    }

    public void Kill()
    {
        Killed = true;
        Exit(-9);
    }

    /// <summary>Ends the process with <paramref name="code"/> (its pipe closes with it).</summary>
    public void Exit(int code, string stderr = "")
    {
        if (HasExited)
            return;
        ExitCode = code;
        if (stderr.Length > 0)
            ErrorTail = stderr;
        _dead.Cancel();
        Host.Rpc?.Dispose();
        _pipe?.Dispose();
        _exited.TrySetResult();
    }

    public void Dispose()
    {
    }
}

internal static class Eventually
{
    /// <summary>
    /// The most a wait lasts: a sanity limit, never the behaviour under test (the manager's times are on the tests' manual
    /// clock). Under parallel full runs this Mac's program launches can stall for tens of seconds (its endpoint protection
    /// authorises each one), and while another test's <c>Process.Start</c> waits on that, .NET's process-wide start lock
    /// holds this process's child-exit handling and the thread pool runs short; a fake host's start, all in this process,
    /// then took over 10 s. Each wait still ends as soon as its condition holds.
    /// </summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>Waits (real time, up to <see cref="Limit"/>) until <paramref name="condition"/> holds; fails with <paramref name="what"/> otherwise.</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"timed out waiting until {what}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Checks that <paramref name="condition"/> holds throughout <paramref name="within"/> (real time).</summary>
    public static async Task Stays(Func<bool> condition, TimeSpan within, string what)
    {
        var end = DateTime.UtcNow + within;
        while (DateTime.UtcNow < end)
        {
            Assert.True(condition(), what);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), what);
    }
}
