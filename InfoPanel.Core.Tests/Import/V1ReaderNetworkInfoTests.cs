using System.Net;
using System.Net.NetworkInformation;
using InfoPanel.Editing;
using InfoPanel.Import;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Clock;
using InfoPanel.Sensors.Network;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Import.ImportKit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// A 1.x profile bound to the 1.x Network Info plugin, whose sensors the engine's network source reads now: reading it,
/// from a <c>.infopanel</c> file or the 1.x folder, binds every item bound to the public address or to one of an
/// interface's entries to the source's key, in the model, so its props and <c>meta.sensor</c> carry the new binding,
/// and reports each (<c>import.sensor.translated</c>). An entry the source doesn't have stays as stored, unresolved
/// (<c>import.sensor</c>). Importing reads no value, so it puts no public address in use.
/// </summary>
public sealed class V1ReaderNetworkInfoTests : IDisposable
{
    private const string Guid = "{2E8F4A1B-7C3D-4E5F-9A0B-1C2D3E4F5A6B}";
    private const string GuidId = "2e8f4a1b7c3d4e5f9a0b1c2d3e4f5a6b";
    private static readonly string[] Fixed = ["id", "name", "description", "status", "speed"];

    private sealed class Interface(string id, string name, params string[] addresses) : INetworkInterface
    {
        public string Id => id;
        public string Name => name;
        public string Description => "Adapter";
        public NetworkInterfaceType InterfaceType => NetworkInterfaceType.Ethernet;
        public OperationalStatus Status => OperationalStatus.Up;
        public long Speed => 1_000_000_000;
        public IReadOnlyList<IPAddress> Addresses => addresses.Select(IPAddress.Parse).ToArray();
    }

    private sealed class Lister(params INetworkInterface[] interfaces) : INetworkLister
    {
        public IReadOnlyList<INetworkInterface> List() => interfaces;
    }

