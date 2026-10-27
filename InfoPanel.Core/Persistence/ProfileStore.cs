using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using InfoPanel.Images;
using InfoPanel.Models;
using Serilog;

namespace InfoPanel.Persistence;

/// <summary>
/// The profile list and each profile's items on disk, in the 1.x layout under <see cref="DataPaths"/>.
/// Every write goes to a uniquely named <c>.tmp</c> file in the same folder and then replaces the target, so a crash
/// leaves either the old file or the new one. One lock per data folder, shared by every store on that folder in the
/// process, serializes all operations; one engine process owns the folder.
/// </summary>
public sealed class ProfileStore : IAssetLocator
{
    private static readonly ILogger Logger = Log.ForContext<ProfileStore>();

    /// <summary>One lock per data root (full path, no trailing separator, case-insensitive), for every store on it.</summary>
    private static readonly ConcurrentDictionary<string, Lock> RootLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The largest profile width or height a 1.x profile may have here (the document format's canvas limit, <c>Workspace.MaxDimension</c>).</summary>
    public const int MaxDimension = 16384;

    private readonly Lock _lock;

    public ProfileStore(DataPaths paths)
    {
        Paths = paths;
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Root));
        _lock = RootLocks.GetOrAdd(key, _ => new Lock());
    }

    public DataPaths Paths { get; }

    public string GetProfileAssetsDirectory(Guid profileId) => Paths.ProfileAssetsDirectory(profileId);

    /// <summary>
    /// Why an imported profile is refused for its size, or null: a width or height over <see cref="MaxDimension"/>.
    /// Smaller sizes, 0 and negative ones included (1.x wrote them; rendering treats them as 1), are kept as they are.
    /// </summary>
    internal static string? ImportSizeError(Profile profile) =>
        profile.Width > MaxDimension || profile.Height > MaxDimension
            ? $"its size {profile.Width}×{profile.Height} is over the {MaxDimension}×{MaxDimension} maximum"
            : null;

    /// <summary>The profile list, or an empty list when there is no <c>profiles.xml</c> yet.</summary>
    /// <exception cref="InvalidDataException"><c>profiles.xml</c> exists but can't be read. It is left untouched.</exception>
    public List<Profile> LoadProfiles()
    {
        lock (_lock)
            return LoadProfilesCore();
    }

    public void SaveProfiles(IEnumerable<Profile> profiles)
    {
        var list = profiles.ToList();
        lock (_lock)
            WriteAtomic(Paths.ProfilesFile, stream => ProfileSerializer.WriteProfiles(stream, list));
    }

    public bool ContainsProfile(Guid profileId)
    {
        lock (_lock)
            return LoadProfilesCore().Any(p => p.Guid == profileId);
    }

    /// <summary>
    /// A profile's items with their profile set and ids made unique, or an empty list when the profile has no
    /// items file yet (as in 1.x).
    /// </summary>
    /// <exception cref="InvalidDataException">The items file exists but can't be read. It is left untouched.</exception>
    public List<DisplayItem> LoadItems(Profile profile)
    {
        List<DisplayItem> items;
        lock (_lock)
        {
            var path = Paths.ProfileItemsFile(profile.Guid);
            if (!File.Exists(path))
                return [];
            items = ReadItemsFile(path);
        }

        foreach (var item in items)
            item.SetProfile(profile);
        return items;
    }

    public void SaveItems(Guid profileId, IEnumerable<DisplayItem> items)
    {
        var list = items.ToList();
        lock (_lock)
            WriteAtomic(Paths.ProfileItemsFile(profileId), stream => ProfileSerializer.WriteItems(stream, list));
    }

    /// <summary>Writes the profile's items, then appends the profile to the list.</summary>
    /// <exception cref="InvalidOperationException">A profile with the same Guid is already in the list.</exception>
    public void AddProfile(Profile profile, IEnumerable<DisplayItem> items)
    {
        var list = items.ToList();
        lock (_lock)
        {
            var profiles = LoadProfilesCore();
            if (profiles.Any(p => p.Guid == profile.Guid))
                throw new InvalidOperationException($"Profile {profile.Guid} already exists.");

            var itemsFile = Paths.ProfileItemsFile(profile.Guid);
            var itemsFileExisted = File.Exists(itemsFile);
            try
            {
                AppendProfile(profiles, profile, list);
            }
            catch
            {
                if (!itemsFileExisted)
                    TryDelete(itemsFile, directory: false, cleanupErrors: null);
                throw;
            }
        }
    }

    /// <summary>
    /// For importers, all under the data folder's lock: gives <paramref name="profile"/> a Guid with no list entry,
    /// items file or assets folder (its own when <paramref name="keepGuidIfFree"/> and it is free, else a new one),
    /// runs <paramref name="writeAssets"/> on that (absent) assets folder, writes the items and appends the profile to
    /// the list. Returns what <paramref name="writeAssets"/> returned. On failure it removes the assets folder and items
    /// file this call created, best effort (anything it can't remove is logged and added to
    /// <paramref name="cleanupErrors"/>), and rethrows; the profile's Guid is left as assigned.
    /// </summary>
    internal int AddImportedProfile(Profile profile, List<DisplayItem> items, bool keepGuidIfFree,
        Func<string, int> writeAssets, ICollection<string> cleanupErrors)
    {
        lock (_lock)
        {
            var profiles = LoadProfilesCore();
            if (!keepGuidIfFree || !IsGuidFree(profiles, profile.Guid))
            {
                do
                    profile.Guid = Guid.NewGuid();
                while (!IsGuidFree(profiles, profile.Guid));
            }

            var assets = Paths.ProfileAssetsDirectory(profile.Guid);
            try
            {
                var result = writeAssets(assets);
                AppendProfile(profiles, profile, items);
                return result;
            }
            catch
            {
                // The Guid was free, so the items file and assets folder, if present now, are this call's.
                TryDelete(Paths.ProfileItemsFile(profile.Guid), directory: false, cleanupErrors);
                TryDelete(assets, directory: true, cleanupErrors);
                throw;
            }
        }
    }

    private bool IsGuidFree(List<Profile> profiles, Guid guid) =>
        profiles.All(p => p.Guid != guid)
        && !File.Exists(Paths.ProfileItemsFile(guid))
        && !Directory.Exists(Paths.ProfileAssetsDirectory(guid));

    private void AppendProfile(List<Profile> profiles, Profile profile, List<DisplayItem> items)
    {
        WriteAtomic(Paths.ProfileItemsFile(profile.Guid), stream => ProfileSerializer.WriteItems(stream, items));
        profiles.Add(profile);
        WriteAtomic(Paths.ProfilesFile, stream => ProfileSerializer.WriteProfiles(stream, profiles));
    }

    /// <summary>
    /// Deletes a file or folder, clearing read-only attributes first (copied 1.x assets can be read-only on Windows).
    /// Failures are logged and added to <paramref name="cleanupErrors"/>, never thrown.
    /// </summary>
    private static void TryDelete(string path, bool directory, ICollection<string>? cleanupErrors)
    {
        try
        {
            if (directory)
            {
                if (!Directory.Exists(path))
                    return;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not remove {Path} while rolling back", path);
            cleanupErrors?.Add($"{path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the profile from the list and deletes exactly its items file and its assets folder.
    /// Nothing else is touched (1.x swept every file and folder not in the list on each save).
    /// </summary>
    public void DeleteProfile(Guid profileId)
    {
        lock (_lock)
        {
            var profiles = LoadProfilesCore();
            if (profiles.RemoveAll(p => p.Guid == profileId) > 0)
                WriteAtomic(Paths.ProfilesFile, stream => ProfileSerializer.WriteProfiles(stream, profiles));

            var itemsFile = Paths.ProfileItemsFile(profileId);
            if (File.Exists(itemsFile))
                File.Delete(itemsFile);

            var assets = Paths.ProfileAssetsDirectory(profileId);
            if (Directory.Exists(assets))
                Directory.Delete(assets, recursive: true);
        }
    }

    private List<Profile> LoadProfilesCore()
    {
        var path = Paths.ProfilesFile;
        if (!File.Exists(path))
            return [];
        try
        {
            using var stream = File.OpenRead(path);
            return ProfileSerializer.ReadProfiles(stream);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }

    internal static List<DisplayItem> ReadItemsFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ProfileSerializer.ReadItems(stream);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Writes <paramref name="path"/> through a uniquely named <c>{path}.{random}.tmp</c> in the same folder, flushed to disk, then
    /// <see cref="File.Replace(string, string, string?, bool)"/> (or a move when the target is new).
    /// On failure the target is unchanged and the temp file is removed.
    /// </summary>
    internal static void WriteAtomic(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
