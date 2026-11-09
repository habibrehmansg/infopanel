using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using InfoPanel.Models;
using InfoPanel.Output;
using InfoPanel.Plugins;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Drives;
using InfoPanel.Sensors.Network;
using InfoPanel.Sensors.SystemInfo;
using InfoPanel.Sensors.Volume;
using InfoPanel.Spec;
using Xunit;
using ManualClock = InfoPanel.Core.Tests.Editing.ManualTimeProvider;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>A simulated subscription's text and tables: made up too, none of this computer's own, but its date and time.</summary>
public class SimulatedTextTests
{
    private readonly FakeSensorReader _sensors = new();
    private readonly List<SensorInfo> _catalogue = [];
    private readonly ManualClock _clock = new();

    private (ISceneInputSource Live, ISceneInputSource Simulated) Inputs()
    {
        var live = new SensorSceneInputs(_sensors, new FakeSensorHistory());
        return (live, new SimulatedSceneInputs(live, _clock, catalogue: () => _catalogue));
    }

    /// <summary>A text sensor of a plugin's, listed under its display name.</summary>
    private SensorKey Text(string id, string name, string value, string source = "acme.tools")
    {
        var key = SensorKey.Plugin($"/{source}/{id}");
        _sensors.Readings[key] = new SensorReading(value);
        _catalogue.Add(new SensorInfo(key, source, "Group", name, ""));
        return key;
    }

    private static string Read(ISceneInputSource inputs, SensorKey key)
    {
        var reading = inputs.Read(key.ToString(), "text");
        Assert.Equal(SceneInputStatus.Live, reading.Status);
        return reading.Text;
    }

    [Fact]
    public void An_address_reading_shows_a_documentation_address_the_same_for_its_key_and_another_for_each_other_key()
    {
        var v4 = Text("en0/ipv4-0", "IPv4 Address", "192.168.1.23", NetworkSource.SourceId);
        var v4b = Text("en0/ipv4-1", "IPv4 Address", "10.20.30.40", NetworkSource.SourceId);
        var v6 = Text("en0/ipv6-0", "IPv6 Address", "fe80::1c2d:3e4f:5a6b:7c8d", NetworkSource.SourceId);
        var pub = Text("public/ipv4", "IPv4", "81.2.69.160", NetworkSource.SourceId);
        var named = Text("lan/local", "Local IP", "unknown yet");
        var shaped = Text("lan/gateway", "Gateway", "10.0.0.1");
        var mac = Text("lan/mac", "MAC Address", "3c:22:fb:12:34:56");
        var macShaped = Text("lan/hw", "Hardware", "3C-22-FB-AB-CD-EF");
        var (live, simulated) = Inputs();

        Assert.Equal("192.168.1.23", Read(live, v4));
        var v4s = new[] { v4, v4b, pub, named, shaped }.Select(k => Read(simulated, k)).ToList();
        Assert.All(v4s, a => Assert.True(IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && a.StartsWith("192.0.2.", StringComparison.Ordinal), a));
        Assert.Equal(v4s.Count, v4s.Distinct().Count());
        var v6s = Read(simulated, v6);
        Assert.True(IPAddress.TryParse(v6s, out var ip6) && ip6.AddressFamily == AddressFamily.InterNetworkV6 && v6s.StartsWith("2001:db8::", StringComparison.Ordinal), v6s);
        Assert.StartsWith("00:00:5E:00:53:", Read(simulated, mac), StringComparison.Ordinal);
        Assert.StartsWith("00:00:5E:00:53:", Read(simulated, macShaped), StringComparison.Ordinal);
        Assert.NotEqual(Read(simulated, mac), Read(simulated, macShaped));

        _clock.Advance(5000);
        Assert.Equal(v4s, new[] { v4, v4b, pub, named, shaped }.Select(k => Read(simulated, k)).ToList());
        Assert.Equal(v6s, Read(simulated, v6));
    }

    [Fact]
    public void A_host_or_computer_name_shows_MY_PC()
    {
        var host = Text("sys/host", "Computer Name", "SECRET-WORKSTATION");
        var hostname = Text("sys/hn", "Hostname", "secret-box.example.lan");
        var shaped = Text("sys/who", "Who", Environment.MachineName);
        var (live, simulated) = Inputs();

        Assert.Equal("SECRET-WORKSTATION", Read(live, host));
        Assert.Equal("MY-PC", Read(simulated, host));
        Assert.Equal("MY-PC", Read(simulated, hostname));
        Assert.Equal("MY-PC", Read(simulated, shaped));
    }

