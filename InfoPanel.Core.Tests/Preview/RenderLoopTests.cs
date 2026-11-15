using InfoPanel.Core.Tests.Documents;
using InfoPanel.Core.Tests.Scenes;
using InfoPanel.Output;
using InfoPanel.Spec;
using SkiaSharp;
using System.Text.Json.Nodes;
using Xunit;

namespace InfoPanel.Core.Tests.Preview;

public class RenderLoopTests
{
    /// <summary>The bar document (unchanged inputs give unchanged frames).</summary>
    private static RenderHarness Bars(bool autoTick = false, TimeProvider? time = null) =>
        new(RenderHarness.BarDocument(), autoTick, time);

    /// <summary>The growing bar: every tick draws new pixels.</summary>
    private static RenderHarness Growing() => new(RenderHarness.GrowingDocument());

    private static byte[] Pixels(byte[] png, int width, int height)
    {
        using var bitmap = SKBitmap.Decode(png, new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        return bitmap.GetPixelSpan().ToArray();
    }

    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(1.1f, 1f)]
    [InlineData(1.13f, 1.25f)]
    [InlineData(2f, 2f)]
    [InlineData(0.2f, 0.25f)]
    [InlineData(0.12f, 0.1f)]
    [InlineData(0.01f, 0.1f)]
    [InlineData(9f, 4f)]
    [InlineData(0f, 1f)]
    [InlineData(-2f, 1f)]
    [InlineData(float.NaN, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    public void Scale_IsRoundedToAQuarter_AndClamped(float requested, float expected)
    {
        Assert.Equal(expected, FrameScale.Normalize(requested, 1920, 480));
    }

    [Theory]
    [InlineData(4096, 4096, 2f, 1f)]       // 16 Mpx exactly fits at 1
    [InlineData(4097, 4096, 1f, 0.75f)]
    [InlineData(16384, 16384, 1f, 0.25f)]  // 4096 × 4096
    [InlineData(20000, 20000, 1f, 0.2f)]   // below 0.25: the largest hundredth that fits
    [InlineData(1920, 480, 4f, 4f)]        // 7680 × 1920 = 14.7 Mpx
    [InlineData(3840, 1080, 4f, 2f)]
    public void Scale_IsReducedSoAFrameHasAtMost16Mpx(int width, int height, float requested, float expected)
    {
        var scale = FrameScale.Normalize(requested, width, height);
        Assert.Equal(expected, scale);
        var (w, h) = FrameScale.PixelSize(scale!.Value, width, height);
        Assert.True((long)w * h <= FrameScale.MaxPixels);
    }

    [Theory]
    [InlineData(16385, 16385)]                   // just over Workspace.MaxDimension: 0.25 still fits
    [InlineData(1_000_000, 1_000_000)]           // 0.1 would be 100,000² (40 GB): the cap wins over Min
    [InlineData(1_000_000, 50_000)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(50_000_000, 400)]
    public void Scale_IsReducedBelowMin_WhenThatIsWhatFitsTheCap(int width, int height)
    {
        foreach (var requested in new[] { 0.1f, 1f, 4f })
        {
            var scale = FrameScale.Normalize(requested, width, height);
            Assert.NotNull(scale);
            var (w, h) = FrameScale.PixelSize(scale.Value, width, height);
            Assert.True((long)w * h <= FrameScale.MaxPixels, $"{w}×{h} at {scale}");
            Assert.True(width * (double)scale >= 1 && height * (double)scale >= 1);
        }

        // The cap is reached, not undershot: about 16 Mpx for a profile this large.
        var (fw, fh) = FrameScale.PixelSize(FrameScale.Normalize(1, width, height)!.Value, width, height);
        Assert.True((long)fw * fh > FrameScale.MaxPixels / 2);
    }

    [Theory]
    [InlineData(1_000_000_000, 10)]  // fitting 16 Mpx would make it a tenth of a pixel tall
    [InlineData(1, int.MaxValue)]
    public void Scale_IsNull_WhenNoScaleFitsTheCapWithAWholePixelPerSide(int width, int height)
    {
        Assert.Null(FrameScale.Normalize(1, width, height));
    }

    [Theory]
    [InlineData(10, 6, 0.25f, 2, 2)]   // spec/docs/api.md: halves to the even integer at multiples of 0.25
    [InlineData(25, 25, 0.1f, 3, 3)]   // at 0.1 halves up (0.1 in single precision is a little over 0.1)
    [InlineData(1, 1, 0.1f, 1, 1)]     // at least 1
    [InlineData(1920, 480, 1.25f, 2400, 600)]
    public void FrameSides_AreRoundedAsTheApiSays(int width, int height, float scale, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), FrameScale.PixelSize(scale, width, height));
    }

    [Theory]
    [InlineData(1_000_000, 1_000_000, 320, 320, 320)]
    [InlineData(int.MaxValue, int.MaxValue, 4096, 4096, 4096)]
    [InlineData(1, int.MaxValue, 320, 1, Thumbnail.MaxHeight)]
    [InlineData(int.MaxValue, 1, 4096, 4096, 1)]
    [InlineData(16, 1_000_000_000, 4096, 1, Thumbnail.MaxHeight)]
    [InlineData(1, 16384, 320, 1, 4096)]     // the document format's extremes
    [InlineData(16384, 1, 320, 320, 1)]
    [InlineData(16384, 1, 99_999, 4096, 1)]
    [InlineData(1, 1, 5, 16, 16)]
    [InlineData(300, 100, 320, 320, 107)]
    public void ThumbnailSizes_AreBoundedInBothDimensions(int width, int height, int requested, int expectedWidth, int expectedHeight)
    {
        var (w, h, scale) = Thumbnail.Size(width, height, requested);
        Assert.Equal((expectedWidth, expectedHeight), (w, h));
        Assert.True(w <= Thumbnail.MaxWidth && h <= Thumbnail.MaxHeight && (long)w * h <= FrameScale.MaxPixels);
        Assert.True(scale > 0);
    }

