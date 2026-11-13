using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.CrashReporting;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Sensors;
using InfoPanel.Tests.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sentry;
using Xunit;

namespace InfoPanel.Tests.CrashReports;

/// <summary>
/// Scrubbing (docs/2.0/crash-reports.md): a crafted event puts each personal value into every field an event has (the
/// message, the exception's value, its frames' file names, the breadcrumbs' message, data and category, the request,
/// tags, extra, contexts, the user, the server name), goes through the SDK as the engine starts it, and the envelope the
/// transport receives holds none of them, one test per value; the allowlisted fields come through. The names it knows are
/// the production collector's (<see cref="CrashReporting.CrashReports.EngineNames"/>), read from a test engine that has
/// the profile, the plugin, its sensor and the saved panel, never a list made by hand.
/// </summary>
[Collection(CrashReportsCollection.Name)]
public sealed class ScrubTests
{
    /// <summary>The values of §4.1, each distinct, and the label the known ones are given.</summary>
    public static readonly string[] Personal =
    [
        "Habibs-MacBook-Pro",
        "habib",
        "/Users/habib/Documents/Rig.infopanel",
        @"C:\Users\habib\AppData\Local\InfoPanel\v2",
        "192.168.1.23",
        "100.101.102.103",
        "habibs-mac.tail1234.ts.net",
        "fe80::1",
        "a4:83:e7:12:34:56",
        "CPU Package",
        "Gaming Rig",
        "InfoPanel.Spotify",
        "TURZX-3.5-SN123456",
        "owner@example.com",
        "acc_01HXYZ7Q2M8N4P6R9S3T5V",
        "Tq3vX9kLm2Np8Rs5Wy7Zb4Cd6Fg1Hj0Kl3Mn5Pq7Rs9u",
        @"C:\Users\habib\My Clients\Secret Budget.json",
        @"\\nas\Family Share\Tax Return.pdf",
        "~/Private Notes/plan.md",
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ",
        "Spotify Now Playing",
        "SN-QWERTY-ZXCV",
    ];

    /// <summary>This computer's names as the engine reads them (<see cref="CrashReporting.CrashReports.ThisComputer"/>), for a computer and user of the test's.</summary>
    private static IReadOnlyList<KnownValue> ThisComputer(DataPaths paths) =>
        CrashReporting.CrashReports.ThisComputer(paths, v1Folder: null, user: "habib", computer: "Habibs-MacBook-Pro", home: "/Users/habib");

    public static TheoryData<string> PersonalValues => new(Personal);

    private static readonly string All = string.Join(" ", Personal.Select(v => $"'{v}' {v}"));

