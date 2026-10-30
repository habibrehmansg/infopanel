using System.Net;
using InfoPanel.Images;
using Xunit;

namespace InfoPanel.Core.Tests.Images;

/// <summary>The address rules (spec/docs/providers.md, "Addresses"), pure: nothing here resolves or connects.</summary>
public sealed class AddressRulesTests
{
    [Theory]
    // Never: unspecified, link-local and cloud metadata, multicast, broadcast, reserved.
    [InlineData("0.0.0.0", AddressKind.Never)]
    [InlineData("0.1.2.3", AddressKind.Never)]
    [InlineData("169.254.169.254", AddressKind.Never)]
    [InlineData("169.254.0.1", AddressKind.Never)]
    [InlineData("224.0.0.251", AddressKind.Never)]
    [InlineData("239.255.255.250", AddressKind.Never)]
    [InlineData("240.0.0.1", AddressKind.Never)]
    [InlineData("255.255.255.255", AddressKind.Never)]
    [InlineData("::", AddressKind.Never)]
    [InlineData("fe80::1", AddressKind.Never)]
    [InlineData("febf::1", AddressKind.Never)]
    [InlineData("ff02::fb", AddressKind.Never)]
    // This computer.
    [InlineData("127.0.0.1", AddressKind.Loopback)]
    [InlineData("127.255.255.254", AddressKind.Loopback)]
    [InlineData("::1", AddressKind.Loopback)]
    // The local network.
    [InlineData("10.0.0.1", AddressKind.LocalNetwork)]
    [InlineData("172.16.0.1", AddressKind.LocalNetwork)]
    [InlineData("172.31.255.255", AddressKind.LocalNetwork)]
    [InlineData("192.168.1.20", AddressKind.LocalNetwork)]
    [InlineData("100.64.0.1", AddressKind.LocalNetwork)]
    [InlineData("100.127.255.255", AddressKind.LocalNetwork)]
    [InlineData("fc00::1", AddressKind.LocalNetwork)]
    [InlineData("fd12:3456::1", AddressKind.LocalNetwork)]
    // Public, including the neighbours of each range.
    [InlineData("8.8.8.8", AddressKind.Public)]
    [InlineData("172.15.255.255", AddressKind.Public)]
    [InlineData("172.32.0.1", AddressKind.Public)]
    [InlineData("100.63.255.255", AddressKind.Public)]
    [InlineData("100.128.0.1", AddressKind.Public)]
    [InlineData("169.253.255.255", AddressKind.Public)]
    [InlineData("223.255.255.255", AddressKind.Public)]
    [InlineData("11.0.0.1", AddressKind.Public)]
    [InlineData("2606:4700::1111", AddressKind.Public)]
    // IPv6 forms that carry an IPv4 address are that address.
    [InlineData("::ffff:127.0.0.1", AddressKind.Loopback)]
    [InlineData("::ffff:169.254.169.254", AddressKind.Never)]
    [InlineData("::ffff:192.168.1.20", AddressKind.LocalNetwork)]
    [InlineData("::ffff:8.8.8.8", AddressKind.Public)]
    [InlineData("64:ff9b::7f00:1", AddressKind.Loopback)]
    [InlineData("64:ff9b::a9fe:a9fe", AddressKind.Never)]
    [InlineData("64:ff9b::808:808", AddressKind.Public)]
    [InlineData("::127.0.0.1", AddressKind.Loopback)]
    [InlineData("::169.254.169.254", AddressKind.Never)]
    [InlineData("2002:a9fe:a9fe::1", AddressKind.Never)]
    [InlineData("2002:c0a8:0114::1", AddressKind.LocalNetwork)]
    public void Each_range_is_classified(string address, AddressKind kind) =>
        Assert.Equal(kind, AddressRules.Classify(IPAddress.Parse(address)));

