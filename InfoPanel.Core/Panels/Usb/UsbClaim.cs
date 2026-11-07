using System.Linq;

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
    /// <see cref="UsbStatus.Busy"/> when another program holds it, <see cref="UsbStatus.Access"/> when the OS refuses,
    /// <see cref="UsbStatus.NoDevice"/> when it is gone or its bus and address now hold another device.
    /// </summary>
    public static UsbStatus Open(IUsbBackend backend, UsbDeviceId id, int configuration, int interfaceNumber, out IUsbDevice? claimed)
    {
        claimed = null;
        // The device at the listed bus and address must still be the listed one (the same ids and port): after an
        // unplug another device can take the address, and it must never get this device's commands.
        if (!backend.ListDevices().Contains(id))
            return UsbStatus.NoDevice;
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
