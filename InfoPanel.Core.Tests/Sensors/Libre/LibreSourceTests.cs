using InfoPanel.Core.Tests.SensorHelper;
using InfoPanel.SensorHelper;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Libre;
using InfoPanel.Settings;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Libre;

/// <summary>
/// The Libre source over the helper's real serve loop and the engine's real client (<see cref="TreeLauncher"/>), or a
/// scripted helper where a test needs an end or an answer the serve loop doesn't give, polled by the test on a manual
/// clock: keys, groups, order and units as 1.4.x; the engine's own average; null a failed read; removed hardware stale,
/// then unavailable; the settings applied live; failures, ends and hangs restarted with backoff, the engine's own stops
/// not; the states and reasons.
/// </summary>
public sealed class LibreSourceTests
{
    private static LibreRig TreeRig(ISensorTree tree, out TreeLauncher launcher)
    {
        TreeLauncher? made = null;
        var rig = new LibreRig(time => made = new TreeLauncher(tree, time));
        launcher = made!;
        return rig;
    }

    private static readonly string[] PcKeys =
    [
        "libre:/lpc/nct6798d/0/voltage/0",
        "libre:/lpc/nct6798d/0/fan/1",
        "libre:/amdcpu/0/load/0",
        "libre:/amdcpu/0/load/1",
        "libre:/amdcpu/0/temperature/2",
        "libre:/gpu-nvidia/0/temperature/0",
        "libre:/nvme/0/data/24",
        "libre:/nic/{4D36E972-E325-11CE}/throughput/7",
    ];

