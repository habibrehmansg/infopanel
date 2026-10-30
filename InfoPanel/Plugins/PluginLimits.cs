using InfoPanel.Plugins.Ipc;

namespace InfoPanel.Plugins;

/// <summary>
/// Bounds on what a plugin host may send. Plugins are code the user chose to run, but a buggy one must not take the
/// engine's memory: a message over <see cref="MessageBytes"/>, or more waiting to be handled than
/// <see cref="PendingMessages"/> or <see cref="PendingBytes"/>, closes the host's connection (it restarts by the policy);
/// the rest is cut, with one warning per entry.
/// </summary>
public sealed record PluginLimits
{
    public static PluginLimits Default { get; } = new();

    /// <summary>The largest message the engine reads from a host (16 MiB).</summary>
    public long MessageBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// The most calls a host may have sent that the engine hasn't handled yet; one more closes its connection (it
    /// restarts by the policy).
    /// </summary>
    public int PendingMessages { get; init; } = 1024;

    /// <summary>The most bytes those calls may come to (32 MiB, two of the largest message); more closes the connection.</summary>
    public long PendingBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>The most entries (sensors, texts, tables) one host's plugins list; the rest are left out.</summary>
    public int EntriesPerHost { get; init; } = 16_384;

    /// <summary>Names, groups and units are cut to this many characters.</summary>
    public int NameChars { get; init; } = 256;

    /// <summary>A text's value is cut to this many characters (4 KiB).</summary>
    public int TextChars { get; init; } = 4096;

    /// <summary>A table keeps this many rows.</summary>
    public int TableRows { get; init; } = 1000;

    /// <summary>A table keeps this many columns.</summary>
    public int TableColumns { get; init; } = 64;

    /// <summary>A table cell's text is cut to this many characters (1 KiB).</summary>
    public int CellChars { get; init; } = 1024;

    /// <summary>A plugin id is at most this many characters.</summary>
    public int IdChars { get; init; } = 64;

    /// <summary>A container's or an entry's id is at most this many characters; a plugin that lists a longer one is refused.</summary>
    public int IdPartChars { get; init; } = 256;

    /// <summary>
    /// A sensor key (<c>plugin:/…</c>) is at most this many characters, as a document's sensor key may be; a plugin that
    /// would make a longer one is refused.
    /// </summary>
    public int KeyChars { get; init; } = 512;

    /// <summary>A package keeps its newest errors, this many.</summary>
    public int ErrorsKept { get; init; } = 20;

    /// <summary>An error message is cut to this many characters.</summary>
    public int ErrorChars { get; init; } = 4096;
}

/// <summary>
/// What a host has sent that the engine hasn't handled yet, bounded: the engine reads a host's messages as they come and
/// queues its calls until each is handled in turn, so a host sending faster than that (each message within
/// <see cref="PluginLimits.MessageBytes"/>) would grow the queue without end. Past <see cref="PluginLimits.PendingMessages"/>
/// calls or <see cref="PluginLimits.PendingBytes"/> bytes waiting, the read fails (an <see cref="IOException"/>, which
/// ends the connection). Answers to the engine's own calls aren't queued, so they don't count.
/// </summary>
internal sealed class PendingBudget(long maxBytes, int maxMessages)
{
    private readonly Lock _lock = new();
    // Each message's length as its headers are read, until it is parsed (the reader reads a little ahead).
    private readonly Queue<long> _read = new();
    // Each call's length, from when it is parsed until it is handled.
    private readonly Queue<long> _pending = new();
    private long _bytes;

    public static PendingBudget For(PluginLimits limits) => new(limits.PendingBytes, limits.PendingMessages);

    /// <summary>A message of <paramref name="length"/> bytes starts (from <see cref="MessageLimitStream"/>).</summary>
    public void Read(long length)
    {
        lock (_lock)
            _read.Enqueue(length);
    }

    /// <summary>The next message was parsed; a call waits to be handled, and counts. Throws when over the budget.</summary>
    public void Parsed(bool call)
    {
        lock (_lock)
        {
            var length = _read.TryDequeue(out var read) ? read : 0;
            if (!call)
                return;
            _pending.Enqueue(length);
            _bytes += length;
            if (_pending.Count > maxMessages)
                throw new IOException($"A plugin host sent more than {maxMessages} messages the engine hadn't handled yet.");
            if (_bytes > maxBytes)
                throw new IOException($"A plugin host sent more than {maxBytes} bytes of messages the engine hadn't handled yet.");
        }
    }

    /// <summary>The oldest waiting call was handled.</summary>
    public void Handled()
    {
        lock (_lock)
        {
            if (_pending.TryDequeue(out var length))
                _bytes -= length;
        }
    }
}

