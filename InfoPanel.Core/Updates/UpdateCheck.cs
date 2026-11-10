using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;

namespace InfoPanel.Updates;

/// <summary>A newer version this engine is offered: its version, its release page, and when the list said so last.</summary>
public sealed record UpdateAvailable(string Version, string Url, DateTimeOffset CheckedAt);

/// <summary>What a check came to.</summary>
public enum UpdateCheckResult
{
    /// <summary>A newer version is offered (<see cref="UpdateCheck.Latest"/>).</summary>
    Available,

    /// <summary>The list was read, and nothing in it is newer for this build.</summary>
    UpToDate,

    /// <summary>The list couldn't be read: unreachable, timed out, a status but 200 or 304, or a list that isn't one.</summary>
    Failed,

    /// <summary>This engine doesn't check (<see cref="UpdateCheck.Enabled"/>), or has stopped: no answer was taken.</summary>
    Off,
}

/// <summary>
/// Whether a newer engine is out (docs/2.0/desktop-shell.md, "Updates"): one <c>GET</c> of the engine's public releases on
/// GitHub (<see cref="GitHubFeed"/>), read by <see cref="ReleaseFeed"/>. The request carries <c>Accept</c>, GitHub's
/// <c>X-GitHub-Api-Version</c> and <c>User-Agent: InfoPanel/{version}</c>, and nothing else (no token, cookie, machine
/// name or install id); <c>If-None-Match</c> with the last answer's <c>ETag</c>, kept in memory, whose 304 keeps that answer.
/// <c>https</c> only (<c>http</c> to this computer, for tests and development); no redirect followed; bounded at
/// <see cref="RequestTimeout"/>; the list read to at most <see cref="ReleaseFeed.MaxBytes"/>; the system's proxy, except
/// under <c>--offline</c>.
/// <para>
/// When: <see cref="FirstCheck"/> after <see cref="Start"/> (the menu-bar item shows), then once
/// <see cref="Interval"/> of wall-clock time has passed since the last answer, looked at every <see cref="Tick"/> (so a
/// computer asleep overnight checks within the hour it wakes); after a failure, <see cref="RetryAfterFailure"/> later;
/// when GitHub limits the address (403 or 429), at its <c>Retry-After</c> or <c>X-RateLimit-Reset</c>, between
/// <see cref="RetryAfterFailure"/> and <see cref="Interval"/>. <see cref="CheckNowAsync"/> checks at once, at most once
/// every <see cref="ManualSpacing"/>. One check at a time.
/// </para>
/// <para>
/// Never for a development build (<see cref="ReleaseFeed.IsDevelopmentBuild"/>, a source build included) or a version that
/// isn't SemVer 2; under <c>--offline</c> only a feed on this computer. <see cref="FeedVariable"/> replaces the address for
/// development. What it found is <see cref="Latest"/>, and <see cref="Changed"/> is raised when that changes; a new
/// version is logged once, a failure once a run as Information and then as Debug.
/// </para>
/// </summary>
public sealed class UpdateCheck : IDisposable
{
    /// <summary>The environment variable that replaces the feed's address (development): an <c>https</c> URL, or <c>http</c> on this computer.</summary>
    public const string FeedVariable = "INFOPANEL_UPDATE_FEED";

    /// <summary>The engine's public releases, newest first, as GitHub lists them.</summary>
    public const string GitHubFeed = "https://api.github.com/repos/habibrehmansg/infopanel/releases?per_page=30";

    public static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan ManualSpacing = TimeSpan.FromSeconds(10);

    private readonly string _version;
    private readonly Uri? _feed;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly HttpClient? _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();

    // Under _gate: the last ETag; when the next scheduled check is due (wall-clock); the last check's end (timestamp) and
    // what it came to; whether a failure was logged as Information this run; the versions logged as available.
    private string? _etag;
    private DateTimeOffset _nextAt = DateTimeOffset.MinValue;
    private long? _lastEnd;
    private UpdateCheckResult _lastResult;
    private bool _failureLogged;
    private string? _announced;

    private volatile UpdateAvailable? _latest;

    // The component's lifetime: every check, scheduled or manual, runs under it; StopAsync and Dispose cancel it for good.
    private readonly CancellationTokenSource _life = new();
    private Task? _loop;

    // The loop's wait, for tests: its length and start (timestamp), and how many waits it has begun.
    private TimeSpan? _waiting;
    private long _waitStart;
    private long _parked;

    /// <param name="version">This engine's version (the session's <c>engineVersion</c>).</param>
    /// <param name="feed">The feed's address (<see cref="FeedVariable"/>), or null for <see cref="GitHubFeed"/>.</param>
    /// <param name="offline"><c>--offline</c>: only a feed on this computer.</param>
    /// <param name="handler">The handler requests go through (tests); null: <see cref="NewHandler"/>. Disposed with this.</param>
    public UpdateCheck(string version, string? feed, bool offline, TimeProvider? time = null, ILogger? log = null, HttpMessageHandler? handler = null)
    {
        _version = version;
        _time = time ?? TimeProvider.System;
        _log = (log ?? Log.Logger).ForContext<UpdateCheck>();
        Off = WhyOff(version, feed ?? GitHubFeed, offline, out _feed);
        if (feed is not null)
            _log.Information("Updates: the feed is {Feed} ({Variable})", feed, FeedVariable);
        if (Off is null)
            _http = new HttpClient(handler ?? NewHandler(offline), disposeHandler: true) { Timeout = RequestTimeout + TimeSpan.FromSeconds(5) };
        else
            handler?.Dispose();
    }

