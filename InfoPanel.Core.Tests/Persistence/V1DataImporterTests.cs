using System.Xml;
using InfoPanel.Models;
using InfoPanel.Persistence;
using Xunit;

namespace InfoPanel.Core.Tests.Persistence;

public class V1DataImporterTests
{
    [Fact]
    public void V1DataImporter_CopiesAssets()
    {
        using var temp = new TempDirectory();
        var v1 = new DataPaths(temp.Combine("v1"));
        var desk = FixtureGenerator.DeskProfileGuid;
        var panel = FixtureGenerator.PanelProfileGuid;
        var missing = new Profile("Never saved", 320, 240);

        // A 1.x folder: the frozen fixtures as written by 1.x, plus a profile whose items file was never written.
        var profiles = ProfileSerializerTests.ReadFixtureProfiles();
        profiles.Add(missing);
        Directory.CreateDirectory(v1.ProfilesDirectory);
        using (var writer = XmlWriter.Create(v1.ProfilesFile, V1Format.WriterSettings()))
            V1Format.ProfilesSerializer.Serialize(writer, profiles);
        File.Copy(ProfileSerializerTests.FixturePath(V1Format.ItemsFixture), v1.ProfileItemsFile(desk));
        File.Copy(ProfileSerializerTests.FixturePath(V1Format.ItemsFixture), v1.ProfileItemsFile(panel));
        Directory.CreateDirectory(Path.Combine(v1.ProfileAssetsDirectory(desk), "icons"));
        File.WriteAllText(Path.Combine(v1.ProfileAssetsDirectory(desk), "logo.png"), "logo");
        File.WriteAllText(Path.Combine(v1.ProfileAssetsDirectory(desk), "icons", "fan.svg"), "<svg/>");

        // The 2.0 store already has the desk profile's Guid.
        var store = new ProfileStore(new DataPaths(temp.Combine("v2")));
        var existing = new Profile("Existing", 100, 100) { Guid = desk };
        store.AddProfile(existing, []);

        var summary = V1DataImporter.ImportFolder(v1.Root, store);

        Assert.Equal(["Desk Overlay", "USB Panel"], summary.Imported.Select(p => p.Name));
        var deskImport = summary.Imported[0];
        Assert.True(deskImport.GuidRegenerated);
        Assert.Equal(desk, deskImport.OriginalGuid);
        Assert.NotEqual(desk, deskImport.Guid);
        Assert.Equal(2, deskImport.AssetCount);
        var panelImport = summary.Imported[1];
        Assert.False(panelImport.GuidRegenerated);
        Assert.Equal(panel, panelImport.Guid);
        Assert.Equal([deskImport], summary.Renamed);

        var skipped = Assert.Single(summary.Skipped);
        Assert.Equal((missing.Guid, "items file missing"), (skipped.Guid, skipped.Reason));

        Assert.Equal([desk, deskImport.Guid, panel], store.LoadProfiles().Select(p => p.Guid));
        var deskAssets = store.GetProfileAssetsDirectory(deskImport.Guid);
        Assert.Equal("logo", File.ReadAllText(Path.Combine(deskAssets, "logo.png")));
        Assert.Equal("<svg/>", File.ReadAllText(Path.Combine(deskAssets, "icons", "fan.svg")));
        Assert.False(Directory.Exists(store.GetProfileAssetsDirectory(desk)));

        var deskProfile = store.LoadProfiles()[1];
        Assert.Equal("Desk Overlay", deskProfile.Name);
        var items = store.LoadItems(deskProfile);
        Assert.Equal(14, ItemTree.Walk(items).Select(i => i.GetType()).Distinct().Count());
        Assert.Equal(deskImport.ItemCount, items.Count);

        // The 1.x folder is only read.
        Assert.True(File.Exists(Path.Combine(v1.ProfileAssetsDirectory(desk), "logo.png")));
    }

    [Fact]
    public void V1DataImporter_ReportsCorruptItemsAndContinues()
    {
        using var temp = new TempDirectory();
        var v1 = new DataPaths(temp.Combine("v1"));
        Directory.CreateDirectory(v1.ProfilesDirectory);
        File.Copy(ProfileSerializerTests.FixturePath(V1Format.ProfilesFixture), v1.ProfilesFile);
        File.WriteAllText(v1.ProfileItemsFile(FixtureGenerator.DeskProfileGuid), "<ArrayOfDisplayItem><DisplayItem");
        File.Copy(ProfileSerializerTests.FixturePath(V1Format.ItemsFixture), v1.ProfileItemsFile(FixtureGenerator.PanelProfileGuid));

        var store = new ProfileStore(new DataPaths(temp.Combine("v2")));
        var summary = V1DataImporter.ImportFolder(v1.Root, store);

        var skipped = Assert.Single(summary.Skipped);
        Assert.Equal(FixtureGenerator.DeskProfileGuid, skipped.Guid);
        Assert.StartsWith("items file unreadable", skipped.Reason);
        Assert.Equal([FixtureGenerator.PanelProfileGuid], summary.Imported.Select(p => p.Guid));
        Assert.Equal([FixtureGenerator.PanelProfileGuid], store.LoadProfiles().Select(p => p.Guid));
    }

