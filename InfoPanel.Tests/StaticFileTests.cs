using System.Net;
using Xunit;

namespace InfoPanel.Tests;

public class StaticFileTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/profiles/abc")]
    [InlineData("/settings")]
    public async Task WebClientRoutes_ServeIndexHtml_WithoutToken(string path)
    {
        using var client = factory.CreateRawClient();
        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(EngineFactory.IndexMarker, await response.Content.ReadAsStringAsync(Ct));
        SecurityTests.AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task StaticFile_IsServed()
    {
        using var client = factory.CreateRawClient();
        using var response = await client.GetAsync("/assets/app.js", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("web client", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task MissingFile_Is404_NotIndex()
    {
        using var client = factory.CreateRawClient();
        using var response = await client.GetAsync("/assets/missing.js", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/nope")]
    [InlineData("/api")]
    [InlineData("/api/profiles/x/y/z")]
    public async Task UnknownApiRoute_Is404ProblemJson_NotIndex(string path)
    {
        using var client = factory.CreateApiClient();
        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(EngineFactory.IndexMarker, await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("/ws")]
    [InlineData("/ws/other")]
    public async Task WsWithoutUpgrade_Is404_NotIndex(string path)
    {
        using var client = factory.CreateRawClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Sec-WebSocket-Protocol", $"infopanel.v1, auth.{EngineFactory.Token}");
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(EngineFactory.IndexMarker, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task AuthWithoutToken_Is404_NotIndex()
    {
        using var client = factory.CreateRawClient();
        using var response = await client.GetAsync("/auth", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(EngineFactory.IndexMarker, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task WrongMethodOnApi_IsProblemJson()
    {
        using var client = factory.CreateApiClient();
        using var response = await client.DeleteAsync("/api/session", Ct);

        // The /api fallback (404) matches before routing can answer 405.
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
