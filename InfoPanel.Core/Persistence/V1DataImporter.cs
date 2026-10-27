using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InfoPanel.Extensions;
using InfoPanel.Models;
using Serilog;

namespace InfoPanel.Persistence;

/// <summary>A 1.x profile added to the store. <see cref="GuidRegenerated"/> means its Guid was already taken.</summary>
public sealed record V1ImportedProfile(string Name, Guid OriginalGuid, Guid Guid, int ItemCount, int AssetCount)
{
    public bool GuidRegenerated => Guid != OriginalGuid;
}

/// <summary>A 1.x profile that was not imported, and why.</summary>
public sealed record V1SkippedProfile(string Name, Guid Guid, string Reason);

public sealed record V1ImportSummary(IReadOnlyList<V1ImportedProfile> Imported, IReadOnlyList<V1SkippedProfile> Skipped)
{
    /// <summary>The imported profiles whose Guid had to be regenerated.</summary>
    public IEnumerable<V1ImportedProfile> Renamed => Imported.Where(p => p.GuidRegenerated);
}

/// <summary>Copies the profiles of a 1.x data folder (<c>%LOCALAPPDATA%\InfoPanel</c>) into a 2.0 store.</summary>
public static class V1DataImporter
{
    private static readonly ILogger Logger = Log.ForContext(typeof(V1DataImporter));

    /// <summary>
    /// Imports every profile in <c>{v1Root}/profiles.xml</c> with its items (<c>profiles/{guid}.xml</c>) and assets
    /// (<c>assets/{guid}/</c>), in list order. Profiles keep their Guid unless the store already has it, in which case
    /// they get a new one (the name is kept). A profile that fails for any reason (wider or taller than
    /// <see cref="ProfileStore.MaxDimension"/>, items file missing or unreadable, assets that can't be copied, a failed write) is skipped and reported, with anything it left in the store removed
    /// best effort. The 1.x folder is only read.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The store's folder is the 1.x folder, contains it, or is inside its <c>profiles/</c> or <c>assets/</c>.
    /// </exception>
    /// <exception cref="FileNotFoundException"><c>profiles.xml</c> is missing.</exception>
    /// <exception cref="InvalidDataException"><c>profiles.xml</c> can't be read.</exception>
    public static V1ImportSummary ImportFolder(string v1Root, ProfileStore store) =>
        ImportFolder(v1Root, store, CopyDirectory);

    /// <param name="copyAssets">Copies a 1.x assets folder (source, destination) and returns the file count.</param>
    internal static V1ImportSummary ImportFolder(string v1Root, ProfileStore store, Func<string, string, int> copyAssets)
    {
        var v1 = new DataPaths(v1Root);
        EnsureSeparateFolders(v1, store.Paths);
        if (!File.Exists(v1.ProfilesFile))
            throw new FileNotFoundException("No 1.x profile list found.", v1.ProfilesFile);

        List<Profile> profiles;
        try
        {
            using var stream = File.OpenRead(v1.ProfilesFile);
            profiles = ProfileSerializer.ReadProfiles(stream);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{v1.ProfilesFile}: {ex.Message}", ex);
        }

        var imported = new List<V1ImportedProfile>();
        var skipped = new List<V1SkippedProfile>();
        foreach (var profile in profiles)
        {
            var originalGuid = profile.Guid;
            if (ProfileStore.ImportSizeError(profile) is { } sizeError)
            {
                Skip(profile, sizeError);
                continue;
            }

            var itemsFile = v1.ProfileItemsFile(originalGuid);
            if (!File.Exists(itemsFile))
            {
                Skip(profile, "items file missing");
                continue;
            }

            List<DisplayItem> items;
            try
            {
                items = ProfileStore.ReadItemsFile(itemsFile);
            }
            catch (Exception ex)
            {
                Skip(profile, "items file unreadable: " + ex.Message);
                continue;
            }

            foreach (var item in items)
                item.SetProfile(profile);

            var cleanupErrors = new List<string>();
            int assetCount;
            try
            {
                assetCount = store.AddImportedProfile(profile, items, keepGuidIfFree: true,
                    destination => copyAssets(v1.ProfileAssetsDirectory(originalGuid), destination), cleanupErrors);
            }
            catch (Exception ex)
            {
                profile.Guid = originalGuid;
                var reason = "copy failed: " + ex.Message;
                if (cleanupErrors.Count > 0)
                    reason += "; cleanup incomplete: " + string.Join("; ", cleanupErrors);
                Skip(profile, reason);
                continue;
            }

            imported.Add(new V1ImportedProfile(profile.Name, originalGuid, profile.Guid, items.Count, assetCount));
            Logger.Information("Imported 1.x profile {ProfileName} {OriginalGuid} as {ProfileGuid}: {ItemCount} items, {AssetCount} assets",
                profile.Name, originalGuid, profile.Guid, items.Count, assetCount);
        }

        return new V1ImportSummary(imported, skipped);

        void Skip(Profile profile, string reason)
        {
            skipped.Add(new V1SkippedProfile(profile.Name, profile.Guid, reason));
            Logger.Warning("Skipped 1.x profile {ProfileName} {ProfileGuid}: {Reason}", profile.Name, profile.Guid, reason);
        }
    }

    /// <summary>
    /// Throws when importing would write into the 1.x data: the store's folder is the 1.x folder, contains it, or is
    /// inside its <c>profiles/</c> or <c>assets/</c>. A store elsewhere inside the 1.x folder is allowed: the default
    /// layout puts 2.0 in <c>InfoPanel/v2</c>, inside 1.x's <c>InfoPanel/</c> on Windows.
    /// </summary>
    private static void EnsureSeparateFolders(DataPaths v1, DataPaths store)
    {
        if (FindOverlap(v1.Root, store.Root) is { } error)
            throw new ArgumentException(error, nameof(store));
    }

    /// <summary>
    /// The rule <see cref="ImportFolder(string, ProfileStore)"/> enforces, for callers that validate up front (the
    /// engine checks it before <c>--fresh</c> deletes anything): the error message when importing
    /// <paramref name="v1Root"/> into a store at <paramref name="storeRoot"/> would write into the 1.x data, else null.
    /// </summary>
    public static string? FindOverlap(string v1Root, string storeRoot) => Import.V1Folder.FindOverlap(v1Root, storeRoot);

    /// <summary>Copies every file under <paramref name="source"/> (if it exists), keeping subfolders. Returns the file count.</summary>
    private static int CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
            return 0;

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
        }
        return count;
    }
}
