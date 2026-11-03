using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Images.Video;

/// <summary>
/// The one connection FFmpeg's RTSP client makes, relayed to a camera connection the provider made itself (resolved,
/// checked by the address rules and, for <c>rtsps</c>, secured with the system's trust): FFmpeg is given a
/// <c>rtsp://127.0.0.1:{port}</c> address, so it never resolves or connects to anything else. A listener on loopback
/// accepts that one connection, then closes.
/// <para>
/// Both ways, the relay reads every message whole and passes on only what it understood, written out again in one
/// form, so FFmpeg reads exactly what the relay checked. A message is a start line (a status line
/// <c>RTSP/1.0 NNN reason</c>, or a request line <c>METHOD target RTSP/1.0</c>), header lines of a name, a colon and a
/// value, an empty line, and a body of its one <c>Content-Length</c>; lines end at a line feed, with a carriage return
/// allowed only just before it. Anything else from the camera ends the relay: another control character, a line over
/// 4,000 bytes (FFmpeg reads 4,095 and drops the rest), a header line that doesn't start with a letter, a second
/// <c>Content-Length</c> or <c>Transport</c>, a header over 64 KiB or a body over 1 MiB, and every redirect (a 3xx answer,
/// or the camera's own REDIRECT request), which FFmpeg would follow to an address nobody checked.
/// </para>
/// <para>
/// FFmpeg's requests name the relay (<c>rtsp://127.0.0.1:{port}/…</c>); the camera is asked at its own address (the
/// scheme and authority it was given, <c>rtsps</c> included), and a <c>Content-Base</c> or <c>Content-Location</c> naming
/// that address is given back to FFmpeg as the relay's. FFmpeg is given no user name or password: when the camera asks
/// (401), the relay signs the request itself with the address's credentials (Digest with MD5 or SHA-256, else Basic)
/// and asks again, and signs every request after it. It never sends Basic, the password itself, over plain
/// <c>rtsp</c> to a camera that offered a Digest it can't answer, and never sends credentials with control characters.
/// </para>
/// <para>
/// An answer is matched to its request by its <c>CSeq</c>; one without a <c>CSeq</c> answers the oldest request not yet
/// answered, as FFmpeg's own client takes it (<c>ff_rtsp_read_reply</c> in <c>libavformat/rtsp.c</c> only warns when the
/// <c>CSeq</c> is missing or another, and reads the answer as its request's). An answer whose <c>CSeq</c> names no request
/// waiting answers none: it agrees no channels, and a 401 with it ends the relay. A request is counted as waiting and
/// written to the camera in one step, under the write lock, so the requests waiting are in the order the camera received
/// them; at most 16 wait, and one more ends the relay rather than let one go unanswered.
/// </para>
/// <para>
/// Interleaved packets pass only on the channels a SETUP agreed, and only when at least 8 bytes long (FFmpeg reads a
/// shorter one's bytes as the next message); between two decoded frames (<see cref="Decoded"/>) the packets passed are
/// held to <c>mediaBytes</c> together, whatever their marker bits say, which bounds what FFmpeg's parser and decoder can
/// gather without making a picture. <see cref="Problem"/> says why the relay ended the connection, when it did.
/// </para>
/// Another process on this computer could take the one connection first: it would get only the camera connection the
/// provider made, which nothing keeps a local process from making itself, and FFmpeg's open would fail and be retried.
/// </summary>
internal sealed class RtspRelay : IDisposable
{
    private const int MaxHeader = 64 * 1024;
    private const int MaxBody = 1024 * 1024;
    private const int MaxLine = 4000;
    private const int MaxWaiting = 16;

    internal static readonly MediaStatus Malformed = new("media.provider", "The camera's answer isn't valid RTSP.");
    internal static readonly MediaStatus Redirected = new("media.provider", "The camera redirects to another address, which isn't followed.");
    internal static readonly MediaStatus TooLong = new("media.provider", "The camera's answer is too long.");
    internal static readonly MediaStatus SignInNeeded = new("media.provider", "The camera asks for a user name and password.");
    internal static readonly MediaStatus SignInRefused = new("media.provider", "The camera refused the user name and password.");
    internal static readonly MediaStatus SignInUnknown = new("media.provider", "The camera asks for a kind of sign-in the engine doesn't have.");
    internal static readonly MediaStatus SignInUnsendable = new("media.provider", "The address's user name or password has control characters, which aren't sent.");
    internal static readonly MediaStatus TooManyWaiting = new("media.provider", "The camera left too many requests unanswered.");

