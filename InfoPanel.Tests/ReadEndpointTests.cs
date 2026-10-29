using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Api;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using InfoPanel.Tests.Contract;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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

        // What the engine reads, by format string: majors 1 at 1.0, no optional capabilities (no media providers), and
        // each format's own limits. No optional API features.
        var support = session.GetProperty("formatSupport");
        Assert.Equal(new[] { "infopanel.document/1", "infopanel.scene/1" }, support.EnumerateObject().Select(p => p.Name));
        foreach (var format in support.EnumerateObject())
        {
            Assert.Equal("1.0", format.Value.GetProperty("version").GetString());
            Assert.Equal(0, format.Value.GetProperty("capabilities").GetArrayLength());
        }
        Assert.Equal(SpecLimits.Default.Nodes, support.GetProperty("infopanel.scene/1").GetProperty("limits").GetProperty("nodes").GetInt32());
        Assert.Equal(SpecLimits.Default.Items, support.GetProperty("infopanel.document/1").GetProperty("limits").GetProperty("items").GetInt32());
        Assert.Equal("[]", session.GetProperty("features").GetRawText());

        // The PC's regional format as the engine read it at start-up (every test engine's is en-US).
        Assert.Equal("""{"tag":"en-US","decimalSeparator":".","groupSeparator":","}""", session.GetProperty("locale").GetRawText());
    }

    [Fact]
    public async Task Session_ReportsTheRegionalFormatTheEngineRead()
    {
        // A custom number format (as a macOS number format or a Windows Region override gives) is reported as read, not
        // rebuilt from its tag: the separators a profile without a locale draws with.
        var custom = RegionalFormat.Create(System.Globalization.CultureInfo.GetCultureInfo("de-CH"), ";", " ");
        using var fresh = new EngineFactory();
        using var engine = fresh.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(custom)));
        using var client = engine.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        using var response = await client.GetAsync("/api/session", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

        Assert.True(Valid(session, "session"));
        var locale = session.GetProperty("locale");
        Assert.Equal(("de-CH", ";", " "), (locale.GetProperty("tag").GetString(), locale.GetProperty("decimalSeparator").GetString(),
            locale.GetProperty("groupSeparator").GetString()));
    }

    /// <summary>The limits.md sections whose limits are the scene format's; every other section's are the document format's.</summary>
    private static readonly string[] SceneSections = ["Scene limits", "Media limits", "Run-time limits"];

    /// <summary>Each limit spec/docs/limits.md lists in a table headed <c>| Limit |</c>, with the section it is under.</summary>
    private static Dictionary<string, string> DocumentedLimitSections()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = "";
        var inTable = false;
        foreach (var line in File.ReadAllLines(Path.Combine(ApiContract.FindSpecDir(), "docs", "limits.md")))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal)) section = line[3..].Trim();
            if (!line.StartsWith('|')) { inTable = false; continue; }
            var first = line.Trim().Trim('|').Split('|')[0].Trim();
            if (first == "Limit") { inTable = true; continue; }
            if (!inTable || first.StartsWith("---", StringComparison.Ordinal)) continue;
            result.Add(first.Trim('`'), section);
        }
        return result;
    }

    [Fact]
    public async Task Session_ReportsEachLimitOnce_UnderItsFormat()
    {
        var support = (await GetJson("/api/session")).GetProperty("formatSupport");
        var all = SpecLimits.Default.ToDictionary();
        var reported = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var format in support.EnumerateObject())
            foreach (var limit in format.Value.GetProperty("limits").EnumerateObject())
            {
                Assert.True(reported.TryAdd(limit.Name, format.Name), $"{limit.Name} is under {reported.GetValueOrDefault(limit.Name)} and {format.Name}");
                Assert.Equal(all[limit.Name], limit.Value.GetDouble());
            }

        // limits.md's scene, media and run-time limits are the scene format's, every other limit the document format's:
        // the request bodies', the socket's, the archives' and the 1.x import's among them.
        var expected = DocumentedLimitSections().ToDictionary(p => p.Key,
            p => SceneSections.Any(s => p.Value.StartsWith(s, StringComparison.Ordinal)) ? SpecSchemas.SceneFormat : SpecSchemas.DocumentFormat);
        Assert.Equal(all.Keys.Order(StringComparer.Ordinal), expected.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal), reported.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal((SpecSchemas.SceneFormat, SpecSchemas.SceneFormat, SpecSchemas.DocumentFormat, SpecSchemas.DocumentFormat),
            (reported["composeDepth"], reported["textLength"], reported["itemDepth"], reported["requestBytes"]));
        Assert.DoesNotContain("groupDepth", reported.Keys);
    }

    [Fact]
    public async Task FormatSupport_IsItsExample()
    {
        // spec/examples/api/formatSupport/engine.json is what this engine reports, every limit included.
        var example = JsonNode.Parse(File.ReadAllText(Path.Combine(ApiContract.FindSpecDir(), "examples", "api", "formatSupport", "engine.json")));
        var support = JsonNode.Parse((await GetJson("/api/session")).GetProperty("formatSupport").GetRawText());
        Assert.True(JsonNode.DeepEquals(example, support), $"formatSupport/engine.json:\n{example?.ToJsonString()}\nthe engine's:\n{support?.ToJsonString()}");
    }

    [Fact]
    public void FormatSupport_GivesEachFormatItsOwnCapabilitiesAndLimits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "infopanel-support-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var workspace = Workspace.Start(new DataPaths(dir), new WorkspaceOptions
            {
                HistoryBudget = new HistoryBudget(HistoryBudget.DefaultMaxBytes),
                Support = new EngineSupport(new HashSet<string> { "media.provider" }, new HashSet<string> { "profile.test" }),
                Limits = SpecLimits.Default.With(new Dictionary<string, double> { ["nodes"] = 8, ["items"] = 9 }),
            }, DateTimeOffset.UnixEpoch);

            var support = JsonSerializer.SerializeToElement(ReadEndpoints.FormatSupportOf(workspace), SpecJson.Options);
            var scene = support.GetProperty(SpecSchemas.SceneFormat);
            var document = support.GetProperty(SpecSchemas.DocumentFormat);
            Assert.Equal("""["media.provider"]""", scene.GetProperty("capabilities").GetRawText());
            Assert.Equal("""["profile.test"]""", document.GetProperty("capabilities").GetRawText());
            Assert.Equal(8, scene.GetProperty("limits").GetProperty("nodes").GetInt32());
            Assert.Equal(9, document.GetProperty("limits").GetProperty("items").GetInt32());
            Assert.False(scene.GetProperty("limits").TryGetProperty("items", out _));
            Assert.False(document.GetProperty("limits").TryGetProperty("nodes", out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
        // Its tag is its document's: new each run, so read from the entry and held to the document's answer.
        var tag = profile.GetProperty("instance").GetString()!;
        Assert.Equal($$"""{"id":"3682945e-c157-4ed7-a6b3-60e651328cf5","status":"ok","instance":"{{tag}}","name":"Wide bar","width":1920,"height":480,"rev":1,"dirty":false}""",
            profile.GetRawText());
        Assert.Equal(tag, (await GetJson("/api/profiles/3682945e-c157-4ed7-a6b3-60e651328cf5")).GetProperty("instance").GetString());
    }

    [Fact]
    public async Task Profile_IsTheDocumentWithItsState()
    {
        var id = await FirstProfileId();
        var response = await GetJson($"/api/profiles/{id}");
        var session = await GetJson("/api/session");

        Assert.True(Valid(response, "documentResponse"));
        // The document's tag, not the run's instance.
        Assert.Matches("^[0-9a-f]{32}$", response.GetProperty("instance").GetString());
        Assert.NotEqual(session.GetProperty("instance").GetString(), response.GetProperty("instance").GetString());
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
