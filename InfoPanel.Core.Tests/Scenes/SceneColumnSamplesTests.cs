using System.Data;
using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// A table's column read as samples (spec/docs/bindings.md, "Samples"): <c>series</c> and <c>history</c> with a
/// <c>column</c>, <c>history</c>'s <c>percentile</c> and <c>share</c> (spec/docs/expressions.md, "Percentiles and
/// shares"), their cost, and what the validator refuses. The exact vectors are in spec/examples/expressions/vectors.json
/// (<see cref="ExpressionVectorTests"/>, over a history and over a column).
/// </summary>
public sealed class SceneColumnSamplesTests
{
    private static readonly SensorKey Key = SensorKey.Plugin("/test/frames");

    /// <summary>A table as a plugin hands it over: a text column, then a number column <c>ms</c> of <paramref name="ms"/>, one a row.</summary>
    private static DataTable Frames(params double[] ms)
    {
        var table = new DataTable();
        table.Columns.Add("frame", typeof(object));
        table.Columns.Add("ms", typeof(object));
        for (var r = 0; r < ms.Length; r++) table.Rows.Add(new PluginText("frame", "#" + r), new PluginSensor("ms", (float)ms[r], "ms"));
        return table;
    }

    /// <summary>
    /// 600 frame times: about 7 ms with a slow wave, a 20 ms frame every 25th and a 40 ms one every 100th (the trace and
    /// the distribution of frame-times.json and frame-histogram.json): 18 of 20 ms and 6 of 40 ms, so the 99th
    /// percentile, rank 594, is 20.
    /// </summary>
    internal static DataTable FrameTimes()
    {
        var ms = new double[600];
        for (var k = 0; k < ms.Length; k++)
            ms[k] = k % 100 == 99 ? 40 : k % 25 == 24 ? 20 : Math.Round(7 + 2.5 * Math.Sin(k / 9.0) + 1.5 * Math.Sin(k / 2.3), 2);
        return Frames(ms);
    }

    /// <summary>The engine's reading of <paramref name="table"/> for the input <c>t</c> (or <paramref name="key"/>).</summary>
    internal static Action<FakeSceneInputs> Table(DataTable table, string key = "test:t") => fake =>
    {
        var sensors = new FakeSensorReader();
        sensors.Readings[Key] = new SensorReading(table, "", "summary");
        fake.Readings[key] = new SensorSceneInputs(sensors, new FakeSensorHistory()).Read(Key.ToString(), "table");
    };

    private static double Num(string expr, DataTable table) => SceneExpressionTests.Eval(expr, Table(table)).Value.N;

    // ---- the samples ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{'op':'history','input':'t','column':'ms','fn':'first','count':6}", 4)]
    [InlineData("{'op':'history','input':'t','column':'ms','fn':'last','count':6}", 42)]
    [InlineData("{'op':'history','input':'t','column':'ms','fn':'first','count':2}", 23)]
    [InlineData("{'op':'history','input':'t','column':'ms','fn':'min','count':6}", 4)]
    [InlineData("{'op':'history','input':'t','column':'ms','fn':'max','count':3}", 42)]
    [InlineData("{'op':'history','input':'t','column':'ms','fn':'avg','count':2}", 32.5)]
    [InlineData("{'op':'history','input':'t','column':1,'fn':'last','count':6}", 42)]
    public void A_columns_samples_are_its_numbers_in_row_order_the_last_row_newest(string expr, double expected) =>
        SceneExpressionTests.AssertExact(expected, Num(expr, Frames(4, 8, 15, 16, 23, 42)), expr);

    [Theory]
    [InlineData("{'op':'history','input':'t','column':'ms','fn':'count','count':100}", 6)]
    [InlineData("{'op':'history','input':'t','column':'ms','fn':'count','count':4}", 4)]
    public void Count_is_the_smaller_of_count_and_the_rows(string expr, double expected) =>
        SceneExpressionTests.AssertExact(expected, Num(expr, Frames(4, 8, 15, 16, 23, 42)), expr);