    [Fact]
    public void A_process_tables_names_come_from_the_list_and_its_numbers_are_made_up()
    {
        var key = SystemInfoSource.KeyOf("processes", "top-cpu-usage");
        var table = new DataTable();
        table.Columns.Add("Process Name", typeof(PluginText));
        table.Columns.Add("Usage", typeof(PluginSensor));
        table.Columns.Add("Memory", typeof(PluginSensor));
        string[] secret = ["SecretTool", "PrivateDaemon", "ClientBillingApp"];
        for (var i = 0; i < secret.Length; i++)
            table.Rows.Add(new PluginText("Process Name", secret[i]), new PluginSensor("Usage", 41.7f - i * 10, "%"), new PluginSensor("Memory", 1234.5f * (i + 1), " MB"));
        _sensors.Readings[key] = new SensorReading(table, SystemInfoSource.TableFormat, "SecretTool, 41.7%, 1234.5 MB");
        _catalogue.Add(new SensorInfo(key, SystemInfoSource.SourceId, SystemInfoSource.ProcessesGroup, "Top CPU Usage", ""));
        var app = Text("focus/app", "Foreground App", "ClientBillingApp");
        var (live, simulated) = Inputs();

        var real = live.Read(key.ToString(), "table");
        Assert.Equal("SecretTool", real.Table.Rows[0][0]);
        var made = simulated.Read(key.ToString(), "table");
        Assert.Equal(SceneInputStatus.Live, made.Status);
        Assert.Equal(real.Table.Columns, made.Table.Columns);
        Assert.Equal(secret.Length, made.Table.Rows.Count);
        Assert.All(made.Table.Rows, r => Assert.Contains(r[0], SimulatedText.Processes));
        Assert.Equal(made.Table.Rows.Count, made.Table.Rows.Select(r => r[0]).Distinct().Count());
        for (var row = 0; row < made.Table.Rows.Count; row++)
        {
            Assert.EndsWith("%", made.Table.Rows[row][1], StringComparison.Ordinal);
            Assert.EndsWith(" MB", made.Table.Rows[row][2], StringComparison.Ordinal);
            Assert.NotEqual(real.Table.Rows[row][1], made.Table.Rows[row][1]);
            Assert.NotEqual(real.Table.Rows[row][2], made.Table.Rows[row][2]);
        }
        Assert.DoesNotContain(secret, s => made.Text.Contains(s, StringComparison.Ordinal));
        Assert.StartsWith(made.Table.Rows[0][0], made.Text, StringComparison.Ordinal);
        Assert.Contains(Read(simulated, app), SimulatedText.Processes);
    }

    [Fact]
    public void Adapter_device_and_drive_names_show_generic_samples()
    {
        var adapter = Text("en0/name", "Name", "Secret Office Wi-Fi", NetworkSource.SourceId);
        var description = Text("en0/description", "Description", "Intel(R) Wi-Fi 6E AX211 160MHz", NetworkSource.SourceId);
        var label = Text("macintosh-hd/volume_label", "Volume Label", "Secret Projects HD", DriveSource.SourceId);
        var drive = Text("macintosh-hd/name", "Name", "/Volumes/Secret Projects HD", DriveSource.SourceId);
        var other = Text("backup/volume_label", "Volume Label", "Secret Backup", DriveSource.SourceId);
        var speaker = Text("default/friendly_name", "Name", "Secret's AirPods Pro", VolumeSource.SourceId);
        var plugin = Text("net/adapter", "Network Adapter", "Realtek PCIe 2.5GbE Family Controller");
        var (live, simulated) = Inputs();

        Assert.Equal("Secret Office Wi-Fi", Read(live, adapter));
        Assert.Contains(Read(simulated, adapter), SimulatedText.Adapters);
        Assert.Equal(Read(simulated, adapter), Read(simulated, description));
        Assert.Contains(Read(simulated, plugin), SimulatedText.Adapters);
        Assert.Equal("Disk 1", Read(simulated, label));
        Assert.Equal("Disk 1", Read(simulated, drive));
        Assert.Equal("Disk 2", Read(simulated, other));
        Assert.Contains(Read(simulated, speaker), SimulatedText.AudioDevices);
    }

