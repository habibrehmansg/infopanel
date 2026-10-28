using System.Data;
using InfoPanel.Models;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The engine's sensors as scene inputs (spec/docs/bindings.md, "Status"): a sensor's status reaches an input through
/// the stated mapping, so a scene sees <c>stale</c> and <c>denied</c> as the sensor list does.
/// </summary>
public sealed class SceneInputStatusTests
{
    /// <summary>A source whose keys each have the status and the kind of reading the test sets.</summary>
    private sealed class StatusSource : ISensorSource, ISensorStatusSource
    {
        public Dictionary<string, (SensorStatus Status, SensorReading Reading)> Keys { get; } = new(StringComparer.Ordinal);

        /// <summary>A key's type when it has no reading (a number unless set).</summary>
        public Dictionary<string, SensorKind> Types { get; } = new(StringComparer.Ordinal);

        /// <summary>Calls into the source, by member name.</summary>
        public List<string> Calls { get; } = [];

        public string Id => "test.status";

        public bool Handles(SensorKey key) => key.Id.StartsWith("/test.status/", StringComparison.Ordinal);

        public IReadOnlyList<SensorInfo> GetSensors() => [];

        public SensorStatus GetStatus(SensorKey key)
        {
            Calls.Add(nameof(GetStatus));
            return StatusOf(key);
        }

        public bool TryRead(SensorKey key, out SensorReading reading)
        {
            Calls.Add(nameof(TryRead));
            reading = Keys.TryGetValue(key.Id, out var k) ? k.Reading : default;
            return StatusOf(key) is SensorStatus.Live or SensorStatus.Stale;
        }

        public SensorState ReadState(SensorKey key)
        {
            Calls.Add(nameof(ReadState));
            var status = StatusOf(key);
            return status is SensorStatus.Live or SensorStatus.Stale
                ? new SensorState(status, SensorState.TypeOf(Keys[key.Id].Reading), Keys[key.Id].Reading)
                : SensorState.Without(status, Types.GetValueOrDefault(key.Id, SensorKind.Number));
        }

        private SensorStatus StatusOf(SensorKey key) => Keys.TryGetValue(key.Id, out var k) ? k.Status : SensorStatus.Unavailable;

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>One numeric sensor <c>a</c> that reads <see cref="Value"/>, or fails with <see cref="Outcome"/>.</summary>
    private sealed class OneSensor : ISensorSampler
    {
        public double Value = 10;
        public SampleOutcome Outcome = SampleOutcome.Ok;

        public IReadOnlyList<SensorDefinition> Discover() => [new SensorDefinition("a", "Group", "A", "u")];

        public void Sample(Span<SensorSample> samples, TimeSpan elapsed) =>
            samples[0] = new SensorSample { Value = Value, Outcome = Outcome };

        public void Dispose()
        {
        }
    }

    /// <summary>A polled source over <see cref="OneSensor"/>, opened (one good sample of 10, live).</summary>
    private static (PolledSensorSource Source, OneSensor Sampler, Sensors.TimerTimeProvider Time) Polled()
    {
        var time = new Sensors.TimerTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var sampler = new OneSensor();
        var source = new PolledSensorSource("test.poll", () => sampler, new PolledSensorSourceOptions(), time);
        source.Open();
        return (source, sampler, time);
    }

    /// <summary>
    /// A source over a polled one that runs <see cref="After"/> once, right after a call into it returns: the seam that
    /// lands a poll between two reads of the same tick.
    /// </summary>
    private sealed class Interleaved(PolledSensorSource inner) : ISensorSource, ISensorStatusSource
    {
        public Action? After { get; set; }

        public string Id => inner.Id;

        public bool Handles(SensorKey key) => inner.Handles(key);

        public IReadOnlyList<SensorInfo> GetSensors() => inner.GetSensors();

        public bool TryRead(SensorKey key, out SensorReading reading)
        {
            var found = inner.TryRead(key, out reading);
            Then();
            return found;
        }

        public SensorStatus GetStatus(SensorKey key)
        {
            var status = inner.GetStatus(key);
            Then();
            return status;
        }

        public SensorState ReadState(SensorKey key)
        {
            var state = inner.ReadState(key);
            Then();
            return state;
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        private void Then()
        {
            var after = After;
            After = null;
            after?.Invoke();
        }
    }

    private static readonly SensorReading Number = new(1, 9, 5, 7.5, "W");

