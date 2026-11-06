using System.Diagnostics;
using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Images;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Core.Tests.Sensors;
using InfoPanel.Service;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Service;

/// <summary>
/// Who holds the service credential (<see cref="ServiceHolder"/>), asked of a fake service on a loopback port
/// (<see cref="LocalHttpServer"/>) with a fake clock: <c>GET /svc/v1/holder</c> to the configured address only, with the
/// credential; asked at start, when the credential or the address changes, and again every 6 hours, no more often; the
/// name, else the email, its control characters dropped and past 40 characters shortened; no credential or a 401: not
/// signed in; a failure keeps the name known for that same credential; a late answer for a credential since replaced
/// dropped; the answer capped and the request timed out; the name never logged.
/// </summary>
public sealed class ServiceHolderTests : IAsyncDisposable
{
    private const string Credential = "holder-test.credential~A1b2+/==";
    private const string Other = "holder-test.other~C3d4";
    private const string Name = "Zed Quist-Holder";
    private const string Email = "zq-holder@example.test";
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDirectory _temp = new();
    private readonly List<LogEvent> _events = [];
    private readonly Serilog.ILogger _log;
    private readonly ServiceCredentials _credentials;
    private readonly LocalHttpServer _service;
    private readonly List<IDisposable> _owned = [];
    private readonly TimerTimeProvider _clock = new(Start);
    private string? _address;

    /// <summary>
    /// What the fake service answers next (status 0: it closes the connection without an answer); by default the holder
    /// <see cref="Name"/>, <see cref="Email"/>.
    /// </summary>
    private Func<ServedRequest, CancellationToken, Task<(int Status, byte[] Body)>> _answer;

