using System.Text;
using InfoPanel.Documents;
using InfoPanel.Import;
using InfoPanel.Models;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Import.ImportKit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// 1.x image paths: how they are classified, resolved inside a profile's assets (folder or archive) and stored only
/// when they pass the media check. Absolute paths are never read and links never followed.
/// </summary>
public sealed class V1AssetsTests : IDisposable
{
    private readonly ImportKit _kit = new();

    public void Dispose() => _kit.Dispose();

    [Theory]
    [InlineData("logo.png", true, V1PathKind.Relative, "logo.png")]
    [InlineData("sub\\logo.png", true, V1PathKind.Relative, "sub/logo.png")]
    [InlineData("sub/./x/../logo.png", true, V1PathKind.Relative, "sub/logo.png")]
    [InlineData("a//b.png", true, V1PathKind.Relative, "a/b.png")]
    [InlineData("../logo.png", true, V1PathKind.Escapes, "")]
    [InlineData("sub/../../logo.png", true, V1PathKind.Escapes, "")]
    [InlineData(".", true, V1PathKind.Escapes, "")]
    [InlineData("C:\\pics\\a.png", true, V1PathKind.Absolute, "")]
    [InlineData("C:a.png", true, V1PathKind.Absolute, "")]
    [InlineData("\\\\server\\share\\a.png", true, V1PathKind.Absolute, "")]
    [InlineData("/Users/me/a.png", true, V1PathKind.Absolute, "")]
    [InlineData("\\a.png", true, V1PathKind.Absolute, "")]
    [InlineData("logo.png", false, V1PathKind.Absolute, "")]
    [InlineData("C:\\pics\\a.png", false, V1PathKind.Absolute, "")]
    [InlineData("https://example.com/a.png", true, V1PathKind.NotAFilePath, "")]
    [InlineData("file:///C:/a.png", false, V1PathKind.NotAFilePath, "")]
    public void Paths_AreClassifiedAs1xCombinesThem(string path, bool relative, V1PathKind kind, string segments)
    {
        // 1.4.x pin 9: a relative path resolves against the profile's assets; a rooted one makes Path.Combine ignore them.
        var classified = V1Path.Classify(path, relative);

        Assert.Equal(kind, classified.Kind);
        Assert.Equal(segments, string.Join('/', classified.Segments));
    }

    [Fact]
    public void Archive_StoresWhatPassesTheMediaCheck_AndReportsTheRest()
    {
        var logo = Asset("logo.png");
        var gif = Asset("blink.gif");
        var svg = Asset("icon.svg");
        var big = new byte[1024 + 1];
        var xml = Items(
            Image("Logo", "logo.png"),
            Image("Again", "logo.png"),                       // one asset entry per distinct pair
            Image("Anim", "anim.gif"),
            Image("Icon", "icon.svg"),
            Image("Clip", "clip.mp4"),
            Image("Bitmap", "old.bmp"),
            Image("Scripted", "script.svg"),
            Image("Huge", "big.png"),
            Image("Gone", "gone.png"),
            Image("Abs", "C:\\pics\\a.png", relative: false),
            Image("Up", "../x.png"),
            Image("Url", "https://example.com/a.png"),
            Image("Remote", "ignored.png", type: "URL"),       // only FILE images read a file
            Gauge("Dial", Frame("f0", "logo.png")));
        var zip = V1Archive(xml, ("assets/logo.png", logo), ("assets/anim.gif", gif), ("assets/icon.svg", svg), ("assets/clip.mp4", VideoStub),
            ("assets/old.bmp", BmpStub), ("assets/script.svg", ScriptSvg), ("assets/big.png", big));

        var envelope = _kit.ReadArchive(zip, new ImportLimits());
        var assets = envelope.Assets;

        Assert.Equal(["logo.png", "anim.gif", "icon.svg", "clip.mp4", "old.bmp", "script.svg", "big.png", "gone.png", "C:\\pics\\a.png", "../x.png", "https://example.com/a.png"],
            assets.Select(a => a.Path));
        Assert.Equal(BlobStore.IdOf(logo), assets[0].Blob!.Id);
        Assert.Equal("png", assets[0].Blob!.Media!.Format);
        Assert.Equal("gif", assets[1].Blob!.Media!.Format);
        Assert.Equal("svg", assets[2].Blob!.Media!.Format);
        Assert.True(_kit.Stored(logo) && _kit.Stored(gif) && _kit.Stored(svg));

        // Not stored: the media check's code is the reason.
        // The video stub is an ftyp box alone: an mp4 with no moov.
        Assert.Equal(("import.asset.media", "media.malformed"), (assets[3].Code, assets[3].Reason));
        Assert.Equal(("import.asset.media", "media.format"), (assets[4].Code, assets[4].Reason));
        Assert.Equal("import.asset.media", assets[5].Code);
        Assert.StartsWith("media.svg", assets[5].Reason);
        Assert.False(_kit.Stored(VideoStub) || _kit.Stored(BmpStub) || _kit.Stored(ScriptSvg));
        Assert.Equal(("import.asset.missing", "import.asset.absolute", "import.asset.path", "import.asset.path"),
            (assets[7].Code, assets[8].Code, assets[9].Code, assets[10].Code));
        Assert.All(assets.Where(a => a.Code is not null), a => Assert.Null(a.Blob));

        Assert.Equal(3, _kit.BlobCount);
        var diagnostics = envelope.Diagnostics.Where(d => d.Path.StartsWith("/assets/")).ToList();
        Assert.Equal(["/assets/3", "/assets/4", "/assets/5", "/assets/6", "/assets/7", "/assets/8", "/assets/9", "/assets/10"], diagnostics.Select(d => d.Path));
        Assert.All(diagnostics, d => Assert.Equal("warning", d.Severity));
        Assert.Equal("assets/clip.mp4", diagnostics[0].File);
    }