    [Fact]
    public async Task TheLargestCanvas_IsRenderedAtAQuarter_AndSoIsItsThumbnailBounded()
    {
        await using var h = new RenderHarness(RenderHarness.Doc(16384, 16384));
        var sink = h.Attach(scale: 1);

        Assert.Equal(1, h.Tick());

        var frame = Assert.Single(sink.Frames).Frame;
        Assert.Equal((0.25f, 4096, 4096), (frame.Scale, frame.PixelWidth, frame.PixelHeight));

        using var thumbnail = SKBitmap.Decode(h.Service.RenderOnce(h.Document, 320));
        Assert.Equal((320, 320), (thumbnail.Width, thumbnail.Height));
    }

    [Theory]
    [InlineData(1, 16384, 1, 4096)]
    [InlineData(16384, 1, 4096, 1)]
    public async Task ThinCanvases_GiveThumbnailsWithinTheCaps(int width, int height, int expectedWidth, int expectedHeight)
    {
        await using var h = new RenderHarness(RenderHarness.Doc(width, height));
        using var thumbnail = SKBitmap.Decode(h.Service.RenderOnce(h.Document, 4096));
        Assert.Equal((expectedWidth, expectedHeight), (thumbnail.Width, thumbnail.Height));
    }

    [Fact]
    public async Task Sinks_AreGroupedByScale_AndShareOneFramePerGroup()
    {
        await using var h = Bars();
        var a = h.Attach(scale: 1);
        var b = h.Attach(scale: 1.1f);
        var c = h.Attach(scale: 2);

        Assert.Equal(3, h.Tick());

        var fa = Assert.Single(a.Frames).Frame;
        var fb = Assert.Single(b.Frames).Frame;
        var fc = Assert.Single(c.Frames).Frame;
        Assert.Same(fa, fb);
        Assert.NotSame(fa, fc);
        Assert.Equal((1f, 300, 100), (fa.Scale, fa.PixelWidth, fa.PixelHeight));
        Assert.Equal((2f, 600, 200), (fc.Scale, fc.PixelWidth, fc.PixelHeight));
        Assert.Equal((300, 100), (fc.ProfileWidth, fc.ProfileHeight));
        Assert.Equal(fa.Tick, fc.Tick);
        Assert.Equal(1, fa.Tick);
        Assert.NotEqual(fa.FrameId, fc.FrameId);

        // Bounds are measured once per tick, in profile coordinates, and shared by every group.
        Assert.Same(fa.Bounds, fc.Bounds);
        Assert.Equal(["bar", "shape"], fa.Bounds.Select(b => b.Id));
        Assert.Equal(new BoundsRect { X = 10, Y = 10, W = 100, H = 30 }, fa.Bounds[0].Frame);
        Assert.Equal(new BoundsRect { X = 220, Y = 50, W = 40, H = 40 }, fa.Bounds[1].Frame);
    }

    [Fact]
    public async Task AFrameOver16Mpx_IsRenderedAtAReducedScale()
    {
        await using var h = new RenderHarness(RenderHarness.Doc(4097, 4096));
        var sink = h.Attach(scale: 1);

        h.Tick();

        var frame = Assert.Single(sink.Frames).Frame;
        Assert.Equal(0.75f, frame.Scale);
        Assert.Equal((3073, 3072), (frame.PixelWidth, frame.PixelHeight));
    }

    [Fact]
    public async Task A_preview_at_scale_4_keeps_the_items_it_draws()
    {
        // Every timed draw takes 100 ms, twice the item budget; at scale 4 that counts 6.25 ms, so no item is suspended.
        await using var h = new RenderHarness(RenderHarness.BarDocument(), drawTime: new StepTime(100));
        var sink = h.Attach(scale: 4);

        for (var i = 0; i < 6; i++) h.Tick();

        Assert.NotEmpty(sink.Frames);
        Assert.All(sink.Frames, f => Assert.All(f.Frame.Bounds, b => Assert.Null(b.Problem)));
        Assert.Equal(4f, sink.Frames[^1].Frame.Scale);
    }

    [Fact]
    public async Task UnchangedFrames_AreNotSent_ARevisionOrANewSinkGetsOne()
    {
        await using var h = Bars();
        var sink = h.Attach();

        Assert.Equal(1, h.Tick());
        Assert.Equal(0, h.Tick());
        Assert.Equal(0, h.Tick());
        Assert.Single(sink.Frames);

        // A change that draws the same pixels (a shape's name) still bumps the revision: one more frame.
        var revision = h.Commit(DocKit.Attrs("shape", name: "Renamed")).Rev;
        Assert.Equal(1, h.Tick());
        Assert.Equal(revision, sink.Frames[^1].Frame.Rev);
        Assert.Equal(sink.Frames[0].Pixels, sink.Frames[^1].Pixels);
        Assert.Equal(0, h.Tick());

        // Changed pixels: a frame.
        h.SetLoad(100);
        Assert.Equal(1, h.Tick());
        Assert.NotEqual(sink.Frames[0].Pixels, sink.Frames[^1].Pixels);
        Assert.Equal(0, h.Tick());
        var count = sink.Frames.Count;

        // A new sink gets the unchanged frame; the old one doesn't.
        var late = h.Attach();
        Assert.Equal(1, h.Tick());
        Assert.Single(late.Frames);
        Assert.Equal(count, sink.Frames.Count);

        // A changed request counts as new.
        late.Request = late.Request with { Scale = 2 };
        h.Service.RequestChanged(late);
        Assert.Equal(1, h.Tick());
        Assert.Equal(600, late.Frames[^1].Frame.PixelWidth);
    }

