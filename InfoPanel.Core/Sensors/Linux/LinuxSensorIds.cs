using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;

namespace InfoPanel.Sensors.Linux;

/// <summary>Which kind of Linux sensor an id names.</summary>
public enum LinuxSensorFamily : byte
{
    /// <summary>A hwmon channel: <c>hwmon/v1/…</c>.</summary>
    Hwmon,

    /// <summary>A thermal zone: <c>thermal/v1/…</c>.</summary>
    Thermal,
}

/// <summary>
/// An anchor (what identifies a device: <c>platform+it87.2624</c>) or a secondary one (what tells apart devices with
/// one anchor: <c>pci+0000-01-00.0</c>): a kind and one or more values, held decoded; <see cref="ToString"/> gives it
/// encoded, as an id holds it. Two are equal when their kinds and values are.
/// </summary>
public sealed class LinuxAnchor : IEquatable<LinuxAnchor>
{
    public LinuxAnchor(string kind, params IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(tokens);
        Kind = kind;
        Tokens = tokens.ToArray();
    }

    /// <summary>The kind: <c>platform</c>, <c>pci</c>, <c>nvme-serial</c> …</summary>
    public string Kind { get; }

    /// <summary>The values, decoded (<c>SMBus I801 adapter</c>, <c>002d</c>).</summary>
    public IReadOnlyList<string> Tokens { get; }

    /// <summary><c>{kind}+{token}[+{token}…]</c>, each value encoded (<see cref="LinuxSensorIds.Encode"/>).</summary>
    public override string ToString() => Kind + string.Concat(Tokens.Select(t => "+" + LinuxSensorIds.Encode(t)));

    public bool Equals(LinuxAnchor? other) =>
        other is not null && Kind == other.Kind && Tokens.SequenceEqual(other.Tokens, StringComparer.Ordinal);

