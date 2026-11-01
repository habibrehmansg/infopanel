using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.SensorHelper;

/// <summary>
/// The helper's process, start to end: <c>InfoPanel.SensorHelper</c>'s whole <c>Main</c> but the tree it serves. It
/// serves only the engine that started it: the process its command line names, with that start time, connected on the
/// pipe (<see cref="HelperProcessOptions.ClientProcessId"/>). The helper is disposable: it ends its own process when the engine closes its pipe, when the engine's process ends, or
/// when LibreHardwareMonitor's computer fails to open, and the engine starts another. It never waits on
/// LibreHardwareMonitor to end: the tree is closed only when no call is inside it (<see cref="HelperServer.Retire"/>),
/// within <see cref="CleanupBound"/>, the engine still watched (its end cuts the clean-up short), and then the process
/// exits at once, whatever still runs in it (<see cref="HardExit"/>): its end is what frees LibreHardwareMonitor's
/// native resources.
/// </summary>
public static class HelperProcess
{
    /// <summary>The longest the helper spends closing the tree before it exits regardless.</summary>
    public static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(2);

    /// <summary>The engine closed the pipe or ended, or the process named isn't the engine (gone, or its id taken by another).</summary>
    public const int ExitDone = 0;

    /// <summary>A command line the helper doesn't take.</summary>
    public const int ExitUsage = 2;

    /// <summary>The helper refused what it was sent.</summary>
    public const int ExitRefused = 3;

    /// <summary>LibreHardwareMonitor's computer failed to open.</summary>
    public const int ExitTreeFailed = 4;

    /// <summary>
    /// Runs the helper on the command line <paramref name="args"/> (<see cref="HelperCommandLine"/>) over the tree
    /// <paramref name="createTree"/> makes, and ends the process through <see cref="HelperProcessOptions.Exit"/> with
    /// its code. Before anything is opened, an engine that is gone or isn't the process that started (its start time
    /// differs) ends it with <see cref="ExitDone"/>, and a command line it doesn't take with <see cref="ExitUsage"/>:
    /// both simply return, nothing having started.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, Func<ISensorTree> createTree, string helperVersion, HelperProcessOptions? options = null)
    {
        options ??= new HelperProcessOptions();
        if (HelperCommandLine.Parse(args) is not { } line)
        {
            options.Error.WriteLine("Usage: InfoPanel.SensorHelper --pipe <name> --parent <engine process id> --parent-start <its start time>");
            return ExitUsage;
        }

        using var engine = options.OpenParent(line.Parent);
        if (engine is null || engine.StartTime != line.ParentStart)
            return ExitDone;

        var tree = createTree();
        var server = new HelperServer(tree, helperVersion);
        var isEngine = options.ClientProcessId is { } clientProcessId ? HelperHost.ClientIs(line.Parent, clientProcessId) : null;
        var exit = await HelperHost.RunAsync(line.Pipe, server, engine.Exited, isEngine).ConfigureAwait(false);
        var code = exit switch
        {
            HelperExit.Refused => ExitRefused,
            HelperExit.TreeFailed => ExitTreeFailed,
            _ => ExitDone,
        };

        // The engine dead, nothing is closed: the process's end frees it. Otherwise the tree is closed only when no
        // call is inside it, and only for as long as the bound and the engine allow.
        if (!engine.Exited.IsCompleted && server.Retire())
        {
            var close = Task.Factory.StartNew(tree.Dispose, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task.WaitAny([close, engine.Exited], options.CleanupBound);
            _ = close.Exception; // a close that threw changes nothing: the process ends either way
        }

        options.Exit(code);
        return code;
    }

    /// <summary>
    /// Ends this process at once with <paramref name="code"/>: no exit handlers, no finalizers, no waiting on threads, so
    /// a thread stuck inside a driver (or holding a lock its unloading needs) can't keep it alive. Windows'
    /// <c>TerminateProcess</c>, elsewhere <c>_exit</c>. Nothing is flushed and no lock taken on the way: a thread holding
    /// the console's lock would hold the exit with it (the console's writers flush every write, so nothing is left in
    /// them anyway).
    /// </summary>
    public static void HardExit(int code)
    {
        if (OperatingSystem.IsWindows())
            TerminateProcess(GetCurrentProcess(), unchecked((uint)code));
        else
            SysExit(code);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void SysExit(int code);
}

/// <summary>The seams of <see cref="HelperProcess"/>: what the helper itself runs with, unless a test replaces one.</summary>
public sealed class HelperProcessOptions
{
    /// <summary>The longest the tree's close may take (<see cref="HelperProcess.CleanupBound"/>).</summary>
    public TimeSpan CleanupBound { get; init; } = HelperProcess.CleanupBound;

    /// <summary>Opens the engine's process by its id (<see cref="HelperParent.Open"/>).</summary>
    public Func<int, IHelperParent?> OpenParent { get; init; } = HelperParent.Open;

    /// <summary>
    /// Tells the process id of the pipe's client, so only the engine is served (<see cref="HelperHost.ClientIs"/>):
    /// <see cref="HelperHost.WindowsClientProcessId"/> on Windows; none elsewhere, where the helper doesn't run and its
    /// tests' stand-in serves whoever connects.
    /// </summary>
    public Func<NamedPipeServerStream, int?>? ClientProcessId { get; init; } = OperatingSystem.IsWindows() ? HelperHost.WindowsClientProcessId : null;

    /// <summary>Ends the process with its code (<see cref="HelperProcess.HardExit"/>).</summary>
    public Action<int> Exit { get; init; } = HelperProcess.HardExit;

    /// <summary>Where the usage goes.</summary>
    public TextWriter Error { get; init; } = Console.Error;
}
