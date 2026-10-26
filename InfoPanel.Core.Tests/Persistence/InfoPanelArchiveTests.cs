using System.IO.Compression;
using InfoPanel.Models;
using InfoPanel.Persistence;
using Xunit;

namespace InfoPanel.Core.Tests.Persistence;

public class InfoPanelArchiveTests
{
    private static readonly byte[] LogoBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];

    private static (ProfileStore Store, Profile Profile, List<DisplayItem> Items) SeedStore(TempDirectory temp)
    {
        var store = new ProfileStore(new DataPaths(temp.Combine("data")));
        var profile = ProfileSerializerTests.ReadFixtureProfiles()[0];
        var items = ProfileSerializerTests.ReadFixtureItems();
        store.AddProfile(profile, items);
        var assets = store.GetProfileAssetsDirectory(profile.Guid);
        Directory.CreateDirectory(assets);
        File.WriteAllBytes(Path.Combine(assets, "logo.png"), LogoBytes);
        return (store, profile, store.LoadItems(profile));
    }

    [Fact]
    public void Archive_ImportExport_RoundTrip()
    {
        using var temp = new TempDirectory();
        var (store, profile, items) = SeedStore(temp);

        var archivePath = temp.Combine(InfoPanelArchive.FileNameFor(profile, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)));
        Assert.EndsWith("Desk_Overlay-1700000000.infopanel", archivePath);
        InfoPanelArchive.Export(profile, items, store, archivePath);

        using (var zip = ZipFile.OpenRead(archivePath))
            Assert.Equal(["Profile.xml", "DisplayItems.xml", "assets/logo.png"], zip.Entries.Select(e => e.FullName));

        // Add what a hostile or Windows-made archive could hold.
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            Write(zip, "assets/../../escaped.txt", "x");
            Write(zip, "assets/sub/../../../escaped2.txt", "x");
            Write(zip, "assets\\win.png", "win");
            zip.CreateEntry("assets/folder/");
            Write(zip, "unrelated.txt", "ignored");
        }

        var result = InfoPanelArchive.Import(archivePath, store);

        var imported = result.Profile;
        Assert.NotEqual(profile.Guid, imported.Guid);
        Assert.Equal("[Import] Desk Overlay", imported.Name);
        // Profile.xml carries what 1.x exported, not this machine's window placement.
        Assert.Equal((800, 480, "#FF102030", 1.5f, true, true), (imported.Width, imported.Height, imported.BackgroundColor, imported.FontScale, imported.ShowFps, imported.OpenGL));
        Assert.Null(imported.TargetWindow);

        Assert.Equal([profile.Guid, imported.Guid], store.LoadProfiles().Select(p => p.Guid));
        var importedItems = store.LoadItems(imported);
        Assert.Equal(items.Count, result.ItemCount);
        Assert.Equal(ItemTree.Walk(items).Select(i => (i.GetType(), i.Name)), ItemTree.Walk(importedItems).Select(i => (i.GetType(), i.Name)));

        var assets = store.GetProfileAssetsDirectory(imported.Guid);
        Assert.Equal(LogoBytes, File.ReadAllBytes(Path.Combine(assets, "logo.png")));
        Assert.Equal("win", File.ReadAllText(Path.Combine(assets, "win.png")));
        Assert.Equal(2, result.AssetCount);
        Assert.Equal(["assets/../../escaped.txt", "assets/sub/../../../escaped2.txt"], result.SkippedEntries.Select(s => s.Entry));
        Assert.Empty(Directory.GetFiles(temp.Path, "escaped*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Archive_Import_RequiresBothXmlEntries()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(new DataPaths(temp.Combine("data")));
        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            Write(zip, "Profile.xml", "<Profile />");
        archive.Position = 0;

        Assert.Throws<InvalidDataException>(() => InfoPanelArchive.Import(archive, store));
        Assert.Throws<InvalidDataException>(() => InfoPanelArchive.Import(new MemoryStream("not a zip"u8.ToArray()), store));
        Assert.Empty(store.LoadProfiles());
    }

    [Fact]
    public void Archive_Import_EnforcesLimits()
    {
        using var temp = new TempDirectory();
        var (store, profile, items) = SeedStore(temp);
        var archive = new MemoryStream();
        InfoPanelArchive.Export(profile, items, store, archive);

        archive.Position = 0;
        Assert.Throws<InvalidDataException>(() => InfoPanelArchive.Import(archive, store, maxEntries: 2, InfoPanelArchive.MaxExtractedBytes));

        // The XML fits, the asset pushes it over: nothing is left behind.
        archive.Position = 0;
        long xmlBytes;
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
            xmlBytes = zip.Entries.Where(e => e.FullName.EndsWith(".xml")).Sum(e => e.Length);
        archive.Position = 0;
        Assert.Throws<InvalidDataException>(() => InfoPanelArchive.Import(archive, store, InfoPanelArchive.MaxEntries, xmlBytes + 1));

        Assert.Single(store.LoadProfiles());
        Assert.Single(Directory.GetDirectories(store.Paths.AssetsDirectory));
    }

    [Fact]
    public void Archive_Import_ListWriteFails_LeavesNoItemsFileOrAssets()
    {
        using var temp = new TempDirectory();
        var (store, profile, items) = SeedStore(temp);
        var archive = new MemoryStream();
        InfoPanelArchive.Export(profile, items, store, archive);

        // Replace profiles.xml with a folder so the list write fails after the items and assets are written.
        var target = new ProfileStore(new DataPaths(temp.Combine("other")));
        Directory.CreateDirectory(target.Paths.ProfilesFile);
        archive.Position = 0;
        Assert.ThrowsAny<IOException>(() => InfoPanelArchive.Import(archive, target));

        Assert.Empty(Directory.GetFiles(target.Paths.ProfilesDirectory));
        Assert.Empty(Directory.GetDirectories(target.Paths.AssetsDirectory));
    }

    [Theory]
    [InlineData("logo.png", true)]
    [InlineData("sub/logo.png", true)]
    [InlineData("../logo.png", false)]
    [InlineData("sub/../../logo.png", false)]
    [InlineData("./logo.png", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/Windows/win.ini", false)]
    [InlineData("sub//logo.png", false)]
    public void ResolveAssetPath_StaysInsideAssets(string relative, bool allowed)
    {
        var assets = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "assets-root"));
        var resolved = InfoPanelArchive.ResolveAssetPath(assets, relative);

        Assert.Equal(allowed, resolved != null);
        if (resolved != null)
            Assert.StartsWith(assets + Path.DirectorySeparatorChar, resolved);
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}
