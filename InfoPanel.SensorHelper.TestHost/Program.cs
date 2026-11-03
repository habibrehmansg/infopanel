using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using InfoPanel.SensorHelper;

namespace InfoPanel.SensorHelper.TestHost;

/// <summary>
/// <c>InfoPanel.SensorHelper.TestHost &lt;behaviour&gt; [--cleanup-ms &lt;n&gt;] [--engine-client &lt;pid&gt;] &lt;the
/// helper's command line&gt;</c>: the helper's lifetime (<see cref="HelperProcess"/>) over a fake tree, for
/// <c>HelperChildProcessTests</c>. The engine named on the helper's command line is a stand-in the test can end, while
/// the test itself connects: <c>--engine-client</c> names the test's process, whose connection then counts as the
/// engine's (the pipe's client still told by the platform, on Windows; elsewhere whoever connects is served). The behaviour
/// is <c>serve</c> (a small tree), <c>hang-update</c> (reading never returns), <c>hang-dispose</c> (closing never
/// returns), <c>fail-open</c> (opening throws) or <c>hold-console</c> (the first read leaves a thread holding
/// <see cref="Console.Out"/>'s lock for good, as a thread stuck mid-write would). It says what the tree does on its
/// output, one word a line, on a stream of its own (so a held <see cref="Console.Out"/> doesn't silence it):
/// <c>tree-created</c>, <c>update-entered</c>, <c>console-held</c>, <c>dispose-entered</c>, <c>disposed</c>; and, as it
/// exits after a close was entered, <c>exit-after-dispose-ms &lt;n&gt;</c>, the time since on its own clock.
/// <para>
/// Run with PawnIO's installer's own arguments, <c>-install -silent</c>, it is <c>PawnIoWindowsTests</c>' harmless
/// stand-in for that installer instead: it writes its own path to <c>ran.txt</c> beside itself and exits with the number
/// in <c>exit-code.txt</c> there. It installs nothing.
/// </para>
/// <para>
/// <c>client-of &lt;pipe&gt; &lt;SID&gt; &lt;file&gt;</c> (Windows; <c>HelperPipeSessionWindowsTests</c>, run as SYSTEM by a
/// task): creates the task's pipe as the helper does (<see cref="WindowsHelperPipe.Create"/>), waits up to 60 s for a
/// client, reads it as the helper does (<see cref="WindowsHelperPipe.ClientOf"/>), then one byte from it, and writes
/// <c>key=value</c> lines to the file: <c>server</c> (its own SID), <c>session</c> (the client's), <c>client</c> (SID and
/// integrity level, or <c>null</c>) and <c>read</c>.
/// </para>
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        if (args is ["-install", "-silent"])
            return Task.FromResult(InstallerStandIn());

        if (args is ["client-of", var pipe, var sid, var file] && OperatingSystem.IsWindows())
            return ClientOfAsync(pipe, sid, file);

        if (args.Length < 1 || args[0] is not ("serve" or "hang-update" or "hang-dispose" or "fail-open" or "hold-console"))
        {
            Console.Error.WriteLine("Usage: InfoPanel.SensorHelper.TestHost <serve|hang-update|hang-dispose|fail-open|hold-console> [--cleanup-ms <n>] [--engine-client <pid>] <helper arguments>");
            return Task.FromResult(2);
        }

        var behaviour = args[0];
        var rest = args[1..];
        var defaults = new HelperProcessOptions();
        var cleanupBound = defaults.CleanupBound;
        int? engineClient = null;
        while (rest.Length >= 2 && rest[0] is "--cleanup-ms" or "--engine-client")
        {
            var value = int.Parse(rest[1], CultureInfo.InvariantCulture);
            if (rest[0] == "--cleanup-ms")
                cleanupBound = TimeSpan.FromMilliseconds(value);
            else
                engineClient = value;
            rest = rest[2..];
        }

        var clientProcessId = defaults.ClientProcessId;
        if (engineClient is { } test && clientProcessId is { } real && HelperCommandLine.Parse(rest) is { } line)
            clientProcessId = pipe => real(pipe) == test ? line.Parent : null;

        var options = new HelperProcessOptions
        {
            CleanupBound = cleanupBound,
            ClientProcessId = clientProcessId,
            Exit = code =>
            {
                // How long the close had, on this process's clock: the test checks the cut there, since the end it sees
                // also holds this process's teardown and its own notice of it, which a loaded machine delays.
                if (Interlocked.Read(ref _disposeEntered) is var entered and not 0)
                    Say($"exit-after-dispose-ms {Stopwatch.GetElapsedTime(entered).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}");
                HelperProcess.HardExit(code);
            },
        };
        return HelperProcess.RunAsync(rest, () => new TestTree(behaviour), "2.0.0-testhost", options);
    }

    /// <summary><c>client-of</c>: the helper's pipe and its reading of the client, written to <paramref name="file"/>.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<int> ClientOfAsync(string pipe, string sid, string file)
    {
        var lines = new List<string> { "server=" + WindowsIdentity.GetCurrent().User?.Value };
        try
        {
            using var server = WindowsHelperPipe.Create(pipe, sid);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await server.WaitForConnectionAsync(wait.Token);
            lines.Add("session=" + (GetNamedPipeClientSessionId(server.SafePipeHandle, out var session) ? session.ToString(CultureInfo.InvariantCulture) : "unknown"));
            var client = WindowsHelperPipe.ClientOf(server);
            lines.Add("client=" + (client is null ? "null" : client.Sid + " " + client.Integrity.ToString(CultureInfo.InvariantCulture)));
            lines.Add("read=" + (await server.ReadAsync(new byte[1], wait.Token)).ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            lines.Add("error=" + ex.GetType().Name + ": " + ex.Message);
        }

        File.WriteAllLines(file, lines);
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);

    /// <summary>The installer's stand-in: says it ran, and exits with the code its folder holds.</summary>
    private static int InstallerStandIn()
    {
        var folder = AppContext.BaseDirectory;
        File.WriteAllText(Path.Combine(folder, "ran.txt"), Environment.ProcessPath ?? "");
        return int.Parse(File.ReadAllText(Path.Combine(folder, "exit-code.txt")).Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>When the tree's close was entered (<see cref="Stopwatch.GetTimestamp"/>), or 0.</summary>
    private static long _disposeEntered;

    // Standard output without the console's streams: on Unix every write through them takes Console.Out's lock.
    private static readonly StreamWriter Output = new(OperatingSystem.IsWindows()
        ? Console.OpenStandardOutput()
        : new FileStream(new SafeFileHandle(1, ownsHandle: false), FileAccess.Write, 1)) { AutoFlush = true };

    private static void Say(string word)
    {
        lock (Output)
            Output.WriteLine(word);
    }

    /// <summary>Leaves a thread holding <see cref="Console.Out"/>'s lock (its writer is synchronized on itself) until the process ends.</summary>
    private static void HoldConsole()
    {
        using var held = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (Console.Out)
            {
                held.Set();
                Thread.Sleep(Timeout.Infinite);
            }
        }) { IsBackground = true };
        holder.Start();
        held.Wait();
    }

    private sealed class TestTree : ISensorTree
    {
        private readonly string _behaviour;
        private bool _consoleHeld;

        public TestTree(string behaviour)
        {
            _behaviour = behaviour;
            Say("tree-created");
        }

        public bool Elevated => false;

        public string Driver => "unknown";

        public void Configure(bool storage, int storageInterval)
        {
            if (_behaviour == "fail-open")
                throw new TimeoutException("A device timed out while the computer opened.");
        }

        public TreeSnapshot Update()
        {
            Say("update-entered");
            if (_behaviour == "hang-update")
                Thread.Sleep(Timeout.Infinite);
            if (_behaviour == "hold-console" && !_consoleHeld)
            {
                HoldConsole();
                _consoleHeld = true;
                Say("console-held");
            }

            return new TreeSnapshot(
                [new TreeHardware("/amdcpu/0", "CPU", "Cpu", 2, null)],
                [new TreeSensor("/amdcpu/0/load/0", "CPU Total", "Load", 0, "/amdcpu/0", 12.5f, 0f, 100f)]);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposeEntered, Stopwatch.GetTimestamp());
            Say("dispose-entered");
            if (_behaviour == "hang-dispose")
                Thread.Sleep(Timeout.Infinite);
            Say("disposed");
        }
    }
}
