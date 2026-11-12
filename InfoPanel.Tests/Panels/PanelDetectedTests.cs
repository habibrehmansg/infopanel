using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Usb;
using InfoPanel.Spec;
using InfoPanel.Tests.Preview;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Tests.Panels;

/// <summary>
/// "Detected, not supported" (spec/docs/api.md "Detected devices"): the manager lists a USB device a family's
/// <c>detect</c> names and no driver's matches do, from its cached descriptors only (never opened, claimed or written),
/// leaves out hubs, boot keyboards and mice and the left-out vendors, follows it being unplugged, and the API answers the
/// list as <c>deviceList.detected</c>; the virtual backend lists <c>1209:0002</c> when asked.
/// </summary>
public sealed class PanelDetectedTests : IAsyncDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("panels-detected-").FullName;
    private readonly FakeUsbBackend _backend = new();
    private readonly List<LogEvent> _events;
    private readonly Serilog.ILogger _logger;
    private PanelManager? _manager;

    public PanelDetectedTests() => (_logger, _events) = PanelStoreTests.Logged();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_manager != null)
            await _manager.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private PanelManager Manager(IUsbBackend? backend = null, IReadOnlyList<IPanelDriver>? drivers = null)
    {
        var store = PanelStore.Read(Path.Combine(_folder, PanelStore.FileName), _logger);
        var host = new PanelHost(_ => true, _ => true, _ => { }, _ => { }, _ => { }, () => 15);
        _manager = new PanelManager(backend ?? _backend, store, host, _backend.Clock, _logger,
            new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan }, drivers: drivers);
        _manager.Start(passes: false);
        return _manager;
    }

    private static UsbInterfaceDescriptor Interface(int number, byte @class, byte subClass = 0, byte protocol = 0) =>
        new(number, @class, subClass, protocol, [(0x01, 2, 512), (0x81, 2, 512)]);

    /// <summary>A vendor-specific device (one interface, class 0xff, a bulk endpoint each way), release 2.10.</summary>
    private static UsbDescriptor Vendor(params UsbInterfaceDescriptor[] interfaces) =>
        new(0, 0, 0, 0x0210, interfaces.Length > 0 ? interfaces : [Interface(0, 0xff)]);

    private FakeUsbDevice Plug(ushort vendor, ushort product, byte address, string port, UsbDescriptor? descriptor) =>
        _backend.Add(new FakeUsbDevice(new UsbDeviceId(vendor, product, 0, address, port)) { Descriptor = descriptor });

    /// <summary>Nothing was asked of the device: no open, configuration, claim, serial read or transfer.</summary>
    private static void Untouched(FakeUsbDevice device)
    {
        Assert.Empty(device.Calls);
        Assert.Empty(device.Transfers);
        Assert.Equal(0, device.OpenHandles);
    }

    [Fact]
    public async Task A_device_the_built_in_familys_pattern_names_is_listed_detected_and_never_opened_until_it_is_unplugged()
    {
        var device = Plug(BeadaPanelProtocol.VendorId, 0x1002, 7, "0-3", Vendor());
        var manager = Manager();
        var before = manager.Version;

        await manager.RunPassAsync();
        await manager.RunPassAsync();

        var detected = Assert.Single(await manager.ListDetectedAsync());
        Assert.Equal(("beadapanel", device.Id, (ushort)0x0210), (detected.Family, detected.Usb, detected.BcdDevice));
        var only = Assert.Single(detected.Interfaces);
        Assert.Equal((0xff, 2), (only.Class, only.Endpoints.Count));
        Assert.Empty(await manager.ListAsync()); // nothing saved
        Untouched(device);
        Assert.True(manager.Version > before);
        Assert.Single(_events, e => e.RenderMessage().Contains("4e58:1002") && e.RenderMessage().Contains("detected"));

        var listed = manager.Version;
        _backend.Unplug(device);
        await manager.RunPassAsync();

        Assert.Empty(await manager.ListDetectedAsync());
        Assert.True(manager.Version > listed);
        Untouched(device);
    }

    [Fact]
    public async Task A_pass_with_nothing_changed_keeps_the_version_and_describes_nothing_again()
    {
        var device = Plug(BeadaPanelProtocol.VendorId, 0x1002, 7, "0-3", Vendor());
        var manager = Manager();

        await manager.RunPassAsync();
        var listed = await manager.ListDetectedAsync();
        var (version, describes) = (manager.Version, _backend.Describes);
        Assert.Equal(1, describes);

        await manager.RunPassAsync();
        await manager.RunPassAsync();

        Assert.Equal((version, describes), (manager.Version, _backend.Describes));
        Assert.Equal(listed, await manager.ListDetectedAsync());
        Untouched(device);
    }

    [Fact]
    public async Task A_device_a_driver_matches_is_never_listed_detected()
    {
        var panel = _backend.Add(new FakeUsbDevice(new UsbDeviceId(BeadaPanelProtocol.VendorId, BeadaPanelProtocol.ProductId, 0, 4, "0-1"))
        {
            Reply = PanelReplies.Info(2, 5, 21, 1920, 480, "934072005c"),
            UsbSerial = "934072005c",
            Descriptor = Vendor(),
        });
        var manager = Manager();

        await manager.RunPassAsync();

        Assert.Single(await manager.ListAsync()); // the BeadaPanel, identified and saved
        Assert.Empty(await manager.ListDetectedAsync());
        Assert.NotEmpty(panel.Calls);
    }

    [Fact]
    public async Task A_hub_a_boot_keyboard_or_mouse_or_a_device_without_descriptors_on_a_detected_vendor_is_left_out()
    {
        UsbDescriptor hub = new(0x09, 0, 1, 0x0100, [Interface(0, 0x09)]);
        FakeUsbDevice[] devices =
        [
            Plug(BeadaPanelProtocol.VendorId, 0x2001, 5, "0-2", hub),
            Plug(BeadaPanelProtocol.VendorId, 0x2002, 6, "0-3", Vendor(Interface(0, 0x03, 1, 1))),
            Plug(BeadaPanelProtocol.VendorId, 0x2003, 7, "0-4", Vendor(Interface(0, 0xff), Interface(1, 0x03, 1, 2))),
            Plug(BeadaPanelProtocol.VendorId, 0x2004, 8, "0-5", null),
            // Vendor-specific in its first alternate setting, a boot keyboard (03/01/01) in its second.
            Plug(BeadaPanelProtocol.VendorId, 0x2005, 9, "0-6", Vendor(Interface(0, 0xff) with { LaterSettings = [(0x03, 1, 1)] })),
        ];
        var manager = Manager();

        await manager.RunPassAsync();

        Assert.Empty(await manager.ListDetectedAsync());
        Assert.All(devices, Untouched);
    }

    [Fact]
    public async Task A_device_no_pattern_names_is_not_detected()
    {
        var other = Plug(0x046d, 0xc52b, 5, "0-2", Vendor());
        var manager = Manager();

        await manager.RunPassAsync();

        Assert.Empty(await manager.ListDetectedAsync());
        Untouched(other);
    }

    /// <summary>A family that drives <c>1209:0001</c> and detects <paramref name="detect"/>; it is never asked anything.</summary>
    private sealed class Declaring(params string[] detect) : IPanelDriver, IDetectsPanels
    {
        public string Family => "virtual";

        public IReadOnlyList<PanelMatch> Matches { get; } = [new(0x1209, 0x0001)];

        public IReadOnlyList<UsbIdPattern> Detect => [.. detect.Select(p => UsbIdPattern.Parse(p)!)];

        public PanelProbe Probe(PanelCandidate device, CancellationToken stop) => new(false, null, PanelDeviceStatus.Ok, "not asked");

        public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop) => throw new InvalidOperationException("never asked");
    }

    [Fact]
    public async Task The_left_out_vendors_are_never_detected_whatever_a_family_declares()
    {
        FakeUsbDevice[] devices =
        [
            Plug(0x1a86, 0x7523, 5, "0-2", Vendor()),
            Plug(0x1d6b, 0x0002, 6, "0-3", Vendor()),
            Plug(0x0525, 0xa4a7, 7, "0-4", Vendor()),
        ];
        var manager = Manager(drivers: [new Declaring("1a86:*", "1d6b:*", "0525:*")]);

        await manager.RunPassAsync();

        Assert.Empty(await manager.ListDetectedAsync());
        Assert.All(devices, Untouched);
    }

    [Fact]
    public async Task The_virtual_backend_lists_1209_0002_when_asked_and_a_family_detecting_1209_lists_it()
    {
        using var plain = new VirtualUsbBackend(1);
        Assert.DoesNotContain(plain.ListDevices(), d => d.ProductId == 0x0002);

        using var backend = new VirtualUsbBackend(1, detected: true);
        var listed = backend.ListDevices();
        Assert.Equal(["1209:0001 virtual-1", "1209:0002 virtual-detected"], listed.Select(d => $"{d.VendorId:x4}:{d.ProductId:x4} {d.PortPath}"));
        var spare = listed.Single(d => d.ProductId == 0x0002);
        Assert.Equal(UsbStatus.NotSupported, backend.Open(spare, out _));

        var manager = Manager(backend, [new Declaring("1209:*")]);
        await manager.RunPassAsync();

        var detected = Assert.Single(await manager.ListDetectedAsync());
        Assert.Equal(("virtual", spare), (detected.Family, detected.Usb));
        Assert.Equal((0xff, 2), (Assert.Single(detected.Interfaces).Class, detected.Interfaces[0].Endpoints.Count));
    }

    private static WebApplicationFactory<Program> WithPanels(EngineFactory engine, FakeUsbBackend backend) =>
        engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:NoPanels", "false");
            builder.ConfigureTestServices(services => services.AddSingleton(
                new PanelServiceOptions { Backend = () => backend, Runner = new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan } }));
        });

    [Fact]
    public async Task The_API_answers_the_detected_devices_with_their_interfaces_and_none_where_panels_are_off()
    {
        Plug(BeadaPanelProtocol.VendorId, 0x1002, 7, "0-3", Vendor(Interface(0, 0xff), new UsbInterfaceDescriptor(1, 0x03, 0, 0, [(0x83, 3, 64)])));
        await using var engine = new EngineFactory();
        await using var factory = WithPanels(engine, _backend);
        using var api = factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);

        JsonElement list;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            list = await api.GetFromJsonAsync<JsonElement>("/api/devices", Ct);
            if (list.TryGetProperty("detected", out var found) && found.GetArrayLength() > 0)
                break;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"never detected: {list}");
            await Task.Delay(20, Ct);
        }

        Assert.True(SpecSchemas.Strict.Validate(list, SpecSchemas.Api + "#/$defs/deviceList").IsValid, list.ToString());
        var detected = Assert.Single(list.GetProperty("detected").EnumerateArray());
        Assert.Equal("""
            {"family":"beadapanel","usb":"4e58:1002","port":"0-3","bcdDevice":528,"interfaces":[{"class":255,"subclass":0,"protocol":0,"endpoints":[{"address":1,"type":"bulk","maxPacketSize":512},{"address":129,"type":"bulk","maxPacketSize":512}]},{"class":3,"subclass":0,"protocol":0,"endpoints":[{"address":131,"type":"interrupt","maxPacketSize":64}]}]}
            """, detected.GetRawText());

        await using var off = new EngineFactory();
        using var offApi = off.CreateApiClient();
        Assert.False((await offApi.GetFromJsonAsync<JsonElement>("/api/devices", Ct)).TryGetProperty("detected", out _));
    }
}
