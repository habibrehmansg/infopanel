using System;
using System.Linq;
using System.Net;

namespace InfoPanel.Service;

/// <summary>
/// The configured service's address (the <c>service.address</c> setting, spec/docs/api.md "The configured service"): an
/// origin, <c>scheme://host[:port]</c> without a path, user info, query or fragment, <c>https</c>, or <c>http</c> on a
/// loopback host (<c>localhost</c>, 127.0.0.0/8, <c>[::1]</c> and its IPv4-mapped forms), where no network sits between
/// the engine and the service. Kept in lower case, without a default port.
/// </summary>
public static class ServiceAddress
{
    /// <summary>The longest address the engine takes, as given.</summary>
    public const int MaxLength = 2048;

    /// <summary>
    /// Whether <paramref name="value"/> is a service address, and its normal form: lower case, without a trailing
    /// <c>/</c> or a default port. Printable ASCII only; a path (beyond one trailing <c>/</c>), a query, a fragment, user
    /// info, a wildcard, a backslash or a percent sign (an escape, or an IPv6 zone) is refused.
    /// </summary>
    public static bool TryNormalize(string? value, out string address)
    {
        address = "";
        if (value is null || value.Length is 0 || value.Length > MaxLength)
            return false;
        if (value.Any(c => c <= 0x20 || c > 0x7E) || value.IndexOfAny(['*', '\\', '%', '@', '?', '#']) >= 0)
            return false;

        var authority = value.IndexOf("://", StringComparison.Ordinal);
        if (authority < 0)
            return false;

        // Uri would quietly drop dot segments (https://host/./ is "https://host/"), so a path is refused before parsing.
        var slash = value.IndexOf('/', authority + 3);
        if (slash >= 0 && slash != value.Length - 1)
            return false;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            return false;
        }

        if (uri.HostNameType == UriHostNameType.IPv6
            && (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var v6) || v6.ScopeId != 0))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(uri))
            return false;

        address = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        return true;
    }

    /// <summary>Whether <paramref name="value"/> is a service address already in its normal form.</summary>
    public static bool IsNormal(string? value) => TryNormalize(value, out var normal) && normal == value;

    /// <summary>Whether <paramref name="uri"/>'s host is this computer: <c>localhost</c>, 127.0.0.0/8, <c>[::1]</c> or an IPv4-mapped loopback.</summary>
    public static bool IsLoopbackHost(Uri uri)
    {
        if (uri.HostNameType == UriHostNameType.Dns)
            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address))
            return false;
        return IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }
}