    private static SensorReading Table()
    {
        var dt = new DataTable();
        dt.Columns.Add("a");
        dt.Rows.Add("x");
        return new SensorReading(dt, "", "summary");
    }

    private static string Key(string name) => SensorKey.Plugin("/test.status/" + name).ToString();

    /// <summary>The adapter over a hub, typed as the engine wires it (an <see cref="ISensorReader"/>).</summary>
    private static (StatusSource Source, SensorSceneInputs Inputs) Hub(SensorHubOptions? options = null)
    {
        var source = new StatusSource();
        ISensorReader hub = new SensorHub([source], options);
        return (source, new SensorSceneInputs(hub, new FakeSensorHistory()));
    }

    /// <summary>Evaluates <paramref name="expr"/> with the number input <c>v</c> bound to the adapter's reading of <paramref name="key"/>.</summary>
    private static Val Eval(SensorSceneInputs inputs, string key, string expr) =>
        SceneExpressionTests.Eval(expr, f => f.Readings["test:v"] = inputs.Read(key, "number")).Value;

    private const string StatusOf = "{'op':'input','name':'v','field':'status'}";
    private const string AvailableOf = "{'op':'input','name':'v','field':'available'}";
    private const string NowOf = "{'op':'input','name':'v','field':'now'}";

    [Fact]
    public void A_stale_reading_reads_stale_in_a_scene_with_its_last_value()
    {
        var (source, inputs) = Hub();
        source.Keys["/test.status/a"] = (SensorStatus.Stale, Number);

        Assert.Equal(SceneInputStatus.Stale, inputs.Read(Key("a"), "number").Status);
        Assert.Equal("stale", Eval(inputs, Key("a"), StatusOf).Text);
        Assert.True(Eval(inputs, Key("a"), AvailableOf).B);
        Assert.Equal(7.5, Eval(inputs, Key("a"), NowOf).N);
    }

    [Fact]
    public void A_refused_reading_reads_denied_in_a_scene()
    {
        var (source, inputs) = Hub();
        source.Keys["/test.status/a"] = (SensorStatus.PermissionDenied, Number);

        Assert.Equal("denied", Eval(inputs, Key("a"), StatusOf).Text);
        Assert.False(Eval(inputs, Key("a"), AvailableOf).B);
        Assert.Equal(0, Eval(inputs, Key("a"), NowOf).N);
    }

    [Theory]
    [InlineData("number", SceneInputStatus.Denied)]
    [InlineData("text", SceneInputStatus.Unavailable)]
    [InlineData("table", SceneInputStatus.Unavailable)]
    public void A_refused_number_sensor_is_denied_to_a_number_input_and_unavailable_to_another_type(string type, SceneInputStatus expected)
    {
        // A sensor of another type than the input is unavailable whatever its status (spec/docs/bindings.md).
        var (source, inputs) = Hub();
        source.Keys["/test.status/a"] = (SensorStatus.PermissionDenied, default);

        var reading = inputs.Read(Key("a"), type);
        Assert.Equal(expected, reading.Status);
        Assert.False(reading.Available);
    }

    [Fact]
    public void A_polled_sensor_of_another_type_reads_unavailable_while_refused_and_after()
    {
        var (polled, sampler, _) = Polled();
        var key = polled.KeyOf("a").ToString();
        var inputs = new SensorSceneInputs(new SensorHub([polled]), new FakeSensorHistory());

        sampler.Outcome = SampleOutcome.PermissionDenied;
        polled.Poll();
        Assert.Equal(SceneInputStatus.Denied, inputs.Read(key, "number").Status);
        Assert.Equal(SceneInputStatus.Unavailable, inputs.Read(key, "text").Status);
        Assert.Equal(SceneInputStatus.Unavailable, inputs.Read(key, "table").Status);

        sampler.Outcome = SampleOutcome.Ok;
        polled.Poll();
        Assert.Equal(SceneInputStatus.Live, inputs.Read(key, "number").Status);
        Assert.Equal(SceneInputStatus.Unavailable, inputs.Read(key, "text").Status);
    }

