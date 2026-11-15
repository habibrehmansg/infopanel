using System.Text.Json;
using InfoPanel.Tests.Editing;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// <c>LICENSES.md</c> against what the builds carry. Each of its build sections lists exactly the NuGet packages, id
/// and version, that its project's restore graph (<c>obj/project.assets.json</c>, written by the restore every build
/// runs) puts files of into the output: a package added, removed or bumped fails here until the list follows. A
/// package counts when it has runtime or native files, or runtime-specific ones for a runtime that build ships for (the
/// sensor helper runs on Windows only); a package whose files are all placeholders (<c>_._</c>)
/// counts for nothing. The last section lists, by name, what isn't in those graphs (<see cref="NotFromTheGraphs"/>).
/// </summary>
public class LicensesListTests
{
    private const string ListPath = "LICENSES.md";

    /// <summary>A build section's heading, the project whose graph it lists, and the runtimes its files count for.</summary>
    private static readonly (string Heading, string Project, string RuntimePrefix)[] BuildSections =
    [
        ("## The engine", "InfoPanel", ""),
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

    /// <summary>
    /// The entries the last section holds, by name (see <see cref="Names"/>): the .NET runtime, the video libraries, the USB
    /// library, the media helper, the driver's installer, the fonts (Inter, Inter Tabular, DSEG7 Classic: each its own entry).
    /// </summary>
    private static readonly string[] NotFromTheGraphs = [".NET runtime and ASP.NET Core", "FFmpeg", "dav1d", "libusb", "mediaremote-adapter", "PawnIO installer", "Inter", "Inter Tabular", "DSEG7 Classic"];

    /// <summary>The section that holds the licence text the bundled fonts share, and the fonts whose entries name it.</summary>
    private const string FontLicenceSection = "## SIL Open Font License, version 1.1 (the bundled fonts)";

    private static readonly string[] BundledFonts = ["Inter", "Inter Tabular", "DSEG7 Classic"];

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
        var listed = Names(OtherSection);
        Assert.Equal(NotFromTheGraphs.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The fonts' licence is the full text, once, in a section of its own: the licence body of <c>InfoPanel.Core/Fonts/OFL.txt</c>
    /// byte for byte (line endings aside), which the other bundled licence file, <c>DSEG-OFL.txt</c>, has too; the packages
    /// carry this list and not those files, so each font's entry says the text is here.
    /// </summary>
    [Fact]
    public void Fonts_licence_text_is_in_the_list_whole_and_each_font_entry_names_its_section()
    {
        var fonts = Path.Combine(LiveEngine.RepoRoot(), "InfoPanel.Core", "Fonts");
        var body = LicenceBody(Path.Combine(fonts, "OFL.txt"));
        Assert.Equal(body, LicenceBody(Path.Combine(fonts, "DSEG-OFL.txt")));

        var lines = Lines();
        var start = Array.IndexOf(lines, FontLicenceSection);
        Assert.True(start >= 0, $"{ListPath} has no section \"{FontLicenceSection}\"");
        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        var section = string.Join('\n', lines[start..(end < 0 ? lines.Length : end)]);
        Assert.True(section.Contains("\n" + body + "\n", StringComparison.Ordinal), $"the section doesn't hold the licence text of OFL.txt as it is");

        var problems = new List<string>();
        foreach (var font in BundledFonts)
        {
            var entry = EntryLines(OtherSection, font);
            if (!entry.Any(l => l.StartsWith("- **Licence**", StringComparison.Ordinal) && l.Contains(FontLicenceSection[3..], StringComparison.Ordinal)))
                problems.Add($"{font}'s entry doesn't name the section \"{FontLicenceSection[3..]}\" in its Licence line");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>A licence file's text from its licence's first line on, line endings as <c>\n</c>, without trailing ones.</summary>
    private static string LicenceBody(string path)
    {
        var text = File.ReadAllText(path).ReplaceLineEndings("\n");
        var start = text.IndexOf("SIL OPEN FONT LICENSE Version 1.1", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{path} has no licence text");
        return text[start..].TrimEnd('\n');
    }

    /// <summary>The lines of the entry named <paramref name="name"/> under the section whose heading starts with <paramref name="heading"/>.</summary>
    private static List<string> EntryLines(string heading, string name)
    {
        var lines = new List<string>();
        var inside = false;
        var mine = false;
        foreach (var line in Lines())
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
                inside = line.StartsWith(heading, StringComparison.Ordinal);
            else if (inside && line.StartsWith("### ", StringComparison.Ordinal))
                mine = NameOf(line[4..]) == name;
            else if (inside && mine)
                lines.Add(line);
        }
        Assert.True(lines.Count > 0, $"{ListPath} has no entry \"{name}\" under \"{heading}\"");
        return lines;
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

    /// <summary>The entries' full names under the section: a heading's words before its version (its first word that starts with a digit), the whole heading when it has none.</summary>
    private static List<string> Names(string heading)
    {
        var names = new List<string>();
        var inside = false;
        foreach (var line in Lines())
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
                inside = line.StartsWith(heading, StringComparison.Ordinal);
            else if (inside && line.StartsWith("### ", StringComparison.Ordinal))
                names.Add(NameOf(line[4..]));
        }
        return names;
    }

    private static string NameOf(string entryHeading)
    {
        var words = entryHeading.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var version = Array.FindIndex(words, w => char.IsAsciiDigit(w[0]));
        return string.Join(' ', version > 0 ? words[..version] : words);
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
