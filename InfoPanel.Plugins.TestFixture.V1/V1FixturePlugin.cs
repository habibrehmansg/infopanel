using InfoPanel.Plugins;

namespace InfoPanel.V1Fixture;

/// <summary>
/// The engine tests' 1.x plugin: built for net8.0 against 1.x's SDK, as 1.x plugins are, so only 1.x's types and members.
/// Its own task updates a counter every 200 ms; a number it never knows (<c>unknown</c>, NaN from the start) and one it
/// knows while its Known setting is on (<c>maybe</c>, NaN while it is off); its Label setting, applied, becomes its Label
/// text; its Mark action counts in <c>marks</c>.
/// </summary>
public sealed class V1FixturePlugin : BasePlugin, IPluginConfigurable
{
    public const string PluginId = "v1-fixture";

    private readonly PluginSensor _counter = new("counter", "Counter", 0, "ticks");
    private readonly PluginSensor _unknown = new("unknown", "Unknown", float.NaN, "V");
    private readonly PluginSensor _maybe = new("maybe", "Maybe", 42, "V");
    private readonly PluginSensor _marks = new("marks", "Marks", 0);
    private readonly PluginText _label = new("label", "Label", "v1");
    private bool _known = true;

    public V1FixturePlugin() : base(PluginId, "V1 Fixture", "A plugin built as 1.x plugins are.")
    {
    }

    public override TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(200);

    public IReadOnlyList<PluginConfigProperty> ConfigProperties =>
    [
        new PluginConfigProperty { Key = "label", DisplayName = "Label", Type = PluginConfigType.String, Value = _label.Value },
        new PluginConfigProperty { Key = "known", DisplayName = "Known", Type = PluginConfigType.Boolean, Value = _known },
    ];

    public void ApplyConfig(string key, object? value)
    {
        switch (key)
        {
            case "label":
                _label.Value = value?.ToString() ?? "";
                break;
            case "known":
                _known = value is true;
                _maybe.Value = _known ? 42 : float.NaN;
                break;
            default:
                throw new ArgumentException($"No setting '{key}'.");
        }
    }

    [PluginAction("Mark")]
    public void Mark() => _marks.Value += 1;

    public override void Initialize()
    {
    }

    public override void Load(List<IPluginContainer> containers)
    {
        var readings = new PluginContainer("readings", "Readings");
        readings.Entries.Add(_counter);
        readings.Entries.Add(_unknown);
        readings.Entries.Add(_maybe);
        readings.Entries.Add(_marks);
        readings.Entries.Add(_label);
        containers.Add(readings);
    }

    public override void Update()
    {
    }

    public override Task UpdateAsync(CancellationToken cancellationToken)
    {
        _counter.Value += 1;
        return Task.CompletedTask;
    }

    public override void Close()
    {
    }
}
