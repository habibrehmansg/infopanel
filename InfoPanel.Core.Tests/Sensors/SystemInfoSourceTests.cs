using System.Data;
using System.Text.Json;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Drives;
using InfoPanel.Sensors.SystemInfo;
using InfoPanel.Settings;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// The system information source (<c>infopanel.system-info</c>) on a fake system: the 1.x System Info plugin's entries
/// under its container and entry ids, polled every second; the uptime in the plugin's formats; the counts, CPU and memory
/// in its units; three tables of processes, a row per name (instances summed), the busiest first, in its columns, cells
/// and format, its default names left out (exact case), cut at a plugin's table limit; where the system has no CPU
/// utility (macOS) Utility reads Usage, and Thread and Handle Count it can't read are unavailable. The plugin's keys are
/// never answered.
/// </summary>
public sealed class SystemInfoSourceTests : IDisposable
{
    private static readonly string[] Ids =
    [
        "uptime/formatted", "uptime/days", "uptime/hours", "uptime/minutes", "uptime/seconds",
        "processes/process-count", "processes/thread-count", "processes/handle-count", "processes/cpu-usage",
        "processes/cpu-utility", "processes/memory-usage", "processes/memory-compression",
        "processes/top-cpu-usage", "processes/top-cpu-utility", "processes/top-memory-usage",
    ];

    private const long MB = 1024 * 1024;

    /// <summary>A system the test sets: macOS's by default (no threads, handles or utility); a part set to throw throws.</summary>
    private sealed class FakeSystem : IProcessSampler
    {
        public int Processors = 4;
        public TimeSpan Up = TimeSpan.FromSeconds(5);
        public int Count = 821;
        public int? Threads;
        public int? Handles;
        public Queue<double?> Usage = new([null]);
        public double? Utility;
        public long AppMemory = 3 * 1024 * MB;
        public long Compressed = 512 * MB;
        public List<ProcessReading> List = [];
        public bool ProcessesThrow;
        public bool PerCpu;

        public int ProcessorCount => Processors;
        public bool ProcessCpuPerCpu => PerCpu;
        public TimeSpan Uptime() => Up;
        public int ProcessCount() => Count;
        public int? ThreadCount() => Threads;
        public int? HandleCount() => Handles;
        public double? CpuUsage() => Usage.Count > 1 ? Usage.Dequeue() : Usage.Peek();
        public double? CpuUtility() => Utility;
        public long MemoryUsage() => AppMemory;
        public long MemoryCompression() => Compressed;

        public IReadOnlyList<ProcessReading> Processes() =>
            ProcessesThrow ? throw new InvalidOperationException("refused") : [.. List];

        /// <summary>Adds CPU time to the process <paramref name="pid"/>.</summary>
        public void Run(int pid, double seconds)
        {
            var i = List.FindIndex(p => p.Pid == pid);
            List[i] = List[i] with { CpuTime = List[i].CpuTime + TimeSpan.FromSeconds(seconds) };
        }
    }

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    private readonly FakeSystem _system = new();
    private readonly TimerTimeProvider _time = new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
    private readonly List<LogEvent> _events = [];
    private readonly Persistence.TempDirectory _temp = new();
    private PolledSensorSource? _source;

    public void Dispose()
    {
        _source?.Dispose();
        _temp.Dispose();
    }

    /// <summary>The source over the fake system, discovered and polled once; with <paramref name="settings"/>, following them.</summary>
    private PolledSensorSource Open(SettingsStore? settings = null)
    {
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
        _source = SystemInfoSource.Create(_system, _time, log: log, settings: settings);
        _source.Open();
        return _source;
    }

    /// <summary>The engine's settings, in a folder of the test's own.</summary>
    private SettingsStore Settings() => SettingsStore.Open(_temp.Combine("settings.json"), _temp.Path, Serilog.Core.Logger.None, faults: null);

    private static void Exclude(SettingsStore settings, string excluded) =>
        settings.Apply(JsonDocument.Parse("""{"sources":{"systemInfo":{"excludedProcesses":""" + excluded + "}}}").RootElement);

    /// <summary>Every process runs a little more, so each is in the tables at the next round.</summary>
    private void RunAll()
    {
        foreach (var p in _system.List.ToArray())
            _system.Run(p.Pid, 0.1);
    }

    /// <summary>One poll, a second after the last (the source's interval).</summary>
    private void Round()
    {
        _time.Advance(SystemInfoSource.Interval);
        _source!.Poll();
    }

    private static SensorKey Key(string id) => SystemInfoSource.KeyOf(id.Split('/')[0], id.Split('/')[1]);

    private SensorState State(string id) => _source!.ReadState(Key(id));

    private string Text(string id)
    {
        var state = State(id);
        Assert.True(state.HasReading, id);
        Assert.Equal(SensorKind.Text, state.Type);
        return state.Reading.ValueText!;
    }

