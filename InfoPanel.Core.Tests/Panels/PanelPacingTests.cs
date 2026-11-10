using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Documents;
using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Core.Tests.Preview;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Usb;
using SkiaSharp;
using Xunit;
using V = InfoPanel.Core.Tests.Panels.BeadaPanel.BeadaPanelVectors;

namespace InfoPanel.Core.Tests.Panels;

/// <summary>
/// A panel's pacing against a device that buffers frames: <see cref="BufferedPanel"/> keeps up to N frames and shows one
/// every 1 / R s; a write returns after its unloaded time while there is room, and waits for the next frame shown when
/// the buffer is full. Frames are offered as the render loop offers them (at the sink's request, the newest when the
/// panel's thread is busy), and all time is the fake clock's, moved by the device and the test only. A change is shown
/// within two frames of the device's when the writes keep its buffer nearly empty; a device that keeps up, and a raw
/// stream whose writes are long by nature, keep their rate; the frame rate setting is never exceeded.
/// </summary>
public sealed class PanelPacingTests
{
    private static readonly UsbDeviceId PanelId = new(0x4E58, 0x1001, 1, 4, "1-2");

    /// <summary>A small T113 (JPEG under auto) so a frame's transform and encode cost little real time.</summary>
    private const int Width = 64;

    private const int Height = 16;

    [Fact]
    public void A_change_is_shown_within_two_frames_when_the_device_takes_fewer_than_the_frame_rate()
    {
        using var run = new PacedRun(frameRate: 30, drainFps: 18, buffer: 40, unloadedMs: 4.5, PanelEncoding.Auto);
        run.RunFor(TimeSpan.FromSeconds(30));

        var change = run.Offer(rev: 2);
        var shownAfter = change.Shown - change.WriteStarted;
        Assert.True(shownAfter <= 2 * run.FrameInterval,
            $"the change was shown {shownAfter.TotalMilliseconds:0} ms after its write began (two of the device's frames: " +
            $"{2 * run.FrameInterval.TotalMilliseconds:0} ms); {run.Buffered} frames were waiting\n{run.Log}");
        // Not by sending next to nothing: the panel still shows most of what it can.
        var shown = run.ShownPerSecond(TimeSpan.FromSeconds(10));
        Assert.True(shown >= 0.6 * 18, $"the panel showed {shown:0.#} frames a second over the last 10 s\n{run.Log}");
        Assert.All(run.Requests, fps => Assert.InRange(fps, 1, 30));
    }

    [Fact]
    public void A_device_that_keeps_up_is_sent_the_frame_rate()
    {
        // Not a T113, which has a ceiling under the frame rate.
        using var run = new PacedRun(frameRate: 30, drainFps: 60, buffer: 40, unloadedMs: 4.5, PanelEncoding.Auto, platform: Other);
        run.RunFor(TimeSpan.FromSeconds(20));

        Assert.Equal(30, run.Sink.Request.MaxFps);
        Assert.InRange(run.ShownPerSecond(TimeSpan.FromSeconds(10)), 29, 31);
        Assert.All(run.Requests, fps => Assert.Equal(30, fps));
    }

    [Fact]
    public async Task Through_the_render_loop_a_change_is_shown_within_two_frames_when_the_device_takes_fewer_than_the_frame_rate()
    {
        // The render loop ticks at 30 fps and lets a sink through every tick when its pace is a little under that:
        // the panel's thread itself must keep to its pace.
        await using var run = new LoopRun(frameRate: 30, drainFps: 18, buffer: 40, unloadedMs: 4.5);
        run.RunFor(TimeSpan.FromSeconds(30));

        var change = run.Change();
        Assert.True(change.Shown - change.WriteStarted <= 2 * run.FrameInterval,
            $"the change was shown {(change.Shown - change.WriteStarted).TotalMilliseconds:0} ms after its write began (two of the device's " +
            $"frames: {2 * run.FrameInterval.TotalMilliseconds:0} ms); {run.Buffered} frames were waiting\n{run.Log}");
        Assert.True(change.Shown - change.Committed <= 2 * run.FrameInterval + run.TickInterval,
            $"the change was shown {(change.Shown - change.Committed).TotalMilliseconds:0} ms after its commit\n{run.Log}");
        var shown = run.ShownPerSecond(TimeSpan.FromSeconds(10));
        Assert.True(shown >= 0.6 * 18, $"the panel showed {shown:0.#} frames a second over the last 10 s\n{run.Log}");
    }

