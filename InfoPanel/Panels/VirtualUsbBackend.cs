using InfoPanel.Panels.Usb;

namespace InfoPanel.Panels;

/// <summary>
/// The USB devices of a spare engine run with the virtual panel package (the <c>Engine:VirtualPanels</c> setting, a count;
/// development and tests, never a flag): that many virtual devices <c>1209:0001</c>, at <c>virtual-1</c>,
/// <c>virtual-2</c> …, in place of the computer's own, so no real device is listed, opened or written. Only a panel host's
/// driver takes them (it opens nothing); the engine's own open of one is refused. With <paramref name="detected"/> (the
/// <c>Engine:VirtualDetected</c> setting) it lists <c>1209:0002</c> too, at <c>virtual-detected</c>, which no family drives
/// and the virtual package detects (<c>1209:*</c>): a device listed as detected, with its cached descriptors (a vendor
/// interface with a bulk endpoint each way).
/// </summary>
public sealed class VirtualUsbBackend(int count, bool detected = false) : IUsbBackend
{
    public const ushort VendorId = 0x1209;
    public const ushort ProductId = 0x0001;

    /// <summary>The device the virtual package detects and no family drives.</summary>
    public const ushort DetectedProductId = 0x0002;

    /// <summary>The most virtual devices a spare engine lists.</summary>
    public const int MaxDevices = 8;

    private static readonly UsbDeviceId Detected = new(VendorId, DetectedProductId, 0, MaxDevices + 1, "virtual-detected");

    private readonly IReadOnlyList<UsbDeviceId> _devices =
    [
        .. Enumerable.Range(1, Math.Clamp(count, 0, MaxDevices)).Select(i => new UsbDeviceId(VendorId, ProductId, 0, (byte)i, $"virtual-{i}")),
        .. detected ? [Detected] : Array.Empty<UsbDeviceId>(),
    ];

    public IReadOnlyList<UsbDeviceId> ListDevices() => _devices;

    public UsbStatus Open(UsbDeviceId device, out IUsbDevice? opened)
    {
        opened = null;
        return UsbStatus.NotSupported;
    }

    /// <summary>The detected device's descriptors, as an OS caches them; the virtual panels' are nobody's business (not supported).</summary>
    public UsbStatus Describe(UsbDeviceId device, out UsbDescriptor? descriptor)
    {
        descriptor = null;
        if (!_devices.Contains(device))
            return UsbStatus.NoDevice;
        if (device != Detected)
            return UsbStatus.NotSupported;
        descriptor = new UsbDescriptor(0, 0, 0, 0x0100, [new UsbInterfaceDescriptor(0, 0xff, 0, 0, [(0x01, 2, 512), (0x81, 2, 512)])]);
        return UsbStatus.Ok;
    }

    public void Dispose()
    {
    }
}
