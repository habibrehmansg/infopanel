using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace InfoPanel.Sensors.Network;

/// <summary>
/// Reads the network for <see cref="NetworkSource"/>: every round lists the interfaces afresh
/// (<see cref="INetworkLister"/>), leaves out loopback, and reads each one's id, name, description, status, speed (whole
/// Mbps; none, negative or zero, or unreadable fails, "—") and unicast addresses by position per family; with a lookup
/// (<see cref="PublicIpLookup"/>, none under <c>--offline</c>), the public addresses first, read from its last result
/// (never waited on), and listed only while lookups are on (<see cref="PublicIpLookup.Enabled"/>): the first round after
/// they are turned off reads them unavailable (their last reading dropped, not kept stale) and unlists them, and the
/// first round after any turn off reads them so, even when they are back on by then, so an address forgotten is never
/// read again. The catalogue is compared with the last one every round (<see cref="ISensorSampler.CatalogueVersion"/>),
/// so an interface or address that appears is listed after the round that saw it and read at the next, and one that
/// goes fails (stale, then unavailable, then unlisted, as <see cref="PolledSensorSource"/> does). When the interfaces,
/// their status or their addresses change from one round to the next, the lookup is told (<see cref="PublicIpLookup.NetworkChanged"/>).
/// <para>
/// Bounded: at most <see cref="MaxInterfaces"/> interfaces (the rest left out unread, one warning) and
/// <see cref="MaxAddresses"/> addresses per family each; texts cut at <see cref="MaxTextChars"/> characters; an interface
/// whose longest key (its <c>description</c>'s) would be longer than <see cref="MaxKeyChars"/>, whose id makes no
/// interface id, or makes the public addresses' (<c>public</c>), is left out. An interface whose read throws is left out
/// of that round alone. Each is logged once while it stands; a listing that throws fails the interfaces' readings for the
/// round (the public addresses still read, and listed or unlisted as the switch says, a first listing's failure
/// included; the interfaces' catalogue stands).
/// </para>
/// <para>
/// An interface id belongs to the interface (by its system id) that took it first: while the system lists that one
/// (its read failing included), however late a round comes, and, once it has gone, until its readings have run out
/// (stale, then unavailable: <see cref="OwnerHold"/>, counted from the end of the last round that read it, when its
/// readings were published, after the round's warnings were written, not from when that round began); another interface whose id makes the same one is left out
/// (logged once while it stands), whatever the order the system lists them in, and takes it only after that, as an
/// interface appearing does. A speed that turns unknown after a good read is unavailable at once
/// (<see cref="SampleOutcome.Unavailable"/>, committed with the round's other readings), not the last speed gone stale.
/// </para>
/// </summary>
public sealed class NetworkSampler : ISensorSampler
{
    /// <summary>At most this many interfaces are listed.</summary>
    public const int MaxInterfaces = 64;

    /// <summary>At most this many addresses of each family are listed per interface.</summary>
    public const int MaxAddresses = 16;

    /// <summary>A text is cut at this many characters.</summary>
    public const int MaxTextChars = 256;

    /// <summary>An interface whose keys would be longer than this (a document's sensor key's bound) is left out.</summary>
    public const int MaxKeyChars = 512;

    /// <summary>An interface's longest entry id, <c>description</c>: its key is the interface's longest.</summary>
    internal static readonly string LongestEntry = NetworkSource.Entries.Select(e => e.Id)
        .Append($"ipv4-{MaxAddresses - 1}").Append($"ipv6-{MaxAddresses - 1}").MaxBy(id => id.Length)!;

    /// <summary>How long a gone interface keeps its interface id by default: its readings run out (unavailable after 30 s), and one round more.</summary>
    public static readonly TimeSpan DefaultOwnerHold = NetworkSource.DefaultOptions.UnavailableAfter + NetworkSource.DefaultOptions.Interval;

    private enum Field : byte
    {
        Id,
        Name,
        Description,
        Status,
        Speed,
        IPv4,
        IPv6,
        PublicIPv4,
        PublicIPv6,
    }

    private sealed record Interface(string Container, string Group, string[] Texts, double Speed, string[] V4, string[] V6, string Signature);

    /// <summary>The interface (by system id) an interface id belongs to, and the timestamp of the end of the last round that read it.</summary>
    private sealed record Owner(string SystemId, long LastSeen);

    private readonly INetworkLister _lister;
    private readonly PublicIpLookup? _lookup;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Owner> _owners = new(StringComparer.Ordinal);