    [Fact]
    public void Nothing_is_filtered_a_text_cell_is_a_sample_of_0()
    {
        var table = Frames(5, 6, 7);
        table.Rows[1]["ms"] = new PluginText("ms", "12");
        Assert.Equal((3.0, 0.0, 7.0), (Num("{'op':'history','input':'t','column':'ms','fn':'count','count':10}", table),
            Num("{'op':'history','input':'t','column':'ms','fn':'min','count':10}", table),
            Num("{'op':'history','input':'t','column':'ms','fn':'last','count':10}", table)));
    }

    [Fact]
    public void A_sample_that_is_not_finite_is_0() =>
        SceneExpressionTests.AssertExact(0, Num("{'op':'history','input':'t','column':'ms','fn':'percentile','count':3,'p':0}", Frames(5, double.NaN, double.PositiveInfinity)), "percentile 0");

    [Theory]
    [InlineData("{'op':'history','input':'t','column':'nope','fn':'count','count':10}")]
    [InlineData("{'op':'history','input':'t','column':'MS','fn':'avg','count':10}")]
    [InlineData("{'op':'history','input':'t','column':2,'fn':'last','count':10}")]
    [InlineData("{'op':'history','input':'t','column':255,'fn':'percentile','count':10,'p':50}")]
    [InlineData("{'op':'history','input':'t','column':'nope','fn':'share','count':10}")]
    public void A_column_the_reading_lacks_has_no_samples(string expr) =>
        SceneExpressionTests.AssertExact(0, Num(expr, Frames(4, 8, 15)), expr);

    [Theory]
    [InlineData("{'op':'history','input':'t','column':'ms','fn':'count','count':10}")]
    [InlineData("{'op':'history','input':'t','column':'ms','fn':'share','count':10}")]
    public void An_unavailable_table_has_no_samples(string expr) =>
        SceneExpressionTests.AssertExact(0, SceneExpressionTests.Eval(expr).Value.N, expr);

    [Fact]
    public void A_column_reads_its_rows_once_made_and_kept()
    {
        // The values read the column three times in one tick: each row is made once and read from where it is kept.
        var table = Lazy(5, out var made);
        var (value, _, _) = SceneExpressionTests.Eval("{'op':'add','args':[{'op':'history','input':'t','column':'ms','fn':'first','count':5},"
            + "{'op':'history','input':'t','column':'ms','fn':'last','count':5},{'op':'history','input':'t','column':'ms','fn':'avg','count':5}]}",
            fake => fake.Readings["test:t"] = new SceneReading { Status = SceneInputStatus.Live, Table = table });
        SceneExpressionTests.AssertExact(0 + 6 + 3, value.N, "first + last + avg");
        Assert.Equal(5, made[0]);
    }

    private static SceneTable Lazy(int rows, out int[] made)
    {
        var counter = new int[1];
        made = counter;
        return SceneTable.Lazy(["frame", "ms"], rows, r =>
        {
            Interlocked.Increment(ref counter[0]);
            return new SceneRow(["#" + r, "x"], [0, r * 1.5]);
        });
    }

    // ---- through the runtime: one reading a tick, the runtime's own row limit ---------------------------------------------

    private static readonly System.Text.Json.JsonElement CountAndLast = SceneKit.Json("""
        {'format': 'infopanel.scene/1', 'inputs': {'t': {'type': 'table'}},
         'nodes': [
          {'kind': 'series', 'input': 't', 'column': 'ms', 'w': 100, 'h': 20, 'min': 0, 'max': 50, 'mode': 'bars', 'count': 100, 'fill': '#ffffff'},
          {'kind': 'text', 'y': 30, 'font': {'family': 'Inter', 'size': 10}, 'fill': '#ffffff', 'text': {'op': 'concat', 'args': [
            {'op': 'format', 'value': {'op': 'history', 'input': 't', 'column': 'ms', 'fn': 'count', 'count': 100}}, '|',
            {'op': 'format', 'value': {'op': 'history', 'input': 't', 'column': 'ms', 'fn': 'last', 'count': 100}}]}}]}
        """);

