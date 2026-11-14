using System.Globalization;
using System.Text.Json;
using InfoPanel.Plugins;

namespace InfoPanel.AiUsage;

/// <summary>Claude Code's limits: each folder's, by its path (<see cref="ClaudeFolder.Path"/>), and the combined set's (the folder whose status line wrote last).</summary>
internal sealed record ClaudeReading(IReadOnlyDictionary<string, LimitsReading> Folders, LimitsReading Combined, string Profile);

/// <summary>
/// Reads Claude Code's limits from <c>infopanel-usage.json</c> in each Claude folder: what Claude Code gives its status
/// line, which the status line writes there once the user connects it (Connect Claude Code limits). Only its
/// <c>rate_limits</c> are taken (<c>five_hour</c> and <c>seven_day</c>, each <c>used_percentage</c> and
/// <c>resets_at</c>); its modification time is when they were seen. A file is read again only when its size or time
/// changed. A file that doesn't parse (caught mid-write) keeps the folder's last good reading; one that keeps failing
/// for <see cref="TornFor"/> while it changes reads "format changed". Not thread-safe: one caller at a time.
/// </summary>
internal sealed class LimitsReader(UsageEnvironment env)
{
    public const string FileName = "infopanel-usage.json";

    /// <summary>The largest usage file read (Claude Code's status line input is a few KiB).</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>How long a usage file may keep failing to parse while it changes before it reads "format changed".</summary>
    public static readonly TimeSpan TornFor = TimeSpan.FromMinutes(2);

    public const string NotConnected = "Not connected: Connect Claude Code limits in AI usage";

    private readonly Dictionary<string, FolderState> _folders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads <paramref name="folders"/>' usage files where they changed. The combined reading is the folder's whose file
    /// was written last; with none, every folder is "not connected".
    /// </summary>
    public ClaudeReading Read(IReadOnlyList<ClaudeFolder> folders)
    {
        var now = env.Now;
        var readings = new Dictionary<string, LimitsReading>(StringComparer.Ordinal);
        (ClaudeFolder Folder, DateTimeOffset Written)? newest = null;
        foreach (var folder in folders)
        {
            if (!_folders.TryGetValue(folder.Path, out var state))
                _folders[folder.Path] = state = new FolderState();
            var path = Path.Combine(folder.Path, FileName);
            var stamp = env.Files.Stat(folder.Path, path);
            if (stamp == null)
            {
                state.Reset();
                readings[folder.Path] = LimitsReading.None(NotConnected, "not connected");
                continue;
            }
            if (stamp != state.Stamp)
                stamp = Update(state, folder.Path, path, stamp.Value, now);
            readings[folder.Path] = state.Reading;
            if (newest == null || stamp.Value.LastWriteUtc > newest.Value.Written)
                newest = (folder, stamp.Value.LastWriteUtc);
        }

        if (folders.Count == 0)
            return new ClaudeReading(readings, LimitsReading.None("no Claude Code folder was found", "Claude Code not found"), "");
        if (newest is not { } last)
            return new ClaudeReading(readings, LimitsReading.None(NotConnected, "not connected"), "");
        return new ClaudeReading(readings, readings[last.Folder.Path], last.Folder.Name);
    }

    /// <summary>
    /// Reads the usage file at <paramref name="path"/> below <paramref name="root"/> again: its open handle's stamp (which
    /// the folder keeps and answers) and at most <see cref="MaxFileBytes"/> of it, whatever its size was when looked at.
    /// </summary>
    private FileStamp Update(FolderState state, string root, string path, FileStamp looked, DateTimeOffset now)
    {
        state.Stamp = looked;
        LimitsReading? read = null;
        string? problem = null;
        FileStamp stamp;
        try
        {
            using var file = env.Files.Open(root, path);
            state.Stamp = stamp = file.Stamp;
            if (stamp.Length > MaxFileBytes || file.ReadAtMost(MaxFileBytes) is not { } bytes)
                problem = "the file is larger than 1 MiB";
            else
            {
                using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
                read = Parse(doc.RootElement, stamp.LastWriteUtc, state.Reading);
            }
        }
        catch (JsonException)
        {
            // Caught mid-write, most likely: the last good reading stays, and the next change is read again.
            stamp = state.Stamp.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_logged.Add("read:" + ex.GetType().Name))
                PluginLog.Warning($"AI usage: a Claude Code usage file wasn't read ({(ex is RefusedFileException ? ex.Message : ex.GetType().Name)})");
            return looked;
        }

