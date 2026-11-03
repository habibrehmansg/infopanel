using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using InfoPanel.Core.Tests.SensorHelper;
using InfoPanel.SensorHelper;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Libre;
using InfoPanel.Settings;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Libre;

/// <summary>
/// "Read all sensors" on fakes: the elevation's state (<c>unavailable</c> without Windows, with the engine elevated and
/// outside Program Files; <c>off</c>, <c>on</c> by the user's task naming this helper, <c>settingUp</c>), the change
/// (this install's helper by its final path, <c>--register</c>/<c>--unregister --user &lt;SID&gt;</c>, through the command processor
/// as <c>runas</c>; one at a time; every outcome; a new helper after <c>on</c> and <c>off</c> only); the helper's choice
/// each start (<see cref="LibreHelperChooser"/>: the task's helper over a real pipe when its owner is SYSTEM, the child
/// otherwise, on a wrong owner, a refused start or the 10 s running out, nothing sent first); and the source starting a
/// new helper when asked. Windows' own side: <c>HelperTaskWindowsTests</c> (opt-in).
/// </summary>
public sealed class LibreElevationTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string FinalHelper = @"\\?\C:\Program Files\InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe";
    private const string LaunchedHelper = @"C:\Program Files\InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "infopanel-elevation-" + Guid.NewGuid().ToString("N"));
    private readonly string _helper;
    private readonly FakeElevationSystem _system = new();
    private readonly FakeShell _shell = new();
    private int _helperChanges;

    public LibreElevationTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "sensor-helper"));
        _helper = Path.Combine(_folder, "sensor-helper", "InfoPanel.SensorHelper.exe");
        File.WriteAllBytes(_helper, "MZ a stand-in for the helper"u8.ToArray());
        _system.Final = _ => FinalHelper;
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private LibreElevation Elevation(bool windows = true) =>
        new(_helper, windows ? _system : null, _shell, () => Interlocked.Increment(ref _helperChanges));

    private static async Task Ended(LibreElevation elevation) => await elevation.Run!.WaitAsync(HelperTestKit.Patience, Ct);

    // The state.

    [Fact]
    public void WithoutWindows_ItIsUnavailable_NotWindows_AndNothingStarts()
    {
        using var elevation = Elevation(windows: false);

        Assert.Equal(new LibreElevationReport("unavailable", "notWindows", null), elevation.Report());
        Assert.Equal(ElevationStart.Unavailable, elevation.Start(on: true));
        Assert.Null(elevation.TaskUser());
        Assert.Empty(_shell.Starts);
    }

    [Fact]
    public void WithTheEngineElevated_ItIsUnavailable_EngineElevated_AndTheChildIsKept()
    {
        _system.EngineElevated = true;
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        using var elevation = Elevation();

        Assert.Equal(new LibreElevationReport("unavailable", "engineElevated", null), elevation.Report());
        Assert.Equal(ElevationStart.Unavailable, elevation.Start(on: true));
        Assert.Null(elevation.TaskUser());
        Assert.Empty(_shell.Starts);
    }

    [Theory]
    [InlineData(@"\\?\C:\Users\me\AppData\Local\InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe", @"C:\Program Files")]
    [InlineData(@"\\?\C:\Program Files Evil\sensor-helper\InfoPanel.SensorHelper.exe", @"C:\Program Files")]
    [InlineData(FinalHelper, null)]
    public void AHelperOutsideProgramFiles_IsUnavailable_NotInstalled_AndNothingIsLaunched(string finalPath, string? programFiles)
    {
        _system.Final = _ => finalPath;
        _system.ProgramFiles = programFiles;
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        using var elevation = Elevation();

        Assert.Equal(new LibreElevationReport("unavailable", "notInstalled", null), elevation.Report());
        Assert.Equal(ElevationStart.Unavailable, elevation.Start(on: true));
        Assert.Equal(ElevationStart.Unavailable, elevation.Start(on: false));
        Assert.Null(elevation.TaskUser());
        Assert.Empty(_shell.Starts);
    }

    [Fact]
    public void NoHelperAtAll_IsUnavailable_NotInstalled()
    {
        File.Delete(_helper);
        using var elevation = Elevation();

        Assert.Equal(new LibreElevationReport("unavailable", "notInstalled", null), elevation.Report());
        Assert.Equal(ElevationStart.Unavailable, elevation.Start(on: true));
    }

    [Fact]
    public void AHelperThatIsALink_IsUnavailable_NotInstalled()
    {
        var real = Path.Combine(_folder, "real.exe");
        File.Move(_helper, real);
        File.CreateSymbolicLink(_helper, real);
        using var elevation = Elevation();

        Assert.Equal(new LibreElevationReport("unavailable", "notInstalled", null), elevation.Report());
        Assert.Equal(ElevationStart.Unavailable, elevation.Start(on: true));
        Assert.Empty(_shell.Starts);
    }

    [Fact]
    public void WithNoTask_ItIsOff_AndTheChildRuns()
    {
        using var elevation = Elevation();

        Assert.Equal(new LibreElevationReport("off", null, null), elevation.Report());
        Assert.Null(elevation.TaskUser());
    }

    [Fact]
    public void WithTheUsersTaskNamingThisHelper_ItIsOn_AndTheTaskRuns()
    {
        _system.Task = (LaunchedHelper.ToLowerInvariant(), HelperTask.ServeArguments(Sid));
        using var elevation = Elevation();

        Assert.Equal(new LibreElevationReport("on", null, null), elevation.Report());
        Assert.Equal(Sid, elevation.TaskUser());
    }

    [Theory]
    [InlineData(@"C:\Program Files\Old InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe", "--serve --user " + Sid)]
    [InlineData(LaunchedHelper, "--serve --user S-1-5-21-1-2-3-1002")]
    [InlineData(LaunchedHelper, "--pipe x")]
    public void ATaskNamingAnotherInstall_OrOtherArguments_IsOff(string path, string arguments)
    {
        _system.Task = (path, arguments);
        using var elevation = Elevation();

        Assert.Equal("off", elevation.Report().State);
        Assert.Null(elevation.TaskUser());
    }

    [Fact]
    public void ATaskThatCantBeRead_IsOff_AndTheHelpersStartIsToldItCouldntBeRead()
    {
        _system.TaskRefusal = () => new UnauthorizedAccessException("denied");
        using var elevation = Elevation();

        Assert.Equal("off", elevation.Report().State);
        // Not "no task" (Codex r2a 2): the start falls back, and says why (LibreHelperChooser.TaskUnreadable).
        Assert.Throws<UnauthorizedAccessException>(() => elevation.TaskUser());
    }

    [Fact]
    public void TheStateReadsTheTask_AtMostEveryTenSeconds_TheHelpersStartReadsItAfresh()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var elevation = new LibreElevation(_helper, _system, _shell, () => { }, time);

        Assert.Equal("off", elevation.Report().State);
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        Assert.Equal("off", elevation.Report().State);
        Assert.Equal(Sid, elevation.TaskUser());
        _system.Task = null;
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal("on", elevation.Report().State);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("off", elevation.Report().State);
    }

    // The change.

    private const string HelperFolder = @"C:\Program Files\InfoPanel\sensor-helper";

    /// <summary>The listing file a launch's command line names (in the helper's folder, its name new each launch).</summary>
    private static string ListingOf(ProcessStartInfo start)
    {
        var match = System.Text.RegularExpressions.Regex.Match(start.Arguments, @"1>""(C:\\Program Files\\InfoPanel\\sensor-helper\\elevation-[0-9a-f]{32}\.tmp)""");
        Assert.True(match.Success, start.Arguments);
        return match.Groups[1].Value;
    }

    /// <summary>What the command processor is asked, written out once, by hand: the variables listed, cleared, checked, then the helper.</summary>
    private static string CommandLine(string listing, string helperArguments) =>
        "/d /e:on /v:off /s /c \"((set DOTNET_) & (set COMPlus_) & (set CORECLR_) & (set COREHOST_) & (set COR_)) 1>\"" + listing + "\" 2>nul"
        + " & (for /f \"usebackq delims==\" %v in (\"" + listing + "\") do @set \"%v=\")"
        + " & del /f /q \"" + listing + "\" 1>nul 2>nul"
        + " & (set DOTNET_) 1>nul 2>nul && exit 15 & (set COMPlus_) 1>nul 2>nul && exit 15 & (set CORECLR_) 1>nul 2>nul && exit 15"
        + " & (set COREHOST_) 1>nul 2>nul && exit 15 & (set COR_) 1>nul 2>nul && exit 15"
        + " & \"" + LaunchedHelper + "\" " + helperArguments + "\"";

    [Fact]
    public async Task SettingUp_RunsThisInstallsHelper_ThroughTheCommandProcessor_TheRuntimesVariablesCleared_AsRunas()
    {
        using var elevation = Elevation();

        Assert.Equal(ElevationStart.Started, elevation.Start(on: true));
        var start = await _shell.Started(1);

        // An elevated process takes the approving user's environment, which their own programs write: the helper, a .NET
        // program, is run by Windows' command processor, which clears .NET's variables first.
        Assert.Equal(@"C:\WINDOWS\system32\cmd.exe", start.FileName);
        Assert.Equal(CommandLine(ListingOf(start), "--register --user " + Sid), start.Arguments);
        Assert.Empty(start.ArgumentList);
        Assert.True(start.UseShellExecute);
        Assert.Equal("runas", start.Verb);
        Assert.Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.Equal(HelperFolder, start.WorkingDirectory);
        _shell.Exit(0);
        await Ended(elevation);
    }

    [Fact]
    public async Task EachLaunch_ListsTheVariables_InAFileOfItsOwn()
    {
        using var elevation = Elevation();
        elevation.Start(on: true);
        var first = await _shell.Started(1);
        _shell.Exit(0);
        await Ended(elevation);
        elevation.Start(on: false);
        var second = await _shell.Started(2);
        _shell.Exit(0);
        await Ended(elevation);

        Assert.NotEqual(ListingOf(first), ListingOf(second));
    }

    [Fact]
    public void TheCommandLine_ClearsEveryPrefixOfDotNets()
    {
        Assert.Equal(["DOTNET_", "COMPlus_", "CORECLR_", "COREHOST_", "COR_"], LibreElevation.ClearedPrefixes);
        Assert.Equal(15, HelperTask.ExitEnvironmentLeft);
    }

    [Fact]
    public void EachPrefixQuery_EndsAtThePrefix_WithNothingAfterIt()
    {
        // The command processor's SET matches a prefix as written, spaces after it included (all but the one before an
        // operator): "set CORECLR_ 1>nul 2>nul" queries "CORECLR_  ", which nothing matches, so a check written so never
        // fires (shown on Windows: LibreElevationWindowsTests). Every query, the listing's and the check's, is "(set X)".
        var line = LibreElevation.CommandProcessorArguments(LaunchedHelper, "--register --user " + Sid, HelperFolder + @"\x.tmp")!;
        foreach (var prefix in LibreElevation.ClearedPrefixes)
        {
            var queries = System.Text.RegularExpressions.Regex.Matches(line, "set " + System.Text.RegularExpressions.Regex.Escape(prefix) + "(.)");
            Assert.Equal(2, queries.Count);
            Assert.All(queries, q => Assert.Equal(")", q.Groups[1].Value));
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\100%\sensor-helper\InfoPanel.SensorHelper.exe")]
    [InlineData(@"C:\Program Files\%DOTNET_ROOT%\sensor-helper\InfoPanel.SensorHelper.exe")]
    [InlineData("C:\\Program Files\\a\tb\\InfoPanel.SensorHelper.exe")]
    public void AHelperPathTheCommandLineCantQuote_IsUnavailable_NotInstalled(string launchPath)
    {
        _system.Final = _ => @"\\?\" + launchPath;
        using var elevation = Elevation();

        Assert.Null(LibreElevation.CommandProcessorArguments(launchPath, "--register --user " + Sid, HelperFolder + @"\x.tmp"));
        Assert.Equal(new LibreElevationReport("unavailable", "notInstalled", null), elevation.Report());
        Assert.Equal(ElevationStart.Unavailable, elevation.Start(on: true));
        Assert.Empty(_shell.Starts);
    }

    [Fact]
    public async Task TheCommandProcessorFindingAVariableLeft_IsFailed_With15()
    {
        using var elevation = Elevation();

        elevation.Start(on: true);
        await _shell.Started(1);
        _shell.Exit(HelperTask.ExitEnvironmentLeft);
        await Ended(elevation);

        Assert.Equal(new LibreElevationChange("failed", 15), elevation.LastChange);
        Assert.Equal(0, _helperChanges);
    }

    [Fact]
    public async Task TheHelper_IsHeldOpen_SharedForReadingOnly_UntilTheLaunchReturns()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Sharing modes are Windows' (elsewhere they aren't enforced).");
        _shell.OnStart = () =>
        {
            Assert.ThrowsAny<IOException>(() => new FileStream(_helper, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            using var reading = new FileStream(_helper, FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        using var elevation = Elevation();
        elevation.Start(on: true);
        await _shell.Started(1);
        _shell.Exit(0);
        await Ended(elevation);

        using var writable = new FileStream(_helper, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
    }

    [Fact]
    public async Task TurningOff_RunsUnregister_TheSameWay()
    {
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        using var elevation = Elevation();

        Assert.Equal(ElevationStart.Started, elevation.Start(on: false));
        var start = await _shell.Started(1);

        Assert.Equal(@"C:\WINDOWS\system32\cmd.exe", start.FileName);
        Assert.Equal(CommandLine(ListingOf(start), "--unregister --user " + Sid), start.Arguments);
        Assert.Equal("runas", start.Verb);
        _system.Task = null;
        _shell.Exit(0);
        await Ended(elevation);
        Assert.Equal(new LibreElevationReport("off", null, new LibreElevationChange("off", null)), elevation.Report());
        Assert.Equal(1, _helperChanges);
    }

    [Fact]
    public async Task WhileAChangeRuns_ItIsSettingUp_AndAnotherIsRefused_UntilItEnds()
    {
        using var elevation = Elevation();

        Assert.Equal(ElevationStart.Started, elevation.Start(on: true));
        await _shell.Started(1);
        Assert.Equal("settingUp", elevation.Report().State);
        Assert.Equal(ElevationStart.Busy, elevation.Start(on: true));
        Assert.Equal(ElevationStart.Busy, elevation.Start(on: false));
        Assert.Single(_shell.Starts);

        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        _shell.Exit(0);
        await Ended(elevation);

        Assert.Equal(new LibreElevationReport("on", null, new LibreElevationChange("on", null)), elevation.Report());
        Assert.Equal(1, _helperChanges);
        Assert.Equal(ElevationStart.Started, elevation.Start(on: false));
        _shell.Exit(0);
    }

    [Fact]
    public async Task SetUp_ReadsTheTaskAgain_AtOnce()
    {
        using var elevation = Elevation();
        Assert.Equal("off", elevation.Report().State);

        elevation.Start(on: true);
        await _shell.Started(1);
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        _shell.Exit(0);
        await Ended(elevation);

        Assert.Equal("on", elevation.Report().State);
    }

    [Fact]
    public async Task AReadingOfTheTask_BegunBeforeASetUpEnded_DoesntOverwriteWhatWasReadAfter()
    {
        using var elevation = Elevation();
        var hold = new ManualResetEventSlim();
        _system.Hold = hold;
        var before = Task.Run(() => elevation.Report()); // reads "no task", then waits
        Assert.True(_system.Entered.Wait(HelperTestKit.Patience, Ct));

        elevation.Start(on: true);
        await _shell.Started(1);
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        _shell.Exit(0);
        await Ended(elevation);
        Assert.Equal("on", elevation.Report().State); // read after: on

        hold.Set();
        Assert.Equal("off", (await before.WaitAsync(HelperTestKit.Patience, Ct)).State); // it began before

        Assert.Equal(new LibreElevationReport("on", null, new LibreElevationChange("on", null)), elevation.Report());
    }

    [Fact]
    public async Task AReadingOfTheTask_BegunBeforeATurnOffEnded_DoesntOverwriteWhatWasReadAfter()
    {
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        using var elevation = Elevation();
        var hold = new ManualResetEventSlim();
        _system.Hold = hold;
        var before = Task.Run(() => elevation.Report());
        Assert.True(_system.Entered.Wait(HelperTestKit.Patience, Ct));

        elevation.Start(on: false);
        await _shell.Started(1);
        _system.Task = null;
        _shell.Exit(0);
        await Ended(elevation);
        Assert.Equal("off", elevation.Report().State);

        hold.Set();
        Assert.Equal("on", (await before.WaitAsync(HelperTestKit.Patience, Ct)).State);

        Assert.Equal(new LibreElevationReport("off", null, new LibreElevationChange("off", null)), elevation.Report());
    }

    [Fact]
    public async Task ThePromptDeclined_IsDeclined_AndNothingChanges()
    {
        _shell.Refusal = () => new Win32Exception(1223);
        using var elevation = Elevation();

        Assert.Equal(ElevationStart.Started, elevation.Start(on: true));
        await Ended(elevation);

        Assert.Equal(new LibreElevationReport("off", null, new LibreElevationChange("declined", null)), elevation.Report());
        Assert.Equal(0, _helperChanges);
    }

    [Theory]
    [InlineData(HelperTask.ExitNotInProgramFiles)]
    [InlineData(HelperTask.ExitNotAUser)]
    [InlineData(HelperTask.ExitSchedulerFailed)]
    [InlineData(-532462766)]
    public async Task AnyOtherExit_IsFailed_WithItsCode(int code)
    {
        using var elevation = Elevation();

        elevation.Start(on: true);
        await _shell.Started(1);
        _shell.Exit(code);
        await Ended(elevation);

        Assert.Equal(new LibreElevationReport("off", null, new LibreElevationChange("failed", code)), elevation.Report());
        Assert.Equal(0, _helperChanges);
    }

    [Fact]
    public async Task AHelperThatCouldntStart_IsFailed_WithNoCode()
    {
        _shell.Refusal = () => new Win32Exception(2);
        using var elevation = Elevation();

        elevation.Start(on: true);
        await Ended(elevation);

        Assert.Equal(new LibreElevationChange("failed", null), elevation.LastChange);
        Assert.Equal(0, _helperChanges);
    }

    [Fact]
    public async Task TheEngineStopping_AbandonsTheWait_NoOutcomeAndNoNewHelper()
    {
        var elevation = Elevation();
        elevation.Start(on: true);
        await _shell.Started(1);

        elevation.Dispose();
        await Ended(elevation);
        _shell.Exit(0);

        Assert.Null(elevation.LastChange);
        Assert.Equal(0, _helperChanges);
    }

    // The source, asked to start a new helper.

    [Fact]
    public void HelperChanged_StartsANewHelperAtTheNextRound_Once_WithNoBackoff()
    {
        var launcher = new ScriptedLauncher(helper => helper.Sample = () => ScriptedHelper.Answer(1, 5f));
        using var rig = new LibreRig(_ => launcher);
        rig.Open();
        rig.Poll(LibreSource.Interval);
        Assert.Equal(1, launcher.Starts);

        rig.Source.HelperChanged();
        rig.Poll(LibreSource.Interval);
        Assert.Equal(2, launcher.Starts);
        Assert.True(launcher.Helpers[0].Disposed);
        Assert.Equal(SourceState.Running, rig.State);

        rig.Poll(LibreSource.Interval);
        Assert.Equal(2, launcher.Starts);
    }

    [Fact]
    public void HelperChanged_WhileARestartWaits_StartsOneAtTheNextRound()
    {
        var launcher = new ScriptedLauncher(helper => helper.Sample = () => ScriptedHelper.Answer(1, 5f)) { Refuse = () => new IOException("broke") };
        using var rig = new LibreRig(_ => launcher);
        rig.Open();
        for (var i = 0; i < 200 && !rig.Log.Any(e => e.RenderMessage().Contains("restarted in 60 s")); i++)
            rig.Poll(LibreSource.Interval);
        var starts = launcher.Starts;
        rig.Poll(LibreSource.Interval);
        Assert.Equal(starts, launcher.Starts);

        launcher.Refuse = null;
        rig.Source.HelperChanged();
        rig.Poll(LibreSource.Interval);

        Assert.Equal(starts + 1, launcher.Starts);
        Assert.Equal(SourceState.Running, rig.State);
    }

    // The helper's choice, each start.

    private static HelperServeOptions TaskServe(string pipe, Func<NamedPipeServerStream, string?>? clientUser = null) => new()
    {
        CreatePipe = (name, _) => new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous),
        Client = p => (clientUser ?? (_ => Sid))(p) is { } user ? new HelperClient(user, HelperTask.MediumIntegrity) : null,
        PipeName = _ => pipe,
        Exit = _ => { },
    };

    private (LibreHelperChooser Chooser, ScriptedLauncher Child, List<string> Log) Chooser(string pipe, Func<string?>? taskUser = null, TimeSpan? connectTimeout = null,
        TimeSpan? runAgainEvery = null, Action<LibreHelperFallback?>? fellBack = null)
    {
        var child = new ScriptedLauncher();
        var log = new List<string>();
        var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new ListSink(log)).CreateLogger();
        return (new LibreHelperChooser(child, taskUser ?? (() => Sid), _system, connectTimeout, _ => pipe, log: logger, runAgainEvery: runAgainEvery, fellBack: fellBack), child, log);
    }

    /// <summary>
    /// The Task Scheduler as <c>IgnoreNew</c> has it: a start while the task's helper runs is dropped. Its helper serves
    /// on <paramref name="pipe"/>; once its engine leaves, it closes its pipe at once, then cleans up until
    /// <paramref name="cleanedUp"/> completes, and only then does the task count as ended.
    /// </summary>
    private sealed class IgnoreNewTask(string pipe, Func<Task> cleanedUp)
    {
        private readonly object _lock = new();
        private bool _running;

        public int Started { get; private set; }

        public int Dropped { get; private set; }

        /// <summary>The last helper's serving: complete once its engine left and its pipe is closed (its clean-up may go on).</summary>
        public Task<int> LastServed { get; private set; } = Task.FromResult(0);

        public void Run(string sid)
        {
            lock (_lock)
            {
                if (_running)
                {
                    Dropped++;
                    return;
                }

                _running = true;
                Started++;
            }

            var serving = HelperTaskCommands.ServeAsync(sid, () => FakeTree.Pc(), "2.0.0-test", TaskServe(pipe));
            LastServed = serving;
            _ = Task.Run(async () =>
            {
                await serving;
                await cleanedUp();
                lock (_lock)
                    _running = false;
            });
        }
    }

    /// <summary>
    /// The task's helpers one after another, for the source's tests (which start the next at once): a start waits until
    /// the helper before it has ended, its pipe gone (a client could reach that pipe before it goes, where nothing answers
    /// it), then serves on <paramref name="pipe"/> from <paramref name="tree"/> (a small PC's by default).
    /// </summary>
    private sealed class OneAfterAnother(string pipe, Func<ISensorTree>? tree = null)
    {
        private readonly Lock _lock = new();
        private Task<int> _serving = Task.FromResult(0);

        public int Started { get; private set; }

        public void Run(string sid)
        {
            lock (_lock)
            {
                Assert.True(_serving.Wait(HelperTestKit.Patience), "the helper before it never ended");
                Started++;
                _serving = HelperTaskCommands.ServeAsync(sid, tree ?? (() => FakeTree.Pc()), "2.0.0-test", TaskServe(pipe));
            }
        }
    }

    [Fact]
    public async Task ARestart_WhileTheHelperItReplacesStillCleansUp_StartsTheTaskAgain_AndGetsItsHelper()
    {
        // Libre off and on at once: the old helper's pipe is gone, its process still ends, and IgnoreNew drops the first start.
        var pipe = HelperTestKit.PipeName();
        var cleanUp = new TaskCompletionSource();
        var task = new IgnoreNewTask(pipe, () => cleanUp.Task);
        _system.Run = task.Run;
        var (chooser, child, _) = Chooser(pipe, connectTimeout: TimeSpan.FromSeconds(8), runAgainEvery: TimeSpan.FromMilliseconds(250));
        var first = await chooser.StartAsync(null, Ct);
        await first.HelloAsync(Ct);
        await first.DisposeAsync();
        await task.LastServed.WaitAsync(HelperTestKit.Patience, Ct); // its pipe is gone; it still cleans up

        var restart = chooser.StartAsync(null, Ct);
        await Task.Delay(700, Ct);
        cleanUp.SetResult(); // the old helper is gone now
        var second = await restart.WaitAsync(HelperTestKit.Patience, Ct);
        var hello = await second.HelloAsync(Ct);

        Assert.Equal(HelperProtocol.Version, hello.Protocol);
        Assert.Equal(0, child.Starts);
        Assert.Equal(2, task.Started);
        Assert.True(task.Dropped >= 1, $"dropped {task.Dropped}");
        Assert.Null(chooser.LastFallback);
        await second.DisposeAsync();
    }

    [Fact]
    public void TheTaskIsStartedAgain_EverySecond() => Assert.Equal(TimeSpan.FromSeconds(1), LibreHelperChooser.RunAgainEvery);

    [Fact]
    public async Task AFallbackWhileOn_IsReported_WithWhy_UntilAStartGetsTheTasksHelper()
    {
        var pipe = HelperTestKit.PipeName();
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        using var elevation = Elevation();
        var (chooser, _, _) = Chooser(pipe, connectTimeout: TimeSpan.FromMilliseconds(400), runAgainEvery: TimeSpan.FromMilliseconds(100), fellBack: elevation.HelperFellBack);

        await using (await chooser.StartAsync(null, Ct)) { }

        var report = elevation.Report();
        Assert.Equal("on", report.State);
        Assert.Equal(LibreHelperChooser.NotServing, report.Fallback?.Code);
        Assert.Contains("within", report.Fallback?.Message);

        _system.Run = sid => _ = HelperTaskCommands.ServeAsync(sid, () => FakeTree.Pc(), "2.0.0-test", TaskServe(pipe));
        await using (await chooser.StartAsync(null, Ct)) { }

        Assert.Equal(new LibreElevationReport("on", null, null), elevation.Report());
    }

    [Fact]
    public async Task ATaskThatCouldntBeRead_AtTheStart_IsAFallback_TheStatusReadingItLaterStillSaysSo()
    {
        // Codex r2a 2: an unreadable task isn't an absent one. The start reads it, fails, and the child serves; the
        // status, reading it again 10 s later, finds it set up: on, with why the helper running isn't the task's.
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var pipe = HelperTestKit.PipeName();
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        using var elevation = new LibreElevation(_helper, _system, _shell, () => { }, time);
        var (chooser, child, _) = Chooser(pipe, taskUser: elevation.TaskUser, fellBack: elevation.HelperFellBack);
        _system.TaskRefusal = () => new UnauthorizedAccessException("the Task Scheduler refused to read it");

        await using (await chooser.StartAsync(null, Ct)) { }

        Assert.Equal(1, child.Starts);
        Assert.Equal(0, _system.Runs);
        Assert.Equal(LibreHelperChooser.TaskUnreadable, chooser.LastFallback?.Code);
        Assert.Contains("refused to read", chooser.LastFallback?.Message);
        _system.TaskRefusal = null;
        time.Advance(LibreElevation.TaskCheckInterval);
        var report = elevation.Report();
        Assert.Equal("on", report.State);
        Assert.Equal(LibreHelperChooser.TaskUnreadable, report.Fallback?.Code);

        // A later start that gets the task's helper ends it.
        _system.Run = sid => _ = HelperTaskCommands.ServeAsync(sid, () => FakeTree.Pc(), "2.0.0-test", TaskServe(pipe));
        await using (await chooser.StartAsync(null, Ct)) { }
        Assert.Null(elevation.Report().Fallback);
    }

    [Fact]
    public async Task ATaskThatIsntSetUp_IsNoFallback()
    {
        var (chooser, child, _) = Chooser(HelperTestKit.PipeName(), taskUser: () => null);

        await using (await chooser.StartAsync(null, Ct)) { }

        Assert.Equal(1, child.Starts);
        Assert.Null(chooser.LastFallback);
    }

    [Fact]
    public async Task AFallback_IsntReported_WhenItIsOff()
    {
        using var elevation = Elevation();
        elevation.HelperFellBack(new LibreHelperFallback(LibreHelperChooser.NotServing, "nothing served its pipe within 10 s"));

        Assert.Equal(new LibreElevationReport("off", null, null), elevation.Report());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WithNoTaskUser_TheChildStarts_AndNoTaskRuns()
    {
        var (chooser, child, _) = Chooser(HelperTestKit.PipeName(), () => null);

        await using var helper = await chooser.StartAsync(null, Ct);

        Assert.Equal(1, child.Starts);
        Assert.Equal(0, _system.Runs);
    }

    [Fact]
    public async Task WithTheTask_ItsHelperIsRun_AndServed_WhenItsPipeIsSystems()
    {
        var pipe = HelperTestKit.PipeName();
        Task<int>? serving = null;
        _system.Run = sid => serving = HelperTaskCommands.ServeAsync(sid, () => FakeTree.Pc(), "2.0.0-test", TaskServe(pipe));
        var (chooser, child, _) = Chooser(pipe);

        var helper = await chooser.StartAsync(null, Ct);
        var hello = await helper.HelloAsync(Ct);
        var catalogue = await helper.CatalogueAsync(Ct);

        Assert.Equal([Sid], _system.RunSids);
        Assert.Equal(0, child.Starts);
        Assert.Equal(HelperProtocol.Version, hello.Protocol);
        Assert.NotEmpty(catalogue.Sensors);
        Assert.Null(chooser.LastFallback);
        Assert.False(helper.Exited.IsCompleted);

        await helper.DisposeAsync(); // the pipe closes: the task's helper ends
        Assert.Equal(HelperTask.ExitDone, await serving!.WaitAsync(HelperTestKit.Patience, Ct));
    }

    [Fact]
    public async Task APipeOwnedByAnyoneButSystem_IsRefused_NothingSent_AndTheChildStarts()
    {
        var pipe = HelperTestKit.PipeName();
        using var squatter = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = squatter.WaitForConnectionAsync(Ct);
        _system.Owner = _ => Sid; // the user's own process made it
        var (chooser, child, log) = Chooser(pipe);

        await using var helper = await chooser.StartAsync(null, Ct);

        Assert.Equal(1, child.Starts);
        Assert.Contains("not SYSTEM", chooser.LastFallback?.Message);
        Assert.Equal(LibreHelperChooser.NotSystem, chooser.LastFallback?.Code);
        Assert.Contains(log, line => line.Contains("didn't serve") && line.Contains(Sid));
        await accepted.WaitAsync(HelperTestKit.Patience, Ct);
        var buffer = new byte[16];
        Assert.Equal(0, await squatter.ReadAsync(buffer, Ct).AsTask().WaitAsync(HelperTestKit.Patience, Ct)); // closed, nothing written
    }

    [Fact]
    public async Task APipeWhoseOwnerCantBeTold_IsRefused_AndTheChildStarts()
    {
        var pipe = HelperTestKit.PipeName();
        using var squatter = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _ = squatter.WaitForConnectionAsync(Ct);
        _system.Owner = _ => null;
        var (chooser, child, _) = Chooser(pipe);

        await using var helper = await chooser.StartAsync(null, Ct);

        Assert.Equal(1, child.Starts);
        Assert.Equal(LibreHelperChooser.NotSystem, chooser.LastFallback?.Code);
    }

    [Fact]
    public async Task ATaskWhoseHelperNeverServes_FallsBackToTheChild_WhenTheTimeRunsOut()
    {
        var (chooser, child, _) = Chooser(HelperTestKit.PipeName(), connectTimeout: TimeSpan.FromMilliseconds(400));
        var watch = Stopwatch.StartNew();

        await using var helper = await chooser.StartAsync(null, Ct);

        Assert.Equal(1, child.Starts);
        Assert.Equal(1, _system.Runs);
        Assert.Contains("within", chooser.LastFallback?.Message);
        Assert.Equal(LibreHelperChooser.NotServing, chooser.LastFallback?.Code);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(350), HelperTestKit.Patience);
    }

    [Fact]
    public async Task ATaskWindowsRefusesToRun_FallsBackToTheChild()
    {
        _system.Run = _ => throw new IOException("schtasks /Run exited with 1: access denied");
        var (chooser, child, _) = Chooser(HelperTestKit.PipeName());

        await using var helper = await chooser.StartAsync(null, Ct);

        Assert.Equal(1, child.Starts);
        Assert.Contains("access denied", chooser.LastFallback?.Message);
        Assert.Equal(LibreHelperChooser.StartRefused, chooser.LastFallback?.Code);
    }

    [Fact]
    public async Task ATaskHelperThatRefusesTheEngine_AfterTheOwnerCheck_FallsBackToTheChild_NotServing()
    {
        // W5.2 PC check 1 (P2): the task's helper dropped every engine unserved (P1), and the engine never started its own.
        var pipe = HelperTestKit.PipeName();
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        _system.Run = sid => _ = HelperTaskCommands.ServeAsync(sid, () => FakeTree.Pc(), "2.0.0-test", TaskServe(pipe, clientUser: _ => "S-1-5-21-9-9-9-1001"));
        using var elevation = Elevation();
        var (chooser, child, _) = Chooser(pipe, fellBack: elevation.HelperFellBack);
        var watch = Stopwatch.StartNew();

        await using var helper = await chooser.StartAsync(null, Ct);

        Assert.Equal(1, child.Starts);
        Assert.Equal(LibreHelperChooser.NotServing, chooser.LastFallback?.Code);
        Assert.Contains("didn't serve", chooser.LastFallback?.Message);
        Assert.True(watch.Elapsed < LibreHelperChooser.ConnectTimeout, $"{watch.Elapsed}"); // within the same deadline
        var report = elevation.Report();
        Assert.Equal("on", report.State);
        Assert.Equal(LibreHelperChooser.NotServing, report.Fallback?.Code);
    }

    [Fact]
    public async Task ATaskHelperThatAcceptsThenCloses_FallsBackToTheChild_NotServing()
    {
        var pipe = HelperTestKit.PipeName();
        _system.Run = sid => _ = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(Ct);
        }); // closed as soon as it accepted
        var (chooser, child, _) = Chooser(pipe);

        await using var helper = await chooser.StartAsync(null, Ct);

        Assert.Equal(1, child.Starts);
        Assert.Equal(1, _system.Runs);
        Assert.Equal(LibreHelperChooser.NotServing, chooser.LastFallback?.Code);
    }

    [Fact]
    public async Task ATaskHelperThatSaidHello_IsHandedOn_ItsHelloAnsweredOnce()
    {
        var pipe = HelperTestKit.PipeName();
        _system.Run = sid => _ = HelperTaskCommands.ServeAsync(sid, () => FakeTree.Pc(), "2.0.0-test", TaskServe(pipe));
        var (chooser, child, _) = Chooser(pipe);

        await using var helper = await chooser.StartAsync(null, Ct);
        var hello = await helper.HelloAsync(Ct); // the one the start had answered
        var sample = await helper.SampleAsync(Ct);

        Assert.Equal(HelperProtocol.Version, hello.Protocol);
        Assert.NotNull(sample);
        Assert.Equal(0, child.Starts);
        Assert.Null(chooser.LastFallback);
    }

    [Fact]
    public async Task TaskHelpersLostBeforeServingASample_TwiceInARow_GiveTheNextStartToTheChild_NotServing()
    {
        // A task's helper that says hello, then fails every read (LibreHardwareMonitor failing: its sample answered
        // treeFailed) until the sampler gives it up as failed, each restart. A stop the engine chose in between (Libre
        // off) neither counts nor resets the count (Codex W5.2 r4 2: the old test counted the test's own disposal).
        var pipe = HelperTestKit.PipeName();
        var failing = true;
        Task<int> serving = Task.FromResult(0);
        FakeTree Tree()
        {
            var tree = FakeTree.Pc();
            tree.Throw = failing;
            return tree;
        }

        _system.Run = sid => serving = HelperTaskCommands.ServeAsync(sid, Tree, "2.0.0-test", TaskServe(pipe));
        var (chooser, child, _) = Chooser(pipe);

        async Task Lost(ILibreHelper helper)
        {
            var failed = await Assert.ThrowsAsync<HelperErrorException>(() => helper.SampleAsync(Ct));
            Assert.Equal(ErrorAnswer.TreeFailed, failed.Code);
            helper.GivenUp(); // as the sampler does, giving it up
            await Stopped(helper);
        }

        async Task Stopped(ILibreHelper helper)
        {
            await helper.DisposeAsync();
            await serving.WaitAsync(HelperTestKit.Patience, Ct); // its helper gone before the next start
        }

        await Lost(await chooser.StartAsync(null, Ct)); // lost: 1
        await Stopped(await chooser.StartAsync(null, Ct)); // stopped on purpose: still 1
        await Lost(await chooser.StartAsync(null, Ct)); // lost: 2
        Assert.Equal(0, child.Starts);

        await using (await chooser.StartAsync(null, Ct)) { }

        Assert.Equal(1, child.Starts);
        Assert.Equal(3, _system.Runs);
        Assert.Equal(LibreHelperChooser.NotServing, chooser.LastFallback?.Code);
        Assert.Contains("2 starts in a row", chooser.LastFallback?.Message);

        // The child had its turn: the next start tries the task again, and one that serves a sample resets the count.
        failing = false;
        var served = await chooser.StartAsync(null, Ct);
        await served.SampleAsync(Ct);
        await Stopped(served);
        failing = true;
        await Lost(await chooser.StartAsync(null, Ct)); // lost: 1
        failing = false;
        await using (await chooser.StartAsync(null, Ct)) { }
        Assert.Equal(1, child.Starts);
        Assert.Equal(6, _system.Runs);
        Assert.Null(chooser.LastFallback);
    }

    /// <summary>A tree that lists its sensors once (the catalogue a start fetches), then fails every read: LibreHardwareMonitor failing.</summary>
    private sealed class FailsAfterItsCatalogue : ISensorTree
    {
        private readonly FakeTree _inner = FakeTree.Pc();
        private int _updates;

        public bool Elevated => true;

        public string Driver => "installed";

        public void Configure(bool storage, int storageInterval) => _inner.Configure(storage, storageInterval);

        public TreeSnapshot Update() => Interlocked.Increment(ref _updates) == 1 ? _inner.Update() : throw new InvalidOperationException("LibreHardwareMonitor threw.");

        public void Dispose() => _inner.Dispose();
    }

    [Theory]
    [InlineData("its reads")]
    [InlineData("its catalogue")]
    public void TheSource_GivingUpTasksHelpersLostBeforeASample_TwiceInARow_StartsTheChild_NotServing(string failing)
    {
        // The real failures, through the sampler: each task's helper lists its sensors, then fails every read (three
        // failed rounds and the source gives it up), or fails the catalogue its start fetches (the start gives it up). The
        // second such loss sends the next start to the child.
        var pipe = HelperTestKit.PipeName();
        var task = new OneAfterAnother(pipe, failing == "its reads" ? () => new FailsAfterItsCatalogue() : () =>
        {
            var tree = FakeTree.Pc();
            tree.Throw = true;
            return tree;
        });
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        _system.Run = task.Run;
        using var elevation = Elevation();
        var child = new ScriptedLauncher(helper => helper.Sample = () => ScriptedHelper.Answer(1, 5f));
        using var rig = new LibreRig(_ => new LibreHelperChooser(child, elevation.TaskUser, _system, pipeName: _ => pipe,
            runAgainEvery: TimeSpan.FromMilliseconds(100), fellBack: elevation.HelperFellBack));

        rig.Open();
        for (var i = 0; i < 40 && child.Starts == 0; i++)
            rig.Poll(LibreSource.Interval);

        Assert.Equal(2, task.Started);
        Assert.Equal(1, child.Starts);
        Assert.Equal(LibreHelperChooser.NotServing, elevation.Report().Fallback?.Code);
        Assert.Contains("2 starts in a row", elevation.Report().Fallback?.Message);
    }

    [Fact]
    public void LibreTurnedOffAndOn_BeforeTheTasksHelperServedASample_TwiceInARow_StillGetsTheTasksHelper()
    {
        // Codex W5.2 r4 2: stops the engine asked for (Libre off, then on) before a first sample counted as failures, so
        // two quick toggles sent the next start to the child (notServing) despite a working SYSTEM helper.
        var pipe = HelperTestKit.PipeName();
        var task = new OneAfterAnother(pipe);
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        _system.Run = task.Run;
        using var elevation = Elevation();
        var child = new ScriptedLauncher();
        using var rig = new LibreRig(_ => new LibreHelperChooser(child, elevation.TaskUser, _system, pipeName: _ => pipe,
            runAgainEvery: TimeSpan.FromMilliseconds(100), fellBack: elevation.HelperFellBack));

        rig.SetQuietly(LibreSettings.Default with { Enabled = false });
        rig.Open();
        rig.Settings = rig.Settings with { Enabled = true };
        rig.Poll(LibreSource.Interval); // Libre enabled: the task's helper started (its hello and catalogue), no sample yet
        Assert.Equal(1, task.Started);
        for (var toggle = 0; toggle < 2; toggle++)
        {
            rig.Settings = rig.Settings with { Enabled = false };
            rig.Settings = rig.Settings with { Enabled = true };
            rig.Poll(LibreSource.Interval); // the off honoured (its helper stopped), a new one started, before any sample
        }

        Assert.Equal(3, task.Started);
        Assert.Equal(0, child.Starts);
        Assert.Null(elevation.Report().Fallback);
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
    }

    [Fact]
    public void TheTimeout_IsTenSeconds() => Assert.Equal(TimeSpan.FromSeconds(10), LibreHelperChooser.ConnectTimeout);

    [Fact]
    public async Task TheSource_ReadsThroughTheTasksHelper_WhenItIsSetUp()
    {
        var pipe = HelperTestKit.PipeName();
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        _system.Run = sid => _ = HelperTaskCommands.ServeAsync(sid, () => new DriverTree(FakeTree.Pc(), "installed", elevated: true), "2.0.0-test", TaskServe(pipe));
        using var elevation = Elevation();
        var child = new ScriptedLauncher();
        using var rig = new LibreRig(_ => new LibreHelperChooser(child, elevation.TaskUser, _system, pipeName: _ => pipe));

        rig.Open();

        Assert.Equal(1, _system.Runs);
        Assert.Equal(0, child.Starts);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
    }

    [Fact]
    public async Task TheSource_ReadsThroughTheChild_WhenTheTasksHelperRefusesTheEngine()
    {
        // W5.2 PC check 1 (P2): 0 sensors and "on" with no fallback; now the child's, and the fallback said.
        var pipe = HelperTestKit.PipeName();
        _system.Task = (LaunchedHelper, HelperTask.ServeArguments(Sid));
        _system.Run = sid => _ = HelperTaskCommands.ServeAsync(sid, () => FakeTree.Pc(), "2.0.0-test", TaskServe(pipe, clientUser: _ => "S-1-5-21-9-9-9-1001"));
        using var elevation = Elevation();
        var child = new ScriptedLauncher();
        using var rig = new LibreRig(_ => new LibreHelperChooser(child, elevation.TaskUser, _system, pipeName: _ => pipe, fellBack: elevation.HelperFellBack));

        rig.Open();
        rig.Poll(LibreSource.Interval);

        Assert.Equal(1, _system.Runs);
        Assert.Equal(1, child.Starts);
        Assert.Equal(SourceState.Running, rig.State); // the child's helper read
        Assert.Equal(LibreHelperChooser.NotServing, elevation.Report().Fallback?.Code);
        await Task.CompletedTask;
    }

    private sealed class ListSink(List<string> lines) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (lines)
                lines.Add(logEvent.RenderMessage());
        }
    }
}