    public override bool Equals(object? obj) => obj is LinuxAnchor other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind, StringComparer.Ordinal);
        foreach (var token in Tokens)
            hash.Add(token, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A Linux sensor id taken apart (<see cref="LinuxSensorIds.TryParse"/>): its family, its chip (a hwmon id's, decoded;
/// null for a thermal zone), its anchor and secondary one, and its channel (<c>fan1</c>; a thermal zone's is
/// <c>temp</c>). <see cref="ToString"/> builds the id again.
/// </summary>
public sealed record LinuxSensorId(LinuxSensorFamily Family, string? Chip, LinuxAnchor Anchor, LinuxAnchor? Secondary, string Channel)
{
    public override string ToString() => Family == LinuxSensorFamily.Hwmon
        ? LinuxSensorIds.Hwmon(Chip!, Anchor, Secondary, Channel)
        : LinuxSensorIds.Thermal(Anchor, Secondary);
}

/// <summary>
/// The Linux sensor ids, the grammar of the Linux fork of InfoPanel 1.x, whose profiles bind them (re-implemented from
/// its published grammar; none of its code):
/// <list type="bullet">
/// <item><c>hwmon/v1/{chip}/{anchor}[~{secondary}]/{channel}</c>: a hwmon channel; <c>{chip}</c> is the device's
/// <c>name</c>, without the kernel's instance numbers (<c>iwlwifi_1</c> → <c>iwlwifi</c>); <c>{channel}</c> is
/// <c>temp</c>, <c>fan</c>, <c>in</c>, <c>curr</c>, <c>power</c>, <c>freq</c> or <c>humidity</c> and its number
/// (<c>0</c>, or with no leading zero);</item>
/// <item><c>thermal/v1/{anchor}[~{secondary}]/temp</c>: a thermal zone;</item>
/// <item>an anchor <c>{kind}+{token}[+{token}…]</c> of the kinds <see cref="AnchorKinds"/>, a secondary one of the
/// kinds <see cref="SecondaryKinds"/>;</item>
/// <item>every value (the chip, each token) percent-encoded (<see cref="Encode"/>).</item>
/// </list>
/// Ids are compared ordinally; parsing is strict, taking only the form a build gives.
/// </summary>
public static class LinuxSensorIds
{
    public const string HwmonPrefix = "hwmon/v1/";

    public const string ThermalPrefix = "thermal/v1/";

    /// <summary>The system readers' ids: <c>system/cpu/total</c>, <c>system/disk/{anchor}/read_speed</c>.</summary>
    public const string SystemPrefix = "system/";

    /// <summary>A thermal zone's one channel.</summary>
    public const string ThermalChannel = "temp";

    /// <summary>The anchors' kinds, from the strongest identity to the weakest.</summary>
    public static IReadOnlySet<string> AnchorKinds { get; } = new[]
    {
        "nvme-serial", "block-wwid", "block-serial", "gpu-uuid", "usb-serial", "usb-port", "pci", "platform", "type", "i2c", "name",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The secondary anchors' kinds.</summary>
    public static IReadOnlySet<string> SecondaryKinds { get; } = new[]
    {
        "pci", "acpi", "of", "port", "role", "adapter", "meta",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The hwmon channels' kinds, as the files name them (<c>fan1_input</c>).</summary>
    public static IReadOnlyList<string> ChannelKinds { get; } = ["temp", "fan", "in", "curr", "power", "freq", "humidity"];

    // Windows' reserved device names: a token whose name before its first dot is one has its first letter encoded.
    private static readonly FrozenSet<string> Reserved = new[]
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// A value as a token: its UTF-8 bytes outside <c>A–Z a–z 0–9 . _ -</c> as upper-case <c>%HH</c>; and, so a token
    /// is a safe file name everywhere, <c>.</c> and <c>..</c> wholly encoded, a trailing dot encoded, and a Windows
    /// device name (<c>CON</c>, <c>lpt1.x</c>) with its first letter encoded.
    /// </summary>
    public static string Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is "." or "..")
            return value.Length == 1 ? "%2E" : "%2E%2E";

        var bytes = Encoding.UTF8.GetBytes(value);
        var sb = new StringBuilder(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            var keep = IsUnreserved(b)
                && !(b == '.' && i == bytes.Length - 1)
                && !(i == 0 && IsReservedName(value));
            if (keep)
                sb.Append((char)b);
            else
                sb.Append('%').Append(HexDigit(b >> 4)).Append(HexDigit(b & 0xF));
        }

        return sb.ToString();
    }

    /// <summary>A token's value, when the token is exactly what <see cref="Encode"/> gives for it (a non-empty value).</summary>
    public static bool TryDecode(string token, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (string.IsNullOrEmpty(token))
            return false;

        var bytes = new List<byte>(token.Length);
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            if (c == '%')
            {
                if (i + 2 >= token.Length || !TryHex(token[i + 1], out var high) || !TryHex(token[i + 2], out var low))
                    return false;
                bytes.Add((byte)(high << 4 | low));
                i += 2;
            }
            else if (c < 0x80 && IsUnreserved((byte)c))
            {
                bytes.Add((byte)c);
            }
            else
            {
                return false;
            }
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (decoded.Length == 0 || Encode(decoded) != token)
            return false;
        value = decoded;
        return true;
    }

    /// <summary>A hwmon channel's id: <c>hwmon/v1/{chip}/{anchor}[~{secondary}]/{channel}</c>.</summary>
    /// <exception cref="ArgumentException">A part the grammar has no place for.</exception>
    public static string Hwmon(string chip, LinuxAnchor anchor, LinuxAnchor? secondary, string channel)
    {
        ArgumentException.ThrowIfNullOrEmpty(chip);
        if (!IsChannel(channel))
            throw new ArgumentException($"'{channel}' is no hwmon channel.", nameof(channel));
        return HwmonPrefix + Encode(chip) + "/" + Anchors(anchor, secondary) + "/" + channel;
    }

    /// <summary>A thermal zone's id: <c>thermal/v1/{anchor}[~{secondary}]/temp</c>.</summary>
    /// <exception cref="ArgumentException">A part the grammar has no place for.</exception>
    public static string Thermal(LinuxAnchor anchor, LinuxAnchor? secondary) =>
        ThermalPrefix + Anchors(anchor, secondary) + "/" + ThermalChannel;

    /// <summary>
    /// A device's metric among the system readers' ids: <c>system/{family}/{anchor}[~{secondary}]/{metric}</c>
    /// (<c>system/disk/nvme-serial+S5GXNX0T/read_speed</c>,
    /// <c>system/gpu/gpu-uuid+GPU-…~pci+0000-01-00.0/utilization</c>, <c>system/amdgpu/pci+0000-03-00.0/utilization</c>).
    /// </summary>
    /// <exception cref="ArgumentException">A part the grammar has no place for.</exception>
    public static string SystemDevice(string family, LinuxAnchor anchor, LinuxAnchor? secondary, string metric)
    {
        if (!IsName(family))
            throw new ArgumentException($"'{family}' is no system family.", nameof(family));
        if (!IsName(metric))
            throw new ArgumentException($"'{metric}' is no metric.", nameof(metric));
        return SystemPrefix + family + "/" + Anchors(anchor, secondary) + "/" + metric;
    }

    // A family's or a metric's name: lower-case letters, digits and '_' (fan0_rpm, amdgpu).
    private static bool IsName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
                return false;
        }

        return true;
    }

    /// <summary>Takes a hwmon or thermal id apart; false for anything else, or a form a build wouldn't give.</summary>
    public static bool TryParse([NotNullWhen(true)] string? id, [NotNullWhen(true)] out LinuxSensorId? parsed)
    {
        parsed = null;
        if (string.IsNullOrEmpty(id))
            return false;

        var parts = id.Split('/');
        LinuxSensorId? result = null;
        if (parts.Length == 5 && parts[0] == "hwmon" && parts[1] == "v1")
        {
            if (TryDecode(parts[2], out var chip) && TryAnchors(parts[3], out var anchor, out var secondary) && IsChannel(parts[4]))
                result = new LinuxSensorId(LinuxSensorFamily.Hwmon, chip, anchor, secondary, parts[4]);
        }
        else if (parts.Length == 4 && parts[0] == "thermal" && parts[1] == "v1" && parts[3] == ThermalChannel)
        {
            if (TryAnchors(parts[2], out var anchor, out var secondary))
                result = new LinuxSensorId(LinuxSensorFamily.Thermal, null, anchor, secondary, ThermalChannel);
        }

        parsed = result;
        return result != null;
    }

    /// <summary>Whether <paramref name="channel"/> is a hwmon channel: its kind and its number, <c>0</c> or with no leading zero.</summary>
    public static bool IsChannel(string? channel)
    {
        if (channel == null)
            return false;
        foreach (var kind in ChannelKinds)
        {
            if (channel.Length > kind.Length && channel.StartsWith(kind, StringComparison.Ordinal))
                return IsIndex(channel.AsSpan(kind.Length));
        }

        return false;
    }

    private static bool IsIndex(ReadOnlySpan<char> index)
    {
        if (index.Length == 0 || index.Length > 9 || (index[0] == '0' && index.Length > 1))
            return false;
        foreach (var c in index)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }

    private static string Anchors(LinuxAnchor anchor, LinuxAnchor? secondary)
    {
        Check(anchor, AnchorKinds, nameof(anchor));
        if (secondary == null)
            return anchor.ToString();
        Check(secondary, SecondaryKinds, nameof(secondary));
        return anchor + "~" + secondary;
    }

    private static void Check(LinuxAnchor anchor, IReadOnlySet<string> kinds, string name)
    {
        ArgumentNullException.ThrowIfNull(anchor, name);
        if (!kinds.Contains(anchor.Kind))
            throw new ArgumentException($"'{anchor.Kind}' is no kind of {name} the grammar has.", name);
        if (anchor.Tokens.Count == 0 || anchor.Tokens.Any(string.IsNullOrEmpty))
            throw new ArgumentException($"An {name} has one value or more, none empty.", name);
    }

    private static bool TryAnchors(string text, [NotNullWhen(true)] out LinuxAnchor? anchor, out LinuxAnchor? secondary)
    {
        anchor = secondary = null;
        var halves = text.Split('~');
        if (halves.Length > 2 || !TryAnchor(halves[0], AnchorKinds, out anchor))
            return false;
        if (halves.Length == 2 && !TryAnchor(halves[1], SecondaryKinds, out secondary))
        {
            anchor = null;
            return false;
        }

        return true;
    }

    private static bool TryAnchor(string text, IReadOnlySet<string> kinds, [NotNullWhen(true)] out LinuxAnchor? anchor)
    {
        anchor = null;
        var parts = text.Split('+');
        if (parts.Length < 2 || !kinds.Contains(parts[0]))
            return false;
        var tokens = new string[parts.Length - 1];
        for (var i = 1; i < parts.Length; i++)
        {
            if (!TryDecode(parts[i], out var value))
                return false;
            tokens[i - 1] = value;
        }

        anchor = new LinuxAnchor(parts[0], tokens);
        return true;
    }

    private static bool IsUnreserved(byte b) => b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'.' or (byte)'_' or (byte)'-';

    private static bool IsReservedName(string value)
    {
        var dot = value.IndexOf('.');
        return Reserved.Contains(dot < 0 ? value : value[..dot]);
    }

    private static char HexDigit(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);

    private static bool TryHex(char c, out int nibble)
    {
        // Upper case only: the form a build gives.
        nibble = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
        return nibble >= 0;
    }
}
