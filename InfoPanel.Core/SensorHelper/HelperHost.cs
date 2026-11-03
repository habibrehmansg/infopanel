using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace InfoPanel.SensorHelper;

/// <summary>
/// The helper serving one engine on one pipe (<see cref="HelperServer"/>) until the engine closes the pipe or the
/// engine's process ends, whichever comes first: before the engine connects, while it is served, or while the tree
/// hangs inside LibreHardwareMonitor (the serve loop sees the pipe close even then, and its call into the tree is left
/// behind). Only the engine is served: a client whose process isn't the engine's is dropped unserved and the helper
/// waits for the engine again (on Windows by <c>GetNamedPipeClientProcessId</c>, so a process that takes the one
/// connection first can't hold the engine out). What happens to the process after: <see cref="HelperProcess"/>.
/// </summary>
/// <remarks>
/// The engine's child's pipe is the current user's only (<see cref="PipeOptions.CurrentUserOnly"/>), which on Windows also
/// requires the same elevation. The helper a task runs as SYSTEM serves on a pipe of its own instead, created with its
/// own security (<see cref="HelperTaskCommands.ServeAsync"/>), through <see cref="ServeAsync"/>.
/// </remarks>
public static class HelperHost
{
    /// <summary>Serves <paramref name="tree"/> on the pipe <paramref name="pipeName"/> until the engine closes it or <paramref name="engineExited"/> completes.</summary>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> isn't a name <see cref="HelperProtocol.IsPipeName"/> takes.</exception>
    public static Task<HelperExit> RunAsync(string pipeName, ISensorTree tree, string helperVersion, Task engineExited, CancellationToken cancel = default) =>
        RunAsync(pipeName, new HelperServer(tree, helperVersion), engineExited, cancel: cancel);

    /// <summary>
    /// Serves with <paramref name="server"/> on the pipe <paramref name="pipeName"/> until the engine closes it or
    /// <paramref name="engineExited"/> completes; a client <paramref name="isEngine"/> refuses is disconnected unserved
    /// (none refused when it is null).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> isn't a name <see cref="HelperProtocol.IsPipeName"/> takes.</exception>
    public static async Task<HelperExit> RunAsync(string pipeName, HelperServer server, Task engineExited, Func<NamedPipeServerStream, bool>? isEngine = null, CancellationToken cancel = default)
    {
        if (!HelperProtocol.IsPipeName(pipeName))
            throw new ArgumentException("The pipe's name is 1 to 64 letters, digits, '.', '_' or '-'.", nameof(pipeName));

        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (OperatingSystem.IsWindows())
            options |= PipeOptions.FirstPipeInstance;

        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, options);
        return await ServeAsync(pipe, server, engineExited, isEngine, cancel: cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves with <paramref name="server"/> on <paramref name="pipe"/>, a server end already created (which it owns and
    /// disposes), until the engine closes it or <paramref name="engineExited"/> completes; a client
    /// <paramref name="isEngine"/> refuses is disconnected unserved (none refused when it is null). With
    /// <paramref name="connectWithin"/>, no engine accepted within that time (on <paramref name="time"/>'s clock, the
    /// clients refused meanwhile included) ends it, <see cref="HelperExit.NoEngine"/>; so does, with
    /// <paramref name="helloWithin"/>, a client accepted that says no hello within that time. A client refused when
    /// <paramref name="endsOnRefusal"/> then says so ends it too, <see cref="HelperExit.Refused"/>, rather than waiting on.
    /// </summary>
    public static async Task<HelperExit> ServeAsync(NamedPipeServerStream pipe, HelperServer server, Task engineExited, Func<NamedPipeServerStream, bool>? isEngine = null,
        TimeSpan? connectWithin = null, TimeProvider? time = null, CancellationToken cancel = default, TimeSpan? helloWithin = null, Func<bool>? endsOnRefusal = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        using var deadline = connectWithin is { } within ? new CancellationTokenSource(within, time ?? TimeProvider.System) : new CancellationTokenSource();
        var serve = Task.Run(async () =>
        {
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, deadline.Token))
            {
                while (true)
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(waiting.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested && !stop.IsCancellationRequested)
                    {
                        return HelperExit.NoEngine;
                    }

                    if (isEngine is null || isEngine(pipe))
                        break;
                    pipe.Disconnect(); // not the engine: nothing read from it, nothing answered
                    if (endsOnRefusal?.Invoke() == true)
                        return HelperExit.Refused;
                }
            }

            var serving = server.ServeAsync(pipe, stop.Token);
            if (helloWithin is { } helloTime)
            {
                using var greeted = new CancellationTokenSource();
                var late = Task.Delay(helloTime, time ?? TimeProvider.System, greeted.Token);
                var first = await Task.WhenAny(serving, server.Greeted, late).ConfigureAwait(false);
                greeted.Cancel();
                if (first == late && !server.Greeted.IsCompleted)
                {
                    // Accepted, then silent: no engine. Its pipe is closed, and the helper ends.
                    stop.Cancel();
                    _ = await serving.ConfigureAwait(false);
                    return HelperExit.NoEngine;
                }
            }

            return await serving.ConfigureAwait(false);
        }, CancellationToken.None);

        try
        {
            if (await Task.WhenAny(serve, engineExited).ConfigureAwait(false) == serve)
                return await serve.ConfigureAwait(false);
            return HelperExit.EngineExited;
        }
        finally
        {
            stop.Cancel();
            pipe.Dispose();
            // A serve loop still waiting for the engine fails on the closed pipe; nobody waits for it.
            _ = serve.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Whether a client is the engine: its process id, as <paramref name="clientProcessId"/> tells it, is
    /// <paramref name="pid"/>. One whose id can't be told (null) isn't.
    /// </summary>
    public static Func<NamedPipeServerStream, bool> ClientIs(int pid, Func<NamedPipeServerStream, int?> clientProcessId) =>
        pipe => clientProcessId(pipe) == pid;

    /// <summary>The process id of the client connected to <paramref name="pipe"/> (<c>GetNamedPipeClientProcessId</c>), null when Windows can't tell it.</summary>
    [SupportedOSPlatform("windows")]
    public static int? WindowsClientProcessId(NamedPipeServerStream pipe) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? unchecked((int)pid) : null;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
