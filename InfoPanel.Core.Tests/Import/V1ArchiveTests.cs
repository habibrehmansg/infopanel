using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Editing;
using InfoPanel.Import;
using InfoPanel.Models;
using InfoPanel.Persistence;
using Xunit;
using static InfoPanel.Core.Tests.Import.ImportKit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// A 1.x <c>.infopanel</c> read into an envelope: what 1.4.x accepted (each pin names its 1.4.x source), and what is
/// refused as a whole.
/// </summary>
public sealed class V1ArchiveTests : IDisposable
{
    private readonly ImportKit _kit = new();

    public void Dispose() => _kit.Dispose();

    [Fact]
    public void Pin1_NamesMatchCaseInsensitively_BackslashAssets_AndTheLaterDuplicateWins()
    {
        // SharedModel.cs (1.4.x) ImportProfile: Profile.xml and DisplayItems.xml match ignoring case, asset entries start
        // "assets/" or "assets\", and each is extracted with overwrite, so the later of two with one name wins.
        var first = Asset("gauge-0.png");
        var later = Asset("gauge-1.png");
        var zip = ZipKit.Zip(
            ("PROFILE.XML", Encoding.UTF8.GetBytes(ProfileXml(new Profile("Case", 320, 240)))),
            ("displayitems.xml", Encoding.UTF8.GetBytes(Items(Image("A", "a.png"), Image("B", "Win\\b.png")))),
            ("assets/a.png", first),
            ("ASSETS\\A.PNG", later),
            ("assets\\win\\B.png", Asset("logo.png")));

        var envelope = _kit.ReadArchive(zip);

        Assert.Equal("Case", (string)envelope.Profile["name"]!);
        Assert.Equal(BlobStore.IdOf(later), envelope.Assets[0].Blob!.Id);
        Assert.False(_kit.Stored(first));
        Assert.Equal(BlobStore.IdOf(Asset("logo.png")), envelope.Assets[1].Blob!.Id);
        var duplicate = Single(envelope, "import.asset.duplicate");
        Assert.Equal(("info", "assets/a.png"), (duplicate.Severity, duplicate.File));
    }

    [Fact]
    public void Pin1_TheLaterOfTwoProfileXmls_Wins()
    {
        var zip = ZipKit.Zip(
            ("Profile.xml", Encoding.UTF8.GetBytes(ProfileXml(new Profile("Earlier", 100, 100)))),
            ("profile.xml", Encoding.UTF8.GetBytes(ProfileXml(new Profile("Later", 100, 100)))),
            ("DisplayItems.xml", Encoding.UTF8.GetBytes(Items())));

        Assert.Equal("Later", (string)_kit.ReadArchive(zip).Profile["name"]!);
    }

    [Fact]
    public void Pin10_AnExportsProfileXml_GivesTheModelsWindowDefaults()
    {
        // SharedModel.cs (1.4.x) ExportProfile writes new Profile(name, w, h) with the look only: the window members
        // are the model's defaults (active, as the model starts; at 0,0; no target window), not where 1.x showed it.
        var source = ProfileSerializerTests.ReadFixtureProfiles()[0];
        using var store = new TempDirectory();
        var profileStore = new ProfileStore(new DataPaths(store.Combine("data")));
        profileStore.AddProfile(source, []);
        var archive = new MemoryStream();
        InfoPanelArchive.Export(source, [], profileStore, archive);

        var envelope = _kit.ReadArchive(archive.ToArray());

        Assert.Equal(("Desk Overlay", 800, 480, true), ((string)envelope.Profile["name"]!, (int)envelope.Profile["width"]!, (int)envelope.Profile["height"]!, (bool)envelope.Profile["showFps"]!));
        Assert.Equal((true, 0, 0, false, true, true), ((bool)envelope.Profile["active"]!, (int)envelope.Profile["windowX"]!, (int)envelope.Profile["windowY"]!,
            (bool)envelope.Profile["topmost"]!, (bool)envelope.Profile["drag"]!, (bool)envelope.Profile["resize"]!));
        Assert.Null(envelope.TargetWindow);
        Assert.Null(envelope.Profile["triggerProcessNames"]);
    }

