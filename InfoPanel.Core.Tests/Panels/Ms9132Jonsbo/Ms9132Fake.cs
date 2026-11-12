using InfoPanel.Panels.Ms9132Jonsbo;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;

namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo;

/// <summary>
/// An MS9132 on the kit's fakes, as 1.4.x met one: a HID control collection whose feature reports echo the report set
/// (its id, opcode and address) and answer an EDID read with the 4 bytes of <see cref="Edid"/> at its address, and a
/// vendor interface 3 with bulk OUT endpoint 0x04 (and an IN, 0x83). With <see cref="EdidAfterPowerOn"/> the bridge
/// answers EDID reads with zeros until it has been powered on (<c>A6 07 01 02</c>). Added to <see cref="Ms9132Devices"/>,
/// whose handles fail every report after one timed out on them, as the engine's seam does.
/// </summary>
internal sealed class Ms9132Fake
{
    public static readonly PanelCandidate Candidate = new(0x345F, 0x9132, 1, 9, "1-4");

    private Ms9132Fake(FakePanelDevice device) => Device = device;

    public FakePanelDevice Device { get; }

    public byte[]? Edid { get; set; }

    public bool EdidAfterPowerOn { get; set; }

    /// <summary>A status reply's data byte 0 (register 0x32: 1, a panel connected).</summary>
    public byte PanelConnected { get; set; } = 1;

    /// <summary>
    /// The bridge answers every report but the EDID and status reads (<c>B5 C0 xx</c>, <c>B5 00 32</c>) with nine zero
    /// bytes, echoing nothing, as a write's reply 1.4.x got and never looked at may be.
    /// </summary>
    public bool WriteRepliesZero { get; set; }

    public static Ms9132Fake Add(Ms9132Devices devices, byte[]? edid, PanelCandidate? at = null)
    {
        var device = devices.Add(at ?? Candidate, PanelTransport.Hid);
        device.Descriptor = new PanelUsbDescriptor(0xEF, 0x02, 0x01, 0x0100,
        [
            new PanelUsbInterface(0, 0x03, 0, 0, [new PanelUsbEndpoint(0x81, PanelEndpointType.Interrupt, 64)]),
            new PanelUsbInterface(3, 0xFF, 0, 0,
            [
                new PanelUsbEndpoint(0x83, PanelEndpointType.Bulk, 512),
                new PanelUsbEndpoint(0x04, PanelEndpointType.Bulk, 512),
            ]),
        ]);
        device.Hid.MaxFeatureReportLength = Ms9132Protocol.FeatureLength;
        device.Hid.MaxInputReportLength = Ms9132Protocol.FeatureLength;
        device.Hid.MaxOutputReportLength = Ms9132Protocol.FeatureLength;
        var fake = new Ms9132Fake(device) { Edid = edid };
        device.Hid.RespondFeature(_ => fake.Reply());
        return fake;
    }

    /// <summary>The reply to the feature report last delivered (under the device's lock: the trace is read, the device isn't called).</summary>
    private byte[]? Reply()
    {
        var entries = Device.Trace.Entries;
        var last = entries.LastOrDefault(e => e.Op == PanelTraceOp.HidSetFeature && e.Status == PanelDeviceStatus.Ok);
        var set = last?.Data ?? new byte[9];
        if (WriteRepliesZero && !(set[1] == 0xB5 && (set[2] == 0xC0 || (set[2] == 0x00 && set[3] == 0x32))))
            return new byte[9];
        var powered = !EdidAfterPowerOn || entries.Any(e => e.Op == PanelTraceOp.HidSetFeature && e.Data.AsSpan(1, 4).SequenceEqual((byte[])[0xA6, 0x07, 0x01, 0x02]));
        var reply = EdidSamples.Reply(set, powered ? Edid : null);
        if (set[1] == 0xB5 && set[2] == 0x00 && set[3] == 0x32)
            reply[4] = PanelConnected;
        return reply;
    }

    /// <summary>The payloads of the feature reports set from entry <paramref name="from"/> on, in order (the report id dropped).</summary>
    public List<byte[]> Reports(int from = 0) =>
        [.. Device.Trace.Entries.Skip(from).Where(e => e.Op == PanelTraceOp.HidSetFeature).Select(e => e.Data[1..])];

    /// <summary>
    /// The payloads of the feature reports delivered from entry <paramref name="from"/> on (set with
    /// <see cref="PanelDeviceStatus.Ok"/>), in order: <see cref="Reports"/> lists every report attempted.
    /// </summary>
    public List<byte[]> Delivered(int from = 0) =>
        [.. Device.Trace.Entries.Skip(from).Where(e => e.Op == PanelTraceOp.HidSetFeature && e.Status == PanelDeviceStatus.Ok).Select(e => e.Data[1..])];