    [Fact]
    public void A_raw_stream_whose_writes_are_long_by_nature_keeps_a_change_within_two_frames_past_the_buffer_filling()
    {
        // 60 ms writes against a device that shows 16 a second (62.5 ms): a full buffer barely lengthens a write, so the
        // pace must stay under the thread's own rate. 60 s is well past the time a 40-frame buffer takes to fill.
        // Not a T113, whose raw ceiling would keep it under the thread's rate anyway.
        using var run = new PacedRun(frameRate: 30, drainFps: 16, buffer: 40, unloadedMs: 60, PanelEncoding.Raw, platform: Other);
        run.RunFor(TimeSpan.FromSeconds(60));

        Assert.False(run.Runner.Jpeg);
        var change = run.Offer(rev: 2);
        Assert.True(change.Shown - change.WriteStarted <= 2 * run.FrameInterval,
            $"the change was shown {(change.Shown - change.WriteStarted).TotalMilliseconds:0} ms after its write began; {run.Buffered} frames were waiting\n{run.Log}");
        // Not collapsed: most of what the device shows.
        var shown = run.ShownPerSecond(TimeSpan.FromSeconds(10));
        Assert.True(shown >= 0.75 * 16, $"the panel showed {shown:0.#} frames a second over the last 10 s\n{run.Log}");
    }

    [Fact]
    public void A_device_a_little_slower_than_a_high_frame_rate_is_detected_and_a_change_waits_two_frames_at_most()
    {
        // 60 fps asked of a device that shows 50: once its buffer is full every write is held back, so few of the last
        // writes are unloaded ones. Not a T113, whose ceiling is far under 50.
        using var run = new PacedRun(frameRate: 60, drainFps: 50, buffer: 40, unloadedMs: 4.5, PanelEncoding.Auto, platform: Other);
        run.RunFor(TimeSpan.FromSeconds(60));

        Assert.True(run.Sink.Request.MaxFps < 50, $"the panel is asked for {run.Sink.Request.MaxFps} fps\n{run.Log}");
        var change = run.Offer(rev: 2);
        Assert.True(change.Shown - change.WriteStarted <= 2 * run.FrameInterval,
            $"the change was shown {(change.Shown - change.WriteStarted).TotalMilliseconds:0} ms after its write began; {run.Buffered} frames were waiting\n{run.Log}");
        var shown = run.ShownPerSecond(TimeSpan.FromSeconds(10));
        Assert.True(shown >= 0.6 * 50, $"the panel showed {shown:0.#} frames a second over the last 10 s\n{run.Log}");
    }

    [Fact]
    public void A_short_burst_of_long_writes_on_a_fast_device_leaves_no_lasting_low_pace()
    {
        // An unbuffered device far faster than the setting; twenty frames take 50 ms each to write (large JPEGs), then
        // writes are short again. Not a T113, which has a ceiling under the frame rate.
        using var run = new PacedRun(frameRate: 30, drainFps: 120, buffer: 1, unloadedMs: 4.5, PanelEncoding.Auto, platform: Other);
        run.RunFor(TimeSpan.FromSeconds(10));
        run.Unloaded = TimeSpan.FromMilliseconds(50);
        for (var i = 0; i < 20; i++)
            run.Offer(rev: 1);
        run.Unloaded = TimeSpan.FromMilliseconds(4.5);
        run.RunFor(TimeSpan.FromSeconds(30));

        Assert.Equal(30, run.Sink.Request.MaxFps);
        var shown = run.ShownPerSecond(TimeSpan.FromSeconds(10));
        Assert.True(shown >= 29, $"the panel showed {shown:0.#} frames a second over the last 10 s\n{run.Log}");
    }

    [Theory]
    [InlineData(10, 60)] // a device far faster than the setting
    [InlineData(20, 18)] // one a little slower, so the pacing moves
    [InlineData(12, 18)] // the setting under what the device takes
    public void The_pacing_never_exceeds_the_frame_rate_setting(int frameRate, int drainFps)
    {
        using var run = new PacedRun(frameRate, drainFps, buffer: 40, unloadedMs: 4.5, PanelEncoding.Auto);
        run.RunFor(TimeSpan.FromSeconds(30));

        Assert.All(run.Requests, fps => Assert.InRange(fps, 1, frameRate));
        Assert.True(run.MostWrittenInASecond <= frameRate, $"{run.MostWrittenInASecond} frames were written in one second at {frameRate} fps");
    }

    [Fact]
    public void A_new_revision_is_logged_once_with_its_write_time()
    {
        using var run = new PacedRun(frameRate: 30, drainFps: 60, buffer: 40, unloadedMs: 4.5, PanelEncoding.Auto);
        run.Offer(rev: 7);
        run.Offer(rev: 7); // new pixels (a sensor moved), the same document
        run.Offer(rev: 8);

        var lines = run.Log.Lines.Where(l => l.Contains("wrote rev")).ToList();
        Assert.Equal(2, lines.Count);
        Assert.EndsWith("wrote rev 7, encode+write 5.5 ms", lines[0]);
        Assert.EndsWith("wrote rev 8, encode+write 5.5 ms", lines[1]);
    }