    [Fact]
    public void HttpSensorImages_AreNotFileAssets()
    {
        // An HTTP sensor image's source is its sensor: its Type hides the base one, which stays FILE, so a leftover
        // FilePath must not be read as a file.
        var logo = Asset("logo.png");
        var xml = Items("""<DisplayItem xsi:type="HttpImageDisplayItem"><Name>Web</Name><FilePath>logo.png</FilePath><RelativePath>true</RelativePath></DisplayItem>""");

        var envelope = _kit.ReadArchive(V1Archive(xml, ("assets/logo.png", logo)));

        Assert.Equal(["httpImage"], envelope.Items.Select(i => i.Kind));
        Assert.Empty(envelope.Assets);
        Assert.DoesNotContain(envelope.Diagnostics, d => d.Path.StartsWith("/assets/", StringComparison.Ordinal));
        Assert.False(_kit.Stored(logo));
        Assert.Equal(0, _kit.BlobCount);
    }

    [Fact]
    public void Media_OverMediaBytes_IsNotStored()
    {
        // The workspace's mediaBytes is 32 MiB; one byte over is refused before the media check runs.
        var over = new byte[32 * 1024 * 1024 + 1];
        var envelope = _kit.ReadArchive(V1Archive(Items(Image("Huge", "huge.png")), ("assets/huge.png", over)));

        Assert.Equal(("import.asset.media", "limit.mediaBytes"), (envelope.Assets[0].Code, envelope.Assets[0].Reason));
        Assert.Equal(0, _kit.BlobCount);
    }

    [Fact]
    public void VideoStub_IsNotStored()
    {
        // The media check comes before storing: a video (until B4-media) never reaches the blob store.
        var envelope = _kit.ReadArchive(V1Archive(Items(Image("Clip", "clip.mp4")), ("assets/clip.mp4", VideoStub)));

        Assert.Equal("import.asset.media", envelope.Assets[0].Code);
        Assert.False(_kit.Stored(VideoStub));
        Assert.Equal(0, _kit.BlobCount);
    }

    /// <summary>A 1.x folder in the kit's temp folder with one profile's assets.</summary>
    private (string Root, Guid Id, string Assets) Folder()
    {
        var root = _kit.Temp.Combine("v1");
        var id = Guid.Parse("6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7");
        var assets = Path.Combine(root, "assets", id.ToString());
        Directory.CreateDirectory(assets);
        return (root, id, assets);
    }

    private IReadOnlyList<V1Asset> Resolve(string root, Guid id, params (string Path, bool Relative)[] uses)
    {
        var context = _kit.Context();
        var diagnostics = new ImportDiagnostics(1000);
        return V1Assets.Resolve(uses.Select(u => new V1AssetUse(u.Path, u.Relative, "/items/0", "Item")).ToList(),
            new V1FolderAssets(root, id, context.Budget), _kit.Workspace, context.Budget, diagnostics);
    }

    [Fact]
    public void Folder_NamesMatchCaseInsensitively_AtEveryStep()
    {
        var (root, id, assets) = Folder();
        Directory.CreateDirectory(Path.Combine(assets, "Sub"));
        File.WriteAllBytes(Path.Combine(assets, "Sub", "LOGO.PNG"), Asset("logo.png"));

        var resolved = Resolve(root, id, ("sub\\logo.png", true), ("SUB/Logo.png", true));

        Assert.All(resolved, a => Assert.Equal(BlobStore.IdOf(Asset("logo.png")), a.Blob!.Id));
    }

    [Fact]
    public void Folder_ARelativePathLeavingTheAssets_IsNeverRead()
    {
        // A file one folder up exists; the path that reaches it is refused before anything is looked up.
        var (root, id, assets) = Folder();
        var secret = Asset("gauge-3.png");
        File.WriteAllBytes(Path.Combine(root, "assets", "secret.png"), secret);
        File.WriteAllBytes(Path.Combine(root, "secret.png"), secret);

        var resolved = Resolve(root, id, ("../secret.png", true), ("x/../../../secret.png", true));

        Assert.All(resolved, a => Assert.Equal("import.asset.path", a.Code));
        Assert.False(_kit.Stored(secret));
    }

