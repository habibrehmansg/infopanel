using System.Diagnostics;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Testing;

/// <summary>
/// No call on the device seam waits without end (<see cref="IPanelDevices"/>): every call that can wait takes a timeout
/// of 1 to <see cref="IPanelDevices.MaxTimeoutMs"/> and a cancellation token, and the fakes honour both. A stalled call
/// (<see cref="FakePanelDevice.Stall"/>) ends at its timeout, at its token, or, on a handle, when the handle is disposed.
/// Each kind of call is tried.
/// </summary>
public sealed class PanelDeviceWaitsTests
{
    private static readonly PanelCandidate Candidate = new(0x1234, 0x5678, 1, 7, "1-2");

    public enum Kind { OpenUsb, OpenHid, OpenSerial, BulkWrite, BulkRead, HidWrite, HidRead, SetFeature, GetFeature, SerialWrite, SerialRead }

    public static TheoryData<Kind> Every => [.. Enum.GetValues<Kind>()];

    public static TheoryData<Kind> OnAHandle => [.. Enum.GetValues<Kind>().Where(k => k is not (Kind.OpenUsb or Kind.OpenHid or Kind.OpenSerial))];

    // A device with every transport, and the handle a kind of call is made on (opened as call 1), or none for an open.
    private sealed class Setup : IDisposable
    {
        public Setup(Kind kind)
        {
            Kind = kind;
            Devices = new FakePanelDevices();
            Device = Devices.Add(Candidate, PanelTransport.Hid, PanelTransport.Serial);
            Device.Hid.ReplyFeature([0x05, 0x01]);
            Device.Hid.Reply([0x00, 0x01]);
            Device.Usb.Reply([0x01]);
            Device.Serial.Arrive([0x01]);
            switch (kind)
            {
                case Kind.BulkWrite or Kind.BulkRead:
                    Assert.Equal(PanelDeviceStatus.Ok, Devices.OpenUsb(Candidate, 0, 1000, out var usb));
                    Handle = usb;
                    break;
                case Kind.HidWrite or Kind.HidRead or Kind.SetFeature or Kind.GetFeature:
                    Assert.Equal(PanelDeviceStatus.Ok, Devices.OpenHid(Candidate, new PanelHidFilter(), 1000, out var hid));
                    Handle = hid;
                    break;
                case Kind.SerialWrite or Kind.SerialRead:
                    Assert.Equal(PanelDeviceStatus.Ok, Devices.OpenSerial(Candidate, new PanelSerialOptions(9600), 1000, out var serial));
                    Handle = serial;
                    break;
            }
        }

        public Kind Kind { get; }

        public FakePanelDevices Devices { get; }

        public FakePanelDevice Device { get; }

        public IDisposable? Handle { get; }

        /// <summary>The number the call under test gets.</summary>
        public int Call => Handle == null ? 1 : 2;

        public PanelDeviceStatus Make(int timeoutMs, CancellationToken token = default)
        {
            var buffer = new byte[64];
            buffer[0] = 0x05;
            switch (Kind)
            {
                case Kind.OpenUsb:
                    {
                        var status = Devices.OpenUsb(Candidate, 0, timeoutMs, out var usb, token);
                        usb?.Dispose();
                        return status;
                    }
                case Kind.OpenHid:
                    {
                        var status = Devices.OpenHid(Candidate, new PanelHidFilter(), timeoutMs, out var hid, token);
                        hid?.Dispose();
                        return status;
                    }
                case Kind.OpenSerial:
                    {
                        var status = Devices.OpenSerial(Candidate, new PanelSerialOptions(9600), timeoutMs, out var serial, token);
                        serial?.Dispose();
                        return status;
                    }
                case Kind.BulkWrite:
                    return ((IPanelUsb)Handle!).BulkWrite(0x01, [0x01], timeoutMs, cancellationToken: token);
                case Kind.BulkRead:
                    return ((IPanelUsb)Handle!).BulkRead(0x81, buffer, timeoutMs, out _, token);
                case Kind.HidWrite:
                    return ((IPanelHid)Handle!).Write([0x00, 0x01], timeoutMs, token);
                case Kind.HidRead:
                    return ((IPanelHid)Handle!).Read(buffer, timeoutMs, out _, token);
                case Kind.SetFeature:
                    return ((IPanelHid)Handle!).SetFeature([0x05, 0x01], timeoutMs, token);
                case Kind.GetFeature:
                    return ((IPanelHid)Handle!).GetFeature(buffer, timeoutMs, out _, token);
                case Kind.SerialWrite:
                    return ((IPanelSerial)Handle!).Write([0x01], timeoutMs, token);
                default:
                    return ((IPanelSerial)Handle!).Read(buffer, timeoutMs, out _, token);
            }
        }

