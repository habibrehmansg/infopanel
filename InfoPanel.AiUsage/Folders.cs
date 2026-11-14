using System.Security.Cryptography;
using System.Text;
using InfoPanel.Plugins;

namespace InfoPanel.AiUsage;

/// <summary>Where the plugin looks and what time it is: the user's home, the environment, the clock and the files (<see cref="IUsageFiles"/>).</summary>
internal sealed record UsageEnvironment(string Home, Func<string, string?> Variable, TimeProvider Time, IUsageFiles Files)
{
    /// <summary>This computer: the user's home (<c>HOME</c>, or <c>USERPROFILE</c> on Windows), its environment, its clock.</summary>
    public static UsageEnvironment Default() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable, TimeProvider.System, new DiskFiles());

    public DateTimeOffset Now => Time.GetUtcNow();

    public TimeZoneInfo Zone => Time.LocalTimeZone;

    /// <summary>A path as the user may write it in a setting: <c>~</c> or <c>~/…</c> is under the home; else as it is.</summary>
    public string Expand(string path)
    {
        path = path.Trim();
        if (path == "~")
            return Home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Home, path[2..]);
        return path;
    }

    /// <summary>A path for a reading: under the home as <c>~/…</c>, so no user name shows; else the folder's own name.</summary>
    public string Show(string path)
    {
        var relative = Path.GetRelativePath(Home, path);
        if (relative == ".")
            return "~";
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            return "~/" + relative.Replace('\\', '/');
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
    }
}

/// <summary>
/// A Claude Code folder (<c>~/.claude</c>, <c>~/.claude-work</c>…): its path (full, with no separator at its end: the
/// key its readings go by), the name a reading shows, its container's id.
/// </summary>
internal sealed record ClaudeFolder(string Path, string Name, string ContainerId);

/// <summary>Where Claude Code and Codex keep their files.</summary>
internal static class Folders
{
    /// <summary>The most Claude folders given readings of their own.</summary>
    public const int MaxClaudeFolders = 16;

    /// <summary>The longest container id given (the engine refuses a catalogue with one over 256 characters).</summary>
    public const int MaxContainerIdChars = 128;

    /// <summary>Room kept at an id's end for the number that sets apart two folders of the same name (<c>-2</c> … <c>-32</c>).</summary>
    private const int CollisionRoom = 4;

    /// <summary>
    /// The Claude folders this computer has, found when the plugin loads, each with readings of its own: the
    /// <c>CLAUDE_CONFIG_DIR</c> folder when it is set and there; every <c>~/.claude</c> and <c>~/.claude-*</c> holding
    /// <c>projects/</c>; and <c>~/.config/claude</c> holding <c>projects/</c>. Folders only: no file is opened, and nothing
    /// below a folder but whether it has <c>projects/</c> is looked at. At most <see cref="MaxClaudeFolders"/>, by name.
    /// The home folder is listed within <see cref="MaxHomeEntries"/> entries and the whole search within
    /// <see cref="MaxFindTime"/>; past either, the folders found so far are given and a warning is logged.
    /// </summary>
    public static IReadOnlyList<ClaudeFolder> FindClaude(UsageEnvironment env) => FindClaude(env, MaxHomeEntries, out _);

    /// <summary>The most entries of the home folder looked at to find its Claude folders (files and folders alike).</summary>
    public const int MaxHomeEntries = 10_000;

