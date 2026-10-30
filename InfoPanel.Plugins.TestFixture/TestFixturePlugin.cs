using System.Data;
using InfoPanel.Plugins;

namespace InfoPanel.TestFixture;

/// <summary>
/// The engine tests' plugin: a sensor, a text and a table in a container, a sensor in an ephemeral container, an action,
/// and a setting of each type; <see cref="FixtureMode"/> makes it misbehave on request.
/// </summary>
public sealed class TestFixturePlugin : BasePlugin, IPluginConfigurable
{
    public const string DefaultId = "test-fixture";

    private readonly PluginSensor _counter = new("counter", "Counter", 0, "ticks");
    private readonly PluginText _status = new("status", "Status", "ok");
    private readonly PluginTable _table;
    private readonly PluginSensor _ephemeral = new("eph-value", "Ephemeral", 7, "V");
    private readonly List<PluginSensor> _flood = [];
    private readonly Dictionary<string, object?> _settings = new()
    {
        ["label"] = "fixture",
        ["count"] = 3,
        ["ratio"] = 0.5,
        ["enabled"] = true,
        ["mode"] = "a",
        ["apiKey"] = "",
    };

    private Thread? _hung;
    private Timer? _exit;

    public TestFixturePlugin() : base(FixtureMode.Current.Id ?? DefaultId, "Test Fixture", "A plugin for the engine's tests.")
    {
        var data = new DataTable();
        data.Columns.Add("Name", typeof(PluginText));
        data.Columns.Add("Load", typeof(PluginSensor));
        data.Rows.Add(new PluginText("alpha", "alpha"), new PluginSensor("load", 12.5f, "%"));
        data.Rows.Add(new PluginText("beta", "beta"), new PluginSensor("load", 40f, "%"));
        _table = new PluginTable("processes", "Processes", data, "0:150|1:80");
    }

    public override TimeSpan UpdateInterval => TimeSpan.Zero;

    public IReadOnlyList<PluginConfigProperty> ConfigProperties =>
    [
        new PluginConfigProperty { Key = "label", DisplayName = "Label", Type = PluginConfigType.String, Value = _settings["label"] },
        new PluginConfigProperty { Key = "count", DisplayName = "Count", Type = PluginConfigType.Integer, Value = _settings["count"], MinValue = 0, MaxValue = 100, Step = 1 },
        new PluginConfigProperty { Key = "ratio", DisplayName = "Ratio", Type = PluginConfigType.Double, Value = _settings["ratio"], MinValue = 0, MaxValue = 1, Step = 0.1 },
        new PluginConfigProperty { Key = "enabled", DisplayName = "Enabled", Type = PluginConfigType.Boolean, Value = _settings["enabled"] },
        new PluginConfigProperty { Key = "mode", DisplayName = "Mode", Type = PluginConfigType.Choice, Value = _settings["mode"], Options = ["a", "b"] },
        new PluginConfigProperty { Key = "apiKey", DisplayName = "API key", Type = PluginConfigType.String, Value = _settings["apiKey"] },
    ];

    public void ApplyConfig(string key, object? value)
    {
        if (!_settings.ContainsKey(key))
            throw new ArgumentException($"No setting '{key}'.");
        _settings[key] = value;
    }

    [PluginAction("Reset counter")]
    public void ResetCounter() => _counter.Value = 0;

    public override void Initialize()
    {
        var mode = FixtureMode.Current;
        if (mode.Hang)
        {
            _hung = new Thread(() => Thread.Sleep(Timeout.Infinite)) { IsBackground = false, Name = "fixture-hang" };
            _hung.Start();
        }

        if (mode.HangExitHandler)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Thread.Sleep(Timeout.Infinite);

        if (mode.ExitAfterMs is { } ms)
        {
            _exit = new Timer(_ =>
            {
                Console.Error.WriteLine("fixture: exiting on request");
                Console.Error.Flush();
                Environment.Exit(3);
            }, null, ms, Timeout.Infinite);
        }

        if (mode.TextChars is { } chars)
            _status.Value = new string('x', chars);
    }

    public override void Load(List<IPluginContainer> containers)
    {
        if (FixtureMode.Current.ThrowLoad)
            throw new InvalidOperationException("fixture: load failed on request");

        var readings = new PluginContainer("readings", "Readings");
        readings.Entries.Add(_counter);
        readings.Entries.Add(_status);
        readings.Entries.Add(_table);
        containers.Add(readings);

        var ephemeral = new PluginContainer("eph", "Ephemeral", isEmphemeralPath: true);
        ephemeral.Entries.Add(_ephemeral);
        containers.Add(ephemeral);

        if (FixtureMode.Current.Entries > 0)
        {
            var flood = new PluginContainer("flood", "Flood");
            for (var i = 0; i < FixtureMode.Current.Entries; i++)
            {
                var sensor = new PluginSensor($"s{i}", $"Sensor {i}", i, "");
                _flood.Add(sensor);
                flood.Entries.Add(sensor);
            }
            containers.Add(flood);
        }
    }

    public override void Update() => _counter.Value += 1;

    public override Task UpdateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Close()
    {
        if (FixtureMode.Current.Hang)
            Thread.Sleep(Timeout.Infinite);
        _exit?.Dispose();
    }
}
