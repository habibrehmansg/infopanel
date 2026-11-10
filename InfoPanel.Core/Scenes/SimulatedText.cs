using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using InfoPanel.Enums;
using InfoPanel.Sensors;
using InfoPanel.Sensors.SystemInfo;

namespace InfoPanel.Scenes;

/// <summary>
/// What a simulated subscription shows for a text reading or a table cell (spec/docs/api.md, "Simulated sensors"): a
/// made-up value of the reading's kind, never this computer's own, but for the engine's clock. The kind is read from the
/// key's source first, then from heuristics on the sensor's display name (a cell's: its column's) and on the value's
/// shape (<see cref="Classify"/>). A heuristic only picks which made-up value shows: none ever lets the value through.
/// A reading none of them places shows its label (<see cref="Label"/>).
/// </summary>
internal static class SimulatedText
{
    /// <summary>What a reading shows.</summary>
    public enum Kind
    {
        /// <summary>This computer's own value: the engine's clock.</summary>
        Real,

        /// <summary>System Info's uptime, from a made-up start (<see cref="UptimeStart"/>) on.</summary>
        Uptime,

        /// <summary>A time of day: <see cref="SampleTime"/>.</summary>
        Time,
        Ipv4,
        Ipv6,
        Mac,
        ComputerName,
        Process,
        Adapter,
        Drive,
        AudioDevice,

        /// <summary>Its label (<see cref="Label"/>).</summary>
        Label,
    }

    /// <summary>A computer's name.</summary>
    public const string ComputerName = "MY-PC";

    /// <summary>The label of a reading whose display name isn't the engine's own (a plugin's), or that has none.</summary>
    public const string NoName = "Text";

    /// <summary>A time of day, with seconds when the reading has them.</summary>
    public const string SampleTime = "12:00", SampleTimeWithSeconds = "12:00:00";

    /// <summary>Where the made-up uptime starts: it moves on from here as time does.</summary>
    public static readonly TimeSpan UptimeStart = new(2, 7, 24, 13);

    /// <summary>Process and app names, a process table's rows in this order.</summary>
    public static IReadOnlyList<string> Processes { get; } =
        ["chrome", "explorer", "Code", "Spotify", "Steam", "Discord", "firefox", "Teams", "OneDrive", "Slack", "obs64", "notepad"];

    /// <summary>Network adapters' names, one for each adapter in turn.</summary>
    public static IReadOnlyList<string> Adapters { get; } = ["Ethernet", "Wi-Fi", "Ethernet 2", "Bluetooth"];

    /// <summary>Audio devices' names, one for each device in turn.</summary>
    public static IReadOnlyList<string> AudioDevices { get; } = ["Speakers", "Headphones", "Display Audio"];

    /// <summary>
    /// The units a made-up number shows (matched in any case, drawn in this list's spelling, <see cref="Unit"/>): a number
    /// reading's unit, and a number cell's in a table the engine names (<see cref="IsTrusted"/>). Any other unit shows
    /// as none, and a cell with any other text after its number is a text cell, so nothing but these follows a made-up
    /// number.
    /// </summary>
    public static IReadOnlySet<string> Units => UnitSpellings;

    private static readonly HashSet<string> UnitSpellings = new(
    [
        "%", "°", "°C", "°F", "C", "F", "K",
        "B", "KB", "MB", "GB", "TB", "KiB", "MiB", "GiB", "TiB",
        "B/s", "KB/s", "MB/s", "GB/s", "KiB/s", "MiB/s", "GiB/s", "bps", "Kbps", "Mbps", "Gbps", "GT/s", "MT/s",
        "Hz", "kHz", "MHz", "GHz", "RPM", "W", "mW", "kW", "Wh", "mWh", "kWh", "V", "mV", "A", "mA", "T",
        "ns", "µs", "ms", "s", "fps", "x", "dB", "dBA", "dBm", "L/h", "µS/cm", "Yes/No",
        "hPa", "inHg", "m/s", "km/h", "mph", "mm", "in",
    ], StringComparer.OrdinalIgnoreCase);

    private const string Clock = "infopanel.clock", SystemInfo = SystemInfoSource.SourceId, Network = "infopanel.network",
        Drives = "infopanel.drives", Volume = "infopanel.volume";

