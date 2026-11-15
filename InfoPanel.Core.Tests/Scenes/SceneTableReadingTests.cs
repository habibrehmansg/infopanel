using System.Data;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// Tables read as data (spec/docs/bindings.md, "Tables"; spec/docs/expressions.md, "Reading text and tables"): a table
/// input's one reading for a tick, its first <c>tableRows</c> rows with each cell's text and number; <c>cell</c>, the
/// <c>rows</c> field, and what the validator refuses. The <c>parse</c> vectors are in spec/examples/expressions/vectors.json.
/// </summary>
public sealed class SceneTableReadingTests
{
    private static readonly SensorKey Key = SensorKey.Plugin("/test/forecast");

    /// <summary>
    /// A forecast as a plugin hands it over: a text column, a number column with a unit, a text column that reads like a
    /// number, and a number column whose second row isn't finite.
    /// </summary>
    private static DataTable Forecast()
    {
        var table = new DataTable();
        table.Columns.Add("day", typeof(object));
        table.Columns.Add("high", typeof(object));
        table.Columns.Add("code", typeof(object));
        table.Columns.Add("rain", typeof(object));
        table.Rows.Add(new PluginText("day", "Mon"), new PluginSensor("high", 28.6f, "%"), new PluginText("code", "42"), new PluginSensor("rain", 1.5f, "mm"));
        table.Rows.Add(new PluginText("day", "Tue"), new PluginSensor("high", -3.25f, "°C"), new PluginText("code", "7"), new PluginSensor("rain", float.NaN, "mm"));
        table.Rows.Add(new PluginText("day", "Wed"), new PluginSensor("high", 31f, "°C"), new PluginText("code", "x"), new PluginSensor("rain", float.PositiveInfinity, "mm"));
        return table;
    }

    /// <summary>The engine's reading of <paramref name="table"/> as a table input <c>t</c> reads it.</summary>
    private static Action<FakeSceneInputs> Table(DataTable table) => fake =>
    {
        var sensors = new FakeSensorReader();
        sensors.Readings[Key] = new SensorReading(table, "", "summary");
        fake.Readings["test:t"] = new SensorSceneInputs(sensors, new FakeSensorHistory()).Read(Key.ToString(), "table");
    };

    private static string Text(string expr, DataTable? table = null) =>
        SceneExpressionTests.Eval(expr, Table(table ?? Forecast())).Value.Text;

    private static double Num(string expr, DataTable? table = null) =>
        SceneExpressionTests.Eval(expr, Table(table ?? Forecast())).Value.N;

    // ---- cell: rows, columns and what a reading lacks ------------------------------------------------------------

    [Theory]
    [InlineData("{'op':'cell','input':'t','row':0,'column':0}", "Mon")]
    [InlineData("{'op':'cell','input':'t','row':2,'column':'day'}", "Wed")]
    [InlineData("{'op':'cell','input':'t','row':1,'column':'day','field':'text'}", "Tue")]
    [InlineData("{'op':'cell','input':'t','row':0,'column':'high'}", "28.6%")]
    public void A_cell_by_index_and_by_name_reads_its_text(string expr, string expected) => Assert.Equal(expected, Text(expr));

    [Theory]
    [InlineData("{'op':'cell','input':'t','row':3,'column':0}")]
    [InlineData("{'op':'cell','input':'t','row':-1,'column':0}")]
    [InlineData("{'op':'cell','input':'t','row':1000000,'column':0}")]
    [InlineData("{'op':'cell','input':'t','row':0,'column':4}")]
    [InlineData("{'op':'cell','input':'t','row':0,'column':255}")]
    [InlineData("{'op':'cell','input':'t','row':0,'column':'nope'}")]
    [InlineData("{'op':'cell','input':'t','row':0,'column':'Day'}")]
    public void A_row_or_column_the_reading_lacks_reads_empty_text(string expr) => Assert.Equal("", Text(expr));

