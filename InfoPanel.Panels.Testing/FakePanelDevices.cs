using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Testing;

/// <summary>
/// A fake of the device seam for a driver's tests: devices added by their candidate, each scripted (its descriptors,
/// its replies, the calls that fail, stall or deliver part of their bytes, when it is unplugged) and each recording every
/// call made on it in its own <see cref="FakePanelDevice.Trace"/>. A candidate never added answers
/// <see cref="PanelDeviceStatus.Gone"/>. Nothing waits unless a test stalls a call
/// (<see cref="FakePanelDevice.Stall"/>): a read with nothing to read answers <see cref="PanelDeviceStatus.Timeout"/> at
/// once, so tests don't take the timeouts they pass. Timeouts and tokens are held to the seam's rules
/// (<see cref="IPanelDevices"/>): a timeout outside 1 to <see cref="IPanelDevices.MaxTimeoutMs"/> throws, and a call
/// whose token is cancelled throws <see cref="OperationCanceledException"/>; neither is counted or traced.
/// </summary>
public sealed class FakePanelDevices : IPanelDevices
{
    private readonly Dictionary<PanelCandidate, FakePanelDevice> _devices = [];

    /// <summary>
    /// Adds a device at <paramref name="candidate"/>: a USB device always (one vendor-specific interface 0 with bulk
    /// endpoints <c>0x01</c> and <c>0x81</c> of 512 bytes, unless <see cref="FakePanelDevice.Descriptor"/> is set), with
    /// a HID collection and a serial port when <paramref name="transports"/> names them.
    /// </summary>
    public FakePanelDevice Add(PanelCandidate candidate, params PanelTransport[] transports)
    {
        var device = new FakePanelDevice(candidate);
        device.Hid.Present = transports.Contains(PanelTransport.Hid);
        device.Serial.Present = transports.Contains(PanelTransport.Serial);
        lock (_devices)
            _devices.Add(candidate, device);
        return device;
    }

    /// <summary>The device added at <paramref name="candidate"/>.</summary>
    public FakePanelDevice this[PanelCandidate candidate]
    {
        get
        {
            lock (_devices)
                return _devices[candidate];
        }
    }

    private FakePanelDevice? Find(PanelCandidate candidate)
    {
        lock (_devices)
            return _devices.GetValueOrDefault(candidate);
    }

    public PanelDeviceStatus Describe(PanelCandidate device, out PanelUsbDescriptor? descriptor)
    {
        descriptor = null;
        if (Find(device) is not { } fake)
            return PanelDeviceStatus.Gone;
        return fake.Describe(out descriptor);
    }

    public PanelDeviceStatus OpenUsb(PanelCandidate device, int interfaceNumber, int timeoutMs, out IPanelUsb? usb, CancellationToken cancellationToken = default)
    {
        usb = null;
        IPanelDevices.CheckTimeout(timeoutMs);
        cancellationToken.ThrowIfCancellationRequested();
        return Find(device) is { } fake ? fake.OpenUsb(interfaceNumber, timeoutMs, cancellationToken, out usb) : PanelDeviceStatus.Gone;
    }

    public PanelDeviceStatus ListHid(PanelCandidate device, PanelHidFilter filter, out IReadOnlyList<PanelHidCollection> collections)
    {
        collections = [];
        return Find(device) is { } fake ? fake.ListHid(filter, out collections) : PanelDeviceStatus.Gone;
    }

    public PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidFilter filter, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default)
    {
        hid = null;
        IPanelDevices.CheckTimeout(timeoutMs);
        cancellationToken.ThrowIfCancellationRequested();
        return Find(device) is { } fake ? fake.OpenHid(filter, null, timeoutMs, cancellationToken, out hid) : PanelDeviceStatus.Gone;
    }

    public PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidCollection collection, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default)
    {
        hid = null;
        IPanelDevices.CheckTimeout(timeoutMs);
        cancellationToken.ThrowIfCancellationRequested();
        return Find(device) is { } fake ? fake.OpenHid(null, collection.Id, timeoutMs, cancellationToken, out hid) : PanelDeviceStatus.Gone;
    }

    public PanelDeviceStatus OpenSerial(PanelCandidate device, PanelSerialOptions options, int timeoutMs, out IPanelSerial? serial, CancellationToken cancellationToken = default)
    {
        serial = null;
        IPanelDevices.CheckTimeout(timeoutMs);
        cancellationToken.ThrowIfCancellationRequested();
        return Find(device) is { } fake ? fake.OpenSerial(options, timeoutMs, cancellationToken, out serial) : PanelDeviceStatus.Gone;
    }
}

