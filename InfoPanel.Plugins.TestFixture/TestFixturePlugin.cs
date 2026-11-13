using System.Data;
using InfoPanel.Plugins;

namespace InfoPanel.TestFixture;

/// <summary>
/// The engine tests' plugin: a sensor, a text and a table in a container, a sensor in an ephemeral container, two actions
/// (Reset counter; Report an error, which throws), and a setting of each type, its Label setting, when applied, becoming
/// its Status text (a setting changing a reading live); it logs two lines as it starts (<see cref="StartedLine"/> and, at
/// Debug, <see cref="DebugLine"/>); <see cref="FixtureMode"/> makes it misbehave on request, or adds a picture (<c>image</c>).
/// </summary>
public sealed class TestFixturePlugin : BasePlugin, IPluginConfigurable
{
    public const string DefaultId = "test-fixture";

    private readonly PluginSensor _counter = new("counter", "Counter", 0, "ticks");
    private readonly PluginText _status = new("status", "Status", "ok");
    private readonly PluginTable _table;
    private readonly PluginSensor _ephemeral = new("eph-value", "Ephemeral", 7, "V");
    private readonly PluginImage _cover = new("cover", "Cover");

    /// <summary>The picture of the <c>image</c> mode's Cover entry: a 1 × 1 PNG.</summary>
    public static readonly byte[] Pixel = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
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
    private Thread? _spew;
    private Timer? _exit;
    private volatile bool _updated;
    private object? _applied;

    public TestFixturePlugin() : base(FixtureMode.Current.Id ?? DefaultId, "Test Fixture", "A plugin for the engine's tests.")
    {
        if (LargeTable() is { } large)
        {
            _table = new PluginTable("processes", "Processes", large, "");
            return;
        }

        var data = new DataTable();
        data.Columns.Add("Name", typeof(PluginText));
        data.Columns.Add("Load", typeof(PluginSensor));
        data.Rows.Add(new PluginText("alpha", "alpha"), new PluginSensor("load", 12.5f, "%"));
        data.Rows.Add(new PluginText("beta", "beta"), new PluginSensor("load", 40f, "%"));
        _table = new PluginTable("processes", "Processes", data, "0:150|1:80");
    }

    /// <summary>The Processes table <c>table-rows</c>, <c>table-columns</c> and <c>table-cell-chars</c> ask for; null when none is given.</summary>
    private static DataTable? LargeTable()
    {
        var mode = FixtureMode.Current;
        if (mode.TableRows is null && mode.TableColumns is null && mode.TableCellChars is null)
            return null;

        var (rows, columns, chars) = (mode.TableRows ?? 2, mode.TableColumns ?? 2, mode.TableCellChars ?? 16);
        var data = new DataTable();
        for (var c = 0; c < columns; c++)
            data.Columns.Add($"c{c}", typeof(PluginText));
        for (var r = 0; r < rows; r++)
        {
            var cells = new object[columns];
            for (var c = 0; c < columns; c++)
            {
                var text = $"r{r}c{c}";
                cells[c] = new PluginText("", text.PadRight(chars, 'x')[..chars]);
            }
            data.Rows.Add(cells);
        }
        return data;
    }

    public override TimeSpan UpdateInterval => TimeSpan.Zero;

    public IReadOnlyList<PluginConfigProperty> ConfigProperties => HangOnRequest() ??
    [
        new PluginConfigProperty { Key = "label", DisplayName = "Label", Type = PluginConfigType.String, Value = _settings["label"] },
        new PluginConfigProperty { Key = "count", DisplayName = "Count", Type = PluginConfigType.Integer, Value = _settings["count"], MinValue = 0, MaxValue = 100, Step = 1 },
        new PluginConfigProperty { Key = "ratio", DisplayName = "Ratio", Type = PluginConfigType.Double, Value = _settings["ratio"], MinValue = 0, MaxValue = 1, Step = 0.1 },
        new PluginConfigProperty { Key = "enabled", DisplayName = "Enabled", Type = PluginConfigType.Boolean, Value = _settings["enabled"] },
        new PluginConfigProperty { Key = "mode", DisplayName = "Mode", Type = PluginConfigType.Choice, Value = _settings["mode"], Options = ["a", "b"] },
        new PluginConfigProperty { Key = "apiKey", DisplayName = "API key", Type = PluginConfigType.String, Value = _settings["apiKey"] },
    ];

    /// <summary>
    /// Under <c>settings-hang</c>, once the plugin has updated, never returns; under <c>throw-list-settings</c> throws;
    /// under <c>throw-settings</c>, once a setting has been applied, throws with its value; else null.
    /// </summary>
    private IReadOnlyList<PluginConfigProperty>? HangOnRequest()
    {
        if (FixtureMode.Current.SettingsHang && _updated)
            Thread.Sleep(Timeout.Infinite);
        if (FixtureMode.Current.ThrowListSettings)
            throw new InvalidOperationException(UnlistedSettings);
        if (FixtureMode.Current.ThrowSettings && _applied is { } applied)
            throw new InvalidOperationException($"fixture: can't list settings after {applied}");
        return null;
    }

