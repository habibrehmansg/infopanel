using InfoPanel.Core.Tests.Golden;
using InfoPanel.Scenes;
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
}