    [Fact]
    public void A_poll_between_the_reads_never_labels_an_old_value_live()
    {
        var (polled, sampler, time) = Polled();
        var key = polled.KeyOf("a").ToString();
        var source = new Interleaved(polled);
        var inputs = new SensorSceneInputs(new SensorHub([source]), new FakeSensorHistory());

        // Stale with 10; a poll that publishes a fresh 20 lands right after the adapter's first call into the source.
        time.Advance(TimeSpan.FromSeconds(6));
        sampler.Value = 20;
        source.After = polled.Poll;
        var reading = inputs.Read(key, "number");
        Assert.Null(source.After);
        Assert.Equal((SceneInputStatus.Stale, 10.0), (reading.Status, reading.Now));

        // The next tick reads the fresh value, live.
        reading = inputs.Read(key, "number");
        Assert.Equal((SceneInputStatus.Live, 20.0), (reading.Status, reading.Now));
    }

    [Fact]
    public void A_stale_text_or_table_reading_is_stale()
    {
        var (source, inputs) = Hub();
        source.Keys["/test.status/text"] = (SensorStatus.Stale, new SensorReading("hello"));
        source.Keys["/test.status/table"] = (SensorStatus.Stale, Table());

        var text = inputs.Read(Key("text"), "text");
        Assert.Equal(SceneInputStatus.Stale, text.Status);
        Assert.Equal("hello", text.Text);
        Assert.Equal(SceneInputStatus.Stale, inputs.Read(Key("table"), "table").Status);
    }

    [Fact]
    public void A_simulated_sensor_reads_live_and_a_malformed_key_unavailable()
    {
        var (_, inputs) = Hub(new SensorHubOptions { SimulateUnknown = true });

        Assert.Equal(SceneInputStatus.Live, inputs.Read(SensorKey.Plugin("/someplugin/sensor").ToString(), "number").Status);
        // A key the hub calls missing (an HWiNFO key with sensor id 0, 1.x's unbound) is unavailable to a scene.
        Assert.Equal(SensorStatus.Missing, new SensorHub([], new SensorHubOptions { SimulateUnknown = true }).GetStatus(SensorKey.HwInfo(-1, 0, 0, 7)));
        Assert.Equal(SceneInputStatus.Unavailable, inputs.Read(SensorKey.HwInfo(-1, 0, 0, 7).ToString(), "number").Status);
        Assert.Equal(SceneInputStatus.Unavailable, inputs.Read("not a key", "number").Status);
    }

    [Fact]
    public void A_stale_or_live_sensor_of_another_type_is_unavailable()
    {
        var (source, inputs) = Hub();
        source.Keys["/test.status/a"] = (SensorStatus.Stale, Number);
        source.Keys["/test.status/b"] = (SensorStatus.Live, new SensorReading("hello"));

        Assert.Equal(SceneInputStatus.Unavailable, inputs.Read(Key("a"), "text").Status);
        Assert.Equal(SceneInputStatus.Unavailable, inputs.Read(Key("b"), "number").Status);
    }

    [Fact]
    public void A_reading_and_its_status_come_from_one_call_into_the_source()
    {
        // One snapshot per key and tick: no call for the status after the reading, which a poll could land between.
        var (source, inputs) = Hub();
        source.Keys["/test.status/a"] = (SensorStatus.Stale, Number);

        Assert.Equal(SceneInputStatus.Stale, inputs.Read(Key("a"), "number").Status);
        Assert.Equal([nameof(StatusSource.ReadState)], source.Calls);
    }

    [Theory]
    [InlineData("text", SceneInputStatus.Denied)]
    [InlineData("number", SceneInputStatus.Unavailable)]
    [InlineData("table", SceneInputStatus.Unavailable)]
    public void A_refused_sensor_is_typed_by_its_source_without_a_reading(string type, SceneInputStatus expected)
    {
        var (source, inputs) = Hub();
        source.Keys["/test.status/a"] = (SensorStatus.PermissionDenied, default);
        source.Types["/test.status/a"] = SensorKind.Text;

        Assert.Equal(expected, inputs.Read(Key("a"), type).Status);
    }

    [Fact]
    public void A_reader_without_statuses_is_live_with_a_reading_and_unavailable_without()
    {
        var reader = new FakeSensorReader();
        var key = SensorKey.Plugin("/plain/a");
        reader.Readings[key] = Number;
        var inputs = new SensorSceneInputs(reader, new FakeSensorHistory());

        Assert.Equal(SceneInputStatus.Live, inputs.Read(key.ToString(), "number").Status);
        Assert.Equal(SceneInputStatus.Unavailable, inputs.Read(SensorKey.Plugin("/plain/b").ToString(), "number").Status);
    }
}