    /// <summary>A table sensor of a plugin's: text columns named <paramref name="columns"/>, each row's cells as given.</summary>
    private SensorKey Table(string id, string name, string[] columns, params string[][] rows) => TableOf("acme.tools", id, name, columns, rows);

    /// <summary>A table sensor of <paramref name="source"/>'s: text columns named <paramref name="columns"/>, each row's cells as given.</summary>
    private SensorKey TableOf(string source, string id, string name, string[] columns, params string[][] rows)
    {
        var key = SensorKey.Plugin($"/{source}/{id}");
        var table = new DataTable();
        foreach (var column in columns)
            table.Columns.Add(column, typeof(string));
        foreach (var row in rows)
            table.Rows.Add(row);
        _sensors.Readings[key] = new SensorReading(table, "", string.Join(", ", rows[0]));
        _catalogue.Add(new SensorInfo(key, source, "Group", name, ""));
        return key;
    }

    /// <summary>A number sensor of <paramref name="source"/>'s, reading <paramref name="value"/> in <paramref name="unit"/>.</summary>
    private SensorKey Number(string id, string unit, double value = 42, string source = "acme.tools")
    {
        var key = SensorKey.Plugin($"/{source}/{id}");
        _sensors.Readings[key] = new SensorReading(value, value, value, value, unit);
        _catalogue.Add(new SensorInfo(key, source, "Group", id, unit));
        return key;
    }

    [Fact]
    public void Any_other_text_shows_a_label_the_engines_own_sources_display_name_else_Text()
    {
        var song = Text("music/now-playing", "Now Playing", "A Secret Song");
        var who = Text("me/user", "alice@example.com", "alice");
        var place = Text("place", "Place", "Secret Village", "infopanel.weather");
        var status = Text("en0/status", "Status", "Up", NetworkSource.SourceId);
        var unlisted = SensorKey.Plugin("/acme.tools/unlisted");
        _sensors.Readings[unlisted] = new SensorReading("Secret Unlisted");
        var (live, simulated) = Inputs();

        Assert.Equal("A Secret Song", Read(live, song));
        // A plugin's display name is its own, as private as its value: neither shows.
        Assert.Equal(SimulatedText.NoName, Read(simulated, song));
        Assert.Equal(SimulatedText.NoName, Read(simulated, who));
        Assert.Equal(SimulatedText.NoName, Read(simulated, unlisted));
        // The engine's own sources name their sensors themselves.
        Assert.Equal("Place", Read(simulated, place));
        Assert.Equal("Status", Read(simulated, status));
        // A key this computer has no text for reads as live: unavailable.
        Assert.Equal(SceneInputStatus.Unavailable, simulated.Read(SensorKey.Plugin("/acme.tools/none").ToString(), "text").Status);
    }

    [Fact]
    public void A_plugins_table_shows_generic_headings_and_none_of_its_cells_the_engines_own_keep_their_columns()
    {
        var key = Table("me/people", "People", ["alice@example.com", "Signed in"], ["alice", "Secret Corp"], ["bob", "Secret Ltd"]);
        var system = SystemInfoSource.KeyOf("processes", "top-cpu-usage");
        var processes = new DataTable();
        processes.Columns.Add("Process Name", typeof(string));
        processes.Columns.Add("Usage", typeof(string));
        processes.Rows.Add("SecretTool", "41.7%");
        _sensors.Readings[system] = new SensorReading(processes, SystemInfoSource.TableFormat, "SecretTool, 41.7%");
        _catalogue.Add(new SensorInfo(system, SystemInfoSource.SourceId, SystemInfoSource.ProcessesGroup, "Top CPU Usage", ""));
        var (live, simulated) = Inputs();

        Assert.Equal(["alice@example.com", "Signed in"], live.Read(key.ToString(), "table").Table.Columns);
        var made = simulated.Read(key.ToString(), "table");
        Assert.Equal(["Column 1", "Column 2"], made.Table.Columns);
        Assert.Equal(2, made.Table.Rows.Count);
        var cells = made.Table.Rows.SelectMany(r => r).Append(made.Text).ToList();
        Assert.DoesNotContain(cells, c => c.Contains("alice", StringComparison.OrdinalIgnoreCase) || c.Contains("bob", StringComparison.Ordinal)
            || c.Contains("Secret", StringComparison.Ordinal) || c.Contains("Signed", StringComparison.Ordinal));
        Assert.Equal(["Process Name", "Usage"], simulated.Read(system.ToString(), "table").Table.Columns);
    }

