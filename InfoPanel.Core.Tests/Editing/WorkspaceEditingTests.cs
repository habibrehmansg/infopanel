using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Editing;
using InfoPanel.Models;
using InfoPanel.Persistence;
using System.Text.Json.Nodes;
using Xunit;

namespace InfoPanel.Core.Tests.Editing;

public sealed class WorkspaceEditingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private DataPaths Paths => new(_temp.Path);

    private Workspace NewWorkspace() => new(new ProfileStore(Paths));

    private static JsonObject Set(string json) => JsonNode.Parse(json)!.AsObject();

    private List<Profile> ProfilesOnDisk()
    {
        using var stream = File.OpenRead(Paths.ProfilesFile);
        return ProfileSerializer.ReadProfiles(stream);
    }

    [Fact]
    public void Create_SavesImmediately_AndValidates()
    {
        var workspace = NewWorkspace();

        var summary = workspace.Create("  Panel  ", 480, 320);

        Assert.Equal(("Panel", 480, 320, 1L, false), (summary.Name, summary.Width, summary.Height, summary.Revision, summary.Dirty));
        Assert.Equal(summary.Id, Assert.Single(ProfilesOnDisk()).Guid);
        Assert.True(File.Exists(Paths.ProfileItemsFile(summary.Id)));
        Assert.Empty(workspace.Open(summary.Id)!.Items);

        Assert.Equal("name", Assert.Throws<PatchException>(() => workspace.Create(" ", 1, 1)).Key);
        Assert.Equal("name", Assert.Throws<PatchException>(() => workspace.Create(null, 1, 1)).Key);
        Assert.Equal("width", Assert.Throws<PatchException>(() => workspace.Create("x", 0, 1)).Key);
        Assert.Equal("height", Assert.Throws<PatchException>(() => workspace.Create("x", 1, Workspace.MaxDimension + 1)).Key);
        Assert.Single(workspace.ListProfiles());
    }

    [Fact]
    public void Save_WritesItemsWithIds_AndOnlyThatProfilesEntry()
    {
        var workspace = NewWorkspace();
        var a = workspace.Create("A", 100, 100).Id;
        var b = workspace.Create("B", 100, 100).Id;
        var docA = workspace.Open(a)!;
        var docB = workspace.Open(b)!;

        docA.PatchProfile(Set("""{"name":"A unsaved"}"""));
        docB.PatchProfile(Set("""{"name":"B saved"}"""));
        var added = docB.AddItem("text", set: Set("""{"fontSize":48}""")).Items![0];

        var result = workspace.Save(b)!;

        Assert.False(result.Dirty);
        Assert.Equal(["A", "B saved"], ProfilesOnDisk().Select(p => p.Name));
        var xml = File.ReadAllText(Paths.ProfileItemsFile(b));
        Assert.Contains($"<Guid>{added.Id}</Guid>", xml);
        Assert.Contains("<FontSize>48</FontSize>", xml);
        Assert.True(docA.Dirty);
        Assert.Equal(["A unsaved", "B saved"], workspace.ListProfiles().Select(s => s.Name));

        // A fresh workspace reads what was saved, ids included.
        var reopened = new Workspace(new ProfileStore(Paths));
        Assert.Equal(added.Id, reopened.Open(b)!.Items.Single().Guid.ToString());
        Assert.Equal("A", reopened.Open(a)!.Profile.Name);
        Assert.Null(workspace.Save(Guid.NewGuid()));
    }

    [Fact]
    public void Duplicate_CopiesTheCurrentState_WithFreshIds_AndAssets()
    {
        var workspace = NewWorkspace();
        var id = workspace.Create("Src", 100, 100).Id;
        var doc = workspace.Open(id)!;
        doc.AddItem("group");
        doc.AddItem("text", doc.Items[0].Guid);
        var assets = Paths.ProfileAssetsDirectory(id);
        Directory.CreateDirectory(Path.Combine(assets, "sub"));
        File.WriteAllText(Path.Combine(assets, "sub", "a.txt"), "asset");

        var copy = workspace.Duplicate(id)!;

        Assert.Equal("Src copy", copy.Name);
        Assert.NotEqual(id, copy.Id);
        var copyDoc = workspace.Open(copy.Id)!;
        Assert.Equal(Names(doc.Items), Names(copyDoc.Items));
        Assert.Empty(ItemTree.Walk(copyDoc.Items).Select(i => i.Guid).Intersect(ItemTree.Walk(doc.Items).Select(i => i.Guid)));
        Assert.Equal("asset", File.ReadAllText(Path.Combine(Paths.ProfileAssetsDirectory(copy.Id), "sub", "a.txt")));
        Assert.False(copyDoc.Dirty);
        Assert.Equal(["Src", "Src copy"], ProfilesOnDisk().Select(p => p.Name));
        Assert.Null(workspace.Duplicate(Guid.NewGuid()));
    }

    private static string Names(IEnumerable<DisplayItem> items) =>
        string.Join(" ", items.Select(i => i is GroupDisplayItem g ? $"{g.Name}[{Names(g.DisplayItems)}]" : i.Name));

    [Fact]
    public void Delete_IsIrreversible()
    {
        var workspace = NewWorkspace();
        var keep = workspace.Create("Keep", 10, 10).Id;
        var id = workspace.Create("Gone", 10, 10).Id;
        workspace.Open(id);
        Directory.CreateDirectory(Paths.ProfileAssetsDirectory(id));

        Assert.True(workspace.Delete(id));

        Assert.Null(workspace.Open(id));
        Assert.False(File.Exists(Paths.ProfileItemsFile(id)));
        Assert.False(Directory.Exists(Paths.ProfileAssetsDirectory(id)));
        Assert.Equal(keep, Assert.Single(ProfilesOnDisk()).Guid);
        Assert.False(workspace.Delete(id));
    }

    [Fact]
    public void ExportThenImport_GivesANewProfileWithTheSameItems()
    {
        var workspace = NewWorkspace();
        var id = workspace.Create("Round trip", 300, 200).Id;
        var doc = workspace.Open(id)!;
        doc.AddItem("text", set: Set("""{"name":"Unsaved text"}"""));
        doc.AddItem("donut");

        using var archive = new MemoryStream();
        var exported = workspace.Export(id, archive)!;
        archive.Position = 0;
        var (summary, result) = workspace.Import(archive);

        Assert.Equal("Round trip", exported.Name);
        Assert.Equal("[Import] Round trip", summary.Name);
        Assert.Equal(2, result.ItemCount);
        Assert.NotEqual(id, summary.Id);
        var imported = workspace.Open(summary.Id)!;
        Assert.Equal(doc.Snapshot.ItemsXml, imported.Snapshot.ItemsXml); // the current (unsaved) state, ids kept
        Assert.Equal(2, workspace.ListProfiles().Count);
        Assert.Null(workspace.Export(Guid.NewGuid(), new MemoryStream()));
        Assert.Throws<InvalidDataException>(() => workspace.Import(new MemoryStream([1, 2, 3])));
        Assert.Equal(2, ProfilesOnDisk().Count);
    }

    [Fact]
    public void ProfilesChanged_HasARisingVersion_ForListAndSummaryChanges()
    {
        var workspace = NewWorkspace();
        var versions = new List<long>();
        workspace.ProfilesChanged += (_, e) => versions.Add(e.Version);

        var id = workspace.Create("A", 10, 10).Id;          // 1
        var doc = workspace.Open(id)!;
        var item = doc.AddItem("text").Items![0].Id;          // 2: dirty
        doc.PatchItems([new ItemPatch(Guid.Parse(item), Set("""{"x":5}"""))]); // still dirty, same name: nothing
        doc.PatchProfile(Set("""{"name":"B"}"""));             // 3
        workspace.Save(id);                                    // 4: clean
        workspace.Duplicate(id);                               // 5
        workspace.Delete(id);                                  // 6
        doc.PatchProfile(Set("""{"name":"closed"}"""));        // a deleted document no longer reports

        Assert.Equal([1, 2, 3, 4, 5, 6], versions);
        Assert.Equal(6, workspace.Version);
    }

    [Fact]
    public async Task ConcurrentEdits_AreSerialised()
    {
        var workspace = NewWorkspace();
        var id = workspace.Create("Busy", 10, 10).Id;
        var doc = workspace.Open(id)!;
        var item = Guid.Parse(doc.AddItem("text").Items![0].Id);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                doc.PatchItems([new ItemPatch(item, Set($$"""{"x":{{t * 1000 + i}}}"""))]);
                workspace.ListProfiles();
                if (i % 10 == 0)
                    workspace.Save(id);
            }
        })));

        Assert.Equal(2 + 8 * 25, doc.Revision);
    }

    [Fact]
    public void DuplicateAndExport_OfAProfileDeletedMidway_AreNotFound_AndAddNothing()
    {
        var workspace = NewWorkspace();
        var keep = workspace.Create("Keep", 10, 10).Id;
        var a = workspace.Create("A", 10, 10).Id;
        var b = workspace.Create("B", 10, 10).Id;
        Directory.CreateDirectory(Paths.ProfileAssetsDirectory(a));
        File.WriteAllText(Path.Combine(Paths.ProfileAssetsDirectory(a), "a.txt"), "asset");

        // Deleted after the document was captured, before the copy: the copy would have had no assets.
        workspace.AfterCapture = () => workspace.Delete(a);
        Assert.Null(workspace.Duplicate(a));

        workspace.AfterCapture = () => workspace.Delete(b);
        using var archive = new MemoryStream();
        Assert.Null(workspace.Export(b, archive));
        Assert.Equal(0, archive.Length);

        workspace.AfterCapture = null;
        Assert.Equal([keep], workspace.ListProfiles().Select(p => p.Id));
        Assert.Equal([keep], ProfilesOnDisk().Select(p => p.Guid));
        Assert.Equal([keep], Directory.GetFiles(Path.GetDirectoryName(Paths.ProfileItemsFile(keep))!).Select(f => Guid.Parse(Path.GetFileNameWithoutExtension(f))));
    }

    [Fact]
    public void Save_WhoseProfilesXmlWriteFails_LeavesTheSavedListAsItWas()
    {
        var workspace = NewWorkspace();
        var a = workspace.Create("A", 100, 100).Id;
        var b = workspace.Create("B", 100, 100).Id;
        var docA = workspace.Open(a)!;
        workspace.Open(b);
        docA.PatchProfile(Set("""{"name":"A renamed"}"""));

        // A directory where profiles.xml goes makes the write fail.
        File.Delete(Paths.ProfilesFile);
        Directory.CreateDirectory(Paths.ProfilesFile);
        Assert.ThrowsAny<IOException>(() => workspace.Save(a));
        Directory.Delete(Paths.ProfilesFile);
        Assert.True(docA.Dirty);

        // Saving another profile writes A as last saved, not its failed save's name.
        workspace.Save(b);
        Assert.Equal(["A", "B"], ProfilesOnDisk().Select(p => p.Name));
    }

    [Fact]
    public void Create_RefusesNamesXmlCantCarry()
    {
        var workspace = NewWorkspace();
        Assert.Equal("name", Assert.Throws<PatchException>(() => workspace.Create("bad\u0000name", 10, 10)).Key);
        Assert.Empty(workspace.ListProfiles());
    }
}
