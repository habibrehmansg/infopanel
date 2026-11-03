using InfoPanel.Core.Tests.SensorHelper;
using InfoPanel.SensorHelper;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Libre;
using InfoPanel.Settings;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Libre;

/// <summary>
/// The Libre source and the PawnIO driver, on a manual clock: its state and reason follow the driver the helper's hello
/// reports; the engine reads the registry itself right before each helper starts and every 10 s and, when the driver
/// changed since then (installed outside the engine, while the helper started too), starts a new helper once, at once,
/// with no backoff; an install that ends well does the same at the next round; either, with no helper running, clears a
/// restart's wait; and the driver the API reports.
/// </summary>
public sealed class LibreDriverTests
{
    private static ScriptedLauncher Launcher(FakePawnIoRegistry? registry = null, bool elevated = false) => new(helper =>
    {
        helper.Hello = new HelloAnswer(HelperProtocol.Version, "2.0.0-test", elevated, registry is null ? "unknown" : PawnIoDriver.Read(registry).State);
        helper.Sample = () => ScriptedHelper.Answer(1, 5f);
    });

    [Theory]
    [InlineData("missing", false, SourceStateKind.Limited, "noDriver")]
    [InlineData("outdated", false, SourceStateKind.Limited, "driverOutdated")]
    [InlineData("notLoaded", false, SourceStateKind.Limited, "notElevated")]
    [InlineData("notLoaded", true, SourceStateKind.Limited, "driverNotLoaded")]
    [InlineData("installed", false, SourceStateKind.Running, null)]
    [InlineData("installed", true, SourceStateKind.Running, null)]
    [InlineData("unknown", false, SourceStateKind.Running, null)]
    public void TheSourcesState_FollowsTheDriverTheHelperReports(string driver, bool elevated, SourceStateKind state, string? reason)
    {
        using var rig = new LibreRig(time => new TreeLauncher(new DriverTree(FakeTree.Pc(), driver, elevated), time));
        rig.Open();

        Assert.Equal((state, reason), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
        rig.Poll(LibreSource.Interval);
        Assert.Equal((state, reason), (rig.State.Kind, rig.State.Reason?.Code));
    }

    [Fact]
    public void EachReason_SaysWhyInEnglish()
    {
        Assert.All(new[] { LibreSource.NoDriver, LibreSource.DriverOutdated, LibreSource.DriverNotLoaded, LibreSource.NotElevated }, reason =>
        {
            Assert.Contains("PawnIO", reason.Message);
            Assert.InRange(reason.Message.Length, 1, 256);
        });
        Assert.Equal(4, new[] { LibreSource.NoDriver, LibreSource.DriverOutdated, LibreSource.DriverNotLoaded, LibreSource.NotElevated }.Select(r => r.Code).Distinct().Count());
    }

    [Fact]
    public void ADriverInstalledOutsideTheEngine_IsNoticedAtTheNextTenSecondRead_AndTheHelperStartedAgainOnce_WithNoBackoff()
    {
        var registry = new FakePawnIoRegistry();
        var launcher = Launcher(registry);
        using var rig = new LibreRig(_ => launcher, driver: registry);
        rig.Open();
        Assert.Equal((SourceStateKind.Limited, "noDriver"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.Equal(new PawnIoReading("missing", null), rig.Source.Driver);

        // Installed from pawnio.eu or winget: nothing happens before the engine's next read, 10 s after the last.
        registry.Value = "2.2.0.0";
        for (var i = 0; i < 4; i++)
            rig.Poll(LibreSource.Interval);
        Assert.Equal(1, launcher.Starts);
        Assert.Equal((SourceStateKind.Limited, "noDriver"), (rig.State.Kind, rig.State.Reason?.Code));

        // At 10 s: the running helper stopped (the engine asked) and a new one started in the same round.
        rig.Poll(LibreSource.Interval);
        Assert.Equal(2, launcher.Starts);
        Assert.True(launcher.Helpers[0].Disposed);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(new PawnIoReading("installed", "2.2.0.0"), rig.Source.Driver);
        Assert.Contains(rig.Log, e => e.Level == LogEventLevel.Information && e.RenderMessage().Contains("missing to installed 2.2.0.0"));
        Assert.DoesNotContain(rig.Log, e => e.Level >= LogEventLevel.Warning);

        // Once: the driver stays as the new helper read it.
        for (var i = 0; i < 20; i++)
            rig.Poll(LibreSource.Interval);
        Assert.Equal(2, launcher.Starts);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
    }

    [Fact]
    public void ADriverInstalledWhileTheHelperStarts_AfterItsHello_IsNoticedAtTheNextRead_AndTheHelperStartedAgainOnce()
    {
        var registry = new FakePawnIoRegistry();
        var launcher = new ScriptedLauncher(helper =>
        {
            helper.Hello = new HelloAnswer(HelperProtocol.Version, "2.0.0-test", false, PawnIoDriver.Read(registry).State);
            helper.Sample = () => ScriptedHelper.Answer(1, 5f);
            // Installed outside the engine after this helper read the driver, before the engine has its catalogue.
            registry.Value = "2.2.0.0";
        });
        using var rig = new LibreRig(_ => launcher, driver: registry);
        rig.Open();
        Assert.Equal((SourceStateKind.Limited, "noDriver"), (rig.State.Kind, rig.State.Reason?.Code));

        for (var i = 0; i < 5; i++)
            rig.Poll(LibreSource.Interval);
        Assert.Equal(2, launcher.Starts);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(new PawnIoReading("installed", "2.2.0.0"), rig.Source.Driver);

        // Once: the new helper read what the engine reads.
        for (var i = 0; i < 20; i++)
            rig.Poll(LibreSource.Interval);
        Assert.Equal(2, launcher.Starts);
        Assert.Equal(SourceState.Running, rig.State);
    }

    /// <summary>A helper that fails to start (until the test lets it), its restart's wait grown to 60 s.</summary>
    private static (LibreRig Rig, ScriptedLauncher Launcher) WaitingSixtySeconds(FakePawnIoRegistry registry)
    {
        var launcher = Launcher(registry);
        launcher.Refuse = () => new IOException("The helper's pipe broke.");
        var rig = new LibreRig(_ => launcher, driver: registry);
        rig.Open();
        for (var i = 0; i < 200 && !rig.Log.Any(e => e.RenderMessage().Contains("restarted in 60 s")); i++)
            rig.Poll(LibreSource.Interval);
        Assert.Contains(rig.Log, e => e.RenderMessage().Contains("restarted in 60 s"));
        // Just after a failed start: the next one is 60 s away.
        var starts = launcher.Starts;
        rig.Poll(LibreSource.Interval);
        Assert.Equal(starts, launcher.Starts);
        return (rig, launcher);
    }

    [Fact]
    public void AnInstallThatEndedWell_WithNoHelperRunning_ClearsTheRestartsWait_AndStartsOneAtTheNextRound()
    {
        var registry = new FakePawnIoRegistry();
        var (rig, launcher) = WaitingSixtySeconds(registry);
        using var _ = rig;
        var starts = launcher.Starts;

        launcher.Refuse = null;
        registry.Value = "2.2.0";
        rig.Source.DriverChanged();
        rig.Poll(LibreSource.Interval);

        Assert.Equal(starts + 1, launcher.Starts);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(new PawnIoReading("installed", "2.2.0"), rig.Source.Driver);
        Assert.Contains(rig.Log, e => e.RenderMessage().Contains("not after its wait"));
    }

    [Fact]
    public void ADriverInstalledOutsideTheEngine_WithNoHelperRunning_ClearsTheRestartsWait_Once()
    {
        var registry = new FakePawnIoRegistry();
        var (rig, launcher) = WaitingSixtySeconds(registry);
        using var _ = rig;
        var starts = launcher.Starts;

        // Found at the next 10 s read, well within the 60 s wait: one start then, which fails again.
        registry.Value = "2.2.0";
        for (var i = 0; i < 5; i++)
            rig.Poll(LibreSource.Interval);
        Assert.Equal(starts + 1, launcher.Starts);

        // Once: the wait after that start is the backoff's again, not cleared at every read.
        for (var i = 0; i < 10; i++)
            rig.Poll(LibreSource.Interval);
        Assert.Equal(starts + 1, launcher.Starts);
    }

    [Fact]
    public void AnUpdatedOrRemovedDriver_StartsTheHelperAgainToo_EachChangeOnce()
    {
        var registry = new FakePawnIoRegistry { Value = "1.0.3.0" };
        var launcher = Launcher(registry);
        using var rig = new LibreRig(_ => launcher, driver: registry);
        rig.Open();
        Assert.Equal((SourceStateKind.Limited, "driverOutdated"), (rig.State.Kind, rig.State.Reason?.Code));

        registry.Value = "2.2.0";
        rig.Poll(TimeSpan.FromSeconds(10));
        Assert.Equal(2, launcher.Starts);
        Assert.Equal(SourceState.Running, rig.State);

        // A version change alone (the same state) counts too.
        registry.Value = "2.3.0";
        rig.Poll(TimeSpan.FromSeconds(10));
        Assert.Equal(3, launcher.Starts);

        registry.Value = null;
        rig.Poll(TimeSpan.FromSeconds(10));
        Assert.Equal(4, launcher.Starts);
        Assert.Equal((SourceStateKind.Limited, "noDriver"), (rig.State.Kind, rig.State.Reason?.Code));
        rig.Poll(TimeSpan.FromSeconds(10));
        Assert.Equal(4, launcher.Starts);
    }

    [Fact]
    public void TheRegistry_IsReadEveryTenSeconds_NotEveryRound()
    {
        var registry = new FakePawnIoRegistry { Value = "2.2.0" };
        using var rig = new LibreRig(_ => Launcher(registry), driver: registry);
        rig.Open();
        var reads = registry.Reads;

        for (var i = 0; i < 15; i++)
            rig.Poll(LibreSource.Interval); // 30 s

        Assert.Equal(3, registry.Reads - reads);
    }

    [Fact]
    public void WithoutARegistry_ADriverReportedDifferently_NeverRestartsTheHelper()
    {
        // A replay, or another operating system: the hello's state is all there is.
        var launcher = new ScriptedLauncher(helper =>
        {
            helper.Hello = new HelloAnswer(HelperProtocol.Version, "2.0.0-test", false, "missing");
            helper.Sample = () => ScriptedHelper.Answer(1, 5f);
        });
        using var rig = new LibreRig(_ => launcher);
        rig.Open();
        for (var i = 0; i < 20; i++)
            rig.Poll(LibreSource.Interval);

        Assert.Equal(1, launcher.Starts);
        Assert.Equal(new PawnIoReading("missing", null), rig.Source.Driver);
    }

    [Fact]
    public void AHelperThatSaysNotLoaded_IsReportedNotLoaded_WithTheRegistrysVersion()
    {
        var registry = new FakePawnIoRegistry { Value = "2.2.0.0" };
        var launcher = new ScriptedLauncher(helper =>
        {
            helper.Hello = new HelloAnswer(HelperProtocol.Version, "2.0.0-test", true, "notLoaded");
            helper.Sample = () => ScriptedHelper.Answer(1, 5f);
        });
        using var rig = new LibreRig(_ => launcher, driver: registry);
        rig.Open();

        Assert.Equal(new PawnIoReading("notLoaded", "2.2.0.0"), rig.Source.Driver);
        Assert.Equal((SourceStateKind.Limited, "driverNotLoaded"), (rig.State.Kind, rig.State.Reason?.Code));
        // The registry says what it said as the helper started: no restart.
        for (var i = 0; i < 10; i++)
            rig.Poll(LibreSource.Interval);
        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public void TheDriver_IsUnknown_BeforeTheSourceStarts_AndAHelloStateItDoesntKnow_IsUnknownToo()
    {
        var launcher = new ScriptedLauncher(helper =>
        {
            helper.Hello = new HelloAnswer(HelperProtocol.Version, "2.0.0-test", false, "somethingNew");
            helper.Sample = () => ScriptedHelper.Answer(1, 5f);
        });
        using var rig = new LibreRig(_ => launcher);
        Assert.Equal(new PawnIoReading("unknown", null), rig.Source.Driver);

        rig.Open();
        Assert.Equal(new PawnIoReading("unknown", null), rig.Source.Driver);
        Assert.Equal(SourceState.Running, rig.State);
    }

    [Fact]
    public void WhileLibreIsOff_TheDriverIsStillRead_ButNoHelperStarts()
    {
        var registry = new FakePawnIoRegistry();
        var launcher = Launcher(registry);
        using var rig = new LibreRig(_ => launcher, driver: registry);
        rig.SetQuietly(LibreSettings.Default with { Enabled = false });
        rig.Open();

        registry.Value = "2.2.0";
        rig.Poll(TimeSpan.FromSeconds(10));
        Assert.Equal(0, launcher.Starts);
        Assert.Equal(new PawnIoReading("installed", "2.2.0"), rig.Source.Driver);
        Assert.Equal((SourceStateKind.Off, "disabled"), (rig.State.Kind, rig.State.Reason?.Code));
    }

    [Fact]
    public void AnInstallThatEndedWell_StartsTheHelperAgain_AtTheNextRound_Once()
    {
        var registry = new FakePawnIoRegistry();
        var launcher = Launcher(registry);
        using var rig = new LibreRig(_ => launcher, driver: registry);
        rig.Open();
        rig.Poll(LibreSource.Interval);

        registry.Value = "2.2.0";
        rig.Source.DriverChanged();
        Assert.Equal(1, launcher.Starts);
        rig.Poll(LibreSource.Interval);
        Assert.Equal(2, launcher.Starts);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(new PawnIoReading("installed", "2.2.0"), rig.Source.Driver);

        // Read again then, so the 10 s read finds nothing new.
        for (var i = 0; i < 10; i++)
            rig.Poll(LibreSource.Interval);
        Assert.Equal(2, launcher.Starts);
    }
}
