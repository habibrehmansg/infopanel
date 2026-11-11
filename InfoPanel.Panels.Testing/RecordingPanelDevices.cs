using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Testing;

/// <summary>
/// Wraps any device seam (the engine's, on a tester's hardware) and records every call made through it, with what it
/// came to, in <see cref="Trace"/> and as the kit's text trace to a file or writer, a line a call, flushed at once: each
/// open on the next handle (<c>h1</c>, <c>h2</c> …), preceded by a <c>#</c> line naming the device it opened, and each
/// transfer and close on the handle it was made on, so the calls of several panels recorded at once are told apart. A
/// hardware run reads back (<see cref="PanelTrace.Parse"/>) as the calls the driver made, ready to become a family's
/// test. A zero-length packet is recorded as the fakes record it: when the driver asked for one and the write, gone
/// through, filled its last packet (a write that failed is one line with its status: the seam doesn't say whether its
/// bytes or its zero-length packet failed). A call cancelled by its token throws, as the seam's does, and isn't
/// recorded. Opening and transfers are passed on unchanged; only the trace is added. When an open can't be recorded (the
/// writer throws), the handle it opened is disposed before the error is thrown, so the device isn't left held.
/// </summary>
public sealed class RecordingPanelDevices : IPanelDevices, IDisposable
{
    private readonly IPanelDevices _inner;
    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly object _gate = new();
    private int _handles;

    /// <summary>
    /// Records to the file at <paramref name="path"/>, appended to (created when absent): its handles numbered on from the
    /// highest one the file already holds, so an earlier recording's handles are never named again.
    /// </summary>
    public RecordingPanelDevices(IPanelDevices inner, string path)
        : this(inner, OpenAppend(path, out var handles), ownsWriter: true, handles)
    {
    }

    /// <summary>Records to <paramref name="writer"/>, which stays the caller's to dispose; its handles from <c>h1</c>.</summary>
    public RecordingPanelDevices(IPanelDevices inner, TextWriter writer)
        : this(inner, writer, ownsWriter: false, handles: 0)
    {
    }

    private RecordingPanelDevices(IPanelDevices inner, TextWriter writer, bool ownsWriter, int handles)
    {
        _inner = inner;
        _writer = writer;
        _ownsWriter = ownsWriter;
        _handles = handles;
    }

    // The file opened to append to, read first for the highest handle it holds; no other writer while it is open.
    private static StreamWriter OpenAppend(string path, out int handles)
    {
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            using (var reader = new StreamReader(file, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true))
                handles = PanelTrace.HighestHandle(reader.ReadToEnd());
            file.Seek(0, SeekOrigin.End);
            return new StreamWriter(file, new UTF8Encoding(false)) { NewLine = "\n" };
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Every call recorded so far, in order.</summary>
    public PanelTrace Trace { get; } = new();

    /// <summary>How a trace names a device: <c>1234:5678 at 1-2 (bus 1, address 7)</c>.</summary>
    public static string DeviceName(PanelCandidate device) =>
        $"{device.VendorId:x4}:{device.ProductId:x4} at {device.PortPath} (bus {device.Bus}, address {device.Address})";

    public PanelDeviceStatus Describe(PanelCandidate device, out PanelUsbDescriptor? descriptor) => _inner.Describe(device, out descriptor);

    public PanelDeviceStatus ListHid(PanelCandidate device, PanelHidFilter filter, out IReadOnlyList<PanelHidCollection> collections) =>
        _inner.ListHid(device, filter, out collections);

    public PanelDeviceStatus OpenUsb(PanelCandidate device, int interfaceNumber, int timeoutMs, out IPanelUsb? usb, CancellationToken cancellationToken = default)
    {
        var status = _inner.OpenUsb(device, interfaceNumber, timeoutMs, out var inner, cancellationToken);
        var handle = Opened(device, $"usb if{interfaceNumber}", status, inner);
        usb = inner == null ? null : new Usb(this, inner, handle, interfaceNumber);
        return status;
    }

    public PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidFilter filter, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default)
    {
        var status = _inner.OpenHid(device, filter, timeoutMs, out var inner, cancellationToken);
        var handle = Opened(device, HidTarget(inner?.Collection), status, inner);
        hid = inner == null ? null : new Hid(this, inner, handle);
        return status;
    }

