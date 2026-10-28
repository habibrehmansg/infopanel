using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Editing;
using InfoPanel.Import;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Import.ImportKit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// A 1.x data folder read in place: the migration's state and run-once record, the profile list, <c>settings.xml</c>,
/// each profile's envelope, the 1.2.x rule, and the folder never written. Over the fixture <c>Fixtures/v1-folder/</c>
/// (hand-written from 1.4.x's models), copied per test so variants can change it.
/// </summary>
public sealed class V1FolderTests : IDisposable
{
    private const string Desk = "6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7", Usb = "0a1b2c3d-4e5f-4061-8273-94a5b6c7d8e9",
        Huge = "1c2d3e4f-5a6b-4c7d-8e9f-a0b1c2d3e4f5", Empty = "2d3e4f5a-6b7c-4d8e-9fa0-b1c2d3e4f5a6";

    private readonly ImportKit _kit = new();
    private readonly string _root;

    public V1FolderTests()
    {
        _root = _kit.Temp.Combine("v1");
        Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder"), _root);
    }

    public void Dispose() => _kit.Dispose();

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    private V1Folder Folder(ImportLimits? limits = null) => new(_root, limits);

    private void Write(string relative, string text) => File.WriteAllText(Path.Combine(_root, relative), text);

    private static string SettingsWith(string inner) =>
        $"""<?xml version="1.0" encoding="utf-8"?><Settings xmlns:xsi="{Xsi}">{inner}</Settings>""";

