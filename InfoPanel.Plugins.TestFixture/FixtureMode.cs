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
/// <item><c>text-chars=N</c>: the status text is N characters long.</item>
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
    public int? TextChars { get; private init; }

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
            TextChars = Number("text-chars"),
        };
    }
}
