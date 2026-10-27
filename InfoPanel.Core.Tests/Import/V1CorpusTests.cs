using System.Diagnostics;
using InfoPanel.Import;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// Opt-in: real 1.x data, never committed (shared themes have their own licences). Set
/// <c>INFOPANEL_V1_CORPUS=&lt;dir&gt;</c>: every <c>.infopanel</c> file under it, and every 1.x folder (one holding
/// <c>profiles.xml</c> or <c>settings.xml</c>) with each of its profiles, must give an envelope or a refusal with an
/// <c>archive.*</c> or <c>import.*</c> code, let no other exception escape, finish within 10 s each, and use only
/// diagnostic codes of the catalogue. Translating and rendering them is a client's.
/// </summary>
public sealed class V1CorpusTests(ITestOutputHelper output)
{
    /// <summary>The diagnostics catalogue: every code an envelope or a folder's state may carry.</summary>
    internal static readonly HashSet<string> Catalogue =
    [
        "import.profiles", "import.profile.duplicate", "import.settings", "import.settings.backup", "import.setting", "import.version",
        "import.record", "import.autosave", "import.items", "import.xml", "import.itemType", "import.nil", "import.element", "import.size",
        "import.font", "import.sensor", "import.asset.path", "import.asset.absolute", "import.asset.missing", "import.asset.link",
        "import.asset.media", "import.asset.duplicate", "import.asset.unused", "import.entry", "more",
    ];

    private static readonly TimeSpan Each = TimeSpan.FromSeconds(10);

    [Fact]
    public void EveryArchiveAndFolder_ReadsOrIsRefusedWithACode()
    {
        var corpus = Environment.GetEnvironmentVariable("INFOPANEL_V1_CORPUS");
        Assert.SkipWhen(string.IsNullOrEmpty(corpus), "Reads real 1.x data: set INFOPANEL_V1_CORPUS=<dir> to run it.");
        Assert.True(Directory.Exists(corpus), $"INFOPANEL_V1_CORPUS={corpus} is not a folder.");

        using var kit = new ImportKit();
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        var archives = 0;
        foreach (var file in Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".infopanel", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            archives++;
            var name = Path.GetRelativePath(corpus, file);
            var clock = Stopwatch.StartNew();
            try
            {
                using var stream = File.OpenRead(file);
                var envelope = V1Reader.ReadArchive(stream, new V1ReadContext(kit.Workspace, kit.Sensors));
                Record(name, envelope.Diagnostics, codes);
                output.WriteLine($"{name}: {envelope.Items.Count} items, {envelope.Guides.Count} guides, {envelope.Assets.Count(a => a.Blob is not null)}/{envelope.Assets.Count} assets stored, {clock.ElapsedMilliseconds} ms");
            }
            catch (ImportRefusal refusal)
            {
                Assert.True(refusal.Code.StartsWith("archive.") || refusal.Code.StartsWith("import."), $"{name}: refused with {refusal.Code}");
                output.WriteLine($"{name}: refused, {refusal.Code}: {refusal.Message}");
            }
            Assert.True(clock.Elapsed < Each, $"{name} took {clock.Elapsed}.");
        }

        var folders = 0;
        foreach (var directory in new[] { corpus }.Concat(Directory.EnumerateDirectories(corpus, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal))
        {
            var folder = new V1Folder(directory);
            if (!folder.HasData)
                continue;
            folders++;
            var name = Path.GetRelativePath(corpus, directory);
            var clock = Stopwatch.StartNew();
            var state = folder.GetState(kit.Data);
            Record(name, state.Diagnostics ?? [], codes);
            Assert.True(clock.Elapsed < Each, $"{name} took {clock.Elapsed}.");
            output.WriteLine($"{name}: {state.State}, {state.Profiles?.Count ?? 0} profiles, settings {(state.Settings is null ? "none" : "version " + state.Settings.Version)}");

            foreach (var profile in state.Profiles ?? [])
            {
                clock.Restart();
                try
                {
                    var envelope = folder.ReadProfile(Guid.Parse(profile.Id), new V1ReadContext(kit.Workspace, kit.Sensors));
                    Assert.NotNull(envelope);
                    Record($"{name}/{profile.Id}", envelope.Diagnostics, codes);
                    output.WriteLine($"  {profile.Id}: {envelope.Items.Count} items, {envelope.Assets.Count(a => a.Blob is not null)}/{envelope.Assets.Count} assets stored, {clock.ElapsedMilliseconds} ms");
                }
                catch (ImportRefusal refusal)
                {
                    Assert.Equal("import.version", refusal.Code);
                    output.WriteLine($"  {profile.Id}: refused, {refusal.Code}");
                }
                Assert.True(clock.Elapsed < Each, $"{name}/{profile.Id} took {clock.Elapsed}.");
            }
        }

        output.WriteLine($"{archives} archives, {folders} folders; codes: {string.Join(", ", codes.OrderBy(c => c.Key).Select(c => $"{c.Key} ×{c.Value}"))}");
        Assert.True(archives + folders > 0, "The corpus holds no .infopanel file and no 1.x folder.");
    }

    private static void Record(string name, IEnumerable<ImportDiagnostic> diagnostics, Dictionary<string, int> codes)
    {
        foreach (var diagnostic in diagnostics)
        {
            Assert.True(Catalogue.Contains(diagnostic.Code), $"{name}: {diagnostic.Code} is not in the catalogue.");
            Assert.Contains(diagnostic.Severity, new[] { "error", "warning", "info" });
            codes[diagnostic.Code] = codes.GetValueOrDefault(diagnostic.Code) + 1;
        }
    }
}
