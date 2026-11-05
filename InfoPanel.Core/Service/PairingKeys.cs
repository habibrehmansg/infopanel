using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace InfoPanel.Service;

/// <summary>
/// The configured service's public keys for pairing passes (spec/docs/api.md, "Pairing through the configured service"),
/// fetched from <c>{service.address}/svc/v1/pairing-keys</c> and kept in memory by <c>kid</c>. The request carries no
/// credential and follows the configured service's other rules (<see cref="ServiceClient"/>'s handler: the address's
/// origin only, <c>https</c> or loopback <c>http</c>, loopback only with <c>--offline</c>, no redirect followed, no proxy,
/// no cookies), within <see cref="FetchTimeout"/> and <see cref="MaxBytes"/>. The list is fetched when a pass names a
/// <c>kid</c> it doesn't hold, or once it is <see cref="MaxAge"/> old, and at most once a <see cref="RefetchInterval"/>
/// (a failed fetch counts); each fetch replaces it whole, so a key the service stopped listing is dropped within the hour.
/// A fetch that fails keeps what was held, but a list <see cref="MaxAge"/> old is trusted only once read again: until
/// then no key is answered, so a key the service revoked stops pairing within the hour, reachable or not. Both ages are
/// measured on the monotonic clock (<see cref="TimeProvider.GetTimestamp"/>), never the time of day: a computer's clock
/// set back makes no expired list fresh, nor holds off the next fetch. The list is exactly one JSON value. A change of
/// address forgets them. Nothing here logs a pass.
/// </summary>
public sealed class PairingKeys : IDisposable
{
    /// <summary>The path the keys are listed at, on the service's origin.</summary>
    public const string KeysPath = "/svc/v1/pairing-keys";

    /// <summary>The largest list read, in bytes: anything longer is no list.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The most keys kept from one list.</summary>
    public const int MaxKeys = 16;

    /// <summary>The longest wait for the list, from the request to its last byte.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>At most one fetch this often, whatever the passes name.</summary>
    public static readonly TimeSpan RefetchInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long a list is used before it is fetched again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    private readonly Func<string?> _address;
    private readonly bool _loopbackOnly;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _fetching = new(1, 1);
    private readonly Lock _lock = new();

    // Under _lock: the address the keys are for, the keys, when the list was last fetched and last tried (monotonic
    // timestamps, TimeProvider.GetTimestamp).
    private string? _for;
    private Dictionary<string, ECParameters> _keys = new(StringComparer.Ordinal);
    private long? _fetched;
    private long? _tried;

    /// <param name="address">The service address in effect, normalised, or null without one.</param>
    /// <param name="loopbackOnly"><c>--offline</c>: the service only on a loopback host.</param>
    public PairingKeys(Func<string?> address, bool loopbackOnly, TimeProvider? time = null, ILogger? logger = null)
        : this(address, loopbackOnly, time, logger, ServiceClient.NewHandler())
    {
    }

    internal PairingKeys(Func<string?> address, bool loopbackOnly, TimeProvider? time, ILogger? logger, HttpMessageHandler handler)
    {
        _address = address;
        _loopbackOnly = loopbackOnly;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? Log.ForContext<PairingKeys>();
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// The public key named <paramref name="kid"/> (P-256) of the service at <paramref name="address"/>, or null when it
    /// lists none by that name, or that isn't the address in effect, or its list can't be had, or the list held is
    /// <see cref="MaxAge"/> old and couldn't be read again.
    /// </summary>
    public async Task<ECParameters?> FindAsync(string address, string kid, CancellationToken ct)
    {
        if (address != _address())
            return null;
        if (Held(address, kid, out var key) is { } answer)
            return answer ? key : null;

        await _fetching.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another request may have fetched meanwhile.
            if (Held(address, kid, out key) is { } settled)
                return settled ? key : null;
            lock (_lock)
                _tried = _time.GetTimestamp();

            // The fetch isn't the request's: a client going away doesn't waste the minute.
            var listed = await FetchAsync(address).ConfigureAwait(false);
            lock (_lock)
            {
                if (_for != address)
                    return null;
                if (listed is not null)
                {
                    _keys = listed;
                    _fetched = _tried;
                }

                // A list an hour old is trusted only once read again: a fetch that fails doesn't extend it.
                if (_fetched is not { } fetched || _time.GetElapsedTime(fetched) >= MaxAge)
                    return null;
                return _keys.TryGetValue(kid, out var found) ? found : null;
            }
        }
        finally
        {
            _fetching.Release();
        }
    }

