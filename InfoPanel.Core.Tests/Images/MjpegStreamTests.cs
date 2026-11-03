using System.Text;
using System.Threading.Channels;
using InfoPanel.Images;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;
using Rig = InfoPanel.Core.Tests.Images.HttpMediaProviderTests.Rig;

namespace InfoPanel.Core.Tests.Images;

/// <summary>
/// MJPEG camera streams on the <c>http</c> provider (spec/docs/providers.md, "Streams"): an answer of
/// <c>multipart/x-mixed-replace</c> stays open and shows its newest part, each a JPEG within <c>mediaBytes</c>, taken and
/// decoded at most once a tick; a part over the limit, a malformed boundary, a dropped connection and a stream that stalls
/// end it, keeping its last picture, and it reconnects at the refresh rate. Servers on 127.0.0.1 only; time is a fake
/// clock, and parts are waited for on the provider's <see cref="HttpMediaProvider.PartReceived"/>, never by sleeping.
/// </summary>
public sealed class MjpegStreamTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Multipart = "multipart/x-mixed-replace; boundary=frame";

    internal static byte[] Jpeg(SKColor color, int w = 8, int h = 6)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static readonly byte[] Red = Jpeg(SKColors.Red);
    private static readonly byte[] Green = Jpeg(SKColors.Lime);
    private static readonly byte[] Blue = Jpeg(SKColors.Blue);
    private static readonly byte[] Yellow = Jpeg(SKColors.Yellow);

    /// <summary>One part: its delimiter line, headers (a length unless <paramref name="length"/> is false), the JPEG and its line end.</summary>
    internal static async Task Part(Stream s, byte[] jpeg, CancellationToken t, bool length = true, string delimiter = "--frame")
    {
        var head = $"{delimiter}\r\nContent-Type: image/jpeg\r\n" + (length ? $"Content-Length: {jpeg.Length}\r\n" : "") + "\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head), t);
        await s.WriteAsync(jpeg, t);
        await s.WriteAsync("\r\n"u8.ToArray(), t);
        await s.FlushAsync(t);
    }

    private static Task Write(Stream s, string text, CancellationToken t) => s.WriteAsync(Encoding.ASCII.GetBytes(text), t).AsTask();

    /// <summary>Completes once <paramref name="count"/> parts of <paramref name="address"/> have been read.</summary>
    private static Task Parts(Rig rig, string address, int count)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var n = 0;
        void Handler(string a)
        {
            if (a != address || Interlocked.Increment(ref n) != count) return;
            rig.Provider.PartReceived -= Handler;
            done.TrySetResult();
        }
        rig.Provider.PartReceived += Handler;
        return done.Task;
    }

    /// <summary>Which of red, green, blue or yellow a decoded JPEG's pixel is (JPEG is lossy: the channels, not the exact colour).</summary>
    private static string Colour(MediaSample? sample)
    {
        Assert.NotNull(sample);
        using var bitmap = SKBitmap.FromImage(sample.Image!);
        var p = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        return (p.Red > 200, p.Green > 200, p.Blue > 200) switch
        {
            (true, false, false) => "red",
            (false, true, false) => "green",
            (false, false, true) => "blue",
            (true, true, false) => "yellow",
            _ => $"{p}",
        };
    }

    [Fact]
    public async Task A_stream_shows_its_newest_part_and_decodes_at_most_one_part_a_tick()
    {
        var more = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(async (r, s, t) =>
        {
            await LocalHttpServer.Head(s, 200, Multipart);
            foreach (var part in new[] { Red, Green, Blue }) await Part(s, part, t);
            await more.Task.WaitAsync(t);
            await Part(s, Yellow, t);
            await Task.Delay(Timeout.Infinite, t);
        });
        var url = rig.Url("/video.mjpg");
        var decoded = 0;
        rig.Cache.FrameDecoded += _ => Interlocked.Increment(ref decoded);

        var three = Parts(rig, url, 3);
        var first = rig.Ask(url);
        Assert.Null(first.Sample);
        Assert.Equal("media.loading", first.Status?.Code);
        await three.WaitAsync(Wait, Ct);

        // Three parts came; the tick takes the newest and decodes only it.
        var shown = rig.Ask(url);
        Assert.Equal("blue", Colour(shown.Sample));
        Assert.Null(shown.Status);
        Rig.Release(shown);
        Assert.Equal(1, decoded);

        var fourth = Parts(rig, url, 1);
        more.SetResult();
        await fourth.WaitAsync(Wait, Ct);
        var next = rig.Ask(url);
        Assert.Equal("yellow", Colour(next.Sample));
        Rig.Release(next);
        Assert.Equal(2, decoded);

        // No new part: nothing is decoded again. One connection, kept open; only the part shown is kept.
        var again = rig.Ask(url);
        Assert.Equal("yellow", Colour(again.Sample));
        Rig.Release(again);
        Assert.Equal(2, decoded);
        Assert.Single(rig.Server.Requests);
        Assert.Equal(Yellow.Length, rig.Store.Bytes);
    }

    [Theory]
    [InlineData(Multipart, "--frame", true)]
    [InlineData(Multipart, "--frame", false)]
    // A boundary declared with its dashes, as some cameras write it, and quoted.
    [InlineData("multipart/x-mixed-replace;boundary=\"--myboundary\"", "--myboundary", false)]
    [InlineData("Multipart/X-Mixed-Replace; boundary=frame", "--frame", true)]
    public async Task Parts_end_at_their_length_or_at_the_next_boundary(string contentType, string delimiter, bool length)
    {
        await using var rig = new Rig(async (r, s, t) =>
        {
            await LocalHttpServer.Head(s, 200, contentType);
            await Write(s, "\r\n", t);
            await Part(s, Red, t, length, delimiter);
            await Part(s, Blue, t, length, delimiter);
            // The closing delimiter ends the last part and the stream.
            await Write(s, delimiter + "--\r\n", t);
            await s.FlushAsync(t);
        });
        var url = rig.Url("/video.mjpg");
        var answered = rig.NextAnswer(url);
        Rig.Release(rig.Ask(url));
        await answered.WaitAsync(Wait, Ct);
        var shown = rig.Ask(url);
        Assert.Equal("blue", Colour(shown.Sample));
        Assert.Equal(new MediaStatus("media.provider", "The stream ended."), shown.Status);
        Rig.Release(shown);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_part_over_mediaBytes_ends_the_stream_and_the_last_picture_stays(bool length)
    {
        var limits = SpecLimits.Default.With(new Dictionary<string, double> { ["mediaBytes"] = 1000 });
        Assert.True(Red.Length < 1000);
        await using var rig = new Rig(async (r, s, t) =>
        {
            await LocalHttpServer.Head(s, 200, Multipart);
            await Part(s, Red, t);
            // Over the limit, with a length that says so, or with none and no boundary in sight.
            await Write(s, length ? "--frame\r\nContent-Type: image/jpeg\r\nContent-Length: 1001\r\n\r\n" : "--frame\r\nContent-Type: image/jpeg\r\n\r\n", t);
            await s.WriteAsync(new byte[length ? 10 : 1100], t);
            await s.FlushAsync(t);
            await Task.Delay(Timeout.Infinite, t);
        }, limits);
        var url = rig.Url("/video.mjpg");
        var answered = rig.NextAnswer(url);
        Rig.Release(rig.Ask(url));
        await answered.WaitAsync(Wait, Ct);
        var after = rig.Ask(url);
        Assert.Equal("red", Colour(after.Sample));
        Assert.Equal(new MediaStatus("limit.mediaBytes", "A part of the stream is over 1000 bytes (mediaBytes)."), after.Status);
        Rig.Release(after);
    }

    [Theory]
    [InlineData("multipart/x-mixed-replace", "--frame", "The stream has no boundary.")]
    [InlineData(Multipart, "--other", "The stream doesn't start with its boundary.")]
    public async Task A_stream_with_a_malformed_boundary_is_refused(string contentType, string delimiter, string message)
    {
        await using var rig = new Rig(async (r, s, t) =>
        {
            await LocalHttpServer.Head(s, 200, contentType);
            await Part(s, Red, t, delimiter: delimiter);
            await Task.Delay(Timeout.Infinite, t);
        });
        var url = rig.Url("/video.mjpg");
        var answered = rig.NextAnswer(url);
        Rig.Release(rig.Ask(url));
        await answered.WaitAsync(Wait, Ct);
        var after = rig.Ask(url);
        Assert.Null(after.Sample);
        Assert.Equal(new MediaStatus("media.provider", message), after.Status);
    }

    [Fact]
    public async Task A_part_that_isnt_a_jpeg_is_skipped_with_its_status_until_the_next_one()
    {
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(async (r, s, t) =>
        {
            await LocalHttpServer.Head(s, 200, Multipart);
            await Part(s, Red, t);
            await second.Task.WaitAsync(t);
            await Part(s, HttpMediaProviderTests.Png(SKColors.Blue), t);
            await third.Task.WaitAsync(t);
            await Part(s, Green, t);
            await Task.Delay(Timeout.Infinite, t);
        });
        var url = rig.Url("/video.mjpg");
        var one = Parts(rig, url, 1);
        Rig.Release(rig.Ask(url));
        await one.WaitAsync(Wait, Ct);
        Rig.Release(rig.Ask(url));

        var png = Parts(rig, url, 1);
        second.SetResult();
        await png.WaitAsync(Wait, Ct);
        var refused = rig.Ask(url);
        Assert.Equal("red", Colour(refused.Sample));
        Assert.Equal(new MediaStatus("media.format", "A part of the stream isn't a JPEG image."), refused.Status);
        Rig.Release(refused);

        var green = Parts(rig, url, 1);
        third.SetResult();
        await green.WaitAsync(Wait, Ct);
        var next = rig.Ask(url);
        Assert.Equal("green", Colour(next.Sample));
        Assert.Null(next.Status);
        Rig.Release(next);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(3, 3)]
    public async Task A_dropped_stream_reconnects_at_the_refresh_rate(int refresh, int seconds)
    {
        var connections = 0;
        await using var rig = new Rig(async (r, s, t) =>
        {
            await LocalHttpServer.Head(s, 200, Multipart);
            if (Interlocked.Increment(ref connections) == 1)
            {
                // The first connection drops after one part.
                await Part(s, Red, t);
                return;
            }
            await Part(s, Blue, t);
            await Task.Delay(Timeout.Infinite, t);
        });
        var url = rig.Url("/video.mjpg");
        var answered = rig.NextAnswer(url);
        Rig.Release(rig.Ask(url, refresh));
        await answered.WaitAsync(Wait, Ct);
        var dropped = rig.Ask(url, refresh);
        Assert.Equal("red", Colour(dropped.Sample));
        Assert.Equal(new MediaStatus("media.provider", "The stream ended."), dropped.Status);
        Rig.Release(dropped);

        rig.Time.Advance(TimeSpan.FromSeconds(seconds - 0.1));
        Rig.Release(rig.Ask(url, refresh));
        Assert.Single(rig.Server.Requests);

        var blue = Parts(rig, url, 1);
        rig.Time.Advance(TimeSpan.FromSeconds(0.1));
        Rig.Release(rig.Ask(url, refresh));
        await blue.WaitAsync(Wait, Ct);
        var back = rig.Ask(url, refresh);
        Assert.Equal("blue", Colour(back.Sample));
        Assert.Null(back.Status);
        Rig.Release(back);
        Assert.Equal(2, rig.Server.Requests.Count);
    }

    [Fact]
    public async Task A_stream_that_sends_no_part_for_15_seconds_is_given_up_and_reconnects()
    {
        var connections = 0;
        await using var rig = new Rig(async (r, s, t) =>
        {
            await LocalHttpServer.Head(s, 200, Multipart);
            await Part(s, Interlocked.Increment(ref connections) == 1 ? Red : Blue, t);
            await Task.Delay(Timeout.Infinite, t);
        });
        var url = rig.Url("/video.mjpg");
        var one = Parts(rig, url, 1);
        Rig.Release(rig.Ask(url));
        await one.WaitAsync(Wait, Ct);
        Rig.Release(rig.Ask(url));

        var answered = rig.NextAnswer(url);
        rig.Time.Advance(TimeSpan.FromSeconds(14.9));
        Rig.Release(rig.Ask(url));
        Assert.False(answered.IsCompleted);
        rig.Time.Advance(TimeSpan.FromSeconds(0.1));
        await answered.WaitAsync(Wait, Ct);
        var stalled = rig.Ask(url);
        Assert.Equal("red", Colour(stalled.Sample));
        Assert.Equal(new MediaStatus("media.provider", "The stream sent nothing for 15 s."), stalled.Status);
        Rig.Release(stalled);

        var blue = Parts(rig, url, 1);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        Rig.Release(rig.Ask(url));
        await blue.WaitAsync(Wait, Ct);
        var back = rig.Ask(url);
        Assert.Equal("blue", Colour(back.Sample));
        Rig.Release(back);
    }

    [Fact]
    public async Task Open_streams_leave_the_request_slots_to_other_fetches()
    {
        // Five cameras, more than the four fetches at once, all streaming; a web image is still fetched.
        await using var rig = new Rig(async (r, s, t) =>
        {
            if (r.Path == "/still.png")
            {
                await LocalHttpServer.Answer(s, 200, HttpMediaProviderTests.Png(SKColors.Red));
                return;
            }
            await LocalHttpServer.Head(s, 200, Multipart);
            await Part(s, Red, t);
            await Task.Delay(Timeout.Infinite, t);
        });
        var cameras = Enumerable.Range(1, 5).Select(n => rig.Url($"/cam{n}.mjpg")).ToArray();
        var parts = cameras.Select(c => Parts(rig, c, 1)).ToArray();
        foreach (var c in cameras) Rig.Release(rig.Ask(c));
        await Task.WhenAll(parts).WaitAsync(Wait, Ct);

        var still = await rig.Fetch(rig.Url("/still.png"));
        Assert.NotNull(still.Sample);
        Rig.Release(still);
        foreach (var c in cameras)
        {
            var shown = rig.Ask(c);
            Assert.Equal("red", Colour(shown.Sample));
            Rig.Release(shown);
        }
    }

    // ---- what streams may hold, and for how long -------------------------------------------------------------------

    /// <summary>A provider over the rig's server, store and cache with stream limits of the test's own (the rig's own provider stays unused).</summary>
    private static HttpMediaProvider Limited(Rig rig, int maxStreams = 16, int perItem = 4, long streamBytes = 128L * 1024 * 1024) =>
        new(rig.Cache, rig.Store, new HttpMediaProviderOptions
        {
            Time = rig.Time,
            UserAgent = "InfoPanel/2.0.0",
            MaxStreams = maxStreams,
            MaxStreamsPerItem = perItem,
            StreamBytes = streamBytes,
        });

    /// <summary>Completes once <paramref name="count"/> parts of <paramref name="address"/> have been read by <paramref name="provider"/>.</summary>
    private static Task PartsOf(HttpMediaProvider provider, string address, int count = 1)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var n = 0;
        void Handler(string a)
        {
            if (a != address || Interlocked.Increment(ref n) != count) return;
            provider.PartReceived -= Handler;
            done.TrySetResult();
        }
        provider.PartReceived += Handler;
        return done.Task;
    }

    /// <summary>Completes when <paramref name="provider"/>'s fetch of <paramref name="address"/> next ends (a stream: when it closes).</summary>
    private static Task AnswerOf(HttpMediaProvider provider, string address)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string a)
        {
            if (a != address) return;
            provider.Answered -= Handler;
            done.TrySetResult();
        }
        provider.Answered += Handler;
        return done.Task;
    }

    /// <summary>A camera per path: one red part, then nothing, open until the server stops.</summary>
    private static async Task OnePartThenWait(ServedRequest r, Stream s, CancellationToken t)
    {
        await LocalHttpServer.Head(s, 200, Multipart);
        await Part(s, Red, t);
        await Task.Delay(Timeout.Infinite, t);
    }

    private static int RequestsFor(Rig rig, string path) => rig.Server.Requests.Count(r => r.Path == path);

    [Fact]
    public async Task Past_the_engines_stream_limit_the_idle_stream_asked_for_longest_ago_closes_and_with_none_idle_a_new_one_is_refused()
    {
        await using var rig = new Rig(OnePartThenWait);
        using var provider = Limited(rig, maxStreams: 2);
        string c1 = rig.Url("/cam1.mjpg"), c2 = rig.Url("/cam2.mjpg"), c3 = rig.Url("/cam3.mjpg"), c4 = rig.Url("/cam4.mjpg");
        void Ask(params string[] cameras)
        {
            foreach (var c in cameras) Rig.Release(provider.Get(new MediaRequest(c, 0, MediaPlayback.Loop)));
        }

        var two = Task.WhenAll(PartsOf(provider, c1), PartsOf(provider, c2));
        Ask(c1, c2);
        await two.WaitAsync(Wait, Ct);

        // c1 isn't asked for 3 s (idle after 2), c2 is. A third stream closes c1, the idle one, and opens.
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        Ask(c2);
        var closed = AnswerOf(provider, c1);
        var third = PartsOf(provider, c3);
        Ask(c3);
        await Task.WhenAll(closed, third).WaitAsync(Wait, Ct);

        // c2 and c3 are both asked for: a fourth is refused, and says why.
        var refused = AnswerOf(provider, c4);
        Ask(c2, c3, c4);
        await refused.WaitAsync(Wait, Ct);
        var fourth = provider.Get(new MediaRequest(c4, 0, MediaPlayback.Loop));
        Assert.Null(fourth.Sample);
        Assert.Equal(new MediaStatus("media.budget", "Too many streams: 2 are open."), fourth.Status);
        Assert.Equal(1, RequestsFor(rig, "/cam2.mjpg"));

        // c1, asked again once c2 is idle, opens again at once (its close was no failure) and closes c2.
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        Ask(c3);
        var reopened = PartsOf(provider, c1);
        var c2Closed = AnswerOf(provider, c2);
        Ask(c1);
        await Task.WhenAll(reopened, c2Closed).WaitAsync(Wait, Ct);
        Assert.Equal(2, RequestsFor(rig, "/cam1.mjpg"));
    }

    [Fact]
    public async Task An_item_keeps_at_most_its_limit_of_streams_closing_the_idle_one_it_asked_for_longest_ago()
    {
        await using var rig = new Rig(OnePartThenWait);
        using var provider = Limited(rig, perItem: 2);
        string c1 = rig.Url("/cam1.mjpg"), c2 = rig.Url("/cam2.mjpg"), c3 = rig.Url("/cam3.mjpg"), c4 = rig.Url("/cam4.mjpg"), c5 = rig.Url("/cam5.mjpg");
        object item = new(), other = new();
        ProvidedMedia Ask(string c, object who) => provider.Get(new MediaRequest(c, 0, MediaPlayback.Loop), new MediaAsk(new MediaTick(), who));

        // The item's address changes every 3 s: c1, c2, then c3, which closes c1, the one it asked for longest ago.
        var p1 = PartsOf(provider, c1);
        Rig.Release(Ask(c1, item));
        await p1.WaitAsync(Wait, Ct);
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        var p2 = PartsOf(provider, c2);
        Rig.Release(Ask(c2, item));
        await p2.WaitAsync(Wait, Ct);
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        var closed = AnswerOf(provider, c1);
        var p3 = PartsOf(provider, c3);
        Rig.Release(Ask(c3, item));
        await Task.WhenAll(closed, p3).WaitAsync(Wait, Ct);

        // Another item's streams count apart: it opens c4 while the first has two.
        var p4 = PartsOf(provider, c4);
        Rig.Release(Ask(c4, other));
        await p4.WaitAsync(Wait, Ct);

        // With both of its streams asked for, none idle, the item's third is refused; c2 was never closed.
        var refused = AnswerOf(provider, c5);
        Rig.Release(Ask(c2, item));
        Rig.Release(Ask(c3, item));
        Rig.Release(Ask(c5, item));
        await refused.WaitAsync(Wait, Ct);
        var fifth = Ask(c5, item);
        Assert.Null(fifth.Sample);
        Assert.Equal(new MediaStatus("media.budget", "Too many streams for one item: 2 are open."), fifth.Status);
        var shown = Ask(c2, item);
        Assert.Equal("red", Colour(shown.Sample));
        Rig.Release(shown);
        Assert.Equal(1, RequestsFor(rig, "/cam2.mjpg"));
    }

    [Fact]
    public async Task An_item_joining_streams_other_items_opened_counts_them_among_its_limit()
    {
        await using var rig = new Rig(OnePartThenWait);
        using var provider = Limited(rig, perItem: 2);
        string c1 = rig.Url("/cam1.mjpg"), c2 = rig.Url("/cam2.mjpg"), c3 = rig.Url("/cam3.mjpg");
        object item = new(), o1 = new(), o2 = new(), o3 = new();
        ProvidedMedia Ask(string c, object who) => provider.Get(new MediaRequest(c, 0, MediaPlayback.Loop), new MediaAsk(new MediaTick(), who));

        // Three other items open a stream each.
        var opened = Task.WhenAll(PartsOf(provider, c1), PartsOf(provider, c2), PartsOf(provider, c3));
        Rig.Release(Ask(c1, o1));
        Rig.Release(Ask(c2, o2));
        Rig.Release(Ask(c3, o3));
        await opened.WaitAsync(Wait, Ct);

        // The item joins c1 and c2, its limit of 2. With every stream asked for (none idle), its join of c3 is refused for
        // it alone: c3 stays open, and shows its picture, for the item that opened it.
        Rig.Release(Ask(c1, item));
        Rig.Release(Ask(c2, item));
        var refused = Ask(c3, item);
        Assert.Null(refused.Sample);
        Assert.Equal(new MediaStatus("media.budget", "Too many streams for one item: 2 are open."), refused.Status);
        var others = Ask(c3, o3);
        Assert.Equal("red", Colour(others.Sample));
        Assert.Null(others.Status);
        Rig.Release(others);

        // Once c1 is idle (no tick asked for it in 2 s), the item's join of c3 closes c1, the one it asked for longest ago.
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        Rig.Release(Ask(c2, o2));
        Rig.Release(Ask(c3, o3));
        Rig.Release(Ask(c2, item));
        var closed = AnswerOf(provider, c1);
        var joined = Ask(c3, item);
        await closed.WaitAsync(Wait, Ct);
        Assert.Equal("red", Colour(joined.Sample));
        Assert.Null(joined.Status);
        Rig.Release(joined);
        Assert.Equal(1, RequestsFor(rig, "/cam3.mjpg"));
    }

    [Fact]
    public async Task Two_thousand_items_on_one_camera_cost_a_tick_work_in_proportion_to_them_and_items_that_stop_asking_are_forgotten()
    {
        await using var rig = new Rig(OnePartThenWait);
        using var provider = Limited(rig);
        var camera = rig.Url("/cam.mjpg");
        var items = Enumerable.Range(0, 2000).Select(_ => new object()).ToArray();
        void Tick(IEnumerable<object> asking)
        {
            var tick = new MediaTick();
            foreach (var item in asking) Rig.Release(provider.Get(new MediaRequest(camera, 0, MediaPlayback.Loop), new MediaAsk(tick, item)));
        }

        // The cold tick, at the clock's start (zero): registering the items is no quadratic either.
        var opened = PartsOf(provider, camera);
        Tick(items);
        await opened.WaitAsync(Wait, Ct);
        var cold = provider.AskerVisits;
        Assert.True(cold <= 2 * items.Length, $"The first tick of {items.Length} items visited {cold} askers.");

        // Each tick, a second apart: every item noted once, and the sweep (once a second) looks at each once. Noting an
        // item looks at no other (2,000 items each looking at all 2,000 would be 4 million a tick, under the provider's lock).
        for (var t = 0; t < 3; t++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            var before = provider.AskerVisits;
            Tick(items);
            var visits = provider.AskerVisits - before;
            Assert.True(visits <= 2 * items.Length, $"A tick of {items.Length} items visited {visits} askers.");
        }
        Assert.Equal(items.Length, provider.AskersOf(camera));

        // One item goes on asking; the others, asked 30 s ago and more, are forgotten by the sweep.
        for (var t = 0; t < 31; t++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            Tick([items[0]]);
        }
        Assert.Equal(1, provider.AskersOf(camera));
    }

    [Fact]
    public async Task A_stream_an_item_at_its_limit_asks_for_too_opens_for_the_items_with_room_and_is_refused_only_to_that_item()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var headers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(async (r, s, t) =>
        {
            if (r.Path == "/camE.mjpg")
            {
                // E's answer waits until the test lets it go.
                arrived.TrySetResult();
                await headers.Task.WaitAsync(t);
            }
            await OnePartThenWait(r, s, t);
        });
        using var provider = Limited(rig, perItem: 2);
        string c1 = rig.Url("/cam1.mjpg"), c2 = rig.Url("/cam2.mjpg"), e = rig.Url("/camE.mjpg");
        object a = new(), b = new();
        ProvidedMedia Ask(string c, object who, MediaTick? tick = null) =>
            provider.Get(new MediaRequest(c, 0, MediaPlayback.Loop), new MediaAsk(tick ?? new MediaTick(), who));

        // A keeps two streams open, its limit; neither is idle.
        var two = Task.WhenAll(PartsOf(provider, c1), PartsOf(provider, c2));
        Rig.Release(Ask(c1, a));
        Rig.Release(Ask(c2, a));
        await two.WaitAsync(Wait, Ct);

        // In one tick B, with room, asks for E, and A asks for E too before E's answer comes.
        var tick = new MediaTick();
        Rig.Release(Ask(e, b, tick));
        await arrived.Task.WaitAsync(Wait, Ct);
        Rig.Release(Ask(e, a, tick));
        var part = PartsOf(provider, e);
        var ended = AnswerOf(provider, e);
        headers.SetResult();
        await Task.WhenAny(part, ended).WaitAsync(Wait, Ct);

        // E opens for B, and shows its picture to it. A is refused E alone; its own two streams stay open.
        Assert.True(part.IsCompleted, "E didn't open.");
        var shown = Ask(e, b);
        Assert.Equal("red", Colour(shown.Sample));
        Assert.Null(shown.Status);
        Rig.Release(shown);
        var refused = Ask(e, a);
        Assert.Null(refused.Sample);
        Assert.Equal(new MediaStatus("media.budget", "Too many streams for one item: 2 are open."), refused.Status);
        var again = Ask(e, b);
        Assert.Equal("red", Colour(again.Sample));
        Assert.Null(again.Status);
        Rig.Release(again);
        Assert.Equal(1, RequestsFor(rig, "/camE.mjpg"));
        Assert.Equal(1, RequestsFor(rig, "/cam1.mjpg"));
        Assert.Equal(1, RequestsFor(rig, "/cam2.mjpg"));
    }

    [Fact]
    public async Task Parts_held_and_being_read_count_against_the_streams_budget_and_past_it_the_idle_stream_closes_first()
    {
        // Parts' bytes count as they are read, not only once whole: these aren't JPEGs, and no tick takes them.
        var sizes = Channel.CreateUnbounded<int>();
        await using var rig = new Rig(async (r, s, t) =>
        {
            await LocalHttpServer.Head(s, 200, Multipart);
            if (r.Path == "/cam1.mjpg")
            {
                await Part(s, new byte[1000], t);
                await Task.Delay(Timeout.Infinite, t);
                return;
            }
            await foreach (var size in sizes.Reader.ReadAllAsync(t)) await Part(s, new byte[size], t);
        });
        using var provider = Limited(rig, streamBytes: 2500);
        string c1 = rig.Url("/cam1.mjpg"), c2 = rig.Url("/cam2.mjpg");

        // c1 holds a part no tick has taken; then it is idle.
        var p1 = PartsOf(provider, c1);
        Rig.Release(provider.Get(new MediaRequest(c1, 0, MediaPlayback.Loop)));
        await p1.WaitAsync(Wait, Ct);
        rig.Time.Advance(TimeSpan.FromSeconds(3));

        // c2 holds one part and reads another: 3000 bytes with c1's, past 2500, so c1 closes and c2 goes on.
        var p2 = PartsOf(provider, c2, 2);
        var closed = AnswerOf(provider, c1);
        Rig.Release(provider.Get(new MediaRequest(c2, 0, MediaPlayback.Loop)));
        sizes.Writer.TryWrite(1000);
        sizes.Writer.TryWrite(1000);
        await Task.WhenAll(p2, closed).WaitAsync(Wait, Ct);

        // A part that alone takes c2 past the budget, with no idle stream left to close, ends c2.
        var ended = AnswerOf(provider, c2);
        sizes.Writer.TryWrite(3000);
        await ended.WaitAsync(Wait, Ct);
        var after = provider.Get(new MediaRequest(c2, 0, MediaPlayback.Loop));
        Assert.Null(after.Sample);
        Assert.Equal(new MediaStatus("media.budget", "The streams are over their budget of 2500 bytes."), after.Status);
    }

    [Fact]
    public async Task A_stream_no_tick_asks_for_closes_30_seconds_after_the_last_ask_with_no_further_lookup()
    {
        var parts = Channel.CreateUnbounded<byte[]>();
        await using var rig = new Rig(async (r, s, t) =>
        {
            await LocalHttpServer.Head(s, 200, Multipart);
            await foreach (var part in parts.Reader.ReadAllAsync(t)) await Part(s, part, t);
        });
        var url = rig.Url("/video.mjpg");
        var first = Parts(rig, url, 1);
        parts.Writer.TryWrite(Red);
        Rig.Release(rig.Ask(url));
        await first.WaitAsync(Wait, Ct);
        // The last ask, at 0 s.
        Rig.Release(rig.Ask(url));

        // The camera sends a part every 10 s or less, so the stall limit never ends it; nothing asks the provider anything.
        var ended = rig.NextAnswer(url);
        foreach (var seconds in new[] { 10, 10, 9.9 })
        {
            rig.Time.Advance(TimeSpan.FromSeconds(seconds));
            var next = Parts(rig, url, 1);
            parts.Writer.TryWrite(Green);
            await next.WaitAsync(Wait, Ct);
        }
        Assert.False(ended.IsCompleted);
        Assert.Equal(1, rig.Provider.FetcherCount);

        rig.Time.Advance(TimeSpan.FromSeconds(0.1));
        await ended.WaitAsync(Wait, Ct);
        Assert.Equal(0, rig.Provider.FetcherCount);
    }

    [Fact]
    public async Task A_repeated_part_whose_bytes_and_frame_were_dropped_is_shown_again_with_asynchronous_decoding()
    {
        var parts = Channel.CreateUnbounded<byte[]>();
        await using var rig = new Rig(async (r, s, t) =>
        {
            await LocalHttpServer.Head(s, 200, Multipart);
            await foreach (var part in parts.Reader.ReadAllAsync(t)) await Part(s, part, t);
        }, synchronous: false);
        var url = rig.Url("/video.mjpg");
        var red = FetchedMediaStore.KeyOf(Red);

        Task Decoded(string hash)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(string h)
            {
                if (h != hash) return;
                rig.Cache.FrameDecoded -= Handler;
                done.TrySetResult();
            }
            rig.Cache.FrameDecoded += Handler;
            return done.Task;
        }

        // One decode worker takes its work in order: once another picture is decoded, all asked before it is done.
        async Task Settle(byte[] other)
        {
            var key = rig.Store.Put(other);
            var done = Decoded(key);
            rig.Cache.Get(key, 0, MediaPlayback.Loop).Sample?.Release();
            await done.WaitAsync(Wait, Ct);
        }

        var first = Parts(rig, url, 1);
        parts.Writer.TryWrite(Red);
        Rig.Release(rig.Ask(url));
        await first.WaitAsync(Wait, Ct);
        var decoded = Decoded(red);
        Rig.Release(rig.Ask(url));
        await decoded.WaitAsync(Wait, Ct);
        var shown = rig.Ask(url);
        Assert.Equal("red", Colour(shown.Sample));
        Rig.Release(shown);

        // The store drops its bytes and the cache its frame (as their budgets may); a lookup then fails on a worker.
        rig.Store.Remove(red);
        rig.Cache.Forget(red);
        Rig.Release(rig.Ask(url));
        await Settle(Blue);

        // The camera sends the same picture again: its bytes are back, and once decoded it is shown again, and stays.
        foreach (var other in new[] { Green, Yellow })
        {
            var again = Parts(rig, url, 1);
            parts.Writer.TryWrite(Red);
            await again.WaitAsync(Wait, Ct);
            Rig.Release(rig.Ask(url));
            await Settle(other);
            var back = rig.Ask(url);
            Assert.Null(back.Status);
            Assert.Equal("red", Colour(back.Sample));
            Rig.Release(back);
        }
    }

    [Fact]
    public async Task A_camera_that_answers_only_requests_accepting_a_stream_is_shown()
    {
        await using var rig = new Rig(async (r, s, t) =>
        {
            // A camera enforcing negotiation: 406 to a request that doesn't accept its stream.
            if (r.Header("accept")?.Contains("multipart/x-mixed-replace", StringComparison.Ordinal) != true)
            {
                await LocalHttpServer.Answer(s, 406, contentType: null);
                return;
            }
            await LocalHttpServer.Head(s, 200, Multipart);
            await Part(s, Red, t);
            await Task.Delay(Timeout.Infinite, t);
        });
        var url = rig.Url("/video.mjpg");
        var first = Task.WhenAny(Parts(rig, url, 1), rig.NextAnswer(url));
        Rig.Release(rig.Ask(url));
        await first.WaitAsync(Wait, Ct);
        var shown = rig.Ask(url);
        Assert.Null(shown.Status);
        Assert.Equal("red", Colour(shown.Sample));
        Rig.Release(shown);
        Assert.Equal("image/*, multipart/x-mixed-replace", rig.Server.Requests.Single().Header("accept"));
    }
}
