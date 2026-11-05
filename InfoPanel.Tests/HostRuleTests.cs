using System.Net;
using System.Text;
using System.Text.Json;
using InfoPanel.Tests.Editing;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// The host rule and the reverse proxy's names (spec/docs/api.md, "Where requests come from"): a request is answered only
/// for a loopback name or one of the <c>proxy.hosts</c> setting's names, matched exactly in any case on any port, the
/// setting read live; a request a proxy marks as from the internet (<c>Tailscale-Funnel-Request</c>) is 403 whatever its
/// host; forwarded and identity headers are never read; the <c>Origin</c> rule and CORS stay as they are. Each test has an
/// engine of its own, since a change is kept in its data folder.
/// </summary>
public sealed class HostRuleTests
{
    private const string Name = "pc.tailnet.ts.net";
    private const string FunnelHeader = "Tailscale-Funnel-Request";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpStatusCode> Status(HttpClient client, string path, string host, params (string Name, string Value)[] headers)
    {
        using var response = await Send(client, HttpMethod.Get, path, host, headers);
        return response.StatusCode;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string host, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Host = host;
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return await client.SendAsync(request, Ct);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Patch(HttpClient client, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/settings") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private static async Task SetHosts(HttpClient client, string hosts)
    {
        var (status, body) = await Patch(client, $$$"""{"proxy":{"hosts":{{{hosts}}}}}""");
        Assert.True(status == HttpStatusCode.OK, $"PATCH {hosts}: {(int)status} {body}");
    }

    private static async Task<JsonElement> Settings(HttpClient client)
    {
        using var response = await client.GetAsync("/api/settings", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private static string[] Hosts(JsonElement settings) =>
        [.. settings.GetProperty("proxy").GetProperty("hosts").EnumerateArray().Select(h => h.GetString()!)];

    // ---- the names --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AConfiguredName_IsAnswered_FromTheNextRequest_AndRefusedAgainOnceRemoved_WithoutARestart()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.BadRequest, await Status(api, "/healthz", Name));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(api, "/api/session", Name));
        Assert.Empty(Hosts(await Settings(api)));

        await SetHosts(api, $"""["{Name}"]""");

        Assert.Equal(new[] { Name }, Hosts(await Settings(api)));
        using (var healthz = await Send(api, HttpMethod.Get, "/healthz", Name))
        {
            Assert.Equal(HttpStatusCode.OK, healthz.StatusCode);
            Assert.Equal("ok", await healthz.Content.ReadAsStringAsync(Ct));
        }

        Assert.Equal(HttpStatusCode.OK, await Status(api, "/api/session", Name));

        // Another name in its place: the old one is refused at once.
        await SetHosts(api, """["other.tailnet.ts.net"]""");
        Assert.Equal(HttpStatusCode.BadRequest, await Status(api, "/api/session", Name));
        Assert.Equal(HttpStatusCode.OK, await Status(api, "/api/session", "other.tailnet.ts.net"));

        await SetHosts(api, "null");

        Assert.Empty(Hosts(await Settings(api)));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(api, "/healthz", "other.tailnet.ts.net"));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(api, "/api/session", "other.tailnet.ts.net"));
        // The loopback names stay.
        Assert.Equal(HttpStatusCode.OK, await Status(api, "/api/session", "localhost"));
        Assert.Equal(HttpStatusCode.OK, await Status(api, "/api/session", "127.0.0.1:47123"));
        Assert.Equal(HttpStatusCode.OK, await Status(api, "/api/session", "[::1]:47123"));
    }

    [Fact]
    public async Task AChangeThroughAName_ThatRemovesIt_IsAnswered_AndTheNextRequestThroughItIsRefused()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetHosts(api, """["proxy.example"]""");

        // The name was allowed when the request came in: the change is made and answered, through the name it removes.
        using (var request = new HttpRequestMessage(HttpMethod.Patch, "/api/settings")
               {
                   Content = new StringContent("""{"proxy":{"hosts":[]}}""", Encoding.UTF8, "application/json"),
               })
        {
            request.Headers.Host = "proxy.example";
            using var response = await api.SendAsync(request, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {text}");
            Assert.Empty(Hosts(JsonDocument.Parse(text).RootElement));
        }

        Assert.Equal(HttpStatusCode.BadRequest, await Status(api, "/api/session", "proxy.example"));
        Assert.Empty(Hosts(await Settings(api)));
    }

    [Fact]
    public async Task TheName_IsMatchedInAnyCase_OnAnyPort()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetHosts(api, $"""["{Name}"]""");

        foreach (var host in new[] { "PC.Tailnet.TS.NET", Name + ":443", Name + ":52345", "Pc.tailnet.ts.net:80" })
            Assert.True(await Status(api, "/api/session", host) == HttpStatusCode.OK, host);
    }