    [Fact]
    public void A_change_of_pace_is_logged_with_the_write_times()
    {
        // Not a T113, whose ceiling (15) is under this device's rate, so its pace wouldn't change.
        using var run = new PacedRun(frameRate: 30, drainFps: 18, buffer: 40, unloadedMs: 4.5, PanelEncoding.Auto, platform: Other);
        run.RunFor(TimeSpan.FromSeconds(30));

        var lines = run.Log.Lines.Where(l => l.Contains(": pacing ")).ToList();
        Assert.NotEmpty(lines);
        Assert.Matches(@"^Panel ""test panel"": pacing \d+ fps \(writes [\d.]+ ms, unloaded 4\.5 ms\)$", lines[0]);
        Assert.True(lines.Count <= 10, $"{lines.Count} pacing lines in 30 s:\n{string.Join("\n", lines)}");
    }

    [Fact]
    public async Task A_frame_rate_lowered_to_the_learned_pace_is_still_kept_at_the_write()
    {
        // A 30 fps loop lets a sink through every tick when its pace is a little under that. A 25 fps panel, its pace
        // learned at 30, then the setting lowered to 22: the panel's thread must still keep to 22.
        await using var run = new LoopRun(frameRate: 30, drainFps: 25, buffer: 40, unloadedMs: 4.5, platform: Other);
        run.RunFor(TimeSpan.FromSeconds(30));
        run.FrameRate = 22;
        var lowered = run.Now;
        run.RunFor(TimeSpan.FromSeconds(60));

        var most = run.MostWrittenInASecond(lowered + TimeSpan.FromSeconds(1));
        Assert.True(most <= 22, $"{most} frames were written in one second at 22 fps\n{run.Log}");
        // Frames the device held at the change drain at 3 a second (22 against 25): 40 of them within 20 s.
        var late = run.MostLate(lowered + TimeSpan.FromSeconds(20));
        Assert.True(late <= 2 * run.FrameInterval,
            $"a frame was shown {late.TotalMilliseconds:0} ms after its write began (two of the device's frames: {2 * run.FrameInterval.TotalMilliseconds:0} ms)\n{run.Log}");
    }

    [Fact]
    public void A_device_a_little_slower_than_a_high_frame_rate_fills_its_buffer_once_at_most()
    {
        // 60 fps asked of a device that shows 50, for minutes: once it is detected, nothing may fill the buffer again.
        using var run = new PacedRun(frameRate: 60, drainFps: 50, buffer: 40, unloadedMs: 4.5, PanelEncoding.Auto, platform: Other);
        run.RunFor(TimeSpan.FromSeconds(30));
        Assert.True(run.Sink.Request.MaxFps < 50, $"the panel is asked for {run.Sink.Request.MaxFps} fps\n{run.Log}");
        var detected = run.Now;
        run.RunFor(TimeSpan.FromMinutes(3));

        var late = run.MostLate(detected);
        Assert.True(late <= 2 * run.FrameInterval,
            $"a frame was shown {late.TotalMilliseconds:0} ms after its write began, after the first detection (two of the device's frames: " +
            $"{2 * run.FrameInterval.TotalMilliseconds:0} ms)\n{run.Log}");
        var shown = run.ShownPerSecond(TimeSpan.FromSeconds(10));
        Assert.True(shown >= 0.6 * 50, $"the panel showed {shown:0.#} frames a second over the last 10 s\n{run.Log}");
    }

    [Fact]
    public void After_lighter_content_the_pace_recovers_but_never_past_the_last_rate_that_kept_up()
    {
        // Not a T113. Light content the thread writes at 30 a second (0.85 × 1000 / 28 ms) on a 40 fps device: 30 keeps
        // up. Heavier content: 40 ms writes on a device that shows 18, lowered under 18. Light content again, on a 60 fps
        // device: back up, but not past 30, though the setting is 60.
        using var run = new PacedRun(frameRate: 60, drainFps: 40, buffer: 40, unloadedMs: 28, PanelEncoding.Auto, platform: Other);
        run.RunFor(TimeSpan.FromSeconds(20));
        Assert.Equal(30, run.Sink.Request.MaxFps);

        run.Drain(18);
        run.Unloaded = TimeSpan.FromMilliseconds(40);
        run.RunFor(TimeSpan.FromMinutes(3));
        var lowered = run.Sink.Request.MaxFps;
        Assert.True(lowered < 18, $"the panel is asked for {lowered} fps on a device that shows 18\n{run.Log}");

        run.Drain(60);
        run.Unloaded = TimeSpan.FromMilliseconds(4.5);
        var light = run.Requests.Count;
        run.RunFor(TimeSpan.FromSeconds(40));
        Assert.True(run.Sink.Request.MaxFps == 30, $"the panel is asked for {run.Sink.Request.MaxFps} fps after light content again (it was {lowered})\n{run.Log}");
        Assert.All(run.Requests.Skip(light), fps => Assert.InRange(fps, 1, 30));
    }

