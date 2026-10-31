using InfoPanel.Models;
using InfoPanel.Persistence;
using Xunit;

namespace InfoPanel.Core.Tests.Persistence;

public class ProfileStoreTests
{
    /// <summary>Not one of the 14 serializer types, so writing it fails part-way.</summary>
    public sealed class UnsupportedItem : TextDisplayItem;

    [Fact]
    public void DataPaths_Layout()
    {
        var paths = new DataPaths("/data/InfoPanel/v2");
        var id = new Guid("6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7");

        Assert.Equal(Path.GetFullPath("/data/InfoPanel/v2/profiles.xml"), paths.ProfilesFile);
        Assert.Equal(Path.GetFullPath("/data/InfoPanel/v2/profiles/6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7.xml"), paths.ProfileItemsFile(id));
        Assert.Equal(Path.GetFullPath("/data/InfoPanel/v2/assets/6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7"), paths.ProfileAssetsDirectory(id));
        Assert.Equal(Path.GetFullPath("/data/InfoPanel/v2/fonts"), paths.FontsDirectory);
        Assert.Equal(Path.GetFullPath("/data/InfoPanel/v2/logs"), paths.LogsDirectory);
        Assert.Equal(Path.GetFullPath("/data/InfoPanel/v2/runtime.json"), paths.RuntimeFile);
        Assert.Equal(Path.GetFullPath("/data/InfoPanel/v2/engine.lock"), paths.EngineLockFile);

        var root = DataPaths.DefaultRoot().Root;
        Assert.EndsWith(Path.Combine("InfoPanel", "v2"), root);
        if (OperatingSystem.IsMacOS())
            Assert.EndsWith(Path.Combine("Library", "Application Support", "InfoPanel", "v2"), root);
    }

    [Fact]
    public void Store_LoadsAndSavesProfilesAndItems()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(new DataPaths(temp.Path));

        Assert.Empty(store.LoadProfiles());

        var profiles = ProfileSerializerTests.ReadFixtureProfiles();
        store.SaveProfiles(profiles);
        var items = ProfileSerializerTests.ReadFixtureItems();
        store.SaveItems(profiles[0].Guid, items);

        var loadedProfiles = store.LoadProfiles();
        Assert.Equal(profiles.Select(p => p.Guid), loadedProfiles.Select(p => p.Guid));

        var loadedItems = store.LoadItems(loadedProfiles[0]);
        Assert.Equal(ItemTree.Walk(items).Select(i => i.Guid), ItemTree.Walk(loadedItems).Select(i => i.Guid));
        Assert.All(ItemTree.Walk(loadedItems), i => Assert.Same(loadedProfiles[0], i.Profile));

