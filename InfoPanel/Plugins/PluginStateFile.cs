using System.Text.Json;
using System.Text.RegularExpressions;
using InfoPanel.Documents;
using InfoPanel.Persistence;

namespace InfoPanel.Plugins;

/// <summary>
/// <c>plugin-state.json</c> in the data folder (docs/2.0/data-folder.md): <c>{"format": "infopanel.plugin-state/1",
/// "off": ["InfoPanel.X"]}</c>, only the plugin packages the user turned off, by folder name compared ignoring case (a
/// name with no package is kept). A host that ends, is given up on or is refused is never written here. Read once at
/// start through <see cref="EngineJson"/>, its members it doesn't know (the 1.4 upgrade's <c>v1Migration</c> among them)
/// kept as written and written back after its own; written through <see cref="AtomicFile"/>. A file it can't hold whole
/// (over <see cref="MaxBytes"/>, nested deeper than it reads, a link, not a regular file, not JSON, another major version,
/// or a member it knows of the wrong type) leaves every package on for the run and is never rewritten: <see cref="Set"/>
/// then throws <see cref="PluginStateUnavailableException"/>.
/// </summary>
public sealed partial class PluginStateFile
{
    public const string FileName = DataPaths.PluginStateFileName;

    public const string Format = "infopanel.plugin-state/1";

    /// <summary>The most the file may hold, its kept members included (1 MiB).</summary>
    public const int MaxBytes = 1024 * 1024;

    private readonly Lock _lock = new();
    private readonly string? _path;
    private readonly List<JsonMemberText> _kept;
    private List<string> _off;
    // Under _lock: the last write replaced the file but couldn't flush its folder, so the next change writes it again.
    private bool _unflushed;

    private PluginStateFile(string? path, List<string> off, List<JsonMemberText> kept, string? problem)
    {
        _path = path;
        _off = off;
        _kept = kept;
        Problem = problem;
    }

    /// <summary>Why the engine can't hold the file (it is then never rewritten, and every package is on), or null.</summary>
    public string? Problem { get; }

    /// <summary>
    /// How the file is written: <see cref="AtomicFile"/>, the third argument called once the file is replaced, before its
    /// folder is flushed (tests give a storage fault).
    /// </summary>
    internal Action<string, byte[], Action> Writer { get; set; } = (path, bytes, replaced) => AtomicFile.Write(path, bytes, null, replaced);

    [GeneratedRegex(@"\Ainfopanel\.plugin-state/([0-9]{1,4})\z", RegexOptions.CultureInvariant)]
    private static partial Regex FormatString();

