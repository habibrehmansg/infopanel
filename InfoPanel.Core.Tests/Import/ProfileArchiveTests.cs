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

    private static byte[] ExportBytes(Workspace workspace, string id) => workspace.Export(id).Document!;

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

    [Fact]
    public void Import_OfAnArchiveOverArchiveBytes_IsArchiveExpanded_AndStoresNothing()
    {
        var source = Source();
        var (archive, _) = ProfileArchive.Export(source, Id, Now);
        byte[] zip;
        using (archive)
            zip = Bytes(archive!);

        var target = NewWorkspace("over-archive-bytes");
        var error = Assert.Throws<ImportRefusal>(() => Open(zip, target, new ImportLimits { ArchiveBytes = zip.Length - 1 }));
        Assert.Equal("archive.expanded", error.Code);
        Assert.False(target.Blobs.Exists(BlobStore.IdOf(Png)));
        Assert.False(target.Blobs.Exists(BlobStore.IdOf(Gif)));

        Assert.Equal(3, Open(zip, target, new ImportLimits { ArchiveBytes = zip.Length }).Blobs.Count);
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

    /// <summary>
    /// The export hashes the bytes it puts in the archive, whatever the store remembers: a blob file changed after a
    /// checked read, to other bytes of the same length with its time set back, is left out.
    /// </summary>
    [Fact]
    public void Export_ABlobChangedAfterACheckedRead_IsHashedAgain_AndLeftOut()
    {
        var source = Source();
        var id = BlobStore.IdOf(Gif);
        var file = source.Blobs.Paths.BlobFile(id);
        Assert.Equal(Gif, source.Blobs.ReadVerified(id));
        var written = File.GetLastWriteTimeUtc(file);
        var damaged = (byte[])Gif.Clone();
        damaged[^1] ^= 1;
        File.WriteAllBytes(file, damaged);
        File.SetLastWriteTimeUtc(file, written);

        var (archive, _) = ProfileArchive.Export(source, Id, Now);
        byte[] zip;
        using (archive)
        {
            Assert.Equal([BlobStore.IdOf(Png), BlobStore.IdOf(Template)], archive!.Blobs.Order(StringComparer.Ordinal));
            zip = Bytes(archive);
        }

        Assert.DoesNotContain("blobs/" + Hex(Gif), Names(zip));
        Assert.Equal([id], Open(zip, NewWorkspace("target")).Missing);
    }

    /// <summary>A local copy that doesn't hash to its id is not stored: an import that doesn't carry the blob reports it missing.</summary>
    [Fact]
    public void Import_ABlobNotCarried_WhoseLocalCopyIsCorrupt_IsMissing()
    {
        var source = Source();
        File.Delete(source.Blobs.Paths.BlobFile(BlobStore.IdOf(Template)));
        var (archive, _) = ProfileArchive.Export(source, Id, Now);
        byte[] zip;
        using (archive)
            zip = Bytes(archive!);

        var target = NewWorkspace("target");
        target.PutBlob(Template);
        var file = target.Blobs.Paths.BlobFile(BlobStore.IdOf(Template));
        var written = File.GetLastWriteTimeUtc(file);
        var damaged = (byte[])Template.Clone();
        damaged[0] ^= 1;
        File.WriteAllBytes(file, damaged);
        File.SetLastWriteTimeUtc(file, written.AddSeconds(1));

        Assert.Equal([BlobStore.IdOf(Template)], Open(zip, target).Missing);
        target.PutBlob(Template);
        Assert.Empty(Open(zip, target).Missing);
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

    /// <summary>
    /// A profile this engine can't open is not exported: its status problem (409), as every other request on it. Its
    /// document in an archive is refused on import with the code a create gives it.
    /// </summary>
    [Fact]
    public void Export_AnUnsupportedProfile_IsNotExported_AndItsDocumentIsRefusedOnImport()
    {
        var workspace = NewWorkspace("source");
        var paths = workspace.Documents.Paths;
        var future = new JsonObject
        {
            ["format"] = "infopanel.document/2",
            ["id"] = Id,
            ["profile"] = new JsonObject { ["name"] = "Future", ["width"] = 10, ["height"] = 10 },
            ["items"] = new JsonArray(),
            ["next"] = new JsonObject { ["asset"] = BlobStore.IdOf(Png) },
        };
        Directory.CreateDirectory(paths.ProfileFolder(Id));
        File.WriteAllText(paths.DocumentFile(Id), future.ToJsonString());
        File.WriteAllText(paths.ProfileOrderFile, $$"""{"order":["{{Id}}"]}""");
        workspace.PutBlob(Png);
        // The store lists what is on disk when it is opened again.
        workspace = NewWorkspace("source");
        Assert.Equal("unsupported", workspace.Entry(Id)!.Status);
        FileStream? file = null;

        var (archive, problem) = ProfileArchive.Export(workspace, Id, Now, ImportLimits.Default, () => file = TempFile.Create());

        Assert.Null(archive);
        Assert.Null(file);
        Assert.Equal((409, "unsupported", "unsupported.format", "/format"), (problem!.Status, problem.Code, problem.Problem.Code, problem.Problem.Path));
        var refusal = Assert.Throws<ImportRefusal>(() => Open(WithDocument(future, ("blobs/" + Hex(Png), Png)), NewWorkspace("target")));
        Assert.Equal([("unsupported.format", "/document/format")], refusal.Errors.Select(e => (e.Code, e.Path)));
    }

    /// <summary>
    /// A stored document over <c>documentBytes</c> (unreadable) is not exported: today's archive of it held the document
    /// and none of its blobs, which its import refuses anyway.
    /// </summary>
    [Fact]
    public void Export_AStoredDocumentOverDocumentBytes_IsNotExported()
    {
        var limits = SpecLimits.Default with { DocumentBytes = 2_000 };
        var workspace = NewWorkspace("over", limits);
        var paths = workspace.Documents.Paths;
        var document = Document();
        document["items"]![0]!["source"]!["data"] = new JsonObject { ["text"] = new string('x', 2_000) };
        var stored = Encoding.UTF8.GetBytes(document.ToJsonString());
        Assert.True(stored.Length > limits.DocumentBytes);
        Directory.CreateDirectory(paths.ProfileFolder(Id));
        File.WriteAllBytes(paths.DocumentFile(Id), stored);
        File.WriteAllText(paths.ProfileOrderFile, $$"""{"order":["{{Id}}"]}""");
        foreach (var blob in new[] { Png, Gif, Template })
            workspace.PutBlob(blob);
        workspace = NewWorkspace("over", limits);
        Assert.Equal(("unreadable", "limit.documentBytes"), (workspace.Entry(Id)!.Status, workspace.Entry(Id)!.Problem!.Code));
        FileStream? file = null;

        var (archive, problem) = ProfileArchive.Export(workspace, Id, Now, ImportLimits.Default, () => file = TempFile.Create());

        Assert.Null(archive);
        Assert.Null(file);
        Assert.Equal((409, "unreadable", "limit.documentBytes"), (problem!.Status, problem.Code, problem.Problem.Code));
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

        // The document's own id is optional and replaced, as a create's (importDocument): another one, or none, is fine ...
        var otherId = Document();
        otherId["id"] = "0e000000-0000-4000-8000-000000000000";
        Assert.Empty(Open(WithDocument(otherId, ("blobs/" + Hex(Png), Png), ("blobs/" + Hex(Gif), Gif), ("blobs/" + Hex(Template), Template)), target).Missing);
        var noId = Document();
        noId.Remove("id");
        Assert.Empty(Open(WithDocument(noId, ("blobs/" + Hex(Png), Png), ("blobs/" + Hex(Gif), Gif), ("blobs/" + Hex(Template), Template)), target).Missing);

        // ... but one that is given must be a document id.
        var notAnId = Document();
        notAnId["id"] = "not an id";
        var badId = Assert.Throws<ImportRefusal>(() => Open(WithDocument(notAnId), target));
        Assert.Equal(("schema", "/document/id"), (Assert.Single(badId.Errors).Code, badId.Errors[0].Path));

        // ... but its numbers are read before it is replaced: one that rounds to ±∞ is json at /document/id.
        var infiniteId = Document();
        infiniteId["id"] = JsonNode.Parse("1e400");
        var infinite = Assert.Throws<ImportRefusal>(() => Open(WithDocument(infiniteId), target));
        Assert.Equal(("json", "/document/id"), (infinite.Errors[0].Code, infinite.Errors[0].Path));

        var notJson = Assert.Throws<ImportRefusal>(() => Open(Zip(Text("document.json", "{ nope")), target));
        Assert.Equal(("json", "/document"), (notJson.Code, notJson.Errors[0].Path));
    }

    [Theory]
    [InlineData("accented")]
    [InlineData("spelled")]
    public void AnArchive_IsRefusedByItsCheck_ExactlyWhenACreateWithItsDocumentIs(string kind)
    {
        // The archive check is the create's check: an archive it passes creates, and one a create would refuse is refused
        // here, with the create's errors. 4,000 é sent unescaped are 8,000 bytes (the store wrote 24,000, escaped, so the
        // archive passed and its create failed); 800 numbers spelled 1e20 are 4,000 bytes as sent and 17,600 measured.
        var limits = new SpecLimits { DocumentBytes = 16_000 };
        var data = kind == "accented"
            ? new JsonObject { ["text"] = new string('é', 4_000) }
            : new JsonObject { ["n"] = new JsonArray(Enumerable.Range(0, 800).Select(_ => JsonNode.Parse("1e20")).ToArray()) };
        var unescaped = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var document = Document(data: data);
        var sent = Encoding.UTF8.GetBytes(document.ToJsonString(unescaped));
        Assert.True(sent.Length < limits.DocumentBytes);

        string archived;
        try
        {
            Open(Zip(("document.json", sent)), NewWorkspace("archive-" + kind, limits));
            archived = "";
        }
        catch (ImportRefusal refusal)
        {
            archived = string.Join("; ", refusal.Errors.Select(e => $"{e.Code} {e.Path}"));
        }
        var created = NewWorkspace("create-" + kind, limits).Create(Encoding.UTF8.GetBytes(new JsonObject { ["id"] = Id, ["document"] = document }.ToJsonString(unescaped)));
        var create = string.Join("; ", created.Invalid?.Errors.Select(e => $"{e.Code} {e.Path}") ?? []);

        Assert.Equal(create, archived);
        Assert.Equal(kind == "accented" ? "" : "limit.documentBytes /document", archived);
    }

    [Theory]
    [InlineData("padded")]
    [InlineData("spelled")]
    public void ADocument_IsAdmittedByItsCanonicalSize_AlikeByACreate_TheArchiveCheckAndAReopen(string kind)
    {
        // The bytes as sent or stored are only held to a raw read cap, a create body's (documentBytes + 64 KiB); the
        // canonical size then decides, the same everywhere. Padded with whitespace past documentBytes, a small document
        // is created, passes the archive check and opens when it is stored by hand; 800 numbers spelled 1e20 (4,000
        // bytes as sent, 17,600 measured) are refused by all three.
        var limits = new SpecLimits { DocumentBytes = 16_000 };
        var compact = Encoding.UTF8.GetBytes((kind == "padded"
            ? Document()
            : Document(data: new JsonObject { ["n"] = new JsonArray(Enumerable.Range(0, 800).Select(_ => JsonNode.Parse("1e20")).ToArray()) })).ToJsonString());
        byte[] sent = kind == "padded" ? [(byte)'{', .. Encoding.ASCII.GetBytes(new string(' ', limits.DocumentBytes)), .. compact.AsSpan(1)] : compact;
        if (kind == "padded")
            Assert.InRange(sent.Length, limits.DocumentBytes + 1, limits.DocumentBytes + 64 * 1024 - 1_024);
        else
            Assert.True(sent.Length < limits.DocumentBytes);

        byte[] body = [.. Encoding.UTF8.GetBytes($$"""{"id":"{{Id}}","document":"""), .. sent, (byte)'}'];
        var created = NewWorkspace("create-" + kind, limits).Create(body);
        var create = string.Join("; ", created.Invalid?.Errors.Select(e => e.Code) ?? []);

        string archived;
        try
        {
            Open(Zip(("document.json", sent)), NewWorkspace("archive-" + kind, limits));
            archived = "";
        }
        catch (ImportRefusal refusal)
        {
            archived = string.Join("; ", refusal.Errors.Select(e => e.Code));
        }

        var paths = new DataPaths(_temp.Combine("reopen-" + kind));
        Directory.CreateDirectory(paths.ProfileFolder(Id));
        File.WriteAllText(paths.ProfileOrderFile, $$"""{"order":["{{Id}}"]}""");
        File.WriteAllBytes(paths.DocumentFile(Id), sent);
        var restarted = Workspace.Start(paths, new WorkspaceOptions { Limits = limits }, Now);
        _open.Add(restarted);
        var entry = Assert.Single(restarted.List());
        var reopened = entry.Status == "ok" ? "" : entry.Problem!.Code;

        var expected = kind == "padded" ? "" : "limit.documentBytes";
        Assert.Equal((expected, expected, expected), (create, archived, reopened));
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