    [Theory]
    [InlineData("{'op':'cell','input':'t','row':3,'column':1,'field':'number'}")]
    [InlineData("{'op':'cell','input':'t','row':-1,'column':1,'field':'number'}")]
    [InlineData("{'op':'cell','input':'t','row':0,'column':4,'field':'number'}")]
    [InlineData("{'op':'cell','input':'t','row':0,'column':'nope','field':'number'}")]
    public void A_row_or_column_the_reading_lacks_reads_0(string expr) => SceneExpressionTests.AssertExact(0, Num(expr), expr);

    [Theory]
    [InlineData(0.49, "Mon")]
    [InlineData(0.5, "Tue")]
    [InlineData(1.5, "Wed")]
    [InlineData(-0.4, "Mon")]
    [InlineData(-0.5, "")]
    [InlineData(2.5, "")]
    public void The_row_is_rounded_half_away_from_zero(double row, string expected) =>
        Assert.Equal(expected, Text($"{{'op':'cell','input':'t','row':{row.ToString(System.Globalization.CultureInfo.InvariantCulture)},'column':'day'}}"));

    [Fact]
    public void A_row_given_by_an_expression_is_read_each_tick() =>
        Assert.Equal("Wed", Text("{'op':'cell','input':'t','row':{'op':'sub','args':[{'op':'input','name':'t','field':'rows'},1]},'column':'day'}"));

    [Fact]
    public void A_column_by_name_is_the_first_with_that_name()
    {
        var expr = "{'op':'cell','input':'t','row':0,'column':'a'}";
        var value = SceneExpressionTests.Eval(expr, fake => fake.Readings["test:t"] = new SceneReading
        {
            Status = SceneInputStatus.Live,
            Table = new SceneTable(["b", "a", "a"], [["one", "two", "three"]]),
        }).Value.Text;
        Assert.Equal("two", value);
    }

    [Fact]
    public void Every_cell_of_an_unavailable_table_reads_empty_and_0()
    {
        Assert.Equal("", SceneExpressionTests.Eval("{'op':'cell','input':'t','row':0,'column':0}").Value.Text);
        SceneExpressionTests.AssertExact(0, SceneExpressionTests.Eval("{'op':'cell','input':'t','row':0,'column':0,'field':'number'}").Value.N, "unavailable");
        SceneExpressionTests.AssertExact(0, SceneExpressionTests.Eval("{'op':'input','name':'t','field':'rows'}").Value.N, "unavailable rows");
    }

    [Fact]
    public void A_cells_text_is_cut_to_text_length()
    {
        var table = new DataTable();
        table.Columns.Add("note", typeof(object));
        table.Rows.Add(new PluginText("note", new string('x', 5000)));
        Assert.Equal(4096, Text("{'op':'cell','input':'t','row':0,'column':0}", table).Length);
    }

    // ---- a cell's number -------------------------------------------------------------------------------------------

    [Fact]
    public void A_cells_number_is_the_sources_own_number_its_unit_left_out() =>
        SceneExpressionTests.AssertExact(28.6f, Num("{'op':'cell','input':'t','row':0,'column':'high','field':'number'}"), "high, row 0");

    [Fact]
    public void A_negative_cell_number_is_kept() =>
        SceneExpressionTests.AssertExact(-3.25, Num("{'op':'cell','input':'t','row':1,'column':1,'field':'number'}"), "high, row 1");

    [Fact]
    public void A_cell_number_that_is_not_a_number_is_0() =>
        SceneExpressionTests.AssertExact(0, Num("{'op':'cell','input':'t','row':1,'column':'rain','field':'number'}"), "NaN");

    [Fact]
    public void A_cell_number_that_is_infinite_is_0() =>
        SceneExpressionTests.AssertExact(0, Num("{'op':'cell','input':'t','row':2,'column':'rain','field':'number'}"), "infinity");

    [Fact]
    public void A_text_cell_has_the_number_0_however_its_text_reads()
    {
        SceneExpressionTests.AssertExact(0, Num("{'op':'cell','input':'t','row':0,'column':'code','field':'number'}"), "'42'");
        Assert.Equal("42", Text("{'op':'cell','input':'t','row':0,'column':'code'}"));
    }

