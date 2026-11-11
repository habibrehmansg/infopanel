using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Panels.Sdk;
using Serilog;

namespace InfoPanel.Panels.Hosted;

/// <summary>The bounds of a hosted driver's calls (tests shorten them).</summary>
public sealed record HostedPanelOptions
{
    /// <summary>A write the host hasn't answered by then is a timeout (its slot stays the host's until it answers).</summary>
    public TimeSpan WriteDeadline { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A probe, a brightness or a slot file's attach the host hasn't answered by then is a timeout.</summary>
    public TimeSpan CallDeadline { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A start the host hasn't answered by then is a timeout.</summary>
    public TimeSpan OpenDeadline { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// A family whose driver runs in its package's host (<see cref="IPanelHostLink"/>): the engine's proxy for it. Each call
/// goes through the link's connection as it is then (after a host restart, the new one): the probe and the start; a
/// session is its host's, and once that host has ended its calls throw <see cref="PanelDriverException"/> (the panel's
/// reason <c>driver</c>), so the manager starts it again on the new host. A session's frames go through its two-slot
/// file (<see cref="PanelSlotFile"/>), one copy a frame (none for a keep-alive's re-send while its slot is free).
/// </summary>
public sealed class HostedPanelDriver(PanelPackageFamily family, IPanelHostLink link, ILogger logger, HostedPanelOptions? options = null) : IPanelDriver
{
    private readonly HostedPanelOptions _options = options ?? new HostedPanelOptions();

    public string Family => family.Id;

    public IReadOnlyList<PanelMatch> Matches => family.Matches;

    /// <summary>What its package's <c>panel.json</c> declares of the family.</summary>
    public PanelPackageFamily Declared => family;

    public IPanelHostLink Link => link;

    public PanelProbe Probe(PanelCandidate device, CancellationToken stop)
    {
        var connection = link.Connect(stop);
        var reply = connection.Call(new PanelHostRequest { Op = "probe", Family = Family, Device = device, Ms = (int)_options.CallDeadline.TotalMilliseconds },
            _options.CallDeadline, stop);
        if (reply == null)
            return new PanelProbe(false, null, PanelDeviceStatus.Timeout, "its host didn't answer the probe");
        if (reply.Status != PanelDeviceStatus.Ok)
            return new PanelProbe(false, null, reply.Status, PanelHostWire.Cut(reply.Message));
        if (reply.Mine != true)
            return new PanelProbe(false, null, PanelDeviceStatus.Ok, PanelHostWire.Cut(reply.Message));
        if (!Valid(reply.Identity))
            return new PanelProbe(false, null, PanelDeviceStatus.Io, "its driver's identity of the panel is out of bounds");
        return new PanelProbe(true, reply.Identity, PanelDeviceStatus.Ok, PanelHostWire.Cut(reply.Message));
    }

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        var connection = link.Connect(stop);
        // One deadline for the start's turn on the pipe, its writing and its answer.
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(stop);
        sending.CancelAfter(_options.OpenDeadline);
        var answer = connection.SendAsync(new PanelHostRequest
        {
            Op = "open",
            Family = Family,
            Device = device,
            Identity = identity,
            Brightness = options.Brightness,
            Rotation = options.Rotation,
            Format = options.Format,
            Ms = (int)_options.OpenDeadline.TotalMilliseconds,
        }, sending.Token);
        // A start given up on (cancelled, or past its deadline) is closed, screen off, once the host answers it.
        void CloseLate() => answer.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result.Session is { } late)
                connection.Post(new PanelHostRequest { Op = "close", Session = late, ScreenOff = true, Ms = 1000 });
        }, TaskScheduler.Default);
        PanelHostReply? reply;
        try
        {
            reply = PanelHostConnection.Await(answer, _options.OpenDeadline, stop);
        }
        catch (OperationCanceledException)
        {
            CloseLate();
            throw;
        }
        if (reply == null)
        {
            sending.Cancel(); // not yet written: dropped; half written: the connection ends
            CloseLate();
            return new PanelOpen(null, PanelDeviceStatus.Timeout, "its host didn't answer the start");
        }
        if (reply.FormatRefused is { } refused)
            options.FormatRefused?.Invoke(refused);
        if (reply.Status != PanelDeviceStatus.Ok || reply.Session is not { } id)
            return new PanelOpen(null, reply.Status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : reply.Status, PanelHostWire.Cut(reply.Message));

        var capabilities = reply.Capabilities;
        if (SlotSizeOf(capabilities) is not { } slotSize)
        {
            connection.Post(new PanelHostRequest { Op = "close", Session = id, ScreenOff = true, Ms = 1000 });
            return new PanelOpen(null, PanelDeviceStatus.Io, "its driver's capabilities are out of bounds");
        }

        PanelSlotFile? slots = null;
        try
        {
            slots = PanelSlotFile.Create(link.SlotDirectory, slotSize);
            var attach = connection.Call(new PanelHostRequest { Op = "attach", Session = id, Path = slots.Path }, _options.CallDeadline, stop);
            if (attach?.Status != PanelDeviceStatus.Ok)
                throw new System.IO.IOException(attach == null ? "its host didn't answer" : PanelHostWire.Cut(attach.Message));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            slots?.Dispose();
            connection.Post(new PanelHostRequest { Op = "close", Session = id, ScreenOff = true, Ms = 1000 });
            return new PanelOpen(null, PanelDeviceStatus.Io, $"its frame slots couldn't be set up: {ex.Message}");
        }
        catch
        {
            slots?.Dispose();
            connection.Post(new PanelHostRequest { Op = "close", Session = id, ScreenOff = true, Ms = 1000 });
            throw;
        }
        link.SessionOpened();
        return new PanelOpen(new HostedPanelSession(connection, link, id, capabilities!, slots, options.Brightness, _options, logger), PanelDeviceStatus.Ok, "");
    }

