using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Spec;
using Serilog;

namespace InfoPanel.Images.Video;

/// <summary>The <see cref="RtspProvider"/>'s budgets (spec/docs/limits.md, process budgets) and seams.</summary>
public sealed class RtspProviderOptions
{
    /// <summary>The clock idling and the reconnection backoff run on (FFmpeg's own waits are bounded in real time).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The media limits a camera's pictures and frames are held to.</summary>
    public SpecLimits Limits { get; init; } = SpecLimits.Default;

    /// <summary>The <c>media.localNetwork</c> setting, read for every connection.</summary>
    public Func<bool> LocalNetwork { get; init; } = () => true;

    /// <summary>Connect only to this computer, and look up no other name (an engine started with <c>--offline</c>).</summary>
    public bool LoopbackOnly { get; init; }

    /// <summary>Resolves a host name (tests answer from a table).</summary>
    public Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; init; } = Dns.GetHostAddressesAsync;

    /// <summary>Connects to a checked address (tests record it).</summary>
    public Func<IPEndPoint, CancellationToken, ValueTask<Stream>> Connect { get; init; } = HttpMediaProvider.ConnectSocketAsync;

    /// <summary>The <c>User-Agent</c> the RTSP requests carry.</summary>
    public string UserAgent { get; init; } = "InfoPanel";

    /// <summary>A camera no tick has asked for this long is closed, and what it showed dropped.</summary>
    public TimeSpan IdleAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The camera's connection, TLS included, must be made within this.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The camera must answer DESCRIBE, SETUP and PLAY within this.</summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>A stream with no frame decoded for this long has ended.</summary>
    public TimeSpan StallAfter { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The first wait before connecting again after a failure; each failure in a row doubles it, up to <see cref="RetryMost"/>.</summary>
    public TimeSpan RetryFirst { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan RetryMost { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Cameras open at once in the engine.</summary>
    public int MaxCameras { get; init; } = 8;

    /// <summary>Cameras open at once among those one item asked for in <see cref="IdleAfter"/>.</summary>
    public int MaxCamerasPerItem { get; init; } = 4;

    /// <summary>A camera no tick has asked for this long is idle: it may be closed to make room for another.</summary>
    public TimeSpan CameraIdleAfter { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// What the open cameras' decoders may hold between them (as for video files, per picture size): 768 MiB. A camera's
    /// decoder is given room before it decodes, for the pictures its stream's description gives (else up to 4K UHD
    /// ones), and allocates no larger picture than that room holds; its first frame says its size, and the room shrinks
    /// to it. Larger pictures connect again for the room they need.
    /// </summary>
    public long WorkingBytes { get; init; } = 768L * 1024 * 1024;

    /// <summary>The longest reference taken.</summary>
    public int MaxAddressLength { get; init; } = 2048;

    /// <summary>Tests: told when closed cameras' runs are about to be stopped, once the provider's lock is let go.</summary>
    internal Action? Cancelling { get; init; }
}

/// <summary>
/// The <c>rtsp</c> media provider (spec/docs/providers.md, "rtsp: camera streams"): RTSP cameras by address
/// (<c>rtsp://</c>, <c>rtsps://</c>), decoded by FFmpeg. One camera per address, shared by every item and profile
/// showing it, open only while ticks ask for it and closed <see cref="RtspProviderOptions.IdleAfter"/> after the last
/// ask. Each camera runs on a thread of its own: it resolves and checks the host by the address rules and connects
/// itself (<see cref="AddressRules.ConnectAsync"/>), on every connection, and hands FFmpeg only a loopback relay of
/// that connection (<see cref="RtspRelay"/>), so FFmpeg reaches nothing the rules didn't allow; RTP comes interleaved
/// over TCP, never UDP. It keeps only the newest frame (and the one before, for a tick that already chose it), shows
/// the last picture through a failure, and connects again with backoff from 1 to 60 s. Pictures that grow past their
/// decoder's room connect again at once for more; a second change within 10 s of the stream starting again counts as a
/// failure, with the same backoff. <see cref="Get(in MediaRequest)"/> never blocks. Open cameras are limited in number
/// (8, 4 for one item, an item joining a camera others opened included) and in their decoders' memory, reserved before
/// decoding (the camera is asked to play only once its decoder has room) and enforced by FFmpeg (the most pixels a
/// picture may have, and only 8-bit 4:2:0 and 4:2:2 pictures); their frames count against the media cache's decoded
/// budget.
/// </summary>
public sealed class RtspProvider : IMediaProvider, IDisposable
{
    public const string Name = "rtsp";

    /// <summary>Whether <paramref name="ffmpeg"/> can open camera streams: it has FFmpeg's RTSP client.</summary>
    public static bool Available(FFmpeg ffmpeg) => RtspStream.HasClient(ffmpeg);

    /// <summary>A connection that ended to start again at once (before a frame, for more room), not a failure.</summary>
    private static readonly MediaStatus Again = new("", "");

    /// <summary>A connection whose pictures grew past its room after a frame: again at once, unless it changed lately.</summary>
    private static readonly MediaStatus Changed = new("", "changed");

    private static readonly MediaStatus KeepsChanging = new("media.provider", "The camera's pictures keep changing size or format.");

    /// <summary>A stream steady this long since it started again: its next change of pictures connects again at once.</summary>
    private const long SteadyAfter = 10_000;

    private static readonly MediaStatus Connecting = new("media.loading", "Connecting.");

    /// <summary>
    /// A decoder's working set for each pixel of its largest picture: as a video file's (<see cref="VideoPlayer.WorkingSet"/>),
    /// 2 bytes a pixel (the most 8-bit 4:2:2 takes; 4:2:0 takes 1.5) for each picture H.264 may keep.
    /// </summary>
    private const long BytesPerPixel = 2 * (16 + VideoDecoder.Threads + 1);

    /// <summary>The pixels a decoder of 4K UHD pictures needs: the room a camera whose description gives no size asks for first.</summary>
    private static readonly long UhdPixels = VideoDecoder.PictureArea(new MediaInfo(MediaFormat.Mp4, 3840, 2160, 0, 0));

    private static readonly ILogger Logger = Log.ForContext<RtspProvider>();

    private readonly FFmpeg _ffmpeg;
    private readonly IFrameBudget? _budget;
    private readonly RtspProviderOptions _options;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Camera> _cameras = new(StringComparer.Ordinal);
    private readonly ITimer _expiry;
    private readonly long _start;
    private long _lastSweep;
    private int _connections;
    private volatile bool _disposed;

    /// <param name="ffmpeg">The decoder.</param>
    /// <param name="cache">The media cache whose decoded budget the cameras' frames count against (none: uncounted).</param>
    /// <param name="options">Budgets and seams.</param>
    public RtspProvider(FFmpeg ffmpeg, MediaSampleCache? cache, RtspProviderOptions? options = null)
    {
        _ffmpeg = ffmpeg;
        _budget = cache;
        _options = options ?? new RtspProviderOptions();
        _start = _options.Time.GetTimestamp();
        // Cameras no tick asks for close on their own, with no lookup to notice them (a camera would stream on).
        _expiry = _options.Time.CreateTimer(_ => Expire(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Addresses with a camera.</summary>
    public int CameraCount
    {
        get
        {
            lock (_lock) return _cameras.Count;
        }
    }

    /// <summary>Camera connections open now.</summary>
    public int OpenConnections => Volatile.Read(ref _connections);

    private long Now() => (long)_options.Time.GetElapsedTime(_start).TotalMilliseconds;

    public ProvidedMedia Get(in MediaRequest request) => Answer(request, null, null);

    /// <summary>
    /// <see cref="Get(in MediaRequest)"/> by an item in a tick: every ask of the tick for an address shows the frame its
    /// first live ask was answered with, and the item counts among those asking (for the cameras one item may keep open).
    /// </summary>
    public ProvidedMedia Get(in MediaRequest request, in MediaAsk ask) => Answer(request, ask.Tick, ask.Item);

    private ProvidedMedia Answer(in MediaRequest request, MediaTick? tick, object? item)
    {
        if (_disposed) return new ProvidedMedia(null, new MediaStatus("media.provider", "The provider is shut down."));
        if (!TryAddress(request.Reference, _options.MaxAddressLength, out var address, out var uri))
            return new ProvidedMedia(null, new MediaStatus("media.provider", "Not a camera stream address."));

        List<CancellationTokenSource>? stops = null;
        MediaSample? retained;
        MediaStatus? status;
        try
        {
            lock (_lock)
            {
                var now = Now();
                stops = Sweep(now);
                if (!_cameras.TryGetValue(address, out var camera))
                {
                    // A one-off render starts nothing: it shows what a live tick has, else the placeholder.
                    if (!request.Live) return new ProvidedMedia(null, new MediaStatus("media.loading", "Not connected yet."));
                    camera = new Camera(address, uri);
                    _cameras[address] = camera;
                }
                if (request.Live && item is not null && Joining(camera, item, now) is { } over)
                    return new ProvidedMedia(null, over);
                // What the tick's first live ask for the address chose (an empty choice included), which every later ask in
                // the tick shows.
                Choice? chose = null;
                if (tick is not null && tick.TryShown(camera, out Choice? chosen)) chose = chosen;
                if (request.Live)
                {
                    camera.LastAsked = now;
                    if (item is not null) camera.Askers[item] = now;
                    if (camera.Run is null) Admit(camera, item, now, ref stops);
                }
                if (chose is not null)
                {
                    retained = chose.Sample?.Retain();
                    status = chose.Status;
                }
                else
                {
                    retained = camera.Current?.Sample.Retain();
                    status = camera.Status ?? (retained is null ? Connecting : null);
                    if (tick is not null && request.Live) tick.Show(camera, new Choice(camera.Current?.Sample, status));
                }
            }
        }
        finally
        {
            Stop(stops);
        }
        return new ProvidedMedia(retained, status ?? (retained is null ? Connecting : null));
    }

    /// <summary>
    /// When <paramref name="item"/> asks for an open camera it hasn't asked for lately: <c>media.budget</c> if it already
    /// keeps the most cameras one item may (the camera stays open for the items that asked for it), else null. Under the lock.
    /// </summary>
    private MediaStatus? Joining(Camera camera, object item, long now)
    {
        var asked = now - (long)_options.IdleAfter.TotalMilliseconds;
        if (camera.Run is null || camera.AskedBy(item, asked)) return null;
        var mine = _cameras.Values.Count(c => c != camera && c.Run is not null && c.AskedBy(item, asked));
        return mine >= Math.Max(1, _options.MaxCamerasPerItem)
            ? new MediaStatus("media.budget", $"Too many camera streams for one item: {_options.MaxCamerasPerItem} are open.")
            : null;
    }

    /// <summary>Stops the runs closed under the lock, once it is let go (each the exact run that was closed).</summary>
    private void Stop(List<CancellationTokenSource>? stops)
    {
        if (stops is null || stops.Count == 0) return;
        _options.Cancelling?.Invoke();
        foreach (var stop in stops) stop.Cancel();
    }

    /// <summary>
    /// Opens <paramref name="camera"/> if the limits allow: past one, the idle camera asked for longest ago closes (it
    /// keeps its picture and opens again on its next ask); with none idle, the camera stays closed with <c>media.budget</c>
    /// and is tried again on its next ask. Under the lock.
    /// </summary>
    private void Admit(Camera camera, object? item, long now, ref List<CancellationTokenSource>? stops)
    {
        var idle = (long)_options.CameraIdleAfter.TotalMilliseconds;
        var asked = now - (long)_options.IdleAfter.TotalMilliseconds;
        while (true)
        {
            var open = _cameras.Values.Where(c => c.Run is not null).ToList();
            var mine = item is null ? [] : open.Where(c => c.AskedBy(item, asked)).ToList();
            string? over = open.Count >= Math.Max(1, _options.MaxCameras) ? $"Too many camera streams: {_options.MaxCameras} are open."
                : mine.Count >= Math.Max(1, _options.MaxCamerasPerItem) ? $"Too many camera streams for one item: {_options.MaxCamerasPerItem} are open."
                : null;
            if (over is null) break;
            var candidates = open.Count >= _options.MaxCameras ? open : mine;
            var idlest = candidates.Where(c => now - c.LastAsked >= idle).OrderBy(c => c.LastAsked).FirstOrDefault();
            if (idlest is null)
            {
                camera.Status = new MediaStatus("media.budget", over);
                return;
            }
            Close(idlest, ref stops);
        }
        if (camera.Status?.Code == "media.budget") camera.Status = null;
        var run = new CameraRun();
        camera.Run = run;
        var thread = new Thread(() => RunLoop(camera, run)) { IsBackground = true, Name = "RTSP camera" };
        run.Thread = thread;
        thread.Start();
    }

    /// <summary>
    /// Stops <paramref name="camera"/>'s connection (it keeps its picture). Under the lock: the run's own stop is added to
    /// <paramref name="stops"/>, which the caller cancels once the lock is let go.
    /// </summary>
    private void Close(Camera camera, ref List<CancellationTokenSource>? stops)
    {
        if (camera.Run is not { } run) return;
        camera.Run = null;
        camera.Working = 0;
        camera.Opening = false;
        (stops ??= []).Add(run.Stop);
        run.Closed = true;
    }

    /// <summary>Drops every camera, so each reference connects again on its next ask (the local network setting changed).</summary>
    public void Reset()
    {
        List<CancellationTokenSource>? stops = null;
        lock (_lock)
        {
            foreach (var camera in _cameras.Values) Drop(camera, ref stops);
            _cameras.Clear();
        }
        Stop(stops);
    }

    /// <summary>Closes <paramref name="camera"/> and lets go of its frames. Under the lock.</summary>
    private void Drop(Camera camera, ref List<CancellationTokenSource>? stops)
    {
        Close(camera, ref stops);
        camera.Previous = Release(camera.Previous);
        camera.Current = Release(camera.Current);
    }

    private Frame? Release(Frame? frame)
    {
        if (frame is null) return null;
        _budget?.Uncharge(frame.Sample.Bytes);
        frame.Sample.Release();
        return null;
    }

    private void Expire()
    {
        if (_disposed) return;
        List<CancellationTokenSource>? stops;
        lock (_lock) stops = Sweep(Now());
        Stop(stops);
    }

    /// <summary>Drops cameras no tick asked for in <see cref="RtspProviderOptions.IdleAfter"/>; at most once a second. Under the lock.</summary>
    private List<CancellationTokenSource>? Sweep(long now)
    {
        if (now - _lastSweep < 1000 && _lastSweep != 0) return null;
        _lastSweep = now;
        var idle = (long)_options.IdleAfter.TotalMilliseconds;
        List<CancellationTokenSource>? stops = null;
        foreach (var (address, camera) in _cameras.Where(p => now - p.Value.LastAsked >= idle).ToList())
        {
            Drop(camera, ref stops);
            _cameras.Remove(address);
        }
        foreach (var camera in _cameras.Values)
        {
            var asked = now - idle;
            foreach (var old in camera.Askers.Where(a => a.Value < asked).Select(a => a.Key).ToList()) camera.Askers.Remove(old);
        }
        return stops;
    }

    // ---- a camera's thread --------------------------------------------------------------------------------------

    private void RunLoop(Camera camera, CameraRun run)
    {
        var token = run.Stop.Token;
        try
        {
            while (!token.IsCancellationRequested && !_disposed)
            {
                MediaStatus? failure;
                try
                {
                    failure = Connect(camera, run, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    Logger.Warning(e, "A camera stream failed");
                    failure = new MediaStatus("media.provider", "The camera's stream failed.");
                }
                if (token.IsCancellationRequested || failure is null) break;
                if (ReferenceEquals(failure, Again)) continue;
                TimeSpan wait;
                if (ReferenceEquals(failure, Changed))
                {
                    if (Churned(camera, run) is not { } churned) continue;
                    wait = churned;
                }
                else
                {
                    wait = Failed(camera, run, failure);
                }
                try
                {
                    Task.Delay(wait, _options.Time, token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                if (camera.Run == run) camera.Run = null;
                if (!run.Closed)
                {
                    camera.Working = 0;
                    camera.Opening = false;
                }
            }
        }
    }

    /// <summary>One connection: connected, relayed, opened and read until it fails (its status) or is stopped (null).</summary>
    private MediaStatus? Connect(Camera camera, CameraRun run, CancellationToken token)
    {
        Stream connection;
        try
        {
            connection = ConnectAsync(camera.Uri, token).GetAwaiter().GetResult();
        }
        catch (AddressRefusedException e)
        {
            return new MediaStatus("media.address", e.Message);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new MediaStatus("media.provider", $"The camera took longer than {_options.ConnectTimeout.TotalSeconds:0} s to connect.");
        }
        catch (AuthenticationException)
        {
            return new MediaStatus("media.provider", "The camera's certificate isn't trusted.");
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            var socket = e as SocketException ?? e.InnerException as SocketException;
            return new MediaStatus("media.provider", socket?.SocketErrorCode == SocketError.HostNotFound
                ? "The camera's name wasn't found."
                : $"The camera couldn't be reached: {(socket ?? e).Message}");
        }

        Interlocked.Increment(ref _connections);
        // FFmpeg is given the relay's address with the camera's path, and no credentials: the relay signs in itself.
        using var relay = RtspRelay.Start(connection, camera.Base, camera.Uri.UserInfo, _options.Limits.MediaBytes,
            () => Interlocked.Decrement(ref _connections));
        var url = relay.RelayBase + camera.Uri.PathAndQuery;
        RtspStream? stream = null;
        long ceiling = 0;
        var decoded = 0L;
        try
        {
            // Opened paused: the camera sends nothing until its decoder has room and is open.
            stream = RtspStream.Open(_ffmpeg, url, _options.UserAgent, _options.Limits, _options.OpenTimeout, token);
            if (!Reserve(camera, run, stream.DescribedPixels, token, out ceiling, out var refused)) return refused;
            stream.OpenDecoder(ceiling);
            stream.Play();
            while (true)
            {
                stream.Next(_options.StallAfter);
                relay.Decoded();
                if (decoded++ == 0)
                {
                    // Its size known, the decoder's room shrinks to its pictures', and FFmpeg allocates no larger one: a
                    // larger picture later changes the stream, which connects again for room.
                    var (w, h) = stream.Size;
                    var pixels = Math.Min(ceiling, VideoDecoder.PictureArea(new MediaInfo(MediaFormat.Mp4, w, h, 0, 0)));
                    stream.SetCeiling(pixels);
                    Settle(camera, run, pixels * BytesPerPixel);
                }
                Publish(camera, run, MediaSample.FromImage(stream.Convert()));
            }
        }
        catch (RtspException e) when (relay.Problem is null && (e.Changed || e.Status.Code == "media.malformed"))
        {
            var outside = new MediaStatus("media.malformed", "The camera's pictures are outside the media limits.");
            var (cw, ch) = e.Coded;
            if (e.Changed && (long)cw * ch > _options.Limits.MediaArea) return outside;
            // Its pictures grew past its room after a frame: it connects again, asking for room for the new ones.
            if (decoded > 0)
            {
                if (!e.Changed) return e.Status;
                run.Need = Math.Min(_options.Limits.MediaArea, VideoDecoder.PictureArea(new MediaInfo(MediaFormat.Mp4, cw, ch, 0, 0))) * BytesPerPixel;
                return Changed;
            }
            // Before its first frame, in less room than the media area's: its pictures didn't fit (their size known), or
            // may not (they didn't decode). It connects again at once for their room, else twice the room, refused
            // (media.budget) when there isn't so much.
            if (ceiling < _options.Limits.MediaArea)
            {
                var needed = e.Changed && (long)cw * ch > ceiling
                    ? VideoDecoder.PictureArea(new MediaInfo(MediaFormat.Mp4, cw, ch, 0, 0))
                    : Math.Max(1, ceiling) * 2;
                run.Need = Math.Min(_options.Limits.MediaArea, needed) * BytesPerPixel;
                return Again;
            }
            return e.Changed ? outside : e.Status;
        }
        catch (RtspException e)
        {
            return relay.Problem ?? e.Status;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>The camera's connection: the host resolved and checked by the address rules, TLS for <c>rtsps</c>, within the connect time limit.</summary>
    private async Task<Stream> ConnectAsync(Uri uri, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(_options.ConnectTimeout, _options.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, token);
        var port = uri.Port > 0 ? uri.Port : uri.Scheme == "rtsps" ? 322 : 554;
        var connection = await AddressRules.ConnectAsync(uri.Host, port, _options.Resolve, _options.Connect, _options.LocalNetwork(),
            _options.LoopbackOnly, linked.Token).ConfigureAwait(false);
        if (uri.Scheme != "rtsps") return connection;
        var tls = new SslStream(connection, leaveInnerStreamOpen: false);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.IdnHost }, linked.Token).ConfigureAwait(false);
            return tls;
        }
        catch
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Room for <paramref name="camera"/>'s decoder before it decodes (and before the camera is asked to play): for the
    /// pictures its stream's description gives (<paramref name="described"/> pixels), else for pictures up to 4K UHD,
    /// or for what the run found it needs (<see cref="CameraRun.Need"/>), within the media area's. When less than it needs
    /// is left, the idle camera asked for longest ago closes to make it, then the next. While other cameras are opening
    /// (they hold room for pictures whose size they don't know yet) and not all it wants is left, it waits for them to
    /// know their sizes (at most <see cref="RtspProviderOptions.OpenTimeout"/>), then takes what is left. Less than the
    /// described pictures', the smallest picture's or the run's need is refused (<c>media.budget</c>).
    /// <paramref name="ceiling"/> is the most pixels a picture may have in the room given.
    /// </summary>
    private bool Reserve(Camera camera, CameraRun run, long described, CancellationToken token, out long ceiling, out MediaStatus refused)
    {
        refused = new MediaStatus("media.budget", "The camera streams open leave no room for this one's decoder.");
        ceiling = 0;
        var most = _options.Limits.MediaArea * BytesPerPixel;
        var least = Math.Min(most, Math.Max(VideoDecoder.PictureArea(new MediaInfo(MediaFormat.Mp4, 1, 1, 0, 0)) * BytesPerPixel, run.Need));
        // What it asks for: the described pictures' room (no less), else 4K UHD's (or less, if only less is left).
        var want = Math.Min(most, Math.Max(least, (described > 0 ? described : UhdPixels) * BytesPerPixel));
        if (described > 0) least = want;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            List<CancellationTokenSource>? stops = null;
            long granted = 0;
            try
            {
                lock (_lock)
                {
                    if (camera.Run != run) return false;
                    var idle = (long)_options.CameraIdleAfter.TotalMilliseconds;
                    var now = Now();
                    long Left() => _options.WorkingBytes - _cameras.Values.Where(c => c != camera).Sum(c => c.Working);
                    while (Left() < least)
                    {
                        var idlest = _cameras.Values.Where(c => c != camera && c.Working > 0 && now - c.LastAsked >= idle).OrderBy(c => c.LastAsked).FirstOrDefault();
                        if (idlest is null) break;
                        Close(idlest, ref stops);
                    }
                    var left = Left();
                    var others = _cameras.Values.Any(c => c != camera && c.Opening);
                    if (left >= least && (left >= want || !others || waited.Elapsed >= _options.OpenTimeout))
                    {
                        granted = Math.Min(want, left);
                        camera.Working = granted;
                        camera.Opening = true;
                    }
                    else if (!others || waited.Elapsed >= _options.OpenTimeout)
                    {
                        return false;
                    }
                }
            }
            finally
            {
                Stop(stops);
            }
            if (granted > 0)
            {
                ceiling = granted / BytesPerPixel;
                return true;
            }
            if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(50))) token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>The decoder's room once its first frame says its size: <paramref name="bytes"/>, no more than it had.</summary>
    private void Settle(Camera camera, CameraRun run, long bytes)
    {
        lock (_lock)
        {
            if (camera.Run != run) return;
            camera.Working = Math.Min(camera.Working, bytes);
            camera.Opening = false;
            run.Need = 0;
        }
    }

    /// <summary>A frame decoded: the camera's newest, its status cleared and its failures forgotten.</summary>
    private void Publish(Camera camera, CameraRun run, MediaSample sample)
    {
        lock (_lock)
        {
            if (camera.Run != run || !_cameras.ContainsKey(camera.Address))
            {
                sample.Release();
                return;
            }
            _budget?.Charge(sample.Bytes);
            Release(camera.Previous);
            camera.Previous = camera.Current;
            camera.Current = new Frame(++camera.Seq, sample);
            camera.Status = null;
            camera.Failures = 0;
        }
    }

    /// <summary>
    /// A connection whose pictures grew past its room after a frame: null to connect again at once, when the stream had
    /// been steady for <see cref="SteadyAfter"/> since it last started again; else a failure (<c>media.provider</c>) whose
    /// wait doubles with each change in a row, from 1 s up to 60 s.
    /// </summary>
    private TimeSpan? Churned(Camera camera, CameraRun run)
    {
        lock (_lock)
        {
            if (camera.Run != run) return null;
            var now = Now();
            camera.Changes = now - camera.SteadySince < SteadyAfter ? Math.Min(camera.Changes + 1, 30) : 0;
            if (camera.Changes == 0)
            {
                camera.SteadySince = now;
                return null;
            }
            camera.Status = KeepsChanging;
            camera.Working = 0;
            camera.Opening = false;
            var wait = TimeSpan.FromSeconds(Math.Min(_options.RetryFirst.TotalSeconds * Math.Pow(2, camera.Changes - 1), _options.RetryMost.TotalSeconds));
            camera.SteadySince = now + (long)wait.TotalMilliseconds;
            return wait;
        }
    }

    /// <summary>A connection failed with <paramref name="status"/>: the wait before the next, doubling from 1 s up to 60 s.</summary>
    private TimeSpan Failed(Camera camera, CameraRun run, MediaStatus status)
    {
        lock (_lock)
        {
            var failures = camera.Failures;
            if (camera.Run == run)
            {
                camera.Status = status;
                camera.Working = 0;
                camera.Opening = false;
                failures = Math.Min(++camera.Failures, 30);
            }
            var wait = _options.RetryFirst.TotalSeconds * Math.Pow(2, failures - 1);
            return TimeSpan.FromSeconds(Math.Min(wait, _options.RetryMost.TotalSeconds));
        }
    }

    /// <summary>
    /// Whether <paramref name="reference"/> is an address the provider takes: absolute <c>rtsp</c> or <c>rtsps</c>, trimmed,
    /// within <paramref name="maxLength"/> characters, with a host that has no <c>%</c>. <paramref name="address"/> is the
    /// trimmed reference (a camera's key).
    /// </summary>
    internal static bool TryAddress(string reference, int maxLength, out string address, out Uri uri)
    {
        address = reference.Trim();
        uri = null!;
        if (address.Length is 0 || address.Length > maxLength) return false;
        if (HostHasPercent(address) || !Uri.TryCreate(address, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("rtsp" or "rtsps") || parsed.Host.Length == 0 || parsed.Host.Contains('%')) return false;
        uri = parsed;
        return true;
    }

    /// <summary>Whether the host of an absolute address, as written, has a <c>%</c> (an escape, or an IPv6 zone).</summary>
    private static bool HostHasPercent(string address) => Authority(address).Contains('%');

    /// <summary>An absolute address's host and port as written (after any user information), or empty.</summary>
    private static string Authority(string address)
    {
        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return "";
        var rest = address.AsSpan(scheme + 3);
        var end = rest.IndexOfAny('/', '?', '#');
        var authority = end < 0 ? rest : rest[..end];
        var at = authority.LastIndexOf('@');
        return (at < 0 ? authority : authority[(at + 1)..]).ToString();
    }

    public void Dispose()
    {
        List<CancellationTokenSource>? stops = null;
        List<Thread> threads;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            threads = [.. _cameras.Values.Select(c => c.Run?.Thread).OfType<Thread>()];
            foreach (var camera in _cameras.Values) Drop(camera, ref stops);
            _cameras.Clear();
        }
        _expiry.Dispose();
        Stop(stops);
        foreach (var thread in threads) thread.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>A decoded frame a camera holds (one reference, counted in the budget), numbered in the camera's order.</summary>
    private sealed record Frame(long Seq, MediaSample Sample);

    /// <summary>
    /// What a tick's first live ask for a camera was answered with: its sample (none included) and status. The sample is
    /// the one that ask retained, which the tick holds until it ends; a later ask retains it again.
    /// </summary>
    private sealed record Choice(MediaSample? Sample, MediaStatus? Status);

    /// <summary>One run of a camera's thread: connecting, streaming and retrying until it is stopped.</summary>
    private sealed class CameraRun
    {
        public readonly CancellationTokenSource Stop = new();
        public Thread? Thread;

        /// <summary>Closed by the provider (idle, limits, reset): what it does after is nobody's.</summary>
        public bool Closed;

        /// <summary>The least room its decoder needs, once its pictures didn't fit less (0: no more than any picture's), until a first frame.</summary>
        public long Need;
    }

    /// <summary>One address's camera. Every field is read and written under the provider's lock.</summary>
    private sealed class Camera(string address, Uri uri)
    {
        public readonly string Address = address;
        public readonly Uri Uri = uri;

        /// <summary>The camera's own scheme and authority as written, without user information: what its requests name.</summary>
        public readonly string Base = uri.Scheme + "://" + Authority(address);

        public readonly Dictionary<object, long> Askers = new(ReferenceEqualityComparer.Instance);
        public long LastAsked;
        public CameraRun? Run;

        /// <summary>Its decoder has room (<see cref="Working"/>) but no frame yet to say its size.</summary>
        public bool Opening;

        public Frame? Current;
        public Frame? Previous;
        public long Seq;
        public MediaStatus? Status;
        public int Failures;
        public long Working;

        /// <summary>Changes of its pictures in a row, each within <see cref="SteadyAfter"/> of the stream starting again.</summary>
        public int Changes;

        /// <summary>When its stream last started again after a change of pictures (its clock's milliseconds).</summary>
        public long SteadySince = long.MinValue / 2;

        public bool AskedBy(object item, long since) => Askers.TryGetValue(item, out var at) && at >= since;
    }
}
