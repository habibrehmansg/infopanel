using InfoPanel.Panels.Usb;

namespace InfoPanel.Panels;

/// <summary>
/// The USB devices of a spare engine run with the virtual panel package (the <c>Engine:VirtualPanels</c> setting, a count;
/// development and tests, never a flag): that many virtual devices <c>1209:0001</c>, at <c>virtual-1</c>,
/// <c>virtual-2</c> …, in place of the computer's own, so no real device is listed, opened or written. Only a panel host's
/// driver takes them (it opens nothing); the engine's own open of one is refused.
/// </summary>
public sealed class VirtualUsbBackend(int count) : IUsbBackend
{
    public const ushort VendorId = 0x1209;
    public const ushort ProductId = 0x0001;

    /// <summary>The most virtual devices a spare engine lists.</summary>
    public const int MaxDevices = 8;

    private readonly IReadOnlyList<UsbDeviceId> _devices =
        [.. Enumerable.Range(1, Math.Clamp(count, 0, MaxDevices)).Select(i => new UsbDeviceId(VendorId, ProductId, 0, (byte)i, $"virtual-{i}"))];

    public IReadOnlyList<UsbDeviceId> ListDevices() => _devices;

    public UsbStatus Open(UsbDeviceId device, out IUsbDevice? opened)
    {
        opened = null;
        return UsbStatus.NotSupported;
    }

    public void Dispose()
    {
    }
}
