using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace InfoPanel.Core.Tests.Images;

/// <summary>
/// A minimal RTSP camera on 127.0.0.1 for the <c>rtsp</c> provider's tests: OPTIONS, DESCRIBE (an SDP for one H.264
/// track), SETUP over TCP only (interleaved; any other transport is answered 461), PLAY, GET_PARAMETER and TEARDOWN. After
/// PLAY it sends the H.264 fixture's frames (<c>clip-h264.mp4</c>, frame i one solid colour, <see cref="VideoFixtures"/>)
/// as RTP packets interleaved on the same connection, looping at <see cref="Fps"/>, each frame's send time recorded.
/// <see cref="Drop"/> ends every connection and refuses new ones until <see cref="Resume"/>, as a camera that drops off
/// the network does. Nothing listens beyond loopback.
/// <para>
/// A test can make it hostile or strict: answer a request with text of its own (<see cref="Raw"/>), flood FU-A fragments
/// after PLAY (<see cref="Flood"/>), switch every connection to a second fixture (a larger picture) after some frames,
/// or have each connection alternate between the two, hold back the first frame on some paths (<see cref="Slow"/>),
/// check that each request names the camera's own address (<see cref="Authority"/>) and ask for Digest
/// (<see cref="Digest"/>, with MD5 or another <see cref="DigestAlgorithm"/>, beside any <see cref="OtherChallenge"/>, in
/// headers of their own or in one, <see cref="OneHeader"/>), as a camera that validates its requests does, describe
/// its track as H.265 (<see cref="Hevc"/>), or answer with another CSeq than the request's, or none (<see cref="EchoCSeq"/>).
/// </para>
/// </summary>
internal sealed class RtspTestServer : IAsyncDisposable
{
    private const int MaxPayload = 1400;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<TcpClient> _clients = [];
    private readonly List<string> _requests = [];
    private readonly List<(int Frame, long SentAt)> _sent = [];
    private readonly Task _accepting;
    private readonly byte[] _sps, _pps;
    private readonly List<byte[][]> _frames;
    private readonly List<byte[][]>? _switch;
    private readonly int _switchAfter;
    private readonly bool _alternate;
    private readonly List<string> _authorizations = [];
    private readonly string _nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    private bool _dropped;
    private int _connections;
    private int _framesSent;
    private int _challenges;

