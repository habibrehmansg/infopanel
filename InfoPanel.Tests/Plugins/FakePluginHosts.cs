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
internal sealed class FakeLauncher(Func<FakeHost> hosts) : IPluginProcessLauncher
{
    private readonly List<FakeProcess> _started = [];

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
        var process = new FakeProcess(command, hosts());
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

    internal FakeProcess Process { get; set; } = null!;
    internal JsonRpc? Rpc { get; set; }

    public PluginMetadataDto Metadata => new() { Id = PluginId, Name = "Fake" };

    public Dictionary<string, List<ContainerDto>> Containers => new()
    {
        [PluginId] =
        [
            new ContainerDto
            {
                Id = "c",
                Name = "Container",
                Entries = [new EntryDto { Id = "v", Name = "Value", Type = "sensor", SensorValue = new SensorValueDto { Value = 1, Unit = "u" } }],
            },
        ],
    };

    public async Task<List<PluginMetadataDto>> InitializeAsync()
    {
        if (InitializeHangs)
            await Task.Delay(Timeout.Infinite, Process.Dead);
        if (!NoLoaded)
            await Rpc!.NotifyAsync(nameof(IPluginClientCallback.OnPluginsLoaded), new List<PluginMetadataDto> { Metadata }, Containers);
        return [Metadata];
    }

    public Task InvokeActionAsync(string pluginId, string methodName) => Task.CompletedTask;

    public Task<List<PluginConfigPropertyDto>> GetConfigPropertiesAsync(string pluginId) => Task.FromResult(new List<PluginConfigPropertyDto>());

    public Task<List<PluginConfigPropertyDto>> ApplyConfigAsync(string pluginId, string key, object? value) =>
        Task.FromResult(new List<PluginConfigPropertyDto>());

    public async Task ShutdownAsync()
    {
        Process.ShutdownsAsked++;
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
    /// <summary>Waits (real time, up to 10 s) until <paramref name="condition"/> holds; fails with <paramref name="what"/> otherwise.</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
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