    /// <summary>An identity within the API's bounds: a model id of 1 to 64 characters, a size of 1 to 16384, a serial and firmware of at most 128.</summary>
    private static bool Valid(PanelIdentity? identity) =>
        identity is { Model.Length: >= 1 and <= 64, Width: >= 1 and <= 16384, Height: >= 1 and <= 16384 }
        && identity.Serial is null or { Length: <= 128 } && identity.Firmware is null or { Length: <= 128 };

    /// <summary>The bytes a slot holds for <paramref name="capabilities"/>: the largest frame of any format it lists (a JPEG's: its cap, else four bytes a pixel); null when out of bounds.</summary>
    internal static int? SlotSizeOf(PanelCapabilities? capabilities)
    {
        if (capabilities is not { Width: >= 1 and <= 16384, Height: >= 1 and <= 16384, MaxFps: >= 1 and <= 240 } || capabilities.Formats is not { Count: >= 1 and <= 8 })
            return null;
        long pixels = (long)capabilities.Width * capabilities.Height;
        long largest = 0;
        foreach (var format in capabilities.Formats)
            largest = Math.Max(largest, format switch
            {
                PanelFrameFormat.Jpeg => capabilities.MaxFrameBytes ?? pixels * 4 + 65536,
                PanelFrameFormat.Rgb565Le or PanelFrameFormat.Rgb565Be => pixels * 2,
                PanelFrameFormat.Rgb888 or PanelFrameFormat.Bgr888 => pixels * 3,
                _ => pixels * 4,
            });
        return largest is >= 1 and <= 256 * 1024 * 1024 ? (int)largest : null;
    }
}

