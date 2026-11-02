using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using InfoPanel.Hosting;

namespace InfoPanel.Security;

/// <summary>A challenge's answer: the engine's nonce Ne and its proof E.</summary>
public sealed record PairingChallengeAnswer(byte[] Nonce, byte[] Proof);

/// <summary>A proof's answer: the run's session token and instance, and the proof A over them.</summary>
public sealed record PairingSessionAnswer(string Token, string Instance, byte[] Proof);

/// <summary>
/// The pairing proofs (spec/docs/api.md, "Pairing"): a client that paired gets the run's session token only after both
/// sides prove the pairing secret K over the engine's own listening port P, so a program on another port (a relay to the
/// engine on its new port, a squatter on its old one) learns nothing it can use.
/// <list type="number">
/// <item><see cref="Challenge"/>: for a pairing the engine has, active or replaced (the handover), it draws Ne, keeps the
/// challenge for <see cref="ChallengeLifetime"/> (at most <see cref="MaxChallenges"/> for all pairings, the oldest
/// dropped) and answers <c>E = HMAC-SHA256(K, "InfoPanel pairing 1: engine" ‖ 0x00 ‖ Np ‖ Ne ‖ P)</c>.</item>
/// <item><see cref="Prove"/>: takes the challenge out (single use), refuses one that is gone, expired, another
/// pairing's or a replaced pairing's, then checks <c>C</c> (the label <c>client</c>) in constant time. On success it
/// records the pairing's use and answers the token and instance with
/// <c>A = HMAC-SHA256(K, "InfoPanel pairing 1: answer" ‖ 0x00 ‖ Np ‖ Ne ‖ P ‖ token ‖ 0x00 ‖ instance)</c>, token and
/// instance as their UTF-8 bytes. A failure is logged as a warning at most once a minute, with a count.</item>
/// </list>
/// P is <see cref="EngineRuntime.Port"/> (2 bytes, big-endian), never the <c>Host</c> header or anything the client sends.
/// Distinct labels keep E from being accepted as C. Removing a pairing drops its challenges. Nothing here logs a secret,
/// nonce, proof or the token.
/// </summary>
public sealed class PairingProofs
{
    /// <summary>The most challenges kept, for all pairings: a bound on memory, not a rate limit.</summary>
    public const int MaxChallenges = 64;

    /// <summary>The bytes of each nonce, Np and Ne.</summary>
    public const int NonceBytes = 32;

    /// <summary>How long a challenge waits for its proof.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(30);

    /// <summary>How often a failed proof is logged at most.</summary>
    public static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

    private static readonly byte[] EngineLabel = "InfoPanel pairing 1: engine"u8.ToArray();
    private static readonly byte[] ClientLabel = "InfoPanel pairing 1: client"u8.ToArray();
    private static readonly byte[] AnswerLabel = "InfoPanel pairing 1: answer"u8.ToArray();

    private readonly PairingStore _pairings;
    private readonly EngineRuntime _runtime;
    private readonly string _instance;
    private readonly TimeProvider _time;
    private readonly Serilog.ILogger _logger;
    private readonly Lock _lock = new();
    private readonly List<Pending> _pending = [];
    private int _failures;
    private DateTimeOffset? _warned;

    public PairingProofs(PairingStore pairings, EngineRuntime runtime, string instance, TimeProvider time, Serilog.ILogger logger)
    {
        _pairings = pairings;
        _runtime = runtime;
        _instance = instance;
        _time = time;
        _logger = logger;
        pairings.Removed += Drop;
    }

    /// <summary>Tests: draws Ne (32 random bytes).</summary>
    internal Func<byte[]> NewNonce { get; set; } = () => RandomNumberGenerator.GetBytes(NonceBytes);

    /// <summary>Step 1: Ne and E for the client's nonce <paramref name="clientNonce"/>; null when the engine doesn't have the pairing.</summary>
    public PairingChallengeAnswer? Challenge(string pairingId, byte[] clientNonce)
    {
        if (_pairings.Find(pairingId) is not { } pairing)
            return null;

        var engineNonce = NewNonce();
        var port = _runtime.Port;
        lock (_lock)
        {
            Prune();
            _pending.Add(new Pending(engineNonce, pairingId, clientNonce, _time.GetUtcNow() + ChallengeLifetime));
            while (_pending.Count > MaxChallenges)
                _pending.RemoveAt(0);
        }

        return new PairingChallengeAnswer(engineNonce, EngineProof(Key(pairing), clientNonce, engineNonce, port));
    }

