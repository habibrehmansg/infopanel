using InfoPanel.Drawing;
using InfoPanel.Models;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests;

/// <summary>Every renderer call at one tick sees the time the first call at that tick sampled.</summary>
public class FrameTimeTests
{
    private static readonly Profile Profile = new("Time", 300, 60) { BackgroundColor = "#FF000000" };

    // 03:04:09: a "%s" clock shows "9", and "10" a second later.
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 9, TimeSpan.Zero);

    private static ProfileRenderer Renderer(TimeProvider time) =>
        new(new FakeSensorReader(), new FakeSensorHistory(), new FakeImageSource(), new FakeAssetLocator(Path.GetTempPath()), time);

    private static ClockDisplayItem SecondsClock() => new("Clock", Profile) { X = 10, Y = 10, Font = "Inter", FontSize = 20, Format = "%s" };

    private static float MeasuredWidth(ProfileRenderer renderer, DisplayItem item, ProfileRenderState state) =>
        Assert.Single(renderer.MeasureBounds(Profile, [item], state)).W;

    private static byte[] Draw(ProfileRenderer renderer, DisplayItem item, ProfileRenderState state)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(Profile.Width, Profile.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            renderer.Draw(canvas, Profile, [item], state, new RenderOptions(Smoothing: false), 1);
        }

        return bitmap.Bytes;
    }

    [Fact]
    public void MeasureBounds_AfterDraw_AtTheSameTick_UsesTheDrawnTime()
    {
        var clock = SecondsClock();
        var nine = MeasuredWidth(Renderer(new ManualTimeProvider(Start)), clock, new ProfileRenderState());
        var ten = MeasuredWidth(Renderer(new ManualTimeProvider(Start.AddSeconds(1))), clock, new ProfileRenderState());
        Assert.True(ten > nine);

        var time = new ManualTimeProvider(Start);
        var renderer = Renderer(time);
        var state = new ProfileRenderState { Tick = 1 };

        Draw(renderer, clock, state);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(nine, MeasuredWidth(renderer, clock, state));

        state.Tick++;
        Assert.Equal(ten, MeasuredWidth(renderer, clock, state));
    }

    [Fact]
    public void Draws_AtTheSameTick_ShowTheSameMarqueeFrame_AndANewTickMovesIt()
    {
        var marquee = new TextDisplayItem("A marquee that is much wider than its box", Profile)
        {
            X = 10, Y = 10, Width = 120, Height = 30, Font = "Inter", FontSize = 20, Color = "#FFFFFFFF", Marquee = true, MarqueeSpeed = 100,
        };

        var time = new ManualTimeProvider(Start);
        var renderer = Renderer(time);
        var state = new ProfileRenderState { Tick = 1 };

        var first = Draw(renderer, marquee, state);
        time.Advance(TimeSpan.FromMilliseconds(500));
        var again = Draw(renderer, marquee, state);

        Assert.Equal(first, again);

        state.Tick++;
        Assert.NotEqual(first, Draw(renderer, marquee, state));
    }
}
