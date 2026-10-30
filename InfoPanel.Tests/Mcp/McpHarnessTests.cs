using System.Diagnostics;
using System.Text.Json.Nodes;
using InfoPanel.Hosting;
using InfoPanel.Persistence;
using Xunit;

namespace InfoPanel.Tests.Mcp;

/// <summary>
/// <c>InfoPanel --mcp</c> driven as an AI app drives it, against real engines started as child processes on temp data
/// folders (their channels in the test run folder): attaching, starting, restarting, the older engine, and what the
/// launch record and a stale <c>runtime.json</c> do to a start. Unix only (the channel is a socket there).
/// </summary>
[Collection(McpHarnessCollection.Name)]
public sealed class McpHarnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InitializeAndToolsList_StartNothing_AndStdoutHoldsOnlyTheProtocol()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        folder.RecordQuietStart();
        await using var front = new McpHarness(folder.Data);
        var init = await front.InitializeAsync("2025-06-18");
        Assert.Equal("2025-06-18", init["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal(McpServerVersions.Newest, (await front.InitializeAsync("1999-01-01"))["result"]!["protocolVersion"]!.GetValue<string>());
        var list = await front.RequestAsync("tools/list");
        // Listed without an engine: the executable's own list, every tool with its built-in wording.
        Assert.Equal(["list_profiles", "get_profile", "list_sensors", "render_preview", "get_design_guide"],
            list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
        Assert.All(list["result"]!["tools"]!.AsArray(), t => Assert.False(string.IsNullOrEmpty(t!["description"]!.GetValue<string>())));
        Assert.Contains("profile", init["result"]!["instructions"]!.GetValue<string>());
        await front.RequestAsync("ping");

        // Nothing was started: no engine answers, nothing was written in the data folder.
        await Task.Delay(1500, Ct);
        Assert.Null(await folder.PidAsync());
        Assert.False(Directory.Exists(folder.Data));
        Assert.Equal(0, await front.CloseAsync());
        front.AssertStdoutIsOnlyProtocol();
        Assert.Contains("infopanel-mcp:", front.Stderr);
    }

    [Fact]
    public async Task TheFirstCall_StartsTheEngineWithoutABrowser_InItsOwnSession_AndItOutlivesTheFront()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        folder.RecordQuietStart();
        int engine;
        await using (var front = new McpHarness(folder.Data))
        {
            await front.InitializeAsync();
            var result = await front.CallAsync("no_such_tool");
            var text = McpHarness.TextOf(result);
            Assert.Contains("wasn't running, so it was started", text);
            Assert.Contains("no_such_tool", text);
            Assert.True(result["isError"]!.GetValue<bool>());

            engine = (await folder.PidAsync())!.Value;
            var args = McpHarness.ArgumentsOf(engine);
            Assert.Contains("--no-browser", args);
            Assert.Contains("--mock-seed", args); // from the launch record
            Assert.Contains(folder.Data, args);
            // A session of its own, so the AI app's process group going away doesn't take it along.
            Assert.Equal(engine, McpHarness.getsid(engine));
            Assert.NotEqual(McpHarness.getsid(front.Pid), McpHarness.getsid(engine));

            Assert.Equal(0, await front.CloseAsync());
            front.AssertStdoutIsOnlyProtocol();
        }

        await Task.Delay(500, Ct);
        Assert.True(McpHarness.IsAlive(engine));
        Assert.Equal(engine, await folder.PidAsync());
    }

    [Fact]
    public async Task AnEngineAlreadyRunning_IsAttachedTo_NotStartedAgain()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        var engine = await folder.StartEngineAsync();
        await using var front = new McpHarness(folder.Data);
        await front.InitializeAsync();
        var text = McpHarness.TextOf(await front.CallAsync("no_such_tool"));
        Assert.DoesNotContain("was started", text);
        Assert.Equal(engine.Id, await folder.PidAsync());
        front.AssertStdoutIsOnlyProtocol();
    }

    [Fact]
    public async Task Attached_TheToolsAnswer_ProfilesSensorsAPreviewAndAProfile()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        await folder.StartEngineAsync();
        await using var front = new McpHarness(folder.Data);
        await front.InitializeAsync();
        Assert.Equal(5, (await front.RequestAsync("tools/list"))["result"]!["tools"]!.AsArray().Count);

        var profiles = McpHarness.TextOf(await front.CallAsync("list_profiles"));
        Assert.Contains("\"name\":\"Wide bar\"", profiles); // the first-run example with mock sensors

        var sensors = await front.CallAsync("list_sensors", new JsonObject { ["query"] = "cpu" });
        Assert.False(sensors["isError"]!.GetValue<bool>());
        Assert.Contains("plugin:/infopanel.mock/cpu/", McpHarness.TextOf(sensors));

        var preview = await front.CallAsync("render_preview", new JsonObject { ["profile"] = "Wide bar", ["width"] = 640 });
        var image = preview["content"]!.AsArray().Single(c => c!["type"]!.GetValue<string>() == "image")!;
        Assert.Equal("image/png", image["mimeType"]!.GetValue<string>());
        using (var bitmap = SkiaSharp.SKBitmap.Decode(Convert.FromBase64String(image["data"]!.GetValue<string>())))
            Assert.Equal((640, 160), (bitmap.Width, bitmap.Height));

        var profile = System.Text.Json.Nodes.JsonNode.Parse(McpHarness.TextOf(await front.CallAsync("get_profile", new JsonObject { ["profile"] = "Wide bar" })))!;
        Assert.Equal(1920, profile["width"]!.GetValue<int>());
        Assert.NotEmpty(profile["items"]!.AsArray());

        var missing = await front.CallAsync("get_profile", new JsonObject { ["profile"] = "Nope" });
        Assert.True(missing["isError"]!.GetValue<bool>());
        Assert.Contains("There's no profile Nope", McpHarness.TextOf(missing));
        front.AssertStdoutIsOnlyProtocol();
    }

    [Fact]
    public async Task AnEngineKilledMidSession_IsNamed_AndStartedAgainOnTheNextCall()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        folder.RecordQuietStart();
        await using var front = new McpHarness(folder.Data);
        await front.InitializeAsync();
        await front.CallAsync("no_such_tool");
        var first = (await folder.PidAsync())!.Value;

        McpHarness.Kill(first);
        await McpHarness.WaitUntil(() => Task.FromResult(!McpHarness.IsAlive(first)), TimeSpan.FromSeconds(10), () => "The killed engine is still there.");

        var text = McpHarness.TextOf(await front.CallAsync("no_such_tool"));
        Assert.Contains("had stopped, so it was started again", text);
        var second = (await folder.PidAsync())!.Value;
        Assert.NotEqual(first, second);
        front.AssertStdoutIsOnlyProtocol();
    }

    [Fact]
    public async Task AnEngineHoldingTheLockWithoutAChannel_IsAnOlderEngine()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        folder.RecordQuietStart();
        Directory.CreateDirectory(folder.Data);
        // An engine too old to have the channel: it holds the folder's lock and listens nowhere the front looks.
        using var held = InstanceLock.TryAcquire(Path.Combine(folder.Data, "engine.lock"), out var error);
        Assert.NotNull(held);

        await using var front = new McpHarness(folder.Data, startSeconds: 4);
        await front.InitializeAsync();
        var result = await front.CallAsync("list_profiles");
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("older version that can't talk to AI apps", McpHarness.TextOf(result));
        Assert.Null(await folder.PidAsync());
        front.AssertStdoutIsOnlyProtocol();
    }

    /// <summary>
    /// Triage #8: a <c>runtime.json</c> a crash left, whose pid now belongs to an unrelated live process, never stops a
    /// start: the folder's lock decides, and nobody holds it.
    /// </summary>
    [Fact]
    public async Task AStaleRuntimeFileNamingALiveUnrelatedPid_DoesntStopTheStart()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        folder.RecordQuietStart();
        Directory.CreateDirectory(folder.Data);
        using var sleeper = Process.Start(new ProcessStartInfo("/bin/sleep", "120") { UseShellExecute = false })!;
        try
        {
            File.WriteAllText(Path.Combine(folder.Data, "runtime.json"),
                new JsonObject { ["pid"] = sleeper.Id, ["port"] = 9, ["url"] = "http://127.0.0.1:9" }.ToJsonString());

            await using var front = new McpHarness(folder.Data, startSeconds: 30);
            await front.InitializeAsync();
            var text = McpHarness.TextOf(await front.CallAsync("no_such_tool"));
            Assert.Contains("wasn't running, so it was started", text);
            Assert.DoesNotContain("older version", text);
            var engine = await folder.PidAsync();
            Assert.NotNull(engine);
            Assert.NotEqual(sleeper.Id, engine);
        }
        finally
        {
            sleeper.Kill();
        }
    }

    /// <summary>
    /// Triage #7: an engine started by hand with a web root and a cross origin, then stopped: the engine the next call
    /// starts has both, from the launch record.
    /// </summary>
    [Fact]
    public async Task TheEngineACallStarts_HasTheFlagsOfTheFoldersLastStart()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        var web = Path.Combine(folder.Root, "web");
        Directory.CreateDirectory(web);
        File.WriteAllText(Path.Combine(web, "index.html"), "<!doctype html><title>launch-record-web-root</title>");
        const string origin = "https://client.example";
        var text = Path.Combine(folder.Root, "text");
        Directory.CreateDirectory(Path.Combine(text, "guide"));
        File.WriteAllText(Path.Combine(text, "guide", "owner-topic.md"), "# The owner's topic\nlaunch-record-live-text");

        var byHand = await folder.StartEngineAsync("--web-root", web, "--allow-origin", origin, "--mcp-text", text);
        byHand.Kill();
        await byHand.WaitForExitAsync(Ct);

        await using var front = new McpHarness(folder.Data);
        await front.InitializeAsync();
        var guide = McpHarness.TextOf(await front.CallAsync("get_design_guide", new JsonObject { ["topic"] = "owner-topic" }));
        Assert.Contains("was started", guide);
        Assert.Contains("launch-record-live-text", guide);

        var started = (await folder.PidAsync())!.Value;
        var port = 0;
        await McpHarness.WaitUntil(() =>
        {
            try
            {
                var runtime = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.Data, "runtime.json"))).RootElement;
                port = runtime.GetProperty("port").GetInt32();
                return Task.FromResult(runtime.GetProperty("pid").GetInt32() == started);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                return Task.FromResult(false);
            }
        }, TimeSpan.FromSeconds(10), () => "The started engine wrote no runtime.json.");
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        Assert.Contains("launch-record-web-root", await http.GetStringAsync("/", Ct));
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/session");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        using var answer = await http.SendAsync(preflight, Ct);
        Assert.Equal(origin, Assert.Single(answer.Headers.GetValues("Access-Control-Allow-Origin")));
        var args = McpHarness.ArgumentsOf((await folder.PidAsync())!.Value);
        Assert.Contains("--no-browser", args);
        Assert.DoesNotContain("--token", args);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class McpHarnessCollection
{
    public const string Name = "MCP harness";
}

internal static class McpServerVersions
{
    public static string Newest => InfoPanel.Mcp.Front.McpServer.ProtocolVersions[^1];
}