    /// <summary>The engine's own sources' keys (<c>infopanel:/…</c>): their ids start with the source id, <c>infopanel.…</c>, which no plugin may take.</summary>
    private const string OwnPrefix = "/infopanel.";

    private const RegexOptions Words = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex IpName = new(@"\bip(v4|v6)?\b", Words);
    private static readonly Regex MacName = new(@"\bmac\s*addr(ess)?\b|\bphysical\s+address\b|\bbssid\b", Words);
    private static readonly Regex HostName = new(@"\bhost\s*name\b|\b(computer|machine|pc|device|node)\s+name\b", Words);
    private static readonly Regex ProcessName = new(@"\b(process|processes|app|apps|application|applications|program|executable)\b", Words);
    private static readonly Regex AdapterName = new(@"\b(adapter|interface|nic|ethernet|wi-?fi|wlan|lan|ssid|network)\b", Words);
    private static readonly Regex DriveName = new(@"\b(drive|disk|partition|mount|volume\s+label)\b", Words);

    private static readonly Regex Ipv4Shape = new(@"^\d{1,3}(\.\d{1,3}){3}(/\d{1,2})?$", RegexOptions.CultureInvariant);
    private static readonly Regex MacShape = new(@"^[0-9A-Fa-f]{2}([:-])[0-9A-Fa-f]{2}(\1[0-9A-Fa-f]{2}){4}$", RegexOptions.CultureInvariant);

    /// <summary>A time of day or a time span: <c>6:37</c>, <c>14:05:33</c>, <c>2:05 PM</c>, <c>3:04:05:06</c>.</summary>
    private static readonly Regex TimeShape = new(@"^\d{1,3}(:\d{2}){1,3}([.,]\d{1,7})?(\s?[AaPp]\.?[Mm]\.?)?$", RegexOptions.CultureInvariant);

    /// <summary>A number with at most a short unit after it: a table's number cell (<c>12.3%</c>, <c>512.0 MB</c>) when the unit is one of <see cref="Units"/>.</summary>
    private static readonly Regex NumberShape = new(@"^(?<sign>[+-]?)(?<whole>\d{1,15})(?:(?<point>[.,])(?<fraction>\d{1,6}))?(?<unit>\s?[^\d\s.,:;][^\d.,:;]{0,5})?$", RegexOptions.CultureInvariant);

    private static readonly Lazy<string[]> ThisComputer = new(() =>
    {
        var names = new List<string> { Environment.MachineName };
        try
        {
            names.Add(Dns.GetHostName());
        }
        catch (SocketException)
        {
        }
        return names.SelectMany(n => new[] { n, n.Split('.')[0] }).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    });

    /// <summary>The engine's clock (<c>infopanel:/clock/…</c>), which stays real whatever its type: the only one.</summary>
    public static bool IsRealTime(SensorKey key) => Family(key) is { Source: Clock };

    /// <summary>
    /// A key of a source whose display names the engine writes itself: the engine's own sources (<c>infopanel:/…</c>:
    /// the network, drives, audio devices, weather, System Info, the clock and the platform's own) and the hardware
    /// monitors it reads (HWiNFO, LibreHardwareMonitor). A plugin's names are its own, as private as its values.
    /// </summary>
    public static bool IsTrusted(SensorKey key) => key.Type is SensorType.HwInfo or SensorType.Libre || Family(key) is not null;

    /// <summary>What a reading of <paramref name="key"/> named <paramref name="name"/> may show of its name: the name when the engine wrote it (<see cref="IsTrusted"/>), else <see cref="NoName"/>.</summary>
    public static string Label(SensorKey key, string name) => IsTrusted(key) && name.Trim().Length > 0 ? name : NoName;

    /// <summary>
    /// What a made-up number of <paramref name="key"/> shows of <paramref name="unit"/>: <see cref="Unit(string?)"/>, the
    /// whole list for a source whose names the engine writes (<see cref="IsTrusted"/>); for any other, only a listed unit
    /// of three characters or more or with a symbol (<c>%</c>, <c>°</c>, <c>/</c>, <c>µ</c>). A unit of one or two letters
    /// (<c>T</c>, <c>mm</c>, <c>in</c>) can be someone's initials, so it shows as none.
    /// </summary>
    public static string Unit(SensorKey key, string? unit)
    {
        var shown = Unit(unit);
        return IsTrusted(key) || Unmistakable(shown.TrimStart()) ? shown : "";
    }

