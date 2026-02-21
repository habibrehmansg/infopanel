namespace TuringSmartScreenLib;

internal static class Helper
{
    public static void Fill(Span<byte> buffer, Span<byte> pattern)
    {
        pattern.CopyTo(buffer);
        var length = pattern.Length;
        var size = buffer.Length;

        while (length < size - length)
        {
            buffer[..length].CopyTo(buffer[length..]);

            length += length;
        }

        if (length < size)
        {
            buffer[..(size - length)].CopyTo(buffer[length..]);
        }
    }
}
