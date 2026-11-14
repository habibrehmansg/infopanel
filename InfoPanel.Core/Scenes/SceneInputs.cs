using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using InfoPanel.Models;
using InfoPanel.Sensors;

namespace InfoPanel.Scenes;

/// <summary>An input's status (spec/docs/bindings.md); its name in expressions is the lower-case member name.</summary>
public enum SceneInputStatus { Unbound, Live, Stale, Unavailable, Denied }

/// <summary>A table sensor's reading: named columns and rows of text cells.</summary>
public sealed record SceneTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public static SceneTable Empty { get; } = new([], []);
}

/// <summary>
/// One sensor's reading for a tick. A reading without a value reads 0, "" and an empty table; <see cref="Status"/>
/// says why (anything but <see cref="SceneInputStatus.Live"/> and <see cref="SceneInputStatus.Stale"/> is unavailable).
/// </summary>
public sealed record SceneReading
{
    public required SceneInputStatus Status { get; init; }
    public double Now { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Avg { get; init; }
    public string Unit { get; init; } = "";
    public string Text { get; init; } = "";
    public SceneTable Table { get; init; } = SceneTable.Empty;

    public static SceneReading Unbound { get; } = new() { Status = SceneInputStatus.Unbound };
    public static SceneReading Unavailable { get; } = new() { Status = SceneInputStatus.Unavailable };

    public bool Available => Status is SceneInputStatus.Live or SceneInputStatus.Stale;
}

/// <summary>
/// Where the scene runtime reads sensors. Called once per key and tick (the tick snapshot), on the thread that ticks;
/// it must not block. <paramref name="inputType"/> is the input's declared type (<c>number</c>, <c>text</c>, <c>table</c>):
/// a sensor of another type reads as unavailable.
/// </summary>
public interface ISceneInputSource
{
    SceneReading Read(string key, string inputType);

    /// <summary>
    /// The key's samples, oldest first, at most 4,096. Reading registers interest, as <see cref="ISensorHistory"/>. The
    /// list is a snapshot the caller may keep: never change one after handing it over (a new sample makes a new list).
    /// </summary>
    IReadOnlyList<double> History(string key);
}

/// <summary>No sensors: every input is unavailable.</summary>
public sealed class NoSceneInputs : ISceneInputSource
{
    public static NoSceneInputs Instance { get; } = new();

    public SceneReading Read(string key, string inputType) => SceneReading.Unavailable;

    public IReadOnlyList<double> History(string key) => [];
}

/// <summary>
/// The engine's sensors (<see cref="ISensorReader"/>, <see cref="ISensorHistory"/>) as scene inputs. Keys are
/// <see cref="SensorKey"/> strings. A sensor of another type than the input is <c>unavailable</c>, whatever its status
/// (an image sensor is a text input's: it reads the picture's reference);
/// otherwise its status maps to the input's as spec/docs/bindings.md states: <c>live</c> and <c>simulated</c> read
/// <c>live</c>, <c>stale</c> <c>stale</c>, <c>denied</c> <c>denied</c>, <c>unavailable</c> and <c>missing</c>
/// <c>unavailable</c>. An <see cref="ISensorStatusReader"/> gives the status, type and reading from one snapshot
/// (<see cref="ISensorStatusReader.ReadState"/>); another reader is <c>live</c> with a reading and <c>unavailable</c>
/// without.
/// </summary>
public sealed class SensorSceneInputs(ISensorReader sensors, ISensorHistory history) : ISceneInputSource
{
    private static readonly SceneReading Denied = new() { Status = SceneInputStatus.Denied };

    private readonly ISensorStatusReader? _statuses = sensors as ISensorStatusReader;

    /// <summary>
    /// Each table reading as converted, held by the table itself: a source hands over a new table for a new reading and
    /// never changes one after (<see cref="SensorSample.SetTable"/>), so a live table converts once, and its conversion
    /// goes with it. Readers of the same table get the one conversion kept.
    /// </summary>
    private readonly ConditionalWeakTable<DataTable, SceneTable> _tables = new();

