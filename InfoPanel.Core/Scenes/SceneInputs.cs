using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Sensors;

namespace InfoPanel.Scenes;

/// <summary>An input's status (spec/docs/bindings.md); its name in expressions is the lower-case member name.</summary>
public enum SceneInputStatus { Unbound, Live, Stale, Unavailable, Denied }

/// <summary>
/// A table input's reading for a tick (spec/docs/bindings.md, "Tables"): its column names and its first rows, up to
/// <c>tableRows</c>, each cell with its text and its number (<see cref="Numbers"/>: the source's own number for a cell
/// it gives as a number, 0 for any other). <c>table</c>, <c>cell</c> and the <c>rows</c> field all read this one reading.
/// </summary>
public sealed record SceneTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public static SceneTable Empty { get; } = new([], []);

    /// <summary>Each cell's number, row by row beside <see cref="Rows"/>; null when no cell has one (every number 0).</summary>
    public IReadOnlyList<IReadOnlyList<double>>? Numbers { get; init; }

    /// <summary>
    /// A table whose <paramref name="count"/> rows are made when first read, each once (<paramref name="make"/> may run
    /// twice for one row when two threads read it at once; one result is kept), so a reading costs the rows a tick reads,
    /// not all of them. <paramref name="make"/> must give the same row whenever it is called.
    /// </summary>
    public static SceneTable Lazy(IReadOnlyList<string> columns, int count, Func<int, SceneRow> make)
    {
        var rows = new LazyRows(count, make);
        return new SceneTable(columns, rows) { Numbers = rows.Numbers };
    }

    /// <summary>
    /// This reading's first <paramref name="rows"/> rows (a runtime's <c>tableRows</c>): this table itself when it has no
    /// more, else a view of it that reads no row beyond them; the table and its kept rows are shared, not copied.
    /// </summary>
    public SceneTable First(int rows)
    {
        rows = Math.Max(0, rows);
        if (Rows.Count <= rows) return this;
        return new SceneTable(Columns, new FirstRows<IReadOnlyList<string>>(Rows, rows))
        {
            Numbers = Numbers is { } numbers ? new FirstRows<IReadOnlyList<double>>(numbers, Math.Min(rows, numbers.Count)) : null,
        };
    }

    /// <summary>The first column named <paramref name="name"/> exactly; -1 when there is none.</summary>
    public int ColumnOf(string name)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i], name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>A cell's text; "" for a row or column the reading lacks.</summary>
    public string Text(int row, int column)
    {
        if (row < 0 || row >= Rows.Count || column < 0 || column >= Columns.Count) return "";
        var cells = Rows[row];
        return column < cells.Count ? cells[column] : "";
    }

    /// <summary>A cell's number (a number that isn't finite is 0); 0 for a row or column the reading lacks.</summary>
    public double Number(int row, int column)
    {
        if (Numbers is not { } numbers || row < 0 || row >= Rows.Count || row >= numbers.Count || column < 0 || column >= Columns.Count) return 0;
        var cells = numbers[row];
        return column < cells.Count ? SceneMath.Finite(cells[column]) : 0;
    }

    /// <summary>The first <paramref name="count"/> of <paramref name="all"/> (<see cref="First"/>).</summary>
    private sealed class FirstRows<T>(IReadOnlyList<T> all, int count) : IReadOnlyList<T>
    {
        public int Count => count;

        public T this[int index] => (uint)index < (uint)count ? all[index] : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < count; i++) yield return all[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>The rows of <see cref="Lazy"/>: each made on its first read and kept, safe to read from any thread.</summary>
    private sealed class LazyRows(int count, Func<int, SceneRow> make) : IReadOnlyList<IReadOnlyList<string>>
    {
        private readonly SceneRow?[] _rows = new SceneRow?[count];

        public int Count => count;

        public IReadOnlyList<string> this[int index] => Row(index).Texts;

        public IReadOnlyList<IReadOnlyList<double>> Numbers => new NumberRows(this);

        private SceneRow Row(int index)
        {
            var row = Volatile.Read(ref _rows[index]);
            if (row is not null) return row;
            var made = make(index);
            return Interlocked.CompareExchange(ref _rows[index], made, null) ?? made;
        }

        public IEnumerator<IReadOnlyList<string>> GetEnumerator()
        {
            for (var i = 0; i < count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class NumberRows(LazyRows rows) : IReadOnlyList<IReadOnlyList<double>>
        {
            public int Count => rows.Count;

            public IReadOnlyList<double> this[int index] => rows.Row(index).Numbers;

            public IEnumerator<IReadOnlyList<double>> GetEnumerator()
            {
                for (var i = 0; i < rows.Count; i++) yield return this[i];
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}

/// <summary>One row of a <see cref="SceneTable"/>: each cell's text and number (an empty list: every number 0).</summary>
public sealed record SceneRow(IReadOnlyList<string> Texts, IReadOnlyList<double> Numbers);

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
    /// <paramref name="table"/> as a scene reads it (spec/docs/bindings.md, "Tables"): its column names and its rows, or its
    /// first <paramref name="maxRows"/>. The conversion is shared by every runtime reading the table, so it holds all the
    /// source's rows (a reference a row, the source having handed over that many) and each runtime reads its own first
    /// <c>tableRows</c> of them (<see cref="InputSnap"/>, <see cref="SceneTable.First"/>), so only rows below the largest
    /// limit of the runtimes reading it are ever made. A row is read from the source's table when first asked for,
    /// then kept: its cells' text, and the number of each cell the source gives as a number (a plugin's sensor cell,
    /// <see cref="IPluginSensor"/>: its value, its unit left out, 0 when it isn't finite; any other cell 0, never read
    /// from its text). The source never changes a table it has handed over (<see cref="SensorSample.SetTable"/>), so a
    /// row read late is the row it handed over.
    /// </summary>
    internal static SceneTable FromDataTable(DataTable table, int? maxRows = null)
    {
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
        var count = maxRows is { } max ? Math.Min(table.Rows.Count, Math.Max(0, max)) : table.Rows.Count;
        return SceneTable.Lazy(columns, count, r => RowOf(table.Rows[r], columns.Length));
    }

    private static SceneRow RowOf(DataRow row, int columns)
    {
        var texts = new string[columns];
        double[]? numbers = null;
        for (var c = 0; c < columns; c++)
        {
            var v = row[c];
            texts[c] = v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v?.ToString() ?? "";
            if (v is IPluginSensor sensor)
                (numbers ??= new double[columns])[c] = SceneMath.Finite(sensor.Value);
        }
        return new SceneRow(texts, numbers ?? []);
    }
}