        public void Dispose() => Handle?.Dispose();
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void A_timeout_outside_1_to_the_most_is_refused_before_anything_reaches_the_device(Kind kind)
    {
        using var setup = new Setup(kind);
        var before = setup.Device.Trace.Format();

        foreach (var timeout in new[] { 0, -1, IPanelDevices.MaxTimeoutMs + 1 })
            Assert.Throws<ArgumentOutOfRangeException>(() => setup.Make(timeout));

        Assert.Equal(setup.Call - 1, setup.Device.Calls);
        Assert.Equal(before, setup.Device.Trace.Format());
        Assert.Equal(PanelDeviceStatus.Ok, setup.Make(IPanelDevices.MaxTimeoutMs)); // the most is taken
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void A_call_whose_token_is_cancelled_throws_and_reaches_nothing(Kind kind)
    {
        using var setup = new Setup(kind);
        var before = setup.Device.Trace.Format();

        Assert.ThrowsAny<OperationCanceledException>(() => setup.Make(1000, new CancellationToken(canceled: true)));

        Assert.Equal(setup.Call - 1, setup.Device.Calls);
        Assert.Equal(before, setup.Device.Trace.Format());
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void A_stalled_call_answers_timeout_once_its_time_has_passed(Kind kind)
    {
        using var setup = new Setup(kind);
        setup.Device.Stall(setup.Call);
        var clock = Stopwatch.StartNew();

        Assert.Equal(PanelDeviceStatus.Timeout, setup.Make(50));

        Assert.True(clock.ElapsedMilliseconds >= 40, $"{clock.ElapsedMilliseconds} ms");
        Assert.EndsWith(" !timeout\n", setup.Device.Trace.Format());
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void A_stalled_call_ends_when_its_token_is_cancelled(Kind kind)
    {
        using var setup = new Setup(kind);
        var stalled = setup.Device.Stall(setup.Call);
        using var stop = new CancellationTokenSource();
        var pending = Task.Run(() => setup.Make(IPanelDevices.MaxTimeoutMs, stop.Token));
        Assert.True(stalled.Wait(TimeSpan.FromSeconds(10)), "the call never stalled");

        stop.Cancel();

        Assert.True(Task.WaitAny([pending], TimeSpan.FromSeconds(10)) == 0, "the call didn't end at its token");
        Assert.True(pending.IsCanceled || pending.Exception?.InnerException is OperationCanceledException, $"{pending.Status}");
    }

    [Theory]
    [MemberData(nameof(OnAHandle))]
    public void Disposing_a_handle_ends_its_stalled_call_with_io_and_returns_once_it_has_ended(Kind kind)
    {
        using var setup = new Setup(kind);
        var stalled = setup.Device.Stall(setup.Call);
        var pending = Task.Run(() => setup.Make(IPanelDevices.MaxTimeoutMs));
        Assert.True(stalled.Wait(TimeSpan.FromSeconds(10)), "the call never stalled");

        var disposing = Task.Run(setup.Handle!.Dispose);

        Assert.True(Task.WaitAll([pending, disposing], TimeSpan.FromSeconds(10)), "the call or the dispose didn't end");
        Assert.Equal(PanelDeviceStatus.Io, pending.Result);
        var lines = setup.Device.Trace.Format().TrimEnd('\n').Split('\n');
        Assert.EndsWith(" !io", lines[^2]); // the call ended before the handle closed
        Assert.Contains(" close ", lines[^1]);
        Assert.ThrowsAny<ObjectDisposedException>(() => setup.Make(100));
    }
}
