using System.IO.Compression;
using InfoPanel.Models;
using InfoPanel.Persistence;
using Xunit;

namespace InfoPanel.Core.Tests.Persistence;

/// <summary>
/// The 1.4 <c>.infopanel</c> export the e2e archive is written with. 1.x archives are read by the 1.x import
/// (<c>InfoPanel.Core.Tests.Import</c>: <c>V1ArchiveTests</c>, <c>ArchiveReaderTests</c>), which installs nothing.
/// </summary>
public class InfoPanelArchiveTests
{
    private static readonly byte[] LogoBytes = ImportKitAsset("logo.png");

    private static byte[] ImportKitAsset(string name) => Import.ImportKit.Asset(name);

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
    public void Archive_Export_ReadsBackThroughThe1xImport()
    {
        using var temp = new TempDirectory();
        var (store, profile, items) = SeedStore(temp);

        var archivePath = temp.Combine(InfoPanelArchive.FileNameFor(profile, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)));
        Assert.EndsWith("Desk_Overlay-1700000000.infopanel", archivePath);
        InfoPanelArchive.Export(profile, items, store, archivePath);

        using (var zip = ZipFile.OpenRead(archivePath))
            Assert.Equal(["Profile.xml", "DisplayItems.xml", "assets/logo.png"], zip.Entries.Select(e => e.FullName));

        using var kit = new Import.ImportKit();
        var envelope = kit.ReadArchive(File.ReadAllBytes(archivePath));

        // Profile.xml carries what 1.x exported, not this machine's window placement.
        Assert.Equal((800, 480, "#FF102030", 1.5f, true, true),
            ((int)envelope.Profile["width"]!, (int)envelope.Profile["height"]!, (string)envelope.Profile["backgroundColor"]!,
                (float)envelope.Profile["fontScale"]!, (bool)envelope.Profile["showFps"]!, (bool)envelope.Profile["openGL"]!));
        Assert.Null(envelope.TargetWindow);
        Assert.Equal(ItemTree.Walk(items).Count(), Walk(envelope.Items).Count());
        Assert.Equal(ItemTree.Walk(items).Select(i => i.Name), Walk(envelope.Items).Select(n => (string?)n.Props["name"]));
        Assert.True(kit.Stored(LogoBytes));

        static IEnumerable<InfoPanel.Editing.ItemNode> Walk(IEnumerable<InfoPanel.Editing.ItemNode> nodes) =>
            nodes.SelectMany(n => new[] { n }.Concat(Walk(n.Children ?? [])).Concat(Walk(n.Images ?? [])));
    }
}