    /// <summary>
    /// Reads the file at <paramref name="path"/> (none: the choices are kept for the run only). Never throws: a file it
    /// can't hold is logged, and the run does without it.
    /// </summary>
    public static PluginStateFile Read(string? path, ILogger logger)
    {
        if (path == null)
            return new PluginStateFile(null, [], [], null);

        byte[] bytes;
        try
        {
            var state = RegularFile.StateOf(path);
            if (state == PathState.Absent)
                return new PluginStateFile(path, [], [], null);
            if (new FileInfo(path).LinkTarget != null)
                return Unheld(path, "it is a link, which the engine doesn't read or write through", logger);
            if (state != PathState.Other)
                return Unheld(path, "it isn't a regular file", logger);
            bytes = RegularFile.ReadAtMost(path, MaxBytes);
        }
        catch (InvalidDataException)
        {
            return Unheld(path, $"it is larger than {MaxBytes} bytes", logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unheld(path, ex.Message, logger);
        }

        if (EngineJson.IsTooDeep(bytes))
            return Unheld(path, $"it nests deeper than {EngineJson.MaxDepth} levels", logger);

        List<JsonMemberText> members;
        try
        {
            members = EngineJson.Members(bytes);
        }
        catch (JsonException)
        {
            return Unheld(path, "it isn't a JSON object", logger);
        }

        string? format = null;
        List<string>? off = null;
        var kept = new List<JsonMemberText>();
        foreach (var member in members)
        {
            switch (member.Name)
            {
                case "format" when format == null:
                    format = ReadString(member.Value);
                    if (format == null)
                        return Unheld(path, "its format isn't a string", logger);
                    break;
                case "off" when off == null:
                    off = ReadNames(member.Value);
                    if (off == null)
                        return Unheld(path, "its off isn't a list of folder names", logger);
                    break;
                case "format" or "off":
                    return Unheld(path, $"it has {member.Name} twice", logger);
                default:
                    kept.Add(member);
                    break;
            }
        }

        if (format == null)
            return Unheld(path, "it has no format", logger);
        var match = FormatString().Match(format);
        if (!match.Success)
            return Unheld(path, "its format isn't infopanel.plugin-state", logger);
        if (match.Groups[1].Value != "1")
            return Unheld(path, $"it is another major version ({format})", logger);

        return new PluginStateFile(path, off ?? [], kept, null);
    }

    private static PluginStateFile Unheld(string path, string why, ILogger logger)
    {
        logger.LogError("The plugin state file {Path} can't be read ({Why}): every plugin package runs this run, and none can be turned on or off; the file is left as it is", path, why);
        return new PluginStateFile(path, [], [], why);
    }

    private static string? ReadString(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, EngineJson.DocumentOptions);
            return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static List<string>? ReadNames(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, EngineJson.DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            var names = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    return null;
                names.Add(element.GetString()!);
            }
            return names;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Whether the user turned the package in <paramref name="folder"/> off (folder names compared ignoring case).</summary>
    public bool IsOff(string folder)
    {
        lock (_lock)
            return _off.Any(n => string.Equals(n, folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Records the user's choice for <paramref name="folder"/> and writes the file (nothing when the choice is the one it
    /// has, unless the last write couldn't flush its folder: then it is written again). The choice counts only once
    /// written: a write that fails throws and changes nothing, except one that replaced the file and failed only to flush
    /// its folder, which throws with the choice in effect (the file holds it), as <c>SettingsStore</c> does.
    /// </summary>
    /// <exception cref="PluginStateUnavailableException">The engine can't hold the file (<see cref="Problem"/>), or the choice would take it over <see cref="MaxBytes"/>.</exception>
    /// <exception cref="IOException">The write failed; nothing changed. Or the file was replaced but its folder couldn't be flushed: the choice is in effect.</exception>
    /// <exception cref="UnauthorizedAccessException">The write was refused; nothing changed.</exception>
    public void Set(string folder, bool on)
    {
        lock (_lock)
        {
            if (Problem != null)
                throw new PluginStateUnavailableException($"The plugin state file can't be read ({Problem}), so no package is turned on or off this run.");

            var listed = _off.Any(n => string.Equals(n, folder, StringComparison.OrdinalIgnoreCase));
            if (listed != on)
            {
                // The last write replaced the file but couldn't flush its folder: write it again, so the retry flushes
                // what that write didn't, or throws as it did.
                if (_unflushed && _path != null)
                {
                    Writer(_path, Compose(_off), static () => { });
                    _unflushed = false;
                }
                return;
            }
            var next = on
                ? _off.Where(n => !string.Equals(n, folder, StringComparison.OrdinalIgnoreCase)).ToList()
                : [.. _off, folder];

            if (_path != null)
            {
                var bytes = Compose(next);
                if (bytes.Length > MaxBytes)
                    throw new PluginStateUnavailableException($"The plugin state file would be larger than {MaxBytes} bytes.");
                var replaced = false;
                try
                {
                    Writer(_path, bytes, () => replaced = true);
                }
                catch when (replaced)
                {
                    // The file was replaced, so it holds the new choice; only flushing its folder failed. It is what the
                    // next start reads, so the run follows it, as SettingsStore does.
                    _off = next;
                    _unflushed = true;
                    throw;
                }
            }

            _off = next;
            _unflushed = false;
        }
    }

    /// <summary>The file: the engine's own members, then the members it kept, each as it was read.</summary>
    private byte[] Compose(List<string> off)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteStartArray("off");
            foreach (var name in off)
                writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return EngineJson.Append(buffer.ToArray(), _kept);
    }
}

/// <summary>The plugin state file can't be changed this run: the engine couldn't hold it, or the change would take it over its cap.</summary>
public sealed class PluginStateUnavailableException(string message) : Exception(message);
