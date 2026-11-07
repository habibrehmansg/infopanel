using System.Text;
using InfoPanel.Panels;
using InfoPanel.Persistence;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Tests.Panels;

/// <summary><c>devices.json</c>: read, written atomically, members it doesn't know kept, and a file it can't hold left alone.</summary>
public sealed class PanelStoreTests : IDisposable
{
    private const string Id1 = "0f8e4a52-6c1b-4f38-9a57-2d1c3b4e5f60";
    private const string Id2 = "1a2b3c4d-5e6f-4a5b-8c7d-9e0f1a2b3c4d";

    private readonly string _folder = Directory.CreateTempSubdirectory("panels-store-").FullName;

    private string FilePath => Path.Combine(_folder, PanelStore.FileName);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    internal static (ILogger Logger, List<LogEvent> Events) Logged()
    {
        var sink = new Collecting();
        return (new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger(), sink.Events);
    }

    [Fact]
    public void An_absent_file_holds_no_panels_and_the_first_save_writes_it()
    {
        var store = PanelStore.Read(FilePath, Logged().Logger);
        Assert.Null(store.Problem);
        Assert.Empty(store.Devices);

        store.Save([new SavedPanel(Id1, "beadapanel", "8", "934072005c00481401044f2420751fd6", "0-1")]);

        Assert.Equal(
            "{\"format\":\"infopanel.devices/1\",\"devices\":[{\"id\":\"" + Id1 + "\",\"family\":\"beadapanel\",\"model\":\"8\","
            + "\"serial\":\"934072005c00481401044f2420751fd6\",\"port\":\"0-1\",\"enabled\":false,\"profileId\":null,\"rotation\":0,\"brightness\":100,\"encoding\":\"auto\"}]}",
            File.ReadAllText(FilePath));
        // Written by renaming a temp file over it: nothing else is left in the folder.
        Assert.Equal([PanelStore.FileName], Directory.GetFileSystemEntries(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void A_round_trip_keeps_every_setting_the_order_and_the_members_it_doesnt_know()
    {
        File.WriteAllText(FilePath, $$"""
            {"format": "infopanel.devices/1", "later": {"a": [1, 2]},
             "devices": [
               {"id": "{{Id2}}", "family": "beadapanel", "model": "6C", "serial": null, "port": "1-2.3", "enabled": true,
                "profileId": "p-1", "rotation": 270, "brightness": 40, "future": "x"},
               {"id": "{{Id1}}", "family": "turing", "model": null, "serial": "S1", "port": null, "enabled": false,
                "profileId": null, "rotation": 0, "brightness": 100}
             ]}
            """);

        var store = PanelStore.Read(FilePath, Logged().Logger);

        Assert.Null(store.Problem);
        Assert.Equal([Id2, Id1], store.Devices.Select(d => d.Id));
        var first = store.Devices[0];
        Assert.Equal(("6C", (string?)null, "1-2.3", true, "p-1", PanelRotation.Rotate270, 40),
            (first.Model, first.Serial, first.Port, first.Enabled, first.ProfileId, first.Rotation, first.Brightness));
        Assert.Equal("turing", store.Devices[1].Family);

        store.Save([store.Devices[0] with { Brightness = 41 }, store.Devices[1]]);
        var written = File.ReadAllText(FilePath);
        Assert.Contains("\"brightness\":41,\"encoding\":\"auto\",\"future\":\"x\"}", written);
        Assert.EndsWith("],\"later\":{\"a\":[1,2]}}", written);
        var again = PanelStore.Read(FilePath, Logged().Logger);
        Assert.Equal(41, again.Devices[0].Brightness);
        Assert.Equal("turing", again.Devices[1].Family);
    }

    [Fact]
    public void Missing_settings_take_the_defaults()
    {
        File.WriteAllText(FilePath, $$"""{"format":"infopanel.devices/1","devices":[{"id":"{{Id1}}","family":"beadapanel"}]}""");
        var device = Assert.Single(PanelStore.Read(FilePath, Logged().Logger).Devices);
        Assert.Equal((false, (string?)null, PanelRotation.None, 100), (device.Enabled, device.ProfileId, device.Rotation, device.Brightness));
    }

    public static TheoryData<string, string> Unheld => new()
    {
        { "not JSON", "{\"format\": \"infopanel.devices/1\", \"devices\": [" },
        { "a list", "[]" },
        { "no format", "{\"devices\": []}" },
        { "another format", "{\"format\": \"infopanel.plugin-state/1\", \"devices\": []}" },
        { "another major version", "{\"format\": \"infopanel.devices/2\", \"devices\": []}" },
        { "devices not a list", "{\"format\": \"infopanel.devices/1\", \"devices\": {}}" },
        { "a rotation of 45", $$"""{"format":"infopanel.devices/1","devices":[{"id":"{{Id1}}","family":"beadapanel","rotation":45}]}""" },
        { "a brightness of 101", $$"""{"format":"infopanel.devices/1","devices":[{"id":"{{Id1}}","family":"beadapanel","brightness":101}]}""" },
        { "enabled as text", $$"""{"format":"infopanel.devices/1","devices":[{"id":"{{Id1}}","family":"beadapanel","enabled":"yes"}]}""" },
        { "an id that isn't a UUID", """{"format":"infopanel.devices/1","devices":[{"id":"panel-1","family":"beadapanel"}]}""" },
        { "an upper-case id", $$"""{"format":"infopanel.devices/1","devices":[{"id":"{{Id1.ToUpperInvariant()}}","family":"beadapanel"}]}""" },
        { "no family", $$"""{"format":"infopanel.devices/1","devices":[{"id":"{{Id1}}"}]}""" },
        { "an id twice", $$"""{"format":"infopanel.devices/1","devices":[{"id":"{{Id1}}","family":"beadapanel"},{"id":"{{Id1}}","family":"beadapanel"}]}""" },
        { "format twice", "{\"format\": \"infopanel.devices/1\", \"format\": \"infopanel.devices/1\"}" },
        { "a format that isn't text (a lone surrogate)", """{"format":"\uD800","devices":[]}""" },
        { "a serial that isn't text (a lone surrogate)", $$"""{"format":"infopanel.devices/1","devices":[{"id":"{{Id1}}","family":"beadapanel","serial":"\uD800"}]}""" },
    };

    [Theory]
    [MemberData(nameof(Unheld))]
    public void A_file_it_cant_hold_whole_is_never_rewritten_and_turns_panels_off(string what, string text)
    {
        File.WriteAllText(FilePath, text);
        var before = File.ReadAllBytes(FilePath);
        var (logger, events) = Logged();

        var store = PanelStore.Read(FilePath, logger);

        Assert.True(store.Problem != null, what);
        Assert.Empty(store.Devices);
        var line = Assert.Single(events);
        Assert.Equal(LogEventLevel.Error, line.Level);
        Assert.Contains("USB panels are off this run", line.RenderMessage());
        Assert.Throws<InvalidOperationException>(() => store.Save([new SavedPanel(Id1, "beadapanel", "8", null, "0-1")]));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void A_file_over_its_cap_or_a_link_isnt_read()
    {
        File.WriteAllBytes(FilePath, Encoding.ASCII.GetBytes("{\"format\":\"infopanel.devices/1\",\"pad\":\"" + new string('x', PanelStore.MaxBytes) + "\"}"));
        Assert.Contains("larger", PanelStore.Read(FilePath, Logged().Logger).Problem);

        var target = Path.Combine(_folder, "elsewhere.json");
        File.WriteAllText(target, "{\"format\":\"infopanel.devices/1\",\"devices\":[]}");
        var link = Path.Combine(_folder, "link", PanelStore.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);
        Assert.Contains("link", PanelStore.Read(link, Logged().Logger).Problem);
    }

    [Fact]
    public void A_failed_write_changes_nothing()
    {
        var store = PanelStore.Read(FilePath, Logged().Logger);
        store.Writer = (_, _) => throw new IOException("disk full");
        Assert.Throws<IOException>(() => store.Save([new SavedPanel(Id1, "beadapanel", "8", null, "0-1")]));
        Assert.Empty(store.Devices);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void The_data_folder_knows_the_file_and_its_temp_files()
    {
        // So --fresh wipes a folder holding it, and the 1.x import's folder check counts it as the engine's.
        Assert.Contains("devices.json", DataPaths.EngineFiles);
        Assert.True(DataPaths.IsEngineEntry("devices.json"));
        Assert.True(DataPaths.IsEngineEntry("devices.json.0123456789abcdef0123456789abcdef.tmp"));
        Assert.Equal(Path.Combine(_folder, "devices.json"), new DataPaths(_folder).DevicesFile);
    }

    private sealed class Collecting : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            lock (Events)
                Events.Add(logEvent);
        }
    }
}
