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

/// <summary>The engine's sensors (<see cref="ISensorReader"/>, <see cref="ISensorHistory"/>) as scene inputs. Keys are <see cref="SensorKey"/> strings.</summary>
public sealed class SensorSceneInputs(ISensorReader sensors, ISensorHistory history) : ISceneInputSource
{
    public SceneReading Read(string key, string inputType)
    {
        if (!SensorKey.TryParse(key, out var parsed) || !sensors.TryRead(parsed, out var reading)) return SceneReading.Unavailable;
        switch (inputType)
        {
            case "number":
                if (reading.ValueTable is not null || reading.ValueText is not null) return SceneReading.Unavailable;
                return new SceneReading
                {
                    Status = SceneInputStatus.Live,
                    Now = SceneMath.Finite(reading.ValueNow),
                    Min = SceneMath.Finite(reading.ValueMin),
                    Max = SceneMath.Finite(reading.ValueMax),
                    Avg = SceneMath.Finite(reading.ValueAvg),
                    Unit = reading.Unit ?? "",
                };
            case "text":
                // Only a text sensor feeds a text input; a numeric or table sensor is of another type (spec/docs/bindings.md).
                return reading.ValueText is { } text && reading.ValueTable is null
                    ? new SceneReading { Status = SceneInputStatus.Live, Text = text }
                    : SceneReading.Unavailable;
            case "table":
                return reading.ValueTable is { } table
                    ? new SceneReading { Status = SceneInputStatus.Live, Text = reading.ValueText ?? "", Table = FromDataTable(table) }
                    : SceneReading.Unavailable;
            default:
                return SceneReading.Unavailable;
        }
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
