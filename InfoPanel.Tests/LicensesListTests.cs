using System.Text.Json;
using InfoPanel.Tests.Editing;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// <c>LICENSES.md</c> against what the builds carry. Each of its build sections lists exactly the NuGet packages, id
/// and version, that its project's restore graph (<c>obj/project.assets.json</c>, written by the restore every build
/// runs) puts files of into the output: a package added, removed or bumped fails here until the list follows. A
/// package counts when it has runtime or native files, or runtime-specific ones for a runtime that build ships for (the
/// bundled plugin and the sensor helper run on Windows only); a package whose files are all placeholders (<c>_._</c>)
/// counts for nothing. The last section lists, by name, what isn't in those graphs (<see cref="NotFromTheGraphs"/>).
/// </summary>
public class LicensesListTests
{
    private const string ListPath = "LICENSES.md";

    /// <summary>A build section's heading, the project whose graph it lists, and the runtimes its files count for.</summary>
    private static readonly (string Heading, string Project, string RuntimePrefix)[] BuildSections =
    [
        ("## The engine", "InfoPanel", ""),
        ("## The bundled plugin", "InfoPanel.Extras", "win"),
        ("## The sensor helper", "InfoPanel.SensorHelper", "win"),
    ];

    public static TheoryData<string, string, string> Builds()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (heading, project, runtimePrefix) in BuildSections)
            data.Add(heading, project, runtimePrefix);
        return data;
    }

    private const string OtherSection = "## Not from the restore graphs";

    /// <summary>The entries the last section holds: the .NET runtime, the video libraries, the driver's installer, the font.</summary>
    private static readonly string[] NotFromTheGraphs = [".NET", "FFmpeg", "dav1d", "PawnIO", "Inter", "DSEG7"];

    [Theory]
    [MemberData(nameof(Builds))]
    public void Build_section_lists_exactly_what_its_restore_graph_ships(string heading, string project, string runtimePrefix)
    {
        var listed = Entries(heading).Select(e => (e.Id, e.Version)).ToList();
        var shipped = Shipped(project, runtimePrefix);

        var problems = new List<string>();
        foreach (var (id, version) in shipped.Where(p => !listed.Contains(p)))
            problems.Add($"{id} {version} ships in {project}'s build but isn't listed under \"{heading}\"");
        foreach (var (id, version) in listed.Where(p => !shipped.Contains(p)))
            problems.Add($"\"{heading}\" lists {id} {version}, which {project}'s restore graph doesn't ship");
        foreach (var dup in listed.GroupBy(p => p).Where(g => g.Count() > 1))
            problems.Add($"\"{heading}\" lists {dup.Key.Id} {dup.Key.Version} more than once");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Last_section_lists_what_the_graphs_do_not_hold()
    {
        var listed = Entries(OtherSection).Select(e => e.Id).ToList();
        Assert.Equal(NotFromTheGraphs.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_entry_is_in_a_known_section()
    {
        var known = BuildSections.Select(b => b.Heading).Append(OtherSection).ToList();
        string? section = null;
        var problems = new List<string>();
        foreach (var line in Lines())
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
                section = known.FirstOrDefault(k => line.StartsWith(k, StringComparison.Ordinal));
            else if (line.StartsWith("### ", StringComparison.Ordinal) && section == null)
                problems.Add($"\"{line}\" isn't under one of the sections this test knows");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>The <c>### {id} {version} …</c> entries under the section whose heading starts with <paramref name="heading"/>.</summary>
    private static List<(string Id, string Version)> Entries(string heading)
    {
        var entries = new List<(string, string)>();
        var found = false;
        var inside = false;
        foreach (var line in Lines())
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inside = line.StartsWith(heading, StringComparison.Ordinal);
                found |= inside;
            }
            else if (inside && line.StartsWith("### ", StringComparison.Ordinal))
            {
                var words = line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                entries.Add((words[0], words.Length > 1 ? words[1] : ""));
            }
        }
        Assert.True(found, $"{ListPath} has no section \"{heading}\"");
        return entries;
    }

    private static string[] Lines() =>
        File.ReadAllText(Path.Combine(LiveEngine.RepoRoot(), ListPath)).ReplaceLineEndings("\n").Split('\n');

    /// <summary>The packages, id and version, whose files <paramref name="project"/>'s restore graph puts in its output.</summary>
    private static List<(string Id, string Version)> Shipped(string project, string runtimePrefix)
    {
        var path = Path.Combine(LiveEngine.RepoRoot(), project, "obj", "project.assets.json");
        Assert.True(File.Exists(path), $"{path} is missing: restore the solution first (dotnet build InfoPanel.slnx)");
        using var assets = JsonDocument.Parse(File.ReadAllBytes(path));

        // The graph without a runtime (a restore for one, as a Windows publish runs, adds a "{framework}/{rid}" target).
        var targets = assets.RootElement.GetProperty("targets").EnumerateObject().Where(t => !t.Name.Contains('/')).ToList();
        Assert.True(targets.Count == 1, $"{path}: expected one framework target, found {targets.Count}");

        var shipped = new List<(string, string)>();
        foreach (var library in targets[0].Value.EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package")
                continue;
            if (!HasFiles(library.Value, "runtime") && !HasFiles(library.Value, "native") && !HasRuntimeFiles(library.Value, runtimePrefix))
                continue;
            var slash = library.Name.IndexOf('/');
            shipped.Add((library.Name[..slash], library.Name[(slash + 1)..]));
        }
        return shipped;
    }

    private static bool HasFiles(JsonElement library, string group) =>
        library.TryGetProperty(group, out var files) && files.EnumerateObject().Any(f => !f.Name.EndsWith("/_._", StringComparison.Ordinal));

    private static bool HasRuntimeFiles(JsonElement library, string runtimePrefix) =>
        library.TryGetProperty("runtimeTargets", out var files) && files.EnumerateObject().Any(f =>
            !f.Name.EndsWith("/_._", StringComparison.Ordinal)
            && f.Value.TryGetProperty("rid", out var rid)
            && (rid.GetString() ?? "").StartsWith(runtimePrefix, StringComparison.Ordinal));
}
