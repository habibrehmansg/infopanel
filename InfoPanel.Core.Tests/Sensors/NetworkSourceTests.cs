using System.Net;
using System.Net.NetworkInformation;
using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Network;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// The network source (<c>infopanel.network</c>) on a fake lister: the 1.x Network Info plugin's entries per interface
/// under keys of its own (the interface id the plugin SDK's id of the system's), loopback left out, the speed in whole
/// Mbps or unavailable, interfaces and addresses followed as they come and go, bounds, failures kept to the interface
/// that failed; the public addresses first, read from the lookup's last result, put in use only by a read of their
/// value, told of a network change, and absent without a lookup (<c>--offline</c>). The plugin's keys are never answered.
/// </summary>
public sealed class NetworkSourceTests : IDisposable
{
    private const string Guid = "{2E8F4A1B-7C3D-4E5F-9A0B-1C2D3E4F5A6B}";
    private const string GuidId = "2e8f4a1b7c3d4e5f9a0b1c2d3e4f5a6b";

    private sealed class FakeInterface(string id, string name, NetworkInterfaceType type = NetworkInterfaceType.Ethernet) : INetworkInterface
    {
        public string InterfaceId = id;
        public string InterfaceName = name;
        public string Desc = "Fake adapter";
        public NetworkInterfaceType Type = type;
        public OperationalStatus State = OperationalStatus.Up;
        public long Bits = 1_000_000_000;
        public Exception? SpeedThrows;
        public Exception? NameThrows;
        public List<IPAddress> Unicast = [];

        /// <summary>When set, every property throws, and each read is counted in <see cref="Touches"/>.</summary>
        public bool Untouchable;

        public int Touches;

        private T Get<T>(T value)
        {
            if (!Untouchable)
                return value;
            Touches++;
            throw new InvalidOperationException($"{InterfaceId} was read");
        }

        public string Id => Get(InterfaceId);
        public string Name => NameThrows is { } e ? throw e : Get(InterfaceName);
        public string Description => Get(Desc);
        public NetworkInterfaceType InterfaceType => Get(Type);
        public OperationalStatus Status => Get(State);
        public long Speed => SpeedThrows is { } e ? throw e : Get(Bits);
        public IReadOnlyList<IPAddress> Addresses => Get(Unicast.ToArray());
    }

    private sealed class FakeLister : INetworkLister
    {
        public readonly List<FakeInterface> Interfaces = [];
        public Exception? Throws;

        /// <summary>When set, a listing signals <see cref="Entered"/> and waits for it.</summary>
        public ManualResetEventSlim? Gate;

        public readonly ManualResetEventSlim Entered = new();

        /// <summary>When set, runs inside a listing: a slow listing advances the clock here.</summary>
        public Action? During;

        public IReadOnlyList<INetworkInterface> List()
        {
            if (Gate is { } gate)
            {
                Entered.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
            }

            During?.Invoke();

            return Throws is { } e ? throw e : Interfaces.ToArray();
        }
    }

    /// <summary>Answers every request with <see cref="Body"/> (each family's own), and counts them.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public string V4 = "203.0.113.7";
        public string V6 = "2001:db8::7";
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var body = request.RequestUri!.Host.StartsWith("api6.", StringComparison.Ordinal) ? V6 : V4;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    /// <summary>
    /// The manual clock, which stops the thread <see cref="PauseOn"/> at every timestamp it reads (signalling
    /// <see cref="Paused"/>) until <see cref="Resume"/>: every point of a poll where it asks the time.
    /// </summary>
    private sealed class PausingTime(ManualTimeProvider inner) : TimeProvider
    {
        public Thread? PauseOn;
        public readonly SemaphoreSlim Paused = new(0);
        public readonly SemaphoreSlim Resume = new(0);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp()
        {
            if (PauseOn == Thread.CurrentThread)
            {
                Paused.Release();
                Resume.Wait(TimeSpan.FromSeconds(30));
            }

            return inner.GetTimestamp();
        }
    }

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeLister _lister = new();
    private readonly StubHandler _handler = new();
    private readonly List<LogEvent> _events = [];
    private NetworkSource? _source;

    public void Dispose() => _source?.Dispose();

    /// <summary>The source over the fake lister, with a lookup over the stub unless <paramref name="offline"/>, discovered and polled once.</summary>
    private NetworkSource Open(bool offline = false, TimeProvider? time = null)
    {
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
        _source = new NetworkSource(_lister, offline ? null : new PublicIpLookup(_handler, _time, log), time ?? _time, log: log);
        _source.Polled.Open();
        return _source;
    }

    private FakeInterface Add(string id, string name, params string[] addresses)
    {
        var ni = new FakeInterface(id, name);
        ni.Unicast.AddRange(addresses.Select(IPAddress.Parse));
        _lister.Interfaces.Add(ni);
        return ni;
    }

