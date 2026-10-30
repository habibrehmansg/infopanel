using System;
using System.Net;
using System.Net.Sockets;

namespace InfoPanel.Images;

/// <summary>What an address is, for the rules a media provider connects by (spec/docs/providers.md, "Addresses").</summary>
public enum AddressKind
{
    /// <summary>Any other address: allowed.</summary>
    Public,

    /// <summary>This computer (<c>127.0.0.0/8</c>, <c>::1</c>): allowed while the local network setting is on.</summary>
    Loopback,

    /// <summary>
    /// The local network (<c>10/8</c>, <c>172.16/12</c>, <c>192.168/16</c>, <c>100.64/10</c>, <c>fc00::/7</c>, a local
    /// translation prefix <c>64:ff9b:1::/48</c>): allowed while the setting is on.
    /// </summary>
    LocalNetwork,

    /// <summary>Unspecified, link-local, a cloud metadata service, multicast, broadcast or reserved: always refused.</summary>
    Never,
}

/// <summary>
/// The addresses a media provider may connect to (spec/docs/providers.md, "Addresses"). Pure: nothing here resolves a
/// name or connects. An IPv6 address that carries an IPv4 one (IPv4-mapped, IPv4-compatible, NAT64 <c>64:ff9b::/96</c>,
/// 6to4 <c>2002::/16</c>) is classified as that IPv4 address. One in the local-use translation prefix
/// <c>64:ff9b:1::/48</c> (RFC 8215), whose layout only its translator knows, is a cloud metadata service when it carries
/// one under any of the layouts its translator's prefix may have; else it is classified as the IPv4 address the /96
/// layout puts in its last 32 bits, unless that is a public address: then as the local network, where its translator is.
/// </summary>
public static class AddressRules
{
    /// <summary>
    /// Cloud metadata services, refused before any broader range: AWS's instance metadata (<c>169.254.169.254</c>, and
    /// <c>fd00:ec2::254</c> over IPv6), AWS ECS's task metadata (<c>169.254.170.2</c>), Alibaba Cloud's
    /// (<c>100.100.100.200</c>) and Oracle Cloud's (<c>192.0.0.192</c>). Google Cloud, Azure, DigitalOcean and others use
    /// <c>169.254.169.254</c>. Each is matched in every form that carries it (see <see cref="Effective"/>).
    /// </summary>
    private static readonly IPAddress[] Metadata =
    [
        IPAddress.Parse("169.254.169.254"),
        IPAddress.Parse("169.254.170.2"),
        IPAddress.Parse("100.100.100.200"),
        IPAddress.Parse("192.0.0.192"),
        IPAddress.Parse("fd00:ec2::254"),
    ];

