using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Images;
using InfoPanel.Hosting;
using InfoPanel.Security;
using InfoPanel.Service;
using InfoPanel.Settings;
using InfoPanel.Tests.Desktop;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// An engine and a fake configured service on a loopback port that lists the service's public keys at
/// <c>/svc/v1/pairing-keys</c>, for <c>POST /auth/pairing/pass</c> (spec/docs/api.md, "Pairing through the configured
/// service"): the engine's own data folder, <c>service.pairing</c> on (unless asked), <c>proxy.hosts</c> holding
/// <see cref="Host"/>, a credential with the id <see cref="DeviceId"/>, and the pass checks on a clock the test moves.
/// </summary>
internal sealed class PassRig : IAsyncDisposable
{
    public const string Host = "pc.tailnet-1234.ts.net";
    public const string DeviceId = "dev_TESTDEVICE0000AB";
    public const string Credential = "ipd_test-credential";
    public const string Kid = "key-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<(string Kid, ECDsa Key)> _listed = [];
    private readonly Lock _lock = new();

    private PassRig(EngineFactory factory)
    {
        Keys = new LocalHttpServer(AnswerAsync);
        var data = Path.Combine(factory.Root, $"pass-{Guid.NewGuid():N}");
        Directory.CreateDirectory(data);
        DataDir = data;
        Engine = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Engine:DataDir", data);
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton(sp => new PairingKeys(() => sp.GetRequiredService<ServiceCredentials>().Address, loopbackOnly: true, Time));
                services.AddSingleton(sp => new PairingPasses(sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<ServiceCredentials>(),
                    sp.GetRequiredService<PairingKeys>(), Time, sp.GetRequiredService<ILogger<PairingPasses>>()));
            });
        });
        Session = Engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        Session.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
        Anonymous = Engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        List(Kid, Key);
    }

    public ManualTime Clock { get; } = new(new DateTimeOffset(2026, 11, 6, 9, 30, 0, TimeSpan.Zero));

    /// <summary>The checks' time: <see cref="Clock"/>, whose wall clock alone can also be set back (<see cref="PassTime.SetBack"/>).</summary>
    public PassTime Time => _time ??= new PassTime(Clock);

    private PassTime? _time;

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public LocalHttpServer Keys { get; }

    public WebApplicationFactory<Program> Engine { get; }

    public string DataDir { get; }

    public HttpClient Session { get; }

    public HttpClient Anonymous { get; }

    /// <summary>When set, the fake service answers the keys' request with this instead of the list.</summary>
    public Func<ServedRequest, Stream, CancellationToken, Task>? Answer { get; set; }

    /// <summary>The requests the engine made for the keys.</summary>
    public int KeyRequests => Keys.Requests.Count;

    public long Now => Time.GetUtcNow().ToUnixTimeSeconds();

    public static async Task<PassRig> StartAsync(EngineFactory factory, bool pairing = true, string? deviceId = DeviceId)
    {
        var rig = new PassRig(factory);
        var address = $"http://127.0.0.1:{rig.Keys.Port}";
        await rig.PatchAsync($$$"""{"service":{"address":"{{{address}}}","pairing":{{{(pairing ? "true" : "false")}}}},"proxy":{"hosts":["{{{Host}}}"]}}""");
        using var put = await rig.Session.PutAsync("/api/service/credential",
            JsonContent.Create(deviceId is null ? (object)new { credential = Credential } : new { credential = Credential, id = deviceId }), Ct);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        return rig;
    }

    public async Task PatchAsync(string change)
    {
        using var patched = await Session.PatchAsync("/api/settings", new StringContent(change, Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
    }

    /// <summary>Lists <paramref name="key"/> under <paramref name="kid"/> on the service's keys.</summary>
    public void List(string kid, ECDsa key)
    {
        lock (_lock)
            _listed.Add((kid, key));
    }

    public void Unlist(string kid)
    {
        lock (_lock)
            _listed.RemoveAll(k => k.Kid == kid);
    }

    private Task AnswerAsync(ServedRequest request, Stream stream, CancellationToken ct)
    {
        if (Answer is { } answer)
            return answer(request, stream, ct);
        if (request.Path != "/svc/v1/pairing-keys")
            return LocalHttpServer.Answer(stream, 404, [], null);
        string body;
        lock (_lock)
            body = JsonSerializer.Serialize(new { keys = _listed.Select(k => Jwk(k.Kid, k.Key)).ToArray() });
        return LocalHttpServer.Answer(stream, 200, Encoding.UTF8.GetBytes(body), "application/json", "Cache-Control: public, max-age=3600");
    }

    public static object Jwk(string kid, ECDsa key)
    {
        var p = key.ExportParameters(includePrivateParameters: false);
        return new { kid, kty = "EC", crv = "P-256", x = B64(p.Q.X!), y = B64(p.Q.Y!) };
    }

    public static string B64(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    public static string B64(string json) => B64(Encoding.UTF8.GetBytes(json));

    public static string NewNonce() => B64(RandomNumberGenerator.GetBytes(16));

    public static string Header(string kid = Kid) => $$"""{"alg":"ES256","kid":"{{kid}}","typ":"infopanel-pairing-pass"}""";

    public string Payload(string aud = "infopanel-pairing", string device = DeviceId, string host = Host, string? nonce = null, long? exp = null) =>
        $$$"""{"v":1,"aud":"{{{aud}}}","device":"{{{device}}}","host":"{{{host}}}","nonce":"{{{nonce ?? NewNonce()}}}","exp":{{{exp ?? Now + 60}}}}""";

    /// <summary>A pass of <paramref name="header"/> and <paramref name="payload"/> as given, signed with <paramref name="key"/>.</summary>
    public string Sign(string header, string payload, ECDsa? key = null) => Sign(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes(payload), key);

    /// <summary>A pass of <paramref name="header"/> and <paramref name="payload"/>'s bytes as given (UTF-8 or not), signed with <paramref name="key"/>.</summary>
    public string Sign(byte[] header, byte[] payload, ECDsa? key = null)
    {
        var input = B64(header) + "." + B64(payload);
        return input + "." + B64((key ?? Key).SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256));
    }

    /// <summary>The service's list of keys, as it answers it.</summary>
    public Task AnswerListAsync(Stream stream)
    {
        string body;
        lock (_lock)
            body = JsonSerializer.Serialize(new { keys = _listed.Select(k => Jwk(k.Kid, k.Key)).ToArray() });
        return LocalHttpServer.Answer(stream, 200, Encoding.UTF8.GetBytes(body), "application/json");
    }

    public string Valid(string? nonce = null) => Sign(Header(), Payload(nonce: nonce));

    /// <summary>Posts <paramref name="pass"/> as a client at <paramref name="host"/> would, from the cross origin.</summary>
    public async Task<HttpResponseMessage> PostAsync(string pass, string host = Host, string name = "Chrome over Tailscale", string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/pairing/pass") { Content = JsonContent.Create(new { pass, name }) };
        request.Headers.Host = host;
        if (origin != null)
            request.Headers.Add("Origin", origin);
        return await Anonymous.SendAsync(request, Ct);
    }

    /// <summary>The one word a refusal carries (its <c>code</c>), after checking it is 403 with no detail beyond it.</summary>
    public async Task<string> RefusedAsync(string pass, string host = Host)
    {
        using var refused = await PostAsync(pass, host);
        return await RefusalAsync(refused);
    }

    /// <summary>As <see cref="RefusedAsync"/>, for an answer already had.</summary>
    public static async Task<string> RefusalAsync(HttpResponseMessage refused)
    {
        var text = await refused.Content.ReadAsStringAsync(Ct);
        Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{(int)refused.StatusCode} {text}");
        Assert.Equal("no-store", refused.Headers.CacheControl?.ToString());
        var problem = JsonDocument.Parse(text).RootElement;
        var code = problem.GetProperty("code").GetString()!;
        Assert.Equal($"The pairing pass was refused: {code}.", problem.GetProperty("detail").GetString());
        return code;
    }

    public async Task AcceptedAsync(string pass, string host = Host)
    {
        using var granted = await PostAsync(pass, host);
        Assert.True(granted.StatusCode == HttpStatusCode.Created, $"{(int)granted.StatusCode} {await granted.Content.ReadAsStringAsync(Ct)}");
    }

    public async ValueTask DisposeAsync()
    {
        Session.Dispose();
        Anonymous.Dispose();
        await Engine.DisposeAsync();
        await Keys.DisposeAsync();
        Key.Dispose();
    }
}

/// <summary>
/// <see cref="ManualTime"/>, with its wall clock movable on its own: a computer's clock set back moves the time of day,
/// never the monotonic clock (<see cref="TimeProvider.GetTimestamp"/>).
/// </summary>
internal sealed class PassTime(ManualTime clock) : TimeProvider
{
    private long _back;

    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow().AddTicks(-Interlocked.Read(ref _back));

    public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;

    public override long TimestampFrequency => clock.TimestampFrequency;

    public override long GetTimestamp() => clock.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        clock.CreateTimer(callback, state, dueTime, period);

    /// <summary>Sets the wall clock back by <paramref name="by"/>; the monotonic clock doesn't move.</summary>
    public void SetBack(TimeSpan by) => Interlocked.Add(ref _back, by.Ticks);
}

/// <summary>
/// <c>POST /auth/pairing/pass</c> (spec/docs/api.md, "Pairing through the configured service"): a pass the configured
/// service signed pairs a client as a code does, after these checks in order, each refusal one word: the switch
/// (<c>off</c>), the form (<c>malformed</c>), the signature against the service's key for its <c>kid</c>
/// (<c>signature</c>), <c>aud</c> (<c>audience</c>), <c>exp</c> with 30 s of skew (<c>expired</c>), <c>host</c> against the
/// request's <c>Host</c> and <c>proxy.hosts</c> (<c>host</c>), <c>device</c> against the id of the engine's credential
/// (<c>device</c>), the nonce once (<c>replayed</c>). The keys are fetched without a credential, cached by <c>kid</c>,
/// fetched again once for an unknown <c>kid</c>, at most once a minute. Nothing logged holds the pass.
/// </summary>
public class PairingPassTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AValidPass_PairsAsACodeDoes_FromTheCrossOrigin()
    {
        await using var rig = await PassRig.StartAsync(factory);

        using var granted = await rig.PostAsync(rig.Valid(), origin: EngineFactory.CrossOrigin);

        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        Assert.Equal("no-store", granted.Headers.CacheControl?.ToString());
        Assert.Equal(EngineFactory.CrossOrigin, granted.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var grant = await granted.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(EngineFactory.Token, grant.GetProperty("token").GetString());
        var session = await rig.Session.GetFromJsonAsync<JsonElement>("/api/session", Ct);
        Assert.Equal(session.GetProperty("instance").GetString(), grant.GetProperty("instance").GetString());
        var pairing = grant.GetProperty("pairing");
        Assert.Equal("Chrome over Tailscale", pairing.GetProperty("name").GetString());
        Assert.Matches("^[A-Za-z0-9_-]{43}$", pairing.GetProperty("secret").GetString()!);
        var listed = await rig.Session.GetFromJsonAsync<JsonElement>("/api/pairings", Ct);
        Assert.Contains(listed.EnumerateArray(), p => p.GetProperty("id").GetString() == pairing.GetProperty("id").GetString());

        // The keys were asked for once, at the service's address, without the credential.
        var asked = Assert.Single(rig.Keys.Requests);
        Assert.Equal(("GET", "/svc/v1/pairing-keys"), (asked.Method, asked.Path));
        Assert.Null(asked.Header("authorization"));
        Assert.Null(asked.Header("cookie"));
    }

    [Fact]
    public async Task Replaces_HandsTheOldPairingOver_AsACodesExchangeDoes()
    {
        await using var rig = await PassRig.StartAsync(factory);
        using var first = await rig.PostAsync(rig.Valid());
        var old = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pairing").GetProperty("id").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/pairing/pass")
        {
            Content = JsonContent.Create(new { pass = rig.Valid(), name = "Chrome over Tailscale", replaces = old }),
        };
        request.Headers.Host = PassRig.Host;
        using var second = await rig.Anonymous.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var listed = await rig.Session.GetFromJsonAsync<JsonElement>("/api/pairings", Ct);
        Assert.DoesNotContain(listed.EnumerateArray(), p => p.GetProperty("id").GetString() == old);
    }

    [Fact]
    public async Task Off_RefusesEveryPass_TheSwitchReadAtEachRequest_AndAsksForNoKeys()
    {
        await using var rig = await PassRig.StartAsync(factory, pairing: false);

        Assert.Equal("off", await rig.RefusedAsync(rig.Valid()));
        // Off first, whatever the pass or the body.
        Assert.Equal("off", await rig.RefusedAsync("not a pass"));
        Assert.Equal(0, rig.KeyRequests);

        await rig.PatchAsync("""{"service":{"pairing":true}}""");
        await rig.AcceptedAsync(rig.Valid());

        await rig.PatchAsync("""{"service":{"pairing":null}}""");
        Assert.Equal("off", await rig.RefusedAsync(rig.Valid()));
    }

    [Fact]
    public async Task ASignatureThatDoesntCheck_IsRefused()
    {
        await using var rig = await PassRig.StartAsync(factory);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // Signed by another key under the listed kid.
        Assert.Equal("signature", await rig.RefusedAsync(rig.Sign(PassRig.Header(), rig.Payload(), other)));
        // The payload changed after signing (another device).
        var signed = rig.Valid().Split('.');
        var forged = PassRig.B64(rig.Payload(device: "dev_SOMEONEELSE00000"));
        Assert.Equal("signature", await rig.RefusedAsync($"{signed[0]}.{forged}.{signed[2]}"));
        // The signature's bytes changed.
        var flipped = System.Buffers.Text.Base64Url.DecodeFromChars(signed[2]);
        flipped[10] ^= 1;
        Assert.Equal("signature", await rig.RefusedAsync($"{signed[0]}.{signed[1]}.{PassRig.B64(flipped)}"));
        // A kid the service doesn't list, even after asking again.
        Assert.Equal("signature", await rig.RefusedAsync(rig.Sign(PassRig.Header("key-unknown"), rig.Payload())));

        await rig.AcceptedAsync(rig.Valid());
    }

    public static TheoryData<string> MalformedPasses => new()
    {
        "",
        "abc",
        "a.b",
        "a.b.c.d",
        "a..c",
        "a b.c.d",
        "YQ==.YQ.YQ",
    };

    [Theory]
    [MemberData(nameof(MalformedPasses))]
    public async Task APassNotInItsForm_IsMalformed(string pass)
    {
        await using var rig = await PassRig.StartAsync(factory);
        Assert.Equal("malformed", await rig.RefusedAsync(pass));
        Assert.Equal(0, rig.KeyRequests);
    }

    public static TheoryData<string> MalformedHeaders => new()
    {
        """{"alg":"HS256","kid":"key-1","typ":"infopanel-pairing-pass"}""",
        """{"alg":"none","kid":"key-1","typ":"infopanel-pairing-pass"}""",
        """{"alg":"ES256","kid":"key-1","typ":"JWT"}""",
        """{"alg":"ES256","kid":"key-1"}""",
        """{"alg":"ES256","typ":"infopanel-pairing-pass"}""",
        """{"alg":"ES256","kid":"key-1","typ":"infopanel-pairing-pass","jku":"https://elsewhere.example"}""",
        """{"alg":"ES256","kid":"key-1","typ":"infopanel-pairing-pass","alg":"ES256"}""",
        """{"alg":"ES256","kid":1,"typ":"infopanel-pairing-pass"}""",
        """{"alg":"ES256","kid":"","typ":"infopanel-pairing-pass"}""",
        """["ES256"]""",
        """not json""",
    };

    [Theory]
    [MemberData(nameof(MalformedHeaders))]
    public async Task AHeaderOtherThanTheFormat_IsMalformed_AndNoKeyIsAskedFor(string header)
    {
        await using var rig = await PassRig.StartAsync(factory);
        Assert.Equal("malformed", await rig.RefusedAsync(rig.Sign(header, rig.Payload())));
        Assert.Equal(0, rig.KeyRequests);
    }

    [Fact]
    public async Task ASignatureOfAnotherLength_IsMalformed()
    {
        await using var rig = await PassRig.StartAsync(factory);
        var signed = rig.Valid().Split('.');
        var der = rig.Key.SignData(Encoding.ASCII.GetBytes(signed[0] + "." + signed[1]), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.Equal("malformed", await rig.RefusedAsync($"{signed[0]}.{signed[1]}.{PassRig.B64(der)}"));
        Assert.Equal("malformed", await rig.RefusedAsync($"{signed[0]}.{signed[1]}.{PassRig.B64(new byte[63])}"));
    }

    public static TheoryData<string> MalformedPayloads => new()
    {
        """{"v":2,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW}""",
        """{"v":"1","aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW}""",
        """{"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW}""",
        """{"v":1,"device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW}""",
        """{"v":1,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA"}""",
        """{"v":1,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW,"iat":NOW}""",
        """{"v":1,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW,"exp":NOW}""",
        """{"v":1,"aud":["infopanel-pairing"],"device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW}""",
        """{"v":1,"aud":"infopanel-pairing","device":7,"host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW}""",
        """{"v":1,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":null,"nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW}""",
        """{"v":1,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"short","exp":NOW}""",
        """{"v":1,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAA=","exp":NOW}""",
        """{"v":1,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":"NOW"}""",
        """{"v":1,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW.5}""",
        """{"v":1,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":LATE}""",
        """["infopanel-pairing"]""",
        """not json""",
    };

    [Theory]
    [MemberData(nameof(MalformedPayloads))]
    public async Task APayloadOtherThanTheFormat_SignedAllTheSame_IsMalformed(string payload)
    {
        await using var rig = await PassRig.StartAsync(factory);
        // NOW: within its minute; LATE: further ahead than a minute and the 30 s of skew.
        var text = payload.Replace("LATE", (rig.Now + 91).ToString()).Replace("NOW", (rig.Now + 60).ToString());
        Assert.Equal("malformed", await rig.RefusedAsync(rig.Sign(PassRig.Header(), text)));
    }

    [Fact]
    public async Task AnotherAudience_IsRefused()
    {
        await using var rig = await PassRig.StartAsync(factory);
        Assert.Equal("audience", await rig.RefusedAsync(rig.Sign(PassRig.Header(), rig.Payload(aud: "infopanel-session"))));
    }

    [Fact]
    public async Task Exp_HoldsWithThirtySecondsOfSkew_EitherWay()
    {
        await using var rig = await PassRig.StartAsync(factory);

        Assert.Equal("expired", await rig.RefusedAsync(rig.Sign(PassRig.Header(), rig.Payload(exp: rig.Now - 31))));
        await rig.AcceptedAsync(rig.Sign(PassRig.Header(), rig.Payload(exp: rig.Now - 30)));
        await rig.AcceptedAsync(rig.Sign(PassRig.Header(), rig.Payload(exp: rig.Now + 90)));
        Assert.Equal("malformed", await rig.RefusedAsync(rig.Sign(PassRig.Header(), rig.Payload(exp: rig.Now + 91))));

        // A pass becomes expired as the clock passes its exp and the skew.
        var pass = rig.Sign(PassRig.Header(), rig.Payload(exp: rig.Now + 60));
        rig.Clock.Advance(TimeSpan.FromSeconds(91));
        Assert.Equal("expired", await rig.RefusedAsync(pass));
    }

    [Fact]
    public async Task Host_IsTheRequestsHost_AndAProxyName()
    {
        await using var rig = await PassRig.StartAsync(factory);
        await rig.PatchAsync($$$"""{"proxy":{"hosts":["{{{PassRig.Host}}}","other.tailnet-1234.ts.net"]}}""");

        // Another proxy name than the request's.
        Assert.Equal("host", await rig.RefusedAsync(rig.Valid(), host: "other.tailnet-1234.ts.net"));
        // The request's name, but not one of proxy.hosts.
        Assert.Equal("host", await rig.RefusedAsync(rig.Sign(PassRig.Header(), rig.Payload(host: "localhost")), host: "localhost"));
        // A longer name ending with it.
        Assert.Equal("host", await rig.RefusedAsync(rig.Sign(PassRig.Header(), rig.Payload(host: "x." + PassRig.Host))));

        // The port and the case of the request's Host don't matter.
        await rig.AcceptedAsync(rig.Valid(), host: PassRig.Host.ToUpperInvariant() + ":443");
        await rig.AcceptedAsync(rig.Sign(PassRig.Header(), rig.Payload(host: "other.tailnet-1234.ts.net")), host: "other.tailnet-1234.ts.net");

        // Read at each request: a name removed from proxy.hosts no longer pairs (here the host rule answers first).
        await rig.PatchAsync("""{"proxy":{"hosts":["other.tailnet-1234.ts.net"]}}""");
        using var gone = await rig.PostAsync(rig.Valid());
        Assert.Equal(HttpStatusCode.BadRequest, gone.StatusCode);
    }

    [Fact]
    public async Task Device_IsTheIdOfTheCredentialTheEngineKeeps()
    {
        await using var rig = await PassRig.StartAsync(factory);

        Assert.Equal("device", await rig.RefusedAsync(rig.Sign(PassRig.Header(), rig.Payload(device: "dev_SOMEONEELSE00000"))));
        await rig.AcceptedAsync(rig.Valid());

        // Another credential, another id: the old one's passes no longer pair.
        using (var put = await rig.Session.PutAsync("/api/service/credential", JsonContent.Create(new { credential = "ipd_another", id = "dev_ANOTHER0000000AB" }), Ct))
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("device", await rig.RefusedAsync(rig.Valid()));
        await rig.AcceptedAsync(rig.Sign(PassRig.Header(), rig.Payload(device: "dev_ANOTHER0000000AB")));

        // A credential provided without an id: no pass names it.
        using (var put = await rig.Session.PutAsync("/api/service/credential", JsonContent.Create(new { credential = "ipd_another" }), Ct))
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("device", await rig.RefusedAsync(rig.Sign(PassRig.Header(), rig.Payload(device: "dev_ANOTHER0000000AB"))));

        // No credential at all.
        using (var deleted = await rig.Session.DeleteAsync("/api/service/credential", Ct))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("device", await rig.RefusedAsync(rig.Valid()));
    }

    [Fact]
    public async Task ThePass_PairsOnce_ANonceIsRememberedUntilItsExpAndTheSkew()
    {
        await using var rig = await PassRig.StartAsync(factory);
        var pass = rig.Valid();

        using var first = await rig.PostAsync(pass);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pairing").GetProperty("id").GetString();
        Assert.Equal("replayed", await rig.RefusedAsync(pass));
        // The same nonce in another pass, signed afresh.
        var nonce = PassRig.NewNonce();
        await rig.AcceptedAsync(rig.Valid(nonce));
        Assert.Equal("replayed", await rig.RefusedAsync(rig.Valid(nonce)));
        // The first use's pairing stays: a pass is only ever used by whoever holds it.
        var listed = await rig.Session.GetFromJsonAsync<JsonElement>("/api/pairings", Ct);
        Assert.Contains(listed.EnumerateArray(), p => p.GetProperty("id").GetString() == id);

        // Still refused at the last second of the skew; expired after it, never paired again.
        rig.Clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal("replayed", await rig.RefusedAsync(pass));
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("expired", await rig.RefusedAsync(pass));
    }

    [Fact]
    public void TheNonces_AreBounded_AndForgottenOnlyAfterTheirTime()
    {
        var nonces = new PairingNonces(capacity: 3);

        Assert.True(nonces.TryAdd("a", until: 100, now: 10));
        Assert.False(nonces.TryAdd("a", until: 100, now: 100));
        Assert.True(nonces.TryAdd("b", until: 50, now: 10));
        Assert.True(nonces.TryAdd("c", until: 100, now: 10));
        // Full: a new nonce is refused, never one remembered dropped for it.
        Assert.False(nonces.TryAdd("d", until: 100, now: 20));
        Assert.False(nonces.TryAdd("a", until: 100, now: 20));
        // Past b's time, its room is free; a is still remembered until its own.
        Assert.True(nonces.TryAdd("d", until: 100, now: 51));
        Assert.False(nonces.TryAdd("a", until: 100, now: 100));
        Assert.True(nonces.TryAdd("a", until: 200, now: 101));
    }

    [Fact]
    public async Task TheKeys_AreCachedByKid_AskedForAgainOnceForAnUnknownKid_AtMostOnceAMinute()
    {
        await using var rig = await PassRig.StartAsync(factory);
        await rig.AcceptedAsync(rig.Valid());
        await rig.AcceptedAsync(rig.Valid());
        Assert.Equal(1, rig.KeyRequests);

        // A new key, listed after the engine read the list: one pass with its kid asks again, and pairs.
        using var rotated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        rig.List("key-2", rotated);
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.AcceptedAsync(rig.Sign(PassRig.Header("key-2"), rig.Payload(), rotated));
        Assert.Equal(2, rig.KeyRequests);
        await rig.AcceptedAsync(rig.Valid());
        Assert.Equal(2, rig.KeyRequests);

        // Unknown kids within the minute: one more request at most, then none until the minute has passed.
        using var later = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal("signature", await rig.RefusedAsync(rig.Sign(PassRig.Header("key-3"), rig.Payload(), later)));
        Assert.Equal(2, rig.KeyRequests);
        rig.List("key-3", later);
        Assert.Equal("signature", await rig.RefusedAsync(rig.Sign(PassRig.Header("key-3"), rig.Payload(), later)));
        Assert.Equal(2, rig.KeyRequests);
        rig.Clock.Advance(TimeSpan.FromSeconds(61));
        await rig.AcceptedAsync(rig.Sign(PassRig.Header("key-3"), rig.Payload(), later));
        Assert.Equal(3, rig.KeyRequests);
    }

    [Fact]
    public async Task TheList_IsReadAgainAfterAnHour_SoAKeyTheServiceDropped_StopsPairing()
    {
        await using var rig = await PassRig.StartAsync(factory);
        await rig.AcceptedAsync(rig.Valid());
        rig.Unlist(PassRig.Kid);

        rig.Clock.Advance(TimeSpan.FromMinutes(59));
        await rig.AcceptedAsync(rig.Valid());
        Assert.Equal(1, rig.KeyRequests);

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("signature", await rig.RefusedAsync(rig.Valid()));
        Assert.Equal(2, rig.KeyRequests);
    }

    public static TheoryData<string> BadKeyAnswers => new()
    {
        "500",
        "redirect",
        "huge",
        "not-json",
        "no-keys",
        "off-curve",
        "trailing",
    };

    [Theory]
    [MemberData(nameof(BadKeyAnswers))]
    public async Task AKeysAnswerThatIsntAList_PairsNothing(string answer)
    {
        await using var rig = await PassRig.StartAsync(factory);
        var redirected = 0;
        rig.Answer = (request, stream, _) =>
        {
            if (request.Path == "/elsewhere")
            {
                Interlocked.Increment(ref redirected);
                return LocalHttpServer.Answer(stream, 200, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { keys = new[] { PassRig.Jwk(PassRig.Kid, rig.Key) } })), "application/json");
            }

            var p = rig.Key.ExportParameters(false);
            var bad = (byte[])p.Q.X!.Clone();
            bad[0] ^= 0x55;
            return answer switch
            {
                "500" => LocalHttpServer.Answer(stream, 500, [], null),
                "redirect" => LocalHttpServer.Answer(stream, 302, [], null, "Location: /elsewhere"),
                "huge" => LocalHttpServer.Answer(stream, 200, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    keys = new[] { PassRig.Jwk(PassRig.Kid, rig.Key) },
                    padding = new string('x', PairingKeys.MaxBytes),
                })), "application/json"),
                "not-json" => LocalHttpServer.Answer(stream, 200, "<html>"u8.ToArray(), "text/html"),
                "no-keys" => LocalHttpServer.Answer(stream, 200, """{"keys":[]}"""u8.ToArray(), "application/json"),
                "trailing" => LocalHttpServer.Answer(stream, 200, [.. Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    keys = new[] { PassRig.Jwk(PassRig.Kid, rig.Key) },
                })), (byte)'x'], "application/json"),
                _ => LocalHttpServer.Answer(stream, 200, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    keys = new[] { new { kid = PassRig.Kid, kty = "EC", crv = "P-256", x = PassRig.B64(bad), y = PassRig.B64(p.Q.Y!) } },
                })), "application/json"),
            };
        };

        Assert.Equal("signature", await rig.RefusedAsync(rig.Valid()));
        Assert.Equal(0, redirected);
    }

    [Fact]
    public async Task AKeysAnswerThatNeverComes_IsGivenUpOn()
    {
        await using var rig = await PassRig.StartAsync(factory);
        rig.Answer = async (_, _, ct) => await Task.Delay(Timeout.Infinite, ct);

        var started = DateTime.UtcNow;
        Assert.Equal("signature", await rig.RefusedAsync(rig.Valid()));
        Assert.True(DateTime.UtcNow - started < PairingKeys.FetchTimeout + TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ARequestBodyBreakingItsDefinition_Is422_WhileTheSwitchIsOn()
    {
        await using var rig = await PassRig.StartAsync(factory);
        foreach (var body in new[] { "{}", """{"pass":"a.b.c"}""", """{"pass":"a.b.c","name":" bad"}""", "not json" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/pairing/pass") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Host = PassRig.Host;
            using var refused = await rig.Anonymous.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        }
    }

    [Fact]
    public async Task APassIsLoggedAsAcceptedOrRefused_NeverItsContent()
    {
        await using var rig = await PassRig.StartAsync(factory);
        var nonce = PassRig.NewNonce();
        var accepted = rig.Valid(nonce);
        var forged = rig.Sign(PassRig.Header(), rig.Payload(device: "dev_SOMEONEELSE00000"));
        var recent = rig.Engine.Services.GetRequiredService<RecentLog>();

        await rig.AcceptedAsync(accepted);
        Assert.Equal("replayed", await rig.RefusedAsync(accepted));
        Assert.Equal("device", await rig.RefusedAsync(forged));

        var entries = recent.After(0);
        Assert.Contains(entries, e => e.Message.Contains("A pairing pass was accepted", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Message.Contains("A pairing pass was refused (replayed)", StringComparison.Ordinal));
        var logged = string.Join("\n", entries.Select(e => e.Message + e.Exception));
        foreach (var file in Directory.GetFiles(Path.Combine(rig.DataDir, "logs")))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            logged += "\n" + new StreamReader(stream).ReadToEnd();
        }

        using var logs = await rig.Session.GetAsync("/api/logs?after=0", Ct);
        logged += "\n" + await logs.Content.ReadAsStringAsync(Ct);
        foreach (var part in accepted.Split('.').Concat(forged.Split('.')).Append(nonce))
            Assert.DoesNotContain(part, logged);
        Assert.DoesNotContain(PassRig.Credential, logged);
    }

    /// <summary>
    /// One exchange, one service: a pass is checked against the keys of the address in effect when its check began and the
    /// credential kept then. While the keys' fetch is under way, the address and the credential change (to another
    /// service's, whose credential the pass names; or another credential at the same address): the pass is refused
    /// <c>device</c>, though the first service's key signed it and the credential now kept is the one it names.
    /// </summary>
    [Theory]
    [InlineData("address")]
    [InlineData("credential")]
    public async Task AFetchOfTheKeysPausedAcrossASwitchOfService_PairsNothing(string change)
    {
        await using var rig = await PassRig.StartAsync(factory);
        await using var other = new LocalHttpServer((_, stream, _) => LocalHttpServer.Answer(stream, 404, [], null));
        const string named = "dev_SERVICEB00000AB";
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Answer = async (_, stream, ct) =>
        {
            asked.TrySetResult();
            await release.Task.WaitAsync(ct);
            await rig.AnswerListAsync(stream);
        };

        // The first service signed it, naming the credential the engine is about to keep.
        var posted = rig.PostAsync(rig.Sign(PassRig.Header(), rig.Payload(device: named)));
        await asked.Task.WaitAsync(Ct);
        if (change == "address")
            await rig.PatchAsync($$$"""{"service":{"address":"http://127.0.0.1:{{{other.Port}}}"}}""");
        using (var put = await rig.Session.PutAsync("/api/service/credential", JsonContent.Create(new { credential = "ipd_service-b", id = named }), Ct))
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        release.TrySetResult();

        using var answered = await posted;
        Assert.Equal("device", await PassRig.RefusalAsync(answered));
        Assert.Empty(other.Requests.Where(r => r.Path == PairingKeys.KeysPath));
    }

    /// <summary>
    /// A list of keys an hour old is trusted only once it is read again: with the service down, a key it held pairs at
    /// 59 minutes, and is refused <c>signature</c> from 61 minutes on (at the failed fetch, and between fetches), however
    /// long the service stays down; once it answers again, the next fetch (still at most once a minute) pairs again.
    /// </summary>
    [Fact]
    public async Task AListAnHourOld_ThatCantBeReadAgain_PairsNothing_UntilTheServiceAnswers()
    {
        await using var rig = await PassRig.StartAsync(factory);
        await rig.AcceptedAsync(rig.Valid());
        Assert.Equal(1, rig.KeyRequests);
        rig.Answer = (_, stream, _) => LocalHttpServer.Answer(stream, 503, [], null);

        // 59 minutes: the list is fresh. An unknown kid asks again, and that fails; what the list holds still pairs.
        rig.Clock.Advance(TimeSpan.FromMinutes(59));
        using var unknown = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal("signature", await rig.RefusedAsync(rig.Sign(PassRig.Header("key-unknown"), rig.Payload(), unknown)));
        Assert.Equal(2, rig.KeyRequests);
        await rig.AcceptedAsync(rig.Valid());
        Assert.Equal(2, rig.KeyRequests);

        // 61 minutes: over an hour old, and the fetch fails; refused then and within the minute after, with no fetch.
        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("signature", await rig.RefusedAsync(rig.Valid()));
        Assert.Equal(3, rig.KeyRequests);
        Assert.Equal("signature", await rig.RefusedAsync(rig.Valid()));
        Assert.Equal(3, rig.KeyRequests);

        // A day on, the service still down.
        rig.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal("signature", await rig.RefusedAsync(rig.Valid()));
        Assert.Equal(4, rig.KeyRequests);

        // The service answers again: within the minute nothing is fetched; after it, the list is read and pairs.
        rig.Answer = null;
        Assert.Equal("signature", await rig.RefusedAsync(rig.Valid()));
        Assert.Equal(4, rig.KeyRequests);
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.AcceptedAsync(rig.Valid());
        Assert.Equal(5, rig.KeyRequests);
        await rig.AcceptedAsync(rig.Valid());
        Assert.Equal(5, rig.KeyRequests);
    }

    /// <summary>
    /// The time the checks use never goes back: a pass used, then another after the first's time (which forgets the
    /// first's nonce), then the clock moved back within the first's time: the first is still past its time, never paired
    /// again.
    /// </summary>
    [Fact]
    public async Task AClockMovedBack_ReopensNoPass()
    {
        await using var rig = await PassRig.StartAsync(factory);
        var pass = rig.Sign(PassRig.Header(), rig.Payload(exp: rig.Now + 60));
        await rig.AcceptedAsync(pass);

        rig.Clock.Advance(TimeSpan.FromSeconds(91));
        await rig.AcceptedAsync(rig.Valid());

        rig.Clock.Advance(TimeSpan.FromSeconds(-81));
        Assert.Equal("expired", await rig.RefusedAsync(pass));
        // A pass made at the clock as it now reads is past its time too, until the clock is back where it was.
        Assert.Equal("expired", await rig.RefusedAsync(rig.Sign(PassRig.Header(), rig.Payload(exp: rig.Now + 50))));
        await rig.AcceptedAsync(rig.Valid());
    }

    [Fact]
    public void TheNonces_RefuseANonceThatMayHaveBeenForgotten_TheClockMovedBackOrNot()
    {
        var nonces = new PairingNonces(capacity: 3);

        Assert.True(nonces.TryAdd("a", until: 100, now: 10));
        // A later time forgets a; a, offered again at an earlier time, may be the one forgotten.
        Assert.True(nonces.TryAdd("b", until: 200, now: 150));
        Assert.False(nonces.TryAdd("a", until: 100, now: 20));
        Assert.False(nonces.TryAdd("c", until: 100, now: 20));
        Assert.True(nonces.TryAdd("c", until: 101, now: 20));
    }

    public static TheoryData<string, string> UndecodableParts => new()
    {
        { "header", """{"alg":"ES256","kid":"\ud800","typ":"infopanel-pairing-pass"}""" },
        { "header", """{"alg":"ES256","kid":"key-1\udc00","typ":"infopanel-pairing-pass"}""" },
        { "header", """{"alg":"ES256","kid":"<FF>","typ":"infopanel-pairing-pass"}""" },
        { "header", """{"alg":"ES256","kid":"<C0><AF>","typ":"infopanel-pairing-pass"}""" },
        { "header", """{"alg":"ES256","kid":"key-1","typ":"infopanel-pairing-pass","\ud800":1}""" },
        { "header", """{"alg":"ES256","kid":"key-1","<FF>":"infopanel-pairing-pass"}""" },
        { "payload", """{"v":1,"aud":"infopanel-pairing","device":"\ud800","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW}""" },
        { "payload", """{"v":1,"aud":"infopanel-pairing\udfff","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW}""" },
        { "payload", """{"v":1,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"<FF>","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW}""" },
        { "payload", """{"v":1,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc<ED><A0><80>","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW}""" },
        { "payload", """{"v":1,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","nonce":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW,"\ud800":1}""" },
        { "payload", """{"v":1,"aud":"infopanel-pairing","device":"dev_TESTDEVICE0000AB","host":"pc.tailnet-1234.ts.net","<C0><AF>":"AAAAAAAAAAAAAAAAAAAAAA","exp":NOW}""" },
    };

    /// <summary>
    /// A header or payload whose text doesn't decode (a lone surrogate escaped, bytes that aren't UTF-8), in a value or a
    /// member's name, is <c>malformed</c>, signed or not; the payload's are signed with the listed key.
    /// </summary>
    [Theory]
    [MemberData(nameof(UndecodableParts))]
    public async Task APartWhoseTextDoesntDecode_IsMalformed(string part, string text)
    {
        await using var rig = await PassRig.StartAsync(factory);
        var bytes = Bytes(text.Replace("NOW", (rig.Now + 60).ToString()));
        var pass = part == "header"
            ? rig.Sign(bytes, Encoding.UTF8.GetBytes(rig.Payload()))
            : rig.Sign(Encoding.UTF8.GetBytes(PassRig.Header()), bytes);
        Assert.Equal("malformed", await rig.RefusedAsync(pass));
        // The engine still answers.
        await rig.AcceptedAsync(rig.Valid());
    }

    /// <summary>
    /// One exchange, one service, decided as the nonce is taken: the address and the credential change (to another
    /// service's whose credential has the same id; or another credential at the same address) after every other check
    /// passed, just before the nonce is taken. The pass is refused <c>device</c>, its nonce spent; and the switch, made
    /// while the pass is checked, is in effect.
    /// </summary>
    [Theory]
    [InlineData("address")]
    [InlineData("credential")]
    public async Task ASwitchOfServiceJustBeforeTheNonceIsTaken_PairsNothing(string change)
    {
        await using var rig = await PassRig.StartAsync(factory);
        await using var other = new LocalHttpServer((_, stream, _) => LocalHttpServer.Answer(stream, 404, [], null));
        var passes = rig.Engine.Services.GetRequiredService<PairingPasses>();
        var switched = 0;
        passes.BeforeNonce = async () =>
        {
            passes.BeforeNonce = null;
            if (change == "address")
                await rig.PatchAsync($$$"""{"service":{"address":"http://127.0.0.1:{{{other.Port}}}"}}""");
            using (var put = await rig.Session.PutAsync("/api/service/credential",
                       JsonContent.Create(new { credential = "ipd_service-b", id = PassRig.DeviceId }), Ct))
                Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
            Interlocked.Increment(ref switched);
        };

        var nonce = PassRig.NewNonce();
        Assert.Equal("device", await rig.RefusedAsync(rig.Valid(nonce)));
        Assert.Equal(1, switched);
        Assert.Null(passes.BeforeNonce);

        // The nonce was taken: the same pass under the service it was made for is a replay.
        if (change == "address")
            await rig.PatchAsync($$$"""{"service":{"address":"http://127.0.0.1:{{{rig.Keys.Port}}}"}}""");
        using (var put = await rig.Session.PutAsync("/api/service/credential",
                   JsonContent.Create(new { credential = PassRig.Credential, id = PassRig.DeviceId }), Ct))
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("replayed", await rig.RefusedAsync(rig.Valid(nonce)));
        await rig.AcceptedAsync(rig.Valid());
    }

    /// <summary>
    /// The list's age and the minute between fetches are measured on the monotonic clock, never the time of day: a list
    /// an hour old whose fetch failed stays untrusted when the computer's clock is set back half an hour (a pass is
    /// refused <c>signature</c>, nothing fetched within the minute), and a minute later the list is read again and pairs.
    /// The time of day set ahead two hours ages nothing: the list pairs with no fetch.
    /// </summary>
    [Fact]
    public async Task TheListsAge_IsMeasuredOnTheMonotonicClock_SoAClockSetBackRestoresNoTrust()
    {
        await using var rig = await PassRig.StartAsync(factory);
        await rig.AcceptedAsync(rig.Valid());
        Assert.Equal(1, rig.KeyRequests);

        rig.Clock.Advance(TimeSpan.FromHours(1));
        rig.Answer = (_, stream, _) => LocalHttpServer.Answer(stream, 503, [], null);
        Assert.Equal("signature", await rig.RefusedAsync(rig.Valid()));
        Assert.Equal(2, rig.KeyRequests);

        // The time of day goes back half an hour: a pass made then is still refused, and nothing is fetched.
        rig.Time.SetBack(TimeSpan.FromMinutes(30));
        Assert.Equal("signature", await rig.RefusedAsync(rig.Valid()));
        Assert.Equal(2, rig.KeyRequests);

        // A minute on the monotonic clock: the list is read again, and pairs.
        rig.Answer = null;
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.AcceptedAsync(rig.Valid());
        Assert.Equal(3, rig.KeyRequests);

        // The time of day set ahead two hours (back past where it was): the list read a moment ago is still fresh.
        rig.Time.SetBack(TimeSpan.FromHours(-2));
        await rig.AcceptedAsync(rig.Valid());
        Assert.Equal(3, rig.KeyRequests);
    }

    public static TheoryData<string, string> TrailingBytes => new()
    {
        { "header", "<FF>" },
        { "header", "{}" },
        { "header", "x" },
        { "header", "<00>" },
        { "payload", "<FF>" },
        { "payload", "1" },
        { "payload", "/**/" },
        { "payload", "<EF><BB><BF>" },
    };

    /// <summary>
    /// A header or payload is exactly one JSON value: anything after it but JSON whitespace (a byte that isn't UTF-8, a
    /// second value, a comment, a stray character) is <c>malformed</c>, though the service's key signed it.
    /// </summary>
    [Theory]
    [MemberData(nameof(TrailingBytes))]
    public async Task APartWithBytesAfterItsValue_IsMalformed(string part, string trailing)
    {
        await using var rig = await PassRig.StartAsync(factory);
        var header = Encoding.UTF8.GetBytes(PassRig.Header());
        var payload = Encoding.UTF8.GetBytes(rig.Payload());
        var pass = part == "header"
            ? rig.Sign([.. header, .. Bytes(trailing)], payload)
            : rig.Sign(header, [.. payload, .. Bytes(trailing)]);
        Assert.Equal("malformed", await rig.RefusedAsync(pass));
        await rig.AcceptedAsync(rig.Valid());
    }

    /// <summary>JSON whitespace around a header's or payload's value is still that one value.</summary>
    [Fact]
    public async Task APartWithJsonWhitespaceAroundItsValue_Pairs()
    {
        await using var rig = await PassRig.StartAsync(factory);
        await rig.AcceptedAsync(rig.Sign(" \t" + PassRig.Header() + "\r\n ", "\n" + rig.Payload() + " \t\r\n"));
    }

    /// <summary><paramref name="text"/>'s UTF-8, each <c>&lt;XX&gt;</c> a raw byte.</summary>
    private static byte[] Bytes(string text)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '<' && i + 3 < text.Length && text[i + 3] == '>')
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(text[i].ToString()));
            }
        }

        return [.. bytes];
    }
}