    [Fact]
    public void A_cells_number_is_never_read_from_its_text_even_a_plain_number()
    {
        // A table of plain .NET values (no source of this engine makes one): their text, and no numbers.
        var table = new DataTable();
        table.Columns.Add("v", typeof(double));
        table.Rows.Add(12.5);
        Assert.Equal("12.5", Text("{'op':'cell','input':'t','row':0,'column':0}", table));
        SceneExpressionTests.AssertExact(0, Num("{'op':'cell','input':'t','row':0,'column':0,'field':'number'}", table), "a double cell");
    }

    [Fact]
    public void Parse_reads_a_number_from_a_cells_text() =>
        SceneExpressionTests.AssertExact(42, Num("{'op':'parse','as':'number','value':{'op':'cell','input':'t','row':0,'column':'code'},'else':-1}"), "parse(cell)");

    [Fact]
    public void A_simulated_tables_made_up_numbers_are_its_number_cells_numbers()
    {
        // A simulated subscription makes a table up from the live one: each number cell shows a made-up number, and that
        // number is the cell's; a text cell's number stays 0.
        var real = new SceneTable(["n", "name"], [["12.5", "alpha"], ["3", "beta"]]);
        var made = SimulatedText.Table(Key, real, step: 7);
        for (var r = 0; r < 2; r++)
        {
            var shown = double.Parse(made.Text(r, 0), System.Globalization.CultureInfo.InvariantCulture);
            SceneExpressionTests.AssertExact(shown, made.Number(r, 0), $"row {r}");
            SceneExpressionTests.AssertExact(0, made.Number(r, 1), $"row {r}, a text cell");
        }
    }

    // ---- rows, and the bound of tableRows ------------------------------------------------------------------------

    [Fact]
    public void Rows_is_the_readings_row_count() =>
        SceneExpressionTests.AssertExact(3, Num("{'op':'input','name':'t','field':'rows'}"), "rows");

    /// <summary>A table of <paramref name="rows"/> rows whose cells count how often they are formatted.</summary>
    private static (DataTable Table, CountingText[] Cells) Long(int rows)
    {
        var table = new DataTable();
        table.Columns.Add("name", typeof(object));
        table.Columns.Add("ms", typeof(object));
        var cells = new CountingText[rows];
        for (var r = 0; r < rows; r++)
            table.Rows.Add(cells[r] = new CountingText($"row {r}"), new PluginSensor("ms", r + 0.5f, "ms"));
        return (table, cells);
    }

    private sealed class CountingText(string value) : PluginText("name", value)
    {
        public int Formatted;

        public override string ToString()
        {
            Interlocked.Increment(ref Formatted);
            return base.ToString();
        }
    }

    [Fact]
    public void A_4096_row_table_reads_through_cell()
    {
        var (table, _) = Long(4096);
        SceneExpressionTests.AssertExact(4096, Num("{'op':'input','name':'t','field':'rows'}", table), "rows");
        Assert.Equal("row 4095", Text("{'op':'cell','input':'t','row':4095,'column':'name'}", table));
        SceneExpressionTests.AssertExact(4095.5, Num("{'op':'cell','input':'t','row':4095,'column':'ms','field':'number'}", table), "row 4095's number");
    }

    [Fact]
    public void The_4097th_row_is_not_read()
    {
        var (table, cells) = Long(4097);
        SceneExpressionTests.AssertExact(4096, Num("{'op':'input','name':'t','field':'rows'}", table), "rows");
        Assert.Equal("", Text("{'op':'cell','input':'t','row':4096,'column':'name'}", table));
        Assert.Equal(0, cells[4096].Formatted);
    }

    [Fact]
    public void Only_the_rows_a_tick_reads_are_formatted()
    {
        var (table, cells) = Long(4096);
        Assert.Equal("row 2000", Text("{'op':'cell','input':'t','row':2000,'column':'name'}", table));
        Assert.Equal(1, cells[2000].Formatted);
        Assert.Equal(0, cells.Where((_, r) => r != 2000).Sum(c => c.Formatted));
    }

    // ---- each runtime's own tableRows -----------------------------------------------------------------------------

