using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Import;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Models;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using InfoPanel.Spec;
using Xunit;
using ManualClock = InfoPanel.Core.Tests.Editing.ManualTimeProvider;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>A simulated subscription's inputs: made-up numbers over this computer's own sensors.</summary>
public class SimulatedSceneInputsTests
{
    private static readonly SensorKey Unknown = SensorKey.HwInfo(-1, 0xF0000300, 0, 7);
    private static readonly SensorKey Clock = SensorKey.Plugin("/test/cpu/clock");
    private static readonly SensorKey Load = SensorKey.Plugin("/test/cpu/load");
    private static readonly SensorKey Fan = SensorKey.Plugin("/test/fan");
    private static readonly SensorKey Name = SensorKey.Plugin("/test/cpu/name");

    private readonly FakeSensorReader _sensors = new();
    private readonly ManualClock _clock = new();

    private (ISceneInputSource Live, ISceneInputSource Simulated) Inputs()
    {
        var live = new SensorSceneInputs(_sensors, new FakeSensorHistory());
        return (live, new SimulatedSceneInputs(live, _clock));
    }

    /// <summary>The key's values over <paramref name="seconds"/> seconds, read once a second, the first read included.</summary>
    private List<SceneReading> Walk(ISceneInputSource inputs, SensorKey key, int seconds)
    {
        var readings = new List<SceneReading> { inputs.Read(key.ToString(), "number") };
        for (var i = 0; i < seconds; i++)
        {
            _clock.Advance(1000);
            readings.Add(inputs.Read(key.ToString(), "number"));
        }
        return readings;
    }

    [Fact]
    public void A_key_this_computer_lacks_reads_unavailable_live_and_moves_when_simulated_from_a_start_between_20_and_80()
    {
        var (live, simulated) = Inputs();

        Assert.Equal(SceneInputStatus.Unavailable, live.Read(Unknown.ToString(), "number").Status);
        var start = simulated.History(Unknown.ToString())[0];
        Assert.InRange(start, 20, 80);
        var walk = Walk(simulated, Unknown, 30);
        Assert.All(walk, r => Assert.Equal(SceneInputStatus.Live, r.Status));
        Assert.All(walk, r => Assert.InRange(r.Now, 0, 100));
        Assert.True(walk.Select(r => r.Now).Distinct().Count() > 20, "The number moves.");
        Assert.Equal(SceneInputStatus.Unavailable, live.Read(Unknown.ToString(), "number").Status);
    }

    [Fact]
    public void Keys_this_computer_lacks_start_between_20_and_80_and_stay_within_them()
    {
        var (_, simulated) = Inputs();
        var keys = Enumerable.Range(0, 40).Select(i => SensorKey.Plugin($"/test/none/{i}")).ToList();

        foreach (var key in keys)
            Assert.All(simulated.History(key.ToString()), v => Assert.InRange(v, 20, 80));
        var walks = keys.ToDictionary(k => k, _ => new List<SceneReading>());
        for (var second = 0; second <= 600; second++)
        {
            foreach (var key in keys)
                walks[key].Add(simulated.Read(key.ToString(), "number"));
            _clock.Advance(1000);
        }

        foreach (var (key, walk) in walks)
        {
            Assert.All(walk, r => Assert.Equal(SceneInputStatus.Live, r.Status));
            Assert.All(walk, r =>
            {
                Assert.InRange(r.Now, 20, 80);
                Assert.InRange(r.Min, 20, 80);
                Assert.InRange(r.Max, 20, 80);
                Assert.InRange(r.Avg, 20, 80);
            });
            Assert.All(simulated.History(key.ToString()), v => Assert.InRange(v, 20, 80));
            Assert.True(walk.Select(r => r.Now).Distinct().Count() > 100, $"{key} moves.");
        }
    }

    [Fact]
    public void A_key_with_a_reading_starts_at_it_and_stays_within_a_band_proportional_to_it()
    {
        _sensors.Readings[Clock] = new SensorReading(3400, 3600, 3500, 3500, "MHz");
        _sensors.Readings[Fan] = new SensorReading(0, 0, 0, 0, "RPM");
        var (_, simulated) = Inputs();

        Assert.Equal(3500, simulated.History(Clock.ToString())[0]);
        var walk = Walk(simulated, Clock, 600);
        Assert.All(walk, r => Assert.InRange(r.Now, 2800, 4200));
        Assert.All(walk, r => Assert.Equal("MHz", r.Unit));
        Assert.True(walk.Select(r => r.Now).Distinct().Count() > 500, "The number moves.");
        Assert.InRange(walk.Average(r => r.Now), 3300, 3700);

        // A reading that isn't negative never turns negative.
        Assert.All(Walk(simulated, Fan, 600), r => Assert.True(r.Now >= 0, $"{r.Now}"));
    }