    private static readonly ConditionalWeakTable<DataTable, SceneTable>.CreateValueCallback Conversion =
        static table => FromDataTable(table);

    /// <summary>
    /// Each key text as parsed, held by the text itself: a binding's key is parsed on its first read, not on every read and
    /// history read, and the entry goes when the binding's text does.
    /// </summary>
    private readonly ConditionalWeakTable<string, ParsedKey> _parsed = new();

    private sealed record ParsedKey(bool Valid, SensorKey Key);

    private static readonly ConditionalWeakTable<string, ParsedKey>.CreateValueCallback Parse =
        static text => SensorKey.TryParse(text, out var key) ? new ParsedKey(true, key) : new ParsedKey(false, default);

    private bool TryParse(string key, out SensorKey parsed)
    {
        var entry = _parsed.GetValue(key, Parse);
        parsed = entry.Key;
        return entry.Valid;
    }

    /// <summary>The sensors' list (their display names) when the reader is the hub; null otherwise.</summary>
    internal Func<IReadOnlyList<SensorInfo>>? Catalogue => sensors is SensorHub hub ? hub.GetSensors : null;

    public SceneReading Read(string key, string inputType)
    {
        if (!TryParse(key, out var parsed)) return SceneReading.Unavailable;
        var state = _statuses?.ReadState(parsed)
            ?? (sensors.TryRead(parsed, out var plain)
                ? new SensorState(SensorStatus.Live, SensorState.TypeOf(plain), plain)
                : SensorState.Without(SensorStatus.Unavailable));
        // The type first: a sensor of another type is unavailable whatever its status (spec/docs/bindings.md). An image
        // sensor reads as a text one: a text input reads its reference.
        SensorKind? expected = inputType switch
        {
            "number" => SensorKind.Number,
            "text" => SensorKind.Text,
            "table" => SensorKind.Table,
            _ => null,
        };
        var type = state.Type == SensorKind.Image ? SensorKind.Text : state.Type;
        if (type != expected) return SceneReading.Unavailable;
        var status = state.Status switch
        {
            SensorStatus.Live or SensorStatus.Simulated => SceneInputStatus.Live,
            SensorStatus.Stale => SceneInputStatus.Stale,
            SensorStatus.PermissionDenied => SceneInputStatus.Denied,
            _ => SceneInputStatus.Unavailable,
        };
        if (status is SceneInputStatus.Denied) return Denied;
        if (status is not (SceneInputStatus.Live or SceneInputStatus.Stale) || !state.HasReading) return SceneReading.Unavailable;
        var reading = state.Reading;
        return type switch
        {
            SensorKind.Number => new SceneReading
            {
                Status = status,
                Now = SceneMath.Finite(reading.ValueNow),
                Min = SceneMath.Finite(reading.ValueMin),
                Max = SceneMath.Finite(reading.ValueMax),
                Avg = SceneMath.Finite(reading.ValueAvg),
                Unit = reading.Unit ?? "",
            },
            SensorKind.Text => new SceneReading { Status = status, Text = reading.ValueText ?? "" },
            _ => new SceneReading { Status = status, Text = reading.ValueText ?? "", Table = _tables.GetValue(reading.ValueTable!, Conversion) },
        };
    }

    public IReadOnlyList<double> History(string key) =>
        TryParse(key, out var parsed) ? history.GetSamples(parsed) : [];

    /// <summary>
    /// <paramref name="table"/> as a scene reads it: its column names and the text of its first <paramref name="maxRows"/>
    /// rows' cells (only those rows are formatted).
    /// </summary>
    internal static SceneTable FromDataTable(DataTable table, int maxRows = 100)
    {
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
        var rows = new List<IReadOnlyList<string>>(Math.Min(table.Rows.Count, maxRows));
        foreach (DataRow row in table.Rows)
        {
            if (rows.Count == maxRows) break;
            rows.Add(row.ItemArray.Select(v => v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v?.ToString() ?? "").ToArray());
        }
        return new SceneTable(columns, rows);
    }
}
