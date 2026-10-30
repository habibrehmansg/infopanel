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
        Assert.Equal(["list_profiles", "get_profile", "list_sensors", "list_fonts", "render_preview", "get_design_guide",
                "create_profile", "add_item", "update_item", "remove_item", "add_asset", "show_on_desktop"],
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
        Assert.Equal(InfoPanel.Mcp.Tools.McpToolHost.ToolNames.Count,(await front.RequestAsync("tools/list"))["result"]!["tools"]!.AsArray().Count);

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

    /// <summary>Every tool that changes a profile, through the front as an AI app calls it: a panel made from scenes.</summary>
    [Fact]
    public async Task Attached_TheDesignToolsMakeAPanel()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        await folder.StartEngineAsync();
        await using var front = new McpHarness(folder.Data);
        await front.InitializeAsync();

        async Task<string> Ok(string tool, JsonObject arguments)
        {
            var result = await front.CallAsync(tool, arguments);
            var text = McpHarness.TextOf(result);
            Assert.False(result["isError"]!.GetValue<bool>(), $"{tool}: {text}");
            return text;
        }

        Assert.Contains("Inter", await Ok("list_fonts", new JsonObject()));
        Assert.Contains("Saved", await Ok("create_profile", new JsonObject { ["name"] = "Harness desk", ["width"] = 480, ["height"] = 240 }));
        var svg = await Ok("add_asset", new JsonObject { ["svg"] = """<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"><circle cx="10" cy="10" r="9" fill="#00aaff"/></svg>""" });
        var asset = System.Text.RegularExpressions.Regex.Match(svg, "sha256:[0-9a-f]{64}").Value;
        Assert.NotEmpty(asset);
        var scene = new JsonObject
        {
            ["format"] = "infopanel.scene/1",
            ["nodes"] = new JsonArray(
                new JsonObject { ["kind"] = "rect", ["w"] = 100, ["h"] = 40, ["fill"] = "#202020" },
                new JsonObject { ["kind"] = "media", ["x"] = 4, ["y"] = 4, ["w"] = 32, ["h"] = 32, ["source"] = new JsonObject { ["asset"] = asset } }),
        };
        var added = await Ok("add_item", new JsonObject { ["profile"] = "Harness desk", ["scene"] = scene, ["x"] = 10, ["y"] = 10, ["name"] = "Card" });
        Assert.Contains("Saved", added);
        await Ok("update_item", new JsonObject { ["profile"] = "Harness desk", ["item"] = "Card", ["x"] = 20, ["width"] = 100, ["height"] = 40 });
        await Ok("show_on_desktop", new JsonObject { ["profile"] = "Harness desk" });

        var profile = JsonNode.Parse(await Ok("get_profile", new JsonObject { ["profile"] = "Harness desk" }))!;
        var card = profile["items"]!.AsArray().Single()!;
        Assert.Equal((20.0, 100.0), (card["x"]!.GetValue<double>(), card["w"]!.GetValue<double>()));
        Assert.False(profile["unsaved"]!.GetValue<bool>());

        await Ok("remove_item", new JsonObject { ["profile"] = "Harness desk", ["item"] = "Card" });
        Assert.Empty(JsonNode.Parse(await Ok("get_profile", new JsonObject { ["profile"] = "Harness desk" }))!["items"]!.AsArray());
        front.AssertStdoutIsOnlyProtocol();
    }

    /// <summary>
    /// Codex p1 round 2, #2: on a volume that folds case, a front that started before its data folder existed hashed the
    /// spelling it was given. An engine then creates the folder in another case: the front finds that engine once the
    /// folder exists, instead of starting one that exits 3 and answering "older engine".
    /// </summary>
    [Fact]
    public async Task AFrontStartedBeforeItsFolderExisted_FindsTheEngineThatMadeItInAnotherCase()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        using var folder = new HarnessFolder("later");
        var probe = Path.Combine(folder.Root, "case-probe");
        Directory.CreateDirectory(probe);
        if (!Directory.Exists(Path.Combine(folder.Root, "CASE-PROBE")))
            return; // a volume that keeps case apart: two spellings are two folders

        folder.RecordQuietStart();
        await using var front = new McpHarness(Path.Combine(folder.Root, "Later"), startSeconds: 5);
        await front.InitializeAsync(); // the front has worked out its channel, the folder not there yet
        Assert.False(Directory.Exists(folder.Data));

        var engine = await folder.StartEngineAsync(); // makes "later"
        var result = await front.CallAsync("list_profiles");
        var text = McpHarness.TextOf(result);
        Assert.False(result["isError"]!.GetValue<bool>(), text);
        Assert.DoesNotContain("was started", text);
        Assert.Contains("Wide bar", text);
        Assert.Equal(engine.Id, await folder.PidAsync());
        front.AssertStdoutIsOnlyProtocol();
    }

    /// <summary>
    /// Codex p2 #7: scenes as deep as the engine takes pass the transport over stdio and the channel: a rect whose width
    /// nests 30 adds (within exprDepth; its MCP message is past 64 levels), and the deepest scene a commit takes (its body
    /// at the format's 256 levels; a newer scene format, kept as sent). One level deeper is the engine's own refusal
    /// (limit.jsonDepth), not unreadable JSON.
    /// </summary>
    [Fact]
    public async Task DeepScenes_PassTheTransport_UpToTheFormatsDepth()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        await folder.StartEngineAsync();
        await using var front = new McpHarness(folder.Data);
        await front.InitializeAsync();
        Assert.False((await front.CallAsync("create_profile", new JsonObject { ["name"] = "Deep", ["width"] = 200, ["height"] = 100 }))["isError"]!.GetValue<bool>());

        async Task<JsonNode> Add(string name, JsonObject scene)
        {
            var answer = await front.RequestAsync("tools/call", new JsonObject
            {
                ["name"] = "add_item",
                ["arguments"] = new JsonObject { ["profile"] = "Deep", ["name"] = name, ["scene"] = scene },
            }, TimeSpan.FromSeconds(30));
            return answer["result"] ?? throw new InvalidOperationException($"add_item answered with an error: {answer.ToJsonString()}");
        }

        JsonNode width = 1;
        for (var i = 0; i < 30; i++)
            width = new JsonObject { ["op"] = "add", ["args"] = new JsonArray(width, 1) };
        var adds = await Add("Adds", new JsonObject
        {
            ["format"] = "infopanel.scene/1",
            ["nodes"] = new JsonArray(new JsonObject { ["kind"] = "rect", ["w"] = width, ["h"] = 10, ["fill"] = "#ffffff" }),
        });
        Assert.False(adds["isError"]!.GetValue<bool>(), McpHarness.TextOf(adds));

        // The commit body: {ops: [{item: {scene: {x: arrays}}}]}: the scene's arrays at levels 6 and on.
        static JsonObject Nested(int arrays)
        {
            JsonNode value = 0;
            for (var i = 0; i < arrays; i++)
                value = new JsonArray(value);
            return new JsonObject { ["format"] = "infopanel.scene/2", ["x"] = value };
        }
        var deepest = await Add("Deepest", Nested(251));
        Assert.False(deepest["isError"]!.GetValue<bool>(), McpHarness.TextOf(deepest));
        var deeper = await Add("Deeper", Nested(252));
        Assert.True(deeper["isError"]!.GetValue<bool>());
        Assert.Contains("limit.jsonDepth", McpHarness.TextOf(deeper));

        var profile = JsonNode.Parse(McpHarness.TextOf(await front.CallAsync("get_profile", new JsonObject { ["profile"] = "Deep" })))!;
        Assert.Equal(["Adds", "Deepest"], profile["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));
        front.AssertStdoutIsOnlyProtocol();
    }

    /// <summary>
    /// Triage p2 #9: on a cold start (no engine yet) the front answers initialize and tools/list with the live text of
    /// the launch record's --mcp-text folder, read through the same layers, not only the built-in wording; a folder that
    /// isn't there leaves the built-in text. Nothing is started.
    /// </summary>
    [Fact]
    public async Task AColdStart_AnswersWithTheLaunchRecordsLiveText()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        var live = Path.Combine(folder.Root, "text");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, "tools.json"),
            """{"instructions": "Live words for a cold start.", "tools": {"list_fonts": {"description": "Live fonts wording."}}}""");
        Directory.CreateDirectory(TestRunFolder.Path);
        File.WriteAllText(folder.LaunchRecordPath, new JsonObject
        {
            ["launch"] = 1,
            ["args"] = new JsonArray([.. folder.QuietFlags.Append("--mcp-text").Append(live).Select(f => (JsonNode)JsonValue.Create(f))]),
        }.ToJsonString());

        await using var front = new McpHarness(folder.Data);
        var init = await front.InitializeAsync();
        Assert.Equal("Live words for a cold start.", init["result"]!["instructions"]!.GetValue<string>());
        var tools = (await front.RequestAsync("tools/list"))["result"]!["tools"]!.AsArray();
        Assert.Equal("Live fonts wording.", tools.Single(t => t!["name"]!.GetValue<string>() == "list_fonts")!["description"]!.GetValue<string>());
        Assert.NotEqual("Live fonts wording.", tools.Single(t => t!["name"]!.GetValue<string>() == "list_profiles")!["description"]!.GetValue<string>());

        // The folder gone: the built-in text again, read on every answer.
        Directory.Move(live, live + "-gone");
        var builtIn = InfoPanel.Mcp.Tools.McpToolHost.BuiltInList();
        tools = (await front.RequestAsync("tools/list"))["result"]!["tools"]!.AsArray();
        Assert.Equal(builtIn["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "list_fonts")!["description"]!.GetValue<string>(),
            tools.Single(t => t!["name"]!.GetValue<string>() == "list_fonts")!["description"]!.GetValue<string>());
        Assert.False(Directory.Exists(folder.Data)); // nothing was started
        front.AssertStdoutIsOnlyProtocol();
    }

    /// <summary>
    /// Codex p2-2 #1: the cold-start wording comes from the launch record only when the run folder and the record pass the
    /// checks the channel makes: the folder this user's, not a link, no one else may write in it; the record a file of
    /// this user's, not a link, no one else may write. A record planted in a folder others can write in, one others can
    /// write, or a link gives the built-in instructions.
    /// </summary>
    [Fact]
    public async Task AColdStart_UsesTheBuiltInText_WhenTheRunFolderOrTheRecordIsntTrusted()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        var planted = Path.Combine(folder.Root, "planted");
        Directory.CreateDirectory(planted);
        File.WriteAllText(Path.Combine(planted, "tools.json"),
            """{"instructions": "Planted words.", "tools": {"list_fonts": {"description": "Planted fonts wording."}}}""");
        var builtIn = InfoPanel.Mcp.Tools.McpToolHost.BuiltInList()["instructions"]!.GetValue<string>();
        Assert.NotEqual("Planted words.", builtIn);

        var run = Path.Combine(Path.GetTempPath(), "ipw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(run);
        var record = LaunchRecord.PathFor(LocalChannel.EndpointFor(run, folder.Data));
        File.WriteAllText(record, new JsonObject
        {
            ["launch"] = 1,
            ["args"] = new JsonArray("--mcp-text", planted),
        }.ToJsonString());

        async Task<string> Instructions()
        {
            await using var front = new McpHarness(folder.Data, startSeconds: 10, environment: new Dictionary<string, string>
            {
                [TestRunFolder.Variable] = run,
            });
            var init = await front.InitializeAsync();
            var tools = (await front.RequestAsync("tools/list"))["result"]!["tools"]!.AsArray();
            var fonts = tools.Single(t => t!["name"]!.GetValue<string>() == "list_fonts")!["description"]!.GetValue<string>();
            var instructions = init["result"]!["instructions"]!.GetValue<string>();
            Assert.Equal(instructions == "Planted words.", fonts == "Planted fonts wording."); // the list agrees
            front.AssertStdoutIsOnlyProtocol();
            return instructions;
        }

        try
        {
            File.SetUnixFileMode(run, (UnixFileMode)0b111_000_000); // 0700
            File.SetUnixFileMode(record, (UnixFileMode)0b110_000_000); // 0600, as an engine writes it
            Assert.Equal("Planted words.", await Instructions()); // trusted: the record's folder is used

            File.SetUnixFileMode(run, (UnixFileMode)0b111_111_111); // 0777: others can write in the run folder
            Assert.Equal(builtIn, await Instructions());
            File.SetUnixFileMode(run, (UnixFileMode)0b111_000_000);

            File.SetUnixFileMode(record, (UnixFileMode)0b110_110_110); // 0666: others can write the record
            Assert.Equal(builtIn, await Instructions());
            File.SetUnixFileMode(record, (UnixFileMode)0b110_000_000);

            var elsewhere = Path.Combine(folder.Root, "record.json"); // the record a link to a file elsewhere
            File.Move(record, elsewhere);
            File.CreateSymbolicLink(record, elsewhere);
            Assert.Equal(builtIn, await Instructions());
            Assert.False(Directory.Exists(folder.Data)); // nothing was started
        }
        finally
        {
            File.SetUnixFileMode(run, (UnixFileMode)0b111_000_000);
            Directory.Delete(run, recursive: true);
        }
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