    /// <summary>The address as it is classified and named: the IPv4 address an IPv6 one carries, else itself.</summary>
    public static IPAddress Effective(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address;
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        Span<byte> b = stackalloc byte[16];
        address.TryWriteBytes(b, out _);
        // IPv4-compatible (::a.b.c.d, deprecated): the first 96 bits zero, but not :: or ::1.
        if (b[..12].IndexOfAnyExcept((byte)0) < 0 && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1))
            return new IPAddress(b[12..]);
        // NAT64's well-known prefix 64:ff9b::/96.
        if (IsNat64(b) && b[4..12].IndexOfAnyExcept((byte)0) < 0)
            return new IPAddress(b[12..]);
        // The local-use translation prefix 64:ff9b:1::/48, whose layout can't be known: a metadata service carried under
        // any layout is that service; else the /96 layout's IPv4 address unless it is a public one, when the address is
        // named as it is (and classified as the local network).
        if (IsLocalNat64(b))
        {
            foreach (var layout in LocalNat64Layouts(b))
                if (IsMetadata(layout)) return layout;
            var carried = new IPAddress(b[12..]);
            return Classify(carried) is AddressKind.Public ? address : carried;
        }
        // 6to4 2002:a.b.c.d::/48.
        if (b[0] == 0x20 && b[1] == 0x02)
            return new IPAddress(b[2..6]);
        return address;
    }

    private static bool IsNat64(ReadOnlySpan<byte> b) => b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b;

    private static bool IsLocalNat64(ReadOnlySpan<byte> b) => IsNat64(b) && b[4] == 0x00 && b[5] == 0x01;

    /// <summary>
    /// The IPv4 address an address in <c>64:ff9b:1::/48</c> carries under each layout RFC 6052 (§2.2) gives a translator
    /// prefix inside it: /48, /56, /64 and /96, the u-octet (bits 64–71) skipped.
    /// </summary>
    private static IPAddress[] LocalNat64Layouts(ReadOnlySpan<byte> b) =>
    [
        new IPAddress(new[] { b[6], b[7], b[9], b[10] }),   // /48
        new IPAddress(new[] { b[7], b[9], b[10], b[11] }),  // /56
        new IPAddress(b[9..13]),                            // /64
        new IPAddress(b[12..16]),                           // /96
    ];

    /// <summary>What <paramref name="address"/> is, by the rules' table.</summary>
    public static AddressKind Classify(IPAddress address)
    {
        address = Effective(address);
        if (IsMetadata(address)) return AddressKind.Never;
        Span<byte> b = stackalloc byte[16];
        address.TryWriteBytes(b, out var length);
        if (length == 4)
        {
            return b[0] switch
            {
                0 => AddressKind.Never,                                              // 0.0.0.0/8: this network, unspecified
                127 => AddressKind.Loopback,                                         // 127.0.0.0/8
                10 => AddressKind.LocalNetwork,                                      // 10.0.0.0/8
                169 when b[1] == 254 => AddressKind.Never,                           // 169.254.0.0/16: link-local, cloud metadata
                172 when (b[1] & 0xf0) == 16 => AddressKind.LocalNetwork,            // 172.16.0.0/12
                192 when b[1] == 168 => AddressKind.LocalNetwork,                    // 192.168.0.0/16
                100 when (b[1] & 0xc0) == 64 => AddressKind.LocalNetwork,            // 100.64.0.0/10: shared address space
                >= 224 => AddressKind.Never,                                         // 224/4 multicast, 240/4 reserved, broadcast
                _ => AddressKind.Public,
            };
        }

        if (address.Equals(IPAddress.IPv6Any)) return AddressKind.Never;            // ::
        if (address.Equals(IPAddress.IPv6Loopback)) return AddressKind.Loopback;    // ::1
        if (b[0] == 0xff) return AddressKind.Never;                                 // ff00::/8 multicast
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return AddressKind.Never;        // fe80::/10 link-local
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0xc0) return AddressKind.LocalNetwork; // fec0::/10 site-local (deprecated)
        if ((b[0] & 0xfe) == 0xfc) return AddressKind.LocalNetwork;                 // fc00::/7 unique local
        if (IsLocalNat64(b)) return AddressKind.LocalNetwork;                       // 64:ff9b:1::/48, a layout not known
        return AddressKind.Public;
    }

    private static bool IsMetadata(IPAddress effective) => Array.IndexOf(Metadata, effective) >= 0;

    /// <summary>
    /// Whether a connection to <paramref name="address"/> itself stays on this computer: <c>127.0.0.0/8</c>, <c>::1</c>,
    /// or an IPv4-mapped one of them. A translated or tunnelled form of a loopback address (NAT64, 6to4,
    /// IPv4-compatible) goes to a translator first, so it doesn't.
    /// </summary>
    public static bool StaysOnThisComputer(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily == AddressFamily.InterNetwork ? IPAddress.IsLoopback(address) : address.Equals(IPAddress.IPv6Loopback);
    }

    /// <summary>
    /// Why a provider may not connect to <paramref name="address"/>, as a sentence naming it, or null when it may:
    /// <paramref name="localNetwork"/> is the <c>media.localNetwork</c> setting, and <paramref name="loopbackOnly"/>
    /// keeps every connection on this computer (an engine started offline): only to an address that
    /// <see cref="StaysOnThisComputer"/>.
    /// </summary>
    public static string? Refusal(IPAddress address, bool localNetwork, bool loopbackOnly = false)
    {
        var shown = Effective(address);
        var kind = Classify(address);
        return kind switch
        {
            AddressKind.Never => $"{shown} is {NeverWhy(shown)}.",
            AddressKind.Loopback when !localNetwork => $"{shown} is on this computer.",
            AddressKind.Loopback when loopbackOnly && !StaysOnThisComputer(address) => $"{address} is outside this computer, and the engine runs offline.",
            AddressKind.LocalNetwork when !localNetwork || loopbackOnly => $"{shown} is on your local network.",
            AddressKind.Public when loopbackOnly => $"{shown} is outside this computer, and the engine runs offline.",
            _ => null,
        };
    }

    private static string NeverWhy(IPAddress address)
    {
        Span<byte> b = stackalloc byte[16];
        address.TryWriteBytes(b, out var length);
        if (length == 4)
        {
            return b[0] switch
            {
                0 => "an unspecified address",
                169 when b[1] == 254 => "a link-local address",
                _ when IsMetadata(address) => "a cloud metadata service",
                >= 224 and < 240 => "a multicast address",
                _ => "a reserved address",
            };
        }
        if (address.Equals(IPAddress.IPv6Any)) return "an unspecified address";
        if (IsMetadata(address)) return "a cloud metadata service";
        return b[0] == 0xff ? "a multicast address" : "a link-local address";
    }
}
