using System.Diagnostics;
using InfoPanel.Core.Tests.Images;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Service;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Service;

/// <summary>
/// The credential a new one replaced, sent once to the configured service to be forgotten (spec/docs/api.md, "The
/// configured service"): <c>DELETE /svc/v1/device</c> with the replaced credential as a Bearer token, to the address it
/// was kept for and only while that is in effect, under <see cref="ServiceClient"/>'s rules; one attempt within a bound,
/// no retry; whatever the answer, the credential kept stays <c>present</c>; one log line, never a credential. The fake
/// service is on a loopback port (<see cref="LocalHttpServer"/>).
/// </summary>
public sealed class ServiceReleaseTests : IDisposable
{
    private const string Replaced = "replaced-credential.Rq7~+/==";
    private const string Kept = "kept-credential.Kq9~+/==";

    private const string Told = "Told the configured service to forget the credential this engine replaced";
    private const string NoLongerKnew = "The configured service no longer knew the credential this engine replaced";
    private const string Couldnt = "Couldn't tell the configured service to forget the credential this engine replaced";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// How much sooner than a <see cref="Stopwatch"/> says a timed wait may end: .NET's timers count on a coarse clock
    /// (on Linux the kernel's coarse monotonic clock, a few milliseconds a tick), so a 300 ms wait can end at 299 ms
    /// by the stopwatch's finer one.
    /// </summary>
    private static readonly TimeSpan TimerGrain = TimeSpan.FromMilliseconds(16);

    private readonly TempDirectory _temp = new();
    private readonly List<LogEvent> _log = [];
    private readonly ILogger _logger;
    private string? _address;

