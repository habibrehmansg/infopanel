using InfoPanel.Models;
using InfoPanel.Sensors.Drives;
using InfoPanel.Settings;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.Network;

/// <summary>
/// The network source, <c>infopanel.network</c> ("Network"): a <see cref="PolledSensorSource"/> over
/// <see cref="NetworkSampler"/>, polled every 5 s (the 1.x Network Info plugin's interval), stale after 15 s, the
/// interfaces and their addresses followed as they come and go. Each interface lists the 1.x plugin's entries
/// (<see cref="Entries"/>, then <c>ipv4-{i}</c> and <c>ipv6-{i}</c> by position) under
/// <c>plugin:/infopanel.network/{interface}/{entry}</c>, its interface id the 1.x plugin SDK's id of the system's
/// interface id (<see cref="ContainerOf"/>: <c>en0</c> → <c>en0</c>, a Windows adapter's GUID without its braces and
/// dashes); with a lookup (none under <c>--offline</c>), the public addresses first, <c>plugin:/infopanel.network/public/ipv4</c>
/// and <c>…/ipv6</c>, from <see cref="PublicIpLookup"/>. A read of a public address (<see cref="ISensorReader.TryRead"/>
/// or <see cref="ISensorStatusSource.ReadState"/>, as a shown profile's and a client's value reads are; a status alone
/// isn't one, nor is a peek (<see cref="PeekState"/>, the sensors' listing), nor is the history's own sampling, which never reads a text: <see cref="SensorHistory"/>) puts it in use
/// (<see cref="PublicIpLookup.NoteUse"/>), the only time the lookup asks. The engine never
/// answers the plugin's keys; a 1.x import binds them to these (<see cref="RetiredPlugins"/>).
/// <para>
/// The setting <c>sources.network.publicIp</c> (<see cref="FollowSettings"/>, <see cref="SetPublicIp"/>) turns the
/// lookup off and on as the engine runs. Off, at once: nothing more is asked (a lookup in flight cancelled), the
/// addresses are forgotten, and the two are unlisted and read unavailable, as a key no source lists (a read still puts
/// them in use, so turning it on again asks at once when a shown profile binds one). On: they are listed again from the
/// next round, and read nothing until a round after the switch has read the lookup's result, so an address read before
/// it was turned off is never read again.
/// </para>
/// </summary>
public sealed class NetworkSource : ISensorSource, ISensorStatusSource, ISensorSourceState, IDisposable
{
    public const string SourceId = "infopanel.network";

    /// <summary>The source's name.</summary>
    public const string Name = "Network";

    /// <summary>The unit of the speed: whole Mbps, as 1.x (bits a second ÷ 1,000,000, rounded down).</summary>
    public const string SpeedUnit = "Mbps";

    /// <summary>The public addresses' interface id, and their group.</summary>
    public const string PublicContainer = "public";

    public const string PublicGroup = "Public IP";

    public const string IPv4Name = "IPv4 Address";

