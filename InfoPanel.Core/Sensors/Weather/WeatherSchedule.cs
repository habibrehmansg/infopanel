using System;

namespace InfoPanel.Sensors.Weather;

/// <summary>
/// How long the weather source waits before it asks the configured service again (<see cref="WeatherFetcher"/>):
/// after an answer, until it <c>expires</c> plus up to <see cref="MaxJitter"/> of random jitter, never sooner than
/// <see cref="Soonest"/> nor later than <see cref="Latest"/>; after a failure, a back-off of 1, 2, 4, 8 and 16 minutes,
/// then 30 minutes until one answers, or the failure's <c>Retry-After</c> when it is longer; after a 429, its
/// <c>Retry-After</c>, at least <see cref="Soonest"/>. A <c>Retry-After</c> is the service's least wait, honoured up to
/// <see cref="LongestRetryAfter"/> (against a nonsense value); <see cref="Latest"/> bounds polling after an answer only.
/// Not thread-safe: the fetch task's own.
/// </summary>
public sealed class WeatherSchedule
{
    /// <summary>The least time between two requests after an answer, or a 429.</summary>
    public static readonly TimeSpan Soonest = TimeSpan.FromMinutes(10);

    /// <summary>The most time between two requests after an answer.</summary>
    public static readonly TimeSpan Latest = TimeSpan.FromMinutes(60);

    /// <summary>The longest <c>Retry-After</c> waited: a longer one is taken as this.</summary>
    public static readonly TimeSpan LongestRetryAfter = TimeSpan.FromHours(24);

    /// <summary>The most random time added after an answer expires, so engines don't all ask at once.</summary>
    public static readonly TimeSpan MaxJitter = TimeSpan.FromSeconds(120);

    /// <summary>The waits after successive failures; the last repeats.</summary>
    public static readonly TimeSpan[] Backoffs =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8),
        TimeSpan.FromMinutes(16), TimeSpan.FromMinutes(30),
    ];

    private int _failures;

    /// <summary>The failures since the last answer.</summary>
    public int Failures => _failures;

    /// <summary>
    /// An answer came at <paramref name="now"/> that <paramref name="expires"/>: the wait until the next request, its
    /// expiry plus <paramref name="jitter"/> (0 to 1) of <see cref="MaxJitter"/>, within <see cref="Soonest"/> and
    /// <see cref="Latest"/>. The back-off starts again.
    /// </summary>
    public TimeSpan Answered(DateTimeOffset now, DateTimeOffset expires, double jitter)
    {
        _failures = 0;
        var wait = expires - now + MaxJitter * Math.Clamp(double.IsFinite(jitter) ? jitter : 0, 0, 1);
        return wait < Soonest ? Soonest : wait > Latest ? Latest : wait;
    }

    /// <summary>A request failed: the wait until the next, the next step of the back-off.</summary>
    public TimeSpan Failed() => Backoffs[Math.Min(_failures++, Backoffs.Length - 1)];

    /// <summary>A request failed, and the service asked to wait <paramref name="retryAfter"/>: the longer of that (at most <see cref="LongestRetryAfter"/>) and the back-off.</summary>
    public TimeSpan Failed(TimeSpan? retryAfter)
    {
        var backoff = Failed();
        return retryAfter is { } asked && asked > backoff ? Bounded(asked) : backoff;
    }

    /// <summary>The service said too many requests (429), asking to wait <paramref name="retryAfter"/>: that wait, at least <see cref="Soonest"/> and at most <see cref="LongestRetryAfter"/>.</summary>
    public static TimeSpan Limited(TimeSpan? retryAfter)
    {
        var wait = retryAfter ?? Soonest;
        return wait < Soonest ? Soonest : Bounded(wait);
    }

    private static TimeSpan Bounded(TimeSpan retryAfter) => retryAfter > LongestRetryAfter ? LongestRetryAfter : retryAfter;

    /// <summary>Something changed that makes the next request a fresh start (the place, the credential, the address, turned on): the back-off starts again.</summary>
    public void Reset() => _failures = 0;
}