/// <summary>
/// One fake device: its USB side (<see cref="Usb"/>), its HID collections (<see cref="Hid"/>, the first, and any
/// <see cref="AddHid"/> adds) and its serial port (<see cref="Serial"/>), with one <see cref="Trace"/> of every call on
/// any of them, in order, each on its handle (<see cref="PanelTraceEntry.Handle"/>: each open takes the next number from
/// 1). Calls are numbered from 1 in the order they are made: each open and each transfer (not a close, not
/// <see cref="IPanelDevices.Describe"/> or <see cref="IPanelDevices.ListHid"/>), so a test can fail the nth with
/// <see cref="Fail"/>, stall it with <see cref="Stall"/>, fail its zero-length packet with <see cref="FailTerminator"/>,
/// or unplug the device at it with <see cref="UnplugAt"/>. A call on a handle already disposed throws
/// <see cref="ObjectDisposedException"/>, reaches nothing and is counted in <see cref="CallsAfterDispose"/>. Thread-safe;
/// a responder runs under the device's lock and must not call the device.
/// </summary>
public sealed class FakePanelDevice
{
    internal readonly object Gate = new();
    private readonly Dictionary<int, (PanelDeviceStatus Status, int Delivered)> _failures = [];
    private readonly Dictionary<int, PanelDeviceStatus> _terminatorFailures = [];
    private readonly Dictionary<int, TaskCompletionSource> _stalls = [];
    private readonly List<FakeHid> _hids = [];
    private int _calls;
    private int _handles;
    private int? _unplugAt;
    private bool _plugged = true;
    private int _callsAfterDispose;
    private PanelUsbDescriptor _descriptor = new(0, 0, 0, 0x0100,
    [
        new PanelUsbInterface(0, 0xff, 0, 0,
        [
            new PanelUsbEndpoint(0x01, PanelEndpointType.Bulk, 512),
            new PanelUsbEndpoint(0x81, PanelEndpointType.Bulk, 512),
        ]),
    ]);

    internal FakePanelDevice(PanelCandidate candidate)
    {
        Candidate = candidate;
        Usb = new FakeUsb(this);
        Hid = new FakeHid(this, "hid0");
        _hids.Add(Hid);
        Serial = new FakeSerial(this);
    }

    public PanelCandidate Candidate { get; }

    /// <summary>What <see cref="IPanelDevices.Describe"/> answers, and the interfaces and endpoints a USB open finds.</summary>
    public PanelUsbDescriptor Descriptor
    {
        get
        {
            lock (Gate)
                return _descriptor;
        }
        set
        {
            lock (Gate)
                _descriptor = value;
        }
    }

    /// <summary>Every call made on the device, in order, with what it came to. Its opens name no device: the trace is this device's own.</summary>
    public PanelTrace Trace { get; } = new();

    public FakeUsb Usb { get; }

    /// <summary>The device's first HID collection (id <c>hid0</c>), present when it was added with <see cref="PanelTransport.Hid"/>.</summary>
    public FakeHid Hid { get; }

    public FakeSerial Serial { get; }

    /// <summary>
    /// Adds another HID collection, present, with the defaults <see cref="Hid"/> has (interface 0, usage page
    /// <c>0xff00</c>, 65-byte reports), its id <c>hid1</c>, <c>hid2</c> … in the order added: a device whose collections
    /// one filter can't tell apart.
    /// </summary>
    public FakeHid AddHid()
    {
        lock (Gate)
        {
            var hid = new FakeHid(this, $"hid{_hids.Count}") { PresentUnlocked = true };
            _hids.Add(hid);
            return hid;
        }
    }

    /// <summary>The calls made so far (opens and transfers).</summary>
    public int Calls
    {
        get
        {
            lock (Gate)
                return _calls;
        }
    }

    /// <summary>The calls made on handles already disposed (each threw).</summary>
    public int CallsAfterDispose
    {
        get
        {
            lock (Gate)
                return _callsAfterDispose;
        }
    }

    /// <summary>Whether the device is still plugged in.</summary>
    public bool Plugged
    {
        get
        {
            lock (Gate)
                return _plugged;
        }
    }