    private SensorDefinition[] _definitions = [];
    // Each handed-out definition's interface id (empty for the public addresses), field and address index.
    private (string Container, Field Field, int Index)[] _handedOut = [];
    // This round's interfaces by interface id; null when the listing failed.
    private Dictionary<string, Interface>? _read;
    private string? _signature;
    private long _version;
    private bool _rounded;

    // This round's public addresses: listed (lookups on), and read unavailable (off, or turned off since the last round).
    private bool _publicOn;
    private bool _publicForget;
    private long _publicGeneration;

    private bool _listFailed;
    private bool _tooMany;
    private HashSet<string> _failing = [];
    private HashSet<string> _clashing = [];
    private HashSet<string> _refused = [];

    /// <param name="lister">The interfaces: <see cref="SystemNetworkLister"/>, or a test's.</param>
    /// <param name="lookup">The public addresses' lookup; null lists none (<c>--offline</c>).</param>
    /// <param name="log">Where it logs; Serilog's global logger but in tests.</param>
    /// <param name="ownerHold">How long a gone interface keeps its interface id (<see cref="OwnerHold"/>); <see cref="DefaultOwnerHold"/> when null.</param>
    /// <param name="time">The clock the hold is measured on: the polled source's; the system's when null.</param>
    public NetworkSampler(INetworkLister lister, PublicIpLookup? lookup, ILogger? log = null, TimeSpan? ownerHold = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(lister);
        _lister = lister;
        _lookup = lookup;
        _log = (log ?? Log.Logger).ForContext<NetworkSampler>();
        _time = time ?? TimeProvider.System;
        OwnerHold = ownerHold ?? DefaultOwnerHold;
    }

    /// <summary>
    /// How long, after the end of the last round that read it, a gone interface keeps its interface id from another whose
    /// id makes the same: longer than its readings take to run out, so they go stale, then unavailable, and are never
    /// another's. An interface the system still lists keeps it, whatever the time.
    /// </summary>
    public TimeSpan OwnerHold { get; }

    public long CatalogueVersion => _version;

    public IReadOnlyList<SensorDefinition> Discover()
    {
        if (!_rounded)
            Refresh();
        _handedOut = _definitions.Select(d => FieldOf(d.Id)).ToArray();
        return _definitions;
    }

    public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
    {
        Refresh();
        var read = _read;
        for (var i = 0; i < samples.Length; i++)
        {
            if (i >= _handedOut.Length)
            {
                samples[i].Outcome = SampleOutcome.Failed;
                continue;
            }

            var (container, field, index) = _handedOut[i];
            if (field is Field.PublicIPv4 or Field.PublicIPv6)
            {
                if (_publicForget)
                {
                    samples[i].SetUnavailable();
                    continue;
                }

                var result = _lookup!.Latest(field == Field.PublicIPv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6);
                if (result.Answered)
                    samples[i].SetText(result.Address);
                else
                    samples[i].Outcome = SampleOutcome.Failed;
                continue;
            }

            if (read == null || !read.TryGetValue(container, out var ni))
            {
                samples[i].Outcome = SampleOutcome.Failed;
                continue;
            }

            switch (field)
            {
                case Field.Speed when double.IsNaN(ni.Speed):
                    // Unknown now: the last speed goes in this round, not stale.
                    samples[i].SetUnavailable();
                    break;
                case Field.Speed:
                    samples[i].Set(ni.Speed);
                    break;
                case Field.IPv4 when index < ni.V4.Length:
                    samples[i].SetText(ni.V4[index]);
                    break;
                case Field.IPv6 when index < ni.V6.Length:
                    samples[i].SetText(ni.V6[index]);
                    break;
                case Field.IPv4 or Field.IPv6:
                    samples[i].Outcome = SampleOutcome.Failed;
                    break;
                default:
                    samples[i].SetText(ni.Texts[(int)field]);
                    break;
            }
        }
    }

    public void Dispose()
    {
    }

    private static (string, Field, int) FieldOf(string definitionId)
    {
        var slash = definitionId.IndexOf('/');
        var container = definitionId[..slash];
        var entry = definitionId[(slash + 1)..];
        if (container == NetworkSource.PublicContainer)
            return ("", entry == "ipv6" ? Field.PublicIPv6 : Field.PublicIPv4, 0);
        if (entry.StartsWith("ipv4-", StringComparison.Ordinal))
            return (container, Field.IPv4, int.Parse(entry[5..], System.Globalization.CultureInfo.InvariantCulture));
        if (entry.StartsWith("ipv6-", StringComparison.Ordinal))
            return (container, Field.IPv6, int.Parse(entry[5..], System.Globalization.CultureInfo.InvariantCulture));
        return (container, entry switch
        {
            "id" => Field.Id,
            "name" => Field.Name,
            "description" => Field.Description,
            "status" => Field.Status,
            "speed" => Field.Speed,
            _ => throw new InvalidOperationException($"No network entry '{entry}'"),
        }, 0);
    }

