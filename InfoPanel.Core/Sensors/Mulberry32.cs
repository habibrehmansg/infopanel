using System.Text;

namespace InfoPanel.Sensors;

/// <summary>
/// The mulberry32 generator: a 32-bit state, doubles in [0, 1).
/// Not thread-safe.
/// </summary>
internal sealed class Mulberry32(uint seed)
{
    private uint _state = seed;

    public double NextDouble()
    {
        unchecked
        {
            _state += 0x6D2B79F5;
            var t = (_state ^ (_state >> 15)) * (1 | _state);
            t = (t + ((t ^ (t >> 7)) * (61 | t))) ^ t;
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }
}

/// <summary>A hash that is the same in every process and on every OS (unlike <c>string.GetHashCode</c>).</summary>
internal static class StableHash
{
    /// <summary>FNV-1a over the UTF-8 bytes.</summary>
    public static uint Fnv1a(string s)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(s))
            {
                hash ^= b;
                hash *= 16777619u;
            }

            return hash;
        }
    }
}