    /// <summary>
    /// Call <paramref name="call"/> (from 1) answers <paramref name="status"/>. It transfers nothing (a read reads
    /// nothing, and its reply stays queued), but a bulk or serial write delivers its first <paramref name="delivered"/>
    /// bytes before it fails, as a real transfer can: the device then has them (a responder sees them as the last write,
    /// and a serial port answers them as a write), though the trace keeps the call as made.
    /// </summary>
    public void Fail(int call, PanelDeviceStatus status, int delivered = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delivered);
        lock (Gate)
            _failures[call] = (status, delivered);
    }

    /// <summary>
    /// Call <paramref name="call"/>, a bulk write that sends a zero-length packet, delivers its bytes, then its
    /// zero-length packet fails with <paramref name="status"/>, which the write answers; traced as the write, then
    /// <c>zlp</c> with the status.
    /// </summary>
    public void FailTerminator(int call, PanelDeviceStatus status)
    {
        lock (Gate)
            _terminatorFailures[call] = status;
    }

    /// <summary>
    /// Call <paramref name="call"/> stalls, as a device that doesn't answer: it waits until its timeout passes (and
    /// answers <see cref="PanelDeviceStatus.Timeout"/>), its token is cancelled (it throws
    /// <see cref="OperationCanceledException"/>, not traced), its handle is disposed (it answers
    /// <see cref="PanelDeviceStatus.Io"/>) or the device is unplugged (<see cref="PanelDeviceStatus.Gone"/>). The task
    /// completes once the call is waiting.
    /// </summary>
    public Task Stall(int call)
    {
        lock (Gate)
        {
            var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stalls[call] = stalled;
            return stalled.Task;
        }
    }

    /// <summary>The device is unplugged at call <paramref name="call"/> (from 1): that call and every later one answer <see cref="PanelDeviceStatus.Gone"/>.</summary>
    public void UnplugAt(int call)
    {
        lock (Gate)
            _unplugAt = call;
    }

    /// <summary>The device is unplugged now: every later call (and a stalled one) answers <see cref="PanelDeviceStatus.Gone"/>, and it is no longer described.</summary>
    public void Unplug()
    {
        lock (Gate)
        {
            _plugged = false;
            Monitor.PulseAll(Gate);
        }
    }

    // The next call: its number, and what the script makes of it: Gone once unplugged, a scripted failure (with the bytes
    // it delivers), a stall waited out, else Ok (the call itself then decides). The caller has checked its handle, its
    // timeout and its token. Under Gate.
    internal (int Call, PanelDeviceStatus Status, int Delivered) Begin(int timeoutMs, CancellationToken token, HandleState? handle)
    {
        var call = ++_calls;
        if (_unplugAt is { } at && call >= at)
            _plugged = false;
        if (!_plugged)
            return (call, PanelDeviceStatus.Gone, 0);
        if (_failures.TryGetValue(call, out var failure))
            return (call, failure.Status, failure.Delivered);
        if (_stalls.Remove(call, out var stalled))
            return (call, Wait(timeoutMs, token, handle, stalled), 0);
        return (call, PanelDeviceStatus.Ok, 0);
    }

    // A stalled call's wait, the lock let go meanwhile. Under Gate.
    private PanelDeviceStatus Wait(int timeoutMs, CancellationToken token, HandleState? handle, TaskCompletionSource stalled)
    {
        var registration = token.Register(() =>
        {
            lock (Gate)
                Monitor.PulseAll(Gate);
        });
        if (handle != null)
            handle.Pending++;
        try
        {
            stalled.TrySetResult();
            var clock = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (handle?.Disposed == true)
                    return PanelDeviceStatus.Io;
                if (!_plugged)
                    return PanelDeviceStatus.Gone;
                var left = timeoutMs - clock.ElapsedMilliseconds;
                if (left <= 0)
                    return PanelDeviceStatus.Timeout;
                Monitor.Wait(Gate, TimeSpan.FromMilliseconds(left));
            }
        }
        finally
        {
            if (handle != null)
            {
                handle.Pending--;
                Monitor.PulseAll(Gate);
            }
            registration.Unregister(); // never Dispose under the lock: it would wait for a callback waiting for the lock
        }
    }

    /// <summary>A zero-length packet's own status for call <paramref name="call"/>. Under Gate.</summary>
    internal PanelDeviceStatus TerminatorOf(int call) =>
        _terminatorFailures.TryGetValue(call, out var status) ? status : PanelDeviceStatus.Ok;

    // Under Gate.
    internal ObjectDisposedException Disposed(string what)
    {
        _callsAfterDispose++;
        return new ObjectDisposedException(what);
    }

    // A disposed handle's waiting call ends; the dispose returns once it has. Under Gate.
    internal void Release(HandleState handle)
    {
        handle.Disposed = true;
        Monitor.PulseAll(Gate);
        while (handle.Pending > 0)
            Monitor.Wait(Gate);
    }

    internal PanelTraceEntry Record(PanelTraceOp op, string target, byte[] data, PanelDeviceStatus status, int handle)
    {
        var entry = new PanelTraceEntry(op, target, data, status) { Handle = handle };
        Trace.Add(entry);
        return entry;
    }

    // An open's line, on the next handle. Under Gate.
    private int RecordOpen(string target, PanelDeviceStatus status)
    {
        var handle = ++_handles;
        Record(PanelTraceOp.Open, target, [], status, handle);
        return handle;
    }

    /// <summary>What the device last received (a bulk write, an output or feature report, serial bytes, or the part of a failed write it got), for a responder. Under Gate.</summary>
    internal PanelTraceEntry? LastWrite { get; set; }

    internal PanelDeviceStatus Describe(out PanelUsbDescriptor? descriptor)
    {
        lock (Gate)
        {
            descriptor = _plugged ? _descriptor : null;
            return _plugged ? PanelDeviceStatus.Ok : PanelDeviceStatus.Gone;
        }
    }

    internal PanelDeviceStatus ListHid(PanelHidFilter filter, out IReadOnlyList<PanelHidCollection> collections)
    {
        lock (Gate)
        {
            collections = _plugged ? [.. _hids.Where(h => h.PresentUnlocked && h.Passes(filter)).Select(h => h.Describe())] : [];
            return _plugged ? PanelDeviceStatus.Ok : PanelDeviceStatus.Gone;
        }
    }

    internal PanelDeviceStatus OpenUsb(int interfaceNumber, int timeoutMs, CancellationToken token, out IPanelUsb? usb)
    {
        usb = null;
        lock (Gate)
        {
            var (_, status, _) = Begin(timeoutMs, token, null);
            var claimed = _descriptor.Interfaces.FirstOrDefault(i => i.Number == interfaceNumber);
            if (status == PanelDeviceStatus.Ok)
                status = claimed == null ? PanelDeviceStatus.NotSupported : Usb.OpenStatus;
            if (status == PanelDeviceStatus.Ok && !Usb.Claimed.Add(interfaceNumber))
                status = PanelDeviceStatus.Busy;
            var handle = RecordOpen($"usb if{interfaceNumber}", status);
            if (status == PanelDeviceStatus.Ok)
                usb = new FakeUsb.Handle(this, handle, interfaceNumber, claimed!.Endpoints, Usb.SerialNumber);
            return status;
        }
    }

    // By filter (the first collection it passes) or by id (that collection, Gone when the device has none such).
    internal PanelDeviceStatus OpenHid(PanelHidFilter? filter, string? id, int timeoutMs, CancellationToken token, out IPanelHid? hid)
    {
        hid = null;
        lock (Gate)
        {
            var (_, status, _) = Begin(timeoutMs, token, null);
            var collection = filter != null
                ? _hids.FirstOrDefault(h => h.PresentUnlocked && h.Passes(filter))
                : _hids.FirstOrDefault(h => h.PresentUnlocked && h.Id == id);
            if (status == PanelDeviceStatus.Ok && collection == null)
                status = filter != null ? PanelDeviceStatus.NotSupported : PanelDeviceStatus.Gone;
            if (status == PanelDeviceStatus.Ok)
                status = collection!.OpenStatusUnlocked;
            if (status == PanelDeviceStatus.Ok && collection!.IsOpen)
                status = PanelDeviceStatus.Busy;
            var named = collection?.Id ?? id; // the collection asked for, as the recorder names it, even when gone
            var handle = RecordOpen(named == null ? "hid" : $"hid {named}", status);
            if (status == PanelDeviceStatus.Ok)
            {
                collection!.IsOpen = true;
                hid = new FakeHid.Handle(this, collection, handle);
            }
            return status;
        }
    }

    internal PanelDeviceStatus OpenSerial(PanelSerialOptions options, int timeoutMs, CancellationToken token, out IPanelSerial? serial)
    {
        serial = null;
        lock (Gate)
        {
            var (_, status, _) = Begin(timeoutMs, token, null);
            if (status == PanelDeviceStatus.Ok)
                status = Serial.Present ? Serial.OpenStatus : PanelDeviceStatus.NotSupported;
            if (status == PanelDeviceStatus.Ok && Serial.IsOpen)
                status = PanelDeviceStatus.Busy;
            var handle = RecordOpen($"serial {options.Baud}{(options.Dtr ? " dtr" : "")}{(options.Rts ? " rts" : "")}", status);
            if (status == PanelDeviceStatus.Ok)
            {
                Serial.IsOpen = true;
                Serial.Options = options;
                serial = new FakeSerial.Handle(this, handle, Serial.PortName);
            }
            return status;
        }
    }
}

