using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>Documents, items and commits as a client sends them (JSON), for the host's tests.</summary>
internal static class TestDocs
{
    /// <summary>A new profile id: a UUID in lower case.</summary>
    public static string NewId() => Guid.NewGuid().ToString();

    public static JsonObject Rect(string fill = "#ff0000", double w = 20, double h = 10) => new()
    {
        ["format"] = "infopanel.scene/1",
        ["nodes"] = new JsonArray(new JsonObject { ["kind"] = "rect", ["w"] = w, ["h"] = h, ["fill"] = fill }),
    };

    /// <summary>A scene with a number input <c>v</c> drawn as a bar <c>v</c> pixels wide and 10 high.</summary>
    public static JsonObject InputScene() => JsonNode.Parse(
        """{"format": "infopanel.scene/1", "inputs": {"v": {"type": "number"}}, "nodes": [{"kind": "rect", "w": {"op": "input", "name": "v"}, "h": 10, "fill": "#00ff00"}]}""")!.AsObject();

    /// <summary>A scene that breaks a rule (its rect reads an input it doesn't declare): kept, drawn as a placeholder.</summary>
    public static JsonObject InvalidScene() => JsonNode.Parse(
        """{"format": "infopanel.scene/1", "nodes": [{"kind": "rect", "w": {"op": "input", "name": "v"}, "h": 10, "fill": "#ffffff"}]}""")!.AsObject();

    public static JsonObject Scene(string id, double x = 0, double y = 0, JsonObject? scene = null, JsonObject? extra = null)
    {
        var item = new JsonObject
        {
            ["kind"] = "scene",
            ["id"] = id,
            ["placement"] = new JsonObject { ["x"] = x, ["y"] = y },
            ["scene"] = scene ?? Rect(),
        };
        if (extra != null)
            foreach (var (k, v) in extra)
                item[k] = v?.DeepClone();
        return item;
    }

    public static JsonObject Group(string id, params JsonObject[] children) => new()
    {
        ["kind"] = "group",
        ["id"] = id,
        ["placement"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
        ["children"] = new JsonArray(children.Select(c => (JsonNode)c).ToArray()),
    };

    public static JsonObject Doc(string name, int width, int height, params JsonObject[] items) => new()
    {
        ["format"] = "infopanel.document/1",
        ["id"] = NewId(),
        ["profile"] = new JsonObject { ["name"] = name, ["width"] = width, ["height"] = height, ["background"] = "#000000" },
        ["items"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()),
    };

    public static JsonObject Place(string id, double x, double y, long? baseRev = null) =>
        Op(new JsonObject { ["op"] = "setPlacement", ["id"] = id, ["placement"] = new JsonObject { ["x"] = x, ["y"] = y } }, baseRev);

    public static JsonObject Design(string id, JsonObject scene, long? baseRev = null) =>
        Op(new JsonObject { ["op"] = "setDesign", ["id"] = id, ["scene"] = scene }, baseRev);

    public static JsonObject Bind(string id, string input, string key) =>
        new() { ["op"] = "setBindings", ["id"] = id, ["bindings"] = new JsonObject { [input] = new JsonObject { ["key"] = key } } };

    public static JsonObject Attrs(string id, bool? visible = null, string? name = null)
    {
        var set = new JsonObject();
        if (visible != null) set["visible"] = visible;
        if (name != null) set["name"] = name;
        return new JsonObject { ["op"] = "setAttributes", ["id"] = id, ["set"] = set };
    }

    public static JsonObject Insert(JsonObject item) => new() { ["op"] = "insert", ["item"] = item };

    public static JsonObject Remove(string id) => new() { ["op"] = "remove", ["id"] = id };

    public static JsonObject Profile(JsonObject set) => new() { ["op"] = "profile", ["set"] = set };

    private static JsonObject Op(JsonObject op, long? baseRev)
    {
        if (baseRev != null)
            op["baseRev"] = baseRev;
        return op;
    }

    /// <summary>A commit body of <paramref name="instance"/> based on <paramref name="baseRev"/>.</summary>
    public static JsonObject CommitBody(string instance, long baseRev, params JsonObject[] ops) => new()
    {
        ["opId"] = Guid.NewGuid().ToString("N"),
        ["instance"] = instance,
        ["baseRev"] = baseRev,
        ["ops"] = new JsonArray(ops.Select(o => (JsonNode)o).ToArray()),
    };

    // ---- in process -------------------------------------------------------------------------------------------------

    /// <summary>Creates a profile in <paramref name="workspace"/> from <paramref name="document"/>; returns its id.</summary>
    public static string Create(Workspace workspace, JsonObject document)
    {
        var id = NewId();
        var outcome = workspace.Create(Encoding.UTF8.GetBytes(new JsonObject { ["id"] = id, ["document"] = document.DeepClone() }.ToJsonString()));
        Assert.True(outcome.Entry != null, string.Join("; ", outcome.Invalid?.Errors.Select(e => $"{e.Path} {e.Code} {e.Message}") ?? []));
        return id;
    }

    /// <summary>Applies a commit based on the document's current revision; it must apply.</summary>
    public static CommitResult Commit(ProfileDocument doc, params JsonObject[] ops)
    {
        var outcome = doc.Commit(Encoding.UTF8.GetBytes(CommitBody(doc.Instance, doc.Rev, ops).ToJsonString()));
        Assert.True(outcome.Result != null, $"{outcome.Status} {JsonSerializer.Serialize(outcome, SpecJson.Options)}");
        return outcome.Result;
    }

    // ---- over HTTP --------------------------------------------------------------------------------------------------

    public static async Task<string> InstanceAsync(HttpClient api, CancellationToken ct) =>
        (await api.GetFromJsonAsync<JsonElement>("/api/session", ct)).GetProperty("instance").GetString()!;

    /// <summary>The profile's current revision (<c>GET /api/profiles/{id}</c>).</summary>
    public static async Task<long> RevAsync(HttpClient api, string id, CancellationToken ct) =>
        (await api.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", ct)).GetProperty("rev").GetInt64();

    /// <summary>Creates a profile from a document over HTTP; returns its id.</summary>
    public static async Task<string> CreateAsync(HttpClient api, JsonObject document, CancellationToken ct)
    {
        var id = NewId();
        using var response = await api.PostAsync("/api/profiles", Json(new JsonObject { ["id"] = id, ["document"] = document.DeepClone() }), ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return id;
    }

    /// <summary>Commits <paramref name="ops"/> over HTTP, based on the profile's current revision; returns the response.</summary>
    public static async Task<HttpResponseMessage> CommitAsync(HttpClient api, string id, CancellationToken ct, params JsonObject[] ops) =>
        await api.PostAsync($"/api/profiles/{id}/commit", Json(CommitBody(await InstanceAsync(api, ct), await RevAsync(api, id, ct), ops)), ct);

    public static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