/// <summary>Windows as the elevation's tests set it: the engine's user, Program Files, the user's task, its run, its pipe's owner.</summary>
internal sealed class FakeElevationSystem : ILibreElevationSystem
{
    public bool EngineElevated { get; set; }

    public string? UserSid { get; set; } = "S-1-5-21-1004336348-1177238915-682003330-1001";

    public string? ProgramFiles { get; set; } = @"C:\Program Files";

    public Func<FileStream, string> Final { get; set; } = file => file.Name;

    public (string Path, string Arguments)? Task { get; set; }

    public Func<Exception>? TaskRefusal { get; set; }

    public Action<string> Run { get; set; } = _ => { };

    public Func<NamedPipeClientStream, string?> Owner { get; set; } = _ => HelperTask.SystemSid;

    public List<string> RunSids { get; } = [];

    public int Runs
    {
        get
        {
            lock (RunSids)
                return RunSids.Count;
        }
    }

    public string FinalPathOf(FileStream file) => Final(file);

    /// <summary>Set: the next reading of the task takes its answer, says so (<see cref="Entered"/>), then waits for it before answering.</summary>
    public ManualResetEventSlim? Hold { get; set; }

    public ManualResetEventSlim Entered { get; } = new();

    public (string Path, string Arguments)? TaskAction(string sid)
    {
        if (TaskRefusal?.Invoke() is { } refusal)
            throw refusal;
        var answer = Task;
        if (Hold is { } hold)
        {
            Hold = null;
            Entered.Set();
            hold.Wait(HelperTestKit.Patience);
        }

        return answer;
    }

