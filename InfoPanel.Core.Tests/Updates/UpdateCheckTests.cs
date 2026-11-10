using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Images;
using InfoPanel.Core.Tests.Sensors;
using InfoPanel.Updates;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Updates;

/// <summary>
/// The engine's update check (<see cref="UpdateCheck"/>) against a fake feed (a <see cref="HttpMessageHandler"/> that
/// answers in memory, or a <see cref="LocalHttpServer"/> on loopback for what goes on the wire) with a fake clock: one
/// GET with <c>User-Agent: InfoPanel/{version}</c> and GitHub's two headers only; 30 s after start, then 24 h of
/// wall-clock time after an answer, looked at hourly; an hour after a failure; GitHub's reset when it limits; a 304 keeps
/// the answer; nothing for a development build, nor off this computer under <c>--offline</c>; no redirect followed; a
/// list over 1 MiB refused.
/// </summary>
public sealed class UpdateCheckTests : IAsyncDisposable
{
    private const string Beta0 = "2.0.0-beta.0";
    private const string Page = "https://github.com/habibrehmansg/infopanel/releases/tag/";
    private const string Loopback = "http://127.0.0.1:9/releases.json";
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TimerTimeProvider _timers = new(Start);
    private readonly WallClock _clock;
    private readonly FakeFeed _feed = new();
    private readonly List<LogEvent> _events = [];
    private readonly Serilog.ILogger _log;
    private readonly List<UpdateCheck> _checks = [];

