using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Preview;
using InfoPanel.Security;
using InfoPanel.Tests.Desktop;
using InfoPanel.Tests.Preview;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// Integration tokens (spec/docs/api.md, "Authentication"): created, listed and revoked with the session token only;
/// scopes checked on every endpoint and failing closed; revocation immediate for REST and <c>/ws</c>; <c>tokens.json</c>
/// holding hashes only.
/// </summary>
public class TokenTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Id, string Token, JsonElement Body)> CreateToken(string name, params string[] scopes) =>
        await CreateToken(factory.CreateApiClient(), name, scopes);

    private static async Task<(string Id, string Token, JsonElement Body)> CreateToken(HttpClient session, string name, params string[] scopes)
    {
        using var response = await session.PostAsJsonAsync("/api/tokens", new { name, scopes }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (body.GetProperty("id").GetString()!, body.GetProperty("token").GetString()!, body);
    }

    private HttpClient Client(string token)
    {
        var client = factory.CreateRawClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    /// <summary>
    /// A create that would take <c>tokens.json</c> over its cap, <c>tokensBytes</c> (16 MiB), is refused 422 with one
    /// error, <c>limit.tokensBytes</c> at <c>""</c> (spec/docs/limits.md), and nothing is created or written.
    /// </summary>
    [Fact]
    public async Task ACreatePastTheTokenFilesCap_Is422_LimitTokensBytes()
    {
        using var full = new EngineFactory();
        Directory.CreateDirectory(full.DataDir);
        var file = Path.Combine(full.DataDir, "tokens.json");
        var nearlyFull = $"{{\"tokens\":[],\"later\":\"{new string('x', 16 * 1024 * 1024 - 100 - 27)}\"}}";
        File.WriteAllText(file, nearlyFull);
        using var session = full.CreateApiClient();

        using var response = await session.PostAsJsonAsync("/api/tokens", new { name = "one-too-many", scopes = new[] { "read" } }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal(("limit.tokensBytes", ""), (error.GetProperty("code").GetString(), error.GetProperty("path").GetString()));
        Assert.Empty((await session.GetFromJsonAsync<JsonElement>("/api/tokens", Ct)).EnumerateArray());
        Assert.Equal(nearlyFull, File.ReadAllText(file));
    }

    private static async Task AssertScopeProblem(HttpResponseMessage response, string scope)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
        Assert.Equal("scope", problem.GetProperty("code").GetString());
        Assert.Equal(scope, problem.GetProperty("scope").GetString());
    }

    private static async Task AssertUnauthorized(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    // ---- the token endpoints ----

    [Fact]
    public async Task Create_AnswersTheSecretOnce_AndTheListHasNoSecrets()
    {
        using var session = factory.CreateApiClient();
        using var response = await session.PostAsJsonAsync("/api/tokens", new { name = "Home Assistant", scopes = new[] { "read" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var token = created.GetProperty("token").GetString()!;
        Assert.Matches("^ipi_[A-Za-z0-9_-]{43}$", token);
        Assert.Equal("integration", created.GetProperty("kind").GetString());
        Assert.Equal(new[] { "read" }, created.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        Assert.False(created.TryGetProperty("lastUsed", out _));

        var listed = await session.GetStringAsync("/api/tokens", Ct);
        Assert.DoesNotContain(token, listed);
        Assert.DoesNotContain("\"token\"", listed);
        var entry = JsonDocument.Parse(listed).RootElement.EnumerateArray().Single(t => t.GetProperty("id").GetString() == created.GetProperty("id").GetString());
        Assert.Equal("Home Assistant", entry.GetProperty("name").GetString());
        Assert.Equal(created.GetProperty("created").GetString(), entry.GetProperty("created").GetString());
    }

    [Fact]
    public async Task Create_EditImpliesRead()
    {
        var (_, _, body) = await CreateToken("script", "edit");
        Assert.Equal(new[] { "read", "edit" }, body.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
    }

    [Theory]
    [InlineData("""{"name": "a/b", "scopes": ["read"]}""", "/name")]
    [InlineData("""{"name": " padded", "scopes": ["read"]}""", "/name")]
    [InlineData("""{"name": "line\n", "scopes": ["read"]}""", "/name")]
    [InlineData("""{"name": "", "scopes": ["read"]}""", "/name")]
    [InlineData("""{"name": "ok", "scopes": []}""", "/scopes")]
    [InlineData("""{"name": "ok", "scopes": ["admin"]}""", "/scopes/0")]
    [InlineData("""{"name": "ok", "scopes": ["read", "read"]}""", "/scopes")]
    [InlineData("""{"name": "ok", "scopes": ["read"], "kind": "session"}""", "/kind")]
    [InlineData("""{"name": "ok"}""", "")]
    [InlineData("""not json""", "")]
    public async Task Create_RefusesABadBody_With422(string body, string path)
    {
        using var session = factory.CreateApiClient();
        var before = (await session.GetFromJsonAsync<JsonElement>("/api/tokens", Ct)).GetArrayLength();
        using var response = await session.PostAsync("/api/tokens", new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("invalid", problem.GetProperty("code").GetString());
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("path").GetString() == path);
        Assert.Equal(before, (await session.GetFromJsonAsync<JsonElement>("/api/tokens", Ct)).GetArrayLength());
    }

    [Fact]
    public async Task Create_WithMoreThan100Errors_ListsTheFirst100_ThenOneMore()
    {
        // 101 members the body doesn't define: 101 schema errors, of which 100 are listed and the last is counted by `more`.
        using var session = factory.CreateApiClient();
        var members = string.Concat(Enumerable.Range(0, 101).Select(i => $",\"x{i:D3}\":0"));
        using var response = await session.PostAsync("/api/tokens",
            new StringContent("{\"name\":\"ok\",\"scopes\":[\"read\"]" + members + "}", Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(101, errors.Count);
        Assert.All(errors.Take(100), e => Assert.Equal("schema", e.GetProperty("code").GetString()));
        Assert.Equal("more", errors[100].GetProperty("code").GetString());
        Assert.Equal("", errors[100].GetProperty("path").GetString());
        Assert.Single(errors, e => e.GetProperty("code").GetString() == "more");
    }

    [Fact]
    public async Task Create_RefusesABodyOverTokenRequestBytes_WithTheLimitsCode()
    {
        using var session = factory.CreateApiClient();
        const string Body = """{"name":"padded","scopes":["read"]}""";
        StringContent Padded(int length) => new(Body + new string(' ', length - Body.Length), Encoding.UTF8, "application/json");

        using (var over = await session.PostAsync("/api/tokens", Padded(4_097), Ct))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
            var error = Assert.Single((await over.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateArray());
            Assert.Equal(("limit.tokenRequestBytes", ""), (error.GetProperty("code").GetString(), error.GetProperty("path").GetString()));
        }
        using (var at = await session.PostAsync("/api/tokens", Padded(4_096), Ct))
            Assert.Equal(HttpStatusCode.Created, at.StatusCode);
    }

    [Fact]
    public async Task Create_RefusesABodyOverTokenRequestBytes_SentWithoutALength()
    {
        // Chunked: no Content-Length, so the size is known only as the body is read.
        using var session = factory.CreateApiClient();
        const string Body = """{"name":"unsized","scopes":["read"]}""";
        StreamContent Padded(int length)
        {
            var content = new StreamContent(new ProfileEndpointTests.UnsizedBody(Encoding.UTF8.GetBytes(Body + new string(' ', length - Body.Length))));
            content.Headers.ContentType = new("application/json");
            return content;
        }

        using (var over = await session.PostAsync("/api/tokens", Padded(4_097), Ct))
        {
            Assert.Null(over.RequestMessage!.Content!.Headers.ContentLength);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
            var error = Assert.Single((await over.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateArray());
            Assert.Equal(("limit.tokenRequestBytes", ""), (error.GetProperty("code").GetString(), error.GetProperty("path").GetString()));
        }
        using (var at = await session.PostAsync("/api/tokens", Padded(4_096), Ct))
            Assert.Equal(HttpStatusCode.Created, at.StatusCode);
    }

    [Fact]
    public async Task Create_ChecksTheBodyToJsonDepth_At256And257()
    {
        // jsonDepth is 256: the root object is level 1, so an unknown member holding 255 nested arrays is at 256 and
        // gets schema at it; one more array is over the limit, the one error limit.jsonDepth at "".
        using var session = factory.CreateApiClient();
        async Task<(string?, string?)> Error(int arrays)
        {
            using var response = await session.PostAsync("/api/tokens",
                new StringContent("{\"name\":\"ok\",\"scopes\":[\"read\"],\"x\":" + new string('[', arrays) + new string(']', arrays) + "}", Encoding.UTF8, "application/json"), Ct);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var error = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateArray());
            return (error.GetProperty("code").GetString(), error.GetProperty("path").GetString());
        }

        Assert.Equal(("schema", "/x"), await Error(255));
        Assert.Equal(("limit.jsonDepth", ""), await Error(256));
    }

    [Fact]
    public async Task Create_ChecksTheBodyToJsonDepth()
    {
        // A member the body doesn't define, nested 5 deep, is a schema error at it: the body is held to jsonDepth (256), as every body is.
        using var session = factory.CreateApiClient();
        using var response = await session.PostAsync("/api/tokens",
            new StringContent("""{"name":"ok","scopes":["read"],"x":[[[[1]]]]}""", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateArray());
        Assert.Equal(("schema", "/x"), (error.GetProperty("code").GetString(), error.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task Create_KeepsTheJsonLevelPassesCodes()
    {
        // A string that isn't Unicode text is json at it, and 101 of them end with one more, as every body reports them.
        using var session = factory.CreateApiClient();
        using (var response = await session.PostAsync("/api/tokens",
                   new StringContent("""{"name":"\ud800","scopes":["read"]}""", Encoding.UTF8, "application/json"), Ct))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var error = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateArray());
            Assert.Equal(("json", "/name"), (error.GetProperty("code").GetString(), error.GetProperty("path").GetString()));
        }

        var scopes = string.Join(",", Enumerable.Repeat("\"\\ud800\"", 101));
        using (var response = await session.PostAsync("/api/tokens",
                   new StringContent("{\"name\":\"ok\",\"scopes\":[" + scopes + "]}", Encoding.UTF8, "application/json"), Ct))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateArray().ToList();
            Assert.Equal(101, errors.Count);
            Assert.All(errors.Take(100), e => Assert.Equal("json", e.GetProperty("code").GetString()));
            Assert.Equal(("more", ""), (errors[100].GetProperty("code").GetString(), errors[100].GetProperty("path").GetString()));
        }
    }

    [Fact]
    public async Task Delete_UnknownToken_Is404()
    {
        using var session = factory.CreateApiClient();
        using var response = await session.DeleteAsync($"/api/tokens/{new string('0', 32)}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- scopes ----

    [Fact]
    public async Task ReadToken_Reads_ButIsRefusedEdits()
    {
        var (_, token, _) = await CreateToken("reader", "read");
        using var client = Client(token);

        using var list = await client.GetAsync("/api/profiles", Ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var session = await client.GetAsync("/api/session", Ct);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);

        var id = (await list.Content.ReadFromJsonAsync<JsonElement>(Ct))[0].GetProperty("id").GetString();
        using var commit = await client.PostAsync($"/api/profiles/{id}/commit", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);
        await AssertScopeProblem(commit, "edit");
        using var blob = await client.PostAsync("/api/blobs", new ByteArrayContent([1, 2, 3]), Ct);
        await AssertScopeProblem(blob, "edit");
    }

    [Fact]
    public async Task EditToken_Edits()
    {
        var (_, token, _) = await CreateToken("script-edits", "read", "edit");
        using var client = Client(token);
        var id = Guid.NewGuid().ToString();

        using var created = await client.PostAsJsonAsync("/api/profiles", new { id, name = "From a script", width = 100, height = 50 }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tag = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("instance").GetString();
        using var deleted = await client.DeleteAsync($"/api/profiles/{id}?instance={tag}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task IntegrationToken_CannotUseTheTokenEndpoints()
    {
        var (id, token, _) = await CreateToken("full", "read", "edit");
        using var client = Client(token);

        using var list = await client.GetAsync("/api/tokens", Ct);
        await AssertScopeProblem(list, "session");
        using var create = await client.PostAsJsonAsync("/api/tokens", new { name = "escalated", scopes = new[] { "edit" } }, Ct);
        await AssertScopeProblem(create, "session");
        using var revoke = await client.DeleteAsync($"/api/tokens/{id}", Ct);
        await AssertScopeProblem(revoke, "session");

        // Nothing changed: the token is still there and works.
        using var still = await client.GetAsync("/api/session", Ct);
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    [Fact]
    public async Task IntegrationToken_CannotSignIn()
    {
        var (_, token, _) = await CreateToken("no-sign-in", "read", "edit");
        using var client = factory.CreateRawClient();
        using var response = await client.GetAsync($"/auth/{token}", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain(EngineFactory.Token, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public void PairingLinks_CarryOnlyTheSessionToken()
    {
        var runtime = factory.Services.GetRequiredService<EngineRuntime>();
        Assert.EndsWith($"&token={EngineFactory.Token}", runtime.PairingUrlFor(EngineFactory.CrossOrigin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ipi_")]
    [InlineData("ipi_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task UnknownToken_Is401(string token)
    {
        using var client = factory.CreateRawClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var response = await client.SendAsync(request, Ct);
        await AssertUnauthorized(response);
    }

    [Fact]
    public async Task OnlyTheWholeSecretAuthenticates()
    {
        var (_, token, _) = await CreateToken("exact", "read");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        foreach (var near in new[] { token[..^1], token + "A", token[..^1] + (token[^1] == 'A' ? 'B' : 'A'), token.ToUpperInvariant(), hash })
        {
            using var client = Client(near);
            using var response = await client.GetAsync("/api/session", Ct);
            await AssertUnauthorized(response);
        }
    }

    /// <summary>
    /// Every endpoint under <c>/api</c> declares its scope: reads (GET, HEAD) <c>read</c>, everything else <c>edit</c>,
    /// and only the token endpoints <c>session</c>; the fallback that answers 404 for unknown paths <c>read</c>. An
    /// endpoint added without a declaration fails here (and is session-only meanwhile).
    /// </summary>
    [Fact]
    public void EveryApiEndpoint_DeclaresTheScopeItsMethodNeeds()
    {
        var endpoints = ApiEndpoints();
        Assert.NotEmpty(endpoints);
        var wrong = new List<string>();
        foreach (var (pattern, methods, scope) in endpoints)
        {
            var name = $"{string.Join(",", methods)} {pattern}";
            var tokenEndpoint = pattern == "/api/tokens" || pattern.StartsWith("/api/tokens/", StringComparison.Ordinal);
            var reads = methods.All(m => m is "GET" or "HEAD") || pattern == "/api/{**rest}";
            ApiScope? expected = tokenEndpoint ? ApiScope.Session : reads ? ApiScope.Read : ApiScope.Edit;
            if (scope != expected)
                wrong.Add($"{name}: declares {scope?.ToString() ?? "nothing"}, needs {expected}");
        }

        Assert.Empty(wrong);
        // /ws is read access too.
        var ws = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/ws");
        Assert.Equal(ApiScope.Read, ws.Metadata.GetMetadata<ApiScopeMetadata>()?.Scope);
    }

    /// <summary>Every endpoint refuses a token without its scope with 403, and every one refuses no token with 401.</summary>
    [Fact]
    public async Task EveryApiEndpoint_RefusesATokenWithoutItsScope()
    {
        var (_, read, _) = await CreateToken("every-read", "read");
        var (_, edit, _) = await CreateToken("every-edit", "edit");
        using var reader = Client(read);
        using var editor = Client(edit);
        using var anonymous = factory.CreateRawClient();
        foreach (var (pattern, methods, scope) in ApiEndpoints())
        {
            var path = pattern.Replace("{id}", Guid.NewGuid().ToString());
            foreach (var method in methods.Select(m => m == "*" ? "GET" : m))
            {
                using (var response = await anonymous.SendAsync(Request(method, path), Ct))
                    Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} without a token: {response.StatusCode}");

                var refused = scope switch
                {
                    ApiScope.Edit => reader,
                    ApiScope.Session => editor,
                    _ => null,
                };
                if (refused == null)
                    continue;
                using var forbidden = await refused.SendAsync(Request(method, path), Ct);
                Assert.True(forbidden.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} without its scope: {forbidden.StatusCode}");
            }
        }

        static HttpRequestMessage Request(string method, string path) =>
            new(new HttpMethod(method), path) { Content = method is "GET" or "HEAD" ? null : new StringContent("{}", Encoding.UTF8, "application/json") };
    }

    /// <summary>
    /// Another spelling of a session-only or <c>edit</c> path (case, doubled, trailing, dot or percent-encoded dot
    /// segments, percent-encoded letters), or a method the route doesn't map, never reaches the endpoint with a token that
    /// lacks its scope: it is refused (403, 405) or not found (404), and nothing changes. Sent as raw request lines to the
    /// engine on Kestrel, so the server's own path decoding and normalisation are what is tested.
    /// </summary>
    [Fact]
    public async Task PathVariants_NeverServeAnEndpointWithoutItsScope()
    {
        var port = EngineRuntime.FindFreePort();
        using var engine = new EngineFactory();
        using var kestrel = engine.WithWebHostBuilder(builder => builder.UseSetting("Engine:Port", port.ToString()));
        kestrel.UseKestrel();
        kestrel.StartServer();
        using var session = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        session.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
        var (readId, read, _) = await CreateToken(session, "variants-read", "read");
        var (_, edit, _) = await CreateToken(session, "variants-edit", "edit");
        var profile = (await session.GetFromJsonAsync<JsonElement>("/api/profiles", Ct))[0].GetProperty("id").GetString()!;
        var tokensBefore = await TokenIds();
        var profilesBefore = (await session.GetFromJsonAsync<JsonElement>("/api/profiles", Ct)).GetArrayLength();

        // A session-token request through the same raw path reaches the endpoint, so the refusals below are the scope's.
        Assert.Equal(200, await Send(EngineFactory.Token, "GET", "/%61pi/./%74okens/"));

        var cases = new List<(string Token, string Method, string Path)>();
        foreach (var token in new[] { read, edit })
        {
            foreach (var path in Variants("/api/tokens"))
            {
                cases.Add((token, "GET", path));
                cases.Add((token, "POST", path));
            }

            foreach (var path in Variants($"/api/tokens/{readId}"))
                cases.Add((token, "DELETE", path));
        }

        foreach (var path in Variants("/api/profiles").Concat(Variants($"/api/profiles/{profile}/save")))
            cases.Add((read, "POST", path));
        foreach (var path in Variants($"/api/profiles/{profile}/commit"))
        {
            cases.Add((read, "POST", path));
            cases.Add((read, "PUT", path));
        }

        foreach (var path in Variants($"/api/profiles/{profile}"))
            cases.Add((read, "DELETE", path));
        foreach (var path in new[] { $"/api/profiles/{profile}/save", $"/api/profiles/{profile}/commit", "/api/blobs", "/api/tokens" })
        {
            cases.Add((read, "HEAD", path));
            cases.Add((edit, "HEAD", path));
        }

        var served = new List<string>();
        foreach (var (token, method, path) in cases)
        {
            var status = await Send(token, method, path);
            if (status is not (403 or 404 or 405))
                served.Add($"{(token == read ? "read" : "edit")} {method} {path}: {status}");
        }

        Assert.Empty(served);
        Assert.Equal(tokensBefore, await TokenIds());
        Assert.Equal(profilesBefore, (await session.GetFromJsonAsync<JsonElement>("/api/profiles", Ct)).GetArrayLength());

        static string[] Variants(string path)
        {
            var rest = path["/api/".Length..];
            return
            [
                path, "/API/" + rest, path.ToUpperInvariant(), "/%61pi/" + rest, "/api//" + rest, path + "/", "/api/./" + rest,
                "/api/x/../" + rest, "/api/%2E/" + rest, "/api/%2e%2E/api/" + rest,
                "/api/%" + Convert.ToHexString([(byte)rest[0]]).ToLowerInvariant() + rest[1..],
                "/api/" + string.Concat(rest.Select(c => c == '/' ? "/" : "%" + Convert.ToHexString([(byte)c]))),
            ];
        }

        async Task<List<string>> TokenIds() =>
            [.. (await session.GetFromJsonAsync<JsonElement>("/api/tokens", Ct)).EnumerateArray().Select(t => t.GetProperty("id").GetString()!)];

        // The request line exactly as given (HttpClient would normalise the path), with a body a create would accept.
        async Task<int> Send(string token, string method, string target)
        {
            var body = method is "GET" or "HEAD" or "DELETE" ? "" : """{"name":"escalated","scopes":["edit"]}""";
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, Ct);
            await using var stream = tcp.GetStream();
            var request = $"{method} {target} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nAuthorization: Bearer {token}\r\n"
                + $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request), Ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var statusLine = await reader.ReadLineAsync(Ct) ?? "";
            return int.Parse(statusLine.Split(' ')[1]);
        }
    }

    private List<(string Pattern, string[] Methods, ApiScope? Scope)> ApiEndpoints() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw && (raw == "/api" || raw.StartsWith("/api/", StringComparison.Ordinal)))
            .Select(e => (e.RoutePattern.RawText!.TrimEnd('/'),
                e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.ToArray() ?? ["*"],
                e.Metadata.GetMetadata<ApiScopeMetadata>()?.Scope))
            .ToList();

    // ---- revocation ----

    [Fact]
    public async Task Revocation_IsImmediate()
    {
        var (id, token, _) = await CreateToken("revoked", "read");
        using var client = Client(token);
        using (var before = await client.GetAsync("/api/session", Ct))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var session = factory.CreateApiClient();
        using (var revoke = await session.DeleteAsync($"/api/tokens/{id}", Ct))
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using (var after = await client.GetAsync("/api/session", Ct))
            await AssertUnauthorized(after);
        using (var again = await session.DeleteAsync($"/api/tokens/{id}", Ct))
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.DoesNotContain(id, await session.GetStringAsync("/api/tokens", Ct));
    }

    [Fact]
    public async Task ReadToken_SubscribesToThePreview()
    {
        var (_, token, _) = await CreateToken("viewer", "read");
        await using var client = await PreviewClient.ConnectAsync(factory, Ct, EngineRuntime.WebSocketProtocol, EngineRuntime.WebSocketAuthPrefix + token);
        await client.ReceiveTextAsync("hello", Ct);
        using var session = factory.CreateApiClient();
        var id = (await session.GetFromJsonAsync<JsonElement>("/api/profiles", Ct))[0].GetProperty("id").GetString();

        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 0.25 }, Ct);
        var doc = await client.ReceiveTextAsync("doc", Ct);
        Assert.Equal(id, doc.GetProperty("profileId").GetString());
        await client.ReceiveFrameAsync(Ct);
    }

    [Fact]
    public async Task Revocation_ClosesThatTokensSockets_AndLeavesTheOthersOpen()
    {
        var (revokedId, revoked, _) = await CreateToken("to-revoke", "read");
        var (_, kept, _) = await CreateToken("to-keep", "read");

        await using var byProtocol = await PreviewClient.ConnectAsync(factory, Ct, EngineRuntime.WebSocketProtocol, EngineRuntime.WebSocketAuthPrefix + revoked);
        await using var byMessage = await PreviewClient.ConnectAsync(factory, Ct, EngineRuntime.WebSocketProtocol);
        await byMessage.SendAsync(new { type = "auth", token = revoked }, Ct);
        await using var other = await PreviewClient.ConnectAsync(factory, Ct, EngineRuntime.WebSocketProtocol, EngineRuntime.WebSocketAuthPrefix + kept);
        await using var sessionSocket = await PreviewClient.ConnectAsync(factory, Ct);
        foreach (var socket in new[] { byProtocol, byMessage, other, sessionSocket })
            await socket.ReceiveTextAsync("hello", Ct);

        using var session = factory.CreateApiClient();
        using (var revoke = await session.DeleteAsync($"/api/tokens/{revokedId}", Ct))
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        foreach (var closed in new[] { byProtocol, byMessage })
        {
            PreviewClient.Message message;
            do
                message = await closed.ReceiveAsync(Ct);
            while (message.CloseStatus == null);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, message.CloseStatus);
            Assert.Equal("token revoked", closed.Socket.CloseStatusDescription);
        }

        foreach (var open in new[] { other, sessionSocket })
        {
            await open.SendAsync(new { type = "subscribe", profileId = Guid.NewGuid().ToString() }, Ct);
            await open.ReceiveTextAsync("error", Ct);
            Assert.Equal(WebSocketState.Open, open.Socket.State);
        }
    }

    /// <summary>
    /// A connection that is subscribed and receiving frames (another client keeps editing the profile) is closed with
    /// 1008 promptly when its token is revoked, frames in flight or not.
    /// </summary>
    [Fact]
    public async Task Revocation_ClosesASubscribedSocketThatIsReceivingFrames()
    {
        var (tokenId, token, _) = await CreateToken("streaming", "read");
        using var session = factory.CreateApiClient();
        var profile = await TestDocs.CreateAsync(session, TestDocs.Doc("Streaming", 480, 320, TestDocs.Scene("shape", 10, 10)), Ct);
        await using var client = await PreviewClient.ConnectAsync(factory, Ct, EngineRuntime.WebSocketProtocol, EngineRuntime.WebSocketAuthPrefix + token);
        await client.ReceiveTextAsync("hello", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = profile, scale = 1, maxFps = 30 }, Ct);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var edits = Task.Run(async () =>
        {
            try
            {
                for (var x = 0; !stop.IsCancellationRequested; x = (x + 7) % 400)
                {
                    using var response = await TestDocs.CommitAsync(session, profile, stop.Token, TestDocs.Place("shape", x, 10));
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    await Task.Delay(20, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Stopped mid-request: the cancellation is the only exception swallowed.
            }
        });

        try
        {
            var (first, _) = await client.ReceiveFrameAsync(Ct);
            await client.ReceiveFrameAsync(Ct, h => h.Rev > first.Rev);

            var revoked = System.Diagnostics.Stopwatch.StartNew();
            using (var revoke = await session.DeleteAsync($"/api/tokens/{tokenId}", Ct))
                Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

            // Frames may still arrive before the close, but not for long.
            PreviewClient.Message message;
            do
            {
                var left = TimeSpan.FromSeconds(2) - revoked.Elapsed;
                Assert.True(left > TimeSpan.Zero, "Not closed within 2 s of the revocation.");
                message = await client.ReceiveAsync(Ct, left);
            }
            while (message.CloseStatus == null);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, message.CloseStatus);
            Assert.Equal(PreviewSocketHandler.TokenRevokedReason, client.Socket.CloseStatusDescription);
        }
        finally
        {
            await stop.CancelAsync();
            await edits;
        }
    }

    [Fact]
    public async Task RevokedToken_CannotOpenASocket()
    {
        var (id, token, _) = await CreateToken("gone", "read");
        using (var session = factory.CreateApiClient())
            Assert.Equal(HttpStatusCode.NoContent, (await session.DeleteAsync($"/api/tokens/{id}", Ct)).StatusCode);

        var ws = factory.Server.CreateWebSocketClient();
        ws.SubProtocols.Add(EngineRuntime.WebSocketProtocol);
        ws.SubProtocols.Add(EngineRuntime.WebSocketAuthPrefix + token);
        ws.ConfigureRequest = request => request.Headers.Origin = EngineFactory.DevOrigin;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(new Uri("ws://localhost/ws"), Ct));
        Assert.Contains("401", error.Message);

        await using var byMessage = await PreviewClient.ConnectAsync(factory, Ct, EngineRuntime.WebSocketProtocol);
        await byMessage.SendAsync(new { type = "auth", token }, Ct);
        var closed = await byMessage.ReceiveAsync(Ct);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, closed.CloseStatus);
        Assert.Equal("auth rejected", byMessage.Socket.CloseStatusDescription);
    }

    /// <summary>
    /// An integration token's secret reaches no log: created, used over REST and <c>/ws</c> (both ways of
    /// authenticating), refused a scope, revoked with a socket open, and refused after, the engine's console and file
    /// sinks hold the token's id but never its secret.
    /// </summary>
    [Fact]
    public async Task Secret_IsNeverLogged()
    {
        var console = new StringWriter();
        var (stdout, stderr) = (Console.Out, Console.Error);
        Console.SetOut(TextWriter.Synchronized(console));
        Console.SetError(TextWriter.Synchronized(console));
        string id, token;
        var logs = new List<(string Sink, string Text)>();
        using var engine = new EngineFactory();
        try
        {
            using var session = engine.CreateApiClient();
            (id, token, _) = await CreateToken(session, "logged", "read");
            using var client = engine.CreateRawClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/session", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/profiles", new StringContent("{}"), Ct)).StatusCode);

            await using var byProtocol = await PreviewClient.ConnectAsync(engine, Ct, EngineRuntime.WebSocketProtocol, EngineRuntime.WebSocketAuthPrefix + token);
            await using var byMessage = await PreviewClient.ConnectAsync(engine, Ct, EngineRuntime.WebSocketProtocol);
            await byMessage.SendAsync(new { type = "auth", token }, Ct);
            await byProtocol.ReceiveTextAsync("hello", Ct);
            await byMessage.ReceiveTextAsync("hello", Ct);

            Assert.Equal(HttpStatusCode.NoContent, (await session.DeleteAsync($"/api/tokens/{id}", Ct)).StatusCode);
            foreach (var closed in new[] { byProtocol, byMessage })
            {
                while ((await closed.ReceiveAsync(Ct)).CloseStatus == null) { }
            }

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/session", Ct)).StatusCode);
            await using var rejected = await PreviewClient.ConnectAsync(engine, Ct, EngineRuntime.WebSocketProtocol);
            await rejected.SendAsync(new { type = "auth", token }, Ct);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, (await rejected.ReceiveAsync(Ct)).CloseStatus);

            // The file sink flushes every event; the engine still has the file open.
            foreach (var file in Directory.GetFiles(Path.Combine(engine.DataDir, "logs"), "infopanel-*.log"))
            {
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                logs.Add(("file", await reader.ReadToEndAsync(Ct)));
            }
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }

        logs.Add(("console", console.ToString()));
        Assert.Contains(logs, l => l.Sink == "file");
        foreach (var (sink, text) in logs)
        {
            Assert.True(text.Contains($"Created integration token {id}") && text.Contains($"Revoked integration token {id}"),
                $"The {sink} log has no token events:\n{text}");
            Assert.False(text.Contains(token[TokenStore.SecretPrefix.Length..]), $"The {sink} log holds the secret");
        }
    }

    // ---- storage ----

    [Fact]
    public async Task TokensFile_HoldsNoSecret_AndIsOwnerOnly()
    {
        var (id, token, _) = await CreateToken("stored", "read", "edit");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(factory.DataDir, "tokens.json"), Ct);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(token)));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(token[TokenStore.SecretPrefix.Length..])));
        Assert.Contains(id, text);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))), text);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(factory.DataDir, "tokens.json")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(factory.DataDir));
        }
    }

    [Fact]
    public async Task Tokens_SurviveARestart()
    {
        using var engine = new EngineFactory();
        string id, token;
        using (var first = engine.WithWebHostBuilder(_ => { }))
        {
            using var session = first.CreateClient();
            session.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
            (id, token, _) = await CreateToken(session, "kept", "read");
            using var use = first.CreateClient();
            use.DefaultRequestHeaders.Authorization = new("Bearer", token);
            Assert.Equal(HttpStatusCode.OK, (await use.GetAsync("/api/session", Ct)).StatusCode);
        }

        using var second = engine.WithWebHostBuilder(_ => { });
        using var again = second.CreateClient();
        again.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
        var listed = (await again.GetFromJsonAsync<JsonElement>("/api/tokens", Ct)).EnumerateArray().Single();
        Assert.Equal(id, listed.GetProperty("id").GetString());
        Assert.Equal("kept", listed.GetProperty("name").GetString());
        Assert.True(listed.TryGetProperty("lastUsed", out _));

        using var client = second.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/session", Ct)).StatusCode);
    }
}

