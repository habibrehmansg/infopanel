using System.Net.Sockets;
using System.Text;
using InfoPanel.Sensors.NowPlaying;
using Tmds.DBus.Protocol;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.NowPlaying;

/// <summary>
/// <see cref="MprisBus"/>'s reading of replies as they come off a real D-Bus connection (Tmds over a Unix socket to
/// <see cref="FakeDBusPeer"/>, which answers each call with the bytes a test gives): a long string, a long key and a long
/// skipped string are read without being copied whole (the parser's allocations measured on the thread it runs on); a
/// reply whose value is over <see cref="MprisBus.MaxReplyBytes"/> fails its call; a dictionary's entry budget counts every
/// entry sent, duplicates and refused keys included.
/// </summary>
public sealed class MprisBusWireTests
{
    private const string Player = "org.mpris.MediaPlayer2.wiretest";

    private static async Task<(T Value, long Allocated)> GetAllMeasured<T>(MprisBus bus, Func<Message, T> read)
    {
        return await bus.CallAsync(Player, MprisBus.ObjectPath, MprisBus.PropertiesInterface, "GetAll", MprisBus.PlayerInterface, null,
            (message, _) =>
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var value = read(message);
                return (value, GC.GetAllocatedBytesForCurrentThread() - before);
            });
    }

    private static async Task<MprisBus> Connected(FakeDBusPeer peer)
    {
        var bus = new MprisBus(peer.Address);
        await bus.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        return bus;
    }

    [Fact]
    public async Task Long_strings_and_keys_are_cut_or_skipped_as_they_are_read_never_copied_whole()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows: the fake peer listens on a Unix socket.");
        const int Long = 1_000_000;
        var title = new string('t', Long);
        var body = new Wire();
        body.Dict(
        [
            ("xesam:title", "s", w => w.Str(title)),
            (new string('k', Long), "s", w => w.Str("refused key's value")),
            ("infopanel:skipped", "(si)", w => { w.Align(8); w.Str(new string('s', Long)); w.U32(7); }),
            ("xesam:album", "s", w => w.Str("Kept Album")),
        ]);
        using var peer = new FakeDBusPeer(_ => FakeDBusPeer.Reply.Return("a{sv}", body.ToArray()));
        using var bus = await Connected(peer);

        var (entries, allocated) = await GetAllMeasured(bus, m => MprisBus.ReadGetAllReply(m, null));

        var kept = Assert.IsType<string>(entries["xesam:title"]);
        Assert.Equal(MprisBus.MaxStringBytes, kept.Length);
        Assert.Equal("Kept Album", entries["xesam:album"]);
        Assert.Null(entries["infopanel:skipped"]);
        Assert.Equal(3, entries.Count);
        Assert.True(allocated < 256 * 1024, $"the parser allocated {allocated} bytes for a reply of {3 * Long} bytes of strings");
    }

    [Fact]
    public async Task A_reply_whose_value_is_over_its_budget_fails_its_call()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows: the fake peer listens on a Unix socket.");
        var over = new string('x', MprisBus.MaxReplyBytes + 1);
        var all = new Wire();
        all.Dict([("xesam:title", "s", w => w.Str(over))]);
        var one = new Wire();
        one.Variant("s", w => w.Str(over));
        using var peer = new FakeDBusPeer(member => member == "GetAll"
            ? FakeDBusPeer.Reply.Return("a{sv}", all.ToArray())
            : FakeDBusPeer.Reply.Return("v", one.ToArray()));
        using var bus = await Connected(peer);

        await Assert.ThrowsAsync<InvalidDataException>(() => bus.GetAllAsync(Player, MprisBus.PlayerInterface));
        await Assert.ThrowsAsync<InvalidDataException>(() => bus.GetAsync(Player, MprisBus.PlayerInterface, "PlaybackStatus"));
    }

    [Fact]
    public async Task Every_entry_sent_counts_against_the_entry_budget_duplicates_and_refused_keys_too()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows: the fake peer listens on a Unix socket.");
        var duplicates = new Wire();
        duplicates.Dict(Enumerable.Range(0, 1000).Select(i => ("xesam:title", "s", (Action<Wire>)(w => w.Str("t" + i)))).ToArray());
        var refused = new Wire();
        refused.Dict(Enumerable.Range(0, 1000).Select(i => (new string('k', 300), "s", (Action<Wire>)(w => w.Str("v" + i))))
            .Append(("xesam:album", "s", w => w.Str("Past The Budget"))).ToArray());
        var replies = new Queue<byte[]>([duplicates.ToArray(), refused.ToArray()]);
        using var peer = new FakeDBusPeer(_ =>
        {
            lock (replies)
                return FakeDBusPeer.Reply.Return("a{sv}", replies.Dequeue());
        });
        using var bus = await Connected(peer);

        var first = await bus.GetAllAsync(Player, MprisBus.PlayerInterface);
        Assert.Equal("t" + (MprisBus.MaxEntries - 1), first["xesam:title"]);

        var second = await bus.GetAllAsync(Player, MprisBus.PlayerInterface);
        Assert.Empty(second);
    }
}

/// <summary>D-Bus's little-endian wire format, for the tests' replies: alignment relative to the start of the bytes.</summary>
internal sealed class Wire
{
    private readonly List<byte> _bytes = [];

    public int Length => _bytes.Count;

    public byte[] ToArray() => [.. _bytes];

    public void Align(int n)
    {
        while (_bytes.Count % n != 0)
            _bytes.Add(0);
    }

    public void Byte(byte b) => _bytes.Add(b);

    public void U32(uint value)
    {
        Align(4);
        _bytes.AddRange(BitConverter.GetBytes(value));
    }