    /// <summary>A line of the rows, row 2's text and row 2's number, and a table node of up to 10 rows.</summary>
    private static readonly System.Text.Json.JsonElement RowTwo = SceneKit.Json("""
        {'format': 'infopanel.scene/1', 'inputs': {'t': {'type': 'table'}},
         'nodes': [
          {'kind': 'text', 'id': 'line', 'font': {'family': 'Inter', 'size': 10}, 'fill': '#ffffff', 'text': {'op': 'concat', 'args': [
            {'op': 'format', 'value': {'op': 'input', 'name': 't', 'field': 'rows'}}, '|',
            {'op': 'cell', 'input': 't', 'row': 2, 'column': 'day'}, '|',
            {'op': 'format', 'value': {'op': 'cell', 'input': 't', 'row': 2, 'column': 'high', 'field': 'number'}}]}},
          {'kind': 'table', 'input': 't', 'y': 20, 'rows': 10, 'header': false, 'columns': [{'column': 'day', 'w': 60}],
           'font': {'family': 'Inter', 'size': 10}, 'fill': '#ffffff'}]}
        """);

    private static SceneRuntime Runtime(ISceneInputSource inputs, int tableRows)
    {
        var runtime = new SceneRuntime(SceneKit.Options(inputs, limits: SpecLimits.Default with { TableRows = tableRows }));
        runtime.Load(SceneKit.Doc(RowTwo, 200, 200, bindings: new Dictionary<string, string> { ["t"] = Key.ToString() }));
        return runtime;
    }

    /// <summary>A tick's line, split at its bars, and the table node's cells (one a row drawn).</summary>
    private static (string[] Line, string[] Cells) Shown(SceneRuntime runtime, double elapsed)
    {
        using var tick = runtime.Tick(elapsed, SceneKit.Now);
        var texts = tick.Texts.Select(t => t.Text).ToList();
        return (texts.Single(t => t.Contains('|', StringComparison.Ordinal)).Split('|'), [.. texts.Where(t => !t.Contains('|', StringComparison.Ordinal))]);
    }

    [Fact]
    public void A_runtimes_own_table_rows_bound_its_reading()
    {
        var sensors = new FakeSensorReader();
        sensors.Readings[Key] = new SensorReading(Forecast(), "", "");
        using var runtime = Runtime(new SensorSceneInputs(sensors, new FakeSensorHistory()), 2);
        var (line, cells) = Shown(runtime, 0);
        Assert.Equal(["2", "", "0"], line);
        Assert.Equal(2, cells.Length);
    }

    [Fact]
    public void Two_runtimes_over_one_table_each_read_it_to_their_own_limit()
    {
        var sensors = new FakeSensorReader();
        sensors.Readings[Key] = new SensorReading(Forecast(), "", "");
        var inputs = new SensorSceneInputs(sensors, new FakeSensorHistory());
        using var small = Runtime(inputs, 2);
        using var large = Runtime(inputs, 4096);
        for (var k = 0; k < 4; k++)
        {
            // In turn, each first: neither runtime's reading changes the other's.
            var first = k % 2 == 0 ? small : large;
            var second = k % 2 == 0 ? large : small;
            var a = Shown(first, k / 10.0);
            var b = Shown(second, k / 10.0);
            var (smallShown, largeShown) = k % 2 == 0 ? (a, b) : (b, a);
            Assert.Equal("2||0 3|Wed|31", string.Join("|", smallShown.Line) + " " + string.Join("|", largeShown.Line));
            Assert.Equal((2, 3), (smallShown.Cells.Length, largeShown.Cells.Length));
        }
        // The two read the one conversion of the source's table, which holds the source's rows.
        var reading = inputs.Read(Key.ToString(), "table").Table;
        Assert.Same(reading, inputs.Read(Key.ToString(), "table").Table);
        Assert.Equal(3, reading.Rows.Count);
    }

    // ---- one reading per tick ------------------------------------------------------------------------------------

