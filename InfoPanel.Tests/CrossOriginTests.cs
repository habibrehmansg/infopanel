using System.Net;
using InfoPanel.Security;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>CORS for an origin given with <c>--allow-origin</c> (<see cref="EngineFactory.CrossOrigin"/>), and only for it.</summary>
public class CrossOriginTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] CorsHeaders =
    [
        "Access-Control-Allow-Origin", "Access-Control-Allow-Methods", "Access-Control-Allow-Headers", "Access-Control-Max-Age",
        "Access-Control-Allow-Private-Network", "Access-Control-Allow-Credentials", "Access-Control-Expose-Headers",
    ];

    private static HttpRequestMessage Preflight(string path, string origin, string method = "PATCH", bool privateNetwork = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        if (privateNetwork)
            request.Headers.Add("Access-Control-Request-Private-Network", "true");
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values)
        : response.Content.Headers.TryGetValues(name, out var content) ? string.Join(", ", content) : null;

    private static void AssertNoCorsHeaders(HttpResponseMessage response)
    {
        foreach (var name in CorsHeaders)
            Assert.Null(Header(response, name));
    }

    [Theory]
    [InlineData("/api/profiles/00000000-0000-0000-0000-000000000001/items", "PATCH")]
    [InlineData("/api/session", "GET")]
    [InlineData("/api/profiles", "POST")]
    [InlineData("/api/profiles/00000000-0000-0000-0000-000000000001", "DELETE")]
    [InlineData("/healthz", "GET")]
    public async Task Preflight_FromTheCrossOrigin_Is204WithTheCorsHeaders_WithoutAToken(string path, string method)
    {
        using var client = factory.CreateRawClient();
        using var request = Preflight(path, EngineFactory.CrossOrigin, method);
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(EngineFactory.CrossOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains(method, Header(response, "Access-Control-Allow-Methods")!.Split(", "));
        Assert.Equal(CrossOrigin.AllowedHeaders, Header(response, "Access-Control-Allow-Headers"));
        Assert.Equal(CrossOrigin.MaxAgeSeconds, Header(response, "Access-Control-Max-Age"));
        Assert.Equal("Origin", Header(response, "Vary"));
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
        Assert.Null(Header(response, "Access-Control-Allow-Private-Network"));
        Assert.Null(Header(response, "Set-Cookie"));
        SecurityTests.AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Preflight_AskingForPrivateNetworkAccess_IsAllowed()
    {
        using var client = factory.CreateRawClient();
        using var request = Preflight("/api/session", EngineFactory.CrossOrigin, "GET", privateNetwork: true);
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("true", Header(response, "Access-Control-Allow-Private-Network"));
        Assert.Equal(EngineFactory.CrossOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Request_FromTheCrossOrigin_CarriesTheCorsHeaders()
    {
        using var client = factory.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Add("Origin", EngineFactory.CrossOrigin);
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(EngineFactory.CrossOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("Origin", Header(response, "Vary"));
        Assert.Equal(CrossOrigin.ExposedHeaders, Header(response, "Access-Control-Expose-Headers"));
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
        Assert.Null(Header(response, "Access-Control-Allow-Methods"));
    }

    [Fact]
    public async Task Request_FromTheCrossOrigin_WithAWrongToken_Is401WithCorsHeaders_SoTheClientCanReadIt()
    {
        using var client = factory.CreateRawClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Add("Origin", EngineFactory.CrossOrigin);
        request.Headers.Authorization = new("Bearer", "not-the-token");
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(EngineFactory.CrossOrigin, Header(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Healthz_FromTheCrossOrigin_CarriesTheCorsHeaders()
    {
        using var client = factory.CreateRawClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("Origin", EngineFactory.CrossOrigin);
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(EngineFactory.CrossOrigin, Header(response, "Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/auth/" + EngineFactory.Token)]
    public async Task OtherPaths_FromTheCrossOrigin_GetNoCorsHeaders(string path)
    {
        using var client = factory.CreateRawClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", EngineFactory.CrossOrigin);
        using var response = await client.SendAsync(request, Ct);

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect, response.StatusCode.ToString());
        AssertNoCorsHeaders(response);
    }

    [Theory]
    [InlineData("https://other.example")]
    [InlineData("https://client.example:8443")]
    [InlineData("http://client.example")]
    [InlineData("null")]
    public async Task Preflight_FromAnotherOrigin_Is403WithoutCorsHeaders(string origin)
    {
        using var client = factory.CreateRawClient();
        using var request = Preflight("/api/session", origin, "GET", privateNetwork: true);
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoCorsHeaders(response);
    }

    [Fact]
    public async Task Request_FromAnotherOrigin_Is403WithoutCorsHeaders()
    {
        using var client = factory.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Add("Origin", "https://other.example");
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoCorsHeaders(response);
    }

    [Theory]
    [InlineData(EngineFactory.DevOrigin)]
    [InlineData("http://127.0.0.1:47123")]
    [InlineData(null)]
    public async Task SameOriginAndDevOrigin_GetNoCorsHeaders(string? origin)
    {
        using var client = factory.CreateApiClient();
        using var preflight = Preflight("/api/session", origin ?? EngineFactory.DevOrigin, "GET", privateNetwork: true);
        if (origin == null)
            preflight.Headers.Remove("Origin");
        using (var response = await client.SendAsync(preflight, Ct))
            AssertNoCorsHeaders(response);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        if (origin != null)
            request.Headers.Add("Origin", origin);
        using var actual = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, actual.StatusCode);
        AssertNoCorsHeaders(actual);
    }
}
