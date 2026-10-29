using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using InfoPanel.Extensions;
using InfoPanel.Images;
using InfoPanel.Models;

namespace InfoPanel.Persistence;

/// <summary>
/// The 1.4 <c>.infopanel</c> export: a zip of <c>Profile.xml</c> (one <c>Profile</c>), <c>DisplayItems.xml</c>
/// (the items) and <c>assets/…</c> (the profile's assets folder). Written for the 1.x e2e archive; 1.x archives are read by
/// the 1.x import (<c>InfoPanel.Import.V1Reader</c>), which installs nothing.
/// </summary>
public static class InfoPanelArchive
{
    public const string ProfileEntry = "Profile.xml";
    public const string ItemsEntry = "DisplayItems.xml";
    public const string FileExtension = ".infopanel";

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
}