    /// <summary>
    /// Step 3: the client's proof <paramref name="proof"/> for the challenge <paramref name="engineNonce"/>; the run's
    /// token, instance and A, or null for any failed check (the challenge is gone either way).
    /// </summary>
    public PairingSessionAnswer? Prove(string pairingId, byte[] engineNonce, byte[] proof)
    {
        Pending? taken;
        lock (_lock)
        {
            Prune();
            taken = _pending.FirstOrDefault(p => CryptographicOperations.FixedTimeEquals(p.EngineNonce, engineNonce));
            if (taken != null)
                _pending.Remove(taken);
        }

        var port = _runtime.Port;
        if (taken is null || taken.PairingId != pairingId || _pairings.Find(pairingId) is not { IsReplaced: false } pairing)
            return Failed();

        var key = Key(pairing);
        if (!CryptographicOperations.FixedTimeEquals(ClientProof(key, taken.ClientNonce, engineNonce, port), proof))
            return Failed();

        _pairings.Touch(pairingId);
        var token = _runtime.Token;
        return new PairingSessionAnswer(token, _instance, AnswerProof(key, taken.ClientNonce, engineNonce, port, token, _instance));
    }

    /// <summary><c>E = HMAC-SHA256(K, "InfoPanel pairing 1: engine" ‖ 0x00 ‖ Np ‖ Ne ‖ P)</c>.</summary>
    public static byte[] EngineProof(byte[] key, byte[] clientNonce, byte[] engineNonce, int port) =>
        Mac(key, EngineLabel, clientNonce, engineNonce, port, null);

    /// <summary><c>C = HMAC-SHA256(K, "InfoPanel pairing 1: client" ‖ 0x00 ‖ Np ‖ Ne ‖ P)</c>.</summary>
    public static byte[] ClientProof(byte[] key, byte[] clientNonce, byte[] engineNonce, int port) =>
        Mac(key, ClientLabel, clientNonce, engineNonce, port, null);

    /// <summary><c>A = HMAC-SHA256(K, "InfoPanel pairing 1: answer" ‖ 0x00 ‖ Np ‖ Ne ‖ P ‖ token ‖ 0x00 ‖ instance)</c>, token and instance in UTF-8.</summary>
    public static byte[] AnswerProof(byte[] key, byte[] clientNonce, byte[] engineNonce, int port, string token, string instance)
    {
        var tail = new List<byte>(Encoding.UTF8.GetBytes(token)) { 0 };
        tail.AddRange(Encoding.UTF8.GetBytes(instance));
        return Mac(key, AnswerLabel, clientNonce, engineNonce, port, [.. tail]);
    }

    private static byte[] Mac(byte[] key, byte[] label, byte[] clientNonce, byte[] engineNonce, int port, byte[]? tail)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(label);
        hmac.AppendData([0]);
        hmac.AppendData(clientNonce);
        hmac.AppendData(engineNonce);
        Span<byte> p = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(p, checked((ushort)port));
        hmac.AppendData(p);
        if (tail != null)
            hmac.AppendData(tail);
        return hmac.GetHashAndReset();
    }

    private static byte[] Key(PairingRecord pairing) => System.Buffers.Text.Base64Url.DecodeFromChars(pairing.Secret);

    private PairingSessionAnswer? Failed()
    {
        var now = _time.GetUtcNow();
        int count;
        lock (_lock)
        {
            _failures++;
            if (_warned is { } last && now - last < WarningInterval)
                return null;
            count = _failures;
            _failures = 0;
            _warned = now;
        }

        _logger.Warning("{Count} pairing proof(s) failed since the last warning (at most one a minute)", count);
        return null;
    }

    private void Drop(string pairingId)
    {
        lock (_lock)
            _pending.RemoveAll(p => p.PairingId == pairingId);
    }

    private void Prune()
    {
        var now = _time.GetUtcNow();
        _pending.RemoveAll(p => now >= p.Expires);
    }

    private sealed record Pending(byte[] EngineNonce, string PairingId, byte[] ClientNonce, DateTimeOffset Expires);
}
