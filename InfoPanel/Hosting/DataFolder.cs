using System.Text.RegularExpressions;

namespace InfoPanel.Hosting;

/// <summary>Start-up chores on the data folder.</summary>
public static partial class DataFolder
{
    /// <summary>Top-level files the engine writes in its data folder.</summary>
    private static readonly HashSet<string> EngineFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "profiles.xml", "runtime.json", "engine.lock",
    };

    /// <summary>Top-level folders the engine writes in its data folder.</summary>
    private static readonly HashSet<string> EngineFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "profiles", "assets", "fonts", "logs",
    };

    /// <summary>A temp file left by an interrupted write of an engine file (<c>{name}.{guid:N}.tmp</c>).</summary>
    [GeneratedRegex("^(profiles\\.xml|runtime\\.json)\\.[0-9a-f]{32}\\.tmp$", RegexOptions.IgnoreCase)]
    private static partial Regex EngineTempFile();

    /// <summary>Owner-only (0700) for folders the engine creates on Unix.</summary>
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates <paramref name="path"/> (and missing parents) when it doesn't exist, owner-only (0700) on Unix.</summary>
    public static void CreatePrivate(string path)
    {
        if (Directory.Exists(path))
            return;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, OwnerOnly);
    }

    /// <summary>
    /// The path with every symbolic link in it (the folder itself and each ancestor) replaced by its final target,
    /// as <c>realpath</c> does; components that don't exist yet are kept as they are.
    /// </summary>
    /// <exception cref="IOException">The links loop.</exception>
    public static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        for (var hops = 0; hops < 64; hops++)
        {
            var next = ResolveFirstLink(full);
            if (next == null)
                return Path.TrimEndingDirectorySeparator(full) is { Length: > 0 } trimmed ? trimmed : full;
            full = next;
        }

        throw new IOException($"Too many levels of symbolic links in '{path}'.");
    }

    /// <summary>The path with its first link component (from the root down) replaced by the link's target, or null when none is a link.</summary>
    private static string? ResolveFirstLink(string full)
    {
        var root = Path.GetPathRoot(full) ?? "";
        var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            var target = LinkTargetOf(info);
            if (target == null && !info.Exists)
                return null; // the rest doesn't exist yet
            if (target != null)
            {
                // A relative target is relative to the folder that holds the link.
                var resolved = Path.GetFullPath(target, Path.GetDirectoryName(current) ?? root);
                return Path.GetFullPath(Path.Combine([resolved, .. parts[(i + 1)..]]));
            }
        }

        return null;
    }

    private static string? LinkTargetOf(FileSystemInfo info)
    {
        try
        {
            return info.LinkTarget;
        }
        catch (IOException)
        {
            return null; // gone, or not readable as a link
        }
    }

    /// <summary>
    /// Whether <c>--fresh</c> may wipe <paramref name="root"/>: null when it may, else why not. Checked on the real
    /// path (links resolved): it must not be a filesystem root, the home folder or a folder above it, and it may hold
    /// only what the engine itself writes there (<c>profiles.xml</c>, <c>profiles/</c>, <c>assets/</c>,
    /// <c>fonts/</c>, <c>logs/</c>, <c>runtime.json</c>, <c>engine.lock</c> and their write temp files). A folder
    /// that doesn't exist yet may be wiped.
    /// </summary>
    public static string? CheckWipeable(string root)
    {
        string real;
        try
        {
            real = ResolveRealPath(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"--fresh refuses to wipe '{root}': its real path can't be resolved ({ex.Message}).";
        }

        var shown = string.Equals(real, Path.GetFullPath(root), StringComparison.Ordinal) ? $"'{real}'" : $"'{root}' (really '{real}')";
        if (EngineOptions.IsProtectedDirectory(Path.GetFullPath(root)) || EngineOptions.IsProtectedDirectory(real))
            return $"--fresh refuses to wipe {shown}: it is a filesystem root, your home folder or a folder above it.";

        if (!Directory.Exists(real))
            return null;

        try
        {
            foreach (var entry in new DirectoryInfo(real).EnumerateFileSystemInfos())
            {
                if (!IsEngineEntry(entry.Name))
                    return $"--fresh refuses to wipe {shown}: it holds '{entry.Name}', which the engine didn't create. " +
                        "Use an empty folder or one the engine made, or empty it yourself.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"--fresh refuses to wipe {shown}: it can't be listed ({ex.Message}).";
        }

        return null;
    }

    private static bool IsEngineEntry(string name) =>
        EngineFiles.Contains(name) || EngineFolders.Contains(name) || EngineTempFile().IsMatch(name);

    /// <summary>
    /// <c>--fresh</c>: deletes everything in <paramref name="root"/> (its real path) except the engine lock, which
    /// this engine already holds. Re-checks <see cref="CheckWipeable"/> first and throws
    /// <see cref="InvalidOperationException"/> when it refuses. Links are deleted, never followed.
    /// </summary>
    public static void Wipe(string root, string lockFile)
    {
        if (CheckWipeable(root) is { } error)
            throw new InvalidOperationException(error);

        var real = ResolveRealPath(root);
        if (!Directory.Exists(real))
            return;

        var lockName = Path.GetFileName(lockFile);
        foreach (var entry in new DirectoryInfo(real).EnumerateFileSystemInfos())
        {
            if (string.Equals(entry.Name, lockName, StringComparison.OrdinalIgnoreCase))
                continue;
            DeleteWithoutFollowing(entry);
        }
    }

    private static void DeleteWithoutFollowing(FileSystemInfo entry)
    {
        if (LinkTargetOf(entry) != null)
        {
            // The link itself: unlink on Unix; a Windows directory link or junction needs RemoveDirectory.
            if (OperatingSystem.IsWindows() && entry.Attributes.HasFlag(FileAttributes.Directory))
                Directory.Delete(entry.FullName);
            else
                File.Delete(entry.FullName);
            return;
        }

        if (entry is DirectoryInfo directory)
        {
            foreach (var child in directory.EnumerateFileSystemInfos())
                DeleteWithoutFollowing(child);
            directory.Delete();
        }
        else
        {
            entry.Attributes = FileAttributes.Normal;
            entry.Delete();
        }
    }

    /// <summary>
    /// The web client's built files: <paramref name="configured"/> when given, else <c>{BaseDirectory}/wwwroot</c>
    /// (where an installer puts the bundled client) when it has an <c>index.html</c>. Null otherwise: the engine
    /// then serves the API only.
    /// </summary>
    public static string? FindWebRoot(string? configured)
    {
        if (configured != null)
            return configured;

        var bundled = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        return File.Exists(Path.Combine(bundled, "index.html")) ? bundled : null;
    }
}
