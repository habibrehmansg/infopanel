using System.Buffers.Text;
using System.Text;

namespace InfoPanel.AiUsage;

/// <summary>
/// What one transcript line gave of its usage (an assistant line's; <see cref="UsageLineReader"/> gives no other): its
/// time, ids, model, speed and token counts, or the field that is missing or of another kind (<see cref="Problem"/>).
/// Nothing else of the line is kept.
/// </summary>
internal sealed record UsageLine(
    string? Timestamp, string? MessageId, string? RequestId, string? Model, string? Speed,
    long Input, long CacheWrite, long CacheRead, long Output, long? Write5m, long? Write1h, string? Problem);

/// <summary>
/// The streaming reader of Claude Code's transcripts (<c>*.jsonl</c>, one JSON object a line): fed a file's bytes in
/// chunks of any size, it hands on each complete line that is an assistant message (top-level <c>type</c>
/// <c>assistant</c>) with its usage fields, and nothing of any other line. It walks the JSON byte by byte, keeping only
/// how deep it is and where (the top level, <c>message</c>, <c>message.usage</c>, <c>message.usage.cache_creation</c>),
/// and copies only the values it needs (<c>type</c>, <c>timestamp</c>, <c>requestId</c>, <c>message.id</c>,
/// <c>message.model</c> and the usage counts and speed), each to a small buffer of its own; every other value, message
/// text included, is passed over where it lies in the chunk, never copied or turned into a string. So a line of any
/// length is read in the memory of its chunk, and a line still being written carries on where it stopped when the
/// file grows (the reader keeps its place). A line that isn't a JSON object, or nests deeper than
/// <see cref="MaxDepth"/>, is passed over to its end. Not thread-safe; one reader a file.
/// </summary>
internal sealed class UsageLineReader
{
    /// <summary>The deepest nesting followed; a line nesting deeper is passed over.</summary>
    public const int MaxDepth = 256;

    private enum Mode : byte { Normal, InString, InBare, Skip }

    private enum Field : byte
    {
        None, Type, Timestamp, RequestId, Id, Model, Speed,
        Input, CacheWrite, CacheRead, Output, Write5m, Write1h,
        Count,
    }

    // The containers whose members are looked at; any other container is "elsewhere".
    private const sbyte Elsewhere = -1, Root = 0, Message = 1, Usage = 2, CacheCreation = 3;

    private const byte Missing = 0, Present = 1, TooLong = 2, WrongKind = 3;

    private static readonly int[] Sizes = [0, 16, 48, 128, 128, 96, 16, 24, 24, 24, 24, 24, 24];

    private readonly byte[][] _values = new byte[(int)Field.Count][];
    private readonly int[] _lengths = new int[(int)Field.Count];
    private readonly byte[] _states = new byte[(int)Field.Count];
    private readonly sbyte[] _codes = new sbyte[MaxDepth];
    private readonly bool[] _arrays = new bool[MaxDepth];
    private readonly byte[] _key = new byte[32];

    private Mode _mode;
    private int _depth;
    private bool _expectKey;
    private bool _inKey;
    private bool _escape;
    private int _keyLength;
    private bool _keyTooLong;
    private Field _field;
    private Field _capturing;
    private bool _rootDone;
    private bool _sawAnything;
    private bool _hasMessage;
    private bool _hasUsage;

    public UsageLineReader()
    {
        for (var i = 1; i < (int)Field.Count; i++)
            _values[i] = new byte[Sizes[i]];
    }

    /// <summary>Whether the reader stands at a line's start, holding nothing of a line.</summary>
    public bool AtLineStart => !_sawAnything && _mode == Mode.Normal;

    /// <summary>Forgets the line it is in: the next byte starts a line.</summary>
    public void Reset()
    {
        _mode = Mode.Normal;
        _depth = 0;
        _expectKey = _inKey = _escape = _keyTooLong = false;
        _keyLength = 0;
        _field = _capturing = Field.None;
        _rootDone = _sawAnything = _hasMessage = _hasUsage = false;
        Array.Clear(_states);
        Array.Clear(_lengths);
    }

    /// <summary>
    /// Reads <paramref name="chunk"/>, the bytes after the last chunk fed; hands each complete assistant line it ends to
    /// <paramref name="line"/>. The bytes of a line not yet ended are read and passed over as they come; what the line
    /// gave so far is kept for when its end comes.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> chunk, Action<UsageLine> line)
    {
        var at = 0;
        while (at < chunk.Length)
        {
            switch (_mode)
            {
                case Mode.Skip:
                {
                    var end = chunk[at..].IndexOf((byte)'\n');
                    if (end < 0)
                        return;
                    at += end + 1;
                    Reset();
                    break;
                }
                case Mode.InString:
                    at = InString(chunk, at, line);
                    break;
                case Mode.InBare:
                    at = InBare(chunk, at);
                    break;
                default:
                    at = Normal(chunk, at, line);
                    break;
            }
        }
    }