    /// <summary>One round: the interfaces listed and read once, the catalogue updated, a change of network told.</summary>
    private void Refresh()
    {
        _rounded = true;
        if (_lookup != null)
        {
            _publicOn = _lookup.Enabled;
            var generation = _lookup.Generation;
            _publicForget = !_publicOn || generation != _publicGeneration;
            _publicGeneration = generation;
        }

        IReadOnlyList<INetworkInterface> interfaces;
        try
        {
            interfaces = _lister.List();
            if (_listFailed)
                _log.Information("Network: the interfaces are listed again");
            _listFailed = false;
        }
        catch (Exception ex)
        {
            if (!_listFailed)
                _log.Warning(ex, "Network: listing the interfaces failed: {Message}", ex.Message);
            _listFailed = true;
            _read = null;
            // The interfaces' catalogue stands; the public addresses follow the switch whatever the listing does (a
            // first listing that fails included).
            Commit([.. _definitions.Where(d => !IsPublic(d.Id))]);
            return;
        }

        var candidates = new List<(string Id, Interface Ni)>();
        var failing = new HashSet<string>(StringComparer.Ordinal);
        var refused = new HashSet<string>(StringComparer.Ordinal);
        var tooMany = false;
        foreach (var n in interfaces)
        {
            if (n == null)
                continue;

            if (candidates.Count == MaxInterfaces)
            {
                tooMany = true;
                if (!_tooMany)
                    _log.Warning("Network: at most {Max} interfaces are listed; the rest are left out unread", MaxInterfaces);
                break;
            }

            string? id = null;
            Interface ni;
            try
            {
                id = n.Id;
                if (id == null || n.InterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                ni = ReadInterface(n, id, _read);
            }
            catch (Exception ex)
            {
                var which = id ?? "";
                failing.Add(which);
                if (!_failing.Contains(which))
                    _log.Warning(ex, "Network: reading the interface {Interface} failed: {Message}", id ?? "(no id)", ex.Message);
                continue;
            }

            if (ni.Container.Length == 0 || ni.Container == NetworkSource.PublicContainer
                || NetworkSource.KeyOf(ni.Container, LongestEntry).ToString().Length > MaxKeyChars)
            {
                refused.Add(id);
                if (!_refused.Contains(id))
                    _log.Warning("Network: the interface {Interface} is left out: its id makes no interface id of its own, or keys longer than {Max} characters", ni.Texts[0], MaxKeyChars);
                continue;
            }

            candidates.Add((id, ni));
        }

        // The interfaces are read: whether a gone owner's hold is over is judged from here, not from when the round
        // began (a slow listing would otherwise eat into it).
        var observed = _time.GetTimestamp();
        // Every system id listed this round, an interface whose read failed included: an owner listed keeps its id.
        var present = new HashSet<string>(failing, StringComparer.Ordinal);
        foreach (var (id, _) in candidates)
            present.Add(id);

        // Interface ids: an interface takes its own, or one no other interface owns (or owned: while listed, then until
        // the hold ends), in the order listed; the first of two with one system id takes it.
        var read = new Dictionary<string, Interface>(StringComparer.Ordinal);
        var listed = new List<(string Id, Interface Ni)>();
        var clashing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, ni) in candidates)
        {
            if (!read.ContainsKey(ni.Container)
                && (!_owners.TryGetValue(ni.Container, out var owner) || owner.SystemId == id || Released(owner, present, observed)))
            {
                read.Add(ni.Container, ni);
                listed.Add((id, ni));
                continue;
            }

            clashing.Add(id);
            if (!_clashing.Contains(id))
                _log.Warning("Network: the interface {Interface} has the interface id '{Container}' of another interface, which keeps it, and is left out", ni.Texts[0], ni.Container);
        }

        // The round's blocking work is done (a clash's warning is written synchronously, and a slow log sink can take
        // seconds): its readings are published as it ends, so the owners' holds are renewed from now, not from before
        // the warnings.
        var published = _time.GetTimestamp();
        foreach (var (id, ni) in listed)
            _owners[ni.Container] = new Owner(id, published);
        foreach (var (container, owner) in _owners.ToArray())
        {
            if (Released(owner, present, published))
                _owners.Remove(container);
        }

        _failing = failing;
        _clashing = clashing;
        _refused = refused;
        _tooMany = tooMany;
        _read = read;
        Publish(listed.ConvertAll(l => l.Ni));