    /// <summary>A 1.x folder with the fixture profiles (desk, panel), the fixture items for both, and two desk assets.</summary>
    private static DataPaths CreateV1Folder(string root)
    {
        var v1 = new DataPaths(root);
        Directory.CreateDirectory(v1.ProfilesDirectory);
        File.Copy(ProfileSerializerTests.FixturePath(V1Format.ProfilesFixture), v1.ProfilesFile);
        File.Copy(ProfileSerializerTests.FixturePath(V1Format.ItemsFixture), v1.ProfileItemsFile(FixtureGenerator.DeskProfileGuid));
        File.Copy(ProfileSerializerTests.FixturePath(V1Format.ItemsFixture), v1.ProfileItemsFile(FixtureGenerator.PanelProfileGuid));
        var deskAssets = v1.ProfileAssetsDirectory(FixtureGenerator.DeskProfileGuid);
        Directory.CreateDirectory(Path.Combine(deskAssets, "icons"));
        File.WriteAllText(Path.Combine(deskAssets, "logo.png"), "logo");
        File.WriteAllText(Path.Combine(deskAssets, "icons", "fan.svg"), "<svg/>");
        return v1;
    }

    [Fact]
    public async Task V1DataImporter_ConcurrentImports_KeepEveryProfilesAssets()
    {
        using var temp = new TempDirectory();
        var v1 = CreateV1Folder(temp.Combine("v1"));
        var root = temp.Combine("v2");

        // Four imports of the same folder at once, each through its own store: all want the same two Guids.
        using var start = new ManualResetEventSlim();
        var imports = Enumerable.Range(0, 4)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                start.Wait();
                return V1DataImporter.ImportFolder(v1.Root, new ProfileStore(new DataPaths(root)));
            }, TaskCreationOptions.LongRunning))
            .ToArray();
        start.Set();
        var summaries = await Task.WhenAll(imports);

        Assert.All(summaries, summary => Assert.Empty(summary.Skipped));
        var desks = summaries.Select(summary => summary.Imported.Single(p => p.OriginalGuid == FixtureGenerator.DeskProfileGuid)).ToList();
        Assert.Single(desks, p => !p.GuidRegenerated);

        var store = new ProfileStore(new DataPaths(root));
        var listed = store.LoadProfiles().Select(p => p.Guid).ToList();
        Assert.Equal(8, listed.Distinct().Count());
        Assert.Equal(summaries.SelectMany(summary => summary.Imported).Select(p => p.Guid).Order(), listed.Order());
        foreach (var desk in desks)
        {
            Assert.Equal("logo", File.ReadAllText(Path.Combine(store.GetProfileAssetsDirectory(desk.Guid), "logo.png")));
            Assert.Equal("<svg/>", File.ReadAllText(Path.Combine(store.GetProfileAssetsDirectory(desk.Guid), "icons", "fan.svg")));
        }
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("")]
    [InlineData("v1/assets")]
    [InlineData("v1/assets/v2")]
    [InlineData("v1/profiles/v2")]
    public void V1DataImporter_RejectsStoreOverlappingThe1xData(string storeFolder)
    {
        using var temp = new TempDirectory();
        var v1 = CreateV1Folder(temp.Combine("v1"));
        var store = new ProfileStore(new DataPaths(Path.Combine(temp.Path, storeFolder)));

        Assert.Throws<ArgumentException>(() => V1DataImporter.ImportFolder(v1.Root, store));

        Assert.Equal(["profiles.xml"], Directory.GetFiles(v1.Root).Select(Path.GetFileName));
        Assert.Equal(2, Directory.GetFiles(v1.ProfilesDirectory).Length);
        Assert.Single(Directory.GetDirectories(v1.AssetsDirectory));
    }

    [Fact]
    public void V1DataImporter_AllowsStoreBesideThe1xDataInItsFolder()
    {
        // The default layout: 2.0 in InfoPanel/v2, inside 1.x's InfoPanel/ (on Windows).
        using var temp = new TempDirectory();
        var v1 = CreateV1Folder(temp.Combine("InfoPanel"));
        var store = new ProfileStore(new DataPaths(temp.Combine("InfoPanel", "v2")));
        var v1ProfilesBefore = File.ReadAllBytes(v1.ProfilesFile);

        var summary = V1DataImporter.ImportFolder(v1.Root, store);

        Assert.Equal(2, summary.Imported.Count);
        Assert.Equal(v1ProfilesBefore, File.ReadAllBytes(v1.ProfilesFile));
        Assert.Equal(2, Directory.GetFiles(v1.ProfilesDirectory).Length);
        Assert.Single(Directory.GetDirectories(v1.AssetsDirectory));
    }

    [Fact]
    public void V1DataImporter_AnyFailure_RollsBackReadOnlyCopiesAndContinues()
    {
        using var temp = new TempDirectory();
        var v1 = CreateV1Folder(temp.Combine("v1"));
        var store = new ProfileStore(new DataPaths(temp.Combine("v2")));
        var deskSource = v1.ProfileAssetsDirectory(FixtureGenerator.DeskProfileGuid);

        // The desk copy leaves a read-only file (as copied 1.x assets can be on Windows), then fails with a non-IO error.
        var summary = V1DataImporter.ImportFolder(v1.Root, store, (source, destination) =>
        {
            if (source != deskSource)
                return 0;
            Directory.CreateDirectory(destination);
            var copied = Path.Combine(destination, "logo.png");
            File.Copy(Path.Combine(source, "logo.png"), copied);
            File.SetAttributes(copied, FileAttributes.ReadOnly);
            throw new NotSupportedException("simulated");
        });

        var skipped = Assert.Single(summary.Skipped);
        Assert.Equal((FixtureGenerator.DeskProfileGuid, "copy failed: simulated"), (skipped.Guid, skipped.Reason));
        Assert.Equal([FixtureGenerator.PanelProfileGuid], summary.Imported.Select(p => p.Guid));
        Assert.False(Directory.Exists(store.GetProfileAssetsDirectory(FixtureGenerator.DeskProfileGuid)));
        Assert.False(File.Exists(store.Paths.ProfileItemsFile(FixtureGenerator.DeskProfileGuid)));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void V1DataImporter_CleanupFailure_IsReportedAndTheRestContinue()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Simulates an undeletable folder with Unix permissions.");

        using var temp = new TempDirectory();
        var v1 = CreateV1Folder(temp.Combine("v1"));
        var store = new ProfileStore(new DataPaths(temp.Combine("v2")));
        var deskSource = v1.ProfileAssetsDirectory(FixtureGenerator.DeskProfileGuid);
        string? locked = null;

        try
        {
            var summary = V1DataImporter.ImportFolder(v1.Root, store, (source, destination) =>
            {
                if (source != deskSource)
                    return 0;
                locked = Path.Combine(destination, "locked");
                Directory.CreateDirectory(locked);
                File.WriteAllText(Path.Combine(locked, "logo.png"), "logo");
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute); // its file can't be removed
                throw new IOException("simulated");
            });

            var skipped = Assert.Single(summary.Skipped);
            Assert.Equal(FixtureGenerator.DeskProfileGuid, skipped.Guid);
            Assert.StartsWith("copy failed: simulated; cleanup incomplete: ", skipped.Reason);
            Assert.Equal([FixtureGenerator.PanelProfileGuid], summary.Imported.Select(p => p.Guid));
            Assert.Equal([FixtureGenerator.PanelProfileGuid], store.LoadProfiles().Select(p => p.Guid));
            Assert.False(File.Exists(store.Paths.ProfileItemsFile(FixtureGenerator.DeskProfileGuid)));
        }
        finally
        {
            if (locked != null && Directory.Exists(locked))
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void V1DataImporter_NilItems_AreDropped()
    {
        using var temp = new TempDirectory();
        var v1 = CreateV1Folder(temp.Combine("v1"));
        File.WriteAllText(v1.ProfileItemsFile(FixtureGenerator.DeskProfileGuid), """
            <?xml version="1.0" encoding="utf-8"?>
            <ArrayOfDisplayItem xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <DisplayItem xsi:nil="true" />
              <DisplayItem xsi:type="TextDisplayItem"><Name>Label</Name></DisplayItem>
            </ArrayOfDisplayItem>
            """);
        var store = new ProfileStore(new DataPaths(temp.Combine("v2")));

        var summary = V1DataImporter.ImportFolder(v1.Root, store);

        Assert.Empty(summary.Skipped);
        Assert.Equal(1, summary.Imported[0].ItemCount);
        Assert.Equal(["Label"], store.LoadItems(store.LoadProfiles()[0]).Select(i => i.Name));
    }

    [Fact]
    public void V1DataImporter_ListWriteFails_LeavesNoItemsFileOrAssets()
    {
        using var temp = new TempDirectory();
        var v1 = CreateV1Folder(temp.Combine("v1"));
        var store = new ProfileStore(new DataPaths(temp.Combine("v2")));
        Directory.CreateDirectory(store.Paths.ProfilesFile); // a folder where profiles.xml goes: the list write fails

        var summary = V1DataImporter.ImportFolder(v1.Root, store);

        Assert.Empty(summary.Imported);
        Assert.Equal(2, summary.Skipped.Count);
        Assert.Empty(Directory.GetFiles(store.Paths.ProfilesDirectory));
        Assert.Empty(Directory.GetDirectories(store.Paths.AssetsDirectory));
    }

    [Fact]
    public void V1DataImporter_MissingProfileList_Throws()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(new DataPaths(temp.Combine("v2")));

        Assert.Throws<FileNotFoundException>(() => V1DataImporter.ImportFolder(temp.Combine("v1"), store));
    }
}
