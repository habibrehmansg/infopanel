using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Hosted;

/// <summary>
/// A message from the engine to a panel host. <see cref="Op"/>: <c>hello</c>; <c>probe</c> (<see cref="Family"/>,
/// <see cref="Device"/>, <see cref="Ms"/> its deadline); <c>open</c> (and <see cref="Identity"/>, <see cref="Brightness"/>,
/// <see cref="Rotation"/>, <see cref="Format"/>, <see cref="FamilySettings"/>); <c>attach</c> (<see cref="Session"/>, <see cref="Path"/>: its slot file);
/// <c>write</c> (<see cref="Session"/>, <see cref="Slot"/>, <see cref="FrameId"/>, <see cref="Repeat"/>: the slot is the
/// host's until its answer); <c>brightness</c>; <c>close</c> (<see cref="ScreenOff"/>, <see cref="Ms"/> its budget);
/// <c>shutdown</c> (every session closed, screen off, within <see cref="Ms"/>, then the host ends).
/// </summary>
public sealed record PanelHostRequest
{
    public long Id { get; init; }
    public required string Op { get; init; }
    public string? Family { get; init; }
    public PanelCandidate? Device { get; init; }
    public PanelIdentity? Identity { get; init; }
    public int? Brightness { get; init; }
    public int? Rotation { get; init; }
    public PanelFrameFormat? Format { get; init; }
    /// <summary>An <c>open</c>'s family settings, as the panel has them set (an object).</summary>
    public JsonElement? FamilySettings { get; init; }
    public long? Session { get; init; }
    public int? Slot { get; init; }
    public long? FrameId { get; init; }
    public bool? Repeat { get; init; }
    public string? Path { get; init; }
    public bool? ScreenOff { get; init; }
    public int? Ms { get; init; }
}

/// <summary>A panel host's answer to the request with its <see cref="Id"/>.</summary>
public sealed record PanelHostReply
{
    public long Id { get; init; }
    public PanelDeviceStatus Status { get; init; }
    public string? Message { get; init; }
    public bool? Mine { get; init; }
    public PanelIdentity? Identity { get; init; }
    public long? Session { get; init; }
    public PanelCapabilities? Capabilities { get; init; }
    public double? DeviceMs { get; init; }
    public int? Queued { get; init; }
    public PanelDeviceStatus? FormatRefused { get; init; }
    public IReadOnlyList<string>? Families { get; init; }
}

/// <summary>
/// The panel host's pipe: each message a 4-byte little-endian length, then that many bytes of UTF-8 JSON, at most
/// <see cref="MaxMessageBytes"/>. No frame crosses it: frames go through the session's slot file.
/// </summary>
public static class PanelHostWire
{
    /// <summary>The largest message either side sends or takes (64 KiB): a larger one ends the connection.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>The longest message text taken from a host (for the log and the API).</summary>
    public const int MaxTextChars = 4096;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
    };

    /// <summary>Writes one message; the caller holds the stream's write lock.</summary>
    public static void Write<T>(Stream stream, T message)
    {
        stream.Write(Frame(message));
        stream.Flush();
    }

    /// <summary>
    /// Writes one message; the caller holds the stream's write turn. <paramref name="stop"/> cancelled while the write is
    /// still under way throws <see cref="OperationCanceledException"/>, the write left as it is (the caller ends the stream).
    /// </summary>
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken stop)
    {
        var writing = stream.WriteAsync(Frame(message), CancellationToken.None).AsTask();
        // A write left under way fails once the stream ends: its failure is seen here, never left unobserved.
        _ = writing.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await writing.WaitAsync(stop).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static byte[] Frame<T>(T message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (body.Length > MaxMessageBytes)
            throw new InvalidDataException($"A panel host message of {body.Length} bytes is over {MaxMessageBytes}.");
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>The next message, or null at the stream's end.</summary>
    /// <exception cref="InvalidDataException">A length over <see cref="MaxMessageBytes"/>, or a message that isn't one.</exception>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken stop) where T : class
    {
        var length = new byte[4];
        if (!await FillAsync(stream, length, stop).ConfigureAwait(false))
            return null;
        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size is <= 0 or > MaxMessageBytes)
            throw new InvalidDataException($"A panel host message of {size} bytes is out of bounds.");
        var body = new byte[size];
        if (!await FillAsync(stream, body, stop).ConfigureAwait(false))
            throw new InvalidDataException("The panel host's pipe ended inside a message.");
        try
        {
            return JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidDataException("A panel host message was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"A panel host message isn't one: {ex.Message}", ex);
        }
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken stop)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), stop).ConfigureAwait(false);
            if (n == 0)
                return read == 0 ? false : throw new InvalidDataException("The panel host's pipe ended inside a message.");
            read += n;
        }
        return true;
    }

    /// <summary><paramref name="text"/> cut to <see cref="MaxTextChars"/>.</summary>
    public static string Cut(string? text) => text is null ? "" : text.Length > MaxTextChars ? text[..MaxTextChars] : text;
}
