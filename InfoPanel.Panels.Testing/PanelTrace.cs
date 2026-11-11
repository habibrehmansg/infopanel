using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Testing;

/// <summary>What a traced call was.</summary>
public enum PanelTraceOp
{
    /// <summary>A handle opened (<see cref="PanelTraceEntry.Target"/>: <c>usb if0</c>, <c>hid hid0</c> (the HID collection's id; <c>hid</c> alone for an open that found none), <c>serial 115200 dtr rts</c>).</summary>
    Open,
    /// <summary>A handle disposed (<see cref="PanelTraceEntry.Target"/>: <c>usb if0</c>, <c>hid</c>, <c>serial</c>).</summary>
    Close,
    /// <summary>A bulk write (<see cref="PanelTraceEntry.Target"/>: the endpoint, <c>ep01</c>); a zero-length packet after a full write is one more, with no bytes.</summary>
    UsbWrite,
    /// <summary>A bulk read (<see cref="PanelTraceEntry.Target"/>: the endpoint, <c>ep81</c>), with the bytes it read.</summary>
    UsbRead,
    HidWrite,
    HidRead,
    HidSetFeature,
    HidGetFeature,
    SerialWrite,
    SerialRead,
    SerialDiscard,
}

/// <summary>
/// One call on a device: what it was, its target (see <see cref="PanelTraceOp"/>; empty for HID and serial transfers),
/// its bytes (what was written, or what was read: none for a read that failed), what it came to, the
/// <see cref="Handle"/> it was made on, and for an open the <see cref="Device"/> it opened when the trace names it.
/// Entries compare by their bytes.
/// </summary>
public sealed record PanelTraceEntry(PanelTraceOp Op, string Target, byte[] Data, PanelDeviceStatus Status = PanelDeviceStatus.Ok)
{
    /// <summary>
    /// The handle the call was made on: each open in a trace (failed ones too) takes the next number from 1, and every
    /// transfer and the close on the handle it gave carry it, so the calls of two handles (two panels on one endpoint,
    /// say) are told apart.
    /// </summary>
    public int Handle { get; init; }

    /// <summary>For an open, the device it opened, as the trace's comment line before it names it (null: none named; always null for any other call).</summary>
    public string? Device { get; init; }

    public bool Equals(PanelTraceEntry? other) =>
        other is not null && Op == other.Op && Target == other.Target && Status == other.Status && Handle == other.Handle
        && Device == other.Device && Data.AsSpan().SequenceEqual(other.Data);

    public override int GetHashCode() => HashCode.Combine(Op, Target, Status, Handle, Data.Length);

    public override string ToString() => PanelTrace.FormatLine(this);
}

/// <summary>
/// The calls made on a device, in order, and their text form: one line a call, which a family's tests write by hand
/// (from 1.x's call order) and a tester's hardware run records. Each line starts with the handle the call was made on
/// (<c>h1</c>, <c>h2</c> …: each open takes the next number, failed ones too), then the call:
/// <code>
/// open usb if0 | open hid hid0 | open serial 115200 [dtr] [rts]  a handle opened (open hid: the collection's id)
/// close usb if0 | close hid | close serial                       a handle disposed
/// > ep01 0a0b…   &lt; ep81 0a0b…                                   a bulk write, a bulk read (zlp: no bytes)
/// hid> …   hid&lt; …   hid-feature> …   hid-feature&lt; …            HID reports and feature reports
/// serial> …   serial&lt; …   serial discard                         a serial port
/// </code>
/// Bytes are lower-case hexadecimal, unspaced; a call that came to anything but <see cref="PanelDeviceStatus.Ok"/> ends
/// with <c>!</c> and the status (<c>!timeout</c>, <c>!gone</c>, <c>!notSupported</c>). A line starting <c>#</c>, and a
/// blank line, isn't a call; the last <c>#</c> line before an open (with no call between) names the device it opened
/// (<see cref="PanelTraceEntry.Device"/>: <c># 1234:5678 at 1-2 (bus 1, address 7)</c>, as the recorder writes it).
/// Thread-safe.
/// </summary>
public sealed class PanelTrace
{
    private readonly List<PanelTraceEntry> _entries = [];