/// <summary>A handle's state, under its device's lock: its number in the trace, whether it is disposed, and the calls on it waiting.</summary>
internal sealed class HandleState(int id)
{
    public int Id { get; } = id;

    public bool Disposed { get; set; }

    public int Pending { get; set; }
}

/// <summary>
/// A fake device's USB side: its serial number, what opening it answers, and its replies. Each reply is one transfer the
/// device sends on an IN endpoint, in packets of the endpoint's size; a read takes packets from the replies queued for
/// its endpoint (else from what the endpoint's responder computes from the device's last write, else it answers
/// <see cref="PanelDeviceStatus.Timeout"/>) until its buffer is full or a short packet comes, as libusb reads: a reply
/// ending on a full packet runs on into the next one queued (with none queued the read answers what came), what is
/// left stays for the next read, and only a packet longer than what is left of the buffer
/// overflows (<see cref="PanelDeviceStatus.Io"/>, the read's bytes lost). A write with a zero-length packet asked for
/// and its length a multiple of the endpoint's packet size is traced as two: its bytes, then <c>zlp</c>.
/// </summary>
public sealed class FakeUsb
{
    private readonly FakePanelDevice _device;
    private readonly Dictionary<byte, Queue<byte[]>> _replies = [];
    private readonly Dictionary<byte, int> _offsets = []; // how much of each endpoint's first reply has been read
    private readonly Dictionary<byte, Func<PanelTraceEntry?, byte[]?>> _responders = [];
    private string? _serialNumber;
    private PanelDeviceStatus _openStatus;

    internal FakeUsb(FakePanelDevice device) => _device = device;

    internal HashSet<int> Claimed { get; } = []; // under the device's gate

    /// <summary>The serial number a handle answers (null: none).</summary>
    public string? SerialNumber
    {
        get
        {
            lock (_device.Gate)
                return _serialNumber;
        }
        set
        {
            lock (_device.Gate)
                _serialNumber = value;
        }
    }

    /// <summary>What an open of an interface the device has answers (<see cref="PanelDeviceStatus.Access"/>, say); <see cref="PanelDeviceStatus.Ok"/> by default.</summary>
    public PanelDeviceStatus OpenStatus
    {
        get
        {
            lock (_device.Gate)
                return _openStatus;
        }
        set
        {
            lock (_device.Gate)
                _openStatus = value;
        }
    }

    /// <summary>Queues <paramref name="data"/> as the next transfer the device sends on <paramref name="endpoint"/> (empty: a zero-length packet).</summary>
    public void Reply(byte[] data, byte endpoint = 0x81)
    {
        lock (_device.Gate)
            Queue(endpoint, data.ToArray());
    }