    /// <param name="fps">Frames a second.</param>
    /// <param name="fixture">The video fixture whose frames it sends.</param>
    /// <param name="switchTo">A second fixture every connection sends instead once <paramref name="switchAfter"/> frames have gone out.</param>
    /// <param name="switchAfter">Frames sent (by every connection together) before the switch.</param>
    /// <param name="alternate">
    /// Instead of switching once: each connection starts with <paramref name="fixture"/> and moves to the other fixture
    /// every <paramref name="switchAfter"/> frames it sent.
    /// </param>
    public RtspTestServer(int fps = 10, string fixture = "clip-h264.mp4", string? switchTo = null, int switchAfter = 0, bool alternate = false)
    {
        Fps = fps;
        (_sps, _pps, _frames) = ReadFixture(VideoFixtures.PathOf(fixture));
        if (switchTo is not null) _switch = ReadFixture(VideoFixtures.PathOf(switchTo)).Frames;
        _switchAfter = switchAfter;
        _alternate = alternate;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    /// <summary>
    /// The text a request is answered with instead of the camera's own answer, as it is, when this gives one: told the
    /// method and the request's CSeq.
    /// </summary>
    public Func<string, string, string?>? Raw { get; set; }

    /// <summary>
    /// After PLAY, FU-A fragments of one NAL unit that never ends (no end bit), 1,400 bytes each, as fast as the
    /// connection takes them, on <c>Channel</c>, each with the RTP marker bit when <c>Marker</c>: never a whole frame.
    /// </summary>
    public (int Channel, bool Marker)? Flood { get; set; }

    /// <summary>
    /// The camera's own address (scheme and authority, <c>rtsp://camera.test:8554</c>): a request whose target doesn't start
    /// with it is answered 400, and the Content-Base it gives starts with it.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>A user name and password every request must carry by Digest (qop auth), else 401 with a challenge.</summary>
    public (string User, string Password)? Digest { get; set; }

    /// <summary>The Digest challenge's algorithm: <c>MD5</c>, <c>SHA-256</c>, or one nothing here answers (none is accepted).</summary>
    public string DigestAlgorithm { get; set; } = "MD5";

    /// <summary>Another <c>WWW-Authenticate</c> value a 401 offers after the Digest one (<c>Basic realm="camera"</c>); never accepted.</summary>
    public string? OtherChallenge { get; set; }

    /// <summary>
    /// When set, the Digest challenge and <see cref="OtherChallenge"/> in one <c>WWW-Authenticate</c> header, as RFC 9110
    /// allows: the other first when true, the Digest first when false.
    /// </summary>
    public bool? OneHeader { get; set; }

    /// <summary>The track described as H.265 (<c>H265/90000</c>) instead of H.264.</summary>
    public bool Hevc { get; set; }

    /// <summary>
    /// The CSeq its own answers carry, told the request's: null leaves the header out, as some cameras do. Unset, the
    /// request's own.
    /// </summary>
    public Func<string, string?>? EchoCSeq { get; set; }

    /// <summary>Every <c>Authorization</c> header a request carried, as it came.</summary>
    public IReadOnlyList<string> Authorizations
    {
        get
        {
            lock (_lock) return [.. _authorizations];
        }
    }

    /// <summary>A path (<c>/slow</c>, as DESCRIBE named it) whose stream sends its first frame only this long after PLAY, as a camera slow to start.</summary>
    public (string Path, TimeSpan After)? Slow { get; set; }

    /// <summary>The 401 challenges sent.</summary>
    public int Challenges => Volatile.Read(ref _challenges);

    public int Fps { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Url(string path = "/camera") => $"rtsp://127.0.0.1:{Port}{path}";

    /// <summary>A DESCRIBE is answered with this redirect instead (status and <c>Location</c>), when set.</summary>
    public (int Status, string Location)? Redirect { get; set; }

    /// <summary>Connections accepted and served (refused ones while dropped aren't counted).</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Every request line, each followed by its <c>Transport</c> header for a SETUP: "SETUP rtsp://… | RTP/AVP/TCP;…".</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_lock) return [.. _requests];
        }
    }

    /// <summary>When the newest send of frame <paramref name="frame"/> started (a <see cref="Stopwatch"/> timestamp), or null.</summary>
    public long? SentAt(int frame)
    {
        lock (_lock)
        {
            for (var i = _sent.Count - 1; i >= 0; i--)
                if (_sent[i].Frame == frame) return _sent[i].SentAt;
            return null;
        }
    }