    private readonly Socket _listener;
    private readonly Stream _camera;
    private readonly long _maxInput;
    private readonly string _cameraBase;
    private readonly (string User, string Password)? _credentials;
    private readonly bool _secure;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Lock _lock = new();
    // The requests sent to the camera and not answered yet, in the order it received them (each counted as it is
    // written, under the write lock): what an answer's CSeq names, and the oldest what an answer without one answers.
    private readonly List<Message> _waiting = [];
    private readonly bool[] _channels = new bool[256];
    private readonly Action? _closed;
    private Challenge? _challenge;
    private long _input;
    private Socket? _client;
    private int _disposed;
    private volatile MediaStatus? _problem;

    private RtspRelay(Stream camera, string cameraBase, string userInfo, long maxInput, Action? closed)
    {
        _camera = camera;
        _cameraBase = cameraBase;
        _secure = cameraBase.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase);
        _maxInput = maxInput;
        _closed = closed;
        if (userInfo.Length > 0)
        {
            var colon = userInfo.IndexOf(':');
            _credentials = colon < 0
                ? (Uri.UnescapeDataString(userInfo), "")
                : (Uri.UnescapeDataString(userInfo[..colon]), Uri.UnescapeDataString(userInfo[(colon + 1)..]));
        }
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    }

    /// <summary>The loopback port FFmpeg connects to.</summary>
    public int Port { get; private set; }

    /// <summary>The address FFmpeg is given, without a path: <c>rtsp://127.0.0.1:{port}</c>.</summary>
    public string RelayBase => "rtsp://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>Tests only: awaited as a request is about to be sent to the camera, before the write lock is taken.</summary>
    internal Func<Task>? Sending { get; set; }

    /// <summary>Why the relay ended the connection itself, when it did.</summary>
    public MediaStatus? Problem => _problem;

    /// <summary>
    /// A relay to <paramref name="camera"/> (the relay owns it), listening on loopback. <paramref name="cameraBase"/> is
    /// the camera's own scheme and authority (<c>rtsps://camera.local:322</c>), <paramref name="userInfo"/> the address's
    /// user information as written (escaped; empty for none), <paramref name="maxInput"/> what may pass between two
    /// decoded frames, and <paramref name="closed"/> is told once when the camera connection is closed.
    /// </summary>
    public static RtspRelay Start(Stream camera, string cameraBase, string userInfo, long maxInput, Action? closed = null)
    {
        var relay = new RtspRelay(camera, cameraBase, userInfo, maxInput, closed);
        try
        {
            relay._listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            relay._listener.Listen(1);
            relay.Port = ((IPEndPoint)relay._listener.LocalEndPoint!).Port;
        }
        catch
        {
            relay.Dispose();
            throw;
        }
        _ = relay.RunAsync();
        return relay;
    }

    /// <summary>A frame was decoded: what FFmpeg holds of the stream's packets starts again from nothing.</summary>
    public void Decoded() => Interlocked.Exchange(ref _input, 0);

    private async Task RunAsync()
    {
        try
        {
            var accepted = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false);
            _client = accepted;
            _listener.Dispose(); // one connection only
            if (Volatile.Read(ref _disposed) != 0)
            {
                accepted.Dispose();
                return;
            }
            accepted.NoDelay = true;
            using var client = new NetworkStream(accepted, ownsSocket: false);
            var requests = RequestsAsync(client, _stop.Token);
            var answers = AnswersAsync(client, _stop.Token);
            await Task.WhenAny(requests, answers).ConfigureAwait(false);
        }
        catch
        {
            // Either side closed, or the relay was disposed.
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>What FFmpeg sends: its requests (to the camera's address, signed when the camera asked) and its answers.</summary>
    private async Task RequestsAsync(Stream client, CancellationToken token)
    {
        var input = new BufferedStream(client, 16 * 1024);
        var one = new byte[1];
        try
        {
            while (true)
            {
                await input.ReadExactlyAsync(one, token).ConfigureAwait(false);
                if (one[0] == (byte)'$')
                {
                    var packet = await ReadPacketAsync(input, token).ConfigureAwait(false);
                    await WriteCameraAsync(packet, token).ConfigureAwait(false);
                    continue;
                }
                var message = await ReadMessageAsync(input, one[0], token).ConfigureAwait(false);
                if (message.Method is null)
                {
                    await WriteCameraAsync(message.Serialize(), token).ConfigureAwait(false); // an answer to the camera's request
                    continue;
                }
                message.SetTarget(ToCamera(message.Target!));
                message.Remove("Authorization");
                await SendAsync(message, token).ConfigureAwait(false);
            }
        }
        catch (RefusedException e)
        {
            _problem ??= e.Status;
        }
    }

    /// <summary>The camera's answers, requests and interleaved packets, each checked whole before it goes to FFmpeg.</summary>
    private async Task AnswersAsync(Stream client, CancellationToken token)
    {
        var input = new BufferedStream(_camera, 64 * 1024);
        var one = new byte[1];
        try
        {
            while (true)
            {
                await input.ReadExactlyAsync(one, token).ConfigureAwait(false);
                if (one[0] == (byte)'$')
                {
                    var packet = await ReadPacketAsync(input, token).ConfigureAwait(false);
                    var length = packet.Length - 4;
                    bool open;
                    lock (_lock) open = _channels[packet[1]];
                    if (!open || length < 8) continue; // FFmpeg would drop it, or read a short one's bytes as a message
                    if (Interlocked.Add(ref _input, length) > _maxInput)
                        throw new RefusedException(new MediaStatus("limit.mediaBytes",
                            $"The camera sent over {_maxInput / (1024 * 1024)} MiB without a picture."));
                    await client.WriteAsync(packet, token).ConfigureAwait(false);
                    continue;
                }

                var message = await ReadMessageAsync(input, one[0], token).ConfigureAwait(false);
                if (message.Method is { } method)
                {
                    if (method.Equals("REDIRECT", StringComparison.OrdinalIgnoreCase)) throw new RefusedException(Redirected);
                }
                else
                {
                    if (message.Status is >= 300 and < 400) throw new RefusedException(Redirected);
                    var request = Answered(message);
                    if (message.Status == 401)
                    {
                        await SignInAsync(message, request, token).ConfigureAwait(false);
                        continue;
                    }
                    if (message.Status is >= 200 and < 300) Agreed(message, request);
                    foreach (var name in (string[])["Content-Base", "Content-Location"])
                        if (message.Header(name) is { } value) message.Set(name, ToRelay(value));
                }
                await client.WriteAsync(message.Serialize(), token).ConfigureAwait(false);
            }
        }
        catch (RefusedException e)
        {
            _problem ??= e.Status;
        }
    }

    /// <summary>An interleaved packet after its <c>$</c>: the channel, a 16-bit length and the packet, all four parts.</summary>
    private static async Task<byte[]> ReadPacketAsync(Stream input, CancellationToken token)
    {
        var header = new byte[3];
        await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
        var packet = new byte[4 + length];
        packet[0] = (byte)'$';
        header.CopyTo(packet, 1);
        await input.ReadExactlyAsync(packet.AsMemory(4), token).ConfigureAwait(false);
        return packet;
    }

    /// <summary>
    /// Sends <paramref name="request"/> to the camera (again): signed for the last challenge, counted as waiting and
    /// written in one step under the write lock, so <see cref="_waiting"/> is in the order the camera receives requests.
    /// Past <see cref="MaxWaiting"/> waiting, the relay ends.
    /// </summary>
    private async Task SendAsync(Message request, CancellationToken token)
    {
        if (Sending is { } sending) await sending().ConfigureAwait(false);
        await _write.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte[] bytes;
            lock (_lock)
            {
                if (_waiting.Count >= MaxWaiting) throw new RefusedException(TooManyWaiting);
                Sign(request);
                _waiting.Add(request);
                bytes = request.Serialize();
            }
            await _camera.WriteAsync(bytes, token).ConfigureAwait(false);
            await _camera.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task WriteCameraAsync(byte[] bytes, CancellationToken token)
    {
        await _write.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _camera.WriteAsync(bytes, token).ConfigureAwait(false);
            await _camera.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    // ---- messages -----------------------------------------------------------------------------------------------

    /// <summary>One message, read whole and understood (see the class's summary), or <see cref="RefusedException"/>.</summary>
    private static async Task<Message> ReadMessageAsync(Stream input, byte first, CancellationToken token)
    {
        var total = 0;
        var message = new Message();
        var start = await ReadLineAsync(input, first, token).ConfigureAwait(false);
        total += start.Length;
        message.ParseStart(start);
        while (true)
        {
            var line = await ReadLineAsync(input, null, token).ConfigureAwait(false);
            if (line.Length == 0) break;
            total += line.Length;
            if (total > MaxHeader) throw new RefusedException(TooLong);
            message.AddHeader(line);
        }
        var length = message.ContentLength();
        if (length > MaxBody) throw new RefusedException(TooLong);
        message.Body = new byte[length];
        await input.ReadExactlyAsync(message.Body, token).ConfigureAwait(false);
        return message;
    }

    /// <summary>A line without its end, as Latin-1: only printable bytes and tabs, a carriage return only before the line feed.</summary>
    private static async Task<string> ReadLineAsync(Stream input, byte? first, CancellationToken token)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        var carriage = false;
        while (true)
        {
            byte b;
            if (first is { } f)
            {
                b = f;
                first = null;
            }
            else
            {
                await input.ReadExactlyAsync(one, token).ConfigureAwait(false);
                b = one[0];
            }
            if (b == (byte)'\n') return line.ToString();
            if (carriage || (b < 0x20 && b != (byte)'\t' && b != (byte)'\r') || b == 0x7F) throw new RefusedException(Malformed);
            if (b == (byte)'\r')
            {
                carriage = true;
                continue;
            }
            if (line.Length >= MaxLine) throw new RefusedException(Malformed);
            line.Append((char)b);
        }
    }

    /// <summary>A message's start line and headers, in order, as they will be written out.</summary>
    private sealed class Message
    {
        private readonly List<(string Name, string Value)> _headers = [];
        private string _start = "";

        /// <summary>An answer's status, 0 for a request.</summary>
        public int Status;
        public string? Method;
        public string? Target;
        public byte[] Body = [];

        /// <summary>Times the relay signed and sent this request again after a 401.</summary>
        public int Retries;

        public void ParseStart(string line)
        {
            if (line.StartsWith("RTSP/", StringComparison.Ordinal))
            {
                // RTSP/1.0, one space, three digits, then the end or one space and a reason.
                if (line.Length < 12 || !line.StartsWith("RTSP/1.0 ", StringComparison.Ordinal)
                    || !char.IsAsciiDigit(line[9]) || !char.IsAsciiDigit(line[10]) || !char.IsAsciiDigit(line[11])
                    || line[9] is < '1' or > '5' || (line.Length > 12 && line[12] != ' '))
                    throw new RefusedException(Malformed);
                Status = int.Parse(line.AsSpan(9, 3), NumberStyles.None, CultureInfo.InvariantCulture);
                _start = line;
                return;
            }
            // METHOD (letters, '_' and '-'), one space, a target without spaces, one space, RTSP/1.0.
            var parts = line.Split(' ');
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2] != "RTSP/1.0"
                || !parts[0].All(c => char.IsAsciiLetter(c) || c is '_' or '-') || parts[1].Contains('\t'))
                throw new RefusedException(Malformed);
            Method = parts[0];
            Target = parts[1];
            _start = line;
        }

        public void SetTarget(string target)
        {
            Target = target;
            _start = $"{Method} {target} RTSP/1.0";
        }

        public void AddHeader(string line)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !char.IsAsciiLetter(line[0]) || line.AsSpan(0, colon).ContainsAnyExcept(NameChars)) throw new RefusedException(Malformed);
            var name = line[..colon];
            var value = line[(colon + 1)..].Trim(' ', '\t');
            // The headers that frame a message, or say where its packets come, appear once.
            if ((name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Transport", StringComparison.OrdinalIgnoreCase))
                && Header(name) is not null)
                throw new RefusedException(Malformed);
            _headers.Add((name, value));
        }

        private static readonly System.Buffers.SearchValues<char> NameChars =
            System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-");

        public int ContentLength()
        {
            if (Header("Content-Length") is not { } value) return 0;
            if (value.Length is 0 or > 9 || !value.All(char.IsAsciiDigit)) throw new RefusedException(Malformed);
            return int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        public string? Header(string name)
        {
            foreach (var (n, v) in _headers)
                if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return v;
            return null;
        }

        public IEnumerable<string> Headers(string name) =>
            _headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);

        public void Set(string name, string value)
        {
            for (var i = 0; i < _headers.Count; i++)
            {
                if (!_headers[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                _headers[i] = (_headers[i].Name, value);
                return;
            }
            _headers.Add((name, value));
        }

        public void Remove(string name) => _headers.RemoveAll(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The message as it is written on: CRLF line ends, one form.</summary>
        public byte[] Serialize()
        {
            var head = new StringBuilder(_start).Append("\r\n");
            foreach (var (name, value) in _headers) head.Append(name).Append(": ").Append(value).Append("\r\n");
            head.Append("\r\n");
            var bytes = new byte[head.Length + Body.Length];
            for (var i = 0; i < head.Length; i++) bytes[i] = (byte)head[i];
            Body.CopyTo(bytes, head.Length);
            return bytes;
        }
    }

    // ---- addresses ----------------------------------------------------------------------------------------------

    /// <summary>A request target naming the relay, as the camera's own address; any other target as it is.</summary>
    private string ToCamera(string target) => Rebase(target, RelayBase, _cameraBase);

    /// <summary>An address naming the camera, as the relay's.</summary>
    private string ToRelay(string value) => Rebase(value, _cameraBase, RelayBase);

    private static string Rebase(string value, string from, string to) =>
        value.StartsWith(from, StringComparison.OrdinalIgnoreCase) && (value.Length == from.Length || value[from.Length] is '/' or '?')
            ? to + value[from.Length..]
            : value;

    /// <summary>
    /// The request <paramref name="answer"/> answers, no longer waiting: the oldest waiting with the <c>CSeq</c> it names, or
    /// with no <c>CSeq</c> the oldest waiting; null when its <c>CSeq</c> names none waiting, or none waits.
    /// </summary>
    private Message? Answered(Message answer)
    {
        lock (_lock)
        {
            var cseq = answer.Header("CSeq");
            var i = cseq is null
                ? (_waiting.Count > 0 ? 0 : -1)
                : _waiting.FindIndex(r => string.Equals(r.Header("CSeq"), cseq, StringComparison.Ordinal));
            if (i < 0) return null;
            var request = _waiting[i];
            _waiting.RemoveAt(i);
            return request;
        }
    }

    /// <summary>A SETUP's answer: the interleaved channels it agreed (the camera's, else those FFmpeg asked for).</summary>
    private void Agreed(Message answer, Message? request)
    {
        lock (_lock)
        {
            if (request?.Method?.Equals("SETUP", StringComparison.OrdinalIgnoreCase) is not true) return;
            var range = Interleaved(answer.Header("Transport")) ?? Interleaved(request.Header("Transport"));
            if (range is not var (low, high)) return;
            for (var c = low; c <= high; c++) _channels[c] = true;
        }
    }

    /// <summary>The <c>interleaved=a-b</c> (or <c>=a</c>) of a Transport header's first transport, as FFmpeg reads it.</summary>
    internal static (int Low, int High)? Interleaved(string? transport)
    {
        if (transport is null) return null;
        var first = transport.Split(',')[0];
        foreach (var part in first.Split(';'))
        {
            var p = part.Trim();
            if (!p.StartsWith("interleaved=", StringComparison.OrdinalIgnoreCase)) continue;
            var range = p["interleaved=".Length..].Split('-');
            if (!int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var low) || low > 255) return null;
            var high = low;
            if (range.Length > 1 && (!int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out high) || high > 255 || high < low)) return null;
            return (low, high);
        }
        return null;
    }

    // ---- signing in ---------------------------------------------------------------------------------------------

    /// <summary>A 401: the request it answers is signed with the address's credentials and sent again, else the relay ends.</summary>
    private async Task SignInAsync(Message answer, Message? request, CancellationToken token)
    {
        lock (_lock)
        {
            if (_credentials is not var (user, password)) throw new RefusedException(SignInNeeded);
            if (user.Any(char.IsControl) || password.Any(char.IsControl)) throw new RefusedException(SignInUnsendable);
            if (request is null) throw new RefusedException(Malformed);
            var challenge = Challenge.Choose(answer.Headers("WWW-Authenticate"), out var digestOffered) ?? throw new RefusedException(SignInUnknown);
            // Basic sends the password itself: not in the clear to a camera that offered a Digest (FFmpeg would fail too).
            if (!challenge.Digest && digestOffered && !_secure) throw new RefusedException(SignInUnknown);
            // Signed once already: refused, unless the camera says only its nonce was stale (once more).
            if (request.Retries >= 2 || (request.Retries == 1 && !challenge.Stale)) throw new RefusedException(SignInRefused);
            request.Retries++;
            _challenge = challenge;
        }
        await SendAsync(request, token).ConfigureAwait(false);
    }

    /// <summary>Signs <paramref name="request"/> for the camera's last challenge, when it made one. Under the lock.</summary>
    private void Sign(Message request)
    {
        if (_challenge is null || _credentials is not var (user, password)) return;
        request.Remove("Authorization");
        request.Set("Authorization", _challenge.Answer(user, password, request.Method!, request.Target!));
    }

    /// <summary>A camera's challenge (RFC 7616 Digest with MD5 or SHA-256, or RFC 7617 Basic) and the requests signed for it.</summary>
    private sealed class Challenge
    {
        private static readonly string[] Algorithms = ["MD5", "MD5-sess", "SHA-256", "SHA-256-sess"];

        private bool _digest;
        private string _realm = "";
        private string _nonce = "";
        private string? _opaque;
        private string? _algorithm;
        private bool _qop;
        private int _count;

        public bool Stale { get; private set; }

        /// <summary>Whether it is a Digest challenge (else Basic).</summary>
        public bool Digest => _digest;

        /// <summary>
        /// The challenge to answer among a 401's, every challenge of every header read first (several to a header, as
        /// RFC 9110 allows): the first Digest with MD5, SHA-256 or their -sess forms (and qop auth, or none), else Basic,
        /// else none. <paramref name="digestOffered"/> says whether any Digest was offered, answerable or not.
        /// </summary>
        public static Challenge? Choose(IEnumerable<string> headers, out bool digestOffered)
        {
            Challenge? basic = null;
            var challenges = Parse(headers);
            digestOffered = challenges.Any(c => c.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));
            foreach (var (scheme, fields) in challenges)
            {
                if (scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
                {
                    basic ??= new Challenge();
                    continue;
                }
                if (!scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase)) continue;
                if (!fields.TryGetValue("nonce", out var nonce)) continue;
                var algorithm = fields.GetValueOrDefault("algorithm");
                if (algorithm is not null && !Algorithms.Contains(algorithm, StringComparer.OrdinalIgnoreCase)) continue;
                var qops = fields.GetValueOrDefault("qop");
                var qop = qops is not null && qops.Split(',').Any(q => q.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase));
                if (qops is not null && !qop) continue;
                return new Challenge
                {
                    _digest = true,
                    _realm = fields.GetValueOrDefault("realm") ?? "",
                    _nonce = nonce,
                    _opaque = fields.GetValueOrDefault("opaque"),
                    _algorithm = algorithm,
                    _qop = qop,
                    Stale = fields.GetValueOrDefault("stale")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true,
                };
            }
            return basic;
        }

        /// <summary>The <c>Authorization</c> value for a request.</summary>
        public string Answer(string user, string password, string method, string uri)
        {
            if (!_digest) return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            var cnonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var nc = (++_count).ToString("x8", CultureInfo.InvariantCulture);
            var sha256 = _algorithm?.StartsWith("SHA-256", StringComparison.OrdinalIgnoreCase) == true;
            string H(string text) => sha256 ? Sha256(text) : Md5(text);
            var ha1 = H($"{user}:{_realm}:{password}");
            if (_algorithm?.EndsWith("-sess", StringComparison.OrdinalIgnoreCase) == true) ha1 = H($"{ha1}:{_nonce}:{cnonce}");
            var ha2 = H($"{method}:{uri}");
            var response = _qop ? H($"{ha1}:{_nonce}:{nc}:{cnonce}:auth:{ha2}") : H($"{ha1}:{_nonce}:{ha2}");
            var value = new StringBuilder("Digest ")
                .Append(CultureInfo.InvariantCulture, $"username=\"{Quote(user)}\", realm=\"{Quote(_realm)}\", nonce=\"{Quote(_nonce)}\", uri=\"{Quote(uri)}\", response=\"{response}\"");
            if (_algorithm is not null) value.Append(", algorithm=").Append(_algorithm);
            if (_opaque is not null) value.Append(CultureInfo.InvariantCulture, $", opaque=\"{Quote(_opaque)}\"");
            if (_qop) value.Append(CultureInfo.InvariantCulture, $", qop=auth, nc={nc}, cnonce=\"{cnonce}\"");
            return value.ToString();
        }

