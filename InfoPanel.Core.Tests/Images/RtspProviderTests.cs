using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using InfoPanel.Core.Tests.Sensors;
using InfoPanel.Images;
using InfoPanel.Images.Video;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Images;

/// <summary>
/// The <c>rtsp</c> media provider (spec/docs/providers.md, "rtsp: camera streams") against <see cref="RtspTestServer"/>,
/// a camera on 127.0.0.1 that sends the H.264 fixture's frames: no test reaches past loopback. Names resolve through a
/// fake resolver, and every connection, whatever address it was allowed to, is made to the test camera and recorded, so
/// the address a connection was checked for is the one it used. The camera's frames are waited for by asking as ticks
/// do, a few milliseconds apart. Skipped, saying why, without FFmpeg's libraries.
/// </summary>
public sealed class RtspProviderTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A provider over the test camera, a fake resolver and a recording connector.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public readonly RtspTestServer Server;
        public readonly MediaSampleCache Cache;
        public readonly RtspProvider Provider;
        public readonly List<IPEndPoint> Connected = [];
        public readonly List<string> Resolved = [];
        public bool LocalNetwork = true;
        public Func<string, IPAddress[]?> Names = _ => null;

        /// <summary>The fake interface list: this computer's addresses with their prefix lengths.</summary>
        public IReadOnlyList<InterfaceAddress> Own = [];

        /// <summary>How often the provider read the interface list.</summary>
        public int InterfaceReads;

        /// <summary>The port every allowed connection is made to on 127.0.0.1: the test camera's unless set.</summary>
        public int? ConnectPort;

        /// <summary>Each camera certificate chain built, as the policy it was built by: downloads off, revocation mode.</summary>
        public readonly List<(bool DownloadsOff, X509RevocationMode Revocation)> Chains = [];

        public Rig(TimeProvider? time = null, int maxCameras = 8, int maxCamerasPerItem = 4, RtspTestServer? server = null,
            SpecLimits? limits = null, long? workingBytes = null, Action? cancelling = null)
        {
            var ffmpeg = FFmpegForTests.Library;
            Server = server ?? new RtspTestServer();
            Cache = new MediaSampleCache(new NoAssets(), new MediaSampleCacheOptions { Synchronous = true });
            Provider = new RtspProvider(ffmpeg, Cache, new RtspProviderOptions
            {
                Time = time ?? TimeProvider.System,
                LocalNetwork = () => LocalNetwork,
                Interfaces = () =>
                {
                    Interlocked.Increment(ref InterfaceReads);
                    return Own;
                },
                ChainBuilt = chain =>
                {
                    lock (Chains) Chains.Add((chain!.ChainPolicy.DisableCertificateDownloads, chain.ChainPolicy.RevocationMode));
                },
                MaxCameras = maxCameras,
                MaxCamerasPerItem = maxCamerasPerItem,
                Limits = limits ?? SpecLimits.Default,
                WorkingBytes = workingBytes ?? 768L * 1024 * 1024,
                Cancelling = cancelling,
                UserAgent = "InfoPanel/2.0.0",
                Resolve = (host, _) =>
                {
                    lock (Resolved) Resolved.Add(host);
                    return Task.FromResult(Names(host) ?? throw new SocketException((int)SocketError.HostNotFound));
                },
                Connect = (endpoint, token) =>
                {
                    lock (Connected) Connected.Add(endpoint);
                    return HttpMediaProvider.ConnectSocketAsync(new IPEndPoint(IPAddress.Loopback, ConnectPort ?? Server.Port), token);
                },
            });
        }

        public ProvidedMedia Ask(string address, object? item = null, bool live = true, bool publicOnly = false) => item is null
            ? Provider.Get(new MediaRequest(address, 0, MediaPlayback.Loop, 0, live, publicOnly))
            : Provider.Get(new MediaRequest(address, 0, MediaPlayback.Loop, 0, live, publicOnly), new MediaAsk(new MediaTick(), item));

        /// <summary>Waits until no camera connection is open, then says whether none is.</summary>
        public async Task<bool> AllClosed(TimeSpan within)
        {
            var closed = Stopwatch.StartNew();
            while (Provider.OpenConnections > 0 && closed.Elapsed < within) await Task.Delay(10, Ct);
            return Provider.OpenConnections == 0;
        }

        /// <summary>Asks as ticks do, every few milliseconds, until an answer passes <paramref name="until"/>: that answer (retained).</summary>
        public async Task<ProvidedMedia> AskUntil(string address, Func<ProvidedMedia, bool> until, object? item = null, TimeSpan? within = null, bool publicOnly = false)
        {
            var deadline = Stopwatch.StartNew();
            ProvidedMedia last = default;
            while (deadline.Elapsed < (within ?? Wait))
            {
                last.Sample?.Release();
                last = Ask(address, item, publicOnly: publicOnly);
                if (until(last)) return last;
                await Task.Delay(5, Ct);
            }
            var status = last.Status;
            last.Sample?.Release();
            Assert.Fail($"No such answer for {address} within {(within ?? Wait).TotalSeconds} s; the last: sample {(last.Sample is null ? "none" : "one")}, status {status}.");
            return default;
        }

        public async ValueTask DisposeAsync()
        {
            Provider.Dispose();
            await Server.DisposeAsync();
            Cache.Dispose();
        }
    }

    private sealed class NoAssets : IMediaAssetStore
    {
        public byte[]? Read(string hash) => null;
    }

    /// <summary>The fixture frame a sample shows (its centre's colour).</summary>
    private static int FrameOf(MediaSample sample)
    {
        using var bitmap = SKBitmap.FromImage(sample.Image!);
        return VideoFixtures.FrameOf(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
    }

    // ---- connecting ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Connect_OverTcpOnly_ShowsTheCamerasFrame()
    {
        await using var rig = new Rig();
        var url = rig.Server.Url();
        var first = await rig.AskUntil(url, m => m.Sample is not null);
        Assert.Equal((VideoFixtures.Width, VideoFixtures.Height), ((int)first.Sample!.Width, (int)first.Sample.Height));
        Assert.InRange(FrameOf(first.Sample), 0, VideoFixtures.Frames - 1);
        first.Sample.Release();

        var requests = rig.Server.Requests;
        Assert.Contains(requests, r => r.StartsWith("DESCRIBE ", StringComparison.Ordinal));
        Assert.Contains(requests, r => r.StartsWith("PLAY ", StringComparison.Ordinal));
        // Every SETUP asked for RTP interleaved on the RTSP connection: no UDP port is ever opened.
        var setups = requests.Where(r => r.StartsWith("SETUP ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(setups);
        Assert.All(setups, s => Assert.Contains("| RTP/AVP/TCP", s));
        Assert.All(setups, s => Assert.Contains("interleaved", s));
        Assert.Equal(1, rig.Server.Connections);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, rig.Server.Port), Assert.Single(rig.Connected));
    }

    [Fact]
    public async Task LiveFrames_FollowTheCamera()
    {
        await using var rig = new Rig();
        var url = rig.Server.Url();
        var seen = new HashSet<int>();
        (await rig.AskUntil(url, m =>
        {
            if (m.Sample is { } s) seen.Add(FrameOf(s));
            return seen.Count >= 4;
        })).Sample?.Release();
        Assert.True(seen.Count >= 4, string.Join(",", seen));
    }

    [Fact]
    public async Task ACameraThatDrops_KeepsItsLastPicture_AndReconnectsByItself()
    {
        await using var rig = new Rig();
        var url = rig.Server.Url();
        (await rig.AskUntil(url, m => m.Sample is not null)).Sample!.Release();

        rig.Server.Drop();
        // The stream ended: its last picture stays, and the status says why.
        var dropped = await rig.AskUntil(url, m => m.Status is { Code: "media.provider" });
        Assert.NotNull(dropped.Sample);
        dropped.Sample.Release();

        rig.Server.Resume();
        // It connects again on its own (1 s after the first failure) and shows live frames again, with no status.
        var back = await rig.AskUntil(url, m => rig.Server.Connections >= 2 && m.Sample is not null && m.Status is null);
        back.Sample!.Release();
        var seen = new HashSet<int>();
        (await rig.AskUntil(url, m =>
        {
            if (m.Sample is { } s) seen.Add(FrameOf(s));
            return seen.Count >= 3;
        })).Sample?.Release();
    }

    [Fact]
    public async Task FramesArrive_WithinHalfASecond()
    {
        await using var rig = new Rig();
        var url = rig.Server.Url();
        (await rig.AskUntil(url, m => m.Sample is not null)).Sample!.Release();

        // Each change of the frame shown, against when the camera began to send that frame.
        var latencies = new List<double>();
        var shown = -1;
        (await rig.AskUntil(url, m =>
        {
            if (m.Sample is not { } s) return false;
            var frame = FrameOf(s);
            if (frame != shown && shown >= 0 && rig.Server.SentAt(frame) is { } sent)
                latencies.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
            shown = frame;
            return latencies.Count >= 10;
        })).Sample?.Release();
        var listed = string.Join(", ", latencies.Select(l => l.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)));
        TestContext.Current.TestOutputHelper?.WriteLine($"latencies (ms): {listed}");
        Assert.True(latencies.Max() < 500, $"latencies (ms): {listed}");
    }

    // ---- what it may connect to ---------------------------------------------------------------------------------

    [Fact]
    public async Task AnAddressTheRulesRefuse_IsNeverConnectedTo()
    {
        await using var rig = new Rig();
        rig.LocalNetwork = false;
        var local = await rig.AskUntil(rig.Server.Url(), m => m.Status is { Code: "media.address" });
        Assert.Equal("127.0.0.1 is on this computer.", local.Status!.Message);
        Assert.Null(local.Sample);

        // A name is resolved by the provider and checked before anything connects: a link-local answer is refused.
        rig.Names = host => host == "camera.test" ? [IPAddress.Parse("169.254.169.254")] : null;
        var metadata = await rig.AskUntil("rtsp://camera.test:8554/live", m => m.Status is { Code: "media.address" });
        Assert.Equal("169.254.169.254 is a link-local address.", metadata.Status!.Message);
        Assert.Contains("camera.test", rig.Resolved);

        Assert.Empty(rig.Connected);
        Assert.Equal(0, rig.Server.Connections);
    }

    private const string Only = ", and this source may reach only public addresses.";

    [Theory]
    [InlineData("nas.test", "192.168.1.20", "media.reach", "192.168.1.20 is on your local network" + Only)]
    [InlineData("loop.test", "127.0.0.1", "media.reach", "127.0.0.1 is on this computer" + Only)]
    [InlineData("self6.test", "2001:db8:1:2::5", "media.reach", "2001:db8:1:2::5 is this computer's own address" + Only)]
    [InlineData("neighbour.test", "2001:db8:1:2::9", "media.reach", "2001:db8:1:2::9 is on your local network" + Only)]
    [InlineData("neighbour4.test", "198.51.100.15", "media.reach", "198.51.100.15 is on your local network" + Only)]
    [InlineData("link.test", "169.254.3.4", "media.address", "169.254.3.4 is a link-local address.")]
    public async Task AMarkedCamera_IsRefusedInwardAddresses_WithTheLocalNetworkSettingOn(string host, string address, string code, string why)
    {
        await using var rig = new Rig();
        rig.LocalNetwork = true;
        rig.Own = PublicReachTests.Home;
        rig.Names = h => h == host ? [IPAddress.Parse(address)] : null;
        var refused = await rig.AskUntil($"rtsp://{host}:8554/live", m => m.Status is { Code: "media.reach" or "media.address" }, publicOnly: true);
        Assert.Equal(new MediaStatus(code, why), refused.Status);
        Assert.Null(refused.Sample);
        Assert.Empty(rig.Connected);
        Assert.Equal(0, rig.Server.Connections);
        Assert.True(Volatile.Read(ref rig.InterfaceReads) >= 1);
    }

    [Fact]
    public async Task AMarkedCamera_AtAPublicAddress_IsShown()
    {
        var server = new RtspTestServer { Authority = "rtsp://camera.test:8554" };
        await using var rig = new Rig(server: server);
        rig.Own = PublicReachTests.Home;
        rig.Names = host => host == "camera.test" ? [IPAddress.Parse("203.0.113.7")] : null;
        var shown = await rig.AskUntil("rtsp://camera.test:8554/live", m => m.Sample is not null, publicOnly: true);
        shown.Sample!.Release();
        lock (rig.Connected) Assert.All(rig.Connected, e => Assert.Equal(IPAddress.Parse("203.0.113.7"), e.Address));
        var reads = Volatile.Read(ref rig.InterfaceReads);
        Assert.True(reads >= 1);
        var connected = rig.Server.Connections;

        // The computer joins the camera's network and the stream drops: its reconnection reads the list again and is
        // refused, the last picture kept.
        rig.Own = [.. PublicReachTests.Home, new(IPAddress.Parse("203.0.113.1"), 24)];
        rig.Server.Drop();
        var refused = await rig.AskUntil("rtsp://camera.test:8554/live", m => m.Status is { Code: "media.reach" }, publicOnly: true);
        Assert.Equal("203.0.113.7 is on your local network" + Only, refused.Status!.Message);
        Assert.NotNull(refused.Sample);
        refused.Sample.Release();
        Assert.True(Volatile.Read(ref rig.InterfaceReads) > reads);
        Assert.Equal(connected, rig.Server.Connections);
    }

    [Fact]
    public async Task AMarkedRtspsCamera_DownloadsNothingItsCertificateNames()
    {
        // A camera at a public address presents a certificate whose missing issuer and revocation list are on an inward
        // address (a second listener on 127.0.0.1): the marked camera's chain is built with downloads off and no
        // revocation check, and nothing reaches the listener.
        await using var hostile = new HostileCertificateServer("camera.test");
        await using var rig = new Rig();
        rig.ConnectPort = hostile.Port;
        rig.Own = PublicReachTests.Home;
        rig.Names = host => host == "camera.test" ? [IPAddress.Parse("203.0.113.7")] : null;
        var url = $"rtsps://camera.test:{hostile.Port}/live";

        var marked = await rig.AskUntil(url, m => m.Status is { Code: "media.provider" }, publicOnly: true);
        Assert.Null(marked.Sample);
        Assert.Equal("The camera's certificate isn't trusted.", marked.Status!.Message);
        lock (rig.Chains) Assert.All(rig.Chains, c => Assert.Equal((true, X509RevocationMode.NoCheck), c));
        lock (rig.Chains) Assert.NotEmpty(rig.Chains);
        Assert.Equal(0, hostile.InwardConnections);
        lock (rig.Connected) Assert.All(rig.Connected, e => Assert.Equal(IPAddress.Parse("203.0.113.7"), e.Address));

        // An unmarked camera's certificate is checked as before: the platform's defaults, downloads included.
        var unmarked = await rig.AskUntil(url, m => m.Status is { Code: "media.provider" });
        Assert.Null(unmarked.Sample);
        lock (rig.Chains) Assert.Contains((false, X509RevocationMode.NoCheck), rig.Chains);
    }

    [Fact]
    public async Task AMarkedAndAnUnmarkedSource_OnOneAddress_NeverShareACameraOrAPicture()
    {
        await using var rig = new Rig();
        var url = rig.Server.Url();
        var unmarked = await rig.AskUntil(url, m => m.Sample is not null);
        unmarked.Sample!.Release();
        var connections = rig.Server.Connections;

        // The marked one has a camera of its own: it never shows the unmarked one's frame, and is refused.
        var first = rig.Ask(url, publicOnly: true);
        Assert.Null(first.Sample);
        var marked = await rig.AskUntil(url, m => m.Status is { Code: "media.reach" }, publicOnly: true);
        Assert.Null(marked.Sample);
        Assert.Equal("127.0.0.1 is on this computer" + Only, marked.Status!.Message);
        Assert.Equal(2, rig.Provider.CameraCount);
        Assert.Equal(connections, rig.Server.Connections);

        // The unmarked one goes on showing its own.
        var still = await rig.AskUntil(url, m => m.Sample is not null);
        Assert.Null(still.Status);
        still.Sample!.Release();
    }

    [Fact]
    public async Task ARedirect_IsNotFollowed()
    {
        await using var rig = new Rig();
        // A redirect FFmpeg would follow by itself, past the provider's check (to the test camera again, so nothing leaves
        // loopback even when it is followed).
        rig.Server.Redirect = (302, rig.Server.Url("/elsewhere"));
        var redirected = await rig.AskUntil(rig.Server.Url(), m => m.Status is { Code: "media.provider" });
        Assert.Equal("The camera redirects to another address, which isn't followed.", redirected.Status!.Message);
        // Every connection made is one the provider checked: the redirect's was never made.
        Assert.Equal(rig.Connected.Count, rig.Server.Connections);
        Assert.All(rig.Connected, e => Assert.Equal(new IPEndPoint(IPAddress.Loopback, rig.Server.Port), e));
        Assert.DoesNotContain(rig.Server.Requests, r => r.Contains("/elsewhere", StringComparison.Ordinal));
        Assert.Contains(rig.Server.Requests, r => r.StartsWith("DESCRIBE ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://127.0.0.1/camera")]
    [InlineData("camera.local/live")]
    [InlineData("rtsp://")]
    [InlineData("rtsp://fe80::1%25en0/live")]
    public async Task NotACameraAddress_IsRefused(string reference)
    {
        await using var rig = new Rig();
        var answer = rig.Ask(reference);
        Assert.Null(answer.Sample);
        Assert.Equal(new MediaStatus("media.provider", "Not a camera stream address."), answer.Status);
        Assert.Empty(rig.Connected);
    }

    // ---- what it keeps open -------------------------------------------------------------------------------------

    [Fact]
    public async Task ACameraNoTickAsksFor_ClosesAfter30Seconds()
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        await using var rig = new Rig(time);
        var url = rig.Server.Url();
        (await rig.AskUntil(url, m => m.Sample is not null)).Sample!.Release();
        Assert.Equal(1, rig.Provider.CameraCount);

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(1, rig.Provider.CameraCount);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, rig.Provider.CameraCount);
        // Its connection is closed: the camera is told so (TEARDOWN) or sees it drop.
        var closed = Stopwatch.StartNew();
        while (rig.Provider.OpenConnections > 0 && closed.Elapsed < Wait) await Task.Delay(10, Ct);
        Assert.Equal(0, rig.Provider.OpenConnections);
        // A one-off render answers what the provider has, now nothing, and starts nothing.
        Assert.Null(rig.Ask(url, live: false).Sample);
        Assert.Equal(0, rig.Provider.CameraCount);
    }

    [Fact]
    public async Task PastTheCameraLimit_ANewCameraIsRefusedWithMediaBudget()
    {
        await using var rig = new Rig(maxCameras: 1);
        var item = new object();
        (await rig.AskUntil(rig.Server.Url("/a"), m => m.Sample is not null, item)).Sample!.Release();
        var refused = rig.Ask(rig.Server.Url("/b"), item);
        Assert.Equal("media.budget", refused.Status?.Code);
        Assert.Null(refused.Sample);
        Assert.Equal(1, rig.Server.Connections);
    }

    // ---- what the camera may send (hostile framing) -------------------------------------------------------------

    private const string Malformed = "The camera's answer isn't valid RTSP.";

    /// <summary>
    /// Where a redirect points: a listener on loopback that answers nothing, so a redirect followed shows as a connection
    /// waiting there (and is followed once, not round and round).
    /// </summary>
    private sealed class Elsewhere : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public Elsewhere() => _listener.Start();

        public string Url => $"rtsp://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/elsewhere";

        public bool Reached => _listener.Pending();

        public void Dispose() => _listener.Stop();
    }

    /// <summary>Asserts that no connection went anywhere the provider didn't check.</summary>
    private static void NothingFollowed(Rig rig, Elsewhere elsewhere)
    {
        Assert.False(elsewhere.Reached, "A redirect was followed.");
        Assert.Equal(rig.Connected.Count, rig.Server.Connections);
        Assert.All(rig.Connected, e => Assert.Equal(new IPEndPoint(IPAddress.Loopback, rig.Server.Port), e));
    }

    [Theory]
    [InlineData("two spaces")]
    [InlineData("a tab")]
    [InlineData("a leading zero")]
    [InlineData("a bare line feed")]
    public async Task ARedirectInAnyForm_IsNeverFollowed(string form)
    {
        await using var rig = new Rig();
        using var away = new Elsewhere();
        var elsewhere = away.Url;
        rig.Server.Raw = (method, cseq) => (form, method) switch
        {
            ("two spaces", "DESCRIBE") => $"RTSP/1.0  302 Moved\r\nCSeq: {cseq}\r\nLocation: {elsewhere}\r\n\r\n",
            ("a tab", "DESCRIBE") => $"RTSP/1.0 302\tMoved\r\nCSeq: {cseq}\r\nLocation: {elsewhere}\r\n\r\n",
            ("a leading zero", "DESCRIBE") => $"RTSP/1.0 0302 Moved\r\nCSeq: {cseq}\r\nLocation: {elsewhere}\r\n\r\n",
            // A header that ends at a bare line feed then CRLF, which FFmpeg reads as its end: what follows is the answer
            // to the next request (DESCRIBE), a redirect.
            ("a bare line feed", "OPTIONS") => $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\n\r\nRTSP/1.0 302 Moved\r\nCSeq: {int.Parse(cseq, System.Globalization.CultureInfo.InvariantCulture) + 1}\r\nLocation: {elsewhere}\r\n\r\n",
            _ => null,
        };
        var refused = await rig.AskUntil(rig.Server.Url(), m => m.Status is { Code: "media.provider" } || away.Reached, within: TimeSpan.FromSeconds(10));
        Assert.Null(refused.Sample);
        NothingFollowed(rig, away);
        Assert.Equal("media.provider", refused.Status?.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TwoContentLengths_AreRefused(bool longerFirst)
    {
        await using var rig = new Rig();
        using var away = new Elsewhere();
        var elsewhere = away.Url;
        rig.Server.Raw = (method, cseq) =>
        {
            if (method != "DESCRIBE") return null;
            if (longerFirst)
            {
                // A request of the camera's whose body (by its first length) hides a redirect FFmpeg would read (by its last).
                var hidden = $"RTSP/1.0 302 Moved\r\nCSeq: {cseq}\r\nLocation: {elsewhere}\r\n\r\n";
                return $"OPTIONS * RTSP/1.0\r\nCSeq: 1\r\nContent-Length: {hidden.Length}\r\nContent-Length: 0\r\n\r\n{hidden}";
            }
            // A body over 1 MiB by the last length, none by the first: interleaved packets the relay would pass as they come.
            var packet = "$\0" + (char)(1000 >> 8) + (char)(1000 & 0xFF) + new string('U', 1000);
            var body = string.Concat(Enumerable.Repeat(packet, 1992));
            return $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nContent-Length: 0\r\nContent-Length: {body.Length}\r\n\r\n{body}";
        };
        var refused = await rig.AskUntil(rig.Server.Url(), m => m.Status is { Code: "media.provider" } || away.Reached, within: TimeSpan.FromSeconds(10));
        NothingFollowed(rig, away);
        Assert.Equal(new MediaStatus("media.provider", Malformed), refused.Status);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task FragmentsThatNeverMakeAPicture_EndTheStreamAtMediaBytes(int channel, bool marker)
    {
        await using var rig = new Rig(limits: SpecLimits.Default with { MediaBytes = 1024 * 1024 });
        rig.Server.Flood = (channel, marker);
        var ended = await rig.AskUntil(rig.Server.Url(), m => m.Status is { Code: "limit.mediaBytes" }, within: TimeSpan.FromSeconds(10));
        Assert.Null(ended.Sample);
    }

    // ---- the camera's own address and credentials ---------------------------------------------------------------

    [Fact]
    public async Task ACameraThatChecksItsAddressAndDigest_IsAskedAtItsOwnAddress_WithItsCredentials()
    {
        var server = new RtspTestServer { Authority = "rtsp://camera.test:8554", Digest = ("viewer", "p@ss word") };
        await using var rig = new Rig(server: server);
        rig.Names = host => host == "camera.test" ? [IPAddress.Loopback] : null;
        var shown = await rig.AskUntil("rtsp://viewer:p%40ss%20word@camera.test:8554/live", m => m.Sample is not null);
        shown.Sample!.Release();
        Assert.True(server.Challenges >= 1);
        // Every request named the camera's own address (the relay's never reached it), and was signed for that address.
        Assert.All(server.Requests, r => Assert.StartsWith("rtsp://camera.test:8554/live", r.Split(' ')[1], StringComparison.Ordinal));
        Assert.Contains(server.Requests, r => r.StartsWith("PLAY ", StringComparison.Ordinal));

        var refused = await rig.AskUntil("rtsp://viewer:wrong@camera.test:8554/live", m => m.Status is { Code: "media.provider" });
        Assert.Equal("The camera refused the user name and password.", refused.Status!.Message);
        Assert.Null(refused.Sample);
    }

    // ---- what a camera's decoder may hold -----------------------------------------------------------------------

    [Fact]
    public async Task ACameraThatGrowsPastTheDecodersBudget_NeverShowsTheLargerPictures()
    {
        // 8 MiB of decoder memory: room for 64 x 48 pictures, not for 640 x 480 ones.
        var server = new RtspTestServer(switchTo: "camera-h264-640x480.mp4", switchAfter: 15);
        await using var rig = new Rig(server: server, workingBytes: 8L * 1024 * 1024);
        var url = server.Url();
        (await rig.AskUntil(url, m => m.Sample is { Width: VideoFixtures.Width })).Sample!.Release();
        var after = await rig.AskUntil(url, m => m.Status is { Code: "media.budget" } || m.Sample is { Width: 640 });
        Assert.NotEqual(640, after.Sample?.Width);
        Assert.Equal("media.budget", after.Status?.Code);
        after.Sample?.Release();
    }

    [Fact]
    public async Task ACameraThatGrowsWithinTheBudget_ShowsTheLargerPictures()
    {
        var server = new RtspTestServer(switchTo: "camera-h264-640x480.mp4", switchAfter: 15);
        await using var rig = new Rig(server: server);
        var url = server.Url();
        (await rig.AskUntil(url, m => m.Sample is { Width: VideoFixtures.Width })).Sample!.Release();
        var larger = await rig.AskUntil(url, m => m.Sample is { Width: 640, Height: 480 } && m.Status is null);
        Assert.InRange(FrameOf(larger.Sample!), 0, VideoFixtures.Frames - 1);
        larger.Sample!.Release();
    }

    [Fact]
    public async Task PicturesInAFormatTheDecodersArentBudgetedFor_AreRefused()
    {
        var server = new RtspTestServer(fixture: "camera-h264-444p10.mp4");
        await using var rig = new Rig(server: server);
        var refused = await rig.AskUntil(server.Url(), m => m.Status is { Code: "media.codec" } || m.Sample is not null);
        Assert.Null(refused.Sample);
        Assert.Equal("media.codec", refused.Status?.Code);
    }

    [Fact]
    public async Task AnH265Camera_IsRefusedBeforeItPlays()
    {
        // H.264 only for now: an HEVC decoder sizes its tables from a parameter set before the pictures' size is checked.
        var server = new RtspTestServer { Hevc = true };
        await using var rig = new Rig(server: server);
        var refused = await rig.AskUntil(server.Url(), m => m.Status is { Code: not "media.loading" } || m.Sample is not null);
        Assert.Null(refused.Sample);
        Assert.Equal("media.codec", refused.Status?.Code);
        Assert.Equal("The camera sends H.265 (HEVC) video; the engine shows H.264 cameras only for now.", refused.Status!.Message);
        Assert.DoesNotContain(server.Requests, r => r.StartsWith("PLAY ", StringComparison.Ordinal));
    }

    // ---- stopping what was closed -------------------------------------------------------------------------------

    /// <summary>A clock moved by hand whose timers never fire: only asks can notice time passing.</summary>
    private sealed class HandClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Never();

        private sealed class Never : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task AOneOffAskThatDropsAnIdleCamera_ClosesItsConnection()
    {
        var clock = new HandClock();
        await using var rig = new Rig(clock);
        (await rig.AskUntil(rig.Server.Url(), m => m.Sample is not null)).Sample!.Release();
        Assert.Equal(1, rig.Provider.OpenConnections);

        clock.Advance(TimeSpan.FromSeconds(31));
        // A one-off render of an address with no camera: it starts nothing, and drops the camera no tick asked for.
        Assert.Null(rig.Ask("rtsp://127.0.0.1:9/none", live: false).Sample);
        Assert.Equal(0, rig.Provider.CameraCount);
        Assert.True(await rig.AllClosed(TimeSpan.FromSeconds(10)), "The dropped camera's connection is still open.");
    }

    [Fact]
    public async Task ACameraClosedThenReopenedBeforeItsStopRuns_StopsTheRunItClosed()
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        Rig? rig = null;
        var fired = 0;
        string x = "", y = "";
        rig = new Rig(time, maxCameras: 1, cancelling: () =>
        {
            if (Interlocked.Exchange(ref fired, 1) != 0) return;
            // Between closing X's run (to open Y) and stopping it: X reopens (closing Y), then every camera is dropped.
            time.Advance(TimeSpan.FromSeconds(3));
            rig!.Ask(x).Sample?.Release();
            rig.Provider.Reset();
        });
        await using var _ = rig;
        x = rig.Server.Url("/x");
        y = rig.Server.Url("/y");
        (await rig.AskUntil(x, m => m.Sample is not null)).Sample!.Release();
        time.Advance(TimeSpan.FromSeconds(3)); // X idle: Y may close it
        rig.Ask(y).Sample?.Release();
        Assert.Equal(1, fired);
        Assert.True(await rig.AllClosed(TimeSpan.FromSeconds(10)), "A camera run nobody holds is still connected.");
    }

    // ---- one tick, one frame ------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryAskOfATick_ShowsWhatItsFirstAskChose_EvenNothing()
    {
        await using var rig = new Rig();
        var url = rig.Server.Url();
        var item = new object();
        var early = new MediaTick();
        var nothing = rig.Provider.Get(new MediaRequest(url, 0, MediaPlayback.Loop), new MediaAsk(early, item));
        Assert.Null(nothing.Sample);

        (await rig.AskUntil(url, m => m.Sample is not null, item)).Sample!.Release();
        // The tick chose nothing: a later ask in it shows nothing too, with the same status, though frames have come.
        var later = rig.Provider.Get(new MediaRequest(url, 1.5, MediaPlayback.Once), new MediaAsk(early, item));
        Assert.Null(later.Sample);
        Assert.Equal(nothing.Status, later.Status);

        var tick = new MediaTick();
        var first = rig.Provider.Get(new MediaRequest(url, 0, MediaPlayback.Loop), new MediaAsk(tick, item));
        Assert.NotNull(first.Sample);
        var chosen = FrameOf(first.Sample);
        await Task.Delay(TimeSpan.FromMilliseconds(450), Ct); // four frames or so later
        var again = rig.Provider.Get(new MediaRequest(url, 2.5, MediaPlayback.Still), new MediaAsk(tick, item));
        Assert.Same(first.Sample, again.Sample);
        Assert.Equal(chosen, FrameOf(again.Sample!));
        again.Sample!.Release();
        first.Sample.Release();
    }

    // ---- an item's cameras --------------------------------------------------------------------------------------

    [Fact]
    public async Task AnItemJoiningCamerasOthersOpened_IsHeldToItsLimit()
    {
        await using var rig = new Rig(maxCamerasPerItem: 2);
        string[] urls = [rig.Server.Url("/a"), rig.Server.Url("/b"), rig.Server.Url("/c")];
        foreach (var url in urls) (await rig.AskUntil(url, m => m.Sample is not null, new object())).Sample!.Release();

        var joiner = new object();
        (await rig.AskUntil(urls[0], m => m.Sample is not null, joiner)).Sample!.Release();
        (await rig.AskUntil(urls[1], m => m.Sample is not null, joiner)).Sample!.Release();
        var third = rig.Ask(urls[2], joiner);
        Assert.Null(third.Sample);
        Assert.Equal(new MediaStatus("media.budget", "Too many camera streams for one item: 2 are open."), third.Status);
        Assert.Equal(3, rig.Provider.OpenConnections);
    }

    // ---- pictures that change -----------------------------------------------------------------------------------

    [Fact]
    public async Task ACameraWhosePicturesKeepChanging_ConnectsAgainOnce_ThenBacksOff()
    {
        // Each connection sends 64 x 48 pictures, then 640 x 480 ones half a second later. The clock's timers never fire,
        // so a backoff never ends: the connections counted are those made at once.
        var server = new RtspTestServer(switchTo: "camera-h264-640x480.mp4", switchAfter: 5, alternate: true);
        await using var rig = new Rig(new HandClock(), server: server);
        var url = server.Url();
        var changing = await rig.AskUntil(url, m => m.Status is { Code: "media.provider", Message: "The camera's pictures keep changing size or format." },
            within: TimeSpan.FromSeconds(10));
        Assert.NotNull(changing.Sample); // the last picture stays
        changing.Sample.Release();
        var connections = server.Connections;
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(2, connections);
        Assert.Equal(connections, server.Connections);
    }

    [Fact]
    public async Task ACameraWhosePicturesShrink_KeepsItsConnection()
    {
        var server = new RtspTestServer(fixture: "camera-h264-640x480.mp4", switchTo: "clip-h264.mp4", switchAfter: 15);
        await using var rig = new Rig(server: server);
        var url = server.Url();
        (await rig.AskUntil(url, m => m.Sample is { Width: 640 })).Sample!.Release();
        var smaller = await rig.AskUntil(url, m => m.Sample is { Width: VideoFixtures.Width } && m.Status is null);
        smaller.Sample!.Release();
        // The smaller pictures fit the room the decoder has: FFmpeg asking for a format again isn't a change to connect for.
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task PicturesOverTheMediaArea_AreRefusedBeforeTheDecoderIsSizedForThem()
    {
        // 640 x 480 is over this media area; the decoder is asked for its format with the coded size before it allocates.
        var server = new RtspTestServer(fixture: "camera-h264-640x480.mp4");
        await using var rig = new Rig(server: server, limits: SpecLimits.Default with { MediaArea = 100_000 });
        var refused = await rig.AskUntil(server.Url(), m => m.Status is not null && m.Status.Code != "media.loading" || m.Sample is not null);
        Assert.Null(refused.Sample);
        Assert.Equal(new MediaStatus("media.malformed", "The camera's pictures are outside the media limits."), refused.Status);
    }

    // ---- cameras opening together -------------------------------------------------------------------------------

    [Fact]
    public async Task TwoCamerasOpeningTogether_NeitherWaitsForTheOther()
    {
        // The first camera opened sends its first frame only 8 s after PLAY; the second must not wait for it to have room.
        var server = new RtspTestServer { Slow = ("/slow", TimeSpan.FromSeconds(8)) };
        await using var rig = new Rig(server: server);
        var slow = server.Url("/slow");
        var fast = server.Url("/fast");
        rig.Ask(slow).Sample?.Release();
        var playing = Stopwatch.StartNew();
        while (!server.Requests.Any(r => r.StartsWith("PLAY ", StringComparison.Ordinal)) && playing.Elapsed < Wait) await Task.Delay(10, Ct);
        Assert.Contains(server.Requests, r => r.StartsWith("PLAY ", StringComparison.Ordinal));
        await Task.Delay(200, Ct);
        var opened = Stopwatch.StartNew();
        var shown = await rig.AskUntil(fast, m =>
        {
            rig.Ask(slow).Sample?.Release();
            return m.Sample is not null;
        }, within: TimeSpan.FromSeconds(6));
        shown.Sample!.Release();
        Assert.True(opened.Elapsed < TimeSpan.FromSeconds(4), $"The second camera's first frame took {opened.Elapsed.TotalSeconds:F1} s.");
    }

    [Fact]
    public async Task CamerasOpeningTogether_TakeTheRoomTheirDescriptionGives()
    {
        // 4 MiB between the decoders: room for two 64 x 48 cameras as their SDP's parameter sets give them, and less than
        // a 4K UHD picture's for one. The first sends its first frame only 8 s after PLAY.
        var server = new RtspTestServer { Slow = ("/slow", TimeSpan.FromSeconds(8)) };
        await using var rig = new Rig(server: server, workingBytes: 4L * 1024 * 1024);
        var slow = server.Url("/slow");
        rig.Ask(slow).Sample?.Release();
        var playing = Stopwatch.StartNew();
        while (!server.Requests.Any(r => r.StartsWith("PLAY ", StringComparison.Ordinal)) && playing.Elapsed < Wait) await Task.Delay(10, Ct);
        Assert.Contains(server.Requests, r => r.StartsWith("PLAY ", StringComparison.Ordinal));
        await Task.Delay(200, Ct);
        var opened = Stopwatch.StartNew();
        var shown = await rig.AskUntil(server.Url("/fast"), m =>
        {
            rig.Ask(slow).Sample?.Release();
            return m.Sample is not null;
        }, within: TimeSpan.FromSeconds(6));
        shown.Sample!.Release();
        Assert.True(opened.Elapsed < TimeSpan.FromSeconds(4), $"The second camera's first frame took {opened.Elapsed.TotalSeconds:F1} s.");
    }

    // ---- signing in ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACameraAskingForDigestWithSha256_IsSignedInto()
    {
        var server = new RtspTestServer { Authority = "rtsp://camera.test:8554", Digest = ("viewer", "secret"), DigestAlgorithm = "SHA-256" };
        await using var rig = new Rig(server: server);
        rig.Names = host => host == "camera.test" ? [IPAddress.Loopback] : null;
        var shown = await rig.AskUntil("rtsp://viewer:secret@camera.test:8554/live", m => m.Sample is not null);
        shown.Sample!.Release();
        Assert.Contains(server.Authorizations, a => a.Contains("algorithm=SHA-256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADigestTheEngineCantAnswer_BesideBasic_NeverSendsThePasswordInTheClear()
    {
        var server = new RtspTestServer { Digest = ("viewer", "secret"), DigestAlgorithm = "SHA-512-256", OtherChallenge = "Basic realm=\"camera\"" };
        await using var rig = new Rig(server: server);
        var url = $"rtsp://viewer:secret@127.0.0.1:{server.Port}/camera";
        var refused = await rig.AskUntil(url, m => m.Status is { Code: "media.provider" });
        Assert.Equal("The camera asks for a kind of sign-in the engine doesn't have.", refused.Status!.Message);
        Assert.Null(refused.Sample);
        Assert.True(server.Challenges >= 1);
        Assert.Empty(server.Authorizations);
    }

    // One header, two challenges (RFC 9110 allows it), a quoted comma in the first realm: the Digest is never missed.
    private const string BasicWithAComma = "Basic realm=\"camera, front door\", charset=\"UTF-8\"";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADigestTheEngineCantAnswer_BesideBasicInOneHeader_NeverSendsThePasswordInTheClear(bool basicFirst)
    {
        var server = new RtspTestServer
        {
            Digest = ("viewer", "secret"), DigestAlgorithm = "SHA-512-256", OtherChallenge = BasicWithAComma, OneHeader = basicFirst,
        };
        await using var rig = new Rig(server: server);
        var url = $"rtsp://viewer:secret@127.0.0.1:{server.Port}/camera";
        var refused = await rig.AskUntil(url, m => m.Status is { Code: "media.provider" });
        Assert.Equal("The camera asks for a kind of sign-in the engine doesn't have.", refused.Status!.Message);
        Assert.Null(refused.Sample);
        Assert.True(server.Challenges >= 1);
        Assert.Empty(server.Authorizations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADigestBesideBasicInOneHeader_IsTheOneAnswered(bool basicFirst)
    {
        var server = new RtspTestServer
        {
            Digest = ("viewer", "secret"), DigestAlgorithm = "SHA-256", OtherChallenge = BasicWithAComma, OneHeader = basicFirst,
        };
        await using var rig = new Rig(server: server);
        var shown = await rig.AskUntil($"rtsp://viewer:secret@127.0.0.1:{server.Port}/camera", m => m.Sample is not null || m.Status is { Code: not "media.loading" });
        Assert.Null(shown.Status);
        shown.Sample!.Release();
        Assert.NotEmpty(server.Authorizations);
        Assert.All(server.Authorizations, a => Assert.StartsWith("Digest ", a, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ControlCharactersInTheCredentials_AreNeverSent()
    {
        var server = new RtspTestServer { Digest = ("viewer", "secret") };
        await using var rig = new Rig(server: server);
        var url = $"rtsp://view%0D%0AX-Injected:%20yes@127.0.0.1:{server.Port}/camera";
        var refused = await rig.AskUntil(url, m => m.Status is { Code: "media.provider" });
        Assert.Equal("The address's user name or password has control characters, which aren't sent.", refused.Status!.Message);
        Assert.Null(refused.Sample);
        Assert.Empty(server.Authorizations);
    }

    // ---- cameras that don't echo CSeq ---------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACameraThatDoesntEchoCSeq_IsSignedInto_AndPlays(bool signIn)
    {
        // An answer without CSeq answers the oldest request not yet answered, as FFmpeg's own client takes it: the
        // SETUP's channels are agreed, and a 401 is signed and asked again.
        var server = new RtspTestServer { EchoCSeq = _ => null, Digest = signIn ? ("viewer", "secret") : null };
        await using var rig = new Rig(server: server);
        var url = signIn ? $"rtsp://viewer:secret@127.0.0.1:{server.Port}/camera" : server.Url();
        var shown = await rig.AskUntil(url, m => m.Sample is not null || m.Status is { Code: not "media.loading" });
        Assert.Null(shown.Status);
        shown.Sample!.Release();
        Assert.Equal(signIn, server.Challenges >= 1);
        Assert.Equal(signIn, server.Authorizations.Count > 0);
    }

    [Fact]
    public async Task AnAnswerNamingNoRequestsCSeq_IsStillRefused()
    {
        // A CSeq that names no request sent isn't taken for another's: the 401 is refused, and nothing is signed.
        var server = new RtspTestServer
        {
            EchoCSeq = cseq => (int.Parse(cseq, System.Globalization.CultureInfo.InvariantCulture) + 1000).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Digest = ("viewer", "secret"),
        };
        await using var rig = new Rig(server: server);
        var refused = await rig.AskUntil($"rtsp://viewer:secret@127.0.0.1:{server.Port}/camera", m => m.Status is { Code: "media.provider" });
        Assert.Equal(Malformed, refused.Status!.Message);
        Assert.Null(refused.Sample);
        Assert.True(server.Challenges >= 1);
        Assert.Empty(server.Authorizations);
    }

    // ---- the relay's answers to its requests, in an order the test sets ------------------------------------------

    private const string Basic = "WWW-Authenticate: Basic realm=\"camera\"\r\n";

    /// <summary>
    /// A relay between a camera and an FFmpeg the test plays itself over loopback sockets, without FFmpeg: each step
    /// writes a message, or reads one whole, so the order the relay sees them in is the test's.
    /// </summary>
    private sealed class RelayRig : IAsyncDisposable
    {
        private readonly Socket _cameraSide;
        private readonly Socket _ffmpegSide;
        private readonly NetworkStream _camera;
        private readonly NetworkStream _ffmpeg;
        private readonly StreamReader _cameraReader;
        private readonly StreamReader _ffmpegReader;

        private RelayRig(RtspRelay relay, Socket cameraSide, Socket ffmpegSide)
        {
            Relay = relay;
            _cameraSide = cameraSide;
            _ffmpegSide = ffmpegSide;
            _camera = new NetworkStream(cameraSide);
            _ffmpeg = new NetworkStream(ffmpegSide);
            _cameraReader = new StreamReader(_camera, System.Text.Encoding.Latin1);
            _ffmpegReader = new StreamReader(_ffmpeg, System.Text.Encoding.Latin1);
        }

        public RtspRelay Relay { get; }

        /// <summary>A relay with <paramref name="userInfo"/>'s credentials; <paramref name="sending"/> is its seam before each send.</summary>
        public static async Task<RelayRig> StartAsync(string userInfo = "", Func<Task>? sending = null)
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var relaySide = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await relaySide.ConnectAsync(listener.LocalEndPoint!, Ct);
            var cameraSide = await listener.AcceptAsync(Ct);
            var relay = RtspRelay.Start(new NetworkStream(relaySide, ownsSocket: true), "rtsp://camera.test", userInfo, 1024 * 1024);
            relay.Sending = sending;
            var ffmpegSide = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await ffmpegSide.ConnectAsync(new IPEndPoint(IPAddress.Loopback, relay.Port), Ct);
            return new RelayRig(relay, cameraSide, ffmpegSide);
        }

        /// <summary>FFmpeg sends a request to the relay's address.</summary>
        public Task Request(int cseq, string method = "OPTIONS") =>
            Write(_ffmpeg, $"{method} {Relay.RelayBase}/camera RTSP/1.0\r\nCSeq: {cseq}\r\n\r\n");

        /// <summary>The camera answers, with <paramref name="cseq"/> or no CSeq.</summary>
        public Task CameraAnswers(string status, int? cseq, string headers = "") =>
            Write(_camera, $"RTSP/1.0 {status}\r\n{(cseq is { } c ? $"CSeq: {c}\r\n" : "")}{headers}\r\n");

        /// <summary>The next message the camera receives, or null when the relay closed its connection.</summary>
        public Task<string?> CameraReads() => Read(_cameraReader);

        /// <summary>The next message FFmpeg receives, or null when the relay closed its connection.</summary>
        public Task<string?> FFmpegReads() => Read(_ffmpegReader);

        private static async Task Write(Stream stream, string message)
        {
            await stream.WriteAsync(System.Text.Encoding.Latin1.GetBytes(message), Ct);
            await stream.FlushAsync(Ct);
        }

        private static async Task<string?> Read(StreamReader reader)
        {
            var message = new System.Text.StringBuilder();
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(Ct).AsTask().WaitAsync(Wait, Ct);
                }
                catch (IOException)
                {
                    line = null; // reset by the relay's close
                }
                if (line is null) return message.Length == 0 ? null : throw new EndOfStreamException("A message cut short");
                if (line.Length == 0) return message.ToString();
                message.Append(line).Append('\n');
            }
        }

        public ValueTask DisposeAsync()
        {
            Relay.Dispose();
            _cameraReader.Dispose();
            _ffmpegReader.Dispose();
            _cameraSide.Dispose();
            _ffmpegSide.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A header's value in a message <see cref="RelayRig"/> read, or null.</summary>
    private static string? HeaderOf(string? message, string name) =>
        message?.Split('\n').Skip(1)
            .Where(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            .Select(l => l[(name.Length + 1)..].Trim())
            .FirstOrDefault();

    [Fact]
    public async Task AfterFifteenAnswered_ACSeqlessAnswer_IsTheRequestStillWaitings()
    {
        // Fifteen requests answered, then A (CSeq 16) and B (CSeq 17): A's answer names A, so B's 401 without CSeq is B's,
        // and B is the request signed and sent again, never A.
        await using var rig = await RelayRig.StartAsync("viewer:secret");
        for (var cseq = 1; cseq <= 15; cseq++)
        {
            await rig.Request(cseq);
            Assert.Equal($"{cseq}", HeaderOf(await rig.CameraReads(), "CSeq"));
            await rig.CameraAnswers("200 OK", cseq);
            Assert.Equal($"{cseq}", HeaderOf(await rig.FFmpegReads(), "CSeq"));
        }
        await rig.Request(16, "DESCRIBE");
        Assert.Equal("16", HeaderOf(await rig.CameraReads(), "CSeq"));
        await rig.Request(17, "SETUP");
        Assert.Equal("17", HeaderOf(await rig.CameraReads(), "CSeq"));

        await rig.CameraAnswers("200 OK", 16);
        Assert.Equal("16", HeaderOf(await rig.FFmpegReads(), "CSeq"));
        await rig.CameraAnswers("401 Unauthorized", null, Basic);

        var again = await rig.CameraReads();
        Assert.StartsWith("SETUP ", again);
        Assert.Equal("17", HeaderOf(again, "CSeq"));
        Assert.NotNull(HeaderOf(again, "Authorization"));
    }

    [Fact]
    public async Task SeventeenRequestsUnanswered_EndTheRelay_AndNoneIsLetGo()
    {
        // Sixteen requests wait for an answer: a seventeenth isn't sent, and the relay ends saying why, rather than let the
        // oldest go and pair the camera's answers without CSeq with the requests after the ones they answer.
        await using var rig = await RelayRig.StartAsync();
        for (var cseq = 1; cseq <= 16; cseq++)
        {
            await rig.Request(cseq);
            Assert.Equal($"{cseq}", HeaderOf(await rig.CameraReads(), "CSeq"));
        }
        await rig.Request(17);

        Assert.Null(await rig.CameraReads());
        Assert.Equal(RtspRelay.TooManyWaiting, rig.Relay.Problem);
    }

    [Fact]
    public async Task ARequestHeldBeforeItsSend_WaitsBehindARetrySentMeanwhile()
    {
        // A waits for its answer; B is read from FFmpeg and held just before it is sent. A's 401 without CSeq sends A again
        // meanwhile, so the camera has A's retry before B: the camera's next 401 without CSeq is the retry's. A was signed
        // once already, so it ends the relay, and B is never signed and sent again for it.
        var sends = 0;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await RelayRig.StartAsync("viewer:secret", async () =>
        {
            if (Interlocked.Increment(ref sends) != 2) return;
            held.TrySetResult();
            await go.Task;
        });
        await rig.Request(1, "DESCRIBE");
        Assert.Equal("1", HeaderOf(await rig.CameraReads(), "CSeq"));
        await rig.Request(2, "OPTIONS");
        await held.Task.WaitAsync(Wait, Ct);

        await rig.CameraAnswers("401 Unauthorized", null, Basic);
        var retry = await rig.CameraReads();
        Assert.StartsWith("DESCRIBE ", retry);
        Assert.NotNull(HeaderOf(retry, "Authorization"));
        go.SetResult();
        Assert.StartsWith("OPTIONS ", await rig.CameraReads());

        await rig.CameraAnswers("401 Unauthorized", null, Basic);
        Assert.Null(await rig.CameraReads());
        Assert.Equal(RtspRelay.SignInRefused, rig.Relay.Problem);
    }
}