    private double Number(string id)
    {
        var state = State(id);
        Assert.True(state.HasReading, id);
        Assert.Equal(SensorKind.Number, state.Type);
        return state.Reading.ValueNow;
    }

    private DataTable Table(string id)
    {
        var state = State(id);
        Assert.True(state.HasReading, id);
        Assert.Equal(SensorKind.Table, state.Type);
        Assert.Equal(SystemInfoSource.TableFormat, state.Reading.ValueTableFormat);
        return state.Reading.ValueTable!;
    }

    private static string[] Names(DataTable table) => table.Rows.Cast<DataRow>().Select(r => ((PluginText)r[0]).Value).ToArray();

    private static PluginSensor Cell(DataTable table, string name, int column) =>
        (PluginSensor)table.Rows.Cast<DataRow>().Single(r => ((PluginText)r[0]).Value == name)[column];

    private void AddProcess(int pid, string name, long memory = 10 * MB, ulong started = 1) =>
        _system.List.Add(new ProcessReading(pid, name, TimeSpan.Zero, memory, started));

    // ---- The catalogue ----

    [Fact]
    public void Lists_The1xPluginsFifteenEntries_UnderItsIds_InItsOrder_GroupsNamesKindsAndUnits()
    {
        Open();
        var sensors = _source!.GetSensors();

        Assert.Equal(Ids.Select(id => $"infopanel:/system-info/{id}"), sensors.Select(s => s.Key.ToString()));
        Assert.All(sensors, s => Assert.Equal("infopanel.system-info", s.SourceId));
        // Each id is the plugin SDK's id of the 1.x name, container and entry.
        Assert.All(sensors, s => Assert.Equal(DriveSource.Slug(s.Group) + "/" + DriveSource.Slug(s.Name), s.Key.Id["/infopanel.system-info/".Length..]));
        Assert.Equal(["Formatted", "Days", "Hours", "Minutes", "Seconds", "Process Count", "Thread Count", "Handle Count", "CPU Usage",
            "CPU Utility", "Memory Usage", "Memory Compression", "Top CPU Usage", "Top CPU Utility", "Top Memory Usage"], sensors.Select(s => s.Name));
        Assert.Equal([.. Enumerable.Repeat("Uptime", 5), .. Enumerable.Repeat("Processes", 10)], sensors.Select(s => s.Group));
        Assert.Equal(["", "", "", "", "", "", "", "", "%", "%", " MB", " MB", "", "", ""], sensors.Select(s => s.Unit));
        Assert.Equal([.. Enumerable.Repeat(SensorKind.Text, 5), .. Enumerable.Repeat(SensorKind.Number, 7), .. Enumerable.Repeat(SensorKind.Table, 3)],
            Ids.Select(id => State(id).Type));
        Assert.Equal(TimeSpan.FromSeconds(1), _source.Options.Interval);
        Assert.False(_source.Handles(new SensorKey(Enums.SensorType.Plugin, "/system-info-plugin/uptime/formatted")));
    }

    // ---- Uptime ----

    [Theory]
    [InlineData(0, "0:00:00:00", "00", "00", "00", "00")]
    [InlineData(59, "0:00:00:59", "00", "00", "00", "59")]
    [InlineData(86_400, "1:00:00:00", "01", "00", "00", "00")]
    [InlineData(93_784, "1:02:03:04", "01", "02", "03", "04")]
    [InlineData(8_640_000, "100:00:00:00", "100", "00", "00", "00")]
    public void Uptime_ReadsIn1xsFormats(int seconds, string formatted, string days, string hours, string minutes, string secs)
    {
        _system.Up = TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(999);
        Open();

        Assert.Equal([formatted, days, hours, minutes, secs], Ids[..5].Select(Text));
        Assert.Equal((formatted, days, hours, minutes, secs), SystemInfoSource.UptimeTexts(TimeSpan.FromSeconds(seconds)));
        Assert.All(Ids[..5], id => Assert.Equal(SensorStatus.Live, State(id).Status));
    }

    [Fact]
    public void Uptime_CountsOnEachRound_AndAnUnchangedTextIsTheSameString()
    {
        Open();
        var days = State("uptime/days").Reading.ValueText;
        _system.Up += TimeSpan.FromSeconds(1);
        Round();

        Assert.Equal("0:00:00:06", Text("uptime/formatted"));
        Assert.Same(days, State("uptime/days").Reading.ValueText);
    }

    // ---- Numbers ----

    [Fact]
    public void ProcessCount_AndMemory_ReadInThePluginsUnits()
    {
        Open();

        Assert.Equal(821, Number("processes/process-count"));
        Assert.Equal(3072, Number("processes/memory-usage"));
        Assert.Equal(512, Number("processes/memory-compression"));
        Assert.Equal(" MB", State("processes/memory-usage").Reading.Unit);
    }

