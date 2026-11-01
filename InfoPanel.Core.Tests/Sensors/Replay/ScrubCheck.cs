using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using InfoPanel.Sensors.HwInfo;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Replay;

/// <summary>
/// What a scrubbed HWiNFO image may hold, checked independently of the scrubber: only the retained fields (the header,
/// each sensor's id, instance and original name, each reading's type, sensor index, id, original label, units and
/// values), every other byte zero (the user's names, partial fields, extensions, gaps, the tail of each string), and no
/// string that looks identifying (a serial number, whole or cut off by its field's end, an address, a default Windows
/// machine name, a host other than an alias, a name the test gives). It is no catalogue of approved strings: the
/// recorder replaces this PC's and this user's names as it records, and the committed frames are read by hand at each
/// change.
/// </summary>
internal static class ScrubCheck
{
    private static readonly Regex Alias = new(@"^(?:SERIAL\d+|PC\d+|IP\d+|MAC\d+|THISPC|THISUSER)$");
    private static readonly Regex Parenthesised = new(@"\(([A-Za-z0-9][A-Za-z0-9._-]{5,})\)");
    // A run the string ends inside: the field (a 16-byte unit, a 128-byte name) cut off its closing parenthesis.
    private static readonly Regex ParenthesisedToTheEnd = new(@"\(([A-Za-z0-9][A-Za-z0-9._-]{5,})$");
    private static readonly Regex AfterSerial = new(@"\b(?:S/?N|Serial(?:\s*(?:Number|No\.?))?)\s*[:#]?\s*([A-Za-z0-9][A-Za-z0-9._-]{5,})", RegexOptions.IgnoreCase);
    private static readonly Regex Ipv4 = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b");
    private static readonly Regex Ipv6 = new(@"(?<![0-9A-Za-z])[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}(?![0-9A-Za-z])");
    private static readonly Regex Mac = new(@"\b[0-9A-Fa-f]{2}([:-])(?:[0-9A-Fa-f]{2}\1){4}[0-9A-Fa-f]{2}\b");
    private static readonly Regex HostPrefix = new(@"^\[([^\]]+)\] ");
    // Windows' default machine names: DESKTOP-, LAPTOP- (client) and WIN- (server) and a random run.
    private static readonly Regex DefaultMachineName = new(@"(?<![0-9A-Za-z])(?:DESKTOP|LAPTOP|WIN)-[0-9A-Za-z]+(?![0-9A-Za-z])", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> HostAliases = new(StringComparer.Ordinal) { "PC1", "PC2", "THISPC" };

    /// <summary>
    /// Why <paramref name="text"/> looks identifying (empty when it doesn't): a serial-like token, an address, one of
    /// Windows' default machine names, a bracketed host other than <c>[PC1]</c>, <c>[PC2]</c> and <c>[THISPC]</c>, or
    /// any of <paramref name="names"/> (a test's machine, user and remote names) as a whole word, in any case.
    /// </summary>
    public static List<string> Problems(string text, IReadOnlyCollection<string>? names = null)
    {
        var problems = new List<string>();
        foreach (var regex in new[] { Parenthesised, ParenthesisedToTheEnd, AfterSerial })
        {
            foreach (Match m in regex.Matches(text))
            {
                var token = m.Groups[1].Value;
                if (token.Any(char.IsDigit) && !Alias.IsMatch(token))
                    problems.Add($"serial-like '{token}'");
            }
        }

        if (Ipv4.IsMatch(text))
            problems.Add("an IPv4 address");
        if (Ipv6.Matches(text).Any(m => m.Value.Any(Uri.IsHexDigit)))
            problems.Add("an IPv6 address");
        if (Mac.IsMatch(text))
            problems.Add("a MAC address");
        if (HostPrefix.Match(text) is { Success: true } host && !HostAliases.Contains(host.Groups[1].Value))
            problems.Add($"the host '{host.Groups[1].Value}'");
        foreach (Match m in DefaultMachineName.Matches(text))
            problems.Add($"the machine name '{m.Value}'");
        foreach (var name in names ?? [])
        {
            if (!string.IsNullOrEmpty(name) && Regex.IsMatch(text, @"(?<![0-9A-Za-z])" + Regex.Escape(name) + @"(?![0-9A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                problems.Add($"the name '{name}'");
        }

        return problems;
    }

    /// <summary>Every string field of a scrubbed image, named, decoded as the reader decodes it.</summary>
    public static List<(string Field, string Text)> Strings(byte[] image)
    {
        var h = Header(image);
        var strings = new List<(string, string)>();
        for (var i = 0; i < h.SensorCount; i++)
            strings.Add(($"sensor {i} name", HwInfoLayout.SensorNameOf(HwInfoLayout.SensorElement(image, h, i))));
        for (var i = 0; i < h.ReadingCount; i++)
        {
            var e = HwInfoLayout.ReadingElement(image, h, i);
            strings.Add(($"reading {i} label", HwInfoLayout.ReadingNameOf(e)));
            strings.Add(($"reading {i} unit", HwInfoLayout.Windows1252(e.Slice(268, 16))));
            if (e.Length >= HwInfoLayout.ReadingSizeWithUtf8)
                strings.Add(($"reading {i} UTF-8 unit", HwInfoLayout.Utf8(e.Slice(444, 16))));
        }

        return strings;
    }

    /// <summary>
    /// Fails unless every string field reads clean (<see cref="Problems"/>, with <paramref name="names"/>) and every byte
    /// outside the retained fields is zero.
    /// </summary>
    public static void AssertScrubbed(byte[] image, string name, IReadOnlyCollection<string>? names = null)
    {
        foreach (var (field, text) in Strings(image))
            Assert.True(Problems(text, names).Count == 0, $"{name}, {field} '{text}': {string.Join(", ", Problems(text, names))}");

        var h = Header(image);
        var retained = new bool[image.Length];
        var hasPeriod = h.SensorOffset >= HwInfoLayout.HeaderSizeWithPeriod && h.ReadingOffset >= HwInfoLayout.HeaderSizeWithPeriod;
        Mark(retained, 0, hasPeriod ? HwInfoLayout.HeaderSizeWithPeriod : HwInfoLayout.HeaderSize);
        var strings = new List<(int At, int Length)>();
        for (var i = 0; i < h.SensorCount; i++)
        {
            var at = (int)(h.SensorOffset + (long)h.SensorSize * i);
            Mark(retained, at, 8);
            strings.Add((at + 8, 128));
        }

        for (var i = 0; i < h.ReadingCount; i++)
        {
            var at = (int)(h.ReadingOffset + (long)h.ReadingSize * i);
            Mark(retained, at, 12);
            strings.Add((at + 12, 128));
            strings.Add((at + 268, 16));
            Mark(retained, at + 284, 32);
            if (h.ReadingSize >= HwInfoLayout.ReadingSizeWithUtf8)
                strings.Add((at + 444, 16));
        }

        // A string field is retained up to its NUL; what follows it is zero.
        foreach (var (at, length) in strings)
        {
            var end = image.AsSpan(at, length).IndexOf((byte)0);
            Mark(retained, at, end < 0 ? length : end);
        }

        for (var i = 0; i < image.Length; i++)
            Assert.True(retained[i] || image[i] == 0, $"{name}: byte {i} is 0x{image[i]:x2}, outside the retained fields");
    }

    private static HwInfoHeader Header(byte[] image)
    {
        Assert.Equal(HwInfoImageState.Ok, HwInfoLayout.ReadHeader(image, image.Length, out var h, out var problem));
        Assert.Null(problem);
        return h;
    }

    private static void Mark(bool[] retained, int at, int length) => retained.AsSpan(at, length).Fill(true);

    /// <summary>
    /// An image with element sizes and gaps HWiNFO 8.54 doesn't write: every byte starts as <c>Z</c> (private-looking),
    /// then the fields a reader reads are written, so whatever a scrub leaves of the rest shows.
    /// </summary>
    public static byte[] OddImage(int sensorSize, int readingSize, int gapAfterHeader, int gapBetween, int tail, string machine)
    {
        var so = 48 + gapAfterHeader;
        var ro = so + sensorSize * 2 + gapBetween;
        var image = new byte[ro + readingSize * 2 + tail];
        Array.Fill(image, (byte)'Z');
        var span = image.AsSpan();
        Encoding.ASCII.GetBytes("HWiS").CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 1);
        BinaryPrimitives.WriteInt64LittleEndian(span[12..], 1_793_508_823);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)so);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)sensorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], (uint)ro);
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..], (uint)readingSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], 2000);
        for (var i = 0; i < 2; i++)
        {
            var e = span.Slice(so + sensorSize * i, sensorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(e, 0xF0000300u + (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], 0);
            Text(e.Slice(8, 128), $"Drive {i} on {machine} (0000FAKE{i}234)");
        }

        for (var i = 0; i < 2; i++)
        {
            var e = span.Slice(ro + readingSize * i, readingSize);
            BinaryPrimitives.WriteUInt32LittleEndian(e, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], 0x1000000u + (uint)i);
            Text(e.Slice(12, 128), $"Temperature {i}");
            Text(e.Slice(268, 16), "C");
            BinaryPrimitives.WriteDoubleLittleEndian(e[284..], 40 + i);
            BinaryPrimitives.WriteDoubleLittleEndian(e[292..], 30);
            BinaryPrimitives.WriteDoubleLittleEndian(e[300..], 90);
            BinaryPrimitives.WriteDoubleLittleEndian(e[308..], 45);
            if (readingSize >= 460)
                Text(e.Slice(444, 16), "C");
        }

        return image;
    }

    /// <summary>ASCII text and its NUL, the rest of the field left as it was.</summary>
    private static void Text(Span<byte> field, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        bytes.CopyTo(field);
        field[bytes.Length] = 0;
    }
}