    [Theory]
    [InlineData(97)]
    [InlineData(2)]
    [InlineData(0)]
    public void A_percentage_stays_within_0_to_100(double load)
    {
        _sensors.Readings[Load] = new SensorReading(load, load, load, load, "%");
        var (_, simulated) = Inputs();

        Assert.Equal(load, simulated.History(Load.ToString())[0]);
        var walk = Walk(simulated, Load, 600);
        Assert.All(walk, r => Assert.InRange(r.Now, 0, 100));
        Assert.All(walk, r => Assert.Equal("%", r.Unit));
        Assert.True(walk.Select(r => r.Now).Distinct().Count() > 100, "The number moves.");
    }

    [Fact]
    public void The_history_holds_120_values_at_the_first_read_then_one_more_a_second()
    {
        var (_, simulated) = Inputs();

        var first = simulated.Read(Unknown.ToString(), "number");
        var history = simulated.History(Unknown.ToString());
        Assert.Equal(SimulatedSceneInputs.Backfill, history.Count);
        Assert.Equal(first.Now, history[^1]);

        _clock.Advance(999);
        Assert.Equal(first.Now, simulated.Read(Unknown.ToString(), "number").Now);
        Assert.Equal(SimulatedSceneInputs.Backfill, simulated.History(Unknown.ToString()).Count);
        _clock.Advance(1);
        var next = simulated.Read(Unknown.ToString(), "number");
        Assert.NotEqual(first.Now, next.Now);
        Assert.Equal(SimulatedSceneInputs.Backfill + 1, simulated.History(Unknown.ToString()).Count);
        Assert.Equal(next.Now, simulated.History(Unknown.ToString())[^1]);
        _clock.Advance(3000);
        Assert.Equal(SimulatedSceneInputs.Backfill + 4, simulated.History(Unknown.ToString()).Count);
    }

    [Fact]
    public void Text_and_tables_read_as_live_ones_do()
    {
        _sensors.Readings[Name] = new SensorReading("Apple M4 Pro");
        var (live, simulated) = Inputs();

        Assert.Equal(live.Read(Name.ToString(), "text"), simulated.Read(Name.ToString(), "text"));
        Assert.Equal("Apple M4 Pro", simulated.Read(Name.ToString(), "text").Text);
        Assert.Equal(SceneInputStatus.Unavailable, simulated.Read(Unknown.ToString(), "text").Status);
        Assert.Equal(SceneInputStatus.Unavailable, simulated.Read(Unknown.ToString(), "table").Status);
    }

    [Fact]
    public void A_key_that_isnt_well_formed_reads_as_live()
    {
        var (_, simulated) = Inputs();

        // 1.x items start out bound to sensor id 0, which means unbound.
        var unbound = SensorKey.HwInfo(-1, 0, 0, 0).ToString();
        Assert.Equal(SceneInputStatus.Unavailable, simulated.Read(unbound, "number").Status);
        Assert.Empty(simulated.History(unbound));
        Assert.Equal(SceneInputStatus.Unavailable, simulated.Read("not a key", "number").Status);
    }