    [Fact]
    public void On_a_T113_heavier_content_is_not_recovered_from_past_the_rate_that_kept_up_with_it()
    {
        // A T113 JPEG panel at its ceiling (15) with 4.5 ms writes on a device that keeps up; then heavier content, 8 ms
        // writes on a device that shows 10. Lowered under 10, the pace must not rise back towards the ceiling, which kept
        // up only with the lighter writes: 15 against 10 would fill the 40 frames again (about 4 s behind).
        using var run = new PacedRun(frameRate: 30, drainFps: 20, buffer: 40, unloadedMs: 4.5, PanelEncoding.Auto);
        run.RunFor(TimeSpan.FromSeconds(20));
        Assert.Equal(15, run.Sink.Request.MaxFps);

        run.Drain(10);
        run.Unloaded = TimeSpan.FromMilliseconds(8);
        var heavier = run.Now;
        while (run.Sink.Request.MaxFps >= 10 && run.Now < heavier + TimeSpan.FromSeconds(60))
            run.Offer(rev: 1);
        Assert.True(run.Sink.Request.MaxFps < 10, $"the panel is asked for {run.Sink.Request.MaxFps} fps on a device that shows 10\n{run.Log}");
        var detected = run.Now;
        var since = run.Requests.Count;
        run.RunFor(TimeSpan.FromMinutes(3));

        Assert.True(run.Requests.Skip(since).Max() < 10,
            $"the panel was asked for {run.Requests.Skip(since).Max()} fps after the heavier content, on a device that shows 10\n{run.Log}");
        // The 40 frames held at the detection drain at 2 or more a second: within 25 s.
        var late = run.MostLate(detected + TimeSpan.FromSeconds(25));
        Assert.True(late <= 2 * run.FrameInterval,
            $"a frame was shown {late.TotalMilliseconds:0} ms after its write began (two of the device's frames: {2 * run.FrameInterval.TotalMilliseconds:0} ms)\n{run.Log}");
    }

    [Theory]
    [InlineData(Other, 30)] // the setting
    [InlineData(T113, 15)] // the ceiling
    public void Forty_long_writes_on_a_fast_device_leave_the_pace_back_up_within_30_s(byte platform, int expected)
    {
        using var run = new PacedRun(frameRate: 30, drainFps: 120, buffer: 1, unloadedMs: 4.5, PanelEncoding.Auto, platform);
        run.RunFor(TimeSpan.FromSeconds(30));
        run.Unloaded = TimeSpan.FromMilliseconds(50);
        for (var i = 0; i < 40; i++)
            run.Offer(rev: 1);
        run.Unloaded = TimeSpan.FromMilliseconds(4.5);
        run.RunFor(TimeSpan.FromSeconds(30));

        Assert.True(run.Sink.Request.MaxFps == expected, $"the panel is asked for {run.Sink.Request.MaxFps} fps 30 s after the long writes\n{run.Log}");
        var shown = run.ShownPerSecond(TimeSpan.FromSeconds(10));
        Assert.True(shown >= expected - 1, $"the panel showed {shown:0.#} frames a second over the last 10 s\n{run.Log}");
    }

    [Theory]
    [InlineData(PanelEncoding.Auto, true, 15)]
    [InlineData(PanelEncoding.Raw, false, 12)]
    public void A_T113_is_sent_at_most_its_ceiling(PanelEncoding encoding, bool jpeg, int ceiling)
    {
        // A fast device and a 30 fps setting: the T113's measured rate (about 17-20 JPEG, 16 raw) is what it is sent under.
        using var run = new PacedRun(frameRate: 30, drainFps: 60, buffer: 40, unloadedMs: 4.5, encoding);
        run.RunFor(TimeSpan.FromSeconds(20));

        // Kept at the write from the first frame, and asked of the render loop from the first check.
        Assert.Equal(jpeg, run.Runner.Jpeg);
        Assert.True(run.MostWrittenInASecond <= ceiling, $"{run.MostWrittenInASecond} frames were written in one second on a T113 ({(jpeg ? "JPEG" : "raw")})\n{run.Log}");
        Assert.Equal(ceiling, run.Sink.Request.MaxFps);
    }

    [Fact]
    public void A_frame_replaced_while_a_write_runs_is_the_one_written_next()
    {
        // While the panel's thread writes a frame, the loop renders two more: the next write is the second of them.
        using var run = new PacedRun(frameRate: 30, drainFps: 60, buffer: 40, unloadedMs: 4.5, PanelEncoding.Auto);
        run.Offer(rev: 1);
        var writes = run.Frames;
        run.DuringNextWrite(() =>
        {
            run.Deliver(rev: 1, SKColors.Red);
            run.Deliver(rev: 1, SKColors.Lime);
        });
        run.Deliver(rev: 1, SKColors.SteelBlue);
        run.UntilFrames(writes + 2);

        Assert.True(run.Runner.Jpeg);
        var during = run.ColorOfWrite(writes);
        var next = run.ColorOfWrite(writes + 1);
        Assert.True(during.Blue > 150 && during.Red < 120, $"the frame written first was {during}, not the one offered before the write (steel blue)");
        Assert.True(next.Green > 200 && next.Red < 60, $"the frame written next was {next}, not the newest (lime)");
    }

