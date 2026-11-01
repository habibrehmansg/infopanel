using System.Globalization;
using InfoPanel.Models;
using InfoPanel.Sensors;
using InfoPanel.Sensors.HwInfo.Gadget;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.HwInfo.Gadget;

/// <summary>
/// HWiNFO's gadget values (the readings a user ticks "Report value in Gadget" for) read from a fake registry through
/// <see cref="HwInfoGadgetSource"/>: the catalogue, keys by name, renumbering, values read culture-free, the shapes and
/// bounds of the key, freshness by the key's last-write time, the hives, remotes and the observation for the source's
/// state; the G tests read through <see cref="GadgetRegistryReader"/> over the fake's raw calls where they say so.
/// </summary>
public class HwInfoGadgetSamplerTests
{
    private const string Prefix = "plugin:/infopanel.hwinfo-gadget/";

    private const string Cpu = "CPU [#0]: AMD Ryzen 7 8845HS: Enhanced";

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    private sealed class Rig
    {
        public readonly GadgetClock Time = new(new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero));
        public readonly FakeGadgetRegistry Registry;
        public readonly List<LogEvent> Events = [];
        public readonly PolledSensorSource Source;
        public HwInfoGadgetSampler? Sampler;

        /// <summary>Whether HWiNFO's process runs, as the sampler's process check sees it (not by default).</summary>
        public bool Running;

        public int RunningChecks;