    private static bool Unmistakable(string spelling) => spelling.Length >= 3 || spelling.IndexOfAny(['%', '°', '/', 'µ']) >= 0;

    /// <summary>
    /// What a made-up number shows of <paramref name="unit"/>, whoever wrote it: the unit of <see cref="Units"/> it names,
    /// in the list's spelling, after a space when it follows one; else none. The source's own spelling never shows.
    /// </summary>
    private static string Unit(string? unit)
    {
        var trimmed = unit?.Trim() ?? "";
        if (trimmed.Length == 0 || !UnitSpellings.TryGetValue(trimmed, out var spelling))
            return "";
        return char.IsWhiteSpace(unit![0]) ? " " + spelling : spelling;
    }

    /// <summary>
    /// What a text reading of <paramref name="key"/> shows: the key's source first (the clock; System Info's uptime; the
    /// network's addresses and its interfaces' id, name and description; a drive's name and volume label; an audio
    /// device's names; the engine's other texts their labels), then <see cref="Heuristic"/> on its display name
    /// <paramref name="name"/> and its value <paramref name="value"/>.
    /// </summary>
    public static Kind Classify(SensorKey key, string name, string value)
    {
        if (Family(key) is { } f)
        {
            switch (f.Source)
            {
                case Clock:
                    return Kind.Real;
                case SystemInfo when f.Container == "uptime":
                    return Kind.Uptime;
                case Network when f.Entry.StartsWith("ipv4", StringComparison.Ordinal):
                    return Kind.Ipv4;
                case Network when f.Entry.StartsWith("ipv6", StringComparison.Ordinal):
                    return Kind.Ipv6;
                case Network when f.Entry is "id" or "name" or "description":
                    return Kind.Adapter;
                case Drives when f.Entry is "name" or "volume_label":
                    return Kind.Drive;
                case Volume when f.Entry is "device_friendly_name" or "friendly_name" or "short_name":
                    return Kind.AudioDevice;
            }
        }
        return Heuristic(name, value);
    }

    /// <summary>
    /// The heuristics, in order: a value shaped as an IPv4 or IPv6 address (or a list of them), a MAC address, this
    /// computer's name or a time (<see cref="TimeShape"/>); then the display name: an IP address (<see cref="IpName"/>),
    /// a MAC address (<see cref="MacName"/>), a host or computer name (<see cref="HostName"/>), a process or app
    /// (<see cref="ProcessName"/>), a network adapter (<see cref="AdapterName"/>), a drive (<see cref="DriveName"/>);
    /// else the label. Each only picks the made-up value shown.
    /// </summary>
    public static Kind Heuristic(string name, string value)
    {
        var trimmed = value.Trim();
        if (IsAddressList(trimmed, out var v6))
            return v6 ? Kind.Ipv6 : Kind.Ipv4;
        if (MacShape.IsMatch(trimmed))
            return Kind.Mac;
        if (IsThisComputer(trimmed))
            return Kind.ComputerName;
        if (TimeShape.IsMatch(trimmed))
            return Kind.Time;
        if (IpName.IsMatch(name))
            return trimmed.Contains(':') ? Kind.Ipv6 : Kind.Ipv4;
        if (MacName.IsMatch(name))
            return Kind.Mac;
        if (HostName.IsMatch(name))
            return Kind.ComputerName;
        if (ProcessName.IsMatch(name))
            return Kind.Process;
        if (AdapterName.IsMatch(name))
            return Kind.Adapter;
        if (DriveName.IsMatch(name))
            return Kind.Drive;
        return Kind.Label;
    }

    /// <summary>
    /// The value <paramref name="kind"/> shows: the <paramref name="index"/>th address, name or sample of its kind (from
    /// 0); a time with seconds when <paramref name="real"/> has them; <paramref name="label"/> for <see cref="Kind.Label"/>.
    /// Never <paramref name="real"/>: <see cref="Kind.Real"/> and <see cref="Kind.Uptime"/> are read apart.
    /// </summary>
    public static string Value(Kind kind, int index, string label, string real) => kind switch
    {
        Kind.Time => real.Count(c => c == ':') >= 2 ? SampleTimeWithSeconds : SampleTime,
        Kind.Ipv4 => string.Create(CultureInfo.InvariantCulture, $"192.0.2.{10 + index % 240}"),
        Kind.Ipv6 => string.Create(CultureInfo.InvariantCulture, $"2001:db8::{10 + index % 0xFFF0:x}"),
        Kind.Mac => string.Create(CultureInfo.InvariantCulture, $"00:00:5E:00:53:{(0x10 + index) % 256:X2}"),
        Kind.ComputerName => ComputerName,
        Kind.Process => Processes[index % Processes.Count],
        Kind.Adapter => Cycle(Adapters, index, "Ethernet"),
        Kind.Drive => string.Create(CultureInfo.InvariantCulture, $"Disk {index + 1}"),
        Kind.AudioDevice => Cycle(AudioDevices, index, "Audio Device"),
        _ => label,
    };

