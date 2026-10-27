using InfoPanel.Editing;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace InfoPanel.Core.Tests.Editing;

/// <summary>
/// The 1.x kind manifest against the committed snapshot <c>InfoPanel.Core.Tests/Snapshots/kinds.json</c>: the item JSON
/// of the 14 1.x kinds (properties and defaults), which a 1.x import reads, so a change to the kinds or their properties
/// shows up as a reviewed diff. The engine no longer serves it (<c>GET /api/kinds</c> went with step 1's editing API).
/// <c>UPDATE_SNAPSHOTS=1 dotnet test InfoPanel.slnx --filter KindManifest</c> rewrites the snapshot.
/// </summary>
public class KindManifestTests
{
    public const string SnapshotPath = "InfoPanel.Core.Tests/Snapshots/kinds.json";

    /// <summary>The API's JSON shape (camelCase, enums as strings, nulls omitted outside <c>defaults</c>), indented with LF.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Kinds in <see cref="ItemKinds.All"/> order; properties (and defaults) base class first, in declaration order.</summary>
    public static string Serialize() => JsonSerializer.Serialize(ItemKinds.BuildManifest(), Options) + "\n";

    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "InfoPanel.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("InfoPanel.slnx not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void KindManifest_MatchesSnapshot()
    {
        var path = Path.Combine(RepoRoot(), SnapshotPath);
        var actual = Serialize();

        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || File.ReadAllText(path) != actual)
                File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"{SnapshotPath} is missing; run UPDATE_SNAPSHOTS=1 dotnet test InfoPanel.slnx --filter KindManifest.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void KindManifest_IsStable()
    {
        Assert.Equal(Serialize(), Serialize());
    }
}