    /// <summary>This engine's version, as the request's <c>User-Agent</c> names it.</summary>
    public string Version => _version;

    /// <summary>Why this engine doesn't check, or null when it does.</summary>
    public string? Off { get; }

    /// <summary>Whether this engine checks: a release or pre-release build, with a feed it may ask.</summary>
    public bool Enabled => Off is null;

    /// <summary>The newer version known, or null when none is (not checked yet, up to date, or off).</summary>
    public UpdateAvailable? Latest => _latest;

    /// <summary>Raised when <see cref="Latest"/>'s version changes, on the check's thread: a handler returns at once.</summary>
    public event Action? Changed;

    /// <summary>The loop's wait still to run, or null while it isn't waiting. For tests.</summary>
    internal TimeSpan? Remaining
    {
        get
        {
            lock (_lock)
                return _waiting is { } wait ? wait - _time.GetElapsedTime(_waitStart) : null;
        }
    }

    /// <summary>How many waits the loop has begun. For tests.</summary>
    internal long Parked
    {
        get
        {
            lock (_lock)
                return _parked;
        }
    }

    /// <summary>
    /// The handler every check goes through: no redirect followed, no cookies, no credentials, no trace headers, connecting
    /// within <see cref="RequestTimeout"/>; the system's proxy unless <paramref name="loopbackOnly"/>. When
    /// <paramref name="loopbackOnly"/> (<c>--offline</c>), an <c>https</c> feed's TLS reaches nothing off this computer
    /// either: no missing issuer certificate is downloaded and no revocation data fetched. Otherwise the platform's defaults.
    /// </summary>
    public static SocketsHttpHandler NewHandler(bool loopbackOnly) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        UseProxy = !loopbackOnly,
        ActivityHeadersPropagator = null,
        ConnectTimeout = RequestTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions = loopbackOnly
            ? new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = new X509ChainPolicy
                {
                    DisableCertificateDownloads = true,
                    RevocationMode = X509RevocationMode.NoCheck,
                },
            }
            : new SslClientAuthenticationOptions(),
    };

    private static string? WhyOff(string version, string feed, bool offline, out Uri? uri)
    {
        uri = null;
        if (!ReleaseFeed.IsVersion(version))
            return $"the engine's version {version} isn't a SemVer 2 version";
        if (ReleaseFeed.IsDevelopmentBuild(version))
            return "a development build";
        if (!Uri.TryCreate(feed, UriKind.Absolute, out var parsed) || !(parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)))
            return "the feed's address isn't https, nor http on this computer";
        if (offline && !parsed.IsLoopback)
            return "--offline, and the feed isn't on this computer";
        uri = parsed;
        return null;
    }

    /// <summary>Starts the schedule: the first check <see cref="FirstCheck"/> from now. Nothing when off, running or stopped.</summary>
    public void Start()
    {
        if (!Enabled)
        {
            _log.Information("Not checking for updates: {Reason}", Off);
            return;
        }

        lock (_lock)
        {
            if (_loop is not null || _life.IsCancellationRequested)
                return;
            var token = _life.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>
    /// Stops for good: cancels the schedule and any check under way, scheduled or manual, and waits for them to end
    /// (bounded by <see cref="RequestTimeout"/>). After it nothing is asked or published, and <see cref="Changed"/> isn't
    /// raised; a manual check cut short or asked for later comes to <see cref="UpdateCheckResult.Off"/>.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        Task? loop;
        lock (_lock)
            loop = _loop;
        _life.Cancel();

        if (loop is not null)
            await loop.WaitAsync(ct).ConfigureAwait(false);
        // Every check holds the gate: once it is free, none is under way, and none can start.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        _gate.Release();
    }

    public void Dispose()
    {
        _life.Cancel();
        _http?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await WaitAsync(FirstCheck, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                TimeSpan wait;
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var now = _time.GetUtcNow();
                    // A clock set back never holds the next check past a day from now.
                    if (_nextAt > now + Interval)
                        _nextAt = now + Interval;
                    if (now >= _nextAt)
                    {
                        await CheckAsync(ct).ConfigureAwait(false);
                        now = _time.GetUtcNow();
                    }

                    wait = _nextAt - now;
                }
                finally
                {
                    _gate.Release();
                }

                await WaitAsync(wait > Tick ? Tick : wait < TimeSpan.Zero ? TimeSpan.Zero : wait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error(ex, "The update check stopped; it starts again with the engine");
        }
    }

    private async Task WaitAsync(TimeSpan wait, CancellationToken ct)
    {
        lock (_lock)
        {
            _waiting = wait;
            _waitStart = _time.GetTimestamp();
            _parked++;
        }

        try
        {
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
                _waiting = null;
        }
    }

    /// <summary>
    /// Checks now (the menu's Check for Updates…), unless off, or a check ended less than <see cref="ManualSpacing"/> ago
    /// (its result is the answer); waits for a check under way first. Under this component's lifetime as well as
    /// <paramref name="ct"/>: stopped (<see cref="StopAsync"/>) before or during it, it comes to
    /// <see cref="UpdateCheckResult.Off"/>.
    /// </summary>
    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken ct)
    {
        if (!Enabled || _life.IsCancellationRequested)
            return UpdateCheckResult.Off;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return UpdateCheckResult.Off;
        }

        try
        {
            if (_life.IsCancellationRequested)
                return UpdateCheckResult.Off;
            if (_lastEnd is { } end && _time.GetElapsedTime(end) < ManualSpacing)
                return _lastResult;
            return await CheckAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return UpdateCheckResult.Off;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One check, under <see cref="_gate"/>: the request, what it came to, and when the next is due.</summary>
    private async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        UpdateCheckResult result;
        TimeSpan next;
        string? failure = null;
        using (var timeout = new CancellationTokenSource(RequestTimeout, _time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
        {
            try
            {
                (result, next, failure) = await RequestAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                (result, next, failure) = (UpdateCheckResult.Failed, RetryAfterFailure, timeout.IsCancellationRequested ? "no answer in 15 s" : ex.Message);
            }
        }

        var now = _time.GetUtcNow();
        _nextAt = now + next;
        _lastEnd = _time.GetTimestamp();
        _lastResult = result;
        if (failure is not null)
        {
            _log.Write(_failureLogged ? LogEventLevel.Debug : LogEventLevel.Information,
                "Couldn't check for updates ({Reason}); trying again in {Wait}", failure, next);
            _failureLogged = true;
        }

        return result;
    }

    private async Task<(UpdateCheckResult, TimeSpan, string?)> RequestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _feed);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.TryAddWithoutValidation("User-Agent", $"InfoPanel/{_version}");
        if (_etag is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
        using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status == 304 && _etag is not null)
        {
            ct.ThrowIfCancellationRequested();
            Answered(_latest is { } kept ? new UpdateOffer(kept.Version, kept.Url) : null);
            return (_latest is null ? UpdateCheckResult.UpToDate : UpdateCheckResult.Available, Interval, null);
        }

        if (status is 403 or 429)
            return (UpdateCheckResult.Failed, LimitWait(response), $"GitHub limited the request ({status})");
        if (status != 200)
            return (UpdateCheckResult.Failed, RetryAfterFailure, $"status {status}");

        var body = await ReadAsync(response, ct).ConfigureAwait(false);
        if (body is null || ReleaseFeed.Parse(body) is not { } releases)
            return (UpdateCheckResult.Failed, RetryAfterFailure, "the list of releases isn't one, or is over 1 MiB");

        _etag = response.Headers.ETag?.ToString();
        var offer = ReleaseFeed.Choose(_version, releases);
        // Stopped while the list was read: nothing is published.
        ct.ThrowIfCancellationRequested();
        Answered(offer);
        return (offer is null ? UpdateCheckResult.UpToDate : UpdateCheckResult.Available, Interval, null);
    }

    /// <summary>An answer: <paramref name="offer"/> published, <see cref="Changed"/> raised when its version changed, a new one logged once.</summary>
    private void Answered(UpdateOffer? offer)
    {
        var before = _latest;
        _latest = offer is null ? null : new UpdateAvailable(offer.Version, offer.Url, _time.GetUtcNow());
        if (offer is not null && offer.Version != _announced)
        {
            _announced = offer.Version;
            _log.Information("InfoPanel {Version} is available: {Url}", offer.Version, offer.Url);
        }

        if (before?.Version == offer?.Version)
            return;
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "A follower of the update check failed");
        }
    }

    /// <summary>
    /// How long GitHub asks the address to wait: its <c>Retry-After</c> (seconds), else until its <c>X-RateLimit-Reset</c>
    /// (Unix seconds); between <see cref="RetryAfterFailure"/> and <see cref="Interval"/>.
    /// </summary>
    private TimeSpan LimitWait(HttpResponseMessage response)
    {
        TimeSpan? wait = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - _time.GetUtcNow(),
            _ => null,
        };
        if (wait is null && response.Headers.TryGetValues("X-RateLimit-Reset", out var resets)
            && long.TryParse(resets.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var reset))
            wait = DateTimeOffset.FromUnixTimeSeconds(Math.Min(reset, 253402300799)) - _time.GetUtcNow();
        if (wait is not { } w || w < RetryAfterFailure)
            return RetryAfterFailure;
        return w > Interval ? Interval : w;
    }

    /// <summary>The list's bytes, or null past <see cref="ReleaseFeed.MaxBytes"/> (refused unread past it).</summary>
    private static async Task<byte[]?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > ReleaseFeed.MaxBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[ReleaseFeed.MaxBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            read += n;
        }

        return read > ReleaseFeed.MaxBytes ? null : buffer[..read];
    }
}
