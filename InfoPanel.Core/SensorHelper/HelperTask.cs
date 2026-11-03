using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace InfoPanel.SensorHelper;

/// <summary>
/// The sensor helper's task: the way it reads every sensor with administrator rights while the engine runs without them.
/// One scheduled task per registered user, <c>\InfoPanel\Sensor helper &lt;user SID&gt;</c>, run as SYSTEM, never under
/// the user's token (so it never takes the user's environment, which any program of the user can write), with no
/// trigger: the user's unelevated engine starts it on demand (<c>schtasks /Run</c>), which its security descriptor
/// allows that user alone (read and run). Its action is the helper in Program Files, <c>--serve --user &lt;SID&gt;</c>:
/// it serves one engine of that user on the pipe <see cref="PipeName"/>, owned by SYSTEM and open to that user only, and
/// ends when that engine disconnects or none connects within <see cref="ConnectWithin"/>. <c>--register</c> creates the
/// task (run elevated, from Program Files only) and <c>--unregister</c> removes it (<see cref="HelperTaskCommands"/>).
/// Everything here is the same in the helper and the engine, which compiles it too.
/// </summary>
public static class HelperTask
{
    /// <summary>The task's principal and the pipe's owner: LocalSystem.</summary>
    public const string SystemSid = "S-1-5-18";

    /// <summary>The Task Scheduler folder the tasks are in.</summary>
    public const string FolderName = "InfoPanel";

    /// <summary>The folder's full path: <c>\InfoPanel</c>.</summary>
    public const string FolderPath = @"\" + FolderName;

    /// <summary>
    /// The folder's security descriptor, set by every <c>--register</c>: owned by Administrators, protected from the
    /// root's (which lets authenticated users create tasks: one a user made first at the task's path would stay theirs),
    /// SYSTEM and Administrators full control, inherited by what is in it (without, an administrator can't update a task
    /// a user made there first: the PC, W5.2 fixer 4), authenticated users read (each engine reads its own task's action).
    /// </summary>
    public const string FolderSecurityDescriptor = "O:BAG:BAD:P(A;OICI;GA;;;SY)(A;OICI;GA;;;BA)(A;;GR;;;AU)";

    /// <summary>The priority the task's process runs at: 5, Windows' normal class (the Task Scheduler's default, 7, is below normal), as the helper the engine starts runs at the engine's.</summary>
    public const int Priority = 5;

    /// <summary>How long the helper serving a task waits for its engine before it ends.</summary>
    public static readonly TimeSpan ConnectWithin = TimeSpan.FromSeconds(30);

    /// <summary>How long the client the helper accepted has to say hello before the helper ends (an engine says it at once).</summary>
    public static readonly TimeSpan HelloWithin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The lowest integrity level the helper serves (Windows' medium, <c>SECURITY_MANDATORY_MEDIUM_RID</c>): the user's
    /// unelevated engine runs at it. A process of the user's at low integrity (a sandboxed one) may still open the pipe
    /// for reading, since Windows' integrity policy blocks only writing up.
    /// </summary>
    public const int MediumIntegrity = 0x2000;

    /// <summary>The task's namespace, Task Scheduler 1.2 (Windows 7 and later).</summary>
    public static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>The task's name in its folder.</summary>
    public static string Name(string sid) => "Sensor helper " + sid;

    /// <summary>The task's full path: <c>\InfoPanel\Sensor helper &lt;SID&gt;</c>.</summary>
    public static string PathOf(string sid) => @"\" + FolderName + @"\" + Name(sid);

    /// <summary>The pipe the task's helper serves its user's engine on (at most 62 characters, a name <see cref="HelperProtocol.IsPipeName"/> takes).</summary>
    public static string PipeName(string sid) => "ipsensors-" + sid;

    /// <summary>The task's arguments: <c>--serve --user &lt;SID&gt;</c>.</summary>
    public static string ServeArguments(string sid) => "--serve --user " + sid;

    /// <summary>What the engine runs elevated to set the task up: <c>--register --user &lt;SID&gt;</c>.</summary>
    public static string RegisterArguments(string sid) => "--register --user " + sid;

    /// <summary>What the engine runs elevated to remove it: <c>--unregister --user &lt;SID&gt;</c>.</summary>
    public static string UnregisterArguments(string sid) => "--unregister --user " + sid;

    /// <summary>
    /// The task's security descriptor, set by every <c>--register</c>: owned by Administrators (a task a user created
    /// first stays theirs otherwise, and its owner may rewrite its DACL), protected from its folder's, SYSTEM and
    /// Administrators full control, the registered user read and run (<c>GRGX</c>), so their unelevated engine can start
    /// it and read its action, and nobody else anything.
    /// </summary>
    public static string SecurityDescriptor(string sid) => $"O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{sid})";

    /// <summary>
    /// Whether <paramref name="sid"/> is written as a user's SID: a local or domain user's
    /// (<c>S-1-5-21-a-b-c-rid</c>) or a Microsoft Entra user's (<c>S-1-12-1-a-b-c-d</c>), each part a 32-bit number,
    /// in the canonical form (no sign, no leading zero, no spaces). Whether that user exists here is
    /// Windows' to say (<see cref="IHelperTaskHost.IsUserAccount"/>).
    /// </summary>
    public static bool IsUserSid(string? sid)
    {
        if (sid is null || sid.Length > 64)
            return false;
        string rest;
        if (sid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
            rest = sid[9..];
        else if (sid.StartsWith("S-1-12-1-", StringComparison.Ordinal))
            rest = sid[9..];
        else
            return false;

        var parts = rest.Split('-');
        if (parts.Length != 4)
            return false;
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 10 || (part.Length > 1 && part[0] == '0'))
                return false;
            foreach (var c in part)
            {
                if (c is < '0' or > '9')
                    return false;
            }

            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="path"/>, a file's final path (with or without Windows' <c>\\?\</c>), is inside
    /// <paramref name="folder"/> (Program Files, from the known-folder API), compared without case as Windows does.
    /// A path that names the folder itself, a <c>\\?\UNC\</c> path, or no folder at all is not.
    /// </summary>
    public static bool IsUnder(string? path, string? folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder))
            return false;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return false;
        var plain = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        var root = folder.TrimEnd('\\') + @"\";
        return root.Length > 1 && plain.Length > root.Length && plain.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The path a file is run by (a task's action, the shell's launch): the open handle's final path
    /// <paramref name="finalPath"/> without Windows' <c>\\?\</c> prefix (<c>\\?\UNC\</c> as <c>\\</c>), only when that
    /// names the very same file: the rest fits in <c>MAX_PATH</c>, and Windows' normalisation, which the prefix turned
    /// off, changes nothing in it (no name ends in a dot or a space, none is empty, and on Windows
    /// <see cref="Path.GetFullPath(string)"/> gives it back as it is). Otherwise null: it isn't run at all.
    /// </summary>
    public static string? LaunchPathOf(string finalPath)
    {
        const int MaxPath = 260; // with the terminating null
        var plain = finalPath.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + finalPath[8..]
            : finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? finalPath[4..]
            : finalPath;
        return plain.Length < MaxPath && NormalisedAsItIs(plain) ? plain : null;
    }

