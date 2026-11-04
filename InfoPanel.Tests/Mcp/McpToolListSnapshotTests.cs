using System.Text.Encodings.Web;
using System.Text.Json;
using InfoPanel.Mcp.Tools;
using Xunit;

namespace InfoPanel.Tests.Mcp;

/// <summary>
/// The engine's built-in MCP tool list (<see cref="McpToolHost.BuiltInList()"/>: names, closed input schemas, the built-in
/// wording and the instructions, never a <c>--mcp-text</c> folder's overrides) against the committed snapshot
/// <c>InfoPanel.Tests/Snapshots/mcp-tools.json</c>, so a client can copy the list and a change to a tool shows up as a
/// reviewed diff. <c>UPDATE_SNAPSHOTS=1 dotnet test InfoPanel.slnx --filter McpToolListSnapshot</c> rewrites it.
/// </summary>
public class McpToolListSnapshotTests
{
    public const string SnapshotPath = "InfoPanel.Tests/Snapshots/mcp-tools.json";

    private const string RewriteCommand = "UPDATE_SNAPSHOTS=1 dotnet test InfoPanel.slnx --filter McpToolListSnapshot";

    /// <summary>Indented, LF line endings on every OS, characters written as they are.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The list in the order <see cref="McpToolHost.ToolNames"/> gives, then a trailing newline.</summary>
    public static string Serialize() => McpToolHost.BuiltInList().ToJsonString(Options) + "\n";

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "InfoPanel.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("InfoPanel.slnx not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void BuiltInToolList_MatchesSnapshot()
    {
        var path = Path.Combine(RepoRoot(), SnapshotPath);
        var actual = Serialize();

        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || File.ReadAllText(path).ReplaceLineEndings("\n") != actual)
                File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"{SnapshotPath} is missing; run {RewriteCommand}.");
        var expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.True(expected == actual,
            $"The built-in MCP tool list differs from {SnapshotPath}; if the change is intended, run {RewriteCommand} and review the diff.");
    }

    [Fact]
    public void BuiltInToolList_IsStable()
    {
        Assert.Equal(Serialize(), Serialize());
    }

    [Fact]
    public void BuiltInToolList_ListsEveryTool_InOrder()
    {
        var names = McpToolHost.BuiltInList()["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>());
        Assert.Equal(McpToolHost.ToolNames, names);
    }
}
