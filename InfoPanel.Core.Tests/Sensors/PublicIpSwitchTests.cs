using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Network;
using InfoPanel.Settings;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// The public address lookup's switch, the setting <c>sources.network.publicIp</c>: off, a lookup in flight is
/// cancelled, nothing more is asked whatever reads or network changes come, the addresses are forgotten and the two
/// entries unlisted at once (a read of one is unavailable); on, they are listed again, unavailable until a lookup
/// answers, and a lookup runs at once while one is in use, none while not. The source follows the settings store as it
/// changes, and an engine started with the setting off asks nothing.
/// </summary>
public sealed class PublicIpSwitchTests : IDisposable
{
    /// <summary>Answers each family's address, or through <see cref="Answer"/>; counts requests and those cancelled.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public string V4 = "203.0.113.7";
        public string V6 = "2001:db8::7";
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Answer;
        public int Requests;
        public int Cancelled;
        public int InFlight;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            Interlocked.Increment(ref InFlight);
            try
            {
                if (Answer is { } answer)
                    return await answer(request, ct);
                var body = request.RequestUri!.Host.StartsWith("api6.", StringComparison.Ordinal) ? V6 : V4;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.ASCII) };
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Cancelled);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref InFlight);
            }
        }
    }

    private sealed class FakeInterface(string id) : INetworkInterface
    {
        public string Id => id;
        public string Name => id;
        public string Description => "Fake adapter";
        public NetworkInterfaceType InterfaceType => NetworkInterfaceType.Ethernet;
        public OperationalStatus Status => OperationalStatus.Up;
        public long Speed => 1_000_000_000;
        public IReadOnlyList<IPAddress> Addresses => [IPAddress.Parse("192.168.1.20")];
    }

    private sealed class FakeLister : INetworkLister
    {
        /// <summary>Listing the interfaces throws while set.</summary>
        public bool Fails;

        public IReadOnlyList<INetworkInterface> List() =>
            Fails ? throw new NetworkInformationException() : [new FakeInterface("en0")];
    }

    private static readonly SensorKey V4 = NetworkSource.PublicKeyOf("ipv4");
    private static readonly SensorKey V6 = NetworkSource.PublicKeyOf("ipv6");

    private readonly TimerTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly StubHandler _handler = new();
    private readonly TempDirectory _temp = new();
    private NetworkSource? _source;
    private PublicIpLookup? _lookup;

    public void Dispose()
    {
        _source?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _source?.Dispose();
        _lookup?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _lookup?.Dispose();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task Until(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Timed out waiting for {what}");
            await Task.Delay(5, Ct);
        }
    }

    private PublicIpLookup Lookup() => _lookup = new PublicIpLookup(_handler, _time);

    private async Task Idle(PublicIpLookup lookup, long lookups) =>
        await Until(() => lookup.Lookups == lookups && lookup.IsWaiting, $"the task to wait after {lookups} lookups");

    /// <summary>The source over one interface and a lookup over the stub, discovered and polled once by hand (its tasks not started).</summary>
    private NetworkSource Source()
    {
        _source = new NetworkSource(new FakeLister(), new PublicIpLookup(_handler, _time), _time);
        _source.Polled.Open();
        return _source;
    }

    private void Round()
    {
        _time.Advance(NetworkSource.Interval);
        _source!.Polled.Poll();
    }

    private bool Listed(SensorKey key) => _source!.GetSensors().Any(s => s.Key == key);

    private SettingsStore Store(bool? publicIp)
    {
        var paths = new DataPaths(_temp.Path);
        if (publicIp is { } on)
            File.WriteAllText(paths.SettingsFile, """{"format":"infopanel.settings/1","sources":{"network":{"publicIp":""" + (on ? "true" : "false") + "}}}");
        return SettingsStore.Open(paths);
    }

    private static JsonElement PublicIp(string value) =>
        JsonDocument.Parse("""{"sources":{"network":{"publicIp":""" + value + "}}}").RootElement.Clone();

    // ---- The lookup's switch ----

    [Fact]
    public async Task Off_CancelsALookupInFlight_AndAsksNothingMore_WhateverComes()
    {
        var lookup = Lookup();
        _handler.Answer = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("never answered");
        };
        lookup.Start();
        lookup.NoteUse();
        await Until(() => _handler.Requests == 2, "the lookup's two requests");

        lookup.SetEnabled(false);

        await Until(() => _handler.Cancelled == 2 && _handler.InFlight == 0, "both requests cancelled");
        Assert.False(lookup.Enabled);
        // In use, the network changing, the half hour passing: nothing is asked.
        for (var i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(31));
            lookup.NoteUse();
            lookup.NetworkChanged();
        }

        await Task.Delay(100, Ct);
        Assert.Equal(2, _handler.Requests);
        Assert.False(lookup.Latest(AddressFamily.InterNetwork).Answered);
    }

    [Fact]
    public async Task Off_ForgetsTheAddresses()
    {
        var lookup = Lookup();
        lookup.Start();
        lookup.NoteUse();
        await Idle(lookup, 1);
        Assert.Equal("203.0.113.7", lookup.Latest(AddressFamily.InterNetwork).Address);

        lookup.SetEnabled(false);

        Assert.Equal(default, lookup.Latest(AddressFamily.InterNetwork));
        Assert.Equal(default, lookup.Latest(AddressFamily.InterNetworkV6));
    }

    [Fact]
    public async Task On_LooksUpAtOnce_WhileInUse_NotAfterTheHalfHour()
    {
        var lookup = Lookup();
        lookup.Start();
        lookup.NoteUse();
        await Idle(lookup, 1);
        lookup.SetEnabled(false);

        // Read on while off: still in use.
        _time.Advance(TimeSpan.FromMinutes(1));
        lookup.NoteUse();
        lookup.SetEnabled(true);

        await Idle(lookup, 2);
        Assert.Equal(4, _handler.Requests);
        Assert.Equal("203.0.113.7", lookup.Latest(AddressFamily.InterNetwork).Address);
    }

    [Fact]
    public async Task On_LooksUpAtOnce_AfterAFailedLookup_NotAfterItsBackOff()
    {
        var lookup = Lookup();
        _handler.V4 = _handler.V6 = "nope";
        lookup.Start();
        lookup.NoteUse();
        await Idle(lookup, 1);

        lookup.SetEnabled(false);
        _handler.V4 = "198.51.100.1";
        lookup.SetEnabled(true);

        await Idle(lookup, 2);
        Assert.Equal("198.51.100.1", lookup.Latest(AddressFamily.InterNetwork).Address);
    }

    [Fact]
    public async Task On_AsksNothing_WhileNotInUse_ThenOnceWhenRead()
    {
        var lookup = Lookup();
        lookup.SetEnabled(false);
        lookup.Start();
        lookup.SetEnabled(true);
        await Idle(lookup, 0);

        _time.Advance(TimeSpan.FromHours(2));
        lookup.NetworkChanged();
        await Task.Delay(100, Ct);
        Assert.Equal(0, _handler.Requests);

        lookup.NoteUse();
        await Idle(lookup, 1);
        Assert.Equal(2, _handler.Requests);
    }

    [Fact]
    public async Task OffBeforeTheStart_TheTaskAsksNothing()
    {
        var lookup = Lookup();
        lookup.SetEnabled(false);
        lookup.Start();

        lookup.NoteUse();
        _time.Advance(TimeSpan.FromHours(1));
        lookup.NetworkChanged();
        await Task.Delay(100, Ct);

        Assert.Equal(0, _handler.Requests);
        Assert.Equal(0, lookup.Lookups);
        // The task itself waits for the switch: it never ran.
        Assert.Equal(0, lookup.Waits);
    }

    [Fact]
    public async Task OffAndOnAgain_WhileALookupIsInFlight_TheNewLookupWaitsForTheCancelledOne_NeverTwoAtOnce()
    {
        var lookup = Lookup();
        var hold = true;
        var max = 0;
        _handler.Answer = async (request, ct) =>
        {
            max = Math.Max(max, _handler.InFlight);
            if (Volatile.Read(ref hold))
                await Task.Delay(Timeout.Infinite, ct);
            var body = request.RequestUri!.Host.StartsWith("api6.", StringComparison.Ordinal) ? "2001:db8::7" : "203.0.113.7";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.ASCII) };
        };
        lookup.Start();
        lookup.NoteUse();
        await Until(() => _handler.Requests == 2, "the first lookup's requests");

        Volatile.Write(ref hold, false);
        lookup.SetEnabled(false);
        lookup.SetEnabled(true);

        await Idle(lookup, 2);
        Assert.Equal(4, _handler.Requests);
        Assert.Equal(2, _handler.Cancelled);
        Assert.True(max <= 2, $"{max} requests at once");
        Assert.True(lookup.Latest(AddressFamily.InterNetwork).Answered);

        await lookup.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(0, _handler.InFlight);
    }

    [Fact]
    public async Task OffAndOnAgain_AsTheOldTaskIsAboutToTakeTheDueLookup_TheNewTaskStillLooksUpAtOnce()
    {
        var lookup = Lookup();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 1;
        async Task Pause()
        {
            paused.SetResult();
            await resume.Task;
        }

        // The first task stops just before it takes the lookup the reading's use made due.
        lookup.BeforeTakeDue = () => Interlocked.Exchange(ref first, 0) == 1 ? Pause() : Task.CompletedTask;
        lookup.NoteUse();
        lookup.Start();
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        lookup.SetEnabled(false);
        lookup.SetEnabled(true);
        // The cancelled task takes what was due, then ends at its cancellation without asking.
        resume.SetResult();

        // The task after it still asks at once: the reading is in use.
        await Idle(lookup, 1);
        Assert.Equal(2, _handler.Requests);
        Assert.Equal("203.0.113.7", lookup.Latest(AddressFamily.InterNetwork).Address);
        Assert.Equal(1, lookup.MostRunning);
    }

    [Fact]
    public async Task OffOnOff_Rapidly_OneTaskAtATime_NothingAskedWhileOff_ThenOneLookupAfterTheLastOn()
    {
        var lookup = Lookup();
        var askedWhileOff = 0;
        _handler.Answer = (request, _) =>
        {
            if (!lookup.Enabled)
                Interlocked.Increment(ref askedWhileOff);
            var body = request.RequestUri!.Host.StartsWith("api6.", StringComparison.Ordinal) ? "2001:db8::7" : "203.0.113.7";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.ASCII) });
        };
        lookup.Start();
        lookup.NoteUse();
        await Idle(lookup, 1);
        // Out of use: the tasks the quick switches start have nothing to take.
        _time.Advance(PublicIpSchedule.UseWindow + TimeSpan.FromMinutes(1));

        for (var i = 0; i < 20; i++)
        {
            lookup.SetEnabled(false);
            lookup.SetEnabled(true);
        }

        lookup.SetEnabled(false);
        await Until(() => lookup.Running == 0, "every task to end");
        var requests = _handler.Requests;
        var lookups = lookup.Lookups;
        Assert.Equal(2, requests);
        // Off: in use, the network changing, the half hour passing ask nothing.
        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(31));
            lookup.NoteUse();
            lookup.NetworkChanged();
        }

        await Task.Delay(100, Ct);
        Assert.Equal(requests, _handler.Requests);
        Assert.Equal(0, askedWhileOff);
        Assert.Equal(default, lookup.Latest(AddressFamily.InterNetwork));

        lookup.SetEnabled(true);

        await Idle(lookup, lookups + 1);
        await Task.Delay(100, Ct);
        Assert.Equal(lookups + 1, lookup.Lookups);
        Assert.Equal(requests + 2, _handler.Requests);
        Assert.Equal(0, askedWhileOff);
        Assert.Equal(1, lookup.Running);
        Assert.Equal(1, lookup.MostRunning);
        Assert.Equal("203.0.113.7", lookup.Latest(AddressFamily.InterNetwork).Address);
    }

    [Fact]
    public async Task ALookupEndingAsItIsSwitchedOff_KeepsNothing()
    {
        var lookup = Lookup();
        var answer = new TaskCompletionSource();
        _handler.Answer = async (request, _) =>
        {
            // Answers whatever the cancellation: the switch comes between the answer and its keeping.
            await answer.Task;
            var body = request.RequestUri!.Host.StartsWith("api6.", StringComparison.Ordinal) ? "2001:db8::7" : "203.0.113.7";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.ASCII) };
        };
        lookup.Start();
        lookup.NoteUse();
        await Until(() => _handler.Requests == 2, "the lookup's requests");

        lookup.SetEnabled(false);
        answer.SetResult();
        await Until(() => _handler.InFlight == 0, "the requests to end");
        await lookup.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(default, lookup.Latest(AddressFamily.InterNetwork));
        Assert.Equal(default, lookup.Latest(AddressFamily.InterNetworkV6));
    }

    [Fact]
    public async Task AnAnswerThatArrivesAfterTheSwitchWentOff_IsDropped_ThoughItsRequestWasntCancelled()
    {
        var lookup = Lookup();
        _handler.Answer = (request, _) =>
        {
            // The switch goes off while the request is answered, and the answer still comes back whole.
            lookup.SetEnabled(false);
            var body = request.RequestUri!.Host.StartsWith("api6.", StringComparison.Ordinal) ? "2001:db8::7" : "203.0.113.7";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.ASCII) });
        };

        await lookup.LookUpAsync(CancellationToken.None);

        Assert.Equal(2, _handler.Requests);
        Assert.Equal(default, lookup.Latest(AddressFamily.InterNetwork));
        Assert.Equal(default, lookup.Latest(AddressFamily.InterNetworkV6));
    }

    // ---- The source's entries ----

    [Fact]
    public async Task Off_UnlistsTheEntriesAtOnce_AReadIsUnavailable_AndTheyStayUnlisted()
    {
        var source = Source();
        await source.Lookup!.LookUpAsync(CancellationToken.None);
        Round();
        Assert.Equal("203.0.113.7", source.ReadState(V4).Reading.ValueText);

        source.SetPublicIp(false);

        Assert.False(source.PublicIp);
        Assert.False(Listed(V4));
        Assert.False(Listed(V6));
        Assert.False(source.TryRead(V4, out _));
        Assert.Equal(SensorStatus.Unavailable, source.ReadState(V6).Status);
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(V4));
        Assert.Contains(source.GetSensors(), s => s.Key == NetworkSource.KeyOf("en0", "name"));
        for (var i = 0; i < 10; i++)
            Round();
        Assert.False(Listed(V4));
        Assert.False(source.TryRead(V4, out _));
        Assert.Equal(0, source.Polled.GetSensors().Count(s => s.Key == V4 || s.Key == V6));
    }

    [Fact]
    public async Task OffThenOn_TheEntriesAreListedAgain_UnavailableUntilALookupAnswers_TheOldAddressNeverBack()
    {
        var source = Source();
        _handler.V4 = "192.0.2.1";
        await source.Lookup!.LookUpAsync(CancellationToken.None);
        Round();

        // Off and on within one round: the address read before is never read again.
        source.SetPublicIp(false);
        source.SetPublicIp(true);
        Assert.False(source.TryRead(V4, out _));
        for (var i = 0; i < 3; i++)
        {
            Round();
            Assert.True(Listed(V4));
            Assert.False(source.TryRead(V4, out _));
        }

        _handler.V4 = "198.51.100.1";
        await source.Lookup.LookUpAsync(CancellationToken.None);
        Round();
        Assert.Equal("198.51.100.1", source.ReadState(V4).Reading.ValueText);
        Assert.Equal(SensorStatus.Live, source.GetStatus(V4));
    }

    [Fact]
    public async Task OffForARound_ThenOn_TheEntriesComeBack_WithoutTheOldAddress()
    {
        var source = Source();
        await source.Lookup!.LookUpAsync(CancellationToken.None);
        Round();

        source.SetPublicIp(false);
        Round();
        source.SetPublicIp(true);
        Round();

        Assert.True(Listed(V4));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(V4));
        Round();
        Assert.False(source.TryRead(V4, out _));
    }

    [Fact]
    public async Task SwitchedWhileTheInterfacesFailToList_TheEntriesFollowTheSwitch_TheInterfacesStayListed()
    {
        var lister = new FakeLister();
        _source = new NetworkSource(lister, new PublicIpLookup(_handler, _time), _time);
        _source.SetPublicIp(false);
        _source.Polled.Open();
        Round();
        var name = NetworkSource.KeyOf("en0", "name");
        Assert.Equal(0, _source.Polled.GetSensors().Count(s => s.Key == V4 || s.Key == V6));

        lister.Fails = true;
        _source.SetPublicIp(true);
        Round();
        Round();

        Assert.True(Listed(V4));
        Assert.True(Listed(V6));
        Assert.Contains(_source.GetSensors(), s => s.Key == name);
        await _source.Lookup!.LookUpAsync(CancellationToken.None);
        Round();
        Assert.Equal("203.0.113.7", _source.ReadState(V4).Reading.ValueText);
        Assert.Equal("2001:db8::7", _source.ReadState(V6).Reading.ValueText);

        // Off again while the listing still fails: unlisted by the polled source too, the interfaces kept.
        _source.SetPublicIp(false);
        Round();
        Round();
        Assert.Equal(0, _source.Polled.GetSensors().Count(s => s.Key == V4 || s.Key == V6));
        Assert.Contains(_source.Polled.GetSensors(), s => s.Key == name);
    }

    [Fact]
    public void Offline_TheSwitchChangesNothing_NoEntriesEitherWay()
    {
        _source = new NetworkSource(new FakeLister(), null, _time);
        _source.Polled.Open();

        _source.SetPublicIp(true);
        Round();
        Assert.False(Listed(V4));
        Assert.False(_source.PublicIp);
        Assert.Equal(0, _handler.Requests);
    }

    // ---- The setting, live ----

    [Fact]
    public async Task TheSetting_IsFollowedLive_OffStopsTheLookups_OnBringsOneAtOnce()
    {
        var store = Store(publicIp: null);
        _source = new NetworkSource(new FakeLister(), new PublicIpLookup(_handler, _time), _time).FollowSettings(store);
        await _source.StartAsync(Ct);
        _source.ReadState(V4);
        await Idle(_source.Lookup!, 1);
        Assert.True(_source.PublicIp);

        store.Apply(PublicIp("false"));

        Assert.False(_source.PublicIp);
        Assert.False(Listed(V4));
        _time.Advance(TimeSpan.FromMinutes(1));
        _source.ReadState(V4);
        _source.Lookup!.NetworkChanged();
        await Task.Delay(100, Ct);
        Assert.Equal(2, _handler.Requests);

        // Another setting's change leaves it off.
        store.Apply(JsonDocument.Parse("""{"frameRate":30}""").RootElement);
        Assert.False(_source.PublicIp);

        store.Apply(PublicIp("null"));

        Assert.True(_source.PublicIp);
        await Idle(_source.Lookup, 2);
        Assert.Equal(4, _handler.Requests);
    }

    [Fact]
    public async Task AnEngineStartedWithTheSettingOff_AsksNothing_AndListsNoPublicAddress()
    {
        var store = Store(publicIp: false);
        _source = new NetworkSource(new FakeLister(), new PublicIpLookup(_handler, _time), _time).FollowSettings(store);
        await _source.StartAsync(Ct);

        for (var i = 0; i < 20; i++)
        {
            _source.ReadState(V4);
            _source.TryRead(V6, out _);
            _time.Advance(TimeSpan.FromMinutes(5));
        }

        _source.Lookup!.NetworkChanged();
        await Task.Delay(100, Ct);
        Assert.Equal(0, _handler.Requests);
        Assert.Equal(0, _source.Lookup.Lookups);
        Assert.False(_source.PublicIp);
        Assert.False(Listed(V4));
        Assert.False(Listed(V6));
    }
}