    /// <summary>The calls so far, in order (a copy).</summary>
    public IReadOnlyList<PanelTraceEntry> Entries
    {
        get
        {
            lock (_entries)
                return [.. _entries];
        }
    }

    /// <summary>Raised after each call is added, on the caller's thread.</summary>
    public event Action<PanelTraceEntry>? Added;

    public void Add(PanelTraceEntry entry)
    {
        lock (_entries)
            _entries.Add(entry);
        Added?.Invoke(entry);
    }

    /// <summary>Every call, a line each, each line ending <c>\n</c>; an open that names its device has that device's comment line before it.</summary>
    public string Format()
    {
        var text = new StringBuilder();
        foreach (var entry in Entries)
            text.Append(FormatLines(entry));
        return text.ToString();
    }

    public override string ToString() => Format();

    /// <summary>One call's lines, each ending <c>\n</c>: the device's comment line before an open that names one, then the call's line.</summary>
    public static string FormatLines(PanelTraceEntry entry)
    {
        var device = entry.Op == PanelTraceOp.Open && entry.Device is { } named ? DeviceLine(named) : "";
        return device + FormatLine(entry) + "\n";
    }

    /// <summary>A device's comment line, ending <c>\n</c> (a line end in the name is a space).</summary>
    public static string DeviceLine(string device) => "# " + device.ReplaceLineEndings(" ").Trim() + "\n";

