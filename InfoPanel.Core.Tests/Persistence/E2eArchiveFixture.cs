using System.IO.Compression;
using InfoPanel.Editing;
using InfoPanel.Models;
using InfoPanel.Persistence;
using Xunit;

namespace InfoPanel.Core.Tests.Persistence;

/// <summary>
/// The web client's end-to-end fixture <c>e2e/fixtures/all-kinds.infopanel</c>: the frozen 1.x "Desk Overlay" profile
/// and its all-kinds items, packaged as 1.x's <c>ExportProfile</c> wrote them on Windows. Rewrite it with
/// <c>UPDATE_FIXTURES=1 dotnet test InfoPanel.slnx --filter FullyQualifiedName~E2eArchiveFixture</c>; the other tests
/// here check the committed file still matches and imports with every kind and its images. With UPDATE_FIXTURES=1
/// every test here rewrites the file first (once per run), so they pass in any order against the new archive.
/// </summary>
public class E2eArchiveFixture
{
    private const string ArchiveRelativePath = "client/e2e/fixtures/all-kinds.infopanel";

    /// <summary>Fixed entry times, so regenerating an unchanged archive leaves its contents unchanged.</summary>
    private static readonly DateTimeOffset EntryTime = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The archive's assets: the file names the fixture items use (<c>FilePath</c>, relative), each taken from
    /// <c>Fixtures/assets</c>. The gauge's three frames are the first, middle and last of the five fixture frames.
    /// </summary>
    private static readonly (string Name, string Source)[] Assets =
    [
        ("anim.gif", "blink.gif"),
        ("g0.png", "gauge-0.png"),
        ("g1.png", "gauge-2.png"),
        ("g2.png", "gauge-4.png"),
        ("icon.svg", "icon.svg"),
        ("logo.png", "logo.png"),
    ];

    private static readonly bool Updating = Environment.GetEnvironmentVariable("UPDATE_FIXTURES") == "1";

    /// <summary>With UPDATE_FIXTURES=1, the archive written once, before any test here reads it.</summary>
    private static readonly Lazy<string> Generated = new(() =>
    {
        var path = ArchivePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Build());
        return path;
    });

    /// <summary>The committed archive's path, rewritten first when UPDATE_FIXTURES=1.</summary>
    private static string Archive() => Updating ? Generated.Value : ArchivePath();

    [Fact]
    public void Generate()
    {
        Assert.SkipUnless(Updating, "Set UPDATE_FIXTURES=1 to rewrite client/e2e/fixtures/all-kinds.infopanel.");
        Assert.True(File.Exists(Archive()));
    }

    /// <summary>The committed archive holds exactly the entries the generator writes, byte for byte.</summary>
    [Fact]
    public void CommittedArchive_MatchesGenerator()
    {
        var path = Archive();
        Assert.True(File.Exists(path), $"{path} is missing; run the generator (see {nameof(E2eArchiveFixture)}).");

        var expected = Entries(Build());
        var actual = Entries(File.ReadAllBytes(path));
        Assert.Equal(expected.Select(e => e.Name), actual.Select(e => e.Name));
        foreach (var (e, a) in expected.Zip(actual))
            Assert.True(e.Bytes.AsSpan().SequenceEqual(a.Bytes), $"{a.Name} differs from what the generator writes.");
    }

    /// <summary>
    /// The committed archive imports as the 2.0 engine imports it, with all 14 kinds (counting nested items) and
    /// every relative image path pointing at a packaged asset.
    /// </summary>
    [Fact]
    public void CommittedArchive_ImportsWithEveryKindAndItsImages()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(new DataPaths(temp.Combine("data")));

        var result = InfoPanelArchive.Import(Archive(), store);

        Assert.Equal("[Import] Desk Overlay", result.Profile.Name);
        Assert.Equal((800, 480), (result.Profile.Width, result.Profile.Height));
        Assert.Equal(Assets.Length, result.AssetCount);
        Assert.Empty(result.SkippedEntries);

        var items = ItemTree.Walk(store.LoadItems(result.Profile)).ToList();
        Assert.Equal(ItemKinds.All.Select(k => k.Id).Order(), items.Select(i => ItemKinds.Of(i).Id).Distinct().Order());

        var assets = store.GetProfileAssetsDirectory(result.Profile.Guid);
        var relative = items.OfType<ImageDisplayItem>()
            .Where(i => i.Type == ImageDisplayItem.ImageType.FILE && i.RelativePath && !string.IsNullOrEmpty(i.FilePath))
            .Select(i => i.FilePath!)
            .ToList();
        Assert.Equal(["anim.gif", "g0.png", "g1.png", "g2.png", "icon.svg", "logo.png"], relative.Order());
        Assert.All(relative, file => Assert.True(File.Exists(Path.Combine(assets, file)), $"{file} is not in the imported assets"));
    }

    /// <summary>
    /// 1.x's export: <c>Profile.xml</c> is a new profile with the exported fields (here with a fixed Guid instead of a
    /// random one, which import replaces anyway), <c>DisplayItems.xml</c> is the saved items file as is (the frozen
    /// 1.x fixture, so its items have no ids), and each asset is <c>assets\{name}</c> (<c>Path.Combine</c> on Windows).
    /// </summary>
    private static byte[] Build()
    {
        var source = V1Format.ReadProfiles(ProfileSerializerTests.FixturePath(V1Format.ProfilesFixture))
            .Single(p => p.Guid == FixtureGenerator.DeskProfileGuid);
        var exported = new Profile(source.Name, source.Width, source.Height)
        {
            Guid = source.Guid,
            ShowFps = source.ShowFps,
            BackgroundColor = source.BackgroundColor,
            Font = source.Font,
            FontSize = source.FontSize,
            Color = source.Color,
            OpenGL = source.OpenGL,
            FontScale = source.FontScale,
        };

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var stream = Create(zip, InfoPanelArchive.ProfileEntry))
                ProfileSerializer.WriteProfile(stream, exported);

            using (var stream = Create(zip, InfoPanelArchive.ItemsEntry))
                stream.Write(File.ReadAllBytes(ProfileSerializerTests.FixturePath(V1Format.ItemsFixture)));

            foreach (var (name, file) in Assets)
            {
                using var stream = Create(zip, "assets\\" + name);
                stream.Write(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "assets", file)));
            }
        }

        return output.ToArray();
    }

    private static Stream Create(ZipArchive zip, string name)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        return entry.Open();
    }

    private static List<(string Name, byte[] Bytes)> Entries(byte[] archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        return zip.Entries.Select(e =>
        {
            using var stream = e.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return (e.FullName, copy.ToArray());
        }).ToList();
    }

    private static string ArchivePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "InfoPanel.slnx")))
                return Path.Combine(dir.FullName, ArchiveRelativePath);
        }

        throw new InvalidOperationException("InfoPanel.slnx not found above " + AppContext.BaseDirectory);
    }
}
