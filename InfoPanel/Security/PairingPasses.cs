using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfoPanel.Service;
using InfoPanel.Settings;

namespace InfoPanel.Security;

/// <summary>Why a pairing pass was refused: one word, the API's <c>pairingPassProblem</c> code.</summary>
public enum PassRefusal
{
    /// <summary>The <c>service.pairing</c> setting is off.</summary>
    Off,

    /// <summary>Not a pass in its form: its parts, its header, its payload's members, or an <c>exp</c> too far ahead.</summary>
    Malformed,

    /// <summary>The service lists no key by its <c>kid</c>, or its signature doesn't check against it.</summary>
    Signature,

    /// <summary>Its <c>aud</c> isn't a pairing pass's.</summary>
    Audience,

    /// <summary>Its <c>exp</c> passed, beyond the skew.</summary>
    Expired,

    /// <summary>Its <c>host</c> isn't the request's <c>Host</c>, or isn't one of <c>proxy.hosts</c>.</summary>
    Host,

    /// <summary>Its <c>device</c> isn't the id of the credential the engine keeps for the service.</summary>
    Device,

    /// <summary>Its nonce was used before (or the engine remembers as many as it can).</summary>
    Replayed,
}

/// <summary>
/// The checks a pairing pass meets at <c>POST /auth/pairing/pass</c> (spec/docs/api.md, "Pairing through the configured
/// service"), in order, the first that fails answering: the <c>service.pairing</c> setting on, read at the request
/// (<c>off</c>); the pass in its form (<c>malformed</c>): three base64url parts, the header exactly
/// <c>{"alg":"ES256","kid","typ":"infopanel-pairing-pass"}</c>, a 64-byte r‖s signature; the signature, ECDSA P-256 with
/// SHA-256 over the first two parts as sent, against the service's key for <c>kid</c> (<see cref="PairingKeys"/>;
/// <c>signature</c>); the payload exactly <c>v</c> 1, <c>aud</c>, <c>device</c>, <c>host</c>, <c>nonce</c> (128 bits or
/// more, base64url) and <c>exp</c>, at most <see cref="MaxLifetime"/> and <see cref="Skew"/> ahead (<c>malformed</c>);
/// <c>aud</c> <see cref="Audience"/> (<c>audience</c>); <c>exp</c> not past by more than the skew (<c>expired</c>);
/// <c>host</c> the request's <c>Host</c> (its port aside, any case) and one of <c>proxy.hosts</c> (<c>host</c>);
/// <c>device</c> the id of the credential the engine keeps (<see cref="ServiceCredentials.Id"/>; <c>device</c>); the nonce
/// not seen before, remembered until <c>exp</c> and the skew (<see cref="PairingNonces"/>; <c>replayed</c>). One exchange,
/// one service: the keys are the address's in effect as the check begins, and the pass is decided as its nonce is taken,
/// against the address and credential then in effect: a change of either since the check began refuses it (<c>device</c>,
/// the nonce spent). The time used never goes back (<see cref="Now"/>). A header or payload is exactly one JSON value
/// (JSON whitespace around it), and its text decodes; else <c>malformed</c>. Logged as accepted or refused with the word,
/// never the pass or anything in it; refusals at most once a minute (on the monotonic clock), with a count.
/// </summary>
public sealed class PairingPasses(SettingsStore settings, ServiceCredentials credentials, PairingKeys keys, TimeProvider time, ILogger<PairingPasses> logger)
{
    /// <summary>A pairing pass's <c>aud</c>.</summary>
    public const string Audience = "infopanel-pairing";

    /// <summary>A pairing pass's <c>typ</c>.</summary>
    public const string Type = "infopanel-pairing-pass";