    /// <summary>The crafted event: every value in every field, written as the SDK reads events.</summary>
    private static SentryEvent Crafted()
    {
        var values = new JsonArray([.. Personal.Select(v => (JsonNode)v)]);
        var everywhere = JsonValue.Create(All);
        var json = new JsonObject
        {
            ["event_id"] = "0123456789abcdef0123456789abcdef",
            ["level"] = "error",
            ["message"] = new JsonObject { ["message"] = All, ["formatted"] = All },
            ["logger"] = All,
            ["server_name"] = "Habibs-MacBook-Pro",
            ["transaction"] = All,
            ["fingerprint"] = new JsonArray(All),
            ["modules"] = new JsonObject { ["InfoPanel.Spotify"] = "1.0.0" },
            ["exception"] = new JsonObject
            {
                ["values"] = new JsonArray(new JsonObject
                {
                    ["type"] = "System.IO.FileNotFoundException",
                    ["value"] = All,
                    ["module"] = "System.Private.CoreLib",
                    ["stacktrace"] = new JsonObject
                    {
                        ["frames"] = new JsonArray(
                            new JsonObject { ["function"] = "Load", ["module"] = "InfoPanel.Documents.DocumentStore", ["filename"] = "/Users/habib/Documents/Rig.infopanel", ["abs_path"] = All, ["lineno"] = 42, ["in_app"] = true },
                            new JsonObject { ["function"] = "Read", ["module"] = "InfoPanel.Persistence.RegularFile", ["filename"] = @"C:\Users\habib\AppData\Local\InfoPanel\v2", ["abs_path"] = All, ["lineno"] = 7, ["in_app"] = true },
                            new JsonObject { ["function"] = "Open", ["module"] = "InfoPanel.Hosting", ["filename"] = "/_/InfoPanel/Hosting/StaticSite.cs", ["lineno"] = 9, ["colno"] = 3, ["in_app"] = true }),
                    },
                }),
            },
            ["breadcrumbs"] = new JsonArray(new JsonObject
            {
                ["timestamp"] = "2026-11-12T10:00:00Z",
                ["message"] = All,
                ["category"] = All,
                ["type"] = "log",
                ["level"] = "info",
                ["data"] = new JsonObject { ["exception_type"] = All, ["path"] = All },
            }),
            ["request"] = new JsonObject { ["url"] = "http://192.168.1.23:5199/?computer=habibs-mac.tail1234.ts.net#token=Tq3vX9kLm2Np8Rs5Wy7Zb4Cd6Fg1Hj0Kl3Mn5Pq7Rs9u", ["query_string"] = All, ["headers"] = new JsonObject { ["Host"] = All }, ["env"] = new JsonObject { ["REMOTE_ADDR"] = "192.168.1.23" } },
            ["tags"] = new JsonObject { ["computer"] = All, ["os"] = All, ["process"] = "engine" },
            ["extra"] = new JsonObject { ["values"] = values.DeepClone(), ["text"] = everywhere.DeepClone() },
            ["contexts"] = new JsonObject
            {
                ["os"] = new JsonObject { ["type"] = "os", ["name"] = "macOS", ["version"] = "26.2", ["raw_description"] = All, ["kernel_version"] = All },
                ["runtime"] = new JsonObject { ["type"] = "runtime", ["name"] = ".NET", ["version"] = "10.0.0", ["raw_description"] = All },
                ["device"] = new JsonObject { ["type"] = "device", ["arch"] = "Arm64", ["name"] = "Habibs-MacBook-Pro", ["model"] = All },
                ["app"] = new JsonObject { ["type"] = "app", ["app_version"] = "2.0.0", ["app_name"] = All, ["device_app_hash"] = All },
                ["custom"] = new JsonObject { ["type"] = "custom", ["text"] = All },
            },
            ["user"] = new JsonObject { ["id"] = "habib", ["username"] = "habib", ["email"] = "owner@example.com", ["ip_address"] = "100.101.102.103", ["data"] = new JsonObject { ["acc"] = "acc_01HXYZ7Q2M8N4P6R9S3T5V" } },
        };
        using var document = JsonDocument.Parse(json.ToJsonString());
        return SentryEvent.FromJson(document.RootElement);
    }