    /// <summary>
    /// Computes what a read from <paramref name="endpoint"/> gets when none is queued, from what the device last
    /// received (null when it received nothing yet); a null answer reads nothing (<see cref="PanelDeviceStatus.Timeout"/>).
    /// </summary>
    public void Respond(Func<PanelTraceEntry?, byte[]?> responder, byte endpoint = 0x81)
    {
        lock (_device.Gate)
            _responders[endpoint] = responder;
    }

    // Under the device's gate.
    private void Queue(byte endpoint, byte[] data)
    {
        if (!_replies.TryGetValue(endpoint, out var queue))
            _replies[endpoint] = queue = new Queue<byte[]>();
        queue.Enqueue(data);
    }

    // One read of at most `room` bytes in packets of `packetSize`: the bytes, or null when nothing came; `overflow` when a
    // packet didn't fit. Under the device's gate.
    private byte[]? Read(byte endpoint, int room, int packetSize, out bool overflow)
    {
        overflow = false;
        if ((!_replies.TryGetValue(endpoint, out var queue) || queue.Count == 0)
            && _responders.TryGetValue(endpoint, out var responder) && responder(_device.LastWrite) is { } computed)
            Queue(endpoint, computed.ToArray());
        if (!_replies.TryGetValue(endpoint, out queue) || queue.Count == 0)
            return null;

        var read = new List<byte>();
        while (true)
        {
            var reply = queue.Peek();
            var offset = _offsets.GetValueOrDefault(endpoint);
            var packet = Math.Min(packetSize, reply.Length - offset);
            var ends = offset + packet == reply.Length;
            if (ends)
            {
                queue.Dequeue();
                _offsets.Remove(endpoint);
            }
            else
            {
                _offsets[endpoint] = offset + packet;
            }
            if (packet > room - read.Count)
            {
                overflow = true; // libusb's overflow: the packet, and the read's bytes, lost
                return [];
            }
            read.AddRange(reply.AsSpan(offset, packet).ToArray());
            // A full packet ending its reply doesn't end the read: the next one queued goes on into it, as one transfer.
            if (packet < packetSize || read.Count == room || queue.Count == 0)
                return [.. read];
        }
    }

    internal sealed class Handle(FakePanelDevice device, int id, int interfaceNumber, IReadOnlyList<PanelUsbEndpoint> endpoints, string? serialNumber) : IPanelUsb
    {
        private readonly HandleState _state = new(id);

        public IReadOnlyList<PanelUsbEndpoint> Endpoints { get; } = endpoints;

        public string? SerialNumber { get; } = serialNumber;

        public PanelDeviceStatus BulkWrite(byte endpoint, ReadOnlySpan<byte> data, int timeoutMs, bool zeroLengthPacketIfFull = false, CancellationToken cancellationToken = default)
        {
            lock (device.Gate)
            {
                Check(timeoutMs, cancellationToken);
                var target = $"ep{endpoint:x2}";
                var (call, status, delivered) = device.Begin(timeoutMs, cancellationToken, _state);
                var outEndpoint = Endpoints.FirstOrDefault(e => e.Address == endpoint && !e.IsIn);
                if (status == PanelDeviceStatus.Ok && outEndpoint == null)
                    status = PanelDeviceStatus.Io;
                var entry = device.Record(PanelTraceOp.UsbWrite, target, data.ToArray(), status, _state.Id);
                if (status != PanelDeviceStatus.Ok)
                {
                    if (delivered > 0 && outEndpoint != null)
                        device.LastWrite = entry with { Data = data[..Math.Min(delivered, data.Length)].ToArray() };
                    return status;
                }
                device.LastWrite = entry;
                if (zeroLengthPacketIfFull && data.Length > 0 && data.Length % outEndpoint!.MaxPacketSize == 0)
                {
                    status = device.TerminatorOf(call);
                    device.Record(PanelTraceOp.UsbWrite, target, [], status, _state.Id);
                }
                return status;
            }
        }

