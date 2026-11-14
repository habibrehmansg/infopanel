using System.Text.Json;

namespace InfoPanel.AiUsage;

/// <summary>
/// Where Connect looks besides the Claude folders: the organisation's (managed) settings files, and whether this is
/// Windows, where Connect waits for its check (docs/2.0/ai-usage.md). The plugin's own: <see cref="Default"/>; a test
/// gives fixture paths, and <see cref="None"/> (no managed settings file) is what a test that doesn't care gets, so no
/// test ever reads this computer's.
/// </summary>
internal sealed record ConnectPlaces(IReadOnlyList<string> ManagedSettings, bool Windows)
{
    /// <summary>No managed settings file, on this computer's platform.</summary>
    public static ConnectPlaces None => new([], OperatingSystem.IsWindows());

    /// <summary>This computer's: Claude Code's managed settings files for its platform.</summary>
    public static ConnectPlaces Default()
    {
        if (OperatingSystem.IsWindows())
            return new([@"C:\Program Files\ClaudeCode\managed-settings.json", @"C:\ProgramData\ClaudeCode\managed-settings.json"], true);
        if (OperatingSystem.IsMacOS())
            return new(["/Library/Application Support/ClaudeCode/managed-settings.json"], false);
        return new(["/etc/claude-code/managed-settings.json"], false);
    }
}

/// <summary>
/// Settings that set Claude Code's status line over the user's own, which Connect changes: the organisation's (managed)
/// settings, and projects' (<c>{project}/.claude/settings.json</c> and <c>settings.local.json</c>). Every file is read
/// through <see cref="IUsageFiles"/>, whole, at most <see cref="MaxFileBytes"/>, and only whether it sets
/// <c>statusLine</c> is taken from it.
/// </summary>
internal static class Overrides
{
    /// <summary>The largest settings file read.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>The most project folders of a Claude folder looked at.</summary>
    public const int MaxProjects = 500;

    /// <summary>The most entries of a Claude folder's <c>projects/</c> looked at.</summary>
    public const int MaxProjectEntries = 2000;

    /// <summary>The most folders listed or asked about to find one project's folder from its name (listings already made for another project are free).</summary>
    public const int MaxLooksPerProject = 64;

    /// <summary>The most entries of one folder looked at to find a project's folder in it.</summary>
    public const int MaxFolderEntries = 10_000;