    private sealed class Ipify : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var v6 = request.RequestUri!.Host.StartsWith("api6.", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(v6 ? "2001:db8::7" : "203.0.113.7") });
        }
    }

    private readonly ImportKit _kit = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 13, 5, 7, TimeSpan.Zero));
    private readonly Ipify _ipify = new();
    private readonly NetworkSource _network;

    public V1ReaderNetworkInfoTests()
    {
        // This PC has en0 (two addresses of each family) and a Windows-style adapter; its public address was looked up.
        _network = new NetworkSource(
            new Lister(new Interface("en0", "en0", "192.168.1.20", "192.168.1.21", "fe80::1%4", "2001:db8::20"), new Interface(Guid, "Ethernet", "10.0.0.5")),
            new PublicIpLookup(_ipify, _time), _time);
        _network.Lookup!.LookUpAsync(CancellationToken.None).GetAwaiter().GetResult();
        _network.Polled.Open();
    }

    public void Dispose()
    {
        _network.Dispose();
        _kit.Dispose();
    }

    /// <summary>This engine's sensors: the clock and the network, unknown keys unavailable (not mock mode).</summary>
    private SensorHub Hub() => new([new ClockSensorSource(_time), _network]);

    private V1ReadContext Context() => new(_kit.Workspace, Hub(), null, Fonts.Contains);

    private V1Profile Read(params string[] items) => V1Reader.ReadArchive(new MemoryStream(V1Archive(Items(items))), Context());

    private static string Bound(string type, string name, string key, string sensorType = "Plugin") =>
        $"""<DisplayItem xsi:type="{type}"><Name>{name}</Name><SensorName>{name}</SensorName><SensorType>{sensorType}</SensorType><PluginSensorId>{key}</PluginSensorId><Id>0</Id><Instance>0</Instance><EntryId>0</EntryId></DisplayItem>""";

    private static string Net(string container, string id) => $"/network-info-plugin/{container}/{id}";

    private static string PropsKey(ItemNode node) => (string)node.Props["pluginSensorId"]!;

    [Fact]
    public void Translated_ThePublicAddresses_AndEveryEntryOfAnInterface_InPropsAndMeta_Live_AndListedOnce()
    {
        string[] entries = [.. Fixed, "ipv4-0", "ipv4-1", "ipv6-0", "ipv6-1"];
        var envelope = Read(
        [
            Bound("SensorDisplayItem", "Public v4", Net("public-ip", "ipv4")),
            Bound("SensorDisplayItem", "Public v6", Net("public-ip", "ipv6")),
            .. entries.Select(id => Bound("SensorDisplayItem", $"en0 {id}", Net("en0", id))),
        ]);

        string[] expected = ["public/ipv4", "public/ipv6", .. entries.Select(id => $"en0/{id}")];
        Assert.Equal(expected.Select(k => $"/infopanel.network/{k}"), envelope.Items.Select(PropsKey));
        Assert.Equal(expected.Select(k => $"infopanel:/network/{k}"), envelope.Items.Select(n => n.Meta.Sensor!.Key));
        Assert.All(envelope.Items, n => Assert.Equal(SensorStatus.Live, n.Meta.Sensor!.Status));
        Assert.All(envelope.Items, n => Assert.Equal("Plugin", (string)n.Props["sensorType"]!));

        var translated = With(envelope, "import.sensor.translated");
        Assert.Equal(expected.Select((_, i) => $"/items/{i}"), translated.Select(d => d.Path));
        Assert.All(translated, d => Assert.Equal(("info", "DisplayItems.xml"), (d.Severity, d.File)));
        Assert.Equal(
            "'Public v4' was bound to plugin:/network-info-plugin/public-ip/ipv4, the 1.x Network Info plugin's; bound to infopanel:/network/public/ipv4, 'IPv4 of Public IP' in 'Network', which the engine reads itself.",
            translated[0].Message);
        Assert.Contains("'Speed of en0' in 'Network'", translated[6].Message);
        Assert.Contains("'IPv4 Address 2 of en0' in 'Network'", translated[8].Message);
        Assert.Contains("'IPv6 Address 1 of en0' in 'Network'", translated[9].Message);
        Assert.Empty(With(envelope, "import.sensor"));
        // Importing reads no value: the public address isn't put in use, nothing more is asked.
        Assert.False(_network.Lookup!.Schedule.InUse(_time.GetTimestamp()));
        Assert.Equal(2, _ipify.Requests);
    }

    [Fact]
    public void Translated_AWindowsAdapter_ByTheSdksIdOfItsGuid_AndAnotherPcsAdapter_WhichReadsUnavailable()
    {
        const string other = "{11111111-2222-3333-4444-555555555555}";
        var envelope = Read(
            Bound("SensorDisplayItem", "LAN", Net(Guid, "ipv4-0")),
            Bound("SensorDisplayItem", "Other LAN", Net(other, "ipv4-0")));

        Assert.Equal([$"infopanel:/network/{GuidId}/ipv4-0", "infopanel:/network/11111111222233334444555555555555/ipv4-0"],
            envelope.Items.Select(n => n.Meta.Sensor!.Key));
        Assert.Equal([SensorStatus.Live, SensorStatus.Unavailable], envelope.Items.Select(n => n.Meta.Sensor!.Status));
        Assert.Contains($"'IPv4 Address 1 of {Guid}' in 'Network'", With(envelope, "import.sensor.translated")[0].Message);
        Assert.Equal(["/items/0", "/items/1"], With(envelope, "import.sensor.translated").Select(d => d.Path));
        Assert.Equal(["/items/1"], With(envelope, "import.sensor").Select(d => d.Path));
    }

    [Fact]
    public void Translated_EveryKindBoundToThePlugin_AtAnyDepth_AndBesideTheClock()
    {
        var envelope = Read(
            Bound("SensorDisplayItem", "Sensor", Net("public-ip", "ipv4")),
            Bound("GraphDisplayItem", "Graph", Net("en0", "speed")),
            Bound("BarDisplayItem", "Bar", Net("en0", "speed")),
            Bound("GaugeDisplayItem", "Gauge", Net("en0", "speed")),
            Group("Box", Group("Inner", Bound("SensorDisplayItem", "Address", Net("en0", "ipv6-1")), Bound("GaugeDisplayItem", "Hand", "/clock-plugin/clock/second"))));

        var inner = envelope.Items[4].Children![0].Children!;
        var bound = envelope.Items.Take(4).Concat(inner).ToList();
        Assert.Equal(["sensor", "graph", "bar", "gauge", "sensor", "gauge"], bound.Select(n => n.Kind));
        Assert.Equal(
            ["infopanel:/network/public/ipv4", "infopanel:/network/en0/speed", "infopanel:/network/en0/speed", "infopanel:/network/en0/speed",
             "infopanel:/network/en0/ipv6-1", "infopanel:/clock/second"],
            bound.Select(n => n.Meta.Sensor!.Key));
        Assert.Equal(["/items/0", "/items/1", "/items/2", "/items/3", "/items/4/children/0/children/0", "/items/4/children/0/children/1"],
            With(envelope, "import.sensor.translated").Select(d => d.Path));
    }

    [Fact]
    public void Translated_OnTheFolderRoute_Too()
    {
        var root = _kit.Temp.Combine("v1");
        CopyFolder(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder"), root);
        const string desk = "6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7";
        File.WriteAllText(Path.Combine(root, "profiles", desk + ".xml"), Items(
            Bound("SensorDisplayItem", "Public", Net("public-ip", "ipv4")),
            Bound("SensorDisplayItem", "Status", Net("en0", "status")),
            Bound("SensorDisplayItem", "LAN", Net(Guid, "name"))));

        var envelope = new V1Folder(root).ReadProfile(System.Guid.Parse(desk), Context())!;

        Assert.Equal("folder", envelope.Source);
        Assert.Equal(["infopanel:/network/public/ipv4", "infopanel:/network/en0/status", $"infopanel:/network/{GuidId}/name"],
            envelope.Items.Select(n => n.Meta.Sensor!.Key));
        Assert.All(envelope.Items, n => Assert.Equal(SensorStatus.Live, n.Meta.Sensor!.Status));
        Assert.Equal(["/items/0", "/items/1", "/items/2"], With(envelope, "import.sensor.translated").Select(d => d.Path));
        Assert.Empty(With(envelope, "import.sensor"));
    }

    [Fact]
    public void Untouched_AnEntryTheSourceDoesntHave_StaysBound_AndUnresolved()
    {
        string[] keys =
        [
            Net("en0", "ipv4-16"),
            Net("en0", "ipv4-01"),
            Net("en0", "ipv4-"),
            Net("en0", "mac"),
            Net("en0", "Speed"),
            Net("public-ip", "ipv5"),
            Net("public-ip", "name"),
            Net("public", "ipv4"),
            Net("{--}", "name"),
            "/network-info-plugin//ipv4-0",
            "/network-info-plugin/en0",
            "/network-info-plugin/en0/x/name",
        ];
        var envelope = Read([.. keys.Select((k, i) => Bound("SensorDisplayItem", $"Item {i}", k))]);

        Assert.Equal(keys, envelope.Items.Select(PropsKey));
        Assert.All(envelope.Items, n => Assert.Equal(SensorStatus.Unavailable, n.Meta.Sensor!.Status));
        Assert.Empty(With(envelope, "import.sensor.translated"));
        Assert.Equal(keys.Select((_, i) => $"/items/{i}"), With(envelope, "import.sensor").Select(d => d.Path));
    }

    [Fact]
    public void Untouched_AnItemWhoseBindingIsntThePlugin()
    {
        var envelope = Read(Bound("SensorDisplayItem", "Libre", Net("en0", "speed"), sensorType: "Libre"));

        Assert.Equal(Net("en0", "speed"), PropsKey(envelope.Items[0]));
        Assert.Empty(With(envelope, "import.sensor.translated"));
    }

    [Fact]
    public void TheTable_TheOldKeysAre1xs_AndEveryNewKeyIsInTheSourcesCatalogue()
    {
        var catalogue = _network.GetSensors().ToDictionary(s => s.Key);

        // 1.x's public addresses: the container "Public IP" and the texts "IPv4" and "IPv6", ids by the plugin SDK's rule.
        var container = new PluginContainer("Public IP").Id;
        foreach (var text in new[] { new PluginText("IPv4", "-"), new PluginText("IPv6", "-") })
        {
            var to = RetiredPlugins.Translate(SensorKey.Plugin($"/network-info-plugin/{container}/{text.Id}"));
            Assert.NotNull(to);
            Assert.True(catalogue.TryGetValue(to.Key, out var sensor), $"{to.Key} isn't listed by the network source");
            Assert.Equal(($"{sensor!.Name} of Public IP", NetworkSource.Name, "Network Info"), (to.Name, to.Source, to.Plugin));
        }

        // An interface's: its container the system's id as it was (1.x gave it as the id), its entries the plugin's own.
        foreach (var (id, entries) in new[] { ("en0", new[] { "ipv4-0", "ipv4-1", "ipv6-0", "ipv6-1" }), (Guid, new[] { "ipv4-0" }) })
        {
            foreach (var entry in Fixed.Concat(entries))
            {
                var to = RetiredPlugins.Translate(SensorKey.Plugin($"/network-info-plugin/{new PluginContainer(id, id).Id}/{entry}"));
                Assert.NotNull(to);
                Assert.True(catalogue.TryGetValue(to.Key, out var sensor), $"{to.Key} isn't listed by the network source");
                Assert.StartsWith(sensor!.Name, to.Name);
                Assert.EndsWith($" of {id}", to.Name);
            }
        }

        Assert.Equal("Network", RetiredPlugins.SourceOf("network-info-plugin"));
        Assert.Equal("Network", RetiredPlugins.SourceOf("Network-Info-Plugin"));
        Assert.Null(RetiredPlugins.Translate(SensorKey.Libre("/network-info-plugin/en0/speed")));
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }
}