    [Fact]
    public void TheKeys_AreLibreHardwareMonitorsIdentifiers_CopiedExactly_InOneFourXsOrder()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out _);
        rig.Open();

        // By the sensor's own hardware's type (the Super I/O chip before the CPU), then its index; braces and all.
        Assert.Equal(PcKeys, rig.Keys);
        Assert.Equal(tree.Sensors.Select(s => "libre:" + s.Identifier).Order(), rig.Keys.Order());
        Assert.All(rig.Source.GetSensors(), s => Assert.Equal("libre", s.SourceId));
    }

    [Fact]
    public void Sensors_AreNamedAsLibreNamesThem_GroupedUnderTheHardwareTheyArePartOf()
    {
        using var rig = TreeRig(FakeTree.Pc(), out _);
        rig.Open();

        var sensors = rig.Source.GetSensors().ToDictionary(s => s.Key.ToString());
        // The board's Super I/O chip is folded into the board, as 1.4.x listed it.
        Assert.Equal(("ASUS PRIME B650", "Fan #2"), (sensors["libre:/lpc/nct6798d/0/fan/1"].Group, sensors["libre:/lpc/nct6798d/0/fan/1"].Name));
        Assert.Equal("ASUS PRIME B650", sensors["libre:/lpc/nct6798d/0/voltage/0"].Group);
        Assert.Equal(("AMD Ryzen 7 8845HS", "CPU Total"), (sensors["libre:/amdcpu/0/load/0"].Group, sensors["libre:/amdcpu/0/load/0"].Name));
        Assert.Equal("Samsung SSD 990 PRO 2TB", sensors["libre:/nvme/0/data/24"].Group);

        // The hardware and sensor types stay in the engine's catalogue.
        var fan = rig.Source.Catalogue.Single(s => s.Identifier == "/lpc/nct6798d/0/fan/1");
        Assert.Equal(("Fan", "SuperIO", "Nuvoton NCT6798D", "/lpc/nct6798d/0", 1), (fan.SensorType, fan.HardwareType, fan.HardwareName, fan.Hardware, fan.Index));
    }

    [Fact]
    public void Units_AreTheirTypes_ThroughputInBytesPerSecond_TheNumberAsLibreGivesIt()
    {
        using var rig = TreeRig(FakeTree.Pc(), out _);
        rig.Open();

        Assert.Equal(["V", "RPM", "%", "%", "°C", "°C", "GB", "B/s"], rig.Source.GetSensors().Select(s => s.Unit));
        // 1.4.x labelled this number "KB/s"; it is LibreHardwareMonitor's bytes per second, unchanged.
        Assert.True(rig.Source.TryRead(SensorKey.Libre("/nic/{4D36E972-E325-11CE}/throughput/7"), out var upload));
        Assert.Equal((1536, "B/s"), (upload.ValueNow, upload.Unit));
    }

    [Fact]
    public void TheAverage_IsTheEnginesOwn_OverTheSamplesRead_NotOneFourXsZero()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out _);
        rig.Open();
        var at = tree.Sensors.FindIndex(s => s.Identifier == "/amdcpu/0/load/0");
        foreach (var value in new[] { 20f, 30f, 40f })
        {
            tree.Sensors[at] = tree.Sensors[at] with { Value = value };
            rig.Poll(LibreSource.Interval);
        }

        Assert.True(rig.Source.TryRead(SensorKey.Libre("/amdcpu/0/load/0"), out var load));
        // 9.75 (the open's round), 20, 30, 40: the engine's mean, minimum and maximum of what it read.
        Assert.Equal(40, load.ValueNow);
        Assert.Equal((9.75 + 20 + 30 + 40) / 4, load.ValueAvg, 4);
        Assert.Equal((9.75, 40.0), (load.ValueMin, load.ValueMax));
    }

    [Fact]
    public void ANullValue_IsAFailedRead_NeverOneFourXsZero()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out _);
        rig.Open();

        // Never read: no reading at all (1.4.x showed 0).
        Assert.Equal(SensorStatus.Unavailable, rig.Status("/lpc/nct6798d/0/voltage/0"));
        Assert.Null(rig.Now("/lpc/nct6798d/0/voltage/0"));
        Assert.Equal(SensorStatus.Unavailable, rig.Status("/amdcpu/0/temperature/2")); // NaN on the helper's side

        // Read, then null: the last value, stale.
        var at = tree.Sensors.FindIndex(s => s.Identifier == "/gpu-nvidia/0/temperature/0");
        tree.Sensors[at] = tree.Sensors[at] with { Value = null };
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SensorStatus.Stale, rig.Status("/gpu-nvidia/0/temperature/0"));
        Assert.Equal(51.5, rig.Now("/gpu-nvidia/0/temperature/0"));
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
    }

    [Fact]
    public void RemovedHardware_GoesStale_ThenUnavailable_ThenUnlisted_NeverLiveAsInOneFourX()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out _);
        rig.Open();
        Assert.Equal(SensorStatus.Live, rig.Status("/nvme/0/data/24"));

        // The drive goes: the helper's catalogue changes, and the engine fetches it.
        tree.Hardware.RemoveAll(h => h.Identifier == "/nvme/0");
        tree.Sensors.RemoveAll(s => s.Hardware == "/nvme/0");
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SensorStatus.Stale, rig.Status("/nvme/0/data/24"));
        Assert.Contains("libre:/nvme/0/data/24", rig.Keys);
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));

        rig.Poll(TimeSpan.FromSeconds(31));
        Assert.Equal(SensorStatus.Unavailable, rig.Status("/nvme/0/data/24"));
        Assert.DoesNotContain("libre:/nvme/0/data/24", rig.Keys);
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
    }

    [Fact]
    public async Task Off_StopsTheHelper_AndSaysOffDisabled_On_StartsItAgain()
    {
        using var rig = TreeRig(FakeTree.Pc(), out var launcher);
        rig.Open();
        Assert.Equal(SourceState.Running, rig.State);

        rig.Settings = rig.Settings with { Enabled = false };
        rig.Poll(LibreSource.Interval);
        Assert.Equal((SourceStateKind.Off, "disabled"), (rig.State.Kind, rig.State.Reason?.Code));
        await launcher.AllEnded(); // the helper's serve loop ended: its pipe closed
        Assert.False(rig.Source.Sampler!.HelperRunning);
        // Turned off on purpose: every Libre entry unlisted at once, read as a key no source lists (not stale for 30 s).
        Assert.Empty(rig.Keys);
        Assert.Equal(SensorStatus.Unavailable, rig.Status("/amdcpu/0/load/0"));

        // Turned off, not failed: never restarted while off.
        rig.Poll(TimeSpan.FromSeconds(120));
        Assert.Equal(1, launcher.Starts);
        Assert.Empty(rig.Keys);

        rig.Settings = rig.Settings with { Enabled = true };
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        Assert.Equal(2, launcher.Starts);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
    }

    [Fact]
    public async Task Off_StopsTheHelper_AtOnce_WhileASampleHangs()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out var launcher);
        rig.Open();

        tree.Go.Reset();
        tree.UpdateEntered.Reset();
        rig.Time.Advance(LibreSource.Interval);
        var poll = Task.Run(rig.Poll, TestContext.Current.CancellationToken);
        Assert.True(tree.UpdateEntered.Wait(HelperTestKit.Patience, TestContext.Current.CancellationToken));

        // The change is heard at once: the hung sample ends, with the clock where it was (not its 10 s deadline).
        rig.Settings = rig.Settings with { Enabled = false };
        await poll.WaitAsync(HelperTestKit.Patience, TestContext.Current.CancellationToken);
        Assert.Equal((SourceStateKind.Off, "disabled"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.False(rig.Source.Sampler!.HelperRunning);
        Assert.Empty(rig.Keys);
        tree.Go.Set();
        await launcher.AllEnded();
        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public async Task Off_StopsTheHelper_AtOnce_WhileACatalogueFetchHangs()
    {
        var asked = new ManualResetEventSlim();
        var samples = 0;
        var launcher = new ScriptedLauncher(h =>
        {
            h.Sample = () => ++samples == 1 ? ScriptedHelper.Answer(1, 5f) : ScriptedHelper.Changed(2, 5f);
            h.NextCatalogue = cancel => ScriptedHelper.Hang<CatalogueAnswer>(asked, cancel);
        });
        using var rig = new LibreRig(_ => launcher);
        rig.Open();

        rig.Time.Advance(LibreSource.Interval);
        var poll = Task.Run(rig.Poll, TestContext.Current.CancellationToken);
        Assert.True(asked.Wait(HelperTestKit.Patience, TestContext.Current.CancellationToken));

        rig.Settings = rig.Settings with { Enabled = false };
        await poll.WaitAsync(HelperTestKit.Patience, TestContext.Current.CancellationToken);
        Assert.Equal((SourceStateKind.Off, "disabled"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.True(launcher.Last.Disposed);
    }

    [Fact]
    public async Task Off_EndsADiscoveryThatHangs_AndSaysOff()
    {
        var never = new TaskCompletionSource<ILibreHelper>();
        var hanging = new HangingLauncher(never.Task);
        using var rig = new LibreRig(_ => hanging);
        var open = Task.Run(rig.Open, TestContext.Current.CancellationToken);
        Assert.True(hanging.Asked.Wait(HelperTestKit.Patience, TestContext.Current.CancellationToken));

        rig.Settings = rig.Settings with { Enabled = false };
        await open.WaitAsync(HelperTestKit.Patience, TestContext.Current.CancellationToken);
        Assert.Equal((SourceStateKind.Off, "disabled"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.False(rig.Source.Sampler!.HelperRunning);
    }

    [Fact]
    public async Task OffHeardAfterDiscoveryReadTheSettings_EndsTheHelpersStart_AndSaysOff()
    {
        var never = new TaskCompletionSource<ILibreHelper>();
        var hanging = new HangingLauncher(never.Task);
        using var rig = new LibreRig(_ => hanging);
        // Discovery reads Libre on; Off arrives before the helper's start is asked for (its cancellation then has no
        // start to end yet).
        rig.AfterNextRead = () => rig.Settings = rig.Settings with { Enabled = false };
        var open = Task.Run(rig.Open, TestContext.Current.CancellationToken);

        await open.WaitAsync(HelperTestKit.Patience, TestContext.Current.CancellationToken);
        Assert.Equal((SourceStateKind.Off, "disabled"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.False(rig.Source.Sampler!.HelperRunning);
    }

    [Fact]
    public async Task OffHeardAfterARestartsRoundReadTheSettings_EndsTheRestart_AndSaysOff()
    {
        var first = new ScriptedHelper { Sample = () => ScriptedHelper.Answer(1, 5f) };
        var launcher = new FirstThenHanging(first);
        using var rig = new LibreRig(_ => launcher);
        rig.Open();
        first.Exit(4);
        rig.Poll(LibreSource.Interval); // lost: restarted in 2 s

        // The restart's round reads Libre on; Off arrives before the new helper's start is asked for.
        rig.AfterNextRead = () => rig.Settings = rig.Settings with { Enabled = false };
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        var poll = Task.Run(rig.Poll, TestContext.Current.CancellationToken);

        await poll.WaitAsync(HelperTestKit.Patience, TestContext.Current.CancellationToken);
        Assert.Equal((SourceStateKind.Off, "disabled"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.False(rig.Source.Sampler!.HelperRunning);
    }

    /// <summary>Starts <paramref name="first"/>, then hangs every later start until it is cancelled.</summary>
    private sealed class FirstThenHanging(ILibreHelper first) : ILibreHelperLauncher
    {
        private int _starts;

        public async Task<ILibreHelper> StartAsync(SensorHelperClient.IObserver? observer, CancellationToken cancel)
        {
            if (++_starts == 1)
                return first;
            await Task.Delay(Timeout.Infinite, cancel);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public void Off_UnlistsTheSensorsOfHardwareThatWentByItself_AtOnceToo()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out _);
        rig.Open();
        tree.Hardware.RemoveAll(h => h.Identifier == "/nvme/0");
        tree.Sensors.RemoveAll(s => s.Hardware == "/nvme/0");
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SensorStatus.Stale, rig.Status("/nvme/0/data/24")); // kept stale: it went by itself

        rig.Settings = rig.Settings with { Enabled = false };
        rig.Poll(LibreSource.Interval);
        Assert.Empty(rig.Keys);
        Assert.Equal(SensorStatus.Unavailable, rig.Status("/nvme/0/data/24"));
    }

    [Fact]
    public void Off_WithNoSensorListedNow_StillUnlistsThoseThatWentByThemselves()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out _);
        rig.Open();
        tree.Hardware.Clear();
        tree.Sensors.Clear();
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        Assert.Contains("libre:/amdcpu/0/load/0", rig.Keys); // kept stale: it went by itself
        Assert.Equal(SensorStatus.Stale, rig.Status("/amdcpu/0/load/0"));

        rig.Settings = rig.Settings with { Enabled = false };
        rig.Poll(LibreSource.Interval);
        Assert.Empty(rig.Keys);
        Assert.Equal(SensorStatus.Unavailable, rig.Status("/amdcpu/0/load/0"));
    }

    [Fact]
    public void StorageOff_UnlistsADriveThatWentByItself_AtOnceToo()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out _);
        rig.Open();
        tree.Hardware.RemoveAll(h => h.Identifier == "/nvme/0");
        tree.Sensors.RemoveAll(s => s.Hardware == "/nvme/0");
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SensorStatus.Stale, rig.Status("/nvme/0/data/24"));

        rig.Settings = rig.Settings with { Storage = false };
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        Assert.DoesNotContain("libre:/nvme/0/data/24", rig.Keys);
        Assert.Equal(SensorStatus.Unavailable, rig.Status("/nvme/0/data/24"));
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
    }

    [Fact]
    public void ASensorReplacedByFreshIdentifiers_IsKeptOnlyWhileListed_SoWhatTheSamplerKeepsStaysBounded()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out _);
        rig.Open();
        var sampler = rig.Source.Sampler!;
        Assert.Equal(8, rig.Keys.Count);
        Assert.Equal(8, sampler.ListedCount);

        var id = "/amdcpu/0/load/1";
        for (var i = 2; i < 52; i++)
        {
            // The core comes back under a fresh identifier: the old one is stale (still listed, still kept), then unlisted.
            var fresh = $"/amdcpu/0/load/{i}";
            var old = tree.Sensors.Single(s => s.Identifier == id);
            tree.Sensors.Remove(old);
            tree.Sensors.Add(old with { Identifier = fresh, Index = i });
            rig.Poll(LibreSource.Interval);
            rig.Poll(LibreSource.Interval);
            Assert.Equal(SensorStatus.Stale, rig.Status(id));
            Assert.Equal(9, rig.Keys.Count);
            Assert.Equal(9, sampler.ListedCount);

            rig.Poll(TimeSpan.FromSeconds(31));
            Assert.DoesNotContain("libre:" + id, rig.Keys);
            Assert.Equal(8, rig.Keys.Count);
            Assert.Equal(8, sampler.ListedCount);
            id = fresh;
        }

        // Off still withdraws every sensor listed, one kept stale included.
        tree.Hardware.RemoveAll(h => h.Identifier == "/nvme/0");
        tree.Sensors.RemoveAll(s => s.Hardware == "/nvme/0");
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SensorStatus.Stale, rig.Status("/nvme/0/data/24"));
        Assert.Equal(8, sampler.ListedCount);

        rig.Settings = rig.Settings with { Enabled = false };
        rig.Poll(LibreSource.Interval);
        Assert.Empty(rig.Keys);
        Assert.Equal(0, sampler.ListedCount);
    }

    [Fact]
    public void OffThenOn_BetweenRounds_StopsTheHelper_AndStartsANewOne()
    {
        var launcher = new ScriptedLauncher(h => h.Sample = () => ScriptedHelper.Answer(1, 5f));
        using var rig = new LibreRig(_ => launcher);
        rig.Open();

        rig.Settings = rig.Settings with { Enabled = false };
        rig.Settings = rig.Settings with { Enabled = true };
        rig.Poll(LibreSource.Interval);
        Assert.True(launcher.Helpers[0].Disposed);
        Assert.Equal(2, launcher.Starts);
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
    }

    [Fact]
    public void OffWhileTheHelperStarts_StopsItBeforeItIsUsed()
    {
        LibreRig? made = null;
        ScriptedLauncher? launcher = null;
        launcher = new ScriptedLauncher(h =>
        {
            h.Sample = () => ScriptedHelper.Answer(1, 5f);
            if (launcher!.Starts == 2) // the restart: turned off while it starts (the settings read after the start sees it)
                made!.SetQuietly(made.Settings with { Enabled = false });
        });
        using var rig = made = new LibreRig(_ => launcher);
        rig.Open();
        launcher.Last.Exit(4);
        rig.Poll(LibreSource.Interval);
        rig.Poll(TimeSpan.FromSeconds(2)); // the restart's round

        Assert.Equal(2, launcher.Starts);
        Assert.Equal((SourceStateKind.Off, "disabled"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.True(launcher.Last.Disposed);
    }

    [Fact]
    public void ACatalogueFetchThatFails_FailsTheRoundsReadings_NotLeavingThemLive()
    {
        var samples = 0;
        var launcher = new ScriptedLauncher(h =>
        {
            h.Sample = () => ++samples == 1 ? ScriptedHelper.Answer(1, 5f) : ScriptedHelper.Changed(2, 6f);
            h.NextCatalogue = _ => Task.FromException<CatalogueAnswer>(new IOException("The pipe broke."));
        });
        using var rig = new LibreRig(_ => launcher);
        rig.Open();
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));

        rig.Poll(LibreSource.Interval);
        Assert.Equal((SourceStateKind.Unavailable, "helperFailed"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.Equal(SensorStatus.Stale, rig.Status("/amdcpu/0/load/0"));
    }

    [Fact]
    public void TwoFailedRounds_ACatalogueChange_ThenAFailedRound_DontRestart()
    {
        var script = new Queue<Func<HelperSample>>([
            () => ScriptedHelper.Answer(1, 5f), // the open's round
            () => throw ScriptedHelper.TreeFailed(),
            () => throw ScriptedHelper.TreeFailed(),
            () => ScriptedHelper.Changed(2, 5f), // answered: the catalogue changed, and is fetched
            () => throw ScriptedHelper.TreeFailed(),
        ]);
        var launcher = new ScriptedLauncher(h =>
        {
            h.Sample = () => script.Dequeue()();
            h.NextCatalogue = _ => Task.FromResult(h.Catalogue with { Version = 2 });
        });
        using var rig = new LibreRig(_ => launcher);
        rig.Open();
        for (var i = 0; i < 4; i++)
            rig.Poll(LibreSource.Interval);

        // Not three in a row: the answered sample between them reset the count.
        Assert.Equal(SourceState.Running, rig.State);
        Assert.False(launcher.Last.Disposed);
        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public void ACatalogueChange_AfterARestart_ResetsTheBackoff()
    {
        ScriptedLauncher? launcher = null;
        launcher = new ScriptedLauncher(h =>
        {
            // The first helper's samples are under the catalogue it started with; a later helper's under a new one.
            var restarted = launcher!.Starts > 1;
            h.Sample = () => restarted ? ScriptedHelper.Changed(2, 5f) : ScriptedHelper.Answer(1, 5f);
            h.NextCatalogue = _ => Task.FromResult(h.Catalogue with { Version = 2 });
        });
        using var rig = new LibreRig(_ => launcher);
        rig.Open();
        launcher.Last.Exit(4);
        rig.Poll(LibreSource.Interval); // lost: the first restart, in 2 s
        rig.Poll(TimeSpan.FromSeconds(2)); // started (a start's round reads nothing)
        Assert.Equal(2, launcher.Starts);
        rig.Poll(LibreSource.Interval); // its sample: answered, under a new catalogue, which is fetched

        // The helper answered since the restart, so the next loss waits 2 s again, not 4.
        launcher.Last.Exit(4);
        rig.Poll(LibreSource.Interval);
        rig.Poll(TimeSpan.FromSeconds(2));
        Assert.Equal(3, launcher.Starts);
    }

    [Fact]
    public void OnAgain_ListsTheSensors_FromTheNextCatalogueAndSample_StartingAfresh()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out _);
        rig.Open();
        var at = tree.Sensors.FindIndex(s => s.Identifier == "/amdcpu/0/load/0");
        tree.Sensors[at] = tree.Sensors[at] with { Value = 80f };
        rig.Poll(LibreSource.Interval);

        rig.Settings = rig.Settings with { Enabled = false };
        rig.Poll(LibreSource.Interval);
        tree.Sensors[at] = tree.Sensors[at] with { Value = 50f };
        rig.Settings = rig.Settings with { Enabled = true };
        rig.Poll(LibreSource.Interval); // started: the catalogue, listed
        Assert.Contains("libre:/amdcpu/0/load/0", rig.Keys);
        rig.Poll(LibreSource.Interval); // its first sample

        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
        Assert.True(rig.Source.TryRead(SensorKey.Libre("/amdcpu/0/load/0"), out var load));
        // Nothing carried from before it was turned off.
        Assert.Equal((50.0, 50.0, 50.0), (load.ValueMin, load.ValueMax, load.ValueAvg));
    }

    [Fact]
    public void StorageOnAgain_ListsTheDrives_FromTheNextCatalogue_StartingAfresh()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out _);
        rig.Open();
        rig.Settings = rig.Settings with { Storage = false };
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        Assert.DoesNotContain("libre:/nvme/0/data/24", rig.Keys);

        rig.Settings = rig.Settings with { Storage = true };
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        Assert.Contains("libre:/nvme/0/data/24", rig.Keys);
        Assert.Equal(SensorStatus.Live, rig.Status("/nvme/0/data/24"));
    }

    [Fact]
    public void OffAtStart_StartsNoHelper()
    {
        using var rig = TreeRig(FakeTree.Pc(), out var launcher);
        rig.Settings = rig.Settings with { Enabled = false };
        rig.Open();
        rig.Poll(LibreSource.Interval);

        Assert.Equal(0, launcher.Starts);
        Assert.Equal((SourceStateKind.Off, "disabled"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.Equal("Libre Hardware Monitor is turned off in the settings", rig.State.Reason?.Message);
    }

    [Fact]
    public void StorageAndItsInterval_ReachTheHelperLive_AsConfigure_AndTheDrivesGo()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out var launcher);
        rig.Open();
        Assert.Equal([(true, 30)], tree.Configured); // the helper opened the computer at its defaults; the engine sent nothing

        rig.Settings = rig.Settings with { Storage = false };
        rig.Poll(LibreSource.Interval); // configure, then a sample under the new catalogue version: fetched
        rig.Poll(LibreSource.Interval);
        Assert.Equal([(true, 30), (false, 30)], tree.Configured);
        // Storage turned off on purpose: the drives' entries unlisted at once; the rest read on.
        Assert.DoesNotContain("libre:/nvme/0/data/24", rig.Keys);
        Assert.Equal(SensorStatus.Unavailable, rig.Status("/nvme/0/data/24"));
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));

        rig.Settings = rig.Settings with { Storage = true, StorageInterval = 120 };
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        Assert.Equal([(true, 30), (false, 30), (true, 120)], tree.Configured);
        Assert.Equal(SensorStatus.Live, rig.Status("/nvme/0/data/24"));
        Assert.Equal(1, launcher.Starts); // reconfigured, never restarted
    }

    [Fact]
    public void AHelperStartedWithOtherSettings_IsConfiguredBeforeItsCatalogue()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out _);
        rig.Settings = new LibreSettings(true, false, 45);
        rig.Open();

        Assert.Equal([(false, 45)], tree.Configured);
        Assert.DoesNotContain("libre:/nvme/0/data/24", rig.Keys);
    }

    [Fact]
    public void ThreeFailedRoundsInARow_RestartTheHelper_TwoSecondsOn()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out var launcher);
        rig.Open();

        tree.Throw = true; // LibreHardwareMonitor throws: treeFailed, the pipe still in step
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(SensorStatus.Stale, rig.Status("/amdcpu/0/load/0"));
        rig.Poll(LibreSource.Interval);
        Assert.Equal((SourceStateKind.Unavailable, "helperFailed"), (rig.State.Kind, rig.State.Reason?.Code));

        tree.Throw = false;
        rig.Poll(TimeSpan.FromSeconds(1.9));
        Assert.Equal(1, launcher.Starts);
        rig.Poll(TimeSpan.FromSeconds(0.1));
        Assert.Equal(2, launcher.Starts);
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
    }

    [Fact]
    public void TwoFailedRounds_ThenAGoodOne_DontRestart()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out var launcher);
        rig.Open();
        for (var i = 0; i < 3; i++)
        {
            tree.Throw = true;
            rig.Poll(LibreSource.Interval);
            rig.Poll(LibreSource.Interval);
            tree.Throw = false;
            rig.Poll(LibreSource.Interval);
        }

        Assert.Equal(1, launcher.Starts);
        Assert.Equal(SourceState.Running, rig.State);
    }

    [Fact]
    public async Task ASampleUnansweredForTenSeconds_FailsTheRound_AndRestartsTheHelper()
    {
        var tree = FakeTree.Pc();
        using var rig = TreeRig(tree, out var launcher);
        rig.Open();

        tree.Go.Reset();
        tree.UpdateEntered.Reset();
        rig.Time.Advance(LibreSource.Interval);
        var poll = Task.Run(rig.Poll, TestContext.Current.CancellationToken);
        Assert.True(tree.UpdateEntered.Wait(HelperTestKit.Patience, TestContext.Current.CancellationToken));
        rig.Time.Advance(TimeSpan.FromSeconds(9.9));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(poll.IsCompleted);
        rig.Time.Advance(TimeSpan.FromSeconds(0.1));
        await poll.WaitAsync(HelperTestKit.Patience, TestContext.Current.CancellationToken);

        Assert.Equal((SourceStateKind.Unavailable, "helperFailed"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.Equal(SensorStatus.Stale, rig.Status("/amdcpu/0/load/0"));
        tree.Go.Set();
        rig.Poll(TimeSpan.FromSeconds(2));
        Assert.Equal(2, launcher.Starts);
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
    }

    [Theory]
    [InlineData(4)] // LibreHardwareMonitor's computer failed to open
    [InlineData(0)] // a parent it didn't recognise, or any end the engine didn't ask for
    [InlineData(-1073741819)] // a crash
    public void AnEndTheEngineDidntAskFor_RestartsTheHelperWithBackoff(int code)
    {
        var launcher = new ScriptedLauncher(h => h.Sample = () => ScriptedHelper.Answer(1, 5f));
        using var rig = new LibreRig(_ => launcher);
        rig.Open();
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));

        launcher.Last.Exit(code);
        rig.Poll(LibreSource.Interval);
        Assert.Equal((SourceStateKind.Unavailable, "helperFailed"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.True(launcher.Helpers[0].Disposed);
        Assert.Contains(rig.Log, e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("exit code " + code));

        rig.Poll(TimeSpan.FromSeconds(2));
        Assert.Equal(2, launcher.Starts);
        Assert.Equal(SourceState.Running, rig.State);
    }

    [Fact]
    public void TheBackoff_Doubles_UpToAMinute_AndAGoodSampleResetsIt()
    {
        Assert.Equal([2, 4, 8, 16, 32, 60, 60], Enumerable.Range(1, 7).Select(i => LibreSampler.BackoffOf(i).TotalSeconds));

        var launcher = new ScriptedLauncher(h => h.Sample = () => ScriptedHelper.Answer(1, 5f));
        using var rig = new LibreRig(_ => launcher);
        launcher.Refuse = () => new IOException("The sensor helper ended with exit code 4 before it served.");
        rig.Open();
        Assert.Equal(1, launcher.Starts);

        // Each restart waits twice the last, to a minute: a start is due exactly then, not a moment before.
        foreach (var wait in new[] { 2, 4, 8, 16, 32, 60, 60 })
        {
            var starts = launcher.Starts;
            rig.Poll(TimeSpan.FromSeconds(wait - 0.5));
            Assert.Equal(starts, launcher.Starts);
            rig.Poll(TimeSpan.FromSeconds(0.5));
            Assert.Equal(starts + 1, launcher.Starts);
        }

        // It starts and samples: the next loss waits 2 s again.
        launcher.Refuse = null;
        rig.Poll(TimeSpan.FromSeconds(60));
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SourceState.Running, rig.State);
        var before = launcher.Starts;
        launcher.Last.Exit(0);
        rig.Poll(LibreSource.Interval);
        rig.Poll(TimeSpan.FromSeconds(2));
        Assert.Equal(before + 1, launcher.Starts);
    }

    [Fact]
    public async Task AStopTheEngineAskedFor_IsNeverRestarted()
    {
        var launcher = new ScriptedLauncher(h => h.Sample = () => ScriptedHelper.Answer(1, 5f));
        using var rig = new LibreRig(_ => launcher);
        await rig.Source.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, launcher.Starts);

        await rig.Source.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(launcher.Last.Disposed);
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public async Task AStop_EndsAHelperStartThatHangs()
    {
        var never = new TaskCompletionSource<ILibreHelper>();
        var hanging = new HangingLauncher(never.Task);
        using var rig = new LibreRig(_ => hanging);
        var start = rig.Source.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(hanging.Asked.Wait(HelperTestKit.Patience, TestContext.Current.CancellationToken));

        await rig.Source.StopAsync(TestContext.Current.CancellationToken).WaitAsync(HelperTestKit.Patience, TestContext.Current.CancellationToken);
        await start.WaitAsync(HelperTestKit.Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheEngineStoppingWhileTheHelperStarts_EndsTheStart()
    {
        // The engine's sensor service cancels a start still running when it stops, and waits for it before stopping sources.
        var never = new TaskCompletionSource<ILibreHelper>();
        var hanging = new HangingLauncher(never.Task);
        using var rig = new LibreRig(_ => hanging);
        using var stopping = new CancellationTokenSource();
        var start = rig.Source.StartAsync(stopping.Token);
        Assert.True(hanging.Asked.Wait(HelperTestKit.Patience, TestContext.Current.CancellationToken));

        await stopping.CancelAsync();
        await start.WaitAsync(HelperTestKit.Patience, TestContext.Current.CancellationToken);
        await rig.Source.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class HangingLauncher(Task<ILibreHelper> never) : ILibreHelperLauncher
    {
        public ManualResetEventSlim Asked { get; } = new();

        public Task<ILibreHelper> StartAsync(SensorHelperClient.IObserver? observer, CancellationToken cancel)
        {
            Asked.Set();
            return never.WaitAsync(cancel);
        }
    }

    [Fact]
    public void TheDriverMissing_IsLimited_NoDriver_ItsSensorsRead()
    {
        using var rig = TreeRig(new DriverTree(FakeTree.Pc(), "missing"), out _);
        rig.Open();

        Assert.Equal((SourceStateKind.Limited, "noDriver"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("installed")]
    public void ADriverThatIsntMissing_IsRunning(string driver)
    {
        using var rig = TreeRig(new DriverTree(FakeTree.Pc(), driver), out _);
        rig.Open();

        Assert.Equal(SourceState.Running, rig.State);
    }

    [Fact]
    public void AMissingHelper_IsUnavailable_HelperFailed_LoggedOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), "no-helper-" + Guid.NewGuid().ToString("N"), "InfoPanel.SensorHelper.exe");
        using var rig = new LibreRig(time => new LibreHelperProcess(path, time: time));
        rig.Open();
        for (var i = 0; i < 8; i++)
            rig.Poll(TimeSpan.FromSeconds(60));

        Assert.Equal((SourceStateKind.Unavailable, "helperFailed"), (rig.State.Kind, rig.State.Reason?.Code));
        Assert.Equal("The sensor helper isn't installed with the engine", rig.State.Reason?.Message);
        Assert.Single(rig.Log, e => e.Level >= LogEventLevel.Warning);
        Assert.Contains(path, rig.Log.Single(e => e.Level >= LogEventLevel.Warning).RenderMessage());
        Assert.Empty(rig.Keys);
    }
}
