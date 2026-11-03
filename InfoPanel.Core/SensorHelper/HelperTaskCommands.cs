using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.SensorHelper;

/// <summary>
/// What <c>--register</c> and <c>--unregister</c> ask of Windows (<see cref="WindowsHelperTaskHost"/>; a fake in tests).
/// </summary>
public interface IHelperTaskHost
{
    /// <summary>Whether this process runs with administrator rights.</summary>
    bool IsElevated { get; }

    /// <summary>This helper's own executable by its final path (every link resolved), null when it can't be told.</summary>
    string? OwnPath();

    /// <summary>Program Files as the known-folder API gives it (<c>FOLDERID_ProgramFiles</c>), never an environment variable; null when it can't.</summary>
    string? ProgramFiles();

    /// <summary>Whether <paramref name="sid"/> is a user on this computer (Windows' <c>LookupAccountSid</c>: <c>SidTypeUser</c>).</summary>
    bool IsUserAccount(string sid);

    /// <summary>Whether a task is at <paramref name="path"/>.</summary>
    bool TaskExists(string path);

    /// <summary>Removes the task at <paramref name="path"/>.</summary>
    void DeleteTask(string path);

    /// <summary>
    /// Registers the task <paramref name="xml"/> at <paramref name="path"/>, run as SYSTEM, with the security descriptor
    /// <paramref name="securityDescriptor"/>, the definition passed in memory (never through a file): created, or one
    /// already there updated in place (<c>TASK_CREATE_OR_UPDATE</c>), so a refusal leaves it as it was. An update keeps
    /// the task's own security descriptor (Windows': W5.2 PC check 1, P3), which <see cref="SetTaskSecurity"/> replaces.
    /// </summary>
    void RegisterTask(string path, string xml, string securityDescriptor);

    /// <summary>Sets the security descriptor of the task at <paramref name="path"/> (owner and DACL) to <paramref name="securityDescriptor"/>.</summary>
    void SetTaskSecurity(string path, string securityDescriptor);

    /// <summary>Creates the task folder <paramref name="folder"/> when it isn't there, and sets its security descriptor (owner and DACL) to <paramref name="securityDescriptor"/>.</summary>
    void SetFolderSecurity(string folder, string securityDescriptor);

    /// <summary>Removes the task folder <paramref name="folder"/> when it holds no task (hidden ones counted) and no folder; whether it did.</summary>
    bool DeleteFolderIfEmpty(string folder);

    /// <summary>The definition of the task at <paramref name="path"/> as the Task Scheduler reads it back, or null when there is no such task; throws when it can't be read.</summary>
    HelperTaskDefinition? TaskDefinition(string path);
}

/// <summary>
/// A registered task's definition as the Task Scheduler reads it back (<see cref="IHelperTaskHost.TaskDefinition"/>): the
/// SID it runs as, its principal's (null when it names no user, a group's task, or Windows can't tell its SID), and its
/// actions in order.
/// </summary>
public sealed record HelperTaskDefinition(string? UserSid, IReadOnlyList<HelperTaskAction> Actions);

/// <summary>One of a task's actions: the program it runs and its arguments; <see cref="Path"/> null for an action that runs no program (an e-mail, a message, a COM handler).</summary>
public sealed record HelperTaskAction(string? Path, string Arguments);

/// <summary>The client of the task's pipe: its user's SID, read from its token, and that token's integrity level (the RID of its mandatory label: <see cref="HelperTask.MediumIntegrity"/> for an unelevated engine).</summary>
public sealed record HelperClient(string Sid, int Integrity);

/// <summary>
/// The Win32 calls that read the pipe's client from its token (<see cref="HelperPipeClient.Read"/>): Windows' in
/// <see cref="WindowsHelperPipe.ClientOf"/>, a fake in tests.
/// </summary>
public interface IPipeClientToken
{
    /// <summary>
    /// <c>ImpersonateNamedPipeClient</c>: this thread takes the client's token at the level the client allowed
    /// (identification, for the engine). False when Windows refuses.
    /// </summary>
    bool Impersonate(NamedPipeServerStream pipe);

    /// <summary>
    /// <c>OpenThreadToken(TOKEN_QUERY, OpenAsSelf)</c>: the handle of the token this thread holds, opened with the helper's
    /// own rights (the client's, at identification, can't open anything); <see cref="IntPtr.Zero"/> when it can't be.
    /// The only call made while impersonating: at identification nothing else works as the client (a file, a library,
    /// a type to load).
    /// </summary>
    IntPtr OpenThreadToken();

    /// <summary>
    /// <c>RevertToSelf</c>: this thread is the helper again. A failure ends the process: the thread can't go on as its
    /// client.
    /// </summary>
    void RevertToSelf();

