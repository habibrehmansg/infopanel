using InfoPanel.Panels.Usb;

namespace InfoPanel.Core.Tests.Panels.Usb;

/// <summary>
/// A clock for USB tests: it moves only when something waits on it. A timer (a <c>Task.Delay</c> through it) moves
/// the clock by its due time and fires at once; a fake device's write delay moves it too. So a session's waits and
/// transfers land at exact, repeatable times.
/// </summary>
public sealed class FakeUsbClock : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => Start.AddTicks(GetTimestamp());

    /// <summary>Milliseconds since the clock started.</summary>
    public long NowMs => GetTimestamp() / TimeSpan.TicksPerMillisecond;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FiredTimer();
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Advance(dueTime);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!timer.Disposed)
                    callback(state);
            });
        }
        return timer;
    }

    private sealed class FiredTimer : ITimer
    {
        public volatile bool Disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>One transfer a fake device saw: when (the fake clock's milliseconds), on which endpoint, and its bytes.</summary>
public sealed record FakeTransfer(long AtMs, byte Endpoint, byte[] Data)
{
    public bool In => (Endpoint & 0x80) != 0;
}

/// <summary>One write as <see cref="FakeUsbDevice.OnWrite"/> sees it: its number (from 1), endpoint, bytes and timeout.</summary>
public sealed record FakeWrite(int Number, byte Endpoint, byte[] Data, int TimeoutMs);

/// <summary>
/// A scripted USB device: its configuration, what opening and claiming it answer, its info reply, a delay per write,
/// a failure or an unplug at a given write. Every transfer is recorded (<see cref="Transfers"/>) and every call by name
/// (<see cref="Calls"/>).
/// </summary>
public sealed class FakeUsbDevice(UsbDeviceId id)
{
    internal readonly Lock Gate = new();
    private readonly List<FakeTransfer> _transfers = [];
    private readonly List<string> _calls = [];
    private readonly Dictionary<int, UsbStatus> _failures = [];
    private readonly List<int> _timeouts = [];
    private int _writes;

    public UsbDeviceId Id { get; } = id;

    /// <summary>The active configuration; 0 is unconfigured.</summary>
    public int Configuration { get; set; } = 1;

    public UsbStatus OpenResult { get; set; } = UsbStatus.Ok;

    public UsbStatus ClaimResult { get; set; } = UsbStatus.Ok;

    /// <summary>The USB descriptor's serial number string.</summary>
    public string? UsbSerial { get; set; }

    /// <summary>Its cached descriptors, as <see cref="IUsbBackend.Describe"/> answers them (no call is recorded: the OS's cache, no I/O); null: the backend can't tell.</summary>
    public UsbDescriptor? Descriptor { get; set; }

    /// <summary>What a read on an in endpoint answers; none: the read times out.</summary>
    public byte[]? Reply { get; set; }

    /// <summary>How long each write takes on the fake clock.</summary>
    public TimeSpan WriteDelay { get; set; }

    /// <summary>The write (counted from 1 over every endpoint) at which the device is unplugged; that write fails.</summary>
    public int? UnplugAtWrite { get; set; }

    public bool Connected { get; internal set; } = true;

    /// <summary>Handles open now.</summary>
    public int OpenHandles { get; internal set; }

    /// <summary>
    /// Called on every write after it is recorded, outside the device's lock, on the writing thread: it may block, move
    /// the clock or cancel something, and its non-null answer is the write's status.
    /// </summary>
    public Func<FakeWrite, UsbStatus?>? OnWrite { get; set; }

    /// <summary>The write (counted from 1) that fails with <paramref name="status"/>.</summary>
    public void FailWrite(int write, UsbStatus status)
    {
        lock (Gate)
            _failures[write] = status;
    }

    public FakeTransfer[] Transfers
    {
        get
        {
            lock (Gate)
                return [.. _transfers];
        }
    }

    public string[] Calls
    {
        get
        {
            lock (Gate)
                return [.. _calls];
        }
    }

    internal void Call(string name)
    {
        lock (Gate)
            _calls.Add(name);
    }

    /// <summary>The timeout each write was given, in milliseconds, in order.</summary>
    public int[] WriteTimeouts
    {
        get
        {
            lock (Gate)
                return [.. _timeouts];
        }
    }

    internal UsbStatus Write(FakeUsbClock clock, byte endpoint, ReadOnlySpan<byte> data, int timeoutMs)
    {
        FakeWrite write;
        lock (Gate)
        {
            if (!Connected)
                return UsbStatus.NoDevice;
            _writes++;
            _timeouts.Add(timeoutMs);
            _transfers.Add(new FakeTransfer(clock.NowMs, endpoint, data.ToArray()));
            if (UnplugAtWrite == _writes)
            {
                Connected = false;
                return UsbStatus.NoDevice;
            }
            if (_failures.TryGetValue(_writes, out var failure))
                return failure;
            write = new FakeWrite(_writes, endpoint, data.ToArray(), timeoutMs);
        }
        if (OnWrite?.Invoke(write) is { } answer)
            return answer;
        if (WriteDelay > TimeSpan.Zero)
            clock.Advance(WriteDelay);
        return UsbStatus.Ok;
    }

    internal UsbStatus Read(FakeUsbClock clock, byte endpoint, Span<byte> buffer, out int read)
    {
        read = 0;
        lock (Gate)
        {
            if (!Connected)
                return UsbStatus.NoDevice;
            if (Reply is null)
            {
                _transfers.Add(new FakeTransfer(clock.NowMs, endpoint, []));
                return UsbStatus.Timeout;
            }
            read = Math.Min(buffer.Length, Reply.Length);
            Reply.AsSpan(0, read).CopyTo(buffer);
            _transfers.Add(new FakeTransfer(clock.NowMs, endpoint, buffer[..read].ToArray()));
            return UsbStatus.Ok;
        }
    }
}

/// <summary>An <see cref="IUsbBackend"/> over <see cref="FakeUsbDevice"/>s, for tests.</summary>
public sealed class FakeUsbBackend(FakeUsbClock? clock = null) : IUsbBackend
{
    private readonly Lock _lock = new();
    private readonly List<FakeUsbDevice> _devices = [];

    public FakeUsbClock Clock { get; } = clock ?? new FakeUsbClock();

    public bool Disposed { get; private set; }

    /// <summary>How many times <see cref="Describe"/> was called, on any device.</summary>
    public int Describes { get; private set; }

    public FakeUsbDevice Add(FakeUsbDevice device)
    {
        lock (_lock)
            _devices.Add(device);
        return device;
    }

    /// <summary>Unplugs <paramref name="device"/>: gone from the list, and every call on it fails.</summary>
    public void Unplug(FakeUsbDevice device)
    {
        lock (device.Gate)
            device.Connected = false;
    }

    public IReadOnlyList<UsbDeviceId> ListDevices()
    {
        lock (_lock)
        {
            var listed = new List<UsbDeviceId>();
            foreach (var d in _devices)
                if (d.Connected)
                    listed.Add(d.Id);
            return listed;
        }
    }

    public UsbStatus Open(UsbDeviceId device, out IUsbDevice? opened)
    {
        opened = null;
        FakeUsbDevice? found;
        lock (_lock)
            found = _devices.Find(d => d.Id.Bus == device.Bus && d.Id.Address == device.Address && d.Connected); // as libusb: by bus and address
        if (found is null)
            return UsbStatus.NoDevice;
        found.Call("open");
        if (found.OpenResult != UsbStatus.Ok)
            return found.OpenResult;
        lock (found.Gate)
            found.OpenHandles++;
        opened = new Handle(this, found);
        return UsbStatus.Ok;
    }

    /// <summary>A listed device's <see cref="FakeUsbDevice.Descriptor"/>; <see cref="UsbStatus.NotSupported"/> when it has none.</summary>
    public UsbStatus Describe(UsbDeviceId device, out UsbDescriptor? descriptor)
    {
        descriptor = null;
        FakeUsbDevice? found;
        lock (_lock)
        {
            Describes++;
            found = _devices.Find(d => d.Id == device && d.Connected);
        }
        if (found is null)
            return UsbStatus.NoDevice;
        descriptor = found.Descriptor;
        return descriptor is null ? UsbStatus.NotSupported : UsbStatus.Ok;
    }

    public void Dispose() => Disposed = true;

    private sealed class Handle(FakeUsbBackend owner, FakeUsbDevice device) : IUsbDevice
    {
        private bool _closed;

        public UsbStatus GetConfiguration(out int configuration)
        {
            device.Call("getConfiguration");
            configuration = device.Configuration;
            return device.Connected ? UsbStatus.Ok : UsbStatus.NoDevice;
        }

        public UsbStatus SetConfiguration(int configuration)
        {
            device.Call($"setConfiguration {configuration}");
            if (!device.Connected)
                return UsbStatus.NoDevice;
            device.Configuration = configuration;
            return UsbStatus.Ok;
        }

        public UsbStatus ClaimInterface(int interfaceNumber)
        {
            device.Call($"claim {interfaceNumber}");
            return device.Connected ? device.ClaimResult : UsbStatus.NoDevice;
        }

        public UsbStatus ReleaseInterface(int interfaceNumber)
        {
            device.Call($"release {interfaceNumber}");
            return UsbStatus.Ok;
        }

        public string? ReadSerialNumber()
        {
            device.Call("readSerial");
            return device.UsbSerial;
        }

        public UsbStatus BulkWrite(byte endpoint, ReadOnlySpan<byte> data, int timeoutMs)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (data.IsEmpty)
                throw new ArgumentException("A bulk write is never empty (no zero-length packet).", nameof(data));
            return device.Write(owner.Clock, endpoint, data, timeoutMs);
        }

        public UsbStatus BulkRead(byte endpoint, Span<byte> buffer, int timeoutMs, out int read)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return device.Read(owner.Clock, endpoint, buffer, out read);
        }

        public void Dispose()
        {
            if (_closed)
                return;
            _closed = true;
            device.Call("close");
            lock (device.Gate)
                device.OpenHandles--;
        }
    }
}
