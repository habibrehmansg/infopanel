using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Mcp.Tools;
using InfoPanel.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests.Mcp;

/// <summary>The tools as the engine runs them (<see cref="McpToolHost"/>), on test engines.</summary>
public sealed class McpToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every tool and the scope it acts with: a new tool must be added here, with read or edit.</summary>
    private static readonly Dictionary<string, ApiScope> Scopes = new()
    {
        ["list_profiles"] = ApiScope.Read,
        ["get_profile"] = ApiScope.Read,
        ["list_sensors"] = ApiScope.Read,
        ["render_preview"] = ApiScope.Read,
        ["list_fonts"] = ApiScope.Read,
        ["create_profile"] = ApiScope.Edit,
        ["add_item"] = ApiScope.Edit,
        ["add_items"] = ApiScope.Edit,
        ["update_item"] = ApiScope.Edit,
        ["update_items"] = ApiScope.Edit,
        ["remove_item"] = ApiScope.Edit,
        ["add_asset"] = ApiScope.Edit,
        ["show_on_desktop"] = ApiScope.Edit,
    };

    private static McpToolHost Host(EngineFactory factory) => factory.Services.GetRequiredService<McpToolHost>();

    private static async Task<(string Text, bool IsError, JsonNode Result)> Call(McpToolHost host, string tool, string arguments = "{}")
    {
        using var json = JsonDocument.Parse(arguments);
        var result = await host.CallAsync(tool, json.RootElement.Clone(), Ct);
        var text = string.Join("\n", result["content"]!.AsArray().Where(c => c!["type"]!.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()));
        return (text, result["isError"]!.GetValue<bool>(), result);
    }

    /// <summary>
    /// <c>update_items</c> changes a profile (edit, not read-only), listed after <c>update_item</c>; each of its 1 to 50
    /// entries takes exactly <c>update_item</c>'s members but <c>profile</c>, the item required.
    /// </summary>
    [Fact]
    public void UpdateItems_IsAnEditTool_WhoseEntriesAreUpdateItemsMembersWithoutProfile()
    {
        var tools = McpToolHost.BuiltInList()["tools"]!.AsArray().Select(t => t!.AsObject()).ToList();
        var names = tools.Select(t => t["name"]!.GetValue<string>()).ToList();
        Assert.Equal(names.IndexOf("update_item") + 1, names.IndexOf("update_items"));
        var single = tools.Single(t => t["name"]!.GetValue<string>() == "update_item");
        var batch = tools.Single(t => t["name"]!.GetValue<string>() == "update_items");
        Assert.False(batch["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        var schema = batch["inputSchema"]!;
        Assert.Equal(["profile", "items"], schema["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal("""["profile","items"]""", schema["required"]!.ToJsonString());
        var items = schema["properties"]!["items"]!;
        Assert.Equal((1, 50), (items["minItems"]!.GetValue<int>(), items["maxItems"]!.GetValue<int>()));
        var entry = items["items"]!.AsObject().DeepClone().AsObject();
        var expected = single["inputSchema"]!.DeepClone().AsObject();
        expected["properties"]!.AsObject().Remove("profile");
        expected["required"] = new JsonArray("item");
        Assert.Equal(expected.ToJsonString(), entry.ToJsonString());
    }

    [Fact]
    public async Task EveryTool_DeclaresReadOrEdit_HasNoWording_AndAValidClosedSchema()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        Assert.Equal(Scopes.Keys.Order(), host.All.Select(t => t.Name).Order());
        foreach (var tool in host.All)
        {
            Assert.True(tool.Scope is ApiScope.Read or ApiScope.Edit, $"{tool.Name} declares {tool.Scope}: a tool acts with read or edit, never the session's scope.");
            Assert.Equal(Scopes[tool.Name], tool.Scope);
        }

        var list = await host.ListAsync(Ct);
        // The tools' descriptions and the instructions are the service's to give, not the engine's.
        Assert.Null(list["instructions"]);
        var builtIn = McpToolHost.BuiltInList();
        Assert.Equal(list.ToJsonString(), builtIn.ToJsonString());
        foreach (var listed in list["tools"]!.AsArray())
        {
            var name = listed!["name"]!.GetValue<string>();
            Assert.Null(listed["description"]);
            Assert.Equal(Scopes[name] == ApiScope.Read, listed["annotations"]!["readOnlyHint"]!.GetValue<bool>());
            var schema = listed["inputSchema"]!.AsObject();
            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
            var properties = schema["properties"]!.AsObject();
            foreach (var required in schema["required"]!.AsArray())
                Assert.True(properties.ContainsKey(required!.GetValue<string>()), $"{name} requires an argument it doesn't define.");
            foreach (var (member, property) in properties)
            {
                Assert.Contains(property!["type"]!.GetValue<string>(), new[] { "string", "integer", "number", "boolean", "object", "array" });
                Assert.False(string.IsNullOrWhiteSpace(property["description"]?.GetValue<string>()), $"{name}.{member} has no description.");
                if (property["type"]!.GetValue<string>() != "array")
                    continue;
                // A list holds a bounded number of closed objects whose members are as the tools' own: described, of these types.
                Assert.True(property["maxItems"]!.GetValue<int>() > 0, $"{name}.{member} has no bound.");
                var element = property["items"]!.AsObject();
                Assert.Equal("object", element["type"]!.GetValue<string>());
                Assert.False(element["additionalProperties"]!.GetValue<bool>());
                foreach (var required in element["required"]!.AsArray())
                    Assert.True(element["properties"]!.AsObject().ContainsKey(required!.GetValue<string>()), $"{name}.{member} requires a member it doesn't define.");
                foreach (var (inner, innerSchema) in element["properties"]!.AsObject())
                {
                    Assert.Contains(innerSchema!["type"]!.GetValue<string>(), new[] { "string", "integer", "number", "boolean", "object", "array" });
                    Assert.False(string.IsNullOrWhiteSpace(innerSchema["description"]?.GetValue<string>()), $"{name}.{member}.{inner} has no description.");
                    if (innerSchema["type"]!.GetValue<string>() != "array")
                        continue;
                    // One level of lists in a list (a group's children), bounded, of closed objects holding no list.
                    Assert.True(innerSchema["maxItems"]!.GetValue<int>() > 0, $"{name}.{member}.{inner} has no bound.");
                    var nested = innerSchema["items"]!.AsObject();
                    Assert.Equal("object", nested["type"]!.GetValue<string>());
                    Assert.False(nested["additionalProperties"]!.GetValue<bool>());
                    foreach (var required in nested["required"]!.AsArray())
                        Assert.True(nested["properties"]!.AsObject().ContainsKey(required!.GetValue<string>()), $"{name}.{member}.{inner} requires a member it doesn't define.");
                    foreach (var (deepest, deepestSchema) in nested["properties"]!.AsObject())
                    {
                        Assert.Contains(deepestSchema!["type"]!.GetValue<string>(), new[] { "string", "integer", "number", "boolean", "object" });
                        Assert.False(string.IsNullOrWhiteSpace(deepestSchema["description"]?.GetValue<string>()), $"{name}.{member}.{inner}.{deepest} has no description.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// <c>list_sensors</c> gives each sensor's type, and a table's columns and format, so an AI app can set a table's
    /// columns; of its rows, only the first row's text it always gave (the value), never another row.
    /// </summary>
    [Fact]
    public async Task ListSensors_GivesEachType_AndATablesColumnsAndFormat_ButNoRowPastTheFirst()
    {
        var table = new System.Data.DataTable();
        table.Columns.Add("Process", typeof(InfoPanel.Plugins.PluginText));
        table.Columns.Add("CPU", typeof(InfoPanel.Plugins.PluginSensor));
        table.Rows.Add(new InfoPanel.Plugins.PluginText("", "", "first-row-app"), new InfoPanel.Plugins.PluginSensor("", "", 12.5f, "%"));
        table.Rows.Add(new InfoPanel.Plugins.PluginText("", "", "second-row-app"), new InfoPanel.Plugins.PluginSensor("", "", 3.25f, "%"));
        var source = new FixedSensorSource("fixed",
            (InfoPanel.Sensors.SensorKey.Plugin("/fixed/top"), "Top apps", new(InfoPanel.Sensors.SensorStatus.Live, InfoPanel.Sensors.SensorKind.Table,
                new InfoPanel.Models.SensorReading(table, "0:150|1:60", "first-row-app, 12.5%"))),
            (InfoPanel.Sensors.SensorKey.Plugin("/fixed/load"), "Load", new(InfoPanel.Sensors.SensorStatus.Live, InfoPanel.Sensors.SensorKind.Number,
                new InfoPanel.Models.SensorReading(1d, 9d, 5d, 4d, "%"))),
            (InfoPanel.Sensors.SensorKey.Plugin("/fixed/song"), "Song", new(InfoPanel.Sensors.SensorStatus.Live, InfoPanel.Sensors.SensorKind.Text,
                new InfoPanel.Models.SensorReading("Artist - Title"))),
            (InfoPanel.Sensors.SensorKey.Plugin("/fixed/idle"), "Idle", InfoPanel.Sensors.SensorState.Without(InfoPanel.Sensors.SensorStatus.Unavailable, InfoPanel.Sensors.SensorKind.Table)));
        using var fresh = new EngineFactory();
        using var engine = FixedSensorSource.Engine(fresh, source);
        var host = engine.Services.GetRequiredService<McpToolHost>();

        var (text, error, _) = await Call(host, "list_sensors", """{"query":"plugin:/fixed/"}""");

        Assert.False(error);
        var lines = text.Split('\n')[1..].Select(l => JsonNode.Parse(l)!.AsObject()).ToDictionary(l => l["key"]!.GetValue<string>());
        Assert.Equal(
            """{"key":"plugin:/fixed/top","name":"Top apps","group":"Fixed","unit":"","type":"table","status":"live","value":"first-row-app, 12.5%","columns":[{"name":"Process","type":"text","unit":""},{"name":"CPU","type":"number","unit":"%"}],"format":"0:150|1:60"}""",
            lines["plugin:/fixed/top"].ToJsonString());
        Assert.Equal(("number", 4d), (lines["plugin:/fixed/load"]["type"]!.GetValue<string>(), lines["plugin:/fixed/load"]["value"]!.GetValue<double>()));
        Assert.Equal(("text", "Artist - Title"), (lines["plugin:/fixed/song"]["type"]!.GetValue<string>(), lines["plugin:/fixed/song"]["value"]!.GetValue<string>()));
        // A table with no reading: its type, nothing more.
        Assert.Equal("""{"key":"plugin:/fixed/idle","name":"Idle","group":"Fixed","unit":"","type":"table","status":"unavailable"}""", lines["plugin:/fixed/idle"].ToJsonString());
        Assert.DoesNotContain("second-row-app", text);
    }

    [Fact]
    public async Task ListProfiles_GetProfile_ListSensors_AndRenderPreview_Answer()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);

        var (profiles, error, _) = await Call(host, "list_profiles");
        Assert.False(error);
        Assert.Contains("\"name\":\"Wide bar\"", profiles);
        Assert.Contains("\"width\":1920", profiles);
        Assert.Contains("\"unsaved\":false", profiles);

        var (summary, _, _) = await Call(host, "get_profile", """{"profile":"Wide bar"}""");
        var profile = JsonNode.Parse(summary)!;
        Assert.Equal("Wide bar", profile["name"]!.GetValue<string>());
        Assert.Equal(480, profile["height"]!.GetValue<int>());
        var group = profile["items"]![0]!;
        Assert.Equal("group", group["kind"]!.GetValue<string>());
        var ring = group["children"]!.AsArray().First(c => c!["name"]!.GetValue<string>() == "LOAD ring")!;
        Assert.Equal(120, ring["w"]!.GetValue<double>());
        Assert.Equal("infopanel:/mock/gpu/gpu-load", ring["bindings"]!["v"]!.GetValue<string>());
        Assert.Equal("scene", ring["madeWith"]!.GetValue<string>());
        Assert.Null(profile["item"]);

        var id = profile["id"]!.GetValue<string>();
        var (detail, _, _) = await Call(host, "get_profile", $$"""{"profile":"{{id}}","item":"LOAD ring"}""");
        var item = JsonNode.Parse(detail)!["item"]!;
        Assert.Equal("infopanel.scene/1", item["scene"]!["format"]!.GetValue<string>());

        var (sensors, _, _) = await Call(host, "list_sensors", """{"query":"gpu load"}""");
        Assert.Contains("infopanel:/mock/gpu/gpu-load", sensors);
        Assert.Contains("\"status\":\"live\"", sensors);
        var (limited, _, _) = await Call(host, "list_sensors", """{"limit":2}""");
        Assert.StartsWith("2 of ", limited);

        var (_, previewError, preview) = await Call(host, "render_preview", $$"""{"profile":"{{id}}","width":480}""");
        Assert.False(previewError);
        var image = preview["content"]!.AsArray().Single(c => c!["type"]!.GetValue<string>() == "image")!;
        Assert.Equal("image/png", image["mimeType"]!.GetValue<string>());
        using var bitmap = SKBitmap.Decode(Convert.FromBase64String(image["data"]!.GetValue<string>()));
        Assert.Equal((480, 120), (bitmap.Width, bitmap.Height));

        var (_, _, whole) = await Call(host, "render_preview", """{"profile":"Wide bar"}""");
        using var full = SKBitmap.Decode(Convert.FromBase64String(whole["content"]![0]!["data"]!.GetValue<string>()));
        Assert.Equal(1200, full.Width); // the profile's 1920, capped
    }

    [Fact]
    public async Task NotFound_NotOk_Ambiguous_AndBadArguments_AreErrorsNamingTheirCode()
    {
        using var factory = new SeededEngineFactory();
        var host = Host(factory);

        var (missing, missingError, _) = await Call(host, "get_profile", """{"profile":"Nope"}""");
        Assert.True(missingError);
        Assert.Contains("There's no profile Nope (notFound). list_profiles names them.", missing);

        var (broken, brokenError, _) = await Call(host, "render_preview", $$"""{"profile":"{{SeededEngineFactory.NotJson}}"}""");
        Assert.True(brokenError);
        Assert.Contains("can't be opened: unreadable", broken);

        foreach (var (tool, arguments, expected) in new[]
        {
            ("get_profile", "{}", "'profile' is required"),
            ("get_profile", """{"profile":3}""", "'profile' must be text"),
            ("render_preview", """{"profile":"x","width":5000}""", "'width' must be a whole number from 16 to 1200"),
            ("render_preview", """{"profile":"x","width":10.5}""", "'width' must be a whole number"),
            ("list_sensors", """{"colour":"red"}""", "'colour' isn't one of its arguments"),
            ("list_profiles", "[]", "the arguments are not an object"),
        })
        {
            var (text, isError, _) = await Call(host, tool, arguments);
            Assert.True(isError, tool);
            Assert.Contains("(arguments)", text);
            Assert.Contains(expected, text);
        }

        var (unknown, unknownError, _) = await Call(host, "delete_everything");
        Assert.True(unknownError);
        Assert.Contains("(unknownTool)", unknown);

        // Two profiles with one name: the ids are said, and the id picks one.
        using var client = factory.CreateApiClient();
        foreach (var id in new[] { "11111111-0000-4000-8000-000000000001", "11111111-0000-4000-8000-000000000002" })
        {
            using var created = await client.PostAsync("/api/profiles", new StringContent(
                $$"""{"id":"{{id}}","name":"Twin","width":100,"height":50}""", System.Text.Encoding.UTF8, "application/json"), Ct);
            Assert.True(created.IsSuccessStatusCode);
        }
        var (twins, twinsError, _) = await Call(host, "get_profile", """{"profile":"Twin"}""");
        Assert.True(twinsError);
        Assert.Contains("(ambiguous)", twins);
        Assert.Contains("11111111-0000-4000-8000-000000000002", twins);
        Assert.False((await Call(host, "get_profile", """{"profile":"11111111-0000-4000-8000-000000000001"}""")).IsError);
    }

    /// <summary>
    /// Codex p1 #7: a legal scene nested deeper than 64 (a newer scene format, kept as sent) is read through MCP as through
    /// REST, up to the format's 256 levels: the document's deepest, 252 arrays in its scene.
    /// </summary>
    [Theory]
    [InlineData(70)]
    [InlineData(252)]
    public async Task ADeeplyNestedScene_IsReadInFull(int arrays)
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var id = Guid.NewGuid().ToString();
        var nested = new string('[', arrays) + new string(']', arrays);
        using (var client = factory.CreateApiClient())
        {
            using var created = await client.PostAsync("/api/profiles", new StringContent($$$"""
                {"id": "{{{id}}}", "document": {"format": "infopanel.document/1", "id": "{{{id}}}", "profile": {"name": "Deep", "width": 200, "height": 100},
                 "items": [{"kind": "scene", "id": "deep", "placement": {"x": 0, "y": 0}, "scene": {"format": "infopanel.scene/2", "x": {{{nested}}}}}]}}
                """, System.Text.Encoding.UTF8, "application/json"), Ct);
            Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        }

        var (summary, summaryError, _) = await Call(host, "get_profile", $$"""{"profile":"{{id}}"}""");
        Assert.False(summaryError, summary);
        var (text, error, _) = await Call(host, "get_profile", $$"""{"profile":"{{id}}","item":"deep"}""");
        Assert.False(error, text);
        using var read = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 1000 });
        var x = read.RootElement.GetProperty("item").GetProperty("scene").GetProperty("x");
        var depth = 1;
        for (; x.GetArrayLength() == 1; x = x[0])
            depth++;
        Assert.Equal(arrays, depth);
    }

    /// <summary>
    /// Codex p1 #8: a permitted profile whose summary, escaped as the channel sends it, would pass the channel's 32 MiB
    /// (1,000 items of 16 bindings, each key 480 × é: about 15.7 MB stored) is answered within
    /// <see cref="GetProfileTool.MaxReplyBytes"/> as sent, its items cut with "… and N more items"; an item asked for by
    /// name is shown even when the list was cut before it.
    /// </summary>
    [Fact]
    public async Task ASummaryPastTheReplyCap_IsCutWithTheCountOfItemsLeft()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var id = Guid.NewGuid().ToString();
        var key = new string('é', 480);
        var body = new System.Text.StringBuilder();
        body.Append($$"""{"id":"{{id}}","document":{"format":"infopanel.document/1","id":"{{id}}","profile":{"name":"Wide keys","width":200,"height":100},"items":[""");
        for (var i = 0; i < 1000; i++)
        {
            body.Append(i == 0 ? "" : ",").Append($$"""{"kind":"scene","id":"i{{i}}","placement":{"x":0,"y":0},"scene":{"format":"infopanel.scene/2"},"bindings":{""");
            for (var b = 0; b < 16; b++)
                body.Append(b == 0 ? "" : ",").Append($$"""
                    "b{{b}}":{"key":"{{key}}"}
                    """);
            body.Append("}}");
        }
        body.Append("]}}");
        using (var client = factory.CreateApiClient())
        {
            using var created = await client.PostAsync("/api/profiles", new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json"), Ct);
            Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        }

        var (text, error, result) = await Call(host, "get_profile", $$"""{"profile":"{{id}}"}""");
        Assert.False(error, text[..Math.Min(text.Length, 500)]);
        var sent = InfoPanel.Hosting.LocalChannel.Line(result).Length;
        Assert.True(sent <= GetProfileTool.MaxReplyBytes + 1024, $"The reply is {sent} bytes as sent.");
        var summary = JsonNode.Parse(text)!;
        var listed = summary["items"]!.AsArray().Count;
        Assert.InRange(listed, 1, 999);
        Assert.Contains($"… and {1000 - listed} more items", summary["more"]!.GetValue<string>());

        var (detail, detailError, detailResult) = await Call(host, "get_profile", $$"""{"profile":"{{id}}","item":"i999"}""");
        Assert.False(detailError);
        Assert.True(InfoPanel.Hosting.LocalChannel.Line(detailResult).Length <= GetProfileTool.MaxReplyBytes + 1024);
        var item = JsonNode.Parse(detail)!["item"]!;
        Assert.Equal("i999", item["id"]!.GetValue<string>());
        Assert.Equal(key, item["bindings"]!["b15"]!.GetValue<string>());

        // A profile within the cap is listed whole, with no note.
        var (small, _, _) = await Call(host, "get_profile", """{"profile":"Wide bar"}""");
        Assert.Null(JsonNode.Parse(small)!["more"]);
    }

    [Fact]
    public async Task AnItemsSourceIsShown_AndAnItemThatIsntThereIsAnError()
    {
        using var factory = new SeededEngineFactory();
        var host = Host(factory);
        var (text, error, _) = await Call(host, "get_profile", $$"""{"profile":"{{SeededEngineFactory.RefSource}}","item":"held"}""");
        Assert.False(error, text);
        var item = JsonNode.Parse(text)!["item"]!;
        Assert.Equal("ref c1c2b7f0-opaque-reference", item["madeWith"]!.GetValue<string>());
        Assert.Equal("c1c2b7f0-opaque-reference", item["source"]!["ref"]!.GetValue<string>());

        var (missing, missingError, _) = await Call(host, "get_profile", $$"""{"profile":"{{SeededEngineFactory.RefSource}}","item":"nothing"}""");
        Assert.True(missingError);
        Assert.Contains("There's no item nothing", missing);
    }

    // ---- render_preview with a document ------------------------------------------------------------------------------

    /// <summary>A document as a client sends one to draw: no id (the engine draws it under one of its own), a black canvas.</summary>
    private static JsonObject Given(string name, int width, int height, params JsonObject[] items)
    {
        var document = TestDocs.Doc(name, width, height, items);
        document.Remove("id");
        return document;
    }

    /// <summary>render_preview's arguments with <paramref name="document"/> and the other members given.</summary>
    private static string DocumentCall(JsonObject document, string more = "") =>
        $$"""{"document":{{document.ToJsonString()}}{{more}}}""";

    private static SKBitmap PictureOf(JsonNode result) =>
        SKBitmap.Decode(Convert.FromBase64String(result["content"]!.AsArray().Single(c => c!["type"]!.GetValue<string>() == "image")!["data"]!.GetValue<string>()));

    /// <summary>Every file in the data folder but the log's, with its size and time written.</summary>
    private static List<string> DataFolder(EngineFactory factory) =>
        [.. Directory.EnumerateFiles(factory.DataDir, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(factory.DataDir, f).StartsWith("logs", StringComparison.Ordinal))
            .Select(f => new FileInfo(f))
            .Select(f => $"{Path.GetRelativePath(factory.DataDir, f.FullName)} {f.Length} {f.LastWriteTimeUtc:O}")
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// <c>render_preview</c> lists <c>document</c> (an object) and <c>simulated</c> (true or false) beside <c>profile</c>,
    /// which it no longer requires (exactly one of the two, checked when called); it stays a read tool.
    /// </summary>
    [Fact]
    public void RenderPreview_TakesADocumentOrAProfile_AndMadeUpValues_AndStaysARead()
    {
        var tool = McpToolHost.BuiltInList()["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "render_preview")!;
        var properties = tool["inputSchema"]!["properties"]!.AsObject();
        Assert.Equal(["profile", "document", "simulated", "width"], properties.Select(p => p.Key));
        Assert.Equal("object", properties["document"]!["type"]!.GetValue<string>());
        Assert.Equal("boolean", properties["simulated"]!["type"]!.GetValue<string>());
        Assert.Equal("[]", tool["inputSchema"]!["required"]!.ToJsonString());
        Assert.True(tool["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.Equal(ApiScope.Read, Scopes["render_preview"]);
    }

    /// <summary>
    /// A given document is drawn once (its own size, its items), and nothing is stored, listed or announced: the profile
    /// list, every file of the data folder and the workspace's change event are as before, and no render loop is made.
    /// </summary>
    [Fact]
    public async Task RenderPreview_DrawsAGivenDocument_AndStoresListsAndAnnouncesNothing()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = factory.Services.GetRequiredService<Workspace>();
        var render = factory.Services.GetRequiredService<InfoPanel.Output.RenderService>();
        var listed = (await Call(host, "list_profiles")).Text;
        var entries = workspace.List().Select(e => $"{e.Id} {e.Name} {e.Instance}").ToList();
        var files = DataFolder(factory);
        var changes = 0;
        void Changed(object? sender, ProfilesChangedEventArgs e) => Interlocked.Increment(ref changes);
        workspace.ProfilesChanged += Changed;
        JsonNode result;
        string text;
        bool error;
        try
        {
            (text, error, result) = await Call(host, "render_preview", DocumentCall(Given("Drawn", 200, 100,
                TestDocs.Scene("left", 0, 0, TestDocs.Rect("#ff0000", 100, 100)), TestDocs.Scene("right", 100, 0, TestDocs.Rect("#0000ff", 100, 100)))));
        }
        finally
        {
            workspace.ProfilesChanged -= Changed;
        }

        Assert.False(error, text);
        Assert.StartsWith("Drawn: a 200×100 picture of the 200×100 document, drawn once and kept nowhere.\n", text);
        using (var picture = PictureOf(result))
        {
            Assert.Equal((200, 100), (picture.Width, picture.Height));
            Assert.Equal(new SKColor(0xff, 0, 0), picture.GetPixel(50, 50));
            Assert.Equal(new SKColor(0, 0, 0xff), picture.GetPixel(150, 50));
        }
        Assert.Equal(0, changes);
        Assert.Equal(listed, (await Call(host, "list_profiles")).Text);
        Assert.Equal(entries, workspace.List().Select(e => $"{e.Id} {e.Name} {e.Instance}").ToList());
        Assert.Equal(files, DataFolder(factory));
        Assert.Equal(0, render.LoopCount);
    }

    /// <summary>
    /// A document the engine wouldn't store is refused as a create would refuse it, each error by its path under
    /// <c>/document</c> and its code, nothing drawn: one that breaks the schema, one over the format's limits (more
    /// items than a document holds), a canvas over the largest; one within them all, the largest canvas, is drawn within
    /// the picture's own bounds.
    /// </summary>
    [Fact]
    public async Task RenderPreview_RefusesADocumentACreateWouldRefuse_ByEachErrorsPathAndCode()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = factory.Services.GetRequiredService<Workspace>();

        var unplaced = Given("Broken", 100, 100, TestDocs.Scene("a"));
        unplaced["items"]![0]!.AsObject().Remove("placement");
        var tooMany = Given("Many", 100, 100, [.. Enumerable.Range(0, workspace.Limits.Items + 1).Select(i => TestDocs.Scene($"i{i}"))]);
        var huge = Given("Huge", 16385, 100, TestDocs.Scene("a"));
        foreach (var document in new[] { unplaced, tooMany, huge })
        {
            using var json = JsonDocument.Parse(document.ToJsonString());
            var expected = workspace.CheckImportDocument(json.RootElement);
            Assert.NotEmpty(expected);
            var (text, error, result) = await Call(host, "render_preview", DocumentCall(document));
            Assert.True(error, text);
            Assert.DoesNotContain(result["content"]!.AsArray(), c => c!["type"]!.GetValue<string>() == "image");
            Assert.Contains("(invalid)", text);
            foreach (var e in expected)
                Assert.Contains($"{e.Path} ({e.Code})", text);

            // A create of the same document is refused with the same codes.
            var created = workspace.Create(System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["id"] = TestDocs.NewId(), ["document"] = document.DeepClone() }.ToJsonString()));
            Assert.Null(created.Entry);
            Assert.Equal(expected.Select(e => e.Code).Order(), created.Invalid!.Errors.Select(e => e.Code).Order());
        }
        Assert.Contains("/document/items (limit.items)", (await Call(host, "render_preview", DocumentCall(tooMany))).Text);

        // The largest canvas the format takes: drawn, the picture no wider than the tool's most and no taller than a thumbnail's.
        var (largest, largestError, drawn) = await Call(host, "render_preview", DocumentCall(Given("Largest", 16384, 16384, TestDocs.Scene("a"))));
        Assert.False(largestError, largest);
        using var picture = PictureOf(drawn);
        Assert.Equal((RenderPreviewTool.MaxWidth, RenderPreviewTool.MaxWidth), (picture.Width, picture.Height));
    }

    /// <summary>Exactly one of <c>profile</c> and <c>document</c>: both, or neither, refused in words naming them, with the code <c>arguments</c>.</summary>
    [Fact]
    public async Task RenderPreview_TakesExactlyOneOfProfileAndDocument()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var (both, bothError, _) = await Call(host, "render_preview", DocumentCall(Given("Both", 100, 100), ""","profile":"Wide bar" """));
        Assert.True(bothError);
        Assert.Equal("render_preview can't take these arguments (arguments): give profile or document, not both.", both);
        var (neither, neitherError, _) = await Call(host, "render_preview", """{"width":100}""");
        Assert.True(neitherError);
        Assert.Equal("render_preview can't take these arguments (arguments): give profile (a profile's id or exact name) or document (a profile document to draw).", neither);
        var (notAnObject, notAnObjectError, _) = await Call(host, "render_preview", """{"document":5}""");
        Assert.True(notAnObjectError);
        Assert.Contains("'document' must be a JSON object", notAnObject);
    }

    /// <summary>A scene that shows a text input, its text dark on the black canvas so the contrast check quotes it.</summary>
    private static JsonObject SongScene() => JsonNode.Parse("""
        {"format": "infopanel.scene/1", "inputs": {"song": {"type": "text"}},
         "nodes": [{"kind": "text", "x": 0, "y": 0, "font": {"family": "Inter", "size": 20}, "fill": "#101010",
                    "text": {"op": "input", "name": "song", "field": "text"}}]}
        """)!.AsObject();

    private static WebApplicationFactory<Program> SongEngine(EngineFactory fresh) => FixedSensorSource.Engine(fresh,
        new FixedSensorSource("fixed", (InfoPanel.Sensors.SensorKey.Plugin("/fixed/song"), "Song", new(InfoPanel.Sensors.SensorStatus.Live,
            InfoPanel.Sensors.SensorKind.Text, new InfoPanel.Models.SensorReading("Artist - Title")))));

    private static JsonObject SongItem() => TestDocs.Scene("song", 0, 0, SongScene(), new JsonObject
    {
        ["name"] = "Song",
        ["bindings"] = new JsonObject { ["song"] = new JsonObject { ["key"] = "plugin:/fixed/song" } },
    });

    /// <summary>
    /// <c>simulated</c> draws made-up values in place of the readings, as a simulated subscription's: the text a sensor
    /// reads isn't drawn (the contrast check quotes the text drawn), and the answer says the values are made up.
    /// </summary>
    [Fact]
    public async Task RenderPreview_Simulated_DrawsMadeUpValues()
    {
        using var fresh = new EngineFactory();
        using var engine = SongEngine(fresh);
        var host = engine.Services.GetRequiredService<McpToolHost>();
        var document = Given("Song", 300, 40, SongItem());

        var (live, liveError, _) = await Call(host, "render_preview", DocumentCall(document));
        Assert.False(liveError, live);
        Assert.Contains("\"Artist - Title\"", live);
        Assert.DoesNotContain("made-up values", live);

        var (made, madeError, _) = await Call(host, "render_preview", DocumentCall(document, ""","simulated":true"""));
        Assert.False(madeError, made);
        Assert.StartsWith("Song: a 300×40 picture of the 300×40 document, drawn once and kept nowhere, with made-up values.\n", made);
        Assert.DoesNotContain("Artist - Title", made);
        Assert.Contains("\"Song\" (song): text \"", made); // a made-up text, quoted by the same check
    }

    /// <summary>
    /// A given document drawn with made-up values makes up a value of its type for every input without a reading: an
    /// unbound text shows a made-up text (the contrast check quotes the text drawn, dark on black) and an unbound table
    /// made-up rows (white, under it). Drawn with the readings, they draw nothing; and a stored profile's made-up draw
    /// reads them as a simulated subscription does, unbound.
    /// </summary>
    [Fact]
    public async Task RenderPreview_ADocumentWithMadeUpValues_MakesUpAValueForEveryInputWithoutAReading()
    {
        using var fresh = new EngineFactory();
        using var engine = SongEngine(fresh);
        var host = engine.Services.GetRequiredService<McpToolHost>();
        var workspace = engine.Services.GetRequiredService<Workspace>();
        JsonObject[] Items() =>
        [
            TestDocs.Scene("song", 0, 0, SongScene(), new JsonObject { ["name"] = "Song" }),
            TestDocs.Scene("procs", 0, 40, JsonNode.Parse("""
                {"format": "infopanel.scene/1", "inputs": {"p": {"type": "table"}},
                 "nodes": [{"kind": "table", "input": "p", "rows": 3, "rowHeight": 20, "header": false,
                            "columns": [{"column": 0, "w": 120}, {"column": 1, "w": 60}],
                            "font": {"family": "Inter", "size": 14}, "fill": "#ffffff"}]}
                """)!.AsObject(), new JsonObject { ["name"] = "Processes" }),
        ];
        var document = Given("Unbound", 300, 120, Items());
        // The white pixels the table draws, below the text: none when it has no rows.
        static int TableInk(JsonNode result)
        {
            using var picture = PictureOf(result);
            var ink = 0;
            for (var y = 40; y < picture.Height; y++)
                for (var x = 0; x < picture.Width; x++)
                    if (picture.GetPixel(x, y).Red > 200) ink++;
            return ink;
        }

        var (made, madeError, madeResult) = await Call(host, "render_preview", DocumentCall(document, ""","simulated":true"""));
        Assert.False(madeError, made);
        Assert.Contains("\"Song\" (song): text \"Text\"", made);
        Assert.True(TableInk(madeResult) > 100, "The table has made-up rows.");

        var (live, liveError, liveResult) = await Call(host, "render_preview", DocumentCall(document));
        Assert.False(liveError, live);
        Assert.DoesNotContain("(song): text", live);
        Assert.Equal(0, TableInk(liveResult));

        var id = TestDocs.Create(workspace, TestDocs.Doc("Unbound", 300, 120, Items()));
        var (profile, profileError, profileResult) = await Call(host, "render_preview", $$"""{"profile":"{{id}}","simulated":true}""");
        Assert.False(profileError, profile);
        Assert.Contains("with made-up values", profile);
        Assert.DoesNotContain("(song): text", profile);
        Assert.Equal(0, TableInk(profileResult));
    }

    /// <summary>A document with a member twice is refused in words, as an invalid one is, nothing drawn.</summary>
    [Fact]
    public async Task RenderPreview_RefusesADocumentWithAMemberTwice_InWords()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Given("Twice", 100, 100, TestDocs.Scene("a")).ToJsonString();
        var twice = document.Insert(1, "\"profile\":{\"name\":\"Other\",\"width\":50,\"height\":50},");

        var (text, error, result) = await Call(host, "render_preview", $$"""{"document":{{twice}}}""");
        Assert.True(error, text);
        Assert.DoesNotContain(result["content"]!.AsArray(), c => c!["type"]!.GetValue<string>() == "image");
        Assert.StartsWith("The document can't be drawn (invalid): /document (schema): Duplicate property \"profile\". (duplicateKey).", text);
        Assert.EndsWith("Nothing was drawn or kept.", text);
    }

    /// <summary>A sink that counts the frames a profile's live loop sends it.</summary>
    private sealed class Counter(string profileId) : InfoPanel.Output.IFrameSink
    {
        public int Frames;

        public string ProfileId { get; } = profileId;

        public InfoPanel.Output.FrameRequest Request { get; } = new(1, 30, IncludeBounds: false);

        public void OnFrame(InfoPanel.Output.RenderedFrame frame) => Interlocked.Increment(ref Frames);
    }

    /// <summary>
    /// <c>simulated</c> with <c>profile</c>: one draw of the profile's current generation with made-up values, apart from
    /// its live loop: no simulated loop is made, the live output gets no frame and keeps its tick, the document its
    /// revision, and the next live draw reads the sensor again.
    /// </summary>
    [Fact]
    public async Task RenderPreview_SimulatedProfile_ChangesNothingAboutTheLiveProfile()
    {
        using var fresh = new EngineFactory();
        using var songs = SongEngine(fresh);
        using var engine = songs.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(new InfoPanel.Output.RenderServiceOptions(AutoTick: false))));
        var host = engine.Services.GetRequiredService<McpToolHost>();
        var workspace = engine.Services.GetRequiredService<Workspace>();
        var render = engine.Services.GetRequiredService<InfoPanel.Output.RenderService>();
        var id = TestDocs.Create(workspace, TestDocs.Doc("Live song", 300, 40, SongItem()));
        var document = workspace.Find(id).Document!;
        var sink = new Counter(id);
        Assert.True(render.Attach(sink, document));
        try
        {
            render.Tick(id);
            long tick;
            using (var before = render.LatestSnapshot(id))
                tick = before!.Tick;
            var frames = Volatile.Read(ref sink.Frames);
            var rev = document.Rev;

            var (made, madeError, _) = await Call(host, "render_preview", $$"""{"profile":"{{id}}","simulated":true}""");
            Assert.False(madeError, made);
            Assert.StartsWith("Live song: a 300×40 picture of the 300×40 profile, with made-up values.\n", made);
            Assert.DoesNotContain("Artist - Title", made);

            Assert.False(render.IsSimulating(id));
            Assert.Equal(1, render.LoopCount);
            Assert.Equal(frames, Volatile.Read(ref sink.Frames));
            using (var after = render.LatestSnapshot(id))
                Assert.Equal(tick, after!.Tick);
            Assert.Equal(rev, document.Rev);
            var (live, _, _) = await Call(host, "render_preview", $$"""{"profile":"{{id}}"}""");
            Assert.Contains("\"Artist - Title\"", live);
        }
        finally
        {
            render.Detach(sink);
        }
    }

    /// <summary>A document's picture is held to the caller's <c>maxChars</c> as a profile's: drawn narrower, saying so.</summary>
    [Fact]
    public async Task RenderPreview_ADocumentsPicture_IsFittedToMaxChars()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var busy = Given("Busy", 400, 200, [.. Enumerable.Range(0, 60).Select(i =>
            TestDocs.Scene($"r{i}", i * 37 % 380, i * 53 % 180, TestDocs.Rect($"#{i * 40503 & 0xffffff:x6}", 15 + i % 7, 9 + i % 5)))]);

        async Task<InfoPanel.Mcp.Tools.ToolResult> Run(int? maxChars)
        {
            using var json = JsonDocument.Parse(DocumentCall(busy, ""","width":400"""));
            using var cutoff = new InfoPanel.Service.RelayCutoff(TimeProvider.System);
            return await host.RunAsync("render_preview", json.RootElement.Clone(), McpCall.Elsewhere(new InfoPanel.Service.RelayCallContext("e1", maxChars, cutoff)), Ct);
        }

        static string Words(InfoPanel.Mcp.Tools.ToolResult result) =>
            string.Join("\n", result.Content.Where(c => c!["type"]!.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()));
        static string Data(InfoPanel.Mcp.Tools.ToolResult result) => result.Content.Single(c => c!["type"]!.GetValue<string>() == "image")!["data"]!.GetValue<string>();

        var whole = await Run(null);
        Assert.False(whole.IsError, Words(whole));
        var maxChars = Words(whole).Length + Data(whole).Length / 2;
        var held = await Run(maxChars);
        Assert.False(held.IsError, Words(held));
        Assert.True(Words(held).Length + Data(held).Length <= maxChars);
        using var smaller = SKBitmap.Decode(Convert.FromBase64String(Data(held)));
        Assert.InRange(smaller.Width, InfoPanel.Output.Thumbnail.MinWidth, 399);
        Assert.StartsWith($"Busy: a {smaller.Width}×{smaller.Height} picture (drawn smaller than the 400 asked, to fit what this AI app takes) of the 400×200 document, drawn once and kept nowhere.\n",
            Words(held));
    }
}
