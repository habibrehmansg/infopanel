using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using InfoPanel.Documents;
using InfoPanel.Mcp.Tools;
using InfoPanel.Security;
using InfoPanel.Service;
using InfoPanel.Settings;
using InfoPanel.Tests.Mcp;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;
using static InfoPanel.Tests.Relay.RelayRig;

namespace InfoPanel.Tests.Relay;

/// <summary>
/// The engine's own rules for calls from elsewhere (docs/2.0/relay.md), on a test engine's tool host reached through the
/// relay client and a fake relay: every edit tool's write is cut off once <c>cancel</c> arrives, the deadline passes or
/// the connection closes, each paused just before its write; web images are refused; text is held to <c>maxChars</c>;
/// and a frame reaches nothing beyond the tool host.
/// </summary>
public sealed class RelayToolRulesTests
{
    private const string Svg = """<svg xmlns="http://www.w3.org/2000/svg" width="8" height="8" viewBox="0 0 8 8"><rect width="8" height="8" fill="#ff8800"/></svg>""";

    /// <summary>A PNG <c>add_asset</c> takes as <c>data</c>, base64.</summary>
    private static readonly byte[] Png = McpEditToolTests.Encoded(SKEncodedImageFormat.Png, 8, 8);

    /// <summary>
    /// Each edit tool's call, on a profile <c>P</c> holding the items <c>a</c> and <c>b</c>. Every tool that declares
    /// <c>edit</c> must be here (<see cref="EveryEditTool_HasACutoffTest"/>), so a later edit tool can't skip the cutoff.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Func<JsonObject>> EditCalls = new Dictionary<string, Func<JsonObject>>
    {
        ["create_profile"] = () => new JsonObject { ["name"] = "Made elsewhere", ["width"] = 100, ["height"] = 100 },
        ["add_item"] = () => new JsonObject { ["profile"] = "P", ["scene"] = TestDocs.InputScene() },
        ["add_items"] = () => new JsonObject
        {
            ["profile"] = "P",
            ["items"] = new JsonArray(new JsonObject { ["scene"] = TestDocs.InputScene() }, new JsonObject { ["scene"] = TestDocs.InputScene(), ["x"] = 30 }),
        },
        ["update_item"] = () => new JsonObject { ["profile"] = "P", ["item"] = "a", ["x"] = 55 },
        ["update_items"] = () => new JsonObject
        {
            ["profile"] = "P",
            ["items"] = new JsonArray(new JsonObject { ["item"] = "a", ["x"] = 55 }, new JsonObject { ["item"] = "b", ["x"] = 66, ["name"] = "B" }),
        },
        ["remove_item"] = () => new JsonObject { ["profile"] = "P", ["item"] = "a" },
        ["add_asset"] = () => new JsonObject { ["svg"] = Svg },
        ["show_on_desktop"] = () => new JsonObject { ["profile"] = "P" },
    };

    public static TheoryData<string, string> EditToolsAndCutoffs()
    {
        var data = new TheoryData<string, string>();
        foreach (var tool in EditCalls.Keys)
        {
            foreach (var cutoff in new[] { "cancel", "deadline", "closed" })
                data.Add(tool, cutoff);
        }

        return data;
    }

    [Fact]
    public void EveryEditTool_HasACutoffTest()
    {
        using var factory = new EngineFactory();
        var host = factory.Services.GetRequiredService<McpToolHost>();
        var edit = host.All.Where(t => t.Scope == ApiScope.Edit).Select(t => t.Name).Order().ToList();
        Assert.Equal(edit, EditCalls.Keys.Order().ToList());
    }

    [Theory]
    [MemberData(nameof(EditToolsAndCutoffs))]
    public Task AnEditTool_PausedBeforeItsWrite_ThenCutOff_NeverWrites_AndAnswersCancelled(string tool, string cutoff) =>
        CutOffWhilePaused(tool, cutoff, "write");

    /// <summary>
    /// Paused at <c>locking</c>, past the point where a check before the write's lock would already have passed: the
    /// cutoff trips while the write goes for its lock (or waits for it), and the write still never happens, as its
    /// cutoff is asked under that lock.
    /// </summary>
    [Theory]
    [MemberData(nameof(EditToolsAndCutoffs))]
    public Task AnEditTool_CutOffWhileItsWriteGoesForItsLock_NeverWrites_AndAnswersCancelled(string tool, string cutoff) =>
        CutOffWhilePaused(tool, cutoff, "locking");