    [Fact]
    public void A_number_cell_of_a_table_the_engine_names_keeps_its_unit_only_when_it_is_a_known_one_in_the_lists_spelling()
    {
        var key = TableOf("infopanel.tools", "stats", "Stats", ["Item", "Value"], ["a", "42 alice"], ["b", "12.5%"], ["c", "512 mb"], ["d", "7 qzxjv"], ["e", "3.2 ghz"], ["f", "40 °c"]);
        var (live, simulated) = Inputs();

        Assert.Equal("42 alice", live.Read(key.ToString(), "table").Table.Rows[0][1]);
        var rows = simulated.Read(key.ToString(), "table").Table.Rows;
        Assert.DoesNotContain(rows.SelectMany(r => r), c => c.Contains("alice", StringComparison.Ordinal) || c.Contains("qzxjv", StringComparison.Ordinal));
        Assert.Matches(@"^\d+\.\d%$", rows[1][1]);
        Assert.Matches(@"^\d+ MB$", rows[2][1]);
        Assert.Matches(@"^\d+\.\d GHz$", rows[4][1]);
        Assert.Matches(@"^\d+ °C$", rows[5][1]);
        Assert.NotEqual("12.5%", rows[1][1]);
    }

    [Fact]
    public void A_plugins_table_cell_with_anything_after_its_number_is_text_a_number_alone_is_made_up()
    {
        var key = Table("me/stats", "Stats", ["Who", "Value"], ["a", "42 A"], ["b", "7 s"], ["c", "3 x"], ["d", "12.5%"], ["e", "512 MB"], ["f", "1234.5"]);
        var (live, simulated) = Inputs();

        Assert.Equal("42 A", live.Read(key.ToString(), "table").Table.Rows[0][1]);
        var rows = simulated.Read(key.ToString(), "table").Table.Rows;
        // The type of a plugin's cell is unknown, and a value's shape never makes it a number: "A" may be an initial.
        Assert.All(rows.Take(5), r => Assert.Equal("Column 2", r[1]));
        Assert.Matches(@"^\d+\.\d$", rows[5][1]);
        Assert.NotEqual("1234.5", rows[5][1]);
    }

    [Fact]
    public void A_number_reading_shows_a_listed_unit_in_the_lists_spelling_and_no_other_unit()
    {
        var mail = Number("me/score", "alice@example.com / 10.0.0.23");
        var initial = Number("me/initial", " q");
        var clock = Number("cpu/clock", " mhz");
        var temperature = Number("cpu/temperature", "°c", source: "infopanel.tools");
        var load = Number("cpu/load", "%");
        var none = Number("cpu/count", "");
        // Listed units a person's initials can spell: drawn empty for a source that writes its own names.
        var tesla = Number("me/field", "T");
        var millimetres = Number("me/rain", " mm");
        var inches = Number("me/height", " in");
        var ownTesla = Number("me/field", "t", source: "infopanel.tools");
        var ownMillimetres = Number("me/rain", " MM", source: "infopanel.tools");
        var ownInches = Number("me/height", " in", source: "infopanel.tools");
        var (live, simulated) = Inputs();

        string Unit(ISceneInputSource inputs, SensorKey key)
        {
            var reading = inputs.Read(key.ToString(), "number");
            Assert.Equal(SceneInputStatus.Live, reading.Status);
            return reading.Unit;
        }

        Assert.Equal("alice@example.com / 10.0.0.23", Unit(live, mail));
        Assert.Equal("", Unit(simulated, mail));
        Assert.Equal("", Unit(simulated, initial));
        Assert.Equal(" MHz", Unit(simulated, clock));
        Assert.Equal("°C", Unit(simulated, temperature));
        Assert.Equal("%", Unit(simulated, load));
        Assert.Equal("", Unit(simulated, none));
        Assert.Equal("T", Unit(live, tesla));
        Assert.Equal("", Unit(simulated, tesla));
        Assert.Equal("", Unit(simulated, millimetres));
        Assert.Equal("", Unit(simulated, inches));
        Assert.Equal("T", Unit(simulated, ownTesla));
        Assert.Equal(" mm", Unit(simulated, ownMillimetres));
        Assert.Equal(" in", Unit(simulated, ownInches));
    }