    /// <summary>The most time spent looking for projects' status lines, by the plugin's clock: an action's state is asked for often, and must answer within seconds.</summary>
    public static readonly TimeSpan MaxProjectsTime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The organisation's settings: null when none sets the status line, else why Connect can't run. A managed file
    /// that is there but can't be read or isn't plain JSON counts as one that may set it.
    /// </summary>
    public static string? Managed(UsageEnvironment env, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            switch (SetsStatusLine(env, path))
            {
                case Sets.Yes:
                    return "Your organisation's settings set the status line";
                case Sets.Unknown:
                    return "Your organisation's settings couldn't be read, so Connect can't tell whether they set the status line";
            }
        }
        return null;
    }

    /// <summary>
    /// The projects whose settings set the status line, as they show (<c>~/…</c> under the home), by name: every project
    /// Claude Code keeps a folder for in <paramref name="folders"/>' <c>projects/</c>, its path found from the folder's
    /// name (<see cref="ProjectPaths"/>), never by opening a transcript. Settings files in <paramref name="userSettings"/>
    /// (the user's own, real paths) aren't a project's.
    /// </summary>
    public static IReadOnlyList<string> Projects(UsageEnvironment env, IReadOnlyList<ClaudeFolder> folders, IReadOnlySet<string> userSettings)
    {
        var started = env.Time.GetTimestamp();
        bool OutOfTime() => env.Time.GetElapsedTime(started) > MaxProjectsTime;
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var listings = new Dictionary<string, Listed>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            var projects = Path.Combine(folder.Path, "projects");
            var listing = env.Files.Directories(projects, "*", MaxProjectEntries, OutOfTime);
            foreach (var project in listing.Paths.Take(MaxProjects))
            {
                if (OutOfTime())
                    return [.. found];
                var name = Path.GetFileName(project);
                if (!seen.Add(name))
                    continue;
                foreach (var path in ProjectPaths(env, name, listings, OutOfTime))
                {
                    var settings = Path.Combine(path, ".claude");
                    foreach (var file in new[] { "settings.json", "settings.local.json" })
                    {
                        var full = Path.Combine(settings, file);
                        if (userSettings.Contains(ConnectFiles.Real(full) ?? full))
                            continue;
                        if (SetsStatusLine(env, full) == Sets.Yes)
                        {
                            found.Add(ConnectFiles.Show(env, path));
                            break;
                        }
                    }
                }
            }
        }
        return [.. found];
    }

    /// <summary>
    /// Claude Code's name for a project's folder: the folder it ran in, as the system gives it (links resolved, so no
    /// folder on the way is a link), with every character but an ASCII letter or digit as <c>-</c>.
    /// </summary>
    public static string ProjectName(string path) => new([.. path.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);

    /// <summary>
    /// The folders a project folder's <paramref name="name"/> stands for (<c>-Users-jo-my-app</c>: <c>/Users/jo/my-app</c>,
    /// or <c>/Users/jo/my/app</c> if that is there too): from <c>/</c> down, the folders whose names, written as Claude
    /// Code writes them (<see cref="ProjectName"/>), the name goes on with, each followed by the <c>-</c> of a <c>/</c>.
    /// A folder too full to list within <see cref="MaxFolderEntries"/> entries (a temp folder) is asked instead whether
    /// the names the rest of the name gives with each <c>-</c> as itself are there. Folders' names only, through
    /// <see cref="IUsageFiles"/>: at most <see cref="MaxLooksPerProject"/> new listings and folders asked about, at most
    /// 8 folders found; none on Windows or for a name that isn't a path's.
    /// </summary>
    internal static IReadOnlyList<string> ProjectPaths(UsageEnvironment env, string name, Dictionary<string, Listed>? listings = null, Func<bool>? stop = null)
    {
        var found = new List<string>();
        if (OperatingSystem.IsWindows() || name.Length < 2 || name[0] != '-')
            return found;
        var budget = MaxLooksPerProject;
        Walk(env, "/", name[1..], listings ?? new(StringComparer.Ordinal), stop, ref budget, found);
        return found;
    }

    /// <summary>A folder's folders, by name and as Claude Code would write each, and whether there are more than were listed.</summary>
    internal sealed record Listed(IReadOnlyList<(string Name, string Named)> Entries, bool Truncated);

    private static void Walk(UsageEnvironment env, string folder, string rest, Dictionary<string, Listed> listings, Func<bool>? stop, ref int budget, List<string> found)
    {
        if (found.Count >= 8 || stop?.Invoke() == true)
            return;
        if (!listings.TryGetValue(folder, out var listed))
        {
            if (budget-- <= 0)
                return;
            var listing = env.Files.Directories(folder, "*", MaxFolderEntries, stop);
            listings[folder] = listed = new([.. listing.Paths.Select(p => Path.GetFileName(p)).Select(n => (n, ProjectName(n)))], listing.Truncated);
        }
        var tried = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, named) in listed.Entries)
        {
            if (rest == named)
                found.Add(Path.Combine(folder, entry));
            else if (rest.Length > named.Length + 1 && rest.StartsWith(named, StringComparison.Ordinal) && rest[named.Length] == '-')
                Walk(env, Path.Combine(folder, entry), rest[(named.Length + 1)..], listings, stop, ref budget, found);
            tried.Add(entry);
        }
        if (!listed.Truncated)
            return;
        // Past the listing's end: the folder names the rest gives up to each '-' (or whole), each '-' kept as one.
        for (var end = 1; end <= rest.Length; end++)
        {
            if (end < rest.Length && rest[end] != '-')
                continue;
            var entry = rest[..end];
            if (tried.Contains(entry) || budget-- <= 0)
                continue;
            if (!env.Files.DirectoryExists(Path.Combine(folder, entry)))
                continue;
            if (end == rest.Length)
                found.Add(Path.Combine(folder, entry));
            else if (end + 1 < rest.Length)
                Walk(env, Path.Combine(folder, entry), rest[(end + 1)..], listings, stop, ref budget, found);
        }
    }

    private enum Sets { No, Yes, Unknown }

    /// <summary>Whether the settings file at <paramref name="path"/> sets <c>statusLine</c>: no when it isn't there.</summary>
    private static Sets SetsStatusLine(UsageEnvironment env, string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder == null || !env.Files.DirectoryExists(folder))
            return Sets.No;
        try
        {
            using var file = env.Files.Open(folder, path);
            if (file.Stamp.Length > MaxFileBytes || file.ReadAtMost(MaxFileBytes) is not { } bytes)
                return Sets.Unknown;
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Sets.Unknown;
            return doc.RootElement.EnumerateObject().Any(p => p.NameEquals("statusLine") && p.Value.ValueKind != JsonValueKind.Null) ? Sets.Yes : Sets.No;
        }
        catch (FileNotFoundException)
        {
            return Sets.No;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Sets.Unknown;
        }
    }
}