    [Fact]
    public void ThreadAndHandleCount_TheSystemCantRead_AreUnavailable_AsOnMacOS()
    {
        Open();
        Round();

        // Listed, with no reading.
        Assert.Contains(_source!.GetSensors(), s => s.Key == Key("processes/thread-count"));
        Assert.Contains(_source.GetSensors(), s => s.Key == Key("processes/handle-count"));
        Assert.Equal((SensorStatus.Unavailable, SensorKind.Number, false), Of(State("processes/thread-count")));
        Assert.Equal((SensorStatus.Unavailable, SensorKind.Number, false), Of(State("processes/handle-count")));
    }

    [Fact]
    public void ThreadAndHandleCount_ReadWhereTheSystemHasThem()
    {
        _system.Threads = 4000;
        _system.Handles = 90_000;
        Open();

        Assert.Equal((4000, 90_000), (Number("processes/thread-count"), Number("processes/handle-count")));
    }

    [Fact]
    public void CpuUsage_ReadsFromTheSecondRound_AndUtilityReadsUsage_WhereTheSystemHasNoUtility()
    {
        _system.Usage = new([null, 37.5]);
        Open();

        Assert.Equal(SensorStatus.Unavailable, State("processes/cpu-usage").Status);
        Assert.Equal(SensorStatus.Unavailable, State("processes/cpu-utility").Status);

        Round();
        Assert.Equal(37.5, Number("processes/cpu-usage"));
        Assert.Equal(37.5, Number("processes/cpu-utility"));
    }

    [Fact]
    public void CpuUtility_ReadsTheSystemsOwn_WhereItHasOne()
    {
        _system.Usage = new([null, 40]);
        _system.Utility = 55;
        Open();
        Round();

        Assert.Equal((40, 55), (Number("processes/cpu-usage"), Number("processes/cpu-utility")));
    }

    // ---- Tables ----

    [Fact]
    public void Tables_HaveNoReading_UntilASecondRound()
    {
        AddProcess(10, "yes");
        Open();

        Assert.All(Ids[12..], id => Assert.Equal((SensorStatus.Unavailable, SensorKind.Table, false), Of(State(id))));
    }