    [Fact]
    public async Task OnlyTheExactName_IsAnswered()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetHosts(api, $"""["{Name}", "proxy.example"]""");

        foreach (var host in new[]
                 {
                     "x." + Name, Name + ".evil", "x.proxy.example", "proxy.example.evil", "xproxy.example", "proxy.examplex",
                     Name + ".", "tailnet.ts.net", "ts.net", "pc.tailnet.ts.ne", "pc", "evil.com", "127.0.0.2", "localhost.",
                 })
        {
            Assert.True(await Status(api, "/healthz", host) == HttpStatusCode.BadRequest, host);
            Assert.True(await Status(api, "/api/session", host) == HttpStatusCode.BadRequest, host);
        }
    }

    [Fact]
    public async Task ForwardedAndIdentityHeaders_AreNeverRead()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        using var raw = factory.CreateRawClient();
        await SetHosts(api, $"""["{Name}"]""");

        // The Host the engine received decides, never what a proxy (or anyone) says it was.
        Assert.Equal(HttpStatusCode.BadRequest, await Status(api, "/api/session", "evil.com", ("X-Forwarded-Host", Name)));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(api, "/api/session", "evil.com", ("Forwarded", $"host={Name}")));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(api, "/api/session", "evil.com",
            ("X-Forwarded-Host", "localhost"), ("X-Forwarded-For", "127.0.0.1"), ("X-Forwarded-Proto", "https")));

        // A proxy's identity headers are no token.
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(raw, "/api/session", Name,
            ("Tailscale-User-Name", "A User"), ("Tailscale-User-Profile-Pic", "https://example.com/a.png"), ("X-Forwarded-For", "127.0.0.1")));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(raw, "/api/session", "localhost", ("Tailscale-User-Name", "A User")));
    }

    // ---- requests from the internet ---------------------------------------------------------------------------------

    [Fact]
    public async Task AFunnelRequest_Is403_OnALoopbackHost()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        using var raw = factory.CreateRawClient();

        await AssertFunnelRefused(api, raw, ["localhost", "127.0.0.1:47123", "[::1]"]);
    }

    [Fact]
    public async Task AFunnelRequest_Is403_ThroughAConfiguredName()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        using var raw = factory.CreateRawClient();
        await SetHosts(api, $"""["{Name}"]""");

        await AssertFunnelRefused(api, raw, [Name, Name + ":443"]);
    }

    [Fact]
    public async Task AFunnelRequest_Is403_OnAHostTheEngineRefuses()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        using var raw = factory.CreateRawClient();
        await SetHosts(api, """["proxy.example"]""");

        foreach (var host in new[] { "unconfigured.example", "evil.com:443", "x.proxy.example", "proxy.example.", "localhost.", "127.0.0.2" })
        {
            foreach (var (client, path) in new[] { (api, "/api/session"), (raw, "/healthz"), (raw, "/") })
            {
                using var response = await Send(client, HttpMethod.Get, path, host, (FunnelHeader, "?1"));
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{host} {path}: {(int)response.StatusCode}");
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            }

            // Without the header, the host rule refuses it as before.
            Assert.Equal(HttpStatusCode.BadRequest, await Status(raw, "/healthz", host));
        }
    }

    [Fact]
    public async Task OnKestrel_AFunnelRequest_Is403_OnAHostTheEngineRefuses()
    {
        using var factory = new EngineFactory();
        using var live = LiveEngine.Start(factory);

        Assert.Equal(403, await RawStatus(live.Port, "unconfigured.example", FunnelHeader + ": ?1"));
        Assert.Equal(403, await RawStatus(live.Port, "evil.com", "tailscale-funnel-request:"));
        Assert.Equal(400, await RawStatus(live.Port, "unconfigured.example"));
    }

    /// <summary>
    /// Every kind of request through <paramref name="hosts"/> with the Funnel header, in any case, with any value or two,
    /// is 403 <c>problem</c> without CORS; the same requests without it are answered.
    /// </summary>
    private static async Task AssertFunnelRefused(HttpClient api, HttpClient raw, string[] hosts)
    {
        foreach (var host in hosts)
        {
            foreach (var header in new[] { (FunnelHeader, "?1"), ("tailscale-funnel-request", "?1"), ("TAILSCALE-FUNNEL-REQUEST", "?1"), (FunnelHeader, "?0") })
            {
                foreach (var (client, path) in new[] { (api, "/api/session"), (raw, "/healthz"), (raw, "/"), (raw, "/assets/app.js"), (raw, $"/auth/{EngineFactory.Token}") })
                {
                    using var response = await Send(client, HttpMethod.Get, path, host, header);
                    Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{host} {header} {path}: {(int)response.StatusCode}");
                    Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                }
            }

            // Two values, and a pairing code's exchange and a preflight from a cross origin: refused before anything else.
            using (var twice = await Send(raw, HttpMethod.Get, "/healthz", host, (FunnelHeader, "?1"), (FunnelHeader, "?1")))
                Assert.Equal(HttpStatusCode.Forbidden, twice.StatusCode);
            using (var exchange = await Send(raw, HttpMethod.Post, "/auth/pairing/code", host, (FunnelHeader, "?1"), ("Origin", EngineFactory.CrossOrigin)))
            {
                Assert.Equal(HttpStatusCode.Forbidden, exchange.StatusCode);
                Assert.False(exchange.Headers.Contains("Access-Control-Allow-Origin"));
            }

            using (var preflight = await Send(raw, HttpMethod.Options, "/api/session", host, (FunnelHeader, "?1"), ("Origin", EngineFactory.CrossOrigin),
                       ("Access-Control-Request-Method", "GET")))
            {
                Assert.Equal(HttpStatusCode.Forbidden, preflight.StatusCode);
                Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
            }

            // Without the header, the same requests are answered.
            Assert.Equal(HttpStatusCode.OK, await Status(api, "/api/session", host));
            Assert.Equal(HttpStatusCode.OK, await Status(raw, "/healthz", host));
        }
    }

    /// <summary>The status line's code of one raw HTTP/1.1 request to <paramref name="port"/>, sent as written.</summary>
    private static async Task<int> RawStatus(int port, string host, params string[] headers)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, Ct);
        var stream = tcp.GetStream();
        var request = $"GET /healthz HTTP/1.1\r\nHost: {host}\r\n{string.Concat(headers.Select(h => h + "\r\n"))}Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), Ct);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var status = await reader.ReadLineAsync(Ct);
        Assert.NotNull(status);
        return int.Parse(status.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task OnKestrel_TheHostAsReceivedDecides_AndAFunnelHeaderWithoutAValueIs403()
    {
        using var factory = new EngineFactory();
        using var live = LiveEngine.Start(factory);
        using var api = live.Client();

        Assert.Equal(400, await RawStatus(live.Port, Name));
        Assert.Equal(200, await RawStatus(live.Port, "localhost"));

        await SetHosts(api, $"""["{Name}", "xn--bcher-kva.example"]""");

        Assert.Equal(200, await RawStatus(live.Port, Name));
        Assert.Equal(200, await RawStatus(live.Port, "PC.TAILNET.TS.NET:443"));
        // An internationalised name in the form a browser sends it.
        Assert.Equal(200, await RawStatus(live.Port, "xn--bcher-kva.example"));
        Assert.Equal(400, await RawStatus(live.Port, "x." + Name));
        Assert.Equal(400, await RawStatus(live.Port, Name + "."));
        Assert.Equal(400, await RawStatus(live.Port, "evil.com", "X-Forwarded-Host: " + Name));

        Assert.Equal(403, await RawStatus(live.Port, "localhost", FunnelHeader + ":"));
        Assert.Equal(403, await RawStatus(live.Port, Name, "tailscale-funnel-request: ?1"));

        await SetHosts(api, "null");
        Assert.Equal(400, await RawStatus(live.Port, Name));
    }

    // ---- origins ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheOriginRuleAndCors_AreUnchanged_ThroughAConfiguredName()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        using var raw = factory.CreateRawClient();
        await SetHosts(api, $"""["{Name}"]""");

        // The proxy's own name is no origin the engine accepts: a page served there is refused like any other.
        foreach (var origin in new[] { "https://" + Name, "http://" + Name, "https://" + Name + ":443", "http://evil.com" })
        {
            using var response = await Send(api, HttpMethod.Get, "/api/session", Name, ("Origin", origin));
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, origin);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"), origin);
        }

        // The origins the engine was started to accept pass as they do on a loopback name, CORS only for the cross origin.
        using (var dev = await Send(api, HttpMethod.Get, "/api/session", Name, ("Origin", EngineFactory.DevOrigin)))
        {
            Assert.Equal(HttpStatusCode.OK, dev.StatusCode);
            Assert.False(dev.Headers.Contains("Access-Control-Allow-Origin"));
        }

        using (var cross = await Send(api, HttpMethod.Get, "/api/session", Name, ("Origin", EngineFactory.CrossOrigin)))
        {
            Assert.Equal(HttpStatusCode.OK, cross.StatusCode);
            Assert.Equal(EngineFactory.CrossOrigin, Assert.Single(cross.Headers.GetValues("Access-Control-Allow-Origin")));
        }

        using (var preflight = await Send(raw, HttpMethod.Options, "/api/session", Name, ("Origin", EngineFactory.CrossOrigin), ("Access-Control-Request-Method", "GET")))
        {
            Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
            Assert.Equal(EngineFactory.CrossOrigin, Assert.Single(preflight.Headers.GetValues("Access-Control-Allow-Origin")));
        }

        // A web client's files outside /api get no CORS through the name either.
        using (var file = await Send(raw, HttpMethod.Get, "/assets/app.js", Name, ("Origin", EngineFactory.CrossOrigin)))
        {
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.False(file.Headers.Contains("Access-Control-Allow-Origin"));
        }
    }

    // ---- the setting ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSetting_TakesUpTo8LowerCaseDnsNames_AndKeepsThem()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var label63 = new string('a', 63);
        var name253 = string.Join('.', label63, label63, label63, new string('b', 61));
        Assert.Equal(253, name253.Length);
        string[] names =
        [
            Name, "proxy.example", "a.b", "xn--bcher-kva.example", "pc-1.tailnet-2.ts.net", "1pc.example", name253, label63 + ".example",
        ];

        await SetHosts(api, JsonSerializer.Serialize(names));

        Assert.Equal(names, Hosts(await Settings(api)));
        var stored = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(factory.DataDir, "settings.json"), Ct)).RootElement;
        Assert.Equal(names, stored.GetProperty("proxy").GetProperty("hosts").EnumerateArray().Select(h => h.GetString()));
        foreach (var name in names)
            Assert.True(await Status(api, "/api/session", name) == HttpStatusCode.OK, name);

        // An empty list is a list of no names; null goes back to the default, also none.
        await SetHosts(api, "[]");
        Assert.Empty(Hosts(await Settings(api)));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(api, "/api/session", Name));
    }

    [Theory]
    [InlineData("\"PC.tailnet.ts.net\"")] // upper case
    [InlineData("\"100.64.0.1\"")] // IP literals
    [InlineData("\"127.1\"")]
    [InlineData("\"127.0.0.1\"")]
    [InlineData("\"[::1]\"")]
    [InlineData("\"::1\"")]
    [InlineData("\"[fd7a:115c:a1e0::1]\"")]
    [InlineData("\"0x7f.1\"")]
    [InlineData("\"localhost\"")]
    [InlineData("\"pc\"")] // one label
    [InlineData("\"*.tailnet.ts.net\"")] // wildcard
    [InlineData("\"pc.tailnet.ts.net.\"")] // trailing dot
    [InlineData("\".pc.tailnet.ts.net\"")]
    [InlineData("\"pc..ts.net\"")]
    [InlineData("\"-pc.ts.net\"")]
    [InlineData("\"pc-.ts.net\"")]
    [InlineData("\"pc.ts.1\"")] // a numeric top label
    [InlineData("\"pc.ts.net:443\"")] // a port
    [InlineData("\"https://pc.ts.net\"")]
    [InlineData("\"pc.ts.net/\"")]
    [InlineData("\"pć.ts.net\"")] // not in its xn-- form
    [InlineData("\"pc_1.ts.net\"")]
    [InlineData("\" pc.ts.net\"")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("null")]
    public async Task ANameThatIsntOne_IsRefused_AndNothingChanges(string item)
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();

        var (status, body) = await Patch(api, $$$"""{"proxy":{"hosts":["proxy.example", {{{item}}}]}}""");

        Assert.True(status == HttpStatusCode.UnprocessableEntity, $"{item}: {(int)status} {body}");
        Assert.Contains(("/proxy/hosts/1", "schema"), body.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("path").GetString(), e.GetProperty("code").GetString())));
        Assert.Empty(Hosts(await Settings(api)));
        Assert.False(File.Exists(Path.Combine(factory.DataDir, "settings.json")));
    }

    [Theory]
    [InlineData("""["a.example","b.example","c.example","d.example","e.example","f.example","g.example","h.example","i.example"]""")] // 9
    [InlineData("""["a.example","a.example"]""")] // twice
    [InlineData("\"a.example\"")] // not a list
    [InlineData("{}")]
    public async Task AListThatIsntOne_IsRefused(string hosts)
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();

        var (status, body) = await Patch(api, $$$"""{"proxy":{"hosts":{{{hosts}}}}}""");

        Assert.True(status == HttpStatusCode.UnprocessableEntity, $"{hosts}: {(int)status} {body}");
        Assert.Contains(("/proxy/hosts", "schema"), body.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("path").GetString(), e.GetProperty("code").GetString())));
        Assert.Empty(Hosts(await Settings(api)));
    }

    [Fact]
    public async Task AStoredNameThatIsntOne_IsDropped_AndNoneIsAnswered()
    {
        using var factory = new EngineFactory();
        Directory.CreateDirectory(factory.DataDir);
        await File.WriteAllTextAsync(Path.Combine(factory.DataDir, "settings.json"),
            """{"format":"infopanel.settings/1","proxy":{"hosts":["proxy.example","100.64.0.1"]}}""", Ct);
        using var api = factory.CreateApiClient();

        Assert.Empty(Hosts(await Settings(api)));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(api, "/api/session", "proxy.example"));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(api, "/api/session", "100.64.0.1"));
    }

    [Fact]
    public async Task AStoredList_IsAnsweredFromTheStart()
    {
        using var factory = new EngineFactory();
        Directory.CreateDirectory(factory.DataDir);
        await File.WriteAllTextAsync(Path.Combine(factory.DataDir, "settings.json"),
            """{"format":"infopanel.settings/1","proxy":{"hosts":["proxy.example"]}}""", Ct);
        using var api = factory.CreateApiClient();

        Assert.Equal(new[] { "proxy.example" }, Hosts(await Settings(api)));
        Assert.Equal(HttpStatusCode.OK, await Status(api, "/api/session", "proxy.example"));
    }
}
