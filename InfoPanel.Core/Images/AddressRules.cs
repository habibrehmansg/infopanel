using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

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
/// A connection the address rules refused; its message names the address and why, and its <see cref="Code"/> is the
/// item's media problem: <c>media.address</c>, or <c>media.reach</c> for a source marked to reach only public addresses.
/// </summary>
public sealed class AddressRefusedException(string message, string code = AddressRules.AddressCode) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// An address on one of this computer's network interfaces, with its prefix length: the address is this computer's, and
/// every address in its prefix is on-link (spec/docs/providers.md, "Public addresses only").
/// </summary>
public readonly record struct InterfaceAddress(IPAddress Address, int PrefixLength);

/// <summary>
/// The addresses a media provider may connect to (spec/docs/providers.md, "Addresses"). Pure but for
/// <see cref="ConnectAsync"/>, which resolves and connects through the functions it is given. An IPv6 address that carries an IPv4 one (IPv4-mapped, IPv4-compatible, NAT64 <c>64:ff9b::/96</c>,
/// 6to4 <c>2002::/16</c>) is classified as that IPv4 address. One in the local-use translation prefix
/// <c>64:ff9b:1::/48</c> (RFC 8215), whose layout only its translator knows, is a cloud metadata service when it carries
/// one under any of the layouts its translator's prefix may have; else it is classified as the IPv4 address the /96
/// layout puts in its last 32 bits, unless that is a public address: then as the local network, where its translator is.
/// </summary>
public static class AddressRules
{
    /// <summary>The media problem of an address the rules refuse.</summary>
    public const string AddressCode = "media.address";

    /// <summary>The media problem of an address refused only because its source may reach public addresses alone.</summary>
    public const string ReachCode = "media.reach";

    private const string PublicOnlySuffix = ", and this source may reach only public addresses.";

    /// <summary>
    /// Why a source marked to reach only public addresses may not connect to <paramref name="address"/>, or null when it
    /// may: <paramref name="own"/> is this computer's interface addresses, read for this connection.
    /// </summary>
    public static (string Code, string Message)? PublicRefusal(IPAddress address, IReadOnlyList<InterfaceAddress> own, bool loopbackOnly = false)
    {
        const string only = PublicOnlySuffix;
        var shown = Effective(address);
        switch (Classify(address))
        {
            case AddressKind.Never:
                return (AddressCode, $"{shown} is {NeverWhy(shown)}.");
            case AddressKind.Loopback:
                return (ReachCode, $"{shown} is on this computer{only}");
            case AddressKind.LocalNetwork:
                return (ReachCode, $"{shown} is on your local network{only}");
        }
        // Public by its range: this computer's own address, or a neighbour on a network it is on, is still inward. The
        // address is checked as written and as the IPv4 address it carries.
        foreach (var a in own)
            if (SameAddress(a.Address, address) || SameAddress(a.Address, shown))
                return (ReachCode, $"{shown} is this computer's own address{only}");
        foreach (var a in own)
            if (InPrefix(address, a) || InPrefix(shown, a))
                return (ReachCode, $"{shown} is on your local network{only}");
        return loopbackOnly ? (AddressCode, $"{shown} is outside this computer, and the engine runs offline.") : null;
    }

    private static bool SameAddress(IPAddress a, IPAddress b) =>
        a.AddressFamily == b.AddressFamily && a.GetAddressBytes().AsSpan().SequenceEqual(b.GetAddressBytes());

