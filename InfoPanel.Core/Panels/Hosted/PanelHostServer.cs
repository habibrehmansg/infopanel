using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Panels.Sdk;
using Serilog;

namespace InfoPanel.Panels.Hosted;

/// <summary>
/// A panel host's serve loop (the engine executable's <c>--panel-host</c> mode, and tests): it answers the engine's
/// requests (<see cref="PanelHostRequest"/>) with its drivers, each on the thread pool, a session's calls one at a time.
/// A probe and a start get the deadline the request gives; a write reads its frame from the session's slot file, a view
/// of the mapping, and answers once the driver has. It ends at a <c>shutdown</c> (its sessions closed first, within the
/// request's budget) or when the pipe ends (they get <see cref="CloseGrace"/>); a session still busy then is left.
/// </summary>
public sealed class PanelHostServer(IReadOnlyList<IPanelDriver> drivers, ILogger logger)
{
    /// <summary>How long the sessions get to close when the pipe ends without a shutdown.</summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<long, Hosted> _sessions = new();
    private readonly Lock _writing = new();
    private readonly CancellationTokenSource _ending = new();
    private long _nextSession;
    private Stream? _stream;

    private sealed class Hosted(IPanelSession session)
    {
        public IPanelSession Session { get; } = session;
        public SemaphoreSlim Turn { get; } = new(1, 1);
        public PanelSlotFile? Slots { get; set; }
        public bool Closed { get; set; }
        private Task _tail = Task.CompletedTask;

        /// <summary>Runs <paramref name="work"/> after the session's requests before it, on the pool.</summary>
        public void Enqueue(Action work)
        {
            lock (this)
                _tail = _tail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>Serves <paramref name="stream"/> until a shutdown, its end, or <paramref name="stop"/>.</summary>
    public async Task ServeAsync(Stream stream, CancellationToken stop)
    {
        _stream = stream;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, _ending.Token);
        var shutDown = false;
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var request = await PanelHostWire.ReadAsync<PanelHostRequest>(stream, linked.Token).ConfigureAwait(false);
                if (request == null)
                    break;
                if (request.Op == "shutdown")
                {
                    shutDown = true;
                    await CloseAllAsync(TimeSpan.FromMilliseconds(Math.Clamp(request.Ms ?? 0, 0, 10_000))).ConfigureAwait(false);
                    Reply(new PanelHostReply { Id = request.Id });
                    break;
                }
                // A session's frames, brightness and slots in the order they came; anything else at once.
                if (request.Op is "write" or "brightness" or "attach" && request.Session is { } session && _sessions.TryGetValue(session, out var hosted))
                    hosted.Enqueue(() => Handle(request));
                else
                    _ = Task.Run(() => Handle(request));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException or InvalidOperationException)
        {
            if (ex is InvalidDataException)
                logger.Warning("The engine sent a message the host doesn't take: {Why:l}", ex.Message);
        }
        if (!shutDown)
        {
            logger.Information("The engine's pipe ended without a shutdown; closing the sessions");
            await CloseAllAsync(CloseGrace).ConfigureAwait(false);
        }
    }

