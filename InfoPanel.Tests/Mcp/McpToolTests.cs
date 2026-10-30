using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Mcp.Tools;
using InfoPanel.Security;
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
        ["get_design_guide"] = ApiScope.Read,
    };

    private static McpToolHost Host(EngineFactory factory) => factory.Services.GetRequiredService<McpToolHost>();

    private static async Task<(string Text, bool IsError, JsonNode Result)> Call(McpToolHost host, string tool, string arguments = "{}")
    {
        using var json = JsonDocument.Parse(arguments);
        var result = await host.CallAsync(tool, json.RootElement.Clone(), Ct);
        var text = string.Join("\n", result["content"]!.AsArray().Where(c => c!["type"]!.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()));
        return (text, result["isError"]!.GetValue<bool>(), result);
    }

    [Fact]
    public async Task EveryTool_DeclaresReadOrEdit_HasBuiltInText_AndAValidClosedSchema()
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
        Assert.False(string.IsNullOrWhiteSpace(list["instructions"]!.GetValue<string>()));
        var builtIn = McpToolHost.BuiltInList();
        Assert.Equal(list.ToJsonString(), builtIn.ToJsonString()); // no live folder: the engine lists the built-in text
        foreach (var listed in list["tools"]!.AsArray())
        {
            var name = listed!["name"]!.GetValue<string>();
            Assert.True(listed["description"]!.GetValue<string>().Length > 40, $"{name} has no built-in description.");
            Assert.Equal(Scopes[name] == ApiScope.Read, listed["annotations"]!["readOnlyHint"]!.GetValue<bool>());
            var schema = listed["inputSchema"]!.AsObject();
            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
            var properties = schema["properties"]!.AsObject();
            foreach (var required in schema["required"]!.AsArray())
                Assert.True(properties.ContainsKey(required!.GetValue<string>()), $"{name} requires an argument it doesn't define.");
            foreach (var (member, property) in properties)
            {
                Assert.Contains(property!["type"]!.GetValue<string>(), new[] { "string", "integer" });
                Assert.False(string.IsNullOrWhiteSpace(property["description"]?.GetValue<string>()), $"{name}.{member} has no description.");
            }
        }
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
        Assert.Equal("plugin:/infopanel.mock/gpu/gpu-load", ring["bindings"]!["v"]!.GetValue<string>());
        Assert.Equal("scene", ring["madeWith"]!.GetValue<string>());
        Assert.Null(profile["item"]);

        var id = profile["id"]!.GetValue<string>();
        var (detail, _, _) = await Call(host, "get_profile", $$"""{"profile":"{{id}}","item":"LOAD ring"}""");
        var item = JsonNode.Parse(detail)!["item"]!;
        Assert.Equal("infopanel.scene/1", item["scene"]!["format"]!.GetValue<string>());

        var (sensors, _, _) = await Call(host, "list_sensors", """{"query":"gpu load"}""");
        Assert.Contains("plugin:/infopanel.mock/gpu/gpu-load", sensors);
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
}