    /// <summary>
    /// System Info's uptime text <paramref name="key"/> names (formatted, days, hours, minutes or seconds, as the source
    /// writes them) for an uptime of <see cref="UptimeStart"/> plus <paramref name="elapsed"/>; its label for an entry it
    /// doesn't know.
    /// </summary>
    public static string Uptime(SensorKey key, string label, TimeSpan elapsed)
    {
        var (formatted, days, hours, minutes, seconds) = SystemInfoSource.UptimeTexts(UptimeStart + elapsed);
        return Family(key)?.Entry switch
        {
            "formatted" => formatted,
            "days" => days,
            "hours" => hours,
            "minutes" => minutes,
            "seconds" => seconds,
            _ => label,
        };
    }

    /// <summary>
    /// What scopes a sample's turn: the source's container (an adapter, a drive, an audio device) for its texts, so a
    /// network interface's name and description show the same adapter; null for a key of its own.
    /// </summary>
    public static string? Scope(SensorKey key, Kind kind) =>
        kind is Kind.Adapter or Kind.Drive or Kind.AudioDevice && Family(key) is { Source: Network or Drives or Volume } f ? f.Source + "/" + f.Container : null;

    /// <summary>
    /// A made-up table over <paramref name="real"/>'s columns and rows. Its headings are the columns' names when the engine
    /// wrote them (<see cref="IsTrusted"/>), else <c>Column 1</c>, <c>Column 2</c>…. A number cell shows a made-up number
    /// in its decimals and unit (<see cref="Unit"/>), from the top row down, that moves each <paramref name="step"/>: in
    /// a table the engine names, a number with at most a unit of <see cref="Units"/>; in any other, whose cells' types
    /// the engine doesn't know, a number alone (a value's shape never makes <c>42 A</c> a number: <c>A</c> may be an
    /// initial). Any other cell shows a text reading of its column's (<see cref="Heuristic"/> on the real column's name,
    /// its label the heading), the <c>n</c>th row the <c>n</c>th sample. A table with a column of processes or apps keeps
    /// at most <see cref="Processes"/>' rows, so none repeats.
    /// </summary>
    public static SceneTable Table(SensorKey key, SceneTable real, long step)
    {
        var columns = real.Columns;
        var trusted = IsTrusted(key);
        var headings = columns.Select((c, i) => trusted ? c : string.Create(CultureInfo.InvariantCulture, $"Column {i + 1}")).ToArray();
        var kinds = new Kind?[columns.Count];
        var rows = real.Rows.Count;
        for (var c = 0; c < columns.Count; c++)
        {
            var sample = real.Rows.Select(r => c < r.Count ? r[c] : "").FirstOrDefault(v => v.Trim().Length > 0) ?? "";
            kinds[c] = Number(sample, trusted).Success ? null : Heuristic(columns[c], sample);
            if (kinds[c] == Kind.Process)
                rows = Math.Min(rows, Processes.Count);
        }

        var made = new List<IReadOnlyList<string>>(rows);
        for (var r = 0; r < rows; r++)
        {
            var row = real.Rows[r];
            var cells = new string[row.Count];
            for (var c = 0; c < row.Count; c++)
            {
                var cell = row[c];
                var column = c < columns.Count ? columns[c] : "";
                var heading = c < headings.Length ? headings[c] : NoName;
                if (cell.Trim().Length == 0)
                    cells[c] = "";
                else if (Number(cell, trusted) is { Success: true } number)
                    cells[c] = MadeUpNumber(key, c, r, step, number, real, trusted);
                else
                    cells[c] = Value(kinds.ElementAtOrDefault(c) ?? Heuristic(column, cell), r, Label(heading), cell);
            }
            made.Add(cells);
        }
        return new SceneTable(headings, made);
    }

