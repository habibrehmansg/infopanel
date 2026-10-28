using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Documents;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using InfoPanel.Settings;
using InfoPanel.Spec;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Settings;

/// <summary>
/// The engine settings: one sparse, versioned file in the data folder (<c>settings.json</c>), its 1.4.x defaults and
/// ranges, changes merged and written atomically, unknown members kept, and a file the engine can't read never rewritten.
/// 1.4.x references are to <c>InfoPanel/</c> on its <c>1.4.x</c> branch.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<LogEvent> _log = [];
    private readonly ILogger _logger;

    public SettingsStoreTests() => _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_log)).CreateLogger();

    public void Dispose() => _temp.Dispose();

    private DataPaths Paths => new(_temp.Path);

    private string File_ => Paths.SettingsFile;

    private SettingsStore Open(IStorageFaults? faults = null) => SettingsStore.Open(File_, _temp.Path, _logger, faults);

    private static JsonElement Change(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A change setting one client's preferences to <paramref name="valueJson"/>.</summary>
    private static JsonElement Clients(string name, string valueJson) => Change("{\"clients\":{" + Quoted(name) + ":" + valueJson + "}}");

    private static string Quoted(string text) => JsonSerializer.Serialize(text);

    private JsonObject Stored() => JsonNode.Parse(File.ReadAllText(File_))!.AsObject();

    private byte[] Hash() => SHA256.HashData(File.ReadAllBytes(File_));

    private List<LogEvent> Warnings => _log.Where(e => e.Level == LogEventLevel.Warning).ToList();

    // ---- defaults and ranges (1.4.x) ----------------------------------------------------------------------------

    [Fact]
    public void NoFile_GivesThe14xDefaults_AndWritesNothing()
    {
        var store = Open();

        var settings = store.Current.Settings;
        Assert.Equal(SettingsStatus.Ok, store.Status);
        Assert.Null(store.Problem);
        Assert.Equal(0, store.Current.Version);
        // Models/Settings.cs:153 TargetFrameRate = 15
        Assert.Equal(15, settings.FrameRate);
        // Models/Settings.cs:156 TargetGraphUpdateRate = 1000 (ms)
        Assert.Equal(1000, settings.GraphRate);
        // App.xaml.cs:90-94: Information outside debug builds (not a 1.4.x setting)
        Assert.Equal(EngineLogLevel.Information, settings.LogLevel);
        // Models/Settings.cs:54 LibreHardwareMonitor = true, :57 LibreHardwareMonitorStorage = true,
        // :60 LibreHardwareMonitorStorageInterval = 30 (s)
        Assert.Equal(new LibreSettings(Enabled: true, Storage: true, StorageInterval: 30), settings.Libre);
        Assert.Empty(settings.Clients);
        Assert.Equal(EngineSettings.Default, settings with { Clients = EngineSettings.Default.Clients });
        Assert.False(File.Exists(File_));
        Assert.Empty(Directory.GetFileSystemEntries(_temp.Path));
    }

    [Fact]
    public void Limits_Are14xRanges()
    {
        // Views/Pages/SettingsPage.xaml:646-647 frame rate slider 1–60; :680-681 graph rate slider 100–3000;
        // :591-593 storage interval 1–300.
        Assert.Equal((1, 60, 15), (SettingsLimits.FrameRate.Min, SettingsLimits.FrameRate.Max, SettingsLimits.FrameRate.Default));
        Assert.Equal((100, 3000, 1000), (SettingsLimits.GraphRate.Min, SettingsLimits.GraphRate.Max, SettingsLimits.GraphRate.Default));
        Assert.Equal((1, 300, 30), (SettingsLimits.StorageInterval.Min, SettingsLimits.StorageInterval.Max, SettingsLimits.StorageInterval.Default));
    }

    [Theory]
    [InlineData("""{"frameRate":1}""", "frameRate", 1)]
    [InlineData("""{"frameRate":60}""", "frameRate", 60)]
    [InlineData("""{"graphRate":100}""", "graphRate", 100)]
    [InlineData("""{"graphRate":3000}""", "graphRate", 3000)]
    [InlineData("""{"sources":{"libre":{"storageInterval":1}}}""", "storageInterval", 1)]
    [InlineData("""{"sources":{"libre":{"storageInterval":300}}}""", "storageInterval", 300)]
    // Any spelling of a whole number, as the schema's integer allows.
    [InlineData("""{"frameRate":15.0}""", "frameRate", 15)]
    [InlineData("""{"graphRate":1e2}""", "graphRate", 100)]
    // A number is the double nearest its text (spec/docs/validation.md#numbers): these are the doubles 60, 1 and 30.
    [InlineData("""{"frameRate":60.00000000000000000000000000001}""", "frameRate", 60)]
    [InlineData("""{"frameRate":0.99999999999999999999999999999999}""", "frameRate", 1)]
    [InlineData("""{"frameRate":30.00000000000000000000000000001}""", "frameRate", 30)]
    public void TheEdgesOfEachRange_AreAccepted(string change, string member, int value)
    {
        var store = Open();

        var settings = store.Apply(Change(change)).Settings;

        Assert.Equal(value, member switch
        {
            "frameRate" => settings.FrameRate,
            "graphRate" => settings.GraphRate,
            _ => settings.Libre.StorageInterval,
        });
    }

    // ---- changes ------------------------------------------------------------------------------------------------

    [Fact]
    public void AChange_WritesFormatAndOnlyTheSetMembers()
    {
        var store = Open();
        var raised = new List<SettingsSnapshot>();
        store.Changed += raised.Add;

        var result = store.Apply(Change("""{"frameRate":30,"sources":{"libre":{"storageInterval":60}}}"""));

        Assert.Equal((30, 60, 1), (result.Settings.FrameRate, result.Settings.Libre.StorageInterval, result.Version));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"format":"infopanel.settings/1","frameRate":30,"sources":{"libre":{"storageInterval":60}}}"""), Stored()));
        Assert.Equal(result, Assert.Single(raised));
        Assert.Equal(result, store.Current);
        // Read back by the next start.
        Assert.Equal((30, 1000, 60), (Open().Current.Settings.FrameRate, Open().Current.Settings.GraphRate, Open().Current.Settings.Libre.StorageInterval));
    }

    [Fact]
    public void Null_RemovesAMember_BackToItsDefault()
    {
        var store = Open();
        store.Apply(Change("""{"frameRate":30,"graphRate":250,"logLevel":"debug","sources":{"libre":{"enabled":false}}}"""));

        var result = store.Apply(Change("""{"frameRate":null,"logLevel":null,"sources":{"libre":{"enabled":null}}}"""));

        Assert.Equal((15, 250, EngineLogLevel.Information, true), (result.Settings.FrameRate, result.Settings.GraphRate, result.Settings.LogLevel, result.Settings.Libre.Enabled));
        Assert.Equal(2, result.Version);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"format":"infopanel.settings/1","graphRate":250}"""), Stored()));
    }

    [Fact]
    public void ANoOpChange_WritesNothing_AndRaisesNothing()
    {
        var store = Open();
        store.Apply(Change("""{"frameRate":30,"logLevel":"warning"}"""));
        var hash = Hash();
        var written = File.GetLastWriteTimeUtc(File_);
        File.SetLastWriteTimeUtc(File_, written.AddHours(-1));
        var raised = 0;
        store.Changed += _ => raised++;

        var same = store.Apply(Change("""{"frameRate":30}"""));
        var empty = store.Apply(Change("{}"));
        var removeAbsent = store.Apply(Change("""{"graphRate":null,"sources":{"libre":{"storage":null}}}"""));

        Assert.All([same, empty, removeAbsent], s => Assert.Equal(1, s.Version));
        Assert.Equal(0, raised);
        Assert.Equal(hash, Hash());
        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(File_));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }

    [Theory]
    // Numbers compare as the doubles nearest their text (spec/docs/validation.md#numbers), in settings and in client
    // preferences alike: the same value spelled otherwise is no change, and the stored spelling stays.
    [InlineData("""{"frameRate":30.0}""", """{"frameRate":30}""")]
    [InlineData("""{"frameRate":3e1,"sources":{"libre":{"storageInterval":60.0}}}""", """{"frameRate":30,"sources":{"libre":{"storageInterval":6e1}}}""")]
    [InlineData("""{"clients":{"web":{"n":1}}}""", """{"clients":{"web":{"n":1.00000000000000001}}}""")]
    [InlineData("""{"clients":{"web":{"n":[1.00000000000000001,-0]}}}""", """{"clients":{"web":{"n":[1,0]}}}""")]
    public void AChangeToTheSameNumbers_SpelledOtherwise_WritesNothing_AndKeepsTheStoredSpelling(string stored, string change)
    {
        var content = """{"format":"infopanel.settings/1",""" + stored[1..];
        File.WriteAllText(File_, content);
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(File_, written);
        var store = Open();
        var raised = 0;
        store.Changed += _ => raised++;

        var same = store.Apply(Change(change));

        Assert.Equal((0L, 0), (same.Version, raised));
        Assert.Equal(content, File.ReadAllText(File_));
        Assert.Equal(written, File.GetLastWriteTimeUtc(File_));

        // A different value is still a change, and the members it doesn't name keep their spelling.
        var other = store.Apply(Change("""{"graphRate":500}"""));
        Assert.Equal((1L, 1), (other.Version, raised));
        var expected = JsonNode.Parse(content)!.AsObject();
        expected["graphRate"] = 500;
        Assert.Equal(expected.ToJsonString(), Stored().ToJsonString());
    }

    [Fact]
    public void ANoOpChange_OnAFolderWithoutTheFile_WritesNothing()
    {
        var store = Open();

        store.Apply(Change("""{"frameRate":null,"clients":{"x":null}}"""));

        Assert.False(File.Exists(File_));
        Assert.Equal(0, store.Current.Version);
    }

    [Theory]
    [InlineData("""{"frameRate":0}""", "/frameRate")]
    [InlineData("""{"frameRate":61}""", "/frameRate")]
    [InlineData("""{"frameRate":15.5}""", "/frameRate")]
    // The doubles 60.00000000000001 and 0.9999999999999999: not whole.
    [InlineData("""{"frameRate":60.00000000000001}""", "/frameRate")]
    [InlineData("""{"frameRate":0.9999999999999999}""", "/frameRate")]
    [InlineData("""{"frameRate":"15"}""", "/frameRate")]
    [InlineData("""{"graphRate":99}""", "/graphRate")]
    [InlineData("""{"graphRate":3001}""", "/graphRate")]
    [InlineData("""{"sources":{"libre":{"storageInterval":0}}}""", "/sources/libre/storageInterval")]
    [InlineData("""{"sources":{"libre":{"storageInterval":301}}}""", "/sources/libre/storageInterval")]
    [InlineData("""{"sources":{"libre":{"enabled":"yes"}}}""", "/sources/libre/enabled")]
    [InlineData("""{"sources":{"libre":{"storage":1}}}""", "/sources/libre/storage")]
    [InlineData("""{"logLevel":"verbose"}""", "/logLevel")]
    [InlineData("""{"logLevel":"Information"}""", "/logLevel")]
    [InlineData("""{"logLevel":3}""", "/logLevel")]
    [InlineData("""{"theme":"dark"}""", "/theme")]
    [InlineData("""{"format":"infopanel.settings/1"}""", "/format")]
    [InlineData("""{"sources":{"other":{}}}""", "/sources/other")]
    [InlineData("""{"sources":{"libre":{"port":1}}}""", "/sources/libre/port")]
    [InlineData("""{"sources":null}""", "/sources")]
    [InlineData("""{"sources":{"libre":[]}}""", "/sources/libre")]
    [InlineData("""{"clients":[]}""", "/clients")]
    [InlineData("""{"clients":{"":1}}""", "/clients/")]
    [InlineData("""{"clients":{"a/b":1}}""", "/clients/a~1b")]
    [InlineData("[]", "")]
    public void AnInvalidChange_IsRefusedAtItsPath_AndChangesNothing(string change, string path)
    {
        var store = Open();
        store.Apply(Change("""{"graphRate":500}"""));
        var hash = Hash();

        var refused = Assert.Throws<SettingsChangeException>(() => store.Apply(Change(change)));

        var error = Assert.Single(refused.Errors);
        Assert.Equal((path, "schema"), (error.Path, error.Code));
        Assert.Equal(hash, Hash());
        Assert.Equal((500, 1), (store.Current.Settings.GraphRate, store.Current.Version));
    }

    [Theory]
    // A number whose text rounds to ±∞ (spec/docs/validation.md#numbers) is refused wherever it is: in a setting, in a
    // client's preferences (opaque), in a member the engine doesn't know; json, at the number, before any other rule.
    [InlineData("""{"frameRate":1e400}""", "/frameRate")]
    [InlineData("""{"clients":{"web":{"n":1e400}}}""", "/clients/web/n")]
    [InlineData("""{"clients":{"web":[1,{"x":[-1e400]}]}}""", "/clients/web/1/x/0")]
    [InlineData("""{"future":{"n":-1e400}}""", "/future/n")]
    [InlineData("""{"sources":{"libre":{"storageInterval":1e999}}}""", "/sources/libre/storageInterval")]
    public void AChangeWithANumberBeyondTheDoubleRange_IsRefusedAtTheNumber_AndChangesNothing(string change, string path)
    {
        var store = Open();
        store.Apply(Change("""{"graphRate":500}"""));
        var hash = Hash();

        var refused = Assert.Throws<SettingsChangeException>(() => store.Apply(Change(change)));

        var error = Assert.Single(refused.Errors);
        Assert.Equal((path, "json"), (error.Path, error.Code));
        Assert.Equal(hash, Hash());
        Assert.Equal((500, 1), (store.Current.Settings.GraphRate, store.Current.Version));
    }

    [Fact]
    public void AChangeWithNumbersBeyondTheDoubleRange_ListsEachOfThem()
    {
        var store = Open();

        var refused = Assert.Throws<SettingsChangeException>(() => store.Apply(Change(
            """{"frameRate":30,"clients":{"a":{"n":1e400},"b":[2e400]},"x":-3e400}""")));

        Assert.Equal([("/clients/a/n", "json"), ("/clients/b/0", "json"), ("/x", "json")],
            refused.Errors.Select(e => (e.Path, e.Code)).OrderBy(e => e.Path, StringComparer.Ordinal));
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void AChangeNested100000Deep_InAnUnknownMember_IsRefused_WithoutOverflowingTheStack()
    {
        var store = Open();
        store.Apply(Change("""{"graphRate":500}"""));
        var hash = Hash();
        // As the API hands a body over: parsed at any depth (parsing is not recursive), so only the store can bound it.
        var deep = JsonDocument.Parse("""{"future":""" + Nested(100_000) + "}", SpecJson.DocumentOptions).RootElement.Clone();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var refused = Assert.Throws<SettingsChangeException>(() => store.Apply(deep));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Took {clock.Elapsed}.");
        var error = Assert.Single(refused.Errors);
        Assert.Equal(("", "json"), (error.Path, error.Code));
        Assert.Equal(hash, Hash());
        Assert.Equal((500, 1), (store.Current.Settings.GraphRate, store.Current.Version));
    }

    [Fact]
    public void ANonObjectChange_WithANumberBeyondTheDoubleRange_IsRefusedAtTheNumber()
    {
        var store = Open();

        var refused = Assert.Throws<SettingsChangeException>(() => store.Apply(Change("[1e400]")));

        var error = Assert.Single(refused.Errors);
        Assert.Equal(("/0", "json"), (error.Path, error.Code));
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void AnInvalidChange_ListsEveryError_AndAppliesNothingOfIt()
    {
        var store = Open();

        var refused = Assert.Throws<SettingsChangeException>(() => store.Apply(Change(
            """{"frameRate":30,"graphRate":5000,"logLevel":"loud","sources":{"libre":{"storageInterval":0,"enabled":false}},"x":1}""")));

        Assert.Equal(["/graphRate", "/logLevel", "/sources/libre/storageInterval", "/x"], refused.Errors.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal(15, store.Current.Settings.FrameRate);
        Assert.False(File.Exists(File_));
    }

    // ---- what the file holds ------------------------------------------------------------------------------------

    [Fact]
    public void AStoredMemberOutOfRangeOrOfTheWrongType_GetsItsDefault_WithOneWarningNamingIt_AndTheNextWriteDropsIt()
    {
        File.WriteAllText(File_, """
            {"format":"infopanel.settings/1","frameRate":0,"graphRate":"fast","logLevel":"loud",
             "sources":{"libre":{"enabled":false,"storageInterval":301}},"keep":true}
            """);

        var store = Open();

        Assert.Equal(SettingsStatus.Ok, store.Status);
        var settings = store.Current.Settings;
        Assert.Equal((15, 1000, EngineLogLevel.Information), (settings.FrameRate, settings.GraphRate, settings.LogLevel));
        Assert.Equal(new LibreSettings(false, true, 30), settings.Libre);
        var warnings = Warnings.Select(w => w.RenderMessage()).ToList();
        Assert.Equal(4, warnings.Count);
        foreach (var path in new[] { "/frameRate", "/graphRate", "/logLevel", "/sources/libre/storageInterval" })
            Assert.Single(warnings, w => w.Contains(path, StringComparison.Ordinal));

        store.Apply(Change("""{"sources":{"libre":{"storage":false}}}"""));

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"format":"infopanel.settings/1","sources":{"libre":{"enabled":false,"storage":false}},"keep":true}"""), Stored()));
    }

    [Theory]
    [InlineData("60.00000000000000000000000000001", 60)]
    [InlineData("0.99999999999999999999999999999999", 1)]
    [InlineData("30.00000000000000000000000000001", 30)]
    [InlineData("60.00000000000001", 15)]
    [InlineData("20.0", 20)]
    [InlineData("24e0", 24)]
    public void AStoredNumber_IsWholeAndInRangeAsTheDoubleNearestItsText(string frameRate, int expected)
    {
        File.WriteAllText(File_, """{"format":"infopanel.settings/1","frameRate":""" + frameRate + ""","graphRate":1e2}""");

        var store = Open();

        Assert.Equal((expected, 100), (store.Current.Settings.FrameRate, store.Current.Settings.GraphRate));
        var warnings = Warnings.Select(w => w.RenderMessage()).ToList();
        if (expected == SettingsLimits.FrameRate.Default)
            Assert.Contains("/frameRate", Assert.Single(warnings), StringComparison.Ordinal);
        else
            Assert.Empty(warnings);
    }

    [Fact]
    public void StoredContainersOfTheWrongType_AreIgnored_WithAWarning()
    {
        File.WriteAllText(File_, """{"format":"infopanel.settings/1","frameRate":24,"sources":[1],"clients":"x"}""");

        var store = Open();

        Assert.Equal((SettingsStatus.Ok, 24), (store.Status, store.Current.Settings.FrameRate));
        Assert.Equal(new LibreSettings(true, true, 30), store.Current.Settings.Libre);
        Assert.Empty(store.Current.Settings.Clients);
        Assert.Equal(2, Warnings.Count);
    }

    [Fact]
    public void UnknownMembers_AtEveryDepth_SurviveAChange_AsTheyWere()
    {
        const string unknown = """{"n":1.50e2,"s":"café 😀","deep":[{"a":null},true,-0.0],"big":123456789012345678901234567890}""";
        File.WriteAllText(File_, """{"format":"infopanel.settings/1","future":""" + unknown + ""","frameRate":20,"sources":{"other":"""
            + unknown + ""","libre":{"enabled":true,"newer":""" + unknown + "}}}");
        var store = Open();

        store.Apply(Change("""{"frameRate":25,"sources":{"libre":{"storage":false}}}"""));
        store.Apply(Change("""{"sources":{"libre":{"enabled":null}}}"""));

        var stored = Stored();
        var expected = JsonNode.Parse(unknown);
        Assert.True(JsonNode.DeepEquals(expected, stored["future"]));
        Assert.True(JsonNode.DeepEquals(expected, stored["sources"]!["other"]));
        Assert.True(JsonNode.DeepEquals(expected, stored["sources"]!["libre"]!["newer"]));
        // Numbers keep their text: no rounding of the big one, no reformatting of the others.
        var text = File.ReadAllText(File_);
        Assert.Equal(3, CountOf(text, "123456789012345678901234567890"));
        Assert.Equal(3, CountOf(text, "1.50e2"));
        Assert.Equal(3, CountOf(text, "-0.0"));
        Assert.Equal(25, stored["frameRate"]!.GetValue<int>());
        Assert.Null(stored["sources"]!["libre"]!["enabled"]);
        Assert.False(stored["sources"]!["libre"]!["storage"]!.GetValue<bool>());
        Assert.Equal(["format", "future", "frameRate", "sources"], stored.Select(p => p.Key));
        Assert.Empty(Warnings);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // ---- a file the engine can't read is never rewritten ---------------------------------------------------------

    public static TheoryData<string, string, string> UnreadableFiles => new()
    {
        { "not json", "json", "" },
        { """{"format":"infopanel.settings/1","frameRate":30""", "json", "" },
        { """[{"format":"infopanel.settings/1"}]""", "schema", "" },
        { "\"infopanel.settings/1\"", "schema", "" },
        { """{"frameRate":30}""", "schema", "/format" },
        { """{"format":1}""", "schema", "/format" },
        { """{"format":"infopanel.document/1"}""", "schema", "/format" },
        { """{"format":"infopanel.settings/1","format":"infopanel.settings/1"}""", "json", "" },
        // A lone surrogate escape doesn't decode to text: in a known member, an unknown one, or a member name.
        { """{"format":"infopanel.settings/1","logLevel":"\uD800"}""", "json", "" },
        { """{"format":"infopanel.settings/1","future":{"deep":"\uD800"}}""", "json", "" },
        { """{"format":"infopanel.settings/1","\uD800":1}""", "json", "" },
        // A number whose text rounds to ±∞, as in a stored document: in a setting, a client's preferences, an unknown member.
        { """{"format":"infopanel.settings/1","frameRate":1e400}""", "json", "/frameRate" },
        { """{"format":"infopanel.settings/1","clients":{"web":{"n":1e400}}}""", "json", "/clients/web/n" },
        { """{"format":"infopanel.settings/1","future":{"n":-1e400}}""", "json", "/future/n" },
        { """{"format":"infopanel.settings/1","future":[0,[1,-1e400]]}""", "json", "/future/1/1" },
    };

    [Theory]
    [MemberData(nameof(UnreadableFiles))]
    public void AnUnreadableFile_UsesTheDefaults_RefusesChanges_AndIsNeverRewritten(string content, string code, string path)
    {
        File.WriteAllText(File_, content);
        var hash = Hash();
        File.SetLastWriteTimeUtc(File_, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var store = Open();

        Assert.Equal(SettingsStatus.Unreadable, store.Status);
        Assert.Equal((code, path), (store.Problem!.Code, store.Problem.Path));
        Assert.Equal(15, store.Current.Settings.FrameRate);
        Assert.Single(_log, e => e.Level == LogEventLevel.Error);

        var refused = Assert.Throws<SettingsUnavailableException>(() => store.Apply(Change("""{"frameRate":30}""")));
        Assert.Equal(SettingsStatus.Unreadable, refused.Status);
        // Even an invalid change: the status is answered first, as for profiles.
        Assert.Throws<SettingsUnavailableException>(() => store.Apply(Change("""{"frameRate":0}""")));
        Assert.Throws<SettingsUnavailableException>(() => store.Apply(Change("{}")));

        Assert.Equal(hash, Hash());
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(File_));
        Assert.Equal(["settings.json"], Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName));
        Assert.Equal(15, store.Current.Settings.FrameRate);
    }

    [Fact]
    public void AFileOver1MiB_IsUnreadable_AndNeverRewritten()
    {
        var padding = new string(' ', SettingsLimits.MaxFileBytes);
        File.WriteAllText(File_, """{"format":"infopanel.settings/1","frameRate":30}""" + padding);
        var hash = Hash();

        var store = Open();

        Assert.Equal((SettingsStatus.Unreadable, "limit.settingsBytes"), (store.Status, store.Problem!.Code));
        Assert.Equal(15, store.Current.Settings.FrameRate);
        Assert.Throws<SettingsUnavailableException>(() => store.Apply(Change("""{"frameRate":20}""")));
        Assert.Equal(hash, Hash());
    }

    [Fact]
    public void AFileOfExactly1MiB_IsRead()
    {
        var head = """{"format":"infopanel.settings/1","frameRate":30}""";
        File.WriteAllText(File_, head + new string(' ', SettingsLimits.MaxFileBytes - head.Length));

        Assert.Equal((SettingsStatus.Ok, 30), (Open().Status, Open().Current.Settings.FrameRate));
    }

    [Fact]
    public void AFolderAtThePath_IsUnreadable_AndLeftAsItIs()
    {
        Directory.CreateDirectory(File_);

        var store = Open();

        Assert.Equal((SettingsStatus.Unreadable, "io"), (store.Status, store.Problem!.Code));
        Assert.Throws<SettingsUnavailableException>(() => store.Apply(Change("""{"frameRate":20}""")));
        Assert.True(Directory.Exists(File_));
    }

    [Theory]
    [InlineData("infopanel.settings/2")]
    [InlineData("infopanel.settings/10")]
    public void AnotherMajorVersion_IsUnsupported_UsesTheDefaults_AndIsNeverRewritten(string format)
    {
        File.WriteAllText(File_, $$"""{"format":"{{format}}","frameRate":30,"devices":[1,2]}""");
        var hash = Hash();

        var store = Open();

        Assert.Equal(SettingsStatus.Unsupported, store.Status);
        Assert.Equal(("unsupported.format", "/format"), (store.Problem!.Code, store.Problem.Path));
        Assert.Equal(15, store.Current.Settings.FrameRate);
        var refused = Assert.Throws<SettingsUnavailableException>(() => store.Apply(Change("""{"graphRate":500}""")));
        Assert.Equal(SettingsStatus.Unsupported, refused.Status);
        Assert.Equal(hash, Hash());
    }

    [Fact]
    public void AUtf8ByteOrderMark_IsAccepted()
    {
        File.WriteAllBytes(File_, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""{"format":"infopanel.settings/1","frameRate":42}""")]);

        Assert.Equal((SettingsStatus.Ok, 42), (Open().Status, Open().Current.Settings.FrameRate));
    }

    // ---- writes -------------------------------------------------------------------------------------------------

    [Fact]
    public void AFailedWrite_LeavesTheOldFileAndValues_AndRaisesNothing()
    {
        var faults = new MatchFaults();
        var store = Open(faults);
        store.Apply(Change("""{"frameRate":30}"""));
        var hash = Hash();
        var raised = 0;
        store.Changed += _ => raised++;

        foreach (var step in new[] { StorageStep.CreateTemp, StorageStep.WriteTemp, StorageStep.FlushTemp, StorageStep.ReplaceFile })
        {
            faults.Fail = (s, _) => s == step;
            Assert.Throws<IOException>(() => store.Apply(Change("""{"frameRate":45,"graphRate":200}""")));

            Assert.Equal((30, 1000, 1), (store.Current.Settings.FrameRate, store.Current.Settings.GraphRate, store.Current.Version));
            Assert.Equal(hash, Hash());
            Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
        }

        Assert.Equal(0, raised);
        faults.Fail = null;
        Assert.Equal((45, 2), (store.Apply(Change("""{"frameRate":45}""")).Settings.FrameRate, store.Current.Version));
    }

    [Fact]
    public void AFolderFlushThatFailsAfterTheRename_KeepsTheNewValues_WhichTheFileHolds()
    {
        var faults = new MatchFaults();
        var store = Open(faults);
        var raised = new List<SettingsSnapshot>();
        store.Changed += raised.Add;
        faults.Fail = (s, _) => s == StorageStep.SyncFolder;

        Assert.Throws<IOException>(() => store.Apply(Change("""{"frameRate":45}""")));

        // The file was replaced: the store answers what it holds, so a retry adds no second version.
        Assert.Equal((45, 1), (store.Current.Settings.FrameRate, store.Current.Version));
        Assert.Equal(store.Current, Assert.Single(raised));
        Assert.Equal(45, Stored()["frameRate"]!.GetValue<int>());
        // But it doesn't skip the flush still owed: while the folder can't be flushed, the retry fails too.
        Assert.Throws<IOException>(() => store.Apply(Change("""{"frameRate":45}""")));
        faults.Fail = null;
        faults.Steps.Clear();
        Assert.Equal(1, store.Apply(Change("""{"frameRate":45}""")).Version);
        var steps = faults.Steps.Select(s => s.Step).ToList();
        Assert.Contains(StorageStep.ReplaceFile, steps);
        Assert.Contains(StorageStep.SyncFolder, steps);
        Assert.Equal(store.Current, Assert.Single(raised));
        // Flushed: the next identical change is a no-op again and writes nothing.
        faults.Steps.Clear();
        Assert.Equal(1, store.Apply(Change("""{"frameRate":45}""")).Version);
        Assert.Empty(faults.Steps);
    }

    [Fact]
    public void AWrite_GoesThroughATempFileRenamedOverTheTarget_AndFlushesTheFolder()
    {
        var faults = new MatchFaults();
        var store = Open(faults);

        store.Apply(Change("""{"frameRate":30}"""));

        var steps = faults.Steps.Select(s => s.Step).ToList();
        Assert.Equal([StorageStep.CreateTemp, StorageStep.WriteTemp, StorageStep.FlushTemp, StorageStep.ReplaceFile, StorageStep.SyncFolder],
            steps.SkipWhile(s => s != StorageStep.CreateTemp));
        Assert.Equal(File_, faults.Steps.Single(s => s.Step == StorageStep.ReplaceFile).Path);
    }

    [Fact]
    public void TempFilesAnInterruptedWriteLeft_AreRemovedOnOpen_AndOthersAreKept()
    {
        var temp = $"{File_}.{Guid.NewGuid():N}.tmp";
        var other = $"{File_}.notes.tmp";
        File.WriteAllText(temp, "{");
        File.WriteAllText(other, "mine");

        var store = Open();

        Assert.Equal(SettingsStatus.Ok, store.Status);
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void SixteenThreadsChangingDifferentMembers_AllLand_AndTheVersionCountsThem()
    {
        var store = Open();
        var versions = new List<long>();
        store.Changed += s => { lock (versions) versions.Add(s.Version); };

        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            store.Apply(Change("{\"clients\":{\"c" + i + "\":{\"i\":" + i + "}}}")));

        Assert.Equal(16, store.Current.Version);
        // Raised under the store's lock, so in the changes' order.
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (long)i), versions);
        var clients = Stored()["clients"]!.AsObject();
        Assert.Equal(16, clients.Count);
        for (var i = 0; i < 16; i++)
            Assert.Equal(i, clients[$"c{i}"]!["i"]!.GetValue<int>());
        Assert.Equal(16, Open().Current.Settings.Clients.Count);
    }

    // ---- client preferences -------------------------------------------------------------------------------------

    [Fact]
    public void ClientPreferences_AreSetAndRemovedWhole_AndKeptAsTheyWere()
    {
        var store = Open();

        store.Apply(Change("""{"clients":{"web":{"theme":"dark","grid":{"on":true,"spacing":20}},"tray.app-1":[1,"two"]}}"""));
        var replaced = store.Apply(Change("""{"clients":{"web":{"theme":"light"}}}"""));

        Assert.Equal(["tray.app-1", "web"], replaced.Settings.Clients.Keys.Order(StringComparer.Ordinal));
        Assert.True(JsonElement.DeepEquals(Change("""{"theme":"light"}"""), replaced.Settings.Clients["web"]));
        Assert.True(JsonElement.DeepEquals(Change("""[1,"two"]"""), Open().Current.Settings.Clients["tray.app-1"]));

        var removed = store.Apply(Change("""{"clients":{"web":null,"tray.app-1":null}}"""));

        Assert.Empty(removed.Settings.Clients);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"format":"infopanel.settings/1"}"""), Stored()));
    }

    [Fact]
    public void ClientPreferences_KeepTheirLimits()
    {
        var store = Open();
        var name64 = new string('a', SettingsLimits.ClientNameLength);
        var atLimit = new string('x', SettingsLimits.ClientBytes - 2); // a JSON string: two quotes
        store.Apply(Clients(name64, Quoted(atLimit)));

        var tooLong = Assert.Throws<SettingsChangeException>(() => store.Apply(Clients(name64 + "b", "1")));
        Assert.Equal(($"/clients/{name64}b", "schema"), (tooLong.Errors[0].Path, tooLong.Errors[0].Code));
        var tooBig = Assert.Throws<SettingsChangeException>(() => store.Apply(Clients("b", Quoted(atLimit + "x"))));
        Assert.Equal(("/clients/b", "limit.clientBytes"), (tooBig.Errors[0].Path, tooBig.Errors[0].Code));

        for (var i = 1; i < SettingsLimits.Clients; i++)
            store.Apply(Clients("c" + i, i.ToString()));
        var tooMany = Assert.Throws<SettingsChangeException>(() => store.Apply(Change("""{"clients":{"one-more":1}}""")));
        Assert.Equal(("/clients", "limit.clients"), (tooMany.Errors[0].Path, tooMany.Errors[0].Code));
        // Replacing one and removing one in the same change stays within the count.
        Assert.Equal(SettingsLimits.Clients, store.Apply(Change("""{"clients":{"c1":null,"one-more":1}}""")).Settings.Clients.Count);
        Assert.Equal(SettingsLimits.Clients, Open().Current.Settings.Clients.Count);
    }

    public static TheoryData<string> PreferencesOverTheLimitAsStored => new()
    {
        // Sent unescaped (11,002 and 60,002 bytes as given), but each '<' and each CJK character is stored as a six-byte
        // \u escape, so these are over 64 KiB as the file holds them.
        Unescaped(new string('<', 11_000)),
        """{"notes":""" + Unescaped(new string('<', 11_000)) + "}",
        Unescaped(new string('\u6587', 20_000)),
    };

    /// <summary>A JSON string of text that needs no escape in JSON, written as it is (<see cref="Quoted"/> escapes more).</summary>
    private static string Unescaped(string text) => "\"" + text + "\"";

    [Theory]
    [MemberData(nameof(PreferencesOverTheLimitAsStored))]
    public void ClientPreferences_AreMeasuredAsStored_SoOnesOverTheLimitThereAreRefused(string valueJson)
    {
        var store = Open();

        var refused = Assert.Throws<SettingsChangeException>(() => store.Apply(Clients("web", valueJson)));

        Assert.Equal(("/clients/web", "limit.clientBytes"), (refused.Errors[0].Path, refused.Errors[0].Code));
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void ClientPreferences_WithEscapes_UnderTheLimitAsStored_AreKeptAtTheNextStart()
    {
        var value = new string('<', 10_000); // 60,002 bytes as stored
        Open().Apply(Clients("web", Unescaped(value)));

        var reopened = Open();

        Assert.Equal(value, reopened.Current.Settings.Clients["web"].GetString());
        Assert.Empty(Warnings);
    }

    /// <summary>A JSON value of arrays nested <paramref name="depth"/> deep around a number.</summary>
    private static string Nested(int depth) => new string('[', depth) + "1" + new string(']', depth);

    /// <summary>Changes the file couldn't be read back with, each as the API parses a body (any depth, duplicates allowed).</summary>
    public static TheoryData<string> ChangesTheFileCouldntHold => new()
    {
        """{"clients":{"web":{"x":1,"x":2}}}""",
        // The file reads 64 deep; a client's value sits two below the root, so 62 is its deepest.
        """{"clients":{"web":""" + Nested(63) + "}}",
        """{"clients":{"web":""" + Nested(65) + "}}",
        """{"clients":{"web":"\uD800"}}""",
        """{"logLevel":"\uD800"}""",
    };

    [Theory]
    [MemberData(nameof(ChangesTheFileCouldntHold))]
    public void AChangeTheFileCouldntBeReadBackWith_IsRefused_AndWritesNothing(string change)
    {
        var store = Open();
        store.Apply(Change("""{"frameRate":30}"""));
        var hash = Hash();
        var raised = 0;
        store.Changed += _ => raised++;
        var parsed = JsonDocument.Parse(change, SpecJson.DocumentOptions).RootElement.Clone();

        var refused = Assert.Throws<SettingsChangeException>(() => store.Apply(parsed));

        var error = Assert.Single(refused.Errors);
        Assert.Equal(("", "json"), (error.Path, error.Code));
        Assert.Equal(hash, Hash());
        Assert.Equal((0, 1), (raised, store.Current.Version));
        var reopened = Open();
        Assert.Equal((SettingsStatus.Ok, 30), (reopened.Status, reopened.Current.Settings.FrameRate));
    }

    [Fact]
    public void AClientValue62Deep_IsKept_AndReadAtTheNextStart()
    {
        var store = Open();
        var value = Change(Nested(62));

        store.Apply(Change("""{"clients":{"web":""" + Nested(62) + "}}"));

        var reopened = Open();
        Assert.Equal(SettingsStatus.Ok, reopened.Status);
        Assert.True(JsonElement.DeepEquals(value, reopened.Current.Settings.Clients["web"]));
    }

    [Fact]
    public void AChangeThatWouldGrowTheFilePast1MiB_IsRefused()
    {
        // 15 clients of 64 KiB fit (under 1 MiB with their names), as does the 16th: it is the file's own cap that
        // refuses, so the next start can always read what was written.
        File.WriteAllText(File_, $$"""{"format":"infopanel.settings/1","future":"{{new string('f', 200_000)}}"}""");
        var store = Open();
        var big = new string('x', SettingsLimits.ClientBytes - 2);
        var i = 0;
        SettingsChangeException? refused = null;
        for (; i < SettingsLimits.Clients && refused == null; i++)
        {
            try { store.Apply(Clients("c" + i, Quoted(big))); }
            catch (SettingsChangeException ex) { refused = ex; }
        }

        Assert.NotNull(refused);
        Assert.Equal(("", "limit.settingsBytes"), (refused.Errors[0].Path, refused.Errors[0].Code));
        Assert.True(new FileInfo(File_).Length <= SettingsLimits.MaxFileBytes);
        Assert.Equal(SettingsStatus.Ok, Open().Status);
    }

    [Fact]
    public void StoredClientPreferences_OutsideTheLimits_AreIgnored_WithAWarning()
    {
        File.WriteAllText(File_, """{"format":"infopanel.settings/1","clients":{"ok":1,"bad name":2,""" + Quoted(new string('n', 65))
            + """:3,"big":""" + Quoted(new string('x', SettingsLimits.ClientBytes)) + "}}");

        var store = Open();

        Assert.Equal(["ok"], store.Current.Settings.Clients.Keys);
        Assert.Equal(3, Warnings.Count);
    }

    private sealed class CollectingSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }
}