    private static string Shown(SceneRuntime runtime, double elapsed)
    {
        using var tick = runtime.Tick(elapsed, SceneKit.Now);
        return tick.Texts.Select(t => t.Text).Single(t => t.Contains('|', StringComparison.Ordinal));
    }

    [Fact]
    public void The_runtimes_own_table_rows_bound_the_samples()
    {
        var sensors = new FakeSensorReader();
        sensors.Readings[Key] = new SensorReading(Frames(4, 8, 15, 16, 23, 42), "", "");
        using var runtime = new SceneRuntime(SceneKit.Options(new SensorSceneInputs(sensors, new FakeSensorHistory()), limits: SpecLimits.Default with { TableRows = 3 }));
        runtime.Load(SceneKit.Doc(CountAndLast, 200, 60, bindings: new Dictionary<string, string> { ["t"] = Key.ToString() }));
        Assert.Equal("3|15", Shown(runtime, 0));
    }

    [Fact]
    public void A_tick_reads_a_columns_table_once_for_its_series_and_its_history()
    {
        // The source hands over a new table after every read, of another length: within a tick the series and the history
        // read the one reading, so each tick reads the source once and shows that table's rows.
        var sensors = new FakeSensorReader();
        var reads = 0;
        sensors.Readings[Key] = new SensorReading(Frames(1), "", "");
        sensors.AfterRead = key =>
        {
            if (key != Key) return;
            reads++;
            sensors.Readings[Key] = new SensorReading(Frames([.. Enumerable.Range(1, reads % 5 + 1).Select(i => (double)i)]), "", "");
        };
        using var runtime = new SceneRuntime(SceneKit.Options(new SensorSceneInputs(sensors, new FakeSensorHistory())));
        runtime.Load(SceneKit.Doc(CountAndLast, 200, 60, bindings: new Dictionary<string, string> { ["t"] = Key.ToString() }));
        for (var k = 0; k < 8; k++)
        {
            var before = reads;
            var shown = Shown(runtime, k / 10.0).Split('|');
            Assert.Equal(before + 1, reads);
            // The table this tick read: its rows 1 … n, so its count is its last sample.
            Assert.Equal(shown[0], shown[1]);
        }
    }

