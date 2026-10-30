using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
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

        // What the engine reads, by format string: majors 1 at 1.0, the scene format's media.provider (the engine has
        // media providers) and no other optional capability, and each format's own limits. No optional API features.
        var support = session.GetProperty("formatSupport");
        Assert.Equal(new[] { "infopanel.archive/1", "infopanel.document/1", "infopanel.scene/1" }, support.EnumerateObject().Select(p => p.Name));
        foreach (var format in support.EnumerateObject())
        {
            Assert.Equal("1.0", format.Value.GetProperty("version").GetString());
            Assert.Equal(format.Name == "infopanel.scene/1" ? """["media.provider"]""" : "[]", format.Value.GetProperty("capabilities").GetRawText());
        }

        // The media providers the engine has, by name: web images.
        Assert.Equal("""{"providers":["http"]}""", session.GetProperty("media").GetRawText());
        Assert.Equal(SpecLimits.Default.Nodes, support.GetProperty("infopanel.scene/1").GetProperty("limits").GetProperty("nodes").GetInt32());
        Assert.Equal(SpecLimits.Default.Items, support.GetProperty("infopanel.document/1").GetProperty("limits").GetProperty("items").GetInt32());
        Assert.Equal("[]", session.GetProperty("features").GetRawText());

        // The PC's regional format as the engine read it at start-up (every test engine's is en-US).
        Assert.Equal("""{"tag":"en-US","decimalSeparator":".","groupSeparator":","}""", session.GetProperty("locale").GetRawText());

        // What a bug report needs: the engine's build (the commit it was built from), the operating system's version, the
        // architecture the engine runs as, and when this run started.
        var build = session.GetProperty("engineBuild").GetString();
        Assert.Matches("^(unknown|[0-9a-f]{7,64}(-dirty)?)$", build);
        Assert.Equal(ReadEndpoints.EngineBuild(), build);
        var osVersion = session.GetProperty("osVersion").GetString();
        Assert.Equal(ReadEndpoints.OsVersion(), osVersion);
        Assert.NotEqual("unknown", osVersion);
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        Assert.Equal(arch is "x64" or "arm64" or "x86" or "arm" ? arch : "other", session.GetProperty("arch").GetString());
        Assert.Matches(TimestampPattern, session.GetProperty("started").GetString());
    }

    /// <summary>The schema's <c>timestamp</c>: RFC 3339 in UTC, to the second.</summary>
    private const string TimestampPattern = "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$";

    private static DateTimeOffset ParseTimestamp(string? text) =>
        DateTimeOffset.ParseExact(text!, "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    [Fact]
    public async Task Session_ReportsWhenThisRunStarted()
    {
        var before = DateTimeOffset.UtcNow;
        using var fresh = new EngineFactory();
        using var client = fresh.CreateApiClient();
        using var first = await client.GetAsync("/api/session", Ct);
        var session = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
        var after = DateTimeOffset.UtcNow;

        Assert.True(Valid(session, "session"));
        var started = session.GetProperty("started").GetString();
        Assert.Matches(TimestampPattern, started);
        Assert.InRange(ParseTimestamp(started), before.AddTicks(-(before.Ticks % TimeSpan.TicksPerSecond)), after);

        // The run's, kept for the run: asked again, the same.
        using var second = await client.GetAsync("/api/session", Ct);
        var again = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(started, again.GetProperty("started").GetString());
        Assert.Equal(session.GetProperty("instance").GetString(), again.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task Session_ReportsTheRunsStart_InUtc_ToTheSecond()
    {
        // A run that began at 11:04:05.678 in UTC+8 reports 03:04:05Z, however much later it is asked: the run's start,
        // not the time of the request, in UTC and cut to the second.
        var runtime = new InfoPanel.Hosting.EngineRuntime(EngineFactory.Port, EngineFactory.Token, [EngineFactory.DevOrigin], [EngineFactory.CrossOrigin])
        {
            Started = new DateTimeOffset(2026, 1, 2, 11, 4, 5, 678, TimeSpan.FromHours(8)),
        };
        using var fresh = new EngineFactory();
        using var engine = fresh.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(runtime)));
        using var client = engine.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.GetAsync("/api/session", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
            Assert.True(Valid(session, "session"));
            Assert.Equal("2026-01-02T03:04:05Z", session.GetProperty("started").GetString());
        }
    }

    [Theory]
    [InlineData("004cf52")]
    [InlineData("004cf52-dirty")]
    [InlineData("004cf521a45f")]
    [InlineData("004cf521a45fc7c18c426ed6163ac6b7f73ba593")]
    [InlineData("004cf521a45fc7c18c426ed6163ac6b7f73ba593-dirty")]
    public void TheBuild_IsTheCommitTheEngineWasBuiltFrom(string recorded) => Assert.Equal(recorded, ReadEndpoints.BuildOf(recorded));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("004CF52")]
    [InlineData("004cf5")]
    [InlineData("004cf52-modified")]
    [InlineData("004cf52-dirty-dirty")]
    [InlineData(" 004cf52")]
    [InlineData("004cf52\n")]
    [InlineData("warning: ignoring;004cf52")]
    [InlineData("004cf521a45fc7c18c426ed6163ac6b7f73ba593004cf521a45fc7c18c426ed6163ac6b7f7")]
    public void TheBuild_IsUnknown_WhenNoCommitWasRecorded(string? recorded) =>
        Assert.Equal("unknown", ReadEndpoints.BuildOf(recorded));

    [Fact]
    public void TheBuild_NamesTheCommitTheSdkRecorded()
    {
        // The SDK reads the repository itself (without git) and writes the commit it built from after a '+' in the
        // informational version; the engine's build asks git. When both know the commit, they name the same one. Either
        // may not know it (no repository; the engine's also without git, or when git fails): EngineBuildTargetTests
        // holds the build to unknown then, whatever this machine has.
        var informational = typeof(ReadEndpoints).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var plus = informational.IndexOf('+');
        var build = ReadEndpoints.EngineBuild();
        Assert.Matches("^(unknown|[0-9a-f]{7,64}(-dirty)?)$", build);
        if (plus < 0 || build == "unknown")
            return;
        Assert.StartsWith(build.Split('-')[0], informational[(plus + 1)..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("macos", "27.2.0", null, "27.2")]
    [InlineData("macos", "26.1", null, "26.1")]
    [InlineData("macos", "15.6.1", null, "15.6.1")]
    [InlineData("windows", "10.0.26100.0", null, "10.0.26100")]
    [InlineData("windows", "10.0.26100", null, "10.0.26100")]
    [InlineData("linux", "6.8.0.45", "6.8.0-45-generic\n", "6.8.0-45-generic")]
    [InlineData("linux", "6.8.0", null, "6.8.0")]
    [InlineData("linux", "6.8.0", "", "6.8.0")]
    [InlineData("linux", "6.8.0", "not a release", "6.8.0")]
    [InlineData("linux", "6.8.0", "6.8.0-0123456789012345678901234567890123456789012345678901234567890", "6.8.0")]
    [InlineData("other", "1.2.3", null, "1.2.3")]
    [InlineData("macos", "0.0", null, "unknown")]
    [InlineData("windows", "0.0.0.0", null, "unknown")]
    [InlineData("linux", "0.0", null, "unknown")]
    public void TheOsVersion_IsTheVersionTheOsReports(string platform, string version, string? linuxRelease, string expected) =>
        Assert.Equal(expected, ReadEndpoints.OsVersionOf(platform, Version.Parse(version), linuxRelease));

    /// <summary>An OS query that fails: <paramref name="kind"/> names what it throws.</summary>
    private static Version Fails(string kind) => throw (kind switch
    {
        "platform" => new PlatformNotSupportedException("no version"),
        "win32" => new System.ComponentModel.Win32Exception(5),
        "io" => new IOException("no version"),
        _ => (Exception)new InvalidOperationException("no version"),
    });

    [Theory]
    [InlineData("macos", "invalid")]
    [InlineData("windows", "invalid")]
    [InlineData("windows", "win32")]
    [InlineData("windows", "platform")]
    [InlineData("linux", "io")]
    [InlineData("other", "invalid")]
    public void TheOsVersion_IsUnknown_WhenTheOsCantSay(string platform, string failure)
    {
        // Reading the OS's version can itself fail (Environment.OSVersion throws when the system won't say): the session
        // then reports unknown, on every platform, rather than failing.
        Assert.Equal("unknown", ReadEndpoints.ReadOsVersion(platform, () => Fails(failure), () => throw new IOException("no release")));
        Assert.Equal("unknown", ReadEndpoints.ReadOsVersion(platform, () => Fails(failure), () => null));
    }

    [Fact]
    public void TheOsVersion_OnLinux_KeepsWhicheverReadWorked()
    {
        // The kernel release when only the version failed; the version when only the release failed.
        Assert.Equal("6.8.0-45-generic", ReadEndpoints.ReadOsVersion("linux", () => Fails("invalid"), () => "6.8.0-45-generic\n"));
        Assert.Equal("6.8.0", ReadEndpoints.ReadOsVersion("linux", () => new Version(6, 8, 0), () => throw new UnauthorizedAccessException()));
        Assert.Equal("6.8.0", ReadEndpoints.ReadOsVersion("linux", () => new Version(6, 8, 0), () => throw new InvalidOperationException()));
    }

    [Fact]
    public void TheOsVersion_OnAMac_IsItsProductVersion()
    {
        // As the Mac reports it (sw_vers, About This Mac), not the Darwin kernel's version.
        if (!OperatingSystem.IsMacOS())
        {
            Assert.NotEqual("unknown", ReadEndpoints.OsVersion());
            return;
        }
        using var swVers = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/sw_vers", "-productVersion")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var product = swVers.StandardOutput.ReadToEnd().Trim();
        swVers.WaitForExit();
        Assert.Matches(@"^[0-9]+\.[0-9]+(\.[0-9]+)?$", product);
        Assert.Equal(product, ReadEndpoints.OsVersion());
    }

    [Theory]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.Arm64, "arm64")]
    [InlineData(Architecture.X86, "x86")]
    [InlineData(Architecture.Arm, "arm")]
    [InlineData(Architecture.Armv6, "other")]
    [InlineData(Architecture.RiscV64, "other")]
    [InlineData(Architecture.Wasm, "other")]
    [InlineData(Architecture.S390x, "other")]
    public void TheArch_IsTheEnginesArchitecture_OrOther(Architecture architecture, string expected) =>
        Assert.Equal(expected, ReadEndpoints.ArchOf(architecture));

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

    /// <summary>The limits.md sections whose limits are the scene format's, and the archive's; every other section's are the document format's.</summary>
    private static readonly string[] SceneSections = ["Scene limits", "Media limits", "Run-time limits"];

    private const string ArchiveSection = "Archive limits";

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

        // limits.md's scene, media and run-time limits are the scene format's, the archive limits the profile archive's,
        // every other limit the document format's: the request bodies', the socket's and the 1.x import's among them.
        var expected = DocumentedLimitSections().ToDictionary(p => p.Key,
            p => SceneSections.Any(s => p.Value.StartsWith(s, StringComparison.Ordinal)) ? SpecSchemas.SceneFormat
                : p.Value == ArchiveSection ? SpecSchemas.ArchiveFormat : SpecSchemas.DocumentFormat);
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