    /// <summary>A platform other than the T113's (5), and not a write-through one (1, 2): no ceiling, raw under auto.</summary>
    private const byte Other = 4;

    private const byte T113 = 5;

    /// <summary>One frame through the device: when its write began and when the device showed it.</summary>
    private sealed record Delivery(TimeSpan WriteStarted, TimeSpan Shown, long Rev);

    /// <summary>
    /// A runner on <see cref="BufferedPanel"/>, its sink fed by hand as a render loop would feed it: a frame at each of
    /// the sink's intervals, or at once when the panel's thread was busy past it. One frame is offered at a time, and the
    /// next only once the thread has written it and gone round again, so every step is on the fake clock.
    /// </summary>
    private sealed class PacedRun : IDisposable
    {
        private readonly FakeUsbBackend _backend = new();
        private readonly BufferedPanel _panel;
        private readonly SKBitmap _pixels = new(new SKImageInfo(8, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        private readonly List<int> _requests = [];
        private readonly List<TimeSpan> _written = [];
        private PanelSink? _sink;
        private TimeSpan _nextOffer;
        private long _frameId;

        public PacedRun(int frameRate, int drainFps, int buffer, double unloadedMs, PanelEncoding encoding, byte platform = T113)
        {
            var device = _backend.Add(new FakeUsbDevice(PanelId) { Reply = V.InfoReply(2, platform, 21, Width, Height) });
            FrameInterval = Every(drainFps);
            _panel = new BufferedPanel(_backend.Clock, buffer, FrameInterval, TimeSpan.FromMilliseconds(unloadedMs), Width * Height * 2);
            device.OnWrite = _panel.Write;
            _pixels.Erase(SKColors.SteelBlue);
            // A JPEG frame's encode takes 1 ms of the panel's thread.
            var options = new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan, AfterEncode = () => _backend.Clock.Advance(TimeSpan.FromMilliseconds(1)) };
            Runner = new PanelRunner(_backend, PanelId, "test panel", "p-1", 100, PanelRotation.None, sink => { _sink = sink; return true; }, _ => { }, _ => { }, _ => { },
                () => frameRate, _backend.Clock, options, Log.Logger, encoding);
            _panel.Runner = Runner;
            Runner.Start();
            Until(() => Runner.Running, "the panel never ran");
            _nextOffer = Now;
        }

        public PanelRunner Runner { get; }

        public PanelSink Sink => _sink!;

        public KeptLog Log { get; } = new();

        /// <summary>The device's time a frame (1 / R).</summary>
        public TimeSpan FrameInterval { get; private set; }

        /// <summary>Frames the device holds now, not yet shown.</summary>
        public int Buffered => _panel.Buffered(Now);

        /// <summary>A frame write's time while the device has room.</summary>
        public TimeSpan Unloaded
        {
            set => _panel.Unloaded = value;
        }

        /// <summary>The sink's request at every frame offered.</summary>
        public IReadOnlyList<int> Requests => _requests;

        /// <summary>Frames the device took.</summary>
        public int Frames => _panel.Frames;

        public TimeSpan Now => TimeSpan.FromTicks(_backend.Clock.GetTimestamp());

        /// <summary>The device shows <paramref name="fps"/> frames a second from now on (lighter or heavier content).</summary>
        public void Drain(int fps)
        {
            FrameInterval = Every(fps);
            _panel.Interval = FrameInterval;
        }

        /// <summary>The longest a frame whose write began at <paramref name="since"/> or later waited to be shown, from its write's start.</summary>
        public TimeSpan MostLate(TimeSpan since) => _panel.MostLate(since);

        /// <summary><paramref name="during"/> runs once, on the panel's thread, inside its next frame write.</summary>
        public void DuringNextWrite(Action during)
        {
            _panel.KeepData = true;
            _panel.DuringNext = during;
        }

        /// <summary>The top-left pixel of the frame write <paramref name="index"/> (from 0) since <see cref="DuringNextWrite"/>, decoded.</summary>
        public SKColor ColorOfWrite(int index)
        {
            using var bitmap = SKBitmap.Decode(_panel.DataOf(index)) ?? throw new Xunit.Sdk.XunitException($"write {index} isn't a JPEG");
            return bitmap.GetPixel(0, 0);
        }

        public void UntilFrames(int frames) => Until(() => _panel.Frames >= frames, $"{frames} frames weren't written ({_panel.Frames} were)");

        /// <summary>A new frame of <paramref name="color"/> to the sink, as the render loop hands it over, not waiting for it to be written.</summary>
        public long Deliver(long rev, SKColor color)
        {
            var frameId = Interlocked.Increment(ref _frameId);
            using var pixels = new SKBitmap(new SKImageInfo(8, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
            pixels.Erase(color);
            using var image = SKImage.FromBitmap(pixels);
            var frame = new RenderedFrame(image)
            {
                ProfileId = "p-1",
                Rev = rev,
                FrameId = frameId,
                Tick = frameId,
                Scale = 1,
                PixelWidth = pixels.Width,
                PixelHeight = pixels.Height,
                ProfileWidth = pixels.Width,
                ProfileHeight = pixels.Height,
                Bounds = [],
                ContentHash = (ulong)frameId, // new pixels every frame
            };
            Sink.OnFrame(frame);
            frame.Close();
            return frameId;
        }

        /// <summary>Frames offered until the clock has moved on by <paramref name="duration"/>, each a new picture of the same revision.</summary>
        public void RunFor(TimeSpan duration)
        {
            var end = Now + duration;
            while (Now < end)
                Offer(rev: 1);
        }

        /// <summary>The next frame, a new picture of revision <paramref name="rev"/>, offered when the render loop would offer it, and written.</summary>
        public Delivery Offer(long rev)
        {
            // The loop renders at the sink's interval; when the thread was busy past it, its newest frame is waiting.
            var fps = Sink.Request.MaxFps;
            _requests.Add(fps);
            if (_nextOffer > Now)
                _backend.Clock.Advance(_nextOffer - Now);
            _nextOffer = Now + Every(fps);

            var writes = _panel.Frames;
            var frameId = ++_frameId;
            using (var image = SKImage.FromBitmap(_pixels))
            {
                var frame = new RenderedFrame(image)
                {
                    ProfileId = "p-1",
                    Rev = rev,
                    FrameId = frameId,
                    Tick = frameId,
                    Scale = 1,
                    PixelWidth = _pixels.Width,
                    PixelHeight = _pixels.Height,
                    ProfileWidth = _pixels.Width,
                    ProfileHeight = _pixels.Height,
                    Bounds = [],
                    ContentHash = (ulong)frameId, // new pixels every frame
                };
                Sink.OnFrame(frame);
                frame.Close();
            }

            // Written, then the thread back at the top of its loop (its pacing done) before the clock moves again.
            Until(() => _panel.Frames == writes + 1, $"frame {frameId} wasn't written");
            var written = _panel.Last;
            Until(() => Runner.Turns > written.Turn, $"the thread didn't go round after frame {frameId}");
            _written.Add(written.Started);
            return new Delivery(written.Started, written.Shown, rev);
        }

        /// <summary>Frames the device showed a second over the last <paramref name="window"/>.</summary>
        public double ShownPerSecond(TimeSpan window)
        {
            var now = Now;
            return _panel.ShownBetween(now - window, now) / window.TotalSeconds;
        }

        /// <summary>The most frames whose writes began within any one second.</summary>
        public int MostWrittenInASecond
        {
            get
            {
                var most = 0;
                for (int i = 0, j = 0; i < _written.Count; i++)
                {
                    while (_written[i] - _written[j] >= TimeSpan.FromSeconds(1))
                        j++;
                    most = Math.Max(most, i - j + 1);
                }
                return most;
            }
        }

        public void Dispose()
        {
            _panel.Stopping = true;
            Runner.RequestStop();
            Assert.True(Runner.Ended.Wait(TimeSpan.FromSeconds(10)), "the panel didn't stop");
            _pixels.Dispose();
        }

        /// <summary>1 / <paramref name="fps"/> s, rounded up to a tick (so <paramref name="fps"/> of them never fit in less than a second).</summary>
        private static TimeSpan Every(int fps) => TimeSpan.FromTicks((TimeSpan.TicksPerSecond + fps - 1) / fps);

        private static void Until(Func<bool> done, string what) =>
            Assert.True(SpinWait.SpinUntil(done, TimeSpan.FromSeconds(10)), what);
    }

    /// <summary>A change through the render loop: when it was committed, when its frame's write began and when it was shown.</summary>
    private sealed record ChangeShown(TimeSpan Committed, TimeSpan WriteStarted, TimeSpan Shown);

    /// <summary>
    /// A runner on <see cref="BufferedPanel"/>, its sink attached to a real <see cref="RenderService"/> (its loop driven by
    /// hand on the fake clock, ticking at <c>tickFps</c>): a bar on black bound to a sensor that moves every tick, so every
    /// tick the sink is due draws a new frame. A tick that delivers a frame waits for the panel's thread to write it and
    /// go round (its pacing, and any wait, done) before the clock moves again; ticks the thread was busy through are run
    /// as one, as a periodic timer runs them.
    /// </summary>
    private sealed class LoopRun : IAsyncDisposable
    {
        private readonly FakeUsbBackend _backend = new();
        private readonly BufferedPanel _panel;
        private readonly RenderHarness _render;
        private TimeSpan _nextTick;
        private int _load;

        public LoopRun(int frameRate, int drainFps, int buffer, double unloadedMs, int tickFps = 30, byte platform = T113)
        {
            FrameRate = frameRate;
            var device = _backend.Add(new FakeUsbDevice(PanelId) { Reply = V.InfoReply(2, platform, 21, Width, Height) });
            FrameInterval = Every(drainFps);
            TickInterval = Every(tickFps);
            _panel = new BufferedPanel(_backend.Clock, buffer, FrameInterval, TimeSpan.FromMilliseconds(unloadedMs), Width * Height * 2);
            device.OnWrite = _panel.Write;
            _render = new RenderHarness(RenderHarness.BarDocument(Width, Height), time: _backend.Clock);
            var options = new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan, AfterEncode = () => _backend.Clock.Advance(TimeSpan.FromMilliseconds(1)) };
            Runner = new PanelRunner(_backend, PanelId, "test panel", _render.ProfileId, 100, PanelRotation.None, sink => _render.Service.Attach(sink),
                sink => _render.Service.Detach(sink), _ => { }, _ => { }, () => FrameRate, _backend.Clock, options, Log.Logger, PanelEncoding.Auto);
            _panel.Runner = Runner;
            Runner.Start();
            Until(() => Runner.Running, "the panel never ran");
            _nextTick = Now;
        }

        public PanelRunner Runner { get; }

        /// <summary>The engine's frame rate setting, read by the sink and the runner.</summary>
        public volatile int FrameRate;

        public KeptLog Log { get; } = new();

        public TimeSpan FrameInterval { get; }

        public TimeSpan TickInterval { get; }

        public int Buffered => _panel.Buffered(Now);

        public TimeSpan Now => TimeSpan.FromTicks(_backend.Clock.GetTimestamp());

        public void RunFor(TimeSpan duration)
        {
            var end = Now + duration;
            while (Now < end)
                Step();
        }

        /// <summary>The longest a frame whose write began at <paramref name="since"/> or later waited to be shown, from its write's start.</summary>
        public TimeSpan MostLate(TimeSpan since) => _panel.MostLate(since);

        /// <summary>The most frame writes that began within any one second, from <paramref name="since"/>.</summary>
        public int MostWrittenInASecond(TimeSpan since)
        {
            var written = _panel.WriteStarts(since);
            var most = 0;
            for (int i = 0, j = 0; i < written.Count; i++)
            {
                while (written[i] - written[j] >= TimeSpan.FromSeconds(1))
                    j++;
                most = Math.Max(most, i - j + 1);
            }
            return most;
        }

        /// <summary>The background turned white, and ticks until the panel shows it.</summary>
        public ChangeShown Change()
        {
            _panel.Mark = White;
            _render.Commit(DocKit.Profile(new JsonObject { ["background"] = "#ffffff" }));
            var committed = Now;
            while (_panel.Marked == null && Now - committed < TimeSpan.FromSeconds(20))
                Step();
            var marked = _panel.Marked ?? throw new Xunit.Sdk.XunitException($"the change was never shown\n{Log}");
            return new ChangeShown(committed, marked.Started, marked.Shown);
        }

        public double ShownPerSecond(TimeSpan window)
        {
            var now = Now;
            return _panel.ShownBetween(now - window, now) / window.TotalSeconds;
        }

        private void Step()
        {
            if (_nextTick > Now)
                _backend.Clock.Advance(_nextTick - Now);
            while (_nextTick <= Now)
                _nextTick += TickInterval;

            _render.SetLoad(1 + _load++ % 20);
            var writes = _panel.Frames;
            if (_render.Service.Tick(_render.ProfileId) == 0)
                return;
            Until(() => _panel.Frames == writes + 1, "a frame wasn't written");
            var written = _panel.Last;
            Until(() => Runner.Turns > written.Turn, "the thread didn't go round");
        }

        /// <summary>A frame whose top-left pixel is white: the background after <see cref="Change"/>.</summary>
        private static bool White(byte[] data)
        {
            using var bitmap = SKBitmap.Decode(data);
            return bitmap != null && bitmap.GetPixel(0, 0).Red > 200;
        }

        public async ValueTask DisposeAsync()
        {
            _panel.Stopping = true;
            Runner.RequestStop();
            Assert.True(Runner.Ended.Wait(TimeSpan.FromSeconds(10)), "the panel didn't stop");
            await _render.DisposeAsync();
        }

        private static TimeSpan Every(int fps) => TimeSpan.FromTicks((TimeSpan.TicksPerSecond + fps - 1) / fps);

        private static void Until(Func<bool> done, string what) =>
            Assert.True(SpinWait.SpinUntil(done, TimeSpan.FromSeconds(10)), what);
    }

    /// <summary>One frame write the device took: when it began, the runner's turn then, and when the frame was shown.</summary>
    private sealed record FrameWrite(TimeSpan Started, long Turn, TimeSpan Shown);

    /// <summary>
    /// A panel that buffers frames: at most <c>buffer</c> waiting, one shown every <c>interval</c> (or at once when it was
    /// idle). A frame write takes <c>unloaded</c> while there is room; with the buffer full it first waits for the next
    /// frame shown. Called on the panel's thread only (the fake device's <see cref="FakeUsbDevice.OnWrite"/>).
    /// </summary>
    private sealed class BufferedPanel(FakeUsbClock clock, int buffer, TimeSpan interval, TimeSpan unloaded, int rawLength)
    {
        private readonly Lock _lock = new();
        private readonly Queue<TimeSpan> _waiting = new();
        private readonly List<TimeSpan> _shown = [];
        private readonly List<FrameWrite> _writes = [];
        private readonly List<byte[]> _data = [];
        private TimeSpan _lastShown = TimeSpan.MinValue;
        private FrameWrite? _last;
        private FrameWrite? _marked;
        private int _frames;
        private long _unloadedTicks = unloaded.Ticks;
        private long _intervalTicks = interval.Ticks;

        public PanelRunner? Runner { get; set; }

        /// <summary>A frame write's time while there is room.</summary>
        public TimeSpan Unloaded
        {
            set => Interlocked.Exchange(ref _unloadedTicks, value.Ticks);
        }

        /// <summary>The device's time a frame.</summary>
        public TimeSpan Interval
        {
            set => Interlocked.Exchange(ref _intervalTicks, value.Ticks);
        }

        /// <summary>When set, every frame written from now on is kept (<see cref="DataOf"/>).</summary>
        public volatile bool KeepData;

        /// <summary>Run once inside the next frame write, then cleared.</summary>
        public Action? DuringNext { get; set; }

        public byte[] DataOf(int index)
        {
            lock (_lock)
                return _data[index - (_frames - _data.Count)];
        }

        public TimeSpan MostLate(TimeSpan since)
        {
            lock (_lock)
                return _writes.Where(w => w.Started >= since).Select(w => w.Shown - w.Started).DefaultIfEmpty().Max();
        }

        /// <summary>When each frame write began.</summary>
        public List<TimeSpan> WriteStarts(TimeSpan since)
        {
            lock (_lock)
                return _writes.Where(w => w.Started >= since).Select(w => w.Started).ToList();
        }

        /// <summary>When set, the first frame it matches is kept as <see cref="Marked"/>.</summary>
        public Func<byte[], bool>? Mark { get; set; }

        public FrameWrite? Marked
        {
            get { lock (_lock) return _marked; }
        }

        /// <summary>Set before the stop: its black frame is taken at once and not counted.</summary>
        public volatile bool Stopping;

        public int Frames
        {
            get { lock (_lock) return _frames; }
        }

        public FrameWrite Last
        {
            get { lock (_lock) return _last!; }
        }

        private TimeSpan Now => TimeSpan.FromTicks(clock.GetTimestamp());

        public UsbStatus? Write(FakeWrite write)
        {
            var frame = write.Endpoint == BeadaPanelProtocol.PanelOut
                && (write.Data.Length == rawLength || write.Data is [0xFF, 0xD8, ..]);
            if (!frame)
                return null;
            if (Stopping)
                return UsbStatus.Ok;

            var started = Now;
            var turn = Runner!.Turns;
            lock (_lock)
            {
                Drop(Now);
                if (_waiting.Count >= buffer)
                {
                    // Full: the endpoint takes nothing until the panel shows its next frame.
                    var next = _waiting.Peek();
                    if (next > Now)
                        clock.Advance(next - Now);
                    Drop(Now);
                }
            }
            if (DuringNext is { } during)
            {
                DuringNext = null;
                during();
            }
            clock.Advance(TimeSpan.FromTicks(Interlocked.Read(ref _unloadedTicks)));
            var marked = Mark is { } mark && mark(write.Data);
            lock (_lock)
            {
                var arrived = Now;
                var every = TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));
                var shown = _lastShown == TimeSpan.MinValue || arrived > _lastShown + every ? arrived : _lastShown + every;
                _lastShown = shown;
                _waiting.Enqueue(shown);
                _shown.Add(shown);
                _last = new FrameWrite(started, turn, shown);
                _writes.Add(_last);
                if (KeepData)
                    _data.Add(write.Data.ToArray());
                if (marked && _marked == null)
                    _marked = _last;
                _frames++;
            }
            return UsbStatus.Ok;
        }

        public int Buffered(TimeSpan now)
        {
            lock (_lock)
                return _waiting.Count(shown => shown > now);
        }

        public int ShownBetween(TimeSpan from, TimeSpan to)
        {
            lock (_lock)
                return _shown.Count(shown => shown > from && shown <= to);
        }

        private void Drop(TimeSpan now)
        {
            while (_waiting.Count > 0 && _waiting.Peek() <= now)
                _waiting.Dequeue();
        }
    }
}
