using InfoPanel.Panels;
using InfoPanel.Panels.Sdk;
using Xunit;

namespace InfoPanel.Tests.Panels;

/// <summary>
/// The panel host makes a package's driver through its public constructor that takes the device seam, given the host's
/// own (<see cref="IPanelDevices"/>), and through the one that takes nothing only when it has no such constructor or no
/// seam is given.
/// </summary>
public sealed class PanelHostDevicesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("panel-host-devices-").FullName;
    private readonly Serilog.ILogger _logger = Serilog.Core.Logger.None;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static object? DevicesOf(IPanelDriver driver) => driver.GetType().GetProperty("Devices")!.GetValue(driver);

    [Fact]
    public void A_driver_whose_constructor_takes_the_device_seam_gets_the_hosts()
    {
        var package = VirtualPackage.CopyTo(_folder);
        var devices = new InfoPanel.Panels.Devices.EnginePanelDevices(null, null, null, _logger);

        var driver = Assert.Single(PanelHostEntry.Load(package, VirtualPackage.Folder, _logger, devices));

        Assert.Same(devices, DevicesOf(driver));
    }

    [Fact]
    public void Without_a_seam_the_constructor_that_takes_nothing_makes_it()
    {
        var package = VirtualPackage.CopyTo(_folder);

        var driver = Assert.Single(PanelHostEntry.Load(package, VirtualPackage.Folder, _logger));

        Assert.Null(DevicesOf(driver));
    }
}
