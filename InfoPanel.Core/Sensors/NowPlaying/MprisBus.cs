using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace InfoPanel.Sensors.NowPlaying;

/// <summary>
/// The session bus as <see cref="LinuxNowPlayingReader"/> uses it (a fake in tests): the MPRIS players' names and their
/// properties. A property's value is what <see cref="MprisBus"/> keeps of it: a <see cref="string"/>, a list of strings
/// (<see cref="IReadOnlyList{T}"/> of <see cref="string"/>), a <see cref="long"/> (every integer type), a
/// <see cref="double"/>, a <see cref="bool"/>, a dictionary of the same (<see cref="IReadOnlyDictionary{TKey, TValue}"/>,
/// a player's <c>Metadata</c>), else null. Every call may throw; the reader treats any exception as that call failing.
/// </summary>
internal interface IMprisBus : IDisposable
{
    Task ConnectAsync();

    /// <summary>The bus names of the MPRIS players on the bus (<c>org.mpris.MediaPlayer2.*</c>), in the bus's order.</summary>
    Task<IReadOnlyList<string>> ListPlayersAsync();

    /// <summary>One property of the player <paramref name="name"/>'s object <c>/org/mpris/MediaPlayer2</c>.</summary>
    Task<object?> GetAsync(string name, string iface, string property);

    /// <summary>Every property of an interface of the player <paramref name="name"/>'s object <c>/org/mpris/MediaPlayer2</c>.</summary>
    Task<IReadOnlyDictionary<string, object?>> GetAllAsync(string name, string iface);
}

/// <summary>
/// <see cref="IMprisBus"/> over a D-Bus connection (<c>Tmds.DBus.Protocol</c>). Answers are read from the message as it
/// arrived, bounded as they are read: a string keeps at most <see cref="MaxStringBytes"/> of its UTF-8 bytes (so at least
/// <see cref="NowPlayingSource.MaxTextChars"/> characters), a list at most <see cref="MaxListItems"/> strings, a dictionary
/// at most <see cref="MaxEntries"/> entries as sent (a repeated or refused key counts); a byte array (cover art some players
/// put in their metadata) or any other value it doesn't keep is skipped over without being copied. Strings and keys are
/// read byte by byte from the message's own buffers up to their bound, so no string is copied whole. A reply holding a
/// string or array longer than <see cref="MaxReplyBytes"/> fails its call, as does a malformed one. What this can't bound:
/// the connection holds a whole message before it is read (as large as the bus lets a message be), and an error reply's
/// text is read whole by the connection itself; the reader drops it unread.
/// </summary>
internal sealed class MprisBus(string address) : IMprisBus
{
    public const string NamePrefix = "org.mpris.MediaPlayer2.";

    public const string ObjectPath = "/org/mpris/MediaPlayer2";

    public const string RootInterface = "org.mpris.MediaPlayer2";

    public const string PlayerInterface = "org.mpris.MediaPlayer2.Player";

    /// <summary>At most this many players are listed.</summary>
    public const int MaxPlayers = 16;

    /// <summary>A string keeps at most this many of its UTF-8 bytes: at least <see cref="NowPlayingSource.MaxTextChars"/> characters.</summary>
    public const int MaxStringBytes = 4 * NowPlayingSource.MaxTextChars;

    public const int MaxListItems = 64;

    public const int MaxEntries = 64;

    /// <summary>A dictionary's key longer than this is skipped with its value.</summary>
    private const int MaxKeyBytes = 256;

    /// <summary>Variants within variants, and dictionaries within dictionaries, are read this deep at most.</summary>
    private const int MaxDepth = 4;

    /// <summary>A reply whose value (a string or an array) is longer than this is refused: its call fails.</summary>
    public const int MaxReplyBytes = 4 * 1024 * 1024;

    private readonly DBusConnection _connection = new(new DBusConnectionOptions(address) { AutoConnect = false });

    public async Task ConnectAsync() => await _connection.ConnectAsync().ConfigureAwait(false);

    public Task<IReadOnlyList<string>> ListPlayersAsync() =>
        CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListNames", null, null, ReadListNamesReply);

    public Task<object?> GetAsync(string name, string iface, string property) =>
        CallAsync(name, ObjectPath, PropertiesInterface, "Get", iface, property, ReadGetReply);

    public Task<IReadOnlyDictionary<string, object?>> GetAllAsync(string name, string iface) =>
        CallAsync(name, ObjectPath, PropertiesInterface, "GetAll", iface, null, ReadGetAllReply);

