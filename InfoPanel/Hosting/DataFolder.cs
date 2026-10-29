using InfoPanel.Extensions;
using InfoPanel.Persistence;

namespace InfoPanel.Hosting;

/// <summary>Start-up chores on the data folder.</summary>
public static class DataFolder
{
    /// <summary>Owner-only (0700) for folders the engine creates on Unix.</summary>
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Creates <paramref name="path"/> (and missing parents) when nothing is there, owner-only (0700) on Unix. A folder
    /// there (or a link to one) is used as it is; a link whose target can't be reached, or anything but a folder, is
    /// thrown, and nothing is created through it.
    /// </summary>
    /// <exception cref="UnresolvedLinkException">A link is there whose target can't be reached.</exception>
    /// <exception cref="IOException">Something other than a folder is there, or the folder couldn't be created.</exception>
    public static void CreatePrivate(string path)
    {
        switch (RegularFile.StateOf(path))
        {
            case PathState.Folder:
                return;
            case PathState.Unresolved:
                throw new UnresolvedLinkException(path);
            case PathState.Other:
                throw new IOException($"{path} isn't a folder.");
        }
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, OwnerOnly);
    }

    /// <summary>
    /// The path with every symbolic link in it (the folder itself and each ancestor) replaced by its final target,
    /// as <c>realpath</c> does; components that don't exist yet are kept as they are (<see cref="PathExtensions.ResolveRealPath"/>).
    /// </summary>
    /// <exception cref="IOException">The links loop.</exception>
    public static string ResolveRealPath(string path) => PathExtensions.ResolveRealPath(path);

    /// <summary>
    /// Whether <c>--fresh</c> may wipe <paramref name="root"/>: null when it may, else why not. Checked on the real
    /// path (links resolved): it must not be a filesystem root, the home folder or a folder above it, and it may hold
    /// only what the engine writes there (<see cref="DataPaths.IsEngineEntry"/>: <c>profiles.json</c>, <c>tokens.json</c>,
    /// <c>settings.json</c>, <c>profiles/</c>, <c>blobs/</c>, <c>fonts/</c>, <c>logs/</c>, <c>runtime.json</c>, <c>engine.lock</c>, step-1's
    /// <c>profiles.xml</c> and <c>assets/</c>, the 1.x migration's <c>import-v1.json</c>, and their write temp files).
    /// A folder that doesn't exist yet may be wiped.
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
                if (!DataPaths.IsEngineEntry(entry.Name))
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
        if (PathExtensions.LinkTargetOf(entry) != null)
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