    /// <summary>Ends every connection, and accepts and at once closes new ones until <see cref="Resume"/>.</summary>
    public void Drop()
    {
        lock (_lock)
        {
            _dropped = true;
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
    }

    public void Resume()
    {
        lock (_lock) _dropped = false;
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch
            {
                return;
            }
            lock (_lock)
            {
                if (_dropped)
                {
                    client.Dispose();
                    continue;
                }
                _clients.Add(client);
                _connections++;
            }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var stream = client.GetStream();
        var write = new SemaphoreSlim(1, 1);
        Task? playing = null;
        var described = "";
        var session = Random.Shared.Next(100_000, 999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            while (await ReadRequestAsync(stream, connection.Token) is { } request)
            {
                var (line, headers) = request;
                var parts = line.Split(' ');
                var method = parts[0];
                var target = parts.Length > 1 ? parts[1] : "";
                headers.TryGetValue("cseq", out var cseq);
                if (method.StartsWith("RTSP/", StringComparison.Ordinal)) continue; // the client's answer to a request of the camera's
                lock (_lock)
                {
                    _requests.Add(method == "SETUP" ? $"{line} | {headers.GetValueOrDefault("transport")}" : line);
                    if (headers.GetValueOrDefault("authorization") is { } authorization) _authorizations.Add(authorization);
                }
                string reply;
                if (Raw?.Invoke(method, cseq ?? "0") is { } raw)
                {
                    await Send(stream, write, Encoding.Latin1.GetBytes(raw), connection.Token);
                    continue;
                }
                if (Authority is { } authority && !target.StartsWith(authority + "/", StringComparison.Ordinal))
                {
                    await Send(stream, write, Encoding.ASCII.GetBytes(Reply(cseq, "400 Bad Request", "")), connection.Token);
                    continue;
                }
                if (Digest is { } digest && !Authorized(method, target, headers.GetValueOrDefault("authorization"), digest))
                {
                    Interlocked.Increment(ref _challenges);
                    var offered = $"Digest realm=\"camera\", nonce=\"{_nonce}\", qop=\"auth\", algorithm={DigestAlgorithm}";
                    var challenge = (OtherChallenge, OneHeader) switch
                    {
                        ({ } other, true) => $"WWW-Authenticate: {other}, {offered}\r\n",
                        ({ } other, false) => $"WWW-Authenticate: {offered}, {other}\r\n",
                        ({ } other, null) => $"WWW-Authenticate: {offered}\r\nWWW-Authenticate: {other}\r\n",
                        _ => $"WWW-Authenticate: {offered}\r\n",
                    };
                    await Send(stream, write, Encoding.ASCII.GetBytes(Reply(cseq, "401 Unauthorized", challenge)), connection.Token);
                    continue;
                }
                switch (method)
                {
                    case "OPTIONS":
                        reply = Reply(cseq, "200 OK", "Public: OPTIONS, DESCRIBE, SETUP, PLAY, GET_PARAMETER, TEARDOWN\r\n");
                        break;
                    case "DESCRIBE" when Redirect is { } redirect:
                        reply = Reply(cseq, $"{redirect.Status} Moved", $"Location: {redirect.Location}\r\n");
                        break;
                    case "DESCRIBE":
                        described = target;
                        var sdp = Sdp();
                        var contentBase = Authority is null ? Url() : target;
                        reply = Reply(cseq, "200 OK", $"Content-Base: {contentBase}/\r\nContent-Type: application/sdp\r\nContent-Length: {sdp.Length}\r\n") + sdp;
                        break;
                    case "SETUP" when headers.GetValueOrDefault("transport") is { } t && t.StartsWith("RTP/AVP/TCP", StringComparison.Ordinal) && t.Contains("interleaved"):
                        reply = Reply(cseq, "200 OK", $"Transport: RTP/AVP/TCP;unicast;interleaved=0-1\r\nSession: {session};timeout=60\r\n");
                        break;
                    case "SETUP":
                        reply = Reply(cseq, "461 Unsupported Transport", "");
                        break;
                    case "PLAY":
                        reply = Reply(cseq, "200 OK", $"Session: {session}\r\nRange: npt=0.000-\r\n");
                        break;
                    case "TEARDOWN":
                        reply = Reply(cseq, "200 OK", $"Session: {session}\r\n");
                        break;
                    default:
                        reply = Reply(cseq, "200 OK", $"Session: {session}\r\n");
                        break;
                }
                await Send(stream, write, Encoding.ASCII.GetBytes(reply), connection.Token);
                if (method == "PLAY" && playing is null) playing = PlayAsync(stream, write, described, connection.Token);
                if (method == "TEARDOWN") break;
            }
        }
        catch
        {
            // The connection dropped or the server stopped.
        }
        finally
        {
            await connection.CancelAsync();
            lock (_lock) _clients.Remove(client);
        }
        if (playing is not null)
        {
            try
            {
                await playing;
            }
            catch
            {
                // Ended with the connection.
            }
        }
    }

    /// <summary>Whether <paramref name="authorization"/> is a Digest answer to this camera's challenge for the request.</summary>
    private bool Authorized(string method, string target, string? authorization, (string User, string Password) digest)
    {
        if (authorization is null || !authorization.StartsWith("Digest ", StringComparison.Ordinal)) return false;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in authorization[7..].Split(','))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) fields[part[..eq].Trim()] = part[(eq + 1)..].Trim().Trim('"');
        }
        Func<byte[], byte[]>? hash = DigestAlgorithm switch
        {
            "MD5" => MD5.HashData,
            "SHA-256" => SHA256.HashData,
            _ => null,
        };
        if (hash is null) return false;
        string H(string text) => Convert.ToHexString(hash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        if (fields.GetValueOrDefault("username") != digest.User || fields.GetValueOrDefault("realm") != "camera"
            || fields.GetValueOrDefault("nonce") != _nonce || fields.GetValueOrDefault("uri") != target
            || fields.GetValueOrDefault("algorithm") != DigestAlgorithm
            || fields.GetValueOrDefault("qop") != "auth" || !fields.TryGetValue("nc", out var nc) || !fields.TryGetValue("cnonce", out var cnonce))
            return false;
        var expected = H($"{H($"{digest.User}:camera:{digest.Password}")}:{_nonce}:{nc}:{cnonce}:auth:{H($"{method}:{target}")}");
        return fields.GetValueOrDefault("response") == expected;
    }

    private string Reply(string? cseq, string status, string headers)
    {
        var echoed = EchoCSeq is { } echo ? echo(cseq ?? "0") : cseq ?? "0";
        return echoed is null ? $"RTSP/1.0 {status}\r\n{headers}\r\n" : $"RTSP/1.0 {status}\r\nCSeq: {echoed}\r\n{headers}\r\n";
    }

    private string Sdp()
    {
        if (Hevc)
        {
            // An H.265 track, its parameter sets left to the stream (as RFC 7798 allows).
            return "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=Test camera\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\na=control:*\r\n"
                + "m=video 0 RTP/AVP 96\r\na=rtpmap:96 H265/90000\r\na=control:track1\r\n";
        }
        var profile = Convert.ToHexString(_sps.AsSpan(1, 3)).ToLowerInvariant();
        return "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=Test camera\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\na=control:*\r\n"
            + "m=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\n"
            + $"a=fmtp:96 packetization-mode=1;profile-level-id={profile};sprop-parameter-sets={Convert.ToBase64String(_sps)},{Convert.ToBase64String(_pps)}\r\n"
            + "a=control:track1\r\n";
    }

    /// <summary>The fixture's frames as RTP over the connection, looping, one every 1/<see cref="Fps"/> s.</summary>
    private async Task PlayAsync(Stream stream, SemaphoreSlim write, string target, CancellationToken token)
    {
        if (Flood is { } flood)
        {
            await FloodAsync(stream, write, flood.Channel, flood.Marker, token);
            return;
        }
        if (Slow is { } slow && target.Contains(slow.Path, StringComparison.Ordinal)) await Task.Delay(slow.After, token);
        ushort sequence = 0;
        uint timestamp = 0;
        var clock = Stopwatch.StartNew();
        var frames = _frames;
        var index = -1;
        for (long n = 0; !token.IsCancellationRequested; n++)
        {
            var due = TimeSpan.FromSeconds((double)n / Fps) - clock.Elapsed;
            if (due > TimeSpan.Zero) await Task.Delay(due, token);
            if (_alternate && _switch is not null && n > 0 && n % _switchAfter == 0)
            {
                frames = frames == _switch ? _frames : _switch; // from its first frame, an IDR with its parameter sets
                index = -1;
            }
            else if (!_alternate && _switch is not null && frames != _switch && Volatile.Read(ref _framesSent) >= _switchAfter)
            {
                frames = _switch; // from its first frame, an IDR with its parameter sets
                index = -1;
            }
            index = (index + 1) % frames.Count;
            var nals = frames[index];
            Interlocked.Increment(ref _framesSent);
            lock (_lock) _sent.Add((index, Stopwatch.GetTimestamp()));
            var packets = new List<byte[]>();
            foreach (var nal in nals) packets.AddRange(Packetize(nal));
            for (var i = 0; i < packets.Count; i++)
            {
                var payload = packets[i];
                var rtp = new byte[4 + 12 + payload.Length];
                rtp[0] = (byte)'$';
                rtp[1] = 0;
                BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), (ushort)(12 + payload.Length));
                rtp[4] = 0x80;
                rtp[5] = (byte)(96 | (i == packets.Count - 1 ? 0x80 : 0)); // the marker on a frame's last packet
                BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(6), sequence++);
                BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8), timestamp);
                BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(12), 0x1234_5678);
                payload.CopyTo(rtp.AsSpan(16));
                await Send(stream, write, rtp, token);
            }
            timestamp += (uint)(90_000 / Fps);
        }
    }

    /// <summary>FU-A fragments of a slice that never ends (see <see cref="Flood"/>).</summary>
    private static async Task FloodAsync(Stream stream, SemaphoreSlim write, int channel, bool marker, CancellationToken token)
    {
        var packet = new byte[4 + 12 + MaxPayload];
        packet[0] = (byte)'$';
        packet[1] = (byte)channel;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 12 + MaxPayload);
        packet[4] = 0x80;
        packet[5] = (byte)(96 | (marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), 0x1234_5678);
        packet.AsSpan(18).Fill(0x55);
        packet[16] = 0x60 | 28; // FU-A of a reference slice
        for (ushort sequence = 0; !token.IsCancellationRequested; sequence++)
        {
            packet[17] = (byte)(1 | (sequence == 0 ? 0x80 : 0)); // the start bit on the first, the end bit never
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), sequence);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0);
            await Send(stream, write, packet, token);
        }
    }

    /// <summary>One NAL unit as RTP payloads (RFC 6184): whole when it fits, else FU-A fragments.</summary>
    private static IEnumerable<byte[]> Packetize(byte[] nal)
    {
        if (nal.Length <= MaxPayload)
        {
            yield return nal;
            yield break;
        }
        var header = nal[0];
        for (var offset = 1; offset < nal.Length; offset += MaxPayload - 2)
        {
            var size = Math.Min(MaxPayload - 2, nal.Length - offset);
            var fu = new byte[2 + size];
            fu[0] = (byte)((header & 0xE0) | 28);
            fu[1] = (byte)((header & 0x1F) | (offset == 1 ? 0x80 : 0) | (offset + size == nal.Length ? 0x40 : 0));
            Array.Copy(nal, offset, fu, 2, size);
            yield return fu;
        }
    }

    private static async Task Send(Stream stream, SemaphoreSlim write, byte[] bytes, CancellationToken token)
    {
        await write.WaitAsync(token);
        try
        {
            await stream.WriteAsync(bytes, token);
        }
        finally
        {
            write.Release();
        }
    }

    /// <summary>A request: its line and headers (names in lower case); null when the connection ends.</summary>
    private static async Task<(string Line, Dictionary<string, string> Headers)?> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, token) == 0) return null;
            head.Append((char)one[0]);
            if (head.Length > 16384) return null;
        }
        var lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in lines.Skip(1))
        {
            var colon = l.IndexOf(':');
            if (colon > 0) headers[l[..colon].Trim().ToLowerInvariant()] = l[(colon + 1)..].Trim();
        }
        if (headers.TryGetValue("content-length", out var length) && int.TryParse(length, out var n) && n > 0)
            await stream.ReadExactlyAsync(new byte[n], token);
        return (lines[0], headers);
    }

    /// <summary>
    /// The fixture's H.264 parameter sets (its <c>avcC</c>) and frames (its <c>mdat</c>'s length-prefixed NAL units, a
    /// frame ending at each slice: one slice a frame).
    /// </summary>
    private static (byte[] Sps, byte[] Pps, List<byte[][]> Frames) ReadFixture(string path)
    {
        var file = File.ReadAllBytes(path);
        var avcc = file.AsSpan().IndexOf("avcC"u8) + 4;
        var lengthSize = (file[avcc + 4] & 3) + 1;
        var at = avcc + 6;
        var spsLength = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at));
        var sps = file.AsSpan(at + 2, spsLength).ToArray();
        at += 2 + spsLength + 1;
        var ppsLength = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at));
        var pps = file.AsSpan(at + 2, ppsLength).ToArray();

        // The top-level boxes, to the mdat.
        var box = 0;
        while (Encoding.ASCII.GetString(file, box + 4, 4) != "mdat") box += (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(box));
        var end = box + (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(box));
        var frames = new List<byte[][]>();
        var pending = new List<byte[]>();
        for (var p = box + 8; p + lengthSize <= end;)
        {
            var size = 0;
            for (var i = 0; i < lengthSize; i++) size = (size << 8) | file[p + i];
            var nal = file.AsSpan(p + lengthSize, size).ToArray();
            p += lengthSize + size;
            var type = nal[0] & 0x1F;
            if (type == 5) pending.InsertRange(0, [sps, pps]); // the parameter sets before each IDR, so a new viewer can start there
            pending.Add(nal);
            if (type is >= 1 and <= 5)
            {
                frames.Add([.. pending]);
                pending.Clear();
            }
        }
        return (sps, pps, frames);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        lock (_lock)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
        try
        {
            await _accepting;
        }
        catch
        {
            // Stopped.
        }
    }
}
