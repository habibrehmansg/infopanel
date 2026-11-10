using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Tests.Import;
using Xunit;
using static InfoPanel.Tests.TestDocs;

namespace InfoPanel.Tests;

/// <summary>
/// The engine's own sources' keys over HTTP (spec/docs/bindings.md, Keys): listed and read as
/// <c>infopanel:/{source}/{path}</c>, never in the plugin form a plugin's keys have; a create, a commit or an archive's
/// document carrying a built-in key in the plugin form is refused with <c>key.pluginForm</c> at the key; and the 1.x
/// import binds the engine's readings in its own form.
/// </summary>
public class BuiltInKeyFormTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private const string PluginForm = "plugin:/infopanel.mock/cpu/cpu-load";
    private const string OwnForm = "infopanel:/mock/cpu/cpu-load";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Expect(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"expected {(int)status}, got {(int)response.StatusCode}: {text}");
        return text.Length > 0 && response.Content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.Ordinal) == true
            ? JsonDocument.Parse(text).RootElement.Clone()
            : default;
    }

    /// <summary>The one error of a refused body: <c>key.pluginForm</c> at <paramref name="path"/>.</summary>
    private static void AssertPluginForm(JsonElement problem, string path) =>
        Assert.Equal([("key.pluginForm", path)],
            problem.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("code").GetString(), e.GetProperty("path").GetString())));

    private static JsonObject Bound(string id, string key) =>
        Scene(id, 0, 0, InputScene(), new JsonObject { ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = key } } });

    [Fact]
    public async Task TheBuiltInSourcesSensors_AreListedAndRead_InTheEnginesOwnForm()
    {
        using var client = factory.CreateApiClient();

        var sensors = (await Expect(await client.GetAsync("/api/sensors", Ct), HttpStatusCode.OK)).EnumerateArray().ToList();
        var builtIn = sensors.Where(s => s.GetProperty("source").GetString()!.StartsWith("infopanel.", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(builtIn);
        Assert.All(builtIn, s => Assert.StartsWith("infopanel:/" + s.GetProperty("source").GetString()!["infopanel.".Length..] + "/",
            s.GetProperty("key").GetString(), StringComparison.Ordinal));
        Assert.DoesNotContain(sensors, s => s.GetProperty("key").GetString()!.StartsWith("plugin:/infopanel.", StringComparison.OrdinalIgnoreCase));

        // The engine's own usage is read from its source's rounds (its CPU from the second), so a fresh engine may not
        // answer it yet (ReadEndpointTests.MockOnly): every other listed built-in key is answered, and every answered one
        // is listed.
        HashSet<string> usage = [.. InfoPanel.Sensors.SystemInfo.EngineSource.Entries.Select(e => InfoPanel.Sensors.SystemInfo.EngineSource.KeyOf(e.Id).ToString())];
        var listed = builtIn.Select(s => s.GetProperty("key").GetString()!).ToList();
        var values = (await Expect(await client.GetAsync("/api/sensors/values", Ct), HttpStatusCode.OK)).EnumerateArray()
            .Select(v => v.GetProperty("key").GetString()!).ToList();
        var answered = values.Where(k => k.StartsWith("infopanel:/", StringComparison.Ordinal)).ToList();
        Assert.Equal(listed.Where(k => !usage.Contains(k)).Order(StringComparer.Ordinal),
            answered.Where(k => !usage.Contains(k)).Order(StringComparer.Ordinal));
        Assert.All(answered, k => Assert.Contains(k, listed));
        Assert.DoesNotContain(values, k => k.StartsWith("plugin:/infopanel.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ACreate_WithABuiltInKeyInThePluginForm_Is422_AtTheKey()
    {
        using var client = factory.CreateApiClient();

        var refused = await Expect(await client.PostAsync("/api/profiles",
            Json(new JsonObject { ["id"] = NewId(), ["document"] = Doc("Old keys", 200, 100, Scene("plain"), Bound("old", PluginForm)) }), Ct),
            HttpStatusCode.UnprocessableEntity);
        AssertPluginForm(refused, "/document/items/1/bindings/v/key");

        await Expect(await client.PostAsync("/api/profiles",
            Json(new JsonObject { ["id"] = NewId(), ["document"] = Doc("Own keys", 200, 100, Scene("plain"), Bound("own", OwnForm)) }), Ct),
            HttpStatusCode.Created);
    }

    [Fact]
    public async Task ACommit_SettingOrInsertingABuiltInKeyInThePluginForm_Is422_AtTheKey()
    {
        using var client = factory.CreateApiClient();
        var id = NewId();
        await Expect(await client.PostAsync("/api/profiles", Json(new JsonObject { ["id"] = id, ["document"] = Doc("Commits", 200, 100, Bound("a", OwnForm)) }), Ct),
            HttpStatusCode.Created);
        var instance = (await Expect(await client.GetAsync("/api/profiles", Ct), HttpStatusCode.OK)).EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == id).GetProperty("instance").GetString()!;

        AssertPluginForm(await Expect(await client.PostAsync($"/api/profiles/{id}/commit", Json(CommitBody(instance, 1, Bind("a", "v", PluginForm))), Ct),
            HttpStatusCode.UnprocessableEntity), "/ops/0/bindings/v/key");
        AssertPluginForm(await Expect(await client.PostAsync($"/api/profiles/{id}/commit",
            Json(CommitBody(instance, 1, Insert(Group("g", Bound("b", PluginForm))))), Ct), HttpStatusCode.UnprocessableEntity), "/ops/0/item/children/0/bindings/v/key");

        await Expect(await client.PostAsync($"/api/profiles/{id}/commit", Json(CommitBody(instance, 1, Bind("a", "v", "infopanel:/mock/gpu/gpu-load"))), Ct), HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnArchiveWhoseDocumentHasABuiltInKeyInThePluginForm_Is422_AtTheKey()
    {
        using var client = factory.CreateApiClient();
        var manifest = """{"format":"infopanel.archive/1","profiles":["document.json"],"requires":[]}"""u8.ToArray();
        var document = Encoding.UTF8.GetBytes(Doc("Old keys", 200, 100, Bound("old", PluginForm)).ToJsonString());

        AssertPluginForm(await Expect(await client.PostAsync("/api/import/archive", V1Fixture.Bytes(V1Fixture.Zip(("manifest.json", manifest), ("document.json", document))), Ct),
            HttpStatusCode.UnprocessableEntity), "/document/items/0/bindings/v/key");
    }

    [Fact]
    public async Task The1xImport_BindsTheEnginesReadings_InItsOwnForm_ItsPropsAs1xHasThem()
    {
        using var client = factory.CreateApiClient();

        // The 1.x export with all 14 kinds: its plugin bindings name the mock source's readings.
        var envelope = await Expect(await client.PostAsync("/api/import/v1/archive", V1Fixture.Bytes(File.ReadAllBytes(V1Fixture.AllKindsArchive)), Ct), HttpStatusCode.OK);
        var bound = new List<(string Id, string Key)>();
        void Walk(JsonElement items)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("props", out var props) && props.TryGetProperty("sensorType", out var type) && type.GetString() == "Plugin"
                    && item.GetProperty("meta").TryGetProperty("sensor", out var sensor))
                    bound.Add((props.GetProperty("pluginSensorId").GetString()!, sensor.GetProperty("key").GetString()!));
                if (item.TryGetProperty("children", out var children)) Walk(children);
                if (item.TryGetProperty("images", out var images)) Walk(images);
            }
        }
        Walk(envelope.GetProperty("items"));
        Assert.Contains(("/infopanel.mock/gpu/gpu-temp", "infopanel:/mock/gpu/gpu-temp"), bound);
        Assert.All(bound.Where(b => b.Id.StartsWith("/infopanel.", StringComparison.Ordinal)), b => Assert.Equal("infopanel:/" + b.Id["/infopanel.".Length..], b.Key));
        Assert.DoesNotContain("plugin:/infopanel.", envelope.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // A 1.x Clock plugin's reading, bound to the clock source on import.
        var clock = await Expect(await client.PostAsync("/api/import/v1/archive", V1Fixture.Bytes(V1Fixture.V1Archive(V1Fixture.Items(
            """<DisplayItem xsi:type="SensorDisplayItem"><Name>Second</Name><SensorName>Second</SensorName><SensorType>Plugin</SensorType><PluginSensorId>/clock-plugin/clock/second</PluginSensorId><Id>0</Id><Instance>0</Instance><EntryId>0</EntryId></DisplayItem>"""))), Ct),
            HttpStatusCode.OK);
        var item = Assert.Single(clock.GetProperty("items").EnumerateArray());
        Assert.Equal("/infopanel.clock/second", item.GetProperty("props").GetProperty("pluginSensorId").GetString());
        Assert.Equal("infopanel:/clock/second", item.GetProperty("meta").GetProperty("sensor").GetProperty("key").GetString());
    }
}