    public const string IPv6Name = "IPv6 Address";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>The source's ages: polled every 5 s, stale after three intervals (so values don't flicker stale between rounds), unavailable after 30 s.</summary>
    public static PolledSensorSourceOptions DefaultOptions => new()
    {
        Interval = Interval,
        StaleAfter = TimeSpan.FromSeconds(15),
        UnavailableAfter = TimeSpan.FromSeconds(30),
    };

    private static readonly SensorKeySpace Space = SensorKeySpace.Plugin(SourceId);

    /// <summary>Each interface's fixed entries in the order it lists them (the 1.x plugin's): id, name and kind; the speed in <see cref="SpeedUnit"/>. Its addresses follow.</summary>
    public static IReadOnlyList<(string Id, string Name, SensorKind Kind)> Entries { get; } =
    [
        ("id", "Id", SensorKind.Text),
        ("name", "Name", SensorKind.Text),
        ("description", "Description", SensorKind.Text),
        ("status", "Status", SensorKind.Text),
        ("speed", "Speed", SensorKind.Number),
    ];

    /// <summary>The public addresses' entries: id and name (texts).</summary>
    public static IReadOnlyList<(string Id, string Name)> PublicEntries { get; } =
    [
        ("ipv4", "IPv4"),
        ("ipv6", "IPv6"),
    ];

    /// <summary>The key of the entry <paramref name="entryId"/> of the interface <paramref name="container"/>: <c>plugin:/infopanel.network/{container}/{entryId}</c>.</summary>
    public static SensorKey KeyOf(string container, string entryId) => Space.KeyOf(container + "/" + entryId);

    /// <summary>The public address's key: <c>plugin:/infopanel.network/public/ipv4</c> or <c>…/ipv6</c>.</summary>
    public static SensorKey PublicKeyOf(string entryId) => KeyOf(PublicContainer, entryId);

    /// <summary>
    /// An interface's interface id: the 1.x plugin SDK's id (<see cref="DriveSource.Slug"/>) of the system's interface id
    /// (the 1.x plugin used that id as it was: the SDK's rule keeps keys to <c>a</c>–<c>z</c>, <c>0</c>–<c>9</c> and <c>-</c>).
    /// </summary>
    public static string ContainerOf(string interfaceId) => DriveSource.Slug(interfaceId);

    /// <summary>
    /// The name of an interface's entry <paramref name="entryId"/>: a fixed one, or <c>ipv4-{i}</c> and <c>ipv6-{i}</c>
    /// for <c>i</c> from 0 below <see cref="NetworkSampler.MaxAddresses"/>, written as 1.x wrote it (no leading zero);
    /// null when an interface has no such entry.
    /// </summary>
    public static string? EntryName(string entryId)
    {
        foreach (var (id, name, _) in Entries)
        {
            if (string.Equals(id, entryId, StringComparison.Ordinal))
                return name;
        }

        if (entryId.Length is >= 6 and <= 7 && (entryId.StartsWith("ipv4-", StringComparison.Ordinal) || entryId.StartsWith("ipv6-", StringComparison.Ordinal)))
        {
            var number = entryId[5..];
            if ((number.Length == 1 || number[0] != '0') && int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i)
                && i < NetworkSampler.MaxAddresses)
                return entryId[3] == '4' ? IPv4Name : IPv6Name;
        }

        return null;
    }

    private readonly PolledSensorSource _polled;
    private readonly PublicIpLookup? _lookup;
    // One start or stop at a time, across the polling and the lookup.
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SensorKey _publicV4 = PublicKeyOf("ipv4");
    private readonly SensorKey _publicV6 = PublicKeyOf("ipv6");
    private readonly Lock _switch = new();
    // The public addresses are read from the polled source once it has polled this many rounds (two after the switch turned on).
    private long _shownFrom;
    private ListedWithoutPublic? _withoutPublic;
    private Action? _unfollow;

    /// <summary>A listing, and the same without the public addresses, kept while the listing stands.</summary>
    private sealed record ListedWithoutPublic(IReadOnlyList<SensorInfo> Listed, IReadOnlyList<SensorInfo> Without);

    /// <param name="lister">The interfaces: <see cref="SystemNetworkLister"/>, or a test's.</param>
    /// <param name="lookup">The public addresses' lookup, started and stopped (and disposed) with the source; null lists none (<c>--offline</c>).</param>
    public NetworkSource(INetworkLister lister, PublicIpLookup? lookup, TimeProvider? time = null, PolledSensorSourceOptions? options = null, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(lister);
        _lookup = lookup;
        var ages = options ?? DefaultOptions;
        // A gone interface keeps its interface id until its readings have run out, and one round more.
        var ownerHold = ages.UnavailableAfter + ages.Interval;
        _polled = new PolledSensorSource(SourceId, () => new NetworkSampler(lister, lookup, log, ownerHold, time), ages, time, log: log);
    }

    /// <summary>
    /// The source over this computer's interfaces; under <c>--offline</c> (<paramref name="offline"/>) no lookup and no
    /// public addresses. With <paramref name="settings"/>, its lookup follows their <c>sources.network.publicIp</c>.
    /// </summary>
    public static NetworkSource CreateSystem(TimeProvider? time, bool offline, SettingsStore? settings = null)
    {
        var source = new NetworkSource(new SystemNetworkLister(), offline ? null : PublicIpLookup.CreateSystem(time), time);
        return settings == null ? source : source.FollowSettings(settings);
    }

    public string Id => SourceId;

    /// <summary>Whether the public addresses are looked up and listed: false under <c>--offline</c> and while the setting is off.</summary>
    public bool PublicIp => _lookup is { Enabled: true };

    /// <summary>
    /// Turns the public address lookup off or on, at once (the setting <c>sources.network.publicIp</c>); nothing under
    /// <c>--offline</c>, which has no lookup.
    /// </summary>
    public void SetPublicIp(bool on)
    {
        if (_lookup == null)
            return;
        lock (_switch)
        {
            if (_lookup.Enabled == on)
                return;
            // Before the lookup turns on: the round under way may hold what was read before the switch went off, the next doesn't.
            if (on)
                Interlocked.Exchange(ref _shownFrom, _polled.Polls + 2);
            _lookup.SetEnabled(on);
        }
    }

    /// <summary>
    /// Follows <paramref name="settings"/>' <c>sources.network.publicIp</c>: applied now, and at once on every change,
    /// until the source is disposed. Under <c>--offline</c> there is nothing to follow.
    /// </summary>
    public NetworkSource FollowSettings(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_lookup == null)
            return this;
        void Follow(SettingsSnapshot snapshot) => SetPublicIp(snapshot.Settings.Network.PublicIp);
        settings.Changed += Follow;
        _unfollow = () => settings.Changed -= Follow;
        SetPublicIp(settings.Current.Settings.Network.PublicIp);
        return this;
    }

    /// <summary>The polled source underneath. For tests.</summary>
    internal PolledSensorSource Polled => _polled;

    /// <summary>The public addresses' lookup; null under <c>--offline</c>.</summary>
    public PublicIpLookup? Lookup => _lookup;

    public SourceState State => _polled.State;

    public bool Handles(SensorKey key) => _polled.Handles(key);

    /// <summary>The polled source's sensors; while the lookup is off, without the public addresses (until the sampler has unlisted them too).</summary>
    public IReadOnlyList<SensorInfo> GetSensors()
    {
        var listed = _polled.GetSensors();
        if (_lookup is not { Enabled: false })
            return listed;
        var kept = Volatile.Read(ref _withoutPublic);
        if (kept != null && ReferenceEquals(kept.Listed, listed))
            return kept.Without;
        var without = listed.Where(s => s.Key != _publicV4 && s.Key != _publicV6).ToArray();
        Volatile.Write(ref _withoutPublic, new ListedWithoutPublic(listed, without));
        return without;
    }

    public bool TryRead(SensorKey key, out SensorReading reading)
    {
        if (IsPublic(key))
        {
            _lookup!.NoteUse();
            if (Hidden())
            {
                reading = default;
                return false;
            }
        }

        return _polled.TryRead(key, out reading);
    }

    public SensorState ReadState(SensorKey key)
    {
        if (IsPublic(key))
        {
            _lookup!.NoteUse();
            if (Hidden())
                return SensorState.Without(SensorStatus.Unavailable);
        }

        return _polled.ReadState(key);
    }

    /// <summary>A peek isn't a read: <see cref="ReadState"/> without putting a public address in use (listing the sensors).</summary>
    public SensorState PeekState(SensorKey key) =>
        IsPublic(key) && Hidden() ? SensorState.Without(SensorStatus.Unavailable) : _polled.ReadState(key);

    /// <summary>A status isn't a read: listing the sensors (<c>/api/sensors</c>) or importing puts no public address in use.</summary>
    public SensorStatus GetStatus(SensorKey key) => IsPublic(key) && Hidden() ? SensorStatus.Unavailable : _polled.GetStatus(key);

    private bool IsPublic(SensorKey key) => _lookup != null && (key == _publicV4 || key == _publicV6);

    // The public addresses read nothing: the lookup is off, or on again before a round after the switch has read it.
    private bool Hidden() => !_lookup!.Enabled || _polled.Polls < Interlocked.Read(ref _shownFrom);

    /// <summary>Starts the polling (discovery first), then the lookup; a stop asked meanwhile waits for both to have started.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _polled.StartAsync(ct).ConfigureAwait(false);
            _lookup?.Start();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops the lookup (a lookup in flight cancelled) and the polling, and waits for both; after a start under way has
    /// ended, so nothing it starts outlives the stop.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lookup != null)
                await _lookup.StopAsync(ct).ConfigureAwait(false);
            await _polled.StopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Dispose()
    {
        _unfollow?.Invoke();
        _lookup?.Dispose();
        _polled.Dispose();
    }
}