        var signature = string.Concat(listed.Select(l => l.Ni.Signature));
        if (_signature != null && signature != _signature)
            _lookup?.NetworkChanged();
        _signature = signature;
    }

    /// <summary>Whether <paramref name="owner"/> has let its interface id go: not listed this round, and gone for longer than the hold.</summary>
    private bool Released(Owner owner, HashSet<string> present, long observed) =>
        !present.Contains(owner.SystemId) && _time.GetElapsedTime(owner.LastSeen, observed) > OwnerHold;

    /// <summary>A listed interface's texts, speed and addresses, as the 1.x plugin read them.</summary>
    /// <remarks>A text unchanged since <paramref name="last"/>'s round is handed back as the same string.</remarks>
    private static Interface ReadInterface(INetworkInterface n, string id, Dictionary<string, Interface>? last)
    {
        var container = NetworkSource.ContainerOf(id);
        var name = Cut(n.Name ?? "");
        var status = n.Status;
        var texts = new[] { Cut(id), name, Cut(n.Description ?? ""), status.ToString() };
        double speed;
        try
        {
            var bits = n.Speed;
            // Whole Mbps, as 1.x (long division). The system has none (-1 on Windows and Linux, 0 on macOS, which
            // reports no speed for most interfaces) or can't say: unavailable, not 1.x's 0.
            speed = bits <= 0 ? double.NaN : bits / 1000 / 1000;
        }
        catch (Exception)
        {
            speed = double.NaN;
        }

        var addresses = n.Addresses ?? [];
        var v4 = Addresses(addresses, AddressFamily.InterNetwork);
        var v6 = Addresses(addresses, AddressFamily.InterNetworkV6);
        if (last != null && last.TryGetValue(container, out var before))
        {
            Reuse(texts, before.Texts);
            Reuse(v4, before.V4);
            Reuse(v6, before.V6);
        }

        var signature = new StringBuilder().Append(container).Append('|').Append(status == OperationalStatus.Up ? '1' : '0');
        foreach (var a in v4)
            signature.Append('|').Append(a);
        foreach (var a in v6)
            signature.Append('|').Append(a);
        signature.Append(';');
        return new Interface(container, name.Length > 0 ? name : texts[0], texts, speed, v4, v6, signature.ToString());
    }

    private static string[] Addresses(IReadOnlyList<IPAddress> addresses, AddressFamily family) =>
        addresses.Where(a => a != null && a.AddressFamily == family).Take(MaxAddresses).Select(a => Cut(a.ToString())).ToArray();

    private static void Reuse(string[] now, string[] before)
    {
        for (var i = 0; i < now.Length && i < before.Length; i++)
        {
            if (now[i] == before[i])
                now[i] = before[i];
        }
    }

    private static string Cut(string text) =>
        text.Length <= MaxTextChars ? text : text[..(char.IsHighSurrogate(text[MaxTextChars - 1]) ? MaxTextChars - 1 : MaxTextChars)];

    private static bool IsPublic(string definitionId) =>
        definitionId.StartsWith(NetworkSource.PublicContainer + "/", StringComparison.Ordinal);

    private void Publish(List<Interface> interfaces)
    {
        var definitions = new List<SensorDefinition>();
        foreach (var ni in interfaces)
        {
            foreach (var (id, name, kind) in NetworkSource.Entries)
                definitions.Add(new SensorDefinition($"{ni.Container}/{id}", ni.Group, name, kind == SensorKind.Number ? NetworkSource.SpeedUnit : "", kind));
            for (var i = 0; i < ni.V4.Length; i++)
                definitions.Add(new SensorDefinition($"{ni.Container}/ipv4-{i}", ni.Group, NetworkSource.IPv4Name, "", SensorKind.Text));
            for (var i = 0; i < ni.V6.Length; i++)
                definitions.Add(new SensorDefinition($"{ni.Container}/ipv6-{i}", ni.Group, NetworkSource.IPv6Name, "", SensorKind.Text));
        }

        Commit(definitions);
    }

    /// <summary>The catalogue: the public addresses first, while lookups are on, then <paramref name="interfaces"/>' definitions.</summary>
    private void Commit(List<SensorDefinition> interfaces)
    {
        var definitions = new List<SensorDefinition>();
        if (_publicOn)
        {
            foreach (var (id, name) in NetworkSource.PublicEntries)
                definitions.Add(new SensorDefinition($"{NetworkSource.PublicContainer}/{id}", NetworkSource.PublicGroup, name, "", SensorKind.Text));
        }

        definitions.AddRange(interfaces);
        if (definitions.SequenceEqual(_definitions))
            return;
        _definitions = [.. definitions];
        _version++;
    }
}
