using System.Text.RegularExpressions;
using InfoPanel.Persistence;

namespace InfoPanel.Plugins;

/// <summary>
/// A plugin package: a folder <c>InfoPanel.{Name}</c> holding <c>InfoPanel.{Name}.dll</c> (and optionally
/// <c>PluginInfo.ini</c>), bundled beside the engine or installed in the data folder. The engine runs one host per package.
/// </summary>
/// <param name="Folder">The folder's name, which identifies the package (compared ignoring case).</param>
/// <param name="Directory">The folder's full path.</param>
/// <param name="DllPath">The plugin DLL the host loads.</param>
/// <param name="Bundled">Whether it ships with the engine (read-only) rather than being installed by the user.</param>
public sealed record PluginPackage(string Folder, string Directory, string DllPath, bool Bundled);

/// <summary>Finds the plugin packages in the bundled and installed folders.</summary>
public static partial class PluginPackages
{
    /// <summary>At most this many entries are looked at in the installed folder.</summary>
    public const int ListingEntries = 10_000;

    [GeneratedRegex(@"\AInfoPanel\.[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex FolderPattern();

    /// <summary>Whether <paramref name="name"/> is a package folder's name, <c>InfoPanel.{Name}</c>.</summary>
    public static bool IsPackageName(string name) => FolderPattern().IsMatch(name);

    /// <summary>
    /// The packages in <paramref name="bundled"/> (beside the engine; may be null or absent) then in
    /// <paramref name="installed"/> (the data folder's <c>plugins/</c>, listed through <see cref="RegularFile"/>, links
    /// never followed), each in name order. A folder whose name isn't <c>InfoPanel.{Name}</c> or that has no
    /// <c>InfoPanel.{Name}.dll</c> is skipped and logged; an installed package with a bundled one's name (ignoring case)
    /// is skipped too: the bundled one wins. A package that can't be read, or a folder that can't be listed, is skipped
    /// and logged: the others still start.
    /// </summary>
    public static IReadOnlyList<PluginPackage> Find(string? bundled, string installed, ILogger logger)
    {
        var packages = new List<PluginPackage>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (bundled != null && System.IO.Directory.Exists(bundled))
        {
            List<DirectoryInfo> dirs;
            try
            {
                dirs = [.. new DirectoryInfo(bundled).EnumerateDirectories()];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("The bundled plugins folder {Folder} can't be listed, so no bundled plugin starts: {Error}", bundled, ex.Message);
                dirs = [];
            }

            foreach (var dir in dirs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (Package(dir, bundled: true, logger) is { } package && names.Add(package.Folder))
                    packages.Add(package);
            }
        }

        List<FileSystemInfo> entries;
        try
        {
            entries = [.. RegularFile.List(installed, new ListingBudget(ListingEntries, "The plugins folder"))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The plugins folder {Folder} can't be listed, so no installed plugin starts: {Error}", installed, ex.Message);
            return packages;
        }

        foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (entry is not DirectoryInfo dir)
                continue;
            if (dir.LinkTarget != null)
            {
                logger.LogWarning("Skipped {Folder} in the plugins folder: it is a link, which the engine doesn't follow", dir.Name);
                continue;
            }

            if (Package(dir, bundled: false, logger) is not { } package)
                continue;
            if (!names.Add(package.Folder))
            {
                logger.LogWarning("Skipped the installed plugin {Folder}: a bundled plugin has its name", package.Folder);
                continue;
            }
            packages.Add(package);
        }

        return packages;
    }

    private static PluginPackage? Package(DirectoryInfo dir, bool bundled, ILogger logger)
    {
        // Install leftovers and hidden folders aren't packages, and aren't worth a warning.
        if (dir.Name.StartsWith('.'))
            return null;
        if (!IsPackageName(dir.Name))
        {
            logger.LogWarning("Skipped {Folder} in {Parent}: a plugin's folder is named InfoPanel.{{Name}}", dir.Name, dir.Parent?.FullName);
            return null;
        }

        var dll = Path.Combine(dir.FullName, dir.Name + ".dll");
        try
        {
            if (RegularFile.StateOf(dll) != PathState.Other || new FileInfo(dll).LinkTarget != null)
            {
                logger.LogWarning("Skipped the plugin {Folder}: it has no {Dll}", dir.Name, dir.Name + ".dll");
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Skipped the plugin {Folder}: it can't be read: {Error}", dir.Name, ex.Message);
            return null;
        }

        return new PluginPackage(dir.Name, dir.FullName, dll, bundled);
    }
}
