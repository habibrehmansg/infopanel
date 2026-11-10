using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using InfoPanel.SensorHelper;
using Xunit;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// A sensor tree the tests set up by hand: hardware and sensors as LibreHardwareMonitor would list them, what
/// <see cref="Configure"/> was called with (storage off drops the hardware of type <c>Storage</c>), and switches to make
/// <see cref="Update"/> throw or hang.
/// </summary>
internal sealed class FakeTree : ISensorTree
{
    private readonly object _lock = new();
    private bool _storage = true;

    public List<TreeHardware> Hardware { get; } = [];
    public List<TreeSensor> Sensors { get; } = [];
    public List<(bool Storage, int Interval)> Configured { get; } = [];
    public int Updates;
    public bool Throw;

    /// <summary>Makes <see cref="Configure"/> throw, as a <c>Computer.Open()</c> that fails part way does.</summary>
    public bool ThrowOnConfigure;

    /// <summary>While unset, <see cref="Update"/> waits for it; <see cref="UpdateEntered"/> says one is waiting.</summary>
    public ManualResetEventSlim Go { get; } = new(true);

    public ManualResetEventSlim UpdateEntered { get; } = new(false);

    public bool Elevated => false;

    public string Driver => "unknown";

    /// <summary>While unset, <see cref="Dispose"/> waits for it; <see cref="DisposeEntered"/> says one is waiting.</summary>
    public ManualResetEventSlim DisposeGo { get; } = new(true);

    public ManualResetEventSlim DisposeEntered { get; } = new(false);

    public bool Disposed { get; private set; }

    public void Configure(bool storage, int storageInterval)
    {
        lock (_lock)
        {
            Configured.Add((storage, storageInterval));
            if (ThrowOnConfigure)
                throw new TimeoutException("A device timed out while the computer opened.");
            _storage = storage;
        }
    }

    public TreeSnapshot Update()
    {
        UpdateEntered.Set();
        Go.Wait();
        lock (_lock)
        {
            Updates++;
            if (Throw)
                throw new InvalidOperationException("LibreHardwareMonitor threw.");

            var storageIds = Hardware.Where(h => h.Type == "Storage").Select(h => h.Identifier).ToHashSet();
            return new TreeSnapshot(
                [.. Hardware.Where(h => _storage || !storageIds.Contains(h.Identifier))],
                [.. Sensors.Where(s => _storage || !storageIds.Contains(s.Hardware))]);
        }
    }

    public void Dispose()
    {
        DisposeEntered.Set();
        DisposeGo.Wait();
        Disposed = true;
    }

    /// <summary>A small PC: a CPU, an NVIDIA GPU, a board with a Super I/O chip under it, a drive and a network card.</summary>
    public static FakeTree Pc()
    {
        var tree = new FakeTree();
        tree.Hardware.AddRange([
            new("/motherboard", "ASUS PRIME B650", "Motherboard", 0, null),
            new("/lpc/nct6798d/0", "Nuvoton NCT6798D", "SuperIO", 1, "/motherboard"),
            new("/amdcpu/0", "AMD Ryzen 7 8845HS", "Cpu", 2, null),
            new("/gpu-nvidia/0", "NVIDIA GeForce RTX 4070", "GpuNvidia", 4, null),
            new("/nvme/0", "Samsung SSD 990 PRO 2TB", "Storage", 7, null),
            new("/nic/{4D36E972-E325-11CE}", "Ethernet", "Network", 8, null),
        ]);
        tree.Sensors.AddRange([
            new("/gpu-nvidia/0/temperature/0", "GPU Core", "Temperature", 0, "/gpu-nvidia/0", 51.5f, 40f, 72.25f),
            new("/nic/{4D36E972-E325-11CE}/throughput/7", "Upload Speed", "Throughput", 7, "/nic/{4D36E972-E325-11CE}", 1536f, 0f, 90000f),
            new("/amdcpu/0/load/1", "CPU Core #1", "Load", 1, "/amdcpu/0", 12.5f, 0f, 100f),
            new("/amdcpu/0/load/0", "CPU Total", "Load", 0, "/amdcpu/0", 9.75f, 1f, 88f),
            new("/lpc/nct6798d/0/fan/1", "Fan #2", "Fan", 1, "/lpc/nct6798d/0", 1200f, 1100f, 1300f),
            new("/lpc/nct6798d/0/voltage/0", "Vcore", "Voltage", 0, "/lpc/nct6798d/0", null, null, null),
            new("/nvme/0/data/24", "Data Read", "Data", 24, "/nvme/0", 5321.25f, 5000f, 5321.25f),
            new("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", "Temperature", 2, "/amdcpu/0", float.NaN, 35f, float.PositiveInfinity),
        ]);
        return tree;
    }
}