    public PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidCollection collection, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default)
    {
        var status = _inner.OpenHid(device, collection, timeoutMs, out var inner, cancellationToken);
        var handle = Opened(device, HidTarget(inner?.Collection ?? collection), status, inner);
        hid = inner == null ? null : new Hid(this, inner, handle);
        return status;
    }

    public PanelDeviceStatus OpenSerial(PanelCandidate device, PanelSerialOptions options, int timeoutMs, out IPanelSerial? serial, CancellationToken cancellationToken = default)
    {
        var status = _inner.OpenSerial(device, options, timeoutMs, out var inner, cancellationToken);
        var handle = Opened(device, $"serial {options.Baud}{(options.Dtr ? " dtr" : "")}{(options.Rts ? " rts" : "")}", status, inner);
        serial = inner == null ? null : new Serial(this, inner, handle);
        return status;
    }

    /// <summary>Flushes the trace and, when it opened the file, closes it.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Flush();
            if (_ownsWriter)
                _writer.Dispose();
        }
    }

    // A collection's id as one word of a line.
    private static string HidTarget(PanelHidCollection? collection) =>
        collection == null ? "hid" : "hid " + string.Concat(collection.Id.Select(c => char.IsWhiteSpace(c) ? '_' : c));

    // The open's lines on the next handle; the handle it opened disposed when they can't be written.
    private int Opened(PanelCandidate device, string target, PanelDeviceStatus status, IDisposable? opened)
    {
        try
        {
            lock (_gate)
            {
                var handle = ++_handles;
                Record(new PanelTraceEntry(PanelTraceOp.Open, target, [], status) { Handle = handle, Device = DeviceName(device) });
                return handle;
            }
        }
        catch
        {
            opened?.Dispose(); // never the caller's: it gets the error, not the handle
            throw;
        }
    }

    private void Record(PanelTraceEntry entry)
    {
        lock (_gate)
        {
            _writer.Write(PanelTrace.FormatLines(entry));
            _writer.Flush();
            Trace.Add(entry);
        }
    }

    private void Record(int handle, PanelTraceOp op, string target, byte[] data, PanelDeviceStatus status) =>
        Record(new PanelTraceEntry(op, target, data, status) { Handle = handle });

    private void RecordRead(int handle, PanelTraceOp op, string target, PanelDeviceStatus status, ReadOnlySpan<byte> buffer, int read) =>
        Record(handle, op, target, status == PanelDeviceStatus.Ok ? buffer[..Math.Clamp(read, 0, buffer.Length)].ToArray() : [], status);

    private sealed class Usb(RecordingPanelDevices recorder, IPanelUsb inner, int handle, int interfaceNumber) : IPanelUsb
    {
        private int _disposed;

        public IReadOnlyList<PanelUsbEndpoint> Endpoints => inner.Endpoints;

        public string? SerialNumber => inner.SerialNumber;

        public PanelDeviceStatus BulkWrite(byte endpoint, ReadOnlySpan<byte> data, int timeoutMs, bool zeroLengthPacketIfFull = false, CancellationToken cancellationToken = default)
        {
            var status = inner.BulkWrite(endpoint, data, timeoutMs, zeroLengthPacketIfFull, cancellationToken);
            var target = $"ep{endpoint:x2}";
            recorder.Record(handle, PanelTraceOp.UsbWrite, target, data.ToArray(), status);
            if (status == PanelDeviceStatus.Ok && zeroLengthPacketIfFull && data.Length > 0
                && inner.Endpoints.FirstOrDefault(e => e.Address == endpoint) is { MaxPacketSize: > 0 } written
                && data.Length % written.MaxPacketSize == 0)
                recorder.Record(handle, PanelTraceOp.UsbWrite, target, [], PanelDeviceStatus.Ok);
            return status;
        }

        public PanelDeviceStatus BulkRead(byte endpoint, Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
        {
            var status = inner.BulkRead(endpoint, buffer, timeoutMs, out read, cancellationToken);
            recorder.RecordRead(handle, PanelTraceOp.UsbRead, $"ep{endpoint:x2}", status, buffer, read);
            return status;
        }

        public void Dispose()
        {
            inner.Dispose();
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                recorder.Record(handle, PanelTraceOp.Close, $"usb if{interfaceNumber}", [], PanelDeviceStatus.Ok);
        }
    }

    private sealed class Hid(RecordingPanelDevices recorder, IPanelHid inner, int handle) : IPanelHid
    {
        private int _disposed;

        public PanelHidCollection Collection => inner.Collection;

        public int MaxInputReportLength => inner.MaxInputReportLength;

        public int MaxOutputReportLength => inner.MaxOutputReportLength;

        public int MaxFeatureReportLength => inner.MaxFeatureReportLength;

        public PanelDeviceStatus Write(ReadOnlySpan<byte> report, int timeoutMs, CancellationToken cancellationToken = default)
        {
            var status = inner.Write(report, timeoutMs, cancellationToken);
            recorder.Record(handle, PanelTraceOp.HidWrite, "", report.ToArray(), status);
            return status;
        }

        public PanelDeviceStatus Read(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
        {
            var status = inner.Read(buffer, timeoutMs, out read, cancellationToken);
            recorder.RecordRead(handle, PanelTraceOp.HidRead, "", status, buffer, read);
            return status;
        }

        public PanelDeviceStatus SetFeature(ReadOnlySpan<byte> report, int timeoutMs, CancellationToken cancellationToken = default)
        {
            var status = inner.SetFeature(report, timeoutMs, cancellationToken);
            recorder.Record(handle, PanelTraceOp.HidSetFeature, "", report.ToArray(), status);
            return status;
        }

        public PanelDeviceStatus GetFeature(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
        {
            var status = inner.GetFeature(buffer, timeoutMs, out read, cancellationToken);
            recorder.RecordRead(handle, PanelTraceOp.HidGetFeature, "", status, buffer, read);
            return status;
        }

        public void Dispose()
        {
            inner.Dispose();
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                recorder.Record(handle, PanelTraceOp.Close, "hid", [], PanelDeviceStatus.Ok);
        }
    }

    private sealed class Serial(RecordingPanelDevices recorder, IPanelSerial inner, int handle) : IPanelSerial
    {
        private int _disposed;

        public string PortName => inner.PortName;

        public PanelDeviceStatus Write(ReadOnlySpan<byte> data, int timeoutMs, CancellationToken cancellationToken = default)
        {
            var status = inner.Write(data, timeoutMs, cancellationToken);
            recorder.Record(handle, PanelTraceOp.SerialWrite, "", data.ToArray(), status);
            return status;
        }

        public PanelDeviceStatus Read(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
        {
            var status = inner.Read(buffer, timeoutMs, out read, cancellationToken);
            recorder.RecordRead(handle, PanelTraceOp.SerialRead, "", status, buffer, read);
            return status;
        }

        public PanelDeviceStatus DiscardInput()
        {
            var status = inner.DiscardInput();
            recorder.Record(handle, PanelTraceOp.SerialDiscard, "", [], status);
            return status;
        }

        public void Dispose()
        {
            inner.Dispose();
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                recorder.Record(handle, PanelTraceOp.Close, "serial", [], PanelDeviceStatus.Ok);
        }
    }
}
