using InfoPanel.Core.Tests.Documents;
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

    private static async Task WaitUntil(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