/// <summary>
/// The header-delimited JSON-RPC reader, counting each message it parses against the host's <see cref="PendingBudget"/>.
/// A host's calls to the engine are notifications of <see cref="IPluginClientCallback"/>'s methods, and only those reach
/// the engine, so each call counted is handled by a callback, which releases it:
/// <list type="bullet">
/// <item>a call with an id, which would leave a reply for the host to read (one it might never read), fails the read (an
/// <see cref="IOException"/>, which ends the connection), so no reply is ever written;</item>
/// <item>a notification the engine doesn't take (another method, or arguments that don't bind to the method's
/// parameters, checked as the dispatcher binds them) is dropped here, uncounted: <paramref name="ignored"/> is told the
/// first one's method.</item>
/// </list>
/// </summary>
internal sealed class BudgetedMessageHandler(
    Stream writer, Stream reader, StreamJsonRpc.IJsonRpcMessageFormatter formatter, PendingBudget budget, Action<string>? ignored = null)
    : StreamJsonRpc.HeaderDelimitedMessageHandler(writer, reader, formatter)
{
    private const int MethodChars = 64;

    private static readonly Dictionary<string, System.Reflection.ParameterInfo[]> Callbacks =
        typeof(IPluginClientCallback).GetMethods().ToDictionary(m => m.Name, m => m.GetParameters(), StringComparer.Ordinal);

    private bool _saidIgnored;

    protected override async ValueTask<StreamJsonRpc.Protocol.JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await base.ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (message is not StreamJsonRpc.Protocol.JsonRpcRequest request)
            {
                if (message != null)
                    budget.Parsed(call: false);
                return message;
            }

            if (!request.IsNotification)
            {
                budget.Parsed(call: false);
                throw new IOException($"A plugin host asked for a reply to {Cut(request.Method)}; the engine takes only notifications from a host.");
            }

            if (!Takes(request))
            {
                budget.Parsed(call: false);
                // The reader holds the message's buffer until told it is done with, which must come before the next read.
                ((StreamJsonRpc.Reflection.IJsonRpcMessageBufferManager)this).DeserializationComplete(request);
                if (!_saidIgnored)
                {
                    _saidIgnored = true;
                    ignored?.Invoke(Cut(request.Method));
                }
                continue;
            }

            budget.Parsed(call: true);
            return request;
        }
    }

    /// <summary>Whether <paramref name="request"/> names a callback and its arguments bind to that callback's parameters.</summary>
    private static bool Takes(StreamJsonRpc.Protocol.JsonRpcRequest request)
    {
        if (request.Method is not { } method || !Callbacks.TryGetValue(method, out var parameters))
            return false;
        var arguments = new object?[parameters.Length];
        try
        {
            return request.TryGetTypedArguments(parameters, arguments) == StreamJsonRpc.Protocol.JsonRpcRequest.ArgumentMatchResult.Success;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Arguments the formatter can't read at all: the dispatcher wouldn't bind them either.
            return false;
        }
    }

    private static string Cut(string? method) =>
        method == null ? "a call without a method" : method.Length > MethodChars ? method[..MethodChars] + "…" : method;
}

/// <summary>
/// Reads a header-delimited JSON-RPC stream (<c>Content-Length: n</c>, a blank line, then n bytes) through to the
/// reader, and fails the read (an <see cref="IOException"/>, which ends the connection) when a message says it is
/// longer than the limit, or a header line runs past 1 KiB, before any of its body is read. Each message's length goes
/// to <paramref name="budget"/>, when given.
/// </summary>
internal sealed class MessageLimitStream(Stream inner, long maxMessageBytes, PendingBudget? budget = null) : Stream
{
    private const int MaxHeaderLine = 1024;
    private const string LengthHeader = "Content-Length:";

    private readonly byte[] _line = new byte[MaxHeaderLine];
    private int _lineLength;
    private long _length = -1;
    private long _remaining;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Inspect(buffer[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Inspect(buffer.Span[..read]);
        return read;
    }

    private void Inspect(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length)
        {
            if (_remaining > 0)
            {
                var take = (int)Math.Min(_remaining, data.Length - i);
                _remaining -= take;
                i += take;
                continue;
            }

            var b = data[i++];
            if (b != (byte)'\n')
            {
                if (_lineLength == MaxHeaderLine)
                    throw new IOException($"A plugin host sent a header line over {MaxHeaderLine} bytes.");
                _line[_lineLength++] = b;
                continue;
            }

            var line = System.Text.Encoding.ASCII.GetString(_line, 0, _lineLength).TrimEnd('\r');
            _lineLength = 0;
            if (line.Length == 0)
            {
                // The headers end: the body follows.
                if (_length < 0)
                    throw new IOException("A plugin host sent a message without a Content-Length.");
                budget?.Read(_length);
                _remaining = _length;
                _length = -1;
            }
            else if (line.StartsWith(LengthHeader, StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(line.AsSpan(LengthHeader.Length).Trim(), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var length))
                    throw new IOException("A plugin host sent a Content-Length that isn't a number.");
                if (length > maxMessageBytes)
                    throw new IOException($"A plugin host sent a message of {length} bytes, over the {maxMessageBytes} the engine reads.");
                _length = length;
            }
        }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