        public PanelDeviceStatus BulkRead(byte endpoint, Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
        {
            read = 0;
            lock (device.Gate)
            {
                Check(timeoutMs, cancellationToken);
                var target = $"ep{endpoint:x2}";
                var (_, status, _) = device.Begin(timeoutMs, cancellationToken, _state);
                var inEndpoint = Endpoints.FirstOrDefault(e => e.Address == endpoint && e.IsIn);
                if (status == PanelDeviceStatus.Ok && inEndpoint == null)
                    status = PanelDeviceStatus.Io;
                byte[]? bytes = null;
                if (status == PanelDeviceStatus.Ok)
                {
                    bytes = device.Usb.Read(endpoint, buffer.Length, Math.Max(1, inEndpoint!.MaxPacketSize), out var overflow);
                    status = bytes == null ? PanelDeviceStatus.Timeout : overflow ? PanelDeviceStatus.Io : PanelDeviceStatus.Ok;
                }
                if (status != PanelDeviceStatus.Ok)
                {
                    device.Record(PanelTraceOp.UsbRead, target, [], status, _state.Id);
                    return status;
                }
                bytes!.CopyTo(buffer);
                read = bytes!.Length;
                device.Record(PanelTraceOp.UsbRead, target, bytes, status, _state.Id);
                return status;
            }
        }

        private void Check(int timeoutMs, CancellationToken token)
        {
            if (_state.Disposed)
                throw device.Disposed(nameof(IPanelUsb));
            IPanelDevices.CheckTimeout(timeoutMs);
            token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            lock (device.Gate)
            {
                if (_state.Disposed)
                    return;
                device.Release(_state);
                device.Usb.Claimed.Remove(interfaceNumber);
                device.Record(PanelTraceOp.Close, $"usb if{interfaceNumber}", [], PanelDeviceStatus.Ok, _state.Id);
            }
        }
    }
}

/// <summary>
/// One HID collection of a fake device: whether the device has it (<see cref="Present"/>), its id, what a filter sees of
/// it (its interface number, usage page, usage and report lengths), what opening it answers, and its replies: a read takes
/// the next input report queued, else asks the responder with what the device last received, else answers
/// <see cref="PanelDeviceStatus.Timeout"/>; a feature report got takes the next queued, else asks the feature responder
/// with its report id, else answers <see cref="PanelDeviceStatus.Io"/> (the device refused it). Each collection is opened
/// and held on its own.
/// </summary>
public sealed class FakeHid
{
    private readonly FakePanelDevice _device;
    private readonly Queue<byte[]> _replies = new();
    private readonly Queue<byte[]> _features = new();
    private Func<PanelTraceEntry?, byte[]?>? _responder;
    private Func<byte, byte[]?>? _featureResponder;
    private bool _present;
    private int _interface;
    private ushort _usagePage = 0xff00;
    private ushort _usage = 0x0001;
    private int _maxInput = 65;
    private int _maxOutput = 65;
    private int _maxFeature = 65;
    private PanelDeviceStatus _openStatus;

    internal FakeHid(FakePanelDevice device, string id)
    {
        _device = device;
        Id = id;
    }

    internal bool IsOpen { get; set; } // under the device's gate

    internal bool PresentUnlocked { get => _present; set => _present = value; } // under the device's gate

    internal PanelDeviceStatus OpenStatusUnlocked => _openStatus; // under the device's gate

    /// <summary>Its id, as <see cref="IPanelDevices.ListHid"/> lists it and the trace names it (<c>hid0</c>, <c>hid1</c> …).</summary>
    public string Id { get; }

    public bool Present { get => Get(() => _present); set => Set(() => _present = value); }

    /// <summary>Its interface number (0 by default).</summary>
    public int Interface { get => Get(() => _interface); set => Set(() => _interface = value); }

    /// <summary>Its top-level usage page (vendor-defined <c>0xff00</c> by default).</summary>
    public ushort UsagePage { get => Get(() => _usagePage); set => Set(() => _usagePage = value); }

    /// <summary>Its top-level usage (<c>0x0001</c> by default).</summary>
    public ushort Usage { get => Get(() => _usage); set => Set(() => _usage = value); }

    /// <summary>Its longest input report, the report id byte included (65 by default).</summary>
    public int MaxInputReportLength { get => Get(() => _maxInput); set => Set(() => _maxInput = value); }

    /// <summary>Its longest output report, the report id byte included (65 by default): a longer write is <see cref="PanelDeviceStatus.Io"/>.</summary>
    public int MaxOutputReportLength { get => Get(() => _maxOutput); set => Set(() => _maxOutput = value); }

    /// <summary>Its longest feature report, the report id byte included (65 by default).</summary>
    public int MaxFeatureReportLength { get => Get(() => _maxFeature); set => Set(() => _maxFeature = value); }

    /// <summary>What an open of it answers; <see cref="PanelDeviceStatus.Ok"/> by default.</summary>
    public PanelDeviceStatus OpenStatus { get => Get(() => _openStatus); set => Set(() => _openStatus = value); }

    /// <summary>Queues <paramref name="report"/> as the next input report read.</summary>
    public void Reply(byte[] report) => Set(() => _replies.Enqueue(report.ToArray()));

    /// <summary>Computes the input report a read gets when none is queued, from what the device last received; null: none comes.</summary>
    public void Respond(Func<PanelTraceEntry?, byte[]?> responder) => Set(() => _responder = responder);

    /// <summary>Queues <paramref name="report"/> as the next feature report got.</summary>
    public void ReplyFeature(byte[] report) => Set(() => _features.Enqueue(report.ToArray()));

    /// <summary>Computes the feature report got when none is queued, from the report id asked for; null: the device refuses.</summary>
    public void RespondFeature(Func<byte, byte[]?> responder) => Set(() => _featureResponder = responder);

    // Under the device's gate.
    internal bool Passes(PanelHidFilter filter) =>
        (filter.Interface is not { } number || number == _interface)
        && (filter.MinOutputReport is not { } least || _maxOutput >= least)
        && (filter.UsagePage is not { } page || page == _usagePage);

    // Under the device's gate.
    internal PanelHidCollection Describe() => new(Id, _interface, _usagePage, _usage, _maxInput, _maxOutput, _maxFeature);

    private T Get<T>(Func<T> get)
    {
        lock (_device.Gate)
            return get();
    }

    private void Set(Action set)
    {
        lock (_device.Gate)
            set();
    }