        if (read != null)
        {
            state.FailingSince = null;
            state.FailedStamp = null;
            state.Reading = read;
            return stamp;
        }
        if (problem != null)
        {
            state.Reading = LimitsReading.None(Changed(problem), "format changed");
            return stamp;
        }
        // Read again next time, changed or not; "format changed" only once it has kept failing for a while as it changed.
        state.Stamp = null;
        if (state.Reading.Status == NotConnected)
            state.Reading = LimitsReading.None("the usage file didn't parse; reading it again", "reading");
        if (state.FailingSince == null)
        {
            state.FailingSince = now;
            state.FailedStamp = stamp;
        }
        else if (now - state.FailingSince >= TornFor && stamp != state.FailedStamp)
            state.Reading = LimitsReading.None(Changed("it doesn't parse"), "format changed");
        return stamp;
    }

    /// <summary>The reading of a usage file's JSON, seen at <paramref name="asOf"/>; <paramref name="last"/> is the folder's last.</summary>
    private LimitsReading Parse(JsonElement root, DateTimeOffset asOf, LimitsReading last)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return LimitsReading.None(Changed("the file isn't a JSON object"), "format changed");
        if (!root.TryGetProperty("rate_limits", out var limits))
        {
            // A session's status line before its first answer has no limits yet: the last ones stay, with their age.
            return last.AsOf != null && last.Status != NotConnected && (last.FiveHour != null || last.Weekly != null)
                ? last
                : LimitsReading.None("no limits yet: Claude Code gives them after a session's first answer", "no limits yet");
        }
        if (limits.ValueKind == JsonValueKind.Null)
            return LimitsReading.None("no plan limits (an API key, Bedrock or Vertex)", "no plan limits") with { AsOf = asOf };
        if (limits.ValueKind != JsonValueKind.Object)
            return LimitsReading.None(Changed("rate_limits isn't an object"), "format changed");

        var (fiveHour, fiveHourProblem) = WindowOf(limits, "five_hour");
        var (weekly, weeklyProblem) = WindowOf(limits, "seven_day");
        if ((fiveHourProblem ?? weeklyProblem) is { } problem)
            return LimitsReading.None(Changed(problem), "format changed") with { AsOf = asOf };
        if (fiveHour == null && weekly == null)
            return LimitsReading.None("Claude Code's status line gave no limits", "no limits") with { AsOf = asOf };
        var status = fiveHour == null ? "Claude Code's status line gave no five-hour window"
            : weekly == null ? "Claude Code's status line gave no weekly window"
            : LimitsReading.Ok;
        return new LimitsReading(fiveHour, weekly, asOf, status, "–");
    }

    private static (Window? Window, string? Problem) WindowOf(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var window) || window.ValueKind == JsonValueKind.Null)
            return (null, null);
        var path = "rate_limits." + name;
        if (window.ValueKind != JsonValueKind.Object)
            return (null, $"{path} isn't an object");
        if (!window.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number || !double.IsFinite(used.GetDouble()))
            return (null, $"{path}.used_percentage missing");
        if (!window.TryGetProperty("resets_at", out var resets) || ResetsAt(resets) is not { } at)
            return (null, $"{path}.resets_at missing");
        return (new Window(Math.Clamp(used.GetDouble(), 0, 100), at), null);
    }

    /// <summary>A reset time as Unix seconds, or an ISO 8601 time with its offset.</summary>
    private static DateTimeOffset? ResetsAt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds) && seconds is >= 0 and <= 253402300799)
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
            return time;
        return null;
    }

    /// <summary>The status for a format change at <paramref name="what"/> (a field path and what's wrong with it); logged once a run.</summary>
    private string Changed(string what)
    {
        if (_logged.Add(what))
            PluginLog.Warning($"AI usage: Claude Code's usage file format changed ({what})");
        return $"Claude Code's usage file format changed ({what}); InfoPanel may need an update";
    }

    private sealed class FolderState
    {
        public FileStamp? Stamp;
        public LimitsReading Reading = LimitsReading.None(NotConnected, "not connected");
        public DateTimeOffset? FailingSince;
        public FileStamp? FailedStamp;

        public void Reset()
        {
            Stamp = null;
            Reading = LimitsReading.None(NotConnected, "not connected");
            FailingSince = null;
            FailedStamp = null;
        }
    }
}