    /// <summary>One poll, 5 s after the last (the source's interval).</summary>
    private void Round(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            _time.Advance(NetworkSource.Interval);
            _source!.Polled.Poll();
        }
    }

    private static SensorKey Key(string container, string entry) => NetworkSource.KeyOf(container, entry);

    private string[] Listed() => _source!.GetSensors().Select(s => s.Key.ToString()).ToArray();

    private string[] Containers() => _source!.GetSensors().Select(s => s.Key.Id.Split('/')[2]).Distinct().ToArray();

    private string Text(string container, string entry)
    {
        var state = _source!.ReadState(Key(container, entry));
        Assert.True(state.HasReading, $"{container}/{entry}");
        Assert.Equal(SensorKind.Text, state.Type);
        return state.Reading.ValueText!;
    }

    private SensorStatus Status(string container, string entry = "name") => _source!.GetStatus(Key(container, entry));

    private int Warnings(string contains) => _events.Count(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains(contains, StringComparison.Ordinal));

    // ---- Keys and entries ----

    [Fact]
    public void Keys_ThePublicAddressesFirst_ThenEachInterfaceWithThe1xPluginsEntries_ThenItsAddressesByPosition()
    {
        Add("en0", "en0", "192.168.1.20", "fe80::1%4", "2001:db8::20");
        Add(Guid, "Wi-Fi", "10.0.0.5");
        var source = Open();

        Assert.Equal(
        [
            "plugin:/infopanel.network/public/ipv4", "plugin:/infopanel.network/public/ipv6",
            "plugin:/infopanel.network/en0/id", "plugin:/infopanel.network/en0/name", "plugin:/infopanel.network/en0/description",
            "plugin:/infopanel.network/en0/status", "plugin:/infopanel.network/en0/speed",
            "plugin:/infopanel.network/en0/ipv4-0", "plugin:/infopanel.network/en0/ipv6-0", "plugin:/infopanel.network/en0/ipv6-1",
            $"plugin:/infopanel.network/{GuidId}/id", $"plugin:/infopanel.network/{GuidId}/name", $"plugin:/infopanel.network/{GuidId}/description",
            $"plugin:/infopanel.network/{GuidId}/status", $"plugin:/infopanel.network/{GuidId}/speed", $"plugin:/infopanel.network/{GuidId}/ipv4-0",
        ], Listed());
        var sensors = source.GetSensors();
        Assert.Equal(["IPv4", "IPv6", "Id", "Name", "Description", "Status", "Speed", "IPv4 Address", "IPv6 Address", "IPv6 Address"], sensors.Take(10).Select(s => s.Name));
        Assert.Equal(["", "", "", "", "", "", "Mbps", "", "", ""], sensors.Take(10).Select(s => s.Unit));
        Assert.Equal(["Public IP", "en0", "Wi-Fi"], sensors.Select(s => s.Group).Distinct());
        Assert.All(sensors, s => Assert.Equal("infopanel.network", s.SourceId));
        Assert.Equal("infopanel.network", source.Id);
        Assert.Equal(Guid, Text(GuidId, "id"));
        Assert.Equal("Wi-Fi", Text(GuidId, "name"));
        Assert.Equal("Fake adapter", Text(GuidId, "description"));
        Assert.Equal("Up", Text(GuidId, "status"));
        Assert.Equal("192.168.1.20", Text("en0", "ipv4-0"));
        Assert.Equal("fe80::1%4", Text("en0", "ipv6-0"));
        Assert.Equal("2001:db8::20", Text("en0", "ipv6-1"));
    }

    [Theory]
    [InlineData("en0")]
    [InlineData("wlp3s0")]
    [InlineData(Guid)]
    [InlineData("{00000000-0000-0000-0000-000000000000}")]
    [InlineData("Ethernet 2")]
    [InlineData("utun4")]
    public void TheInterfaceId_IsThePluginSdksIdOfTheSystemsId(string id) =>
        Assert.Equal(new PluginContainer(id).Id, NetworkSource.ContainerOf(id));

    [Fact]
    public void Loopback_IsLeftOut()
    {
        _lister.Interfaces.Add(new FakeInterface("lo0", "lo0", NetworkInterfaceType.Loopback) { Unicast = [IPAddress.Loopback] });
        Add("en0", "en0");
        Open();

        Assert.Equal(["public", "en0"], Containers());
    }

    [Fact]
    public void Speed_IsWholeMbps()
    {
        Add("en0", "en0").Bits = 866_699_999;
        var source = Open();

        Assert.True(source.TryRead(Key("en0", "speed"), out var reading));
        Assert.Equal(866, reading.ValueNow);
        Assert.Equal("Mbps", reading.Unit);
    }

    [Fact]
    public void Speed_UnknownOrUnreadable_IsUnavailable_NotZero_AndTheInterfaceStillReads()
    {
        Add("en0", "en0").Bits = -1;
        Add("en1", "en1").SpeedThrows = new PlatformNotSupportedException();
        // macOS reports 0 for an interface with no speed (most of them).
        Add("utun4", "utun4").Bits = 0;
        Open();

        Assert.Equal(SensorStatus.Unavailable, Status("en0", "speed"));
        Assert.Equal(SensorStatus.Unavailable, Status("en1", "speed"));
        Assert.Equal(SensorStatus.Unavailable, Status("utun4", "speed"));
        Assert.False(_source!.TryRead(Key("en0", "speed"), out _));
        Assert.Equal("en1", Text("en1", "name"));
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(1_000_000_000L, true)]
    public void Speed_TurningUnknownAfterAGoodRead_IsUnavailableAtOnce_AndReadsAgainOnceKnown(long bits, bool throws)
    {
        var ni = Add("en0", "en0");
        var source = Open();
        Round();
        Assert.Equal(SensorStatus.Live, Status("en0", "speed"));

        if (throws)
            ni.SpeedThrows = new NetworkInformationException();
        else
            ni.Bits = bits;
        Round();

        var speed = Key("en0", "speed");
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(speed));
        Assert.False(source.TryRead(speed, out _));
        var state = source.ReadState(speed);
        Assert.Equal(SensorStatus.Unavailable, state.Status);
        Assert.False(state.HasReading);
        Assert.Equal(SensorKind.Number, state.Type);
        Assert.Equal(SensorStatus.Unavailable, new SensorHub([source]).ReadState(speed).Status);
        Assert.Equal(SensorStatus.Live, Status("en0", "name"));

        ni.SpeedThrows = null;
        ni.Bits = 100_000_000;
        Round();
        Assert.Equal(SensorStatus.Live, source.GetStatus(speed));
        Assert.True(source.TryRead(speed, out var reading));
        Assert.Equal(100, reading.ValueNow);
    }

    [Fact]
    public void Speed_TurningUnknown_ThenTheInterfaceGone_NeverReadsTheOldSpeedAgain()
    {
        var ni = Add("en0", "en0");
        Open();
        Round();

        ni.Bits = 0;
        Round();
        _lister.Interfaces.Remove(ni);
        for (var i = 0; i < 10; i++)
        {
            Round();
            Assert.Equal(SensorStatus.Unavailable, Status("en0", "speed"));
        }
    }

    [Fact]
    public void Speed_RecoveringFromUnknown_ARead_AtAnyPointOfTheRound_NeverReturnsTheOldSpeed()
    {
        var time = new PausingTime(_time);
        var ni = Add("en0", "en0");
        var source = Open(time: time);
        Round();
        ni.Bits = 0;
        Round();
        var speed = Key("en0", "speed");
        Assert.Equal(SensorStatus.Unavailable, source.ReadState(speed).Status);

        // The round that finds 100 Mbps, stopped at every point it asks the time; a read at each one is unavailable
        // or the new speed, never the 1,000 Mbps from before the speed turned unknown.
        ni.Bits = 100_000_000;
        _time.Advance(NetworkSource.Interval);
        var poll = new Thread(() => source.Polled.Poll());
        time.PauseOn = poll;
        poll.Start();
        var seen = new List<string>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!time.Paused.Wait(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken))
            {
                if (poll.Join(0))
                    break;
                continue;
            }

            var state = source.ReadState(speed);
            seen.Add(state.HasReading ? $"{state.Status} {state.Reading.ValueNow}" : $"{state.Status}");
            time.Resume.Release();
        }

        Assert.True(poll.Join(TimeSpan.FromSeconds(30)));
        Assert.True(seen.Count >= 2, string.Join(", ", seen));
        Assert.All(seen, s => Assert.True(s is "Unavailable" or "Live 100", string.Join(", ", seen)));
        Assert.Equal(SensorStatus.Live, source.GetStatus(speed));
        Assert.True(source.TryRead(speed, out var reading));
        Assert.Equal(100, reading.ValueNow);
    }

    [Fact]
    public void Status_IsTheSystemsText_AndFollowsEveryRound()
    {
        var ni = Add("en0", "en0", "192.168.1.20");
        Open();
        Assert.Equal("Up", Text("en0", "status"));

        ni.State = OperationalStatus.Down;
        Round();

        Assert.Equal("Down", Text("en0", "status"));
    }

    [Fact]
    public void Texts_NameAndDescription_AreReadEveryRound()
    {
        var ni = Add(Guid, "Ethernet");
        Open();

        ni.InterfaceName = "Office LAN";
        ni.Desc = "USB adapter";
        Round(2);

        Assert.Equal("Office LAN", Text(GuidId, "name"));
        Assert.Equal("USB adapter", Text(GuidId, "description"));
        Assert.Equal("Office LAN", _source!.GetSensors().First(s => s.Key == Key(GuidId, "name")).Group);
    }

    // ---- Interfaces and addresses come and go ----

    [Fact]
    public void AnInterface_IsListedAfterTheRoundThatSawIt_LiveAtTheNext_StaleWhenGone_UnlistedAfter30s_AndBackUnderTheSameKeys()
    {
        Add("en0", "en0");
        Open();
        Assert.Equal(["public", "en0"], Containers());

        var vpn = Add("utun4", "utun4", "10.8.0.2");
        Round();
        Assert.Contains("utun4", Containers());
        Round();
        Assert.Equal(SensorStatus.Live, Status("utun4"));
        Assert.Equal("10.8.0.2", Text("utun4", "ipv4-0"));

        _lister.Interfaces.Remove(vpn);
        Round();
        Assert.Equal(SensorStatus.Stale, Status("utun4"));
        Assert.Equal("10.8.0.2", Text("utun4", "ipv4-0"));
        Round(5);
        Assert.Equal(SensorStatus.Stale, Status("utun4"));
        Round();
        Assert.Equal(SensorStatus.Unavailable, Status("utun4"));
        Assert.Equal(["public", "en0"], Containers());

        _lister.Interfaces.Add(vpn);
        Round(2);
        Assert.Equal(SensorStatus.Live, Status("utun4", "ipv4-0"));
    }

    [Fact]
    public void AnAddress_AddedIsListedNextRound_AndOneGone_GoesStaleThenUnlisted()
    {
        var ni = Add("en0", "en0", "192.168.1.20");
        Open();

        ni.Unicast.Add(IPAddress.Parse("192.168.1.21"));
        Round(2);
        Assert.Equal("192.168.1.21", Text("en0", "ipv4-1"));

        ni.Unicast.RemoveAt(1);
        Round();
        Assert.Equal(SensorStatus.Stale, Status("en0", "ipv4-1"));
        Round(7);
        Assert.DoesNotContain(Key("en0", "ipv4-1").ToString(), Listed());
        Assert.Equal(SensorStatus.Live, Status("en0", "ipv4-0"));
    }

    [Fact]
    public void AnAddressChanged_ReadsTheNewOne_ByPosition()
    {
        var ni = Add("en0", "en0", "192.168.1.20");
        Open();

        ni.Unicast[0] = IPAddress.Parse("192.168.1.99");
        Round();

        Assert.Equal("192.168.1.99", Text("en0", "ipv4-0"));
    }

    [Fact]
    public void Values_StayLive_BetweenRounds_StaleOnlyAfterThreeIntervals()
    {
        Add("en0", "en0");
        var source = Open();

        Assert.Equal(TimeSpan.FromSeconds(5), source.Polled.Options.Interval);
        Assert.Equal(TimeSpan.FromSeconds(15), source.Polled.Options.StaleAfter);
        Assert.Equal(TimeSpan.FromSeconds(30), source.Polled.Options.UnavailableAfter);
        _time.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(SensorStatus.Live, Status("en0"));
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(SensorStatus.Stale, Status("en0"));
    }

    // ---- Failures and bounds ----

    [Fact]
    public void AnInterfaceWhoseReadThrows_FailsAlone_TheOthersRead_AndItIsLoggedOnce()
    {
        Add("en0", "en0");
        var bad = Add("en1", "en1");
        Open();

        bad.Untouchable = true;
        Round(3);

        Assert.Equal(SensorStatus.Stale, Status("en1"));
        Assert.Equal(SensorStatus.Live, Status("en0"));
        Assert.Equal(1, Warnings("reading the interface"));
    }

    [Fact]
    public void AListingThatThrows_FailsTheInterfaces_ThePublicAddressesStillRead_AndItIsLoggedOnce()
    {
        Add("en0", "en0");
        var source = Open();
        source.ReadState(NetworkSource.PublicKeyOf("ipv4"));
        source.Lookup!.LookUpAsync(CancellationToken.None).GetAwaiter().GetResult();

        _lister.Throws = new NetworkInformationException();
        Round(2);

        Assert.Equal(SensorStatus.Stale, Status("en0"));
        Assert.Equal(SensorStatus.Live, source.GetStatus(NetworkSource.PublicKeyOf("ipv4")));
        Assert.Equal(1, Warnings("listing the interfaces failed"));
        Assert.Contains(Key("en0", "name").ToString(), Listed());
    }

    [Fact]
    public void AFirstListingThatThrows_StillListsThePublicAddresses_WhichReadOnceAnswered_AndTheInterfacesFollow()
    {
        Add("en0", "en0");
        _lister.Throws = new NetworkInformationException();
        var source = Open();
        var v4 = NetworkSource.PublicKeyOf("ipv4");
        Assert.Equal(["public"], Containers());

        source.ReadState(v4);
        source.Lookup!.LookUpAsync(CancellationToken.None).GetAwaiter().GetResult();
        Round(2);
        Assert.Equal(SensorStatus.Live, source.GetStatus(v4));
        Assert.Equal("203.0.113.7", source.ReadState(v4).Reading.ValueText);

        _lister.Throws = null;
        Round(2);
        Assert.Equal(["public", "en0"], Containers());
        Assert.Equal(SensorStatus.Live, Status("en0"));
        Assert.Equal(SensorStatus.Live, source.GetStatus(v4));
    }

    [Fact]
    public void TwoInterfacesWithOneId_TheFirstIsListed_AndTheClashIsLoggedOnce()
    {
        Add("{AB-CD}", "First");
        Add("{ABCD}", "Second");
        Open();
        Round(2);

        Assert.Equal("First", Text("abcd", "name"));
        Assert.Equal(1, Warnings("of another interface"));
    }

    [Fact]
    public void TwoInterfacesWithOneId_TheOwnersRemoval_GoesStaleThenUnavailable_NeverTheOthersValues_WhichComeOnlyAfter()
    {
        var first = Add("br0.10", "First");
        Add("br010", "Second");
        Open();
        Round();
        Assert.Equal("First", Text("br010", "name"));

        _lister.Interfaces.Remove(first);
        for (var i = 0; i < 6; i++)
        {
            Round();
            Assert.Equal(SensorStatus.Stale, Status("br010"));
            Assert.Equal("First", Text("br010", "name"));
            Assert.Equal("br0.10", Text("br010", "id"));
        }

        Round();
        Assert.Equal(SensorStatus.Unavailable, Status("br010"));

        // The owner's departure run, the other takes the interface id as an interface appearing does.
        Round(2);
        Assert.Equal(SensorStatus.Live, Status("br010"));
        Assert.Equal("Second", Text("br010", "name"));
        Assert.Equal(1, Warnings("of another interface"));
    }

    [Fact]
    public void TwoInterfacesWithOneId_ListedInAnotherOrder_TheOwnerKeepsIt()
    {
        Add("br0.10", "First");
        Add("br010", "Second");
        Open();
        Round();

        _lister.Interfaces.Reverse();
        for (var i = 0; i < 3; i++)
        {
            Round();
            Assert.Equal(SensorStatus.Live, Status("br010"));
            Assert.Equal("First", Text("br010", "name"));
        }

        Assert.Equal(1, Warnings("of another interface"));
    }

    [Fact]
    public void TwoInterfacesWithOneId_TheOwnerWhoseReadThrowsForARound_KeepsIt()
    {
        var first = Add("br0.10", "First");
        Add("br010", "Second");
        Open();
        Round();

        first.Untouchable = true;
        Round();
        Assert.Equal(SensorStatus.Stale, Status("br010"));
        Assert.Equal("First", Text("br010", "name"));

        // Listed again after the round that reads it, live at the next, as an interface appearing.
        first.Untouchable = false;
        Round(2);
        Assert.Equal(SensorStatus.Live, Status("br010"));
        Assert.Equal("First", Text("br010", "name"));
    }

    [Fact]
    public void TwoInterfacesWithOneId_TheOwnerStillListed_KeepsIt_HoweverLateThePollComes()
    {
        Add("br0.10", "First");
        Add("br010", "Second");
        Open();
        Round();

        // The other listed first, and the next poll 40 s late, past the hold (35 s): the owner is still there, and keeps it.
        _lister.Interfaces.Reverse();
        _time.Advance(TimeSpan.FromSeconds(40));
        _source!.Polled.Poll();
        Assert.Equal(SensorStatus.Live, Status("br010"));
        Assert.Equal("First", Text("br010", "name"));
        Assert.Equal("br0.10", Text("br010", "id"));

        Round(3);
        Assert.Equal(SensorStatus.Live, Status("br010"));
        Assert.Equal("First", Text("br010", "name"));
        Assert.Equal(1, Warnings("of another interface"));
    }

    [Fact]
    public void TwoInterfacesWithOneId_TheOwnerListedButFailingToRead_InALatePoll_KeepsIt()
    {
        var first = Add("br0.10", "First");
        Add("br010", "Second");
        Open();
        Round();

        _lister.Interfaces.Reverse();
        first.NameThrows = new NetworkInformationException();
        _time.Advance(TimeSpan.FromSeconds(40));
        _source!.Polled.Poll();
        // The owner's readings have run out (40 s), but the other doesn't take its id while the system lists it.
        Assert.Equal(SensorStatus.Unavailable, Status("br010"));
        Assert.DoesNotContain("br010", Containers());

        first.NameThrows = null;
        Round(2);
        Assert.Equal(SensorStatus.Live, Status("br010"));
        Assert.Equal("First", Text("br010", "name"));
        Assert.Equal(1, Warnings("of another interface"));
    }

    [Fact]
    public void TwoInterfacesWithOneId_AfterASlowRound_TheOwnersRemoval_StillGoesStaleThenUnavailable_BeforeTheOtherTakesIt()
    {
        var first = Add("br0.10", "First");
        Add("br010", "Second");
        Open();
        Round();

        // A round whose listing takes 40 s: started at t=10, its readings published at t=50.
        _lister.During = () => _time.Advance(TimeSpan.FromSeconds(40));
        Round();
        _lister.During = null;
        Assert.Equal(SensorStatus.Live, Status("br010"));
        Assert.Equal("First", Text("br010", "name"));

        // Gone from t=55: stale with the owner's readings up to 30 s after t=50, then unavailable, never the other's.
        _lister.Interfaces.Remove(first);
        for (var i = 0; i < 6; i++)
        {
            Round();
            Assert.Equal(SensorStatus.Stale, Status("br010"));
            Assert.Equal("First", Text("br010", "name"));
        }

        Round();
        Assert.Equal(SensorStatus.Unavailable, Status("br010"));

        // The hold (35 s from t=50) over, the other takes the interface id as an interface appearing does.
        Round(2);
        Assert.Equal(SensorStatus.Live, Status("br010"));
        Assert.Equal("Second", Text("br010", "name"));
    }

    [Fact]
    public void AnInterfaceWhoseIdMakesNoneOrThePublicAddresses_IsLeftOut()
    {
        Add("{--}", "Nothing");
        Add("Public", "Named public");
        Add("en0", "en0");
        Open();

        Assert.Equal(["public", "en0"], Containers());
        Assert.Equal(["IPv4", "IPv6"], _source!.GetSensors().Where(s => s.Key.Id.StartsWith("/infopanel.network/public/", StringComparison.Ordinal)).Select(s => s.Name));
        Assert.Equal(2, Warnings("is left out"));
    }

    [Fact]
    public void AtMost64Interfaces_TheRestLeftOutUnread_LoggedOnce()
    {
        for (var i = 0; i < 64; i++)
            Add($"en{i}", $"en{i}");
        var extra = Add("en64", "en64");
        extra.Untouchable = true;
        Open();
        Round(2);

        Assert.Equal(65, Containers().Length);
        Assert.Equal(0, extra.Touches);
        Assert.Equal(1, Warnings("at most 64 interfaces"));
    }

    [Fact]
    public void AtMost16AddressesOfEachFamily_AndTextsCutAt256()
    {
        var ni = Add(new string('a', 300), new string('n', 300));
        for (var i = 0; i < 20; i++)
        {
            ni.Unicast.Add(IPAddress.Parse($"10.0.0.{i + 1}"));
            ni.Unicast.Add(IPAddress.Parse($"2001:db8::{i + 1:x}"));
        }

        Open();
        var container = new string('a', 300);

        Assert.Equal(16, Listed().Count(k => k.Contains("/ipv4-", StringComparison.Ordinal)));
        Assert.Equal(16, Listed().Count(k => k.Contains("/ipv6-", StringComparison.Ordinal)));
        Assert.Equal(256, Text(container, "id").Length);
        Assert.Equal(256, Text(container, "name").Length);
    }

    [Fact]
    public void AnInterfaceWhoseKeysWouldPass512_IsLeftOut()
    {
        Add(new string('a', 480), "Long");
        Add("en0", "en0");
        Open();

        Assert.Equal(["public", "en0"], Containers());
    }

    [Theory]
    [InlineData(473, true)]
    [InlineData(474, true)]
    [InlineData(475, false)]
    public void AnInterface_IsLeftOut_WhenItsLongestKey_TheDescriptions_WouldPass512(int length, bool listed)
    {
        var id = new string('a', length);
        Add(id, "Long", "10.0.0.1", "2001:db8::1");
        Add("en0", "en0");
        Open();

        // 511, 512 and 513 characters.
        Assert.Equal(length + 38, Key(id, "description").ToString().Length);
        Assert.Equal(listed, Containers().Contains(id));
        Assert.All(Listed(), k => Assert.True(k.Length <= NetworkSampler.MaxKeyChars, k));
        Assert.Equal(listed ? 0 : 1, Warnings("is left out"));
    }

    [Fact]
    public void The1xPluginsKeys_AreNeverAnswered()
    {
        Add("en0", "en0", "192.168.1.20");
        var source = Open();

        foreach (var old in new[] { "/network-info-plugin/en0/ipv4-0", "/network-info-plugin/public-ip/ipv4", "/network-info-plugin/en0/status" })
        {
            Assert.False(source.Handles(SensorKey.Plugin(old)));
            Assert.False(new SensorHub([source]).TryRead(SensorKey.Plugin(old), out _));
            Assert.Equal(SensorStatus.Unavailable, new SensorHub([source]).GetStatus(SensorKey.Plugin(old)));
        }

        Assert.True(source.Handles(Key("anything", "else")));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(Key("anything", "else")));
    }

    // ---- The public addresses ----

    [Fact]
    public void ThePublicAddresses_UnavailableUntilTheFirstAnswer_ThenLive_AsTheLookupLastAnswered()
    {
        Add("en0", "en0");
        var source = Open();
        var v4 = NetworkSource.PublicKeyOf("ipv4");
        var v6 = NetworkSource.PublicKeyOf("ipv6");
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(v4));
        Assert.Equal(SensorKind.Text, source.ReadState(v4).Type);

        source.Lookup!.LookUpAsync(CancellationToken.None).GetAwaiter().GetResult();
        Round();

        Assert.Equal("203.0.113.7", source.ReadState(v4).Reading.ValueText);
        Assert.Equal("2001:db8::7", source.ReadState(v6).Reading.ValueText);
        Assert.Equal(SensorStatus.Live, source.GetStatus(v6));

        // Polls read the last result: live while the lookup that last ran answered, however long ago.
        Round(100);
        Assert.Equal(SensorStatus.Live, source.GetStatus(v4));
    }

    [Fact]
    public void ThePublicAddress_AfterAFailedLookup_IsStaleWithTheLastAddress_ThenUnavailable30sLater()
    {
        Add("en0", "en0");
        var source = Open();
        var v4 = NetworkSource.PublicKeyOf("ipv4");
        source.Lookup!.LookUpAsync(CancellationToken.None).GetAwaiter().GetResult();
        Round();

        _handler.V4 = "<html>Welcome to the Wi-Fi</html>";
        source.Lookup.LookUpAsync(CancellationToken.None).GetAwaiter().GetResult();
        Round();
        Assert.Equal(SensorStatus.Stale, source.GetStatus(v4));
        Assert.Equal("203.0.113.7", source.ReadState(v4).Reading.ValueText);
        Round(6);
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(v4));

        _handler.V4 = "198.51.100.1";
        source.Lookup.LookUpAsync(CancellationToken.None).GetAwaiter().GetResult();
        Round();
        Assert.Equal(SensorStatus.Live, source.GetStatus(v4));
        Assert.Equal("198.51.100.1", source.ReadState(v4).Reading.ValueText);
    }

    [Fact]
    public void ThePublicIPv6_OnANetworkWithoutIt_IsUnavailable()
    {
        Add("en0", "en0");
        var source = Open();
        _handler.V6 = "203.0.113.7";
        source.Lookup!.LookUpAsync(CancellationToken.None).GetAwaiter().GetResult();
        Round();

        Assert.Equal(SensorStatus.Live, source.GetStatus(NetworkSource.PublicKeyOf("ipv4")));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(NetworkSource.PublicKeyOf("ipv6")));
    }

    [Fact]
    public void Offline_NoLookup_AndNoPublicAddressesListed_ThoughReadTheyAskNothing()
    {
        Add("en0", "en0");
        var source = Open(offline: true);

        Assert.Null(source.Lookup);
        Assert.Equal(["en0"], Containers());
        Assert.False(source.TryRead(NetworkSource.PublicKeyOf("ipv4"), out _));
        Assert.Equal(SensorStatus.Unavailable, source.ReadState(NetworkSource.PublicKeyOf("ipv6")).Status);
        Round(3);
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public void InUse_OnlyAReadOfAPublicAddressesValue_NotItsStatus_NorAnotherSensor()
    {
        Add("en0", "en0");
        var source = Open();
        var schedule = source.Lookup!.Schedule;
        var now = _time.GetTimestamp();

        source.GetStatus(NetworkSource.PublicKeyOf("ipv4"));
        source.ReadState(Key("en0", "ipv4-0"));
        source.TryRead(Key("en0", "name"), out _);
        _ = source.GetSensors();
        Assert.False(schedule.InUse(now));

        source.ReadState(NetworkSource.PublicKeyOf("ipv6"));
        Assert.True(schedule.InUse(now));
        Assert.True(schedule.TakeDue(now));

        _time.Advance(TimeSpan.FromMinutes(3));
        Assert.False(schedule.InUse(_time.GetTimestamp()));
        source.TryRead(NetworkSource.PublicKeyOf("ipv4"), out _);
        Assert.True(schedule.TakeDue(_time.GetTimestamp()));
    }

    [Fact]
    public void ThroughTheHub_AShownProfilesReadAndAClientsValueRead_PutItInUse_AListingDoesnt()
    {
        Add("en0", "en0");
        var source = Open();
        var hub = new SensorHub([source]);
        var schedule = source.Lookup!.Schedule;

        _ = hub.GetSensors().Select(s => hub.GetStatus(s.Key)).ToList();
        Assert.False(schedule.InUse(_time.GetTimestamp()));

        hub.ReadState(NetworkSource.PublicKeyOf("ipv4"));
        Assert.True(schedule.InUse(_time.GetTimestamp()));
    }

    [Fact]
    public void ANetworkChange_TellsTheLookup_InterfaceUpOrDown_AddressAddedOrGone_InterfaceAppearing_NotSpeedOrName()
    {
        var en0 = Add("en0", "en0", "192.168.1.20");
        var source = Open();
        var schedule = source.Lookup!.Schedule;

        bool ChangedAfter(Action change)
        {
            // In use, nothing due: then one round after the change.
            source.ReadState(NetworkSource.PublicKeyOf("ipv4"));
            schedule.TakeDue(_time.GetTimestamp());
            schedule.LookedUp(_time.GetTimestamp(), answered: true);
            Assert.False(schedule.TakeDue(_time.GetTimestamp()));
            change();
            Round();
            return schedule.TakeDue(_time.GetTimestamp());
        }

        Assert.False(ChangedAfter(() => { }));
        Assert.False(ChangedAfter(() => en0.Bits = 100_000_000));
        Assert.False(ChangedAfter(() => en0.InterfaceName = "Ethernet"));
        Assert.True(ChangedAfter(() => en0.State = OperationalStatus.Down));
        Assert.True(ChangedAfter(() => en0.State = OperationalStatus.Up));
        Assert.True(ChangedAfter(() => en0.Unicast.Add(IPAddress.Parse("2001:db8::20"))));
        Assert.True(ChangedAfter(() => en0.Unicast.RemoveAt(0)));
        Assert.True(ChangedAfter(() => Add("utun4", "utun4", "10.8.0.2")));
        Assert.True(ChangedAfter(() => _lister.Interfaces.RemoveAt(1)));
    }

    [Fact]
    public void TheHistorysSampling_IsNoUse_AHiddenProfilesPublicAddress_AsksNothingOnANetworkChange()
    {
        var en0 = Add("en0", "en0", "192.168.1.20");
        var source = Open();
        var hub = new SensorHub([source]);
        var history = new SensorHistory(hub, _time);
        var schedule = source.Lookup!.Schedule;
        var v4 = NetworkSource.PublicKeyOf("ipv4");

        // A shown profile reads the address (and asks for its history); it is looked up and answers.
        hub.ReadState(v4);
        Assert.True(schedule.TakeDue(_time.GetTimestamp()));
        source.Lookup.LookUpAsync(CancellationToken.None).GetAwaiter().GetResult();
        Round();
        Assert.Equal(SensorStatus.Live, hub.ReadState(v4).Status);
        history.GetSamples(v4);
        Assert.True(schedule.InUse(_time.GetTimestamp()));

        // Hidden: for three minutes only the history samples, every second, while the source polls.
        for (var s = 1; s <= 180; s++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            history.SampleNow();
            if (s % 5 == 0)
                source.Polled.Poll();
        }

        Assert.False(schedule.InUse(_time.GetTimestamp()));
        en0.Unicast[0] = IPAddress.Parse("192.168.1.21");
        Round();
        Assert.False(schedule.TakeDue(_time.GetTimestamp()));
        Assert.Equal(1, source.Lookup.Lookups);
    }

    [Fact]
    public async Task AStopDuringAStartsDiscovery_LeavesNothingRunning_AndNothingIsAskedAfterwards()
    {
        Add("en0", "en0");
        _lister.Gate = new ManualResetEventSlim();
        _source = new NetworkSource(_lister, new PublicIpLookup(_handler, _time), _time);
        var ct = TestContext.Current.CancellationToken;

        var start = Task.Run(() => _source.StartAsync(CancellationToken.None), ct);
        Assert.True(_lister.Entered.Wait(TimeSpan.FromSeconds(10), ct));
        var stop = Task.Run(() => _source.StopAsync(CancellationToken.None), ct);
        await Task.Delay(100, ct);
        _lister.Gate.Set();
        await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.False(_source.Polled.IsRunning);
        _source.ReadState(NetworkSource.PublicKeyOf("ipv4"));
        _source.Lookup!.NetworkChanged();
        await Task.Delay(200, ct);
        Assert.Equal(0, _handler.Requests);
        Assert.Equal(0, _source.Lookup.Lookups);
    }

    [Fact]
    public void ANetworkChange_WhileUnused_AsksNothing()
    {
        var en0 = Add("en0", "en0", "192.168.1.20");
        var source = Open();

        en0.Unicast[0] = IPAddress.Parse("192.168.1.21");
        Round();

        Assert.False(source.Lookup!.Schedule.TakeDue(_time.GetTimestamp()));
    }

    // ---- This computer (opt-in) ----

    [Fact]
    public void Real_TheSystemListerListsThisComputersInterfaces_AndEveryOneIsPrinted()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("INFOPANEL_SENSOR_TESTS") == "1", "Set INFOPANEL_SENSOR_TESTS=1 to read this computer's interfaces.");
        var interfaces = new SystemNetworkLister().List();
        foreach (var ni in interfaces)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{ni.Id} | {ni.Name} | {ni.Description} | {ni.InterfaceType} | {ni.Status} | {ni.Speed} | {string.Join(", ", ni.Addresses)}");
        }

        using var source = new NetworkSource(new SystemNetworkLister(), lookup: null);
        source.Polled.Open();
        Assert.NotEmpty(source.GetSensors());
        Assert.DoesNotContain(source.GetSensors(), s => s.Group == "lo0" || s.Group == "lo");
    }
}