    private int InString(ReadOnlySpan<byte> chunk, int at, Action<UsageLine> line)
    {
        while (at < chunk.Length)
        {
            if (_escape)
            {
                Take(chunk[at]);
                _escape = false;
                at++;
                continue;
            }
            var rest = chunk[at..];
            var stop = rest.IndexOfAny((byte)'"', (byte)'\\', (byte)'\n');
            if (stop < 0)
            {
                Take(rest);
                return chunk.Length;
            }
            Take(rest[..stop]);
            at += stop;
            var b = chunk[at];
            if (b == (byte)'\n')
            {
                // A raw newline can't be inside a JSON string: the line ended broken.
                EndLine(line);
                return at + 1;
            }
            if (b == (byte)'\\')
            {
                Take(b);
                _escape = true;
                at++;
                continue;
            }
            // The closing quote.
            at++;
            _mode = Mode.Normal;
            if (_inKey)
                EndKey();
            else
                EndValue();
            return at;
        }
        return at;
    }

    private int InBare(ReadOnlySpan<byte> chunk, int at)
    {
        var rest = chunk[at..];
        var stop = rest.IndexOfAny(" \t\r\n,}]"u8);
        if (stop < 0)
        {
            Take(rest);
            return chunk.Length;
        }
        Take(rest[..stop]);
        _mode = Mode.Normal;
        EndValue();
        return at + stop;
    }

    private int Normal(ReadOnlySpan<byte> chunk, int at, Action<UsageLine> line)
    {
        while (at < chunk.Length && _mode == Mode.Normal)
        {
            var b = chunk[at++];
            switch (b)
            {
                case (byte)' ' or (byte)'\t' or (byte)'\r':
                    break;
                case (byte)'\n':
                    EndLine(line);
                    return at;
                case (byte)':':
                    break;
                case (byte)',':
                    if (_depth > 0 && !_arrays[_depth - 1])
                        _expectKey = true;
                    _field = Field.None;
                    break;
                case (byte)'{':
                    Open(array: false);
                    break;
                case (byte)'[':
                    Open(array: true);
                    break;
                case (byte)'}' or (byte)']':
                    if (_depth == 0)
                    {
                        _mode = Mode.Skip;
                        return at;
                    }
                    _depth--;
                    _field = Field.None;
                    _expectKey = false;
                    if (_depth == 0)
                        _rootDone = true;
                    break;
                case (byte)'"':
                    if (!StartValue())
                        return at;
                    if (_depth > 0 && !_arrays[_depth - 1] && _expectKey)
                    {
                        _inKey = true;
                        _keyLength = 0;
                        _keyTooLong = false;
                        _expectKey = false;
                    }
                    else
                        StartCapture(isString: true);
                    _mode = Mode.InString;
                    return at;
                default:
                    if (!StartValue())
                        return at;
                    StartCapture(isString: false);
                    Take(b);
                    _mode = Mode.InBare;
                    return at;
            }
        }
        return at;
    }

    /// <summary>A value (or key) starts: refused when it isn't inside the line's one top-level object.</summary>
    private bool StartValue()
    {
        _sawAnything = true;
        if (_depth == 0)
        {
            _mode = Mode.Skip;
            return false;
        }
        return true;
    }

    private void Open(bool array)
    {
        _sawAnything = true;
        if (_rootDone || (_depth == 0 && array) || _depth == MaxDepth)
        {
            _mode = Mode.Skip;
            return;
        }
        sbyte code;
        if (_depth == 0)
            code = Root;
        else if (array)
            code = Elsewhere;
        else
        {
            var parent = _codes[_depth - 1];
            code = (parent, _field) switch
            {
                (Root, Field.None) when Key("message"u8) => Message,
                (Message, Field.None) when Key("usage"u8) => Usage,
                (Usage, Field.None) when Key("cache_creation"u8) => CacheCreation,
                _ => Elsewhere,
            };
            if (code == Message)
                _hasMessage = true;
            else if (code == Usage)
                _hasUsage = true;
        }
        if (_field != Field.None)
            _states[(int)_field] = WrongKind;
        _codes[_depth] = code;
        _arrays[_depth] = array;
        _depth++;
        _expectKey = !array;
        _field = Field.None;
        _keyLength = 0;
    }

    /// <summary>Whether the key just read (the member whose value starts) is <paramref name="name"/>.</summary>
    private bool Key(ReadOnlySpan<byte> name) => !_keyTooLong && _key.AsSpan(0, _keyLength).SequenceEqual(name);