    [Fact]
    public void Overlapping_first_reads_of_a_row_all_get_the_one_row_kept()
    {
        // Every reader asks for the row before any has made it: each waits inside the making until all are there, so
        // the row is made by every reader at once, and the one row kept is the one each gets.
        const int readers = 8;
        using var making = new CountdownEvent(readers);
        var made = 0;
        var table = SceneTable.Lazy(["a", "n"], 1, r =>
        {
            Interlocked.Increment(ref made);
            making.Signal();
            making.Wait(TimeSpan.FromSeconds(10));
            return new SceneRow(["row " + r, "x"], [r + 0.5, 2]);
        });
        using var start = new Barrier(readers);
        var texts = new IReadOnlyList<string>[readers];
        var numbers = new IReadOnlyList<double>[readers];
        var threads = Enumerable.Range(0, readers).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            texts[i] = table.Rows[0];
            numbers[i] = table.Numbers![0];
        })).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Equal(readers, made);
        Assert.Equal(["row 0", "x"], texts[0]);
        Assert.Equal([0.5, 2], numbers[0]);
        Assert.All(texts, t => Assert.Same(texts[0], t));
        Assert.All(numbers, n => Assert.Same(numbers[0], n));
        Assert.Same(texts[0], table.Rows[0]);
        Assert.Equal(readers, made);
    }

    /// <summary>A table of <paramref name="rows"/> rows, every cell reading <paramref name="tag"/>.</summary>
    private static DataTable Tagged(int rows, string tag)
    {
        var table = new DataTable();
        table.Columns.Add("a", typeof(object));
        table.Columns.Add("n", typeof(object));
        for (var r = 0; r < rows; r++) table.Rows.Add(new PluginText("a", tag), new PluginSensor("n", rows, ""));
        return table;
    }

    [Fact]
    public void A_source_whose_rows_change_between_ticks_never_shows_a_mixed_tick()
    {
        // The source hands over a new table after every read, of another length and with other cells: within a tick the
        // runtime reads it once, so rows, cell and the table node all read that one table.
        var sensors = new FakeSensorReader();
        var published = 1;
        sensors.Readings[Key] = new SensorReading(Tagged(published, "t1"), "", "");
        sensors.AfterRead = key =>
        {
            if (key != Key) return;
            published++;
            sensors.Readings[Key] = new SensorReading(Tagged(published % 7 + 1, "t" + published), "", "");
        };
        var scene = SceneKit.Json("""
            {'format': 'infopanel.scene/1', 'inputs': {'t': {'type': 'table'}},
             'nodes': [
              {'kind': 'text', 'id': 'line', 'font': {'family': 'Inter', 'size': 10}, 'fill': '#ffffff', 'text': {'op': 'concat', 'args': [
                {'op': 'format', 'value': {'op': 'input', 'name': 't', 'field': 'rows'}}, '|',
                {'op': 'format', 'value': {'op': 'cell', 'input': 't', 'row': 0, 'column': 'n', 'field': 'number'}}, '|',
                {'op': 'cell', 'input': 't', 'row': 0, 'column': 'a'}, '|',
                {'op': 'cell', 'input': 't', 'row': {'op': 'sub', 'args': [{'op': 'input', 'name': 't', 'field': 'rows'}, 1]}, 'column': 'a'}]}},
              {'kind': 'table', 'input': 't', 'y': 20, 'rows': 10, 'header': false, 'columns': [{'column': 'a', 'w': 60}],
               'font': {'family': 'Inter', 'size': 10}, 'fill': '#ffffff'}]}
            """);
        using var runtime = new SceneRuntime(SceneKit.Options(new SensorSceneInputs(sensors, new FakeSensorHistory())));
        runtime.Load(SceneKit.Doc(scene, 200, 200, bindings: new Dictionary<string, string> { ["t"] = Key.ToString() }));

        for (var k = 0; k < 20; k++)
        {
            using var tick = runtime.Tick(k / 10.0, SceneKit.Now);
            var texts = tick.Texts.Select(t => t.Text).ToList();
            var parts = texts.Single(t => t.Contains('|', StringComparison.Ordinal)).Split('|');
            Assert.Equal(parts[0], parts[1]);
            Assert.Equal(parts[2], parts[3]);
            // The table node drew a cell for each row of the same reading (it draws up to 10).
            var cells = texts.Count(t => !t.Contains('|', StringComparison.Ordinal));
            Assert.Equal(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), cells);
        }
    }

    // ---- the validator ---------------------------------------------------------------------------------------------

    public static TheoryData<string, string, string, string> Refused() => new()
    {
        { "number", "{'op':'cell','input':'v','row':0,'column':0,'field':'number'}", "type.input", "/nodes/0/w/input" },
        { "text", "{'op':'cell','input':'v','row':0,'column':0,'field':'number'}", "type.input", "/nodes/0/w/input" },
        { "number", "{'op':'input','name':'v','field':'rows'}", "type.input", "/nodes/0/w" },
        { "text", "{'op':'input','name':'v','field':'rows'}", "type.input", "/nodes/0/w" },
        { "table", "{'op':'cell','input':'nope','row':0,'column':0,'field':'number'}", "ref.input", "/nodes/0/w/input" },
        { "table", "{'op':'cell','input':'v','row':'a','column':0,'field':'number'}", "schema", "/nodes/0/w/row" },
        { "table", "{'op':'cell','input':'v','row':0,'column':-1,'field':'number'}", "schema", "/nodes/0/w/column" },
        { "table", "{'op':'cell','input':'v','row':0,'column':0}", "schema", "/nodes/0/w" },
        { "table", "{'op':'cell','input':'v','row':0,'column':0,'field':'text'}", "schema", "/nodes/0/w/field" },
        { "text", "{'op':'parse','as':'date','value':{'op':'input','name':'v','field':'text'},'else':0}", "schema", "/nodes/0/w/as" },
        { "text", "{'op':'parse','as':'color','value':{'op':'input','name':'v','field':'text'},'else':0}", "schema", "/nodes/0/w/as" },
        { "text", "{'op':'parse','as':'number','value':{'op':'input','name':'v','field':'text'}}", "schema", "/nodes/0/w" },
        { "text", "{'op':'parse','as':'number','value':5,'else':0}", "schema", "/nodes/0/w/value" },
        { "text", "{'op':'parse','as':'number','value':'5','else':'#ffffff'}", "schema", "/nodes/0/w/else" },
        { "table", "{'op':'cell','input':'v','row':0,'column':'" + string.Concat(Enumerable.Repeat("😀", 129)) + "','field':'number'}", "schema", "/nodes/0/w/column" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void The_validator_refuses_at_its_code_and_path(string inputType, string w, string code, string path)
    {
        var scene = SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'v':{'type':'" + inputType + "'}},'nodes':[{'kind':'rect','h':4,'fill':'#ffffff','w':" + w + "}]}");
        var check = SceneValidator.Check(scene);
        Assert.Equal(CheckStatus.Invalid, check.Status);
        Assert.Equal((code, path), (check.Errors[0].Code, check.Errors[0].Path));
    }

    [Fact]
    public void A_cells_text_used_as_a_number_is_a_type_mismatch()
    {
        var scene = SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'v':{'type':'table'}},'values':{'c':{'op':'cell','input':'v','row':0,'column':0}},"
            + "'nodes':[{'kind':'rect','h':4,'fill':'#ffffff','w':{'op':'value','name':'c'}}]}");
        var check = SceneValidator.Check(scene);
        Assert.Equal(("type.mismatch", "/nodes/0/w"), (check.Errors[0].Code, check.Errors[0].Path));
    }

    [Fact]
    public void A_text_cell_counts_16_and_a_number_cell_1_as_the_runtime_counts()
    {
        var text = SceneValidator.Check(SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'v':{'type':'table'}},'values':{'c':{'op':'cell','input':'v','row':0,'column':0}},'nodes':[]}"));
        var number = SceneValidator.Check(SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'v':{'type':'table'}},'values':{'c':{'op':'cell','input':'v','row':0,'column':0,'field':'number'}},'nodes':[]}"));
        var parse = SceneValidator.Check(SceneKit.Json("{'format':'infopanel.scene/1','values':{'c':{'op':'parse','as':'number','value':'1','else':0}},'nodes':[]}"));
        Assert.Equal((16, 1, 16), (text.Cost.Work, number.Cost.Work, parse.Cost.Work));
        Assert.Equal((16, 1, 16), (RuntimeWork("{'op':'cell','input':'t','row':0,'column':0}"),
            RuntimeWork("{'op':'cell','input':'t','row':0,'column':0,'field':'number'}"), RuntimeWork("{'op':'parse','as':'number','value':'1','else':0}")));
    }

    /// <summary>The work the runtime counts evaluating <paramref name="expr"/> once, as the value of a scene over the forecast.</summary>
    private static int RuntimeWork(string expr)
    {
        var check = SceneValidator.Check(SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'t':{'type':'table'}},'values':{'x':" + expr + "},'nodes':[]}"));
        Assert.Equal(CheckStatus.Valid, check.Status);
        var scene = CompiledScene.Compile(check.Scene!, check.Cost, SpecLimits.Default);
        var fake = new FakeSceneInputs();
        Table(Forecast())(fake);
        var e = new Ev
        {
            Values = new Val[scene.ValueCount],
            State = [],
            Inputs = [new InputSnap(fake.Read("test:t", "table"), "now", [])],
            Clock = new SceneClock(0, SceneKit.Now, TimeZoneInfo.Utc, RegionalFormat.Invariant),
            MeasureW = [],
            MeasureH = [],
        };
        foreach (var i in scene.PhaseBeforeState) e.Values[i] = scene.Values[i].Eval(e);
        return e.Work;
    }

    // ---- a column's name is up to 128 characters, counted as code points -------------------------------------------

    [Fact]
    public void A_column_name_of_65_emoji_is_accepted()
    {
        var scene = SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'v':{'type':'table'}},'nodes':[{'kind':'rect','h':4,'fill':'#ffffff','w':"
            + "{'op':'cell','input':'v','row':0,'column':'" + string.Concat(Enumerable.Repeat("😀", 65)) + "','field':'number'}}]}");
        var check = SceneValidator.Check(scene);
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path}")));
    }

    // ---- where a value may be any type ------------------------------------------------------------------------------

    /// <summary>
    /// Where a value may be any type, the schema tries each operation's shape and refuses a mistake at the expression,
    /// for these operations as for the older ones: each document's code and path as the engine gives them.
    /// </summary>
    public static TheoryData<string, string, string> AnyType() => new()
    {
        { "{'format':'infopanel.scene/1','values':{'p':{'op':'parse','as':'number','value':5,'else':0}},'nodes':[]}", "schema", "/values/p" },
        { "{'format':'infopanel.scene/1','inputs':{'v':{'type':'table'}},'values':{'c':{'op':'cell','input':'v','row':'x','column':0}},'nodes':[]}", "schema", "/values/c" },
        { "{'format':'infopanel.scene/1','values':{'m':{'op':'if','args':[true,{'op':'parse','as':'number','value':'5','else':0},'a']}},'nodes':[]}", "schema", "/values/m" },
        { "{'format':'infopanel.scene/1','values':{'q':{'op':'eq','args':[{'op':'parse','as':'number','value':5,'else':0},1]}},'nodes':[]}", "schema", "/values/q" },
        // An older operation in the same places.
        { "{'format':'infopanel.scene/1','values':{'p':{'op':'round','args':['a']}},'nodes':[]}", "schema", "/values/p" },
        { "{'format':'infopanel.scene/1','values':{'m':{'op':'if','args':[true,{'op':'round','args':[1.5]},'a']}},'nodes':[]}", "schema", "/values/m" },
        { "{'format':'infopanel.scene/1','values':{'q':{'op':'eq','args':[{'op':'round','args':['a']},1]}},'nodes':[]}", "schema", "/values/q" },
    };

    [Theory]
    [MemberData(nameof(AnyType))]
    public void Where_a_value_may_be_any_type_the_validator_refuses_at_its_code_and_path(string document, string code, string path)
    {
        var check = SceneValidator.Check(SceneKit.Json(document));
        Assert.Equal(CheckStatus.Invalid, check.Status);
        Assert.Equal((code, path), (check.Errors[0].Code, check.Errors[0].Path));
    }
}
