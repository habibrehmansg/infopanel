namespace InfoPanel.Plugins.Ipc
{
    /// <summary>
    /// A picture as a message carries it (<see cref="EntryUpdateDto.ImageValue"/>): base64 text, whose decoded length is
    /// known from its length before anything is decoded.
    /// </summary>
    public static class PictureText
    {
        public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes);

        /// <summary>
        /// The bytes <paramref name="text"/> decodes to, by its length and padding alone (nothing is decoded), or -1 when
        /// its length can't be base64's.
        /// </summary>
        public static long DecodedLength(string text)
        {
            if (text.Length % 4 != 0)
                return -1;
            var padding = text.EndsWith("==", StringComparison.Ordinal) ? 2 : text.EndsWith('=') ? 1 : 0;
            return (long)text.Length / 4 * 3 - padding;
        }

        /// <summary>
        /// Decodes <paramref name="text"/> when it is base64 of at most <paramref name="maxBytes"/> bytes, allocating only
        /// then (exactly its decoded length); else null, with <paramref name="tooLarge"/> telling whether it was the size.
        /// </summary>
        public static byte[]? Decode(string text, long maxBytes, out bool tooLarge)
        {
            var length = DecodedLength(text);
            tooLarge = length > maxBytes;
            if (length < 0 || tooLarge)
                return null;
            var bytes = new byte[length];
            return Convert.TryFromBase64String(text, bytes, out var written) && written == length ? bytes : null;
        }
    }

    /// <summary>
    /// A rate with a burst, as a bucket of <paramref name="capacity"/> that fills at <paramref name="perSecond"/> on
    /// <paramref name="time"/>'s clock, full at first. Not thread-safe: its owner locks.
    /// </summary>
    public sealed class RateBucket(double capacity, double perSecond, TimeProvider time)
    {
        private double _level = capacity;
        private long _at = time.GetTimestamp();

        /// <summary>Whether <paramref name="amount"/> may be taken now.</summary>
        public bool CanTake(double amount)
        {
            var now = time.GetTimestamp();
            _level = Math.Min(capacity, _level + perSecond * time.GetElapsedTime(_at, now).TotalSeconds);
            _at = now;
            return amount <= _level;
        }

        /// <summary>Takes <paramref name="amount"/> (after <see cref="CanTake"/> said it may).</summary>
        public void Take(double amount) => _level -= amount;

        /// <summary>Takes <paramref name="amount"/> when it may be taken now.</summary>
        public bool TryTake(double amount)
        {
            if (!CanTake(amount))
                return false;
            Take(amount);
            return true;
        }

        /// <summary>
        /// Nothing has filled it since the last take: what was taken stays held until now (a send that took this long
        /// holds its reservation through it).
        /// </summary>
        public void Hold() => _at = time.GetTimestamp();
    }
}