    /// <summary>
    /// Under no lock held: true with the key when it is held and the list is fresh; false when it isn't and no fetch may be
    /// made yet (a stale list's key isn't answered then); null when a fetch is due.
    /// </summary>
    private bool? Held(string address, string kid, out ECParameters key)
    {
        lock (_lock)
        {
            if (_for != address)
            {
                _for = address;
                _keys = new Dictionary<string, ECParameters>(StringComparer.Ordinal);
                _fetched = null;
                _tried = null;
            }

            var now = _time.GetTimestamp();
            var held = _keys.TryGetValue(kid, out key);
            var fresh = _fetched is { } fetched && _time.GetElapsedTime(fetched, now) < MaxAge;
            if (held && fresh)
                return true;
            var mayFetch = _tried is not { } tried || _time.GetElapsedTime(tried, now) >= RefetchInterval;
            return mayFetch ? null : false;
        }
    }

    private async Task<Dictionary<string, ECParameters>?> FetchAsync(string address)
    {
        if (!ServiceAddress.IsNormal(address))
            return Failed("the address isn't https, nor http on a loopback host");
        var origin = new Uri(address);
        if (_loopbackOnly && !ServiceAddress.IsLoopbackHost(origin))
            return Failed("the engine is offline");
        if (ServiceClient.Target(origin, KeysPath) is not { } target)
            return Failed("the address can't hold the keys' path");

        using var timeout = new CancellationTokenSource(FetchTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return Failed($"the service answered {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > MaxBytes)
                return Failed("the list is too long");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[MaxBytes + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false)) > 0)
                read += n;
            if (read > MaxBytes)
                return Failed("the list is too long");
            return Parse(buffer.AsSpan(0, read)) ?? Failed("the answer isn't a list of keys");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Failed("the service didn't answer in time");
        }
        catch (HttpRequestException ex)
        {
            return Failed(ex.Message);
        }
        catch (IOException ex)
        {
            return Failed(ex.Message);
        }
    }

    /// <summary>
    /// The keys a list holds: <c>{"keys":[{"kid","kty":"EC","crv":"P-256","x","y"}, …]}</c>, <c>x</c> and <c>y</c> 32 bytes
    /// each as base64url; other members are ignored, and so is an entry that isn't such a key (its point not on the curve
    /// included). Null when it isn't a list, or anything but JSON whitespace follows it.
    /// </summary>
    internal static Dictionary<string, ECParameters>? Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 8 });
            using var json = JsonDocument.ParseValue(ref reader);
            if (json.RootElement.ValueKind != JsonValueKind.Object || bytes[(int)reader.BytesConsumed..].IndexOfAnyExcept(" \t\r\n"u8) >= 0
                || !json.RootElement.TryGetProperty("keys", out var keys)
                || keys.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var found = new Dictionary<string, ECParameters>(StringComparer.Ordinal);
            foreach (var entry in keys.EnumerateArray())
            {
                if (found.Count == MaxKeys)
                    break;
                if (Key(entry) is { } key && !found.ContainsKey(key.Kid))
                    found[key.Kid] = key.Parameters;
            }

            return found;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string Kid, ECParameters Parameters)? Key(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || Text(entry, "kid") is not { Length: > 0 and <= 256 } kid
            || Text(entry, "kty") != "EC" || Text(entry, "crv") != "P-256"
            || Coordinate(Text(entry, "x")) is not { } x || Coordinate(Text(entry, "y")) is not { } y)
        {
            return null;
        }

        var parameters = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } };
        try
        {
            // The platform refuses a point that isn't on the curve.
            using var check = ECDsa.Create(parameters);
            return (kid, parameters);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static byte[]? Coordinate(string? text)
    {
        if (text is not { Length: 43 })
            return null;
        try
        {
            var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(text);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private Dictionary<string, ECParameters>? Failed(string reason)
    {
        _logger.Warning("Couldn't read the configured service's pairing keys ({Reason})", reason);
        return null;
    }

    public void Dispose()
    {
        _http.Dispose();
        _fetching.Dispose();
    }
}