    /// <summary>Whether <paramref name="address"/> is in the network <paramref name="network"/> is on (its prefix).</summary>
    private static bool InPrefix(IPAddress address, InterfaceAddress network)
    {
        if (address.AddressFamily != network.Address.AddressFamily) return false;
        var a = address.GetAddressBytes();
        var n = network.Address.GetAddressBytes();
        var bits = Math.Clamp(network.PrefixLength, 0, a.Length * 8);
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xff : (byte)(0xff << (8 - bits));
            if ((a[i] & mask) != (n[i] & mask)) return false;
        }
        return true;
    }

    /// <summary>The prefix length taken for an IPv4 interface address whose prefix the system can't give.</summary>
    public const int UnknownIPv4Prefix = 24;

    /// <summary>The prefix length taken for an IPv6 interface address whose prefix the system can't give.</summary>
    public const int UnknownIPv6Prefix = 64;

    /// <summary>
    /// This computer's interface addresses, each with its prefix length, as the system lists them now: every interface,
    /// up or down, IPv4 and IPv6 (<see cref="AddressesOf"/>). Throws when the list, or any interface's addresses, can't
    /// be read: a marked source's connection is then refused (<see cref="ConnectAsync"/>).
    /// </summary>
    public static IReadOnlyList<InterfaceAddress> ThisComputersAddresses() =>
        AddressesOf(NetworkInterface.GetAllNetworkInterfaces()
            .Select(nic => (Func<IEnumerable<UnicastIPAddressInformation>>)(() => nic.GetIPProperties().UnicastAddresses)));

    /// <summary>
    /// The addresses of <paramref name="interfaces"/> (each read by its function), with their prefix lengths: the one
    /// the system gives, else the IPv4 mask's, else <see cref="UnknownIPv4Prefix"/> or <see cref="UnknownIPv6Prefix"/>,
    /// so an address whose prefix is unknown still counts its likely network as on-link. An interface whose addresses
    /// can't be read isn't skipped: the read throws.
    /// </summary>
    public static IReadOnlyList<InterfaceAddress> AddressesOf(IEnumerable<Func<IEnumerable<UnicastIPAddressInformation>>> interfaces)
    {
        var list = new List<InterfaceAddress>();
        foreach (var addresses in interfaces)
            foreach (var unicast in addresses())
                list.Add(new InterfaceAddress(unicast.Address, PrefixOf(unicast)));
        return list;
    }

    private static int PrefixOf(UnicastIPAddressInformation unicast)
    {
        var v4 = unicast.Address.AddressFamily == AddressFamily.InterNetwork;
        var full = v4 ? 32 : 128;
        try
        {
            // No interface's network is the whole address space: 0 is a prefix not given.
            if (unicast.PrefixLength is var p && p > 0 && p <= full) return p;
        }
        catch (Exception e) when (e is PlatformNotSupportedException or NotImplementedException or NetworkInformationException)
        {
        }
        if (v4 && MaskLength(unicast) is { } mask) return mask;
        return v4 ? UnknownIPv4Prefix : UnknownIPv6Prefix;
    }

    /// <summary>The length of an IPv4 address's mask when the system gives a contiguous one other than 0.0.0.0.</summary>
    private static int? MaskLength(UnicastIPAddressInformation unicast)
    {
        IPAddress? mask;
        try
        {
            mask = unicast.IPv4Mask;
        }
        catch (Exception e) when (e is PlatformNotSupportedException or NotImplementedException or NetworkInformationException)
        {
            return null;
        }
        if (mask is null || mask.AddressFamily != AddressFamily.InterNetwork) return null;
        var bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(mask.GetAddressBytes());
        var length = System.Numerics.BitOperations.LeadingZeroCount(~bits);
        return length > 0 && bits == (length == 32 ? uint.MaxValue : ~(uint.MaxValue >> length)) ? length : null;
    }

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

    /// <summary>
    /// A provider's connection to <paramref name="host"/>:<paramref name="port"/>: resolves the host here
    /// (<c>localhost</c> and the names under it are this computer's addresses, never looked up; offline, no other name is
    /// looked up at all), and connects to the first of its addresses the rules allow (<see cref="Refusal"/>), so the
    /// address checked is the one connected to. A host none of whose addresses is allowed is refused, naming the first.
    /// With <paramref name="publicOnly"/> (a source marked to reach only public addresses) the setting doesn't apply: the
    /// interface list it gives is read once for this connection, and every address is held to <see cref="PublicRefusal"/>;
    /// a list that can't be read refuses the connection (<c>media.reach</c>).
    /// </summary>
    /// <exception cref="AddressRefusedException">No address of the host may be connected to.</exception>
    public static async ValueTask<Stream> ConnectAsync(string host, int port, Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect, bool localNetwork, bool loopbackOnly, CancellationToken token,
        Func<IReadOnlyList<InterfaceAddress>>? publicOnly = null)
    {
        var literal = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        var isLiteral = IPAddress.TryParse(literal, out var ip);
        // Offline, a name other than this computer's isn't even looked up: the lookup would reach the network.
        if (!isLiteral && loopbackOnly && !IsThisComputer(host))
            throw new AddressRefusedException($"{host} is outside this computer, and the engine runs offline.");
        var addresses = isLiteral ? [ip!]
            : IsThisComputer(host) ? [IPAddress.Loopback, IPAddress.IPv6Loopback]
            : await resolve(host, token).ConfigureAwait(false);
        // A marked source's connection: this computer's interfaces as they are now, read for this connection alone. A list
        // that can't be read whole refuses the connection: an inward address can't be told from a public one without it.
        IReadOnlyList<InterfaceAddress>? own = null;
        if (publicOnly is not null)
        {
            try
            {
                own = publicOnly();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new AddressRefusedException($"This computer's network interfaces couldn't be read{PublicOnlySuffix}", ReachCode);
            }
        }
        (string Code, string Message)? refusal = null;
        foreach (var address in addresses)
        {
            var why = own is not null ? PublicRefusal(address, own, loopbackOnly)
                : Refusal(address, localNetwork, loopbackOnly) is { } message ? (AddressCode, message) : null;
            if (why is null)
                return await connect(new IPEndPoint(address, port), token).ConfigureAwait(false);
            refusal ??= why;
        }
        throw refusal is { } first ? new AddressRefusedException(first.Message, first.Code) : new AddressRefusedException($"{host} has no address.");
    }

    /// <summary>
    /// The TLS options of a provider's connection to <paramref name="targetHost"/> (null: the handler's own host). A
    /// marked source's (<paramref name="publicOnly"/>) checks the server's certificate as usual but downloads nothing to
    /// do it: no missing issuer certificate is fetched from the address the certificate names, and no revocation list or
    /// status is fetched (those addresses aren't held to <see cref="PublicRefusal"/>, so may be inward). An unmarked
    /// source's keeps the platform's defaults. <paramref name="chainBuilt"/> (tests) is shown each certificate chain as
    /// built, and the usual validation decides.
    /// </summary>
    public static SslClientAuthenticationOptions TlsOptions(string? targetHost, bool publicOnly, Action<X509Chain?>? chainBuilt = null)
    {
        var options = new SslClientAuthenticationOptions { TargetHost = targetHost };
        if (publicOnly)
        {
            options.CertificateChainPolicy = new X509ChainPolicy
            {
                DisableCertificateDownloads = true,
                RevocationMode = X509RevocationMode.NoCheck,
            };
        }
        if (chainBuilt is not null)
        {
            options.RemoteCertificateValidationCallback = (_, _, chain, errors) =>
            {
                chainBuilt(chain);
                return errors == SslPolicyErrors.None;
            };
        }
        return options;
    }

    /// <summary><c>localhost</c> and the names under it (RFC 6761), which name this computer: 127.0.0.1 and ::1.</summary>
    public static bool IsThisComputer(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

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