    /// <summary>
    /// The 1.x e2e archive (<c>Fixtures/e2e/all-kinds.infopanel</c>), read by the 1.x import, as its reference document
    /// (<c>Scenes/Reference/v1/all-kinds.document.json</c>, the same items as a 2.0 document): with no sensor source, its
    /// HWiNFO-bound sensor text reads no number live, and draws numbers when simulated.
    /// </summary>
    [Fact]
    public void The_1x_all_kinds_profile_draws_numbers_for_its_HWiNFO_keys_when_simulated()
    {
        using var kit = new ImportKit();
        var envelope = kit.ReadArchive(File.ReadAllBytes(ProfileSerializerTests.FixturePath("Fixtures/e2e/all-kinds.infopanel")));
        var archiveKeys = Nodes(envelope.Items).Select(n => n.Meta.Sensor?.Key).OfType<string>().Where(k => k.StartsWith("hwinfo:", StringComparison.Ordinal)).ToHashSet();
        var path = Path.Combine(V1ParityTests.ReferenceDir, "all-kinds.document.json");
        var documentKeys = Keys(JsonNode.Parse(File.ReadAllText(path))!).Where(k => k.StartsWith("hwinfo:", StringComparison.Ordinal)).ToHashSet();
        Assert.Equal(3, archiveKeys.Count);
        Assert.Equal(archiveKeys.Order(), documentKeys.Order());

        var document = SceneKit.Load(path);
        var hub = new SensorHub([]);
        var live = new SensorSceneInputs(hub, new SensorHistory(hub, _clock));
        var simulated = new SimulatedSceneInputs(live, _clock);
        foreach (var key in archiveKeys)
        {
            Assert.Equal(SceneInputStatus.Unavailable, live.Read(key, "number").Status);
            Assert.Equal(SceneInputStatus.Live, simulated.Read(key, "number").Status);
        }

        // "CPU Temp" is a sensor value bound to one of them.
        Assert.DoesNotContain(Text(live), char.IsAsciiDigit);
        var drawn = Text(simulated);
        Assert.Contains(drawn, char.IsAsciiDigit);
        _clock.Advance(5000);
        Assert.NotEqual(drawn, Text(simulated));

        string Text(ISceneInputSource inputs)
        {
            var options = new SceneRuntimeOptions { Inputs = inputs, LocalZone = TimeZoneInfo.Utc, Locale = RegionalFormat.Invariant };
            using var snapshot = SceneRuntime.RenderOnce(document, _clock.GetUtcNow(), options);
            return Assert.Single(snapshot.Texts, t => t.ItemId == "v1-011").Text;
        }
    }

    [Fact]
    public void Every_binding_of_a_document_at_its_limits_is_simulated_and_after_a_rebinding_every_new_one()
    {
        var (_, simulated) = Inputs();
        // A key of its own for each binding a document may hold: each item's bindings, for every item it may have.
        var bindings = SpecLimits.Default.Items * SpecLimits.Default.Bindings;
        int Simulated(string prefix) => Enumerable.Range(0, bindings)
            .Count(i => simulated.Read(SensorKey.Plugin($"/{prefix}/{i}").ToString(), "number").Status == SceneInputStatus.Live);

        Assert.Equal(bindings, Simulated("before"));
        // Every item bound again: the keys no longer read are forgotten, so every new one is simulated too.
        _clock.Advance(61_000);
        Assert.Equal(bindings, Simulated("after"));
    }

    [Fact]
    public void A_key_unread_for_a_minute_is_forgotten_and_starts_afresh_when_read_again()
    {
        var (_, simulated) = Inputs();
        var key = Unknown.ToString();
        var started = simulated.History(key);
        Walk(simulated, Unknown, 30);
        Assert.Equal(SimulatedSceneInputs.Backfill + 30, simulated.History(key).Count);

        _clock.Advance(60_000);
        Assert.Equal(started, simulated.History(key));
        Assert.Equal(started[^1], simulated.Read(key, "number").Now);
    }

    [Fact]
    public void A_full_history_takes_each_step_without_moving_the_values_before_it()
    {
        var live = new SensorSceneInputs(_sensors, new FakeSensorHistory());
        var rare = new SimulatedSceneInputs(live, _clock);
        var often = new SimulatedSceneInputs(live, _clock);
        var key = Unknown.ToString();
        rare.Read(key, "number");
        often.Read(key, "number");

        // A loop that ticks every 30 s catches up 30 steps a read: 4,096 steps go past a full history.
        var seconds = 2 * SensorHistory.MaxSamples;
        for (var second = 1; second <= seconds; second++)
        {
            _clock.Advance(1000);
            often.Read(key, "number");
            if (second % 30 == 0 || second == seconds)
                rare.Read(key, "number");
        }

        var history = rare.History(key);
        Assert.Equal(SensorHistory.MaxSamples, history.Count);
        Assert.Equal(often.History(key), history);
        // Linear in the steps: no step copies the history; growing it and the one read of it copy each value at most twice.
        Assert.InRange(rare.Copied, 0, 2L * SensorHistory.MaxSamples);
    }

    /// <summary>A key of its own for each binding a document at its limits holds.</summary>
    private static List<string> EveryBinding(string prefix) => Enumerable.Range(0, SpecLimits.Default.Items * SpecLimits.Default.Bindings)
        .Select(i => SensorKey.Plugin($"/{prefix}/{i}").ToString()).ToList();