    [Fact]
    public void A_number_reading_whose_source_names_itself_draws_no_listed_unit_of_one_or_two_letters_the_engines_own_keep_every_listed_unit()
    {
        var plugin = SensorKey.Plugin("/acme.tools/me/score");
        var lookalike = SensorKey.Plugin("/acme.infopanel.tools/me/score");
        var own = SensorKey.Plugin("/infopanel.tools/cpu/clock");
        var hwinfo = SensorKey.HwInfo(0, 1, 0, 2);
        var libre = SensorKey.Libre("/amdcpu/0/power/0");

        // A unit a person's initials can spell is drawn empty for a source that writes its own names.
        foreach (var untrusted in new[] { plugin, lookalike })
        {
            Assert.Equal("", SimulatedText.Unit(untrusted, "T"));
            Assert.Equal("", SimulatedText.Unit(untrusted, " mm"));
            Assert.Equal("", SimulatedText.Unit(untrusted, " in"));
            Assert.Equal("", SimulatedText.Unit(untrusted, "MB"));
            Assert.Equal("", SimulatedText.Unit(untrusted, " W"));
            Assert.Equal("", SimulatedText.Unit(untrusted, "s"));
            // Three characters or more, or a symbol: kept, in the list's spelling.
            Assert.Equal(" MHz", SimulatedText.Unit(untrusted, " mhz"));
            Assert.Equal("%", SimulatedText.Unit(untrusted, "%"));
            Assert.Equal("°C", SimulatedText.Unit(untrusted, "°c"));
            Assert.Equal(" µs", SimulatedText.Unit(untrusted, " µs"));
            Assert.Equal(" GT/s", SimulatedText.Unit(untrusted, " gt/s"));
            Assert.Equal("", SimulatedText.Unit(untrusted, "q"));
        }

        // The engine's own sources and the hardware monitors it reads keep the whole list.
        foreach (var trusted in new[] { own, hwinfo, libre })
        {
            Assert.Equal("T", SimulatedText.Unit(trusted, "t"));
            Assert.Equal(" mm", SimulatedText.Unit(trusted, " MM"));
            Assert.Equal(" in", SimulatedText.Unit(trusted, " in"));
            Assert.Equal(" W", SimulatedText.Unit(trusted, " w"));
            Assert.Equal(" MHz", SimulatedText.Unit(trusted, " mhz"));
            Assert.Equal("", SimulatedText.Unit(trusted, "q"));
        }
    }