    /// <summary>The user and integrity level of the token <paramref name="token"/> (from <see cref="OpenThreadToken"/>), null when either can't be read; the handle closed, whatever happens.</summary>
    HelperClient? Read(IntPtr token);
}

/// <summary>Reads the pipe's client from its token (<see cref="IPipeClientToken"/>).</summary>
public static class HelperPipeClient
{
    /// <summary>
    /// The user connected to <paramref name="pipe"/> and their integrity level, read from the token this thread takes
    /// while impersonating them at the level they allowed (identification: nothing can be done as them, and SYSTEM is
    /// never given a token it could act with): impersonate, open the thread's token, revert (on every path), then read
    /// it as the helper. Null when either can't be told. Not .NET's <c>RunAsClient</c>: it makes the client's token the
    /// thread's identity, which identification refuses (<c>ERROR_BAD_IMPERSONATION_LEVEL</c>), so every engine was
    /// refused (W5.2 PC check 1).
    /// </summary>
    public static HelperClient? Read(NamedPipeServerStream pipe, IPipeClientToken token)
    {
        try
        {
            var handle = IntPtr.Zero;
            try
            {
                if (token.Impersonate(pipe))
                    handle = token.OpenThreadToken();
            }
            finally
            {
                token.RevertToSelf();
            }

            return handle == IntPtr.Zero ? null : token.Read(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>The seams of <c>--serve</c> (<see cref="HelperTaskCommands.ServeAsync"/>): what the helper runs with, unless a test replaces one.</summary>
public sealed class HelperServeOptions
{
    /// <summary>
    /// Creates the pipe's server end for the user's SID: on Windows <see cref="WindowsHelperPipe.Create"/> (first
    /// instance, remote clients refused, owned by SYSTEM, open to that user only). Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when the name is taken.
    /// </summary>
    public required Func<string, string, NamedPipeServerStream> CreatePipe { get; init; }

    /// <summary>The user connected to the pipe and their integrity level (<see cref="WindowsHelperPipe.ClientOf"/>), null when they can't be told.</summary>
    public required Func<NamedPipeServerStream, HelperClient?> Client { get; init; }

    /// <summary>How long the client accepted has to say hello (<see cref="HelperTask.HelloWithin"/>).</summary>
    public TimeSpan HelloWithin { get; init; } = HelperTask.HelloWithin;

    /// <summary>The pipe's name for the user's SID (<see cref="HelperTask.PipeName"/>; the tests' own short ones).</summary>
    public Func<string, string> PipeName { get; init; } = HelperTask.PipeName;

    /// <summary>How long it waits for its engine (<see cref="HelperTask.ConnectWithin"/>).</summary>
    public TimeSpan ConnectWithin { get; init; } = HelperTask.ConnectWithin;

    /// <summary>The clock the wait and the clean-up run on.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The longest the tree's close may take (<see cref="HelperProcess.CleanupBound"/>).</summary>
    public TimeSpan CleanupBound { get; init; } = HelperProcess.CleanupBound;

    /// <summary>Ends the process with its code (<see cref="HelperProcess.HardExit"/>).</summary>
    public Action<int> Exit { get; init; } = HelperProcess.HardExit;
}

/// <summary>
/// The helper's task commands (<see cref="HelperTask"/>): <c>--register</c> and <c>--unregister</c>, run elevated by the
/// engine's action or an installer, and <c>--serve</c>, run as SYSTEM by the task. Exit codes are
/// <see cref="HelperTask"/>'s <c>Exit…</c>; what went wrong goes to the error output.
/// </summary>
public static class HelperTaskCommands
{
    /// <summary>Runs the task command <paramref name="args"/> holds (<see cref="HelperTaskCommandLine"/>); one it doesn't take exits <see cref="HelperTask.ExitUsage"/>.</summary>
    public static Task<int> RunAsync(string[] args, IHelperTaskHost host, Func<ISensorTree> createTree, string helperVersion, HelperServeOptions serve, TextWriter error)
    {
        if (HelperTaskCommandLine.Parse(args) is not { } line)
        {
            error.WriteLine("Usage: InfoPanel.SensorHelper --register|--unregister|--serve --user <a user's SID>");
            return Task.FromResult(HelperTask.ExitUsage);
        }

        return line.Command switch
        {
            HelperTaskCommand.Register => Task.FromResult(Register(line.Sid, host, error)),
            HelperTaskCommand.Unregister => Task.FromResult(Unregister(line.Sid, host, error)),
            _ => ServeAsync(line.Sid, createTree, helperVersion, serve),
        };
    }

    /// <summary>
    /// Creates <paramref name="sid"/>'s task (<see cref="HelperTask.Xml"/>, <see cref="HelperTask.SecurityDescriptor"/>),
    /// replacing one already there, which may name another install's helper. Refuses, changing nothing, without
    /// administrator rights (<see cref="HelperTask.ExitNotElevated"/>), from a helper whose final path isn't under
    /// Program Files (<see cref="HelperTask.ExitNotInProgramFiles"/>: a binary a user could write, run by a SYSTEM task,
    /// would be a way to administrator rights), or for a SID that isn't a user here
    /// (<see cref="HelperTask.ExitNotAUser"/>), and when that final path can't be the action as it is
    /// (<see cref="HelperTask.ExitUnsafePath"/>: Windows' normalisation would change it, or it holds a Task Scheduler
    /// substitution). The task's action is that final path. Its folder's security descriptor
    /// (<see cref="HelperTask.FolderSecurityDescriptor"/>) and the task's are set every time: a task already there keeps
    /// its own through the update, so its own is set before the update too, or it isn't updated
    /// (<see cref="HelperTask.ExitTaskNotSecured"/>). The task registered is read back and must be
    /// <see cref="HelperTask.IsItsTask">SYSTEM running this helper for this user</see>, else
    /// <see cref="HelperTask.ExitTaskNotSecured"/>.
    /// </summary>
    public static int Register(string sid, IHelperTaskHost host, TextWriter error)
    {
        if (!HelperTask.IsUserSid(sid))
        {
            error.WriteLine("Not a user's SID.");
            return HelperTask.ExitUsage;
        }

        if (!host.IsElevated)
        {
            error.WriteLine("Setting the sensor helper's task up needs administrator rights.");
            return HelperTask.ExitNotElevated;
        }

        var own = host.OwnPath();
        var programFiles = host.ProgramFiles();
        if (own is null || !HelperTask.IsUnder(own, programFiles))
        {
            error.WriteLine($"The sensor helper runs from {own ?? "a place it can't tell"}, not under Program Files ({programFiles ?? "unknown"}); its task isn't set up.");
            return HelperTask.ExitNotInProgramFiles;
        }

        // The action is the very file checked: its final path as Windows' normalisation leaves it, and nothing in it the
        // Task Scheduler would replace from a run's parameters (which the user may pass: they can run the task).
        if (HelperTask.LaunchPathOf(own) is not { } plain || HelperTask.HasSubstitution(plain))
        {
            error.WriteLine($"The sensor helper's path {own} can't be a task's action as it is; its task isn't set up.");
            return HelperTask.ExitUnsafePath;
        }

        if (!host.IsUserAccount(sid))
        {
            error.WriteLine($"{sid} isn't a user on this computer.");
            return HelperTask.ExitNotAUser;
        }

        // The folder's security first, every time: nobody but an administrator creates a task in it after. Then created or
        // updated in one call: a task already there (another install's, say) is replaced only when the new one is
        // registered, and kept as it was when the Task Scheduler refuses. An update keeps the task's own security
        // descriptor (a user's, for a task they created there first), so it is set again, owner and all, every time.
        var path = HelperTask.PathOf(sid);
        var security = HelperTask.SecurityDescriptor(sid);
        bool there;
        try
        {
            host.SetFolderSecurity(HelperTask.FolderPath, HelperTask.FolderSecurityDescriptor);
            there = host.TaskExists(path);
        }
        catch (Exception ex)
        {
            error.WriteLine($"The Task Scheduler refused the sensor helper's task: {ex.Message}");
            return HelperTask.ExitSchedulerFailed;
        }

        // A task already there is made the administrators' (owner and DACL) before it is updated: a user who owns it (one
        // they created there first) could otherwise replace it between the update and its security, which would then
        // protect their task (Codex W5.2 r4 1). When that can't be done it isn't updated at all.
        if (there)
        {
            try
            {
                host.SetTaskSecurity(path, security);
            }
            catch (Exception ex)
            {
                error.WriteLine($"The sensor helper's task already at {path} couldn't be made the administrators'; it isn't updated: {ex.Message}");
                return HelperTask.ExitTaskNotSecured;
            }
        }

        HelperTaskDefinition? registered;
        try
        {
            host.RegisterTask(path, HelperTask.Xml(plain, sid), security);
            host.SetTaskSecurity(path, security);
            registered = host.TaskDefinition(path);
        }
        catch (Exception ex)
        {
            error.WriteLine($"The Task Scheduler refused the sensor helper's task: {ex.Message}");
            return HelperTask.ExitSchedulerFailed;
        }

        // What was registered, read back: SYSTEM running this helper for this user, and nothing else.
        if (!HelperTask.IsItsTask(registered, plain, sid))
        {
            error.WriteLine($"The sensor helper's task at {path} doesn't read back as SYSTEM running {plain} {HelperTask.ServeArguments(sid)}; it isn't set up.");
            return HelperTask.ExitTaskNotSecured;
        }

        return HelperTask.ExitDone;
    }

    /// <summary>
    /// Removes <paramref name="sid"/>'s task, when there is one (none is done too), then its folder when no task is left
    /// in it (one that can't be removed is said on the error output; the task is gone, so it is done); without
    /// administrator rights, <see cref="HelperTask.ExitNotElevated"/>.
    /// </summary>
    public static int Unregister(string sid, IHelperTaskHost host, TextWriter error)
    {
        if (!HelperTask.IsUserSid(sid))
        {
            error.WriteLine("Not a user's SID.");
            return HelperTask.ExitUsage;
        }

        if (!host.IsElevated)
        {
            error.WriteLine("Removing the sensor helper's task needs administrator rights.");
            return HelperTask.ExitNotElevated;
        }

        var path = HelperTask.PathOf(sid);
        try
        {
            if (host.TaskExists(path))
                host.DeleteTask(path);
        }
        catch (Exception ex)
        {
            error.WriteLine($"The Task Scheduler refused to remove the sensor helper's task: {ex.Message}");
            return HelperTask.ExitSchedulerFailed;
        }

        try
        {
            host.DeleteFolderIfEmpty(HelperTask.FolderPath);
        }
        catch (Exception ex)
        {
            // The task is gone: what was asked is done.
            error.WriteLine($"The sensor helper's task is removed; its empty folder {HelperTask.FolderPath} couldn't be: {ex.Message}");
        }

        return HelperTask.ExitDone;
    }

    /// <summary>Whether <paramref name="client"/> may be served: the user <paramref name="sid"/>, at medium integrity or above.</summary>
    public static bool IsItsEngine(HelperClient? client, string sid) =>
        client is { } c && string.Equals(c.Sid, sid, StringComparison.OrdinalIgnoreCase) && c.Integrity >= HelperTask.MediumIntegrity;

    /// <summary>
    /// Serves one engine of the user <paramref name="sid"/> on <see cref="HelperServeOptions.PipeName"/>, then ends the
    /// process through <see cref="HelperServeOptions.Exit"/>. The pipe is created first-instance
    /// (<see cref="HelperServeOptions.CreatePipe"/>): when the name is taken nothing is served
    /// (<see cref="HelperTask.ExitPipeTaken"/>). A client whose user isn't <paramref name="sid"/>, or who runs below
    /// medium integrity (<see cref="IsItsEngine"/>), is disconnected unserved and the helper waits on; one whose user
    /// can't be told is disconnected unserved and the helper ends (<see cref="HelperTask.ExitRefused"/>): only the user's
    /// processes can open the pipe, so that was its engine, which falls back to its own helper, and a helper left waiting
    /// would hold the task's one instance against that engine's next start. The one served is its only engine: when it
    /// disconnects the helper ends, as it does when none comes within
    /// <see cref="HelperServeOptions.ConnectWithin"/>, or when the one accepted says no hello within
    /// <see cref="HelperServeOptions.HelloWithin"/>.
    /// </summary>
    public static async Task<int> ServeAsync(string sid, Func<ISensorTree> createTree, string helperVersion, HelperServeOptions options)
    {
        NamedPipeServerStream pipe;
        try
        {
            pipe = options.CreatePipe(options.PipeName(sid), sid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            options.Exit(HelperTask.ExitPipeTaken);
            return HelperTask.ExitPipeTaken;
        }

        var tree = createTree();
        var server = new HelperServer(tree, helperVersion);
        var engineRuns = new TaskCompletionSource().Task; // no process to watch: the pipe's close is the engine's end
        var unknown = false; // the last client refused couldn't be told
        bool IsEngine(NamedPipeServerStream p)
        {
            var client = options.Client(p);
            unknown = client is null;
            return IsItsEngine(client, sid);
        }

        var exit = await HelperHost.ServeAsync(pipe, server, engineRuns, IsEngine,
            options.ConnectWithin, options.Time, helloWithin: options.HelloWithin, endsOnRefusal: () => unknown).ConfigureAwait(false);
        var code = exit switch
        {
            HelperExit.Refused => HelperTask.ExitRefused,
            HelperExit.TreeFailed => HelperTask.ExitTreeFailed,
            _ => HelperTask.ExitDone,
        };

        HelperProcess.CloseTree(server, tree, engineRuns, options.CleanupBound, options.Time);
        options.Exit(code);
        return code;
    }
}
