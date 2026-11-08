using System.Net;
using InfoPanel.Core.Tests.Images;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Service;
using Serilog;
using Xunit;

namespace InfoPanel.Core.Tests.Service;

/// <summary>
/// The rules every request to the configured service follows (<see cref="ServiceClient"/>), against a fake service on a
/// loopback port (<see cref="LocalHttpServer"/>): the credential as a Bearer token to the address's origin only; no
/// redirect followed; no credential, no request; a 401 makes it rejected and refuses every request after it here, until a
/// client provides another; a 429 or 503 hands back its <c>Retry-After</c>; <c>https</c>, or <c>http</c> on loopback only;
/// with <c>--offline</c>, loopback only.
/// </summary>
public sealed class ServiceClientTests : IDisposable
{
    private const string Credential = "test-credential.A1b2~+/==";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDirectory _temp = new();
    private string? _address;

    public void Dispose() => _temp.Dispose();

    private ServiceCredentials Credentials() =>
        new(FileServiceCredentialStore.ForPrivateFile(_temp.Path, Logger), () => _address, Logger);

    private static Serilog.ILogger Logger => Serilog.Core.Logger.None;

    private static Func<ServedRequest, Stream, CancellationToken, Task> Answering(int status, params string[] headers) =>
        (_, stream, _) => LocalHttpServer.Answer(stream, status, "{}"u8.ToArray(), "application/json", headers);

    [Fact]
    public async Task ARequest_GoesToTheAddressOrigin_WithTheCredentialAsABearerToken()
    {
        await using var service = new LocalHttpServer(Answering(200));
        _address = $"http://127.0.0.1:{service.Port}";
        var credentials = Credentials();
        credentials.Put(Credential);
        using var client = new ServiceClient(credentials, loopbackOnly: false);

        using var answer = await client.SendAsync(HttpMethod.Get, "/v1/data?q=1", ct: Ct);

        Assert.Null(answer.Refused);
        Assert.Equal(HttpStatusCode.OK, answer.Response!.StatusCode);
        var request = Assert.Single(service.Requests);
        Assert.Equal(("GET", "/v1/data?q=1"), (request.Method, request.Path));
        Assert.Equal($"Bearer {Credential}", request.Header("authorization"));
        Assert.Null(request.Header("cookie"));
    }

    [Fact]
    public async Task NoCredential_NoRequest()
    {
        await using var service = new LocalHttpServer(Answering(200));
        _address = $"http://127.0.0.1:{service.Port}";
        using var client = new ServiceClient(Credentials(), loopbackOnly: false);

        using var answer = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct);

        Assert.Equal(ServiceRefusal.NoCredential, answer.Refused);
        Assert.Null(answer.Response);
        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task NoAddress_NoRequest()
    {
        _address = null;
        var handler = new CountingHandler();
        using var client = new ServiceClient(Credentials(), loopbackOnly: false, time: null, handler);

        using var answer = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct);

        Assert.Equal(ServiceRefusal.NoService, answer.Refused);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task ARedirect_IsTheAnswer_NeverFollowed_SoItsLocationNeverGetsTheCredential()
    {
        await using var elsewhere = new LocalHttpServer(Answering(200));
        await using var service = new LocalHttpServer(Answering(302, $"Location: {elsewhere.Url("/collect")}"));
        _address = $"http://127.0.0.1:{service.Port}";
        var credentials = Credentials();
        credentials.Put(Credential);
        using var client = new ServiceClient(credentials, loopbackOnly: false);

        using var answer = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct);

