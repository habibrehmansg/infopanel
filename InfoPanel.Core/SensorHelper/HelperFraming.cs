using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.SensorHelper;

/// <summary>Frames on the helper's pipe: a 4-byte little-endian length, then that many bytes (1 to <see cref="HelperProtocol.MaxFrameBytes"/>).</summary>
public static class HelperFraming
{
    /// <summary>The bytes of the next frame, or null when the stream ended cleanly between frames (the other side closed).</summary>
    /// <exception cref="HelperProtocolException">
    /// The stream ended inside a frame (<c>truncated</c>), or its length is 0 (<c>malformed</c>) or over the cap
    /// (<c>tooLarge</c>, its bytes never read).
    /// </exception>
    public static async ValueTask<byte[]?> ReadAsync(Stream stream, CancellationToken cancel)
    {
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancel).ConfigureAwait(false);
        if (read == 0)
            return null;
        if (read < header.Length)
            throw new HelperProtocolException("truncated", "The pipe closed inside a frame's length.");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0)
            throw new HelperProtocolException("malformed", "A frame is empty.");
        if (length > HelperProtocol.MaxFrameBytes)
            throw new HelperProtocolException("tooLarge", $"A frame of {length} bytes; at most {HelperProtocol.MaxFrameBytes} are read.");

        var payload = new byte[length];
        if (await stream.ReadAtLeastAsync(payload, payload.Length, throwOnEndOfStream: false, cancel).ConfigureAwait(false) < payload.Length)
            throw new HelperProtocolException("truncated", "The pipe closed inside a frame.");
        return payload;
    }

    /// <summary>Writes <paramref name="payload"/> as one frame and flushes it.</summary>
    /// <exception cref="HelperProtocolException">The payload is empty (<c>malformed</c>) or over the cap (<c>tooLarge</c>); nothing is written.</exception>
    public static async ValueTask WriteAsync(Stream stream, byte[] payload, CancellationToken cancel)
    {
        if (payload.Length == 0)
            throw new HelperProtocolException("malformed", "A frame is empty.");
        if (payload.Length > HelperProtocol.MaxFrameBytes)
            throw new HelperProtocolException("tooLarge", $"A frame of {payload.Length} bytes; at most {HelperProtocol.MaxFrameBytes} are sent.");

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }
}