        /// <param name="throughReader">Read through <see cref="GadgetRegistryReader"/> over the fake's raw calls, not its snapshots.</param>
        /// <param name="running">The process check; by default <see cref="Running"/>, counted in <see cref="RunningChecks"/>.</param>
        public Rig(bool throughReader = false, Func<bool>? running = null)
        {
            Registry = new FakeGadgetRegistry(Time);
            IGadgetRegistry registry = throughReader ? new GadgetRegistryReader(Registry.Api) : Registry;
            var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(Events)).CreateLogger();
            Source = HwInfoGadgetSource.Create(() => Sampler = new HwInfoGadgetSampler(registry, Time, log, running ?? (() =>
            {
                RunningChecks++;
                return Running;
            })), Time);
        }

        public FakeGadgetRegistry.Key Key => Registry[GadgetHive.CurrentUser];

        public void Open() => Source.Open();

        /// <summary>Polls once a second for <paramref name="seconds"/>, before each poll calling <paramref name="each"/> with the second.</summary>
        public void Run(int seconds, Action<int>? each = null)
        {
            for (var s = 1; s <= seconds; s++)
            {
                Time.Advance(TimeSpan.FromSeconds(1));
                each?.Invoke(s);
                Source.Poll();
            }
        }

        /// <summary>HWiNFO rewriting the key every <paramref name="every"/> seconds (its default poll is 2 s) for <paramref name="seconds"/>.</summary>
        public void Writing(int seconds, int every = 2) => Run(seconds, s =>
        {
            if (s % every == 0)
                Key.Touch();
        });

        public SensorStatus Status(string id) => Source.GetStatus(HwInfoGadgetSource.KeyOf(id));

        public SensorReading Read(string id)
        {
            Assert.True(Source.TryRead(HwInfoGadgetSource.KeyOf(id), out var reading), id);
            return reading;
        }

        public string[] Ids => Source.GetSensors().Select(s => s.Key.ToString()[Prefix.Length..]).ToArray();

        public int Logged(LogEventLevel level, string text)
        {
            lock (Events)
                return Events.Count(e => e.Level == level && e.RenderMessage().Contains(text, StringComparison.Ordinal));
        }
    }

    private static GadgetReading R(string sensor, string label, string raw, string? value = null) => new(sensor, label, value ?? raw, raw);

    // ---- Catalogue ----

    [Fact]
    public void Catalogue_ReadingsInIndexOrder_GroupIsTheSensor_NameTheLabel_UnitFromValue()
    {
        var rig = new Rig();
        var key = rig.Registry.Create();
        // Added out of order: index order is numeric (2 before 10), not the order the values come in or their names sort.
        key.Set(10, R("CPU [#0]: AMD Ryzen 7 8845HS", "Core Clocks (avg)", "4198", "4,198 MHz"));
        key.Set(3, R("Drive: Example SSD", "Drive Failure", "1", "Yes"));
        key.Set(2, R(Cpu, "CPU (Tctl/Tdie)", "45", "45 °C"));
        key.Set(0, R("System: Example SER8", "Physical Memory Load", "24.1", "24.1 %"));
        rig.Open();

        Assert.Equal(
            [
                ("System: Example SER8/Physical Memory Load", "System: Example SER8", "Physical Memory Load", "%"),
                (Cpu + "/CPU (Tctl%2FTdie)", Cpu, "CPU (Tctl/Tdie)", "°C"),
                ("Drive: Example SSD/Drive Failure", "Drive: Example SSD", "Drive Failure", ""),
                ("CPU [#0]: AMD Ryzen 7 8845HS/Core Clocks (avg)", "CPU [#0]: AMD Ryzen 7 8845HS", "Core Clocks (avg)", "MHz"),
            ],
            rig.Source.GetSensors().Select(s => (s.Key.ToString()[Prefix.Length..], s.Group, s.Name, s.Unit)));
        Assert.All(rig.Source.GetSensors(), s => Assert.Equal("infopanel.hwinfo-gadget", s.SourceId));
        Assert.All(rig.Source.GetSensors(), s => Assert.True(SensorHub.IsWellFormed(s.Key)));
        Assert.Equal(4198, rig.Read("CPU [#0]: AMD Ryzen 7 8845HS/Core Clocks (avg)").ValueNow);
        Assert.Equal(24.1, rig.Read("System: Example SER8/Physical Memory Load").ValueNow);
        Assert.Equal("MHz", rig.Read("CPU [#0]: AMD Ryzen 7 8845HS/Core Clocks (avg)").Unit);
        Assert.Equal(SensorStatus.Live, rig.Status(Cpu + "/CPU (Tctl%2FTdie)"));
    }

    [Theory]
    [InlineData("4,198 MHz", "MHz")]
    [InlineData("45 °C", "°C")]
    [InlineData("24.1 %", "%")]
    [InlineData("-3 °C", "°C")]
    [InlineData("1 234,5 MHz", "MHz")]
    [InlineData("  12 V  ", "V")]
    [InlineData("Yes", "")]
    [InlineData("24.1", "")]
    public void Catalogue_TheUnitIsValue_WithoutItsLeadingNumber(string value, string unit)
    {
        var rig = new Rig();
        rig.Registry.Create().Set(0, R("S", "L", "1", value));
        rig.Open();
        Assert.Equal(unit, Assert.Single(rig.Source.GetSensors()).Unit);
    }

    [Fact]
    public void Catalogue_AtRunTime_AReadingTickedLaterAppears_AndAKeyThatAppearsLaterIsRead()
    {
        var rig = new Rig();
        rig.Open();
        Assert.Empty(rig.Source.GetSensors());

        // Listed at the next round, read at the one after (the source samples the catalogue it had).
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Run(1);
        Assert.Equal(["CPU/Temp"], rig.Ids);
        rig.Run(1);
        Assert.Equal(SensorStatus.Live, rig.Status("CPU/Temp"));

        rig.Key.Write(R("CPU", "Temp", "51"), R("GPU", "Temp", "60"));
        rig.Run(1);
        Assert.Equal(["CPU/Temp", "GPU/Temp"], rig.Ids);
        rig.Run(1);
        Assert.Equal(60, rig.Read("GPU/Temp").ValueNow);
        Assert.Equal(51, rig.Read("CPU/Temp").ValueNow);
    }

    // ---- Keys ----

    [Fact]
    public void Keys_AreTheSensorAndLabelByName_Escaped()
    {
        var rig = new Rig();
        rig.Registry.Create().Write(
            R(Cpu, "CPU (Tctl/Tdie)", "45"),
            R("GPU [#0]: AMD Radeon 780M w/ 100% ~ x", "GPU Load", "7"),
            R("  Drive: Spaced  ", "  Temp  ", "33"),
            R("S", "Fan", "1"),
            R("S", "Fan~2", "4"));
        rig.Open();

        Assert.Equal(
            [
                Cpu + "/CPU (Tctl%2FTdie)",
                "GPU [#0]: AMD Radeon 780M w%2F 100%25 %7E x/GPU Load",
                "Drive: Spaced/Temp",
                "S/Fan",
                "S/Fan%7E2",
            ],
            rig.Ids);
        Assert.Equal("plugin:/infopanel.hwinfo-gadget/CPU [#0]: AMD Ryzen 7 8845HS: Enhanced/CPU (Tctl%2FTdie)", rig.Source.GetSensors()[0].Key.ToString());
        Assert.Equal((1, 4), (rig.Read("S/Fan").ValueNow, rig.Read("S/Fan%7E2").ValueNow));
        Assert.Equal("Drive: Spaced", rig.Source.GetSensors()[2].Group);
        Assert.Equal("Temp", rig.Source.GetSensors()[2].Name);
    }

    [Fact]
    public void Keys_ANameEmpty_WithAControlCharacter_OrTooLong_AndAKeyPast512_SkipItsReading_LoggedOnce()
    {
        var rig = new Rig();
        var a255 = new string('a', 255);
        // The prefix is 32 characters: a 255-character sensor, '/' and 224 more make 512.
        rig.Registry.Create().Write(
            R("Kept", "One", "1"),
            R("   ", "Empty sensor", "2"),
            R("Empty label", "", "3"),
            R("Control\u0001", "x", "4"),
            R("Long", new string('b', 256), "5"),
            R("Long", new string('c', 255), "6"),
            R(a255, new string('d', 224), "7"),
            R(a255, new string('e', 225), "8"),
            R(a255, new string('f', 222) + "/", "9"));
        rig.Open();
        rig.Writing(3);

        Assert.Equal(["Kept/One", "Long/" + new string('c', 255), a255 + "/" + new string('d', 224)], rig.Ids);
        Assert.All(rig.Source.GetSensors(), s => Assert.True(s.Key.ToString().Length <= 512));
        // Six readings skipped, each logged once over four rounds.
        Assert.Equal(6, rig.Logged(LogEventLevel.Warning, "skipped"));
    }

    [Fact]
    public void Keys_AreNeverHWiNFOsIndex_NorThe1xPluginsForm()
    {
        var rig = new Rig();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"), R("GPU", "Temp", "60"));
        rig.Open();

        Assert.All(rig.Source.GetSensors(), s =>
        {
            var id = s.Key.ToString()[Prefix.Length..];
            Assert.False(int.TryParse(id, out _), id);
            Assert.Contains('/', id);
            Assert.DoesNotContain("hwinfo-registry-plugin", s.Key.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AnOld1xBindingToTheRegistryPlugin_ReadsUnavailable_NothingAnswersIt()
    {
        var rig = new Rig();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"), R("GPU", "Temp", "60"));
        rig.Open();
        var hub = new SensorHub([rig.Source], new SensorHubOptions { SimulateUnknown = false });

        foreach (var old in new[] { "plugin:/hwinfo-registry-plugin/0", "plugin:/hwinfo-registry-plugin/1" })
        {
            Assert.True(SensorKey.TryParse(old, out var key));
            Assert.False(rig.Source.Handles(key));
            Assert.Equal(SensorStatus.Unavailable, hub.GetStatus(key));
            Assert.False(hub.TryRead(key, out _));
        }
    }

    // ---- Renumbering ----

    [Fact]
    public void Renumbering_UntickingTheMiddleReading_KeepsTheOthersKeysValuesAndStatistics_AndTickingItAgainBringsItBack()
    {
        var rig = new Rig();
        var a = "CPU/Temp";
        var b = "Memory/Load";
        var c = "GPU/Temp";
        rig.Registry.Create().Write(R("CPU", "Temp", "50"), R("Memory", "Load", "30"), R("GPU", "Temp", "60"));
        rig.Open();
        rig.Run(2, s => rig.Key.Write(R("CPU", "Temp", (50 + 2 * s).ToString(CultureInfo.InvariantCulture)), R("Memory", "Load", "31"), R("GPU", "Temp", (60 - s).ToString(CultureInfo.InvariantCulture))));
        Assert.Equal((54, 50, 54), (rig.Read(a).ValueNow, rig.Read(a).ValueMin, rig.Read(a).ValueMax));

        // Memory Load unticked: GPU Temp moves from index 2 to 1, where Memory Load was.
        rig.Run(1, _ => rig.Key.Write(R("CPU", "Temp", "56"), R("GPU", "Temp", "65")));
        Assert.Equal(SensorStatus.Live, rig.Status(a));
        Assert.Equal((56, 50, 56), (rig.Read(a).ValueNow, rig.Read(a).ValueMin, rig.Read(a).ValueMax));
        Assert.Equal(SensorStatus.Live, rig.Status(c));
        Assert.Equal((65, 58, 65), (rig.Read(c).ValueNow, rig.Read(c).ValueMin, rig.Read(c).ValueMax));
        // The unticked one never shows another reading: it fails from now on (stale, its last value kept) ...
        Assert.Equal(SensorStatus.Stale, rig.Status(b));
        Assert.Equal(31, rig.Read(b).ValueNow);

        // ... until it is unavailable and unlisted.
        rig.Writing(31);
        Assert.Equal(SensorStatus.Unavailable, rig.Status(b));
        Assert.Equal([a, c], rig.Ids);
        Assert.Equal(SensorStatus.Live, rig.Status(c));

        // Ticked again: back under its old key.
        rig.Run(1, _ => rig.Key.Write(R("CPU", "Temp", "56"), R("Memory", "Load", "33"), R("GPU", "Temp", "65")));
        Assert.Equal([a, b, c], rig.Ids);
        rig.Run(1);
        Assert.Equal(SensorStatus.Live, rig.Status(b));
        Assert.Equal(33, rig.Read(b).ValueNow);
        Assert.Equal(65, rig.Read(c).ValueNow);
    }

    [Fact]
    public void Renumbering_ReTickedWithinTheWindow_KeepsItsStatistics()
    {
        var rig = new Rig();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"), R("Memory", "Load", "30"));
        rig.Open();
        rig.Run(1, _ => rig.Key.Write(R("CPU", "Temp", "50"), R("Memory", "Load", "20")));
        rig.Run(1, _ => rig.Key.Write(R("CPU", "Temp", "50")));
        rig.Run(1, _ => rig.Key.Write(R("Memory", "Load", "25"), R("CPU", "Temp", "51")));
        rig.Run(1);

        Assert.Equal(["Memory/Load", "CPU/Temp"], rig.Ids);
        var load = rig.Read("Memory/Load");
        Assert.Equal((25, 20, 30, SensorStatus.Live), (load.ValueNow, load.ValueMin, load.ValueMax, rig.Status("Memory/Load")));
    }

    // ---- ValueRaw ----

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void ValueRaw_IsReadWithoutTheCulture_EitherSeparatorADecimalPoint_AnythingElseFailsThatReadingOnly(string culture)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try
        {
            var raws = new[] { "24.1", "24,1", "-3", " 12 ", "4198", "+7.25", "1,234.5", "1e5", "N/A", "", "∞", "1.2.3", "0x10", "1 234" };
            var rig = new Rig();
            rig.Registry.Create().Write(raws.Select((raw, i) => R("S", "R" + i, raw, "x")).ToArray());
            rig.Open();
            rig.Writing(2);

            Assert.Equal(raws.Length, rig.Ids.Length);
            Assert.Equal(
                [24.1, 24.1, -3, 12, 4198, 7.25],
                Enumerable.Range(0, 6).Select(i => rig.Read("S/R" + i).ValueNow));
            for (var i = 6; i < raws.Length; i++)
                Assert.Equal(SensorStatus.Unavailable, rig.Status("S/R" + i));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    // ---- Shapes ----

    [Fact]
    public void Shapes_OnlyExactNames_IndicesUpTo1023_AndTextValues_MakeAReading_ValueIsOptional()
    {
        var rig = new Rig();
        var key = rig.Registry.Create();
        void Reading(string n, bool label = true, bool raw = true, bool value = true, GadgetValueKind rawKind = GadgetValueKind.String, GadgetValueKind sensorKind = GadgetValueKind.String)
        {
            key.Set("Sensor" + n, sensorKind, "S" + n);
            if (label)
                key.Set("Label" + n, "L" + n);
            if (value)
                key.Set("Value" + n, "1 V");
            if (raw)
                key.Set("ValueRaw" + n, rawKind, "1");
        }

        Reading("01");
        Reading("Foo");
        Reading("1024");
        Reading("5", rawKind: GadgetValueKind.Other);
        Reading("6", label: false);
        Reading("7", raw: false);
        Reading("8", value: false);
        Reading("9", sensorKind: GadgetValueKind.Other);
        Reading("1023");
        Reading("0");
        key.Set("sensor3", "lower");
        key.Set("Label3", "L3");
        key.Set("ValueRaw3", "1");
        key.Set(" Sensor4", "S4");
        key.Set("Label4", "L4");
        key.Set("ValueRaw4", "1");
        key.Set("Sensor-2", "S");
        key.Set("Label-2", "L");
        key.Set("ValueRaw-2", "1");
        key.Set("Sensor١", "S");
        key.Set("Label١", "L");
        key.Set("ValueRaw١", "1");
        rig.Open();

        Assert.Equal(["S0/L0", "S8/L8", "S1023/L1023"], rig.Ids);
        Assert.Equal("", rig.Source.GetSensors()[1].Unit);
        Assert.Equal("V", rig.Source.GetSensors()[0].Unit);
    }

    // ---- Bounds ----

    [Fact]
    public void Bounds_MoreThan8192Values_FailTheRound_LoggedOnce()
    {
        var rig = new Rig();
        var key = rig.Registry.Create();
        key.Write(R("CPU", "Temp", "50"));
        rig.Open();
        rig.Writing(2);
        Assert.Equal(SensorStatus.Live, rig.Status("CPU/Temp"));

        for (var i = key.Values.Count; i < HwInfoGadgetSampler.MaxValues + 1; i++)
            key.Set("Pad" + i, "x");
        rig.Writing(4);
        Assert.Equal(SensorStatus.Stale, rig.Status("CPU/Temp"));
        Assert.Equal(1, rig.Logged(LogEventLevel.Warning, "8193 values"));

        key.Remove("Pad100");
        rig.Writing(2);
        Assert.Equal(SensorStatus.Live, rig.Status("CPU/Temp"));
    }

    [Fact]
    public void Bounds_AtMost1024Readings_AndATextOver1024CharactersSkipsItsReading()
    {
        var rig = new Rig();
        var key = rig.Registry.Create();
        for (var i = 0; i <= 1024; i++)
            key.Set(i, R("S", "R" + i.ToString(CultureInfo.InvariantCulture), "1"));
        rig.Open();
        Assert.Equal(1024, rig.Ids.Length);
        Assert.Equal("S/R1023", rig.Ids[^1]);

        var rig2 = new Rig();
        rig2.Registry.Create().Write(
            R("S", "Kept", "1", "5 " + new string('x', 1022)),
            R("S", "Too long", "1", "5 " + new string('x', 1023)),
            R("S", "Raw too long", "1" + new string(' ', 1024)),
            R("S", "Last", "2"));
        rig2.Open();
        Assert.Equal(["S/Kept", "S/Last"], rig2.Ids);
        Assert.Equal(new string('x', 1022), rig2.Source.GetSensors()[0].Unit);
    }

    [Fact]
    public void Bounds_AReaderThatThrows_FailsTheRound_LoggedOnce_AndReadingGoesOn()
    {
        var rig = new Rig();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Open();
        rig.Registry.Throw = new UnauthorizedAccessException("denied");
        rig.Writing(3);
        Assert.Equal(SensorStatus.Stale, rig.Status("CPU/Temp"));
        Assert.Equal(1, rig.Logged(LogEventLevel.Warning, "denied"));

        rig.Registry.Throw = null;
        rig.Writing(2);
        Assert.Equal(SensorStatus.Live, rig.Status("CPU/Temp"));
    }

    // ---- Freshness ----

    [Fact]
    public void Freshness_TheKeyStopsChanging_StaleAfterItsWindow_UnavailableAndUnlisted30sAfterTheLastGoodRead()
    {
        var rig = new Rig();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"), R("GPU", "Temp", "60"));
        rig.Open();
        rig.Writing(20);
        // The last write was at the last round; HWiNFO writes every 2 s, so the window is 10 s (3 × 2 s, at least 10).
        rig.Run(10);
        Assert.Equal(SensorStatus.Live, rig.Status("CPU/Temp"));
        rig.Run(1);
        Assert.Equal(SensorStatus.Stale, rig.Status("CPU/Temp"));
        Assert.Equal(50, rig.Read("CPU/Temp").ValueNow);
        Assert.Equal(GadgetObservation.NotUpdating, rig.Sampler!.Observation);

        // The last good read was at second 10: stale for 30 s, then unavailable and unlisted.
        rig.Run(29);
        Assert.Equal(SensorStatus.Stale, rig.Status("CPU/Temp"));
        Assert.Equal(2, rig.Ids.Length);
        rig.Run(1);
        Assert.Equal(SensorStatus.Unavailable, rig.Status("CPU/Temp"));
        Assert.Empty(rig.Ids);
    }

    [Fact]
    public void Freshness_ANewWrite_IsLiveAgain_WithItsStatisticsKept()
    {
        var rig = new Rig();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Open();
        rig.Run(6, s =>
        {
            if (s % 2 == 0)
                rig.Key.Write(R("CPU", "Temp", (50 + s).ToString(CultureInfo.InvariantCulture)));
        });
        rig.Run(15);
        Assert.Equal(SensorStatus.Stale, rig.Status("CPU/Temp"));

        rig.Run(1, _ => rig.Key.Write(R("CPU", "Temp", "40")));
        var cpu = rig.Read("CPU/Temp");
        Assert.Equal(SensorStatus.Live, rig.Status("CPU/Temp"));
        Assert.Equal((40, 40, 56), (cpu.ValueNow, cpu.ValueMin, cpu.ValueMax));
    }

    [Fact]
    public void Freshness_AKeyLastWrittenHoursAgo_WithHWiNFONotRunning_IsNeverLive_UntilItIsWrittenAgain()
    {
        var rig = new Rig();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Time.Advance(TimeSpan.FromHours(2));
        rig.Open();
        Assert.NotEqual(SensorStatus.Live, rig.Status("CPU/Temp"));
        rig.Run(5);
        Assert.NotEqual(SensorStatus.Live, rig.Status("CPU/Temp"));
        Assert.Equal(GadgetObservation.NotUpdating, rig.Sampler!.Observation);
        Assert.False(rig.Sampler.HadReadings);

        rig.Run(1, _ => rig.Key.Write(R("CPU", "Temp", "51")));
        rig.Run(1);
        Assert.Equal(SensorStatus.Live, rig.Status("CPU/Temp"));
        Assert.Equal(51, rig.Read("CPU/Temp").ValueNow);
    }

    [Fact]
    public void Freshness_TheWallClockMoving_ChangesNothing_OnlyTheKeysChangesCount()
    {
        var rig = new Rig();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Open();
        rig.Writing(20);

        // Set an hour on while HWiNFO writes: still live.
        rig.Time.ShiftWall(TimeSpan.FromHours(1));
        rig.Writing(20);
        Assert.Equal(SensorStatus.Live, rig.Status("CPU/Temp"));

        // Set an hour back, and HWiNFO stops: stale after the same window, though the last write now looks an hour ahead.
        rig.Time.ShiftWall(TimeSpan.FromHours(-1));
        rig.Run(10);
        Assert.Equal(SensorStatus.Live, rig.Status("CPU/Temp"));
        rig.Run(1);
        Assert.Equal(SensorStatus.Stale, rig.Status("CPU/Temp"));
    }

    [Fact]
    public void Freshness_ValuesThatAllHoldStill_StayLiveWhileHWiNFORuns_AndGoStaleOnceItStops()
    {
        // Windows doesn't move the key's last-write time when HWiNFO writes the same data again: readings that hold
        // still never move it.
        var rig = new Rig { Running = true };
        rig.Registry.Create().Write(R("Drive: Example SSD", "Drive Temperature", "33"));
        rig.Open();
        rig.Run(120);
        Assert.Equal(SensorStatus.Live, rig.Status("Drive: Example SSD/Drive Temperature"));
        Assert.Equal(GadgetObservation.Reading, rig.Sampler!.Observation);

        // HWiNFO quits and leaves the key: stale within the process check's 5 s, then unavailable and unlisted.
        rig.Running = false;
        rig.Run(5);
        Assert.Equal(SensorStatus.Stale, rig.Status("Drive: Example SSD/Drive Temperature"));
        Assert.Equal(GadgetObservation.NotUpdating, rig.Sampler.Observation);
        rig.Run(31);
        Assert.Equal(SensorStatus.Unavailable, rig.Status("Drive: Example SSD/Drive Temperature"));
        Assert.Empty(rig.Ids);
    }

    [Fact]
    public void Freshness_AKeyLastWrittenHoursAgo_IsLive_WhileHWiNFORuns()
    {
        var rig = new Rig { Running = true };
        rig.Registry.Create().Write(R("Drive: Example SSD", "Drive Temperature", "33"));
        rig.Time.Advance(TimeSpan.FromHours(2));
        rig.Open();
        Assert.Equal(SensorStatus.Live, rig.Status("Drive: Example SSD/Drive Temperature"));
    }

    [Fact]
    public void Freshness_HWiNFOsProcessIsAskedAtMostEvery5Seconds_OnlyWhileTheKeyIsntChanging_AndOnlyWithReadings()
    {
        var rig = new Rig { Running = true };
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Open();
        rig.Writing(20);
        Assert.Equal(0, rig.RunningChecks);

        // Changed last at second 20, so fresh by the key until second 30; asked at 31, 36, 41 and 46.
        rig.Run(30);
        Assert.Equal(4, rig.RunningChecks);
        Assert.Equal(SensorStatus.Live, rig.Status("CPU/Temp"));

        var empty = new Rig { Running = true };
        empty.Registry.Create().Set("Sensor0", "CPU");
        empty.Open();
        empty.Run(30);
        Assert.Equal(0, empty.RunningChecks);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 10)]
    [InlineData(5, 15)]
    [InlineData(20, 60)]
    [InlineData(45, 60)]
    public void Freshness_TheWindowIsThreeTimesTheIntervalBetweenWrites_From10To60Seconds(int every, int window)
    {
        var rig = new Rig();
        rig.Registry.Create().Write(R("CPU", "Temp", "50"));
        rig.Open();
        rig.Writing(every * 4, every);

        rig.Run(window);
        Assert.Equal(SensorStatus.Live, rig.Status("CPU/Temp"));
        rig.Run(1);
        Assert.Equal(SensorStatus.Stale, rig.Status("CPU/Temp"));
    }

    // ---- Hives ----

    [Fact]
    public void Hives_TheUsersKeyFirst_TheMachinesWhenTheUserHasNone()
    {
        var rig = new Rig();
        rig.Registry.Create(GadgetHive.LocalMachine).Write(R("Machine", "Temp", "40"));
        rig.Open();
        Assert.Equal(["Machine/Temp"], rig.Ids);
        Assert.Equal(SensorStatus.Live, rig.Status("Machine/Temp"));

        rig.Run(1, _ => rig.Registry.Create().Write(R("User", "Temp", "50")));
        Assert.Contains("User/Temp", rig.Ids);
        rig.Run(1);
        Assert.Equal(SensorStatus.Live, rig.Status("User/Temp"));
        Assert.NotEqual(SensorStatus.Live, rig.Status("Machine/Temp"));

        var both = new Rig();
        both.Registry.Create(GadgetHive.LocalMachine).Write(R("Machine", "Temp", "40"));
        both.Registry.Create().Write(R("User", "Temp", "50"));
        both.Open();
        Assert.Equal(["User/Temp"], both.Ids);
    }

    // ---- Remotes (HWiNFO's remote PCs, if it reports them to the gadget) ----

    [Fact]
    public void Remotes_KeepTheirHostPrefix_DistinctFromTheSameLocalReading_AndGoStaleAlone()
    {
        var rig = new Rig();
        var local = R("CPU [#0]: AMD Ryzen 7 8845HS", "CPU (Tctl/Tdie)", "50");
        var remote = R("[PC1] CPU [#0]: AMD Ryzen 7 8845HS", "CPU (Tctl/Tdie)", "70");
        rig.Registry.Create().Write(local, remote);
        rig.Open();
        rig.Writing(4);
        var localId = "CPU [#0]: AMD Ryzen 7 8845HS/CPU (Tctl%2FTdie)";
        var remoteId = "[PC1] CPU [#0]: AMD Ryzen 7 8845HS/CPU (Tctl%2FTdie)";
        Assert.Equal([localId, remoteId], rig.Ids);
        Assert.Equal(70, rig.Read(remoteId).ValueNow);

        // The remote disconnects: HWiNFO reports only the local reading.
        rig.Run(1, _ => rig.Key.Write(local));
        rig.Writing(10);
        Assert.Equal(SensorStatus.Stale, rig.Status(remoteId));
        Assert.Equal(SensorStatus.Live, rig.Status(localId));
        rig.Writing(21);
        Assert.Equal(SensorStatus.Unavailable, rig.Status(remoteId));
        Assert.Equal([localId], rig.Ids);
        Assert.Equal(SensorStatus.Live, rig.Status(localId));
    }

    // ---- Observation ----

    [Fact]
    public void Observation_KeyMissing_NoReadings_Reading_NotUpdating_EachChangeLoggedOnce()
    {
        var rig = new Rig();
        rig.Open();
        rig.Run(3);
        Assert.Equal((GadgetObservation.KeyMissing, false), (rig.Sampler!.Observation, rig.Sampler.HadReadings));

        rig.Run(3, s =>
        {
            if (s == 1)
                rig.Registry.Create().Set("Sensor0", "CPU");
        });
        Assert.Equal((GadgetObservation.NoReadings, false), (rig.Sampler.Observation, rig.Sampler.HadReadings));

        rig.Run(1, _ => rig.Key.Write(R("CPU", "Temp", "50")));
        rig.Writing(4);
        Assert.Equal((GadgetObservation.Reading, true), (rig.Sampler.Observation, rig.Sampler.HadReadings));

        rig.Run(15);
        Assert.Equal((GadgetObservation.NotUpdating, true), (rig.Sampler.Observation, rig.Sampler.HadReadings));

        rig.Run(3, s =>
        {
            if (s == 1)
                rig.Registry.Delete();
        });
        Assert.Equal((GadgetObservation.KeyMissing, true), (rig.Sampler.Observation, rig.Sampler.HadReadings));

        // Five observations, each logged once (the first included), never per round.
        Assert.Equal(5, rig.Logged(LogEventLevel.Information, "HWiNFO Gadget:"));
    }

    // ---- Codex round 1 (G1-G6) ----

    [Fact]
    public void G1_Bounds_TheReaderListsNoMoreThanTheKeysCount_ReadsOnlyHWiNFOsNames_AndNeverTheDataOfAnOversizedText()
    {
        var rig = new Rig(throughReader: true);
        var key = rig.Registry.Create();
        key.Write(
            R("CPU", "Temp", "50"),
            R("S", "Long unit", "1", "5 " + new string('x', 1100)),
            R("S", "Edge", "1" + new string(' ', 1023)),
            R("S", "Over", "1" + new string(' ', 1024)));
        key.Set("Unrelated", new string('u', 100_000));
        key.Set("ValueRaw0Extra", "9");
        key.Set("AValueNameLongerThanAnyOfHWiNFOs", "9");
        rig.Open();
        rig.Writing(2);

        Assert.Equal(["CPU/Temp", "S/Edge"], rig.Ids);
        Assert.Equal(SensorStatus.Live, rig.Status("S/Edge"));
        Assert.Equal(2, rig.Logged(LogEventLevel.Warning, "a text longer than 1024 characters"));
        // Names that aren't HWiNFO's are never asked about, and an oversized text's size is read, never its data.
        Assert.DoesNotContain(rig.Registry.Calls, c => c.EndsWith(" Unrelated", StringComparison.Ordinal) || c.EndsWith(" ValueRaw0Extra", StringComparison.Ordinal) || c.EndsWith(" AValueNameLongerThanAnyOfHWiNFOs", StringComparison.Ordinal));
        Assert.Contains("query Value1", rig.Registry.Calls);
        Assert.DoesNotContain("read Value1", rig.Registry.Calls);
        Assert.Contains("query ValueRaw3", rig.Registry.Calls);
        Assert.DoesNotContain("read ValueRaw3", rig.Registry.Calls);
        Assert.Contains("read ValueRaw2", rig.Registry.Calls);

        // A key that grows while its names are listed: no index past the count it had is asked for.
        var count = key.Values.Count;
        rig.Registry.Calls.Clear();
        rig.Registry.OnCall = c =>
        {
            if (c == "enum 0")
            {
                rig.Registry.OnCall = null;
                for (var i = 0; i < HwInfoGadgetSampler.MaxValues; i++)
                    key.Values.Add(new GadgetValue("Pad" + i.ToString(CultureInfo.InvariantCulture), GadgetValueKind.String, "x"));
            }
        };
        rig.Run(1);
        Assert.Contains("enum " + (count - 1).ToString(CultureInfo.InvariantCulture), rig.Registry.Calls);
        Assert.DoesNotContain("enum " + count.ToString(CultureInfo.InvariantCulture), rig.Registry.Calls);
        Assert.Equal(SensorStatus.Stale, rig.Status("CPU/Temp"));
        Assert.Equal(1, rig.Logged(LogEventLevel.Warning, "values, more than 8192"));
    }

    [Fact]
    public void G2_Keys_ASensorAndLabelThatOccurTwice_AreNotOffered_OneWarningNamesThePair_AndNoKeyMovesWhateverIsTicked()
    {
        var rig = new Rig();
        rig.Registry.Create().Write(R("S", "Fan", "1000"), R("CPU", "Temp", "50"), R("S", "Fan", "2000"));
        rig.Open();
        rig.Writing(6);

        Assert.Equal(["CPU/Temp"], rig.Ids);
        Assert.Equal(SensorStatus.Unavailable, rig.Status("S/Fan"));
        Assert.Equal(1, rig.Logged(LogEventLevel.Warning, "the same sensor and label"));
        Assert.Equal(1, rig.Logged(LogEventLevel.Warning, "\"Fan\""));

        // The first fan unticked: its pair is unique now, and offered with the one fan left; CPU Temp keeps its key.
        rig.Run(1, _ => rig.Key.Write(R("CPU", "Temp", "51"), R("S", "Fan", "2000")));
        rig.Run(1);
        Assert.Equal(["CPU/Temp", "S/Fan"], rig.Ids);
        Assert.Equal((2000, SensorStatus.Live), (rig.Read("S/Fan").ValueNow, rig.Status("S/Fan")));
        Assert.Equal((51, SensorStatus.Live), (rig.Read("CPU/Temp").ValueNow, rig.Status("CPU/Temp")));

        // Ticked again: the pair is withdrawn, never moved to the other fan, and warned about again.
        rig.Run(1, _ => rig.Key.Write(R("S", "Fan", "1000"), R("CPU", "Temp", "52"), R("S", "Fan", "2000")));
        rig.Writing(4);
        Assert.Equal(SensorStatus.Stale, rig.Status("S/Fan"));
        Assert.Equal(2000, rig.Read("S/Fan").ValueNow);
        Assert.Equal((52, SensorStatus.Live), (rig.Read("CPU/Temp").ValueNow, rig.Status("CPU/Temp")));
        Assert.Equal(2, rig.Logged(LogEventLevel.Warning, "the same sensor and label"));
        Assert.DoesNotContain(rig.Ids, id => id.Contains('~', StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void H2_Keys_ADuplicatePairWithAnOversizedValue_IsStillADuplicate_NeitherIsOffered_WhicheverHasIt(bool throughReader, int oversized)
    {
        // Two complete S/Fan readings: one's Value (or ValueRaw) is too long to read. The pair is still counted twice,
        // so S/Fan is never offered as the other fan's value.
        foreach (var raw in new[] { false, true })
        {
            var rig = new Rig(throughReader);
            var fans = new[] { R("S", "Fan", "1000"), R("S", "Fan", "2000") };
            fans[oversized] = raw
                ? R("S", "Fan", fans[oversized].Raw + new string(' ', HwInfoGadgetSampler.MaxTextLength))
                : R("S", "Fan", fans[oversized].Raw, "5 " + new string('x', HwInfoGadgetSampler.MaxTextLength));
            rig.Registry.Create().Write(fans[0], R("CPU", "Temp", "50"), fans[1]);
            rig.Open();
            rig.Writing(4);

            Assert.Equal(["CPU/Temp"], rig.Ids);
            Assert.Equal(SensorStatus.Unavailable, rig.Status("S/Fan"));
            Assert.Equal(1, rig.Logged(LogEventLevel.Warning, "the same sensor and label"));
        }
    }

    [Fact]
    public void G3_Renumbering_WhileTheKeyIsRead_IsReadAgain_NeverPairingOneReadingsNameWithAnothersValue()
    {
        var rig = new Rig(throughReader: true);
        rig.Registry.Create().Write(R("CPU", "Temp", "50"), R("GPU", "Load", "7"));
        rig.Open();
        rig.Writing(2);

        // CPU Temp unticked once Sensor0 and Label0 are read: GPU Load moves to index 0 before Value0 and ValueRaw0 are.
        rig.Registry.OnCall = c =>
        {
            if (c == "query Value0")
            {
                rig.Registry.OnCall = null;
                rig.Key.Write(R("GPU", "Load", "7"));
            }
        };
        rig.Run(1);
        Assert.Equal((50, SensorStatus.Stale), (rig.Read("CPU/Temp").ValueNow, rig.Status("CPU/Temp")));
        Assert.Equal((50, 50), (rig.Read("CPU/Temp").ValueMin, rig.Read("CPU/Temp").ValueMax));
        Assert.Equal((7, SensorStatus.Live), (rig.Read("GPU/Load").ValueNow, rig.Status("GPU/Load")));

        // A key that changes during every read: read at most 4 times, then the round is dropped, logged once.
        rig.Registry.OnCall = c =>
        {
            if (c == "query Value0")
            {
                rig.Time.ShiftWall(TimeSpan.FromMilliseconds(1));
                rig.Key.Write(R("GPU", "Load", "8"));
            }
        };
        rig.Registry.Calls.Clear();
        rig.Run(1);
        Assert.Equal(4, rig.Registry.Calls.Count(c => c == "query Value0"));
        Assert.Equal((7, SensorStatus.Stale), (rig.Read("GPU/Load").ValueNow, rig.Status("GPU/Load")));
        rig.Run(2);
        Assert.Equal(1, rig.Logged(LogEventLevel.Warning, "kept changing"));

        rig.Registry.OnCall = null;
        rig.Run(1);
        Assert.Equal((8, SensorStatus.Live), (rig.Read("GPU/Load").ValueNow, rig.Status("GPU/Load")));
    }

    [Fact]
    public void G4_Freshness_AHWiNFOInAnotherSession_DoesntKeepThisUsersStillValuesLive()
    {
        var sessions = new List<int> { 2 };
        var rig = new Rig(running: HwInfoGadgetSource.HwInfoRunningIn(1, () => sessions));
        rig.Registry.Create().Write(R("Drive", "Temp", "33"));
        rig.Open();
        rig.Run(15);
        Assert.Equal(SensorStatus.Stale, rig.Status("Drive/Temp"));
        Assert.Equal(GadgetObservation.NotUpdating, rig.Sampler!.Observation);

        // HWiNFO started in this session: its still values are live.
        sessions.Add(1);
        rig.Run(5);
        Assert.Equal(SensorStatus.Live, rig.Status("Drive/Temp"));
        Assert.True(HwInfoGadgetSource.HwInfoRunningIn(3, () => [1, 3])());
        Assert.False(HwInfoGadgetSource.HwInfoRunningIn(3, () => [])());
    }

    [Fact]
    public void H1_Freshness_AnEngineInSession0_WithAHWiNFOInSession0_StillGoesStale()
    {
        // Session 0 is the services' session, shared by users: a HWiNFO there may be another user's, writing its
        // own hive. There, freshness is the key's last-write time alone.
        var rig = new Rig(running: HwInfoGadgetSource.HwInfoRunningIn(0, () => [0]));
        rig.Registry.Create().Write(R("Drive", "Temp", "33"));
        rig.Open();
        rig.Writing(6);
        Assert.Equal(SensorStatus.Live, rig.Status("Drive/Temp"));
        rig.Run(15);
        Assert.Equal(SensorStatus.Stale, rig.Status("Drive/Temp"));
        Assert.Equal(GadgetObservation.NotUpdating, rig.Sampler!.Observation);
        Assert.False(HwInfoGadgetSource.HwInfoRunningIn(0, () => [0, 1])());
        Assert.True(HwInfoGadgetSource.HwInfoRunningIn(1, () => [0, 1])());
    }

    [Fact]
    public void G5_Hives_FallingBackToAnHoursOldMachineKey_DoesntMakeItLive_UntilItIsWritten()
    {
        var rig = new Rig();
        rig.Registry.Create(GadgetHive.LocalMachine).Write(R("Machine", "Temp", "40"));
        rig.Time.Advance(TimeSpan.FromHours(3));
        rig.Registry.Create().Write(R("User", "Temp", "50"));
        rig.Open();
        rig.Writing(20);
        Assert.Equal(SensorStatus.Live, rig.Status("User/Temp"));

        // The user's key deleted: the machine's, last written three hours ago, is read, but never live.
        rig.Run(1, _ => rig.Registry.Delete());
        for (var s = 0; s < 40; s++)
        {
            Assert.NotEqual(SensorStatus.Live, rig.Status("Machine/Temp"));
            rig.Run(1);
        }

        Assert.Equal(GadgetObservation.NotUpdating, rig.Sampler!.Observation);
        Assert.Empty(rig.Ids);

        // Written now: live.
        rig.Run(1, _ => rig.Registry[GadgetHive.LocalMachine].Write(R("Machine", "Temp", "41")));
        rig.Run(1);
        Assert.Equal((41, SensorStatus.Live), (rig.Read("Machine/Temp").ValueNow, rig.Status("Machine/Temp")));

        // The user's key made again: a new key, live at once.
        rig.Run(1, _ => rig.Registry.Create().Write(R("User", "Temp", "52")));
        rig.Run(1);
        Assert.Equal((52, SensorStatus.Live), (rig.Read("User/Temp").ValueNow, rig.Status("User/Temp")));
    }

    [Fact]
    public void G6_AValueDeletedBetweenListingAndReading_IsSkipped_TheRoundGoesOn()
    {
        var rig = new Rig(throughReader: true);
        rig.Registry.Create().Write(R("CPU", "Temp", "50"), R("GPU", "Load", "7"));
        rig.Open();
        rig.Writing(2);

        // Listed, then deleted before it is read (the key's time not moved yet).
        rig.Registry.OnCall = c =>
        {
            if (c == "query ValueRaw1")
            {
                rig.Registry.OnCall = null;
                rig.Key.Values.RemoveAll(v => v.Name == "ValueRaw1");
            }
        };
        rig.Run(1, _ => rig.Key.Write(R("CPU", "Temp", "51"), R("GPU", "Load", "8")));
        Assert.Contains("query ValueRaw1", rig.Registry.Calls);
        Assert.Equal((51, SensorStatus.Live), (rig.Read("CPU/Temp").ValueNow, rig.Status("CPU/Temp")));
        Assert.Equal(SensorStatus.Stale, rig.Status("GPU/Load"));
        Assert.Equal(0, rig.Logged(LogEventLevel.Warning, "failed"));
    }

    // ---- The source ----

    private sealed class OneSensorSampler : ISensorSampler
    {
        public IReadOnlyList<SensorDefinition> Discover() => [new SensorDefinition("CPU/Temp", "CPU", "Temp", "°C")];

        public void Sample(Span<SensorSample> samples, TimeSpan elapsed) => samples[0].Set(42);

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Source_IsInfopanelHwinfoGadget_PolledEverySecond_AnsweringOnlyItsOwnKeys()
    {
        var time = new GadgetClock(DateTimeOffset.UnixEpoch);
        using var source = HwInfoGadgetSource.Create(() => new OneSensorSampler(), time);
        source.Open();

        Assert.Equal("infopanel.hwinfo-gadget", source.Id);
        Assert.Equal(TimeSpan.FromSeconds(1), source.Options.Interval);
        var sensor = Assert.Single(source.GetSensors());
        Assert.Equal("plugin:/infopanel.hwinfo-gadget/CPU/Temp", sensor.Key.ToString());
        Assert.Equal(sensor.Key, HwInfoGadgetSource.KeyOf("CPU/Temp"));
        Assert.Equal(42, source.TryRead(sensor.Key, out var reading) ? reading.ValueNow : double.NaN);

        Assert.True(source.Handles(SensorKey.Plugin("/infopanel.hwinfo-gadget/Anything/Else")));
        Assert.False(source.Handles(SensorKey.Plugin("/hwinfo-registry-plugin/0")));
        Assert.False(source.Handles(SensorKey.Plugin("/infopanel.macos/cpu/load")));
        Assert.False(source.Handles(SensorKey.Plugin("/infopanel.hwinfo-gadgets/CPU/Temp")));
        Assert.False(source.Handles(SensorKey.HwInfo(-1, 1, 0, 1)));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(SensorKey.Plugin("/infopanel.hwinfo-gadget/No/Such")));
    }
}