    public void RunTask(string sid)
    {
        lock (RunSids)
            RunSids.Add(sid);
        Run(sid);
    }

    public string? PipeOwner(NamedPipeClientStream pipe) => Owner(pipe);

    public string SystemDirectory { get; set; } = @"C:\WINDOWS\system32";
}

/// <summary>The shell as the elevation's tests set it: each launch recorded, its process exiting when the test says.</summary>
internal sealed class FakeShell : IPawnIoLauncher
{
    private readonly List<TaskCompletionSource<int>> _exits = [];
    private readonly List<TaskCompletionSource> _started = [new(TaskCreationOptions.RunContinuationsAsynchronously)];

    public List<ProcessStartInfo> Starts { get; } = [];

    public Func<Exception>? Refusal { get; set; }

    public Action? OnStart { get; set; }

    public IPawnIoProcess? Start(ProcessStartInfo start)
    {
        OnStart?.Invoke();
        if (Refusal?.Invoke() is { } refusal)
            throw refusal;
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Starts)
        {
            Starts.Add(start);
            _exits.Add(exit);
            _started[^1].TrySetResult();
            _started.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        return new Process(exit.Task);
    }

    /// <summary>The <paramref name="count"/>th launch, once it happened.</summary>
    public async Task<ProcessStartInfo> Started(int count)
    {
        Task wait;
        lock (Starts)
            wait = Starts.Count >= count ? Task.CompletedTask : _started[count - 1].Task;
        await wait.WaitAsync(HelperTestKit.Patience, TestContext.Current.CancellationToken);
        lock (Starts)
            return Starts[count - 1];
    }

    /// <summary>The last process launched exits with <paramref name="code"/>.</summary>
    public void Exit(int code)
    {
        lock (Starts)
            _exits[^1].TrySetResult(code);
    }

    private sealed class Process(Task<int> exit) : IPawnIoProcess
    {
        public Task WaitForExitAsync(CancellationToken cancel) => exit.WaitAsync(cancel);

        public int ExitCode => exit.Result;

        public void Dispose()
        {
        }
    }
}