#pragma warning disable CA5351 // RTSP Digest is defined over MD5; it signs a request, it protects nothing stored.
        private static string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
#pragma warning restore CA5351

        private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        private static string Quote(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

        /// <summary>
        /// Every challenge of a 401's <c>WWW-Authenticate</c> headers, in order: each header is a comma-separated list
        /// (commas inside quoted strings kept) in which an element starting with a token and then a space, or a token
        /// alone, starts a challenge (its scheme), and any other element is a <c>name=value</c> field of the challenge
        /// before it, its value a token or a quoted string.
        /// </summary>
        private static List<(string Scheme, Dictionary<string, string> Fields)> Parse(IEnumerable<string> headers)
        {
            var challenges = new List<(string, Dictionary<string, string>)>();
            foreach (var header in headers)
            {
                Dictionary<string, string>? fields = null;
                foreach (var element in Elements(header))
                {
                    var text = element.Trim(' ', '\t');
                    var token = 0;
                    while (token < text.Length && IsTokenChar(text[token])) token++;
                    var next = token;
                    while (next < text.Length && text[next] is ' ' or '\t') next++;
                    if (token > 0 && (next == text.Length || (next > token && text[next] != '=')))
                    {
                        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        challenges.Add((text[..token], fields));
                        text = text[next..];
                    }
                    if (fields is null || text.Length == 0) continue; // nothing before a scheme belongs to a challenge
                    var eq = text.IndexOf('=');
                    if (eq < 1) continue; // no name=value (a token68, or junk): not a field
                    var name = text[..eq].Trim(' ', '\t');
                    var value = text[(eq + 1)..].Trim(' ', '\t');
                    fields[name] = value.StartsWith('"') ? Unquote(value) : value;
                }
            }
            return challenges;
        }

        /// <summary>A header's comma-separated elements, a comma inside a quoted string (escapes included) not splitting one.</summary>
        private static IEnumerable<string> Elements(string header)
        {
            var start = 0;
            var quoted = false;
            for (var i = 0; i < header.Length; i++)
            {
                if (quoted && header[i] == '\\') i++;
                else if (header[i] == '"') quoted = !quoted;
                else if (!quoted && header[i] == ',')
                {
                    yield return header[start..i];
                    start = i + 1;
                }
            }
            yield return header[start..];
        }

        /// <summary>A quoted string's text, its escapes undone (to its closing quote).</summary>
        private static string Unquote(string quoted)
        {
            var value = new StringBuilder();
            for (var i = 1; i < quoted.Length && quoted[i] != '"'; i++)
            {
                if (quoted[i] == '\\' && i + 1 < quoted.Length) i++;
                value.Append(quoted[i]);
            }
            return value.ToString();
        }

        /// <summary>An RFC 9110 token character.</summary>
        private static bool IsTokenChar(char c) =>
            c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal);
    }

    /// <summary>Why the relay ends: a message or packet it doesn't pass on.</summary>
    private sealed class RefusedException(MediaStatus status) : Exception(status.Message)
    {
        public MediaStatus Status { get; } = status;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _listener.Dispose();
        _client?.Dispose();
        _camera.Dispose();
        _closed?.Invoke();
    }
}
