using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Settings;
using InfoPanel.Spec;
using Serilog.Core;
using Xunit;

namespace InfoPanel.Core.Tests.Settings;

/// <summary>
/// The <c>sources.systemInfo.excludedProcesses</c> setting (spec/docs/api.md, "Settings"): the process names the system
/// information source's tables leave out. By default the 1.x System Info plugin's nine; at most 64 names, each 1 to 64
/// characters, kept exactly as given (no white space trimmed, any case, a name twice allowed), set whole, and back to the
/// default with <c>null</c>; an empty list leaves nothing out. The store's own check, which a change and the file both go
/// through, agrees with the schema's.
/// </summary>
public sealed class ExcludedProcessesSettingTests : IDisposable
{
    private static readonly string[] Nine = ["_Total", "Idle", "dwm", "csrss", "svchost", "lsass", "system", "spoolsv", "Memory Compression"];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string File_ => _temp.Combine("settings.json");

    private SettingsStore Open() => SettingsStore.Open(File_, _temp.Path, Logger.None, faults: null);

    private static JsonElement Change(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement ChangeOf(string excluded) => Change("""{"sources":{"systemInfo":{"excludedProcesses":""" + excluded + "}}}");

    private static bool SchemaTakes(JsonElement change) =>
        SpecSchemas.Strict.Validate(change, $"{SpecSchemas.Api}#/$defs/settingsChange").IsValid;

    private JsonNode? Stored() => JsonNode.Parse(File.ReadAllText(File_))!["sources"]?["systemInfo"]?["excludedProcesses"];

    [Fact]
    public void TheDefault_Is1xsNineNames()
    {
        Assert.Equal(Nine, SystemInfoSettings.Default.ExcludedProcesses);
        Assert.Equal(Nine, EngineSettings.Default.SystemInfo.ExcludedProcesses);
        Assert.Equal(Nine, Open().Current.Settings.SystemInfo.ExcludedProcesses);
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void Names_AreKeptExactlyAsGiven_SetWhole_AndNullSetsThe1xDefaultBack()
    {
        var store = Open();
        string[] names = ["InfoPanel", "node ", " node", "NODE", "Google Chrome Helper (Renderer)", "名前", "node"];
        var change = ChangeOf(JsonSerializer.Serialize(names));

        Assert.True(SchemaTakes(change));
        Assert.Empty(SettingsStore.CheckChange(change, SpecLimits.Default));
        store.Apply(change);

        Assert.Equal(names, store.Current.Settings.SystemInfo.ExcludedProcesses);
        Assert.Equal(names, Stored()!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(names, Open().Current.Settings.SystemInfo.ExcludedProcesses);

        // Set whole: a second list replaces the first, the default's names included.
        store.Apply(ChangeOf("""["InfoPanel","InfoPanel"]"""));
        Assert.Equal(["InfoPanel", "InfoPanel"], store.Current.Settings.SystemInfo.ExcludedProcesses);

        // Empty: nothing is left out, and it is stored.
        store.Apply(ChangeOf("[]"));
        Assert.Empty(store.Current.Settings.SystemInfo.ExcludedProcesses);
        Assert.Empty(Stored()!.AsArray());

        store.Apply(ChangeOf("null"));
        Assert.Equal(Nine, store.Current.Settings.SystemInfo.ExcludedProcesses);
        Assert.Null(JsonNode.Parse(File.ReadAllText(File_))!["sources"]);
    }

    [Fact]
    public void SixtyFourNamesOf64Characters_AreKept_ACharacterOutsideTheBasicPlaneCountingOnce()
    {
        var names = Enumerable.Range(0, 64).Select(i => i.ToString("D2") + new string('x', 62)).ToList();
        names[^1] = string.Concat(Enumerable.Repeat("😀", 64));
        Assert.Equal(128, names[^1].Length);
        var change = ChangeOf(JsonSerializer.Serialize(names));

        Assert.True(SchemaTakes(change));
        Assert.Empty(SettingsStore.CheckChange(change, SpecLimits.Default));
        var store = Open();
        store.Apply(change);

        Assert.Equal(names, store.Current.Settings.SystemInfo.ExcludedProcesses);
    }

    public static TheoryData<string, string> BadLists => new()
    {
        { JsonSerializer.Serialize(Enumerable.Range(0, 65).Select(i => $"p{i}")), "/sources/systemInfo/excludedProcesses" },
        { JsonSerializer.Serialize(new[] { new string('x', 65) }), "/sources/systemInfo/excludedProcesses/0" },
        { JsonSerializer.Serialize(new[] { "ok", string.Concat(Enumerable.Repeat("😀", 65)) }), "/sources/systemInfo/excludedProcesses/1" },
        { """["ok",""]""", "/sources/systemInfo/excludedProcesses/1" },
        { "[1]", "/sources/systemInfo/excludedProcesses/0" },
        { "[null]", "/sources/systemInfo/excludedProcesses/0" },
        { "\"InfoPanel\"", "/sources/systemInfo/excludedProcesses" },
        { """{"names":[]}""", "/sources/systemInfo/excludedProcesses" },
    };

    [Theory]
    [MemberData(nameof(BadLists))]
    public void AListThatIsntOne_IsRefusedWithAProblemAtItsPath_AsTheSchemaRefusesIt_AndNothingIsStored(string excluded, string path)
    {
        var store = Open();
        var change = ChangeOf(excluded);

        var refused = Assert.Throws<SettingsChangeException>(() => store.Apply(change));

        Assert.Equal([(path, "schema")], refused.Errors.Select(e => (e.Path, e.Code)));
        Assert.All(refused.Errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
        Assert.False(SchemaTakes(change));
        Assert.False(File.Exists(File_));
        Assert.Equal(Nine, store.Current.Settings.SystemInfo.ExcludedProcesses);
    }

    [Theory]
    [InlineData("""{"sources":{"systemInfo":{"excluded":[]}}}""", "/sources/systemInfo/excluded")]
    [InlineData("""{"sources":{"systemInfo":null}}""", "/sources/systemInfo")]
    [InlineData("""{"sources":{"systemInfo":[]}}""", "/sources/systemInfo")]
    public void AnythingElseUnderSystemInfo_IsRefused(string change, string path)
    {
        Assert.Equal([(path, "schema")], SettingsStore.CheckChange(Change(change), SpecLimits.Default).Select(e => (e.Path, e.Code)));
        Assert.False(SchemaTakes(Change(change)));
    }

    public static TheoryData<string> BadStoredLists => new()
    {
        """["ok",""]""",
        """["ok",1]""",
        "\"InfoPanel\"",
        JsonSerializer.Serialize(new[] { new string('x', 65) }),
        JsonSerializer.Serialize(Enumerable.Range(0, 65).Select(i => $"p{i}")),
    };

    [Theory]
    [MemberData(nameof(BadStoredLists))]
    public void AStoredListThatIsntOne_GetsTheDefault_AndIsDroppedByTheNextChange(string excluded)
    {
        File.WriteAllText(File_, """{"format":"infopanel.settings/1","sources":{"systemInfo":{"excludedProcesses":""" + excluded + "}}}");

        var store = Open();
        Assert.Equal(Nine, store.Current.Settings.SystemInfo.ExcludedProcesses);
        store.Apply(Change("""{"frameRate":20}"""));

        Assert.Null(Stored());
        Assert.Equal(20, JsonNode.Parse(File.ReadAllText(File_))!["frameRate"]!.GetValue<int>());
    }

    [Fact]
    public void TheStoresLimits_AreTheSchemas()
    {
        var defs = JsonNode.Parse(SpecSchemas.Files["api.schema.json"])!["$defs"]!;
        var setting = defs["excludedProcesses"]!;
        var change = defs["settingsChange"]!["properties"]!["sources"]!["properties"]!["systemInfo"]!["properties"]!["excludedProcesses"]!;

        Assert.Equal((64, 64), (SystemInfoSettings.MaxNames, SystemInfoSettings.NameLength));
        foreach (var list in new[] { setting, change })
        {
            Assert.Equal(SystemInfoSettings.MaxNames, list["maxItems"]!.GetValue<int>());
            Assert.Equal(SystemInfoSettings.NameLength, list["items"]!["maxLength"]!.GetValue<int>());
            Assert.Equal(1, list["items"]!["minLength"]!.GetValue<int>());
        }
    }
}
