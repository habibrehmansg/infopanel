using InfoPanel.Sensors;
using InfoPanel.Sensors.HwInfo.Gadget;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.HwInfo.Gadget;

/// <summary>
/// The HWiNFO Gadget source's state from a fake registry and a fake process check, the plan's table: running while it
/// reads; <c>off</c> · <c>notSetUp</c> with no gadget readings yet this run; after readings (or a key that never
/// updated), <c>unavailable</c> · <c>notRunning</c>, or <c>stopped</c> while HWiNFO runs.
/// </summary>
public class HwInfoGadgetStateTests
{
    private sealed class Rig
    {
        public readonly GadgetClock Time = new(new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero));
        public readonly FakeGadgetRegistry Registry;
        public readonly PolledSensorSource Source;

        public bool Running;

        public int Checks;

        public Rig()
        {
            Registry = new FakeGadgetRegistry(Time);
            Source = HwInfoGadgetSource.Create(Registry, Time, hwinfoRunning: () =>
            {
                Checks++;
                return Running;
            });
        }

        public FakeGadgetRegistry.Key Key => Registry[GadgetHive.CurrentUser];

        public void Run(int seconds, Action<int>? each = null)
        {
            for (var s = 1; s <= seconds; s++)
            {
                Time.Advance(TimeSpan.FromSeconds(1));
                each?.Invoke(s);
                Source.Poll();
            }
        }