    [Fact]
    public void Tables_SumEachNamesInstances_SortTheBusiestFirst_InThe1xColumnsCellsAndFormat()
    {
        _system.Usage = new([null, 50]);
        AddProcess(10, "yes", memory: 1 * MB);
        AddProcess(11, "Code Helper", memory: 100 * MB);
        AddProcess(12, "Code Helper", memory: 150 * MB);
        AddProcess(13, "node", memory: 400 * MB);
        Open();
        // A second on 4 CPUs: yes runs one of them, the two helpers a quarter of one each, node a tenth.
        _system.Run(10, 1);
        _system.Run(11, 0.25);
        _system.Run(12, 0.25);
        _system.Run(13, 0.1);
        Round();

        var usage = Table("processes/top-cpu-usage");
        Assert.Equal(["Process Name", "Usage", "Utility", "Memory"], usage.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
        Assert.Equal([typeof(PluginText), typeof(PluginSensor), typeof(PluginSensor), typeof(PluginSensor)], usage.Columns.Cast<DataColumn>().Select(c => c.DataType));
        Assert.Equal(["yes", "Code Helper", "node"], Names(usage));
        Assert.Equal((25f, "%"), (Cell(usage, "yes", 1).Value, Cell(usage, "yes", 1).Unit));
        Assert.Equal(12.5f, Cell(usage, "Code Helper", 1).Value);
        Assert.Equal(2.5, Cell(usage, "node", 1).Value, 3);
        Assert.Equal((250f, " MB"), (Cell(usage, "Code Helper", 3).Value, Cell(usage, "Code Helper", 3).Unit));
        Assert.Equal("Usage", Cell(usage, "yes", 1).Name);
        Assert.Equal(("Process Name", "yes"), (((PluginText)usage.Rows[0][0]).Name, usage.Rows[0][0].ToString()));
        // A table reads as text its first row, as a plugin's does (in the culture's numbers).
        Assert.Equal(new PluginTable("", "", usage, SystemInfoSource.TableFormat).ToString(), State("processes/top-cpu-usage").Reading.ValueText);
        Assert.StartsWith("yes, 25", State("processes/top-cpu-usage").Reading.ValueText);

        Assert.Equal(["yes", "Code Helper", "node"], Names(Table("processes/top-cpu-utility")));
        Assert.Equal(["node", "Code Helper", "yes"], Names(Table("processes/top-memory-usage")));
    }

    [Fact]
    public void Tables_UtilityIsUsage_WhereTheSystemHasNoUtility()
    {
        _system.Usage = new([null, 50]);
        AddProcess(10, "yes");
        AddProcess(11, "node");
        Open();
        _system.Run(10, 1);
        _system.Run(11, 0.5);
        Round();

        foreach (var id in Ids[12..14])
        {
            var table = Table(id);
            foreach (var name in new[] { "yes", "node" })
                Assert.Equal(Cell(table, name, 1).Value, Cell(table, name, 2).Value);
        }
    }

    [Fact]
    public void Tables_Utility_IsTheSystemsUtility_InProportion_As1x()
    {
        _system.Usage = new([null, 50]);
        _system.Utility = 80;
        AddProcess(10, "yes");
        AddProcess(11, "node");
        Open();
        _system.Run(10, 1); // 25 % of 4 CPUs: half the system's 50, so half its 80
        _system.Run(11, 3); // 75 %, over the system's usage: 0, as 1.x
        Round();

        var table = Table("processes/top-cpu-utility");
        Assert.Equal(40f, Cell(table, "yes", 2).Value);
        Assert.Equal(0f, Cell(table, "node", 2).Value);
        Assert.Equal(["yes", "node"], Names(table));
    }

    [Fact]
    public void Tables_LeaveOutThe1xDefaultNames_ExactlyAndInTheirCase()
    {
        _system.Usage = new([null, 50]);
        string[] excluded = ["_Total", "Idle", "dwm", "csrss", "svchost", "lsass", "system", "spoolsv", "Memory Compression"];
        string[] kept = ["System", "IDLE", "svchost.exe", "Memory compression", "kernel"];
        var pid = 100;
        foreach (var name in excluded.Concat(kept))
            AddProcess(pid++, name);
        Open();
        foreach (var p in _system.List.ToArray())
            _system.Run(p.Pid, 0.1);
        Round();

        Assert.Equal(excluded, SystemInfoSource.DefaultExcluded);
        foreach (var id in Ids[12..])
            Assert.Equal(kept.Order(StringComparer.Ordinal), Names(Table(id)));
    }

    // ---- The setting: sources.systemInfo.excludedProcesses ----

    [Fact]
    public void Tables_LeaveOutTheSettingsNames_AChangeAppliedFromTheNextRound()
    {
        _system.Usage = new([null, 50]);
        AddProcess(10, "svchost");
        AddProcess(11, "InfoPanel");
        AddProcess(12, "node");
        var settings = Settings();
        Open(settings);
        RunAll();
        Round();
        // The default: 1.x's nine.
        Assert.All(Ids[12..], id => Assert.Equal(["InfoPanel", "node"], Names(Table(id)).Order(StringComparer.Ordinal)));

        Exclude(settings, """["InfoPanel"]""");
        // The round before the change is as it was; the next round leaves out the new list's names, and only them.
        Assert.Equal(["InfoPanel", "node"], Names(Table("processes/top-cpu-usage")).Order(StringComparer.Ordinal));
        RunAll();
        Round();
        Assert.All(Ids[12..], id => Assert.Equal(["node", "svchost"], Names(Table(id)).Order(StringComparer.Ordinal)));

        Exclude(settings, "[]");
        RunAll();
        Round();
        Assert.All(Ids[12..], id => Assert.Equal(["InfoPanel", "node", "svchost"], Names(Table(id)).Order(StringComparer.Ordinal)));

        Exclude(settings, "null");
        RunAll();
        Round();
        Assert.All(Ids[12..], id => Assert.Equal(["InfoPanel", "node"], Names(Table(id)).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Tables_MatchTheSettingsNamesExactly_ANameWithATrailingSpaceOrInAnotherCase_LeavesNothingOut()
    {
        _system.Usage = new([null, 50]);
        AddProcess(10, "InfoPanel");
        AddProcess(11, "node");
        AddProcess(12, "Google Chrome");
        var settings = Settings();
        Exclude(settings, """["InfoPanel ", "NODE", " Google Chrome", "google chrome"]""");
        Open(settings);
        RunAll();
        Round();

        Assert.All(Ids[12..], id => Assert.Equal(["Google Chrome", "InfoPanel", "node"], Names(Table(id)).Order(StringComparer.Ordinal)));

        Exclude(settings, """["InfoPanel", "node", "Google Chrome"]""");
        RunAll();
        Round();

        Assert.All(Ids[12..], id => Assert.Empty(Names(Table(id))));
    }

    [Fact]
    public void Tables_AreCut_AtAPluginsTableLimit_KeepingTheBusiest()
    {
        _system.Usage = new([null, 50]);
        for (var i = 0; i < SystemInfoSource.MaxRows + 5; i++)
            AddProcess(1000 + i, $"p{i:D4}", memory: (i + 1) * MB);
        Open();
        for (var i = 0; i < SystemInfoSource.MaxRows + 5; i++)
            _system.Run(1000 + i, (i + 1) / 100_000d);
        Round();

        Assert.Equal(1000, SystemInfoSource.MaxRows);
        var usage = Table("processes/top-cpu-usage");
        Assert.Equal(SystemInfoSource.MaxRows, usage.Rows.Count);
        Assert.Equal("p1004", Names(usage)[0]);
        Assert.Equal("p0005", Names(usage)[^1]);
        Assert.Equal(SystemInfoSource.MaxRows, Table("processes/top-memory-usage").Rows.Count);
    }

    [Fact]
    public void Tables_LeaveOutAProcessNewThisRound_AndAPidUsedAgain_UntilTheirNextRound()
    {
        _system.Usage = new([null, 50]);
        AddProcess(10, "yes");
        AddProcess(11, "old", started: 1);
        Open();
        _system.List[1] = new ProcessReading(11, "new", TimeSpan.FromSeconds(9), 1 * MB, Started: 2);
        AddProcess(12, "late");
        Round();
        Assert.Equal(["yes"], Names(Table("processes/top-cpu-usage")));

        _system.Run(11, 0.5);
        Round();
        Assert.Equal(["new", "late", "yes"], Names(Table("processes/top-cpu-usage")));
    }

    [Fact]
    public void EachRound_HandsOverNewTables()
    {
        _system.Usage = new([null, 50]);
        AddProcess(10, "yes");
        Open();
        Round();
        var first = Table("processes/top-cpu-usage");
        Round();

        Assert.NotSame(first, Table("processes/top-cpu-usage"));
        Assert.Equal(["yes"], Names(first));
    }

    [Fact]
    public void AFailingRead_FailsItsOwnReadingsOnly_LoggedOnce_AndTheNextGoodRoundStartsTheTablesAfresh()
    {
        _system.Usage = new([null, 50]);
        AddProcess(10, "yes");
        Open();
        Round();
        Assert.Equal(["yes"], Names(Table("processes/top-cpu-usage")));

        _system.ProcessesThrow = true;
        Round();
        Round();
        Assert.All(Ids[12..], id => Assert.Equal(SensorStatus.Stale, State(id).Status));
        Assert.Equal(SensorStatus.Live, State("uptime/formatted").Status);
        Assert.Equal(SensorStatus.Live, State("processes/process-count").Status);
        Assert.Single(_events, e => e.Level == LogEventLevel.Warning);

        // Back: the first round has no interval, so the tables stay as they were, then read.
        _system.ProcessesThrow = false;
        _system.Run(10, 1);
        Round();
        Assert.Equal(SensorStatus.Stale, State("processes/top-cpu-usage").Status);
        Round();
        Assert.Equal(SensorStatus.Live, State("processes/top-cpu-usage").Status);
        Assert.Equal(0f, Cell(Table("processes/top-cpu-usage"), "yes", 1).Value);
    }

    [Fact]
    public void Tables_ListAProcessNameInFull()
    {
        _system.Usage = new([null, 50]);
        const string name = "a-process-whose-name-is-forty-characters";
        Assert.Equal(40, name.Length);
        AddProcess(10, name);
        Open();
        _system.Run(10, 0.5);
        Round();

        Assert.All(Ids[12..], id => Assert.Equal([name], Names(Table(id))));
    }

    [Fact]
    public void Tables_WhereTheSystemCountsPerCpu_ABusyThreadReads100_SeveralMore_AndCpuUsageStaysTheShareOfEveryCpu()
    {
        // macOS's, as Activity Monitor: one busy thread on 14 CPUs reads 100 %, not a fourteenth of it.
        _system.Processors = 14;
        _system.PerCpu = true;
        _system.Usage = new([null, 30]);
        AddProcess(10, "yes");
        AddProcess(11, "make");
        Open();
        _system.Run(10, 1);
        _system.Run(11, 3);
        Round();

        foreach (var id in Ids[12..])
        {
            var table = Table(id);
            Assert.Equal(100, Cell(table, "yes", 1).Value, 3);
            Assert.Equal(300, Cell(table, "make", 1).Value, 3);
            // No utility counter: Utility reads Usage.
            Assert.Equal(Cell(table, "yes", 1).Value, Cell(table, "yes", 2).Value);
        }

        Assert.Equal("100%", Cell(Table("processes/top-cpu-usage"), "yes", 1).ToString());
        Assert.Equal(30, Number("processes/cpu-usage"));
        Assert.Equal(30, Number("processes/cpu-utility"));
    }

    [Fact]
    public void Tables_ShowEachProcesssCpu_AveragedOverTheLastThreeRounds_RefreshedEachRound()
    {
        _system.Processors = 1;
        _system.Usage = new([null, 50]);
        AddProcess(10, "burst");
        AddProcess(11, "steady");
        Open();

        // A burst of 0.9 s in the first second, then none; a steady 0.4 s each second.
        (double Burst, double Steady)[] expected = [(90, 40), (45, 40), (30, 40), (0, 40)];
        string[][] order = [["burst", "steady"], ["burst", "steady"], ["steady", "burst"], ["steady", "burst"]];
        DataTable? last = null;
        for (var round = 0; round < expected.Length; round++)
        {
            if (round == 0)
                _system.Run(10, 0.9);
            _system.Run(11, 0.4);
            Round();

            var table = Table("processes/top-cpu-usage");
            Assert.NotSame(last, table);
            last = table;
            Assert.Equal(expected[round].Burst, Cell(table, "burst", 1).Value, 3);
            Assert.Equal(expected[round].Steady, Cell(table, "steady", 1).Value, 3);
            Assert.Equal(order[round], Names(table));
            Assert.Equal(order[round], Names(Table("processes/top-cpu-utility")));
            Assert.Equal(50, Number("processes/cpu-usage"));
        }

        Assert.Equal(3, SystemInfoSource.CpuRounds);
    }

    [Fact]
    public void Tables_AverageEachNameOverItsAppearancesInTheLastThreeRounds_ARoundWithoutItForgetsNothing()
    {
        _system.Processors = 1;
        _system.Usage = new([null, 50]);
        AddProcess(10, "worker");
        Open();

        // One process, named worker, then helper, then worker again: 90 %, then 0 %, then 0 %.
        _system.Run(10, 0.9);
        Round();
        Assert.Equal(90, Cell(Table("processes/top-cpu-usage"), "worker", 1).Value, 3);

        _system.List[0] = _system.List[0] with { Name = "helper" };
        Round();
        Assert.Equal(["helper"], Names(Table("processes/top-cpu-usage")));
        Assert.Equal(0, Cell(Table("processes/top-cpu-usage"), "helper", 1).Value, 3);

        // Its two appearances in the last three rounds: (90 + 0) / 2.
        _system.List[0] = _system.List[0] with { Name = "worker" };
        Round();
        Assert.Equal(45, Cell(Table("processes/top-cpu-usage"), "worker", 1).Value, 3);
        Assert.Equal(45, Cell(Table("processes/top-cpu-utility"), "worker", 2).Value, 3);

        // The 90 % round is now four rounds back: out of the window.
        Round();
        Assert.Equal(0, Cell(Table("processes/top-cpu-usage"), "worker", 1).Value, 3);
    }

    [Fact]
    public void Tables_MemoryOf1024MBOrMore_ReadsInGB_RowsSortedByBytes_AndMemoryUsageStaysInMB()
    {
        _system.Usage = new([null, 50]);
        AddProcess(10, "big", memory: 2048 * MB);
        AddProcess(11, "small", memory: 900 * MB);
        AddProcess(12, "edge", memory: 1024 * MB);
        AddProcess(13, "under", memory: 1023 * MB);
        Open();
        Round();

        var table = Table("processes/top-memory-usage");
        Assert.Equal(["big", "edge", "under", "small"], Names(table));
        Assert.Equal((2f, " GB"), (Cell(table, "big", 3).Value, Cell(table, "big", 3).Unit));
        Assert.Equal((1f, " GB"), (Cell(table, "edge", 3).Value, Cell(table, "edge", 3).Unit));
        Assert.Equal((1023f, " MB"), (Cell(table, "under", 3).Value, Cell(table, "under", 3).Unit));
        Assert.Equal((900f, " MB"), (Cell(table, "small", 3).Value, Cell(table, "small", 3).Unit));
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal(["2.0 GB", "1.0 GB", "1023.0 MB", "900.0 MB"], table.Rows.Cast<DataRow>().Select(r => r[3].ToString()));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }

        // The Memory Usage reading stays a number of megabytes (a 1.x profile binds it with its own decimals).
        Assert.Equal((3072, " MB"), (Number("processes/memory-usage"), State("processes/memory-usage").Reading.Unit));
    }

    [Fact]
    public void Tables_AMemoryCellThatWouldRead1024MB_ReadsInGB()
    {
        _system.Usage = new([null, 50]);
        // 1,023.949999… MB, whose cell value (a float) reads 1024.0 MB to one decimal.
        AddProcess(10, "edge", memory: 1_073_689_395);
        Open();
        Round();

        var cell = Cell(Table("processes/top-memory-usage"), "edge", 3);
        Assert.Equal(" GB", cell.Unit);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal("1.0 GB", cell.ToString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    private static (SensorStatus, SensorKind, bool) Of(SensorState state) => (state.Status, state.Type, state.HasReading);

    // ---- The 1.x import ----

    private static string Bound(string type, string name, string key) =>
        $"""<DisplayItem xsi:type="{type}"><Name>{name}</Name><SensorName>{name}</SensorName><SensorType>Plugin</SensorType><PluginSensorId>{key}</PluginSensorId><Id>0</Id><Instance>0</Instance><EntryId>0</EntryId></DisplayItem>""";

    [Fact]
    public void A1xProfileBoundToSystemInfo_IsTranslatedToTheSource_AsVolumesIs()
    {
        using var kit = new Import.ImportKit();
        var source = Open();
        var hub = new SensorHub([source]);
        var envelope = InfoPanel.Import.V1Reader.ReadArchive(new MemoryStream(Import.ImportKit.V1Archive(Import.ImportKit.Items(
            Bound("SensorDisplayItem", "Uptime", "/system-info-plugin/uptime/formatted"),
            Import.ImportKit.Group("Box", Bound("TableSensorDisplayItem", "Top", "/system-info-plugin/processes/top-cpu-usage")),
            Bound("SensorDisplayItem", "Volume", "/volume-plugin/default/volume")))),
            new InfoPanel.Import.V1ReadContext(kit.Workspace, hub, null, Import.ImportKit.Fonts.Contains));

        var items = new[] { envelope.Items[0], envelope.Items[1].Children![0], envelope.Items[2] };
        Assert.Equal(["/infopanel.system-info/uptime/formatted", "/infopanel.system-info/processes/top-cpu-usage", "/infopanel.volume/default/volume"],
            items.Select(n => (string)n.Props["pluginSensorId"]!));
        Assert.Equal(["infopanel:/system-info/uptime/formatted", "infopanel:/system-info/processes/top-cpu-usage", "infopanel:/volume/default/volume"],
            items.Select(n => n.Meta.Sensor!.Key));
        // The source reads the uptime from its first round, its tables from the second; this hub has no volume source.
        Assert.Equal([SensorStatus.Live, SensorStatus.Unavailable, SensorStatus.Unavailable], items.Select(n => n.Meta.Sensor!.Status));
        Assert.Equal(["/items/0", "/items/1/children/0", "/items/2"], Import.ImportKit.With(envelope, "import.sensor.translated").Select(d => d.Path));
        Assert.Equal("System Info", RetiredPlugins.SourceOf("system-info-plugin"));
        Assert.Equal("Volume", RetiredPlugins.SourceOf("volume-plugin"));
        Assert.All(new[] { "uptime/formatted", "processes/top-cpu-usage" }, path =>
            Assert.Contains(source.GetSensors(), s => s.Key.ToString() == "infopanel:/system-info/" + path));
    }

    // ---- The macOS reader's arithmetic, on a fake proc_listallpids ----

    /// <summary>
    /// A fake <c>proc_listallpids</c> over <paramref name="processes"/> pids (read again each call, so the test can grow
    /// them): the hint is the count, a listing fills what fits and returns how many it listed; null fails (0, ENOMEM).
    /// </summary>
    private sealed class FakeListing(Func<int?> processes)
    {
        public List<int> Fills = [];

        public int List(Span<int> buffer, out int errno)
        {
            errno = 0;
            if (processes() is not { } count)
            {
                errno = 12;
                return 0;
            }

            if (buffer.IsEmpty)
                return count;

            Fills.Add(buffer.Length);
            var got = Math.Min(count, buffer.Length);
            for (var i = 0; i < got; i++)
                buffer[i] = i + 1;
            return got;
        }
    }

    [Fact]
    public void MacListing_AListingThatReadsZero_IsAFailedRead_NeverNoProcesses()
    {
        // libproc's wrapper turns a failed call into 0: there are always processes (this one), so 0 is a failure.
        var pids = new int[8];
        var fails = new FakeListing(() => null);
        var error = Assert.Throws<InvalidOperationException>(() => MacProcesses.ListPids(fails.List, ref pids).Length);
        Assert.Contains("errno 12", error.Message);

        // The size hint reads, then the listing fails.
        var calls = 0;
        var second = new FakeListing(() => ++calls == 1 ? 500 : null);
        error = Assert.Throws<InvalidOperationException>(() => MacProcesses.ListPids(second.List, ref pids).Length);
        Assert.Contains("errno 12", error.Message);

        // A good listing reads every pid.
        var good = new FakeListing(() => 500);
        Assert.Equal(500, MacProcesses.ListPids(good.List, ref pids).Length);
    }

    [Fact]
    public void MacListing_AFullBuffer_IsListedAgain_TwiceTheSize_AtMostThreeTimes_ThenReadsWhatItListed()
    {
        // The processes grow past the hint (100, so 356 slots) before the listing: it fills them, then 712 and 1,424,
        // then holds them in 2,848.
        var calls = 0;
        var growing = new FakeListing(() => ++calls == 1 ? 100 : 2000);
        var pids = new int[8];
        var read = MacProcesses.ListPids(growing.List, ref pids);
        Assert.Equal(2000, read.Length);
        Assert.Equal([356, 712, 1424, 2848], growing.Fills);
        Assert.Equal(2848, pids.Length);

        // A buffer that is always full: three larger listings, then what the last one listed.
        calls = 0;
        var endless = new FakeListing(() => ++calls == 1 ? 100 : int.MaxValue);
        pids = new int[8];
        read = MacProcesses.ListPids(endless.List, ref pids);
        Assert.Equal([356, 712, 1424, 2848], endless.Fills);
        Assert.Equal(2848, read.Length);
    }

    [Theory]
    // Apple silicon's timebase (125/3 ns a unit, 24 MHz): exact, where a double of 125/3 falls a 100 ns tick short (780
    // units are 32,500 ns).
    [InlineData(780UL, 125U, 3U, 325L)]
    [InlineData(24_000_000UL, 125U, 3U, 10_000_000L)]
    [InlineData(3UL, 125U, 3U, 1L)]
    [InlineData(86_400UL * 24_000_000, 125U, 3U, 864_000_000_000L)]
    // Intel's (1 ns a unit), and none read (0/0): nanoseconds.
    [InlineData(1_500UL, 1U, 1U, 15L)]
    [InlineData(1_500UL, 0U, 0U, 15L)]
    public void MacCpuTime_ConvertsMachUnits_Exactly(ulong units, uint numer, uint denom, long ticks) =>
        Assert.Equal(TimeSpan.FromTicks(ticks), MacProcesses.CpuTime(units, numer, denom));

    [Fact]
    public void MacName_IsTheExecutablesWholeName_FromItsPath_ElseTheKernelsShortName()
    {
        // proc_name's is cut at 31 bytes; the path's last component is whole.
        const string name = "a-process-whose-name-is-forty-characters";
        Assert.Equal(name, MacProcesses.Name(System.Text.Encoding.UTF8.GetBytes("/private/tmp/scratch/" + name), "a-process-whose-name-is-forty-c"u8));
        Assert.Equal("Google Chrome Helper (Renderer)", MacProcesses.Name(
            "/Applications/Google Chrome.app/Contents/Frameworks/Google Chrome Framework.framework/Versions/1/Helpers/Google Chrome Helper (Renderer).app/Contents/MacOS/Google Chrome Helper (Renderer)"u8,
            "Google Chrome Helper (Rendere"u8));
        Assert.Equal("Café", MacProcesses.Name("/Applications/Café"u8, "Caf"u8));

        // The path can't be read (none), or names no file: the kernel's short name.
        Assert.Equal("kernel_task", MacProcesses.Name(default, "kernel_task"u8));
        Assert.Equal("yes", MacProcesses.Name("/"u8, "yes"u8));
    }

    // ---- This Mac ----

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Real_ThisMac_ReadsTheUptime_EveryProcess_AndThisUsersProcesses_ButNoThreadsHandlesOrUtility()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The macOS reader.");
        var mac = new MacProcessSampler();

        Assert.InRange(mac.Uptime(), TimeSpan.FromSeconds(1), TimeSpan.FromDays(3650));
        Assert.InRange(mac.ProcessCount(), 2, 1_000_000);
        Assert.Null(mac.ThreadCount());
        Assert.Null(mac.HandleCount());
        Assert.Null(mac.CpuUtility());
        Assert.Null(mac.CpuUsage());
        Assert.InRange(mac.CpuUsage()!.Value, 0, 100);
        Assert.True(mac.MemoryUsage() > 0);
        Assert.True(mac.MemoryCompression() >= 0);
        var processes = mac.Processes();
        var self = Assert.Single(processes, p => p.Pid == Environment.ProcessId);
        Assert.False(string.IsNullOrEmpty(self.Name));
        Assert.True(self.CpuTime > TimeSpan.Zero);
        Assert.True(self.MemoryBytes > 0);
        // launchd (pid 1) is root's: not read unprivileged.
        Assert.DoesNotContain(processes, p => p.Pid == 1);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Real_ThisMac_TheSourceFollowsTheEnginesSettings_ThisProcessLeftOutFromTheNextRound()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The macOS reader.");
        var self = new MacProcessSampler().Processes().Single(p => p.Pid == Environment.ProcessId).Name;
        var settings = Settings();
        _source = SystemInfoSource.CreateSystem(_time, settings);
        _source.Open();
        Round();
        Assert.Contains(self, Names(Table("processes/top-memory-usage")));

        Exclude(settings, JsonSerializer.Serialize(new[] { self }));
        Round();

        Assert.DoesNotContain(self, Names(Table("processes/top-memory-usage")));
    }
}