    public void ApplyConfig(string key, object? value)
    {
        if (!_settings.ContainsKey(key))
            throw new ArgumentException($"No setting '{key}'.");
        if (FixtureMode.Current.ThrowApply)
            throw new InvalidOperationException($"fixture: can't take {value}");
        _settings[key] = value;
        _applied = value;
        if (key == "label" && FixtureMode.Current.TextChars is null)
            _status.Value = value?.ToString() ?? "";
    }

    [PluginAction("Reset counter")]
    public void ResetCounter() => _counter.Value = 0;

    public const string ReportedError = "fixture: an error on request";

    /// <summary>The failures of <c>throw-list-settings</c>, <c>throw-read</c> and <c>throw-close</c>.</summary>
    public const string UnlistedSettings = "fixture: can't list settings";
    public const string UnreadValue = "fixture: can't read the value";
    public const string UnclosedPlugin = "fixture: can't close";

    [PluginAction("Report an error")]
    public void ReportError() => throw new InvalidOperationException(ReportedError);

    /// <summary>The line the plugin logs at Information as it starts (<see cref="PluginLog"/>).</summary>
    public const string StartedLine = "fixture: started";

    /// <summary>The line the plugin logs at Debug as it starts.</summary>
    public const string DebugLine = "fixture: a detail at debug";

    public override void Initialize()
    {
        PluginLog.Info(StartedLine);
        PluginLog.Debug(DebugLine);
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

        if (mode.SpewBytes is { } bytes)
        {
            _spew = new Thread(() => Spew(bytes)) { IsBackground = true, Name = "fixture-spew" };
            _spew.Start();
        }
    }

    /// <summary>Writes <paramref name="bytes"/> to stdout and to stderr with no newline, then <c>spew-end</c> to stderr.</summary>
    private static void Spew(int bytes)
    {
        // The process's own streams, not Console's writers: no newline, no buffering of ours.
        var stdout = Console.OpenStandardOutput();
        var stderr = Console.OpenStandardError();
        var chunk = new byte[64 * 1024];
        foreach (var (stream, fill) in new[] { (stdout, (byte)'o'), (stderr, (byte)'e') })
        {
            Array.Fill(chunk, fill);
            for (var left = bytes; left > 0; left -= chunk.Length)
                stream.Write(chunk, 0, Math.Min(chunk.Length, left));
            stream.Flush();
        }
        stderr.Write("spew-end"u8);
        stderr.Flush();
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

        if (FixtureMode.Current.ThrowRead)
        {
            var broken = new PluginContainer("broken", "Broken");
            broken.Entries.Add(new UnreadableSensor());
            containers.Add(broken);
        }

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

        if (FixtureMode.Current.Texts > 0)
        {
            var texts = new PluginContainer("texts", "Texts");
            for (var i = 0; i < FixtureMode.Current.Texts; i++)
                texts.Entries.Add(new PluginText($"t{i}", $"Text {i}", new string('x', 4096)));
            containers.Add(texts);
        }

        if (FixtureMode.Current.Image)
        {
            var pictures = new PluginContainer("pictures", "Pictures");
            _cover.SetImage(Pixel);
            pictures.Entries.Add(_cover);
            containers.Add(pictures);
        }

        if (FixtureMode.Current.EmptyTables > 0)
        {
            var tables = new PluginContainer("empty-tables", "Empty tables");
            for (var i = 0; i < FixtureMode.Current.EmptyTables; i++)
            {
                var data = new DataTable();
                for (var c = 0; c < 64; c++)
                    data.Columns.Add($"c{c}".PadRight(256, 'x'), typeof(PluginText));
                tables.Entries.Add(new PluginTable($"e{i}", $"Table {i}", data, ""));
            }
            containers.Add(tables);
        }
    }

    public override void Update()
    {
        _counter.Value += 1;
        _updated = true;
    }

    public override Task UpdateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Close()
    {
        if (FixtureMode.Current.Hang)
            Thread.Sleep(Timeout.Infinite);
        _exit?.Dispose();
        if (FixtureMode.Current.ThrowClose)
            throw new InvalidOperationException(UnclosedPlugin);
    }

    /// <summary>The <c>throw-read</c> sensor: its value can't be read.</summary>
    private sealed class UnreadableSensor : IPluginSensor
    {
        public string Id => "broken";
        public string Name => "Broken";
        public string? Unit => null;
        public float Value { get => throw new InvalidOperationException(UnreadValue); set { } }
        public override string ToString() => Name;
    }
}