    /// <summary>The bulk writes from entry <paramref name="from"/> on, in order.</summary>
    public List<byte[]> Writes(int from = 0) =>
        [.. Device.Trace.Entries.Skip(from).Where(e => e.Op == PanelTraceOp.UsbWrite).Select(e => e.Data)];

    /// <summary>The trace's lines that aren't feature reports or bulk writes (the opens and closes), with their handles.</summary>
    public List<string> Handles() =>
        [.. Device.Trace.Entries.Where(e => e.Op is PanelTraceOp.Open or PanelTraceOp.Close).Select(PanelTrace.FormatLine)];
}

/// <summary>
/// The MS9132 tests' device seam: the kit's fakes (<see cref="Fakes"/>), each HID collection opened through it handed out
/// behind <see cref="TimedOutHid"/>, the engine's rule for a report that timed out.
/// </summary>
internal sealed class Ms9132Devices : IPanelDevices
{
    public FakePanelDevices Fakes { get; } = new();

    public FakePanelDevice this[PanelCandidate candidate] => Fakes[candidate];

    public FakePanelDevice Add(PanelCandidate candidate, params PanelTransport[] transports) => Fakes.Add(candidate, transports);

    public PanelDeviceStatus Describe(PanelCandidate device, out PanelUsbDescriptor? descriptor) => Fakes.Describe(device, out descriptor);

    public PanelDeviceStatus OpenUsb(PanelCandidate device, int interfaceNumber, int timeoutMs, out IPanelUsb? usb, CancellationToken cancellationToken = default) =>
        Fakes.OpenUsb(device, interfaceNumber, timeoutMs, out usb, cancellationToken);

    public PanelDeviceStatus ListHid(PanelCandidate device, PanelHidFilter filter, out IReadOnlyList<PanelHidCollection> collections) =>
        Fakes.ListHid(device, filter, out collections);

    public PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidFilter filter, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default)
    {
        var status = Fakes.OpenHid(device, filter, timeoutMs, out var opened, cancellationToken);
        hid = opened == null ? null : new TimedOutHid(opened, Fakes[device]);
        return status;
    }

    public PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidCollection collection, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default)
    {
        var status = Fakes.OpenHid(device, collection, timeoutMs, out var opened, cancellationToken);
        hid = opened == null ? null : new TimedOutHid(opened, Fakes[device]);
        return status;
    }

    public PanelDeviceStatus OpenSerial(PanelCandidate device, PanelSerialOptions options, int timeoutMs, out IPanelSerial? serial, CancellationToken cancellationToken = default) =>
        Fakes.OpenSerial(device, options, timeoutMs, out serial, cancellationToken);
}

/// <summary>
/// A HID collection's handle as the engine's seam has it: once a call on it has timed out, the seam has closed the
/// collection's stream (HidSharpSystem.Bounded), so every later call on this handle, a set as much as a get, fails with
/// <see cref="PanelDeviceStatus.Io"/> and delivers nothing. The call still reaches the kit's fake, failed there, so the
/// trace keeps it as attempted (<c>!io</c>) and never as delivered (<see cref="Ms9132Fake.Delivered"/>). A new handle on
/// the collection is a new stream. One call at a time, as the seam's handles take them.
/// </summary>
internal sealed class TimedOutHid(IPanelHid hid, FakePanelDevice device) : IPanelHid
{
    private bool _closed;

    // The next call on the device (this one: one at a time) fails at the fake when the stream is closed.
    private void Closing()
    {
        if (_closed)
            device.Fail(device.Calls + 1, PanelDeviceStatus.Io);
    }

    private PanelDeviceStatus After(PanelDeviceStatus status)
    {
        if (status == PanelDeviceStatus.Timeout)
            _closed = true;
        return status;
    }

    public PanelHidCollection Collection => hid.Collection;

    public int MaxInputReportLength => hid.MaxInputReportLength;

    public int MaxOutputReportLength => hid.MaxOutputReportLength;

    public int MaxFeatureReportLength => hid.MaxFeatureReportLength;

    public PanelDeviceStatus Write(ReadOnlySpan<byte> report, int timeoutMs, CancellationToken cancellationToken = default)
    {
        Closing();
        return After(hid.Write(report, timeoutMs, cancellationToken));
    }

    public PanelDeviceStatus Read(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
    {
        Closing();
        return After(hid.Read(buffer, timeoutMs, out read, cancellationToken));
    }

    public PanelDeviceStatus SetFeature(ReadOnlySpan<byte> report, int timeoutMs, CancellationToken cancellationToken = default)
    {
        Closing();
        return After(hid.SetFeature(report, timeoutMs, cancellationToken));
    }

    public PanelDeviceStatus GetFeature(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
    {
        Closing();
        return After(hid.GetFeature(buffer, timeoutMs, out read, cancellationToken));
    }

    public void Dispose() => hid.Dispose();
}
