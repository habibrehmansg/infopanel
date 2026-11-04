using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Service;

/// <summary>Why <see cref="ServiceClient"/> made no request.</summary>
public enum ServiceRefusal
{
    /// <summary>There is no service address.</summary>
    NoService,

    /// <summary>No credential is kept.</summary>
    NoCredential,

    /// <summary>The service refused the credential (401); none is sent until a client provides another.</summary>
    Rejected,

    /// <summary>The request named another origin than the service's (an absolute URL, a scheme-relative path, a backslash).</summary>
    OtherOrigin,

    /// <summary>The address isn't <c>https</c>, nor <c>http</c> on a loopback host.</summary>
    Insecure,

    /// <summary><c>--offline</c>: the service isn't on this computer.</summary>
    Offline,
}

/// <summary>
/// What <see cref="ServiceClient.SendAsync"/> came back with: a refusal, with no request made, or the service's answer
/// (any status, a 3xx too, never followed), and for a 429 or 503 the wait its <c>Retry-After</c> asks for. The caller
/// disposes it, and with it the answer.
/// </summary>
public sealed class ServiceAnswer : IDisposable
{
    internal ServiceAnswer(ServiceRefusal refused) => Refused = refused;

    internal ServiceAnswer(HttpResponseMessage response, TimeSpan? retryAfter)
    {
        Response = response;
        RetryAfter = retryAfter;
    }

    /// <summary>Why no request was made, or null when one was.</summary>
    public ServiceRefusal? Refused { get; }

    /// <summary>The service's answer, its headers read and its body not yet, or null when no request was made.</summary>
    public HttpResponseMessage? Response { get; }

    /// <summary>For a 429 or 503, how long the service asked the engine to wait (<c>Retry-After</c>, never negative), else null.</summary>
    public TimeSpan? RetryAfter { get; }

    public void Dispose() => Response?.Dispose();
}

/// <summary>
/// The one way the engine sends a request to the configured service (spec/docs/api.md, "The configured service"), with
/// the rules every caller gets: only to the address in effect, its origin exactly (a path and query given, never a URL);
/// <c>https</c>, or <c>http</c> to a loopback host; with <c>--offline</c> (<paramref name="loopbackOnly"/>), a loopback host
/// only; the credential as <c>Authorization: Bearer</c>; redirects never followed (a 3xx is the answer, and its
/// <c>Location</c> never gets the credential); no credential, no request; a 401 marks the credential rejected
/// (<see cref="ServiceCredentials.Reject"/>, only the credential that request was sent with), and every request after it
/// is refused here until a client provides another; a 429 or 503 hands the caller its <c>Retry-After</c>. No cookies, no
/// proxy (one the environment names would get the credential, a loopback service's too), no credentials of the handler's.
/// </summary>
public sealed class ServiceClient : IDisposable
{
    private readonly ServiceCredentials _credentials;
    private readonly bool _loopbackOnly;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;

    public ServiceClient(ServiceCredentials credentials, bool loopbackOnly, TimeProvider? time = null)
        : this(credentials, loopbackOnly, time, NewHandler())
    {
    }

    internal ServiceClient(ServiceCredentials credentials, bool loopbackOnly, TimeProvider? time, HttpMessageHandler handler)
    {
        _credentials = credentials;
        _loopbackOnly = loopbackOnly;
        _time = time ?? TimeProvider.System;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>The handler every request goes through: no redirect followed, no cookies, no proxy, no credentials of its own.</summary>
    internal static SocketsHttpHandler NewHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        Proxy = null,
        Credentials = null,
        PreAuthenticate = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Sends <paramref name="method"/> <paramref name="pathAndQuery"/> (from <c>/</c>, on the service's origin) with
    /// <paramref name="content"/>, and answers once the answer's headers are read; or refuses, making no request.
    /// </summary>
    public async Task<ServiceAnswer> SendAsync(HttpMethod method, string pathAndQuery, HttpContent? content = null, CancellationToken ct = default)
    {
        // The address and credential taken together, once: the request goes to that address with that credential alone.
        if (!_credentials.TryTake(out var address, out var credential, out var state, out var generation))
        {
            return new ServiceAnswer(address is null ? ServiceRefusal.NoService
                : state == ServiceCredentialState.Rejected ? ServiceRefusal.Rejected
                : ServiceRefusal.NoCredential);
        }

        if (!ServiceAddress.IsNormal(address))
            return new ServiceAnswer(ServiceRefusal.Insecure);
        var origin = new Uri(address!);
        if (_loopbackOnly && !ServiceAddress.IsLoopbackHost(origin))
            return new ServiceAnswer(ServiceRefusal.Offline);
        if (Target(origin, pathAndQuery) is not { } target)
            return new ServiceAnswer(ServiceRefusal.OtherOrigin);

        using var request = new HttpRequestMessage(method, target) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                _credentials.Reject(generation);
                return new ServiceAnswer(response, null);
            case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable:
                return new ServiceAnswer(response, RetryAfterOf(response.Headers.RetryAfter));
            default:
                return new ServiceAnswer(response, null);
        }
    }

    /// <summary>
    /// <paramref name="pathAndQuery"/> on <paramref name="origin"/>, or null when it would reach anything else: it must
    /// start with one <c>/</c>, hold no backslash or control character, and the URL it makes must keep the origin.
    /// </summary>
    internal static Uri? Target(Uri origin, string pathAndQuery)
    {
        if (pathAndQuery.Length == 0 || pathAndQuery[0] != '/' || pathAndQuery.StartsWith("//", StringComparison.Ordinal)
            || pathAndQuery.Contains('\\') || pathAndQuery.Contains('#'))
        {
            return null;
        }

        foreach (var c in pathAndQuery)
        {
            if (c <= 0x20 || c == 0x7F)
                return null;
        }

        var authority = origin.GetLeftPart(UriPartial.Authority);
        if (!Uri.TryCreate(authority + pathAndQuery, UriKind.Absolute, out var target))
            return null;
        return string.Equals(target.GetLeftPart(UriPartial.Authority), authority, StringComparison.OrdinalIgnoreCase) && target.UserInfo.Length == 0
            ? target
            : null;
    }

    private TimeSpan? RetryAfterOf(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    public void Dispose() => _http.Dispose();
}
