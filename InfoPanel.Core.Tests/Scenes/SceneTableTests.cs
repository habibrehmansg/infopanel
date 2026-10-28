using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The table node (spec/docs/smart-nodes.md, "table"): header-only tables, cells laid out as text in their column
/// (several lines, the first centred in its row, the rest over the rows below), and columns the table doesn't have
/// skipped. Pixel comparisons against equivalent tables and text nodes, so no golden is involved.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class SceneTableTests(ITestOutputHelper output)
{
    private static readonly SKColor Background = SKColor.Parse("#15191f");

    private static readonly SceneTable Processes = new(["Name", "CPU", "RAM"],
    [
        ["WindowServer", "12.5", "512 MB"],
        ["kernel_task", "3.0", "2 GB"],
        ["Finder", "0.4", "96 MB"],
        ["Dock", "0.1", "40 MB"],
    ]);

    private static SceneTable One(string cell) => new(["A"], [[cell]]);

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Table(string members, string columns = "[{'column':0,'w':120}]") =>
        "{'kind':'table','input':'t','columns':" + columns + ",'font':{'family':'Inter','size':16},'fill':'#ffffff'" + (members.Length > 0 ? "," + members : "") + "}";

    /// <summary>The frame of a scene with <paramref name="nodes"/> (table input <c>t</c> reading <paramref name="table"/>), and its item's ink box.</summary>
    private static (SKBitmap Frame, BoundsRect Ink) Draw(string nodes, SceneTable table, int width = 320, int height = 200)
    {
        var inputs = new FakeSceneInputs();
        inputs.Readings["test:t"] = new SceneReading { Status = SceneInputStatus.Live, Table = table };
        var scene = SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'t':{'type':'table'}},'nodes':[" + nodes + "]}");
        var check = SceneValidator.Check(scene);
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")));
        using var snapshot = SceneRuntime.RenderOnce(SceneKit.Doc(scene, width, height), SceneKit.Now, SceneKit.Options(inputs));
        var bounds = snapshot.Bounds.Single();
        Assert.Null(bounds.Problem);
        return (snapshot.Render(), bounds.Ink);
    }

    /// <summary>The rows (y) of <paramref name="frame"/> with ink between x0 and x1.</summary>
    private static List<int> InkRows(SKBitmap frame, int x0 = 0, int x1 = int.MaxValue)
    {
        var rows = new List<int>();
        for (var y = 0; y < frame.Height; y++)
            for (var x = x0; x < Math.Min(x1, frame.Width); x++)
            {
                var p = frame.GetPixel(x, y);
                if (Math.Abs(p.Red - Background.Red) > 40 || Math.Abs(p.Green - Background.Green) > 40 || Math.Abs(p.Blue - Background.Blue) > 40)
                {
                    rows.Add(y);
                    break;
                }
            }
        return rows;
    }

    /// <summary>Runs of consecutive rows: one per line of text.</summary>
    private static List<(int Top, int Bottom)> Bands(List<int> rows)
    {
        var bands = new List<(int, int)>();
        foreach (var y in rows)
        {
            if (bands.Count > 0 && bands[^1].Item2 == y - 1) bands[^1] = (bands[^1].Item1, y);
            else bands.Add((y, y));
        }
        return bands;
    }

    private static TextLayout Measure(string text) =>
        TextLayoutEngine.Layout(new TextLayoutKey { Runs = [new TextRun(text, "Inter", 16, 400, false)], BaseSize = 16 });

    [Fact]
    public void Rows_0_draws_the_header_only()
    {
        var (frame, ink) = Draw(Table("'rows':0,'rowHeight':20", "[{'column':0,'w':120},{'column':1,'w':80}]"), Processes);
        using (frame)
        {
            Assert.Equal(20, ink.H, 3);
            Assert.Equal(200, ink.W, 3);
            var rows = InkRows(frame);
            Assert.NotEmpty(rows);
            Assert.True(rows.Max() < 20, $"ink down to y {rows.Max()}: a data row drew");
        }

        var (none, noneInk) = Draw(Table("'rows':0,'header':false,'rowHeight':20", "[{'column':0,'w':120},{'column':1,'w':80}]"), Processes);
        using (none)
        {
            Assert.Equal(0, noneInk.H, 3);
            Assert.Empty(InkRows(none));
        }
    }

    [Theory]
    [InlineData("[{'column':0,'w':110},{'column':7,'w':60},{'column':2,'w':70,'align':'end'}]", "[{'column':0,'w':110},{'column':2,'w':70,'align':'end'}]")]
    [InlineData("[{'column':0,'w':110},{'column':7,'w':60,'title':'Gone'},{'column':2,'w':70,'align':'end'}]", "[{'column':0,'w':110},{'column':2,'w':70,'align':'end'}]")]
    [InlineData("[{'column':5,'w':40,'title':'Gone'},{'column':'Name','w':110},{'column':'Nope','w':60},{'column':'RAM','w':70}]", "[{'column':'Name','w':110},{'column':'RAM','w':70}]")]
    public void A_column_the_table_does_not_have_is_skipped(string with, string without)
    {
        var (a, inkA) = Draw(Table("'gap':10,'rowHeight':20", with), Processes);
        var (b, inkB) = Draw(Table("'gap':10,'rowHeight':20", without), Processes);
        using (a)
        using (b)
        {
            Assert.NotEmpty(InkRows(b));
            Assert.Equal(0, SceneKit.DifferingPixels(a, b));
            Assert.Equal(inkB, inkA);
            Assert.Equal(190, inkA.W, 3);
        }
    }

    [Fact]
    public void A_cell_wraps_into_lines_whose_first_sits_where_a_single_line_cell_does()
    {
        var w = Math.Ceiling(Measure("WWW").Width) + 2;
        var lineHeight = Measure("WWW").Lines[0].Height;
        Assert.True(Measure("WWW WWW").Width > w, "the column must be too narrow for two words");
        var columns = $"[{{'column':0,'w':{N(w)}}}]";
        const string members = "'header':false,'rowHeight':30,'wrap':'word','maxLines':3,'overflow':'visible'";

        var (multi, _) = Draw(Table(members, columns), One("WWW WWW WWW WWW"));
        var (single, _) = Draw(Table(members, columns), One("WWW"));
        // The same text as a text node: the column's width, the table's wrap, maxLines and overflow, its top where the
        // first line is centred in the 30 px row.
        var text = $"{{'kind':'text','text':'WWW WWW WWW WWW','x':0,'y':{N((30 - lineHeight) / 2)},'w':{N(w)},'wrap':'word','maxLines':3,'overflow':'visible','font':{{'family':'Inter','size':16}},'fill':'#ffffff'}}";
        var (asText, _) = Draw(text, One("x"));
        using (multi)
        using (single)
        using (asText)
        {
            var bands = Bands(InkRows(multi));
            output.WriteLine($"line height {lineHeight}; bands {string.Join(", ", bands)}");
            Assert.Equal(3, bands.Count);
            Assert.Equal(Bands(InkRows(single)).Single(), bands[0]);
            // One line apart, not one row apart, and over the row below.
            Assert.InRange(bands[1].Top - bands[0].Top, lineHeight - 1, lineHeight + 1);
            Assert.InRange(bands[2].Top - bands[1].Top, lineHeight - 1, lineHeight + 1);
            Assert.True(bands[2].Top > 30, "the third line runs over the row below");
            Assert.Equal(0, SceneKit.DifferingPixels(asText, multi));
        }
    }

    [Fact]
    public void A_cell_with_a_line_break_shows_its_first_line_with_an_ellipsis_by_default()
    {
        var (broken, _) = Draw(Table("'header':false"), One("ab\ncd"));
        var (cut, _) = Draw(Table("'header':false"), One("ab…"));
        using (broken)
        using (cut)
        {
            Assert.NotEmpty(InkRows(cut));
            Assert.Equal(0, SceneKit.DifferingPixels(cut, broken));
        }
    }

    [Fact]
    public void One_word_wrapped_line_with_an_ellipsis_is_the_unwrapped_line_with_an_ellipsis()
    {
        const string cell = "a cell much wider than its column";
        var (word, _) = Draw(Table("'header':false,'wrap':'word','maxLines':1,'overflow':'ellipsis'"), One(cell));
        var (none, _) = Draw(Table("'header':false,'overflow':'ellipsis'"), One(cell));
        using (word)
        using (none)
        {
            Assert.NotEmpty(InkRows(none));
            Assert.Equal(0, SceneKit.DifferingPixels(none, word));
        }
    }

    [Fact]
    public void One_word_wrapped_visible_line_draws_whole_glyphs_where_clip_cuts_one()
    {
        // The column ends halfway through the second word's first glyph.
        var first = Measure("WWW").Width;
        var w = Measure("WWW W").Width - Measure("W").Width / 2;
        var columns = $"[{{'column':0,'w':{N(w)}}}]";
        var (visible, _) = Draw(Table("'header':false,'wrap':'word','maxLines':1,'overflow':'visible'", columns), One("WWW WWW"));
        var (whole, _) = Draw(Table("'header':false,'overflow':'visible'", columns), One("WWW"));
        var (clip, _) = Draw(Table("'header':false,'overflow':'clip'", columns), One("WWW WWW"));
        using (visible)
        using (whole)
        using (clip)
        {
            Assert.Equal(0, SceneKit.DifferingPixels(whole, visible));
            var past = (int)Math.Ceiling(first) + 2;
            Assert.Empty(InkRows(visible, past, (int)Math.Ceiling(w) + 8));
            Assert.NotEmpty(InkRows(clip, past, (int)Math.Floor(w)));
            Assert.Empty(InkRows(clip, (int)Math.Ceiling(w) + 1));
        }
    }

    [Fact]
    public void Clip_hides_the_lines_past_the_row()
    {
        var lineHeight = Measure("WWW").Lines[0].Height;
        const string columns = "[{'column':0,'w':60}]";
        var (clip, _) = Draw(Table("'header':false,'wrap':'word','maxLines':3,'overflow':'clip'", columns), One("WWW WWW WWW"));
        var (visible, _) = Draw(Table("'header':false,'wrap':'word','maxLines':3,'overflow':'visible'", columns), One("WWW WWW WWW"));
        using (clip)
        using (visible)
        {
            var below = (int)Math.Ceiling(lineHeight) + 1;
            Assert.NotEmpty(InkRows(clip));
            Assert.DoesNotContain(InkRows(clip), y => y >= below);
            Assert.Contains(InkRows(visible), y => y >= below);
        }
    }

    private static string Tables(int count) =>
        string.Join(",", Enumerable.Range(0, count).Select(i =>
            $"{{\"kind\":\"table\",\"input\":\"t\",\"y\":{i * 10},\"rows\":100,\"wrap\":\"word\",\"maxLines\":2,\"overflow\":\"visible\",\"columns\":["
            + string.Join(",", Enumerable.Range(0, 16).Select(c => $"{{\"column\":{c},\"w\":40}}"))
            + "],\"font\":{\"family\":\"Inter\",\"size\":8},\"fill\":\"#ffffff\"}"));

    private static SceneReading TwoLineCells(int counter) => new()
    {
        Status = SceneInputStatus.Live,
        Table = new SceneTable(Enumerable.Range(0, 16).Select(c => "c" + c).ToArray(),
            Enumerable.Range(0, 100).Select(r => (IReadOnlyList<string>)Enumerable.Range(0, 16).Select(c => $"{r}:{c}\n{counter}").ToArray()).ToArray()),
    };

    private static SceneRuntime Runtime(int tables, FakeSceneInputs inputs)
    {
        var json = $"{{\"format\":\"infopanel.scene/1\",\"inputs\":{{\"t\":{{\"type\":\"table\"}}}},\"nodes\":[{Tables(tables)}]}}";
        var scene = JsonDocument.Parse(json, SpecJson.DocumentOptions).RootElement.Clone();
        var check = SceneValidator.Check(scene);
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Take(3).Select(e => $"{e.Code} {e.Path} {e.Message}")));
        var runtime = new SceneRuntime(SceneKit.Options(inputs));
        runtime.Load(SceneKit.Doc(scene, 800, 600));
        return runtime;
    }

    [Fact]
    public void Changing_tables_of_two_line_cells_are_bounded_by_the_text_budget()
    {
        var inputs = new FakeSceneInputs();
        inputs.Readings["test:t"] = TwoLineCells(0);
        using var runtime = Runtime(8, inputs);
        var watch = Stopwatch.StartNew();
        for (var tick = 0; tick < 2; tick++)
        {
            inputs.Readings["test:t"] = TwoLineCells(tick);
            using var s = runtime.Tick(tick * 0.1, SceneKit.Now);
            Assert.Equal("budget", s.Bounds.Single().Problem);
            Assert.Contains(s.Diagnostics, d => d.Code == "budget.text");
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"two ticks {watch.Elapsed}");
    }

    private static string WideTable(int columnWidth, int maxLines, int size) =>
        $"{{\"kind\":\"table\",\"input\":\"t\",\"rows\":100,\"header\":false,\"wrap\":\"word\",\"maxLines\":{maxLines},\"overflow\":\"visible\",\"columns\":["
        + string.Join(",", Enumerable.Range(0, 16).Select(c => $"{{\"column\":{c},\"w\":{columnWidth}}}"))
        + $"],\"font\":{{\"family\":\"Inter\",\"size\":{size}}},\"fill\":\"#ffffff\"}}";

    private static SceneRuntime TableRuntime(string node, FakeSceneInputs inputs, SceneRuntimeOptions? options = null)
    {
        var json = $"{{\"format\":\"infopanel.scene/1\",\"inputs\":{{\"t\":{{\"type\":\"table\"}}}},\"nodes\":[{node}]}}";
        var scene = JsonDocument.Parse(json, SpecJson.DocumentOptions).RootElement.Clone();
        var check = SceneValidator.Check(scene);
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Take(3).Select(e => $"{e.Code} {e.Path} {e.Message}")));
        var runtime = new SceneRuntime(options ?? SceneKit.Options(inputs));
        runtime.Load(SceneKit.Doc(scene, 800, 600));
        return runtime;
    }

    private static SceneReading Cells(Func<int, int, string> cell) => new()
    {
        Status = SceneInputStatus.Live,
        Table = new SceneTable(Enumerable.Range(0, 16).Select(c => "c" + c).ToArray(),
            Enumerable.Range(0, 100).Select(r => (IReadOnlyList<string>)Enumerable.Range(0, 16).Select(c => cell(r, c)).ToArray()).ToArray()),
    };

    [Fact]
    public void A_table_of_changing_cells_wrapped_to_the_most_lines_stops_at_the_text_budget()
    {
        // 100 × 16 cells of "W " × 2,000 in 20 px columns at 16 px, up to 1,000 lines each, the first character changing
        // every tick: 1,600 cells (within the item's 2,000 layouts) that would make 1.6 million lines. The budget counts
        // lines as they are made, so the item stops at it.
        var inputs = new FakeSceneInputs();
        SceneReading Tick(int tick) => Cells((_, _) => (char)('a' + tick) + string.Concat(Enumerable.Repeat(" W", 1_999)) + " ");
        inputs.Readings["test:t"] = Tick(0);
        using var runtime = TableRuntime(WideTable(20, 1_000, 16), inputs);
        for (var tick = 0; tick < 3; tick++)
        {
            inputs.Readings["test:t"] = Tick(tick);
            TickSnapshot s = null!;
            var lines = LinesLaidOut.While(() => s = runtime.Tick(tick * 0.1, SceneKit.Now));
            using (s)
            {
                Assert.Equal("budget", s.Bounds.Single().Problem);
                Assert.Contains(s.Diagnostics, d => d.Code == "budget.text");
            }
            Assert.InRange(lines, 1, SceneEvaluator.MaxItemLayouts);
        }
    }

    [Fact]
    public void Wrapped_tables_within_the_text_budget_are_drawn()
    {
        // 1,600 changing cells that each wrap to one line (1,600 lines), and a small table whose changing cells wrap to
        // two or three lines: both within the budget every tick, as before lines were counted.
        var inputs = new FakeSceneInputs();
        inputs.Readings["test:t"] = Cells((r, c) => $"{r}:{c}");
        using (var runtime = TableRuntime(WideTable(40, 1_000, 8), inputs))
            for (var tick = 0; tick < 2; tick++)
            {
                inputs.Readings["test:t"] = Cells((r, c) => $"{r}:{c}:{tick}");
                TickSnapshot s = null!;
                var lines = LinesLaidOut.While(() => s = runtime.Tick(tick * 0.1, SceneKit.Now));
                using (s)
                {
                    Assert.Null(s.Bounds.Single().Problem);
                    Assert.DoesNotContain(s.Diagnostics, d => d.Code.StartsWith("budget", StringComparison.Ordinal));
                }
                Assert.Equal(1_600, lines);
            }

        var small = new FakeSceneInputs();
        SceneReading Small(int tick) => new()
        {
            Status = SceneInputStatus.Live,
            Table = new SceneTable(Processes.Columns, Processes.Rows.Select(r => (IReadOnlyList<string>)r.Select(c => $"{c} {c} {tick}").ToArray()).ToArray()),
        };
        small.Readings["test:t"] = Small(0);
        using (var runtime = TableRuntime(Table("'wrap':'word','maxLines':3,'overflow':'visible'", "[{'column':0,'w':60},{'column':1,'w':60},{'column':2,'w':60}]").Replace('\'', '"'), small))
            for (var tick = 0; tick < 2; tick++)
            {
                small.Readings["test:t"] = Small(tick);
                TickSnapshot s = null!;
                var lines = LinesLaidOut.While(() => s = runtime.Tick(tick * 0.1, SceneKit.Now));
                using (s)
                {
                    Assert.Null(s.Bounds.Single().Problem);
                    Assert.DoesNotContain(s.Diagnostics, d => d.Code.StartsWith("budget", StringComparison.Ordinal));
                }
                Assert.InRange(lines, 16, 45); // header and 4 rows of 3 cells, most of them wrapped
            }
    }

    [Fact]
    public void An_unchanged_table_over_the_text_budget_keeps_no_more_lines_than_the_budget()
    {
        // 100 × 16 unchanged cells of "W " × 2,000 in 20 px columns, each wrapped to 1,000 lines: every tick lays out a
        // couple of cells before the budget refuses the item. Layouts kept from earlier ticks count against the budget
        // like new ones, and a refusal drops them, so what the item keeps never grows past the budget. The watchdog is
        // out of the way so every tick is evaluated.
        var inputs = new FakeSceneInputs();
        inputs.Readings["test:t"] = Cells((_, _) => string.Concat(Enumerable.Repeat("W ", 2_000)));
        var options = new SceneRuntimeOptions
        {
            Inputs = inputs, LocalZone = TimeZoneInfo.Utc, Locale = RegionalFormat.Invariant, ItemBudgetMs = double.MaxValue,
        };
        var node = WideTable(20, 1_000, 16).Replace("\"header\":false", "\"header\":false,\"rowHeight\":20", StringComparison.Ordinal);
        using var runtime = TableRuntime(node, inputs, options);
        for (var tick = 0; tick < 50; tick++)
        {
            using (var s = runtime.Tick(tick * 0.1, SceneKit.Now))
            {
                Assert.Equal("budget", s.Bounds.Single().Problem);
                Assert.Contains(s.Diagnostics, d => d.Code == "budget.text");
            }
            Assert.InRange(runtime.RetainedLines("item"), 0, SceneEvaluator.MaxItemLayouts);
        }
    }

    [Fact]
    public void Kept_cell_layouts_count_against_the_text_budget_and_a_refused_tick_keeps_none()
    {
        // One column of unchanged 900-line cells, one more row every tick: two rows (1,800 lines) are drawn, the second
        // made and the first kept; three are over the budget although only the third is new, and the item keeps nothing.
        var inputs = new FakeSceneInputs();
        var cell = string.Concat(Enumerable.Repeat("W ", 900));
        SceneReading Rows(int n) => new()
        {
            Status = SceneInputStatus.Live,
            Table = new SceneTable(["c0"], Enumerable.Range(0, n).Select(_ => (IReadOnlyList<string>)[cell]).ToArray()),
        };
        var options = new SceneRuntimeOptions
        {
            Inputs = inputs, LocalZone = TimeZoneInfo.Utc, Locale = RegionalFormat.Invariant, ItemBudgetMs = double.MaxValue,
        };
        var node = "{\"kind\":\"table\",\"input\":\"t\",\"header\":false,\"rowHeight\":20,\"wrap\":\"word\",\"maxLines\":1000,"
            + "\"overflow\":\"visible\",\"columns\":[{\"column\":0,\"w\":20}],\"font\":{\"family\":\"Inter\",\"size\":16},\"fill\":\"#ffffff\"}";
        inputs.Readings["test:t"] = Rows(1);
        using var runtime = TableRuntime(node, inputs, options);
        for (var tick = 0; tick < 4; tick++)
        {
            inputs.Readings["test:t"] = Rows(tick + 1);
            TickSnapshot s = null!;
            var lines = LinesLaidOut.While(() => s = runtime.Tick(tick * 0.1, SceneKit.Now));
            using (s)
            {
                if (tick < 2)
                {
                    Assert.Null(s.Bounds.Single().Problem);
                    Assert.InRange(lines, 900, 1_000); // the new row only
                    Assert.InRange(runtime.RetainedLines("item"), 900 * (tick + 1), 1_000 * (tick + 1));
                }
                else
                {
                    Assert.Equal("budget", s.Bounds.Single().Problem);
                    Assert.Contains(s.Diagnostics, d => d.Code == "budget.text");
                    Assert.Equal(0, runtime.RetainedLines("item"));
                }
            }
        }
    }

    [Fact]
    public void An_unchanged_table_keeps_its_cell_layouts()
    {
        // A table within the budget lays its cells out once; while nothing changes, later ticks reuse them.
        var inputs = new FakeSceneInputs();
        inputs.Readings["test:t"] = new SceneReading { Status = SceneInputStatus.Live, Table = Processes };
        using var runtime = TableRuntime(Table("'wrap':'word','maxLines':3,'overflow':'visible'",
            "[{'column':0,'w':60},{'column':1,'w':60},{'column':2,'w':60}]").Replace('\'', '"'), inputs);
        for (var tick = 0; tick < 5; tick++)
        {
            TickSnapshot s = null!;
            var lines = LinesLaidOut.While(() => s = runtime.Tick(tick * 0.1, SceneKit.Now));
            using (s)
            {
                Assert.Null(s.Bounds.Single().Problem);
                Assert.DoesNotContain(s.Diagnostics, d => d.Code.StartsWith("budget", StringComparison.Ordinal));
            }
            if (tick == 0) Assert.InRange(lines, 16, 45);
            else Assert.Equal(0, lines);
            Assert.InRange(runtime.RetainedLines("item"), 16, 45);
        }
    }

    /// <summary>One table of 100 × 16 changing two-line cells: tick times and what the tick reports, printed, not asserted.</summary>
    [Fact]
    [Trait("Category", "Performance")]
    public void A_full_table_of_changing_two_line_cells()
    {
        var inputs = new FakeSceneInputs();
        inputs.Readings["test:t"] = TwoLineCells(0);
        using var runtime = Runtime(1, inputs);
        var times = new List<double>();
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        for (var tick = 0; tick < 20; tick++)
        {
            inputs.Readings["test:t"] = TwoLineCells(tick);
            var watch = Stopwatch.StartNew();
            using var s = runtime.Tick(tick * 0.1, SceneKit.Now);
            times.Add(watch.Elapsed.TotalMilliseconds);
            foreach (var d in s.Diagnostics) codes.Add(d.Code);
            if (s.Bounds.Single().Problem is { } problem) codes.Add("problem:" + problem);
        }
        times.Sort();
        double P(double q) => times[(int)Math.Min(times.Count - 1, Math.Floor(q * times.Count))];
        output.WriteLine(FormattableString.Invariant($"1,600 two-line cells changing every tick: p50 {P(0.5):0.0} ms, p95 {P(0.95):0.0} ms, max {times[^1]:0.0} ms; reported: {string.Join(", ", codes)}"));
    }
}
