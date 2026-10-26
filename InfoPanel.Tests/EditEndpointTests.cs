using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>The write API (plan §6.3) over HTTP: profile and item edits, undo/redo/save/revert, import and export.</summary>
public class EditEndpointTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, object? body = null, HttpClient? client = null)
    {
        using var api = client == null ? factory.CreateApiClient() : null;
        using var request = new HttpRequestMessage(method, path);
        if (body is string raw)
            request.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        else if (body != null)
            request.Content = JsonContent.Create(body);
        using var response = await (client ?? api!).SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        if ((int)response.StatusCode >= 400)
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<string> CreateProfile(string name = "Edit me")
    {
        var (status, body) = await Send(HttpMethod.Post, "/api/profiles", new { name, width = 480, height = 320 });
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("id").GetString()!;
    }

    private async Task<string> AddItem(string profileId, object request)
    {
        var (status, body) = await Send(HttpMethod.Post, $"/api/profiles/{profileId}/items", request);
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("items")[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Create_Add_Patch_Save_WritesTheItemsFileWithIds()
    {
        var id = await CreateProfile();
        Assert.True(File.Exists(Path.Combine(factory.DataDir, "profiles", id + ".xml")));

        var (_, added) = await Send(HttpMethod.Post, $"/api/profiles/{id}/items", new { kind = "text" });
        var item = added.GetProperty("items")[0];
        var itemId = item.GetProperty("id").GetString()!;
        Assert.Equal("Custom Text", item.GetProperty("props").GetProperty("name").GetString());
        Assert.Equal(2, added.GetProperty("revision").GetInt64());
        Assert.False(added.TryGetProperty("doc", out _));

        var (status, patched) = await Send(HttpMethod.Patch, $"/api/profiles/{id}/items",
            new { patches = new[] { new { id = itemId, set = new { fontSize = 48 } } }, coalesceKey = "fontSize" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(48, patched.GetProperty("items")[0].GetProperty("props").GetProperty("fontSize").GetInt32());
        Assert.Equal(3, patched.GetProperty("revision").GetInt64());
        Assert.True(patched.GetProperty("dirty").GetBoolean());
        Assert.True(patched.GetProperty("canUndo").GetBoolean());

        var (saveStatus, saved) = await Send(HttpMethod.Post, $"/api/profiles/{id}/save");
        Assert.Equal(HttpStatusCode.OK, saveStatus);
        Assert.False(saved.GetProperty("dirty").GetBoolean());
        Assert.Equal(3, saved.GetProperty("revision").GetInt64());

        var xml = await File.ReadAllTextAsync(Path.Combine(factory.DataDir, "profiles", id + ".xml"), Ct);
        Assert.Contains($"<Guid>{itemId}</Guid>", xml);
        Assert.Contains("<FontSize>48</FontSize>", xml);
        Assert.Contains($"<Guid>{id}</Guid>", await File.ReadAllTextAsync(Path.Combine(factory.DataDir, "profiles.xml"), Ct));
    }

    [Fact]
    public async Task Undo_Redo_Revert_ReturnTheDoc()
    {
        var id = await CreateProfile();
        var itemId = await AddItem(id, new { kind = "sensor" });
        await Send(HttpMethod.Post, $"/api/profiles/{id}/save");
        await Send(HttpMethod.Patch, $"/api/profiles/{id}/items", new { patches = new[] { new { id = itemId, set = new { x = 5 } } } });

        var (_, undone) = await Send(HttpMethod.Post, $"/api/profiles/{id}/undo");
        var doc = undone.GetProperty("doc");
        Assert.Equal(100, doc.GetProperty("items")[0].GetProperty("props").GetProperty("x").GetInt32());
        Assert.False(doc.GetProperty("dirty").GetBoolean());
        Assert.True(doc.GetProperty("canRedo").GetBoolean());
        Assert.Equal(undone.GetProperty("revision").GetInt64(), doc.GetProperty("revision").GetInt64());

        var (_, redone) = await Send(HttpMethod.Post, $"/api/profiles/{id}/redo");
        Assert.Equal(5, redone.GetProperty("doc").GetProperty("items")[0].GetProperty("props").GetProperty("x").GetInt32());
        Assert.True(redone.GetProperty("dirty").GetBoolean());

        var (_, reverted) = await Send(HttpMethod.Post, $"/api/profiles/{id}/revert");
        Assert.Equal(100, reverted.GetProperty("doc").GetProperty("items")[0].GetProperty("props").GetProperty("x").GetInt32());
        Assert.False(reverted.GetProperty("dirty").GetBoolean());
        Assert.True(reverted.GetProperty("canUndo").GetBoolean());
    }

    [Fact]
    public async Task StructuralEdits_OverHttp()
    {
        var id = await CreateProfile();
        var group = await AddItem(id, new { kind = "group" });
        var other = await AddItem(id, new { kind = "group" });
        var text = await AddItem(id, new { kind = "text", parentId = group, index = 0, set = new { name = "Inside" } });

        var (_, moved) = await Send(HttpMethod.Post, $"/api/profiles/{id}/items/{text}/move", new { parentId = other, index = 0 });
        var items = moved.GetProperty("doc").GetProperty("items");
        Assert.Equal(0, items[0].GetProperty("children").GetArrayLength());
        Assert.Equal(text, items[1].GetProperty("children")[0].GetProperty("id").GetString());

        var (_, toTop) = await Send(HttpMethod.Post, $"/api/profiles/{id}/items/{text}/move", """{"parentId":null,"index":0}""");
        Assert.Equal(text, toTop.GetProperty("doc").GetProperty("items")[0].GetProperty("id").GetString());

        var (_, duplicated) = await Send(HttpMethod.Post, $"/api/profiles/{id}/items/{text}/duplicate");
        var copy = duplicated.GetProperty("items")[0];
        Assert.NotEqual(text, copy.GetProperty("id").GetString());
        Assert.Equal(110, copy.GetProperty("props").GetProperty("x").GetInt32());

        var (status, removed) = await Send(HttpMethod.Delete, $"/api/profiles/{id}/items/{text}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, removed.GetProperty("doc").GetProperty("items").GetArrayLength());

        var (_, collapsed) = await Send(HttpMethod.Patch, $"/api/profiles/{id}/items",
            new { patches = new[] { new { id = group, set = new { isExpanded = false } } }, record = false });
        Assert.False(collapsed.GetProperty("items")[0].GetProperty("props").GetProperty("isExpanded").GetBoolean());

        var (_, renamed) = await Send(HttpMethod.Patch, $"/api/profiles/{id}", new { set = new { name = "Renamed", backgroundColor = "#FF102030" } });
        Assert.Equal("Renamed", renamed.GetProperty("doc").GetProperty("profile").GetProperty("name").GetString());
        var (_, list) = await Send(HttpMethod.Get, "/api/profiles");
        Assert.Contains(list.EnumerateArray(), p => p.GetProperty("id").GetString() == id && p.GetProperty("name").GetString() == "Renamed");
    }

    [Fact]
    public async Task ExportThenImport_GivesANewProfileWithTheSameItems()
    {
        var id = await CreateProfile("My Panel");
        await AddItem(id, new { kind = "clock" });
        await AddItem(id, new { kind = "donut", set = new { color = "#FF00FF00" } });

        using var client = factory.CreateApiClient();
        using var export = await client.GetAsync($"/api/profiles/{id}/export", Ct);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);
        var fileName = export.Content.Headers.ContentDisposition?.FileNameStar ?? export.Content.Headers.ContentDisposition?.FileName;
        Assert.Matches(@"^My_Panel-\d+\.infopanel$", fileName!.Trim('"'));
        var archive = await export.Content.ReadAsByteArrayAsync(Ct);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(archive);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", fileName);
        using var import = await client.PostAsync("/api/import", form, Ct);
        Assert.Equal(HttpStatusCode.Created, import.StatusCode);
        var summary = JsonDocument.Parse(await import.Content.ReadAsStringAsync(Ct)).RootElement;
        var importedId = summary.GetProperty("id").GetString()!;
        Assert.NotEqual(id, importedId);
        Assert.Equal("[Import] My Panel", summary.GetProperty("name").GetString());

        var (_, original) = await Send(HttpMethod.Get, $"/api/profiles/{id}");
        var (_, imported) = await Send(HttpMethod.Get, $"/api/profiles/{importedId}");
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(original.GetProperty("items").GetRawText()),
            JsonNode.Parse(imported.GetProperty("items").GetRawText())));

        using var notAZip = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "x.infopanel" } };
        using var bad = await client.PostAsync("/api/import", notAZip, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var notMultipart = await client.PostAsync("/api/import", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, notMultipart.StatusCode);
    }

    [Fact]
    public async Task Duplicate_And_Delete_Profiles()
    {
        var id = await CreateProfile("Twin");
        await AddItem(id, new { kind = "bar" });

        var (status, copy) = await Send(HttpMethod.Post, $"/api/profiles/{id}/duplicate");
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("Twin copy", copy.GetProperty("name").GetString());
        var copyId = copy.GetProperty("id").GetString()!;
        var (_, copyDoc) = await Send(HttpMethod.Get, $"/api/profiles/{copyId}");
        Assert.Equal("bar", copyDoc.GetProperty("items")[0].GetProperty("kind").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/api/profiles/{copyId}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/api/profiles/{copyId}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, $"/api/profiles/{copyId}")).Status);
        Assert.False(File.Exists(Path.Combine(factory.DataDir, "profiles", copyId + ".xml")));
    }

    [Theory]
    [InlineData("""{"patches":[{"id":"ITEM","set":{"nope":1}}]}""", "nope")]
    [InlineData("""{"patches":[{"id":"ITEM","set":{"valueType":"max"}}]}""", "valueType")]
    [InlineData("""{"patches":[{"id":"ITEM","set":{"color":"teal"}}]}""", "color")]
    [InlineData("""{"patches":[{"id":"ITEM","set":{"kind":"text"}}]}""", "kind")]
    [InlineData("""{"patches":[{"id":"ITEM","set":{"fontSize":"48"}}]}""", "fontSize")]
    [InlineData("""{"patches":[{"id":"ITEM","set":{"x":1}}],"record":false}""", "x")]
    [InlineData("""{"patches":[{"id":"ITEM"}]}""", "set")]
    [InlineData("""{}""", "patches")]
    public async Task InvalidPatches_Are400_WithTheKey(string body, string key)
    {
        var id = await CreateProfile();
        var item = await AddItem(id, new { kind = "sensor" });

        var (status, problem) = await Send(HttpMethod.Patch, $"/api/profiles/{id}/items", body.Replace("ITEM", item));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal(key, problem.GetProperty("key").GetString());
    }

    [Theory]
    [InlineData("text", """{"name":"\u0000"}""", "name")]
    [InlineData("text", """{"name":"lone \uD800"}""", "name")]
    [InlineData("graph", """{"type":"HISTOGRAM","thickness":2147483647,"step":2147483647}""", "thickness")]
    [InlineData("text", """{"fontSize":100000}""", "fontSize")]
    [InlineData("calendar", """{"format":"Q"}""", "format")]
    public async Task RefusedValues_Are400_ChangeNothing_AndLaterEditsWork(string kind, string set, string key)
    {
        var id = await CreateProfile();
        var item = await AddItem(id, new { kind });
        var (_, before) = await Send(HttpMethod.Get, $"/api/profiles/{id}");

        var (status, problem) = await Send(HttpMethod.Patch, $"/api/profiles/{id}/items",
            $$"""{"patches":[{"id":"{{item}}","set":{{set}}}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(key, problem.GetProperty("key").GetString());
        var (_, after) = await Send(HttpMethod.Get, $"/api/profiles/{id}");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before.GetRawText()), JsonNode.Parse(after.GetRawText())));

        var (okStatus, ok) = await Send(HttpMethod.Patch, $"/api/profiles/{id}/items", new { patches = new[] { new { id = item, set = new { x = 3 } } } });
        Assert.Equal(HttpStatusCode.OK, okStatus);
        Assert.Equal(before.GetProperty("revision").GetInt64() + 1, ok.GetProperty("revision").GetInt64());

        var (profileStatus, profileProblem) = await Send(HttpMethod.Patch, $"/api/profiles/{id}", """{"set":{"name":"\u0001"}}""");
        Assert.Equal(HttpStatusCode.BadRequest, profileStatus);
        Assert.Equal("name", profileProblem.GetProperty("key").GetString());
    }

    [Fact]
    public async Task Import_OfMalformedMultipart_Is400()
    {
        using var client = factory.CreateApiClient();

        async Task Expect400(HttpContent content)
        {
            using var response = await client.PostAsync("/api/import", content, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("file", JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("key").GetString());
        }

        // No boundary.
        using var noBoundary = new ByteArrayContent(Encoding.ASCII.GetBytes("--x\r\n\r\n"));
        noBoundary.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
        await Expect400(noBoundary);

        // A body cut off in the middle of a part.
        using var truncated = new ByteArrayContent(Encoding.ASCII.GetBytes(
            "--b\r\nContent-Disposition: form-data; name=\"file\"; filename=\"x.infopanel\"\r\n\r\nPK\u0003\u0004 and then nothing"));
        truncated.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=b");
        await Expect400(truncated);
    }

    [Fact]
    public async Task ErrorShapes_400_404_409()
    {
        var id = await CreateProfile();
        var group = await AddItem(id, new { kind = "group" });
        var text = await AddItem(id, new { kind = "text", parentId = group });
        var unknown = Guid.NewGuid();

        async Task Expect(HttpStatusCode expected, HttpMethod method, string path, object? body = null, string? key = null)
        {
            var (status, problem) = await Send(method, path, body);
            Assert.Equal(expected, status);
            Assert.Equal((int)expected, problem.GetProperty("status").GetInt32());
            if (key != null)
                Assert.Equal(key, problem.GetProperty("key").GetString());
        }

        await Expect(HttpStatusCode.BadRequest, HttpMethod.Post, $"/api/profiles/{id}/items", new { kind = "image" }, "kind");
        await Expect(HttpStatusCode.BadRequest, HttpMethod.Post, $"/api/profiles/{id}/items", new { }, "kind");
        await Expect(HttpStatusCode.BadRequest, HttpMethod.Post, $"/api/profiles/{id}/items", "{not json");
        await Expect(HttpStatusCode.BadRequest, HttpMethod.Post, "/api/profiles", new { name = "x", width = 0, height = 1 }, "width");
        await Expect(HttpStatusCode.BadRequest, HttpMethod.Patch, $"/api/profiles/{id}", new { set = new { id = "x" } }, "id");
        await Expect(HttpStatusCode.BadRequest, HttpMethod.Post, $"/api/profiles/{id}/items/{text}/move", new { parentId = (string?)null }, "index");
        await Expect(HttpStatusCode.NotFound, HttpMethod.Patch, $"/api/profiles/{unknown}/items", new { patches = Array.Empty<object>() });
        await Expect(HttpStatusCode.NotFound, HttpMethod.Post, $"/api/profiles/{unknown}/undo");
        await Expect(HttpStatusCode.NotFound, HttpMethod.Post, "/api/profiles/not-a-guid/save");
        await Expect(HttpStatusCode.NotFound, HttpMethod.Get, $"/api/profiles/{unknown}/export");
        await Expect(HttpStatusCode.NotFound, HttpMethod.Delete, $"/api/profiles/{id}/items/{unknown}", key: "id");
        await Expect(HttpStatusCode.NotFound, HttpMethod.Patch, $"/api/profiles/{id}/items",
            new { patches = new[] { new { id = unknown, set = new { x = 1 } } } }, "id");
        await Expect(HttpStatusCode.NotFound, HttpMethod.Post, $"/api/profiles/{id}/items/{text}/move", new { parentId = unknown, index = 0 }, "parentId");
        await Expect(HttpStatusCode.Conflict, HttpMethod.Post, $"/api/profiles/{id}/items/{group}/move", new { parentId = group, index = 0 }, "parentId");
        await Expect(HttpStatusCode.Conflict, HttpMethod.Post, $"/api/profiles/{id}/items/{group}/move", new { parentId = text, index = 0 }, "parentId");
        await Expect(HttpStatusCode.Conflict, HttpMethod.Post, $"/api/profiles/{id}/items", new { kind = "text", parentId = text }, "parentId");

        var (_, doc) = await Send(HttpMethod.Get, $"/api/profiles/{id}");
        Assert.Equal(3, doc.GetProperty("revision").GetInt64()); // none of the refused edits changed anything
    }

    [Fact]
    public async Task Mutations_NeedTheBearerToken_AndAnAllowedOrigin()
    {
        var id = await CreateProfile();
        using var raw = factory.CreateRawClient();

        var (unauthorized, _) = await Send(HttpMethod.Post, $"/api/profiles/{id}/items", new { kind = "text" }, raw);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized);

        using var foreign = factory.CreateApiClient();
        foreign.DefaultRequestHeaders.Add("Origin", "http://evil.example");
        var (forbidden, _) = await Send(HttpMethod.Post, $"/api/profiles/{id}/items", new { kind = "text" }, foreign);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden);
        var (forbiddenDelete, _) = await Send(HttpMethod.Delete, $"/api/profiles/{id}", null, foreign);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenDelete);

        using var webClient = factory.CreateApiClient();
        webClient.DefaultRequestHeaders.Add("Origin", EngineFactory.DevOrigin);
        var (allowed, _) = await Send(HttpMethod.Post, $"/api/profiles/{id}/items", new { kind = "text" }, webClient);
        Assert.Equal(HttpStatusCode.OK, allowed);

        var (_, doc) = await Send(HttpMethod.Get, $"/api/profiles/{id}");
        Assert.Equal(1, doc.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Kinds_MatchWebClientsSnapshot()
    {
        var (_, kinds) = await Send(HttpMethod.Get, "/api/kinds");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "InfoPanel.slnx")))
            root = root.Parent!;
        var snapshot = await File.ReadAllTextAsync(Path.Combine(root.FullName, "client", "src", "api", "kinds.generated.json"), Ct);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(snapshot), JsonNode.Parse(kinds.GetRawText())));
    }
}