    private void Handle(PanelHostRequest request)
    {
        PanelHostReply reply;
        try
        {
            reply = request.Op switch
            {
                "hello" => new PanelHostReply { Families = [.. drivers.Select(d => d.Family)] },
                "probe" => Probe(request),
                "open" => Open(request),
                "attach" => OnSession(request, h => Attach(request, h)),
                "write" => OnSession(request, h => Write(request, h)),
                "brightness" => OnSession(request, h => new PanelHostReply { Status = h.Session.SetBrightness(Math.Clamp(request.Brightness ?? 100, 0, 100)) }),
                "close" => Close(request),
                _ => new PanelHostReply { Status = PanelDeviceStatus.NotSupported, Message = $"no request {request.Op}" },
            };
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "A driver's {Op:l} failed", request.Op);
            reply = new PanelHostReply { Status = PanelDeviceStatus.Io, Message = PanelHostWire.Cut($"its driver failed: {ex.Message}") };
        }
        Reply(reply with { Id = request.Id });
    }

    private void Reply(PanelHostReply reply)
    {
        try
        {
            lock (_writing)
                PanelHostWire.Write(_stream!, reply);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _ending.Cancel(); // the engine is gone
        }
    }

    private IPanelDriver? DriverOf(string? family) => drivers.FirstOrDefault(d => d.Family == family);

    private static CancellationTokenSource Deadline(int? ms) => new(TimeSpan.FromMilliseconds(Math.Clamp(ms ?? 5000, 1, 60_000)));

    private PanelHostReply Probe(PanelHostRequest request)
    {
        if (DriverOf(request.Family) is not { } driver || request.Device is not { } device)
            return new PanelHostReply { Status = PanelDeviceStatus.NotSupported, Message = $"this host has no driver for {request.Family}" };
        using var deadline = Deadline(request.Ms);
        var probe = driver.Probe(device, deadline.Token);
        return new PanelHostReply { Status = probe.Status, Mine = probe.Mine, Identity = probe.Identity, Message = PanelHostWire.Cut(probe.Message) };
    }

    private PanelHostReply Open(PanelHostRequest request)
    {
        if (DriverOf(request.Family) is not { } driver || request.Device is not { } device || request.Identity is not { } identity)
            return new PanelHostReply { Status = PanelDeviceStatus.NotSupported, Message = $"this host has no driver for {request.Family}" };
        PanelDeviceStatus? refused = null;
        var options = new PanelOpenOptions(Math.Clamp(request.Brightness ?? 100, 0, 100), request.Rotation ?? 0, request.Format)
        {
            FormatRefused = status => refused = status,
        };
        using var deadline = Deadline(request.Ms);
        var open = driver.Open(device, identity, options, deadline.Token);
        if (open.Session is not { } session)
            return new PanelHostReply { Status = open.Status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : open.Status, Message = PanelHostWire.Cut(open.Message), FormatRefused = refused };
        var id = Interlocked.Increment(ref _nextSession);
        _sessions[id] = new Hosted(session);
        logger.Information("Session {Session} of {Family:l} at {Port:l}: {Width} x {Height}", id, driver.Family, device.PortPath, session.Capabilities.Width, session.Capabilities.Height);
        return new PanelHostReply { Session = id, Capabilities = session.Capabilities, FormatRefused = refused };
    }

    private PanelHostReply OnSession(PanelHostRequest request, Func<Hosted, PanelHostReply> act)
    {
        if (request.Session is not { } id || !_sessions.TryGetValue(id, out var hosted))
            return new PanelHostReply { Status = PanelDeviceStatus.Gone, Message = "no such session" };
        hosted.Turn.Wait();
        try
        {
            return hosted.Closed ? new PanelHostReply { Status = PanelDeviceStatus.Gone, Message = "its session ended" } : act(hosted);
        }
        finally
        {
            hosted.Turn.Release();
        }
    }

    private PanelHostReply Attach(PanelHostRequest request, Hosted hosted)
    {
        if (request.Path is not { } path || !Path.GetFileName(path).EndsWith(PanelSlotFile.Extension, StringComparison.Ordinal))
            return new PanelHostReply { Status = PanelDeviceStatus.Io, Message = "no slot file" };
        hosted.Slots?.Dispose();
        hosted.Slots = PanelSlotFile.Open(path);
        return new PanelHostReply();
    }

    private PanelHostReply Write(PanelHostRequest request, Hosted hosted)
    {
        if (hosted.Slots is not { } slots || !slots.TryRead(request.Slot ?? -1, request.FrameId ?? -1, out var bytes, out var format))
            return new PanelHostReply { Status = PanelDeviceStatus.Io, Message = "the slot doesn't hold the frame named" };
        var write = hosted.Session.WriteFrame(new PanelFrameView(bytes, format, request.FrameId!.Value, request.Repeat == true), _ending.Token);
        return new PanelHostReply { Status = write.Status, DeviceMs = write.DeviceTime.TotalMilliseconds, Queued = write.Queued };
    }

    /// <summary>A session's close: its turn waited for within the budget (a write still under way past it leaves the session as it is).</summary>
    private PanelHostReply Close(PanelHostRequest request)
    {
        if (request.Session is not { } id || !_sessions.TryGetValue(id, out var hosted))
            return new PanelHostReply();
        var budget = TimeSpan.FromMilliseconds(Math.Clamp(request.Ms ?? 0, 0, 10_000));
        var started = Stopwatch.GetTimestamp();
        if (!hosted.Turn.Wait(budget))
            return new PanelHostReply { Status = PanelDeviceStatus.Timeout, Message = "its session was still busy" };
        try
        {
            CloseOne(id, hosted, request.ScreenOff == true, budget - Stopwatch.GetElapsedTime(started));
        }
        finally
        {
            hosted.Turn.Release();
        }
        return new PanelHostReply();
    }

    private void CloseOne(long id, Hosted hosted, bool screenOff, TimeSpan budget)
    {
        if (hosted.Closed)
            return;
        hosted.Closed = true;
        _sessions.TryRemove(id, out _);
        try
        {
            hosted.Session.Close(screenOff, budget < TimeSpan.Zero ? TimeSpan.Zero : budget);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Session {Session}: its screen off failed", id);
        }
        try
        {
            hosted.Session.Dispose();
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Session {Session}: letting the panel go failed", id);
        }
        hosted.Slots?.Dispose();
        logger.Information("Session {Session} closed{ScreenOff:l}", id, screenOff ? ", screen off" : "");
    }

    /// <summary>Every session closed, screen off, in parallel, within <paramref name="budget"/> in all; one still busy then is left.</summary>
    private async Task CloseAllAsync(TimeSpan budget)
    {
        var started = Stopwatch.GetTimestamp();
        var closing = _sessions.ToList().Select(pair => Task.Run(() =>
        {
            var left = budget - Stopwatch.GetElapsedTime(started);
            if (!pair.Value.Turn.Wait(left < TimeSpan.Zero ? TimeSpan.Zero : left))
                return;
            try
            {
                CloseOne(pair.Key, pair.Value, screenOff: true, budget - Stopwatch.GetElapsedTime(started));
            }
            finally
            {
                pair.Value.Turn.Release();
            }
        })).ToList();
        if (closing.Count == 0)
            return;
        var left = budget - Stopwatch.GetElapsedTime(started);
        await Task.WhenAny(Task.WhenAll(closing), Task.Delay(left < TimeSpan.Zero ? TimeSpan.Zero : left)).ConfigureAwait(false);
    }
}