    internal sealed class Handle : IPanelHid
    {
        private readonly FakePanelDevice _device;
        private readonly FakeHid _hid;
        private readonly HandleState _state;

        public Handle(FakePanelDevice device, FakeHid hid, int id) // under the device's gate
        {
            _device = device;
            _hid = hid;
            _state = new HandleState(id);
            Collection = hid.Describe();
        }

        public PanelHidCollection Collection { get; }

        public int MaxInputReportLength => Collection.MaxInputReportLength;

        public int MaxOutputReportLength => Collection.MaxOutputReportLength;

        public int MaxFeatureReportLength => Collection.MaxFeatureReportLength;

        public PanelDeviceStatus Write(ReadOnlySpan<byte> report, int timeoutMs, CancellationToken cancellationToken = default) =>
            WriteOf(PanelTraceOp.HidWrite, report, MaxOutputReportLength, timeoutMs, cancellationToken);

        public PanelDeviceStatus SetFeature(ReadOnlySpan<byte> report, int timeoutMs, CancellationToken cancellationToken = default) =>
            WriteOf(PanelTraceOp.HidSetFeature, report, MaxFeatureReportLength, timeoutMs, cancellationToken);

        private PanelDeviceStatus WriteOf(PanelTraceOp op, ReadOnlySpan<byte> report, int max, int timeoutMs, CancellationToken token)
        {
            lock (_device.Gate)
            {
                Check(timeoutMs, token);
                var (_, status, _) = _device.Begin(timeoutMs, token, _state);
                if (status == PanelDeviceStatus.Ok && (report.Length == 0 || report.Length > max))
                    status = PanelDeviceStatus.Io;
                var entry = _device.Record(op, "", report.ToArray(), status, _state.Id);
                if (status == PanelDeviceStatus.Ok)
                    _device.LastWrite = entry;
                return status;
            }
        }

        public PanelDeviceStatus Read(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
        {
            lock (_device.Gate)
            {
                Check(timeoutMs, cancellationToken);
                var (_, status, _) = _device.Begin(timeoutMs, cancellationToken, _state);
                byte[]? reply = null;
                if (status == PanelDeviceStatus.Ok)
                {
                    reply = _hid._replies.Count > 0 ? _hid._replies.Dequeue() : _hid._responder?.Invoke(_device.LastWrite)?.ToArray();
                    status = reply == null ? PanelDeviceStatus.Timeout : reply.Length > buffer.Length ? PanelDeviceStatus.Io : PanelDeviceStatus.Ok;
                }
                return Answer(PanelTraceOp.HidRead, status, reply, buffer, out read);
            }
        }

        public PanelDeviceStatus GetFeature(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
        {
            lock (_device.Gate)
            {
                Check(timeoutMs, cancellationToken);
                var (_, status, _) = _device.Begin(timeoutMs, cancellationToken, _state);
                byte[]? reply = null;
                if (status == PanelDeviceStatus.Ok)
                {
                    if (buffer.Length == 0)
                        status = PanelDeviceStatus.Io;
                    else
                    {
                        reply = _hid._features.Count > 0 ? _hid._features.Dequeue() : _hid._featureResponder?.Invoke(buffer[0])?.ToArray();
                        status = reply == null || reply.Length > buffer.Length ? PanelDeviceStatus.Io : PanelDeviceStatus.Ok;
                    }
                }
                return Answer(PanelTraceOp.HidGetFeature, status, reply, buffer, out read);
            }
        }

        private PanelDeviceStatus Answer(PanelTraceOp op, PanelDeviceStatus status, byte[]? reply, Span<byte> buffer, out int read)
        {
            read = 0;
            if (status != PanelDeviceStatus.Ok)
            {
                _device.Record(op, "", [], status, _state.Id);
                return status;
            }
            var bytes = reply!;
            bytes.CopyTo(buffer);
            read = bytes.Length;
            _device.Record(op, "", bytes, status, _state.Id);
            return status;
        }

        private void Check(int timeoutMs, CancellationToken token)
        {
            if (_state.Disposed)
                throw _device.Disposed(nameof(IPanelHid));
            IPanelDevices.CheckTimeout(timeoutMs);
            token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            lock (_device.Gate)
            {
                if (_state.Disposed)
                    return;
                _device.Release(_state);
                _hid.IsOpen = false;
                _device.Record(PanelTraceOp.Close, "hid", [], PanelDeviceStatus.Ok, _state.Id);
            }
        }
    }
}

/// <summary>
/// A fake device's serial port: whether it has one (<see cref="Present"/>), its name, what opening it answers, the
/// settings it was last opened with, and its replies. A reply answers a write: each write that goes through (or, failed,
/// delivered a prefix: then from that prefix) puts the next queued reply into the port's input, else what the responder
/// computes from it; <see cref="Arrive"/> puts bytes
/// there unasked. A read takes what is in the input, at most its buffer, and answers <see cref="PanelDeviceStatus.Timeout"/>
/// when it is empty.
/// </summary>
public sealed class FakeSerial
{
    private readonly FakePanelDevice _device;
    private readonly Queue<byte[]> _replies = new();
    private readonly List<byte> _input = [];
    private Func<PanelTraceEntry, byte[]?>? _responder;
    private bool _present;
    private string _portName = "COM9";
    private PanelDeviceStatus _openStatus;
    private PanelSerialOptions? _options;