    public UpdateCheckTests()
    {
        _clock = new WallClock(_timers);
        _log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
        _feed.Answer = _ => Json(200, Releases(("2.0.0-beta.1", false), ("1.4.0-preview.4", false), ("1.3.1", false), ("2.0.0-beta.9", true)));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var check in _checks)
        {
            await check.StopAsync(Ct);
            check.Dispose();
        }
    }

    private UpdateCheck Check(string version = Beta0, string? feed = null, bool offline = false, HttpMessageHandler? handler = null)
    {
        var check = new UpdateCheck(version, feed, offline, _clock, _log, handler ?? _feed);
        _checks.Add(check);
        return check;
    }

    /// <summary>GitHub's list: each release's tag and draft flag, a pre-release when its tag has <c>-</c>.</summary>
    private static byte[] Releases(params (string Tag, bool Draft)[] releases) => JsonSerializer.SerializeToUtf8Bytes(
        releases.Select(r => new { tag_name = r.Tag, draft = r.Draft, prerelease = r.Tag.Contains('-'), html_url = "https://example.test/elsewhere" }));

    private static HttpResponseMessage Json(int status, byte[] body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(body) };
        foreach (var (name, value) in headers)
            response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    private static HttpResponseMessage Status(int status, params (string Name, string Value)[] headers) => Json(status, [], headers);

    // The loop's progress, read through its test hooks: parked (waiting for its time), and how many times it has parked.

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The update check didn't get there.");
            await Task.Delay(5, Ct);
        }
    }

    /// <summary>Lets <paramref name="span"/> of the timers' time pass, the loop running each wait it comes to in turn.</summary>
    private async Task PassAsync(UpdateCheck check, TimeSpan span)
    {
        var left = span;
        while (true)
        {
            await Until(() => check.Remaining is not null);
            var wait = check.Remaining!.Value;
            var parked = check.Parked;
            if (wait > left)
            {
                _timers.Advance(left);
                return;
            }

            _timers.Advance(wait);
            left -= wait;
            await Until(() => check.Parked > parked);
        }
    }

    private async Task<UpdateCheck> StartedAsync(UpdateCheck check)
    {
        check.Start();
        await Until(() => check.Remaining is not null);
        return check;
    }

    [Fact]
    public async Task TheFirstCheck_Is30SecondsAfterStart()
    {
        var check = await StartedAsync(Check());

        await PassAsync(check, TimeSpan.FromSeconds(29));
        Assert.Equal(0, _feed.Count);

        await PassAsync(check, TimeSpan.FromSeconds(1));
        Assert.Equal(1, _feed.Count);
    }

    [Fact]
    public async Task ANewerVersion_IsPublished_WithItsPageFromTheTag_ChangedRaised_AndLoggedOnce()
    {
        var check = await StartedAsync(Check());
        var changed = 0;
        var triggers = new List<UpdateTrigger>();
        check.Changed += trigger =>
        {
            Interlocked.Increment(ref changed);
            lock (triggers)
                triggers.Add(trigger);
        };

        await PassAsync(check, TimeSpan.FromSeconds(30));
        await PassAsync(check, TimeSpan.FromHours(25));

        Assert.Equal(2, _feed.Count);
        Assert.Equal(new UpdateAvailable("2.0.0-beta.1", Page + "2.0.0-beta.1", Start + TimeSpan.FromSeconds(30) + TimeSpan.FromHours(24)), check.Latest);
        Assert.Equal(1, changed);
        lock (triggers)
            Assert.Equal([UpdateTrigger.Background], triggers);
        Assert.Single(_events, e => e.RenderMessage() == $"InfoPanel \"2.0.0-beta.1\" is available: \"{Page}2.0.0-beta.1\"");
    }

    [Fact]
    public async Task UpToDate_PublishesNothing()
    {
        var check = await StartedAsync(Check("2.0.0-beta.1"));

        Assert.Equal(UpdateCheckResult.UpToDate, await check.CheckNowAsync(Ct));

        Assert.Null(check.Latest);
    }

    /// <summary>The User-Agent is exactly <c>InfoPanel/{version}</c>; nothing else is sent but GitHub's two headers.</summary>
    [Fact]
    public async Task TheRequest_CarriesTheVersion_AndGitHubsTwoHeaders_AndNothingElse()
    {
        var check = Check();

        await check.CheckNowAsync(Ct);

        var request = Assert.Single(_feed.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("https://api.github.com/repos/habibrehmansg/infopanel/releases?per_page=30"), request.Uri);
        Assert.Equal(
            [("Accept", "application/vnd.github+json"), ("User-Agent", "InfoPanel/2.0.0-beta.0"), ("X-GitHub-Api-Version", "2022-11-28")],
            request.Headers.OrderBy(h => h.Name, StringComparer.Ordinal));
    }

    /// <summary>
    /// On the wire, through the engine's own handler (a loopback feed): the same headers and only the ones HTTP itself
    /// needs, even with a trace under way (no <c>traceparent</c>), no cookie, no proxy header.
    /// </summary>
    [Fact]
    public async Task OnTheWire_OnlyTheHostAndTheThreeHeadersAreSent()
    {
        await using var server = new LocalHttpServer((_, stream, _) => LocalHttpServer.Answer(stream, 200, Releases(("2.0.0-beta.1", false)), "application/json"));
        var check = Check(feed: server.Url("/releases.json"), offline: true, handler: UpdateCheck.NewHandler(loopbackOnly: true));
        using var source = new ActivitySource("update-check-test");
        using var listener = new ActivityListener { ShouldListenTo = _ => true, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded };
        ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity("menu");
        Assert.NotNull(Activity.Current);

        Assert.Equal(UpdateCheckResult.Available, await check.CheckNowAsync(Ct));

        var served = Assert.Single(server.Requests);
        Assert.Equal("/releases.json", served.Path);
        Assert.Equal(["accept", "host", "user-agent", "x-github-api-version"], served.Headers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("InfoPanel/2.0.0-beta.0", served.Header("user-agent"));
    }

    [Fact]
    public async Task NoRedirect_IsFollowed()
    {
        await using var server = new LocalHttpServer((request, stream, _) => request.Path == "/releases.json"
            ? LocalHttpServer.Answer(stream, 302, null, null, "Location: /moved.json")
            : LocalHttpServer.Answer(stream, 200, Releases(("2.0.0-beta.1", false)), "application/json"));
        var check = Check(feed: server.Url("/releases.json"), offline: true, handler: UpdateCheck.NewHandler(loopbackOnly: true));

        Assert.Equal(UpdateCheckResult.Failed, await check.CheckNowAsync(Ct));

        Assert.Equal(["/releases.json"], server.Requests.Select(r => r.Path));
        Assert.Null(check.Latest);
    }

    [Fact]
    public void TheEnginesHandler_FollowsNoRedirect_SendsNoCookieOrCredential_AndPropagatesNoTrace()
    {
        using var handler = UpdateCheck.NewHandler(loopbackOnly: false);

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.False(handler.PreAuthenticate);
        Assert.Null(handler.ActivityHeadersPropagator);
        Assert.Equal(TimeSpan.FromSeconds(15), handler.ConnectTimeout);
        Assert.True(handler.UseProxy);
        using var offline = UpdateCheck.NewHandler(loopbackOnly: true);
        Assert.False(offline.UseProxy);
    }

    /// <summary>
    /// Under <c>--offline</c> an <c>https</c> feed on this computer reaches nothing else: its TLS downloads no issuer
    /// certificate and fetches no revocation data. Online, the platform's defaults.
    /// </summary>
    [Fact]
    public void TheOfflineHandler_DownloadsNoCertificate_AndChecksNoRevocation()
    {
        using var offline = UpdateCheck.NewHandler(loopbackOnly: true);
        var policy = offline.SslOptions.CertificateChainPolicy;
        Assert.NotNull(policy);
        Assert.True(policy.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.NoCheck, policy.RevocationMode);

        using var online = UpdateCheck.NewHandler(loopbackOnly: false);
        Assert.Null(online.SslOptions.CertificateChainPolicy);
    }

    /// <summary>
    /// A manual check under way when the engine stops is cancelled with it: stop waits for it and returns, the check comes
    /// to <see cref="UpdateCheckResult.Off"/>, and after stop nothing is asked, published or raised.
    /// </summary>
    [Fact]
    public async Task Stop_EndsAManualCheckUnderWay_AndNothingIsPublishedAfter()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _feed.AnswerAsync = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        };
        var check = Check();
        var changed = 0;
        check.Changed += _ => Interlocked.Increment(ref changed);
        var manual = check.CheckNowAsync(Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await check.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(UpdateCheckResult.Off, await manual.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        _feed.AnswerAsync = null;
        Assert.Equal(UpdateCheckResult.Off, await check.CheckNowAsync(Ct));
        Assert.Equal(1, _feed.Count);
        Assert.Null(check.Latest);
        Assert.Equal(0, changed);
    }

    /// <summary>Only whitespace may follow the list: <c>[]garbage</c> is a list that isn't one, a failure, not up to date.</summary>
    [Fact]
    public async Task AListWithAnythingAfterIt_IsAFailure()
    {
        _feed.Answer = _ => Json(200, Encoding.UTF8.GetBytes("[]garbage"));
        var check = Check();

        Assert.Equal(UpdateCheckResult.Failed, await check.CheckNowAsync(Ct));
    }

    [Theory]
    [InlineData("2.0.0-dev")]
    [InlineData("2.0.0-dev.ci.7")]
    [InlineData("not a version")]
    public async Task ADevelopmentBuild_SendsNothing_OnItsScheduleOrWhenAsked(string version)
    {
        var check = Check(version);
        check.Start();

        _timers.Advance(TimeSpan.FromDays(3));
        var result = await check.CheckNowAsync(Ct);

        Assert.False(check.Enabled);
        Assert.Equal(UpdateCheckResult.Off, result);
        Assert.Equal(0, _feed.Count);
        Assert.Equal(0, _timers.TimerCount);
    }

    [Fact]
    public async Task Offline_RefusesAFeedOffThisComputer_AndAsksALoopbackOne()
    {
        var github = Check(offline: true);
        var other = Check(feed: "https://feed.example.test/releases.json", offline: true);
        Assert.False(github.Enabled);
        Assert.False(other.Enabled);
        Assert.Equal(UpdateCheckResult.Off, await github.CheckNowAsync(Ct));
        Assert.Equal(UpdateCheckResult.Off, await other.CheckNowAsync(Ct));
        Assert.Equal(0, _feed.Count);

        var loopback = Check(feed: Loopback, offline: true);
        Assert.True(loopback.Enabled);
        Assert.Equal(UpdateCheckResult.Available, await loopback.CheckNowAsync(Ct));
        Assert.Equal(new Uri(Loopback), Assert.Single(_feed.Requests).Uri);
    }

    [Theory]
    [InlineData("http://feed.example.test/releases.json", false)]
    [InlineData("ftp://127.0.0.1/releases.json", false)]
    [InlineData("releases.json", false)]
    [InlineData("https://feed.example.test/releases.json", true)]
    [InlineData("http://127.0.0.1:5383/releases.json", true)]
    [InlineData("http://[::1]:5383/releases.json", true)]
    public void TheFeed_IsHttps_OrHttpOnThisComputer(string feed, bool enabled) => Assert.Equal(enabled, Check(feed: feed).Enabled);

    [Fact]
    public async Task AListOverOneMebibyte_IsRefused()
    {
        var entry = new string(' ', ReleaseFeed.MaxBytes);
        _feed.Answer = _ => Json(200, Encoding.UTF8.GetBytes($"[{entry}{{\"tag_name\": \"2.0.0-beta.1\"}}]"));
        var check = Check();

        Assert.Equal(UpdateCheckResult.Failed, await check.CheckNowAsync(Ct));

        Assert.Null(check.Latest);
    }

    [Fact]
    public async Task A304_KeepsTheAnswer_AndSendsTheLastETag()
    {
        _feed.Answer = _ => Json(200, Releases(("2.0.0-beta.1", false)), ("ETag", "W/\"list-1\""));
        var check = await StartedAsync(Check());
        await PassAsync(check, TimeSpan.FromSeconds(30));
        var first = check.Latest;
        Assert.NotNull(first);
        _feed.Answer = _ => Status(304, ("ETag", "W/\"list-1\""));

        await PassAsync(check, TimeSpan.FromHours(24));

        Assert.Equal(2, _feed.Count);
        Assert.Null(_feed.Requests[0].Header("If-None-Match"));
        Assert.Equal("W/\"list-1\"", _feed.Requests[1].Header("If-None-Match"));
        Assert.Equal(first with { CheckedAt = Start + TimeSpan.FromSeconds(30) + TimeSpan.FromHours(24) }, check.Latest);
        // An answer: the next is a day later, not an hour.
        await PassAsync(check, TimeSpan.FromHours(23));
        Assert.Equal(2, _feed.Count);
    }

    [Fact]
    public async Task A403WithGitHubsReset_WaitsForIt()
    {
        var reset = Start + TimeSpan.FromSeconds(30) + TimeSpan.FromHours(3);
        _feed.Answer = _ => Status(403, ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString()));
        var check = await StartedAsync(Check());
        await PassAsync(check, TimeSpan.FromSeconds(30));
        Assert.Equal(1, _feed.Count);

        await PassAsync(check, TimeSpan.FromHours(3) - TimeSpan.FromSeconds(1));
        Assert.Equal(1, _feed.Count);

        await PassAsync(check, TimeSpan.FromSeconds(1));
        Assert.Equal(2, _feed.Count);
    }

    [Fact]
    public async Task A429WithRetryAfter_WaitsForIt_AtLeastAnHour_AtMostADay()
    {
        _feed.Answer = _ => Status(429, ("Retry-After", ((int)TimeSpan.FromHours(5).TotalSeconds).ToString()));
        var check = await StartedAsync(Check());
        await PassAsync(check, TimeSpan.FromSeconds(30));

        await PassAsync(check, TimeSpan.FromHours(5) - TimeSpan.FromSeconds(1));
        Assert.Equal(1, _feed.Count);
        _feed.Answer = _ => Status(429, ("Retry-After", "5"));
        await PassAsync(check, TimeSpan.FromSeconds(1));
        Assert.Equal(2, _feed.Count);

        // A few seconds' wait is an hour's; a week's a day's.
        await PassAsync(check, TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
        Assert.Equal(2, _feed.Count);
        _feed.Answer = _ => Status(429, ("Retry-After", ((int)TimeSpan.FromDays(7).TotalSeconds).ToString()));
        await PassAsync(check, TimeSpan.FromSeconds(1));
        Assert.Equal(3, _feed.Count);
        await PassAsync(check, TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        Assert.Equal(3, _feed.Count);
        await PassAsync(check, TimeSpan.FromSeconds(1));
        Assert.Equal(4, _feed.Count);
    }

    [Fact]
    public async Task Offline_OrA5xx_IsTriedAgainInAnHour_LoggedOnceAsInformation()
    {
        _feed.Answer = _ => throw new HttpRequestException("No route to host");
        var check = await StartedAsync(Check());
        await PassAsync(check, TimeSpan.FromSeconds(30));
        Assert.Equal(1, _feed.Count);

        await PassAsync(check, TimeSpan.FromMinutes(59));
        Assert.Equal(1, _feed.Count);
        _feed.Answer = _ => Status(503);
        await PassAsync(check, TimeSpan.FromMinutes(1));
        Assert.Equal(2, _feed.Count);
        await PassAsync(check, TimeSpan.FromHours(1));
        Assert.Equal(3, _feed.Count);

        var failures = _events.Where(e => e.RenderMessage().StartsWith("Couldn't check for updates", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, failures.Count);
        Assert.Equal([LogEventLevel.Information, LogEventLevel.Debug, LogEventLevel.Debug], failures.Select(e => e.Level));
    }

    /// <summary>
    /// A day of wall-clock time after the last answer, looked at every hour: a computer asleep overnight (its timers
    /// stopped, its clock not) checks within the hour it wakes; one awake checks a day after the answer, not before.
    /// </summary>
    [Fact]
    public async Task TheNextCheck_IsADayOfWallClockAfterTheAnswer_LookedAtHourly()
    {
        var check = await StartedAsync(Check());
        await PassAsync(check, TimeSpan.FromSeconds(30));
        await PassAsync(check, TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        Assert.Equal(1, _feed.Count);
        await PassAsync(check, TimeSpan.FromSeconds(1));
        Assert.Equal(2, _feed.Count);

        // Asleep for 22 hours: the wall clock moves, the timers don't.
        await PassAsync(check, TimeSpan.FromMinutes(30));
        _clock.Sleep(TimeSpan.FromHours(22));
        await PassAsync(check, TimeSpan.FromMinutes(30));
        Assert.Equal(2, _feed.Count);
        await PassAsync(check, TimeSpan.FromHours(1));
        Assert.Equal(3, _feed.Count);
    }

    [Fact]
    public async Task CheckNow_AsksAtOnce_AtMostOnceEvery10Seconds()
    {
        var check = Check();

        Assert.Equal(UpdateCheckResult.Available, await check.CheckNowAsync(Ct));
        _timers.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(UpdateCheckResult.Available, await check.CheckNowAsync(Ct));
        Assert.Equal(1, _feed.Count);

        _feed.Answer = _ => throw new HttpRequestException("No route to host");
        _timers.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(UpdateCheckResult.Failed, await check.CheckNowAsync(Ct));
        Assert.Equal(2, _feed.Count);
        // A failure keeps what an earlier answer offered.
        Assert.Equal("2.0.0-beta.1", check.Latest?.Version);
    }

    /// <summary>What the fake feed was asked: the method, the address and every header, as sent.</summary>
    private sealed record SentRequest(HttpMethod Method, Uri? Uri, IReadOnlyList<(string Name, string Value)> Headers)
    {
        public string? Header(string name) => Headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault();
    }

    private sealed class FakeFeed : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private readonly List<SentRequest> _requests = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        /// <summary>When set, answers in place of <see cref="Answer"/>, with the request's cancellation.</summary>
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AnswerAsync { get; set; }

        public IReadOnlyList<SentRequest> Requests
        {
            get
            {
                lock (_lock)
                    return [.. _requests];
            }
        }

        public int Count => Requests.Count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            var headers = request.Headers.SelectMany(h => h.Value.Select(v => (h.Key, v)))
                .Concat(request.Content?.Headers.SelectMany(h => h.Value.Select(v => (h.Key, v))) ?? []).ToList();
            lock (_lock)
                _requests.Add(new SentRequest(request.Method, request.RequestUri, headers));
            return AnswerAsync is { } answer ? await answer(request, cancellationToken) : Answer(request);
        }
    }

    /// <summary>The timers' clock, with a wall clock that can move on alone (<see cref="Sleep"/>), as a computer's does asleep.</summary>
    private sealed class WallClock(TimerTimeProvider timers) : TimeProvider
    {
        private long _slept;

        public void Sleep(TimeSpan span) => Interlocked.Add(ref _slept, span.Ticks);

        public override DateTimeOffset GetUtcNow() => timers.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _slept));

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long TimestampFrequency => timers.TimestampFrequency;

        public override long GetTimestamp() => timers.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            timers.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }
}