        Assert.Equal(HttpStatusCode.Found, answer.Response!.StatusCode);
        Assert.Single(service.Requests);
        Assert.Empty(elsewhere.Requests);
    }

    [Theory]
    [InlineData("http://127.0.0.1:{other}/collect")]
    [InlineData("//127.0.0.1:{other}/collect")]
    [InlineData("/\\127.0.0.1:{other}/collect")]
    [InlineData("\\\\127.0.0.1:{other}/collect")]
    [InlineData("@127.0.0.1:{other}/collect")]
    [InlineData("v1/data")]
    [InlineData("/v1/data#x")]
    [InlineData("")]
    public async Task ARequestForAnotherOrigin_IsRefused_BeforeAnythingIsSent(string target)
    {
        await using var elsewhere = new LocalHttpServer(Answering(200));
        await using var service = new LocalHttpServer(Answering(200));
        _address = $"http://127.0.0.1:{service.Port}";
        var credentials = Credentials();
        credentials.Put(Credential);
        using var client = new ServiceClient(credentials, loopbackOnly: false);

        using var answer = await client.SendAsync(HttpMethod.Get, target.Replace("{other}", elsewhere.Port.ToString()), ct: Ct);

        Assert.Equal(ServiceRefusal.OtherOrigin, answer.Refused);
        Assert.Empty(service.Requests);
        Assert.Empty(elsewhere.Requests);
    }

    [Theory]
    [InlineData("http://10.0.0.1")]
    [InlineData("http://service.example")]
    [InlineData("https://service.example/api")]
    public async Task AnAddressThatIsntHttpsNorLoopbackHttp_GetsNoRequest(string address)
    {
        // The settings refuse such an address; the client checks again, whatever hands it one.
        _address = "https://service.example";
        var credentials = Credentials();
        credentials.Put(Credential);
        // The same record, kept for the address under test, as a store could hold it.
        FileServiceCredentialStore.ForPrivateFile(_temp.Path, Logger).Write(new StoredServiceCredential(address, Credential, false));
        _address = address;
        credentials = Credentials();
        var handler = new CountingHandler();
        using var client = new ServiceClient(credentials, loopbackOnly: false, time: null, handler);

        using var answer = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct);

        Assert.Equal(ServiceRefusal.Insecure, answer.Refused);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task Offline_OnlyALoopbackServiceGetsARequest()
    {
        _address = "https://service.example";
        var credentials = Credentials();
        credentials.Put(Credential);
        var handler = new CountingHandler();
        using var client = new ServiceClient(credentials, loopbackOnly: true, time: null, handler);

        using var answer = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct);

        Assert.Equal(ServiceRefusal.Offline, answer.Refused);
        Assert.Equal(0, handler.Count);

        await using var service = new LocalHttpServer(Answering(200));
        _address = $"http://localhost:{service.Port}";
        var local = Credentials();
        local.Put(Credential);
        using var loopback = new ServiceClient(local, loopbackOnly: true);
        using var answered = await loopback.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct);
        Assert.Equal(HttpStatusCode.OK, answered.Response!.StatusCode);
    }

    [Fact]
    public async Task A401_RejectsTheCredential_AndEveryLaterRequestIsRefusedHere_AcrossARestart_UntilANewOne()
    {
        var status = 401;
        await using var service = new LocalHttpServer((_, stream, _) => LocalHttpServer.Answer(stream, status, "{}"u8.ToArray(), "application/json"));
        _address = $"http://127.0.0.1:{service.Port}";
        var credentials = Credentials();
        credentials.Put(Credential);
        using var client = new ServiceClient(credentials, loopbackOnly: false);

        using (var refused = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct))
            Assert.Equal(HttpStatusCode.Unauthorized, refused.Response!.StatusCode);
        Assert.Equal(ServiceCredentialState.Rejected, credentials.Status.Credential);

        using (var after = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct))
            Assert.Equal(ServiceRefusal.Rejected, after.Refused);
        Assert.Single(service.Requests);

        // A restart keeps it rejected: still no request.
        var restarted = Credentials();
        using (var restartedClient = new ServiceClient(restarted, loopbackOnly: false))
        using (var afterRestart = await restartedClient.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct))
            Assert.Equal(ServiceRefusal.Rejected, afterRestart.Refused);
        Assert.Single(service.Requests);

        // A new credential: requests again.
        status = 200;
        restarted.Put("another-credential");
        using (var renewed = new ServiceClient(restarted, loopbackOnly: false))
        using (var answered = await renewed.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct))
            Assert.Equal(HttpStatusCode.OK, answered.Response!.StatusCode);
        Assert.Equal("Bearer another-credential", service.Requests[^1].Header("authorization"));
    }

    [Fact]
    public async Task AnAddressChangedWhileARequestIsPrepared_NeverGetsTheCredentialProvidedForTheOldOne()
    {
        await using var first = new LocalHttpServer(Answering(200));
        await using var second = new LocalHttpServer(Answering(200));
        _address = $"http://127.0.0.1:{first.Port}";
        var next = $"http://127.0.0.1:{second.Port}";
        // The seam: the address in effect is read through this. Armed, its next read answers the old address, and then
        // the settings publish the next one: as a settings change on another thread does, before its handler waits on the
        // credentials' lock to remove the credential.
        var armed = false;
        string? Address()
        {
            var now = _address;
            if (armed)
            {
                armed = false;
                _address = next;
            }

            return now;
        }

        var credentials = new ServiceCredentials(FileServiceCredentialStore.ForPrivateFile(_temp.Path, Logger), Address, Logger);
        credentials.Put(Credential);
        using var client = new ServiceClient(credentials, loopbackOnly: false);

        armed = true;
        using var answer = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct);

        Assert.False(armed);
        Assert.Empty(second.Requests);
        // If it went anywhere, it went to the address it was provided for.
        Assert.All(first.Requests, r => Assert.Equal($"Bearer {Credential}", r.Header("authorization")));
    }

    [Fact]
    public async Task ALate401ForARequestSentBefore_DoesntRejectTheSameCredentialProvidedAgain()
    {
        var (arrived, release) = (NewSignal(), NewSignal());
        await using var service = new LocalHttpServer(Gated(arrived, release));
        _address = $"http://127.0.0.1:{service.Port}";
        var credentials = Credentials();
        credentials.Put(Credential);
        using var client = new ServiceClient(credentials, loopbackOnly: false);

        // The service holds the first request's answer until the same credential has been provided again.
        var sending = client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct);
        await arrived.Task.WaitAsync(Ct);
        credentials.Put(Credential);
        release.SetResult();
        using (var late = await sending)
            Assert.Equal(HttpStatusCode.Unauthorized, late.Response!.StatusCode);

        Assert.Equal(ServiceCredentialState.Present, credentials.Status.Credential);
        Assert.Equal(ServiceCredentialState.Present, Credentials().Status.Credential);
        using var after = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct);
        Assert.Equal(HttpStatusCode.OK, after.Response!.StatusCode);
    }

    [Fact]
    public async Task ALate401FromTheOldAddress_DoesntRejectTheCredentialProvidedForTheNewOne()
    {
        var (arrived, release) = (NewSignal(), NewSignal());
        await using var old = new LocalHttpServer(Gated(arrived, release));
        await using var service = new LocalHttpServer(Answering(200));
        _address = $"http://127.0.0.1:{old.Port}";
        var credentials = Credentials();
        credentials.Put(Credential);
        using var client = new ServiceClient(credentials, loopbackOnly: false);

        // The old address holds its answer until the address has changed and the same text was provided for the new one.
        var sending = client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct);
        await arrived.Task.WaitAsync(Ct);
        _address = $"http://127.0.0.1:{service.Port}";
        credentials.AddressChanged();
        credentials.Put(Credential);
        release.SetResult();
        using (var late = await sending)
            Assert.Equal(HttpStatusCode.Unauthorized, late.Response!.StatusCode);

        Assert.Equal(ServiceCredentialState.Present, credentials.Status.Credential);
        using var after = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct);
        Assert.Equal(HttpStatusCode.OK, after.Response!.StatusCode);
        Assert.Equal($"Bearer {Credential}", Assert.Single(service.Requests).Header("authorization"));
    }

    [Fact]
    public async Task ACredentialNoLongerKept_IsSentToTheAddressItWasKeptFor_AndA401RejectsNothing()
    {
        await using var service = new LocalHttpServer(Answering(401));
        _address = $"http://127.0.0.1:{service.Port}";
        var credentials = Credentials();
        credentials.Put(Credential);
        using var client = new ServiceClient(credentials, loopbackOnly: true);

        using var answer = await client.SendWithAsync(new ReplacedServiceCredential(_address, "replaced-credential"), HttpMethod.Delete, "/v1/data", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, answer.Response!.StatusCode);
        var request = Assert.Single(service.Requests);
        Assert.Equal(("DELETE", "/v1/data", "Bearer replaced-credential"), (request.Method, request.Path, request.Header("authorization")));
        // The kept credential isn't the one the service refused.
        Assert.Equal(ServiceCredentialState.Present, credentials.Status.Credential);
        Assert.Equal(ServiceCredentialState.Present, Credentials().Status.Credential);
    }

    [Fact]
    public async Task ACredentialNoLongerKept_IsSentOnlyWhileItsAddressIsInEffect_AndOfflineOnlyToALoopbackOne()
    {
        await using var old = new LocalHttpServer(Answering(204));
        await using var service = new LocalHttpServer(Answering(204));
        var kept = $"http://127.0.0.1:{old.Port}";
        _address = $"http://127.0.0.1:{service.Port}";
        var handler = new CountingHandler();
        using (var client = new ServiceClient(Credentials(), loopbackOnly: false))
        using (var answer = await client.SendWithAsync(new ReplacedServiceCredential(kept, "replaced-credential"), HttpMethod.Delete, "/v1/data", Ct))
            Assert.Equal(ServiceRefusal.OtherOrigin, answer.Refused);
        Assert.Empty(old.Requests);
        Assert.Empty(service.Requests);

        _address = "https://service.example";
        using (var offline = new ServiceClient(Credentials(), loopbackOnly: true, time: null, handler))
        using (var answer = await offline.SendWithAsync(new ReplacedServiceCredential(_address, "replaced-credential"), HttpMethod.Delete, "/v1/data", Ct))
            Assert.Equal(ServiceRefusal.Offline, answer.Refused);
        Assert.Equal(0, handler.Count);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A service whose first request sets <paramref name="arrived"/> and is answered 401 once <paramref name="release"/>
    /// is set; every later one is answered 200 at once.
    /// </summary>
    private static Func<ServedRequest, Stream, CancellationToken, Task> Gated(TaskCompletionSource arrived, TaskCompletionSource release)
    {
        var calls = 0;
        return async (_, stream, ct) =>
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                await LocalHttpServer.Answer(stream, 200, "{}"u8.ToArray(), "application/json");
                return;
            }

            arrived.SetResult();
            await release.Task.WaitAsync(ct);
            await LocalHttpServer.Answer(stream, 401, "{}"u8.ToArray(), "application/json");
        };
    }

    [Fact]
    public async Task A429OrA503_HandsBackItsRetryAfter_InSecondsOrAsADate()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var answers = new Queue<(int, string[])>([
            (429, ["Retry-After: 120"]),
            (503, ["Retry-After: Wed, 04 Nov 2026 12:05:00 GMT"]),
            (503, []),
            (500, ["Retry-After: 30"]),
        ]);
        await using var service = new LocalHttpServer((_, stream, _) =>
        {
            var (status, headers) = answers.Dequeue();
            return LocalHttpServer.Answer(stream, status, "{}"u8.ToArray(), "application/json", headers);
        });
        _address = $"http://127.0.0.1:{service.Port}";
        var credentials = Credentials();
        credentials.Put(Credential);
        using var client = new ServiceClient(credentials, loopbackOnly: false, time);

        using (var seconds = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct))
            Assert.Equal(TimeSpan.FromSeconds(120), seconds.RetryAfter);
        using (var date = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct))
            Assert.Equal(TimeSpan.FromMinutes(5), date.RetryAfter);
        using (var none = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct))
            Assert.Null(none.RetryAfter);
        using (var other = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: Ct))
            Assert.Null(other.RetryAfter);
        Assert.Equal(ServiceCredentialState.Present, credentials.Status.Credential);
    }

    /// <summary>A handler that counts what reaches it and answers 200: a request that got this far would have been sent.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