    [Fact]
    public void Pin7_ABareProfile_GivesTheModelsDefaults()
    {
        // Profile.cs (1.4.x): what an empty <Profile/> reads as. The default font is Core's (Inter): 1.4.x's was the
        // machine's first system font, which a file never leaves out (the serializer always writes Font).
        var envelope = _kit.ReadArchive(V1Archive("<Profile />", Items()));

        var expected = new JsonObject
        {
            ["name"] = "Profile", ["width"] = 600, ["height"] = 400, ["showFps"] = false, ["drag"] = true, ["backgroundColor"] = "#FFFFFFFF",
            ["active"] = true, ["openGL"] = false, ["fontScale"] = 1.33f, ["topmost"] = false, ["font"] = "Inter", ["fontSize"] = 26,
            ["color"] = "#FF000000", ["windowX"] = 0, ["windowY"] = 0, ["resize"] = true, ["strictWindowMatching"] = false,
            ["triggerProcessNames"] = null, ["isSelected"] = false,
        };
        Assert.Equal(expected.OrderBy(p => p.Key).Select(p => (p.Key, p.Value?.ToJsonString())), envelope.Profile.OrderBy(p => p.Key).Select(p => (p.Key, p.Value?.ToJsonString())));
        Assert.Null(envelope.TargetWindow);
        Assert.True(Guid.TryParse(envelope.Id, out _));
    }

    [Fact]
    public void TheE2eArchive_ReadsWithEveryKind()
    {
        var envelope = _kit.ReadArchive(File.ReadAllBytes(ProfileSerializerTests.FixturePath("Fixtures/e2e/all-kinds.infopanel")));

        Assert.Equal(14, ItemKinds.All.Select(k => k.Id).Intersect(Walk(envelope.Items).Select(n => n.Kind)).Count());
        Assert.DoesNotContain(envelope.Diagnostics, d => d.Severity == "error");

        static IEnumerable<InfoPanel.Editing.ItemNode> Walk(IEnumerable<InfoPanel.Editing.ItemNode> nodes) =>
            nodes.SelectMany(n => new[] { n }.Concat(Walk(n.Children ?? [])).Concat(Walk(n.Images ?? [])));
    }

    [Fact]
    public void SubFolders_ResolveAgainstTheSameSubFolder()
    {
        // A deviation from 1.4.x, which flattened sub-folders on import (so such an item drew the placeholder).
        var envelope = _kit.ReadArchive(V1Archive(Items(Image("Deep", "icons/a.png")), ("assets/icons/a.png", Asset("logo.png")), ("assets/a.png", Asset("gauge-0.png"))));

        Assert.Equal(BlobStore.IdOf(Asset("logo.png")), envelope.Assets[0].Blob!.Id);
        Assert.StartsWith("1 asset file(s)", Single(envelope, "import.asset.unused").Message);
    }

    [Fact]
    public void OtherEntries_AreIgnored_AndUnusedAssetsCounted()
    {
        var envelope = _kit.ReadArchive(V1Archive(Items(Image("A", "a.png")),
            ("assets/a.png", Asset("logo.png")), ("assets/spare.png", Asset("gauge-0.png")), ("assets/other.png", Asset("gauge-1.png")),
            ("readme.txt", "hello"u8.ToArray()), ("assets/", []), ("thumbs/x.png", [1])));

        Assert.Equal(["readme.txt", "thumbs/x.png"], With(envelope, "import.entry").Select(d => d.File));
        var unused = Single(envelope, "import.asset.unused");
        Assert.StartsWith("2 asset file(s)", unused.Message);
        Assert.False(_kit.Stored(Asset("gauge-0.png")));
    }

    [Fact]
    public void NoProfileXmlOrItems_IsImportArchive()
    {
        var onlyProfile = ZipKit.Zip(("Profile.xml", Encoding.UTF8.GetBytes(ProfileXml(new Profile()))));
        var onlyItems = ZipKit.Zip(("DisplayItems.xml", Encoding.UTF8.GetBytes(Items())));
        var inAFolder = ZipKit.Zip(("theme/Profile.xml", Encoding.UTF8.GetBytes(ProfileXml(new Profile()))), ("theme/DisplayItems.xml", Encoding.UTF8.GetBytes(Items())));

        foreach (var zip in new[] { onlyProfile, onlyItems, inAFolder })
            Assert.Equal("import.archive", ZipKit.Refused(() => _kit.ReadArchive(zip)).Code);
        Assert.Equal(0, _kit.BlobCount);
    }

