using System.Security.Cryptography;
using System.Text;

namespace InfoPanel.Security;

/// <summary>What a code's use came to (<see cref="PairingCodes.Redeem"/>).</summary>
public enum CodeOutcome
{
    /// <summary>The code's first use, within its time: the pairing was made (or, with no pairings to keep, none).</summary>
    Paired,

    /// <summary>A second use of a code used before: refused, and the pairing the first use made is to be removed.</summary>
    Reused,

    /// <summary>An unknown or expired code: refused.</summary>
    Refused,
}

/// <summary>A code's use, and the pairing it concerns: the one made (<see cref="CodeOutcome.Paired"/>) or the one to remove (<see cref="CodeOutcome.Reused"/>).</summary>
public readonly record struct CodeUse(CodeOutcome Outcome, string? PairingId);

/// <summary>
/// The one-time pairing codes a link the engine opens carries (spec/docs/api.md, "Pairing"): 16 random bytes as 22 base64url
/// characters, kept in memory only, at most <see cref="MaxOutstanding"/> unused (the oldest dropped), each for
/// <see cref="Lifetime"/> from minting. The first use within that time pairs; a used code is remembered until its time
/// ends (past <see cref="MaxRemembered"/>, only those whose pairing the engine still has), and a second use is refused
/// and names the pairing the first made, to be removed. Uses are serialised, so two at
/// once can't both pair. Nothing here logs a code.
/// </summary>
/// <param name="pairingExists">Whether the engine still has a pairing (active or replaced), by its id.</param>
public sealed class PairingCodes(TimeProvider time, Func<string, bool> pairingExists)
{
    /// <summary>The most unused codes kept: minting another drops the oldest.</summary>
    public const int MaxOutstanding = 8;

    /// <summary>
    /// The most used codes remembered (for a second use) until their time ends: past it the oldest whose pairing is gone,
    /// or that made none, is forgotten. A code whose pairing still exists is never forgotten early.
    /// </summary>
    public const int MaxRemembered = 64;

    /// <summary>How long a code is usable, from minting.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private const int CodeBytes = 16;

    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];

    /// <summary>Tests: how many used codes are remembered.</summary>
    internal int Remembered
    {
        get
        {
            lock (_lock)
                return _entries.Count(e => e.Used);
        }
    }

    /// <summary>A new code, usable once within <see cref="Lifetime"/>.</summary>
    public string Mint() => Mint(out _);

    /// <summary>A new code, usable once until <paramref name="expires"/> (<see cref="Lifetime"/> from now).</summary>
    public string Mint(out DateTimeOffset expires)
    {
        var code = PairingStore.Base64Url(RandomNumberGenerator.GetBytes(CodeBytes));
        lock (_lock)
        {
            Prune();
            var minted = time.GetUtcNow();
            expires = minted + Lifetime;
            _entries.Add(new Entry(Encoding.ASCII.GetBytes(code), minted));
            while (_entries.Count(e => !e.Used) > MaxOutstanding)
                _entries.Remove(_entries.First(e => !e.Used));
        }

        return code;
    }

    /// <summary>
    /// Uses <paramref name="code"/>: its first use within its time calls <paramref name="pair"/> (under the codes' lock,
    /// so uses are one at a time), which makes the pairing and answers its id (null when none was made).
    /// </summary>
    public CodeUse Redeem(string code, Func<string?> pair)
    {
        var presented = Encoding.UTF8.GetBytes(code);
        lock (_lock)
        {
            Prune();
            // Every entry compared, without stopping at a match.
            Entry? found = null;
            foreach (var entry in _entries)
            {
                if (CryptographicOperations.FixedTimeEquals(entry.Code, presented))
                    found = entry;
            }

            if (found is null)
                return new CodeUse(CodeOutcome.Refused, null);
            if (found.Used)
                return new CodeUse(CodeOutcome.Reused, found.PairingId);

            found.Used = true;
            found.PairingId = pair();
            // Past the cap the oldest used code whose pairing is gone (or that made none) is forgotten; a code whose
            // pairing the engine still has is kept until its time ends, so its second use still revokes it. Each pairing
            // comes from one code and there are at most PairingStore.MaxPairings (32 < 64), so this stays within the cap.
            while (_entries.Count(e => e.Used) > MaxRemembered
                && _entries.FirstOrDefault(e => e.Used && (e.PairingId is not { } id || !pairingExists(id))) is { } forgotten)
                _entries.Remove(forgotten);
            return new CodeUse(CodeOutcome.Paired, found.PairingId);
        }
    }

    /// <summary>Drops the codes whose time has ended, used or not.</summary>
    private void Prune()
    {
        var now = time.GetUtcNow();
        _entries.RemoveAll(e => now - e.Minted >= Lifetime);
    }

    private sealed class Entry(byte[] code, DateTimeOffset minted)
    {
        public byte[] Code { get; } = code;

        public DateTimeOffset Minted { get; } = minted;

        public bool Used { get; set; }

        public string? PairingId { get; set; }
    }
}