    public const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    /// <summary>A method call with no, one or two string arguments, its reply read by <paramref name="read"/> (the tests' seam too).</summary>
    internal Task<T> CallAsync<T>(string destination, string path, string iface, string member, string? first, string? second, MessageValueReader<T> read)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination, path, iface, member, first is null ? null : second is null ? "s" : "ss");
        if (first is not null)
            writer.WriteString(first);
        if (second is not null)
            writer.WriteString(second);
        return _connection.CallMethodAsync(writer.CreateMessage(), read);
    }

    /// <summary><c>ListNames</c>' reply: the MPRIS players' names.</summary>
    internal static IReadOnlyList<string> ReadListNamesReply(Message message, object? _) => ReadPlayers(message.GetBodyReader());

    /// <summary><c>Get</c>'s reply: one variant.</summary>
    internal static object? ReadGetReply(Message message, object? _)
    {
        var reader = message.GetBodyReader();
        return ReadVariant(ref reader, 0);
    }

    /// <summary><c>GetAll</c>'s reply: an <c>a{sv}</c>.</summary>
    internal static IReadOnlyDictionary<string, object?> ReadGetAllReply(Message message, object? _)
    {
        var reader = message.GetBodyReader();
        return ReadDictionary(ref reader, 0);
    }

    public void Dispose() => _connection.Dispose();

    private static IReadOnlyList<string> ReadPlayers(Reader reader)
    {
        var names = new List<string>();
        CheckLength(reader);
        var end = reader.ReadArrayStart(DBusType.String);
        while (reader.HasNext(end))
        {
            // A bus name is at most 255 bytes: a longer one is skipped over.
            if (ReadBounded(ref reader, 255) is { } name && names.Count < MaxPlayers && name.Length > NamePrefix.Length
                && name.StartsWith(NamePrefix, StringComparison.Ordinal))
                names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// An <c>a{sv}</c>, each value as <see cref="ReadValue"/> keeps it. At most <see cref="MaxEntries"/> entries are read,
    /// counted as sent (a repeated key, or one refused for its length, counts too); the rest of the array is skipped.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ReadDictionary(ref Reader reader, int depth)
    {
        var entries = new Dictionary<string, object?>(StringComparer.Ordinal);
        CheckLength(reader);
        var end = reader.ReadArrayStart(DBusType.DictEntry);
        var sent = 0;
        while (reader.HasNext(end))
        {
            if (sent++ >= MaxEntries)
            {
                reader.SkipTo(end);
                break;
            }

            reader.AlignStruct();
            if (ReadBounded(ref reader, MaxKeyBytes) is not { } name)
            {
                SkipVariant(ref reader, depth);
                continue;
            }

            entries[name] = ReadVariant(ref reader, depth);
        }

        return entries;
    }

    /// <summary>The length a string or array starts with, read ahead (the reader doesn't move): over <see cref="MaxReplyBytes"/>, the reply is refused.</summary>
    private static void CheckLength(Reader ahead)
    {
        if (ahead.ReadUInt32() > MaxReplyBytes)
            throw new InvalidDataException("A reply's value is over its budget.");
    }

    /// <summary>
    /// A string (or object path) whose UTF-8 is at most <paramref name="maxBytes"/>, else null; either way the reader is
    /// past it. Its bytes are read one by one from the message as it arrived (a string is an array of bytes and a NUL), so
    /// nothing longer than the bound is copied, wherever the message's buffers split it.
    /// </summary>
    private static string? ReadBounded(ref Reader reader, int maxBytes)
    {
        Span<byte> kept = stackalloc byte[MaxStringBytes];
        CheckLength(reader);
        var end = reader.ReadArrayStart(DBusType.Byte);
        var length = 0;
        while (length <= maxBytes && length < kept.Length && reader.HasNext(end))
            kept[length++] = reader.ReadByte();
        var whole = !reader.HasNext(end);
        reader.SkipTo(end);
        reader.ReadByte();
        return whole && length <= maxBytes ? Encoding.UTF8.GetString(kept[..length]) : null;
    }

    /// <summary>A string (or object path): at most <see cref="MaxStringBytes"/> of its UTF-8, decoded (as <see cref="Text"/>), the rest skipped over in place.</summary>
    private static string ReadText(ref Reader reader)
    {
        Span<byte> kept = stackalloc byte[MaxStringBytes];
        CheckLength(reader);
        var end = reader.ReadArrayStart(DBusType.Byte);
        var length = 0;
        while (length < kept.Length && reader.HasNext(end))
            kept[length++] = reader.ReadByte();
        reader.SkipTo(end);
        reader.ReadByte();
        return Text(kept[..length]);
    }

    private static void SkipString(ref Reader reader)
    {
        CheckLength(reader);
        reader.SkipTo(reader.ReadArrayStart(DBusType.Byte));
        reader.ReadByte();
    }

    private static object? ReadVariant(ref Reader reader, int depth)
    {
        var signature = reader.ReadSignatureAsSpan();
        return ReadValue(ref reader, signature, depth);
    }

    private static void SkipVariant(ref Reader reader, int depth)
    {
        var signature = reader.ReadSignatureAsSpan();
        Skip(ref reader, signature, depth);
    }

    /// <summary>A value of the single complete type <paramref name="signature"/>, as this bus keeps it (null: skipped).</summary>
    internal static object? ReadValue(ref Reader reader, ReadOnlySpan<byte> signature, int depth)
    {
        if (signature.Length == 0 || CompleteTypeLength(signature) != signature.Length)
            throw new InvalidDataException("A variant's signature isn't one complete type.");
        switch (signature[0])
        {
            case (byte)'s':
            case (byte)'o':
                return ReadText(ref reader);
            case (byte)'b':
                return reader.ReadBool();
            case (byte)'y':
                return (long)reader.ReadByte();
            case (byte)'n':
                return (long)reader.ReadInt16();
            case (byte)'q':
                return (long)reader.ReadUInt16();
            case (byte)'i':
                return (long)reader.ReadInt32();
            case (byte)'u':
                return (long)reader.ReadUInt32();
            case (byte)'x':
                return reader.ReadInt64();
            case (byte)'t':
                var unsigned = reader.ReadUInt64();
                return unsigned <= long.MaxValue ? (long)unsigned : (double)unsigned;
            case (byte)'d':
                return reader.ReadDouble();
            case (byte)'v' when depth < MaxDepth:
                return ReadVariant(ref reader, depth + 1);
            case (byte)'a' when signature.SequenceEqual("as"u8) || signature.SequenceEqual("ao"u8):
                return ReadStrings(ref reader, signature[1]);
            case (byte)'a' when signature.SequenceEqual("a{sv}"u8) && depth < MaxDepth:
                return ReadDictionary(ref reader, depth + 1);
            default:
                Skip(ref reader, signature, depth);
                return null;
        }
    }

    private static IReadOnlyList<string> ReadStrings(ref Reader reader, byte element)
    {
        var items = new List<string>();
        CheckLength(reader);
        var end = reader.ReadArrayStart((DBusType)element);
        while (reader.HasNext(end))
        {
            if (items.Count >= MaxListItems)
            {
                reader.SkipTo(end);
                break;
            }

            items.Add(ReadText(ref reader));
        }

        return items;
    }

    /// <summary>Reads past a value of the single complete type <paramref name="signature"/>, copying nothing of an array.</summary>
    private static void Skip(ref Reader reader, ReadOnlySpan<byte> signature, int depth)
    {
        switch (signature[0])
        {
            case (byte)'a':
                var element = signature[1] switch
                {
                    (byte)'{' => DBusType.DictEntry,
                    (byte)'(' => DBusType.Struct,
                    var e => (DBusType)e,
                };
                CheckLength(reader);
                reader.SkipTo(reader.ReadArrayStart(element));
                return;
            case (byte)'(':
                reader.AlignStruct();
                var members = signature[1..^1];
                while (members.Length > 0)
                {
                    var length = CompleteTypeLength(members);
                    Skip(ref reader, members[..length], depth);
                    members = members[length..];
                }

                return;
            case (byte)'v':
                if (depth >= MaxDepth)
                    throw new InvalidDataException("Variants nest too deep.");
                SkipVariant(ref reader, depth + 1);
                return;
            case (byte)'s':
            case (byte)'o':
                SkipString(ref reader);
                return;
            case (byte)'g':
                reader.ReadSignatureAsSpan();
                return;
            case (byte)'b':
                reader.ReadBool();
                return;
            case (byte)'y':
                reader.ReadByte();
                return;
            case (byte)'n':
            case (byte)'q':
                reader.ReadUInt16();
                return;
            case (byte)'i':
            case (byte)'u':
            case (byte)'h':
                reader.ReadUInt32();
                return;
            case (byte)'x':
            case (byte)'t':
            case (byte)'d':
                reader.ReadUInt64();
                return;
            default:
                throw new InvalidDataException("A value of a type D-Bus doesn't have.");
        }
    }

    /// <summary>The length of the single complete type at the start of <paramref name="signature"/>.</summary>
    internal static int CompleteTypeLength(ReadOnlySpan<byte> signature)
    {
        if (signature.Length == 0)
            throw new InvalidDataException("A signature ends inside a type.");
        switch (signature[0])
        {
            case (byte)'a':
                return 1 + CompleteTypeLength(signature[1..]);
            case (byte)'(':
            case (byte)'{':
                var close = signature[0] == (byte)'(' ? (byte)')' : (byte)'}';
                var at = 1;
                while (at < signature.Length && signature[at] != close)
                    at += CompleteTypeLength(signature[at..]);
                if (at >= signature.Length || at == 1)
                    throw new InvalidDataException("A signature's struct isn't closed.");
                return at + 1;
            case (byte)'y' or (byte)'b' or (byte)'n' or (byte)'q' or (byte)'i' or (byte)'u' or (byte)'x' or (byte)'t'
                or (byte)'d' or (byte)'h' or (byte)'s' or (byte)'o' or (byte)'g' or (byte)'v':
                return 1;
            default:
                throw new InvalidDataException("A signature names a type D-Bus doesn't have.");
        }
    }

    /// <summary>At most <see cref="MaxStringBytes"/> of a string's UTF-8, decoded (a sequence cut short decodes as U+FFFD, past every whole character kept).</summary>
    internal static string Text(ReadOnlySpan<byte> utf8) =>
        Encoding.UTF8.GetString(utf8.Length <= MaxStringBytes ? utf8 : utf8[..MaxStringBytes]);
}
