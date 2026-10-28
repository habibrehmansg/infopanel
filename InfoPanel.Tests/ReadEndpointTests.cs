using System.Net;
using System.Text.Json;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests;

public class ReadEndpointTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<JsonElement> GetJson(string path)
    {
        using var client = factory.CreateApiClient();
        using var response = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private async Task<string> FirstProfileId()
    {
        var profiles = await GetJson("/api/profiles");
        return profiles[0].GetProperty("id").GetString()!;
    }

    /// <summary>True when <paramref name="body"/> is a valid <paramref name="definition"/> of the API schema.</summary>
    internal static bool Valid(JsonElement body, string definition)
    {
        var result = SpecSchemas.Strict.Validate(body, SpecSchemas.Api + "#/$defs/" + definition);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => $"{e.InstancePath} {e.Keyword} {e.Message}")));
        return true;
    }

    [Fact]
    public async Task Session()
    {
        var session = await GetJson("/api/session");

        Assert.True(Valid(session, "session"));
        Assert.False(string.IsNullOrEmpty(session.GetProperty("engineVersion").GetString()));
        Assert.Equal(2, session.GetProperty("protocol").GetInt32());
        Assert.Matches("^[0-9a-f]{32}$", session.GetProperty("instance").GetString());
        Assert.Equal(factory.DataDir, session.GetProperty("dataDir").GetString());
        Assert.Contains(session.GetProperty("platform").GetString(), new[] { "macos", "windows", "linux", "other" });

        // What the engine reads: format majors 1, no optional capabilities (no media providers), the limits.
        var support = session.GetProperty("formatSupport");
        Assert.Equal("""["infopanel.document/1"]""", support.GetProperty("document").GetProperty("formats").GetRawText());
        Assert.Equal("""["infopanel.scene/1"]""", support.GetProperty("scene").GetProperty("formats").GetRawText());
        Assert.Equal(0, support.GetProperty("scene").GetProperty("capabilities").GetArrayLength());
        Assert.Equal(SpecLimits.Default.MediaBytes, support.GetProperty("limits").GetProperty("mediaBytes").GetInt32());
    }

    [Fact]
    public async Task Session_ReportsEveryLimitByItsName()
    {
        var limits = (await GetJson("/api/session")).GetProperty("formatSupport").GetProperty("limits")
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());

        Assert.Equal(SpecLimits.Default.ToDictionary().OrderBy(p => p.Key, StringComparer.Ordinal), limits.OrderBy(p => p.Key, StringComparer.Ordinal));
        // The request bodies' limits, compose depth and item depth (once "groupDepth") among them.
        Assert.Equal((16.0, 8.0, 65_536.0, 4_096.0), (limits["composeDepth"], limits["itemDepth"], limits["requestBytes"], limits["tokenRequestBytes"]));
        Assert.DoesNotContain("groupDepth", limits.Keys);
    }

    [Theory]
    [InlineData("/api/kinds")]
    [InlineData("/api/import")]
    [InlineData("/api/profiles/00000000-0000-4000-8000-000000000001/export")]
    public async Task TheStep1Endpoints_AreGone(string path)
    {
        using var client = factory.CreateApiClient();
        using var response = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Fonts_StartWithRegisteredFamilies()
    {
        var fonts = (await GetJson("/api/fonts")).EnumerateArray().Select(f => f.GetString()!).ToList();

        Assert.Equal("Inter", fonts[0]);
        Assert.Equal(fonts.Count, fonts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Sensors_ListMockSensorsByKey()
    {
        var sensors = (await GetJson("/api/sensors")).EnumerateArray().ToList();

        Assert.Equal(37, sensors.Count);
        var gpuTemp = sensors.Single(s => s.GetProperty("key").GetString() == "plugin:/infopanel.mock/gpu/gpu-temp");
        Assert.Equal("infopanel.mock", gpuTemp.GetProperty("source").GetString());
        Assert.Equal("GPU [#0]: NVIDIA GeForce RTX 4090", gpuTemp.GetProperty("group").GetString());
        Assert.Equal("GPU Temperature", gpuTemp.GetProperty("name").GetString());
        Assert.Equal("°C", gpuTemp.GetProperty("unit").GetString());
        Assert.Equal("live", gpuTemp.GetProperty("status").GetString());
        Assert.All(sensors, s => Assert.Equal("live", s.GetProperty("status").GetString()));
        Assert.Equal(["key", "source", "group", "name", "unit", "status"], gpuTemp.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task SensorValues_WithHistory()
    {
        var values = (await GetJson("/api/sensors/values?history=60")).EnumerateArray().ToList();

        Assert.Equal(37, values.Count);
        var cpuLoad = values.Single(v => v.GetProperty("key").GetString() == "plugin:/infopanel.mock/cpu/cpu-load");
        var now = cpuLoad.GetProperty("now").GetDouble();
        Assert.InRange(now, 0, 100);
        Assert.True(cpuLoad.GetProperty("min").GetDouble() <= cpuLoad.GetProperty("avg").GetDouble());
        Assert.True(cpuLoad.GetProperty("avg").GetDouble() <= cpuLoad.GetProperty("max").GetDouble());
        Assert.Equal("%", cpuLoad.GetProperty("unit").GetString());
        Assert.False(cpuLoad.TryGetProperty("text", out _));
        Assert.Equal(60, cpuLoad.GetProperty("history").GetArrayLength());
        Assert.Equal("live", cpuLoad.GetProperty("status").GetString());

        var table = values.Single(v => v.GetProperty("key").GetString() == "plugin:/infopanel.mock/system/top-processes");
        Assert.False(string.IsNullOrEmpty(table.GetProperty("text").GetString()));
        Assert.False(table.TryGetProperty("history", out _));

        var text = values.Single(v => v.GetProperty("key").GetString() == "plugin:/infopanel.mock/system/now-playing");
        Assert.False(string.IsNullOrEmpty(text.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task SensorValues_WithoutHistory_OmitsIt()
    {
        var values = (await GetJson("/api/sensors/values")).EnumerateArray().ToList();

        Assert.All(values, v => Assert.False(v.TryGetProperty("history", out _)));
    }

    [Fact]
    public async Task Profiles_ListsTheExampleProfile()
    {
        var profiles = await GetJson("/api/profiles");

        var profile = Assert.Single(profiles.EnumerateArray());
        Assert.True(Valid(profile, "profileEntry"));
        Assert.Equal("""{"id":"3682945e-c157-4ed7-a6b3-60e651328cf5","status":"ok","name":"Wide bar","width":1920,"height":480,"rev":1,"dirty":false}""",
            profile.GetRawText());
    }

    [Fact]
    public async Task Profile_IsTheDocumentWithItsState()
    {
        var id = await FirstProfileId();
        var response = await GetJson($"/api/profiles/{id}");
        var session = await GetJson("/api/session");

        Assert.True(Valid(response, "documentResponse"));
        Assert.Equal(session.GetProperty("instance").GetString(), response.GetProperty("instance").GetString());
        Assert.Equal((1, false, false, false), (response.GetProperty("rev").GetInt64(), response.GetProperty("dirty").GetBoolean(),
            response.GetProperty("canUndo").GetBoolean(), response.GetProperty("canRedo").GetBoolean()));

        var document = response.GetProperty("document");
        Assert.Equal(id, document.GetProperty("id").GetString());
        Assert.Equal(1, document.GetProperty("rev").GetInt64());
        Assert.Equal(1, document.GetProperty("profile").GetProperty("rev").GetInt64());
        Assert.Equal("Wide bar", document.GetProperty("profile").GetProperty("name").GetString());
        var items = Flatten(document.GetProperty("items")).ToList();
        Assert.Equal(56, items.Count);
        Assert.All(items, item => Assert.Equal(1, item.GetProperty("rev").GetInt64()));
        Assert.Contains(items, item => item.TryGetProperty("bindings", out var b) && b.ToString().Contains("plugin:/infopanel.mock/gpu/gpu-load"));
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement nodes)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            yield return node;
            if (node.TryGetProperty("children", out var children))
            {
                foreach (var child in Flatten(children))
                    yield return child;
            }
        }
    }

    [Theory]
    [InlineData("/api/profiles/00000000-0000-0000-0000-000000000001")]
    [InlineData("/api/profiles/not-a-guid")]
    [InlineData("/api/profiles/3682945E-C157-4ED7-A6B3-60E651328CF5")]
    [InlineData("/api/profiles/00000000-0000-0000-0000-000000000001/thumbnail")]
    [InlineData("/api/profiles/00000000-0000-0000-0000-000000000001/document")]
    public async Task UnknownProfile_Is404ProblemJson(string path)
    {
        using var client = factory.CreateApiClient();
        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(320, 320, 80)]
    [InlineData(100, 100, 25)]
    [InlineData(1, 16, 4)]
    [InlineData(100000, 4096, 1024)]
    public async Task Thumbnail_IsAPngOfTheRequestedWidth(int requested, int width, int height)
    {
        var id = await FirstProfileId();
        using var client = factory.CreateApiClient();
        using var response = await client.GetAsync($"/api/profiles/{id}/thumbnail?w={requested}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);

        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);
    }

    [Fact]
    public async Task Thumbnail_DefaultsTo320()
    {
        var id = await FirstProfileId();
        using var client = factory.CreateApiClient();
        var bytes = await client.GetByteArrayAsync($"/api/profiles/{id}/thumbnail", Ct);

        using var bitmap = SKBitmap.Decode(bytes);
        Assert.Equal(320, bitmap.Width);
    }

    [Fact]
    public async Task Thumbnail_WithInvalidWidth_Is400ProblemJson()
    {
        var id = await FirstProfileId();
        using var client = factory.CreateApiClient();
        using var response = await client.GetAsync($"/api/profiles/{id}/thumbnail?w=wide", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