    /// <summary>One call's line, without its line end (and without its device's comment).</summary>
    public static string FormatLine(PanelTraceEntry entry)
    {
        var line = new StringBuilder();
        line.Append('h').Append(entry.Handle.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
        switch (entry.Op)
        {
            case PanelTraceOp.Open:
                line.Append("open ").Append(entry.Target);
                break;
            case PanelTraceOp.Close:
                line.Append("close ").Append(entry.Target);
                break;
            case PanelTraceOp.UsbWrite or PanelTraceOp.UsbRead:
                line.Append(entry.Op == PanelTraceOp.UsbWrite ? "> " : "< ").Append(entry.Target);
                // A failed read has no bytes and shows none; any other transfer of none is a zero-length packet.
                if (entry.Data.Length > 0)
                    line.Append(' ').Append(Convert.ToHexStringLower(entry.Data));
                else if (entry.Op == PanelTraceOp.UsbWrite || entry.Status == PanelDeviceStatus.Ok)
                    line.Append(" zlp");
                break;
            case PanelTraceOp.SerialDiscard:
                line.Append("serial discard");
                break;
            default:
                line.Append(Prefix(entry.Op));
                if (entry.Data.Length > 0)
                    line.Append(' ').Append(Convert.ToHexStringLower(entry.Data));
                break;
        }
        if (entry.Status != PanelDeviceStatus.Ok)
            line.Append(" !").Append(StatusName(entry.Status));
        return line.ToString();
    }

    /// <summary>The calls a trace's text holds; <see cref="FormatException"/>, naming the line, for a line that isn't one.</summary>
    public static IReadOnlyList<PanelTraceEntry> Parse(string text)
    {
        var entries = new List<PanelTraceEntry>();
        var number = 0;
        string? comment = null; // the last comment since the last call
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.StartsWith('#'))
                comment = line[1..].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var entry = ParseLine(line) ?? throw new FormatException($"line {number} isn't a call: {line}");
            entries.Add(entry.Op == PanelTraceOp.Open && !string.IsNullOrEmpty(comment) ? entry with { Device = comment } : entry);
            comment = null;
        }
        return entries;
    }

    private static PanelTraceEntry? ParseLine(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count < 2 || HandleOf(words[0]) is not { } handle)
            return null;
        words.RemoveAt(0);
        return ParseCall(words) is { } call ? call with { Handle = handle } : null;
    }

    /// <summary>The highest handle a trace's text names at the start of a line (0: none); any other line is passed over.</summary>
    internal static int HighestHandle(string text) =>
        text.Split('\n').Select(line => line.TrimStart().Split(' ', 2)[0]).Select(HandleOf).Max() ?? 0;

    private static int? HandleOf(string word) =>
        word.Length is >= 2 and <= 10 && word[0] == 'h' && word.Skip(1).All(char.IsAsciiDigit)
        && int.TryParse(word.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var handle)
            ? handle : null;

    private static PanelTraceEntry? ParseCall(List<string> words)
    {
        var status = PanelDeviceStatus.Ok;
        if (words[^1].StartsWith('!'))
        {
            if (StatusOf(words[^1][1..]) is not { } known || known == PanelDeviceStatus.Ok)
                return null;
            status = known;
            words.RemoveAt(words.Count - 1);
        }
        if (words.Count == 0)
            return null;

        switch (words[0])
        {
            case "open" or "close":
                {
                    var op = words[0] == "open" ? PanelTraceOp.Open : PanelTraceOp.Close;
                    var target = string.Join(' ', words.Skip(1));
                    return IsTarget(op, words.Skip(1).ToList()) ? new PanelTraceEntry(op, target, [], status) : null;
                }
            case ">" or "<":
                {
                    if (words.Count is < 2 or > 3 || !IsEndpoint(words[1]))
                        return null;
                    var op = words[0] == ">" ? PanelTraceOp.UsbWrite : PanelTraceOp.UsbRead;
                    byte[]? data = words.Count == 2 ? [] : words[2] == "zlp" ? [] : Hex(words[2]);
                    return data == null ? null : new PanelTraceEntry(op, words[1], data, status);
                }
            case "serial" when words.Count == 2 && words[1] == "discard":
                return new PanelTraceEntry(PanelTraceOp.SerialDiscard, "", [], status);
            default:
                {
                    if (OpOf(words[0]) is not { } op || words.Count > 2)
                        return null;
                    var data = words.Count == 1 ? [] : Hex(words[1]);
                    return data == null ? null : new PanelTraceEntry(op, "", data, status);
                }
        }
    }

    private static bool IsTarget(PanelTraceOp op, List<string> words)
    {
        if (words.Count == 0)
            return false;
        return words[0] switch
        {
            "usb" => words.Count == 2 && words[1].StartsWith("if", StringComparison.Ordinal) && int.TryParse(words[1].AsSpan(2), out var n) && n is >= 0 and <= 255,
            "hid" when op == PanelTraceOp.Close => words.Count == 1,
            "hid" => words.Count is 1 or 2,
            "serial" when op == PanelTraceOp.Close => words.Count == 1,
            "serial" => words.Count >= 2 && int.TryParse(words[1], out _) && words.Skip(2).SequenceEqual(words.Skip(2).Where(w => w is "dtr" or "rts").Distinct()),
            _ => false,
        };
    }

    private static bool IsEndpoint(string word) =>
        word.Length == 4 && word.StartsWith("ep", StringComparison.Ordinal) && Hex(word[2..]) is not null;

    private static byte[]? Hex(string word)
    {
        if (word.Length % 2 != 0 || word.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return null;
        return Convert.FromHexString(word);
    }

    private static string Prefix(PanelTraceOp op) => op switch
    {
        PanelTraceOp.HidWrite => "hid>",
        PanelTraceOp.HidRead => "hid<",
        PanelTraceOp.HidSetFeature => "hid-feature>",
        PanelTraceOp.HidGetFeature => "hid-feature<",
        PanelTraceOp.SerialWrite => "serial>",
        PanelTraceOp.SerialRead => "serial<",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };

    private static PanelTraceOp? OpOf(string prefix) => prefix switch
    {
        "hid>" => PanelTraceOp.HidWrite,
        "hid<" => PanelTraceOp.HidRead,
        "hid-feature>" => PanelTraceOp.HidSetFeature,
        "hid-feature<" => PanelTraceOp.HidGetFeature,
        "serial>" => PanelTraceOp.SerialWrite,
        "serial<" => PanelTraceOp.SerialRead,
        _ => null,
    };

    /// <summary>A status as a trace writes it: the enum's name, its first letter lower-case (<c>notSupported</c>).</summary>
    public static string StatusName(PanelDeviceStatus status)
    {
        var name = status.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static PanelDeviceStatus? StatusOf(string name) =>
        Enum.GetValues<PanelDeviceStatus>().Select(s => (PanelDeviceStatus?)s).FirstOrDefault(s => StatusName(s!.Value) == name);
}