    /// <summary>
    /// An edit that changes only which groups are drawn as layers is drawn, not kept: a hidden Porter-Duff child makes
    /// its parent a layer, so the green in a multiply group no longer multiplies the red below it (black) but is drawn on
    /// the layer's nothing (green).
    /// </summary>
    [Fact]
    public async Task An_edit_that_makes_a_group_a_layer_draws_new_pixels()
    {
        static JsonObject Scene(bool isolated) => JsonNode.Parse(
            """{"format": "infopanel.scene/1", "nodes": [{"kind": "rect", "w": 40, "h": 20, "fill": "#ff0000"}, {"kind": "group", "children": ["""
            + """{"kind": "group", "blend": "multiply", "children": [{"kind": "rect", "w": 20, "h": 20, "fill": "#00ff00"}]}"""
            + (isolated ? """, {"kind": "group", "visible": false, "blend": "destination-in", "children": []}""" : "") + "]}]}")!.AsObject();
        static byte[] At(byte[] pixels, int x, int y) => pixels[((y * 40 + x) * 4)..((y * 40 + x) * 4 + 4)];
        await using var h = new RenderHarness(RenderHarness.Doc(40, 20, DocKit.Scene("s", 0, 0, Scene(false))));
        var sink = h.Attach();

        Assert.Equal(1, h.Tick());
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, At(sink.Frames[^1].Pixels, 10, 10));

