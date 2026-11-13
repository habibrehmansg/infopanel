using System.Globalization;

namespace InfoPanel.TestFixture;

/// <summary>
/// What a test asked of the fixture: <c>fixture-mode.txt</c> next to the DLL, one setting a line (<c>name</c> or
/// <c>name=value</c>), read once per process. With no file the fixture behaves.
/// <list type="bullet">
/// <item><c>id=…</c>: the main plugin's id.</item>
/// <item><c>throw-load</c>: the main plugin's Load throws.</item>
/// <item><c>hang</c>: a foreground thread that never ends, and a Close that never returns.</item>
/// <item><c>hang-exit-handler</c>: a process-exit handler that never returns.</item>
/// <item><c>exit-after-ms=N</c>: the host process exits (code 3) N ms after the plugin starts, saying so on stderr.</item>
/// <item><c>entries=N</c>: N more sensors, in a container of their own.</item>
/// <item><c>texts=N</c>: N more texts of 4,096 characters each, in a container of their own (<c>texts</c>).</item>
/// <item><c>empty-tables=N</c>: N more tables without rows, each of 64 columns with names of 256 characters, in a
/// container of their own (<c>empty-tables</c>).</item>
/// <item><c>text-chars=N</c>: the status text is N characters long (applying the Label setting doesn't change it).</item>
/// <item><c>settings-hang</c>: once the plugin has updated, asking its settings never returns (its start's own reads still do).</item>
/// <item><c>throw-apply</c>: applying a setting throws, the value given in the exception's message.</item>
/// <item><c>throw-settings</c>: once a setting has been applied, reading the settings throws, the value applied in the
/// exception's message.</item>
/// <item><c>throw-list-settings</c>: reading the settings always throws.</item>
/// <item><c>throw-read</c>: one more sensor (<c>broken</c>, in a container of its own, <c>broken</c>) whose value can't
/// be read: reading it throws.</item>
/// <item><c>throw-close</c>: the main plugin's Close throws.</item>
/// <item><c>table-rows=N</c>, <c>table-columns=N</c>, <c>table-cell-chars=N</c>: the Processes table is N rows by N
/// columns (2 each by default) of text cells N characters long (16 by default), when any of the three is given.</item>
/// <item><c>spew-bytes=N</c>: once started, N bytes to stdout and N to stderr with no newline, then <c>spew-end</c> on
/// stderr, and the plugin runs on.</item>
/// <item><c>image</c>: a picture, a 1 × 1 PNG, in an image entry (<c>cover</c>) in a container of its own (<c>pictures</c>).</item>
/// </list>
/// </summary>
public sealed class FixtureMode
{
    public const string FileName = "fixture-mode.txt";

    private static readonly Lazy<FixtureMode> Lazy = new(Load);

    public static FixtureMode Current => Lazy.Value;

    public string? Id { get; private init; }
    public bool ThrowLoad { get; private init; }
    public bool Hang { get; private init; }
    public bool HangExitHandler { get; private init; }
    public int? ExitAfterMs { get; private init; }
    public int Entries { get; private init; }
    public int Texts { get; private init; }
    public int EmptyTables { get; private init; }
    public int? TextChars { get; private init; }
    public int? SpewBytes { get; private init; }
    public bool SettingsHang { get; private init; }
    public bool ThrowApply { get; private init; }
    public bool ThrowSettings { get; private init; }
    public bool ThrowListSettings { get; private init; }
    public bool ThrowRead { get; private init; }
    public bool ThrowClose { get; private init; }
    public int? TableRows { get; private init; }
    public int? TableColumns { get; private init; }
    public int? TableCellChars { get; private init; }
    public bool Image { get; private init; }

    private static FixtureMode Load()
    {
        var folder = Path.GetDirectoryName(typeof(FixtureMode).Assembly.Location) ?? "";
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
            return new FixtureMode();

        var settings = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

        int? Number(string name) =>
            settings.TryGetValue(name, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : null;

        return new FixtureMode
        {
            Id = settings.GetValueOrDefault("id"),
            ThrowLoad = settings.ContainsKey("throw-load"),
            Hang = settings.ContainsKey("hang"),
            HangExitHandler = settings.ContainsKey("hang-exit-handler"),
            ExitAfterMs = Number("exit-after-ms"),
            Entries = Number("entries") ?? 0,
            Texts = Number("texts") ?? 0,
            EmptyTables = Number("empty-tables") ?? 0,
            TextChars = Number("text-chars"),
            SpewBytes = Number("spew-bytes"),
            SettingsHang = settings.ContainsKey("settings-hang"),
            ThrowApply = settings.ContainsKey("throw-apply"),
            ThrowSettings = settings.ContainsKey("throw-settings"),
            ThrowListSettings = settings.ContainsKey("throw-list-settings"),
            ThrowRead = settings.ContainsKey("throw-read"),
            ThrowClose = settings.ContainsKey("throw-close"),
            TableRows = Number("table-rows"),
            TableColumns = Number("table-columns"),
            TableCellChars = Number("table-cell-chars"),
            Image = settings.ContainsKey("image"),
        };
    }
}