    /// <summary>How far the service's clock and the engine's may differ, either way.</summary>
    public static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);

    /// <summary>The longest a pass is made to last.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(60);

    /// <summary>The most nonces remembered at once: far more than the service mints in a pass's time.</summary>
    public const int MaxNonces = 1024;

    private static readonly TimeSpan RefusalLogInterval = TimeSpan.FromMinutes(1);

    private readonly PairingNonces _nonces = new(MaxNonces);
    private readonly Lock _lock = new();
    private int _refused;
    private long? _logged;
    private long _latest = long.MinValue;

    /// <summary>Tests only: awaited just before the nonce is taken, every other check passed.</summary>
    internal Func<Task>? BeforeNonce { get; set; }

    /// <summary>Null when <paramref name="pass"/> is accepted, its nonce now used; else why not.</summary>
    /// <param name="requestHost">The request's <c>Host</c> header as received.</param>
    public async Task<PassRefusal?> CheckAsync(string pass, string? requestHost, CancellationToken ct)
    {
        var refusal = await RefusalAsync(pass, requestHost, ct);
        if (refusal is { } refused)
            LogRefusal(refused);
        else
            logger.LogInformation("A pairing pass was accepted");
        return refusal;
    }

    /// <summary>Whether the <c>service.pairing</c> setting is on, as it reads now.</summary>
    private bool On() => settings.Current.Settings.Service.Pairing;

    /// <summary>
    /// The time the checks use, in unix seconds: the clock, but never earlier than a time already used, so a clock moved
    /// back reopens no pass's time; expiry and the nonces' keeping alike.
    /// </summary>
    private long Now()
    {
        var clock = time.GetUtcNow().ToUnixTimeSeconds();
        lock (_lock)
            return _latest = Math.Max(_latest, clock);
    }

    private async Task<PassRefusal?> RefusalAsync(string pass, string? requestHost, CancellationToken ct)
    {
        if (!On())
            return PassRefusal.Off;
        if (Split(pass) is not { } parts || Header(parts.Header) is not { } kid || parts.Signature.Length != 64)
            return PassRefusal.Malformed;

        // One exchange, one service: the keys of the address in effect now, and the credential kept now; a change of
        // either before the nonce is taken refuses the pass (a fetch of the keys can take a while).
        var service = credentials.Binding;
        if (service.Address is not { } address || await keys.FindAsync(address, kid, ct) is not { } key
            || !Verifies(key, parts.SigningInput, parts.Signature))
        {
            return PassRefusal.Signature;
        }

        var now = Now();
        if (Payload(parts.Payload, now) is not { } payload)
            return PassRefusal.Malformed;
        if (payload.Aud != Audience)
            return PassRefusal.Audience;
        if (now > payload.Exp + (long)Skew.TotalSeconds)
            return PassRefusal.Expired;
        if (!IsThisHost(payload.Host, requestHost, settings.Current.Settings.Proxy.Hosts))
            return PassRefusal.Host;
        if (!IsDevice(payload.Device))
            return PassRefusal.Device;
        // The switch read again before the pass is used: a fetch of the keys may have taken a while.
        if (!On())
            return PassRefusal.Off;
        if (BeforeNonce is { } beforeNonce)
            await beforeNonce();
        if (!_nonces.TryAdd(payload.Nonce, payload.Exp + (long)Skew.TotalSeconds, now))
            return PassRefusal.Replayed;

        // Decided as the nonce is taken: the service the pass was checked against is still the one in effect, and its
        // credential the one the pass names. A change of either before this refuses the pass, its nonce spent; a change
        // after it is a change after the pairing. The id is read before the binding: a binding unchanged since the check
        // began (a new generation for every credential provided or dropped) means the id read is that same credential's.
        if (!IsDevice(payload.Device) || credentials.Binding != service)
            return PassRefusal.Device;
        return null;
    }

    /// <summary>Whether <paramref name="device"/> is the id of the credential the engine keeps for the address in effect.</summary>
    private bool IsDevice(string device) => credentials.Id is { } id && string.Equals(id, device, StringComparison.Ordinal);

    private sealed record Parts(byte[] Header, byte[] Payload, byte[] Signature, byte[] SigningInput);

    private sealed record PassPayload(string Aud, string Device, string Host, string Nonce, long Exp);

    /// <summary>The three parts of a compact pass, decoded; null unless it is exactly three non-empty unpadded base64url parts.</summary>
    private static Parts? Split(string pass)
    {
        var parts = pass.Split('.');
        if (parts.Length != 3)
            return null;
        var decoded = new byte[3][];
        for (var i = 0; i < 3; i++)
        {
            if (Decode(parts[i]) is not { } bytes)
                return null;
            decoded[i] = bytes;
        }

        return new Parts(decoded[0], decoded[1], decoded[2], Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
    }

    private static byte[]? Decode(string part)
    {
        if (part.Length == 0)
            return null;
        foreach (var c in part)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'))
                return null;
        }

        try
        {
            return Base64Url.DecodeFromChars(part);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The header's <c>kid</c>, when it is exactly <c>{"alg":"ES256","kid":…,"typ":"infopanel-pairing-pass"}</c> (any order).</summary>
    private static string? Header(byte[] bytes)
    {
        if (Members(bytes, ["alg", "kid", "typ"]) is not { } members)
            return null;
        return Text(members["alg"]) == "ES256" && Text(members["typ"]) == Type && Text(members["kid"]) is { Length: > 0 and <= 256 } kid ? kid : null;
    }

    /// <summary>The payload when it is exactly <c>v</c> 1, <c>aud</c>, <c>device</c>, <c>host</c>, <c>nonce</c>, <c>exp</c>, and <c>exp</c> isn't too far ahead.</summary>
    private static PassPayload? Payload(byte[] bytes, long now)
    {
        if (Members(bytes, ["v", "aud", "device", "host", "nonce", "exp"]) is not { } members)
            return null;
        if (members["v"] is not { ValueKind: JsonValueKind.Number } v || !v.TryGetInt64(out var version) || version != 1
            || members["exp"] is not { ValueKind: JsonValueKind.Number } e || !e.TryGetInt64(out var exp)
            || exp > now + (long)(MaxLifetime + Skew).TotalSeconds
            || Text(members["aud"]) is not { } aud
            || Text(members["device"]) is not { Length: > 0 and <= 256 } device
            || Text(members["host"]) is not { Length: > 0 and <= 253 } host
            || Text(members["nonce"]) is not { } nonce || !IsNonce(nonce))
        {
            return null;
        }

        return new PassPayload(aud, device, host, nonce, exp);
    }

    /// <summary>A nonce of 128 bits or more: 22 to 86 base64url characters, unpadded.</summary>
    private static bool IsNonce(string nonce)
    {
        if (nonce.Length is < 22 or > 86)
            return false;
        foreach (var c in nonce)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The members of a JSON object that has exactly <paramref name="names"/>, each once; null for anything else (not
    /// JSON, not an object, anything but JSON whitespace after it, a member missing, another member, one twice, a name or
    /// a text that doesn't decode: bytes that aren't UTF-8, a lone surrogate escaped).
    /// </summary>
    private static Dictionary<string, JsonElement>? Members(byte[] bytes, string[] names)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 4 });
            using var json = JsonDocument.ParseValue(ref reader);
            // Exactly one value: nothing after it but JSON whitespace.
            if (json.RootElement.ValueKind != JsonValueKind.Object || bytes.AsSpan((int)reader.BytesConsumed).IndexOfAnyExcept(" \t\r\n"u8) >= 0)
                return null;
            var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var member in json.RootElement.EnumerateObject())
            {
                if (Array.IndexOf(names, member.Name) < 0 || !members.TryAdd(member.Name, member.Value.Clone()))
                    return null;
                // Every text decoded here, so none fails later.
                if (member.Value.ValueKind == JsonValueKind.String)
                    _ = member.Value.GetString();
            }

            return members.Count == names.Length ? members : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Verifies(ECParameters key, byte[] signingInput, byte[] signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create(key);
            return ecdsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="host"/> is the name the request came to (its <c>Host</c>, as received, its port aside, in
    /// any ASCII case) and one of <paramref name="proxyHosts"/>.
    /// </summary>
    private static bool IsThisHost(string host, string? requestHost, IReadOnlyList<string> proxyHosts)
    {
        var name = new HostString(requestHost ?? "").Host;
        if (string.IsNullOrEmpty(name) || !Ascii.IsValid(name) || !string.Equals(name, host, StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var candidate in proxyHosts)
        {
            if (string.Equals(host, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void LogRefusal(PassRefusal refusal)
    {
        var now = time.GetTimestamp();
        int count;
        lock (_lock)
        {
            _refused++;
            if (_logged is { } last && time.GetElapsedTime(last, now) < RefusalLogInterval)
                return;
            count = _refused;
            _refused = 0;
            _logged = now;
        }

        logger.LogWarning("A pairing pass was refused ({Reason:l}); {Count} refused since the last such entry (at most one a minute)",
            Word(refusal), count);
    }

    /// <summary>The refusal's word, as the API answers it.</summary>
    public static string Word(PassRefusal refusal) => refusal switch
    {
        PassRefusal.Off => "off",
        PassRefusal.Malformed => "malformed",
        PassRefusal.Signature => "signature",
        PassRefusal.Audience => "audience",
        PassRefusal.Expired => "expired",
        PassRefusal.Host => "host",
        PassRefusal.Device => "device",
        PassRefusal.Replayed => "replayed",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
    };
}

/// <summary>
/// The nonces of the pairing passes accepted, each remembered until its time (unix seconds) has passed, at most
/// <c>capacity</c> at once: when full, a new nonce is refused rather than one remembered dropped for it. A nonce whose
/// time is no later than one already forgotten is refused too: it may be one of those.
/// </summary>
public sealed class PairingNonces(int capacity)
{
    private readonly Dictionary<string, long> _until = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private long _forgotten = long.MinValue;

    /// <summary>
    /// Remembers <paramref name="nonce"/> until <paramref name="until"/>; false when it is remembered already, it may have
    /// been forgotten, or there is no room.
    /// </summary>
    public bool TryAdd(string nonce, long until, long now)
    {
        lock (_lock)
        {
            if (_until.Count > 0)
            {
                foreach (var gone in _until.Where(n => n.Value < now).ToList())
                {
                    _until.Remove(gone.Key);
                    _forgotten = Math.Max(_forgotten, gone.Value);
                }
            }

            if (until <= _forgotten || _until.ContainsKey(nonce) || _until.Count >= capacity)
                return false;
            _until[nonce] = until;
            return true;
        }
    }
}