    public ServiceReleaseTests() => _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_log)).CreateLogger();

    public void Dispose() => _temp.Dispose();

    private ServiceCredentials Credentials() => new(FileServiceCredentialStore.ForPrivateFile(_temp.Path, _logger), () => _address, _logger);

    /// <summary>The credential kept, with the one it replaced: <see cref="Replaced"/> then <see cref="Kept"/> provided.</summary>
    private (ServiceCredentials Credentials, ReplacedServiceCredential? Replaced) Replacing()
    {
        var credentials = Credentials();
        credentials.Put(Replaced);
        return (credentials, credentials.Put(Kept));
    }

    private ServiceRelease Release(ServiceClient client, TimeSpan? bound = null) =>
        new(client, bound ?? ServiceRelease.Bound, TimeProvider.System, CancellationToken.None, _logger);

    private static Func<ServedRequest, Stream, CancellationToken, Task> Answering(int status) =>
        (_, stream, _) => LocalHttpServer.Answer(stream, status, status == 204 ? [] : "{}"u8.ToArray(), "application/json");

    private LogEvent[] Lines()
    {
        lock (_log)
            return [.. _log.Where(e => e.MessageTemplate.Text.Contains("credential this engine replaced"))];
    }

    private void AssertNoCredentialLogged()
    {
        lock (_log)
        {
            foreach (var e in _log)
            {
                var text = e.RenderMessage() + e.Exception;
                Assert.DoesNotContain("replaced-credential", text);
                Assert.DoesNotContain("kept-credential", text);
                Assert.DoesNotContain(ServiceCredentials.Fingerprint(Replaced), text);
                Assert.DoesNotContain(ServiceCredentials.Fingerprint(Kept), text);
            }
        }
    }

    [Fact]
    public async Task TheReplacedCredential_IsSentOnce_ToBeForgotten_WithItself_ToTheAddressItWasKeptFor()
    {
        await using var service = new LocalHttpServer(Answering(204));
        _address = $"http://127.0.0.1:{service.Port}";
        var (credentials, replaced) = Replacing();
        using var client = new ServiceClient(credentials, loopbackOnly: true);

        await Release(client).Start(replaced).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var request = Assert.Single(service.Requests);
        Assert.Equal(("DELETE", "/svc/v1/device", $"Bearer {Replaced}"), (request.Method, request.Path, request.Header("authorization")));
        Assert.Null(request.Header("cookie"));
        var line = Assert.Single(Lines());
        Assert.Equal((LogEventLevel.Information, Told), (line.Level, line.RenderMessage()));
        Assert.Equal(ServiceCredentialState.Present, credentials.Status.Credential);
        Assert.Equal(ServiceCredentials.Fingerprint(Kept), credentials.Status.Fingerprint);
        AssertNoCredentialLogged();
    }

    [Theory]
    [InlineData(200, LogEventLevel.Information, Told)]
    [InlineData(204, LogEventLevel.Information, Told)]
    [InlineData(401, LogEventLevel.Information, NoLongerKnew)]
    [InlineData(404, LogEventLevel.Warning, Couldnt + " (404)")]
    [InlineData(429, LogEventLevel.Warning, Couldnt + " (429)")]
    [InlineData(503, LogEventLevel.Warning, Couldnt + " (503)")]
    [InlineData(302, LogEventLevel.Warning, Couldnt + " (302)")]
    public async Task WhateverTheServiceAnswers_TheKeptCredentialStaysPresent_NeverRejected_AndOneLineSaysHowItWent(
        int status, LogEventLevel level, string message)
    {
        await using var service = new LocalHttpServer(Answering(status));
        _address = $"http://127.0.0.1:{service.Port}";
        var (credentials, replaced) = Replacing();
        using var client = new ServiceClient(credentials, loopbackOnly: true);

        await Release(client).Start(replaced).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Single(service.Requests);
        var line = Assert.Single(Lines());
        Assert.Equal((level, message), (line.Level, line.RenderMessage()));
        Assert.Equal(ServiceCredentialState.Present, credentials.Status.Credential);
        Assert.Equal(ServiceCredentialState.Present, Credentials().Status.Credential);
        // The kept credential is still sent: nothing refuses it here.
        Assert.True(credentials.TryTake(out _, out var kept, out _));
        Assert.Equal(Kept, kept);
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task ADroppedConnection_IsLoggedByItsType_AndChangesNothing()
    {
        // The service reads the request, starts its answer and closes the connection part way through the status line.
        // (A connection closed before any byte of an answer is opened again by .NET's handler itself, up to three times,
        // within the same bound.)
        await using var service = new LocalHttpServer((_, stream, _) => stream.WriteAsync("HTTP/1.1 2"u8.ToArray()).AsTask());
        _address = $"http://127.0.0.1:{service.Port}";
        var (credentials, replaced) = Replacing();
        using var client = new ServiceClient(credentials, loopbackOnly: true);

        await Release(client).Start(replaced).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Single(service.Requests);
        var line = Assert.Single(Lines());
        Assert.Equal((LogEventLevel.Warning, Couldnt + " (HttpRequestException)"), (line.Level, line.RenderMessage()));
        Assert.Null(line.Exception);
        Assert.Equal(ServiceCredentialState.Present, credentials.Status.Credential);
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task AServiceThatNeverAnswers_IsDroppedAtTheBound_WithAWarning_NoRetry()
    {
        await using var service = new LocalHttpServer(async (_, _, ct) => await Task.Delay(Timeout.Infinite, ct));
        _address = $"http://127.0.0.1:{service.Port}";
        var (credentials, replaced) = Replacing();
        using var client = new ServiceClient(credentials, loopbackOnly: true);
        var bound = TimeSpan.FromMilliseconds(300);
        var clock = Stopwatch.StartNew();

        await Release(client, bound).Start(replaced).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(clock.Elapsed >= bound - TimerGrain, $"ended after {clock.Elapsed}, before the bound");
        Assert.Single(service.Requests);
        var line = Assert.Single(Lines());
        Assert.Equal((LogEventLevel.Warning, Couldnt + " (no answer in 0.3 s)"), (line.Level, line.RenderMessage()));
        Assert.Equal(ServiceCredentialState.Present, credentials.Status.Credential);
    }

    [Fact]
    public void TheBound_Is15Seconds_AndItsReason_SaysSo()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), ServiceRelease.Bound);
        Assert.Equal("no answer in 15 s", ServiceRelease.NoAnswer(ServiceRelease.Bound));
    }

    [Fact]
    public async Task Offline_AServiceNotOnThisComputer_GetsNothing_AndTheLineSaysSo()
    {
        _address = "https://service.example";
        var (credentials, replaced) = Replacing();
        Assert.NotNull(replaced);
        using var client = new ServiceClient(credentials, loopbackOnly: true);

        await Release(client).Start(replaced).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var line = Assert.Single(Lines());
        Assert.Equal((LogEventLevel.Warning, Couldnt + " (not sent: offline)"), (line.Level, line.RenderMessage()));
        Assert.Equal(ServiceCredentialState.Present, credentials.Status.Credential);
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task TheAddressChangedBeforeItWasSent_NeitherAddressGetsIt()
    {
        await using var old = new LocalHttpServer(Answering(204));
        await using var next = new LocalHttpServer(Answering(204));
        _address = $"http://127.0.0.1:{old.Port}";
        var (credentials, replaced) = Replacing();
        Assert.NotNull(replaced);
        _address = $"http://127.0.0.1:{next.Port}";
        credentials.AddressChanged();
        using var client = new ServiceClient(credentials, loopbackOnly: true);

        await Release(client).Start(replaced).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Empty(old.Requests);
        Assert.Empty(next.Requests);
        var line = Assert.Single(Lines());
        Assert.Equal((LogEventLevel.Warning, Couldnt + " (not sent: the address changed)"), (line.Level, line.RenderMessage()));
    }

    /// <summary>
    /// A client whose connections are held while being made: <paramref name="entered"/> is set, then each waits for
    /// <paramref name="gate"/> (or its own cancellation) before connecting. Nothing is on the wire meanwhile.
    /// </summary>
    private static ServiceClient HeldClient(ServiceCredentials credentials, TaskCompletionSource entered, Task gate)
    {
        var handler = ServiceClient.NewHandler();
        handler.ConnectCallback = async (context, ct) =>
        {
            entered.TrySetResult();
            await gate.WaitAsync(ct);
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, ct);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
        return new ServiceClient(credentials, loopbackOnly: true, time: null, handler);
    }

    /// <summary>Whether <paramref name="release"/> ended within a few seconds, while its connection is still held.</summary>
    private static async Task<bool> EndsWhileHeld(Task release) =>
        await Task.WhenAny(release, Task.Delay(TimeSpan.FromSeconds(5), Ct)) == release;

    [Fact]
    public async Task TheReplacedCredentialProvidedAgain_BeforeItIsSent_IsNeverSent()
    {
        await using var service = new LocalHttpServer(Answering(204));
        _address = $"http://127.0.0.1:{service.Port}";
        var (credentials, replaced) = Replacing();
        Assert.NotNull(replaced);
        credentials.Put(Replaced);
        using var client = new ServiceClient(credentials, loopbackOnly: true);

        await Release(client).Start(replaced).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Empty(service.Requests);
        var line = Assert.Single(Lines());
        Assert.Equal((LogEventLevel.Warning, Couldnt + " (dropped: the credential is in use again)"), (line.Level, line.RenderMessage()));
        Assert.Equal(ServiceCredentialState.Present, credentials.Status.Credential);
        Assert.Equal(ServiceCredentials.Fingerprint(Replaced), credentials.Status.Fingerprint);
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task TheReplacedCredentialProvidedAgain_WhileItsRequestIsBeingMade_CancelsIt()
    {
        await using var service = new LocalHttpServer(Answering(204));
        _address = $"http://127.0.0.1:{service.Port}";
        var (credentials, replaced) = Replacing();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = HeldClient(credentials, entered, gate.Task);
        var release = Release(client).Start(replaced);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        credentials.Put(Replaced);
        var ended = await EndsWhileHeld(release);
        gate.TrySetResult();
        await release.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(ended, "the request went on once the credential it would remove was in use again");
        Assert.Empty(service.Requests);
        var line = Assert.Single(Lines());
        Assert.Equal((LogEventLevel.Warning, Couldnt + " (dropped: the credential is in use again)"), (line.Level, line.RenderMessage()));
        Assert.Equal(ServiceCredentials.Fingerprint(Replaced), credentials.Status.Fingerprint);
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task TheAddressChanging_WhileItsRequestIsBeingMade_CancelsIt()
    {
        await using var old = new LocalHttpServer(Answering(204));
        await using var next = new LocalHttpServer(Answering(204));
        _address = $"http://127.0.0.1:{old.Port}";
        var (credentials, replaced) = Replacing();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = HeldClient(credentials, entered, gate.Task);
        var release = Release(client).Start(replaced);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var nextAddress = $"http://127.0.0.1:{next.Port}";
        using (credentials.AddressChanging(nextAddress))
            _address = nextAddress;
        credentials.AddressChanged();
        var ended = await EndsWhileHeld(release);
        gate.TrySetResult();
        await release.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(ended, "the request went on once its address was no longer in effect");
        Assert.Empty(old.Requests);
        Assert.Empty(next.Requests);
        var line = Assert.Single(Lines());
        Assert.Equal((LogEventLevel.Warning, Couldnt + " (dropped: the address changed)"), (line.Level, line.RenderMessage()));
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task TheEngineStopping_EndsAHeldRequest_AndStartsNoOther()
    {
        await using var service = new LocalHttpServer(Answering(204));
        _address = $"http://127.0.0.1:{service.Port}";
        var (credentials, replaced) = Replacing();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = HeldClient(credentials, entered, gate.Task);
        using var stopping = new CancellationTokenSource();
        var release = new ServiceRelease(client, ServiceRelease.Bound, TimeProvider.System, stopping.Token, _logger);
        var running = release.Start(replaced);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await stopping.CancelAsync();
        var ended = await EndsWhileHeld(running);
        var later = credentials.Put("later-credential");
        Assert.NotNull(later);
        var afterStop = release.Start(later);
        var stopped = Stopwatch.StartNew();
        await release.DisposeAsync();
        var stopTook = stopped.Elapsed;
        gate.TrySetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(ended, "the request went on once the engine was stopping");
        Assert.True(afterStop.IsCompleted, "a release started after the engine began stopping");
        Assert.True(stopTook <= ServiceRelease.StopWait + TimeSpan.FromSeconds(1), $"the stop waited {stopTook}");
        Assert.Empty(service.Requests);
        var line = Assert.Single(Lines());
        Assert.Equal((LogEventLevel.Warning, Couldnt + " (dropped: the engine stops)"), (line.Level, line.RenderMessage()));
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task AnOlderReleaseEnding_LeavesANewerOneOfTheSameCredential_Pending_SoProvidingItAgainDropsIt()
    {
        // Replaced, then Kept (r1 holds Replaced); Replaced again (r1 dropped); Kept again (r2 holds Replaced, equal to r1
        // by value). r2's request is held while being made, r1's ends, and Replaced is provided once more: r2 is dropped.
        await using var service = new LocalHttpServer(Answering(204));
        _address = $"http://127.0.0.1:{service.Port}";
        var credentials = Credentials();
        credentials.Put(Replaced);
        var r1 = credentials.Put(Kept);
        credentials.Put(Replaced);
        var r2 = credentials.Put(Kept);
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal(r1, r2);
        Assert.NotSame(r1, r2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = HeldClient(credentials, entered, gate.Task);
        var release = Release(client);
        var second = release.Start(r2);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // r1 was dropped: it ends without connecting.
        await release.Start(r1).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        credentials.Put(Replaced);
        var ended = await EndsWhileHeld(second);
        gate.TrySetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(ended, "the newer request went on once the credential it would remove was in use again");
        Assert.Empty(service.Requests);
        var lines = Lines();
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Equal(Couldnt + " (dropped: the credential is in use again)", l.RenderMessage()));
        Assert.Equal(ServiceCredentials.Fingerprint(Replaced), credentials.Status.Fingerprint);
        AssertNoCredentialLogged();
    }

    /// <summary>A store in memory whose writes wait for <see cref="Gate"/> (open at first), holding the credentials' lock meanwhile.</summary>
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

    /// <summary>
    /// The credential kept with the one it replaced, over <paramref name="store"/>; then another credential is being
    /// provided, its write held by the store's gate, so a release waits for the store's lock before it can send.
    /// </summary>
    private (ServiceCredentials Credentials, ReplacedServiceCredential Replaced, Task Writing) ReplacingWhileAWriteIsHeld(GatedStore store)
    {
        var credentials = new ServiceCredentials(store, () => _address, _logger);
        credentials.Put(Replaced);
        var replaced = credentials.Put(Kept);
        Assert.NotNull(replaced);
        store.Gate.Reset();
        store.Writing.Reset();
        var writing = Task.Run(() => credentials.Put("another-credential"), Ct);
        Assert.True(store.Writing.Wait(TimeSpan.FromSeconds(10), Ct), "the write didn't start");
        return (credentials, replaced, writing);
    }

    private const string StopsWhileRunning = "The engine stops while a request to the configured service to forget a replaced credential still runs";

    private int StopWarnings()
    {
        lock (_log)
            return _log.Count(e => e.MessageTemplate.Text == StopsWhileRunning);
    }

    [Fact]
    public async Task TheEngineStopping_WaitsForAReleaseThatEndsWithinItsWait_BeforeItReturns()
    {
        await using var service = new LocalHttpServer(Answering(204));
        _address = $"http://127.0.0.1:{service.Port}";
        var store = new GatedStore();
        var (credentials, replaced, writing) = ReplacingWhileAWriteIsHeld(store);
        using var client = new ServiceClient(credentials, loopbackOnly: true);
        var stopWait = TimeSpan.FromSeconds(10);
        var release = new ServiceRelease(client, ServiceRelease.Bound, TimeProvider.System, CancellationToken.None, _logger, stopWait);
        var running = release.Start(replaced);

        // The release waits for the store's lock until the write ends, well within the stop's wait.
        var opening = Task.Delay(TimeSpan.FromMilliseconds(300), Ct).ContinueWith(_ => store.Gate.Set(), Ct, TaskContinuationOptions.None, TaskScheduler.Default);
        var stopped = Stopwatch.StartNew();
        await release.DisposeAsync();
        var stopTook = stopped.Elapsed;
        var endedFirst = running.IsCompleted;
        var linesAtStop = Lines().Length;
        await opening;
        await writing.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(endedFirst, $"the stop returned after {stopTook} before the release it waits for ended");
        Assert.True(stopTook < stopWait, $"the stop waited {stopTook}");
        Assert.Empty(service.Requests);
        Assert.Equal(1, linesAtStop);
        var line = Assert.Single(Lines());
        Assert.Equal((LogEventLevel.Warning, Couldnt + " (dropped: the engine stops)"), (line.Level, line.RenderMessage()));
        Assert.Equal(0, StopWarnings());
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task TheEngineStopping_ReturnsAtItsWait_AndAReleaseEndingAfterwards_SendsNothing_AndLogsNothing()
    {
        await using var service = new LocalHttpServer(Answering(204));
        _address = $"http://127.0.0.1:{service.Port}";
        var store = new GatedStore();
        var (credentials, replaced, writing) = ReplacingWhileAWriteIsHeld(store);
        var client = new ServiceClient(credentials, loopbackOnly: true);
        var stopWait = TimeSpan.FromMilliseconds(300);
        var release = new ServiceRelease(client, ServiceRelease.Bound, TimeProvider.System, CancellationToken.None, _logger, stopWait);
        var running = release.Start(replaced);

        // The release waits for the store's lock past the stop's wait; the host then disposes the client.
        var stopped = Stopwatch.StartNew();
        await release.DisposeAsync();
        var stopTook = stopped.Elapsed;
        var endedFirst = running.IsCompleted;
        client.Dispose();
        store.Gate.Set();
        await writing.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.False(endedFirst, "the release ended before the stop's wait: this test needs it held past it");
        Assert.True(stopTook >= stopWait - TimerGrain && stopTook < stopWait + TimeSpan.FromSeconds(5), $"the stop waited {stopTook}");
        Assert.Equal(1, StopWarnings());
        Assert.Empty(service.Requests);
        Assert.Empty(Lines());
        AssertNoCredentialLogged();
    }

    [Fact]
    public async Task AConnectionClosedBeforeAnyAnswerByte_MayBeResentByTheConnectionLayer_WithinTheBound_AndChangesNothing()
    {
        // The service reads the request and closes the connection without a byte of an answer: .NET's handler may send
        // the same request again on a new connection, a bounded number of times, all within the release's one bound.
        await using var service = new LocalHttpServer((_, _, _) => Task.CompletedTask);
        _address = $"http://127.0.0.1:{service.Port}";
        var (credentials, replaced) = Replacing();
        using var client = new ServiceClient(credentials, loopbackOnly: true);
        var clock = Stopwatch.StartNew();

        await Release(client).Start(replaced).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(clock.Elapsed < ServiceRelease.Bound, $"ended after {clock.Elapsed}, past the bound");
        Assert.InRange(service.Requests.Count, 1, 4);
        Assert.All(service.Requests, r => Assert.Equal(("DELETE", $"Bearer {Replaced}"), (r.Method, r.Header("authorization"))));
        var line = Assert.Single(Lines());
        Assert.Equal((LogEventLevel.Warning, Couldnt + " (HttpRequestException)"), (line.Level, line.RenderMessage()));
        Assert.Equal(ServiceCredentialState.Present, credentials.Status.Credential);
        Assert.Equal(ServiceCredentials.Fingerprint(Kept), credentials.Status.Fingerprint);
        AssertNoCredentialLogged();
    }

    private sealed class CollectingSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events) events.Add(logEvent);
        }
    }
}
