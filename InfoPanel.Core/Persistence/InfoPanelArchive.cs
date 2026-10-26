using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using InfoPanel.Extensions;
using InfoPanel.Images;
using InfoPanel.Models;
using Serilog;

namespace InfoPanel.Persistence;

/// <summary>An asset entry left out of an import, and why.</summary>
public sealed record SkippedArchiveEntry(string Entry, string Reason);

/// <summary>The profile an import added to the store, and the asset entries it left out.</summary>
public sealed record ArchiveImportResult(Profile Profile, int ItemCount, int AssetCount, IReadOnlyList<SkippedArchiveEntry> SkippedEntries);

/// <summary>
/// The 1.4 <c>.infopanel</c> export: a zip of <c>Profile.xml</c> (one <c>Profile</c>), <c>DisplayItems.xml</c>
/// (the items) and <c>assets/…</c> (the profile's assets folder).
/// </summary>
public static class InfoPanelArchive
{
    private static readonly ILogger Logger = Log.ForContext(typeof(InfoPanelArchive));

    public const string ProfileEntry = "Profile.xml";
    public const string ItemsEntry = "DisplayItems.xml";
    public const string FileExtension = ".infopanel";

    /// <summary>Most bytes an import decompresses, XML and assets together.</summary>
    public const long MaxExtractedBytes = 512L * 1024 * 1024;

    /// <summary>Most entries an importable archive may have.</summary>
    public const int MaxEntries = 10_000;

    /// <summary>1.x's export file name: the profile name with invalid characters and spaces replaced, then Unix seconds.</summary>
    public static string FileNameFor(Profile profile, DateTimeOffset now) =>
        profile.Name.SanitizeFileName().Replace(" ", "_") + "-" + now.ToUnixTimeSeconds() + FileExtension;

    public static void Export(Profile profile, IEnumerable<DisplayItem> items, IAssetLocator assets, string archivePath)
    {
        using var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        Export(profile, items, assets, stream);
    }

    /// <summary>
    /// Writes the archive. <c>Profile.xml</c> carries what 1.x exported (name, size, background, font, colour, FPS,
    /// OpenGL, font scale), not the window placement or activation of this machine.
    /// </summary>
    public static void Export(Profile profile, IEnumerable<DisplayItem> items, IAssetLocator assets, Stream destination)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        var exported = new Profile(profile.Name, profile.Width, profile.Height)
        {
            ShowFps = profile.ShowFps,
            BackgroundColor = profile.BackgroundColor,
            Font = profile.Font,
            FontSize = profile.FontSize,
            Color = profile.Color,
            OpenGL = profile.OpenGL,
            FontScale = profile.FontScale,
        };
        using (var stream = archive.CreateEntry(ProfileEntry).Open())
            ProfileSerializer.WriteProfile(stream, exported);

        using (var stream = archive.CreateEntry(ItemsEntry).Open())
            ProfileSerializer.WriteItems(stream, items);

        var assetsDirectory = assets.GetProfileAssetsDirectory(profile.Guid);
        if (!Directory.Exists(assetsDirectory))
            return;
        foreach (var file in Directory.EnumerateFiles(assetsDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(assetsDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
            archive.CreateEntryFromFile(file, "assets/" + relative);
        }
    }

    public static ArchiveImportResult Import(string archivePath, ProfileStore store)
    {
        using var stream = File.OpenRead(archivePath);
        return Import(stream, store);
    }

    /// <summary>
    /// Adds the archive's profile to <paramref name="store"/> under a new Guid, named <c>"[Import] " + name</c>,
    /// with its items (ids made unique) and assets. Asset entries that would land outside the profile's assets
    /// folder are skipped and reported; directory entries are skipped silently. On any failure the assets folder and
    /// items file this import wrote are removed (best effort) and the exception is rethrown.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Not a zip, no <c>Profile.xml</c> or <c>DisplayItems.xml</c>, unreadable XML, a profile wider or taller than
    /// <see cref="ProfileStore.MaxDimension"/>, more than <see cref="MaxEntries"/>
    /// entries or more than <see cref="MaxExtractedBytes"/> decompressed. Nothing is added to the store.
    /// </exception>
    public static ArchiveImportResult Import(Stream archiveStream, ProfileStore store) =>
        Import(archiveStream, store, MaxEntries, MaxExtractedBytes);

    internal static ArchiveImportResult Import(Stream archiveStream, ProfileStore store, int maxEntries, long maxExtractedBytes)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("Not an .infopanel archive: " + ex.Message, ex);
        }