    internal FakeSerial(FakePanelDevice device) => _device = device;

    internal bool IsOpen { get; set; } // under the device's gate

    public bool Present
    {
        get
        {
            lock (_device.Gate)
                return _present;
        }
        set
        {
            lock (_device.Gate)
                _present = value;
        }
    }

    /// <summary>The port's name a handle answers (<c>COM9</c> by default).</summary>
    public string PortName
    {
        get
        {
            lock (_device.Gate)
                return _portName;
        }
        set
        {
            lock (_device.Gate)
                _portName = value;
        }
    }

    /// <summary>What an open of a device with a port answers; <see cref="PanelDeviceStatus.Ok"/> by default.</summary>
    public PanelDeviceStatus OpenStatus
    {
        get
        {
            lock (_device.Gate)
                return _openStatus;
        }
        set
        {
            lock (_device.Gate)
                _openStatus = value;
        }
    }

    /// <summary>The settings the port was last opened with (null: never opened).</summary>
    public PanelSerialOptions? Options
    {
        get
        {
            lock (_device.Gate)
                return _options;
        }
        internal set => _options = value; // under the device's gate
    }

    /// <summary>Queues <paramref name="data"/> as the answer to the next write.</summary>
    public void Reply(byte[] data)
    {
        lock (_device.Gate)
            _replies.Enqueue(data.ToArray());
    }

    /// <summary>Computes the answer to a write when none is queued, from the write itself; null: no answer.</summary>
    public void Respond(Func<PanelTraceEntry, byte[]?> responder)
    {
        lock (_device.Gate)
            _responder = responder;
    }

    /// <summary>Puts <paramref name="data"/> in the port's input now, unasked.</summary>
    public void Arrive(byte[] data)
    {
        lock (_device.Gate)
            _input.AddRange(data);
    }

    internal sealed class Handle(FakePanelDevice device, int id, string portName) : IPanelSerial
    {
        private readonly FakeSerial _serial = device.Serial;
        private readonly HandleState _state = new(id);

        public string PortName { get; } = portName;

        public PanelDeviceStatus Write(ReadOnlySpan<byte> data, int timeoutMs, CancellationToken cancellationToken = default)
        {
            lock (device.Gate)
            {
                Check(timeoutMs, cancellationToken);
                var (_, status, delivered) = device.Begin(timeoutMs, cancellationToken, _state);
                var entry = device.Record(PanelTraceOp.SerialWrite, "", data.ToArray(), status, _state.Id);
                if (status != PanelDeviceStatus.Ok)
                {
                    if (delivered > 0)
                        Answer(entry with { Data = data[..Math.Min(delivered, data.Length)].ToArray() });
                    return status;
                }
                Answer(entry);
                return status;
            }
        }

        // The device got `seen` (a whole write, or the prefix a failed one delivered): it is the last write, and it is
        // answered. Under the device's gate.
        private void Answer(PanelTraceEntry seen)
        {
            device.LastWrite = seen;
            var answer = _serial._replies.Count > 0 ? _serial._replies.Dequeue() : _serial._responder?.Invoke(seen);
            if (answer != null)
                _serial._input.AddRange(answer);
        }

        public PanelDeviceStatus Read(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
        {
            read = 0;
            lock (device.Gate)
            {
                Check(timeoutMs, cancellationToken);
                var (_, status, _) = device.Begin(timeoutMs, cancellationToken, _state);
                if (status == PanelDeviceStatus.Ok && (_serial._input.Count == 0 || buffer.Length == 0))
                    status = PanelDeviceStatus.Timeout;
                if (status != PanelDeviceStatus.Ok)
                {
                    device.Record(PanelTraceOp.SerialRead, "", [], status, _state.Id);
                    return status;
                }
                read = Math.Min(buffer.Length, _serial._input.Count);
                var bytes = _serial._input.GetRange(0, read).ToArray();
                _serial._input.RemoveRange(0, read);
                bytes.CopyTo(buffer);
                device.Record(PanelTraceOp.SerialRead, "", bytes, status, _state.Id);
                return status;
            }
        }

        public PanelDeviceStatus DiscardInput()
        {
            lock (device.Gate)
            {
                if (_state.Disposed)
                    throw device.Disposed(nameof(IPanelSerial));
                var (_, status, _) = device.Begin(1, CancellationToken.None, null); // it doesn't wait: never stalled for long
                if (status == PanelDeviceStatus.Ok)
                    _serial._input.Clear();
                device.Record(PanelTraceOp.SerialDiscard, "", [], status, _state.Id);
                return status;
            }
        }

        private void Check(int timeoutMs, CancellationToken token)
        {
            if (_state.Disposed)
                throw device.Disposed(nameof(IPanelSerial));
            IPanelDevices.CheckTimeout(timeoutMs);
            token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            lock (device.Gate)
            {
                if (_state.Disposed)
                    return;
                device.Release(_state);
                _serial.IsOpen = false;
                device.Record(PanelTraceOp.Close, "serial", [], PanelDeviceStatus.Ok, _state.Id);
            }
        }
    }
}
