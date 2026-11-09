using System.Data;
using InfoPanel.Models;
using InfoPanel.Sensors;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// A plugin's sensors as the hub lists and reads them, each carrying a private sentinel where a plugin could put one:
/// its display names, a number's unit, a table's headings, a cell's suffix and its values, time-like ones included.
/// Every sentinel is in <see cref="Sentinels"/>, <see cref="Suffixes"/> or <see cref="Units"/>, so a test can look for
/// each in what a frame draws.
/// </summary>
internal sealed class SentinelPlugin : ISensorSource
{
    public const string SourceId = "acme.sentinel";

    /// <summary>The text sensors: the scene input each is bound to, its key, display name and value.</summary>
    public static IReadOnlyList<(string Input, SensorKey Key, string Name, string Value)> Texts { get; } =
    [
        ("name", Key("me/user"), "sentinel-name-k7q", "sentinel-value-k7q"),
        ("mail", Key("me/mail"), "kq7mail@example.com", "kq7mail"),
        ("sunrise", Key("sky/sunrise"), "Sunrise Time", "06:37"),
        ("code", Key("auth/code"), "One-time code", "7391"),
        ("lan", Key("net/local"), "Gateway", "10.231.77.19"),
        ("count", Key("me/count"), "Count", "42 kq7nick"),
    ];

    /// <summary>
    /// The number sensors: the scene input each is bound to, its key, display name, value and a private unit. The first
    /// unit is private text; the others are a person's initials that are also listed units' spellings (<see cref="Units"/>).
    /// </summary>
    public static IReadOnlyList<(string Input, SensorKey Key, string Name, double Value, string Unit)> Numbers { get; } =
    [
        ("score", Key("me/score"), "Score", 42, "kq7unit@example.com / 10.77.0.23"),
        ("field", Key("me/field"), "Field", 3, "T"),
        ("rain", Key("me/rain"), "Rain", 12, " mm"),
        ("height", Key("me/height"), "Height", 70, " in"),
    ];

    /// <summary>The table sensor: its key and display name.</summary>
    public static SensorKey TableKey { get; } = Key("me/table");

    public const string TableName = "sentinel-table-k7q";

    private static readonly string[] Columns = ["sentinel-heading-k7q", "Load"];

    /// <summary>The table's rows: the last three end in a private initial or word that is also a unit's spelling.</summary>
    private static readonly string[][] Rows =
        [["sentinel-cell-k7q", "42 qzxjv"], ["kq7row", "17.5%"], ["kq7row-a", "42 A"], ["kq7row-s", "7 s"], ["kq7row-x", "3 x"]];

    /// <summary>Every private value above: none of them may show in a simulated frame.</summary>
    public static IReadOnlyList<string> Sentinels { get; } =
    [
        "sentinel-name-k7q", "sentinel-value-k7q", "kq7mail", "06:37", "7391", "10.231.77.19", "kq7nick",
        "sentinel-table-k7q", "sentinel-heading-k7q", "sentinel-cell-k7q", "qzxjv", "kq7row", "kq7row-a", "kq7row-s",
        "kq7row-x", "kq7unit", "10.77.0.23",
    ];

    /// <summary>
    /// The private initials and words the table's cells carry after a number (<c>42 A</c>): too short to look for as
    /// text, so a simulated frame may draw none of them after a number.
    /// </summary>
    public static IReadOnlyList<string> Suffixes { get; } = ["A", "s", "x"];

    /// <summary>
    /// The private initials the number sensors carry as their unit (<see cref="Numbers"/>): listed units' spellings too
    /// short to look for as text, so a simulated frame may draw none of them as a whole text.
    /// </summary>
    public static IReadOnlyList<string> Units { get; } = ["T", "mm", "in"];

    /// <summary>The sentinels only a display name carries: a live frame draws values, not names, so it shows none of these.</summary>
    public static IReadOnlyList<string> Names { get; } = ["sentinel-name-k7q", "sentinel-table-k7q"];

    private static SensorKey Key(string id) => SensorKey.Plugin($"/{SourceId}/{id}");

    public string Id => SourceId;

    public bool Handles(SensorKey key) => key.Id.StartsWith($"/{SourceId}/", StringComparison.Ordinal);

    public IReadOnlyList<SensorInfo> GetSensors() =>
        [
            .. Texts.Select(t => new SensorInfo(t.Key, SourceId, "kq7group", t.Name, "")),
            .. Numbers.Select(n => new SensorInfo(n.Key, SourceId, "kq7group", n.Name, n.Unit)),
            new SensorInfo(TableKey, SourceId, "kq7group", TableName, ""),
        ];

    public bool TryRead(SensorKey key, out SensorReading reading)
    {
        foreach (var number in Numbers)
        {
            if (number.Key == key)
            {
                reading = new SensorReading(number.Value, number.Value, number.Value, number.Value, number.Unit);
                return true;
            }
        }
        if (key == TableKey)
        {
            var table = new DataTable();
            foreach (var column in Columns)
                table.Columns.Add(column, typeof(string));
            foreach (var row in Rows)
                table.Rows.Add(row);
            reading = new SensorReading(table, "", string.Join(", ", Rows[0]));
            return true;
        }
        foreach (var text in Texts)
        {
            if (text.Key == key)
            {
                reading = new SensorReading(text.Value);
                return true;
            }
        }
        reading = default;
        return false;
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
