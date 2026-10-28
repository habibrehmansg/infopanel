using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
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

    /// <summary>The key's samples, oldest first, at most 4,096. Reading registers interest, as <see cref="ISensorHistory"/>.</summary>
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
/// <see cref="SensorKey"/> strings. A sensor of another type than the input is <c>unavailable</c>, whatever its status;
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

    public SceneReading Read(string key, string inputType)
    {
        if (!SensorKey.TryParse(key, out var parsed)) return SceneReading.Unavailable;
        var state = _statuses?.ReadState(parsed)
            ?? (sensors.TryRead(parsed, out var plain)
                ? new SensorState(SensorStatus.Live, SensorState.TypeOf(plain), plain)
                : SensorState.Without(SensorStatus.Unavailable));
        // The type first: a sensor of another type is unavailable whatever its status (spec/docs/bindings.md).
        SensorKind? expected = inputType switch
        {
            "number" => SensorKind.Number,
            "text" => SensorKind.Text,
            "table" => SensorKind.Table,
            _ => null,
        };
        if (state.Type != expected) return SceneReading.Unavailable;
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
        return state.Type switch
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
            _ => new SceneReading { Status = status, Text = reading.ValueText ?? "", Table = FromDataTable(reading.ValueTable!) },
        };
    }

    public IReadOnlyList<double> History(string key) =>
        SensorKey.TryParse(key, out var parsed) ? history.GetSamples(parsed) : [];

    internal static SceneTable FromDataTable(DataTable table)
    {
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
        var rows = new List<IReadOnlyList<string>>(Math.Min(table.Rows.Count, 100));
        foreach (DataRow row in table.Rows)
        {
            if (rows.Count == 100) break;
            rows.Add(row.ItemArray.Select(v => v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v?.ToString() ?? "").ToArray());
        }
        return new SceneTable(columns, rows);
    }
}
