using Xunit;
using InfoPanel.Panels.Usb;

namespace InfoPanel.Core.Tests.Panels.Usb;

/// <summary>Opening a device safely beside another holder (<see cref="UsbClaim"/>), on the fake backend.</summary>
public class UsbClaimTests
{
    private static readonly UsbDeviceId Id = new(0x4E58, 0x1001, 2, 7, "2-1.3");

    [Fact]
    public void A_configured_device_is_claimed_without_setting_its_configuration()
    {
        var backend = new FakeUsbBackend();
        var device = backend.Add(new FakeUsbDevice(Id) { Configuration = 1 });

        Assert.Equal(UsbStatus.Ok, UsbClaim.Open(backend, Id, 1, 0, out var opened));
        opened!.Dispose();

        Assert.Equal(["open", "getConfiguration", "claim 0", "close"], device.Calls);
    }

    [Fact]
    public void An_unconfigured_device_gets_the_configuration()
    {
        var backend = new FakeUsbBackend();
        var device = backend.Add(new FakeUsbDevice(Id) { Configuration = 0 });

        Assert.Equal(UsbStatus.Ok, UsbClaim.Open(backend, Id, 1, 0, out var opened));
        opened!.Dispose();

        Assert.Equal(["open", "getConfiguration", "setConfiguration 1", "claim 0", "close"], device.Calls);
    }

    [Fact]
    public void A_device_in_another_configuration_is_left_in_it()
    {
        var backend = new FakeUsbBackend();
        var device = backend.Add(new FakeUsbDevice(Id) { Configuration = 2 });
        Assert.Equal(UsbStatus.Ok, UsbClaim.Open(backend, Id, 1, 0, out var opened));
        opened!.Dispose();
        Assert.DoesNotContain(device.Calls, c => c.StartsWith("setConfiguration", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(UsbStatus.Busy)]
    [InlineData(UsbStatus.Access)]
    public void A_failed_claim_closes_the_device_and_says_why(UsbStatus claim)
    {
        var backend = new FakeUsbBackend();
        var device = backend.Add(new FakeUsbDevice(Id) { ClaimResult = claim });

        Assert.Equal(claim, UsbClaim.Open(backend, Id, 1, 0, out var opened));

        Assert.Null(opened);
        Assert.Equal(["open", "getConfiguration", "claim 0", "close"], device.Calls);
        Assert.Equal(0, device.OpenHandles);
        Assert.Empty(device.Transfers);
    }

    [Fact]
    public void A_gone_device_is_no_device()
    {
        var backend = new FakeUsbBackend();
        Assert.Equal(UsbStatus.NoDevice, UsbClaim.Open(backend, Id, 1, 0, out var opened));
        Assert.Null(opened);
    }

    [Fact]
    public void The_seam_has_no_reset_and_no_driver_detach()
    {
        // Nothing above the seam can reset a device or detach a kernel driver: neither is a member of it, and the
        // libusb binding doesn't bind either function.
        string[] members = [.. typeof(IUsbDevice).GetMethods().Concat(typeof(IUsbBackend).GetMethods()).Select(m => m.Name.ToLowerInvariant())];
        Assert.DoesNotContain(members, m => m.Contains("reset") || m.Contains("detach") || m.Contains("kernel"));
        string[] bound = [.. typeof(LibUsb).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Select(f => f.Name)];
        Assert.Contains("libusb_claim_interface", bound);
        Assert.DoesNotContain(bound, f => f.Contains("reset") || f.Contains("detach") || f.Contains("kernel_driver"));
    }

    [Fact]
    public void A_bus_address_now_holding_another_device_isnt_opened()
    {
        // The panel was listed at bus 2, address 7; it went, and another device came at the same bus and address.
        var backend = new FakeUsbBackend();
        var panel = backend.Add(new FakeUsbDevice(Id));
        backend.Unplug(panel);
        var other = backend.Add(new FakeUsbDevice(Id with { VendorId = 0x1234, ProductId = 0x5678 }));

        Assert.Equal(UsbStatus.NoDevice, UsbClaim.Open(backend, Id, 1, 0, out var opened));
        Assert.Null(opened);
        Assert.Empty(other.Calls);

        // The same at another port path.
        var moved = new FakeUsbBackend();
        var elsewhere = moved.Add(new FakeUsbDevice(Id with { PortPath = "2-4" }));
        Assert.Equal(UsbStatus.NoDevice, UsbClaim.Open(moved, Id, 1, 0, out _));
        Assert.Empty(elsewhere.Calls);
    }
}