        h.Commit(DocKit.Design("s", Scene(true)));
        Assert.Equal(1, h.Tick());
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, At(sink.Frames[^1].Pixels, 10, 10));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(sink.Frames[^1].Pixels, 30, 10));
    }

    [Fact]
    public async Task EachSink_GetsAtMostItsOwnFrameRate()
    {
        await using var h = Bars();
        var fast = h.Attach(maxFps: 30);
        var slow = h.Attach(maxFps: 1);
        var period = 1000.0 / 30;

        // 3 seconds of ticks at the loop's rate, with the content changing every tick.
        var elapsed = 0.0;
        var slowTimes = new List<double>();
        for (var i = 0; i < 90; i++)
        {
            h.SetLoad(i % 2 == 0 ? 10 : 90);
            var before = slow.Frames.Count;
            h.Clock.Advance(TimeSpan.FromMilliseconds(period));
            elapsed += period;
            h.Service.Tick(h.ProfileId);
            if (slow.Frames.Count > before)
                slowTimes.Add(elapsed);
        }

        Assert.InRange(fast.Frames.Count, 88, 90);
        Assert.InRange(slow.Frames.Count, 3, 4); // the first tick, then one a second
        for (var i = 1; i < slowTimes.Count; i++)
            Assert.True(slowTimes[i] - slowTimes[i - 1] >= 1000 - period / 2, $"slow frames {slowTimes[i - 1]} and {slowTimes[i]} ms");

    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-5, 30)]
    [InlineData(1, 1)]
    [InlineData(45, 45)]
    [InlineData(1000, 60)]
    public void FrameRates_DefaultTo30_AndAreCappedAt60(int requested, int effective)
    {
        Assert.Equal(effective, RenderLoop.EffectiveFps(new FrameRequest(1, requested)));
    }

    [Fact]
    public async Task AnimationState_AdvancesOncePerTick_WithSeveralScalesAndThumbnails()
    {
        // The same document in two services: one renders a single scale, the other three scales and a thumbnail
        // between ticks. Their scale-1 frames must match tick for tick while the bar grows.
        await using var single = Growing();
        await using var many = Growing();

        var reference = single.Attach(scale: 1);
        var one = many.Attach(scale: 1);
        many.Attach(scale: 2);
        many.Attach(scale: 0.5f);

        for (var tick = 1; tick <= 6; tick++)
        {
            single.Tick();
            many.Tick();
            Assert.NotEmpty(many.Service.RenderOnce(many.Document, 150));

            Assert.Equal(tick, reference.Frames[^1].Frame.Tick);
            Assert.Equal(tick, one.Frames[^1].Frame.Tick);
            Assert.Equal(reference.Frames[^1].Pixels, one.Frames[^1].Pixels);
        }

        Assert.Equal(6, one.Frames.Count);
        Assert.NotEqual(one.Frames[0].Pixels, one.Frames[^1].Pixels); // the bar did grow
    }

    [Fact]
    public async Task RenderOnce_WithoutALoop_IsAOneOffSnapshotAtInit()
    {
        await using var h = Growing();
        var first = h.Service.RenderOnce(h.Document, 300);
        h.Clock.Advance(1000);
        Assert.Equal(first, h.Service.RenderOnce(h.Document, 300)); // elapsed 0, state at init, whatever the time

        // A loop, then the loop's frames; once it has stopped, the one-off again.
        var sink = h.Attach();
        h.Tick(500);
        h.Tick(500);
        Assert.NotEqual(Pixels(first, 300, 100), sink.Frames[^1].Pixels);
        Assert.Equal(sink.Frames[^1].Pixels, Pixels(h.Service.RenderOnce(h.Document, 300), 300, 100));

        h.Service.Detach(sink);
        Assert.Equal(first, h.Service.RenderOnce(h.Document, 300));
    }

    [Fact]
    public async Task RenderOnce_WhileALoopRuns_IsTheLoopsFrameAtItsTick_AndLeavesItsNextFrameUnchanged()
    {
        // Two identical services tick in step while the bar grows; one also takes thumbnails at the profile's width
        // after each tick. Each thumbnail is that tick's frame byte for byte, and the loop's following frames match the
        // service that took none.
        await using var h = Growing();
        await using var reference = Growing();
        var sink = h.Attach();
        var referenceSink = reference.Attach();

        for (var i = 0; i < 4; i++)
        {
            h.Tick();
            reference.Tick();
            Assert.Equal(referenceSink.Frames[^1].Pixels, sink.Frames[^1].Pixels);

            var tick = sink.Frames[^1].Frame.Tick;
            for (var repeat = 0; repeat < 2; repeat++)
                Assert.Equal(sink.Frames[^1].Pixels, Pixels(h.Service.RenderOnce(h.Document, 300), 300, 100));

            h.Tick();
            reference.Tick();
            Assert.Equal(tick + 1, sink.Frames[^1].Frame.Tick);
            Assert.Equal(referenceSink.Frames[^1].Pixels, sink.Frames[^1].Pixels);
        }

        Assert.Equal(8, sink.Frames.Count);
        Assert.Equal(sink.Frames.Count, sink.Frames.Select(f => Convert.ToHexString(f.Pixels)).Distinct().Count());
    }

    [Fact]
    public async Task EachNewGeneration_IsLoaded_AndTicksNeverGoDown()
    {
        await using var h = Bars();
        var sink = h.Attach();
        h.Tick();
        Assert.Equal(1, sink.Frames[^1].Frame.Rev);

        var moved = h.Commit(DocKit.Place("shape", 100, 50)).Rev;
        h.Tick();
        var frame = sink.Frames[^1].Frame;
        Assert.Equal(moved, frame.Rev);
        Assert.Equal(new BoundsRect { X = 100, Y = 50, W = 40, H = 40 }, frame.Bounds.Single(b => b.Id == "shape").Frame);

        // Undo is a new revision over the earlier content: drawn as it was, at the newer revision.
        var doc = h.Document;
        var undone = doc.Undo(new HistoryRequest { Instance = doc.Instance, BaseRev = moved }).Result!.Rev;
        h.Tick();
        Assert.Equal(undone, sink.Frames[^1].Frame.Rev);
        Assert.Equal(sink.Frames[0].Pixels, sink.Frames[^1].Pixels);

        var ticks = sink.Frames.Select(f => f.Frame.Tick).ToList();
        Assert.Equal(ticks.Order(), ticks);
        Assert.Equal(ticks.Count, ticks.Distinct().Count());
    }

    [Fact]
    public async Task TheStatsOverlay_IsDrawnOnLiveFrames_NotOnThumbnails()
    {
        await using var plain = Bars();
        await using var h = Bars();
        h.Commit(DocKit.Profile(new JsonObject { ["statsOverlay"] = true }));
        var plainSink = plain.Attach();
        var sink = h.Attach();
        plain.Tick();
        h.Tick();

        // The band covers the top 15 rows; below it the frames are the same.
        Assert.NotEqual(plainSink.Frames[^1].Pixels, sink.Frames[^1].Pixels);
        var row = 300 * 4;
        Assert.Equal(plainSink.Frames[^1].Pixels.AsSpan(16 * row).ToArray(), sink.Frames[^1].Pixels.AsSpan(16 * row).ToArray());
        Assert.Equal(Pixels(plain.Service.RenderOnce(plain.Document, 300), 300, 100), Pixels(h.Service.RenderOnce(h.Document, 300), 300, 100));
    }

    [Theory]
    [InlineData("drawn")]
    [InlineData("refused")]
    [InlineData("lost")]
    public async Task TheStatsOverlay_NamesTheBackendThatDrewTheFrame(string how)
    {
        // The same profile with the overlay on the CPU, and through a GPU stand-in that draws the frame ("Metal"), refuses
        // its size, or is lost while drawing it (the CPU redraws that frame): those two say "CPU", as the CPU's frame does.
        static JsonObject Overlay() => DocKit.Profile(new JsonObject { ["statsOverlay"] = true });
        await using var cpu = Bars();
        cpu.Commit(Overlay());
        var device = new FakeGpuDevice { Refuses = how == "refused" ? _ => true : null };
        await using var h = new RenderHarness(RenderHarness.BarDocument(), gpu: () => device);
        h.Commit(Overlay());
        Assert.Equal(InfoPanel.Output.Gpu.GpuRendering.Metal, h.Service.Gpu.Apply(true).Using);
        if (how == "lost")
            device.LoseAtSubmit = "Lost.";
        var reference = cpu.Attach();
        var sink = h.Attach();

        cpu.Tick();
        h.Tick();

        var row = 300 * 4;
        Assert.Equal(reference.Frames[^1].Pixels.AsSpan(16 * row).ToArray(), sink.Frames[^1].Pixels.AsSpan(16 * row).ToArray());
        if (how == "drawn")
            Assert.NotEqual(reference.Frames[^1].Pixels, sink.Frames[^1].Pixels);
        else
            Assert.Equal(reference.Frames[^1].Pixels, sink.Frames[^1].Pixels);
    }

    [Fact]
    public async Task OnTheGpu_ThePreviewsSinkAPanelsSinkAndTheThumbnail_GetTheSameBytes_AsOnTheCpu()
    {
        // A /ws-style sink (bounds) and a panel-style one (none) on one loop drawing through the GPU stand-in, and the
        // thumbnail at the profile's width: one frame for all, equal to the same service's frame on the CPU.
        var device = new FakeGpuDevice();
        await using var cpu = Growing();
        await using var h = new RenderHarness(RenderHarness.GrowingDocument(), gpu: () => device);
        Assert.Equal(InfoPanel.Output.Gpu.GpuRendering.Metal, h.Service.Gpu.Apply(true).Using);
        var preview = h.Attach(includeBounds: true);
        var panel = h.Attach(includeBounds: false);
        var reference = cpu.Attach();

        h.Tick(500);
        cpu.Tick(500);

        Assert.Equal(1, device.Surfaces);
        Assert.Equal(reference.Frames[^1].Pixels, preview.Frames[^1].Pixels);
        Assert.Equal(preview.Frames[^1].Pixels, panel.Frames[^1].Pixels);
        Assert.Same(preview.Frames[^1].Frame, panel.Frames[^1].Frame);
        Assert.Equal(preview.Frames[^1].Pixels, Pixels(h.Service.RenderOnce(h.Document, 300), 300, 100));
        Assert.Equal(2, device.Surfaces);
    }

    [Fact]
    public async Task TheLoop_Stops_WhenItsLastSinkDetaches()
    {
        await using var h = Bars();
        var a = h.Attach();
        var b = h.Attach();
        Assert.Equal(1, h.Service.LoopCount);

        h.Service.Detach(a);
        Assert.True(h.Service.IsRendering(h.ProfileId));
        h.Service.Detach(a); // twice is fine

        h.Service.Detach(b);
        Assert.False(h.Service.IsRendering(h.ProfileId));
        Assert.Equal(0, h.Service.LoopCount);
        Assert.Equal(0, h.Tick());

        // Attaching again starts a new loop, and the new sink gets a frame.
        var c = h.Attach();
        Assert.Equal(1, h.Tick());
        Assert.Single(c.Frames);
        Assert.Throws<InvalidOperationException>(() => h.Service.Attach(c));
    }

    [Fact]
    public async Task Attach_ToAnUnknownProfile_IsFalse()
    {
        await using var h = Bars();
        Assert.False(h.Service.Attach(new FakeSink(Guid.NewGuid().ToString(), new FrameRequest(1, 30))));
        Assert.Equal(0, h.Service.LoopCount);
    }

    [Fact]
    public async Task DeletingTheProfile_StopsItsLoop_AndClosesItsSinks()
    {
        await using var h = Bars();
        var sink = h.Attach();
        h.Tick();

        Assert.True(h.Workspace.DeleteWithItsTag(h.ProfileId));

        Assert.Equal([FrameSinkClosedReason.ProfileDeleted], sink.Closed);
        Assert.False(h.Service.IsRendering(h.ProfileId));
        Assert.Equal(0, h.Service.Tick(h.ProfileId));
        h.Service.Detach(sink); // already detached: a no-op
    }

    private static byte[] CreatedAgain(RenderHarness h) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["id"] = h.ProfileId, ["document"] = RenderHarness.BarDocument() });

    [Fact]
    public async Task AttachingADeletedDocument_IsRefused_AndLeavesTheLoopOfItsIdsNewDocumentAlone()
    {
        await using var h = Bars();
        var deleted = h.Document;
        Assert.True(h.Workspace.DeleteWithItsTag(h.ProfileId));
        Assert.Equal(201, h.Workspace.Create(CreatedAgain(h)).Status);
        var current = h.Attach();

        // A caller that read the deleted document before the id was created again.
        var late = new FakeSink(h.ProfileId, new FrameRequest(1, 30));
        Assert.False(h.Service.Attach(late, deleted));

        Assert.Equal(1, h.Tick());
        Assert.Single(current.Frames);
        Assert.Empty(late.Frames);
        Assert.True(h.Workspace.DeleteWithItsTag(h.ProfileId));
        Assert.Equal([FrameSinkClosedReason.ProfileDeleted], current.Closed);
        Assert.Equal(0, h.Service.LoopCount);
    }

    [Fact]
    public async Task ALoopOfADeletedDocument_StillListed_IsStoppedWhenItsIdsNewDocumentIsAttached()
    {
        await using var h = Bars();
        RenderService? service = null;
        var replacement = new FakeSink(h.ProfileId, new FrameRequest(1, 30));
        var events = 0;
        // Subscribed before the service, so each event reaches this handler first.
        h.Workspace.ProfilesChanged += (_, _) =>
        {
            switch (++events)
            {
                case 1: // the delete, not yet seen by the service: the id is created again
                    Assert.Equal(201, h.Workspace.Create(CreatedAgain(h)).Status);
                    break;
                case 2: // the create, before the service has seen either: the new document is attached
                    Assert.True(service!.Attach(replacement));
                    break;
            }
        };
        service = new RenderService(h.Workspace, RenderHarness.Scenes(h.Workspace, h.Sensors, h.History), h.Time, new RenderServiceOptions(AutoTick: false));
        try
        {
            var old = new FakeSink(h.ProfileId, new FrameRequest(1, 30));
            Assert.True(service.Attach(old));
            Assert.Equal(1, service.Tick(h.ProfileId));

            Assert.True(h.Workspace.DeleteWithItsTag(h.ProfileId));

            Assert.Equal([FrameSinkClosedReason.ProfileDeleted], old.Closed);
            Assert.Equal(1, service.LoopCount);
            Assert.Equal(1, service.Tick(h.ProfileId));
            Assert.Single(replacement.Frames);
            Assert.Single(old.Frames);
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeletingTheProfile_WhileASinkIsInOnFrame_ClosesItOnlyAfterOnFrameReturns_AndNoFrameFollowsAClose()
    {
        await using var h = Bars();
        var blocking = new BlockingSink(h.ProfileId, new FrameRequest(1, 30));
        Assert.True(h.Service.Attach(blocking));
        var other = h.Attach(); // the same group, served after the blocking sink

        h.Clock.Advance(34);
        var tick = Task.Run(() => h.Service.Tick(h.ProfileId), TestContext.Current.CancellationToken);
        blocking.Entered.Wait(TestContext.Current.CancellationToken);

        // The delete doesn't wait for the sink, and doesn't close it while OnFrame runs.
        Assert.True(h.Workspace.DeleteWithItsTag(h.ProfileId));
        Assert.Equal(["frame"], blocking.Events);
        Assert.Equal([FrameSinkClosedReason.ProfileDeleted], other.Closed); // not in OnFrame: closed at once

        blocking.Release.Set();
        Assert.Equal(1, await tick); // only the blocking sink got the frame: the other was closed before its turn

        Assert.Equal(["frame", "frame returned", "closed ProfileDeleted"], blocking.Events);
        Assert.Empty(other.Frames);
        Assert.Equal(0, h.Service.Tick(h.ProfileId));
    }

    [Fact]
    public async Task Detach_StopsFramesThatHaveNotStarted_WithoutOnClosed()
    {
        await using var h = Bars();
        var blocking = new BlockingSink(h.ProfileId, new FrameRequest(1, 30));
        Assert.True(h.Service.Attach(blocking));
        var other = h.Attach();
        var keep = h.Attach(); // keeps the loop running

        h.Clock.Advance(34);
        var tick = Task.Run(() => h.Service.Tick(h.ProfileId), TestContext.Current.CancellationToken);
        blocking.Entered.Wait(TestContext.Current.CancellationToken);
        h.Service.Detach(other);
        blocking.Release.Set();
        await tick;

        Assert.Empty(other.Frames);
        Assert.Empty(other.Closed);
        Assert.Single(keep.Frames);
    }

    [Fact]
    public async Task OneTick_ReadsEachSensorOnce_SoItsFramesBoundsAndThumbnailsAgree()
    {
        await using var h = Bars();
        var reads = 0;
        h.Sensors.AfterRead = key => h.SetLoad(++reads % 2 == 1 ? 90 : 20); // a new value after every read
        var one = h.Attach(scale: 1);
        h.Attach(scale: 2);
        h.Attach(scale: 0.5f);

        Assert.Equal(3, h.Tick());
        var thumbnail = h.Service.RenderOnce(h.Document, 300); // from the loop's tick
        Assert.Equal(1, reads); // three scales, the bounds and a thumbnail: one read

        // The thumbnail at the profile's width draws what the loop drew, though the sensor has changed since.
        Assert.Equal(one.Frames[^1].Pixels, Pixels(thumbnail, 300, 100));
        Assert.Equal(100, one.Frames[^1].Frame.Bounds[0].Frame.W); // 2 × 50, the value read

        // The next tick reads the new value.
        Assert.Equal(3, h.Tick());
        Assert.Equal(2, reads);
        Assert.NotEqual(one.Frames[0].Pixels, one.Frames[^1].Pixels);
    }

    [Fact]
    public async Task AThrowingSink_DoesNotStopTheLoopOrTheOtherSinks()
    {
        await using var h = Bars();
        var bad = h.Attach();
        bad.Throws = true;
        var good = h.Attach();

        Assert.Equal(2, h.Tick());
        h.SetLoad(100);
        Assert.Equal(2, h.Tick());

        Assert.Equal(2, good.Frames.Count);
        Assert.Equal(2, bad.Frames.Count);
        Assert.True(h.Service.IsRendering(h.ProfileId));
    }

    [Fact]
    public async Task ThePng_IsEncodedOnce_AndSharedByTheSinksOfAFrame()
    {
        await using var h = Bars();
        var a = h.Attach();
        var b = h.Attach();
        a.KeepPng = true;
        b.KeepPng = true;

        h.Tick();

        var pa = Assert.Single(a.Frames).Png;
        var pb = Assert.Single(b.Frames).Png;
        Assert.True(pa.Span.Overlaps(pb.Span));
        Assert.Equal(pa.Length, pb.Length);
        Assert.True(pa.Span.StartsWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }));

        using var decoded = SKBitmap.Decode(pa.ToArray());
        Assert.Equal((300, 100), (decoded.Width, decoded.Height));

        // After OnFrame: the image is gone, the encoded PNG stays readable.
        var frame = a.Frames[0].Frame;
        Assert.Throws<ObjectDisposedException>(() => frame.Image);
        Assert.Equal(pa.Length, frame.GetPng().Length);
    }

    [Fact]
    public async Task GetPng_AfterOnFrame_WithoutEncoding_Throws()
    {
        await using var h = Bars();
        var sink = h.Attach();
        h.Tick();
        Assert.Throws<InvalidOperationException>(() => sink.Frames[0].Frame.GetPng());
    }

    [Fact]
    public async Task Bounds_AreLeftOut_WhenNoSinkAsks()
    {
        await using var h = Bars();
        var sink = h.Attach(includeBounds: false);
        h.Tick();
        Assert.Empty(sink.Frames[0].Frame.Bounds);
    }

    [Fact]
    public async Task Dispose_ClosesEverySink()
    {
        var h = Bars();
        var sink = h.Attach();
        await h.DisposeAsync();
        Assert.Equal([FrameSinkClosedReason.Shutdown], sink.Closed);
        Assert.Throws<ObjectDisposedException>(() => h.Service.Attach(new FakeSink(h.ProfileId, new FrameRequest(1, 30))));
    }

    [Fact]
    public async Task AutoTicking_DeliversFrames_AndWakesOnAChange()
    {
        await using var h = Bars(autoTick: true, time: TimeProvider.System);
        var sink = h.Attach(maxFps: 30);
        var ct = TestContext.Current.CancellationToken;

        await WaitUntil(() => sink.Frames.Count >= 1, ct);
        var revision = h.Commit(DocKit.Place("shape", 100, 50)).Rev;
        await WaitUntil(() => sink.Frames.Count > 0 && sink.Frames[^1].Frame.Rev == revision, ct);

        h.Service.Detach(sink);
        Assert.False(h.Service.IsRendering(h.ProfileId));
        var count = sink.Frames.Count;
        await Task.Delay(150, ct);
        Assert.InRange(sink.Frames.Count, count, count + 1); // at most a tick already under way
    }

    [Fact]
    public async Task Static_document_is_drawn_once()
    {
        // A rect and a literal text: no time, input, media, animation or state, no stats overlay.
        var label = SceneKit.Obj("{'format': 'infopanel.scene/1', 'nodes': [{'kind': 'text', 'font': {'family': 'Inter', 'size': 24}, 'fill': '#ffffff', 'text': 'Hello'}]}");
        JsonObject StaticDoc() => RenderHarness.Doc(300, 100,
            DocKit.Scene("shape", 220, 50, DocKit.Rect("#00ff00", 40, 40)), DocKit.Scene("label", 10, 10, label.DeepClone().AsObject()));
        await using var h = new RenderHarness(StaticDoc());
        var sink = h.Attach(maxFps: 30);

        RenderLoop.DrawsMade = 0;
        for (var i = 0; i < 30; i++) h.Tick(34);

        Assert.Single(sink.Frames);
        Assert.True(RenderLoop.DrawsMade <= 1, $"{RenderLoop.DrawsMade} draws in 30 ticks of a static document");

        // A new sink still gets the frame, at another scale too.
        var late = h.Attach(scale: 2);
        h.Tick(34);
        Assert.Single(late.Frames);
        Assert.Equal(600, late.Frames[0].Frame.PixelWidth);
        Assert.Equal(sink.Frames[0].Frame.ContentHash, sink.Frames[^1].Frame.ContentHash);

        // The late sink's scale-2 picture is the content a fresh render of the same document at scale 2 draws.
        await using var fresh = new RenderHarness(StaticDoc());
        var expected = fresh.Attach(scale: 2);
        fresh.Tick(34);
        Assert.Equal(expected.Frames[0].Frame.ContentHash, late.Frames[0].Frame.ContentHash);
        Assert.NotEqual(sink.Frames[0].Frame.ContentHash, late.Frames[0].Frame.ContentHash);
    }

    [Fact]
    public async Task A_changing_time_or_state_is_drawn_every_tick()
    {
        var clock = SceneKit.Obj("{'format': 'infopanel.scene/1', 'nodes': [{'kind': 'rect', 'w': {'op': 'mod', 'args': [{'op': 'mul', 'args': [{'op': 'time', 'field': 'elapsed'}, 30]}, 200]}, 'h': 10, 'fill': '#0000ff'}]}");
        await using var h = new RenderHarness(RenderHarness.Doc(300, 100,
            DocKit.Scene("bar", 10, 10, RenderHarness.BarScene(), new JsonObject { ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = RenderHarness.Load.ToString() } } }),
            DocKit.Scene("clock", 10, 60, clock)));
        h.Attach();

        // Elapsed time moves the clock's bar every tick: every tick draws and sends a new frame.
        RenderLoop.DrawsMade = 0;
        for (var i = 0; i < 5; i++) Assert.Equal(1, h.Tick(34));
        Assert.Equal(5, RenderLoop.DrawsMade);

        // A state cell that grows every tick is drawn every tick.
        await using var g = Growing();
        var grown = g.Attach();
        RenderLoop.DrawsMade = 0;
        for (var i = 0; i < 5; i++) Assert.Equal(1, g.Tick(34));
        Assert.Equal(5, RenderLoop.DrawsMade);
        Assert.Equal(5, grown.Frames.Select(f => f.Frame.ContentHash).Distinct().Count());
    }

    [Fact]
    public async Task A_sensor_change_is_drawn_on_its_tick_and_only_then()
    {
        // Only the bound bar: nothing else on the canvas changes, so a draw and new pixels come from the sensor alone.
        await using var h = new RenderHarness(RenderHarness.Doc(300, 100,
            DocKit.Scene("bar", 10, 10, RenderHarness.BarScene(), new JsonObject { ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = RenderHarness.Load.ToString() } } })));
        var sink = h.Attach();

        RenderLoop.DrawsMade = 0;
        Assert.Equal(1, h.Tick(34));
        Assert.Equal(1, RenderLoop.DrawsMade);

        // Nothing changed: no draw, no frame.
        Assert.Equal(0, h.Tick(34));
        Assert.Equal(1, RenderLoop.DrawsMade);

        // The sensor changes: one draw and a frame with other pixels.
        var before = sink.Frames[^1].Pixels;
        h.SetLoad(90);
        Assert.Equal(1, h.Tick(34));
        Assert.Equal(2, RenderLoop.DrawsMade);
        Assert.NotEqual(before, sink.Frames[^1].Pixels);
    }

    [Fact]
    public async Task A_change_that_draws_the_same_pixels_is_delivered_without_a_draw()
    {
        await using var h = Bars();
        var sink = h.Attach();
        var large = h.Attach(scale: 2);
        Assert.Equal(2, h.Tick());
        Assert.Equal(0, h.Tick());

        // A shape's name: a new revision, the same pixels. Each sink gets it, as it would after a draw; nothing is drawn.
        RenderLoop.DrawsMade = 0;
        var revision = h.Commit(DocKit.Attrs("shape", name: "Renamed")).Rev;
        Assert.Equal(2, h.Tick());
        Assert.Equal(0, RenderLoop.DrawsMade);
        foreach (var s in new[] { sink, large })
        {
            Assert.Equal(2, s.Frames.Count);
            Assert.Equal(revision, s.Frames[^1].Frame.Rev);
            Assert.Equal(s.Frames[0].Pixels, s.Frames[^1].Pixels);
            Assert.Equal(s.Frames[0].Frame.ContentHash, s.Frames[^1].Frame.ContentHash);
        }
        Assert.Equal(0, h.Tick());
    }

    [Fact]
    public async Task An_animated_gif_is_drawn_once_per_scale_on_each_tick_its_frame_changes_and_not_between()
    {
        // Two 100 ms frames, red then blue, beside a still shape; sinks at scale 1 and 2.
        var gif = TestGif.Build(8, 8, new TestGif.Frame(0, 0, 8, 8, 1), new TestGif.Frame(0, 0, 8, 8, 3));
        var hash = InfoPanel.Documents.BlobStore.IdOf(gif);
        var picture = SceneKit.Obj($"{{'format': 'infopanel.scene/1', 'nodes': [{{'kind': 'media', 'w': 40, 'h': 40, 'source': {{'asset': '{hash}'}}}}]}}");
        await using var h = new RenderHarness(RenderHarness.Doc(300, 100,
            DocKit.Scene("shape", 220, 50, DocKit.Rect("#00ff00", 40, 40)),
            DocKit.Scene("picture", 20, 20, picture)));
        var assets = new MemoryAssets();
        assets.Assets[hash] = gif;
        using var media = SceneKit.SyncMedia(assets);
        var scenes = RenderHarness.Scenes(h.Workspace, h.Sensors, h.History);
        await using var service = new RenderService(h.Workspace, new InfoPanel.Scenes.SceneRuntimeOptions
        {
            Inputs = scenes.Inputs, Media = media, Limits = scenes.Limits, Support = EngineSupport.All,
            LocalZone = scenes.LocalZone, Locale = scenes.Locale,
        }, h.Time, new RenderServiceOptions(false));
        var small = new FakeSink(h.ProfileId, new FrameRequest(1, 30));
        var large = new FakeSink(h.ProfileId, new FrameRequest(2, 30));
        Assert.True(service.Attach(small, h.Document));
        Assert.True(service.Attach(large, h.Document));

        // Tick k is k × 34 ms into the animation: frame 0 below 100 ms, frame 1 to 200 ms, and round again.
        var shown = new Dictionary<int, (byte[] Small, byte[] Large)>();
        var previous = -1;
        var changes = 0;
        for (var k = 0; k < 24; k++)
        {
            var frame = (34 * k % 200) < 100 ? 0 : 1;
            var draws = RenderLoop.DrawsMade;
            var (smallCount, largeCount) = (small.Frames.Count, large.Frames.Count);
            if (k > 0) h.Clock.Advance(34);
            var delivered = service.Tick(h.ProfileId);
            if (frame == previous)
            {
                Assert.True(delivered == 0, $"tick {k}: {delivered} frames sent with the GIF's frame unchanged");
                Assert.Equal(draws, RenderLoop.DrawsMade);
                continue;
            }

            Assert.True(delivered == 2, $"tick {k}: {delivered} frames sent as the GIF's frame changed to {frame}");
            Assert.Equal(draws + 2, RenderLoop.DrawsMade);
            Assert.Equal(smallCount + 1, small.Frames.Count);
            Assert.Equal(largeCount + 1, large.Frames.Count);
            if (previous >= 0)
            {
                changes++;
                Assert.NotEqual(small.Frames[^2].Pixels, small.Frames[^1].Pixels);
                Assert.NotEqual(large.Frames[^2].Pixels, large.Frames[^1].Pixels);
            }
            // Each frame shows as it did the first time.
            if (shown.TryGetValue(frame, out var first))
            {
                Assert.Equal(first.Small, small.Frames[^1].Pixels);
                Assert.Equal(first.Large, large.Frames[^1].Pixels);
            }
            else shown[frame] = (small.Frames[^1].Pixels, large.Frames[^1].Pixels);
            previous = frame;
        }
        Assert.True(changes >= 6, $"{changes} frame changes in 24 ticks");
        Assert.Equal(600, large.Frames[^1].Frame.PixelWidth);
    }

    [Fact]
    public async Task Raster_media_at_scale_2_reuses_one_picture_of_the_canvas_across_ticks()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(40, 30, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(200, 100, 50));
        var png = InfoPanel.Core.Tests.Golden.GoldenImage.EncodePng(bitmap);
        var hash = InfoPanel.Documents.BlobStore.IdOf(png);
        var picture = SceneKit.Obj($"{{'format': 'infopanel.scene/1', 'nodes': [{{'kind': 'media', 'x': 0.3, 'y': 0.6, 'source': {{'asset': '{hash}'}}}}]}}");
        await using var h = new RenderHarness(RenderHarness.Doc(300, 100,
            DocKit.Scene("bar", 10, 60, RenderHarness.BarScene(), new JsonObject { ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = RenderHarness.Load.ToString() } } }),
            DocKit.Scene("picture", 200, 20, picture)));
        var assets = new MemoryAssets();
        assets.Assets[hash] = png;
        using var media = SceneKit.SyncMedia(assets);
        var scenes = RenderHarness.Scenes(h.Workspace, h.Sensors, h.History);
        await using var service = new RenderService(h.Workspace, new InfoPanel.Scenes.SceneRuntimeOptions
        {
            Inputs = scenes.Inputs, Media = media, Limits = scenes.Limits, Support = EngineSupport.All,
            LocalZone = scenes.LocalZone, Locale = scenes.Locale,
        }, h.Time, new RenderServiceOptions(false));
        var sink = new FakeSink(h.ProfileId, new FrameRequest(2, 30));
        Assert.True(service.Attach(sink, h.Document));

        // Every tick changes the bar, so every tick draws the raster above scale 1.
        var pictures = 0;
        InfoPanel.Scenes.SceneDrawer.PictureProbe = _ => pictures++;
        try
        {
            for (var i = 0; i < 30; i++)
            {
                h.SetLoad(i + 1);
                h.Clock.Advance(34);
                service.Tick(h.ProfileId);
            }
        }
        finally
        {
            InfoPanel.Scenes.SceneDrawer.PictureProbe = null;
        }

        Assert.Equal(30, sink.Frames.Count);
        Assert.Equal(600, sink.Frames[^1].Frame.PixelWidth);
        // The raster is drawn: its colour at its centre, (200.3 + 20, 20.6 + 15) × 2.
        var at = (71 * 600 + 440) * 4;
        Assert.Equal(new byte[] { 200, 100, 50, 255 }, sink.Frames[^1].Pixels[at..(at + 4)]);
        Assert.True(pictures <= 1, $"{pictures} canvas-size pictures allocated in 30 ticks");
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
