using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using InfoPanel.Core.Tests.SensorHelper;
using InfoPanel.SensorHelper;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Libre;
using InfoPanel.Settings;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Libre;

/// <summary>
/// Installing the PawnIO driver from the bundled installer, with a fake launcher (no test runs an installer anywhere)
/// on a manual clock: only the fixed path and <c>-install -silent</c>, through the shell as <c>runas</c>; the launch by
/// the path the open handle resolves to (its final path, the open file held until the launch returns), Windows' long-path
/// prefix dropped only when the rest fits and normalising changes nothing in it, else nothing launched; the pinned SHA-256 checked before every launch, a missing file, a link, another
/// hash or another operating system meaning no installer; one at a time, for as long as the installer runs; the outcomes
/// of exits 0, 3010 and 1641, a declined prompt (1223) and any other code, a late one too; the engine stopping abandons
/// the wait, kills nothing and publishes nothing, a launch it blocked included; and after success the Libre source reads the driver again and starts a new helper.
/// After an install that ended <c>installed</c> the driver reads <c>installing</c>, and another install is refused, until
/// the engine has read the driver again and the helper started for it said hello, or no such helper will (Libre off,
/// the source stopped, its start failed); a read racing that end never gives the state from before the install.
/// (<c>PawnIoWindowsTests</c> launches for real on Windows.)
/// </summary>
public sealed class PawnIoInstallerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "infopanel-pawnio-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly string _sha256;
    private readonly TimerTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly List<LogEvent> _log = [];
    private readonly FakeLauncher _launcher = new();
    private int _installed;

    public PawnIoInstallerTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "pawnio"));
        _path = Path.Combine(_folder, "pawnio", "PawnIO_setup.exe");
        var bytes = "MZ not really an installer"u8.ToArray();
        File.WriteAllBytes(_path, bytes);
        _sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private PawnIoInstaller Installer(bool supported = true, string? sha256 = null, Func<FileStream, string>? finalPath = null) =>
        new(_path, _launcher, () => Interlocked.Increment(ref _installed), supported, _time,
            new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new Sink(_log)).CreateLogger(), sha256 ?? _sha256, finalPath);

    private static async Task Ended(PawnIoInstaller installer) => await installer.Run!.WaitAsync(HelperTestKit.Patience, Ct);

    [Fact]
    public void TheBundledInstaller_IsPinned_ByVersionAndHash_AtOneFixedPlaceBesideTheEngine()
    {
        Assert.Equal("2.2.0", PawnIoInstaller.BundledVersion);
        Assert.Equal("1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032", PawnIoInstaller.BundledSha256);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "pawnio", "PawnIO_setup.exe"), PawnIoInstaller.DefaultPath);
    }

    [Fact]
    public async Task AnInstall_RunsOnlyTheFixedPath_WithInstallSilent_ThroughTheShell_AsRunas()
    {
        var installer = Installer();
        Assert.Equal("2.2.0", installer.Available);

        Assert.Equal(PawnIoStart.Started, installer.Start());
        var process = await _launcher.Launched(0);
        var start = _launcher.Starts.Single();
        Assert.Equal(_path, start.FileName);
        Assert.Equal("-install -silent", start.Arguments);
        Assert.Empty(start.ArgumentList);
        Assert.True(start.UseShellExecute);
        Assert.Equal("runas", start.Verb);
        Assert.Equal(Path.Combine(_folder, "pawnio"), start.WorkingDirectory);
        Assert.True(installer.Installing);

        process.Exit(0);
        await Ended(installer);
        Assert.False(installer.Installing);
    }

    [Fact]
    public async Task TheHash_IsCheckedBeforeEveryLaunch_AnotherIsNoInstaller_LoggedOnce()
    {
        var installer = Installer();
        Assert.Equal(PawnIoStart.Started, installer.Start());
        (await _launcher.Launched(0)).Exit(0);
        await Ended(installer);

        // The file replaced (its size and time may even stay): the launch is refused, and nothing runs.
        File.WriteAllBytes(_path, "MZ not really an installer, changed"u8.ToArray());
        Assert.Equal(PawnIoStart.NoInstaller, installer.Start());
        Assert.Equal(PawnIoStart.NoInstaller, installer.Start());
        Assert.Single(_launcher.Starts);
        Assert.Null(installer.Available);
        Assert.False(installer.Installing);
        Assert.Single(_log, e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("SHA-256"));
    }

    [Fact]
    public async Task AFileReplacedWithItsSizeAndTimeKept_IsStillRefusedAtTheLaunch_AndNoLongerOffered()
    {
        var installer = Installer();
        Assert.Equal("2.2.0", installer.Available);
        Assert.Equal(PawnIoStart.Started, installer.Start());
        (await _launcher.Launched(0)).Exit(0);
        await Ended(installer);

        // The same length and time: only the launch's own hash can tell.
        var written = File.GetLastWriteTimeUtc(_path);
        File.WriteAllBytes(_path, "MZ not really an installeR"u8.ToArray());
        File.SetLastWriteTimeUtc(_path, written);
        Assert.Equal(PawnIoStart.NoInstaller, installer.Start());
        Assert.Single(_launcher.Starts);
        Assert.Null(installer.Available);
    }

    [Fact]
    public void AnotherPinnedHash_IsNoInstaller_AndNothingIsLaunched()
    {
        var installer = Installer(sha256: PawnIoInstaller.BundledSha256);

        Assert.Null(installer.Available);
        Assert.Equal(PawnIoStart.NoInstaller, installer.Start());
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public void AMissingFile_IsNoInstaller()
    {
        File.Delete(_path);
        var installer = Installer();

        Assert.Null(installer.Available);
        Assert.Equal(PawnIoStart.NoInstaller, installer.Start());
        Assert.Empty(_launcher.Starts);
        Assert.False(installer.Installing);
    }

    [Fact]
    public void AnotherOperatingSystem_OrAReplay_IsNoInstaller_EvenWithTheFile()
    {
        var installer = Installer(supported: false);

        Assert.Null(installer.Available);
        Assert.Equal(PawnIoStart.NoInstaller, installer.Start());
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public async Task OneInstall_RunsAtATime()
    {
        var installer = Installer();
        Assert.Equal(PawnIoStart.Started, installer.Start());
        var process = await _launcher.Launched(0);

        Assert.Equal(PawnIoStart.Busy, installer.Start());
        Assert.Equal(PawnIoStart.Busy, installer.Start());
        Assert.Single(_launcher.Starts);

        process.Exit(PawnIoInstaller.RebootRequired);
        await Ended(installer);
        Assert.Equal(PawnIoStart.Started, installer.Start());
        (await _launcher.Launched(1)).Exit(0);
        await Ended(installer);
        Assert.Equal(2, _launcher.Starts.Count);
    }

    [Theory]
    [InlineData(0, "installed", null, 1)]
    [InlineData(3010, "restartNeeded", null, 0)]
    [InlineData(1641, "restartNeeded", null, 0)]
    [InlineData(5, "failed", 5, 0)]
    [InlineData(-2147024891, "failed", -2147024891, 0)]
    [InlineData(1223, "failed", 1223, 0)] // an exit code of 1223 is the installer's own; only the launch's 1223 is the prompt
    public async Task AnExit_IsItsOutcome_AndOnlySuccess_StartsTheHelperAgain(int code, string outcome, int? reported, int installed)
    {
        var installer = Installer();
        Assert.Null(installer.LastInstall);
        Assert.Equal(PawnIoStart.Started, installer.Start());
        (await _launcher.Launched(0)).Exit(code);
        await Ended(installer);

        Assert.Equal(new PawnIoOutcome(outcome, reported), installer.LastInstall);
        Assert.Equal(installed, Volatile.Read(ref _installed));
        Assert.False(installer.Installing);
        Assert.Contains(_log, e => e.RenderMessage().Contains("exited with code " + code));
    }

    [Fact]
    public async Task ThePromptDeclined_IsDeclined_AndChangesNothing()
    {
        _launcher.Refusal = () => new Win32Exception(PawnIoInstaller.Declined, "The operation was canceled by the user.");
        var installer = Installer();

        Assert.Equal(PawnIoStart.Started, installer.Start());
        await Ended(installer);

        Assert.Equal(new PawnIoOutcome("declined", null), installer.LastInstall);
        Assert.Equal(0, Volatile.Read(ref _installed));
        Assert.Contains(_log, e => e.RenderMessage().Contains("declined"));
        Assert.Equal(PawnIoStart.Started, installer.Start());
        await Ended(installer);
    }

    [Fact]
    public async Task ALaunchThatFailsOtherwise_IsFailed_WithNoCode()
    {
        _launcher.Refusal = () => new Win32Exception(2, "The system cannot find the file specified.");
        var installer = Installer();

        Assert.Equal(PawnIoStart.Started, installer.Start());
        await Ended(installer);

        Assert.Equal(new PawnIoOutcome("failed", null), installer.LastInstall);
        Assert.Equal(0, Volatile.Read(ref _installed));
    }

    [Fact]
    public async Task TheLaunch_NamesTheFileTheOpenHandleResolvesTo_NotTheFixedPathAgain_HeldOpenUntilItReturns()
    {
        // Windows' final path of the open handle: here a stand-in, as a junction on the fixed path would resolve it.
        var resolved = Path.Combine(_folder, "elsewhere", "PawnIO_setup.exe");
        FileStream? held = null;
        var installer = Installer(finalPath: stream =>
        {
            held = stream;
            return resolved;
        });
        var openAtLaunch = false;
        _launcher.OnStart = () => openAtLaunch = held is { CanRead: true };

        Assert.Equal(PawnIoStart.Started, installer.Start());
        (await _launcher.Launched(0)).Exit(0);
        await Ended(installer);

        var start = _launcher.Starts.Single();
        Assert.Equal(resolved, start.FileName);
        Assert.Equal(Path.Combine(_folder, "elsewhere"), start.WorkingDirectory);
        Assert.Equal(("-install -silent", "runas"), (start.Arguments, start.Verb));
        Assert.Equal(_path, held!.Name);
        Assert.True(openAtLaunch);
        Assert.False(held.CanRead); // let go once the launch returned
        Assert.Equal(new PawnIoOutcome("installed", null), installer.LastInstall);
    }

    [Theory]
    [InlineData(@"\\?\C:\Program Files\InfoPanel\pawnio\PawnIO_setup.exe", @"C:\Program Files\InfoPanel\pawnio\PawnIO_setup.exe")]
    [InlineData(@"\\?\UNC\server\share\InfoPanel\pawnio\PawnIO_setup.exe", @"\\server\share\InfoPanel\pawnio\PawnIO_setup.exe")]
    [InlineData(@"C:\InfoPanel\pawnio\PawnIO_setup.exe", @"C:\InfoPanel\pawnio\PawnIO_setup.exe")]
    // Names Windows' normalisation leaves as they are: 8.3-looking ones, dots inside or leading, three dots inside.
    [InlineData(@"\\?\Q:\NOSUCH~1\pawnio\PawnIO_setup.exe", @"Q:\NOSUCH~1\pawnio\PawnIO_setup.exe")]
    [InlineData(@"\\?\C:\InfoPanel 2.0\v2.2.0\.pawnio\PawnIO_setup.exe", @"C:\InfoPanel 2.0\v2.2.0\.pawnio\PawnIO_setup.exe")]
    [InlineData(@"\\?\C:\a...b\pawnio\PawnIO_setup.exe", @"C:\a...b\pawnio\PawnIO_setup.exe")]
    public void TheLaunchPath_DropsWindowsLongPathPrefix_WhenTheRestFits(string finalPath, string launched) =>
        Assert.Equal(launched, PawnIoInstaller.LaunchPathOf(finalPath));

    [Theory]
    [InlineData(@"\\?\C:\temp\verified.\PawnIO_setup.exe")] // without \\?\, Windows drops the dot: C:\temp\verified, another folder
    [InlineData(@"\\?\C:\temp\verified..\PawnIO_setup.exe")]
    [InlineData(@"\\?\C:\temp\verified \PawnIO_setup.exe")] // a trailing space, dropped the same way
    [InlineData(@"\\?\C:\temp\verified\PawnIO_setup.exe.")]
    [InlineData(@"\\?\C:\temp\verified\PawnIO_setup.exe ")]
    [InlineData(@"\\?\C:\temp\..\pawnio\PawnIO_setup.exe")] // a literal `..` name, which would go up a folder
    [InlineData(@"\\?\C:\temp\.\pawnio\PawnIO_setup.exe")]
    [InlineData(@"\\?\C:\temp\\pawnio\PawnIO_setup.exe")] // an empty name, which would be folded away
    [InlineData(@"\\?\UNC\server\share.\pawnio\PawnIO_setup.exe")]
    [InlineData(@"C:\temp\verified.\PawnIO_setup.exe")]
    public void ALaunchPathWindowsWouldNormaliseIntoAnother_IsRefused(string finalPath) =>
        Assert.Null(PawnIoInstaller.LaunchPathOf(finalPath));

    [Fact]
    public void ALaunchPathTooLongForTheShell_IsRefused_NeverLaunchedWithItsPrefix()
    {
        var fits = @"\\?\C:\" + new string('a', 259 - 3);
        var tooLong = @"\\?\C:\" + new string('a', 260 - 3);

        Assert.Equal(259, PawnIoInstaller.LaunchPathOf(fits)?.Length);
        Assert.Null(PawnIoInstaller.LaunchPathOf(tooLong));
    }

    [Fact]
    public async Task AnInstallerWhosePathCantBeLaunchedSafely_IsFailed_WithNoCode_Logged_AndNothingIsLaunched()
    {
        // Its final path names a folder `verified.`: launched without \\?\, Windows would run C:\temp\verified's file.
        FileStream? held = null;
        var installer = Installer(finalPath: stream =>
        {
            held = stream;
            return @"\\?\C:\temp\verified.\PawnIO_setup.exe";
        });

        Assert.Equal(PawnIoStart.Started, installer.Start());
        await Ended(installer);

        Assert.Empty(_launcher.Starts);
        Assert.Equal(new PawnIoOutcome("failed", null), installer.LastInstall);
        Assert.False(installer.Installing);
        Assert.Equal(0, Volatile.Read(ref _installed));
        Assert.False(held!.CanRead); // let go
        Assert.Single(_log, e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("can't be launched safely"));
    }

    [Fact]
    public void AnInstallerThatIsALink_IsRefused_AndNotOffered_EvenWithThePinnedHash()
    {
        // Making a symbolic link on Windows needs Developer Mode or an administrator: PawnIoWindowsTests runs it there.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows this case runs in PawnIoWindowsTests (INFOPANEL_WINDOWS_TESTS=1, elevated).");
        // The pinned file elsewhere, and the fixed place a link to it.
        var real = Path.Combine(_folder, "real-setup.exe");
        File.Move(_path, real);
        File.CreateSymbolicLink(_path, real);
        var installer = Installer();

        Assert.Null(installer.Available);
        Assert.Equal(PawnIoStart.NoInstaller, installer.Start());
        Assert.Equal(PawnIoStart.NoInstaller, installer.Start());
        Assert.Empty(_launcher.Starts);
        Assert.False(installer.Installing);
        Assert.Single(_log, e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("is a link"));
    }

    [Fact]
    public async Task AnInstallerThatDoesntEnd_IsWaitedFor_HoweverLong_OneAtATime_AndItsLateOutcomeCounts()
    {
        var registry = new FakePawnIoRegistry();
        var libreLauncher = new ScriptedLauncher(helper =>
        {
            helper.Hello = new HelloAnswer(HelperProtocol.Version, "2.0.0-test", false, PawnIoDriver.Read(registry).State);
            helper.Sample = () => ScriptedHelper.Answer(1, 5f);
        });
        using var rig = new LibreRig(_ => libreLauncher, driver: registry);
        rig.Open();
        var installer = new PawnIoInstaller(_path, _launcher, rig.Source.DriverChanged, supported: true, _time, rig.Logger, _sha256);
        Assert.Equal(PawnIoStart.Started, installer.Start());
        var process = await _launcher.Launched(0);
        await process.Waited.Task.WaitAsync(HelperTestKit.Patience, Ct);

        // A prompt left open for a day: still installing, and every other install refused (409).
        foreach (var later in new[] { TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(23) })
        {
            _time.Advance(later);
            await Task.Delay(50, Ct);
            Assert.True(installer.Installing);
            Assert.Equal(PawnIoStart.Busy, installer.Start());
        }

        Assert.Single(_launcher.Starts);
        Assert.Null(installer.LastInstall);
        Assert.False(process.LetGoWhileRunning);

        // It ends at last: its outcome is the install's, and the helper starts again with the driver read again.
        registry.Value = "2.2.0.0";
        process.Exit(0);
        await Ended(installer);
        Assert.Equal(new PawnIoOutcome("installed", null), installer.LastInstall);
        Assert.False(installer.Installing);
        rig.Poll(LibreSource.Interval);
        Assert.Equal(2, libreLauncher.Starts);
        Assert.Equal(new PawnIoReading("installed", "2.2.0.0"), rig.Source.Driver);
        Assert.Equal(PawnIoStart.Started, installer.Start());
        (await _launcher.Launched(1)).Exit(0);
        await Ended(installer);
    }

    [Fact]
    public async Task TheEngineStopping_AbandonsTheWait_WithNoOutcome_AndKillsNothing()
    {
        var installer = Installer();
        Assert.Equal(PawnIoStart.Started, installer.Start());
        var process = await _launcher.Launched(0);
        await process.Waited.Task.WaitAsync(HelperTestKit.Patience, Ct);

        installer.Dispose();
        await Ended(installer);

        Assert.Null(installer.LastInstall);
        Assert.False(installer.Installing);
        Assert.Equal(0, Volatile.Read(ref _installed));
        Assert.Contains(_log, e => e.RenderMessage().Contains("left running"));
        // Let go of, never killed (the interface has no way to): its end, later, changes nothing.
        process.Exit(0);
        await Task.Delay(50, Ct);
        Assert.Null(installer.LastInstall);
        Assert.Equal(0, Volatile.Read(ref _installed));
    }

    [Theory]
    [InlineData(true)] // the prompt declined: the launch throws 1223
    [InlineData(false)] // the launch returns an installer that has already exited 0
    public async Task TheEngineStopping_DuringABlockedLaunch_PublishesNoOutcome_AndCallsNothingBack(bool declined)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        _launcher.OnStart = () =>
        {
            entered.TrySetResult();
            release.Wait(HelperTestKit.Patience);
        };
        if (declined)
            _launcher.Refusal = () => new Win32Exception(PawnIoInstaller.Declined, "The operation was canceled by the user.");
        else
            _launcher.ExitAtLaunch = 0;
        var installer = Installer();

        Assert.Equal(PawnIoStart.Started, installer.Start());
        await entered.Task.WaitAsync(HelperTestKit.Patience, Ct);
        installer.Dispose(); // while the launch hasn't returned
        release.Set();
        await Ended(installer);

        Assert.Single(_launcher.Starts);
        Assert.Null(installer.LastInstall);
        Assert.False(installer.Installing);
        Assert.Equal(0, Volatile.Read(ref _installed));
        Assert.DoesNotContain(_log, e => e.RenderMessage().Contains("the install ended"));
    }

    [Fact]
    public async Task AnInstallThatEndsWell_StartsANewSensorHelper_WithTheDriverReadAgain()
    {
        var registry = new FakePawnIoRegistry();
        var libreLauncher = new ScriptedLauncher(helper =>
        {
            helper.Hello = new HelloAnswer(HelperProtocol.Version, "2.0.0-test", false, PawnIoDriver.Read(registry).State);
            helper.Sample = () => ScriptedHelper.Answer(1, 5f);
        });
        using var rig = new LibreRig(_ => libreLauncher, driver: registry);
        rig.Open();
        Assert.Equal((SourceStateKind.Limited, "noDriver"), (rig.State.Kind, rig.State.Reason?.Code));
        var installer = new PawnIoInstaller(_path, _launcher, rig.Source.DriverChanged, supported: true, _time, rig.Logger, _sha256);

        Assert.Equal(PawnIoStart.Started, installer.Start());
        registry.Value = "2.2.0.0"; // what the installer writes
        (await _launcher.Launched(0)).Exit(0);
        await Ended(installer);
        rig.Poll(LibreSource.Interval);

        Assert.Equal(2, libreLauncher.Starts);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(new PawnIoReading("installed", "2.2.0.0"), rig.Source.Driver);
    }

    /// <summary>
    /// Helpers that say the driver as the real one does without administrator rights (the PC's case: PawnIO installed is
    /// <c>notLoaded</c>), from the registry the installer writes; <paramref name="starting"/> is called as each starts,
    /// before its hello.
    /// </summary>
    private static ScriptedLauncher Helpers(FakePawnIoRegistry registry, Action? starting = null) => new(helper =>
    {
        starting?.Invoke();
        helper.Hello = new HelloAnswer(HelperProtocol.Version, "2.0.0-test", false, PawnIoDriver.OfHelper(PawnIoDriver.Read(registry), false));
        helper.Sample = () => ScriptedHelper.Answer(1, 5f);
    });

    /// <summary>The installer as the engine wires it to the Libre source.</summary>
    private PawnIoInstaller WiredTo(LibreRig rig) => new(_path, _launcher, rig.Source.DriverChanged, supported: true, _time, rig.Logger, _sha256);

    /// <summary>The driver as <c>GET /api/sensors/sources</c> reports it.</summary>
    private static PawnIoReading Reported(PawnIoInstaller installer, LibreRig rig) =>
        PawnIoInstaller.Report(installer, () => rig.Source.DriverSettling, () => rig.Source.Driver);

    /// <summary>An install as <c>POST /api/sensors/sources/libre/driver</c> starts it.</summary>
    private static PawnIoStart Install(PawnIoInstaller installer, LibreRig rig) => installer.Start(() => rig.Source.DriverSettling);

    [Fact]
    public async Task AfterAnInstallThatEndedWell_TheDriverIsInstalling_UntilTheNewHelperSaysHello_ThenWhatThatHelperSays()
    {
        var registry = new FakePawnIoRegistry();
        PawnIoInstaller? installer = null;
        LibreRig? rig = null;
        var whileStarting = new List<PawnIoReading>();
        // A new helper starting (the engine has read the driver again), before its hello: what a client reads meanwhile.
        var helpers = Helpers(registry, () =>
        {
            if (installer is not null)
                whileStarting.Add(Reported(installer, rig!));
        });
        rig = new LibreRig(_ => helpers, driver: registry);
        using var owned = rig;
        rig.Open();
        installer = WiredTo(rig);
        Assert.Equal(new PawnIoReading("missing", null), Reported(installer, rig));

        Assert.Equal(PawnIoStart.Started, Install(installer, rig));
        var process = await _launcher.Launched(0);
        Assert.Equal("installing", Reported(installer, rig).State);

        // The installer writes the registry and exits 0: its own wait is over, but the engine hasn't read the driver again.
        registry.Value = "2.2.0.0";
        process.Exit(0);
        await Ended(installer);
        Assert.Equal(new PawnIoOutcome("installed", null), installer.LastInstall);
        Assert.False(installer.Installing);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("installing", Reported(installer, rig).State);
            Assert.True(rig.Source.DriverSettling);
            rig.Time.Advance(TimeSpan.FromMilliseconds(500)); // short of the next round
        }

        Assert.Equal(1, helpers.Starts);

        // The next round reads it and starts a new helper: installing while it starts, then what its hello says.
        rig.Poll(LibreSource.Interval);
        Assert.Equal(2, helpers.Starts);
        Assert.Equal(["installing"], whileStarting.Select(r => r.State));
        Assert.Equal(new PawnIoReading("notLoaded", "2.2.0.0"), Reported(installer, rig));
        Assert.False(rig.Source.DriverSettling);
        Assert.Equal((SourceStateKind.Limited, "notElevated"), (rig.State.Kind, rig.State.Reason?.Code));
    }

    [Fact]
    public async Task AnotherInstall_IsRefused_UntilTheNewHelperSaysHello()
    {
        var registry = new FakePawnIoRegistry();
        var helpers = Helpers(registry);
        using var rig = new LibreRig(_ => helpers, driver: registry);
        rig.Open();
        var installer = WiredTo(rig);
        Assert.Equal(PawnIoStart.Started, Install(installer, rig));
        registry.Value = "2.2.0.0";
        (await _launcher.Launched(0)).Exit(0);
        await Ended(installer);

        // The installer has exited; the engine hasn't read the driver it installed: one install at a time includes this.
        Assert.Equal(PawnIoStart.Busy, Install(installer, rig));
        Assert.Equal(PawnIoStart.Busy, Install(installer, rig));
        Assert.Single(_launcher.Starts);

        rig.Poll(LibreSource.Interval);
        Assert.Equal(2, helpers.Starts);
        Assert.Equal(PawnIoStart.Started, Install(installer, rig));
        (await _launcher.Launched(1)).Exit(0);
        await Ended(installer);
        Assert.Equal(2, _launcher.Starts.Count);
    }

    [Theory]
    [InlineData("libreTurnedOff", null)]
    [InlineData("sourceStopped", null)]
    [InlineData("sourceStoppedWhileTheInstallerRan", null)]
    [InlineData("theNewHelpersStartFailed", null)]
    // An older PawnIO before it, the helper unelevated: its hello said `notLoaded`, and a helper retired by any of the
    // ends no longer speaks for the driver.
    [InlineData("libreTurnedOff", "2.0.0.0")]
    [InlineData("sourceStopped", "2.0.0.0")]
    [InlineData("sourceStoppedWhileTheInstallerRan", "2.0.0.0")]
    [InlineData("theNewHelpersStartFailed", "2.0.0.0")]
    public async Task Installing_EndsAtOnce_WithWhatTheRegistrySays_WhenNoNewHelperWillSayHello(string end, string? before)
    {
        var registry = new FakePawnIoRegistry { Value = before };
        var helpers = Helpers(registry);
        using var rig = new LibreRig(_ => helpers, driver: registry);
        rig.Open();
        if (before is not null)
            Assert.Equal(new PawnIoReading("notLoaded", before), rig.Source.Driver);
        var installer = WiredTo(rig);
        Assert.Equal(PawnIoStart.Started, Install(installer, rig));
        var process = await _launcher.Launched(0);
        registry.Value = "2.2.0.0";
        if (end == "sourceStoppedWhileTheInstallerRan")
            rig.Source.Dispose();
        process.Exit(0);
        await Ended(installer);
        Assert.Equal(new PawnIoOutcome("installed", null), installer.LastInstall);

        switch (end)
        {
            case "libreTurnedOff":
                Assert.Equal("installing", Reported(installer, rig).State);
                rig.Settings = LibreSettings.Default with { Enabled = false };
                rig.Poll(LibreSource.Interval);
                Assert.Equal(1, helpers.Starts);
                Assert.Equal((SourceStateKind.Off, "disabled"), (rig.State.Kind, rig.State.Reason?.Code));
                break;
            case "sourceStopped":
                Assert.Equal("installing", Reported(installer, rig).State);
                rig.Source.Dispose(); // the source's stop closes its sampler
                Assert.Equal(1, helpers.Starts);
                break;
            case "theNewHelpersStartFailed":
                Assert.Equal("installing", Reported(installer, rig).State);
                helpers.Refuse = () => new IOException("The helper's pipe broke.");
                rig.Poll(LibreSource.Interval);
                Assert.Equal(2, helpers.Starts);
                Assert.Equal((SourceStateKind.Unavailable, "helperFailed"), (rig.State.Kind, rig.State.Reason?.Code));
                break;
        }

        // Not installing, and never the pre-install `missing`: the registry as read after the installer's exit.
        Assert.False(rig.Source.DriverSettling);
        Assert.Equal(new PawnIoReading("installed", "2.2.0.0"), Reported(installer, rig));
        Assert.Equal(PawnIoStart.Started, Install(installer, rig));
        (await _launcher.Launched(1)).Exit(5);
        await Ended(installer);
    }

    [Theory]
    [InlineData("declined")]
    [InlineData("restartNeeded")]
    [InlineData("failed")]
    public async Task AnInstallThatEndedOtherwise_EndsInstalling_AtTheInstallersExit(string outcome)
    {
        var registry = new FakePawnIoRegistry();
        var helpers = Helpers(registry);
        using var rig = new LibreRig(_ => helpers, driver: registry);
        rig.Open();
        var installer = WiredTo(rig);
        if (outcome == "declined")
            _launcher.Refusal = () => new Win32Exception(PawnIoInstaller.Declined, "The operation was canceled by the user.");

        Assert.Equal(PawnIoStart.Started, Install(installer, rig));
        if (outcome != "declined")
            (await _launcher.Launched(0)).Exit(outcome == "restartNeeded" ? PawnIoInstaller.RebootRequired : 5);
        await Ended(installer);

        Assert.Equal(outcome, installer.LastInstall?.Outcome);
        Assert.False(rig.Source.DriverSettling);
        Assert.Equal(new PawnIoReading("missing", null), Reported(installer, rig));
        _launcher.Refusal = null;
        Assert.Equal(PawnIoStart.Started, Install(installer, rig));
        (await _launcher.Launched(1)).Exit(5);
        await Ended(installer);
        Assert.Equal(1, helpers.Starts);
    }

    [Theory]
    [InlineData(true)] // the installer still runs as the read begins
    [InlineData(false)] // it has exited 0; the round that reads the driver again hasn't run
    public async Task ASourcesReadRacingTheInstallsEnd_NeverGivesTheStateFromBeforeTheInstall(bool running)
    {
        var registry = new FakePawnIoRegistry();
        var helpers = Helpers(registry);
        using var rig = new LibreRig(_ => helpers, driver: registry);
        rig.Open();
        var installer = WiredTo(rig);
        Assert.Equal(PawnIoStart.Started, Install(installer, rig));
        var process = await _launcher.Launched(0);
        registry.Value = "2.2.0.0";
        if (!running)
        {
            process.Exit(0);
            await Ended(installer);
        }

        // The race, run once right after the read's first look at the source, whichever that is: the installer exits 0,
        // its end is taken, and the round reads the driver again and starts the new helper, which says hello.
        var raced = false;
        void Race()
        {
            if (raced)
                return;
            raced = true;
            process.Exit(0);
            Assert.True(installer.Run!.Wait(HelperTestKit.Patience));
            rig.Poll(LibreSource.Interval);
            Assert.Equal(2, helpers.Starts);
        }

        var read = PawnIoInstaller.Report(installer,
            () =>
            {
                var settling = rig.Source.DriverSettling;
                Race();
                return settling;
            },
            () =>
            {
                var driver = rig.Source.Driver;
                Race();
                return driver;
            });

        Assert.True(raced);
        Assert.Equal("installing", read.State);
        Assert.Equal(new PawnIoReading("notLoaded", "2.2.0.0"), Reported(installer, rig));
    }

    /// <summary>Records each start and hands back a process the test ends; never starts anything.</summary>
    private sealed class FakeLauncher : IPawnIoLauncher
    {
        private readonly List<TaskCompletionSource<FakeProcess>> _launched = [];

        public List<ProcessStartInfo> Starts { get; } = [];

        public Func<Exception>? Refusal { get; set; }

        /// <summary>Called as each launch starts, before it returns.</summary>
        public Action? OnStart { get; set; }

        /// <summary>The exit code each process launched has already exited with when the launch returns; none when null.</summary>
        public int? ExitAtLaunch { get; set; }

        public IPawnIoProcess? Start(ProcessStartInfo start)
        {
            OnStart?.Invoke();
            lock (Starts)
                Starts.Add(start);
            if (Refusal?.Invoke() is { } refusal)
                throw refusal;
            var process = new FakeProcess();
            if (ExitAtLaunch is { } code)
                process.Exit(code);
            Slot(Starts.Count - 1).TrySetResult(process);
            return process;
        }

        /// <summary>The <paramref name="index"/>th process launched, once it is.</summary>
        public Task<FakeProcess> Launched(int index) => Slot(index).Task.WaitAsync(HelperTestKit.Patience, Ct);

        private TaskCompletionSource<FakeProcess> Slot(int index)
        {
            lock (_launched)
            {
                while (_launched.Count <= index)
                    _launched.Add(new TaskCompletionSource<FakeProcess>(TaskCreationOptions.RunContinuationsAsynchronously));
                return _launched[index];
            }
        }
    }

    /// <summary>An installer's process: it exits when the test says. It can't be killed: the interface has no way to.</summary>
    private sealed class FakeProcess : IPawnIoProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Waited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DisposedTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed => DisposedTask.Task.IsCompleted;

        public void Exit(int code) => _exit.TrySetResult(code);

        public async Task WaitForExitAsync(CancellationToken cancel)
        {
            Waited.TrySetResult();
            await _exit.Task.WaitAsync(cancel);
        }

        public int ExitCode => _exit.Task.IsCompletedSuccessfully ? _exit.Task.Result : throw new InvalidOperationException("It hasn't exited.");

        /// <summary>Whether the installer let go of it (its handle closed) while it still ran.</summary>
        public bool LetGoWhileRunning { get; private set; }

        public void Dispose()
        {
            if (!_exit.Task.IsCompleted)
                LetGoWhileRunning = true;
            DisposedTask.TrySetResult();
        }
    }

    private sealed class Sink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }
}
