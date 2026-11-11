using System.IO.Pipes;
using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Panels;
using InfoPanel.Panels.Hosted;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Usb;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Hosted;

/// <summary>
/// A family whose driver runs in a panel host: the engine's proxy driver and session over a host's connection (a real
/// current-user pipe, the host's serve loop in this process): probes, starts and frames go through it, each frame through
/// a slot of the session's two-slot file (its bytes, format and id as written); the engine never writes a slot the host
/// holds, and a write the host hasn't answered by its deadline is a timeout; after the host restarts, the proxy goes
/// through the new connection, never the stale one (the plugin host's fix for actions after a restart, applied to panels).
/// </summary>
public sealed class HostedPanelTests : IAsyncDisposable
{
    internal static readonly PanelPackageFamily Family = new("virtual", "Virtual panel", [new PanelMatch(0x1209, 0x0001)],
        [new PanelPackageModel("4x2", "Virtual 4 x 2", 4, 2, null)]);

    internal static readonly PanelCandidate Candidate = new(0x1209, 0x0001, 0, 9, "0-9");

    private readonly string _slots = Directory.CreateTempSubdirectory("panel-slots-").FullName;
    private readonly KeptLog _log = new();
    private readonly List<IAsyncDisposable> _hosts = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
        Directory.Delete(_slots, recursive: true);
    }

    private async Task<TestHost> Host(RecordingDriver driver)
    {
        var host = await TestHost.StartAsync(driver, _log.Logger);
        _hosts.Add(host);
        return host;
    }

    private static byte[] Filled(byte value) => Enumerable.Repeat(value, 4 * 2 * 2).ToArray();

    private static PanelWrite Write(IPanelSession session, byte value, long frameId) =>
        session.WriteFrame(new PanelFrameView(Filled(value), PanelFrameFormat.Rgb565Le, frameId, repeat: false), CancellationToken.None);

    [Fact]
    public async Task A_frame_reaches_the_host_through_its_slot_with_its_bytes_format_and_id()
    {
        var hosted = new RecordingDriver();
        var host = await Host(hosted);
        var link = new TestLink(_slots) { Current = host.Connection };
        var driver = new HostedPanelDriver(Family, link, _log.Logger);

        var probe = driver.Probe(Candidate, CancellationToken.None);
        Assert.Equal((true, PanelDeviceStatus.Ok, hosted.Identity), (probe.Mine, probe.Status, probe.Identity));
        Assert.Equal(Candidate, Assert.Single(hosted.Probed));

        var open = driver.Open(Candidate, probe.Identity!, new PanelOpenOptions(70, 90), CancellationToken.None);
        Assert.Equal(PanelDeviceStatus.Ok, open.Status);
        var session = open.Session!;
        Assert.Equal(hosted.Capabilities.Width, session.Capabilities.Width);
        Assert.Equal(hosted.Capabilities.Formats, session.Capabilities.Formats);
        Assert.Equal((70, 90), (hosted.Options!.Brightness, hosted.Options.Rotation));
        Assert.Single(Directory.EnumerateFiles(_slots, "*.slots"));

        var frame = Enumerable.Range(0, 16).Select(i => (byte)(i * 7)).ToArray();
        var write = session.WriteFrame(new PanelFrameView(frame, PanelFrameFormat.Rgb565Le, 41, repeat: false), CancellationToken.None);
        var again = session.WriteFrame(new PanelFrameView(frame, PanelFrameFormat.Rgb565Le, 41, repeat: true), CancellationToken.None);
        Assert.Equal((PanelDeviceStatus.Ok, TimeSpan.FromMilliseconds(3)), (write.Status, write.DeviceTime));
        Assert.Equal(PanelDeviceStatus.Ok, again.Status);
        Assert.Equal(PanelDeviceStatus.Ok, session.SetBrightness(40));
        var written = hosted.Session!.Written;
        Assert.Equal([(41L, false), (41L, true)], written.Select(w => (w.FrameId, w.Repeat)));
        Assert.All(written, w =>
        {
            Assert.Equal(PanelFrameFormat.Rgb565Le, w.Format);
            Assert.Equal(frame, w.Bytes); // by content: a tuple would compare the arrays by reference
        });
        Assert.Equal([70, 40], hosted.Session.Brightness);

        session.Close(screenOff: true, TimeSpan.FromSeconds(2));
        session.Dispose();
        Assert.True(hosted.Session.ClosedWithScreenOff);
        Assert.InRange(hosted.Session.CloseBudget, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        Assert.Empty(Directory.EnumerateFiles(_slots, "*.slots"));
        Assert.Equal(0, link.Sessions);
    }

    [Fact]
    public async Task The_engine_never_writes_a_slot_the_host_holds_and_a_write_past_its_deadline_is_a_timeout()
    {
        var hosted = new RecordingDriver();
        var host = await Host(hosted);
        var driver = new HostedPanelDriver(Family, new TestLink(_slots) { Current = host.Connection }, _log.Logger,
            new HostedPanelOptions { WriteDeadline = TimeSpan.FromMilliseconds(300) });
        var session = driver.Open(Candidate, hosted.Identity, new PanelOpenOptions(100), CancellationToken.None).Session!;

        // The host holds the first frame's slot until it answers, which it doesn't until the gate opens.
        hosted.Session!.Gate.Reset();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(PanelDeviceStatus.Timeout, Write(session, 0xAA, 1).Status);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
        // The next frame takes the other slot (held too, once sent), and a third finds none free: refused unwritten.
        Assert.Equal(PanelDeviceStatus.Timeout, Write(session, 0xBB, 2).Status);
        clock.Restart();
        Assert.Equal(PanelDeviceStatus.Timeout, Write(session, 0xCC, 3).Status);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(250), "a write with no slot free waited for one");

        // The host reads each slot only now: each still holds its own frame. Their late answers free them.
        hosted.Session.Gate.Set();
        await Until(() => hosted.Session.Written.Count == 2, "the held frames never reached the driver");
        Assert.Equal([(1L, Filled(0xAA)), (2L, Filled(0xBB))], hosted.Session.Written.Select(w => (w.FrameId, w.Bytes)));
        await Until(() => Write(session, 0xDD, 4).Status == PanelDeviceStatus.Ok, "no slot was freed by the late answers");
        Assert.Equal([1L, 2L, 4L], hosted.Session.Written.Select(w => w.FrameId));
        Assert.Equal(Filled(0xDD), hosted.Session.Written[^1].Bytes);
        session.Close(screenOff: true, TimeSpan.FromSeconds(1));
        session.Dispose();
    }

    [Fact]
    public async Task After_its_host_restarts_the_proxy_goes_through_the_new_connection_never_the_stale_one()
    {
        var first = new RecordingDriver();
        var second = new RecordingDriver();
        var old = await Host(first);
        var link = new TestLink(_slots) { Current = old.Connection };
        var driver = new HostedPanelDriver(Family, link, _log.Logger);
        var session = driver.Open(Candidate, first.Identity, new PanelOpenOptions(100), CancellationToken.None).Session!;
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, 1, 1).Status);

        // The host ends (a crash) and a new one starts in its place: the link's connection is the new host's.
        await old.DisposeAsync();
        link.Current = (await Host(second)).Connection;

        // The old session's next write fails at once, as its host's: it never reaches the new host.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<PanelDriverException>(() => Write(session, 2, 2));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the stale session's write took {clock.Elapsed.TotalMilliseconds} ms");
        session.Close(screenOff: true, TimeSpan.FromSeconds(1));
        session.Dispose();
        Assert.Null(second.Session);

        // Started again, the panel's session is the new host's, and its next write reaches it.
        var again = driver.Open(Candidate, first.Identity, new PanelOpenOptions(100), CancellationToken.None).Session!;
        Assert.Equal(PanelDeviceStatus.Ok, Write(again, 3, 3).Status);
        Assert.Equal([3L], second.Session!.Written.Select(w => w.FrameId));
        Assert.Equal([1L], first.Session!.Written.Select(w => w.FrameId));
        again.Close(screenOff: true, TimeSpan.FromSeconds(1));
        again.Dispose();
    }

    [Fact]
    public async Task A_close_its_host_cant_do_within_its_budget_ends_that_host_so_the_device_and_mapping_go_with_it()
    {
        var hosted = new RecordingDriver();
        var host = await Host(hosted);
        var link = new TestLink(_slots) { Current = host.Connection };
        var driver = new HostedPanelDriver(Family, link, _log.Logger, new HostedPanelOptions { WriteDeadline = TimeSpan.FromMilliseconds(200) });
        var session = driver.Open(Candidate, hosted.Identity, new PanelOpenOptions(100), CancellationToken.None).Session!;
        hosted.Session!.Gate.Reset(); // the driver's write doesn't return while the test holds it
        try
        {
            Assert.Equal(PanelDeviceStatus.Timeout, Write(session, 0xAA, 1).Status);

            // The host's session is still busy past the close's budget: the host is ended (and started again while wanted).
            session.Close(screenOff: true, TimeSpan.FromMilliseconds(300));
            session.Dispose();

            await host.Connection.Closed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, link.Sessions);
            Assert.Contains(_log.Lines, l => l.Contains("couldn't close its session") && l.Contains("its host is ended"));
        }
        finally
        {
            hosted.Session.Gate.Set();
        }
    }

    [Fact]
    public async Task Requests_a_host_never_answers_are_let_go_at_their_deadline_and_too_many_kept_end_the_connection()
    {
        // A host that reads every request and answers none.
        var (engineEnd, hostEnd) = await TestHost.PipePairAsync();
        await using var _ = hostEnd;
        var reading = Task.Run(async () =>
        {
            try
            {
                while (await PanelHostWire.ReadAsync<PanelHostRequest>(hostEnd, CancellationToken.None) != null)
                {
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }, TestContext.Current.CancellationToken);
        using var connection = new PanelHostConnection(engineEnd, _log.Logger);

        for (var i = 0; i < 100; i++)
            Assert.Null(connection.Call(new PanelHostRequest { Op = "probe" }, TimeSpan.FromMilliseconds(1)));
        Assert.Equal(0, connection.Outstanding);
        Assert.False(connection.IsClosed);

        // Those kept for their late answers (a start's, a write's) are bounded: one more ends the connection, failing them all.
        var kept = Enumerable.Range(0, PanelHostConnection.MaxOutstanding).Select(_ => connection.SendAsync(new PanelHostRequest { Op = "open" })).ToList();
        Assert.False(connection.IsClosed);
        var over = connection.SendAsync(new PanelHostRequest { Op = "open" });
        Assert.True(connection.IsClosed, "a host that answers nothing kept its connection");
        await Assert.ThrowsAsync<PanelDriverException>(() => over);
        Assert.All(kept, t => Assert.True(t.IsFaulted));
        Assert.Equal(0, connection.Outstanding);
    }

    [Fact]
    public async Task A_host_that_stops_reading_never_holds_a_caller_past_its_deadline_and_its_connection_ends()
    {
        // A host's end that reads nothing: the engine's requests fill the pipe, and a write then waits for room.
        var (engineEnd, hostEnd) = await TestHost.PipePairAsync();
        await using var _ = hostEnd;
        var connection = new PanelHostConnection(engineEnd, _log.Logger);
        var large = new PanelHostRequest { Op = "attach", Path = new string('x', 60_000) };

        // Each call ends by its deadline: no answer, or (once the connection ended at a deadline) the host's end.
        var calling = Task.Run(() =>
        {
            for (var i = 0; i < 8 && !connection.IsClosed; i++)
                try
                {
                    Assert.Null(connection.Call(large, TimeSpan.FromMilliseconds(300)));
                }
                catch (PanelDriverException)
                {
                }
        }, TestContext.Current.CancellationToken);

        try
        {
            await calling.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            connection.Dispose(); // the caller still held is let go
            throw;
        }
        Assert.True(connection.IsClosed, "the connection to a host that stopped reading was kept");
        await Assert.ThrowsAsync<PanelDriverException>(() => connection.SendAsync(large));
    }

    [Fact]
    public async Task A_late_starts_close_to_a_host_that_stopped_reading_ends_its_connection_by_its_deadline()
    {
        // A host that reads the start, answers it only past its deadline, then reads nothing more: its pipe is full, so
        // a write to it never finishes.
        var (pipeEnd, hostEnd) = await TestHost.PipePairAsync();
        await using var _ = hostEnd;
        var engineEnd = new StallingStream(pipeEnd);
        using var connection = new PanelHostConnection(engineEnd, _log.Logger, postDeadline: TimeSpan.FromMilliseconds(300));
        var driver = new HostedPanelDriver(Family, new TestLink(_slots) { Current = connection }, _log.Logger,
            new HostedPanelOptions { OpenDeadline = TimeSpan.FromMilliseconds(200) });

        var reading = Task.Run(() => PanelHostWire.ReadAsync<PanelHostRequest>(hostEnd, CancellationToken.None), TestContext.Current.CancellationToken);
        var open = driver.Open(Candidate, new RecordingDriver().Identity, new PanelOpenOptions(100), CancellationToken.None);
        Assert.Equal(PanelDeviceStatus.Timeout, open.Status);
        var start = await reading.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("open", start!.Op);

        // It stops reading, then answers the start: the engine's close of that late session can't be written.
        engineEnd.Stalled = true;
        PanelHostWire.Write(hostEnd, new PanelHostReply { Id = start.Id, Status = PanelDeviceStatus.Ok, Session = 7 });

        // The close's write past its deadline ends the connection: the next request fails at once, not queued behind it.
        await connection.Closed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Throws<PanelDriverException>(() => connection.Call(new PanelHostRequest { Op = "probe" }, TimeSpan.FromMilliseconds(300)));
        Assert.Contains(_log.Lines, l => l.Contains("didn't take a request in time"));
    }

    [Fact]
    public async Task A_cleanup_close_that_expires_waiting_its_turn_ends_the_connection_so_its_host_lets_the_session_go()
    {
        // A host that reads every request, whose pipe holds the engine's next write a while (a long-deadline request).
        var (pipeEnd, hostEnd) = await TestHost.PipePairAsync();
        await using var _ = hostEnd;
        var engineEnd = new StallingStream(pipeEnd);
        using var connection = new PanelHostConnection(engineEnd, _log.Logger, postDeadline: TimeSpan.FromMilliseconds(300));
        var read = new List<string>();
        var reading = Task.Run(async () =>
        {
            try
            {
                while (await PanelHostWire.ReadAsync<PanelHostRequest>(hostEnd, CancellationToken.None) is { } request)
                    lock (read)
                        read.Add(request.Op);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }, TestContext.Current.CancellationToken);

        engineEnd.Stalled = true;
        using var longDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var held = connection.SendAsync(new PanelHostRequest { Op = "probe" }, longDeadline.Token); // holds the pipe's turn
        // A late start's close, queued behind it: its deadline passes before its turn comes.
        connection.Post(new PanelHostRequest { Op = "close", Session = 7, ScreenOff = true, Ms = 1000 });
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        // The held request goes on within its own deadline; the close was never written, so the connection must not go on
        // (the host would keep the abandoned session and its device): it has ended, and the host sees its pipe end.
        engineEnd.Resume();
        await connection.Closed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await reading.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        lock (read)
            Assert.DoesNotContain("close", read);
        await Assert.ThrowsAsync<PanelDriverException>(() => held);
        Assert.Contains(_log.Lines, l => l.Contains("didn't take a cleanup request in time"));
    }

    private async Task Until(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{what}\nThe log:\n{_log}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>
/// The manager with hosted families: a family whose package is off leaves its panels off with the <c>driver</c> reason
/// and its devices unasked; one whose package failed leaves them failed with it; a saved panel of a family no driver
/// declares is failed with it too.
/// </summary>
public sealed class HostedPanelStateTests : IAsyncDisposable
{
    private readonly FakeUsbBackend _backend = new();
    private readonly KeptLog _log = new();
    private readonly string _slots = Directory.CreateTempSubdirectory("panel-slots-").FullName;
    private PanelManager? _manager;

    public async ValueTask DisposeAsync()
    {
        if (_manager != null)
            await _manager.DisposeAsync();
        Directory.Delete(_slots, recursive: true);
    }

    private PanelManager Manager(IPanelHostLink link, params SavedPanel[] saved)
    {
        var store = PanelStore.InMemory();
        if (saved.Length > 0)
            store.Save(saved);
        var host = new PanelHost(_ => true, _ => true, _ => { }, _ => { }, _ => { }, () => 30);
        _manager = new PanelManager(_backend, store, host, _backend.Clock, _log.Logger, new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan },
            drivers: [new HostedPanelDriver(HostedPanelTests.Family, link, _log.Logger)]);
        _manager.Start(passes: false);
        return _manager;
    }

    private static SavedPanel Saved(string id, string family) => new(id, family, "4x2", "VIRTUAL9", "0-9", true, "p-1");

    [Theory]
    [InlineData(true, "off", "its driver, the panel package InfoPanel.Panels.Virtual, is turned off")]
    [InlineData(false, "failed", "its driver failed: its host kept ending")]
    public async Task A_package_off_or_failed_leaves_its_panels_with_the_driver_reason_and_its_devices_unasked(bool off, string state, string message)
    {
        _backend.Add(new FakeUsbDevice(new UsbDeviceId(0x1209, 0x0001, 0, 9, "0-9")));
        var link = new TestLink(_slots) { Problem = new PanelDriverProblem(off, message) };
        var manager = Manager(link, Saved("0f8e4a52-6c1b-4f38-9a57-2d1c3b4e5f60", "virtual"), Saved("1f8e4a52-6c1b-4f38-9a57-2d1c3b4e5f60", "gone"));

        await manager.RunPassAsync();
        await manager.RunPassAsync();

        var list = await manager.ListAsync();
        Assert.Equal((state, "driver", message, false), (list[0].State, list[0].ReasonCode, list[0].Reason, list[0].Connected));
        Assert.Equal(("failed", "driver", "no driver for the family gone is installed"), (list[1].State, list[1].ReasonCode, list[1].Reason));
        Assert.Equal(0, link.Connects);
    }
}

/// <summary>A family's state is part of the list's version: its package failing, or turned off, counts as a change with no saved panel.</summary>
public sealed class HostedFamilyVersionTests : IAsyncDisposable
{
    private readonly FakeUsbBackend _backend = new();
    private readonly KeptLog _log = new();
    private PanelManager? _manager;

    public async ValueTask DisposeAsync()
    {
        if (_manager != null)
            await _manager.DisposeAsync();
    }

    [Fact]
    public async Task A_family_whose_package_fails_raises_the_list_version_with_no_saved_panel()
    {
        var link = new TestLink(Path.GetTempPath());
        var host = new PanelHost(_ => true, _ => true, _ => { }, _ => { }, _ => { }, () => 30);
        _manager = new PanelManager(_backend, PanelStore.InMemory(), host, _backend.Clock, _log.Logger, new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan },
            drivers: [new HostedPanelDriver(HostedPanelTests.Family, link, _log.Logger)]);
        _manager.Start(passes: false);
        var raised = new List<long>();
        _manager.Changed += v =>
        {
            lock (raised)
                raised.Add(v);
        };
        await _manager.RunPassAsync();
        var before = _manager.Version;

        link.Problem = new PanelDriverProblem(false, "its driver failed: its host kept ending");
        await _manager.RunPassAsync();
        await _manager.RunPassAsync(); // nothing more changed

        Assert.Equal(before + 1, _manager.Version);
        lock (raised)
            Assert.Equal([before + 1], raised);
        Assert.Empty(await _manager.ListAsync());
    }
}

/// <summary>A host's link a test sets: its connection now, its problem, the sessions it counts and whether a device of it is plugged in.</summary>
internal sealed class TestLink(string slots) : IPanelHostLink
{
    private int _sessions;
    private int _connects;

    public string Folder => "InfoPanel.Panels.Virtual";

    public string SlotDirectory => slots;

    public PanelHostConnection? Current { get; set; }

    public PanelDriverProblem? Problem { get; set; }

    public int Sessions => Volatile.Read(ref _sessions);

    public int Connects => Volatile.Read(ref _connects);

    public bool Present { get; private set; }

    public PanelHostConnection Connect(CancellationToken stop)
    {
        Interlocked.Increment(ref _connects);
        return Current ?? throw new PanelDriverException("no host");
    }

    public void SessionOpened() => Interlocked.Increment(ref _sessions);

    public void SessionClosed() => Interlocked.Decrement(ref _sessions);

    public void SetPresent(bool present) => Present = present;
}

/// <summary>A panel host in this process: its serve loop over a current-user pipe, the engine's end of it as a connection.</summary>
internal sealed class TestHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private NamedPipeServerStream? _engineEnd;
    private NamedPipeClientStream? _hostEnd;
    private Task _serving = Task.CompletedTask;

    public PanelHostConnection Connection { get; private set; } = null!;

    /// <summary>A connected current-user pipe: the engine's end and a host's.</summary>
    public static async Task<(NamedPipeServerStream EngineEnd, NamedPipeClientStream HostEnd)> PipePairAsync()
    {
        var name = "ippt-" + Guid.NewGuid().ToString("N")[..16]; // short: a socket in the temp folder on macOS
        var engineEnd = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var hostEnd = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await Task.WhenAll(engineEnd.WaitForConnectionAsync(), hostEnd.ConnectAsync());
        return (engineEnd, hostEnd);
    }

    public static async Task<TestHost> StartAsync(IPanelDriver driver, Serilog.ILogger logger)
    {
        var (engineEnd, hostEnd) = await PipePairAsync();
        var host = new TestHost { _engineEnd = engineEnd, _hostEnd = hostEnd };
        var server = new PanelHostServer([driver], logger);
        host._serving = Task.Run(() => server.ServeAsync(host._hostEnd, host._stop.Token));
        host.Connection = new PanelHostConnection(host._engineEnd, logger);
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _hostEnd?.Dispose();
        try
        {
            await _serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // ended as a crash would
        }
        Connection.Dispose();
        await Connection.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        _engineEnd?.Dispose();
    }
}

/// <summary>
/// The engine's end of a pipe whose host stops reading once <see cref="Stalled"/>: a write then never finishes, as one to
/// a full pipe doesn't (until the stream is disposed, or <see cref="Resume"/> lets the held writes go on); reads go on.
/// </summary>
internal sealed class StallingStream(Stream inner) : Stream
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public volatile bool Stalled;

    /// <summary>The host reads again: the writes held go on, and those after them aren't held.</summary>
    public void Resume()
    {
        Stalled = false;
        _resumed.TrySetResult();
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        Stalled ? HeldAsync(buffer, cancellationToken) : inner.WriteAsync(buffer, cancellationToken);

    private async ValueTask HeldAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await await Task.WhenAny(_resumed.Task, _disposed.Task); // disposed first: throws
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed.TrySetException(new ObjectDisposedException(nameof(StallingStream)));
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>A driver that keeps what it was asked: a 4 × 2 RGB565 panel whose writes wait for <see cref="RecordingSession.Gate"/>.</summary>
internal sealed class RecordingDriver : IPanelDriver
{
    private readonly List<PanelCandidate> _probed = [];

    public string Family => "virtual";

    public IReadOnlyList<PanelMatch> Matches { get; } = [new PanelMatch(0x1209, 0x0001)];

    public PanelIdentity Identity { get; } = new("4x2", 4, 2, "VIRTUAL9", "1");

    public PanelCapabilities Capabilities { get; } = new(4, 2, [PanelFrameFormat.Rgb565Le], MaxFps: 30, Brightness: new PanelRange(0, 100));

    public PanelOpenOptions? Options { get; private set; }

    public RecordingSession? Session { get; private set; }

    public IReadOnlyList<PanelCandidate> Probed
    {
        get
        {
            lock (_probed)
                return [.. _probed];
        }
    }

    public PanelProbe Probe(PanelCandidate device, CancellationToken stop)
    {
        lock (_probed)
            _probed.Add(device);
        return new PanelProbe(true, Identity, PanelDeviceStatus.Ok);
    }

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        Options = options;
        Session = new RecordingSession(Capabilities, options.Brightness);
        return new PanelOpen(Session, PanelDeviceStatus.Ok, "");
    }
}

internal sealed class RecordingSession(PanelCapabilities capabilities, int brightness) : IPanelSession
{
    private readonly List<(long FrameId, PanelFrameFormat Format, byte[] Bytes, bool Repeat)> _written = [];
    private readonly List<int> _brightness = [brightness];

    public ManualResetEventSlim Gate { get; } = new(true);

    public PanelCapabilities Capabilities => capabilities;

    public IReadOnlyList<(long FrameId, PanelFrameFormat Format, byte[] Bytes, bool Repeat)> Written
    {
        get
        {
            lock (_written)
                return [.. _written];
        }
    }

    public IReadOnlyList<int> Brightness
    {
        get
        {
            lock (_brightness)
                return [.. _brightness];
        }
    }

    public bool ClosedWithScreenOff { get; private set; }

    public TimeSpan CloseBudget { get; private set; }

    public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
    {
        Gate.Wait(TimeSpan.FromSeconds(30));
        lock (_written)
            _written.Add((frame.FrameId, frame.Format, frame.Bytes.ToArray(), frame.Repeat));
        return new PanelWrite(PanelDeviceStatus.Ok, TimeSpan.FromMilliseconds(3));
    }

    public PanelDeviceStatus SetBrightness(int percent)
    {
        lock (_brightness)
            if (_brightness[^1] != percent)
                _brightness.Add(percent);
        return PanelDeviceStatus.Ok;
    }

    public void Close(bool screenOff, TimeSpan budget)
    {
        ClosedWithScreenOff = screenOff;
        CloseBudget = budget;
    }

    public void Dispose()
    {
    }
}