        using (archive)
        {
            if (archive.Entries.Count > maxEntries)
                throw new InvalidDataException($"The archive has {archive.Entries.Count} entries; at most {maxEntries} are allowed.");

            ZipArchiveEntry? profileEntry = null;
            ZipArchiveEntry? itemsEntry = null;
            var assetEntries = new List<(ZipArchiveEntry Entry, string Relative)>();
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Equals(ProfileEntry, StringComparison.OrdinalIgnoreCase))
                    profileEntry = entry;
                else if (entry.FullName.Equals(ItemsEntry, StringComparison.OrdinalIgnoreCase))
                    itemsEntry = entry;
                else if (entry.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
                         || entry.FullName.StartsWith("assets\\", StringComparison.OrdinalIgnoreCase))
                    assetEntries.Add((entry, entry.FullName["assets/".Length..].Replace('\\', '/')));
            }

            if (profileEntry == null || itemsEntry == null)
                throw new InvalidDataException($"The archive has no {ProfileEntry} or {ItemsEntry}.");

            var budget = maxExtractedBytes;
            var profile = ProfileSerializer.ReadProfile(ReadEntry(profileEntry, ref budget));
            if (ProfileStore.ImportSizeError(profile) is { } sizeError)
                throw new InvalidDataException($"The archive's profile can't be imported: {sizeError}.");

            var items = ProfileSerializer.ReadItems(ReadEntry(itemsEntry, ref budget));

            profile.Name = "[Import] " + profile.Name;
            foreach (var item in items)
                item.SetProfile(profile);

            var skipped = new List<SkippedArchiveEntry>();
            var assetCount = store.AddImportedProfile(profile, items, keepGuidIfFree: false, directory =>
            {
                var assetsDirectory = Path.GetFullPath(directory);
                var count = 0;
                foreach (var (entry, relative) in assetEntries)
                {
                    if (relative.Length == 0 || relative.EndsWith('/'))
                        continue; // a directory entry

                    var target = ResolveAssetPath(assetsDirectory, relative);
                    if (target == null)
                    {
                        skipped.Add(new SkippedArchiveEntry(entry.FullName, "path escapes assets/"));
                        Logger.Warning("Skipped archive entry {Entry}: its path escapes assets/", entry.FullName);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var source = entry.Open())
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        CopyLimited(source, output, ref budget);
                    count++;
                }
                return count;
            }, cleanupErrors: []);

            Logger.Information("Imported profile {ProfileName} as {ProfileGuid} with {ItemCount} items and {AssetCount} assets",
                profile.Name, profile.Guid, items.Count, assetCount);
            return new ArchiveImportResult(profile, items.Count, assetCount, skipped);
        }
    }

    /// <summary>
    /// The full path for an asset entry's path relative to <c>assets/</c>, or null when it is rooted, has an empty,
    /// <c>.</c> or <c>..</c> segment, or would resolve outside <paramref name="assetsDirectory"/>.
    /// </summary>
    internal static string? ResolveAssetPath(string assetsDirectory, string relative)
    {
        var segments = relative.Split('/');
        if (segments.Any(s => s.Length == 0 || s == "." || s == ".." || s.Contains(':')))
            return null;

        var native = Path.Combine(segments);
        if (Path.IsPathRooted(native))
            return null;

        var target = Path.GetFullPath(Path.Combine(assetsDirectory, native));
        if (!target.IsSubdirectoryOf(assetsDirectory) || target.Equals(assetsDirectory, StringComparison.OrdinalIgnoreCase))
            return null;
        return target;
    }

    private static MemoryStream ReadEntry(ZipArchiveEntry entry, ref long budget)
    {
        var buffer = new MemoryStream();
        using (var source = entry.Open())
            CopyLimited(source, buffer, ref budget);
        buffer.Position = 0;
        return buffer;
    }

    /// <summary>Copies, counting decompressed bytes against <paramref name="budget"/> (entry headers can lie about sizes).</summary>
    private static void CopyLimited(Stream source, Stream destination, ref long budget)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            budget -= read;
            if (budget < 0)
                throw new InvalidDataException("The archive decompresses to more than the import limit.");
            destination.Write(buffer, 0, read);
        }
    }
}