    [Fact]
    public void At_a_documents_limits_only_1024_keys_keep_a_history_and_the_values_held_stay_within_their_full_histories()
    {
        var simulated = new SimulatedSceneInputs(new SensorSceneInputs(_sensors, new FakeSensorHistory()), _clock);
        var keys = EveryBinding("held");
        var histories = 0;
        // As the runtime reads a tick: each number binding's reading, then its history.
        void Tick()
        {
            histories = 0;
            foreach (var key in keys)
            {
                Assert.Equal(SceneInputStatus.Live, simulated.Read(key, "number").Status);
                if (simulated.History(key).Count > 0) histories++;
            }
        }

        Tick();
        Assert.Equal(SensorHistory.MaxSeries, histories);
        Assert.Equal(SimulatedSceneInputs.Backfill, simulated.History(keys[0]).Count);
        Assert.Empty(simulated.History(keys[^1]));

        // Over an hour, every key read within a minute of its last read: the 1,024 histories fill, the rest hold none.
        for (var second = 0; second < SensorHistory.MaxSamples; second += 59)
        {
            _clock.Advance(59_000);
            Tick();
        }
        Assert.Equal(SensorHistory.MaxSeries, histories);
        Assert.Equal(SensorHistory.MaxSamples, simulated.History(keys[0]).Count);
        Assert.InRange(simulated.Held, 0, (long)SensorHistory.MaxSeries * SensorHistory.MaxSamples);
        // A key with no history still has its minimum, maximum and average.
        var last = simulated.Read(keys[^1], "number");
        Assert.True(last.Min <= last.Avg && last.Avg <= last.Max && last.Min < last.Max, $"{last.Min} {last.Avg} {last.Max}");
    }

    [Fact]
    public void With_every_key_of_a_document_at_its_limits_walking_a_rebinding_is_simulated_at_its_first_read()
    {
        var (_, simulated) = Inputs();
        var keys = EveryBinding("bound");
        Assert.All(keys, k => Assert.Equal(SceneInputStatus.Live, simulated.Read(k, "number").Status));

        // A second later one item, halfway through the document, is bound to another key.
        _clock.Advance(1000);
        var rebound = keys.Count / 2;
        keys[rebound] = SensorKey.Plugin("/bound/new").ToString();
        Assert.All(keys, k => Assert.Equal(SceneInputStatus.Live, simulated.Read(k, "number").Status));
        // The keys still bound walk on: none of them started afresh.
        Assert.Equal(SimulatedSceneInputs.Backfill + 1, simulated.History(keys[0]).Count);
    }

    [Fact]
    public void A_key_unread_for_a_minute_starts_afresh_even_when_another_was_read_just_before()
    {
        var (_, simulated) = Inputs();
        var a = Unknown.ToString();
        var b = Load.ToString();

        simulated.Read(a, "number");
        _clock.Advance(59_500);
        simulated.Read(b, "number");
        _clock.Advance(500);
        simulated.Read(a, "number");
        Assert.Equal(SimulatedSceneInputs.Backfill, simulated.History(a).Count);
    }

    [Theory]
    [InlineData(double.MaxValue)]
    [InlineData(-double.MaxValue)]
    public void A_reading_at_the_ends_of_the_doubles_publishes_finite_values(double reading)
    {
        _sensors.Readings[Clock] = new SensorReading(reading, reading, reading, reading, "MHz");
        var (_, simulated) = Inputs();

        Assert.All(simulated.History(Clock.ToString()), v => Assert.True(double.IsFinite(v), $"{v}"));
        Assert.All(Walk(simulated, Clock, 120), r =>
            Assert.True(double.IsFinite(r.Now) && double.IsFinite(r.Min) && double.IsFinite(r.Max) && double.IsFinite(r.Avg), $"{r.Now} {r.Min} {r.Max} {r.Avg}"));
        Assert.All(simulated.History(Clock.ToString()), v => Assert.True(double.IsFinite(v), $"{v}"));
    }

    private static IEnumerable<InfoPanel.Editing.ItemNode> Nodes(IEnumerable<InfoPanel.Editing.ItemNode> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Nodes(n.Children ?? [])).Concat(Nodes(n.Images ?? [])));

    private static IEnumerable<string> Keys(JsonNode node) => node switch
    {
        JsonObject o => o.SelectMany(p => p.Key == "key" && p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? [s] : p.Value is { } child ? Keys(child) : []),
        JsonArray a => a.SelectMany(n => n is null ? [] : Keys(n)),
        _ => [],
    };
}
