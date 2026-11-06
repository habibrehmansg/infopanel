using System.IO.Pipes;
using System.Xml.Linq;
using InfoPanel.Core.Tests.Sensors;
using InfoPanel.SensorHelper;
using Xunit;
using static InfoPanel.Core.Tests.SensorHelper.HelperTestKit;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// The sensor helper's task (<see cref="HelperTask"/>, <see cref="HelperTaskCommands"/>): its definition pinned (SYSTEM,
/// no trigger, the action under Program Files, no time limit, kept on battery, one instance, the user's read-and-run
/// grant); <c>--register</c> refusing without administrator rights, outside Program Files and for a SID that isn't a
/// user's, and replacing an existing task; <c>--unregister</c>; and <c>--serve</c> over a real pipe: first instance,
/// the client's user, one engine, its end on the engine's disconnect and after 30 s with none. Windows' own side runs in
/// <c>HelperTaskWindowsTests</c> (opt-in).
/// </summary>
public sealed class HelperTaskTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string Helper = @"C:\Program Files\InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe";
    private static readonly XNamespace T = HelperTask.TaskNamespace;

    private static XElement Definition(string xml) => XElement.Parse(xml);

    private static string Value(XElement task, params string[] path)
    {
        var element = task;
        foreach (var name in path)
            element = element.Element(T + name) ?? throw new Xunit.Sdk.XunitException($"No <{name}> in {element.Name.LocalName}.");
        return element.Value;
    }

    [Fact]
    public void TheTask_RunsAsSystem_WithNoTrigger_OnDemandOnly_OneInstance()
    {
        var task = Definition(HelperTask.Xml(Helper, Sid));

        Assert.Equal(T + "Task", task.Name);
        Assert.Equal("1.2", task.Attribute("version")?.Value);
        Assert.Equal("S-1-5-18", Value(task, "Principals", "Principal", "UserId"));
        Assert.Null(task.Element(T + "Principals")!.Element(T + "Principal")!.Element(T + "LogonType")); // SYSTEM's principal has none
        Assert.Null(task.Element(T + "Triggers"));
        Assert.Equal("true", Value(task, "Settings", "AllowStartOnDemand"));
        Assert.Equal("IgnoreNew", Value(task, "Settings", "MultipleInstancesPolicy"));
        Assert.Equal("true", Value(task, "Settings", "Enabled"));
        Assert.Equal(@"\InfoPanel 2\Sensor helper " + Sid, Value(task, "RegistrationInfo", "URI"));
    }

    [Fact]
    public void TheTask_HasNoTimeLimit_RunsOnBattery_AtNormalPriority()
    {
        var task = Definition(HelperTask.Xml(Helper, Sid));

        Assert.Equal("PT0S", Value(task, "Settings", "ExecutionTimeLimit"));
        Assert.Equal("false", Value(task, "Settings", "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value(task, "Settings", "StopIfGoingOnBatteries"));
        Assert.Equal("false", Value(task, "Settings", "IdleSettings", "StopOnIdleEnd"));
        Assert.Equal("5", Value(task, "Settings", "Priority"));
    }

    [Fact]
    public void TheTasksAction_IsTheHelperUnderProgramFiles_Serving_ItsUser_InItsOwnFolder()
    {
        var task = Definition(HelperTask.Xml(Helper, Sid));
        var exec = task.Element(T + "Actions")!.Elements(T + "Exec").Single();

        Assert.Single(task.Element(T + "Actions")!.Elements());
        Assert.Equal(Helper, exec.Element(T + "Command")!.Value);
        Assert.Equal("--serve --user " + Sid, exec.Element(T + "Arguments")!.Value);
        Assert.Equal(@"C:\Program Files\InfoPanel\sensor-helper", exec.Element(T + "WorkingDirectory")!.Value);
    }

    [Fact]
    public void ThePathIsEscaped_NotInterpretedAsMarkup()
    {
        const string odd = @"C:\Program Files\A & <B>\sensor-helper\InfoPanel.SensorHelper.exe";
        var exec = Definition(HelperTask.Xml(odd, Sid)).Element(T + "Actions")!.Element(T + "Exec")!;

        Assert.Equal(odd, exec.Element(T + "Command")!.Value);
    }

    [Fact]
    public void TheSecurityDescriptor_GrantsTheUser_ReadAndRunOnly_AdministratorsAndSystemAll_NobodyElse()
    {
        Assert.Equal($"O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{Sid})", HelperTask.SecurityDescriptor(Sid));
        Assert.Equal("O:BAG:BAD:P(A;OICI;GA;;;SY)(A;OICI;GA;;;BA)(A;;GR;;;AU)", HelperTask.FolderSecurityDescriptor);
    }

    [Fact]
    public void TheFolder_IsNot1xsTask()
    {
        // 1.4.x's Run on startup is the task \InfoPanel at the root: a folder of that name can't be created beside it,
        // and a task of that name made beside the folder hides the folder (shown on a PC). Windows compares without case.
        Assert.False(string.Equals("InfoPanel", HelperTask.FolderName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"\" + HelperTask.FolderName, HelperTask.FolderPath);
    }

    [Fact]
    public void TheTask_AndItsPipe_AreNamedForTheUser()
    {
        Assert.Equal(@"\InfoPanel 2\Sensor helper " + Sid, HelperTask.PathOf(Sid));
        Assert.Equal("ipsensors-" + Sid, HelperTask.PipeName(Sid));
        // The longest SID a user has still names a pipe the protocol takes.
        Assert.True(HelperProtocol.IsPipeName(HelperTask.PipeName("S-1-5-21-4294967295-4294967295-4294967295-4294967295")));
        Assert.Equal(TimeSpan.FromSeconds(30), HelperTask.ConnectWithin);
    }

    [Theory]
    [InlineData(Sid, true)]
    [InlineData("S-1-5-21-1-2-3-500", true)]
    [InlineData("S-1-12-1-1234567890-1234567890-1234567890-1234567890", true)]
    [InlineData("S-1-5-21-4294967295-4294967295-4294967295-4294967295", true)]
    [InlineData("S-1-5-18", false)] // SYSTEM
    [InlineData("S-1-5-32-544", false)] // Administrators
    [InlineData("S-1-1-0", false)] // Everyone
    [InlineData("S-1-5-21-1-2-3", false)]
    [InlineData("S-1-5-21-1-2-3-4-5", false)]
    [InlineData("s-1-5-21-1-2-3-1001", false)]
    [InlineData("S-1-5-21-01-2-3-1001", false)]
    [InlineData("S-1-5-21-1-2-3-4294967296", false)]
    [InlineData("S-1-5-21-1-2-3-+1", false)]
    [InlineData("S-1-5-21-1-2-3-1001 ", false)]
    [InlineData("S-1-5-21-1-2-3-1001\" /c calc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAUserAccountsSid_IsTaken(string? sid, bool taken) => Assert.Equal(taken, HelperTask.IsUserSid(sid));

    [Theory]
    [InlineData(@"\\?\C:\Program Files\InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe", @"C:\Program Files", true)]
    [InlineData(@"C:\Program Files\InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe", @"C:\Program Files\", true)]
    [InlineData(@"c:\program files\InfoPanel\x.exe", @"C:\Program Files", true)]
    [InlineData(@"C:\Program Files Evil\x.exe", @"C:\Program Files", false)]
    [InlineData(@"C:\Users\me\AppData\Local\InfoPanel\x.exe", @"C:\Program Files", false)]
    [InlineData(@"C:\Program Files", @"C:\Program Files", false)]
    [InlineData(@"\\?\UNC\server\share\Program Files\x.exe", @"\\server\share\Program Files", false)]
    [InlineData(@"C:\Program Files\x.exe", null, false)]
    [InlineData(null, @"C:\Program Files", false)]
    public void OnlyAPathInsideProgramFiles_IsUnderIt(string? path, string? folder, bool under) => Assert.Equal(under, HelperTask.IsUnder(path, folder));

    [Theory]
    [InlineData(new[] { "--register", "--user", Sid }, HelperTaskCommand.Register)]
    [InlineData(new[] { "--unregister", "--user", Sid }, HelperTaskCommand.Unregister)]
    [InlineData(new[] { "--serve", "--user", Sid }, HelperTaskCommand.Serve)]
    public void TheTaskCommandLines_AreTaken(string[] args, HelperTaskCommand command)
    {
        Assert.True(HelperTaskCommandLine.IsTaskCommand(args));
        Assert.Equal(new HelperTaskCommandLine(command, Sid), HelperTaskCommandLine.Parse(args));
    }

    [Theory]
    [InlineData("--register")]
    [InlineData("--register " + Sid)]
    [InlineData("--register --user S-1-5-18")]
    [InlineData("--register --user " + Sid + " --user " + Sid)]
    [InlineData("--install --user " + Sid)]
    public void AnyOtherCommandLine_IsRefused(string line) => Assert.Null(HelperTaskCommandLine.Parse(line.Split(' ')));

    [Fact]
    public void TheEnginesChildCommandLine_IsNoTaskCommand() =>
        Assert.False(HelperTaskCommandLine.IsTaskCommand(["--pipe", "x", "--parent", "1", "--parent-start", "2"]));

    // --register and --unregister.

    /// <summary>
    /// The Task Scheduler as Windows has it (W5.2 PC check 1): a task created takes the security descriptor it is
    /// registered with, one updated keeps its own (P3); a folder created under the root inherits the root's, which lets
    /// any user create a task in it (P4), until its own is set. A user may rewrite a task whose security descriptor lets
    /// them (<see cref="UserReplaces"/>: they own it, or it grants them full control), at any moment between two of
    /// <c>--register</c>'s calls (<see cref="AfterRegister"/>: Codex W5.2 r4 1). <see cref="TaskDefinition"/> reads the
    /// stored definition back (<see cref="ReadBack"/> changes what it says).
    /// </summary>
    private sealed class FakeHost : IHelperTaskHost
    {
        public const string Inherited = "inherited: authenticated users may create tasks";

        public Dictionary<string, string> Folders { get; } = [];

        public bool IsElevated { get; set; } = true;
        public string? Own { get; set; } = @"\\?\" + Helper;
        public string? Folder { get; set; } = @"C:\Program Files";
        public HashSet<string> Users { get; } = [Sid];
        public Dictionary<string, (string Xml, string Sddl)> Tasks { get; } = [];
        public List<string> Calls { get; } = [];
        public Exception? Refusal { get; set; }

        public string? OwnPath() => Own;

        public string? ProgramFiles() => Folder;

        public bool IsUserAccount(string sid) => Users.Contains(sid);

        public bool TaskExists(string path) => Tasks.ContainsKey(path);

        public void DeleteTask(string path)
        {
            Calls.Add("delete " + path);
            if (Refusal is { } refusal)
                throw refusal;
            Tasks.Remove(path);
        }

        public Exception? RegisterRefusal { get; set; }

        /// <summary>Runs right after each <see cref="RegisterTask"/> that registered: what a user does before the next call.</summary>
        public Action? AfterRegister { get; set; }

        public void RegisterTask(string path, string xml, string securityDescriptor)
        {
            Calls.Add("register " + path);
            if ((RegisterRefusal ?? Refusal) is { } refusal)
                throw refusal;
            Folders.TryAdd(HelperTask.FolderPath, Inherited);
            Tasks[path] = (xml, Tasks.TryGetValue(path, out var there) ? there.Sddl : securityDescriptor); // an update keeps its own
            AfterRegister?.Invoke();
        }

        /// <summary>Makes <see cref="SetTaskSecurity"/> refuse (an administrator the task's DACL leaves out).</summary>
        public Exception? SecurityRefusal { get; set; }

        public void SetTaskSecurity(string path, string securityDescriptor)
        {
            Calls.Add("security " + path);
            if ((SecurityRefusal ?? Refusal) is { } refusal)
                throw refusal;
            Tasks[path] = (Tasks[path].Xml, securityDescriptor);
        }

        /// <summary>
        /// The user <see cref="Sid"/> rewrites the task at <paramref name="path"/> to <paramref name="xml"/>, keeping its
        /// security descriptor, when that lets them: they own it, or it grants them full control. Whether they could.
        /// </summary>
        public bool UserReplaces(string path, string xml)
        {
            if (!Tasks.TryGetValue(path, out var there))
                return false;
            var sddl = there.Sddl;
            if (!sddl.StartsWith($"O:{Sid}", StringComparison.Ordinal) && !sddl.Contains($";FA;;;{Sid})", StringComparison.Ordinal)
                && !sddl.Contains($";GA;;;{Sid})", StringComparison.Ordinal))
                return false;
            Tasks[path] = (xml, sddl);
            return true;
        }

        public int Reads { get; private set; }

        /// <summary>Changes the definition <see cref="TaskDefinition"/> reads back (a Task Scheduler that stored something else).</summary>
        public Func<HelperTaskDefinition?, HelperTaskDefinition?>? ReadBack { get; set; }

        public HelperTaskDefinition? TaskDefinition(string path)
        {
            Reads++;
            var stored = Tasks.TryGetValue(path, out var there) ? DefinitionOfXml(there.Xml) : null;
            return ReadBack is { } readBack ? readBack(stored) : stored;
        }

        /// <summary>A task's XML as the Task Scheduler reads it back: its principal's user, each <c>Exec</c> action.</summary>
        private static HelperTaskDefinition? DefinitionOfXml(string xml)
        {
            XElement task;
            try
            {
                task = XElement.Parse(xml);
            }
            catch (System.Xml.XmlException)
            {
                return new HelperTaskDefinition(null, []);
            }

            var user = task.Element(T + "Principals")?.Element(T + "Principal")?.Element(T + "UserId")?.Value;
            HelperTaskAction[] actions = [.. (task.Element(T + "Actions")?.Elements() ?? []).Select(action => action.Name == T + "Exec"
                ? new HelperTaskAction(action.Element(T + "Command")?.Value ?? "", action.Element(T + "Arguments")?.Value ?? "")
                : new HelperTaskAction(null, ""))];
            return new HelperTaskDefinition(user, actions);
        }

        public void SetFolderSecurity(string folder, string securityDescriptor)
        {
            Calls.Add("folder " + folder);
            if (Refusal is { } refusal)
                throw refusal;
            Folders[folder] = securityDescriptor;
        }

        public bool DeleteFolderIfEmpty(string folder)
        {
            Calls.Add("delete folder " + folder);
            if (!Folders.ContainsKey(folder) || Tasks.Keys.Any(path => path.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase)))
                return false;
            Folders.Remove(folder);
            return true;
        }
    }

    private static Task<int> Run(FakeHost host, params string[] args) =>
        HelperTaskCommands.RunAsync(args, host, () => FakeTree.Pc(), "2.0.0-test",
            new HelperServeOptions { CreatePipe = (_, _) => throw new InvalidOperationException("not served here"), Client = _ => null }, TextWriter.Null);

    [Fact]
    public async Task Register_CreatesTheUsersTask_RunAsSystem_ItsActionThisHelpersFinalPath()
    {
        var host = new FakeHost();

        Assert.Equal(0, await Run(host, "--register", "--user", Sid));

        var (xml, sddl) = host.Tasks[HelperTask.PathOf(Sid)];
        Assert.Equal(HelperTask.Xml(Helper, Sid), xml); // \\?\ dropped
        Assert.Equal(HelperTask.SecurityDescriptor(Sid), sddl);
    }

    [Fact]
    public async Task Register_RefusesWithoutAdministratorRights()
    {
        var host = new FakeHost { IsElevated = false };

        Assert.Equal(HelperTask.ExitNotElevated, await Run(host, "--register", "--user", Sid));
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(@"\\?\C:\Users\me\AppData\Local\InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe", @"C:\Program Files")]
    [InlineData(@"\\?\C:\Program Files Evil\sensor-helper\InfoPanel.SensorHelper.exe", @"C:\Program Files")]
    [InlineData(@"\\?\C:\ip\try\sensor-helper\InfoPanel.SensorHelper.exe", null)]
    [InlineData(null, @"C:\Program Files")]
    public async Task Register_RefusesAHelperOutsideProgramFiles(string? own, string? programFiles)
    {
        var host = new FakeHost { Own = own, Folder = programFiles };

        Assert.Equal(HelperTask.ExitNotInProgramFiles, await Run(host, "--register", "--user", Sid));
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheOptInWindowsTest_TakesWhatTheBuiltHelperAnswers_ElevatedOrNot(bool elevated)
    {
        // LibreElevationWindowsTests runs a built helper (not under Program Files) with --register for a SID no user has,
        // from whatever shell runs the tests: unelevated, it refuses first for that (Codex W5.2 r3).
        var host = new FakeHost { IsElevated = elevated, Own = @"\\?\C:\ip\ws-w52\sensor-helper\InfoPanel.SensorHelper.exe" };
        host.Users.Clear();

        Assert.Contains(await Run(host, "--register", "--user", "S-1-5-21-0-0-0-1"), Sensors.Libre.LibreElevationWindowsTests.HelperRefusals);
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-5-21-1-2-3")]
    [InlineData("nobody")]
    public async Task Register_RefusesAMalformedSid(string sid)
    {
        var host = new FakeHost();

        Assert.Equal(HelperTask.ExitUsage, await Run(host, "--register", "--user", sid));
        Assert.Equal(HelperTask.ExitUsage, HelperTaskCommands.Register(sid, host, TextWriter.Null));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Register_RefusesASidThatIsntAUserAccountHere()
    {
        var host = new FakeHost();
        host.Users.Clear();

        Assert.Equal(HelperTask.ExitNotAUser, await Run(host, "--register", "--user", Sid));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Register_ReplacesTheUsersExistingTask_ThatNamedAnotherInstall()
    {
        var host = new FakeHost();
        host.Tasks[HelperTask.PathOf(Sid)] = (HelperTask.Xml(@"C:\Program Files\Old InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe", Sid), "D:(A;;GA;;;WD)");

        Assert.Equal(0, await Run(host, "--register", "--user", Sid));

        Assert.DoesNotContain(host.Calls, call => call.StartsWith("delete", StringComparison.Ordinal)); // updated in place, never deleted first
        Assert.Equal((HelperTask.Xml(Helper, Sid), HelperTask.SecurityDescriptor(Sid)), host.Tasks[HelperTask.PathOf(Sid)]);
    }

    [Fact]
    public async Task Register_ATaskSchedulerThatRefusesTheUpdate_KeepsTheTaskThatWasThere()
    {
        var host = new FakeHost { RegisterRefusal = new UnauthorizedAccessException("no") };
        var working = (HelperTask.Xml(@"C:\Program Files\Old InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe", Sid), HelperTask.SecurityDescriptor(Sid));
        host.Tasks[HelperTask.PathOf(Sid)] = working;

        Assert.Equal(HelperTask.ExitSchedulerFailed, await Run(host, "--register", "--user", Sid));

        Assert.Equal(working, host.Tasks[HelperTask.PathOf(Sid)]);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("delete", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"\\?\C:\Program Files\$(Arg0)\sensor-helper\InfoPanel.SensorHelper.exe")]
    [InlineData(@"\\?\C:\Program Files\InfoPanel\$(TaskName)\InfoPanel.SensorHelper.exe")]
    public async Task Register_RefusesAnInstallPathWithATaskSchedulerSubstitution(string own)
    {
        // A run's parameters (the user can run the task) would replace it in the action and its folder: a SYSTEM helper
        // started in a folder the user picked.
        var host = new FakeHost { Own = own };

        Assert.Equal(HelperTask.ExitUnsafePath, await Run(host, "--register", "--user", Sid));
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(@"\\?\C:\Program Files\InfoPanel.\sensor-helper\InfoPanel.SensorHelper.exe")]
    [InlineData(@"\\?\C:\Program Files\InfoPanel \sensor-helper\InfoPanel.SensorHelper.exe")]
    [InlineData(@"\\?\C:\Program Files\InfoPanel\\sensor-helper\InfoPanel.SensorHelper.exe")]
    public async Task Register_RefusesAFinalPathWindowsWouldNormaliseToAnotherFile(string own)
    {
        // Without \\?\ Windows drops the dot or the space: the action would run InfoPanel\…, not the InfoPanel.\… checked.
        var host = new FakeHost { Own = own };

        Assert.Equal(HelperTask.ExitUnsafePath, await Run(host, "--register", "--user", Sid));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Register_RefusesAFinalPathTooLongForTheAction()
    {
        var host = new FakeHost { Own = @"\\?\C:\Program Files\" + new string('i', 230) + @"\sensor-helper\InfoPanel.SensorHelper.exe" };

        Assert.Equal(HelperTask.ExitUnsafePath, await Run(host, "--register", "--user", Sid));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Register_ATaskSchedulerThatRefuses_Exits13()
    {
        var host = new FakeHost { Refusal = new UnauthorizedAccessException("no") };

        Assert.Equal(HelperTask.ExitSchedulerFailed, await Run(host, "--register", "--user", Sid));
    }

    [Fact]
    public async Task Unregister_RemovesTheUsersTask_AndNoneIsDoneToo()
    {
        var host = new FakeHost();
        host.Tasks[HelperTask.PathOf(Sid)] = ("x", "y");
        host.Tasks[HelperTask.PathOf("S-1-5-21-1-2-3-1002")] = ("x", "y");

        Assert.Equal(0, await Run(host, "--unregister", "--user", Sid));
        Assert.Equal(0, await Run(host, "--unregister", "--user", Sid));

        Assert.Equal([HelperTask.PathOf("S-1-5-21-1-2-3-1002")], host.Tasks.Keys);
        Assert.Equal(["delete " + HelperTask.PathOf(Sid)], host.Calls.Where(c => !c.StartsWith("delete folder", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Register_ResetsTheSecurityOfTheTaskAlreadyThere_EveryTime()
    {
        // W5.2 PC check 1 (P3): create-or-update kept a task's own security descriptor, a changed one included.
        var host = new FakeHost();
        var path = HelperTask.PathOf(Sid);
        Assert.Equal(0, await Run(host, "--register", "--user", Sid));
        host.Tasks[path] = (host.Tasks[path].Xml, "D:P(A;;GA;;;SY)(A;;GA;;;BA)"); // the user's read and run taken off

        Assert.Equal(0, await Run(host, "--register", "--user", Sid));

        Assert.Equal(HelperTask.SecurityDescriptor(Sid), host.Tasks[path].Sddl);
        Assert.Equal(["register " + path, "security " + path], host.Calls.Where(c => !c.StartsWith("folder", StringComparison.Ordinal)).TakeLast(2));
    }

    [Fact]
    public async Task Register_SetsTheFoldersSecurity_AndTakesOverATaskAUserCreatedThereFirst()
    {
        // W5.2 PC check 1 (P4): \InfoPanel inherited the root's write for authenticated users; a task a user created at
        // the path first stayed theirs (owner and DACL) through --register.
        var host = new FakeHost();
        var path = HelperTask.PathOf(Sid);
        host.Folders[HelperTask.FolderPath] = FakeHost.Inherited;
        var users = $"O:{Sid}D:(A;;FA;;;{Sid})";
        host.Tasks[path] = ("<Task>the user's own</Task>", users);

        Assert.Equal(0, await Run(host, "--register", "--user", Sid));

        Assert.Equal(HelperTask.FolderSecurityDescriptor, host.Folders[HelperTask.FolderPath]);
        Assert.Equal((HelperTask.Xml(Helper, Sid), HelperTask.SecurityDescriptor(Sid)), host.Tasks[path]);
        // The folder first: nobody creates in it after; the task theirs made the administrators' before its update (Codex W5.2 r4 1).
        Assert.Equal(["folder " + HelperTask.FolderPath, "security " + path, "register " + path, "security " + path], host.Calls);
    }

    /// <summary>A task the user wrote: run as themselves, their own program.</summary>
    private static string UsersOwnTask() => HelperTask.Xml(@"C:\Users\me\AppData\Local\mine.exe", Sid).Replace(HelperTask.SystemSid, Sid, StringComparison.Ordinal);

    [Fact]
    public async Task Register_ATaskAUserCreatedThereFirst_IsMadeTheAdministratorsBeforeItsUpdate_SoTheyCantReplaceItInBetween()
    {
        // Codex W5.2 r4 1: the user, owner of a task they made at the path first (full control theirs), replaced it
        // between its update and its security; the security then protected their task, and --register said done.
        var host = new FakeHost();
        var path = HelperTask.PathOf(Sid);
        host.Folders[HelperTask.FolderPath] = FakeHost.Inherited;
        host.Tasks[path] = (UsersOwnTask(), $"O:{Sid}D:(A;;FA;;;{Sid})");
        var replaced = false;
        host.AfterRegister = () => replaced = host.UserReplaces(path, UsersOwnTask());

        Assert.Equal(HelperTask.ExitDone, await Run(host, "--register", "--user", Sid));

        Assert.False(replaced); // no longer theirs to rewrite once it was updated
        Assert.Equal((HelperTask.Xml(Helper, Sid), HelperTask.SecurityDescriptor(Sid)), host.Tasks[path]);
        Assert.Equal(["folder " + HelperTask.FolderPath, "security " + path, "register " + path, "security " + path], host.Calls);
    }

    [Fact]
    public void Register_ATaskThereThatCantBeMadeTheAdministrators_IsntUpdated_Exit17()
    {
        // Fail closed: updated while the user still owns it, it could be replaced before its security is set.
        var host = new FakeHost { SecurityRefusal = new UnauthorizedAccessException("access denied") };
        var path = HelperTask.PathOf(Sid);
        var theirs = (UsersOwnTask(), $"O:{Sid}D:P(A;;FA;;;{Sid})");
        host.Tasks[path] = theirs;
        var error = new StringWriter();

        Assert.Equal(HelperTask.ExitTaskNotSecured, HelperTaskCommands.Register(Sid, host, error));

        Assert.Equal(theirs, host.Tasks[path]);
        Assert.DoesNotContain("register " + path, host.Calls);
        Assert.Contains("isn't updated", error.ToString());
    }

    public static TheoryData<string> ReadBacks() => ["the user's principal", "no principal", "another program", "other arguments", "a second action", "no action", "a non-program action", "no task"];

    private static HelperTaskDefinition? ReadBackAs(string kind, HelperTaskDefinition ours) => kind switch
    {
        "the user's principal" => ours with { UserSid = Sid },
        "no principal" => ours with { UserSid = null },
        "another program" => ours with { Actions = [new HelperTaskAction(@"C:\Users\me\AppData\Local\mine.exe", HelperTask.ServeArguments(Sid))] },
        "other arguments" => ours with { Actions = [new HelperTaskAction(Helper, HelperTask.ServeArguments(Sid) + " --x")] },
        "a second action" => ours with { Actions = [.. ours.Actions, new HelperTaskAction(@"C:\Users\me\AppData\Local\mine.exe", "")] },
        "no action" => ours with { Actions = [] },
        "a non-program action" => ours with { Actions = [new HelperTaskAction(null, "")] },
        _ => null,
    };

    [Theory]
    [MemberData(nameof(ReadBacks))]
    public void Register_ATaskThatDoesntReadBackAsSystemRunningThisHelper_Exits17(string kind)
    {
        // Codex W5.2 r4 1: --register said done without checking what was registered.
        var host = new FakeHost { ReadBack = stored => ReadBackAs(kind, stored!) };
        var error = new StringWriter();

        Assert.Equal(HelperTask.ExitTaskNotSecured, HelperTaskCommands.Register(Sid, host, error));

        Assert.Equal(1, host.Reads);
        Assert.Contains("doesn't read back", error.ToString());
    }

    [Fact]
    public async Task Register_ReadsTheTaskBack_AsSystemRunningThisHelper_ThePathsCaseAside()
    {
        var host = new FakeHost();

        Assert.Equal(HelperTask.ExitDone, await Run(host, "--register", "--user", Sid));
        Assert.Equal(1, host.Reads);

        var upper = new FakeHost { ReadBack = stored => stored! with { Actions = [new HelperTaskAction(Helper.ToUpperInvariant(), HelperTask.ServeArguments(Sid))] } };
        Assert.Equal(HelperTask.ExitDone, await Run(upper, "--register", "--user", Sid));
    }

    [Fact]
    public void Register_ATaskThatCantBeReadBack_Exits13()
    {
        var host = new ReadRefusingHost(new FakeHost());

        Assert.Equal(HelperTask.ExitSchedulerFailed, HelperTaskCommands.Register(Sid, host, TextWriter.Null));
    }

    /// <summary><see cref="FakeHost"/> whose task can't be read back.</summary>
    private sealed class ReadRefusingHost(FakeHost host) : IHelperTaskHost
    {
        public bool IsElevated => host.IsElevated;
        public string? OwnPath() => host.OwnPath();
        public string? ProgramFiles() => host.ProgramFiles();
        public bool IsUserAccount(string sid) => host.IsUserAccount(sid);
        public bool TaskExists(string path) => host.TaskExists(path);
        public void DeleteTask(string path) => host.DeleteTask(path);
        public void RegisterTask(string path, string xml, string securityDescriptor) => host.RegisterTask(path, xml, securityDescriptor);
        public void SetTaskSecurity(string path, string securityDescriptor) => host.SetTaskSecurity(path, securityDescriptor);
        public void SetFolderSecurity(string folder, string securityDescriptor) => host.SetFolderSecurity(folder, securityDescriptor);
        public bool DeleteFolderIfEmpty(string folder) => host.DeleteFolderIfEmpty(folder);
        public HelperTaskDefinition? TaskDefinition(string path) => throw new UnauthorizedAccessException("the Task Scheduler is busy");
    }

    [Fact]
    public async Task Register_ANewFolder_GetsItsOwnSecurity()
    {
        var host = new FakeHost();

        Assert.Equal(0, await Run(host, "--register", "--user", Sid));

        Assert.Equal(HelperTask.FolderSecurityDescriptor, host.Folders[HelperTask.FolderPath]);
    }

    [Fact]
    public async Task Unregister_RemovesTheFolder_OnceNoTaskIsLeftInIt()
    {
        // W5.2 PC check 1 (P7): --unregister left the empty \InfoPanel.
        const string other = "S-1-5-21-1-2-3-1002";
        var host = new FakeHost();
        Assert.Equal(0, await Run(host, "--register", "--user", Sid));
        host.Users.Add(other);
        Assert.Equal(0, await Run(host, "--register", "--user", other));

        Assert.Equal(0, await Run(host, "--unregister", "--user", Sid));
        Assert.True(host.Folders.ContainsKey(HelperTask.FolderPath)); // another user's task is in it

        Assert.Equal(0, await Run(host, "--unregister", "--user", other));
        Assert.False(host.Folders.ContainsKey(HelperTask.FolderPath));
    }

    [Fact]
    public async Task Unregister_AFolderThatCantBeRemoved_IsStillDone_TheTaskGone()
    {
        var host = new FakeHost();
        Assert.Equal(0, await Run(host, "--register", "--user", Sid));
        var error = new StringWriter();
        var failing = new FolderRefusingHost(host);

        Assert.Equal(HelperTask.ExitDone, HelperTaskCommands.Unregister(Sid, failing, error));

        Assert.Empty(host.Tasks);
        Assert.Contains("folder", error.ToString());
    }

    /// <summary><see cref="FakeHost"/> whose folder can't be removed.</summary>
    private sealed class FolderRefusingHost(FakeHost host) : IHelperTaskHost
    {
        public bool IsElevated => host.IsElevated;
        public string? OwnPath() => host.OwnPath();
        public string? ProgramFiles() => host.ProgramFiles();
        public bool IsUserAccount(string sid) => host.IsUserAccount(sid);
        public bool TaskExists(string path) => host.TaskExists(path);
        public void DeleteTask(string path) => host.DeleteTask(path);
        public void RegisterTask(string path, string xml, string securityDescriptor) => host.RegisterTask(path, xml, securityDescriptor);
        public void SetTaskSecurity(string path, string securityDescriptor) => host.SetTaskSecurity(path, securityDescriptor);
        public void SetFolderSecurity(string folder, string securityDescriptor) => host.SetFolderSecurity(folder, securityDescriptor);
        public bool DeleteFolderIfEmpty(string folder) => throw new UnauthorizedAccessException("the folder is in use");
        public HelperTaskDefinition? TaskDefinition(string path) => host.TaskDefinition(path);
    }

    [Fact]
    public async Task Unregister_RefusesWithoutAdministratorRights()
    {
        var host = new FakeHost { IsElevated = false };
        host.Tasks[HelperTask.PathOf(Sid)] = ("x", "y");

        Assert.Equal(HelperTask.ExitNotElevated, await Run(host, "--unregister", "--user", Sid));
        Assert.Single(host.Tasks);
    }

    [Fact]
    public void TheExitCodes_AreDistinct()
    {
        int[] codes = [HelperTask.ExitDone, HelperTask.ExitUsage, HelperTask.ExitRefused, HelperTask.ExitTreeFailed, HelperTask.ExitNotElevated,
            HelperTask.ExitNotInProgramFiles, HelperTask.ExitNotAUser, HelperTask.ExitSchedulerFailed, HelperTask.ExitPipeTaken,
            HelperTask.ExitEnvironmentLeft, HelperTask.ExitUnsafePath, HelperTask.ExitTaskNotSecured];
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Equal((HelperProcess.ExitRefused, HelperProcess.ExitTreeFailed), (HelperTask.ExitRefused, HelperTask.ExitTreeFailed));
    }

    // --serve, over a real pipe of the test's own (plain: the pipe's security is Windows', in the opt-in tests).

    private sealed class Serving
    {
        public required string Pipe { get; init; }
        public required Task<int> Run { get; init; }
        public required FakeTree Tree { get; init; }
        public required List<int> Exits { get; init; }
    }

    private static Serving Serve(Func<NamedPipeServerStream, string?>? clientUser = null, TimeProvider? time = null, string? pipe = null) =>
        ServeClients(clientUser is null ? null : p => clientUser(p) is { } user ? new HelperClient(user, HelperTask.MediumIntegrity) : null, time, pipe);

    private static Serving ServeClients(Func<NamedPipeServerStream, HelperClient?>? client, TimeProvider? time = null, string? pipe = null)
    {
        pipe ??= PipeName();
        var tree = FakeTree.Pc();
        var exits = new List<int>();
        var options = new HelperServeOptions
        {
            CreatePipe = (name, _) => new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous),
            Client = client ?? (_ => new HelperClient(Sid, HelperTask.MediumIntegrity)),
            PipeName = _ => pipe,
            Time = time ?? TimeProvider.System,
            Exit = code =>
            {
                lock (exits)
                    exits.Add(code);
            },
        };
        return new Serving { Pipe = pipe, Run = HelperTaskCommands.ServeAsync(Sid, () => tree, "2.0.0-test", options), Tree = tree, Exits = exits };
    }

    private static async Task<NamedPipeClientStream> Plain(string pipe, int timeoutMs)
    {
        var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeoutMs, Cancel);
        return client;
    }

    [Fact]
    public async Task Serve_ServesItsUsersEngine_AndEndsWhenItDisconnects()
    {
        var serving = Serve();
        var client = new SensorHelperClient(await Plain(serving.Pipe, 15_000));
        var hello = await client.HelloAsync(Cancel);
        Assert.Equal(HelperProtocol.Version, hello.Protocol);
        await client.SampleAsync(Cancel);
        Assert.False(serving.Run.IsCompleted);

        await client.DisposeAsync();

        Assert.Equal(HelperTask.ExitDone, await serving.Run.WaitAsync(Patience, Cancel));
        Assert.True(serving.Tree.Disposed);
        Assert.Equal([HelperTask.ExitDone], serving.Exits);
    }

    [Fact]
    public async Task Serve_ServesOneEngineOnly_ItsPipeGoneOnceThatEngineLeft()
    {
        var serving = Serve();
        var first = new SensorHelperClient(await Plain(serving.Pipe, 15_000));
        await first.HelloAsync(Cancel);

        await first.DisposeAsync();
        Assert.Equal(HelperTask.ExitDone, await serving.Run.WaitAsync(Patience, Cancel));
        await Assert.ThrowsAnyAsync<Exception>(() => Plain(serving.Pipe, 300));
    }

    [Fact]
    public async Task Serve_DropsAClientOfAnotherUser_Unserved_AndServesItsUserAfter()
    {
        var users = new Queue<string?>(["S-1-5-21-9-9-9-1001", "S-1-5-21-9-9-9-1002", Sid]);
        var serving = Serve(_ =>
        {
            lock (users)
                return users.Dequeue();
        });

        using (var other = await Plain(serving.Pipe, 15_000))
            Assert.Null(await Receive(other)); // disconnected unserved

        using (var another = await Plain(serving.Pipe, 15_000))
            Assert.Null(await Receive(another));

        var engine = new SensorHelperClient(await Plain(serving.Pipe, 15_000));
        await engine.HelloAsync(Cancel);
        Assert.False(serving.Run.IsCompleted);
        await engine.DisposeAsync();
        Assert.Equal(HelperTask.ExitDone, await serving.Run.WaitAsync(Patience, Cancel));
    }

    [Fact]
    public async Task Serve_AClientWhoseUserCantBeTold_IsDroppedUnserved_AndTheHelperEnds_Refused()
    {
        // W5.2 PC check 1 (P6): the engine refused because its token couldn't be read (P1) left the helper waiting 30 s,
        // and the task's IgnoreNew dropped the engine's next start (Libre off and on) all that time. Only the engine of
        // its user can open the pipe (its DACL); one it can't tell is its engine's attempt, which falls back to its own.
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
        var serving = Serve(_ => null, time);

        using (var unknown = await Plain(serving.Pipe, 15_000))
        {
            Assert.Equal(HelperTask.ExitRefused, await serving.Run.WaitAsync(Patience, Cancel)); // at once, not after 30 s
            Assert.Null(await Receive(unknown));
        }

        Assert.Equal([HelperTask.ExitRefused], serving.Exits);
        Assert.True(serving.Tree.Disposed);
        await Assert.ThrowsAnyAsync<Exception>(() => Plain(serving.Pipe, 300));
    }

    [Fact]
    public async Task Serve_DropsAClientOfItsUser_BelowMediumIntegrity_Unserved_AndServesItsEngineAfter()
    {
        // A sandboxed process of the user's (low integrity) can open the pipe for reading: it isn't served, nor held.
        var clients = new Queue<HelperClient?>([new HelperClient(Sid, 0x1000), new HelperClient(Sid, 0), new HelperClient(Sid, HelperTask.MediumIntegrity)]);
        var serving = ServeClients(_ =>
        {
            lock (clients)
                return clients.Dequeue();
        });

        using (var low = await Plain(serving.Pipe, 15_000))
            Assert.Null(await Receive(low)); // disconnected unserved

        using (var untrusted = await Plain(serving.Pipe, 15_000))
            Assert.Null(await Receive(untrusted));

        var engine = new SensorHelperClient(await Plain(serving.Pipe, 15_000));
        await engine.HelloAsync(Cancel);
        Assert.False(serving.Run.IsCompleted);
        await engine.DisposeAsync();
        Assert.Equal(HelperTask.ExitDone, await serving.Run.WaitAsync(Patience, Cancel));
    }

    [Theory]
    [InlineData(0x1000, false)]
    [InlineData(0x1fff, false)]
    [InlineData(0x2000, true)]
    [InlineData(0x2100, true)]
    [InlineData(0x3000, true)]
    public void OnlyItsUser_AtMediumIntegrityOrAbove_IsItsEngine(int integrity, bool engine)
    {
        Assert.Equal(engine, HelperTaskCommands.IsItsEngine(new HelperClient(Sid, integrity), Sid));
        Assert.False(HelperTaskCommands.IsItsEngine(new HelperClient("S-1-5-21-9-9-9-1001", integrity), Sid));
        Assert.False(HelperTaskCommands.IsItsEngine(null, Sid));
    }

    [Fact]
    public async Task Serve_EndsWhenTheClientAcceptedSaysNoHelloWithin5Seconds()
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
        var serving = Serve(time: time);
        await WaitFor(() => time.TimerCount > 0); // the 30 s
        using var silent = await Plain(serving.Pipe, 15_000);
        await WaitFor(() => time.TimerCount > 1); // and the 5 s, once it was accepted

        time.Advance(TimeSpan.FromSeconds(4.9));
        await Task.Delay(200, Cancel);
        Assert.False(serving.Run.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(HelperTask.ExitDone, await serving.Run.WaitAsync(Patience, Cancel));
        Assert.Null(await Receive(silent)); // closed on it, nothing answered
        Assert.Equal([HelperTask.ExitDone], serving.Exits);
    }

    [Fact]
    public async Task Serve_AnEngineThatSaidHello_IsServedOn_PastTheFiveSeconds()
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
        var serving = Serve(time: time);
        var engine = new SensorHelperClient(await Plain(serving.Pipe, 15_000));
        await engine.HelloAsync(Cancel);

        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(200, Cancel);

        Assert.False(serving.Run.IsCompleted);
        await engine.SampleAsync(Cancel);
        await engine.DisposeAsync();
        Assert.Equal(HelperTask.ExitDone, await serving.Run.WaitAsync(Patience, Cancel));
    }

    [Fact]
    public void TheHello_IsAwaitedFiveSeconds() => Assert.Equal(TimeSpan.FromSeconds(5), HelperTask.HelloWithin);

    [Fact]
    public async Task Serve_EndsAfter30Seconds_WhenNoEngineConnects()
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
        var serving = Serve(time: time);
        await WaitFor(() => time.TimerCount > 0);

        time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(200, Cancel);
        Assert.False(serving.Run.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HelperTask.ExitDone, await serving.Run.WaitAsync(Patience, Cancel));
        await Assert.ThrowsAnyAsync<Exception>(() => Plain(serving.Pipe, 300));
    }

    [Fact]
    public async Task Serve_TheThirtySeconds_CountClientsOfAnotherUser()
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
        var serving = Serve(_ => "S-1-5-21-9-9-9-1001", time);
        await WaitFor(() => time.TimerCount > 0);
        using (var other = await Plain(serving.Pipe, 15_000))
            Assert.Null(await Receive(other));

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(HelperTask.ExitDone, await serving.Run.WaitAsync(Patience, Cancel));
    }

    [Fact]
    public async Task Serve_ServesNothing_WhenItsPipeExistsAlready()
    {
        var pipe = PipeName();
        using var squatter = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var created = 0;
        var tree = FakeTree.Pc();
        var exits = new List<int>();
        var options = new HelperServeOptions
        {
            CreatePipe = (name, _) =>
            {
                created++;
                return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            },
            Client = _ => new HelperClient(Sid, HelperTask.MediumIntegrity),
            PipeName = _ => pipe,
            Exit = exits.Add,
        };

        Assert.Equal(HelperTask.ExitPipeTaken, await HelperTaskCommands.ServeAsync(Sid, () => tree, "2.0.0-test", options).WaitAsync(Patience, Cancel));
        Assert.Equal(1, created);
        Assert.Equal([HelperTask.ExitPipeTaken], exits);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition never held.");
            await Task.Delay(10, Cancel);
        }
    }
}
