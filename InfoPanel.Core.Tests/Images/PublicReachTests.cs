using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using InfoPanel.Images;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;
using Rig = InfoPanel.Core.Tests.Images.HttpMediaProviderTests.Rig;

namespace InfoPanel.Core.Tests.Images;

/// <summary>
/// Sources marked <c>"reach": "public"</c> (spec/docs/providers.md, "Public addresses only"): the rule itself, pure, and
/// the <c>http</c> provider holding a marked source to it on every connection, against a server on 127.0.0.1, a fake
/// resolver and a fake interface list: no test reaches past loopback or reads this computer's real interfaces, but one
/// (which only lists them).
/// </summary>
public sealed class PublicReachTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Red = HttpMediaProviderTests.Png(SKColors.Red);

    /// <summary>
    /// A home computer's interfaces: a private IPv4 network, a global IPv6 /64, a public IPv4 network on-link (a /28),
    /// and loopback.
    /// </summary>
    internal static readonly IReadOnlyList<InterfaceAddress> Home =
    [
        new(IPAddress.Parse("192.168.1.5"), 24),
        new(IPAddress.Parse("2001:db8:1:2::5"), 64),
        new(IPAddress.Parse("198.51.100.10"), 28),
        new(IPAddress.Loopback, 8),
        new(IPAddress.IPv6Loopback, 128),
    ];

    private const string Only = ", and this source may reach only public addresses.";

    // ---- the rule -----------------------------------------------------------------------------------------------

    [Theory]
    // This computer and the local network by their ranges: refused for the mark, whatever the setting says.
    [InlineData("127.0.0.1", "media.reach", "127.0.0.1 is on this computer" + Only)]
    [InlineData("::1", "media.reach", "::1 is on this computer" + Only)]
    [InlineData("10.0.0.1", "media.reach", "10.0.0.1 is on your local network" + Only)]
    [InlineData("172.16.0.1", "media.reach", "172.16.0.1 is on your local network" + Only)]
    [InlineData("192.168.7.20", "media.reach", "192.168.7.20 is on your local network" + Only)]
    [InlineData("100.64.0.1", "media.reach", "100.64.0.1 is on your local network" + Only)]
    [InlineData("fd12:3456::1", "media.reach", "fd12:3456::1 is on your local network" + Only)]
    [InlineData("::ffff:192.168.1.20", "media.reach", "192.168.1.20 is on your local network" + Only)]
    // Always refused, for every source: the words of the address rules.
    [InlineData("169.254.10.1", "media.address", "169.254.10.1 is a link-local address.")]
    [InlineData("fe80::1", "media.address", "fe80::1 is a link-local address.")]
    [InlineData("169.254.169.254", "media.address", "169.254.169.254 is a link-local address.")]
    // This computer's own addresses, public by their ranges.
    [InlineData("2001:db8:1:2::5", "media.reach", "2001:db8:1:2::5 is this computer's own address" + Only)]
    [InlineData("198.51.100.10", "media.reach", "198.51.100.10 is this computer's own address" + Only)]
    [InlineData("::ffff:198.51.100.10", "media.reach", "198.51.100.10 is this computer's own address" + Only)]
    // Neighbours on a network this computer is on (on-link), public by their ranges.
    [InlineData("2001:db8:1:2::9", "media.reach", "2001:db8:1:2::9 is on your local network" + Only)]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "media.reach", "2001:db8:1:2:ffff:ffff:ffff:ffff is on your local network" + Only)]
    [InlineData("198.51.100.15", "media.reach", "198.51.100.15 is on your local network" + Only)]
    public void A_marked_source_is_refused_this_computer_and_the_local_network(string address, string code, string why) =>
        Assert.Equal((code, why), AddressRules.PublicRefusal(IPAddress.Parse(address), Home));

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("2606:4700::1111")]
    [InlineData("198.51.100.16")]          // just past the on-link /28
    [InlineData("2001:db8:1:3::9")]        // another /64 of the home's prefix: the residual, public here
    public void A_marked_source_may_reach_a_public_address(string address) =>
        Assert.Null(AddressRules.PublicRefusal(IPAddress.Parse(address), Home));

    [Fact]
    public void Offline_a_marked_source_reaches_nothing()
    {
        Assert.Equal(("media.address", "203.0.113.7 is outside this computer, and the engine runs offline."),
            AddressRules.PublicRefusal(IPAddress.Parse("203.0.113.7"), [], loopbackOnly: true));
        Assert.Equal(("media.reach", "127.0.0.1 is on this computer" + Only),
            AddressRules.PublicRefusal(IPAddress.Loopback, [], loopbackOnly: true));
    }

    [Fact]
    public async Task A_marked_connection_reads_the_interface_list_each_time_and_an_unmarked_one_never()
    {
        var reads = 0;
        IReadOnlyList<InterfaceAddress> own = [];
        var connected = new List<IPEndPoint>();
        IReadOnlyList<InterfaceAddress> Interfaces()
        {
            reads++;
            return own;
        }
        Task<IPAddress[]> Resolve(string host, CancellationToken token) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.7") });
        ValueTask<Stream> Connect(IPEndPoint endpoint, CancellationToken token)
        {
            connected.Add(endpoint);
            return ValueTask.FromResult<Stream>(new MemoryStream());
        }

        await using (await AddressRules.ConnectAsync("radar.test", 80, Resolve, Connect, true, false, Ct, Interfaces)) { }
        Assert.Equal(1, reads);
        // The list changes: the next connection sees it.
        own = [new(IPAddress.Parse("203.0.113.1"), 24)];
        var refused = await Assert.ThrowsAsync<AddressRefusedException>(async () =>
            await AddressRules.ConnectAsync("radar.test", 80, Resolve, Connect, true, false, Ct, Interfaces));
        Assert.Equal(2, reads);
        Assert.Equal("media.reach", refused.Code);
        Assert.Equal("203.0.113.7 is on your local network" + Only, refused.Message);
        Assert.Single(connected);

        // Unmarked: the list isn't read, and the setting alone applies.
        await using (await AddressRules.ConnectAsync("radar.test", 80, Resolve, Connect, true, false, Ct)) { }
        Assert.Equal(2, reads);
        var local = await Assert.ThrowsAsync<AddressRefusedException>(async () =>
            await AddressRules.ConnectAsync("127.0.0.1", 80, Resolve, Connect, false, false, Ct));
        Assert.Equal(("media.address", "127.0.0.1 is on this computer."), (local.Code, local.Message));
    }

    [Fact]
    public void This_computers_interface_list_has_its_loopback_address_with_a_prefix()
    {
        // The real list, read from the system (no request is sent): loopback is on every machine the tests run on.
        var own = AddressRules.ThisComputersAddresses();
        Assert.Contains(own, a => IPAddress.IsLoopback(a.Address) && a.PrefixLength > 0);
        Assert.All(own, a => Assert.InRange(a.PrefixLength, 0, a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
    }

    /// <summary>An interface address as the system would list it: its prefix and IPv4 mask read through functions that may throw.</summary>
    private sealed class Unicast(string address, Func<int> prefix, Func<IPAddress> mask) : UnicastIPAddressInformation
    {
        public override IPAddress Address { get; } = IPAddress.Parse(address);
        public override int PrefixLength => prefix();
        public override IPAddress IPv4Mask => mask();
        public override bool IsDnsEligible => true;
        public override bool IsTransient => false;
        public override long AddressPreferredLifetime => 0;
        public override long AddressValidLifetime => 0;
        public override long DhcpLeaseLifetime => 0;
        public override DuplicateAddressDetectionState DuplicateAddressDetectionState => DuplicateAddressDetectionState.Preferred;
        public override PrefixOrigin PrefixOrigin => PrefixOrigin.Manual;
        public override SuffixOrigin SuffixOrigin => SuffixOrigin.Manual;
    }

    private static int NoPrefix() => throw new PlatformNotSupportedException();

    private static IPAddress NoMask() => throw new PlatformNotSupportedException();

    [Fact]
    public void An_interface_address_whose_prefix_the_system_cant_give_counts_its_likely_network_as_on_link()
    {
        var own = AddressRules.AddressesOf(
        [
            () => [new Unicast("2001:db8:1:2::5", NoPrefix, NoMask), new Unicast("2001:db8:7::5", () => 0, NoMask)],
            () => [new Unicast("198.51.100.10", NoPrefix, () => IPAddress.Parse("255.255.255.240")), new Unicast("203.0.113.10", NoPrefix, NoMask)],
            () => [new Unicast("192.0.2.10", () => 0, () => IPAddress.Parse("0.0.0.0")), new Unicast("192.168.1.5", () => 24, NoMask)],
        ]);
        // The system's prefix when it gives one; else the IPv4 mask's; else a /64 (IPv6) or a /24 (IPv4).
        Assert.Equal(
            [new InterfaceAddress(IPAddress.Parse("2001:db8:1:2::5"), 64), new(IPAddress.Parse("2001:db8:7::5"), 64),
             new(IPAddress.Parse("198.51.100.10"), 28), new(IPAddress.Parse("203.0.113.10"), 24),
             new(IPAddress.Parse("192.0.2.10"), 24), new(IPAddress.Parse("192.168.1.5"), 24)],
            own);
        Assert.Equal(("media.reach", "2001:db8:1:2::9 is on your local network" + Only), AddressRules.PublicRefusal(IPAddress.Parse("2001:db8:1:2::9"), own));
        Assert.Equal(("media.reach", "203.0.113.200 is on your local network" + Only), AddressRules.PublicRefusal(IPAddress.Parse("203.0.113.200"), own));
        Assert.Equal(("media.reach", "198.51.100.15 is on your local network" + Only), AddressRules.PublicRefusal(IPAddress.Parse("198.51.100.15"), own));
        Assert.Null(AddressRules.PublicRefusal(IPAddress.Parse("198.51.100.16"), own));
        Assert.Null(AddressRules.PublicRefusal(IPAddress.Parse("2001:db8:1:3::9"), own));
    }

    [Fact]
    public async Task A_marked_connection_is_refused_when_this_computers_interfaces_cant_be_read()
    {
        const string why = "This computer's network interfaces couldn't be read" + Only;
        var connected = new List<IPEndPoint>();
        Task<IPAddress[]> Resolve(string host, CancellationToken token) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.7") });
        ValueTask<Stream> Connect(IPEndPoint endpoint, CancellationToken token)
        {
            connected.Add(endpoint);
            return ValueTask.FromResult<Stream>(new MemoryStream());
        }

        // One interface's addresses can't be read: it isn't left out of the list, the whole read fails.
        IReadOnlyList<InterfaceAddress> OneUnreadable() => AddressRules.AddressesOf(
            [() => [new Unicast("192.168.1.5", () => 24, NoMask)], () => throw new NetworkInformationException(5)]);
        var refused = await Assert.ThrowsAsync<AddressRefusedException>(async () =>
            await AddressRules.ConnectAsync("radar.test", 80, Resolve, Connect, true, false, Ct, OneUnreadable));
        Assert.Equal(("media.reach", why), (refused.Code, refused.Message));

        // The list itself can't be read.
        IReadOnlyList<InterfaceAddress> Unreadable() => throw new NetworkInformationException(5);
        refused = await Assert.ThrowsAsync<AddressRefusedException>(async () =>
            await AddressRules.ConnectAsync("radar.test", 80, Resolve, Connect, true, false, Ct, Unreadable));
        Assert.Equal(("media.reach", why), (refused.Code, refused.Message));
        Assert.Empty(connected);

        // An unmarked connection doesn't read it, and goes ahead.
        await using (await AddressRules.ConnectAsync("radar.test", 80, Resolve, Connect, true, false, Ct)) { }
        Assert.Single(connected);
    }

    // ---- the http provider --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("nas.test", "192.168.1.20", "media.reach", "192.168.1.20 is on your local network" + Only)]
    [InlineData("ten.test", "10.1.2.3", "media.reach", "10.1.2.3 is on your local network" + Only)]
    [InlineData("loop.test", "127.0.0.1", "media.reach", "127.0.0.1 is on this computer" + Only)]
    [InlineData("loop6.test", "::1", "media.reach", "::1 is on this computer" + Only)]
    [InlineData("link.test", "169.254.3.4", "media.address", "169.254.3.4 is a link-local address.")]
    [InlineData("self6.test", "2001:db8:1:2::5", "media.reach", "2001:db8:1:2::5 is this computer's own address" + Only)]
    [InlineData("neighbour.test", "2001:db8:1:2::9", "media.reach", "2001:db8:1:2::9 is on your local network" + Only)]
    public async Task A_marked_web_image_is_refused_inward_addresses_with_the_local_network_setting_on(string host, string address, string code, string why)
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        rig.LocalNetwork = true;
        rig.Own = Home;
        rig.Names = h => h == host ? [IPAddress.Parse(address)] : null;
        var refused = await rig.Fetch($"http://{host}:{rig.Server.Port}/x.png", publicOnly: true);
        Assert.Null(refused.Sample);
        Assert.Equal(new MediaStatus(code, why), refused.Status);
        lock (rig.Connected) Assert.Empty(rig.Connected);
        Assert.Empty(rig.Server.Requests);
    }

    [Fact]
    public async Task A_marked_https_source_downloads_nothing_its_servers_certificate_names()
    {
        // A server at a public address presents a certificate whose missing issuer and revocation list are on an inward
        // address (a second listener on 127.0.0.1). Checking the certificate must not reach it for a marked source: the
        // chain is built with downloads off and no revocation check (seen through the test seam on every platform, and
        // at the listener where the platform's chain building would download).
        await using var hostile = new HostileCertificateServer("radar.test");
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        rig.ConnectPort = hostile.Port;
        rig.Own = Home;
        rig.Names = h => h == "radar.test" ? [IPAddress.Parse("203.0.113.7")] : null;
        var url = $"https://radar.test:{hostile.Port}/x.png";

        var marked = await rig.Fetch(url, publicOnly: true);
        Assert.Null(marked.Sample);
        Assert.Equal("media.provider", marked.Status?.Code);
        Assert.Equal(1, hostile.Handshakes);
        lock (rig.Chains) Assert.Equal([(true, X509RevocationMode.NoCheck)], rig.Chains);
        Assert.Equal(0, hostile.InwardConnections);
        lock (rig.Connected) Assert.Equal([IPAddress.Parse("203.0.113.7")], rig.Connected.Select(e => e.Address));

        // An unmarked source's certificate is checked as before: the platform's defaults, downloads included.
        var unmarked = await rig.Fetch(url);
        Assert.Null(unmarked.Sample);
        Assert.Equal("media.provider", unmarked.Status?.Code);
        Assert.Equal(2, hostile.Handshakes);
        lock (rig.Chains) Assert.Equal((false, X509RevocationMode.NoCheck), rig.Chains[^1]);
    }

    [Fact]
    public async Task A_marked_sources_https_redirect_hop_downloads_nothing_its_servers_certificate_names()
    {
        // A marked fetch from a public address is redirected to an https address whose server presents the hostile
        // certificate: the hop's chain, too, is built with downloads off and no revocation check.
        await using var hostile = new HostileCertificateServer("cdn.test");
        Rig? self = null;
        await using var rig = new Rig((r, s, t) =>
        {
            // The hop's connection goes to the certificate's server.
            self!.ConnectPort = hostile.Port;
            return LocalHttpServer.Answer(s, 302, contentType: null, headers: $"Location: https://cdn.test:{hostile.Port}/y.png");
        });
        self = rig;
        rig.Own = Home;
        rig.Names = h => h switch
        {
            "radar.test" => [IPAddress.Parse("203.0.113.7")],
            "cdn.test" => [IPAddress.Parse("203.0.113.8")],
            _ => null,
        };

        var marked = await rig.Fetch($"http://radar.test:{rig.Server.Port}/start", publicOnly: true);
        Assert.Null(marked.Sample);
        Assert.Equal("media.provider", marked.Status?.Code);
        Assert.Equal("/start", Assert.Single(rig.Server.Requests).Path);
        Assert.Equal(1, hostile.Handshakes);
        lock (rig.Chains) Assert.Equal([(true, X509RevocationMode.NoCheck)], rig.Chains);
        Assert.Equal(0, hostile.InwardConnections);
        lock (rig.Connected) Assert.Equal([IPAddress.Parse("203.0.113.7"), IPAddress.Parse("203.0.113.8")], rig.Connected.Select(e => e.Address));
    }

    [Fact]
    public async Task A_marked_web_image_at_this_computers_literal_address_is_refused()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        var refused = await rig.Fetch(rig.Url("/x.png"), publicOnly: true);
        Assert.Equal(new MediaStatus("media.reach", "127.0.0.1 is on this computer" + Only), refused.Status);
        Assert.Empty(rig.Server.Requests);
    }

    [Fact]
    public async Task A_marked_web_image_at_a_public_address_is_shown()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        rig.Own = Home;
        rig.Names = h => h == "radar.test" ? [IPAddress.Parse("203.0.113.7")] : null;
        var shown = await rig.Fetch($"http://radar.test:{rig.Server.Port}/x.png", publicOnly: true);
        Assert.NotNull(shown.Sample);
        Assert.Null(shown.Status);
        Rig.Release(shown);
        lock (rig.Connected) Assert.Equal([IPAddress.Parse("203.0.113.7")], rig.Connected.Select(e => e.Address));
        Assert.Equal(1, Volatile.Read(ref rig.InterfaceReads));
    }

    [Theory]
    [InlineData("http://nas.test:{port}/x.png", "192.168.1.20 is on your local network" + Only)]
    [InlineData("http://[2001:db8:1:2::9]:{port}/x.png", "2001:db8:1:2::9 is on your local network" + Only)]
    [InlineData("http://127.0.0.1:{port}/x.png", "127.0.0.1 is on this computer" + Only)]
    public async Task A_marked_web_images_redirect_inward_is_refused_at_its_hop(string target, string why)
    {
        var port = 0;
        await using var rig = new Rig((r, s, t) => r.Path == "/start"
            ? LocalHttpServer.Answer(s, 302, contentType: null, headers: "Location: " + target.Replace("{port}", Volatile.Read(ref port).ToString()))
            : LocalHttpServer.Answer(s, 200, Red));
        Volatile.Write(ref port, rig.Server.Port);
        rig.Own = Home;
        rig.Names = h => h switch
        {
            "radar.test" => [IPAddress.Parse("203.0.113.7")],
            "nas.test" => [IPAddress.Parse("192.168.1.20")],
            _ => null,
        };
        var refused = await rig.Fetch($"http://radar.test:{rig.Server.Port}/start", publicOnly: true);
        Assert.Null(refused.Sample);
        Assert.Equal(new MediaStatus("media.reach", why), refused.Status);
        Assert.Equal("/start", Assert.Single(rig.Server.Requests).Path);
        lock (rig.Connected) Assert.Equal([IPAddress.Parse("203.0.113.7")], rig.Connected.Select(e => e.Address));
        // The first connection and the hop's each read the list.
        Assert.Equal(2, Volatile.Read(ref rig.InterfaceReads));
    }

    [Fact]
    public async Task Each_hop_of_a_marked_fetch_reads_the_interface_list_and_a_change_applies_at_the_next_fetch()
    {
        await using var rig = new Rig((r, s, t) => r.Path switch
        {
            "/r0" => LocalHttpServer.Answer(s, 302, contentType: null, headers: "Location: /r1"),
            "/r1" => LocalHttpServer.Answer(s, 302, contentType: null, headers: "Location: /r2"),
            _ => LocalHttpServer.Answer(s, 200, Red),
        });
        rig.Own = Home;
        rig.Names = h => h == "radar.test" ? [IPAddress.Parse("203.0.113.7")] : null;
        var url = $"http://radar.test:{rig.Server.Port}/r0";
        var shown = await rig.Fetch(url, refresh: 10, publicOnly: true);
        Assert.NotNull(shown.Sample);
        Rig.Release(shown);
        Assert.Equal(3, rig.Server.Requests.Count);
        Assert.Equal(3, Volatile.Read(ref rig.InterfaceReads));

        // The computer joins the picture's network: the next fetch is refused, and the last good picture stays.
        rig.Own = [.. Home, new(IPAddress.Parse("203.0.113.1"), 24)];
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        var after = await rig.Fetch(url, refresh: 10, publicOnly: true);
        Assert.NotNull(after.Sample);
        Assert.Equal(new MediaStatus("media.reach", "203.0.113.7 is on your local network" + Only), after.Status);
        Rig.Release(after);
        Assert.Equal(3, rig.Server.Requests.Count);
        Assert.Equal(4, Volatile.Read(ref rig.InterfaceReads));
    }

    [Fact]
    public async Task A_marked_web_images_name_re_pointed_inward_is_refused_on_a_later_fetch()
    {
        var answers = 0;
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        rig.Own = Home;
        rig.Names = h => h == "rebind.test"
            ? [Interlocked.Increment(ref answers) == 1 ? IPAddress.Parse("203.0.113.7") : IPAddress.Parse("192.168.1.20")]
            : null;
        var url = $"http://rebind.test:{rig.Server.Port}/x.png";
        Rig.Release(await rig.Fetch(url, refresh: 10, publicOnly: true));
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        var after = await rig.Fetch(url, refresh: 10, publicOnly: true);
        Assert.NotNull(after.Sample);
        Assert.Equal(new MediaStatus("media.reach", "192.168.1.20 is on your local network" + Only), after.Status);
        Rig.Release(after);
        Assert.Single(rig.Server.Requests);
        lock (rig.Connected) Assert.Equal([IPAddress.Parse("203.0.113.7")], rig.Connected.Select(e => e.Address));
    }

    [Fact]
    public async Task An_unmarked_web_image_keeps_the_local_network_settings_behaviour_and_reads_no_interface_list()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        rig.Own = Home;
        rig.Names = h => h switch
        {
            "nas.test" => [IPAddress.Parse("192.168.1.20")],
            "self6.test" => [IPAddress.Parse("2001:db8:1:2::5")],
            _ => null,
        };
        var nas = await rig.Fetch($"http://nas.test:{rig.Server.Port}/x.png");
        Assert.NotNull(nas.Sample);
        Rig.Release(nas);
        var self = await rig.Fetch($"http://self6.test:{rig.Server.Port}/x.png");
        Assert.NotNull(self.Sample);
        Rig.Release(self);
        Assert.Equal(0, Volatile.Read(ref rig.InterfaceReads));

        rig.LocalNetwork = false;
        rig.Provider.Reset();
        var off = await rig.Fetch($"http://nas.test:{rig.Server.Port}/x.png");
        Assert.Equal(new MediaStatus("media.address", "192.168.1.20 is on your local network."), off.Status);
    }

    [Fact]
    public async Task A_marked_and_an_unmarked_source_on_one_address_never_share_a_fetcher_or_a_picture()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        var url = rig.Url("/x.png");

        // The unmarked source fetches and shows its picture.
        var unmarked = await rig.Fetch(url);
        Assert.NotNull(unmarked.Sample);
        Rig.Release(unmarked);

        // The marked one on the same address has a fetcher of its own: it shows nothing of the unmarked one's, and its own
        // fetch is refused, through a connection of its own (it read the interface list).
        var answered = rig.NextAnswer(url);
        var first = rig.Ask(url, publicOnly: true);
        Assert.Null(first.Sample);
        Assert.Equal("media.loading", first.Status?.Code);
        Assert.Equal(2, rig.Provider.FetcherCount);
        await answered.WaitAsync(Wait, Ct);
        var marked = rig.Ask(url, publicOnly: true);
        Assert.Null(marked.Sample);
        Assert.Equal(new MediaStatus("media.reach", "127.0.0.1 is on this computer" + Only), marked.Status);
        Assert.Equal(1, Volatile.Read(ref rig.InterfaceReads));
        Assert.Single(rig.Server.Requests);

        // The unmarked one is untouched.
        var still = rig.Ask(url);
        Assert.NotNull(still.Sample);
        Assert.Null(still.Status);
        Rig.Release(still);
        Assert.Equal(2, rig.Provider.FetcherCount);
    }

    [Fact]
    public async Task A_refused_marked_source_leaves_an_unmarked_one_on_its_address_to_fetch_for_itself()
    {
        await using var rig = new Rig((r, s, t) => LocalHttpServer.Answer(s, 200, Red));
        var url = rig.Url("/x.png");
        var marked = await rig.Fetch(url, publicOnly: true);
        Assert.Equal("media.reach", marked.Status?.Code);
        var unmarked = await rig.Fetch(url);
        Assert.NotNull(unmarked.Sample);
        Assert.Null(unmarked.Status);
        Rig.Release(unmarked);
        Assert.Single(rig.Server.Requests);
        // Asked again, the marked one is still refused and shows nothing.
        var again = rig.Ask(url, publicOnly: true);
        Assert.Null(again.Sample);
        Assert.Equal("media.reach", again.Status?.Code);
    }
}
