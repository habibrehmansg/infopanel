using System.Net;
using System.Net.WebSockets;
using InfoPanel.Hosting;
using Xunit;

namespace InfoPanel.Tests;

public class SecurityTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Api_WithoutToken_Is401ProblemJson()
    {
        using var client = factory.CreateRawClient();
        using var response = await client.GetAsync("/api/session", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Api_WithWrongToken_Is401()
    {
        using var client = factory.CreateRawClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Authorization = new("Bearer", EngineFactory.Token + "x");
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_WithBearerToken_Is200()
    {
        using var client = factory.CreateApiClient();
        using var response = await client.GetAsync("/api/session", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Ws_WithoutToken_Is401()
    {
        using var client = factory.CreateRawClient();
        using var response = await client.GetAsync("/ws", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ws_BearerHeaderIsNotEnough()
    {
        using var client = factory.CreateApiClient();
        using var response = await client.GetAsync("/ws", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SessionCookieAlone_Is401()
    {
        using var client = factory.CreateRawClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Add("Cookie", $"infopanel_session={EngineFactory.Token}");
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NoResponseSetsACookie()
    {
        using var client = factory.CreateApiClient();
        foreach (var path in new[] { "/", "/healthz", "/api/session", "/api/profiles", $"/auth/{EngineFactory.Token}", "/auth/wrong", "/ws" })
        {
            using var response = await client.GetAsync(path, Ct);
            Assert.False(response.Headers.Contains("Set-Cookie"), path);
        }
    }

    [Theory]
    [InlineData("evil.com")]
    [InlineData("evil.com:47123")]
    [InlineData("127.0.0.2")]
    public async Task ForeignHost_Is400(string host)
    {
        using var client = factory.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Host = host;
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertSecurityHeaders(response);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:47123")]
    [InlineData("[::1]:47123")]
    public async Task LoopbackHosts_AreAccepted(string host)
    {
        using var client = factory.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Host = host;
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("http://evil.com")]
    [InlineData("null")]
    [InlineData("http://localhost:1234")]
    [InlineData("https://127.0.0.1:47123")]
    public async Task ForeignOrigin_OnPost_Is403(string origin)
    {
        using var client = factory.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/profiles");
        request.Headers.Add("Origin", origin);
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task ForeignOrigin_OnPublicGet_Is403()
    {
        using var client = factory.CreateRawClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("Origin", "http://evil.com");
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("http://127.0.0.1:47123")]
    [InlineData("http://localhost:47123")]
    [InlineData(EngineFactory.DevOrigin)]
    public async Task AllowedOrigins_PassTheOriginCheck(string origin)
    {
        using var client = factory.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Add("Origin", origin);
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ForeignOrigin_OnWebSocketUpgradeRequest_Is403()
    {
        using var client = factory.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ws");
        request.Headers.Add("Origin", "http://evil.com");
        request.Headers.Add("Connection", "Upgrade");
        request.Headers.Add("Upgrade", "websocket");
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ForeignOrigin_OnWebSocketConnect_Is403()
    {
        var error = await ConnectWebSocket("http://evil.com", EngineRuntime.WebSocketProtocol, "auth." + EngineFactory.Token);

        Assert.Contains("403", error);
    }

    [Theory]
    [InlineData]
    [InlineData("infopanel.v1")]
    [InlineData("infopanel.v1", "auth.wrong-token")]
    [InlineData("infopanel.v1", "auth.")]
    [InlineData("auth." + EngineFactory.Token)]
    public async Task WebSocketConnect_WithoutTheAuthSubprotocol_Is401(params string[] protocols)
    {
        var error = await ConnectWebSocket(EngineFactory.DevOrigin, protocols);

        Assert.Contains("401", error);
    }

    [Fact]
    public async Task WebSocketConnect_WithTheAuthSubprotocol_PassesTheSecurityChecks()
    {
        // There is no /ws endpoint until M6: getting past the middleware means a 404 instead of a 401/403.
        var error = await ConnectWebSocket(EngineFactory.DevOrigin, EngineRuntime.WebSocketProtocol, "auth." + EngineFactory.Token);

        Assert.Contains("404", error);
    }

    /// <summary>Connects a WebSocket to <c>/ws</c> (which fails while there is no endpoint) and returns the error message.</summary>
    private async Task<string> ConnectWebSocket(string origin, params string[] protocols)
    {
        var ws = factory.Server.CreateWebSocketClient();
        foreach (var protocol in protocols)
            ws.SubProtocols.Add(protocol);
        ws.ConfigureRequest = request => request.Headers.Origin = origin;

        var error = await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(new Uri("ws://localhost/ws"), Ct));
        return error.Message;
    }

    [Fact]
    public async Task Auth_WithToken_RedirectsWithTheTokenInTheFragment()
    {
        using var client = factory.CreateRawClient();
        using var response = await client.GetAsync($"/auth/{EngineFactory.Token}", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/#token={EngineFactory.Token}", response.Headers.Location?.OriginalString);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.True(response.Headers.CacheControl?.NoStore);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Auth_WithWrongToken_Is401()
    {
        using var client = factory.CreateRawClient();
        using var response = await client.GetAsync("/auth/not-the-token", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Null(response.Headers.Location);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Healthz_IsPublic()
    {
        using var client = factory.CreateRawClient();
        using var response = await client.GetAsync("/healthz", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task NoCorsHeaders()
    {
        using var client = factory.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/session");
        request.Headers.Add("Origin", EngineFactory.DevOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        using var response = await client.SendAsync(request, Ct);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    internal static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal(
            "default-src 'self'; img-src 'self' blob: data:; connect-src 'self'; style-src 'self' 'unsafe-inline'",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }
}
