using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Usb;

namespace InfoPanel.Core.Tests.Panels.Testing;

/// <summary>
/// The engine's USB backend (<see cref="IUsbBackend"/>, which the built-in BeadaPanel driver talks through) over the
/// SDK's device seam, so the BeadaPanel driver runs unchanged over the kit's fakes and the conformance suite can check it
/// as it checks a package's driver. It lists the candidates it is given while the seam still describes them; a device is
/// configured (configuration 1) and its claim is the seam's <see cref="IPanelDevices.OpenUsb"/>; releasing the interface
/// or closing the device disposes the claimed handle.
/// </summary>
internal sealed class SeamUsbBackend(IPanelDevices devices, IReadOnlyList<PanelCandidate> candidates) : IUsbBackend
{
    private const int ClaimTimeoutMs = 2000;

    public IReadOnlyList<UsbDeviceId> ListDevices() =>
        [.. candidates.Where(c => devices.Describe(c, out _) == PanelDeviceStatus.Ok).Select(IdOf)];

    public UsbStatus Open(UsbDeviceId device, out IUsbDevice? opened)
    {
        opened = null;
        var candidate = candidates.FirstOrDefault(c => IdOf(c) == device);
        if (candidate == null || devices.Describe(candidate, out _) != PanelDeviceStatus.Ok)
            return UsbStatus.NoDevice;
        opened = new Device(devices, candidate);
        return UsbStatus.Ok;
    }

    public void Dispose()
    {
    }

    private static UsbDeviceId IdOf(PanelCandidate c) => new(c.VendorId, c.ProductId, c.Bus, c.Address, c.PortPath);

    internal static UsbStatus StatusOf(PanelDeviceStatus status) => status switch
    {
        PanelDeviceStatus.Ok => UsbStatus.Ok,
        PanelDeviceStatus.Busy => UsbStatus.Busy,
        PanelDeviceStatus.Access => UsbStatus.Access,
        PanelDeviceStatus.Gone => UsbStatus.NoDevice,
        PanelDeviceStatus.NotSupported => UsbStatus.NotSupported,
        PanelDeviceStatus.Timeout => UsbStatus.Timeout,
        _ => UsbStatus.Io,
    };

    private sealed class Device(IPanelDevices devices, PanelCandidate candidate) : IUsbDevice
    {
        private IPanelUsb? _usb;

        public UsbStatus GetConfiguration(out int configuration)
        {
            configuration = 1;
            return UsbStatus.Ok;
        }

        public UsbStatus SetConfiguration(int configuration) => UsbStatus.Ok;

        public UsbStatus ClaimInterface(int interfaceNumber)
        {
            if (_usb != null)
                return UsbStatus.Busy;
            var status = devices.OpenUsb(candidate, interfaceNumber, ClaimTimeoutMs, out _usb);
            return StatusOf(status);
        }

        public UsbStatus ReleaseInterface(int interfaceNumber)
        {
            _usb?.Dispose();
            _usb = null;
            return UsbStatus.Ok;
        }

        public string? ReadSerialNumber() => _usb?.SerialNumber;

        public UsbStatus BulkWrite(byte endpoint, ReadOnlySpan<byte> data, int timeoutMs) =>
            _usb == null ? UsbStatus.Io : StatusOf(_usb.BulkWrite(endpoint, data, timeoutMs));

        public UsbStatus BulkRead(byte endpoint, Span<byte> buffer, int timeoutMs, out int read)
        {
            read = 0;
            return _usb == null ? UsbStatus.Io : StatusOf(_usb.BulkRead(endpoint, buffer, timeoutMs, out read));
        }

        public void Dispose() => ReleaseInterface(0);
    }
}

/// <summary>
/// A clock whose waits end at once (a <c>Task.Delay</c> through it fires immediately) while its timestamps are the real
/// ones: a BeadaPanel start skips its 1.3 s of settling, and the time a transfer takes, or a screen-off's budget, is still
/// measured as it passes.
/// </summary>
internal sealed class InstantWaits : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        TimeProvider.System.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
}
