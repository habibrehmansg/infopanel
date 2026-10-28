using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Import;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Import.ZipKit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// The 2.0 profile archive: export (the document and every blob it refers to, verified, in id order, built before
/// anything is sent), and the strict import that only unpacks: exact names, the document checked as a create checks
/// one, every blob referenced and hashed against its name, then stored. A client creates the profile afterwards.
/// </summary>
public sealed class ProfileArchiveTests : IDisposable
{
    private const string Id = "5b6c7d8e-9fa0-4b1c-8d2e-3f4a5b6c7d8e";
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly List<Workspace> _open = [];

    public void Dispose()
    {
        foreach (var workspace in _open)
            workspace.Dispose();
        _temp.Dispose();
    }

    private Workspace NewWorkspace(string name, SpecLimits? limits = null)
    {
        var paths = new DataPaths(_temp.Combine(name));
        var documents = new DocumentStore(paths, limits);
        var blobs = new BlobStore(paths, limits);
        documents.Open();
        blobs.Open();
        var workspace = new Workspace(documents, blobs, new WorkspaceOptions { Limits = limits ?? SpecLimits.Default });
        _open.Add(workspace);
        return workspace;
    }

    private static readonly byte[] Png = ImportKit.Asset("logo.png");
    private static readonly byte[] Gif = ImportKit.Asset("blink.gif");
    private static readonly byte[] Template = Encoding.UTF8.GetBytes("""{"format":"example.template/1","parts":[]}""");