    public static TheoryData<string, string> PausesAndCutoffs()
    {
        var data = new TheoryData<string, string>();
        foreach (var pauseAt in new[] { "write", "locking" })
        {
            foreach (var cutoff in new[] { "cancel", "deadline", "closed" })
                data.Add(pauseAt, cutoff);
        }

        return data;
    }

    /// <summary><c>add_asset</c> given a picture's bytes (<c>data</c>) is cut off as its <c>svg</c> is: its one write is guarded.</summary>
    [Theory]
    [MemberData(nameof(PausesAndCutoffs))]
    public Task AddAsset_GivenData_PausedBeforeItsWrite_ThenCutOff_NeverWrites_AndAnswersCancelled(string pauseAt, string cutoff) =>
        CutOffWhilePaused("add_asset", cutoff, pauseAt, new JsonObject { ["data"] = Convert.ToBase64String(Png) });

    private static async Task CutOffWhilePaused(string tool, string cutoff, string pauseAt, JsonObject? arguments = null)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var before = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        engine.Host.StepHook = step =>
        {
            if (step != pauseAt)
                return;
            paused.TrySetResult();
            release.Wait(Limit, Ct);
        };

        await peer.SendAsync(rig.Call("edit", tool, arguments ?? EditCalls[tool]()));
        await paused.Task.WaitAsync(RelayRig.Limit, Ct);
        switch (cutoff)
        {
            case "cancel":
                await peer.SendAsync(new JsonObject { ["type"] = "cancel", ["id"] = "edit" });
                await FlushAsync(peer);
                break;
            case "deadline":
                // The cancel was lost: 60 s after it was sent, nothing may commit.
                rig.Clocks.Advance(TimeSpan.FromSeconds(61));
                break;
            case "closed":
                peer.Abort();
                await rig.WaitForAsync(RelayState.Connecting);
                break;
        }

        release.Set();
        var (id, answer) = await engine.Answers.Reader.ReadAsync(Ct).AsTask().WaitAsync(Limit, Ct);
        Assert.Equal("edit", id);
        Assert.True(answer.Cancelled, answer.Result.ToJsonString());
        Assert.Contains("cancelled", answer.Result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(before, engine.Written());
        if (cutoff != "closed")
            Assert.Equal("""{"type":"cancelled","id":"edit"}""", (await peer.NextAsync(Ct)).ToJsonString());
    }

