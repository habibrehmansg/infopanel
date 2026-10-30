using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Mcp.Text;
using InfoPanel.Mcp.Tools;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfoPanel.Tests.Mcp;

/// <summary>
/// The text layers (<see cref="McpText"/>): the built-in text, the live folder over it read on every call, and the live
/// folder's boundary: catalogued ids, contained regular files, checked shapes and ranges.
/// </summary>
public sealed class McpTextTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipt-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ListLogger _log = new();

    public McpTextTests()
    {
        Directory.CreateDirectory(Path.Combine(Live, "guide"));
        Directory.CreateDirectory(Path.Combine(Live, "examples"));
    }

    private string Live => Path.Combine(_root, "live");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private McpText Text() => new(Live, McpToolHost.ToolNames.ToHashSet(), _log);

    private void Write(string relative, string content) => File.WriteAllText(Path.Combine(Live, relative), content);

    private async Task<(string Text, bool IsError)> Guide(McpText text, string arguments)
    {
        var tool = new GetDesignGuideTool(text);
        using var json = JsonDocument.Parse(arguments);
        try
        {
            var result = await tool.CallAsync(ToolArguments.Check(tool.Name, tool.InputSchema, json.RootElement), Ct);
            return (result.Content[0]!["text"]!.GetValue<string>(), result.IsError);
        }
        catch (ToolFailure failure)
        {
            return (failure.Message, true);
        }
    }

    [Fact]
    public void TheBuiltInText_HasEveryTool_TheScenesTopic_AndTheThresholds()
    {
        var text = new McpText(null, McpToolHost.ToolNames.ToHashSet(), _log);
        var tools = text.Tools();
        Assert.Equal(McpToolHost.ToolNames.Order(), tools.Descriptions.Keys.Order());
        Assert.Contains("profile", tools.Instructions);
        Assert.Equal("The scene format in short", Assert.Single(text.Topics(), t => t.Id == "scenes").Title);
        Assert.Contains("infopanel.scene/1", text.Topic("scenes"));
        Assert.Equal(new CheckThresholds(1, 0.1, 4.5), text.Checks());
        Assert.Null(text.Palettes());
        Assert.Empty(text.Examples());
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void ALiveToolsFile_MergesPerTool_AndIsReadOnEveryCall()
    {
        var text = Text();
        var builtIn = text.Tools();
        Write("tools.json", """{"tools":{"get_profile":{"description":"Live wording."},"not_a_tool":{"description":"ignored"}}}""");
        var live = text.Tools();
        Assert.Equal("Live wording.", live.Descriptions["get_profile"]);
        Assert.Equal(builtIn.Descriptions["list_profiles"], live.Descriptions["list_profiles"]);
        Assert.Equal(builtIn.Instructions, live.Instructions);
        Assert.False(live.Descriptions.ContainsKey("not_a_tool"));

        Write("tools.json", """{"instructions":"Live instructions.","tools":{"get_profile":{"description":"Changed again."}}}""");
        var again = text.Tools();
        Assert.Equal("Changed again.", again.Descriptions["get_profile"]);
        Assert.Equal("Live instructions.", again.Instructions);
    }

    [Fact]
    public void ALiveGuideTopic_ReplacesTheBuiltInOne_AndANewOneIsAdded()
    {
        var text = Text();
        Write(Path.Combine("guide", "scenes.md"), "# Scenes, the owner's way\nUse few colours.");
        Write(Path.Combine("guide", "design.md"), "# Design\nNever more than three colours.");
        Write(Path.Combine("guide", "Not-An-Id.md"), "# ignored");
        Write(Path.Combine("guide", "notes.txt"), "ignored");
        Assert.Equal(["design", "scenes"], text.Topics().Select(t => t.Id));
        Assert.Equal("Scenes, the owner's way", text.Topics().Single(t => t.Id == "scenes").Title);
        Assert.Contains("three colours", text.Topic("design"));
        Assert.Contains("few colours", text.Topic("scenes"));
        Assert.Null(text.Topic("Not-An-Id"));
    }

    [Theory]
    [InlineData("tools.json", "{ not json")]
    [InlineData("tools.json", """{"tools":{"get_profile":{"description":42}}}""")]
    [InlineData("tools.json", """{"instructions":["a"]}""")]
    public void ABrokenLiveFile_FallsBackToTheLayerBelow_AndIsLoggedOnce(string file, string content)
    {
        var text = Text();
        var builtIn = new McpText(null, McpToolHost.ToolNames.ToHashSet(), new ListLogger()).Tools();
        Write(file, content);
        Assert.Equal(builtIn.Descriptions["get_profile"], text.Tools().Descriptions["get_profile"]);
        Assert.Equal(builtIn.Descriptions["get_profile"], text.Tools().Descriptions["get_profile"]);
        Assert.Contains(file, Assert.Single(_log.Lines));
    }

    [Fact]
    public void ADescriptionOver4KiB_SkipsTheToolsFile()
    {
        var text = Text();
        Write("tools.json", new JsonObject { ["tools"] = new JsonObject { ["get_profile"] = new JsonObject { ["description"] = new string('x', 4097) } } }.ToJsonString());
        Assert.NotEqual(new string('x', 4097), text.Tools().Descriptions["get_profile"]);
        Assert.Contains("4096", Assert.Single(_log.Lines));
    }

    [Fact]
    public void AFileOver1MiB_IsIgnored()
    {
        var text = Text();
        Write(Path.Combine("guide", "huge.md"), "# Huge\n" + new string('x', McpText.MaxFileBytes));
        Assert.Null(text.Topic("huge"));
        Assert.Contains(_log.Lines, l => l.Contains("huge.md") && l.Contains("over"));
    }

    [Theory]
    [InlineData("""{"contrastMin":"x"}""")]
    [InlineData("""{"contrastMin":-1}""")]
    [InlineData("""{"contrastMin":99}""")]
    [InlineData("""{"overlapMin":1.5}""")]
    [InlineData("""{"canvasTolerance":51}""")]
    [InlineData("""[1,2]""")]
    public void AChecksFileOutOfShapeOrRange_FallsBackToTheBuiltInThresholds_AndIsLogged(string content)
    {
        var text = Text();
        Write("checks.json", content);
        Assert.Equal(new CheckThresholds(1, 0.1, 4.5), text.Checks());
        Assert.Contains("checks.json", Assert.Single(_log.Lines));
    }

    [Fact]
    public void AChecksFileInRange_IsUsed_OverTheBuiltInOnes()
    {
        var text = Text();
        Write("checks.json", """{"contrastMin":7,"overlapMin":0.25}""");
        Assert.Equal(new CheckThresholds(1, 0.25, 7), text.Checks());
        Assert.Empty(_log.Lines);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("../../x")]
    [InlineData("/etc/hosts")]
    [InlineData("guide/x")]
    [InlineData("X.md")]
    [InlineData("scenes.md")]
    [InlineData("nothing-here")]
    public async Task ATopicOutsideTheCatalogue_IsAnErrorNamingTheTopics(string topic)
    {
        File.WriteAllText(Path.Combine(_root, "x.md"), "# Outside\nsecret");
        var (text, isError) = await Guide(Text(), new JsonObject { ["topic"] = topic }.ToJsonString());
        Assert.True(isError);
        Assert.Contains("(unknownTopic)", text);
        Assert.Contains("The topics are: scenes", text);
        Assert.DoesNotContain("secret", text);
    }

    [Fact]
    public async Task AGuideLinkToAFileOutsideTheFolder_IsRefused()
    {
        if (OperatingSystem.IsWindows())
            return;
        var outside = Path.Combine(_root, "private.md");
        File.WriteAllText(outside, "# Leak\nthe owner's secret");
        File.CreateSymbolicLink(Path.Combine(Live, "guide", "leak.md"), outside);
        // A link that stays inside the folder is fine.
        Write("inside.md", "# Inside\nshared text");
        File.CreateSymbolicLink(Path.Combine(Live, "guide", "inside.md"), Path.Combine(Live, "inside.md"));

        var text = Text();
        Assert.Null(text.Topic("leak"));
        Assert.DoesNotContain(text.Topics(), t => t.Id == "leak");
        Assert.Contains("shared text", text.Topic("inside"));
        var (answer, isError) = await Guide(text, """{"topic":"leak"}""");
        Assert.True(isError);
        Assert.DoesNotContain("secret", answer);
        Assert.Contains(_log.Lines, l => l.Contains("leak.md") && l.Contains("leaves the folder"));
    }

    [Fact]
    public async Task AFifoNamedLikeATopic_DoesntBlockTheCall()
    {
        if (OperatingSystem.IsWindows())
            return;
        using (var mkfifo = Process.Start("/usr/bin/mkfifo", Path.Combine(Live, "guide", "fifo.md")))
            await mkfifo.WaitForExitAsync(Ct);
        using (var mkfifo = Process.Start("/usr/bin/mkfifo", Path.Combine(Live, "tools.json")))
            await mkfifo.WaitForExitAsync(Ct);

        // Each on its own thread, so a read that blocks fails the test instead of hanging the run.
        var watch = Stopwatch.StartNew();
        var answer = await Task.Run(() => Guide(Text(), """{"topic":"fifo"}"""), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var list = await Task.Run(() => Guide(Text(), "{}"), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var tools = await Task.Run(() => Text().Tools(), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"The calls took {watch.Elapsed}.");
        Assert.True(answer.IsError);
        Assert.False(list.IsError);
        Assert.Contains("scenes", list.Text);
        Assert.NotEmpty(tools.Descriptions);
        Assert.Contains(_log.Lines, l => l.Contains("fifo.md") && l.Contains("regular file"));
    }

    [Fact]
    public async Task PalettesAndExamples_AreServedFromTheirCatalogue()
    {
        var text = Text();
        Write("palettes.json", """{"palettes":[{"name":"Night","colors":["#000000","#ffb000"]}]}""");
        Write(Path.Combine("examples", "cpu-card.json"), """{"description":"A CPU card","item":{}}""");
        Write(Path.Combine("examples", "bad.json"), """{"description":7}""");
        var (index, _) = await Guide(text, "{}");
        Assert.Contains("- palettes:", index);
        Assert.Contains("- cpu-card: A CPU card", index);
        Assert.DoesNotContain("bad", index);
        Assert.Contains("Night", (await Guide(text, """{"topic":"palettes"}""")).Text);
        Assert.Contains("A CPU card", (await Guide(text, """{"example":"cpu-card"}""")).Text);
        var (missing, isError) = await Guide(text, """{"example":"../tools"}""");
        Assert.True(isError);
        Assert.Contains("The examples are: cpu-card", missing);
    }

    /// <summary>Records each log line at warning and above.</summary>
    private sealed class ListLogger : ILogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                    return [.. _lines];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                lock (_lines)
                    _lines.Add(formatter(state, exception));
        }
    }
}