/// <summary>
/// A session in a panel host, through the connection it started on. A frame goes into a slot the host doesn't hold, then
/// a <c>write</c> hands that slot to the host until its answer (even one that comes after the deadline): with both held,
/// a write is refused unwritten as a timeout. The brightness is sent only when it changes. Once its host has ended,
/// every call throws <see cref="PanelDriverException"/>.
/// </summary>
internal sealed class HostedPanelSession(PanelHostConnection connection, IPanelHostLink link, long id, PanelCapabilities capabilities, PanelSlotFile slots,
    int brightness, HostedPanelOptions options, ILogger logger) : IPanelSession
{
    private readonly Lock _lock = new();
    private readonly bool[] _held = new bool[PanelSlotFile.Slots];
    private readonly long[] _frameIn = [-1, -1];
    private int _next;
    private int _brightness = brightness;
    private bool _slowLogged;
    private int _closed;

    public PanelCapabilities Capabilities => capabilities;

    public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
    {
        if (connection.IsClosed)
            throw new PanelDriverException(PanelHostConnection.Ended);
        int slot;
        bool copy;
        var frameId = frame.FrameId;
        lock (_lock)
        {
            // A re-send of the frame a free slot still holds goes as it is; any other frame into a free slot, the other first.
            var same = Array.IndexOf(_frameIn, frameId);
            slot = frame.Repeat && same >= 0 && !_held[same] ? same
                : !_held[_next] ? _next
                : !_held[1 - _next] ? 1 - _next
                : -1;
            if (slot < 0)
                return new PanelWrite(PanelDeviceStatus.Timeout, TimeSpan.Zero);
            copy = _frameIn[slot] != frame.FrameId || !frame.Repeat;
            _held[slot] = true;
            _next = 1 - slot;
        }
        if (copy)
        {
            slots.Write(slot, frame.Bytes, frame.Format, frame.FrameId);
            _frameIn[slot] = frame.FrameId;
        }

        var started = Stopwatch.GetTimestamp();
        // One deadline for the write's turn on the pipe, its writing and its answer.
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(stop);
        sending.CancelAfter(options.WriteDeadline);
        var answer = connection.SendAsync(new PanelHostRequest { Op = "write", Session = id, Slot = slot, FrameId = frame.FrameId, Repeat = frame.Repeat }, sending.Token);
        // The slot is the host's until it answers, whenever that is (or its pipe ends, or the write was never sent).
        answer.ContinueWith(_ =>
        {
            lock (_lock)
                _held[slot] = false;
        }, TaskContinuationOptions.ExecuteSynchronously);
        if (PanelHostConnection.Await(answer, options.WriteDeadline, stop) is not { } reply)
        {
            sending.Cancel(); // not yet written: dropped; half written: the connection ends
            return new PanelWrite(PanelDeviceStatus.Timeout, Stopwatch.GetElapsedTime(started));
        }
        var device = TimeSpan.FromMilliseconds(Math.Clamp(reply.DeviceMs ?? 0, 0, 60_000)); // a host's number, bounded
        var roundTrip = Stopwatch.GetElapsedTime(started);
        if (!_slowLogged && roundTrip > device + TimeSpan.FromMilliseconds(50) && roundTrip > device * 2)
        {
            _slowLogged = true;
            logger.Information("The panel package {Folder:l}'s write took {RoundTrip:0.#} ms through its host against {Device:0.#} ms on the device (once a session)",
                link.Folder, roundTrip.TotalMilliseconds, device.TotalMilliseconds);
        }
        return new PanelWrite(reply.Status, device, reply.Queued);
    }

    public PanelDeviceStatus SetBrightness(int percent)
    {
        if (percent == _brightness)
            return PanelDeviceStatus.Ok;
        var reply = connection.Call(new PanelHostRequest { Op = "brightness", Session = id, Brightness = percent }, options.CallDeadline);
        if (reply == null)
            return PanelDeviceStatus.Timeout;
        if (reply.Status == PanelDeviceStatus.Ok)
            _brightness = percent;
        return reply.Status;
    }

    public void Close(bool screenOff, TimeSpan budget)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
            return;
        try
        {
            if (connection.IsClosed)
                return;
            var reply = budget > TimeSpan.Zero
                ? connection.Call(new PanelHostRequest { Op = "close", Session = id, ScreenOff = screenOff, Ms = (int)budget.TotalMilliseconds }, budget)
                : null;
            if (reply is null or { Status: PanelDeviceStatus.Timeout })
            {
                // The host may still hold the device and the slots' mapping (a write under way past the budget): it is
                // ended, which lets both go, and started again while a device of it is plugged in.
                logger.Warning("The panel package {Folder:l} couldn't close its session within {Budget:0} ms, so its host is ended", link.Folder, budget.TotalMilliseconds);
                connection.Dispose();
            }
        }
        catch (PanelDriverException)
        {
            // its host ended: nothing to close
        }
        finally
        {
            Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            connection.Post(new PanelHostRequest { Op = "close", Session = id, ScreenOff = false, Ms = 0 });
            Release();
        }
    }

    private int _released;

    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
            return;
        slots.Dispose();
        link.SessionClosed();
    }
}