        // A profile without an items file has no items, as in 1.x.
        Assert.Empty(store.LoadItems(loadedProfiles[1]));
        Assert.Equal(Path.Combine(temp.Path, "assets", profiles[0].Guid.ToString()), store.GetProfileAssetsDirectory(profiles[0].Guid));
    }

    [Fact]
    public void AtomicWrite_ReplacesTargetAndLeavesNoTempFile()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(new DataPaths(temp.Path));
        var profile = new Profile("Atomic", 400, 200);
        var file = store.Paths.ProfileItemsFile(profile.Guid);

        store.SaveItems(profile.Guid, [new TextDisplayItem("first", profile)]);
        store.SaveItems(profile.Guid, [new TextDisplayItem("second", profile)]);

        Assert.Equal(["second"], store.LoadItems(profile).Select(i => i.Name));
        Assert.Equal([Path.GetFileName(file)], Directory.GetFiles(store.Paths.ProfilesDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public void AtomicWrite_FailureKeepsPreviousFile()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(new DataPaths(temp.Path));
        var profile = new Profile("Atomic", 400, 200);
        var file = store.Paths.ProfileItemsFile(profile.Guid);

        store.SaveItems(profile.Guid, [new TextDisplayItem("kept", profile)]);
        var before = File.ReadAllBytes(file);

        Assert.Throws<InvalidOperationException>(() =>
            store.SaveItems(profile.Guid, [new TextDisplayItem("lost", profile), new UnsupportedItem()]));

        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Empty(Directory.GetFiles(store.Paths.ProfilesDirectory, "*.tmp"));
        Assert.Equal(["kept"], store.LoadItems(profile).Select(i => i.Name));
    }

    [Fact]
    public void CorruptFiles_ThrowAndStayUntouched()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(new DataPaths(temp.Path));
        var profile = new Profile("Corrupt", 400, 200);

        Directory.CreateDirectory(store.Paths.ProfilesDirectory);
        File.WriteAllText(store.Paths.ProfilesFile, "<ArrayOfProfile><Profile>");
        File.WriteAllText(store.Paths.ProfileItemsFile(profile.Guid), "garbage");

        var ex = Assert.Throws<InvalidDataException>(() => store.LoadProfiles());
        Assert.Contains("profiles.xml", ex.Message);
        Assert.Throws<InvalidDataException>(() => store.LoadItems(profile));
        Assert.Equal("<ArrayOfProfile><Profile>", File.ReadAllText(store.Paths.ProfilesFile));
        Assert.Equal("garbage", File.ReadAllText(store.Paths.ProfileItemsFile(profile.Guid)));
    }

    [Fact]
    public void AddProfile_RejectsDuplicateGuid()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(new DataPaths(temp.Path));
        var profile = new Profile("One", 400, 200);

        store.AddProfile(profile, [new TextDisplayItem("item", profile)]);

        Assert.True(store.ContainsProfile(profile.Guid));
        Assert.Throws<InvalidOperationException>(() => store.AddProfile(profile, []));
        Assert.Single(store.LoadProfiles());
    }

    [Fact]
    public void Stores_OnTheSameFolder_ShareOneLock()
    {
        using var temp = new TempDirectory();
        // Two instances on one folder, spelled differently, as an importer and the engine might hold them.
        var first = new ProfileStore(new DataPaths(temp.Path));
        var second = new ProfileStore(new DataPaths(temp.Path + Path.DirectorySeparatorChar));
        var added = new System.Collections.Concurrent.ConcurrentBag<Guid>();

        Parallel.For(0, 40, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var profile = new Profile("P" + i, 100, 100);
            (i % 2 == 0 ? first : second).AddProfile(profile, [new TextDisplayItem("item", profile)]);
            added.Add(profile.Guid);
        });

        Assert.Equal(added.Order(), first.LoadProfiles().Select(p => p.Guid).Order());
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void AddProfile_ListWriteFails_RemovesItsItemsFile()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(new DataPaths(temp.Path));
        var profile = new Profile("One", 400, 200);
        Directory.CreateDirectory(store.Paths.ProfilesFile); // a folder where profiles.xml goes: the list write fails

        // The list write's failure propagates as the OS reports it (Unix: IOException; Windows: UnauthorizedAccessException
        // for a move onto a folder), and the items file it wrote is gone. Any other error didn't come from the list write.
        var error = Record.Exception(() => store.AddProfile(profile, [new TextDisplayItem("item", profile)]));
        Assert.True(error is IOException or UnauthorizedAccessException, $"Expected the list write's error, got {error?.ToString() ?? "none"}");

        Assert.False(File.Exists(store.Paths.ProfileItemsFile(profile.Guid)));
    }

    [Fact]
    public void DeleteProfile_RemovesOnlyThatProfile()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(new DataPaths(temp.Path));
        var keep = new Profile("Keep", 400, 200);
        var delete = new Profile("Delete", 400, 200);
        store.AddProfile(keep, [new TextDisplayItem("k", keep)]);
        store.AddProfile(delete, [new TextDisplayItem("d", delete)]);
        foreach (var p in new[] { keep, delete })
        {
            Directory.CreateDirectory(Path.Combine(store.GetProfileAssetsDirectory(p.Guid), "sub"));
            File.WriteAllText(Path.Combine(store.GetProfileAssetsDirectory(p.Guid), "sub", "logo.png"), "png");
        }

        // Files no profile owns: 1.x swept these on every save; 2.0 leaves them.
        var orphanItems = store.Paths.ProfileItemsFile(Guid.NewGuid());
        File.WriteAllText(orphanItems, "<ArrayOfDisplayItem />");
        var orphanAssets = store.GetProfileAssetsDirectory(Guid.NewGuid());
        Directory.CreateDirectory(orphanAssets);

        store.DeleteProfile(delete.Guid);

        Assert.Equal([keep.Guid], store.LoadProfiles().Select(p => p.Guid));
        Assert.False(File.Exists(store.Paths.ProfileItemsFile(delete.Guid)));
        Assert.False(Directory.Exists(store.GetProfileAssetsDirectory(delete.Guid)));
        Assert.True(File.Exists(store.Paths.ProfileItemsFile(keep.Guid)));
        Assert.True(File.Exists(Path.Combine(store.GetProfileAssetsDirectory(keep.Guid), "sub", "logo.png")));
        Assert.True(File.Exists(orphanItems));
        Assert.True(Directory.Exists(orphanAssets));

        // Deleting again, or an unknown profile, is a no-op.
        store.DeleteProfile(delete.Guid);
        Assert.Single(store.LoadProfiles());
    }
}