    /// <summary>A document using a stored image, a template package its source lists, and the no-image id.</summary>
    private static JsonObject Document(string name = "Desk: Main/1", JsonNode? data = null) => new()
    {
        ["format"] = "infopanel.document/1",
        ["id"] = Id,
        ["profile"] = new JsonObject { ["name"] = name, ["width"] = 200, ["height"] = 100, ["background"] = "#000000" },
        ["items"] = new JsonArray(
            new JsonObject
            {
                ["kind"] = "scene",
                ["id"] = "img",
                ["placement"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
                ["source"] = new JsonObject { ["format"] = "example.widget/1", ["data"] = data ?? new JsonObject(), ["deps"] = new JsonArray(BlobStore.IdOf(Template)) },
                ["scene"] = new JsonObject
                {
                    ["format"] = "infopanel.scene/1",
                    ["nodes"] = new JsonArray(
                        new JsonObject { ["kind"] = "media", ["w"] = 10, ["h"] = 10, ["source"] = new JsonObject { ["asset"] = BlobStore.IdOf(Png) } },
                        new JsonObject { ["kind"] = "media", ["w"] = 10, ["h"] = 10, ["source"] = new JsonObject { ["asset"] = BlobStore.IdOf(Gif) } },
                        new JsonObject { ["kind"] = "media", ["w"] = 10, ["h"] = 10, ["source"] = new JsonObject { ["asset"] = ProfileArchive.NoImage } }),
                },
            }),
    };

    /// <summary>A workspace holding the document's blobs and a profile created from it.</summary>
    private Workspace Source(JsonObject? document = null)
    {
        var workspace = NewWorkspace("source");
        foreach (var blob in new[] { Png, Gif, Template })
            workspace.PutBlob(blob);
        var created = workspace.Create(Encoding.UTF8.GetBytes(new JsonObject { ["id"] = Id, ["document"] = document ?? Document() }.ToJsonString()));
        Assert.True(created.Entry is not null, string.Join("; ", created.Invalid?.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}") ?? []));
        return workspace;
    }

    private static byte[] Bytes(ExportedArchive archive)
    {
        using var copy = new MemoryStream();
        archive.Content.CopyTo(copy);
        return copy.ToArray();
    }

    private static byte[] ExportBytes(Workspace workspace, string id)
    {
        var (document, _) = workspace.Export(id);
        using (document)
        {
            using var copy = new MemoryStream();
            document!.Content.CopyTo(copy);
            return copy.ToArray();
        }
    }

    private static List<string> Names(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    private static byte[] Entry(byte[] zip, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var stream = archive.GetEntry(name)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static ArchiveContents Open(byte[] zip, Workspace workspace, ImportLimits? limits = null) =>
        ProfileArchive.Open(new MemoryStream(zip), workspace, limits);

    private static string Hex(byte[] bytes) => BlobStore.IdOf(bytes)[BlobStore.IdPrefix.Length..];

    // ---- export, import and create ------------------------------------------------------------------------------

    /// <summary>The source's export at the default limits: its entries, the bytes an import reads, and the file's length.</summary>
    private static (int Entries, long Expanded, long Length) Totals(Workspace source)
    {
        var (archive, _) = ProfileArchive.Export(source, Id, Now);
        using (archive)
            return (1 + archive!.Blobs.Count, ExportBytes(source, Id).Length + Png.Length + Gif.Length + Template.Length, archive.Content.Length);
    }

    [Theory]
    [InlineData("entries", "archive.entries")]
    [InlineData("expanded", "archive.expanded")]
    [InlineData("archive", "archive.expanded")]
    public void Export_ThatAnImportWouldRefuse_IsRefused(string limit, string code)
    {
        var source = Source();
        var (entries, expanded, length) = Totals(source);
        var limits = limit switch
        {
            "entries" => new ImportLimits { Entries = entries - 1 },
            "expanded" => new ImportLimits { ExpandedBytes = expanded - 1 },
            _ => new ImportLimits { ArchiveBytes = length - 1 },
        };
        FileStream? file = null;

        var error = Assert.Throws<ImportRefusal>(() => ProfileArchive.Export(source, Id, Now, limits, () => file = TempFile.Create()));

        Assert.Equal(code, error.Code);
        Assert.False(File.Exists(file!.Name));
    }

    [Fact]
    public void Export_AtTheImportLimits_Exports_AndImportsWithThem()
    {
        var source = Source();
        var (entries, expanded, length) = Totals(source);
        var limits = new ImportLimits { Entries = entries, ExpandedBytes = expanded, ArchiveBytes = length };

        var (archive, problem) = ProfileArchive.Export(source, Id, Now, limits);
        Assert.Null(problem);
        byte[] zip;
        using (archive)
            zip = Bytes(archive!);

        Assert.Equal(3, Open(zip, NewWorkspace("target"), limits).Blobs.Count);
    }

    /// <summary>A workspace holding one profile whose document refers to no blob, and that document's export length.</summary>
    private (Workspace Source, long Length) DocumentOnly()
    {
        var workspace = NewWorkspace("document-only");
        var document = new JsonObject
        {
            ["format"] = "infopanel.document/1",
            ["id"] = Id,
            ["profile"] = new JsonObject { ["name"] = "Plain", ["width"] = 200, ["height"] = 100, ["background"] = "#000000" },
            ["items"] = new JsonArray(),
        };
        var created = workspace.Create(Encoding.UTF8.GetBytes(new JsonObject { ["id"] = Id, ["document"] = document }.ToJsonString()));
        Assert.True(created.Entry is not null, string.Join("; ", created.Invalid?.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}") ?? []));
        return (workspace, ExportBytes(workspace, Id).Length);
    }

    [Theory]
    [InlineData("entries", "archive.entries")]
    [InlineData("expanded", "archive.expanded")]
    [InlineData("expanded-none", "archive.expanded")]
    public void Export_DocumentOnly_ThatAnImportWouldRefuse_IsRefused(string limit, string code)
    {
        var (source, length) = DocumentOnly();
        var limits = limit switch
        {
            "entries" => new ImportLimits { Entries = 0 },
            "expanded" => new ImportLimits { ExpandedBytes = length - 1 },
            _ => new ImportLimits { ExpandedBytes = 0 },
        };
        FileStream? file = null;

        var error = Assert.Throws<ImportRefusal>(() => ProfileArchive.Export(source, Id, Now, limits, () => file = TempFile.Create()));

        // The import, given the same limits, refuses the archive exported without them with the same code.
        var (archive, _) = ProfileArchive.Export(source, Id, Now);
        byte[] zip;
        using (archive)
            zip = Bytes(archive!);
        Assert.Equal((code, code, false), (error.Code, Assert.Throws<ImportRefusal>(() => Open(zip, NewWorkspace("target"), limits)).Code, File.Exists(file!.Name)));
    }

    [Fact]
    public void Export_DocumentOnly_AtTheImportLimits_Exports_AndImportsWithThem()
    {
        var (source, length) = DocumentOnly();
        var limits = new ImportLimits { Entries = 1, ExpandedBytes = length };

        var (archive, problem) = ProfileArchive.Export(source, Id, Now, limits);
        Assert.Null(problem);
        byte[] zip;
        using (archive)
            zip = Bytes(archive!);

        Assert.Equal([ProfileArchive.DocumentEntry], Names(zip));
        Assert.Empty(Open(zip, NewWorkspace("target"), limits).Blobs);
    }

    [Fact]
    public void RoundTrip_TheDocumentComesBackByteEqual_WithEachBlobOnce()
    {
        var source = Source();
        var (archive, problem) = ProfileArchive.Export(source, Id, Now);
        Assert.Null(problem);
        byte[] zip;
        using (archive)
            zip = Bytes(archive!);

        // document.json first, then each referenced blob once, in id order: the template dependency included, the
        // no-image id never.
        var blobs = new[] { Png, Gif, Template }.Select(b => BlobStore.IdOf(b)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "document.json" }.Concat(blobs.Select(b => "blobs/" + b[7..])), Names(zip));
        Assert.Equal(blobs, archive!.Blobs);
        var exported = ExportBytes(source, Id);
        Assert.Equal(exported, Entry(zip, "document.json"));
        Assert.Equal(Png, Entry(zip, "blobs/" + Hex(Png)));

        // Into another engine: verified, stored, nothing missing.
        var target = NewWorkspace("target");
        var contents = Open(zip, target);

        Assert.Equal(Encoding.UTF8.GetString(exported), contents.Document.GetRawText());
        Assert.Equal(blobs, contents.Blobs.Select(b => b.Id).Order(StringComparer.Ordinal));
        Assert.Equal("png", contents.Blobs.Single(b => b.Id == BlobStore.IdOf(Png)).Media!.Format);
        Assert.NotNull(contents.Blobs.Single(b => b.Id == BlobStore.IdOf(Template)).MediaError);
        Assert.Empty(contents.Missing);
        Assert.All(blobs, b => Assert.True(target.Blobs.Exists(b)));

        // The client creates the profile from the answer (under an id it chooses).
        const string NewId = "6c7d8e9f-a0b1-4c2d-8e3f-4a5b6c7d8e9f";
        var body = new JsonObject { ["id"] = NewId, ["document"] = JsonNode.Parse(contents.Document.GetRawText()) };
        var created = target.Create(Encoding.UTF8.GetBytes(body.ToJsonString()));
        Assert.Equal(201, created.Status);
        var again = JsonNode.Parse(ExportBytes(target, NewId))!;
        again["id"] = Id;
        Assert.Equal(JsonNode.Parse(exported)!.ToJsonString(), again.ToJsonString());
    }

    [Fact]
    public void Export_EntryOrderIsDeterministic()
    {
        var source = Source();
        List<string> First()
        {
            var (archive, _) = ProfileArchive.Export(source, Id, Now);
            using (archive)
                return Names(Bytes(archive!));
        }

        Assert.Equal(First(), First());
    }

    [Fact]
    public void Export_ACorruptBlob_IsLeftOut_AndThenReportedMissing()
    {
        var source = Source();
        File.WriteAllBytes(source.Blobs.Paths.BlobFile(BlobStore.IdOf(Gif)), Png);

        var (archive, _) = ProfileArchive.Export(source, Id, Now);
        byte[] zip;
        using (archive)
            zip = Bytes(archive!);

        Assert.DoesNotContain("blobs/" + Hex(Gif), Names(zip));
        var contents = Open(zip, NewWorkspace("target"));
        Assert.Equal([BlobStore.IdOf(Gif)], contents.Missing);
        Assert.DoesNotContain(ProfileArchive.NoImage, contents.Missing);
    }

    [Fact]
    public void Export_AMissingBlob_IsLeftOut_AndIsNotMissingWhereItIsStored()
    {
        var source = Source();
        File.Delete(source.Blobs.Paths.BlobFile(BlobStore.IdOf(Template)));

        var (archive, _) = ProfileArchive.Export(source, Id, Now);
        byte[] zip;
        using (archive)
            zip = Bytes(archive!);

        Assert.Equal(2, Names(zip).Count(n => n.StartsWith("blobs/")));
        var target = NewWorkspace("target");
        Assert.Equal([BlobStore.IdOf(Template)], Open(zip, target).Missing);
        target.PutBlob(Template);
        Assert.Empty(Open(zip, target).Missing);
    }

    [Fact]
    public void Export_AnUnsupportedProfile_IsItsStoredBytes()
    {
        var workspace = NewWorkspace("source");
        var paths = workspace.Documents.Paths;
        var stored = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["format"] = "infopanel.document/2",
            ["id"] = Id,
            ["profile"] = new JsonObject { ["name"] = "Future", ["width"] = 10, ["height"] = 10 },
            ["items"] = new JsonArray(),
            ["next"] = new JsonObject { ["asset"] = BlobStore.IdOf(Png) },
        }.ToJsonString());
        Directory.CreateDirectory(paths.ProfileFolder(Id));
        File.WriteAllBytes(paths.DocumentFile(Id), stored);
        File.WriteAllText(paths.ProfileOrderFile, $$"""{"order":["{{Id}}"]}""");
        workspace.PutBlob(Png);
        // The store lists what is on disk when it is opened again.
        workspace = NewWorkspace("source");
        Assert.Equal("unsupported", workspace.Entry(Id)!.Status);

        var (archive, _) = ProfileArchive.Export(workspace, Id, Now);
        byte[] zip;
        using (archive)
            zip = Bytes(archive!);

        Assert.Equal(stored, Entry(zip, "document.json"));
        Assert.Contains("blobs/" + Hex(Png), Names(zip));
        Assert.Equal("Future-1793188800.infopanel2", archive!.FileName);
        // An unsupported document is refused on import, as a create would refuse it.
        var refusal = Assert.Throws<ImportRefusal>(() => Open(zip, NewWorkspace("target")));
        Assert.StartsWith("/document", refusal.Errors[0].Path);
    }

    [Fact]
    public void Export_NoSuchProfile_OrOneThatCantBeRead()
    {
        var workspace = NewWorkspace("source");
        Assert.Equal((null, null), ProfileArchive.Export(workspace, Id, Now));

        // A document file that can't be opened at all (a folder in its place): the profile's status problem (409).
        var paths = workspace.Documents.Paths;
        Directory.CreateDirectory(paths.DocumentFile(Id));
        File.WriteAllText(paths.ProfileOrderFile, $$"""{"order":["{{Id}}"]}""");
        workspace = NewWorkspace("source");

        var (archive, problem) = ProfileArchive.Export(workspace, Id, Now);

        Assert.Null(archive);
        Assert.Equal(("unreadable", "io"), (problem!.Code, problem.Problem.Code));
    }

    [Fact]
    public void FileName_Follows14xsExportRule()
    {
        // 1.4.x pin 11 (SharedModel.cs ExportProfile): the name with invalid file name characters and spaces replaced by
        // '_', a dash and the Unix seconds; the same on every OS.
        Assert.Equal("Desk__Main_1-1793188800.infopanel2", ProfileArchive.FileNameFor("Desk: Main/1", Now));
        Assert.Equal("a_b_c_d_e_f_g_h_i-1793188800.infopanel2", ProfileArchive.FileNameFor("a\"b<c>d|e*f?g\\h\ti", Now));
        Assert.Equal("Überblick_✓-1793188800.infopanel2", ProfileArchive.FileNameFor("Überblick ✓", Now));

        var (archive, _) = ProfileArchive.Export(Source(), Id, Now);
        using (archive)
            Assert.Equal("Desk__Main_1-1793188800.infopanel2", archive!.FileName);
    }

    [Fact]
    public void Export_IsBuiltInATemporaryFile_GoneWhenDisposed()
    {
        var (archive, _) = ProfileArchive.Export(Source(), Id, Now);
        var path = archive!.Content.Name;
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar));
        Assert.Equal(0, archive.Content.Position);

        archive.Dispose();

        Assert.False(File.Exists(path));
    }

