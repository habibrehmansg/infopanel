using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using InfoPanel.Core.Tests.Scenes;
using InfoPanel.Core.Tests.Sensors;
using InfoPanel.Images;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using InfoPanel.Sensors.NowPlaying;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Images;

/// <summary>
/// The <c>now-playing</c> media provider (spec/docs/providers.md): a token the now-playing source keeps is answered its
/// cover, decoded from memory under the media limits (never read from the stored assets); any other reference nothing,
/// with <c>media.provider</c>; a token no tick asks for dropped 30 s after the last ask, a one-off ask keeping nothing
/// alive; a cover given as a web address asked of the web image provider; and, drawn by the scene runtime, the example
/// scene bound to the <c>art</c> reading shows the cover and holds the old one across a track change until the new one
/// is decoded. Under made-up readings the reference is the reading's label, which names no cover.
/// </summary>
public sealed class NowPlayingProviderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly byte[] Red = HttpMediaProviderTests.Png(SKColors.Red);
    private static readonly byte[] Blue = HttpMediaProviderTests.Png(SKColors.Blue, 6, 5);

    private readonly TimerTimeProvider _time = new(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
    private readonly FetchedMediaStore _store = new();
    private readonly CountingStore _media;
    private readonly NowPlayingArt _art = new();
    private readonly List<IDisposable> _owned = [];

    public NowPlayingProviderTests() => _media = new CountingStore(_store);

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }

    /// <summary>The media cache's store: the fetched media, every read counted (and one key's reads held while <see cref="Gate"/> is closed).</summary>
    private sealed class CountingStore(FetchedMediaStore fetched) : IMediaAssetStore
    {
        public int AssetReads;
        public string? Gated;
        public readonly ManualResetEventSlim Gate = new(true);

        public byte[]? Read(string hash)
        {
            // Anything but the fetched media's keys would be a stored asset: the provider never reaches one.
            if (!hash.StartsWith(FetchedMediaStore.Prefix, StringComparison.Ordinal)) Interlocked.Increment(ref AssetReads);
            if (hash == Gated) Gate.Wait(Wait);
            return fetched.Read(hash);
        }
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private MediaSampleCache Cache(bool synchronous = true, SpecLimits? limits = null, List<Serilog.Events.LogEvent>? log = null) =>
        Own(new MediaSampleCache(_media, new MediaSampleCacheOptions
        {
            Synchronous = synchronous,
            Workers = 1,
            Limits = limits ?? SpecLimits.Default,
            Log = log is null ? null : new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(log)).CreateLogger(),
        }));

    private NowPlayingProvider Provider(MediaSampleCache cache, NowPlayingArt? art = null, IMediaProvider? web = null) =>
        Own(new NowPlayingProvider(art ?? _art, cache, _store, web, _time));

    private string Publish(byte[]? art = null, string? address = null) =>
        _art.Publish(new NowPlayingSnapshot { State = NowPlayingState.Playing, Art = art, ArtAddress = address })!;

    private static ProvidedMedia Ask(IMediaProvider provider, string reference, bool live = true) =>
        provider.Get(new MediaRequest(reference, 0, MediaPlayback.Loop, 0, live));

    /// <summary>A cover's key in the store and the cache: the provider's own, never another provider's.</summary>
    private static string KeyOf(byte[] bytes) => NowPlayingProvider.KeyOf(Convert.ToHexStringLower(SHA256.HashData(bytes)));

    private static readonly MediaStatus NoArt = new("media.provider", "No cover art.");

    /// <summary>What every cover's failure says, whatever its code.</summary>
    private const string CoverFailed = "Couldn't load the cover art.";

    /// <summary>A task done when <paramref name="cache"/> has decoded a frame of <paramref name="key"/>.</summary>
    private static Task Decoded(MediaSampleCache cache, string key)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.FrameDecoded += hash =>
        {
            if (hash == key) done.TrySetResult();
        };
        return done.Task;
    }

    /// <summary>A PNG of one colour, distinct for each <paramref name="i"/>.</summary>
    private static byte[] Cover(int i) => HttpMediaProviderTests.Png(new SKColor((byte)(40 * i), 10, 200), 4 + i, 3);

    // ---- Answers ----

    [Fact]
    public void The_current_token_is_answered_its_cover_decoded_from_memory_never_from_the_stored_assets()
    {
        var provider = Provider(Cache());
        var token = Publish(Red);

        var answer = Ask(provider, token);
        Assert.Null(answer.Status);
        using (var image = SKBitmap.FromImage(Assert.IsType<MediaSample>(answer.Sample).Image!))
            Assert.Equal(SKColors.Red, image.GetPixel(1, 1));
        Assert.Equal((4f, 3f), (answer.Sample!.Width, answer.Sample.Height));
        answer.Sample.Release();

        // In memory, under the fetched media's key; no stored asset is read.
        Assert.True(_store.Contains(KeyOf(Red)));
        Assert.Equal(0, _media.AssetReads);
        Assert.Equal(1, provider.Held);
    }

    [Fact]
    public void Each_kept_cover_answers_its_own_picture_the_one_before_too()
    {
        var provider = Provider(Cache());
        var red = Publish(Red);
        var blue = Publish(Blue);

        var old = Ask(provider, red, live: false);
        var current = Ask(provider, blue);
        Assert.Equal((4f, 3f), (old.Sample!.Width, old.Sample.Height));
        Assert.Equal((6f, 5f), (current.Sample!.Width, current.Sample.Height));
        old.Sample.Release();
        current.Sample.Release();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Cover Art")]
    [InlineData("0123456789abcdef")]
    [InlineData("https://covers.example/a.jpg")]
    public void Any_other_reference_gets_nothing_and_media_provider(string reference)
    {
        var provider = Provider(Cache());
        var token = Publish(Red);

        Assert.Equal(new ProvidedMedia(null, NoArt), Ask(provider, reference));
        // A token's own case only, whole.
        Assert.Equal(new ProvidedMedia(null, NoArt), Ask(provider, token.ToUpperInvariant()));
        Assert.Equal(new ProvidedMedia(null, NoArt), Ask(provider, token + " "));
        Assert.Equal(0, provider.Held);
        Assert.Equal(0, _store.Bytes);
    }

    [Fact]
    public void An_engine_without_the_now_playing_source_answers_no_cover()
    {
        var token = Publish(Red);
        using var provider = new NowPlayingProvider(null, Cache(), _store, null, _time);

        Assert.Equal(new ProvidedMedia(null, NoArt), Ask(provider, token));
    }

    [Fact]
    public void A_cover_over_the_media_limits_is_refused_with_its_code_and_one_that_isnt_an_image_too()
    {
        var provider = Provider(Cache(limits: SpecLimits.Default with { MediaSide = 2 }));

        var over = Ask(provider, Publish(Red));
        Assert.Null(over.Sample);
        Assert.Equal("limit.mediaSide", over.Status?.Code);

        var junk = Ask(provider, Publish([1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.Null(junk.Sample);
        Assert.Contains(junk.Status?.Code, new[] { "media.format", "media.malformed" });
    }

    // ---- Dropped when unasked ----

    [Fact]
    public void A_token_no_tick_asks_for_is_dropped_30_seconds_after_the_last_ask_a_one_off_ask_keeping_nothing_alive()
    {
        var provider = Provider(Cache());
        var token = Publish(Red);
        Ask(provider, token).Sample!.Release();

        // A one-off ask (a thumbnail, a held picture) is answered, but keeps it no longer.
        _time.Advance(TimeSpan.FromSeconds(20));
        var once = Ask(provider, token, live: false);
        Assert.NotNull(once.Sample);
        once.Sample!.Release();
        _time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(1, provider.Held);
        Assert.True(_store.Contains(KeyOf(Red)));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, provider.Held);
        Assert.False(_store.Contains(KeyOf(Red)));

        // Asked again: its bytes are put back and decoded afresh.
        var again = Ask(provider, token);
        Assert.NotNull(again.Sample);
        again.Sample!.Release();
        Assert.True(_store.Contains(KeyOf(Red)));

        // A one-off ask of a token it hasn't held yet is answered too, and kept no longer than 30 s.
        var blue = Publish(Blue);
        var first = Ask(provider, blue, live: false);
        Assert.NotNull(first.Sample);
        first.Sample!.Release();
        Assert.Equal(2, provider.Held);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, provider.Held);
        Assert.Equal(0, _store.Bytes);
    }

    [Fact]
    public void Bytes_the_store_let_go_are_put_back_at_the_next_ask()
    {
        var provider = Provider(Cache());
        var token = Publish(Red);
        Ask(provider, token).Sample!.Release();

        _store.Remove(KeyOf(Red));
        var answer = Ask(provider, token);
        Assert.NotNull(answer.Sample);
        answer.Sample!.Release();
        Assert.True(_store.Contains(KeyOf(Red)));
    }

    [Fact]
    public void Disposed_it_lets_go_of_every_cover_and_answers_nothing()
    {
        var provider = Provider(Cache());
        var token = Publish(Red);
        Ask(provider, token).Sample!.Release();

        provider.Dispose();
        Assert.False(_store.Contains(KeyOf(Red)));
        Assert.Equal(0, provider.Held);
        Assert.Equal("media.provider", Ask(provider, token).Status?.Code);
    }

    // ---- A cover given as a web address ----

    /// <summary>A web image provider that records what it is asked and answers a status.</summary>
    private sealed class FakeWeb : IMediaProvider
    {
        public readonly List<(MediaRequest Request, bool WithAsk)> Asks = [];
        public readonly List<MediaRequest> Told = [];

        public ProvidedMedia Get(in MediaRequest request)
        {
            Asks.Add((request, false));
            return new ProvidedMedia(null, new MediaStatus("media.loading", "From the web."));
        }

        public ProvidedMedia Get(in MediaRequest request, in MediaAsk ask)
        {
            Asks.Add((request, true));
            return new ProvidedMedia(null, new MediaStatus("media.loading", "From the web."));
        }

        public void Asked(in MediaRequest request) => Told.Add(request);
    }

    [Fact]
    public void A_cover_given_as_a_web_address_is_asked_of_the_web_image_provider_for_that_address_under_its_rules()
    {
        const string Address = "https://covers.example/a.jpg";
        var web = new FakeWeb();
        var provider = Provider(Cache(), web: web);
        var token = Publish(address: Address);

        var request = new MediaRequest(token, 1.5, MediaPlayback.Still, 0, Live: true, PublicOnly: true);
        // Its status is the web provider's code with the provider's own text.
        Assert.Equal(new MediaStatus("media.loading", "Loading the cover art."), provider.Get(request).Status);
        Assert.Equal(new MediaStatus("media.loading", "Loading the cover art."), provider.Get(request, new MediaAsk(new MediaTick(), new object())).Status);
        provider.Asked(request);

        // Asked as a private address: its fetch logs neither it nor a message that could name it.
        var forwarded = request with { Reference = Address, Private = true };
        Assert.Equal([(forwarded, false), (forwarded, true)], web.Asks);
        Assert.Equal([forwarded], web.Told);
        Assert.Equal(0, provider.Held);

        // Without a web image provider, no cover.
        var alone = Provider(Cache());
        Assert.Equal(new ProvidedMedia(null, NoArt), Ask(alone, token));
    }

    [Fact]
    public void A_cover_given_as_a_web_address_is_asked_with_no_refresh_whatever_the_source_says()
    {
        const string Address = "https://covers.example/a.jpg";
        var web = new FakeWeb();
        var provider = Provider(Cache(), web: web);
        var token = Publish(address: Address);

        var request = new MediaRequest(token, 0, MediaPlayback.Loop, Refresh: 7);
        provider.Get(request);
        provider.Get(request, new MediaAsk(new MediaTick(), new object()));
        provider.Asked(request);

        Assert.All(web.Asks, a => Assert.Equal(0, a.Request.Refresh));
        Assert.Equal(0, Assert.Single(web.Told).Refresh);
        Assert.Equal(2, web.Asks.Count);
    }

    /// <summary>A clock whose one-shot timers (a fetch's time limit) can't be made: a fault inside a fetch.</summary>
    private sealed class FaultyTimers(TimerTimeProvider time, string fault) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();

        public override TimeZoneInfo LocalTimeZone => time.LocalTimeZone;

        public override long TimestampFrequency => time.TimestampFrequency;

        public override long GetTimestamp() => time.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            period == Timeout.InfiniteTimeSpan ? throw new InvalidOperationException(fault) : time.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class CollectingSink(List<Serilog.Events.LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            lock (events) events.Add(logEvent);
        }
    }

    /// <summary>The web image provider an offline engine has, its log collected; a name it is asked to look up isn't found.</summary>
    private HttpMediaProvider Web(MediaSampleCache cache, List<Serilog.Events.LogEvent> log, TimeProvider? time = null) =>
        Own(new HttpMediaProvider(cache, _store, new HttpMediaProviderOptions
        {
            Time = time ?? _time,
            LoopbackOnly = true,
            Resolve = (_, _) => throw new SocketException((int)SocketError.HostNotFound),
            Log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(log)).CreateLogger(),
        }));

    private static string Logged(List<Serilog.Events.LogEvent> log)
    {
        lock (log) return string.Join("\n", log.Select(e => e.RenderMessage() + " " + e.Exception));
    }

    [Fact]
    public async Task A_cover_at_a_web_address_that_cant_be_had_is_answered_its_code_and_a_fixed_text_naming_no_host()
    {
        const string Address = "https://private-album.example/art.jpg";
        List<Serilog.Events.LogEvent> log = [];
        var cache = Cache();
        var web = Web(cache, log);
        var provider = Provider(cache, web: web);
        var token = Publish(address: Address);

        // Offline, the cover's host is refused: the web provider's own status names it, the cover's never.
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        web.Answered += _ => answered.TrySetResult();
        Ask(provider, token);
        await answered.Task.WaitAsync(Wait, Ct);
        var status = Ask(provider, token).Status;
        Assert.NotNull(status);
        Assert.StartsWith("media.", status.Code);
        Assert.NotEqual("media.loading", status.Code);
        Assert.Equal(CoverFailed, status.Message);
        Assert.DoesNotContain("private-album", Logged(log));
    }

    [Fact]
    public async Task A_cover_fetch_that_fails_unexpectedly_logs_only_the_exceptions_type_and_code_never_the_address()
    {
        const string Address = "https://private-album.example/art.jpg";
        List<Serilog.Events.LogEvent> log = [];
        var cache = Cache();
        var web = Web(cache, log, new FaultyTimers(_time, "No timer for " + Address));
        var provider = Provider(cache, web: web);
        var token = Publish(address: Address);

        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        web.Answered += _ => answered.TrySetResult();
        Ask(provider, token);
        await answered.Task.WaitAsync(Wait, Ct);

        var logged = Logged(log);
        Assert.DoesNotContain("private-album", logged);
        lock (log) Assert.All(log, e => Assert.Null(e.Exception));
        Assert.Contains(nameof(InvalidOperationException), logged);
        Assert.Contains("media.provider", logged);
        Assert.Equal(new MediaStatus("media.provider", CoverFailed), Ask(provider, token).Status);
    }

    [Fact]
    public void A_rejected_covers_status_is_its_code_and_a_fixed_text_never_the_covers_own_text()
    {
        var provider = Provider(Cache());
        var svg = """<svg xmlns="http://www.w3.org/2000/svg" width="4" height="4"><rect id="PrivateSong" width="2" height="2"/><rect id="PrivateSong" x="2" width="2" height="2"/></svg>"""u8.ToArray();

        var answer = Ask(provider, Publish(svg));
        Assert.Null(answer.Sample);
        Assert.NotNull(answer.Status);
        Assert.StartsWith("media.", answer.Status.Code);
        Assert.Equal(CoverFailed, answer.Status.Message);

        // A cover over the media limits too: its code, the same text.
        var limited = Provider(Cache(limits: SpecLimits.Default with { MediaSide = 2 }));
        Assert.Equal(new MediaStatus("limit.mediaSide", CoverFailed), Ask(limited, Publish(Red)).Status);
    }

    [Fact]
    public void A_covers_decode_that_fails_unexpectedly_logs_only_the_exceptions_type_and_code_never_its_key_or_message()
    {
        List<Serilog.Events.LogEvent> log = [];
        var cache = Cache(log: log);
        cache.DecodingSvg = (_, _) => throw new InvalidOperationException("Couldn't draw PrivateSong");
        var provider = Provider(cache);
        var svg = """<svg xmlns="http://www.w3.org/2000/svg" width="4" height="4"><rect width="2" height="2"/></svg>"""u8.ToArray();
        var token = Publish(svg);

        Assert.Equal(new ProvidedMedia(null, new MediaStatus("media.malformed", CoverFailed)), Ask(provider, token));

        // The cover's key holds its token: neither it nor the exception's message is logged.
        var logged = Logged(log);
        Assert.Contains(nameof(InvalidOperationException), logged);
        Assert.Contains("media.malformed", logged);
        Assert.DoesNotContain("PrivateSong", logged);
        Assert.DoesNotContain(token, logged);
        Assert.DoesNotContain(NowPlayingProvider.KeyPrefix, logged);
        lock (log) Assert.All(log, e => Assert.Null(e.Exception));
    }

    [Fact]
    public async Task An_ordinary_item_and_a_cover_at_the_same_address_fetch_apart_so_the_items_failures_keep_their_diagnostics()
    {
        const string Address = "https://shared-album.example/art.jpg";
        List<Serilog.Events.LogEvent> log = [];
        var cache = Cache();
        var web = Web(cache, log, new FaultyTimers(_time, "No timer for " + Address));
        var provider = Provider(cache, web: web);
        var token = Publish(address: Address);

        var answers = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        web.Answered += _ =>
        {
            if (Interlocked.Increment(ref answers) == 2) both.TrySetResult();
        };
        // An ordinary item shows the address, and the cover of what plays is the same address.
        web.Get(new MediaRequest(Address, 0, MediaPlayback.Loop));
        Ask(provider, token);
        Assert.Equal(2, web.FetcherCount);
        await both.Task.WaitAsync(Wait, Ct);

        // The item's failure is logged with its exception; the cover's with the exception's type and code only.
        lock (log)
        {
            Assert.Single(log, e => e.Exception is InvalidOperationException);
            var cover = Assert.Single(log, e => e.Exception is null);
            Assert.DoesNotContain("shared-album", cover.RenderMessage());
            Assert.Contains(nameof(InvalidOperationException), cover.RenderMessage());
        }
    }

    // ---- Who owns a cover's bytes ----

    [Fact]
    public void At_most_the_four_covers_the_source_keeps_are_in_the_store_and_clearing_the_source_empties_the_store_and_the_cache()
    {
        var cache = Cache();
        var provider = Provider(cache);
        var covers = Enumerable.Range(1, 5).Select(Cover).ToArray();
        foreach (var cover in covers)
        {
            var answer = Ask(provider, Publish(cover));
            Assert.NotNull(answer.Sample);
            answer.Sample!.Release();
        }

        // The first cover left the source: its bytes and frames are gone with it.
        Assert.Equal(NowPlayingArt.MaxKept, provider.Held);
        Assert.False(_store.Contains(KeyOf(covers[0])));
        Assert.Equal(covers.Skip(1).Sum(c => (long)c.Length), _store.Bytes);
        Assert.Equal(NowPlayingArt.MaxKept, cache.AssetCount);

        // The source stops reading: nothing of its covers is left.
        _art.Clear();
        Assert.Equal(0, provider.Held);
        Assert.Equal(0, _store.Bytes);
        Assert.Equal(0, cache.AssetCount);
    }

    [Fact]
    public void A_cover_is_kept_under_a_key_of_its_own_so_expiring_it_leaves_another_providers_identical_image()
    {
        var cache = Cache(synchronous: false);
        var provider = Provider(cache);

        // The web image provider shows the same bytes, under the fetched media's key.
        var webKey = _store.Put(Red.ToArray());
        var webDecoded = Decoded(cache, webKey);
        cache.Get(webKey, 0, MediaPlayback.Loop).Sample?.Release();
        webDecoded.Wait(Wait, Ct);

        var token = Publish(Red);
        var coverDecoded = Decoded(cache, KeyOf(Red));
        var first = Ask(provider, token);
        if (first.Sample is { } decoded) decoded.Release();
        else coverDecoded.Wait(Wait, Ct);
        var shown = Ask(provider, token);
        Assert.NotNull(shown.Sample);
        shown.Sample!.Release();

        _time.Advance(NowPlayingProvider.IdleAfter);
        Assert.Equal(0, provider.Held);
        Assert.True(_store.Contains(webKey));
        var web = cache.Get(webKey, 0, MediaPlayback.Loop);
        Assert.NotNull(web.Sample);
        web.Sample!.Release();
    }

    [Fact]
    public void An_expiry_paused_before_it_forgets_never_undoes_a_tick_that_asked_again_meanwhile()
    {
        var cache = Cache(synchronous: false);
        var provider = Provider(cache);
        var token = Publish(Red);
        var decoded = Decoded(cache, KeyOf(Red));
        Ask(provider, token).Sample?.Release();
        decoded.Wait(Wait, Ct);

        // The expiry has let go of the cover's bytes and not yet forgotten its frames when a tick asks again, from a
        // thread of its own: the expiry goes on once that ask is answered, or once it waits (it can't get past the expiry).
        var again = Decoded(cache, KeyOf(Red));
        var asking = false;
        Thread? tick = null;
        provider.Admitting = () => Volatile.Write(ref asking, true);
        provider.Expired = () =>
        {
            provider.Expired = null;
            tick = new Thread(() => Ask(provider, token).Sample?.Release());
            tick.Start();
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (tick.IsAlive && waited.Elapsed < Wait
                   && !(Volatile.Read(ref asking) && (tick.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0))
                Thread.Yield();
        };
        _time.Advance(NowPlayingProvider.IdleAfter);
        Assert.NotNull(tick);
        Assert.True(tick.Join(Wait));
        provider.Admitting = null;
        again.Wait(Wait, Ct);

        // The next tick draws it: nothing the expiry did after the renewal undid it.
        var next = Ask(provider, token);
        Assert.Null(next.Status);
        Assert.NotNull(next.Sample);
        next.Sample!.Release();
        Assert.Equal(1, provider.Held);
    }

    [Fact]
    public void An_ask_paused_past_its_first_check_while_the_provider_is_disposed_leaves_nothing_behind()
    {
        var cache = Cache();
        var provider = Provider(cache);
        var token = Publish(Red);

        provider.Admitting = () => provider.Dispose();
        var answer = Ask(provider, token);

        Assert.Null(answer.Sample);
        Assert.Equal("media.provider", answer.Status?.Code);
        Assert.Equal(0, provider.Held);
        Assert.Equal(0, _store.Bytes);
        Assert.Equal(0, cache.AssetCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_ask_paused_just_after_admitting_while_the_provider_is_disposed_or_the_source_cleared_leaves_nothing_behind(bool dispose)
    {
        var cache = Cache();
        var provider = Provider(cache);
        var token = Publish(Red);

        provider.Admitted = () =>
        {
            if (dispose) provider.Dispose();
            else _art.Clear();
        };
        var answer = Ask(provider, token);

        Assert.Null(answer.Sample);
        Assert.Equal(dispose ? new MediaStatus("media.provider", "The provider is shut down.") : NoArt, answer.Status);
        Assert.Equal(0, provider.Held);
        Assert.Equal(0, _store.Bytes);
        Assert.Equal(0, cache.AssetCount);
    }

    [Fact]
    public void An_ask_paused_before_admitting_holds_none_of_the_covers_bytes_while_a_fifth_pushes_it_out()
    {
        var provider = Provider(Cache());
        var tokens = Enumerable.Range(0, NowPlayingArt.MaxKept).Select(i => PublishLarge(_art, i).Token!).ToArray();
        var oldest = KeptBytes(_art, tokens[0]);

        // An ask for the oldest cover pauses before admitting it; meanwhile a fifth cover pushes the oldest out.
        bool? oldestHeld = null;
        provider.Admitting = () =>
        {
            provider.Admitting = null;
            Assert.NotNull(PublishLarge(_art, NowPlayingArt.MaxKept).Token);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            oldestHeld = oldest.IsAlive;
        };
        var answer = Ask(provider, tokens[0]);

        // The paused ask held none of the oldest's bytes: the four covers kept are all the bytes there are.
        Assert.False(oldestHeld);
        Assert.Equal(NowPlayingArt.MaxKept * (long)NowPlayingSource.MaxArtBytes, _art.Bytes);
        Assert.Equal(new ProvidedMedia(null, NoArt), answer);
        Assert.Equal(0, provider.Held);
        Assert.Equal(0, _store.Bytes);
    }

    /// <summary>The bytes the source keeps for <paramref name="token"/>, held by nothing else here.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference KeptBytes(NowPlayingArt art, string token)
    {
        Assert.True(art.TryGet(token, out var picture));
        return new WeakReference(picture.Bytes);
    }

    [Fact]
    public void A_fifth_cover_never_takes_the_bytes_kept_past_32_MiB_and_no_original_is_kept_beside_the_copies()
    {
        long most = 0;
        _art.Copying = () => most = Math.Max(most, _art.Bytes + NowPlayingSource.MaxArtBytes);
        for (var i = 0; i < NowPlayingArt.MaxKept; i++)
            Assert.NotNull(PublishLarge(_art, i).Token);
        var fifth = PublishLarge(_art, NowPlayingArt.MaxKept);
        Assert.NotNull(fifth.Token);

        // Before the fifth is copied, the oldest is gone: the copies never pass four covers' bytes.
        Assert.Equal(NowPlayingArt.MaxKept * (long)NowPlayingSource.MaxArtBytes, _art.Bytes);
        Assert.True(most <= NowPlayingArt.MaxKept * (long)NowPlayingSource.MaxArtBytes, $"{most} bytes at once");

        // And the reader's own buffer isn't kept beside its copy.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(fifth.Original.IsAlive);
    }

    /// <summary>Publishes a cover of the largest size, its bytes all <paramref name="fill"/>, held by nothing else here.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string? Token, WeakReference Original) PublishLarge(NowPlayingArt art, int fill)
    {
        var bytes = new byte[NowPlayingSource.MaxArtBytes];
        Array.Fill(bytes, (byte)fill);
        var token = art.Publish(new NowPlayingSnapshot { State = NowPlayingState.Playing, Art = bytes });
        return (token, new WeakReference(bytes));
    }

    // ---- In a scene ----

    /// <summary>A reader that plays what the test sets.</summary>
    private sealed class FakeReader : INowPlayingReader
    {
        public NowPlayingSnapshot? Snapshot;
        private bool _running;

        public SourceState? State => null;

        public void Start() => _running = true;

        public void Stop() => _running = false;

        public bool TryRead(out NowPlayingSnapshot? snapshot)
        {
            snapshot = Snapshot;
            return _running;
        }

        public void Dispose()
        {
        }
    }

    private static NowPlayingSnapshot Song(byte[] art) => new() { Title = "A Secret Song", State = NowPlayingState.Playing, Art = art };

    [Fact]
    public void The_example_scene_bound_to_the_art_reading_draws_the_cover_and_holds_it_across_a_track_change_until_the_next_is_decoded()
    {
        var reader = new FakeReader { Snapshot = Song(Red) };
        var source = Own(NowPlayingSource.Create(reader, _time));
        source.Open();
        var inputs = new SensorSceneInputs(new SensorHub([source]), new FakeSensorHistory());
        var cache = Cache(synchronous: false);
        var provider = Provider(cache, source.Art);
        using var runtime = new SceneRuntime(SceneKit.Options(inputs, cache, new Dictionary<string, IMediaProvider> { [NowPlayingProvider.Name] = provider }));
        runtime.Load(SceneKit.Doc(SceneKit.Example("scene/valid/provider-media.json"), 116, 116, 10, 10,
            bindings: new Dictionary<string, string> { ["art"] = NowPlayingSource.KeyOf("art").ToString() }));

        (SKColor Pixel, ItemMedia? Media) Tick(double elapsed)
        {
            using var s = runtime.Tick(elapsed, SceneKit.Now);
            using var frame = s.Render();
            return (frame.GetPixel(58, 58), s.Bounds.Single().Media);
        }

        Task Decoded(byte[] bytes)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cache.FrameDecoded += hash =>
            {
                if (hash == KeyOf(bytes)) done.TrySetResult();
            };
            return done.Task;
        }

        // A read puts the source in use; the next poll reads the cover's token. The first tick asks for it: decoding.
        Assert.False(source.TryRead(NowPlayingSource.KeyOf("art"), out _));
        _time.Advance(NowPlayingSource.Interval);
        source.Poll();
        var red = Decoded(Red);
        Assert.Equal("media.loading", Tick(0).Media?.Code);
        red.Wait(Wait, Ct);
        Assert.Equal((SKColors.Red, (ItemMedia?)null), Tick(0.2));

        // The next track: its cover is held from decoding, and the one before stays drawn meanwhile, the item loading.
        _media.Gated = KeyOf(Blue);
        _media.Gate.Reset();
        reader.Snapshot = Song(Blue);
        _time.Advance(NowPlayingSource.Interval);
        source.Poll();
        var blue = Decoded(Blue);
        var (pixel, media) = Tick(1.5);
        Assert.Equal(SKColors.Red, pixel);
        Assert.Equal("media.loading", media?.Code);

        // Decoded: the new cover is drawn.
        _media.Gate.Set();
        blue.Wait(Wait, Ct);
        Assert.Equal((SKColors.Blue, (ItemMedia?)null), Tick(1.6));
    }

    [Fact]
    public void Under_made_up_readings_the_art_input_reads_its_label_which_names_no_cover()
    {
        var reader = new FakeReader { Snapshot = Song(Red) };
        var source = Own(NowPlayingSource.Create(reader, _time));
        source.Open();
        var key = NowPlayingSource.KeyOf("art").ToString();
        var live = new SensorSceneInputs(new SensorHub([source]), new FakeSensorHistory());
        live.Read(key, "text");
        _time.Advance(NowPlayingSource.Interval);
        source.Poll();
        var token = live.Read(key, "text").Text;
        Assert.Equal(16, token.Length);

        var simulated = new SimulatedSceneInputs(live, _time, catalogue: source.GetSensors);
        var label = simulated.Read(key, "text").Text;
        Assert.Equal("Cover Art", label);
        var provider = Provider(Cache(), source.Art);
        Assert.Equal(new ProvidedMedia(null, NoArt), Ask(provider, label));
        var cover = Ask(provider, token);
        Assert.NotNull(cover.Sample);
        cover.Sample!.Release();
    }
}