    public void Str(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        U32((uint)utf8.Length);
        _bytes.AddRange(utf8);
        _bytes.Add(0);
    }

    public void Sig(string signature)
    {
        _bytes.Add((byte)signature.Length);
        _bytes.AddRange(Encoding.ASCII.GetBytes(signature));
        _bytes.Add(0);
    }

    public void Variant(string signature, Action<Wire> value)
    {
        Sig(signature);
        value(this);
    }

    /// <summary>An array: its length, then (aligned to <paramref name="elementAlignment"/>) its content.</summary>
    public void Array(int elementAlignment, Action<Wire> content)
    {
        U32(0);
        var at = _bytes.Count - 4;
        Align(elementAlignment);
        var start = _bytes.Count;
        content(this);
        var length = BitConverter.GetBytes((uint)(_bytes.Count - start));
        for (var i = 0; i < 4; i++)
            _bytes[at + i] = length[i];
    }

    /// <summary>An <c>a{sv}</c> of these entries, in this order.</summary>
    public void Dict((string Key, string Signature, Action<Wire> Value)[] entries) => Array(8, w =>
    {
        foreach (var (key, signature, value) in entries)
        {
            w.Align(8);
            w.Str(key);
            w.Variant(signature, value);
        }
    });
}

/// <summary>
/// A D-Bus peer on a Unix socket for the tests: it accepts one client, answers its authentication (no descriptor
/// passing) and its <c>Hello</c>, then answers each method call with what <c>answer</c> gives for its member.
/// </summary>
internal sealed class FakeDBusPeer : IDisposable
{
    public sealed record Reply(string Signature, byte[] Body, string? ErrorName)
    {
        public static Reply Return(string signature, byte[] body) => new(signature, body, null);
    }

    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ipdb-" + Guid.NewGuid().ToString("N")[..12] + ".sock");
    private readonly Func<string, Reply> _answer;
    private uint _serial;

    public string Address => "unix:path=" + _path;

    public FakeDBusPeer(Func<string, Reply> answer)
    {
        _answer = answer;
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        _listener.Listen(1);
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _listener.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    private async Task ServeAsync()
    {
        try
        {
            using var client = await _listener.AcceptAsync();
            await using var stream = new NetworkStream(client, ownsSocket: false);
            var nul = new byte[1];
            await stream.ReadExactlyAsync(nul);
            while (true)
            {
                var line = await ReadLineAsync(stream);
                if (line.StartsWith("AUTH", StringComparison.Ordinal))
                    await WriteAsync(stream, "OK 0123456789abcdef0123456789abcdef\r\n");
                else if (line.StartsWith("BEGIN", StringComparison.Ordinal))
                    break;
                else
                    await WriteAsync(stream, "ERROR\r\n");
            }

            while (true)
            {
                var start = new byte[16];
                await stream.ReadExactlyAsync(start);
                var bodyLength = BitConverter.ToUInt32(start, 4);
                var serial = BitConverter.ToUInt32(start, 8);
                var fieldsLength = (int)BitConverter.ToUInt32(start, 12);
                var fields = new byte[(fieldsLength + 7) / 8 * 8];
                await stream.ReadExactlyAsync(fields);
                await stream.ReadExactlyAsync(new byte[bodyLength]);
                var member = MemberOf(fields, fieldsLength);
                var hello = new Wire();
                hello.Str(":1.1");
                var reply = member == "Hello" ? Reply.Return("s", hello.ToArray()) : _answer(member ?? "");
                await stream.WriteAsync(Message(reply, serial));
            }
        }
        catch (Exception)
        {
            // The client went away, or the peer was disposed.
        }
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(one);
            if (one[0] == '\n')
                return line.ToString();
            line.Append((char)one[0]);
        }
    }

    private static Task WriteAsync(NetworkStream stream, string line) => stream.WriteAsync(Encoding.ASCII.GetBytes(line)).AsTask();

    /// <summary>The MEMBER header field of a message's header fields (which start at offset 16).</summary>
    private static string? MemberOf(byte[] fields, int length)
    {
        var at = 0;
        int Align(int n) => (n - (16 + at) % n) % n;
        string? member = null;
        while (at < length)
        {
            at += Align(8);
            var code = fields[at++];
            var signatureLength = fields[at++];
            var signature = (char)fields[at];
            at += signatureLength + 1;
            switch (signature)
            {
                case 's' or 'o':
                    at += Align(4);
                    var stringLength = (int)BitConverter.ToUInt32(fields, at);
                    at += 4;
                    if (code == 3)
                        member = Encoding.UTF8.GetString(fields, at, stringLength);
                    at += stringLength + 1;
                    break;
                case 'g':
                    at += fields[at] + 2;
                    break;
                case 'u':
                    at += Align(4) + 4;
                    break;
                default:
                    throw new InvalidDataException("A header field the fake peer doesn't read.");
            }
        }

        return member;
    }

    private byte[] Message(Reply reply, uint replySerial)
    {
        var message = new Wire();
        message.Byte((byte)'l');
        message.Byte(reply.ErrorName is null ? (byte)2 : (byte)3);
        message.Byte(1);
        message.Byte(1);
        message.U32((uint)reply.Body.Length);
        message.U32(++_serial);
        message.Array(8, w =>
        {
            w.Align(8);
            w.Byte(5);
            w.Sig("u");
            w.U32(replySerial);
            if (reply.ErrorName is { } error)
            {
                w.Align(8);
                w.Byte(4);
                w.Sig("s");
                w.Str(error);
            }

            if (reply.Body.Length > 0)
            {
                w.Align(8);
                w.Byte(8);
                w.Sig("g");
                w.Sig(reply.Signature);
            }
        });
        message.Align(8);
        return [.. message.ToArray(), .. reply.Body];
    }
}