    /// <summary>
    /// Whether Windows' normalisation leaves <paramref name="path"/> (<c>C:\…</c> or <c>\\server\share\…</c>) as it is: it
    /// drops a name's trailing dots and spaces (<c>verified.</c> would be <c>verified</c>, another folder), folds empty
    /// names and resolves <c>.</c> and <c>..</c>.
    /// </summary>
    private static bool NormalisedAsItIs(string path)
    {
        var names = path.StartsWith(@"\\", StringComparison.Ordinal) ? path[2..] : path;
        foreach (var name in names.Split('\\'))
        {
            if (name.Length == 0 || name.EndsWith('.') || name.EndsWith(' '))
                return false;
        }

        return !OperatingSystem.IsWindows() || string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="path"/> holds a Task Scheduler substitution (<c>$(Arg0)</c> and the like), which it replaces in a task's action, its folder and its arguments when the task runs.</summary>
    public static bool HasSubstitution(string path) => path.Contains("$(", StringComparison.Ordinal);

    /// <summary>
    /// The task's definition: run as SYSTEM; no trigger (started on demand only); one instance at a time, a second
    /// start ignored while one runs; no execution time limit (the default 72 hours would end a helper that serves for
    /// days); started and kept on battery; normal priority (<see cref="Priority"/>); its action the helper at
    /// <paramref name="helperPath"/> (its final path, in Program Files) with <see cref="ServeArguments"/>, in the
    /// helper's own folder.
    /// </summary>
    public static string Xml(string helperPath, string sid)
    {
        var t = TaskNamespace;
        var folder = helperPath[..Math.Max(0, helperPath.LastIndexOf('\\'))];
        var task = new XElement(t + "Task", new XAttribute("version", "1.2"),
            new XElement(t + "RegistrationInfo",
                new XElement(t + "Description", "Reads every sensor with administrator rights for InfoPanel, for the user it names; started by that user's InfoPanel."),
                new XElement(t + "URI", PathOf(sid))),
            new XElement(t + "Principals",
                new XElement(t + "Principal", new XAttribute("id", "Author"),
                    new XElement(t + "UserId", SystemSid),
                    new XElement(t + "RunLevel", "HighestAvailable"))),
            new XElement(t + "Settings",
                new XElement(t + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(t + "DisallowStartIfOnBatteries", "false"),
                new XElement(t + "StopIfGoingOnBatteries", "false"),
                new XElement(t + "AllowHardTerminate", "true"),
                new XElement(t + "StartWhenAvailable", "false"),
                new XElement(t + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(t + "IdleSettings",
                    new XElement(t + "StopOnIdleEnd", "false"),
                    new XElement(t + "RestartOnIdle", "false")),
                new XElement(t + "AllowStartOnDemand", "true"),
                new XElement(t + "Enabled", "true"),
                new XElement(t + "Hidden", "false"),
                new XElement(t + "RunOnlyIfIdle", "false"),
                new XElement(t + "WakeToRun", "false"),
                new XElement(t + "ExecutionTimeLimit", "PT0S"),
                new XElement(t + "Priority", Priority.ToString(CultureInfo.InvariantCulture))),
            new XElement(t + "Actions", new XAttribute("Context", "Author"),
                new XElement(t + "Exec",
                    new XElement(t + "Command", helperPath),
                    new XElement(t + "Arguments", ServeArguments(sid)),
                    new XElement(t + "WorkingDirectory", folder))));
        return task.ToString();
    }

    /// <summary><c>--register</c>, <c>--unregister</c> or <c>--serve</c> did what it was asked (or <c>--serve</c>'s engine disconnected, or none came within <see cref="ConnectWithin"/>).</summary>
    public const int ExitDone = 0;

    /// <summary>A command line the helper doesn't take, a malformed SID among them.</summary>
    public const int ExitUsage = 2;

    /// <summary><c>--serve</c>: the helper refused what its engine sent (as <see cref="HelperProcess.ExitRefused"/>), or a client whose user it couldn't tell.</summary>
    public const int ExitRefused = 3;

    /// <summary><c>--serve</c>: LibreHardwareMonitor's computer failed to open (as <see cref="HelperProcess.ExitTreeFailed"/>).</summary>
    public const int ExitTreeFailed = 4;

    /// <summary><c>--register</c> or <c>--unregister</c> without administrator rights.</summary>
    public const int ExitNotElevated = 10;

    /// <summary><c>--register</c> from a helper whose final path isn't under Program Files.</summary>
    public const int ExitNotInProgramFiles = 11;

    /// <summary><c>--register</c> for a SID that isn't a user's on this computer.</summary>
    public const int ExitNotAUser = 12;

    /// <summary><c>--register</c> or <c>--unregister</c>: the Task Scheduler refused (what it said is on the error output).</summary>
    public const int ExitSchedulerFailed = 13;

    /// <summary><c>--serve</c>: the pipe exists already (another helper, or a squatter): nothing is served.</summary>
    public const int ExitPipeTaken = 14;

    /// <summary>
    /// Not the helper's own: the command processor the engine runs <c>--register</c> and <c>--unregister</c> through
    /// found a variable of .NET's it couldn't clear, and didn't run the helper (<c>LibreElevation.CommandProcessorArguments</c>).
    /// </summary>
    public const int ExitEnvironmentLeft = 15;

    /// <summary>
    /// <c>--register</c> from a helper whose final path can't be a task's action as it is: Windows' normalisation would
    /// change it (a name ending in a dot or a space), it is too long, or it holds a Task Scheduler substitution
    /// (<c>$(</c>), which a run's parameters would replace.
    /// </summary>
    public const int ExitUnsafePath = 16;

    /// <summary>
    /// <c>--register</c>: the task already at the path couldn't be made the administrators' (owner and DACL) before its
    /// update, so it isn't updated; or the task registered doesn't read back as <see cref="IsItsTask">the one asked</see>.
    /// </summary>
    public const int ExitTaskNotSecured = 17;

    /// <summary>
    /// Whether <paramref name="definition"/>, read back after <c>--register</c>, is <paramref name="sid"/>'s task as it
    /// writes it: run as SYSTEM, with one action only, the helper at <paramref name="helperPath"/> (compared without case,
    /// as Windows and the engine do) with exactly <see cref="ServeArguments"/>.
    /// </summary>
    public static bool IsItsTask(HelperTaskDefinition? definition, string helperPath, string sid) =>
        definition is { UserSid: SystemSid, Actions: [{ Path: { } path, Arguments: var arguments }] }
        && string.Equals(path, helperPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(arguments, ServeArguments(sid), StringComparison.Ordinal);
}

/// <summary>What a task command line asks.</summary>
public enum HelperTaskCommand
{
    Register,
    Unregister,
    Serve,
}

/// <summary>
/// The helper's task command lines: <c>--register --user &lt;SID&gt;</c>, <c>--unregister --user &lt;SID&gt;</c> and
/// <c>--serve --user &lt;SID&gt;</c>, exactly so, the SID a user's as written (<see cref="HelperTask.IsUserSid"/>).
/// </summary>
public sealed record HelperTaskCommandLine(HelperTaskCommand Command, string Sid)
{
    /// <summary>Whether <paramref name="args"/> asks for a task command at all (rather than the engine's child's <see cref="HelperCommandLine"/>).</summary>
    public static bool IsTaskCommand(string[] args) => args.Length > 0 && args[0] is "--register" or "--unregister" or "--serve";

    /// <summary>The command line <paramref name="args"/> holds, or null when it holds anything else.</summary>
    public static HelperTaskCommandLine? Parse(string[] args)
    {
        if (args.Length != 3 || args[1] != "--user" || !HelperTask.IsUserSid(args[2]))
            return null;
        HelperTaskCommand? command = args[0] switch
        {
            "--register" => HelperTaskCommand.Register,
            "--unregister" => HelperTaskCommand.Unregister,
            "--serve" => HelperTaskCommand.Serve,
            _ => null,
        };
        return command is { } c ? new HelperTaskCommandLine(c, args[2]) : null;
    }
}
