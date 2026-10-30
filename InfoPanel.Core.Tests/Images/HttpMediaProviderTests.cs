using System.Net;
using System.Net.Sockets;
using System.Text;
using InfoPanel.Core.Tests.Scenes;
using InfoPanel.Core.Tests.Sensors;
using InfoPanel.Images;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Images;

/// <summary>
/// The <c>http</c> media provider (spec/docs/providers.md) against servers on 127.0.0.1 that the tests start: no test
/// reaches past loopback. Names resolve through a fake resolver and every connection, whatever address it was allowed
/// to, is made to the test's server and recorded, so the address a connection was checked for is the one it used.
/// Time is a fake clock (refresh, retries, idling, the time limits); a fetch is waited for on the provider's
/// <see cref="HttpMediaProvider.Answered"/>, never by sleeping.
/// </summary>
public sealed class HttpMediaProviderTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static byte[] Png(SKColor color, int w = 4, int h = 3)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static readonly byte[] Red = Png(SKColors.Red);
    private static readonly byte[] Blue = Png(SKColors.Blue, 6, 5);

    /// <summary>A PNG whose header is sound (it passes the inspection an asset gets) but whose pixel data can't be decoded.</summary>
    internal static byte[] BrokenPng()
    {
        var png = Png(SKColors.Lime);
        var idat = png.AsSpan().IndexOf("IDAT"u8);
        var length = (png[idat - 4] << 24) | (png[idat - 3] << 16) | (png[idat - 2] << 8) | png[idat - 1];
        png.AsSpan(idat + 4, length).Fill(0xff);
        return png;
    }

    /// <summary>A provider over a server, a fake clock, a fake resolver and a recording connector.</summary>
    internal sealed class Rig : IAsyncDisposable
    {
        public readonly TimerTimeProvider Time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        public readonly FetchedMediaStore Store = new();
        public readonly MediaSampleCache Cache;
        public readonly HttpMediaProvider Provider;
        public readonly LocalHttpServer Server;
        public readonly List<IPEndPoint> Connected = [];
        public bool LocalNetwork = true;

        /// <summary>The fake resolver's answers by host name; a name it lacks isn't found.</summary>
        public Func<string, IPAddress[]?> Names = _ => null;

        /// <summary>Every name the fake resolver was asked for.</summary>
        public readonly List<string> Resolved = [];

        public Rig(Func<ServedRequest, Stream, CancellationToken, Task> handler, SpecLimits? limits = null, bool loopbackOnly = false, TimeSpan? answerTimeout = null, bool synchronous = true)
        {
            Server = new LocalHttpServer(handler);
            // Synchronous decoding unless a test needs the engine's real one: decode workers (one here, so a test can hold it).
            Cache = new MediaSampleCache(new EngineMediaStore(new NoAssets(), Store), new MediaSampleCacheOptions { Synchronous = synchronous, Workers = 1, Time = Time, Limits = limits ?? SpecLimits.Default });
            Provider = new HttpMediaProvider(Cache, Store, new HttpMediaProviderOptions
            {
                Time = Time,
                Limits = limits ?? SpecLimits.Default,
                LocalNetwork = () => LocalNetwork,
                LoopbackOnly = loopbackOnly,
                AnswerTimeout = answerTimeout ?? TimeSpan.FromSeconds(15),
                UserAgent = "InfoPanel/2.0.0",
                Resolve = (host, _) =>
                {
                    lock (Resolved) Resolved.Add(host);
                    return Task.FromResult(Names(host) ?? throw new SocketException((int)SocketError.HostNotFound));
                },
                Connect = (endpoint, token) =>
                {
                    lock (Connected) Connected.Add(endpoint);
                    return HttpMediaProvider.ConnectSocketAsync(new IPEndPoint(IPAddress.Loopback, Server.Port), token);
                },
            });
        }

        public string Url(string path) => Server.Url(path);

        /// <summary>The next answer for <paramref name="address"/>.</summary>
        public Task NextAnswer(string address)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(string a)
            {
                if (a != address) return;
                Provider.Answered -= Handler;
                done.TrySetResult();
            }
            Provider.Answered += Handler;
            return done.Task;
        }

        /// <summary>Asks as a tick does, waits for the fetch it starts, and answers what the next tick gets.</summary>
        public async Task<ProvidedMedia> Fetch(string address, int refresh = 0, double seconds = 0)
        {
            var answered = NextAnswer(address);
            Release(Ask(address, refresh, seconds));
            await answered.WaitAsync(Wait, Ct);
            return Ask(address, refresh, seconds);
        }

        public ProvidedMedia Ask(string address, int refresh = 0, double seconds = 0, bool live = true) =>
            Provider.Get(new MediaRequest(address, seconds, MediaPlayback.Loop, refresh, live));

        public static void Release(ProvidedMedia media) => media.Sample?.Release();

        public async ValueTask DisposeAsync()
        {
            Provider.Dispose();
            await Server.DisposeAsync();
            Cache.Dispose();
        }
    }

    private sealed class NoAssets : IMediaAssetStore
    {
        public byte[]? Read(string hash) => null;
    }

    private static SKColor Pixel(MediaSample sample)
    {
        using var bitmap = SKBitmap.FromImage(sample.Image!);
        return bitmap.GetPixel(0, 0);
    }

    // ---- fetching -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_ask_loads_and_a_later_tick_shows_the_image()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        var url = rig.Url("/radar.png");

        var answered = rig.NextAnswer(url);
        var first = rig.Ask(url);
        Assert.Null(first.Sample);
        Assert.Equal("media.loading", first.Status?.Code);
        await answered.WaitAsync(Wait, Ct);
        var shown = rig.Ask(url);
        Assert.NotNull(shown.Sample);
        Assert.Null(shown.Status);
        Assert.Equal((4f, 3f), (shown.Sample.Width, shown.Sample.Height));
        Assert.Equal(SKColors.Red, Pixel(shown.Sample));
        Rig.Release(shown);

        var request = Assert.Single(rig.Server.Requests);
        Assert.Equal(("GET", "/radar.png"), (request.Method, request.Path));
        Assert.Equal("image/*", request.Header("accept"));
        Assert.Equal("InfoPanel/2.0.0", request.Header("user-agent"));
        Assert.Null(request.Header("cookie"));
        Assert.Null(request.Header("authorization"));
        Assert.Null(request.Header("accept-encoding"));
    }

    [Fact]
    public async Task Items_showing_one_address_share_one_fetcher()
    {
        var gate = new TaskCompletionSource();
        await using var rig = new Rig(async (r, s, t) =>
        {
            await gate.Task.WaitAsync(t);
            await LocalHttpServer.Answer(s, 200, Red);
        });
        var url = rig.Url("/shared.png");
        var answered = rig.NextAnswer(url);
        Rig.Release(rig.Ask(url, refresh: 10));
        Rig.Release(rig.Ask(url, refresh: 0));
        Rig.Release(rig.Ask("  " + url + " ", refresh: 60));
        gate.SetResult();
        await answered.WaitAsync(Wait, Ct);

        var a = rig.Ask(url, refresh: 10);
        var b = rig.Ask(url, refresh: 0);
        Assert.Same(a.Sample!.Image, b.Sample!.Image);
        Rig.Release(a);
        Rig.Release(b);
        Assert.Single(rig.Server.Requests);
        Assert.Equal(1, rig.Provider.FetcherCount);
    }

    [Fact]
    public async Task A_refresh_told_by_an_item_sharing_the_answer_counts_as_asked()
    {
        // Each tick asks with refresh 0 and is told of another item's 10 (the scene shares one answer per tick): the
        // reference is fetched again 10 s after its answer, as the shortest refresh says.
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        var url = rig.Url("/radar.png");
        void Tell() => rig.Provider.Asked(new MediaRequest(url, 0, MediaPlayback.Loop, 10));
        var answered = rig.NextAnswer(url);
        Rig.Release(rig.Ask(url));
        Tell();
        await answered.WaitAsync(Wait, Ct);
        // Asked at 3, 6 and 9.9 s: not yet.
        foreach (var step in new[] { 3, 3, 3.9 })
        {
            rig.Time.Advance(TimeSpan.FromSeconds(step));
            Rig.Release(rig.Ask(url));
            Tell();
            Assert.Single(rig.Server.Requests);
        }
        rig.Time.Advance(TimeSpan.FromSeconds(0.1));
        Rig.Release(await rig.Fetch(url));
        Assert.Equal(2, rig.Server.Requests.Count);

        // A one-off render's telling counts for nothing, and an address with no fetcher is ignored.
        rig.Provider.Asked(new MediaRequest(rig.Url("/other.png"), 0, MediaPlayback.Loop, 1));
        Assert.Equal(1, rig.Provider.FetcherCount);
    }

    [Fact]
    public async Task A_reference_no_tick_asks_for_is_dropped_after_30_seconds_and_fetched_again_when_asked()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        var url = rig.Url("/once.png");
        Rig.Release(await rig.Fetch(url));

        rig.Time.Advance(TimeSpan.FromSeconds(29));
        Rig.Release(rig.Ask(url));
        Assert.Single(rig.Server.Requests);
        rig.Time.Advance(TimeSpan.FromSeconds(29));
        Rig.Release(rig.Ask(rig.Url("/other.png"), live: false));
        Assert.Equal(1, rig.Provider.FetcherCount);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        // Nothing asked for 30 s: the next look drops it (a one-off render's ask starts nothing).
        Rig.Release(rig.Ask(rig.Url("/other.png"), live: false));
        Assert.Equal(0, rig.Provider.FetcherCount);
        Assert.Single(rig.Server.Requests);

        // Asked again, it is fetched again.
        Rig.Release(await rig.Fetch(url));
        Assert.Equal(2, rig.Server.Requests.Count);
    }

    [Fact]
    public async Task A_refresh_fetches_again_after_its_seconds_and_a_304_keeps_the_image()
    {
        await using var rig = new Rig((r, s, t) => r.Header("if-none-match") == "\"v1\""
            ? LocalHttpServer.Answer(s, 304, contentType: null, headers: "ETag: \"v1\"")
            : LocalHttpServer.Answer(s, 200, Red, headers: "ETag: \"v1\""));
        var url = rig.Url("/radar.png");
        var first = await rig.Fetch(url, refresh: 10);
        var image = first.Sample!.Image;
        Rig.Release(first);

        rig.Time.Advance(TimeSpan.FromSeconds(9.9));
        Rig.Release(rig.Ask(url, refresh: 10));
        Assert.Single(rig.Server.Requests);

        rig.Time.Advance(TimeSpan.FromSeconds(0.1));
        var again = await rig.Fetch(url, refresh: 10);
        Assert.Equal(2, rig.Server.Requests.Count);
        Assert.Equal("\"v1\"", rig.Server.Requests[1].Header("if-none-match"));
        Assert.Same(image, again.Sample!.Image);
        Assert.Null(again.Status);
        Rig.Release(again);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_last_good_image_and_says_why()
    {
        var count = 0;
        await using var rig = new Rig((r, s, t) => Interlocked.Increment(ref count) == 1
            ? LocalHttpServer.Answer(s, 200, Red)
            : LocalHttpServer.Answer(s, 500, Encoding.UTF8.GetBytes("down"), "text/plain"));
        var url = rig.Url("/radar.png");
        Rig.Release(await rig.Fetch(url, refresh: 1));

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var after = await rig.Fetch(url, refresh: 1);
        Assert.Equal(2, rig.Server.Requests.Count);
        Assert.NotNull(after.Sample);
        Assert.Equal(SKColors.Red, Pixel(after.Sample));
        Assert.Equal(new MediaStatus("media.provider", "The server answered 500."), after.Status);
        Rig.Release(after);
    }

    [Fact]
    public async Task A_new_image_replaces_the_old_one_at_the_refresh()
    {
        var count = 0;
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Interlocked.Increment(ref count) == 1 ? Red : Blue));
        var url = rig.Url("/radar.png");
        var first = await rig.Fetch(url, refresh: 2);
        Assert.Equal(SKColors.Red, Pixel(first.Sample!));
        Rig.Release(first);
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        var second = await rig.Fetch(url, refresh: 2);
        Assert.Equal(SKColors.Blue, Pixel(second.Sample!));
        Assert.Equal((6f, 5f), (second.Sample!.Width, second.Sample.Height));
        Rig.Release(second);
    }

    [Fact]
    public async Task With_no_refresh_a_failure_is_retried_after_5_10_and_20_seconds()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 404, contentType: null));
        var url = rig.Url("/nothing.png");
        var failed = await rig.Fetch(url);
        Assert.Null(failed.Sample);
        Assert.Equal(new MediaStatus("media.provider", "The server answered 404."), failed.Status);

        foreach (var (seconds, count) in new[] { (5, 2), (10, 3), (20, 4) })
        {
            rig.Time.Advance(TimeSpan.FromSeconds(seconds - 0.5));
            Rig.Release(rig.Ask(url));
            Assert.Equal(count - 1, rig.Server.Requests.Count);
            rig.Time.Advance(TimeSpan.FromSeconds(0.5));
            Rig.Release(await rig.Fetch(url));
            Assert.Equal(count, rig.Server.Requests.Count);
        }
    }

    [Fact]
    public async Task A_refresh_that_cant_be_decoded_keeps_the_last_good_image_and_says_why()
    {
        var count = 0;
        var broken = BrokenPng();
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Interlocked.Increment(ref count) == 1 ? Red : broken));
        var url = rig.Url("/radar.png");
        Rig.Release(await rig.Fetch(url, refresh: 1));

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var after = await rig.Fetch(url, refresh: 1);
        Assert.Equal(2, rig.Server.Requests.Count);
        Assert.NotNull(after.Sample);
        Assert.Equal(SKColors.Red, Pixel(after.Sample));
        Assert.Equal(new MediaStatus("media.malformed", "The image could not be decoded (ErrorInInput)."), after.Status);
        Rig.Release(after);
        // It stays so on the next ticks, and the next refresh is tried at its time.
        var again = rig.Ask(url, refresh: 1);
        Assert.Equal(SKColors.Red, Pixel(again.Sample!));
        Assert.Equal("media.malformed", again.Status?.Code);
        Rig.Release(again);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Rig.Release(await rig.Fetch(url, refresh: 1));
        Assert.Equal(3, rig.Server.Requests.Count);
    }

    [Fact]
    public async Task An_image_that_cant_be_decoded_says_why_and_is_fetched_again_on_the_retry_schedule()
    {
        var broken = BrokenPng();
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, broken));
        var url = rig.Url("/broken.png");
        var failed = await rig.Fetch(url);
        Assert.Null(failed.Sample);
        Assert.Equal(new MediaStatus("media.malformed", "The image could not be decoded (ErrorInInput)."), failed.Status);
        Assert.Equal("media.malformed", rig.Ask(url).Status?.Code);

        // As any failed fetch: tried again 5 s later, then 10 s after that, not on every tick.
        rig.Time.Advance(TimeSpan.FromSeconds(4.9));
        Assert.Equal("media.malformed", rig.Ask(url).Status?.Code);
        Assert.Single(rig.Server.Requests);
        rig.Time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal("media.malformed", (await rig.Fetch(url)).Status?.Code);
        Assert.Equal(2, rig.Server.Requests.Count);
        rig.Time.Advance(TimeSpan.FromSeconds(9.9));
        Rig.Release(rig.Ask(url));
        Assert.Equal(2, rig.Server.Requests.Count);
        rig.Time.Advance(TimeSpan.FromSeconds(0.1));
        Rig.Release(await rig.Fetch(url));
        Assert.Equal(3, rig.Server.Requests.Count);
    }

    [Fact]
    public async Task With_the_engines_decoding_a_new_image_replaces_the_shown_one_only_once_it_is_decoded()
    {
        // The real decode transition: a decode worker, held on a picture of its own while an image fetched waits behind it;
        // a later fetch's failure isn't cleared when the older image it waited behind is shown.
        var count = 0;
        await using var rig = new Rig((r, s, t) => Interlocked.Increment(ref count) switch
        {
            1 => LocalHttpServer.Answer(s, 200, Red),
            2 => LocalHttpServer.Answer(s, 200, Blue),
            _ => LocalHttpServer.Answer(s, 404, Encoding.UTF8.GetBytes("gone"), "text/plain"),
        }, synchronous: false);
        var url = rig.Url("/radar.png");
        // Subscribed before the worker that decodes it is let go, so the decode can't finish unseen.
        Task Decoded(byte[] bytes)
        {
            var key = FetchedMediaStore.KeyOf(bytes);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Cache.FrameDecoded += hash =>
            {
                if (hash == key) done.TrySetResult();
            };
            return done.Task;
        }
        var holds = new Dictionary<string, (TaskCompletionSource Held, TaskCompletionSource Release)>();
        rig.Cache.DecodingSvg = (hash, _) =>
        {
            (TaskCompletionSource Held, TaskCompletionSource Release) hold;
            lock (holds)
            {
                if (!holds.TryGetValue(hash, out hold)) return;
            }
            hold.Held.TrySetResult();
            hold.Release.Task.Wait();
        };
        // Holds the one decode worker on a picture of its own until the answer is completed.
        async Task<TaskCompletionSource> Hold(string fill)
        {
            var hold = (Held: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), Release: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            var blocker = rig.Store.Put(Encoding.UTF8.GetBytes($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"><rect width=\"4\" height=\"4\" fill=\"{fill}\"/></svg>"));
            lock (holds) holds[blocker] = hold;
            Assert.Null(rig.Cache.Get(blocker, 0, MediaPlayback.Still).Sample);
            await hold.Held.Task.WaitAsync(Wait, Ct);
            return hold.Release;
        }

        try
        {
            // The worker is held, so the first image is fetched and seen undecoded.
            var first = await Hold("#00ff00");
            var red = Decoded(Red);
            var decoding = await rig.Fetch(url, refresh: 2);
            Assert.Null(decoding.Sample);
            Assert.Equal("media.loading", decoding.Status?.Code);
            first.SetResult();
            await red.WaitAsync(Wait, Ct);
            var shown = rig.Ask(url, refresh: 2);
            Assert.Equal(SKColors.Red, Pixel(shown.Sample!));
            Assert.Null(shown.Status);
            Rig.Release(shown);

            // Held again, so the refreshed image waits to be decoded.
            var second = await Hold("#00fe00");
            var blue = Decoded(Blue);
            rig.Time.Advance(TimeSpan.FromSeconds(2));
            Rig.Release(await rig.Fetch(url, refresh: 2));
            Assert.Equal(2, rig.Server.Requests.Count);
            // Fetched, not yet decoded: the red image stays, with no problem.
            var waiting = rig.Ask(url, refresh: 2);
            Assert.Equal(SKColors.Red, Pixel(waiting.Sample!));
            Assert.Null(waiting.Status);
            Rig.Release(waiting);

            // The next refresh fails while the blue image still waits: the red one stays, saying why.
            rig.Time.Advance(TimeSpan.FromSeconds(2));
            var failed = await rig.Fetch(url, refresh: 2);
            Assert.Equal(3, rig.Server.Requests.Count);
            Assert.Equal(SKColors.Red, Pixel(failed.Sample!));
            Assert.Equal(new MediaStatus("media.provider", "The server answered 404."), failed.Status);
            Rig.Release(failed);

            // The blue image, decoded, replaces the red one, and the newer fetch's failure still stands.
            second.SetResult();
            await blue.WaitAsync(Wait, Ct);
            var replaced = rig.Ask(url, refresh: 2);
            Assert.Equal(SKColors.Blue, Pixel(replaced.Sample!));
            Assert.Equal(new MediaStatus("media.provider", "The server answered 404."), replaced.Status);
            Rig.Release(replaced);
        }
        finally
        {
            lock (holds)
            {
                foreach (var hold in holds.Values) hold.Release.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task Nothing_is_fetched_while_no_tick_asks()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        var url = rig.Url("/radar.png");
        Rig.Release(await rig.Fetch(url, refresh: 1));
        // Ten refreshes' time with no ask, then a look from a one-off render: nothing fetched, nothing started.
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, rig.Provider.FetchesStarted);
        var look = rig.Ask(url, refresh: 1, live: false);
        Assert.NotNull(look.Sample);
        Rig.Release(look);
        Assert.Equal(1, rig.Provider.FetchesStarted);
        Assert.Single(rig.Server.Requests);
        // A one-off render of an address no tick asked for starts nothing either.
        var other = rig.Ask(rig.Url("/other.png"), live: false);
        Assert.Null(other.Sample);
        Assert.Equal("media.loading", other.Status?.Code);
        Assert.Single(rig.Server.Requests);
        Assert.Equal(1, rig.Provider.FetchesStarted);
    }

    // ---- limits -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_body_over_mediaBytes_is_refused_as_it_is_read()
    {
        var limits = SpecLimits.Default.With(new Dictionary<string, double> { ["mediaBytes"] = 1000 });
        // No length, 1001 bytes, then the server holds the connection open: the provider must stop at the limit.
        await using var rig = new Rig(async (r, s, t) =>
        {
            await LocalHttpServer.Head(s, 200);
            await s.WriteAsync(new byte[1001], t);
            await s.FlushAsync(t);
            await Task.Delay(Timeout.Infinite, t);
        }, limits);
        var url = rig.Url("/huge.png");
        var refused = await rig.Fetch(url);
        Assert.Null(refused.Sample);
        Assert.Equal(new MediaStatus("limit.mediaBytes", "The image is over 1000 bytes (mediaBytes)."), refused.Status);
    }

    [Fact]
    public async Task A_length_over_mediaBytes_is_refused_before_reading()
    {
        var limits = SpecLimits.Default.With(new Dictionary<string, double> { ["mediaBytes"] = 1000 });
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, new byte[1001]), limits);
        var refused = await rig.Fetch(rig.Url("/huge.png"));
        Assert.Equal("limit.mediaBytes", refused.Status?.Code);
    }

    [Fact]
    public async Task An_answer_that_stalls_is_given_up_after_15_seconds()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(async (r, s, t) =>
        {
            await LocalHttpServer.Head(s, 200);
            await s.WriteAsync(Red.AsMemory(0, 10), t);
            await s.FlushAsync(t);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, t);
        });
        var url = rig.Url("/slow.png");
        var answered = rig.NextAnswer(url);
        Rig.Release(rig.Ask(url));
        await started.Task.WaitAsync(Wait, Ct);
        rig.Time.Advance(TimeSpan.FromSeconds(15));
        await answered.WaitAsync(Wait, Ct);
        var after = rig.Ask(url);
        Assert.Null(after.Sample);
        Assert.Equal(new MediaStatus("media.provider", "The server took longer than 15 s."), after.Status);
    }

    [Fact]
    public async Task Five_redirects_are_followed_and_a_sixth_is_refused()
    {
        await using var rig = new Rig((r, s, t) =>
        {
            // /a0 … /a5 and /b0 … /b6: each redirects to the next; the last of each answers the image.
            var n = int.Parse(r.Path[2..]);
            var last = r.Path[1] == 'a' ? 5 : 6;
            return n < last
                ? LocalHttpServer.Answer(s, 302, contentType: null, headers: $"Location: /{r.Path[1]}{n + 1}")
                : LocalHttpServer.Answer(s, 200, Red);
        });
        var five = await rig.Fetch(rig.Url("/a0"));
        Assert.NotNull(five.Sample);
        Rig.Release(five);
        Assert.Equal(6, rig.Server.Requests.Count);

        var six = await rig.Fetch(rig.Url("/b0"));
        Assert.Null(six.Sample);
        Assert.Equal(new MediaStatus("media.provider", "The server redirected more than 5 times."), six.Status);
        Assert.Equal(12, rig.Server.Requests.Count);
        Assert.DoesNotContain(rig.Server.Requests, q => q.Path == "/b6");
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/", "169.254.169.254 is a link-local address.")]
    [InlineData("http://[fe80::1]/x.png", "fe80::1 is a link-local address.")]
    [InlineData("http://metadata.test/x.png", "169.254.169.254 is a link-local address.")]
    public async Task A_redirect_to_a_refused_address_is_refused(string target, string why)
    {
        await using var rig = new Rig((r, s, t) => r.Path == "/start"
            ? LocalHttpServer.Answer(s, 302, contentType: null, headers: $"Location: {target}")
            : LocalHttpServer.Answer(s, 200, Red));
        rig.Names = host => host == "metadata.test" ? [IPAddress.Parse("169.254.169.254")] : null;
        var refused = await rig.Fetch(rig.Url("/start"));
        Assert.Null(refused.Sample);
        Assert.Equal(new MediaStatus("media.address", why), refused.Status);
        Assert.Single(rig.Server.Requests);
        lock (rig.Connected) Assert.All(rig.Connected, e => Assert.Equal(IPAddress.Loopback, e.Address));
    }

    [Fact]
    public async Task Bytes_that_are_not_an_image_are_refused_as_an_asset_would_be()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Encoding.UTF8.GetBytes("<html>not an image</html>"), "image/png"));
        var refused = await rig.Fetch(rig.Url("/page.png"));
        Assert.Null(refused.Sample);
        Assert.Equal("media.format", refused.Status?.Code);
    }

    [Fact]
    public async Task An_animated_gif_is_timed_by_the_tick()
    {
        var gif = File.ReadAllBytes(Path.Combine(SceneKit.ExamplesDir, "media", "valid", "blink.gif"));
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, gif, "image/gif"));
        var url = rig.Url("/blink.gif");
        Rig.Release(await rig.Fetch(url));
        // The pixels each tick's time is answered with: the animation moves, and a time is always the same frame.
        SKColor At(double seconds)
        {
            var media = rig.Ask(url, seconds: seconds);
            Assert.NotNull(media.Sample);
            try
            {
                using var bitmap = SKBitmap.FromImage(media.Sample.Image!);
                return bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
            }
            finally
            {
                Rig.Release(media);
            }
        }
        var times = new[] { 0.0, 0.35, 0.7, 1.05 };
        var shown = times.Select(At).ToList();
        Assert.True(shown.Distinct().Count() > 1, $"every tick showed the same pixels: {string.Join(", ", shown)}");
        Assert.Equal(shown, times.Select(At));
    }

    // ---- what is sent ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_addresses_user_info_is_sent_as_the_server_asks_and_no_cookie_is_sent()
    {
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:pa ss"));
        await using var rig = new Rig((r, s, t) =>
            r.Header("authorization") is null
                ? LocalHttpServer.Answer(s, 401, contentType: null, headers: "WWW-Authenticate: Basic realm=\"camera\"")
                : r.Header("authorization") == expected
                    ? LocalHttpServer.Answer(s, 200, Red, headers: "Set-Cookie: session=abc; Path=/")
                    : LocalHttpServer.Answer(s, 403, contentType: null));
        var url = $"http://admin:pa%20ss@127.0.0.1:{rig.Server.Port}/snapshot.jpg";
        var shown = await rig.Fetch(url, refresh: 1);
        Assert.NotNull(shown.Sample);
        Rig.Release(shown);
        Assert.Equal(2, rig.Server.Requests.Count);
        Assert.Null(rig.Server.Requests[0].Header("authorization"));
        Assert.Equal(expected, rig.Server.Requests[1].Header("authorization"));

        // The refresh sends no cookie, though the server set one.
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Rig.Release(await rig.Fetch(url, refresh: 1));
        Assert.All(rig.Server.Requests, q => Assert.Null(q.Header("cookie")));
        Assert.True(rig.Server.Requests.Count >= 3);
    }

    [Fact]
    public async Task Credentials_stay_with_the_reference_that_carries_them()
    {
        static string Basic(string user, string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        await using var rig = new Rig((r, s, t) => r.Header("authorization") switch
        {
            null => LocalHttpServer.Answer(s, 401, contentType: null, headers: "WWW-Authenticate: Basic realm=\"camera\""),
            var a when a == Basic("admin", "one") || a == Basic("admin", "two") => LocalHttpServer.Answer(s, 200, Red),
            _ => LocalHttpServer.Answer(s, 403, contentType: null),
        });
        var bare = rig.Url("/snapshot.jpg");
        var one = $"http://admin:one@127.0.0.1:{rig.Server.Port}/snapshot.jpg";
        var two = $"http://admin:two@127.0.0.1:{rig.Server.Port}/snapshot.jpg";
        Rig.Release(await rig.Fetch(one));
        Rig.Release(await rig.Fetch(two));
        Assert.Equal([null, Basic("admin", "one"), null, Basic("admin", "two")], rig.Server.Requests.Select(q => q.Header("authorization")));

        // The same address without user info is asked as it is: no saved credentials, so the server's 401 is the answer.
        var asked = await rig.Fetch(bare);
        Assert.Null(asked.Sample);
        Assert.Equal(new MediaStatus("media.provider", "The server answered 401."), asked.Status);
        Assert.Null(rig.Server.Requests[^1].Header("authorization"));

        // Each reference keeps its own: one is fetched again (dropped and asked again) with its own password, not two's.
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        Rig.Release(rig.Ask(rig.Url("/other.png"), live: false));
        Assert.Equal(0, rig.Provider.FetcherCount);
        Rig.Release(await rig.Fetch(one));
        Assert.Equal(Basic("admin", "one"), rig.Server.Requests[^1].Header("authorization"));
    }

    [Fact]
    public async Task At_most_four_fetches_run_at_once()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        await using var rig = new Rig(async (r, s, t) =>
        {
            if (Interlocked.Increment(ref received) == 4) fourIn.TrySetResult();
            await gate.Task.WaitAsync(t);
            await LocalHttpServer.Answer(s, 200, Red);
        });
        rig.Provider.Waiting += n =>
        {
            if (n == 2) twoWait.TrySetResult();
        };
        var urls = Enumerable.Range(1, 6).Select(i => rig.Url($"/cam{i}.png")).ToList();
        var answers = urls.Select(rig.NextAnswer).ToList();
        foreach (var url in urls) Rig.Release(rig.Ask(url));

        await Task.WhenAll(fourIn.Task, twoWait.Task).WaitAsync(Wait, Ct);
        Assert.Equal(4, rig.Server.Requests.Count);
        gate.SetResult();
        await Task.WhenAll(answers).WaitAsync(Wait, Ct);
        Assert.Equal(6, rig.Server.Requests.Count);
        Assert.Equal(4, rig.Server.PeakOpen);
        Assert.Equal(4, rig.Provider.PeakRequests);
    }

    [Fact]
    public async Task A_dropped_reference_stops_its_fetch_and_its_queued_one_never_starts()
    {
        // Four fetches held by the server, two more waiting for a slot; then no tick asks for any of them for 30 s. (The
        // answer's time limit is longer here, so only the drop can end the four.)
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        await using var rig = new Rig(async (r, s, t) =>
        {
            if (Interlocked.Increment(ref received) == 4) fourIn.TrySetResult();
            await gate.Task.WaitAsync(t);
            await LocalHttpServer.Answer(s, 200, Red);
        }, answerTimeout: TimeSpan.FromHours(1));
        rig.Provider.Waiting += n =>
        {
            if (n == 2) twoWait.TrySetResult();
        };
        var urls = Enumerable.Range(1, 6).Select(i => rig.Url($"/cam{i}.png")).ToList();
        var answers = urls.Select(rig.NextAnswer).ToList();
        foreach (var url in urls) Rig.Release(rig.Ask(url));
        await Task.WhenAll(fourIn.Task, twoWait.Task).WaitAsync(Wait, Ct);

        rig.Time.Advance(TimeSpan.FromSeconds(30));
        Rig.Release(rig.Ask(rig.Url("/other.png"), live: false));
        Assert.Equal(0, rig.Provider.FetcherCount);
        // Every one of them ends without the server answering: the four under way are stopped, the two waiting never start.
        await Task.WhenAll(answers).WaitAsync(Wait, Ct);
        gate.SetResult();
        Assert.Equal(4, rig.Server.Requests.Count);
        Assert.Equal(0, rig.Store.Bytes);

        // Asked again, each is a new fetch.
        var shown = await rig.Fetch(urls[5]);
        Assert.NotNull(shown.Sample);
        Rig.Release(shown);
        Assert.Equal(5, rig.Server.Requests.Count);
    }

    [Fact]
    public async Task A_fetch_that_ends_after_its_reference_was_dropped_keeps_nothing()
    {
        // A fetch has the image's bytes and is held before it publishes them; the local network setting changes then, so
        // what it brought back is not kept (nothing is left to cancel: only the dropped outcome's discarding keeps it out).
        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        rig.Provider.Publishing = async (_, fetched) =>
        {
            held.TrySetResult(fetched);
            await resume.Task;
        };
        var url = rig.Url("/radar.png");
        var answered = rig.NextAnswer(url);
        Rig.Release(rig.Ask(url));
        Assert.True(await held.Task.WaitAsync(Wait, Ct));
        rig.Provider.Reset();
        resume.SetResult();
        await answered.WaitAsync(Wait, Ct);
        Assert.Equal(0, rig.Store.Bytes);
        Assert.Equal(0, rig.Provider.FetcherCount);
    }

    // ---- addresses --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("radar.png")]
    [InlineData("/radar.png")]
    [InlineData("ftp://example.test/radar.png")]
    [InlineData("file:///etc/passwd")]
    [InlineData("plugin-image://weather/radar")]
    [InlineData("http://exa%6dple.test/radar.png")]
    [InlineData("http://[fe80::1%25en0]/radar.png")]
    [InlineData("Now playing: a song")]
    public async Task A_reference_that_is_not_a_web_address_is_refused_without_a_request(string reference)
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        var refused = rig.Ask(reference);
        Assert.Null(refused.Sample);
        Assert.Equal(new MediaStatus("media.provider", "Not a web address."), refused.Status);
        Assert.Equal(0, rig.Provider.FetcherCount);
    }

    [Fact]
    public async Task An_address_over_2048_characters_is_refused()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        var url = rig.Url("/" + new string('a', 2048));
        Assert.Equal("Not a web address.", rig.Ask(url).Status?.Message);
        Assert.Equal(0, rig.Provider.FetcherCount);
    }

    [Fact]
    public async Task Each_connection_goes_to_the_address_it_checked_however_the_name_answers_later()
    {
        // A name that answers a public address, then this computer's: with the local network off, the connection must
        // be to the public address the check allowed, never to a second answer.
        var answers = 0;
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        rig.LocalNetwork = false;
        rig.Names = host => host == "rebind.test"
            ? [Interlocked.Increment(ref answers) == 1 ? IPAddress.Parse("203.0.113.7") : IPAddress.Loopback]
            : null;
        var shown = await rig.Fetch($"http://rebind.test:{rig.Server.Port}/radar.png");
        Assert.NotNull(shown.Sample);
        Rig.Release(shown);
        lock (rig.Connected)
            Assert.Equal([IPAddress.Parse("203.0.113.7")], rig.Connected.Select(e => e.Address));
        Assert.Equal("rebind.test:" + rig.Server.Port, Assert.Single(rig.Server.Requests).Header("host"));
    }

    [Theory]
    [InlineData("metadata.test", "169.254.169.254", true, "169.254.169.254 is a link-local address.")]
    [InlineData("mapped.test", "::ffff:169.254.169.254", true, "169.254.169.254 is a link-local address.")]
    [InlineData("nas.test", "192.168.1.20", false, "192.168.1.20 is on your local network.")]
    [InlineData("localhost", "127.0.0.1", false, "127.0.0.1 is on this computer.")]
    [InlineData("nat64.test", "64:ff9b::7f00:1", false, "127.0.0.1 is on this computer.")]
    [InlineData("local-nat64.test", "64:ff9b:1:fffe::c0a8:114", false, "192.168.1.20 is on your local network.")]
    [InlineData("alibaba.test", "100.100.100.200", true, "100.100.100.200 is a cloud metadata service.")]
    [InlineData("aws6.test", "fd00:ec2::254", true, "fd00:ec2::254 is a cloud metadata service.")]
    public async Task A_name_that_resolves_to_a_refused_address_is_refused_at_connect_time(string host, string address, bool localNetwork, string why)
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        rig.LocalNetwork = localNetwork;
        rig.Names = h => h == host ? [IPAddress.Parse(address)] : null;
        var refused = await rig.Fetch($"http://{host}:{rig.Server.Port}/x.png");
        Assert.Null(refused.Sample);
        Assert.Equal(new MediaStatus("media.address", why), refused.Status);
        lock (rig.Connected) Assert.Empty(rig.Connected);
        Assert.Empty(rig.Server.Requests);
    }

    [Fact]
    public async Task This_computer_is_allowed_while_the_local_network_setting_is_on()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        rig.Names = h => h == "localhost" ? [IPAddress.IPv6Loopback, IPAddress.Loopback] : null;
        var shown = await rig.Fetch($"http://localhost:{rig.Server.Port}/x.png");
        Assert.NotNull(shown.Sample);
        Rig.Release(shown);
    }

    [Fact]
    public async Task A_name_with_one_refused_and_one_allowed_address_connects_to_the_allowed_one()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        rig.Names = h => h == "mixed.test" ? [IPAddress.Parse("169.254.169.254"), IPAddress.Parse("198.51.100.4")] : null;
        var shown = await rig.Fetch($"http://mixed.test:{rig.Server.Port}/x.png");
        Assert.NotNull(shown.Sample);
        Rig.Release(shown);
        lock (rig.Connected) Assert.Equal([IPAddress.Parse("198.51.100.4")], rig.Connected.Select(e => e.Address));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("camera.localhost")]
    [InlineData("CAMERA.LOCALHOST")]
    public async Task A_name_under_localhost_is_this_computer_without_a_lookup(string host)
    {
        // Offline, where a lookup would reach the network, and online alike: the name is this computer's.
        foreach (var offline in new[] { true, false })
        {
            await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red), loopbackOnly: offline);
            var shown = await rig.Fetch($"http://{host}:{rig.Server.Port}/x.png");
            Assert.NotNull(shown.Sample);
            Rig.Release(shown);
            lock (rig.Resolved) Assert.Empty(rig.Resolved);
            lock (rig.Connected) Assert.All(rig.Connected, e => Assert.True(IPAddress.IsLoopback(e.Address)));
        }
    }

    [Theory]
    // Forms that carry 127.0.0.1 but go to a translator or tunnel: offline, the connection itself must stay here.
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b:1::7f00:1")]
    [InlineData("::127.0.0.1")]
    [InlineData("2002:7f00:1::1")]
    public async Task Offline_an_address_that_only_carries_this_computers_is_refused(string address)
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red), loopbackOnly: true);
        var refused = await rig.Fetch($"http://[{address}]:{rig.Server.Port}/x.png");
        Assert.Null(refused.Sample);
        Assert.Equal(new MediaStatus("media.address", $"{address} is outside this computer, and the engine runs offline."), refused.Status);
        lock (rig.Connected) Assert.Empty(rig.Connected);
        Assert.Empty(rig.Server.Requests);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("[::ffff:127.0.0.1]")]
    public async Task Offline_this_computers_own_addresses_are_allowed(string host)
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red), loopbackOnly: true);
        var shown = await rig.Fetch($"http://{host}:{rig.Server.Port}/x.png");
        Assert.NotNull(shown.Sample);
        Rig.Release(shown);
    }

    [Fact]
    public async Task Turning_the_local_network_off_drops_what_was_fetched()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        var url = rig.Url("/radar.png");
        Rig.Release(await rig.Fetch(url));
        rig.LocalNetwork = false;
        rig.Provider.Reset();
        var blocked = await rig.Fetch(url);
        Assert.Null(blocked.Sample);
        Assert.Equal(new MediaStatus("media.address", "127.0.0.1 is on this computer."), blocked.Status);
        rig.LocalNetwork = true;
        rig.Provider.Reset();
        var back = await rig.Fetch(url);
        Assert.NotNull(back.Sample);
        Rig.Release(back);
    }
}