    [Theory]
    [InlineData("169.254.169.254", true, "169.254.169.254 is a link-local address.")]
    [InlineData("::ffff:169.254.169.254", true, "169.254.169.254 is a link-local address.")]
    [InlineData("0.0.0.0", true, "0.0.0.0 is an unspecified address.")]
    [InlineData("::", true, ":: is an unspecified address.")]
    [InlineData("fe80::1", true, "fe80::1 is a link-local address.")]
    [InlineData("224.0.0.251", true, "224.0.0.251 is a multicast address.")]
    [InlineData("ff02::fb", true, "ff02::fb is a multicast address.")]
    [InlineData("255.255.255.255", true, "255.255.255.255 is a reserved address.")]
    [InlineData("127.0.0.1", false, "127.0.0.1 is on this computer.")]
    [InlineData("::1", false, "::1 is on this computer.")]
    [InlineData("::ffff:127.0.0.1", false, "127.0.0.1 is on this computer.")]
    [InlineData("192.168.1.20", false, "192.168.1.20 is on your local network.")]
    [InlineData("fd00::20", false, "fd00::20 is on your local network.")]
    public void A_refused_address_is_named_with_why(string address, bool localNetwork, string why) =>
        Assert.Equal(why, AddressRules.Refusal(IPAddress.Parse(address), localNetwork));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("192.168.1.20")]
    [InlineData("10.1.2.3")]
    [InlineData("fd00::20")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700::1111")]
    public void This_computer_the_local_network_and_public_addresses_are_allowed_while_the_setting_is_on(string address) =>
        Assert.Null(AddressRules.Refusal(IPAddress.Parse(address), localNetwork: true));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700::1111")]
    public void Public_addresses_are_allowed_with_the_setting_off(string address) =>
        Assert.Null(AddressRules.Refusal(IPAddress.Parse(address), localNetwork: false));

    [Theory]
    // A local NAT64 prefix (64:ff9b:1::/48, RFC 8215): the IPv4 address at the /96 layout's place decides, and one whose
    // layout can't be known (a public address there) counts as the local network, where its translator is.
    [InlineData("64:ff9b:1:fffe::c0a8:114", AddressKind.LocalNetwork, "192.168.1.20 is on your local network.")]
    [InlineData("64:ff9b:1::7f00:1", AddressKind.Loopback, "127.0.0.1 is on this computer.")]
    [InlineData("64:ff9b:1:ab::a9fe:a9fe", AddressKind.Never, "169.254.169.254 is a link-local address.")]
    [InlineData("64:ff9b:1::e000:fb", AddressKind.Never, "224.0.0.251 is a multicast address.")]
    [InlineData("64:ff9b:1::808:808", AddressKind.LocalNetwork, "64:ff9b:1::808:808 is on your local network.")]
    [InlineData("64:ff9b:1:ffff:ffff:ffff:808:808", AddressKind.LocalNetwork, "64:ff9b:1:ffff:ffff:ffff:808:808 is on your local network.")]
    public void A_local_translation_prefix_is_refused_with_the_local_network_off(string address, AddressKind kind, string why)
    {
        Assert.Equal(kind, AddressRules.Classify(IPAddress.Parse(address)));
        Assert.Equal(why, AddressRules.Refusal(IPAddress.Parse(address), localNetwork: false));
    }

    [Theory]
    // Its neighbours are public.
    [InlineData("64:ff9b:0:1::c0a8:114")]
    [InlineData("64:ff9b:2::c0a8:114")]
    public void The_neighbours_of_the_local_translation_prefix_are_public(string address) =>
        Assert.Equal(AddressKind.Public, AddressRules.Classify(IPAddress.Parse(address)));

    [Theory]
    // Cloud metadata services, refused before any broader range (here the shared address space, unique local and IETF
    // protocol assignments, which would otherwise be the local network or public), in every form that carries them.
    [InlineData("100.100.100.200", "100.100.100.200 is a cloud metadata service.")]
    [InlineData("::ffff:100.100.100.200", "100.100.100.200 is a cloud metadata service.")]
    [InlineData("::100.100.100.200", "100.100.100.200 is a cloud metadata service.")]
    [InlineData("64:ff9b::6464:64c8", "100.100.100.200 is a cloud metadata service.")]
    [InlineData("64:ff9b:1::6464:64c8", "100.100.100.200 is a cloud metadata service.")]
    [InlineData("2002:6464:64c8::1", "100.100.100.200 is a cloud metadata service.")]
    [InlineData("192.0.0.192", "192.0.0.192 is a cloud metadata service.")]
    [InlineData("::ffff:192.0.0.192", "192.0.0.192 is a cloud metadata service.")]
    [InlineData("fd00:ec2::254", "fd00:ec2::254 is a cloud metadata service.")]
    [InlineData("169.254.169.254", "169.254.169.254 is a link-local address.")]
    [InlineData("169.254.170.2", "169.254.170.2 is a link-local address.")]
    public void Cloud_metadata_services_are_always_refused(string address, string why)
    {
        Assert.Equal(AddressKind.Never, AddressRules.Classify(IPAddress.Parse(address)));
        Assert.Equal(why, AddressRules.Refusal(IPAddress.Parse(address), localNetwork: true));
        Assert.Equal(why, AddressRules.Refusal(IPAddress.Parse(address), localNetwork: false));
    }

    [Theory]
    // In the local-use translation prefix the translator's layout isn't known: a metadata service carried under any of
    // RFC 6052's layouts its prefix may have (/48, /56, /64, /96; the u-octet, bits 64-71, skipped) is that service.
    [InlineData("64:ff9b:1:6464:64:c800::", "100.100.100.200 is a cloud metadata service.")]          // /48
    [InlineData("64:ff9b:1:6464:64:c800:808:808", "100.100.100.200 is a cloud metadata service.")]    // /48, a public /96 tail
    [InlineData("64:ff9b:1:64:64:64c8::", "100.100.100.200 is a cloud metadata service.")]            // /56
    [InlineData("64:ff9b:1:0:64:6464:c800:0", "100.100.100.200 is a cloud metadata service.")]        // /64
    [InlineData("64:ff9b:1::6464:64c8", "100.100.100.200 is a cloud metadata service.")]              // /96
    [InlineData("64:ff9b:1:a9fe:a9:fe00::", "169.254.169.254 is a link-local address.")]              // /48
    [InlineData("64:ff9b:1:c0:0:c0::", "192.0.0.192 is a cloud metadata service.")]                   // /56
    [InlineData("64:ff9b:1:0:a9:feaa:200:0", "169.254.170.2 is a link-local address.")]               // /64
    public void A_metadata_service_under_any_layout_of_the_local_translation_prefix_is_refused(string address, string why)
    {
        Assert.Equal(AddressKind.Never, AddressRules.Classify(IPAddress.Parse(address)));
        Assert.Equal(why, AddressRules.Refusal(IPAddress.Parse(address), localNetwork: true));
        Assert.Equal(why, AddressRules.Refusal(IPAddress.Parse(address), localNetwork: false));
    }

    [Theory]
    // The metadata services' neighbours keep their ranges' rules.
    [InlineData("100.100.100.201", AddressKind.LocalNetwork)]
    [InlineData("192.0.0.193", AddressKind.Public)]
    [InlineData("fd00:ec2::253", AddressKind.LocalNetwork)]
    public void A_metadata_services_neighbour_keeps_its_ranges_rule(string address, AddressKind kind) =>
        Assert.Equal(kind, AddressRules.Classify(IPAddress.Parse(address)));

    [Fact]
    public void An_engine_started_offline_connects_only_to_this_computer()
    {
        Assert.Null(AddressRules.Refusal(IPAddress.Loopback, localNetwork: true, loopbackOnly: true));
        Assert.Null(AddressRules.Refusal(IPAddress.IPv6Loopback, localNetwork: true, loopbackOnly: true));
        Assert.Equal("8.8.8.8 is outside this computer, and the engine runs offline.", AddressRules.Refusal(IPAddress.Parse("8.8.8.8"), true, loopbackOnly: true));
        Assert.Equal("192.168.1.20 is on your local network.", AddressRules.Refusal(IPAddress.Parse("192.168.1.20"), true, loopbackOnly: true));
        Assert.Equal("127.0.0.1 is on this computer.", AddressRules.Refusal(IPAddress.Loopback, localNetwork: false, loopbackOnly: true));
    }
}