        /// <summary>HWiNFO rewriting the key every 2 s, with a new value each time, for <paramref name="seconds"/>.</summary>
        public void Writing(int seconds) => Run(seconds, s =>
        {
            if (s % 2 == 0)
                Key.Write(R("CPU", "Temp", (40 + s % 7).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        });

        public SourceState State => Source.State;
    }

    private static GadgetReading R(string sensor, string label, string raw) => new(sensor, label, raw + " °C", raw);

    private static SourceState Unavailable(SourceReason reason) => new(SourceStateKind.Unavailable, reason);

    private static readonly SourceState NotSetUp = new(SourceStateKind.Off, HwInfoGadgetSource.NotSetUp);

    [Fact]
    public void TheReasons_AreTheTables_InEnglish()
    {
        Assert.Equal(("notSetUp", "HWiNFO isn't reporting any reading to gadgets"), (HwInfoGadgetSource.NotSetUp.Code, HwInfoGadgetSource.NotSetUp.Message));
        Assert.Equal(("notRunning", "HWiNFO isn't running"), (HwInfoGadgetSource.NotRunning.Code, HwInfoGadgetSource.NotRunning.Message));
        Assert.Equal(("stopped", "HWiNFO stopped reporting to gadgets: check Enable reporting to Gadget"), (HwInfoGadgetSource.Stopped.Code, HwInfoGadgetSource.Stopped.Message));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoKey_OrAKeyWithNoReading_BeforeAnyReadings_IsOffNotSetUp_WhetherOrNotHWiNFORuns(bool running)
    {
        var rig = new Rig { Running = running };
        Assert.Equal(SourceState.Starting, rig.State);
        rig.Source.Open();
        Assert.Equal(NotSetUp, rig.State);
        rig.Run(10);
        Assert.Equal(NotSetUp, rig.State);

        // Reporting turned on, nothing ticked: still not set up.
        rig.Registry.Create().Set("Sensor0", "CPU");
        rig.Run(10, _ => rig.Key.Touch());
        Assert.Equal(NotSetUp, rig.State);
        Assert.Equal(0, rig.Source.GetSensors().Count);
    }

    [Fact]
    public void Readings_AreRunning()
    {
        var rig = new Rig();
        rig.Source.Open();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Writing(6);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(1, rig.Source.GetSensors().Count);
    }

    [Fact]
    public void ReadingsThatStop_WithHWiNFONotRunning_AreNotRunning()
    {
        var rig = new Rig();
        rig.Source.Open();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Writing(6);
        Assert.Equal(SourceState.Running, rig.State);

        // HWiNFO quits, leaving the key: outside its window (10 s), not running.
        rig.Run(15);
        Assert.Equal(Unavailable(HwInfoGadgetSource.NotRunning), rig.State);

        // It deletes the key as well: still not running (it had readings this run).
        rig.Registry.Delete();
        rig.Run(2);
        Assert.Equal(Unavailable(HwInfoGadgetSource.NotRunning), rig.State);

        // HWiNFO back, writing: running again.
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Writing(4);
        Assert.Equal(SourceState.Running, rig.State);
    }

    [Fact]
    public void ReadingsThatStop_WhileHWiNFORuns_AreStopped()
    {
        var rig = new Rig();
        rig.Source.Open();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Writing(6);

        // Reporting turned off in HWiNFO, which runs on: the key goes.
        rig.Running = true;
        rig.Registry.Delete();
        rig.Run(6);
        Assert.Equal(Unavailable(HwInfoGadgetSource.Stopped), rig.State);

        // Every reading unticked: the key stays, with none.
        rig.Registry.Create().Set("Sensor0", "CPU");
        rig.Run(6, _ => rig.Key.Touch());
        Assert.Equal(Unavailable(HwInfoGadgetSource.Stopped), rig.State);

        // HWiNFO quits too.
        rig.Running = false;
        rig.Run(6);
        Assert.Equal(Unavailable(HwInfoGadgetSource.NotRunning), rig.State);
    }

    [Fact]
    public void AKeyThatNeverUpdated_FromTheStart_IsNotRunning_NotNotSetUp()
    {
        var rig = new Rig();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Time.Advance(TimeSpan.FromHours(2));
        rig.Source.Open();
        rig.Run(3);
        Assert.Equal(Unavailable(HwInfoGadgetSource.NotRunning), rig.State);
    }

    [Fact]
    public void ARegistryThatFails_FromTheStart_IsUnavailable_NotNotSetUp()
    {
        var rig = new Rig();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Registry.Throw = new UnauthorizedAccessException("denied");
        rig.Source.Open();
        Assert.Equal(SourceState.Unavailable, rig.State);
        rig.Run(10);
        Assert.Equal(SourceState.Unavailable, rig.State);

        // It reads again: what the key shows.
        rig.Registry.Throw = null;
        rig.Writing(4);
        Assert.Equal(SourceState.Running, rig.State);
    }

    [Fact]
    public void ARegistryThatFails_AfterReadings_IsUnavailable_ThenRecovers()
    {
        var rig = new Rig();
        rig.Source.Open();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Writing(6);
        Assert.Equal(SourceState.Running, rig.State);

        // Every read fails, for longer than the catalogue stands: unavailable, never running.
        rig.Registry.Throw = new System.IO.IOException("gone");
        rig.Run(1);
        Assert.Equal(SourceState.Unavailable, rig.State);
        rig.Writing(40);
        Assert.Equal(SourceState.Unavailable, rig.State);
        Assert.Equal(0, rig.Source.GetSensors().Count);

        // It reads again, HWiNFO still writing: running.
        rig.Registry.Throw = null;
        rig.Writing(4);
        Assert.Equal(SourceState.Running, rig.State);
        Assert.Equal(1, rig.Source.GetSensors().Count);

        // It fails again, then reads a key HWiNFO stopped writing: the observation decides again (not running).
        rig.Registry.Throw = new System.IO.IOException("gone");
        rig.Run(3);
        Assert.Equal(SourceState.Unavailable, rig.State);
        rig.Registry.Throw = null;
        rig.Run(15);
        Assert.Equal(Unavailable(HwInfoGadgetSource.NotRunning), rig.State);
    }

    [Fact]
    public void AKeyWithTooManyValues_IsUnavailable_FromTheStart_AndAfterReadings_ThenRecovers()
    {
        var rig = new Rig();
        var key = rig.Registry.Create();
        for (var i = 0; i <= HwInfoGadgetSampler.MaxValues; i++)
            key.Set("Other" + i, "x");
        rig.Source.Open();
        Assert.Equal(SourceState.Unavailable, rig.State);
        rig.Run(3, _ => rig.Key.Touch());
        Assert.Equal(SourceState.Unavailable, rig.State);

        // A key it can read, being written: running.
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Writing(6);
        Assert.Equal(SourceState.Running, rig.State);

        // The key grows past the limit while HWiNFO writes it: unavailable for as long as it stays, never running.
        for (var i = 0; i <= HwInfoGadgetSampler.MaxValues; i++)
            rig.Key.Set("Other" + i, "x");
        rig.Run(40, _ => rig.Key.Touch());
        Assert.Equal(SourceState.Unavailable, rig.State);
        Assert.Equal(0, rig.Source.GetSensors().Count);

        // Back under it.
        rig.Key.Values.RemoveAll(v => v.Name.StartsWith("Other", StringComparison.Ordinal));
        rig.Writing(4);
        Assert.Equal(SourceState.Running, rig.State);
    }

    [Fact]
    public void HWiNFOsProcess_IsAskedAtMostEvery5Seconds_AndNeverWhileItReads()
    {
        var rig = new Rig();
        rig.Source.Open();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Writing(6);
        var reading = rig.Checks;
        rig.Writing(20);
        Assert.Equal(reading, rig.Checks);

        rig.Registry.Delete();
        rig.Run(30);
        Assert.InRange(rig.Checks - reading, 6, 7);
    }
}