    [Fact]
    public void Only_the_engines_clock_stays_real_any_other_time_shows_a_made_up_one()
    {
        var hour = SensorKey.Plugin("/infopanel.clock/24-hour");
        _sensors.Readings[hour] = new SensorReading(14, 14, 14, 14, "");
        var code = Text("auth/code", "One-time code", "7391");
        var sunrise = Text("sky/sunrise", "Sunrise Time", "06:37");
        var time = Text("clock/time", "Time", "14:05:33");
        var date = Text("clock/date", "Date", "2026-11-09");
        var zone = Text("clock/zone", "Time Zone", "Secret City");
        var (live, simulated) = Inputs();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(14, simulated.Read(hour.ToString(), "number").Now);
            _clock.Advance(1000);
        }
        Assert.Equal(live.History(hour.ToString()), simulated.History(hour.ToString()));
        Assert.Equal("06:37", Read(live, sunrise));
        Assert.Equal("12:00", Read(simulated, sunrise));
        Assert.Equal("12:00:00", Read(simulated, time));
        Assert.Equal(SimulatedText.NoName, Read(simulated, code));
        Assert.Equal(SimulatedText.NoName, Read(simulated, date));
        Assert.Equal(SimulatedText.NoName, Read(simulated, zone));
    }

    [Fact]
    public void The_uptime_starts_from_a_made_up_point_and_moves_as_time_does()
    {
        var real = SystemInfoSource.UptimeTexts(new TimeSpan(3, 4, 5, 6));
        var formatted = Text("uptime/formatted", "Formatted", real.Formatted, SystemInfoSource.SourceId);
        var days = Text("uptime/days", "Days", real.Days, SystemInfoSource.SourceId);
        var seconds = Text("uptime/seconds", "Seconds", real.Seconds, SystemInfoSource.SourceId);
        var (_, simulated) = Inputs();

        var start = Uptime(Read(simulated, formatted));
        Assert.NotEqual(real.Formatted, Read(simulated, formatted));
        Assert.NotEqual(real.Days, Read(simulated, days));
        _clock.Advance(5000);
        var later = Uptime(Read(simulated, formatted));
        Assert.Equal(TimeSpan.FromSeconds(5), later - start);
        Assert.Equal(SystemInfoSource.UptimeTexts(later).Seconds, Read(simulated, seconds));
        Assert.Equal(SystemInfoSource.UptimeTexts(later).Days, Read(simulated, days));

        static TimeSpan Uptime(string formatted)
        {
            var parts = formatted.Split(':').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            return new TimeSpan(parts[0], parts[1], parts[2], parts[3]);
        }
    }

    [Fact]
    public void Text_keys_the_list_doesnt_name_stay_within_the_keys_limit()
    {
        var limits = SpecLimits.Default with { Items = 1, Bindings = 4 };
        var live = new SensorSceneInputs(_sensors, new FakeSensorHistory());
        var simulated = new SimulatedSceneInputs(live, _clock, limits, () => _catalogue);

        for (var i = 0; i < 100; i++)
        {
            var key = SensorKey.Plugin($"/acme.tools/unlisted/{i}");
            _sensors.Readings[key] = new SensorReading($"Secret {i}");
            Assert.Equal(SimulatedText.NoName, Read(simulated, key));
        }

        var (keys, turns) = simulated.TextState;
        Assert.InRange(keys, 0, 4);
        Assert.InRange(turns, 0, 4);
    }

    [Fact]
    public void At_the_keys_limit_the_text_key_read_longest_ago_goes_and_the_others_keep_their_values()
    {
        var limits = SpecLimits.Default with { Items = 1, Bindings = 2 };
        var a = Text("lan/a", "Local IP", "10.0.0.1");
        var b = Text("lan/b", "Local IP", "10.0.0.2");
        var c = Text("lan/c", "Local IP", "10.0.0.3");
        var live = new SensorSceneInputs(_sensors, new FakeSensorHistory());
        var simulated = new SimulatedSceneInputs(live, _clock, limits, () => _catalogue);

        var first = Read(simulated, a);
        Assert.NotEqual(first, Read(simulated, b));
        _clock.Advance(1000);
        Assert.Equal(first, Read(simulated, a));
        // B is replaced by C: A, still read, keeps its address, and C gets one of its own.
        var other = Read(simulated, c);
        Assert.NotEqual(first, other);
        _clock.Advance(1000);
        Assert.Equal(first, Read(simulated, a));
        Assert.Equal(other, Read(simulated, c));
        Assert.Equal(2, simulated.TextState.Keys);
    }

    /// <summary>
    /// A plugin's sensors that carry private sentinels (<see cref="SentinelPlugin"/>) in their display names, a number's
    /// unit (initials that are listed units too), a table's headings, a cell's suffix and their values, time-like ones included, drawn by a scene: the live
    /// frame shows them (the check can see them), and a simulated frame's text shows none.
    /// </summary>
    [Fact]
    public void A_simulated_frame_draws_none_of_a_plugins_private_names_headings_suffixes_or_values()
    {
        var hub = new SensorHub([new SentinelPlugin()]);
        var sensors = new SensorSceneInputs(hub, new FakeSensorHistory());
        var live = new Recorder(sensors);
        var simulated = new Recorder(RenderService.SimulatedOptions(SceneKit.Options(sensors), _clock).Inputs);
        var numbers = SentinelPlugin.Numbers;
        var scene = SceneKit.Json("{'format':'infopanel.scene/1','inputs':{" + string.Join(",", SentinelPlugin.Texts.Select(t => $"'{t.Input}':{{'type':'text'}}")) + "," + string.Join(",", numbers.Select(n => $"'{n.Input}':{{'type':'number'}}")) + ",'t':{'type':'table'}},'nodes':["
            + string.Join(",", SentinelPlugin.Texts.Select((t, i) => $"{{'kind':'text','x':0,'y':{i * 20},'w':400,'font':{{'family':'Inter','size':12}},'fill':'#ffffff','text':{{'op':'input','name':'{t.Input}','field':'text'}}}}"))
            + "," + string.Join(",", numbers.Select((n, i) => $"{{'kind':'text','x':{i * 100},'y':160,'w':100,'font':{{'family':'Inter','size':12}},'fill':'#ffffff','text':{{'op':'input','name':'{n.Input}','field':'unit'}}}}"))
            + ",{'kind':'table','input':'t','y':200,'header':true,'columns':[{'column':0,'w':200},{'column':1,'w':200}],'font':{'family':'Inter','size':12},'fill':'#ffffff'}"
            + ",{'kind':'text','x':0,'y':380,'w':400,'font':{'family':'Inter','size':12},'fill':'#ffffff','text':{'op':'input','name':'t','field':'text'}}]}");
        Assert.True(SceneValidator.Check(scene).Status == CheckStatus.Valid);
        var bindings = SentinelPlugin.Texts.ToDictionary(t => t.Input, t => t.Key.ToString());
        foreach (var number in numbers)
            bindings[number.Input] = number.Key.ToString();
        bindings["t"] = SentinelPlugin.TableKey.ToString();
        var doc = SceneKit.Doc(scene, 400, 400, bindings: bindings);

        // What a frame shows: its text nodes' text, and the readings it was drawn from (a table's cells and headings
        // are drawn from these; the snapshot's text report carries no cell's text).
        List<string> Drawn(Recorder inputs)
        {
            inputs.Readings.Clear();
            using var snapshot = SceneRuntime.RenderOnce(doc, SceneKit.Now, SceneKit.Options(inputs));
            Assert.Null(snapshot.Bounds.Single().Problem);
            var texts = snapshot.Texts.Select(t => t.Text).ToList();
            foreach (var reading in inputs.Readings)
            {
                texts.Add(reading.Text);
                texts.Add(reading.Unit);
                texts.AddRange(reading.Table.Columns);
                texts.AddRange(reading.Table.Rows.SelectMany(r => r));
            }
            return texts;
        }

        bool Shows(List<string> texts, string sentinel) =>
            sentinel.Any(char.IsLetter) ? texts.Any(t => t.Contains(sentinel, StringComparison.OrdinalIgnoreCase)) : texts.Any(t => t.Contains(sentinel, StringComparison.Ordinal));

        // A suffix is a letter or two: it shows when a text ends with it after a number (42 A).
        bool ShowsAfterANumber(List<string> texts, string suffix) =>
            texts.Any(t => Regex.IsMatch(t, $@"\d\s?{Regex.Escape(suffix)}$", RegexOptions.CultureInvariant));

        // A unit of a letter or two is a person's initials when a text is that unit alone (T, mm).
        bool ShowsAlone(List<string> texts, string unit) => texts.Any(t => string.Equals(t.Trim(), unit, StringComparison.Ordinal));

        var real = Drawn(live);
        Assert.All(SentinelPlugin.Sentinels.Except(SentinelPlugin.Names), s => Assert.True(Shows(real, s), $"The live frame shows {s}: {string.Join(" | ", real)}"));
        Assert.All(SentinelPlugin.Suffixes, s => Assert.True(ShowsAfterANumber(real, s), $"The live frame shows {s}: {string.Join(" | ", real)}"));
        Assert.All(SentinelPlugin.Units, s => Assert.True(ShowsAlone(real, s), $"The live frame shows {s}: {string.Join(" | ", real)}"));

        var leaks = new List<string>();
        for (var tick = 0; tick < 3; tick++)
        {
            var made = Drawn(simulated);
            leaks.AddRange(SentinelPlugin.Sentinels.Where(s => Shows(made, s)).Select(s => $"{s} in {string.Join(" | ", made)}"));
            leaks.AddRange(SentinelPlugin.Suffixes.Where(s => ShowsAfterANumber(made, s)).Select(s => $"a number then {s} in {string.Join(" | ", made)}"));
            leaks.AddRange(SentinelPlugin.Units.Where(s => ShowsAlone(made, s)).Select(s => $"the unit {s} in {string.Join(" | ", made)}"));
            _clock.Advance(1000);
        }
        Assert.True(leaks.Count == 0, string.Join("\n", leaks));
    }

    [Fact]
    public void The_live_inputs_a_simulated_loop_is_made_over_still_read_this_computers_text()
    {
        var ip = Text("en0/ipv4-0", "IPv4 Address", "192.168.1.23", NetworkSource.SourceId);
        var live = new SceneRuntimeOptions { Inputs = new SensorSceneInputs(_sensors, new FakeSensorHistory()) };

        var simulated = RenderService.SimulatedOptions(live, _clock);

        Assert.StartsWith("192.0.2.", simulated.Inputs.Read(ip.ToString(), "text").Text, StringComparison.Ordinal);
        Assert.Equal("192.168.1.23", live.Inputs.Read(ip.ToString(), "text").Text);
        Assert.IsType<SensorSceneInputs>(live.Inputs);
    }

    /// <summary>
    /// This computer's own sources (its network, drives, processes on macOS, and its audio devices with
    /// <c>INFOPANEL_SENSOR_TESTS=1</c>) read through a simulated loop's inputs: no text value they read, but the time's,
    /// appears in any text or table the simulated loop reads.
    /// </summary>
    [Fact]
    public void A_simulated_loop_serves_none_of_this_computers_own_text()
    {
        var sources = new List<ISensorSource>();
        using var network = new NetworkSource(new SystemNetworkLister(), lookup: null, _clock);
        network.Polled.Open();
        sources.Add(network);
        var drives = DriveSource.CreateSystem(_clock);
        drives.Open();
        sources.Add(drives);
        if (OperatingSystem.IsMacOS())
        {
            var processes = SystemInfoSource.Create(new MacProcessSampler(), _clock);
            processes.Open();
            _clock.Advance(1000);
            processes.Poll();
            sources.Add(processes);
        }
        if (Environment.GetEnvironmentVariable("INFOPANEL_SENSOR_TESTS") == "1")
        {
            var audio = VolumeSource.CreateSystem(_clock);
            audio.Open();
            sources.Add(audio);
        }

        var hub = new SensorHub(sources);
        var live = new SceneRuntimeOptions { Inputs = new SensorSceneInputs(hub, new FakeSensorHistory()) };
        var simulated = RenderService.SimulatedOptions(live, _clock).Inputs;

        var sensors = hub.GetSensors();
        var real = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Environment.MachineName, Environment.UserName };
        var read = new List<(string Key, string Type)>();
        foreach (var sensor in sensors)
        {
            var state = hub.ReadState(sensor.Key);
            if (!state.HasReading || state.Type == SensorKind.Number)
                continue;
            var type = state.Type == SensorKind.Table ? "table" : "text";
            read.Add((sensor.Key.ToString(), type));
            // The uptime is a time: it stays real.
            if (sensor.Key.Id.StartsWith($"/{SystemInfoSource.SourceId}/uptime/", StringComparison.Ordinal))
                continue;
            var reading = live.Inputs.Read(sensor.Key.ToString(), type);
            real.Add(reading.Text);
            foreach (var row in reading.Table.Rows)
                real.UnionWith(row);
        }

        // What isn't this computer's own: the display names and columns shown, the samples, and numbers.
        var shown = new HashSet<string>(sensors.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        shown.UnionWith(SimulatedText.Processes);
        shown.UnionWith(SimulatedText.Adapters);
        shown.UnionWith(SimulatedText.AudioDevices);
        real.RemoveWhere(v => v.Trim().Length < 3 || shown.Contains(v.Trim()) || IsNumber(v));
        Assert.NotEmpty(read);
        Assert.NotEmpty(real);

        var served = new List<string>();
        for (var tick = 0; tick < 3; tick++)
        {
            foreach (var (key, type) in read)
            {
                var reading = simulated.Read(key, type);
                served.Add(reading.Text);
                foreach (var row in reading.Table.Rows)
                    served.AddRange(row);
            }
            _clock.Advance(1000);
        }

        var leaks = real.Where(v => served.Any(s => s.Contains(v.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.True(leaks.Count == 0, $"{leaks.Count} of {real.Count} real values served: {string.Join(" | ", leaks)}");
    }

    /// <summary>Inputs that keep every reading a frame was drawn from.</summary>
    private sealed class Recorder(ISceneInputSource inner) : ISceneInputSource
    {
        public List<SceneReading> Readings { get; } = [];

        public SceneReading Read(string key, string inputType)
        {
            var reading = inner.Read(key, inputType);
            lock (Readings)
                Readings.Add(reading);
            return reading;
        }

        public IReadOnlyList<double> History(string key) => inner.History(key);
    }

    private static bool IsNumber(string value)
    {
        var digits = new string(value.Where(c => !char.IsLetter(c) && c != '%' && c != '°' && c != ' ').ToArray());
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            || double.TryParse(digits, NumberStyles.Float, CultureInfo.CurrentCulture, out _);
    }
}