internal static class HelperTestKit
{
    /// <summary>A pipe name of this test run's own: short (macOS's socket paths are), never the engine's.</summary>
    public static string PipeName() => "ipt" + Guid.NewGuid().ToString("N")[..12];

    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Why a test of the helper's stand-in as a child process skips on Linux. The helper runs on Windows only; its
    /// stand-in proves the lifetime on macOS (and Windows). It serves only an engine whose start time matches the one
    /// it was given exactly, and on Linux .NET derives a process's start time from the boot time at each reading, so
    /// two processes read the same process's start a fraction of a millisecond apart: the stand-in never serves there.
    /// </summary>
    public const string LinuxChildSkip = "Linux: the helper is Windows-only, and its stand-in's exact start-time check can't match across processes on Linux (.NET reads a process's start from the boot time at each reading).";

    /// <summary>A never-ending engine process, for helpers that should stop only when their pipe closes.</summary>
    public static Task EngineRunsOn => new TaskCompletionSource().Task;

    /// <summary>Starts a helper serving <paramref name="tree"/> on a new pipe.</summary>
    public static (string Pipe, Task<HelperExit> Run) StartHelper(ISensorTree tree, Task? engineExited = null)
    {
        var name = PipeName();
        return (name, HelperHost.RunAsync(name, tree, "2.0.0-test", engineExited ?? EngineRunsOn));
    }

    public static async Task<SensorHelperClient> Connect(string pipe, TimeProvider? time = null, SensorHelperClient.IObserver? observer = null) =>
        await SensorHelperClient.ConnectAsync(pipe, Patience, time, observer, TestContext.Current.CancellationToken);

    /// <summary>A client end that sends and reads raw frames, as a broken or hostile engine might.</summary>
    public static async Task<NamedPipeClientStream> RawClient(string pipe)
    {
        var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync((int)Patience.TotalMilliseconds, TestContext.Current.CancellationToken);
        return client;
    }

    /// <summary>A server end that answers with raw frames, as a broken or hostile helper might, and the client on it.</summary>
    public static async Task<(NamedPipeServerStream Server, SensorHelperClient Client)> RawHelper(TimeProvider? time = null, SensorHelperClient.IObserver? observer = null)
    {
        var name = PipeName();
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var waiting = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        var client = await Connect(name, time, observer);
        await waiting;
        return (server, client);
    }

    public static byte[] Frame(string json) => Frame(Encoding.UTF8.GetBytes(json));

    public static byte[] Frame(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    public static async Task Send(Stream stream, string json)
    {
        await stream.WriteAsync(Frame(json), TestContext.Current.CancellationToken);
        await stream.FlushAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The next frame's JSON, or null when the other side closed.</summary>
    public static async Task<string?> Receive(Stream stream)
    {
        var frame = await HelperFraming.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask().WaitAsync(Patience, TestContext.Current.CancellationToken);
        return frame is null ? null : Encoding.UTF8.GetString(frame);
    }

    /// <summary>JSON nested <paramref name="depth"/> levels deep: an object holding arrays.</summary>
    public static string Nested(int depth, string type = "catalogue") =>
        "{\"type\":\"" + type + "\",\"x\":" + new string('[', depth - 1) + new string(']', depth - 1) + "}";

    /// <summary>
    /// A task that completes when <paramref name="engine"/> ends, as the helper watches it. The parent is held until
    /// then and disposed after, as the helper holds it for its whole run.
    /// </summary>
    public static Task Watch(Process engine)
    {
        var parent = HelperParent.Open(engine.Id) ?? throw new InvalidOperationException("The engine isn't running.");
        return parent.Exited.ContinueWith(_ => parent.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>A process that runs until killed: the engine, for the tests that end it.</summary>
    public static Process LongRunningProcess()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1 >nul")
            : new ProcessStartInfo("/bin/sleep", "120");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        return Process.Start(info)!;
    }
}