    [Fact]
    public void Folder_AbsolutePaths_AreNeverRead_TheSentinelStaysUnstored()
    {
        // 1.1.x wrote absolute paths; a real image at one is never read, from any spelling of it.
        var (root, id, _) = Folder();
        var sentinel = _kit.Temp.Combine("sentinel.png");
        var bytes = Asset("gauge-4.png");
        File.WriteAllBytes(sentinel, bytes);

        var resolved = Resolve(root, id, (sentinel, false), (sentinel, true), (sentinel.Replace('/', '\\'), true), ("file://" + sentinel, false));

        Assert.Equal(["import.asset.absolute", "import.asset.absolute", "import.asset.absolute", "import.asset.path"], resolved.Select(a => a.Code));
        Assert.False(_kit.Stored(bytes));
        Assert.Equal(0, _kit.BlobCount);
    }

    [Fact]
    public void Folder_Links_AreNeverFollowed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs privileges on Windows.");
        var (root, id, assets) = Folder();
        var target = _kit.Temp.Combine("outside.png");
        var bytes = Asset("gauge-2.png");
        File.WriteAllBytes(target, bytes);
        File.CreateSymbolicLink(Path.Combine(assets, "link.png"), target);
        var outside = _kit.Temp.Combine("outside-dir");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "in.png"), bytes);
        Directory.CreateSymbolicLink(Path.Combine(assets, "dir"), outside);

        var resolved = Resolve(root, id, ("link.png", true), ("dir/in.png", true), ("LINK.PNG", true));

        Assert.All(resolved, a => Assert.Equal("import.asset.link", a.Code));
        Assert.False(_kit.Stored(bytes));
    }

    [Fact]
    public void Folder_AFifo_IsMissing_WithoutWaiting()
    {
        Assert.SkipUnless(Fifo.Supported, "Windows has no FIFOs in folders.");
        var (root, id, assets) = Folder();
        var fifo = Path.Combine(assets, "pipe.png");
        Fifo.Create(fifo);

        var asset = Assert.Single(Fifo.Within(10, () => Resolve(root, id, ("pipe.png", true)), fifo));

        Assert.Equal("import.asset.missing", asset.Code);
        Assert.Null(asset.Blob);
        Assert.Equal(0, _kit.BlobCount);
    }

    [Fact]
    public void Folder_ALinkedAssetsFolder_IsNeverFollowed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs privileges on Windows.");
        var root = _kit.Temp.Combine("v1-linked");
        var id = Guid.NewGuid();
        var elsewhere = _kit.Temp.Combine("elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "a.png"), Asset("logo.png"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        Directory.CreateSymbolicLink(Path.Combine(root, "assets", id.ToString()), elsewhere);

        Assert.Equal("import.asset.link", Resolve(root, id, ("a.png", true))[0].Code);
    }

    [Fact]
    public void Folder_MissingFiles_AndAMissingAssetsFolder_AreMissing()
    {
        var (root, id, assets) = Folder();
        Directory.CreateDirectory(Path.Combine(assets, "dir.png"));

        Assert.Equal(["import.asset.missing", "import.asset.missing"], Resolve(root, id, ("nope.png", true), ("dir.png", true)).Select(a => a.Code));
        Assert.Equal("import.asset.missing", Resolve(root, Guid.NewGuid(), ("nope.png", true))[0].Code);
    }

    [Fact]
    public void Folder_ReadsCountAgainstTheRequest_AndTheRestAreNotStored()
    {
        // Folder asset reads count toward the request's bytes: once over, the remaining assets are left out with
        // archive.expanded as their reason.
        var (root, id, assets) = Folder();
        File.WriteAllBytes(Path.Combine(assets, "a.png"), Asset("logo.png"));
        File.WriteAllBytes(Path.Combine(assets, "b.png"), Asset("gauge-0.png"));
        File.WriteAllBytes(Path.Combine(assets, "c.png"), Asset("gauge-1.png"));
        var context = _kit.Context(new ImportLimits { ExpandedBytes = Asset("logo.png").Length + 100 });
        var diagnostics = new ImportDiagnostics(1000);

        var resolved = V1Assets.Resolve(
            [new("a.png", true, "/items/0", null), new("b.png", true, "/items/1", null), new("c.png", true, "/items/2", null)],
            new V1FolderAssets(root, id, context.Budget), _kit.Workspace, context.Budget, diagnostics);

        Assert.NotNull(resolved[0].Blob);
        Assert.Equal([("import.asset.media", "archive.expanded"), ("import.asset.media", "archive.expanded")], resolved.Skip(1).Select(a => (a.Code!, a.Reason!)));
    }
}