    /// <summary>A table's text: its first row's cells, as a table reading's (the 1.x plugins' summary); <c>-</c> without rows.</summary>
    public static string Summary(SceneTable table) => table.Rows.Count > 0 ? string.Join(", ", table.Rows[0]) : "-";

    /// <summary>
    /// A number cell: a number alone, or, in a table the engine names (<paramref name="trusted"/>), a number with one of
    /// <see cref="Units"/> after it; else no match.
    /// </summary>
    private static Match Number(string cell, bool trusted)
    {
        var match = NumberShape.Match(cell.Trim());
        if (!match.Success)
            return Match.Empty;
        var unit = match.Groups["unit"];
        return !unit.Success || trusted && UnitSpellings.Contains(unit.Value.Trim()) ? match : Match.Empty;
    }

    /// <summary>
    /// A made-up number for a cell: its column's top value from the order of the column's first number (1 to 9 times it,
    /// seeded from the key and column; a percentage up to 90), a third less a row down, moved by up to 10 % each step;
    /// in the list's spelling of its unit (<see cref="Unit"/>).
    /// </summary>
    private static string MadeUpNumber(SensorKey key, int column, int row, long step, Match number, SceneTable real, bool trusted)
    {
        var unit = Unit(number.Groups["unit"].Value);
        var decimals = number.Groups["fraction"].Length;
        var point = number.Groups["point"].Success ? number.Groups["point"].Value : ".";

        var scale = 1.0;
        var top = real.Rows.Count > 0 && column < real.Rows[0].Count ? Number(real.Rows[0][column], trusted) : Match.Empty;
        if (top.Success && double.TryParse(top.Groups["whole"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var whole) && whole >= 10)
            scale = Math.Pow(10, Math.Floor(Math.Log10(whole)));
        var seed = new Mulberry32(StableHash.Fnv1a(string.Create(CultureInfo.InvariantCulture, $"{key}#{column}")));
        var start = unit.Trim() == "%" ? 10 + seed.NextDouble() * 80 : scale * (1 + seed.NextDouble() * 8);
        var jitter = new Mulberry32(StableHash.Fnv1a(string.Create(CultureInfo.InvariantCulture, $"{key}#{column}#{row}#{step}")));
        var value = start * Math.Pow(2.0 / 3, row) * (0.9 + jitter.NextDouble() * 0.2);
        var text = Math.Round(value, decimals).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return number.Groups["sign"].Value + (point == "." ? text : text.Replace(".", point, StringComparison.Ordinal)) + unit;
    }

    private static string Label(string name) => name.Trim().Length > 0 ? name : NoName;

    private static string Cycle(IReadOnlyList<string> samples, int index, string more) =>
        index < samples.Count ? samples[index] : string.Create(CultureInfo.InvariantCulture, $"{more} {index + 1}");

    /// <summary>The source (by its id, <c>infopanel.network</c>), container and entry of a key of one of the engine's own sources: <c>infopanel:/{source}/{container}/{entry}</c>.</summary>
    private static (string Source, string Container, string Entry)? Family(SensorKey key)
    {
        if (key.Type != SensorType.Plugin || !key.Id.StartsWith(OwnPrefix, StringComparison.Ordinal))
            return null;
        var parts = key.Id.Split('/');
        return parts.Length switch
        {
            3 => (parts[1], "", parts[2]),
            >= 4 => (parts[1], parts[2], string.Join('/', parts.Skip(3))),
            _ => null,
        };
    }

    /// <summary>An IPv4 or IPv6 address, with a prefix length or zone, or a list of them; <paramref name="v6"/> when the first is IPv6.</summary>
    private static bool IsAddressList(string value, out bool v6)
    {
        v6 = false;
        var parts = value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            bool isV6;
            if (Ipv4Shape.IsMatch(part))
                isV6 = false;
            else if (part.Count(c => c == ':') >= 2 && IPAddress.TryParse(part.Split('/')[0], out var address) && address.AddressFamily == AddressFamily.InterNetworkV6)
                isV6 = true;
            else
                return false;
            if (i == 0)
                v6 = isV6;
        }
        return true;
    }

    private static bool IsThisComputer(string value) =>
        value.Length > 0 && ThisComputer.Value.Any(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
}