    [Fact]
    public void A_series_over_a_column_draws_the_bars_a_number_input_holding_the_same_samples_draws()
    {
        var ms = Enumerable.Range(0, 120).Select(k => Math.Round(10 + 8 * Math.Sin(k / 5.0), 2)).ToArray();
        var overColumn = SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'t':{'type':'table'}},'nodes':[{'kind':'series','input':'t','column':'ms','w':120,'h':40,'min':0,'max':20,'mode':'bars','pitch':1,'count':120,'fill':'#38bdf8','stroke':{'paint':'#ffffff','width':0.5}}]}");
        var overHistory = SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'t':{'type':'number'}},'nodes':[{'kind':'series','input':'t','w':120,'h':40,'min':0,'max':20,'mode':'bars','pitch':1,'count':120,'fill':'#38bdf8','stroke':{'paint':'#ffffff','width':0.5}}]}");
        var column = new FakeSceneInputs();
        Table(Frames(ms))(column);
        var history = new FakeSceneInputs();
        history.Number("test:t", ms[^1]);
        // The plugin's cell holds a float, as the history would hold the sensor's value.
        history.Histories["test:t"] = [.. ms.Select(v => (double)(float)v)];
        using var a = SceneKit.Render(SceneKit.Doc(overColumn, 140, 50, 10, 5), SceneKit.Options(column));
        using var b = SceneKit.Render(SceneKit.Doc(overHistory, 140, 50, 10, 5), SceneKit.Options(history));
        Assert.Equal(0, SceneKit.DifferingPixels(a, b, tolerance: 0));
        // And it drew bars at all.
        using var none = SceneKit.Render(SceneKit.Doc(overColumn, 140, 50, 10, 5), SceneKit.Options());
        Assert.NotEqual(0, SceneKit.DifferingPixels(a, none, tolerance: 0));
    }

    [Fact]
    public void A_series_over_a_column_the_reading_lacks_draws_nothing()
    {
        var scene = SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'t':{'type':'table'}},'nodes':[{'kind':'series','input':'t','column':'nope','w':120,'h':40,'min':0,'max':20,'mode':'bars','fill':'#38bdf8'}]}");
        var inputs = new FakeSceneInputs();
        Table(Frames(5, 10, 15))(inputs);
        using var drawn = SceneKit.Render(SceneKit.Doc(scene, 140, 50, 10, 5), SceneKit.Options(inputs));
        using var empty = SceneKit.Render(SceneKit.Doc(scene, 140, 50, 10, 5), SceneKit.Options());
        Assert.Equal(0, SceneKit.DifferingPixels(drawn, empty, tolerance: 0));
    }

    [Fact]
    public void The_trace_draws_its_p99_line_over_the_same_600_samples_as_its_bars()
    {
        // frame-times.json over 600 frames: the line's value is the 99th percentile of the very samples the bars draw.
        var table = FrameTimes();
        var ms = Enumerable.Range(0, 600).Select(r => (double)((PluginSensor)table.Rows[r]["ms"]).Value).Order().ToArray();
        var expected = ms[(int)Math.Ceiling(99 * 600 / 100.0) - 1];
        var value = SceneExpressionTests.Eval("{'op':'history','input':'t','column':'ms','fn':'percentile','count':600,'p':99}", Table(table)).Value.N;
        SceneExpressionTests.AssertExact(expected, value, "p99 of the trace");
        Assert.Equal(20, value);
    }

    // ---- the validator -------------------------------------------------------------------------------------------

    /// <summary>A scene with an input <c>v</c> of the stated type and one node: a series (<paramref name="node"/>) or a rect whose <c>w</c> is an expression.</summary>
    private static SceneCheck Check(string inputType, string node) =>
        SceneValidator.Check(SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'v':{'type':'" + inputType + "'}},'values':{'s':'text'},'nodes':[" + node + "]}"));

    private static string Rect(string w) => "{'kind':'rect','h':4,'fill':'#ffffff','w':" + w + "}";

    private static string Series(string members) => "{'kind':'series','input':'v','w':100,'h':20,'min':0,'max':1" + members + "}";

    /// <summary>Each row a typed context, its code and path the same in the editor's validator (its parity rows).</summary>
    public static TheoryData<string, string, string, string> Refused() => new()
    {
        { "number", Series(",'column':'ms'"), "type.input", "/nodes/0/input" },
        { "text", Series(",'column':'ms'"), "type.input", "/nodes/0/input" },
        { "table", Series(""), "type.input", "/nodes/0/input" },
        { "table", Series(",'column':-1"), "schema", "/nodes/0/column" },
        { "table", Series(",'column':''"), "schema", "/nodes/0/column" },
        { "table", Series(",'column':'" + string.Concat(Enumerable.Repeat("😀", 129)) + "'"), "schema", "/nodes/0/column" },
        { "number", Rect("{'op':'history','input':'v','column':'ms','fn':'avg','count':10}"), "type.input", "/nodes/0/w/input" },
        { "table", Rect("{'op':'history','input':'v','fn':'avg','count':10}"), "type.input", "/nodes/0/w/input" },
        { "table", Rect("{'op':'history','input':'nope','column':'ms','fn':'avg','count':10}"), "ref.input", "/nodes/0/w/input" },
        { "table", Rect("{'op':'history','input':'v','column':256,'fn':'avg','count':10}"), "schema", "/nodes/0/w/column" },
        { "number", Rect("{'op':'history','input':'v','fn':'percentile','count':60}"), "schema", "/nodes/0/w" },
        { "number", Rect("{'op':'history','input':'v','fn':'percentile','count':60,'p':101}"), "schema", "/nodes/0/w/p" },
        { "number", Rect("{'op':'history','input':'v','fn':'percentile','count':60,'p':-1}"), "schema", "/nodes/0/w/p" },
        { "number", Rect("{'op':'history','input':'v','fn':'percentile','count':60,'p':'a'}"), "schema", "/nodes/0/w/p" },
        { "number", Rect("{'op':'history','input':'v','fn':'percentile','count':60,'p':{'op':'value','name':'s'}}"), "type.mismatch", "/nodes/0/w/p" },
        { "number", Rect("{'op':'history','input':'v','fn':'percentile','count':60,'p':50,'lo':1}"), "schema", "/nodes/0/w/lo" },
        { "number", Rect("{'op':'history','input':'v','fn':'share','count':60,'p':50}"), "schema", "/nodes/0/w/p" },
        { "number", Rect("{'op':'history','input':'v','fn':'share','count':60,'lo':'x'}"), "schema", "/nodes/0/w/lo" },
        { "number", Rect("{'op':'history','input':'v','fn':'share','count':60,'hi':{'op':'value','name':'s'}}"), "type.mismatch", "/nodes/0/w/hi" },
        { "number", Rect("{'op':'history','input':'v','fn':'avg','count':60,'p':50}"), "schema", "/nodes/0/w/p" },
        { "number", Rect("{'op':'history','input':'v','fn':'max','count':60,'hi':5}"), "schema", "/nodes/0/w/hi" },
        // History's earlier refusals keep their paths with the three shapes.
        { "number", Rect("{'op':'history','input':'v','fn':'median','count':60}"), "schema", "/nodes/0/w/fn" },
        { "number", Rect("{'op':'history','input':'v','count':60}"), "schema", "/nodes/0/w" },
        { "number", Rect("{'op':'history','input':'v','fn':'avg','count':0}"), "schema", "/nodes/0/w/count" },
        { "number", Rect("{'op':'history','input':'v','fn':'avg','count':4097}"), "schema", "/nodes/0/w/count" },
        { "number", Rect("{'op':'history','input':'v','fn':'avg'}"), "schema", "/nodes/0/w" },
        { "number", Rect("{'op':'history','input':'v','fn':'avg','count':60,'extra':1}"), "schema", "/nodes/0/w/extra" },
        { "number", Rect("{'op':'history','input':'v','fn':'avg','count':60,'column':'ms'}"), "type.input", "/nodes/0/w/input" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void The_validator_refuses_at_its_code_and_path(string inputType, string node, string code, string path)
    {
        var check = Check(inputType, node);
        Assert.Equal(CheckStatus.Invalid, check.Status);
        Assert.Equal((code, path), (check.Errors[0].Code, check.Errors[0].Path));
    }

    [Theory]
    [InlineData("table", "{'kind':'series','input':'v','column':'ms','w':100,'h':20,'min':0,'max':1}")]
    [InlineData("table", "{'kind':'series','input':'v','column':0,'w':100,'h':20,'min':0,'max':1}")]
    [InlineData("table", "{'kind':'rect','h':4,'fill':'#ffffff','w':{'op':'history','input':'v','column':'ms','fn':'percentile','count':600,'p':{'op':'value','name':'n'}}}")]
    [InlineData("number", "{'kind':'rect','h':4,'fill':'#ffffff','w':{'op':'history','input':'v','fn':'share','count':600,'lo':{'op':'value','name':'n'},'hi':16.7}}")]
    [InlineData("number", "{'kind':'rect','h':4,'fill':'#ffffff','w':{'op':'history','input':'v','fn':'percentile','count':60,'p':0}}")]
    [InlineData("number", "{'kind':'rect','h':4,'fill':'#ffffff','w':{'op':'history','input':'v','fn':'share','count':60}}")]
    public void The_validator_accepts(string inputType, string node)
    {
        var check = SceneValidator.Check(SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'v':{'type':'" + inputType + "'}},'values':{'n':5},'nodes':[" + node + "]}"));
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")));
    }

    /// <summary>
    /// Where a value may be any type, the schema tries each operation's shape and refuses a mistake at the whole
    /// expression (a known difference: the editor's validator reports it at the member); pinned for a percentile.
    /// </summary>
    [Fact]
    public void Where_a_value_may_be_any_type_a_percentiles_p_out_of_range_is_refused_at_the_expression()
    {
        var check = SceneValidator.Check(SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'v':{'type':'number'}},'values':{'q':{'op':'history','input':'v','fn':'percentile','count':10,'p':101}},'nodes':[]}"));
        Assert.Equal(CheckStatus.Invalid, check.Status);
        Assert.Equal(("schema", "/values/q"), (check.Errors[0].Code, check.Errors[0].Path));
    }

    // ---- a history's operands: dependencies and the work guard ---------------------------------------------------

    /// <summary>A scene with a number input <c>v</c>, the values given (each a JSON member list) and a state cell <c>c</c> of text.</summary>
    private static SceneCheck CheckValues(string values) =>
        SceneValidator.Check(SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'v':{'type':'number'}},'values':{" + values + "},'state':{'c':{'init':'text','next':'text'}},'nodes':[]}"));

    private const string QP = "'q':{'op':'history','input':'v','fn':'percentile','count':1,'p':";
    private const string QLo = "'q':{'op':'history','input':'v','fn':'share','count':1,'lo':";
    private const string QHi = "'q':{'op':'history','input':'v','fn':'share','count':1,'hi':";

    /// <summary>
    /// A history's <c>p</c>, <c>lo</c> and <c>hi</c> are walked for the values' order like any other operand: a cycle
    /// through one, and a value of the wrong type it names declared after it or a state cell of the wrong type, are
    /// refused there. Each row the editor's validator's too (its parity rows).
    /// </summary>
    public static TheoryData<string, string, string> RefusedOperands() => new()
    {
        { QP + "{'op':'value','name':'q'}}", "cycle.value", "/values/q/p" },
        { QLo + "{'op':'value','name':'q'}}", "cycle.value", "/values/q/lo" },
        { QHi + "{'op':'value','name':'q'}}", "cycle.value", "/values/q/hi" },
        { QP + "{'op':'value','name':'s'}},'s':'text'", "type.mismatch", "/values/q/p" },
        { "'s':'text'," + QP + "{'op':'value','name':'s'}}", "type.mismatch", "/values/q/p" },
        { QLo + "{'op':'value','name':'s'}},'s':'text'", "type.mismatch", "/values/q/lo" },
        { "'s':'text'," + QLo + "{'op':'value','name':'s'}}", "type.mismatch", "/values/q/lo" },
        { QHi + "{'op':'value','name':'s'}},'s':'text'", "type.mismatch", "/values/q/hi" },
        { "'s':'text'," + QHi + "{'op':'value','name':'s'}}", "type.mismatch", "/values/q/hi" },
        { QP + "{'op':'state','name':'c'}}", "type.mismatch", "/values/q/p" },
        { QLo + "{'op':'state','name':'c'}}", "type.mismatch", "/values/q/lo" },
        { QHi + "{'op':'state','name':'c'}}", "type.mismatch", "/values/q/hi" },
    };

    [Theory]
    [MemberData(nameof(RefusedOperands))]
    public void A_historys_operands_are_walked_for_cycles_and_types_whatever_the_order(string values, string code, string path)
    {
        var check = CheckValues(values);
        Assert.Equal([(code, path)], check.Errors.Select(e => (e.Code, e.Path)));
    }

    [Theory]
    [InlineData("'n':5," + QP + "{'op':'value','name':'n'}}")]
    [InlineData(QP + "{'op':'value','name':'n'}},'n':5")]
    [InlineData("'n':5,'q':{'op':'history','input':'v','fn':'share','count':1,'lo':{'op':'value','name':'n'},'hi':{'op':'value','name':'n'}}")]
    public void A_historys_operand_naming_a_number_value_is_accepted(string values)
    {
        var check = CheckValues(values);
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")));
    }

    /// <summary>The operations a history counts for the work guard, its operands' included: the same numbers in the editor's validator.</summary>
    [Theory]
    [InlineData("{'op':'history','input':'v','fn':'percentile','count':1,'p':{'op':'add','args':[{'op':'mul','args':[1,2]},{'op':'neg','args':[3]}]}}", 4)]
    [InlineData("{'op':'history','input':'v','fn':'share','count':1,'lo':{'op':'abs','args':[{'op':'dt'}]}}", 3)]
    [InlineData("{'op':'history','input':'v','fn':'share','count':1,'lo':{'op':'neg','args':[1]},'hi':{'op':'max','args':[{'op':'neg','args':[1]},{'op':'dt'}]}}", 5)]
    public void A_historys_operands_count_towards_its_operations(string history, int ops)
    {
        var scene = SpecJson.Read<Scene>(SceneKit.Json("{'format':'infopanel.scene/1','values':{'q':" + history + "},'nodes':[]}"));
        Assert.Equal(ops, SceneValidator.Rules.Ops(scene.Values!["q"]));
    }

    /// <summary>
    /// A tree of 34,953 operations hidden in a percentile's <c>p</c> at the end of a chain of 64 state cells: the guard
    /// counts it, so the state cells' types aren't narrowed over it 64 times; the scene is refused at the work limit
    /// having visited the tree about once.
    /// </summary>
    [Fact]
    public void An_operand_tree_counts_towards_the_work_guard_so_the_cells_are_not_narrowed_over_it()
    {
        var t1 = "{'op':'add','args':[" + string.Join(",", Enumerable.Repeat("{'op':'dt'}", 16)) + "]}";
        var t2 = "{'op':'add','args':[" + string.Join(",", Enumerable.Repeat(t1, 16)) + "]}";
        var t3 = "{'op':'add','args':[" + string.Join(",", Enumerable.Repeat(t2, 16)) + "]}";
        var tree = "{'op':'add','args':[" + string.Join(",", Enumerable.Repeat(t3, 8)) + "]}";
        const int treeOps = 34_953;
        var cells = string.Join(",", Enumerable.Range(0, 63).Select(i => "'s" + i + "':{'init':'#000000','next':{'op':'state','name':'s" + (i + 1) + "'}}"))
            + ",'s63':{'init':'#000000','next':{'op':'rgba','args':[{'op':'history','input':'v','fn':'percentile','count':1,'p':" + tree + "},0,0,1]}}";
        var json = SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'v':{'type':'number'}},'state':{" + cells + "},'nodes':[]}");

        var check = SceneValidator.Check(json);
        Assert.Equal([("limit.exprWork", "")], check.Errors.Select(e => (e.Code, e.Path)));

        var rules = new SceneValidator.Rules(SpecJson.Read<Scene>(json), SpecLimits.Default);
        rules.Run();
        Assert.Equal([("limit.exprWork", "")], rules.Errors.Select(e => (e.Code, e.Path)));
        Assert.True(rules.Visits < 2 * treeOps, $"{rules.Visits} expressions visited for a tree of {treeOps}");
    }

    // ---- the cost ------------------------------------------------------------------------------------------------

    /// <summary>A node whose work is counted, its input <c>t</c>'s type, and the work: the validator's and the runtime's.</summary>
    public static TheoryData<string, string, int> Costs() => new()
    {
        // history: 1, and 1 more per 64 samples; a percentile 1 more per 8.
        { "table", Rect("{'op':'history','input':'t','column':'ms','fn':'percentile','count':600,'p':99}"), 1 + 75 },
        { "table", Rect("{'op':'history','input':'t','column':'ms','fn':'share','count':600,'lo':8.4,'hi':16.7}"), 1 + 10 },
        { "table", Rect("{'op':'history','input':'t','column':'ms','fn':'avg','count':600}"), 1 + 10 },
        { "number", Rect("{'op':'history','input':'t','fn':'avg','count':600}"), 1 + 10 },
        { "number", Rect("{'op':'history','input':'t','fn':'percentile','count':1,'p':50}"), 1 + 1 },
        { "number", Rect("{'op':'history','input':'t','fn':'percentile','count':4096,'p':50}"), 1 + 512 },
        // The percentile's rounding up, at its boundary.
        { "number", Rect("{'op':'history','input':'t','fn':'percentile','count':7,'p':50}"), 1 + 1 },
        { "number", Rect("{'op':'history','input':'t','fn':'percentile','count':8,'p':50}"), 1 + 1 },
        { "number", Rect("{'op':'history','input':'t','fn':'percentile','count':9,'p':50}"), 1 + 2 },
        // A series over a column: 1 per 64 of the samples it may draw (its count, else 4,096); over a history, nothing.
        { "table", "{'kind':'series','input':'t','column':'ms','w':300,'h':80,'min':0,'max':33.4,'count':63}", 1 },
        { "table", "{'kind':'series','input':'t','column':'ms','w':300,'h':80,'min':0,'max':33.4,'count':64}", 1 },
        { "table", "{'kind':'series','input':'t','column':'ms','w':300,'h':80,'min':0,'max':33.4,'count':65}", 2 },
        { "table", "{'kind':'series','input':'t','column':'ms','w':300,'h':80,'min':0,'max':33.4,'count':600}", 10 },
        { "table", "{'kind':'series','input':'t','column':'ms','w':300,'h':80,'min':0,'max':33.4}", 64 },
        { "number", "{'kind':'series','input':'t','w':300,'h':80,'min':0,'max':33.4,'count':600}", 0 },
    };

    [Theory]
    [MemberData(nameof(Costs))]
    public void A_column_read_and_a_percentile_cost_the_same_in_the_validator_and_the_runtime(string inputType, string node, int work)
    {
        var check = SceneValidator.Check(SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'t':{'type':'" + inputType + "'}},'nodes':[" + node + "]}"));
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")));
        Assert.Equal(work, check.Cost.Work);
        // The runtime counts the same: within a budget of exactly that work it evaluates, within one less it doesn't.
        Assert.True(Evaluates(check, inputType, work), "within its own work");
        if (work > 0) Assert.False(Evaluates(check, inputType, work - 1), "within one less");
    }

    private static bool Evaluates(SceneCheck check, string inputType, int workLimit)
    {
        var compiled = CompiledScene.Compile(check.Scene!, check.Cost, SpecLimits.Default);
        var rt = new ItemRuntime("x");
        rt.Use(compiled);
        var fake = new FakeSceneInputs();
        Table(FrameTimes())(fake);
        fake.Number("test:n", 7);
        fake.Histories["test:n"] = [.. Enumerable.Range(0, 600).Select(i => (double)i)];
        var reading = inputType == "table" ? fake.Read("test:t", "table") : fake.Read("test:n", "number");
        InputSnap[] inputs = [new InputSnap(reading, "now", inputType == "table" ? [] : fake.Histories["test:n"])];
        var evaluator = new SceneEvaluator(compiled, rt, new TickServices(), inputs, SceneClock.At(0, SceneKit.Now), 400, 100, new ItemDiagnostics("x"), workLimit);
        try
        {
            evaluator.Run(advance: false);
            return true;
        }
        catch (BudgetExceeded e) when (e.Code == "budget.work")
        {
            return false;
        }
    }
}