    // ---- the strict import --------------------------------------------------------------------------------------

    private byte[] Exported()
    {
        var (archive, _) = ProfileArchive.Export(Source(), Id, Now);
        using (archive)
            return Bytes(archive!);
    }

    private byte[] WithDocument(JsonObject document, params (string Name, byte[] Bytes)[] blobs) =>
        Zip([("document.json", Encoding.UTF8.GetBytes(document.ToJsonString())), .. blobs]);

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("Document.json")]
    [InlineData("blobs/")]
    [InlineData("blobs/ABCDEF0000000000000000000000000000000000000000000000000000000000")]
    [InlineData("blobs/abc")]
    [InlineData("blobs\\0000000000000000000000000000000000000000000000000000000000000001")]
    [InlineData("other/document.json")]
    public void Names_AreExact(string name)
    {
        var target = NewWorkspace("target");
        var zip = WithDocument(Document(), (name, name.EndsWith('/') ? [] : [1, 2, 3]));

        var refusal = Assert.Throws<ImportRefusal>(() => Open(zip, target));

        Assert.Equal(("archive.name", ImportRefusal.EntryPath(name)), (refusal.Code, refusal.Errors[0].Path));
        Assert.False(Directory.Exists(target.Blobs.Paths.BlobsDirectory) && Directory.EnumerateFiles(target.Blobs.Paths.BlobsDirectory).Any());
    }

    [Fact]
    public void NoDocument_IsArchiveDocument()
    {
        Assert.Equal("archive.document", Assert.Throws<ImportRefusal>(() => Open(Zip(("blobs/" + Hex(Png), Png)), NewWorkspace("target"))).Code);
    }

    [Fact]
    public void AHashMismatch_IsArchiveHash()
    {
        var target = NewWorkspace("target");
        var zip = WithDocument(Document(), ("blobs/" + Hex(Png), Gif));

        var refusal = Assert.Throws<ImportRefusal>(() => Open(zip, target));

        Assert.Equal(("archive.hash", ImportRefusal.EntryPath("blobs/" + Hex(Png))), (refusal.Code, refusal.Errors[0].Path));
        Assert.False(target.Blobs.Exists(BlobStore.IdOf(Gif)));
        Assert.False(target.Blobs.Exists(BlobStore.IdOf(Png)));
    }

    [Fact]
    public void AnUnreferencedBlob_IsRefused_BeforeAnyBlobIsStored()
    {
        var target = NewWorkspace("target");
        var stray = ImportKit.Asset("gauge-4.png");
        var zip = WithDocument(Document(), ("blobs/" + Hex(Png), Png), ("blobs/" + Hex(stray), stray));

        var refusal = Assert.Throws<ImportRefusal>(() => Open(zip, target));

        Assert.Equal(("archive.unreferenced", ImportRefusal.EntryPath("blobs/" + Hex(stray))), (refusal.Code, refusal.Errors[0].Path));
        Assert.False(target.Blobs.Exists(BlobStore.IdOf(stray)));
        Assert.False(target.Blobs.Exists(BlobStore.IdOf(Png)));
    }

    [Fact]
    public void TheDocument_IsCheckedAsACreateChecksIt()
    {
        var target = NewWorkspace("target");

        // Nested deeper than jsonDepth (256) inside opaque data.
        JsonNode deep = new JsonArray();
        for (var i = 0; i < 260; i++)
            deep = new JsonArray(deep);
        var tooDeep = Assert.Throws<ImportRefusal>(() => Open(WithDocument(Document(data: deep)), target));
        Assert.Contains(tooDeep.Errors, e => e.Code == "limit.jsonDepth");

        // A newer major format: unsupported, refused here as a create refuses it.
        var future = Document();
        future["format"] = "infopanel.document/2";
        Assert.All(Assert.Throws<ImportRefusal>(() => Open(WithDocument(future), target)).Errors, e => Assert.StartsWith("/document", e.Path));

        // Invalid: errors under /document.
        var invalid = Document();
        invalid["profile"]!["width"] = -5;
        var refusal = Assert.Throws<ImportRefusal>(() => Open(WithDocument(invalid), target));
        Assert.Contains(refusal.Errors, e => e.Path.StartsWith("/document/profile"));

        // The document's own id doesn't matter: a create replaces it.
        var otherId = Document();
        otherId["id"] = "not an id";
        Assert.Empty(Open(WithDocument(otherId, ("blobs/" + Hex(Png), Png), ("blobs/" + Hex(Gif), Gif), ("blobs/" + Hex(Template), Template)), target).Missing);

        var notJson = Assert.Throws<ImportRefusal>(() => Open(Zip(Text("document.json", "{ nope")), target));
        Assert.Equal(("json", "/document"), (notJson.Code, notJson.Errors[0].Path));
    }

    [Fact]
    public void Limits_DocumentAndMediaBytes()
    {
        var small = NewWorkspace("small", new SpecLimits { DocumentBytes = 200, MediaBytes = 1000 });

        Assert.Equal("limit.documentBytes", Assert.Throws<ImportRefusal>(() => Open(WithDocument(Document()), small)).Code);

        var withBlob = NewWorkspace("blob", new SpecLimits { MediaBytes = 1000 });
        var refusal = Assert.Throws<ImportRefusal>(() => Open(WithDocument(Document(), ("blobs/" + Hex(Png), Png)), withBlob));
        Assert.Equal(("limit.mediaBytes", ImportRefusal.EntryPath("blobs/" + Hex(Png))), (refusal.Code, refusal.Errors[0].Path));
    }

    [Fact]
    public void WrongKinds_GiveTheirHints()
    {
        // A 1.x archive on the 2.0 route, and a 2.0 archive on the 1.x route.
        var v1 = ImportKit.V1Archive(ImportKit.Items(ImportKit.Text("A")));
        Assert.Equal("archive.v1", Assert.Throws<ImportRefusal>(() => Open(v1, NewWorkspace("target"))).Code);

        using var kit = new ImportKit();
        Assert.Equal("archive.v2", Assert.Throws<ImportRefusal>(() => kit.ReadArchive(Exported())).Code);
    }

    [Fact]
    public void Guards_ApplyToTheProfileArchiveToo()
    {
        var target = NewWorkspace("target");
        Assert.Equal("archive.zip", Assert.Throws<ImportRefusal>(() => Open("nope"u8.ToArray(), target)).Code);
        Assert.Equal("archive.duplicate", Assert.Throws<ImportRefusal>(() => Open(Zip(Text("document.json", "{}"), Text("document.json", "{}")), target)).Code);
        Assert.Equal("archive.name", Assert.Throws<ImportRefusal>(() => Open(Zip(Text("document.json", "{}"), Text("../document.json", "{}")), target)).Code);
        Assert.Equal("archive.expanded", Assert.Throws<ImportRefusal>(() => Open(Exported(), target, new ImportLimits { ExpandedBytes = 1000 })).Code);
    }
}