    /// <summary>The most time spent finding the Claude folders, by the plugin's clock.</summary>
    public static readonly TimeSpan MaxFindTime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// <see cref="FindClaude(UsageEnvironment)"/> with the home folder listed within <paramref name="maxHomeEntries"/>;
    /// <paramref name="truncated"/> when the home folder wasn't listed to its end or the time ran out, so a Claude folder
    /// may be left out.
    /// </summary>
    internal static IReadOnlyList<ClaudeFolder> FindClaude(UsageEnvironment env, int maxHomeEntries, out bool truncated)
    {
        var started = env.Time.GetTimestamp();
        var outOfTime = false;
        bool OutOfTime() => outOfTime |= env.Time.GetElapsedTime(started) > MaxFindTime;

        truncated = false;
        var paths = new List<string>();
        if (env.Variable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured && env.Files.DirectoryExists(env.Expand(configured)))
            paths.Add(env.Expand(configured));
        if (env.Home.Length > 0)
        {
            var listing = env.Files.Directories(env.Home, ".claude*", maxHomeEntries, OutOfTime);
            truncated = listing.Truncated;
            // By name, and projects/ looked for only until the folders given readings are found: past them, none would be.
            var claude = 0;
            foreach (var folder in listing.Paths.OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                if (claude == MaxClaudeFolders)
                    break;
                if (OutOfTime())
                {
                    truncated = true;
                    break;
                }
                var name = Path.GetFileName(folder);
                if ((name == ".claude" || name.StartsWith(".claude-", StringComparison.Ordinal)) && env.Files.DirectoryExists(Path.Combine(folder, "projects")))
                {
                    paths.Add(folder);
                    claude++;
                }
            }
            var config = Path.Combine(env.Home, ".config", "claude");
            if (env.Files.DirectoryExists(Path.Combine(config, "projects")))
                paths.Add(config);
        }
        if (truncated)
            PluginLog.Warning(outOfTime
                ? $"AI usage: finding the Claude folders took longer than {MaxFindTime.TotalSeconds:0} s; some may be left out (name them in Other Claude folders)"
                : $"AI usage: the home folder holds more than {maxHomeEntries} entries; Claude folders past them may be left out (name them in Other Claude folders)");
        return Named(env, paths, []);
    }

    /// <summary>
    /// The folders the "Other Claude folders" setting names (separated by <c>;</c>), leaving out those in
    /// <paramref name="known"/>; as written, whether or not they exist. Their ids are unique among <paramref name="known"/>'s too.
    /// </summary>
    public static IReadOnlyList<ClaudeFolder> OtherClaude(UsageEnvironment env, string setting, IReadOnlyList<ClaudeFolder> known)
    {
        var paths = setting.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(env.Expand)
            .Where(p => Path.IsPathRooted(p) && !known.Any(k => SamePath(k.Path, p)))
            .ToList();
        return Named(env, paths, known.Select(k => k.ContainerId));
    }

    /// <summary>Codex's folder: the setting when it names one, else <c>CODEX_HOME</c>, else <c>~/.codex</c>.</summary>
    public static string Codex(UsageEnvironment env, string setting)
    {
        if (setting.Trim().Length > 0)
            return env.Expand(setting);
        if (env.Variable("CODEX_HOME") is { Length: > 0 } home)
            return env.Expand(home);
        return Path.Combine(env.Home, ".codex");
    }

    /// <summary>A folder's path as its readings are keyed: full, with no separator at its end.</summary>
    public static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static List<ClaudeFolder> Named(UsageEnvironment env, List<string> paths, IEnumerable<string> taken)
    {
        var folders = new List<ClaudeFolder>();
        var ids = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Select(Normal).Distinct(PathComparer).Order(StringComparer.Ordinal))
        {
            if (folders.Count == MaxClaudeFolders)
                break;
            var name = env.Show(path);
            var shortName = name.StartsWith("~/", StringComparison.Ordinal) ? name[2..] : name;
            var id = IdOf(shortName);
            var unique = id;
            for (var n = 2; !ids.Add(unique); n++)
                unique = $"{id}-{n}";
            folders.Add(new ClaudeFolder(path, shortName, unique));
        }
        return folders;
    }

    /// <summary>
    /// <c>profile-</c> and the name, any character but letters, digits, <c>.</c>, <c>-</c> and <c>_</c> as <c>-</c>; a name
    /// too long for <see cref="MaxContainerIdChars"/> (with room for a collision's number) is cut and ends with a hash of
    /// the whole name, so the same folder always gets the same id and two long names stay apart.
    /// </summary>
    private static string IdOf(string name)
    {
        var id = "profile-" + new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        const int room = MaxContainerIdChars - CollisionRoom;
        if (id.Length <= room)
            return id;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
        return id[..(room - hash.Length - 1)] + "-" + hash;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool SamePath(string a, string b) => PathComparer.Equals(Normal(a), Normal(b));
}
