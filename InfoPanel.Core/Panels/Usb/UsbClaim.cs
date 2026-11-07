namespace InfoPanel.Panels.Usb;

/// <summary>
/// Opens a device and claims one interface, safely beside another holder: the configuration is set only when the
/// device has none (setting it again would reset the device's state under another program), the device is never reset
/// and no kernel driver is ever detached. A claim that fails closes the device again, so nothing is written to it.
/// </summary>
public static class UsbClaim
{
    /// <summary>
    /// The device opened with <paramref name="interfaceNumber"/> claimed, or the status that stopped it:
    /// <see cref="UsbStatus.Busy"/> when another program holds it, <see cref="UsbStatus.Access"/> when the OS refuses.
    /// </summary>
    public static UsbStatus Open(IUsbBackend backend, UsbDeviceId id, int configuration, int interfaceNumber, out IUsbDevice? claimed)
    {
        claimed = null;
        var status = backend.Open(id, out var device);
        if (status != UsbStatus.Ok || device is null)
            return status == UsbStatus.Ok ? UsbStatus.Io : status;

        // Unknown (the call failed) counts as set: only a device known to be unconfigured is configured.
        if (device.GetConfiguration(out var active) == UsbStatus.Ok && active == 0)
        {
            status = device.SetConfiguration(configuration);
            if (status != UsbStatus.Ok)
            {
                device.Dispose();
                return status;
            }
        }

        status = device.ClaimInterface(interfaceNumber);
        if (status != UsbStatus.Ok)
        {
            device.Dispose();
            return status;
        }
        claimed = device;
        return UsbStatus.Ok;
    }
}