    /// <summary>
    /// A test engine holding the names: the profile <c>Gaming Rig</c>, the plugin package <c>InfoPanel.Spotify</c> (its
    /// plugin <c>Spotify Now Playing</c>, whose sensor is <c>CPU Package</c>), and a saved panel whose serial is
    /// <c>TURZX-3.5-SN123456</c> and whose 1.x device id is <c>SN-QWERTY-ZXCV</c>.
    /// </summary>
    internal static async Task<(EngineFactory Factory, WebApplicationFactory<Program> Engine, string Root)> EngineWithNamesAsync()
    {
        var root = PluginFixtures.NewTempRoot("ip-crash-");
        var factory = new EngineFactory();
        var bundled = Path.Combine(root, "bundled");
        Directory.CreateDirectory(Path.Combine(bundled, "InfoPanel.Spotify"));
        File.WriteAllText(Path.Combine(bundled, "InfoPanel.Spotify", "InfoPanel.Spotify.dll"), "not loaded: the host is a fake");
        Directory.CreateDirectory(factory.DataDir);
        File.WriteAllText(Path.Combine(factory.DataDir, DataPaths.DevicesFileName),
            """{"format":"infopanel.devices/1","devices":[{"id":"6d2c1f0e-4b8a-4c1e-9a43-0c5b2d7e8f10","family":"later","model":null,"serial":"TURZX-3.5-SN123456","port":null,"enabled":false,"profileId":null,"rotation":0,"brightness":100,"v1":{"deviceId":"SN-QWERTY-ZXCV","deviceLocation":""}}]}""");
        var launcher = new FakeLauncher(() => new FakeHost
        {
            PluginId = "spotify-now-playing",
            Name = "Spotify Now Playing",
            Entries = () => [new EntryDto { Id = "cpu", Name = "CPU Package", Type = "sensor", SensorValue = new SensorValueDto { Value = 1, Unit = "u" } }],
        });
        var engine = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:NoPlugins", "false");
            builder.UseSetting("Engine:BundledPlugins", bundled);
            builder.ConfigureTestServices(s => s.AddSingleton<InfoPanel.Plugins.IPluginProcessLauncher>(launcher));
        });
        using var client = engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        using var made = await client.PostAsync("/api/profiles",
            new StringContent($$"""{"id":"{{Guid.NewGuid():D}}","name":"Gaming Rig","width":100,"height":50}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(201, (int)made.StatusCode);
        var hub = engine.Services.GetRequiredService<SensorHub>();
        await Eventually.Until(() => hub.GetSensors().Any(s => s.Name == "CPU Package"), "the plugin's sensor is listed");
        return (factory, engine, root);
    }

    private static readonly Lazy<(string Text, JsonNode Event, string InstallId)> Sent = new(() =>
    {
        var (factory, engine, root) = EngineWithNamesAsync().GetAwaiter().GetResult();
        using var kit = new CrashReportsKit();
        var reports = kit.Start(known: ThisComputer(kit.Paths),
            names: () => CrashReporting.CrashReports.EngineNames(engine.Services.GetRequiredService<Workspace>(), engine.Services.GetRequiredService<DataPaths>(), engine.Services))!;
        SentrySdk.ConfigureScope(scope =>
        {
            scope.SetExtra("scope extra", All);
            scope.SetTag("scope-tag", All);
        });
        kit.Logger("InfoPanel.Hosting.Test").Information("Loaded {Count} profiles from {Path}", 3, "/Users/habib/Documents/Rig.infopanel");
        SentrySdk.CaptureEvent(Crafted());
        kit.Flush();
        kit.Transport.Until(t => t.Events().Count >= 1).GetAwaiter().GetResult();
        var sent = (kit.Transport.AllText(), kit.Transport.Events().Single(), reports.InstallId!);
        reports.Dispose();
        engine.Dispose();
        factory.Dispose();
        PluginFixtures.DeleteQuietly(root);
        return sent;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public async Task TheEnginesNames_AreItsProfilesPluginsPanelsAndSensors()
    {
        var (factory, engine, root) = await EngineWithNamesAsync();
        try
        {
            var names = CrashReporting.CrashReports.EngineNames(engine.Services.GetRequiredService<Workspace>(), engine.Services.GetRequiredService<DataPaths>(), engine.Services).ToList();
            foreach (var (value, label) in new[]
            {
                ("Gaming Rig", "<profile>"), ("InfoPanel.Spotify", "<plugin>"), ("Spotify Now Playing", "<plugin>"), ("spotify-now-playing", "<plugin>"),
                ("TURZX-3.5-SN123456", "<device>"), ("SN-QWERTY-ZXCV", "<device>"), ("CPU Package", "<sensor>"),
            })
                Assert.Contains(new KnownValue(value, label), names);
        }
        finally
        {
            engine.Dispose();
            factory.Dispose();
            PluginFixtures.DeleteQuietly(root);
        }
    }

    [Theory]
    [MemberData(nameof(PersonalValues))]
    public void TheEnvelopeSent_HoldsNo(string value)
    {
        Assert.DoesNotContain(value, Sent.Value.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheAllowlistedFields_ComeThrough()
    {
        var sent = Sent.Value.Event;
        var exception = sent["exception"]!["values"]![0]!;
        Assert.Equal("System.IO.FileNotFoundException", exception["type"]!.GetValue<string>());
        var frames = exception["stacktrace"]!["frames"]!.AsArray();
        Assert.Contains(frames, f => f!["function"]!.GetValue<string>() == "Load" && f["module"]!.GetValue<string>() == "InfoPanel.Documents.DocumentStore" && f["lineno"]!.GetValue<int>() == 42);
        Assert.Contains(frames, f => f!["filename"]?.GetValue<string>() == "InfoPanel/Hosting/StaticSite.cs" && f["colno"]!.GetValue<int>() == 3);
        Assert.Contains(frames, f => f!["filename"]?.GetValue<string>() == "<path>.infopanel");
        Assert.Equal(("error", "infopanel-engine@2.0.0-beta.1+abc1234", "beta"),
            (sent["level"]!.GetValue<string>(), sent["release"]!.GetValue<string>(), sent["environment"]!.GetValue<string>()));
        Assert.Equal(Sent.Value.InstallId, sent["user"]!["id"]!.GetValue<string>());
        Assert.Equal(["id"], sent["user"]!.AsObject().Select(p => p.Key));
        Assert.Equal("engine", sent["tags"]!["process"]!.GetValue<string>());
        Assert.Equal("macOS", sent["contexts"]!["os"]!["name"]!.GetValue<string>());
        Assert.Equal("Arm64", sent["contexts"]!["device"]!["arch"]!.GetValue<string>());
        // The log's breadcrumb: its template, never its values.
        Assert.Contains(sent["breadcrumbs"]!.AsArray(), b => b!["message"]?.GetValue<string>() == "Loaded {Count} profiles from {Path}");
    }

    [Fact]
    public void WhatIsntAllowlisted_IsDropped_AnUnknownContextExtraOrTagIncluded()
    {
        var sent = Sent.Value.Event.AsObject();
        foreach (var dropped in new[] { "request", "extra", "server_name", "modules", "logger", "transaction", "fingerprint", "threads", "debug_meta" })
            Assert.False(sent.ContainsKey(dropped), dropped);
        Assert.Equal(["app", "device", "os", "runtime"], sent["contexts"]!.AsObject().Select(c => c.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["arch", "type"], sent["contexts"]!["device"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.All(sent["tags"]!.AsObject(), tag => Assert.Contains(tag.Key, CrashScrubber.Tags));
        Assert.DoesNotContain("scope-tag", Sent.Value.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("scope extra", Sent.Value.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Could not open '/Users/jane/Rig.infopanel'", "Could not open <value>")]
    [InlineData("Could not open /Users/jane/Rig.infopanel now", "Could not open <path>.infopanel now")]
    [InlineData(@"Access to C:\Users\jane\x.json is denied", "Access to <path>.json is denied")]
    [InlineData("connect 192.168.1.23:5199 and 127.0.0.1", "connect <ip>:5199 and loopback")]
    [InlineData("host pc.tail1234.ts.net or nas.local", "host <host> or <host>")]
    [InlineData("mac a4:83:e7:12:34:56, ip fe80::1%en0 and ::1", "mac <mac>, ip <ip> and loopback")]
    [InlineData("mail jane@example.com", "mail <email>")]
    [InlineData("doesn't read it's InvalidOperationException at 12:34:56", "doesn't read it's InvalidOperationException at 12:34:56")]
    [InlineData("token abcdefghijklmnop0123456789", "token <token>")]
    // Whole paths first, spaces and all, before any known folder becomes a label.
    [InlineData(@"Access to C:\Users\Alice\Clients\SecretBudget.json is denied", "Access to <path>.json is denied")]
    [InlineData(@"Access to C:\Users\Alice\My Clients\Secret Budget.json is denied", "Access to <path>.json is denied")]
    [InlineData(@"Could not read \\nas\Family Share\Tax Return.pdf, giving up", "Could not read <path>.pdf, giving up")]
    [InlineData("Could not read /Users/alice/My Documents/Secret Plan.txt today", "Could not read <path>.txt today")]
    [InlineData(@"Could not read ~/Private Notes/plan.md or ~\Clients\x.json", "Could not read <path>.md or <path>.json")]
    // Tokens, digits or not; a marked one whatever its characters.
    [InlineData("Bearer abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ", "Bearer <token>")]
    [InlineData("auth failed: Bearer xyz.short-one and then", "auth failed: Bearer <token> and then")]
    [InlineData("the link #token=abc-def&next", "the link #token=<token>&next")]
    [InlineData("a session password: hunter2", "a session password: <token>")]
    [InlineData("the token was refused", "the token was refused")]
    // The engine's own long words stay: CamelCase names, hyphenated words; a run of 20 letters that reads as neither goes,
    // a lower-case word of over 15 letters included.
    [InlineData("InvalidOperationException in PluginHostConnection at RenderService.SetBeforeSendTransaction", "InvalidOperationException in PluginHostConnection at RenderService.SetBeforeSendTransaction")]
    [InlineData("the crash-reports-folder-name and HTTPRequestException", "the crash-reports-folder-name and HTTPRequestException")]
    [InlineData("internationalisations and qwrtzpsdfghjklyxcvbnm", "<token> and <token>")]
    [InlineData("------------------------ done, 0000000000000000000000", "------------------------ done, 0000000000000000000000")]
    // A path's plain words, up to its next separator, belong to it.
    [InlineData(@"Access to C:\Users\Alice\Old Family Files\Secret Budget.json is denied", "Access to <path>.json is denied")]
    [InlineData("Could not read /Users/alice/Old Family Files/Secret Budget.json today", "Could not read <path>.json today")]
    // A URL's user information, before anything else changes the URL.
    [InlineData("rtsp://guest:letmein@[fd00::1]:554/live", "rtsp://<credentials>@[<ip>]:554/live")]
    [InlineData("http://admin@192.168.1.23/snapshot.jpg", "http://<credentials>@<ip>/snapshot.jpg")]
    // Authorization schemes in any case.
    [InlineData("authorization: bearer short.jwt.secret", "authorization: bearer <token>")]
    [InlineData("BASIC dXNlcjpwYXNz failed", "BASIC <token> failed")]
    // A long hex run is a token, though it reads as CamelCase.
    [InlineData("id CafeBabeDeadBeefCafeBabe", "id <token>")]
    public void FreeText_LosesWhatItShould_AndKeepsTheRest(string text, string scrubbed)
    {
        Assert.Equal(scrubbed, CrashScrubber.ScrubText(text, []));
    }

    [Theory]
    // Short user and computer names, at word boundaries only.
    [InlineData("Could not impersonate Al", "Could not impersonate <user>")]
    [InlineData("Also Al's folder; ALBUM and al", "Also <user>'s folder; ALBUM and <user>")]
    [InlineData("Shared by PC on the network", "Shared by <computer> on the network")]
    // The home folder as a label only once whole paths are gone: a path under it is a path.
    [InlineData(@"C:\Users\Al\Secret.json and C:\Users\Al", "<path>.json and <path>")]
    public void ShortNames_AreReplaced_AsWholeWords(string text, string scrubbed)
    {
        Assert.Equal(scrubbed, CrashScrubber.ScrubText(text, [new("Al", "<user>"), new("PC", "<computer>"), new(@"C:\Users\Al", "~")]));
    }

    [Theory]
    // A known name that is also a credential's name leaves the credential marked.
    [InlineData("token=short.secret", "<profile>=<token>")]
    // A known name never changes a label made before it.
    [InlineData("Could not open /Users/x/a.json: path", "Could not open <path>.json: <profile>")]
    public void KnownNames_NeverUndoWhatTheRulesFound(string text, string scrubbed)
    {
        Assert.Equal(scrubbed, CrashScrubber.ScrubText(text, [new("token", "<profile>"), new("path", "<profile>")]));
    }

    /// <summary>Round two's leaks, each a value that must never leave the engine.</summary>
    public static readonly string[] Leaks =
    [
        @"C:\Users\Alice\Old Family Files\Secret Budget.json",
        "/Users/alice/Old Family Files/Secret Budget.json",
        "rtsp://guest:letmein@[fd00::1]:554/live",
        "token=short.secret",
        "authorization: bearer short.jwt.secret",
        "CafeBabeDeadBeefCafeBabe",
    ];

    /// <summary>What of each leak must be gone: its secret parts.</summary>
    private static readonly string[] LeakedParts =
        ["Old Family", "Family Files", "Secret Budget", "guest", "letmein", "short.secret", "short.jwt.secret", "CafeBabeDeadBeefCafeBabe", "Alice"];

    [Fact]
    public async Task AnExceptionsMessages_AndTheLogsValues_NeverLeaveTheEngine_ItsTypesFramesAndTemplateDo()
    {
        using var kit = new CrashReportsKit();
        var reports = kit.Start(known: ThisComputer(kit.Paths), names: () => [new("Gaming Rig", "<profile>"), new("token", "<profile>")])!;
        var everything = string.Join(" ", Personal.Concat(Leaks));
        const string template = "Render of {Profile} failed at {Path} with {Values}";

        Exception thrown;
        try
        {
            ThrowWith(everything);
            throw new System.Diagnostics.UnreachableException();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        kit.Logger("InfoPanel.Rendering.Test").Error(thrown, template, "Gaming Rig", "/Users/habib/Documents/Rig.infopanel", everything);
        reports.Flush(TimeSpan.FromSeconds(5));

        Assert.True(await kit.Transport.Until(t => t.Events().Count == 1));
        var text = kit.Transport.AllText();
        foreach (var value in Personal.Concat(LeakedParts))
            Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);

        var sent = kit.Transport.Events()[0];
        var exceptions = sent["exception"]!["values"]!.AsArray();
        Assert.Equal(["System.IO.IOException", "System.InvalidOperationException"], exceptions.Select(e => e!["type"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.All(exceptions, e => Assert.False(e!.AsObject().ContainsKey("value")));
        Assert.Contains(exceptions.SelectMany(e => e!["stacktrace"]?["frames"]?.AsArray() ?? []), f => f!["function"]?.GetValue<string>()?.Contains(nameof(ThrowWith), StringComparison.Ordinal) == true);
        Assert.Contains(template, sent["logentry"]?.ToJsonString() + sent["message"]?.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains(sent["breadcrumbs"]!.AsArray(), b => b!["message"]?.GetValue<string>() == template);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowWith(string text)
    {
        try
        {
            throw new IOException(text);
        }
        catch (IOException inner)
        {
            throw new InvalidOperationException(text, inner);
        }
    }

    [Fact]
    public void FreeText_IsCutAt300Characters()
    {
        var scrubbed = CrashScrubber.ScrubText(new string('x', 1000), []);
        Assert.Equal(CrashScrubber.MaxText, scrubbed.Length);
    }
}