/// <summary><see cref="HttpClient.DefaultProxy"/> is the process's: the tests that change it run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessProxyCollection
{
    public const string Name = "The process's proxy";
}

/// <summary>A proxy the environment names (<c>http_proxy</c>, <c>all_proxy</c>) never gets a request to the service.</summary>
[Collection(ProcessProxyCollection.Name)]
public sealed class ServiceClientProxyTests : IDisposable
{
    private const string Credential = "test-credential.A1b2~+/==";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProxyTheEnvironmentNames_GetsNoRequest_SoNeverTheCredential(bool offline)
    {
        await using var proxy = new LocalHttpServer((_, stream, _) => LocalHttpServer.Answer(stream, 200, "{}"u8.ToArray(), "application/json"));
        await using var service = new LocalHttpServer((_, stream, _) => LocalHttpServer.Answer(stream, 200, "{}"u8.ToArray(), "application/json"));
        var address = $"http://127.0.0.1:{service.Port}";
        var credentials = new ServiceCredentials(FileServiceCredentialStore.ForPrivateFile(_temp.Path, Serilog.Core.Logger.None), () => address,
            Serilog.Core.Logger.None);
        credentials.Put(Credential);

        var previous = HttpClient.DefaultProxy;
        // What http_proxy and all_proxy make it: every host through the proxy, loopback ones too (the environment's proxy
        // has no loopback bypass of its own).
        HttpClient.DefaultProxy = new EveryHostProxy(new Uri(proxy.Url("/")));
        try
        {
            using var client = new ServiceClient(credentials, loopbackOnly: offline);
            using var answer = await client.SendAsync(HttpMethod.Get, "/v1/data", ct: TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, answer.Response!.StatusCode);
        }
        finally
        {
            HttpClient.DefaultProxy = previous;
        }

        Assert.Empty(proxy.Requests);
        Assert.Equal($"Bearer {Credential}", Assert.Single(service.Requests).Header("authorization"));
    }

    private sealed class EveryHostProxy(Uri proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }
}