    private void EndKey()
    {
        _inKey = false;
        var code = _codes[_depth - 1];
        _field = code switch
        {
            Root when Key("type"u8) => Field.Type,
            Root when Key("timestamp"u8) => Field.Timestamp,
            Root when Key("requestId"u8) => Field.RequestId,
            Message when Key("id"u8) => Field.Id,
            Message when Key("model"u8) => Field.Model,
            Usage when Key("input_tokens"u8) => Field.Input,
            Usage when Key("cache_creation_input_tokens"u8) => Field.CacheWrite,
            Usage when Key("cache_read_input_tokens"u8) => Field.CacheRead,
            Usage when Key("output_tokens"u8) => Field.Output,
            Usage when Key("speed"u8) => Field.Speed,
            CacheCreation when Key("ephemeral_5m_input_tokens"u8) => Field.Write5m,
            CacheCreation when Key("ephemeral_1h_input_tokens"u8) => Field.Write1h,
            _ => Field.None,
        };
    }

    private void StartCapture(bool isString)
    {
        _capturing = Field.None;
        if (_field == Field.None)
            return;
        var wantsString = _field <= Field.Speed;
        _capturing = _field;
        _lengths[(int)_field] = 0;
        // Of another kind, it is still taken, so a null can be told from a value of the wrong kind at its end.
        _states[(int)_field] = wantsString == isString ? Present : WrongKind;
    }

    private void EndValue()
    {
        // A member that is null is as good as missing.
        if (_capturing != Field.None && _states[(int)_capturing] == WrongKind && Bytes(_capturing).SequenceEqual("null"u8))
            _states[(int)_capturing] = Missing;
        _capturing = Field.None;
        _field = Field.None;
    }

    private void Take(byte b)
    {
        if (_inKey)
        {
            if (_keyLength < _key.Length)
                _key[_keyLength++] = b;
            else
                _keyTooLong = true;
            return;
        }
        if (_capturing == Field.None)
            return;
        var i = (int)_capturing;
        if (_lengths[i] < _values[i].Length)
            _values[i][_lengths[i]++] = b;
        else
            _states[i] = TooLong;
    }

    private void Take(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        if (_inKey)
        {
            var room = _key.Length - _keyLength;
            if (bytes.Length > room)
            {
                _keyTooLong = true;
                bytes = bytes[..room];
            }
            bytes.CopyTo(_key.AsSpan(_keyLength));
            _keyLength += bytes.Length;
            return;
        }
        if (_capturing == Field.None)
            return;
        var i = (int)_capturing;
        var space = _values[i].Length - _lengths[i];
        if (bytes.Length > space)
        {
            _states[i] = TooLong;
            bytes = bytes[..space];
        }
        bytes.CopyTo(_values[i].AsSpan(_lengths[i]));
        _lengths[i] += bytes.Length;
    }

    /// <summary>A line ended: an assistant line whose object closed is handed on; the reader then stands at the next line's start.</summary>
    private void EndLine(Action<UsageLine> line)
    {
        var whole = _rootDone && _mode != Mode.Skip;
        if (whole && _states[(int)Field.Type] == Present && Bytes(Field.Type).SequenceEqual("assistant"u8))
            line(Build());
        Reset();
    }

    private ReadOnlySpan<byte> Bytes(Field field) => _values[(int)field].AsSpan(0, _lengths[(int)field]);

    private UsageLine Build()
    {
        string? problem = null;
        string? Text(Field field, string path)
        {
            switch (_states[(int)field])
            {
                case Present:
                    return Encoding.UTF8.GetString(Bytes(field));
                case Missing:
                    return null;
                default:
                    problem ??= path + " isn't a short text";
                    return null;
            }
        }
        long? Count(Field field, string path, bool required)
        {
            switch (_states[(int)field])
            {
                case Present when Utf8Parser.TryParse(Bytes(field), out long value, out var used) && used == _lengths[(int)field] && value >= 0:
                    return value;
                case Missing when !required:
                    return null;
                case Missing:
                    problem ??= path + " missing";
                    return null;
                default:
                    problem ??= path + " isn't a count";
                    return null;
            }
        }

        var timestamp = Text(Field.Timestamp, "timestamp");
        var request = Text(Field.RequestId, "requestId");
        var id = Text(Field.Id, "message.id");
        var model = Text(Field.Model, "message.model");
        if (!_hasMessage)
            problem ??= "message missing";
        else if (!_hasUsage)
            problem ??= "message.usage missing";
        var speed = Text(Field.Speed, "message.usage.speed");
        var input = Count(Field.Input, "message.usage.input_tokens", _hasUsage);
        var output = Count(Field.Output, "message.usage.output_tokens", _hasUsage);
        var write = Count(Field.CacheWrite, "message.usage.cache_creation_input_tokens", false);
        var read = Count(Field.CacheRead, "message.usage.cache_read_input_tokens", false);
        var write5m = Count(Field.Write5m, "message.usage.cache_creation.ephemeral_5m_input_tokens", false);
        var write1h = Count(Field.Write1h, "message.usage.cache_creation.ephemeral_1h_input_tokens", false);
        return new UsageLine(timestamp, id, request, model, speed, input ?? 0, write ?? 0, read ?? 0, output ?? 0, write5m, write1h, problem);
    }
}