    public ServiceHolderTests()
    {
        _log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
        _answer = (_, _) => Task.FromResult((200, HolderBody(Name, Email)));
        _service = new LocalHttpServer(async (request, stream, ct) =>
        {
            var (status, body) = await _answer(request, ct);
            if (status != 0)
                await LocalHttpServer.Answer(stream, status, body, "application/json");
        });
        _address = $"http://127.0.0.1:{_service.Port}";
        _credentials = new ServiceCredentials(FileServiceCredentialStore.ForPrivateFile(_temp.Path, _log), () => _address, _log);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in _owned)
            owned.Dispose();
        await _service.DisposeAsync();
        _temp.Dispose();
    }

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    /// <summary>The service's answer: <c>{"name": …, "email": …}</c>.</summary>
    private static byte[] HolderBody(string? name, string email) => JsonSerializer.SerializeToUtf8Bytes(new { name, email });

    private static (int, byte[]) Answer(string? name, string email = Email) => (200, HolderBody(name, email));

    private ServiceHolder Holder(bool offline = false)
    {
        var client = new ServiceClient(_credentials, loopbackOnly: offline, _clock);
        var holder = new ServiceHolder(client, _credentials, _clock, _log);
        _owned.Add(holder);
        _owned.Add(client);
        return holder;
    }

    private async Task<ServiceHolder> Started(bool offline = false)
    {
        var holder = Holder(offline);
        holder.Start();
        await Idle(holder, 1);
        return holder;
    }

    /// <summary>Waits (on the wall clock, at most 10 s) until <paramref name="condition"/> holds.</summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Timed out waiting for {what}");
            await Task.Delay(5, Ct);
        }
    }

    /// <summary>Waits until the holder has asked <paramref name="asks"/> times and waits, so nothing more is under way.</summary>
    private static Task Idle(ServiceHolder holder, long asks) =>
        Until(() => holder.Asks == asks && holder.IsWaiting, $"the holder to wait after {asks} asks");

    /// <summary>Whether the credential is held and the name shown (the email beside it is checked on its own).</summary>
    private static void AssertShown(ServiceHolder holder, bool held, string? name) => Assert.Equal((held, name), (holder.Current.Held, holder.Current.Name));

    /// <summary>Waits until the holder has asked <paramref name="asks"/> times and the service has been sent <paramref name="requests"/> requests.</summary>
    private async Task Asked(ServiceHolder holder, long asks, int requests)
    {
        await Idle(holder, asks);
        Assert.Equal(requests, _service.Requests.Count);
    }

    // ---- When it asks ----

    [Fact]
    public async Task NoCredential_NoRequest_EvenAfterHours_AndNotSignedIn()
    {
        var holder = await Started();
        _clock.Advance(TimeSpan.FromHours(13));
        await Idle(holder, 1);

        Assert.Empty(_service.Requests);
        Assert.Equal(0, holder.Requests);
        AssertShown(holder, held: false, name: null);
    }

    [Fact]
    public async Task AtStart_WithACredential_AsksTheConfiguredAddress_WithTheCredential_AndShowsTheName()
    {
        _credentials.Put(Credential);

        var holder = await Started();

        var request = Assert.Single(_service.Requests);
        Assert.Equal(("GET", "/svc/v1/holder"), (request.Method, request.Path));
        Assert.Equal($"Bearer {Credential}", request.Header("Authorization"));
        Assert.Equal($"127.0.0.1:{_service.Port}", request.Header("Host"));
        AssertShown(holder, held: true, name: Name);
    }

    [Fact]
    public async Task APut_AsksAgain_WithTheNewCredential_AndShowsItsHolder()
    {
        _credentials.Put(Credential);
        var holder = await Started();

        _answer = (_, _) => Task.FromResult(Answer("Bo Other"));
        _credentials.Put(Other);
        await Idle(holder, 2);

        Assert.Equal(2, _service.Requests.Count);
        Assert.Equal($"Bearer {Other}", _service.Requests[1].Header("Authorization"));
        AssertShown(holder, held: true, name: "Bo Other");
    }

    [Fact]
    public async Task TheSameCredentialProvidedAgain_AsksAgain()
    {
        _credentials.Put(Credential);
        var holder = await Started();

        _credentials.Put(Credential);
        await Idle(holder, 2);

        Assert.Equal(2, _service.Requests.Count);
        AssertShown(holder, held: true, name: Name);
    }

    [Fact]
    public async Task ADelete_IsNotSignedIn_AsksWithoutARequest_AndAPutAfterItAsks()
    {
        _credentials.Put(Credential);
        var holder = await Started();

        _credentials.Delete();
        await Idle(holder, 2);

        Assert.Single(_service.Requests);
        AssertShown(holder, held: false, name: null);

        _credentials.Put(Credential);
        await Idle(holder, 3);
        Assert.Equal(2, _service.Requests.Count);
        AssertShown(holder, held: true, name: Name);
    }

    [Fact]
    public async Task AChangeOfAddress_Asks_AndTheNextCredential_GoesToTheNewAddressOnly()
    {
        await using var next = new LocalHttpServer((_, stream, _) => LocalHttpServer.Answer(stream, 200, HolderBody("New Place", Email), "application/json"));
        _credentials.Put(Credential);
        var holder = await Started();

        _address = $"http://127.0.0.1:{next.Port}";
        _credentials.AddressChanged();
        await Idle(holder, 2);
        // The credential was for the address before: removed with it.
        AssertShown(holder, held: false, name: null);

        _credentials.Put(Other);
        await Idle(holder, 3);

        Assert.Single(_service.Requests);
        var request = Assert.Single(next.Requests);
        Assert.Equal(("/svc/v1/holder", $"Bearer {Other}"), (request.Path, request.Header("Authorization")));
        AssertShown(holder, held: true, name: "New Place");
    }

    [Fact]
    public async Task EverySixHours_WhileACredentialIsHeld_AndNoMoreOften()
    {
        _credentials.Put(Credential);
        var holder = await Started();

        _clock.Advance(ServiceHolder.Interval - TimeSpan.FromSeconds(1));
        await Idle(holder, 1);
        Assert.Single(_service.Requests);

        _answer = (_, _) => Task.FromResult(Answer("Renamed Holder"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Idle(holder, 2);
        Assert.Equal(2, _service.Requests.Count);
        AssertShown(holder, held: true, name: "Renamed Holder");

        _clock.Advance(ServiceHolder.Interval);
        await Idle(holder, 3);
        Assert.Equal(3, _service.Requests.Count);
    }

    [Fact]
    public async Task Offline_AServiceElsewhere_IsNeverAsked()
    {
        _address = "https://service.example";
        _credentials.Put(Credential);

        var holder = await Started(offline: true);

        Assert.Equal(0, holder.Requests);
        Assert.Empty(_service.Requests);
        AssertShown(holder, held: true, name: null);
    }

    // ---- What it shows ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0007\u0085")]
    public async Task WithoutAName_TheEmailShows(string? name)
    {
        _answer = (_, _) => Task.FromResult(Answer(name));
        _credentials.Put(Credential);

        var holder = await Started();

        AssertShown(holder, held: true, name: Email);
    }

    [Fact]
    public async Task AnAnswerWithoutTheName_TheEmailShows()
    {
        _answer = (_, _) => Task.FromResult((200, Encoding.UTF8.GetBytes($$"""{"email":"{{Email}}"}""")));
        _credentials.Put(Credential);

        var holder = await Started();

        AssertShown(holder, held: true, name: Email);
    }

    [Theory]
    [InlineData("Ann Lee", "Ann Lee")]
    [InlineData("Ann\nLee", "AnnLee")]
    [InlineData("\tAnn\u0000 Lee\u007f\u009b", "Ann Lee")]
    [InlineData("Ann\u2028Lee", "AnnLee")]
    [InlineData("Zoë Ångström-Lee", "Zoë Ångström-Lee")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa…")]
    public void TheNameShown_WithoutControlCharacters_AndShortenedPast40Characters(string name, string shown)
    {
        Assert.Equal(shown, ServiceHolder.Shown(name, Email));
    }

    [Fact]
    public void TheLength_CountsWhatReadsAsOneCharacter()
    {
        const string coder = "👩🏽‍💻";
        Assert.Equal(string.Concat(Enumerable.Repeat(coder, 40)), ServiceHolder.Shown(string.Concat(Enumerable.Repeat(coder, 40)), Email));
        Assert.Equal(string.Concat(Enumerable.Repeat(coder, 39)) + "…", ServiceHolder.Shown(string.Concat(Enumerable.Repeat(coder, 41)), Email));
    }

    [Fact]
    public void ALongEmail_ShownInTheNamesPlace_IsShortenedInItsMiddle()
    {
        var email = new string('e', 60) + "@example.test";

        Assert.Equal(new string('e', 15) + "…eee@example.test", ServiceHolder.Shown(null, email));
        Assert.Equal(new string('e', 15) + "…eee@example.test", ServiceHolder.Shown(" ", email));
    }

    /// <summary>An email past 32 characters (what reads as one): its first 15, "…" and its last 16, so the domain stays.</summary>
    [Theory]
    [InlineData("abcdefghijklmnopqrst@example.com", "abcdefghijklmnopqrst@example.com")]
    [InlineData("abcdefghijklmnopqrstu@example.com", "abcdefghijklmno…rstu@example.com")]
    [InlineData("\tabcdefghijklmnopqrst@example.com\u0000 ", "abcdefghijklmnopqrst@example.com")]
    public void TheEmail_Past32Characters_KeepsItsStartAndItsEnd(string email, string shown)
    {
        Assert.Equal(32, ServiceHolder.MaxEmailLength);
        Assert.Equal(shown, ServiceHolder.ShownBeside("Ann Lee", email));
    }

    [Fact]
    public void TheEmailsLength_CountsWhatReadsAsOneCharacter()
    {
        const string coder = "👩🏽‍💻";
        var fits = string.Concat(Enumerable.Repeat(coder, 20)) + "@example.com";
        var over = string.Concat(Enumerable.Repeat(coder, 21)) + "@example.com";

        Assert.Equal(fits, ServiceHolder.ShownBeside("Ann Lee", fits));
        Assert.Equal(string.Concat(Enumerable.Repeat(coder, 15)) + "…" + string.Concat(Enumerable.Repeat(coder, 4)) + "@example.com",
            ServiceHolder.ShownBeside("Ann Lee", over));
    }

    // ---- The email beside the name ----

    [Fact]
    public async Task TheEmail_IsKeptBesideTheName()
    {
        _credentials.Put(Credential);

        var holder = await Started();

        Assert.Equal(new ServiceHolderState(true, Name, Email), holder.Current);
    }

    [Fact]
    public async Task WithoutAName_TheEmailShows_AndNothingBesideIt()
    {
        _answer = (_, _) => Task.FromResult(Answer(null));
        _credentials.Put(Credential);

        var holder = await Started();

        Assert.Equal(new ServiceHolderState(true, Email, null), holder.Current);
    }

    [Theory]
    [InlineData("\tzq\u0000-holder@example.test\u2028", "zq-holder@example.test")]
    [InlineData("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee@example.test", "eeeeeeeeeeeeeee…eee@example.test")]
    [InlineData(" ", null)]
    public async Task TheEmail_IsCleanedAndShortened_AsTheNameIs(string email, string? shown)
    {
        _answer = (_, _) => Task.FromResult(Answer("Ann Lee", email));
        _credentials.Put(Credential);

        var holder = await Started();

        Assert.Equal(new ServiceHolderState(true, "Ann Lee", shown), holder.Current);
    }

    [Fact]
    public async Task TheEmail_GoesWithTheName_OnARemoval_ARefusal_OrAnotherCredential_AndStaysThroughAFailure()
    {
        _credentials.Put(Credential);
        var holder = await Started();

        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        _clock.Advance(ServiceHolder.Interval);
        await Idle(holder, 2);
        Assert.Equal(new ServiceHolderState(true, Name, Email), holder.Current);

        _credentials.Put(Other);
        await Idle(holder, 3);
        Assert.Equal(new ServiceHolderState(true, null, null), holder.Current);

        _answer = (_, _) => Task.FromResult(Answer(Name));
        _credentials.Put(Credential);
        await Idle(holder, 4);
        Assert.Equal(new ServiceHolderState(true, Name, Email), holder.Current);
        _credentials.Delete();
        await Idle(holder, 5);
        Assert.Equal(new ServiceHolderState(false, null, null), holder.Current);

        _credentials.Put(Credential);
        await Idle(holder, 6);
        _answer = (_, _) => Task.FromResult((401, Array.Empty<byte>()));
        _clock.Advance(ServiceHolder.Interval);
        await Until(() => holder.Requests == 6 && holder.IsWaiting, "the 401");
        Assert.Equal(new ServiceHolderState(false, null, null), holder.Current);
    }

    // ---- A 401, a failure, a late answer ----

    [Fact]
    public async Task A401_IsNotSignedIn_ForgetsTheName_AndAsksNoMore()
    {
        _credentials.Put(Credential);
        var holder = await Started();
        AssertShown(holder, held: true, name: Name);

        _answer = (_, _) => Task.FromResult((401, Array.Empty<byte>()));
        _clock.Advance(ServiceHolder.Interval);
        await Until(() => holder.Requests == 2 && holder.IsWaiting, "the 401");

        AssertShown(holder, held: false, name: null);
        _clock.Advance(ServiceHolder.Interval * 3);
        await Until(() => holder.IsWaiting, "the wait");
        Assert.Equal(2, _service.Requests.Count);
        AssertShown(holder, held: false, name: null);
    }

    public static TheoryData<int, string> Failures => new()
    {
        { 500, Encoding.UTF8.GetString(HolderBody("Not Shown", Email)) },
        { 503, "" },
        { 302, "" },
        { 404, "" },
        { 200, "not json" },
        { 200, "[]" },
        { 200, """{"name":"No Email"}""" },
        { 200, """{"name":null,"email":" "}""" },
        { 200, """{"name":7,"email":"x@example.test"}""" },
        { 200, """{"name":"Email Not A String","email":7}""" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailure_KeepsTheNameKnownForThatSameCredential(int status, string body)
    {
        _credentials.Put(Credential);
        var holder = await Started();

        _answer = (_, _) => Task.FromResult((status, Encoding.UTF8.GetBytes(body)));
        _clock.Advance(ServiceHolder.Interval);
        await Until(() => holder.Requests == 2 && holder.IsWaiting, "the failure");

        AssertShown(holder, held: true, name: Name);
    }

    [Fact]
    public async Task AFailure_ForANewCredential_ShowsNoName()
    {
        _credentials.Put(Credential);
        var holder = await Started();

        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        _credentials.Put(Other);
        await Idle(holder, 2);

        AssertShown(holder, held: true, name: null);
    }

    [Fact]
    public async Task AnUnreachableService_KeepsTheName()
    {
        _credentials.Put(Credential);
        var holder = await Started();

        _answer = (_, _) => Task.FromResult((0, Array.Empty<byte>()));
        _clock.Advance(ServiceHolder.Interval);
        await Idle(holder, 2);

        AssertShown(holder, held: true, name: Name);
    }

    [Fact]
    public async Task ALateAnswer_ForACredentialReplacedSince_IsDropped()
    {
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        _answer = async (request, ct) =>
        {
            var replaced = request.Header("Authorization") == $"Bearer {Other}";
            await (replaced ? second : first).Task.WaitAsync(ct);
            return Answer(replaced ? "New Holder" : "Old Holder");
        };
        _credentials.Put(Credential);
        var holder = Holder();
        holder.Start();
        await Until(() => _service.Requests.Count == 1, "the first request");

        // Replaced while its request is under way; then the old credential's answer comes.
        _credentials.Put(Other);
        first.SetResult();
        await Until(() => _service.Requests.Count == 2, "the new credential's request");

        AssertShown(holder, held: true, name: null);

        second.SetResult();
        await Idle(holder, 2);
        AssertShown(holder, held: true, name: "New Holder");
    }

    [Fact]
    public async Task ALateAnswer_ForACredentialRemovedSince_ShowsNothing()
    {
        var gate = new TaskCompletionSource();
        _answer = async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return Answer("Gone Holder");
        };
        _credentials.Put(Credential);
        var holder = Holder();
        holder.Start();
        await Until(() => _service.Requests.Count == 1, "the request");

        _credentials.Delete();
        gate.SetResult();
        await Idle(holder, 2);

        AssertShown(holder, held: false, name: null);
        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        _credentials.Put(Credential);
        await Idle(holder, 3);
        // The same text again is another credential: the answer to the one removed isn't its name.
        AssertShown(holder, held: true, name: null);
    }

    // ---- After a failure: asked again soon ----

    /// <summary>The waits after successive failures, in minutes: 1, doubling, then the 6-hour interval.</summary>
    public static readonly int[] RetryMinutes = [1, 2, 4, 8, 16, 32, 64, 128, 256, 360, 360];

    [Fact]
    public async Task AFailure_IsAskedAgainAfterAMinute_ThenTwo_Four_Doubling_UpToTheInterval()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), ServiceHolder.FirstRetry);
        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        _credentials.Put(Credential);
        var holder = await Started();

        var asks = 1;
        foreach (var minutes in RetryMinutes)
        {
            _clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            await Asked(holder, asks, asks);
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Asked(holder, ++asks, asks);
        }

        AssertShown(holder, held: true, name: null);
    }

    [Fact]
    public async Task AnAnswerAfterFailures_WaitsTheInterval_AndTheNextFailureStartsAgainAtAMinute()
    {
        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        _credentials.Put(Credential);
        var holder = await Started();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Asked(holder, 2, 2);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await Asked(holder, 3, 3);

        _answer = (_, _) => Task.FromResult(Answer(Name));
        _clock.Advance(TimeSpan.FromMinutes(4));
        await Asked(holder, 4, 4);
        AssertShown(holder, held: true, name: Name);

        _clock.Advance(ServiceHolder.Interval - TimeSpan.FromSeconds(1));
        await Asked(holder, 4, 4);
        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Asked(holder, 5, 5);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await Asked(holder, 6, 6);
        AssertShown(holder, held: true, name: Name);
    }

    [Fact]
    public async Task AChangeOfCredential_StartsTheRetriesAgainAtAMinute()
    {
        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        _credentials.Put(Credential);
        var holder = await Started();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Asked(holder, 2, 2);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await Asked(holder, 3, 3);

        _credentials.Put(Other);
        await Asked(holder, 4, 4);
        _clock.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(1));
        await Asked(holder, 4, 4);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Asked(holder, 5, 5);
    }

    [Fact]
    public async Task ARetryAfter_LongerThanTheRetry_IsWaited()
    {
        // 503 asking for 10 minutes: the service's least wait, longer than the first retry's minute.
        await using var limited = new LocalHttpServer((_, stream, _) =>
            LocalHttpServer.Answer(stream, 503, [], "application/json", "Retry-After: 600"));
        _address = $"http://127.0.0.1:{limited.Port}";
        _credentials.Put(Credential);
        var holder = await Started();

        _clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        await Idle(holder, 1);
        Assert.Single(limited.Requests);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Idle(holder, 2);
        Assert.Equal(2, limited.Requests.Count);
    }

    [Fact]
    public async Task ANetworkChange_AfterAFailure_AsksAtOnce_AndTheRetriesStartAgain()
    {
        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        _credentials.Put(Credential);
        var holder = await Started();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Asked(holder, 2, 2);

        // The network changed (a sleep's end, another Wi-Fi): asked at once, no time passing.
        Assert.True(holder.NetworkChanged());
        await Asked(holder, 3, 3);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Asked(holder, 4, 4);

        _answer = (_, _) => Task.FromResult(Answer(Name));
        Assert.True(holder.NetworkChanged());
        await Asked(holder, 5, 5);
        AssertShown(holder, held: true, name: Name);
    }

    [Fact]
    public async Task ANetworkChange_WithoutAFailure_AsksNothing()
    {
        // No credential.
        var holder = await Started();
        Assert.False(holder.NetworkChanged());

        // An answer.
        _credentials.Put(Credential);
        await Asked(holder, 2, 1);
        Assert.False(holder.NetworkChanged());

        // A 401: refused until another credential.
        _answer = (_, _) => Task.FromResult((401, Array.Empty<byte>()));
        _clock.Advance(ServiceHolder.Interval);
        await Until(() => holder.Requests == 2 && holder.IsWaiting, "the 401");
        Assert.False(holder.NetworkChanged());

        _clock.Advance(TimeSpan.FromHours(1));
        await Until(() => holder.IsWaiting, "the wait");
        Assert.Equal(2, _service.Requests.Count);
    }

    /// <summary>What the fake service answers: a 503, once <paramref name="gate"/> is open.</summary>
    private static Func<ServedRequest, CancellationToken, Task<(int Status, byte[] Body)>> FailAfter(TaskCompletionSource gate) => async (_, ct) =>
    {
        await gate.Task.WaitAsync(ct);
        return (503, Array.Empty<byte>());
    };

    [Fact]
    public async Task ANetworkChange_DuringAnAskThatFails_AsksAtOnceAfterIt_AndTheRetriesStartAgain()
    {
        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        _credentials.Put(Credential);
        var holder = await Started();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Asked(holder, 2, 2);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await Asked(holder, 3, 3);

        // The fourth ask, after 4 minutes, waits for its answer; the network changes meanwhile.
        var gate = new TaskCompletionSource();
        _answer = FailAfter(gate);
        _clock.Advance(TimeSpan.FromMinutes(4));
        await Until(() => _service.Requests.Count == 4, "the fourth request");
        Assert.True(holder.NetworkChanged());

        // It fails: asked again at once, no time passing, and the retries start again at a minute (not 8).
        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        gate.SetResult();
        await Asked(holder, 5, 5);
        _clock.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(1));
        await Asked(holder, 5, 5);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Asked(holder, 6, 6);
    }

    [Fact]
    public async Task ANetworkChange_DuringAnAskThatIsAnswered_AsksNothingMore()
    {
        _credentials.Put(Credential);
        var holder = await Started();

        var gate = new TaskCompletionSource();
        _answer = async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return Answer(Name);
        };
        _clock.Advance(ServiceHolder.Interval);
        await Until(() => _service.Requests.Count == 2, "the second request");
        holder.NetworkChanged();
        gate.SetResult();
        await Asked(holder, 2, 2);

        _clock.Advance(ServiceHolder.Interval - TimeSpan.FromSeconds(1));
        await Asked(holder, 2, 2);
        AssertShown(holder, held: true, name: Name);
    }

    [Fact]
    public async Task ALateFailure_ForACredentialReplacedSince_DoesntAdvanceTheNewCredentialsRetries()
    {
        // The first credential's request waits; the second is provided meanwhile, then the first's request fails.
        var gate = new TaskCompletionSource();
        _answer = async (request, ct) =>
        {
            if (request.Header("Authorization") == $"Bearer {Credential}")
                await gate.Task.WaitAsync(ct);
            return (503, Array.Empty<byte>());
        };
        _credentials.Put(Credential);
        var holder = Holder();
        holder.Start();
        await Until(() => _service.Requests.Count == 1, "the first request");

        _credentials.Put(Other);
        gate.SetResult();
        await Asked(holder, 2, 2);
        Assert.Equal($"Bearer {Other}", _service.Requests[1].Header("Authorization"));

        // The new credential's first failure: asked again after a minute, not two.
        _clock.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(1));
        await Asked(holder, 2, 2);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Asked(holder, 3, 3);
    }

    [Fact]
    public async Task ALateFailure_ForAnAddressChangedSince_DoesntAdvanceTheRetries()
    {
        await using var next = new LocalHttpServer((_, stream, _) => LocalHttpServer.Answer(stream, 503, [], "application/json"));
        var gate = new TaskCompletionSource();
        _answer = FailAfter(gate);
        _credentials.Put(Credential);
        var holder = Holder();
        holder.Start();
        await Until(() => _service.Requests.Count == 1, "the first request");

        // The address changes (the credential goes with it) and another is provided, while the first request waits.
        _address = $"http://127.0.0.1:{next.Port}";
        _credentials.AddressChanged();
        _credentials.Put(Other);
        gate.SetResult();
        await Until(() => next.Requests.Count == 1 && holder.IsWaiting, "the new address's request");

        _clock.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(1));
        await Until(() => holder.IsWaiting, "the wait");
        Assert.Single(next.Requests);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => next.Requests.Count == 2 && holder.IsWaiting, "the retry after a minute");
    }

    [Fact]
    public async Task ANetworkStorm_ProbesOncePerFailure_NotOncePerChange()
    {
        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        _credentials.Put(Credential);
        var holder = await Started();

        // After a failure: the first change asks at once; the changes after that ask's failure ask nothing.
        Assert.True(holder.NetworkChanged());
        await Asked(holder, 2, 2);
        for (var i = 0; i < 10; i++)
        {
            Assert.False(holder.NetworkChanged());
            await Asked(holder, 2, 2);
        }

        // A burst during the next ask (after the minute the probe's failure waits): one ask after it fails, not ten.
        var gate = new TaskCompletionSource();
        _answer = FailAfter(gate);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Until(() => _service.Requests.Count == 3, "the third request");
        var asked = 0;
        for (var i = 0; i < 10; i++)
            asked += holder.NetworkChanged() ? 1 : 0;
        Assert.Equal(1, asked);
        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        gate.SetResult();
        await Asked(holder, 4, 4);
        for (var i = 0; i < 10; i++)
        {
            Assert.False(holder.NetworkChanged());
            await Asked(holder, 4, 4);
        }

        // The retries keep their schedule: the next after a minute.
        _clock.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(1));
        await Asked(holder, 4, 4);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Asked(holder, 5, 5);
    }

    [Fact]
    public async Task ANetworkBurst_WhileWaitingAfterAFailure_AsksOnce()
    {
        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        _credentials.Put(Credential);
        var holder = await Started();

        var asked = 0;
        for (var i = 0; i < 20; i++)
            asked += holder.NetworkChanged() ? 1 : 0;
        await Asked(holder, 2, 2);

        Assert.Equal(1, asked);
        await Task.Delay(100, Ct);
        await Asked(holder, 2, 2);
    }

    // ---- Bounds ----

    [Fact]
    public async Task ARetryAfterPastTheInterval_WaitsTheInterval_AndAChangeStillAsks()
    {
        // 503 asking for 60 days, past what a wait can be: the holder asks again after its 6 hours, and a change wakes it.
        await using var limited = new LocalHttpServer((_, stream, _) =>
            LocalHttpServer.Answer(stream, 503, [], "application/json", "Retry-After: 5184000"));
        _address = $"http://127.0.0.1:{limited.Port}";
        _credentials.Put(Credential);
        var holder = await Started();
        Assert.Single(limited.Requests);

        _clock.Advance(ServiceHolder.Interval - TimeSpan.FromSeconds(1));
        await Idle(holder, 1);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Idle(holder, 2);
        Assert.Equal(2, limited.Requests.Count);

        _credentials.Put(Other);
        await Idle(holder, 3);
        Assert.Equal(3, limited.Requests.Count);
    }

    [Fact]
    public async Task AFailureOfTheTaskItself_EndsNothing_TheNextChangeAsks()
    {
        var clock = new FailingClock(_clock);
        var client = new ServiceClient(_credentials, loopbackOnly: false, _clock);
        var holder = new ServiceHolder(client, _credentials, clock, _log);
        _owned.Add(holder);
        _owned.Add(client);
        holder.Start();
        await Idle(holder, 1);

        // The ask's timer can't be made: that ask fails as a whole, and the task lives on.
        clock.Fail = true;
        _credentials.Put(Credential);
        await Idle(holder, 2);
        Assert.Empty(_service.Requests);

        clock.Fail = false;
        _credentials.Put(Credential);
        await Idle(holder, 3);
        Assert.Single(_service.Requests);
        AssertShown(holder, held: true, name: Name);
    }

    /// <summary><paramref name="inner"/>, whose timers can't be made while <see cref="Fail"/> is set.</summary>
    private sealed class FailingClock(TimeProvider inner) : TimeProvider
    {
        public volatile bool Fail;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Fail ? throw new InvalidOperationException("No timer") : inner.CreateTimer(callback, state, dueTime, period);
    }

    // ---- The menu's read ----

    [Fact]
    public async Task TheMenusRead_NeverWaitsForTheStore()
    {
        // A credential store whose write hangs (slow storage): the menu reads who holds the credential meanwhile.
        var store = new GatedStore();
        var credentials = new ServiceCredentials(store, () => _address, _log);
        credentials.Put(Credential);
        using var holder = new ServiceHolder(null, credentials, _clock, _log);
        Assert.Equal(new ServiceHolderState(true, null), holder.Current);

        store.Gate.Reset();
        store.Writing.Reset();
        var put = Task.Run(() => credentials.Put(Other), Ct);
        Assert.True(store.Writing.Wait(TimeSpan.FromSeconds(10), Ct), "the write to start");
        var read = Task.Run(() => holder.Current, Ct);
        var answered = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2), Ct)) == read;
        store.Gate.Set();
        await put;

        Assert.True(answered, "the menu's read waited for the store's write");
        Assert.Equal(new ServiceHolderState(true, null), await read);
        Assert.Equal(new ServiceHolderState(true, null), holder.Current);
    }

    /// <summary>A store in memory whose writes wait for <see cref="Gate"/>, <see cref="Writing"/> set once one does.</summary>
    private sealed class GatedStore : IServiceCredentialStore
    {
        private StoredServiceCredential? _record;

        public ManualResetEventSlim Gate { get; } = new(true);
        public ManualResetEventSlim Writing { get; } = new(false);
        public string Name => "private";
        public StoredServiceCredential? Read() => _record;

        public void Write(StoredServiceCredential record)
        {
            Writing.Set();
            Gate.Wait(TimeSpan.FromSeconds(30));
            _record = record;
        }

        public void Delete() => _record = null;
    }

    [Theory]
    [InlineData(ServiceHolder.MaxBytes, true)]
    [InlineData(ServiceHolder.MaxBytes + 1, false)]
    public async Task TheAnswer_IsReadToAtMostMaxBytes(int size, bool read)
    {
        var start = Encoding.UTF8.GetBytes($$"""{"name":"Capped Holder","email":"{{Email}}","pad":" """);
        var end = "\"}"u8.ToArray();
        var body = start.Concat(Enumerable.Repeat((byte)'p', size - start.Length - end.Length)).Concat(end).ToArray();
        Assert.Equal(size, body.Length);
        _answer = (_, _) => Task.FromResult((200, body));
        _credentials.Put(Credential);

        var holder = await Started();

        AssertShown(holder, held: true, name: read ? "Capped Holder" : null);
    }

    [Fact]
    public async Task ARequestThatIsntAnswered_EndsAfterItsTimeout()
    {
        _answer = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Answer("Never");
        };
        _credentials.Put(Credential);
        var holder = Holder();
        holder.Start();
        await Until(() => _service.Requests.Count == 1, "the request");

        _clock.Advance(ServiceHolder.RequestTimeout - TimeSpan.FromSeconds(1));
        await Task.Delay(100, Ct);
        Assert.False(holder.IsWaiting);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await Idle(holder, 1);
        AssertShown(holder, held: true, name: null);
    }

    // ---- Never kept, never logged ----

    [Fact]
    public async Task TheNameAndEmail_AreNeverLogged_NorTheCredential()
    {
        _credentials.Put(Credential);
        var holder = await Started();
        _answer = (_, _) => Task.FromResult((503, Array.Empty<byte>()));
        _clock.Advance(ServiceHolder.Interval);
        await Idle(holder, 2);
        _answer = (_, _) => Task.FromResult((200, HolderBody(Name, Email)));
        _clock.Advance(ServiceHolder.Interval);
        await Idle(holder, 3);
        AssertShown(holder, held: true, name: Name);

        List<LogEvent> events;
        lock (_events)
            events = [.. _events];
        Assert.Contains(events, e => e.MessageTemplate.Text.Contains("holder", StringComparison.OrdinalIgnoreCase));
        foreach (var e in events)
        {
            var text = e.RenderMessage() + string.Join(" ", e.Properties.Values) + e.Exception;
            Assert.DoesNotContain("Quist", text);
            Assert.DoesNotContain("zq-holder", text);
            Assert.DoesNotContain("holder-test.credential", text);
        }

        Assert.DoesNotContain(Directory.GetFiles(_temp.Path, "*", SearchOption.AllDirectories), f => File.ReadAllText(f).Contains("Quist"));
    }

    [Fact]
    public async Task Stopped_ItAsksNoMore()
    {
        _credentials.Put(Credential);
        var holder = await Started();

        await holder.StopAsync(Ct);
        _credentials.Put(Other);
        _clock.Advance(ServiceHolder.Interval * 2);
        await Task.Delay(100, Ct);

        Assert.Single(_service.Requests);
    }
}
