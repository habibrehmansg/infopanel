using System.Globalization;
using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Output;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// A clock whose every reading is a fixed number of milliseconds after the one before, so a draw timed with it measures
/// exactly one step, whatever the machine does.
/// </summary>
internal sealed class StepTime(double stepMs) : TimeProvider
{
    private long _now;

    public override long TimestampFrequency => 1_000_000;

    public override long GetTimestamp() => Interlocked.Add(ref _now, (long)(stepMs * 1_000));
}

/// <summary>
/// The draw watchdog counts a draw's time per canvas pixel (spec/docs/limits.md): a frame drawn at a scale above 1
/// counts its time divided by the scale squared; one at the canvas's size or smaller counts whole. The item budget is
/// 50 ms and every timed draw takes exactly the step given.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class SceneWatchdogTests
{
    /// <summary>
    /// Ticks a one-item document <paramref name="ticks"/> times, drawing each tick once at <paramref name="scale"/> with
    /// every draw taking <paramref name="drawMs"/>; the number of the first tick the item is drawn as over budget, else null.
    /// </summary>
    private static int? FirstSuspendedTick(double drawMs, float scale, int ticks)
    {
        var options = new SceneRuntimeOptions { LocalZone = TimeZoneInfo.Utc, DrawTime = new StepTime(drawMs) };
        using var runtime = new SceneRuntime(options);
        runtime.Load(SceneKit.Doc(SceneKit.Example("scene/valid/shapes.json"), 220, 180));
        int? first = null;
        for (var i = 1; i <= ticks; i++)
        {
            using var snapshot = runtime.Tick(i * 0.1, SceneKit.Now);
            var suspended = snapshot.Bounds.Single().Problem == "budget" && snapshot.Diagnostics.Any(d => d.Code == "budget.time");
            if (suspended) first ??= i;
            else Assert.Null(first); // a suspension lasts well past this test
            snapshot.Render(scale).Dispose();
        }
        return first;
    }

    [Fact]
    public void A_frame_larger_than_the_canvas_does_not_suspend_an_item_by_its_size()
    {
        // 100 ms at scale 4 counts 6.25 ms.
        Assert.Null(FirstSuspendedTick(100, 4f, 6));
    }

    [Fact]
    public void A_slow_draw_at_the_canvas_size_is_still_suspended()
    {
        // Ticks 1 to 3 draw slowly; tick 4 is evaluated and decides, and tick 5 is drawn as over budget.
        Assert.Equal(5, FirstSuspendedTick(100, 1f, 6));
    }

    [Fact]
    public void A_draw_slow_for_its_size_is_suspended_at_any_scale()
    {
        // 300 ms at scale 2 counts 75 ms.
        Assert.Equal(5, FirstSuspendedTick(300, 2f, 6));
    }

    [Fact]
    public void A_frame_at_twice_the_scale_counts_its_time_divided_by_four_not_by_two()
    {
        // 160 ms at scale 2 counts 40 ms, under the budget; divided by the scale alone it would count 80 ms.
        Assert.Null(FirstSuspendedTick(160, 2f, 6));
    }

    [Fact]
    public void A_frame_smaller_than_the_canvas_counts_its_whole_time()
    {
        // Not divided by 0.25 (40 ms would be 160), and not exempt (60 ms is over the budget).
        Assert.Null(FirstSuspendedTick(40, 0.5f, 6));
        Assert.Equal(5, FirstSuspendedTick(60, 0.5f, 6));
    }

    // ---- what a suspension's warning says ----------------------------------------------------------------------

    /// <summary>
    /// Ticks <paramref name="document"/> six times, drawing each tick once with <paramref name="draw"/>, every timed draw
    /// taking <paramref name="drawMs"/> on the wall clock and every span <paramref name="threadMs"/> of the thread's
    /// processor time; every event the runtime logged.
    /// </summary>
    private static List<LogEvent> Logged(JsonElement document, double drawMs, double threadMs, Action<TickSnapshot> draw, double budgetMs = 50,
        double labelThreadMs = 0, string? label = null)
    {
        var events = new List<LogEvent>();
        var options = new SceneRuntimeOptions
        {
            LocalZone = TimeZoneInfo.Utc,
            Locale = RegionalFormat.Invariant,
            DrawTime = new StepThreadTime(drawMs, threadMs, labelThreadMs, label),
            ItemBudgetMs = budgetMs,
            Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger(),
        };
        using var runtime = new SceneRuntime(options);
        var check = runtime.Load(document);
        var errors = check.Errors.Concat(check.Items.SelectMany(i => i.Errors)).Select(e => $"{e.Path} {e.Code} {e.Message}").ToList();
        Assert.True(check.Status == CheckStatus.Valid && errors.Count == 0, string.Join("; ", errors));
        for (var i = 1; i <= 6; i++)
        {
            using var snapshot = runtime.Tick(i * 0.1, SceneKit.Now);
            draw(snapshot);
        }
        return events;
    }

    private static JsonElement Shapes() => SceneKit.Doc(SceneKit.Example("scene/valid/shapes.json"), 220, 180);

    /// <summary>The suspension's slow ticks, as logged: each a structure of its figures by name.</summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> SlowTicks(LogEvent warning)
    {
        var sequence = Assert.IsType<SequenceValue>(warning.Properties["SlowTicks"]);
        return sequence.Elements
            .Select(e => (IReadOnlyDictionary<string, object?>)Assert.IsType<StructureValue>(e).Properties
                .ToDictionary(p => p.Name, p => Assert.IsType<ScalarValue>(p.Value).Value))
            .ToList();
    }

    /// <summary>The line as the log file and the recent entries render it.</summary>
    private static string Line(LogEvent e)
    {
        var text = new StringWriter();
        new MessageTemplateTextFormatter("{Message:lj}", CultureInfo.InvariantCulture).Format(e, text);
        return text.ToString();
    }

    private static readonly string[] Figures = ["Tick", "Stage", "WallMs", "ThreadMs", "GcPauseMs", "Gen0", "Gen1", "Gen2", "Scale", "Drawer", "Qos"];

    [Fact]
    public void A_suspension_says_for_each_of_its_three_slow_ticks_how_long_the_draw_took_and_why()
    {
        var events = Logged(Shapes(), drawMs: 100, threadMs: 1.5, s => s.Render(1).Dispose());

        // One line, for the one suspension, and nothing per tick.
        var warning = Assert.Single(events);
        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.StartsWith("Item item is suspended for 5 s: it took too long to evaluate or draw", Line(warning));
        var marks = SlowTicks(warning);
        Assert.Equal([1L, 2L, 3L], marks.Select(m => (long)m["Tick"]!));
        foreach (var mark in marks)
        {
            Assert.Equal(Figures, mark.Keys);
            Assert.Equal("draw", mark["Stage"]);
            Assert.Equal(100.0, mark["WallMs"]);
            Assert.Equal(1.5, mark["ThreadMs"]);
            Assert.True(Assert.IsType<double>(mark["GcPauseMs"]) >= 0);
            foreach (var gen in new[] { "Gen0", "Gen1", "Gen2" }) Assert.True(Assert.IsType<int>(mark[gen]) >= 0);
            Assert.Equal(1.0, mark["Scale"]);
            Assert.Equal("other", mark["Drawer"]);
            if (OperatingSystem.IsMacOS()) Assert.IsType<string>(mark["Qos"]);
            else Assert.Null(mark["Qos"]);
        }
        // The figures are in the line itself, which is all the log file and the recent entries keep.
        var text = Line(warning);
        Assert.True(text.Contains("\"Stage\":\"draw\"") && text.Contains("\"ThreadMs\":1.5"), text);
    }

    [Fact]
    public void Finding_out_who_draws_is_not_counted_in_the_draws_figures()
    {
        // Naming the drawer (a walk of the stack) costs 7 ms of the thread here, after the draw's own 1.5 ms.
        var events = Logged(Shapes(), drawMs: 100, threadMs: 1.5, s => s.Render(1).Dispose(), labelThreadMs: 7, label: "named");

        Assert.All(SlowTicks(Assert.Single(events)), m => Assert.Equal((1.5, "named"), ((double)m["ThreadMs"]!, (string)m["Drawer"]!)));
    }

    [Fact]
    public void A_draw_above_scale_1_is_logged_with_its_scale_and_its_whole_wall_time()
    {
        // 300 ms at scale 2 counts 75 ms; the line gives the 300 and the scale it is divided by.
        var events = Logged(Shapes(), drawMs: 300, threadMs: 280, s => s.Render(2).Dispose());

        var marks = SlowTicks(Assert.Single(events));
        Assert.All(marks, m => Assert.Equal((300.0, 280.0, 2.0), ((double)m["WallMs"]!, (double)m["ThreadMs"]!, (double)m["Scale"]!)));
    }

    [Fact]
    public void A_thumbnails_slow_draw_is_logged_as_a_thumbnails()
    {
        // A thumbnail as wide as the canvas is drawn at scale 1.
        var events = Logged(Shapes(), drawMs: 100, threadMs: 2, s => Thumbnail.RenderPng(s, 220));

        Assert.All(SlowTicks(Assert.Single(events)), m => Assert.Equal(("draw", "thumbnail"), (m["Stage"], m["Drawer"])));
    }

    [Fact]
    public void A_slow_evaluation_is_logged_as_an_evaluation_without_a_scale()
    {
        // With no budget at all, every evaluation is slow; it marks each tick before any of its draws can.
        var events = Logged(Shapes(), drawMs: 0, threadMs: 3, s => s.Render(1).Dispose(), budgetMs: 0);

        var marks = SlowTicks(Assert.Single(events));
        Assert.Equal([1L, 2L, 3L], marks.Select(m => (long)m["Tick"]!));
        Assert.All(marks, m =>
        {
            Assert.Equal(("evaluate", 3.0), (m["Stage"], m["ThreadMs"]));
            Assert.True((double)m["WallMs"]! >= 0); // the real time, rounded to 0.01 ms
            Assert.Null(m["Scale"]);
            Assert.Null(m["Drawer"]);
        });
    }

    [Fact]
    public void A_thread_clock_that_cant_be_read_is_logged_as_unknown()
    {
        var events = Logged(Shapes(), drawMs: 100, threadMs: double.NaN, s => s.Render(1).Dispose());

        Assert.All(SlowTicks(Assert.Single(events)), m => Assert.Null(m["ThreadMs"]));
    }

    [Fact]
    public void A_suspensions_line_carries_no_text_of_the_document()
    {
        // The profile's and the item's names and the text drawn stay out of the line and its properties.
        var document = SceneKit.Json("""
            {'format': 'infopanel.document/1', 'id': '00000000-0000-4000-8000-000000000001',
             'profile': {'name': 'Profile Name Words', 'width': 220, 'height': 180, 'background': '#000000ff'},
             'items': [{'kind': 'scene', 'id': 'item', 'name': 'Item Name Words', 'placement': {'x': 0, 'y': 0},
               'scene': {'format': 'infopanel.scene/1', 'nodes': [{'kind': 'text', 'font': {'family': 'Inter', 'size': 14}, 'fill': '#ffffff', 'text': 'Drawn Text Words'}]}}]}
            """);
        var drawn = new List<string>();
        var events = Logged(document, drawMs: 100, threadMs: 1, s =>
        {
            drawn.AddRange(s.Texts.Select(t => t.Text));
            s.Render(1).Dispose();
        });

        Assert.Contains("Drawn Text Words", drawn);
        var warning = Assert.Single(events);
        var everything = Line(warning) + string.Join(" ", warning.Properties.Values);
        Assert.Contains("\"Stage\":\"draw\"", everything);
        foreach (var words in new[] { "Profile Name", "Item Name", "Drawn Text" })
            Assert.DoesNotContain(words, everything);
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

/// <summary>
/// As <see cref="StepTime"/>, and a thread clock whose every reading is <paramref name="threadStepMs"/> after the one
/// before (none when it is NaN), so a span on either measures exactly one step; <see cref="Drawer"/> is the stack walk
/// that names who draws, with its own cost on the thread clock.
/// </summary>
internal sealed class StepThreadTime(double stepMs, double threadStepMs, double labelThreadMs = 0, string? label = null) : TimeProvider, IThreadTime
{
    private long _now, _thread;

    public override long TimestampFrequency => 1_000_000;

    public override long GetTimestamp() => Interlocked.Add(ref _now, (long)(stepMs * 1_000));

    /// <summary>Naming the drawer takes <c>labelThreadMs</c> of the thread, and gives <c>label</c> when there is one.</summary>
    public string? Drawer()
    {
        Interlocked.Add(ref _thread, (long)(labelThreadMs * 10_000));
        return label;
    }

    public TimeSpan? ThreadTime() => double.IsNaN(threadStepMs) ? null : TimeSpan.FromTicks(Interlocked.Add(ref _thread, (long)(threadStepMs * 10_000)));
}