/// <summary><see cref="TokenStore"/> on its own: the file, constant-time lookup and last use.</summary>
public sealed class TokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "infopanel-token-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.Zero));

    public TokenStoreTests() => Directory.CreateDirectory(_dir);

    private string File_ => Path.Combine(_dir, "tokens.json");

    private TokenStore Open() => TokenStore.Open(File_, _dir, _time, Serilog.Core.Logger.None);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Lookup_ComparesWithEveryStoredHash_WhicheverMatches()
    {
        var store = Open();
        var secrets = Enumerable.Range(0, 5).Select(i => store.Create($"t{i}", TokenScopes.Read).Secret).ToList();
        var compared = 0;
        store.Compared = () => compared++;

        foreach (var candidate in new[] { secrets[0], secrets[^1], TokenStore.SecretPrefix + "unknown" })
        {
            compared = 0;
            store.Authenticate(candidate);
            Assert.Equal(secrets.Count, compared);
        }

        Assert.Equal(store.List()[2].Id, store.Authenticate(secrets[2])?.TokenId);
    }

    [Fact]
    public void LastUse_IsWrittenAtMostOncePerMinute()
    {
        var store = Open();
        var (record, secret) = store.Create("used", TokenScopes.Read);
        Assert.Null(record.LastUsed);

        store.Authenticate(secret);
        var first = Open().List().Single().LastUsed;
        Assert.Equal(_time.GetUtcNow(), first);

        _time.Advance(TimeSpan.FromSeconds(30));
        store.Authenticate(secret);
        Assert.Equal(first, Open().List().Single().LastUsed);

        _time.Advance(TimeSpan.FromSeconds(31));
        store.Authenticate(secret);
        Assert.Equal(_time.GetUtcNow(), Open().List().Single().LastUsed);
    }

    /// <summary>
    /// While the file can't be written, a use is still allowed and its time kept in memory, so the write is tried again
    /// once a minute, not by every request; once it can be written again, the next try stores the time.
    /// </summary>
    [Fact]
    public void LastUse_FailedWrite_IsRetriedAtMostOncePerMinute()
    {
        var store = Open();
        var (_, secret) = store.Create("failing", TokenScopes.Read);
        var tries = 0;
        var failing = true;
        store.Saving = () =>
        {
            tries++;
            if (failing)
                throw new IOException("injected failure");
        };

        Assert.NotNull(store.Authenticate(secret));
        Assert.Equal(1, tries);
        Assert.Equal(_time.GetUtcNow(), store.List().Single().LastUsed);
        Assert.Null(Open().List().Single().LastUsed);

        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            Assert.NotNull(store.Authenticate(secret));
        }

        Assert.Equal(1, tries);

        _time.Advance(TimeSpan.FromSeconds(11));
        Assert.NotNull(store.Authenticate(secret));
        Assert.Equal(2, tries);

        failing = false;
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.NotNull(store.Authenticate(secret));
        Assert.Equal(3, tries);
        Assert.Equal(_time.GetUtcNow(), Open().List().Single().LastUsed);
    }

    /// <summary>
    /// The file is read tolerantly: members this engine doesn't know, in the file and in a token, are ignored and kept
    /// whenever the engine rewrites it (a last use, a create, a revoke).
    /// </summary>
    [Fact]
    public void UnknownMembers_AreKept_WhenTheFileIsRewritten()
    {
        var (_, secret) = Open().Create("kept", TokenScopes.Read);
        var file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(File_))!.AsObject();
        file["note"] = "from a later engine";
        file["tokens"]![0]!["label"] = new System.Text.Json.Nodes.JsonObject { ["color"] = "blue" };
        File.WriteAllText(File_, file.ToJsonString());

        var store = Open();
        Assert.NotNull(store.Authenticate(secret));
        var afterUse = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(File_))!;
        Assert.Equal(TokenStore.Format(_time.GetUtcNow()), afterUse["tokens"]![0]!["lastUsed"]?.GetValue<string>());

        var (other, _) = store.Create("other", TokenScopes.Read);
        Assert.True(store.Revoke(other.Id));

        foreach (var text in new[] { afterUse.ToJsonString(), File.ReadAllText(File_) })
        {
            var after = System.Text.Json.Nodes.JsonNode.Parse(text)!;
            Assert.Equal("from a later engine", after["note"]?.GetValue<string>());
            Assert.Equal("blue", after["tokens"]![0]!["label"]?["color"]?.GetValue<string>());
        }
        Assert.Equal("kept", Assert.Single(Open().List()).Name);
    }

    /// <summary>
    /// A member this engine doesn't know is kept as JSON text, whatever it holds: a string that holds no text no longer
    /// stops the open, and it is written back as it was.
    /// </summary>
    [Fact]
    public void AnUnknownMemberItCantDecode_IsKeptAsItWas()
    {
        Open().Create("kept", TokenScopes.Read);
        File.WriteAllText(File_, File.ReadAllText(File_).Replace("\"tokens\"", "\"later\": \"\\uD800\", \"tokens\""));

        Open().Create("other", TokenScopes.Read);

        Assert.Contains("\"later\":\"\\uD800\"", File.ReadAllText(File_));
        Assert.Equal(["kept", "other"], Open().List().Select(t => t.Name));
    }

    /// <summary>
    /// The members it keeps are written back compact, as they were, never re-indented: a deep, wide member costs its own
    /// bytes and no more when the file is rewritten.
    /// </summary>
    [Fact]
    public void KeptMembers_AreWrittenBackCompact()
    {
        Open().Create("kept", TokenScopes.Read);
        var wide = new string('[', 50) + string.Join(",", Enumerable.Repeat("1", 1000)) + new string(']', 50);
        var text = File.ReadAllText(File_).Replace("\"tokens\"", $"\"later\": {wide}, \"tokens\"");
        File.WriteAllText(File_, text);

        Open().Create("other", TokenScopes.Read);

        var after = File.ReadAllText(File_);
        Assert.Contains($"\"later\":{wide}", after);
        Assert.True(after.Length < text.Length + 400, $"{after.Length} bytes after the create, {text.Length} before");
    }

    /// <summary>
    /// <c>tokens.json</c> is held to its own byte cap, 16 MiB, the members it keeps included: a larger file is unreadable
    /// (the engine stops) and left as it is, and a create that would take the file over it is refused, nothing written.
    /// </summary>
    [Fact]
    public void TheFile_IsHeldToItsByteCap_KeptMembersIncluded()
    {
        const int Cap = 16 * 1024 * 1024; // TokenStore.MaxFileBytes, docs/2.0/data-folder.md
        Open().Create("kept", TokenScopes.Read);
        var text = File.ReadAllText(File_);
        string Padded(int bytes) => text.Replace("\"tokens\"", $"\"later\":\"{new string('x', bytes - text.Length - 11)}\",\"tokens\"");

        var over = Padded(Cap + 1);
        Assert.Equal(Cap + 1, over.Length);
        File.WriteAllText(File_, over);
        Assert.Throws<InvalidDataException>(Open);
        Assert.Equal(over, File.ReadAllText(File_));

        var full = Padded(Cap - 100);
        File.WriteAllText(File_, full);
        var store = Open();
        Assert.ThrowsAny<IOException>(() => store.Create("over", TokenScopes.Read));
        Assert.Equal(["kept"], store.List().Select(t => t.Name));
        Assert.Equal(full, File.ReadAllText(File_));
    }

    /// <summary>
    /// A file a few bytes under its cap, whose tokens are then each used once: recording a use would take it over, so no
    /// use is written, and what a revoke writes is the file as it is on disk without the token, which never grows it. The
    /// revoke succeeds, the token stops working at once, and the file is smaller and still readable.
    /// </summary>
    [Fact]
    public void Revoking_PastTheCap_AfterUsesThatCouldntBeRecorded_Succeeds()
    {
        const int Cap = 16 * 1024 * 1024; // TokenStore.MaxFileBytes
        var created = Open();
        var secrets = Enumerable.Range(0, 10).Select(i => created.Create($"t{i}", TokenScopes.Read).Secret).ToList();
        var text = File.ReadAllText(File_);
        var full = text.Replace("\"tokens\"", $"\"later\":\"{new string('x', Cap - 20 - text.Length - 11)}\",\"tokens\"");
        Assert.Equal(Cap - 20, full.Length); // under the cap by less than one recorded use
        File.WriteAllText(File_, full);
        var store = Open();

        foreach (var secret in secrets)
            Assert.NotNull(store.Authenticate(secret));
        Assert.Equal(full, File.ReadAllText(File_));

        var revoked = store.List()[3];
        Assert.True(store.Revoke(revoked.Id));

        Assert.Null(store.Authenticate(secrets[3]));
        Assert.False(store.IsActive(revoked.Id));
        var after = File.ReadAllText(File_);
        Assert.True(after.Length < full.Length, $"{after.Length} bytes after the revoke, {full.Length} before");
        Assert.Equal(9, Open().List().Count);
        Assert.DoesNotContain(Open().List(), t => t.Id == revoked.Id);
    }

    [Fact]
    public void AFifoAtThePath_StopsTheOpen_WithoutBlocking()
    {
        if (!InfoPanel.Core.Tests.Fifo.Supported)
            return;
        InfoPanel.Core.Tests.Fifo.Create(File_);

        InfoPanel.Core.Tests.Fifo.Within(10, () => Assert.Throws<InvalidDataException>(Open), File_);

        Assert.True(File.Exists(File_));
    }

    [Fact]
    public void UnreadableFile_StopsTheOpen_AndIsLeftAlone()
    {
        File.WriteAllText(File_, "{ not json");
        Assert.Throws<InvalidDataException>(Open);
        Assert.Equal("{ not json", File.ReadAllText(File_));

        File.WriteAllText(File_, """{"tokens":[{"id":"0123456789abcdef0123456789abcdef","name":"x","kind":"session","scopes":["read"],"created":"2026-10-27T09:30:00Z","sha256":"00"}]}""");
        Assert.Throws<InvalidDataException>(Open);
    }

    /// <summary>
    /// <c>tokens.json</c> as a link to a drive that isn't there: the tokens aren't "none yet", so the open stops (the engine
    /// with it) and the link is left as it is, never replaced by a new file that would disconnect them.
    /// </summary>
    [Fact]
    public void ALinkToNothingAtThePath_StopsTheOpen_AndIsLeftAsALink()
    {
        if (OperatingSystem.IsWindows())
            return; // symbolic links need privileges there
        var target = Path.Combine(_dir, "unmounted", "tokens.json");
        File.CreateSymbolicLink(File_, target);

        Assert.Throws<InvalidDataException>(Open);

        Assert.Equal(target, new FileInfo(File_).LinkTarget);
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
    }

    /// <summary>Only a missing file is no tokens: a folder at its path stops the open too, and is left as it is.</summary>
    [Fact]
    public void FolderAtThePath_StopsTheOpen_AndIsLeftAlone()
    {
        Directory.CreateDirectory(Path.Combine(File_, "inside"));
        Assert.Throws<InvalidDataException>(Open);
        Assert.True(Directory.Exists(Path.Combine(File_, "inside")));
    }

    [Fact]
    public void Open_RemovesTempFilesOfAnInterruptedWrite()
    {
        var temp = Path.Combine(_dir, $"tokens.json.{Guid.NewGuid():N}.tmp");
        var other = Path.Combine(_dir, "tokens.json.keep.tmp");
        File.WriteAllText(temp, "x");
        File.WriteAllText(other, "x");

        Open();
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Revoke_RaisesRevoked_AndForgetsTheToken()
    {
        var store = Open();
        var (record, secret) = store.Create("gone", TokenScopes.Read | TokenScopes.Edit);
        var raised = new List<string>();
        store.Revoked += raised.Add;

        Assert.True(store.Revoke(record.Id));
        Assert.Equal([record.Id], raised);
        Assert.Null(store.Authenticate(secret));
        Assert.False(store.IsActive(record.Id));
        Assert.False(store.Revoke(record.Id));
        Assert.Empty(Open().List());
    }
}