    [Fact]
    public void A2Archive_OnThe1xRoute_IsArchiveV2()
    {
        var zip = ZipKit.Zip(ZipKit.Text("document.json", "{}"), ("blobs/" + new string('0', 64), [1]));

        Assert.Equal("archive.v2", ZipKit.Refused(() => _kit.ReadArchive(zip)).Code);
    }

    [Fact]
    public void UnreadableProfileXml_IsImportXml()
    {
        static byte[] With(string profileXml) => V1Archive(profileXml, Items());

        Assert.Equal("import.xml", ZipKit.Refused(() => _kit.ReadArchive(With("<Profile><Name>"))).Code);
        Assert.Equal("import.xml", ZipKit.Refused(() => _kit.ReadArchive(With("<!DOCTYPE Profile []><Profile />"))).Code);
        Assert.Equal("import.xml", ZipKit.Refused(() => _kit.ReadArchive(With("<NotAProfile />"))).Code);
        var error = ZipKit.Refused(() => _kit.ReadArchive(With("<Profile><Width>wide</Width></Profile>")));
        Assert.Equal(("import.xml", "/entries/Profile.xml"), (error.Code, error.Errors[0].Path));
    }

    [Fact]
    public void ProfileXml_OverFourMiB_IsImportXml()
    {
        // The real limit, 4 MiB: one byte over is refused before the XML is read.
        var padded = new string(' ', 4 * 1024 * 1024) + ProfileXml(new Profile("Big", 100, 100)).TrimStart('\uFEFF');

        var error = ZipKit.Refused(() => _kit.ReadArchive(V1Archive(padded, Items())));

        Assert.Equal("import.xml", error.Code);
        Assert.Contains("larger than 4194304 bytes", error.Message);
    }

    [Fact]
    public void Guards_RefuseTheWholeArchive_BeforeAnythingIsStored()
    {
        var logo = Asset("logo.png");
        var traversal = V1Archive(Items(Image("A", "a.png")), ("assets/a.png", logo), ("assets/../../x.png", logo));
        Assert.Equal("archive.name", ZipKit.Refused(() => _kit.ReadArchive(traversal)).Code);

        var link = ZipKit.WithAttributes("assets/a.png", logo, unchecked((int)0xA1FF0000));
        Assert.Equal("archive.link", ZipKit.Refused(() => _kit.ReadArchive(link)).Code);

        Assert.Equal("archive.zip", ZipKit.Refused(() => _kit.ReadArchive("PK not really"u8.ToArray())).Code);
        Assert.Equal(0, _kit.BlobCount);
    }

    [Fact]
    public void ExpandedBytes_OverTheRequestsLimit_RefuseTheArchive()
    {
        var zip = V1Archive(Items(Image("A", "a.png"), Image("B", "b.png")), ("assets/a.png", Asset("logo.png")), ("assets/b.png", Asset("gauge-0.png")));
        var limit = new ImportLimits { ExpandedBytes = 3000 };

        Assert.Equal("archive.expanded", ZipKit.Refused(() => _kit.ReadArchive(zip, limit)).Code);
    }

    [Fact]
    public void AbsolutePaths_InAnArchive_AreReported_NotRefused()
    {
        // The 1.2.x rule for archives: a .infopanel carries no version, so none is refused as 1.1.x data; an absolute
        // image path, what 1.1.x data had, is reported and never read.
        var envelope = _kit.ReadArchive(V1Archive(Items(Image("Old", "C:\\Users\\me\\Pictures\\bg.png", relative: false), Text("Label"))));

        Assert.Equal(2, envelope.Items.Count);
        Assert.Equal("import.asset.absolute", envelope.Assets[0].Code);
        Assert.Empty(With(envelope, "import.version"));
    }
}
