using System.Net;
using System.Text.Json;
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

    [Fact]
    public async Task Session()
    {
        var session = await GetJson("/api/session");

        Assert.False(string.IsNullOrEmpty(session.GetProperty("engineVersion").GetString()));
        Assert.Equal(1, session.GetProperty("protocol").GetInt32());
        Assert.Equal(factory.DataDir, session.GetProperty("dataDir").GetString());
        Assert.Contains(session.GetProperty("platform").GetString(), new[] { "macos", "windows", "linux", "other" });
    }

    [Fact]
    public async Task Kinds_ListsTheFourteenKindsWithPropertiesAndDefaults()
    {
        var kinds = await GetJson("/api/kinds");

        var ids = kinds.EnumerateArray().Select(k => k.GetProperty("kind").GetString()).ToList();
        Assert.Equal(
            ["text", "sensor", "clock", "calendar", "bar", "graph", "donut", "shape", "group", "table", "image", "sensorImage", "httpImage", "gauge"],
            ids);

        foreach (var kind in kinds.EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(kind.GetProperty("clrType").GetString()));
            Assert.False(string.IsNullOrEmpty(kind.GetProperty("label").GetString()));
            var properties = kind.GetProperty("properties").EnumerateArray().ToList();
            Assert.Contains(properties, p => p.GetProperty("name").GetString() == "name");
            var defaults = kind.GetProperty("defaults");
            foreach (var property in properties)
            {
                var name = property.GetProperty("name").GetString()!;
                Assert.True(defaults.TryGetProperty(name, out _), $"{name} has no default");
                Assert.Contains(property.GetProperty("type").GetString(), new[] { "int", "uint", "float", "double", "bool", "string", "enum" });
                Assert.Equal(property.GetProperty("type").GetString() == "enum", property.TryGetProperty("enumValues", out _));
                _ = property.GetProperty("readOnly").GetBoolean();
            }
        }

        var sensor = kinds.EnumerateArray().Single(k => k.GetProperty("kind").GetString() == "sensor");
        var sensorType = sensor.GetProperty("properties").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "sensorType");
        Assert.Equal(["HwInfo", "Libre", "Plugin"], sensorType.GetProperty("enumValues").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task Fonts_StartWithRegisteredFamilies()
    {
        var fonts = (await GetJson("/api/fonts")).EnumerateArray().Select(f => f.GetString()!).ToList();

        Assert.Equal("Inter", fonts[0]);
        Assert.Equal(fonts.Count, fonts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Sensors_ListMockSensorsWithBindings()
    {
        var sensors = (await GetJson("/api/sensors")).EnumerateArray().ToList();

        Assert.Equal(37, sensors.Count);
        var gpuTemp = sensors.Single(s => s.GetProperty("key").GetString() == "plugin:/infopanel.mock/gpu/gpu-temp");
        Assert.Equal("infopanel.mock", gpuTemp.GetProperty("source").GetString());
        Assert.Equal("GPU [#0]: NVIDIA GeForce RTX 4090", gpuTemp.GetProperty("group").GetString());
        Assert.Equal("GPU Temperature", gpuTemp.GetProperty("name").GetString());
        Assert.Equal("°C", gpuTemp.GetProperty("unit").GetString());

        var binding = gpuTemp.GetProperty("binding");
        Assert.Equal("GPU Temperature", binding.GetProperty("sensorName").GetString());
        Assert.Equal("Plugin", binding.GetProperty("sensorType").GetString());
        Assert.Equal("/infopanel.mock/gpu/gpu-temp", binding.GetProperty("pluginSensorId").GetString());
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
    public async Task Profiles_ListsTheStarterProfile()
    {
        var profiles = (await GetJson("/api/profiles")).EnumerateArray().ToList();

        var profile = Assert.Single(profiles);
        Assert.True(Guid.TryParse(profile.GetProperty("id").GetString(), out _));
        Assert.Equal("Welcome", profile.GetProperty("name").GetString());
        Assert.Equal(800, profile.GetProperty("width").GetInt32());
        Assert.Equal(480, profile.GetProperty("height").GetInt32());
        Assert.Equal(1, profile.GetProperty("revision").GetInt64());
        Assert.False(profile.GetProperty("dirty").GetBoolean());
    }

    [Fact]
    public async Task Profile_IsAProfileDoc()
    {
        var id = await FirstProfileId();
        var doc = await GetJson($"/api/profiles/{id}");

        Assert.Equal(id, doc.GetProperty("id").GetString());
        Assert.Equal(1, doc.GetProperty("revision").GetInt64());
        Assert.False(doc.GetProperty("dirty").GetBoolean());
        Assert.False(doc.GetProperty("canUndo").GetBoolean());
        Assert.False(doc.GetProperty("canRedo").GetBoolean());

        var profile = doc.GetProperty("profile");
        Assert.Equal("Welcome", profile.GetProperty("name").GetString());
        Assert.Equal(800, profile.GetProperty("width").GetInt32());
        Assert.False(profile.TryGetProperty("guid", out _));

        var items = doc.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        foreach (var item in items)
        {
            Assert.True(Guid.TryParse(item.GetProperty("id").GetString(), out _));
            Assert.False(string.IsNullOrEmpty(item.GetProperty("kind").GetString()));
            Assert.Equal(JsonValueKind.Object, item.GetProperty("props").ValueKind);
            Assert.Equal(JsonValueKind.Object, item.GetProperty("meta").ValueKind);
        }

        var sensor = items.First(i => i.GetProperty("kind").GetString() == "sensor");
        var meta = sensor.GetProperty("meta").GetProperty("sensor");
        Assert.Equal("plugin:/infopanel.mock/cpu/cpu-temp", meta.GetProperty("key").GetString());
        Assert.Equal("live", meta.GetProperty("status").GetString());
        Assert.Equal("Plugin", sensor.GetProperty("props").GetProperty("sensorType").GetString());
    }

    [Theory]
    [InlineData("/api/profiles/00000000-0000-0000-0000-000000000001")]
    [InlineData("/api/profiles/not-a-guid")]
    [InlineData("/api/profiles/00000000-0000-0000-0000-000000000001/thumbnail")]
    public async Task UnknownProfile_Is404ProblemJson(string path)
    {
        using var client = factory.CreateApiClient();
        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(320, 320, 192)]
    [InlineData(100, 100, 60)]
    [InlineData(1, 16, 10)]
    [InlineData(100000, 4096, 2458)]
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
