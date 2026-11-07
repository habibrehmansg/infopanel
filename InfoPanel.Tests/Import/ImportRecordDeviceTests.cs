using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace InfoPanel.Tests.Import;

/// <summary>
/// The migration record's 1.x panel devices (spec/docs/import.md "The record"): <c>devices</c> stored as given and
/// answered by <c>PUT</c> and <c>GET /api/import/v1</c>; absent when the request had none, so a client tells a folder whose
/// devices it dealt with from one whose devices it didn't; checked as every body; a stored one held to its schema.
/// </summary>
public class ImportRecordDeviceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Expect(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"expected {(int)status}, got {(int)response.StatusCode}: {text}");
        return text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default;
    }

    private const string Saved = "3c2b1a09-8f7e-4d6c-9b5a-493827160504";

    private const string Devices = $$"""
        [
          {"family":"beada","deviceId":"USB\\VID_4E58&PID_1001\\934072005C","deviceLocation":"Port_#0002.Hub_#0001","id":"{{Saved}}","outcome":"imported"},
          {"family":"beada","deviceId":"","deviceLocation":"","id":null,"outcome":"failed","reason":"No model."},
          {"family":"turing","deviceId":"a1","deviceLocation":"","id":null,"outcome":"skipped"}
        ]
        """;

    private static string Request(string? devices) =>
        $$"""{"profiles":[{"v1Id":"{{V1Fixture.Desk}}","id":"{{V1Fixture.Desk}}","outcome":"imported"}],"settings":"imported"{{(devices == null ? "" : $",\"devices\":{devices}")}}}""";

    private static string Canonical(JsonElement element) => JsonNode.Parse(element.GetRawText())!.ToJsonString();

    private static string Canonical(string json) => JsonNode.Parse(json)!.ToJsonString();

    [Fact]
    public async Task ARecordsDevices_AreStoredAsGiven_AndAnsweredByPutAndGet()
    {
        using var engine = new V1FolderEngineFactory();
        using var client = engine.CreateApiClient();

        var done = await Expect(await client.PutAsync("/api/import/v1", Json(Request(Devices)), Ct), HttpStatusCode.OK);
        Assert.Equal("done", done.GetProperty("state").GetString());
        Assert.Equal(Canonical(Devices), Canonical(done.GetProperty("record").GetProperty("devices")));

        var again = await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK);
        Assert.Equal(Canonical(Devices), Canonical(again.GetProperty("record").GetProperty("devices")));
        var stored = JsonNode.Parse(File.ReadAllText(Path.Combine(engine.DataDir, "import-v1.json")))!.AsObject();
        Assert.Equal(Canonical(Devices), stored["devices"]!.ToJsonString());

        // An empty list is a list: the folder's devices were dealt with, none of them brought over.
        done = await Expect(await client.PutAsync("/api/import/v1", Json(Request("[]")), Ct), HttpStatusCode.OK);
        Assert.Equal(0, done.GetProperty("record").GetProperty("devices").GetArrayLength());
        Assert.Equal("[]", JsonNode.Parse(File.ReadAllText(Path.Combine(engine.DataDir, "import-v1.json")))!["devices"]!.ToJsonString());

        // A record that replaces it without devices has none: the request replaces the whole record.
        await Expect(await client.PutAsync("/api/import/v1", Json(Request(Devices)), Ct), HttpStatusCode.OK);
        done = await Expect(await client.PutAsync("/api/import/v1", Json(Request(null)), Ct), HttpStatusCode.OK);
        Assert.False(done.GetProperty("record").TryGetProperty("devices", out _));
        again = await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK);
        Assert.False(again.GetProperty("record").TryGetProperty("devices", out _));
        Assert.False(JsonNode.Parse(File.ReadAllText(Path.Combine(engine.DataDir, "import-v1.json")))!.AsObject().ContainsKey("devices"));
    }

    [Fact]
    public async Task ARecordWithoutDevices_HasNone()
    {
        using var engine = new V1FolderEngineFactory();
        using var client = engine.CreateApiClient();

        var done = await Expect(await client.PutAsync("/api/import/v1", Json(Request(null)), Ct), HttpStatusCode.OK);
        Assert.False(done.GetProperty("record").TryGetProperty("devices", out _));
        Assert.False(JsonNode.Parse(File.ReadAllText(Path.Combine(engine.DataDir, "import-v1.json")))!.AsObject().ContainsKey("devices"));
    }

    [Fact]
    public async Task ARecordsDevices_AreCheckedAsEveryBody_AndARefusedOneStoresNothing()
    {
        using var engine = new V1FolderEngineFactory();
        using var client = engine.CreateApiClient();

        string One(string outcome) => $"[{outcome}]";
        var tooMany = "[" + string.Join(",", Enumerable.Repeat("""{"family":"beada","deviceId":"","deviceLocation":"","id":null,"outcome":"skipped"}""", 513)) + "]";
        foreach (var (devices, path) in new[]
        {
            (One("""{"family":"beada","deviceId":"","deviceLocation":"","id":null,"outcome":"done"}"""), "/devices/0/outcome"),
            (One($$"""{"family":"beada","deviceId":"","deviceLocation":"","id":"{{Saved.ToUpperInvariant()}}","outcome":"imported"}"""), "/devices/0/id"),
            (One("""{"family":"beadapanel","deviceId":"","deviceLocation":"","id":null,"outcome":"failed"}"""), "/devices/0/family"),
            (One("""{"family":"beada","deviceId":"","id":null,"outcome":"failed"}"""), "/devices/0"),
            (One($$"""{"family":"beada","deviceId":"{{new string('x', 1025)}}","deviceLocation":"","id":null,"outcome":"failed"}"""), "/devices/0/deviceId"),
            (One($$"""{"family":"beada","deviceId":"","deviceLocation":"","id":null,"outcome":"failed","reason":"{{new string('x', 257)}}"}"""), "/devices/0/reason"),
            ("null", "/devices"),
            (tooMany, "/devices"),
        })
        {
            var problem = await Expect(await client.PutAsync("/api/import/v1", Json(Request(devices)), Ct), HttpStatusCode.UnprocessableEntity);
            Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("code").GetString() == "schema" && e.GetProperty("path").GetString() == path);
        }

        Assert.Equal("pending", (await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK)).GetProperty("state").GetString());
        Assert.False(File.Exists(Path.Combine(engine.DataDir, "import-v1.json")));
    }

    [Fact]
    public async Task AStoredRecordWhoseDevicesBreakTheirSchema_CantBeRead_AndCountsAsDone()
    {
        using var engine = new V1FolderEngineFactory();
        using var client = engine.CreateApiClient();
        await Expect(await client.GetAsync("/api/session", Ct), HttpStatusCode.OK);
        File.WriteAllText(Path.Combine(engine.DataDir, "import-v1.json"),
            $$$"""{"completed":"2026-11-08T03:30:00Z","folder":{{{JsonSerializer.Serialize(engine.V1Dir)}}},"profiles":[],"settings":"skipped","devices":[{"family":"beada","deviceId":"","deviceLocation":"","id":"not-an-id","outcome":"imported"}]}""");

        var state = await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK);
        Assert.Equal("done", state.GetProperty("state").GetString());
        Assert.False(state.TryGetProperty("record", out _));
        Assert.Contains(state.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("code").GetString() == "import.record");
    }

    [Fact]
    public async Task AStoredRecordWhoseDevicesAreNull_CantBeRead_AndCountsAsDone()
    {
        using var engine = new V1FolderEngineFactory();
        using var client = engine.CreateApiClient();
        await Expect(await client.GetAsync("/api/session", Ct), HttpStatusCode.OK);
        File.WriteAllText(Path.Combine(engine.DataDir, "import-v1.json"),
            $$$"""{"completed":"2026-11-08T03:30:00Z","folder":{{{JsonSerializer.Serialize(engine.V1Dir)}}},"profiles":[],"settings":"skipped","devices":null}""");

        // A stored null breaks the record's schema: it isn't read as a record without devices.
        var state = await Expect(await client.GetAsync("/api/import/v1", Ct), HttpStatusCode.OK);
        Assert.Equal("done", state.GetProperty("state").GetString());
        Assert.False(state.TryGetProperty("record", out _));
        Assert.Contains(state.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("code").GetString() == "import.record");
    }
}