    [Theory]
    [MemberData(nameof(EditToolNames))]
    public async Task AnEditTool_FromElsewhere_NotCutOff_Writes(string tool)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var before = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        await peer.SendAsync(rig.Call("edit", tool, EditCalls[tool]()));
        var result = await peer.ExpectAsync("result", Ct);
        Assert.False(result["result"]!["isError"]!.GetValue<bool>(), result.ToJsonString());
        Assert.NotEqual(before, engine.Written());
    }

    public static TheoryData<string> EditToolNames() => [.. EditCalls.Keys];

    [Fact]
    public async Task AddAsset_GivenData_FromElsewhere_NotCutOff_Writes()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var before = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        await peer.SendAsync(rig.Call("edit", "add_asset", new JsonObject { ["data"] = Convert.ToBase64String(Png) }));
        var result = await peer.ExpectAsync("result", Ct);
        Assert.False(result["result"]!["isError"]!.GetValue<bool>(), result.ToJsonString());
        Assert.EndsWith(" | png ", before); // not stored before
        Assert.EndsWith($" | png {Png.Length}", engine.Written());
    }

    /// <summary>
    /// The largest picture <c>add_asset</c> takes, a file of 6 MiB (here a PNG followed by zeros to that size), is
    /// 8,388,608 characters of base64: its call fits one frame (9 MiB), and the bytes stored are the bytes given.
    /// </summary>
    [Fact]
    public async Task AddAsset_APictureOf6MiB_AsBase64_FitsOneCallFrame_AndIsStoredAsGiven()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var bytes = new byte[6 * 1024 * 1024];
        McpEditToolTests.Encoded(SKEncodedImageFormat.Png, 64, 64).CopyTo(bytes, 0);
        var data = Convert.ToBase64String(bytes);
        Assert.Equal(8_388_608, data.Length);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        var call = rig.Call("big", "add_asset", new JsonObject { ["data"] = data });
        Assert.InRange(Encoding.UTF8.GetByteCount(call.ToJsonString()), 8_388_608, RelayFrames.MaxFrameBytes);
        await peer.SendAsync(call);
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.False(result["isError"]!.GetValue<bool>(), text);
        var id = BlobStore.IdOf(bytes);
        Assert.StartsWith($"Stored the PNG picture as {id} (64×64, {bytes.Length} bytes). ", text);
        Assert.Equal(bytes, factory.Services.GetRequiredService<Workspace>().Blobs.ReadHashed(id));
    }

    private const string WebImageRefusal = "An AI app can't add or change a picture from a web address (webImage): nothing was changed. " +
        "add_asset stores a picture to show instead; moving, hiding or removing such an item works.";

    [Fact]
    public async Task WebImages_AreRefusedWhenAddedOrChanged_ButAnItemShowingOneCanBeMovedHiddenOrRemoved()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = factory.Services.GetRequiredService<Workspace>();
        var id = TestDocs.Create(workspace, TestDocs.Doc("W", 400, 200,
            TestDocs.Scene("cam", 0, 0, WebImageScene("http://192.168.1.10/cam.jpg")), TestDocs.Scene("box", 100, 0)));
        var document = workspace.Find(id).Document!;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        async Task<(string Text, bool IsError)> Call(string tool, JsonObject arguments)
        {
            await peer.SendAsync(rig.Call("c" + Guid.NewGuid().ToString("N")[..8], tool, arguments));
            var result = (await peer.ExpectAsync("result", Ct))["result"]!;
            return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
        }

        var rev = document.Rev;
        foreach (var (tool, arguments) in new (string, JsonObject)[]
        {
            ("add_item", new JsonObject { ["profile"] = "W", ["scene"] = WebImageScene("https://example.com/a.png") }),
            ("update_item", new JsonObject { ["profile"] = "W", ["item"] = "box", ["scene"] = WebImageScene("https://example.com/a.png") }),
            ("update_item", new JsonObject { ["profile"] = "W", ["item"] = "cam", ["scene"] = WebImageScene("http://10.0.0.1/x.jpg") }),
            ("update_item", new JsonObject { ["profile"] = "W", ["item"] = "cam", ["width"] = 50 }),
            ("update_item", new JsonObject { ["profile"] = "W", ["item"] = "cam", ["bindings"] = new JsonObject { ["v"] = "plugin:/infopanel.mock/cpu/cpu-load" } }),
        })
        {
            var (text, error) = await Call(tool, arguments);
            Assert.True(error, $"{tool} {arguments.ToJsonString()}: {text}");
            Assert.Contains("(webImage)", text);
            Assert.Equal(WebImageRefusal, text);
            Assert.Equal(rev, document.Rev);
        }

        // Moving, hiding, renaming, replacing its scene with one without a web image, removing: fine.
        Assert.False((await Call("update_item", new JsonObject { ["profile"] = "W", ["item"] = "cam", ["x"] = 10, ["visible"] = false, ["name"] = "Camera" })).IsError);
        Assert.False((await Call("update_item", new JsonObject { ["profile"] = "W", ["item"] = "box", ["x"] = 5 })).IsError);
        Assert.False((await Call("remove_item", new JsonObject { ["profile"] = "W", ["item"] = "cam" })).IsError);
        Assert.Equal(rev + 3, document.Rev);

        // From this computer, the rule doesn't apply.
        using var json = JsonDocument.Parse(new JsonObject { ["profile"] = "W", ["scene"] = WebImageScene("https://example.com/a.png") }.ToJsonString());
        var local = await engine.Host.CallAsync("add_item", json.RootElement.Clone(), Ct);
        Assert.False(local["isError"]!.GetValue<bool>(), local.ToJsonString());
    }

    [Fact]
    public async Task WebImages_AreKnownByTheirMediaNodes_NotByMembersNamedLikeTheirSource()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        async Task<(string Text, bool IsError)> Call(string tool, JsonObject arguments)
        {
            await peer.SendAsync(rig.Call("c" + Guid.NewGuid().ToString("N")[..8], tool, arguments));
            var result = (await peer.ExpectAsync("result", Ct))["result"]!;
            return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
        }

        // Inputs named provider and ref: no media source, so no web image; added, and its bindings changed.
        var named = JsonNode.Parse("""{"format":"infopanel.scene/1","inputs":{"provider":{"type":"number"},"ref":{"type":"number"}},"nodes":[{"kind":"rect","w":10,"h":10}]}""")!.AsObject();
        var added = await Call("add_item", new JsonObject { ["profile"] = "P", ["name"] = "named", ["scene"] = named });
        Assert.False(added.IsError, added.Text);
        var bound = await Call("update_item", new JsonObject { ["profile"] = "P", ["item"] = "named", ["bindings"] = new JsonObject { ["provider"] = "plugin:/infopanel.mock/cpu/cpu-load" } });
        Assert.False(bound.IsError, bound.Text);

        // A provider's source deep in groups is one.
        var media = WebImageScene("https://example.com/a.png")["nodes"]![0]!.DeepClone();
        var grouped = WebImageScene("https://example.com/a.png");
        grouped["nodes"] = new JsonArray(new JsonObject
        {
            ["kind"] = "group",
            ["children"] = new JsonArray(new JsonObject { ["kind"] = "group", ["children"] = new JsonArray(media) }),
        });
        var refused = await Call("add_item", new JsonObject { ["profile"] = "P", ["scene"] = grouped });
        Assert.True(refused.IsError, refused.Text);
        Assert.Contains("(webImage)", refused.Text);
    }

    [Fact]
    public async Task ABindingChange_ReadOnASceneWithoutAWebImage_IsRefused_WhenADesignChangeMeanwhileGaveItOne()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = factory.Services.GetRequiredService<Workspace>();
        var id = TestDocs.Create(workspace, TestDocs.Doc("W", 400, 200, TestDocs.Scene("box", 100, 0, TestDocs.InputScene())));
        var document = workspace.Find(id).Document!;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        // The binding change from elsewhere has read the item (no web image) and is about to write.
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        engine.Host.StepHook = step =>
        {
            if (step != "write")
                return;
            paused.TrySetResult();
            release.Wait(Limit, Ct);
        };
        await peer.SendAsync(rig.Call("bind", "update_item",
            new JsonObject { ["profile"] = "W", ["item"] = "box", ["bindings"] = new JsonObject { ["v"] = "plugin:/infopanel.mock/cpu/cpu-load" } }));
        await paused.Task.WaitAsync(Limit, Ct);

        // Meanwhile, on this computer, the item's design becomes a web image whose address the bindings could change.
        engine.Host.StepHook = null;
        TestDocs.Commit(document, TestDocs.Design("box", WebImageScene("http://192.168.1.10/cam.jpg")));
        var rev = document.Rev;
        release.Set();

        // Design and bindings have revisions of their own, so no conflict: the rule is asked on the design the commit finds.
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Contains("(webImage)", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(rev, document.Rev);
        Assert.Null(Assert.IsType<InfoPanel.Spec.SceneItem>(Assert.Single(document.Current.Document.Items)).Bindings);
    }

    internal static JsonObject WebImageScene(string address) => new()
    {
        ["format"] = "infopanel.scene/1",
        ["requires"] = new JsonArray("media.provider"),
        ["inputs"] = new JsonObject { ["v"] = new JsonObject { ["type"] = "number" } },
        ["nodes"] = new JsonArray(new JsonObject
        {
            ["kind"] = "media",
            ["w"] = 40,
            ["h"] = 30,
            ["source"] = new JsonObject { ["provider"] = "http", ["ref"] = address },
        }),
    };

    [Fact]
    public async Task MaxChars_ShortensListings_ToWhatTheAiAppTakes()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = factory.Services.GetRequiredService<Workspace>();
        TestDocs.Create(workspace, TestDocs.Doc("Many", 400, 200, [.. Enumerable.Range(0, 200).Select(i => TestDocs.Scene($"item-{i:000}", i, 0))]));
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        await peer.SendAsync(rig.Call("p", "get_profile", new JsonObject { ["profile"] = "Many" }, maxChars: 2000));
        var profile = (await peer.ExpectAsync("result", Ct))["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.True(profile.Length <= 2000, $"{profile.Length} characters");
        var summary = JsonNode.Parse(profile)!;
        Assert.Contains("more items: the list stops at 2000 characters", summary["more"]!.GetValue<string>());
        Assert.InRange(summary["items"]!.AsArray().Count, 1, 199);

        await peer.SendAsync(rig.Call("s", "list_sensors", new JsonObject { ["limit"] = 1000 }, maxChars: 1000));
        var sensors = (await peer.ExpectAsync("result", Ct))["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.True(sensors.Length <= 1000, $"{sensors.Length} characters");
        Assert.Contains("more than this AI app takes (1000)", sensors);

        // Without maxChars, the same listing whole.
        await peer.SendAsync(rig.Call("all", "get_profile", new JsonObject { ["profile"] = "Many" }));
        var whole = JsonNode.Parse((await peer.ExpectAsync("result", Ct))["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(200, whole["items"]!.AsArray().Count);
        Assert.Null(whole["more"]);
    }

    [Fact]
    public async Task MaxChars_HoldsTheWholeAnswer_PicturesIncluded()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = factory.Services.GetRequiredService<Workspace>();
        TestDocs.Create(workspace, TestDocs.Doc("Busy", 440, 220, [.. Enumerable.Range(0, 60).Select(i =>
            TestDocs.Scene($"r{i}", i * 37 % 420, i * 53 % 200, TestDocs.Rect($"#{i * 40503 & 0xffffff:x6}", 15 + i % 7, 9 + i % 5)))]));
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        async Task<(JsonNode Result, int Characters)> Preview(int? maxChars)
        {
            await peer.SendAsync(rig.Call("p" + Guid.NewGuid().ToString("N")[..8], "render_preview", new JsonObject { ["profile"] = "Busy", ["width"] = 440 }, maxChars: maxChars));
            var result = (await peer.ExpectAsync("result", Ct))["result"]!;
            // What the AI app takes in: every part's text, a picture's data.
            var characters = result["content"]!.AsArray().Sum(p => (p!["text"] ?? p["data"])!.GetValue<string>().Length);
            return (result, characters);
        }

        // Without maxChars: the picture, over 1000 characters as sent on its own.
        var (whole, wholeCharacters) = await Preview(null);
        var picture = whole["content"]!.AsArray().Single(p => p!["type"]!.GetValue<string>() == "image")!["data"]!.GetValue<string>();
        Assert.True(picture.Length > 1000, $"{picture.Length} characters");

        // At 1000: the whole answer within it, saying why there is no picture and how to get one.
        var (held, heldCharacters) = await Preview(1000);
        Assert.True(heldCharacters <= 1000, $"{heldCharacters} characters: {held.ToJsonString()[..Math.Min(300, held.ToJsonString().Length)]}");
        Assert.DoesNotContain(held["content"]!.AsArray(), p => p!["type"]!.GetValue<string>() == "image");
        Assert.True(held["isError"]!.GetValue<bool>());
        var text = held["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("1000", text);
        Assert.Contains("width", text);

        // With room for it all: as without a limit.
        var (roomy, _) = await Preview(wholeCharacters);
        Assert.Equal(picture, roomy["content"]!.AsArray().Single(p => p!["type"]!.GetValue<string>() == "image")!["data"]!.GetValue<string>());
        Assert.False(roomy["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AFrame_ReachesNothingBeyondTheToolHost()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var settings = factory.Services.GetRequiredService<SettingsStore>();
        var tokens = factory.Services.GetRequiredService<TokenStore>();
        using (var on = JsonDocument.Parse("""{"service":{"aiApps":true}}"""))
            settings.Apply(on.RootElement);
        var version = settings.Current.Version;
        var tokenCount = tokens.List().Count;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        // Calls naming anything but a tool: the API, settings (the switch included), tokens, logs, folders, plugins.
        string[] notTools =
        [
            "PATCH /api/settings", "/api/settings", "settings", "service.aiApps", "set_ai_apps", "aiApps", "session", "/api/session",
            "create_token", "/api/tokens", "get_logs", "open_folder", "plugins", "list_profiles/../../api/settings", "LIST_PROFILES",
        ];
        foreach (var name in notTools)
        {
            await peer.SendAsync(rig.Call("n", name, new JsonObject { ["service"] = new JsonObject { ["aiApps"] = false } }));
            var result = (await peer.ExpectAsync("result", Ct))["result"]!;
            Assert.True(result["isError"]!.GetValue<bool>());
            Assert.Contains("(unknownTool)", result["content"]![0]!["text"]!.GetValue<string>());
        }

        // Frames of other types: answered unsupported, never dispatched.
        foreach (var frame in new[]
        {
            new JsonObject { ["type"] = "settings", ["service"] = new JsonObject { ["aiApps"] = false } },
            new JsonObject { ["type"] = "request", ["method"] = "PATCH", ["path"] = "/api/settings", ["body"] = """{"service":{"aiApps":false}}""" },
            new JsonObject { ["type"] = "session", ["id"] = "s1" },
            new JsonObject { ["type"] = "token", ["id"] = "t1", ["scopes"] = new JsonArray("edit") },
        })
        {
            await peer.SendAsync(frame);
            Assert.Equal("unsupported", (await peer.ExpectAsync("error", Ct))["code"]!.GetValue<string>());
        }

        Assert.Equal(version, settings.Current.Version);
        Assert.True(settings.Current.Settings.Service.AiApps);
        Assert.Equal(tokenCount, tokens.List().Count);
        Assert.Equal(notTools, engine.Called.ToArray());

        // What a relay call is given: the tool host, and nothing that reaches the API, the settings or the engine's stores.
        var given = typeof(RelayClient).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
        Assert.Equal([typeof(ServiceCredentials), typeof(IRelayToolHost), typeof(RelayOptions), typeof(TimeProvider), typeof(Serilog.ILogger), typeof(Func<double>)], given);
        Type[] forbidden = [typeof(SettingsStore), typeof(Workspace), typeof(TokenStore), typeof(IServiceProvider), typeof(HttpClient), typeof(ServiceClient)];
        var fields = new[] { typeof(RelayClient) }.Concat(typeof(RelayClient).GetNestedTypes(BindingFlags.NonPublic))
            .SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            .Where(f => forbidden.Any(t => t.IsAssignableFrom(f.FieldType)))
            .Select(f => $"{f.DeclaringType!.Name}.{f.Name}")
            .ToList();
        Assert.Empty(fields);
    }

    /// <summary>A test engine's tool host, reached through a recorder of what each relay call answered.</summary>
    internal sealed class Engine
    {
        private readonly EngineFactory _factory;

        public Engine(EngineFactory factory)
        {
            _factory = factory;
            Host = factory.Services.GetRequiredService<McpToolHost>();
            Recording = new Recorder(this);
            var workspace = factory.Services.GetRequiredService<Workspace>();
            TestDocs.Create(workspace, TestDocs.Doc("P", 400, 200, TestDocs.Scene("a", 10, 10), TestDocs.Scene("b", 100, 10)));
        }

        public McpToolHost Host { get; }

        public IRelayToolHost Recording { get; }

        public Channel<(string Id, RelayToolAnswer Answer)> Answers { get; } = Channel.CreateUnbounded<(string, RelayToolAnswer)>();

        public ConcurrentQueue<string> Called { get; } = new();

        /// <summary>What the edit tools write: the profiles, each one's revision and window setting, and whether the SVG and the PNG are stored.</summary>
        public string Written()
        {
            var workspace = _factory.Services.GetRequiredService<Workspace>();
            var profiles = workspace.List().OrderBy(e => e.Id).Select(e =>
            {
                var document = workspace.Find(e.Id).Document!;
                var state = document.Read();
                return $"{e.Name}@{state.Generation.Rev}:{string.Join(",", state.Document.Items.Select(i => $"{i.Id}/{i.Placement.X}"))}"
                    + $":{state.Document.Profile.Window?.Enabled}";
            });
            var blob = workspace.Blobs.StoredSize(BlobStore.IdOf(Encoding.UTF8.GetBytes(Svg)));
            var png = workspace.Blobs.StoredSize(BlobStore.IdOf(Png));
            return string.Join(" | ", profiles) + $" | svg {blob} | png {png}";
        }

        private sealed class Recorder(Engine engine) : IRelayToolHost
        {
            public async Task<RelayToolAnswer> CallAsync(string tool, JsonElement arguments, RelayCallContext context, CancellationToken ct)
            {
                engine.Called.Enqueue(tool);
                var answer = await ((IRelayToolHost)engine.Host).CallAsync(tool, arguments, context, ct);
                engine.Answers.Writer.TryWrite((context.Id, answer));
                return answer;
            }

            public async Task<RelayToolAnswer> ReadAsync(string profile, string item, bool detail, RelayCallContext context, CancellationToken ct)
            {
                engine.Called.Enqueue("read");
                var answer = await ((IRelayToolHost)engine.Host).ReadAsync(profile, item, detail, context, ct);
                engine.Answers.Writer.TryWrite((context.Id, answer));
                return answer;
            }
        }
    }
}