    /// <summary>Every file and folder under <paramref name="root"/>: names, kinds, bytes and write times.</summary>
    private static string TreeHash(string root)
    {
        var text = new StringBuilder();
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(entry);
            text.Append(Path.GetRelativePath(root, entry)).Append('|').Append(info.Attributes).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('|');
            if (File.Exists(entry))
                text.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry))));
            text.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    // ---- state and record ---------------------------------------------------------------------------------------

    [Fact]
    public void State_IsNone_WithoutProfilesOrSettings()
    {
        var empty = _kit.Temp.Combine("no-1x");
        Directory.CreateDirectory(Path.Combine(empty, "assets"));

        var state = new V1Folder(empty).GetState(_kit.Data);

        Assert.Equal(("none", Path.GetFullPath(empty)), (state.State, state.Folder));
        Assert.Null(state.Profiles);
        Assert.Null(state.Settings);
        Assert.False(new V1Folder(_kit.Temp.Combine("not-there")).HasData);
    }

    [Fact]
    public void State_IsPending_WithEitherFile()
    {
        var onlySettings = _kit.Temp.Combine("settings-only");
        Directory.CreateDirectory(onlySettings);
        File.WriteAllText(Path.Combine(onlySettings, "SETTINGS.XML"), SettingsWith("<Version>131</Version>"));
        Assert.Equal("pending", new V1Folder(onlySettings).GetState(_kit.Data).State);

        Assert.Equal("pending", Folder().GetState(_kit.Data).State);
    }

    [Fact]
    public void State_ListsTheProfilesIn1xOrder_WithTheirSummaries()
    {
        // 1.4.x pin 8: 1.x list order is kept.
        var state = Folder().GetState(_kit.Data);

        Assert.Equal([Desk, Usb, Huge, Usb, Empty], state.Profiles!.Select(p => p.Id));
        Assert.Equal(new V1ProfileSummary { Id = Desk, Name = "Desk Overlay", Width = 800, Height = 480, Active = true }, state.Profiles![0]);
        Assert.Equal(("Huge Wall", 20_000, true), (state.Profiles![2].Name, state.Profiles[2].Width, state.Profiles[2].Active));
        var duplicate = Assert.Single(state.Diagnostics!, d => d.Code == "import.profile.duplicate");
        Assert.Equal(("warning", "/profiles/3"), (duplicate.Severity, duplicate.Path));
        Assert.DoesNotContain(state.Diagnostics!, d => d.Severity == "error");
    }

    [Fact]
    public void Record_GoesFromPendingToDone_AndSurvivesARestart()
    {
        var folder = Folder();
        Assert.Equal("pending", folder.GetState(_kit.Data).State);

        var now = new DateTimeOffset(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);
        var done = folder.WriteRecord(_kit.Data,
            [
                new V1MigrationOutcome { V1Id = Desk, Id = Desk, Outcome = "imported" },
                new V1MigrationOutcome { V1Id = Usb, Id = null, Outcome = "failed", Reason = "the client's translation failed" },
                new V1MigrationOutcome { V1Id = Huge, Id = null, Outcome = "skipped" },
            ], "imported", now);

        Assert.Equal("done", done.State);
        Assert.Equal(("2026-10-28T09:30:00Z", folder.Root, "imported"), (done.Record!.Completed, done.Record.Folder, done.Record.Settings));
        Assert.Equal(["imported", "failed", "skipped"], done.Record.Profiles.Select(p => p.Outcome));

        // A new engine on the same data folder reads it back.
        var again = new V1Folder(_root).GetState(new DataPaths(_kit.Data.Root));
        Assert.Equal("done", again.State);
        Assert.Equal(JsonSerializer.Serialize(done.Record, SpecJson.Options), JsonSerializer.Serialize(again.Record, SpecJson.Options));
        // The record writes a null id as null.
        Assert.Contains("\"id\":null", File.ReadAllText(V1Folder.RecordFile(_kit.Data)));
        // The folder's contents are still there for "Import from 1.x" later.
        Assert.Equal(5, again.Profiles!.Count);
    }

    [Fact]
    public void Record_Unreadable_CountsAsDone_WithAWarning()
    {
        Directory.CreateDirectory(_kit.Data.Root);
        File.WriteAllText(V1Folder.RecordFile(_kit.Data), "{ not json");

        var state = Folder().GetState(_kit.Data);

        Assert.Equal("done", state.State);
        Assert.Null(state.Record);
        var warning = Assert.Single(state.Diagnostics!, d => d.Code == "import.record");
        Assert.Equal(("warning", "/record"), (warning.Severity, warning.Path));

        // Also without 1.x data: migration never runs on its own twice.
        Assert.Equal("done", new V1Folder(_kit.Temp.Combine("gone")).GetState(_kit.Data).State);
    }

    [Fact]
    public void Record_OutsideItsRules_IsRefused_AndNothingIsWritten()
    {
        var folder = Folder();
        var now = DateTimeOffset.UnixEpoch;
        ImportRefusal Refused(IReadOnlyList<V1MigrationOutcome> outcomes, string settings = "skipped") =>
            Assert.Throws<ImportRefusal>(() => folder.WriteRecord(_kit.Data, outcomes, settings, now));
        V1MigrationOutcome Ok(int i) => new() { V1Id = new Guid(i, 0, 0, new byte[8]).ToString(), Id = null, Outcome = "skipped" };

        Assert.Equal("limit.recordOutcomes", Refused(Enumerable.Range(0, 1001).Select(Ok).ToList()).Code);
        Assert.Equal("record.reason", Refused([Ok(1) with { Reason = new string('x', 257) }]).Code);
        var small = new V1Folder(_root, new ImportLimits { RecordBytes = 4096 });
        Assert.Equal("limit.recordBytes", Assert.Throws<ImportRefusal>(() =>
            small.WriteRecord(_kit.Data, Enumerable.Range(0, 20).Select(i => Ok(i) with { Reason = new string('x', 200) }).ToList(), "skipped", now)).Code);
        Assert.Equal("record.outcome", Refused([Ok(1) with { Outcome = "done" }]).Code);
        Assert.Equal("record.v1Id", Refused([Ok(1) with { V1Id = Desk.ToUpperInvariant() }]).Code);
        Assert.Equal("record.id", Refused([Ok(1) with { Id = "not-an-id" }]).Code);
        Assert.Equal("record.settings", Refused([Ok(1)], settings: "partly").Code);
        Assert.False(File.Exists(V1Folder.RecordFile(_kit.Data)));

        // 1,000 outcomes fit, each with both ids and the longest reason.
        var most = Enumerable.Range(0, 1000).Select(i => Ok(i) with { Id = new Guid(i, 1, 1, new byte[8]).ToString(), Outcome = "imported", Reason = new string('r', 256) }).ToList();
        Assert.Equal("done", folder.WriteRecord(_kit.Data, most, "skipped", now).State);
    }

    [Fact]
    public void Overlap_TheDataFolderMustStayOutOfThe1xData()
    {
        var v1 = _kit.Temp.Combine("InfoPanel");

        Assert.Null(V1Folder.FindOverlap(v1, Path.Combine(v1, "v2")));
        Assert.Null(V1Folder.FindOverlap(v1, _kit.Temp.Combine("elsewhere")));
        Assert.NotNull(V1Folder.FindOverlap(v1, v1));
        Assert.NotNull(V1Folder.FindOverlap(v1, _kit.Temp.Path));
        Assert.NotNull(V1Folder.FindOverlap(v1, Path.Combine(v1, "profiles", "x")));
        Assert.NotNull(V1Folder.FindOverlap(v1, Path.Combine(v1, "assets")));
    }

    [Fact]
    public void Overlap_IsCheckedOnRealPathsToo()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs privileges on Windows.");
        var v1 = _kit.Temp.Combine("InfoPanel");
        Directory.CreateDirectory(Path.Combine(v1, "assets"));

        // A data folder that is a link into the 1.x assets folder.
        var intoAssets = _kit.Temp.Combine("data-link");
        Directory.CreateSymbolicLink(intoAssets, Path.Combine(v1, "assets"));

        // A 1.x folder that is a link to a folder inside the data folder.
        var data = _kit.Temp.Combine("data");
        Directory.CreateDirectory(Path.Combine(data, "inner"));
        var v1Link = _kit.Temp.Combine("v1-link");
        Directory.CreateSymbolicLink(v1Link, Path.Combine(data, "inner"));

        // The default layout with the 1.x folder itself a link (a relocated AppData folder) stays allowed.
        var relocated = _kit.Temp.Combine("InfoPanel-relocated");
        Directory.CreateSymbolicLink(relocated, v1);

        // Refused: the link into assets (and a folder under it that doesn't exist yet), the linked 1.x folder; allowed: the default layout.
        Assert.Equal(
            (true, true, true, false),
            (V1Folder.FindOverlap(v1, intoAssets) is not null, V1Folder.FindOverlap(v1, Path.Combine(intoAssets, "not-yet")) is not null,
                V1Folder.FindOverlap(v1Link, data) is not null, V1Folder.FindOverlap(relocated, Path.Combine(relocated, "v2")) is not null));
    }

    // ---- settings -----------------------------------------------------------------------------------------------

    [Fact]
    public void Settings_UserDataIsReadAsStored()
    {
        var settings = Folder().GetState(_kit.Data).Settings!;

        Assert.Equal((131, 30, 500), (settings.Version, settings.TargetFrameRate, settings.TargetGraphUpdateRate));
        Assert.Equal(new V1LibreSettings { Enabled = false, Storage = false, StorageInterval = 60 }, settings.Libre);
        Assert.Equal(new V1ProgramSpecificSettings { Enabled = true, HideOthers = false }, settings.ProgramSpecific);
        Assert.Equal(["beada", "beada", "turing", "turing", "lianLi", "lianLi", "thermalright", "thermalright", "thermaltake", "thermaltake", "jl", "jl", "jonsbo", "jonsbo", "vmax", "vmax"],
            settings.Devices.Select(d => d.Family));
        Assert.All(settings.Devices.Where((_, i) => i % 2 == 0), d => Assert.Equal(Desk, d.Profile));
        Assert.All(settings.Devices.Where((_, i) => i % 2 == 1), d => Assert.Null(d.Profile));
        var beada = settings.Devices[0];
        Assert.Equal(("USB\\VID_0000&PID_0000\\BEADAPANEL0", "80", "Beada5", "true"), (beada.Fields["DeviceId"], beada.Fields["Brightness"], beada.Fields["Model"], beada.Fields["Enabled"]));
        Assert.Equal("Port_#0002.Hub_#0001", settings.Devices[^1].Fields["DeviceLocation"]);
        Assert.Equal(["turing", "vmax"], settings.MultiDevice.Where(m => m.Value).Select(m => m.Key));
        Assert.Equal(8, settings.MultiDevice.Count);
        Assert.Equal(
            [
                new V1Hotkey { Modifiers = "Control Alt", Key = "F1", DeviceType = "Beada", DeviceId = "USB\\VID_0000&PID_0000\\BEADAPANEL0", DeviceLocation = "Port_#0001.Hub_#0001", Profile = Usb },
                new V1Hotkey { Modifiers = "Shift", Key = "F2", DeviceType = "Turing", DeviceId = "USB\\VID_0001&PID_0000\\TURINGPANEL0", DeviceLocation = "Port_#0001.Hub_#0001", Profile = Desk },
            ],
            settings.Hotkeys);

        // Theme, UI scale, window size and pane state are left out.
        var json = JsonSerializer.Serialize(settings, SpecJson.Options);
        Assert.DoesNotContain("theme", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uiScale", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_AbsentElements_Give14xDefaults()
    {
        // 1.4.x pin 6: Settings.cs's defaults.
        Write("settings.xml", SettingsWith("<Version>131</Version>"));

        var settings = Folder().GetState(_kit.Data).Settings!;

        Assert.Equal((131, 15, 1000), (settings.Version, settings.TargetFrameRate, settings.TargetGraphUpdateRate));
        Assert.Equal(new V1LibreSettings { Enabled = true, Storage = true, StorageInterval = 30 }, settings.Libre);
        Assert.Equal(new V1ProgramSpecificSettings { Enabled = false, HideOthers = true }, settings.ProgramSpecific);
        Assert.Empty(settings.Devices);
        Assert.Empty(settings.Hotkeys);
        Assert.All(settings.MultiDevice.Values, Assert.False);
    }

    [Fact]
    public void Settings_FallBackToTheBackup_As14xDoes()
    {
        // 1.4.x pin 5: ConfigModel.LoadSettings reads settings.xml.bak when settings.xml can't be read.
        Write("settings.xml", "<Settings><Version>131");

        var state = Folder().GetState(_kit.Data);

        Assert.Equal(20, state.Settings!.TargetFrameRate);
        var backup = Assert.Single(state.Diagnostics!, d => d.Code == "import.settings.backup");
        Assert.Equal(("info", "settings.xml.bak"), (backup.Severity, backup.File));

        File.Delete(Path.Combine(_root, "settings.xml"));
        Assert.Equal(20, Folder().GetState(_kit.Data).Settings!.TargetFrameRate);
    }

    [Fact]
    public void Settings_NeitherReadable_IsImportSettings()
    {
        Write("settings.xml", "not xml");
        Write("settings.xml.bak", "<NotSettings />");

        var state = Folder().GetState(_kit.Data);

        Assert.Null(state.Settings);
        var error = Assert.Single(state.Diagnostics!, d => d.Code == "import.settings");
        Assert.Equal("error", error.Severity);
        // The profiles are still listed and read: a damaged settings file says nothing about which 1.x wrote the data.
        Assert.Equal(5, state.Profiles!.Count);
        Assert.NotNull(Folder().ReadProfile(Guid.Parse(Huge), _kit.Context()));
    }

    [Fact]
    public void Settings_OverFourMiB_IsUnreadable()
    {
        File.Delete(Path.Combine(_root, "settings.xml.bak"));
        Write("settings.xml", SettingsWith("<Version>131</Version>" + new string(' ', 4 * 1024 * 1024)));

        var state = Folder().GetState(_kit.Data);

        Assert.Contains("larger than 4194304 bytes", Assert.Single(state.Diagnostics!, d => d.Code == "import.settings").Message);
    }

    [Fact]
    public void Setting_ValuesThatDontParse_GetTheDefault_AndLimitsLeaveTheRestOut()
    {
        var longValue = new string('n', 1025);
        Write("settings.xml", SettingsWith(
            "<Version>131</Version>" +
            "<TargetFrameRate>fast</TargetFrameRate>\n" +
            "<LibreHardwareMonitor>maybe</LibreHardwareMonitor>\n" +
            "<BeadaPanelDevices>\n" +
            $"<BeadaPanelDevice><DeviceId>a</DeviceId><Notes>{longValue}</Notes><Model>m</Model><Brightness>1</Brightness></BeadaPanelDevice>\n" +
            "<BeadaPanelDevice><DeviceId>b</DeviceId></BeadaPanelDevice>\n" +
            "<BeadaPanelDevice><DeviceId>c</DeviceId></BeadaPanelDevice>\n" +
            "</BeadaPanelDevices>\n" +
            "<HotkeyBindings><HotkeyBinding><Key>F1</Key></HotkeyBinding><HotkeyBinding><Key>F2</Key></HotkeyBinding></HotkeyBindings>"));

        var state = Folder(new ImportLimits { DevicesPerFamily = 2, FieldsPerDevice = 2, Hotkeys = 1 }).GetState(_kit.Data);
        var settings = state.Settings!;

        Assert.Equal((15, true), (settings.TargetFrameRate, settings.Libre.Enabled));
        Assert.Equal(["a", "b"], settings.Devices.Select(d => d.Fields["DeviceId"]));
        Assert.Equal(["DeviceId", "Model"], settings.Devices[0].Fields.Keys);
        Assert.Equal(["F1"], settings.Hotkeys.Select(h => h.Key));
        var warnings = state.Diagnostics!.Where(d => d.Code == "import.setting").ToList();
        // A value that doesn't parse (two), a value too long, too many fields, too many devices, too many hotkeys.
        Assert.Equal(["/settings/devices", "/settings/devices", "/settings/devices", "/settings/hotkeys", "/settings/libre/enabled", "/settings/targetFrameRate"],
            warnings.Select(w => w.Path).Order(StringComparer.Ordinal));
        Assert.All(warnings, w => Assert.Equal(("warning", "settings.xml"), (w.Severity, w.File)));
        Assert.All(warnings, w => Assert.NotNull(w.Line));
    }

    // ---- the 1.2.x rule -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("<Version>114</Version>", 114)]
    [InlineData("<Version>100</Version>", 100)]
    [InlineData("<TargetFrameRate>15</TargetFrameRate>", 114)]
    public void A11xFolder_IsReported_AndNothingOfItIsRead(string settings, int version)
    {
        // 1.1.x data: settings with a Version of 114 or lower, or none (1.4.x's default, 114, made it run its 1.1.4 upgrade).
        Write("settings.xml", SettingsWith(settings));
        var folder = Folder();

        var state = folder.GetState(_kit.Data);

        Assert.Equal("pending", state.State);
        var error = Assert.Single(state.Diagnostics!, d => d.Code == "import.version");
        Assert.Equal(("error", ""), (error.Severity, error.Path));
        Assert.Contains($"settings version {version}", error.Message);
        Assert.Null(state.Settings);
        Assert.Equal(5, state.Profiles!.Count);

        // A 1.1.x profile: refused, and nothing of it is stored.
        var refusal = Assert.Throws<ImportRefusal>(() => folder.ReadProfile(Guid.Parse(Desk), _kit.Context()));
        Assert.Equal("import.version", refusal.Code);
        Assert.Equal(0, _kit.BlobCount);
    }

    [Theory]
    [InlineData("<Version>115</Version>")]
    [InlineData("<Version>131</Version>")]
    [InlineData("<Version>soon</Version>")]
    public void A12xOrLaterFolder_Imports(string settings)
    {
        // 1.2.x writes 115 on its first start (after its 1.1.4 upgrade), 1.3.1 and later 131. A version that doesn't
        // parse says nothing either way: the data is read, with an import.setting warning.
        Write("settings.xml", SettingsWith(settings));

        var state = Folder().GetState(_kit.Data);

        Assert.DoesNotContain(state.Diagnostics!, d => d.Code == "import.version");
        Assert.NotNull(state.Settings);
        Assert.NotNull(Folder().ReadProfile(Guid.Parse(Desk), _kit.Context()));
    }

    [Fact]
    public void AFolderWithoutSettings_Imports()
    {
        // No settings file to tell the version by: 1.2.x and later always write one on their first start, but its
        // absence alone isn't taken as 1.1.x data.
        File.Delete(Path.Combine(_root, "settings.xml"));
        File.Delete(Path.Combine(_root, "settings.xml.bak"));

        var state = Folder().GetState(_kit.Data);

        Assert.Null(state.Settings);
        Assert.DoesNotContain(state.Diagnostics!, d => d.Code is "import.version" or "import.settings");
        Assert.NotNull(Folder().ReadProfile(Guid.Parse(Desk), _kit.Context()));
    }

    // ---- profiles -----------------------------------------------------------------------------------------------

    [Fact]
    public void Profiles_Unreadable_ListsNone()
    {
        Write("profiles.xml", "<ArrayOfProfile><Profile>");

        var state = Folder().GetState(_kit.Data);

        Assert.Empty(state.Profiles!);
        Assert.Equal("error", Assert.Single(state.Diagnostics!, d => d.Code == "import.profiles").Severity);
        Assert.Null(Folder().ReadProfile(Guid.Parse(Desk), _kit.Context()));
    }

    [Fact]
    public void Profiles_ANilProfile_IsLeftOut_WithAnInfo()
    {
        // The fixture's first two profiles with an xsi:nil one between them.
        var text = File.ReadAllText(Path.Combine(_root, "profiles.xml"));
        var firstEnd = text.IndexOf("</Profile>", StringComparison.Ordinal) + "</Profile>".Length;
        var secondEnd = text.IndexOf("</Profile>", firstEnd, StringComparison.Ordinal) + "</Profile>".Length;
        Write("profiles.xml", text[..firstEnd] + "\n  <Profile xsi:nil=\"true\" />" + text[firstEnd..secondEnd] + "\n</ArrayOfProfile>");

        var state = Folder().GetState(_kit.Data);

        Assert.Equal([Desk, Usb], state.Profiles!.Select(p => p.Id));
        var nil = Assert.Single(state.Diagnostics!, d => d.Code == "import.nil");
        Assert.Equal(("info", "/profiles", "profiles.xml"), (nil.Severity, nil.Path, nil.File!));
        Assert.NotNull(Folder().ReadProfile(Guid.Parse(Desk), _kit.Context()));
        Assert.NotNull(Folder().ReadProfile(Guid.Parse(Usb), _kit.Context()));
    }

    [Fact]
    public void Profiles_OverTheLimits_AreLeftOut()
    {
        var state = Folder(new ImportLimits { Profiles = 2 }).GetState(_kit.Data);
        Assert.Equal([Desk, Usb], state.Profiles!.Select(p => p.Id));
        Assert.Equal("warning", Assert.Single(state.Diagnostics!, d => d.Code == "import.profiles").Severity);
        Assert.Null(Folder(new ImportLimits { Profiles = 2 }).ReadProfile(Guid.Parse(Huge), _kit.Context()));

        var tooLarge = Folder(new ImportLimits { ProfileXmlBytes = 1024 }).GetState(_kit.Data);
        Assert.Contains("larger than 1024 bytes", Assert.Single(tooLarge.Diagnostics!, d => d.Code == "import.profiles").Message);
    }

    [Fact]
    public void Profiles_ADtd_IsRefused()
    {
        Write("profiles.xml", "<!DOCTYPE ArrayOfProfile [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><ArrayOfProfile><Profile><Name>&x;</Name></Profile></ArrayOfProfile>");

        var state = Folder().GetState(_kit.Data);

        Assert.Contains("DTD", Assert.Single(state.Diagnostics!, d => d.Code == "import.profiles").Message);
        Assert.Empty(state.Profiles!);
    }

    [Fact]
    public void ReadProfile_AllKinds_WithItsAssets()
    {
        var envelope = Folder().ReadProfile(Guid.Parse(Desk), _kit.Context())!;

        Assert.Equal(("folder", Desk), (envelope.Source, envelope.Id));
        Assert.Equal((true, "game.exe, another.exe"), ((bool)envelope.Profile["active"]!, (string)envelope.Profile["triggerProcessNames"]!));
        Assert.Equal(2560, envelope.TargetWindow!.Width);

        var today = ProfileSerializerTests.ReadFixtureItems();
        var profile = ProfileSerializerTests.ReadFixtureProfiles()[0];
        foreach (var item in today)
            item.SetProfile(profile);
        Assert.Equal(WithoutIds(today.Select(i => ItemJson.ToNode(i, _kit.Sensors)).ToList()).ToJsonString(), WithoutIds(envelope.Items).ToJsonString());

        // 1.4.x pin 9: relative paths resolve against assets/{guid}/, names ignoring case (LOGO.PNG for logo.png).
        Assert.Equal(["icon.svg", "anim.gif", "logo.png", "g0.png", "g1.png", "g2.png"], envelope.Assets.Select(a => a.Path));
        Assert.Equal(BlobStore.IdOf(Asset("logo.png")), envelope.Assets[2].Blob!.Id);
        Assert.All(envelope.Assets.Take(5), a => Assert.NotNull(a.Blob));
        Assert.Equal("import.asset.missing", envelope.Assets[5].Code);
        Assert.Equal("assets/6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7/g2.png", Single(envelope, "import.asset.missing").File);

        // 1.x holds autosaved changes for it: reported; the saved version is read.
        var autosave = Single(envelope, "import.autosave");
        Assert.Equal(("warning", $"autosave/profiles/{Desk}.xml"), (autosave.Severity, autosave.File));
    }

    [Fact]
    public void ReadProfile_ReportsWhatItFound()
    {
        // The items file is named in upper case: found ignoring case.
        var envelope = Folder().ReadProfile(Guid.Parse(Usb), _kit.Context())!;

        Assert.Equal("USB Panel", (string)envelope.Profile["name"]!);
        Assert.Equal(["Title", "Banner", "Clip", "Bitmap", "Scripted", "Wallpaper", "Sneaky", "Fancy"], envelope.Items.Select(i => (string)i.Props["name"]!));
        Assert.Equal("Centre", Assert.Single(envelope.Guides).Name);
        Assert.Equal([("/profile", "profiles.xml"), ("/items/1", $"profiles/{Usb}.xml")], With(envelope, "import.element").Select(d => (d.Path, d.File!)));
        Assert.Equal("/items", Single(envelope, "import.itemType").Path);
        Assert.Equal("/items", Single(envelope, "import.nil").Path);
        Assert.Equal("/items/7/props/font", Single(envelope, "import.font").Path);

        Assert.Equal(["banner.png", "clip.mp4", "old.bmp", "script.svg", "C:\\Users\\me\\Pictures\\wall.png", "..\\6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7\\g0.png"],
            envelope.Assets.Select(a => a.Path));
        Assert.Equal(BlobStore.IdOf(Asset("gauge-2.png")), envelope.Assets[0].Blob!.Id);
        Assert.Equal([null, "import.asset.media", "import.asset.media", "import.asset.media", "import.asset.absolute", "import.asset.path"], envelope.Assets.Select(a => a.Code));
        Assert.False(_kit.Stored(Asset("gauge-0.png")));
        Assert.False(_kit.Stored(Asset("gauge-3.png")));
        Assert.Equal(1, _kit.BlobCount);
        Assert.Empty(With(envelope, "import.autosave"));
    }

    [Fact]
    public void ReadProfile_SizeAndAMissingItemsFile()
    {
        var huge = Folder().ReadProfile(Guid.Parse(Huge), _kit.Context())!;
        Assert.Equal("/profile/width", Single(huge, "import.size").Path);

        // 1.4.x pin 2: a profile without an items file loads with no items.
        var empty = Folder().ReadProfile(Guid.Parse(Empty), _kit.Context())!;
        Assert.Empty(empty.Items);
        Assert.Equal(("info", "/items", $"profiles/{Empty}.xml"), (Single(empty, "import.items").Severity, Single(empty, "import.items").Path, Single(empty, "import.items").File!));

        Assert.Null(Folder().ReadProfile(Guid.NewGuid(), _kit.Context()));
    }

    [Fact]
    public void ReadProfile_UnreadableItems_ReadNone()
    {
        Write($"profiles/{Huge}.xml", "<ArrayOfDisplayItem><DisplayItem>");
        Assert.Equal("error", Single(Folder().ReadProfile(Guid.Parse(Huge), _kit.Context())!, "import.xml").Severity);

        Write($"profiles/{Huge}.xml", Items(Text("A")));
        var over = Folder().ReadProfile(Guid.Parse(Huge), _kit.Context(new ImportLimits { ItemsXmlBytes = 100 }))!;
        Assert.Contains("larger than 100 bytes", Single(over, "import.items").Message);
    }

    [Fact]
    public void EveryCode_TheFixtureGives_IsInTheCatalogue()
    {
        var folder = Folder();
        var codes = folder.GetState(_kit.Data).Diagnostics!.Select(d => d.Code)
            .Concat(new[] { Desk, Usb, Huge, Empty }.SelectMany(id => folder.ReadProfile(Guid.Parse(id), _kit.Context())!.Diagnostics.Select(d => d.Code)))
            .ToHashSet();

        Assert.Subset(V1CorpusTests.Catalogue, codes);
        Assert.Superset(new HashSet<string> { "import.profile.duplicate", "import.autosave", "import.items", "import.itemType", "import.nil", "import.element", "import.size",
            "import.font", "import.sensor", "import.asset.path", "import.asset.absolute", "import.asset.missing", "import.asset.media" }, codes);
    }

    [Fact]
    public void TheFolder_IsNeverWritten_ByAnyRead()
    {
        // The tree (names, kinds, bytes, write times) is the same after every read and the record's write.
        var before = TreeHash(_root);
        var folder = Folder();

        folder.GetState(_kit.Data);
        foreach (var id in new[] { Desk, Usb, Huge, Empty })
            folder.ReadProfile(Guid.Parse(id), _kit.Context());
        folder.WriteRecord(_kit.Data, [new V1MigrationOutcome { V1Id = Desk, Id = Desk, Outcome = "imported" }], "imported", DateTimeOffset.UnixEpoch);
        folder.GetState(_kit.Data);

        Assert.Equal(before, TreeHash(_root));
        Assert.False(File.Exists(Path.Combine(_root, V1Folder.RecordFileName)));
        Assert.True(File.Exists(V1Folder.RecordFile(_kit.Data)));

        // A 1.1.x folder too.
        Write("settings.xml", SettingsWith("<Version>114</Version>"));
        var v11 = TreeHash(_root);
        Assert.Throws<ImportRefusal>(() => Folder().ReadProfile(Guid.Parse(Desk), _kit.Context()));
        Folder().GetState(_kit.Data);
        Assert.Equal(v11, TreeHash(_root));
    }
}
