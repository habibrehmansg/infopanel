using System.Net;
using System.Text;
using Xunit;

namespace InfoPanel.Tests.Contract;

/// <summary>
/// The response conformance check (<see cref="ContractCheck"/>) is on for every engine host, and it refuses what
/// spec/openapi.json doesn't declare: a status, a media type, a body, a missing header.
/// </summary>
public class ConformanceTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApiContract Contract => ApiContract.Instance;

    private static Exchange Response(string key, int status, string? contentType, string body, params string[] headers) =>
        new(key.Split(' ')[0], key.Split(' ')[1], status, contentType, Encoding.UTF8.GetBytes(body), headers.Contains);

    private static string? Check(string key, Exchange r) => ContractCheck.Conforms(Contract, Contract.Find(key)!, r);

    [Fact]
    public async Task EveryEngineHost_ChecksItsResponses()
    {
        using var api = factory.CreateApiClient();
        using (var session = await api.GetAsync("/api/session", Ct))
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using (var missing = await api.GetAsync("/api/profiles/00000000-0000-4000-8000-000000000000", Ct))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        Assert.Contains(("GET /api/session", 200), factory.Contract.Seen);
        Assert.Contains(("GET /api/profiles/{id}", 404), factory.Contract.Seen);
        Assert.Empty(factory.Contract.Violations);
    }

    [Fact]
    public void AConformingResponse_Passes()
    {
        Assert.Null(Check("GET /api/sensors", Response("GET /api/sensors", 200, "application/json; charset=utf-8",
            """[{"key":"k","source":"s","group":"g","name":"n","unit":"","status":"live"}]""")));
        Assert.Null(Check("DELETE /api/tokens/{id}", Response("DELETE /api/tokens/{id}", 204, null, "")));
    }

    [Fact]
    public void AnUndeclaredStatus_Fails_NamingTheOperation()
    {
        var violation = Check("POST /api/profiles/{id}/save", Response("POST /api/profiles/x/save", 202, "application/json", "{}"));
        Assert.NotNull(violation);
        Assert.Contains("POST /api/profiles/{id}/save (save) answered 202", violation);
    }

    [Fact]
    public void AMemberTheDefinitionLacks_Fails_NamingTheOperationAndTheDefinition()
    {
        var violation = Check("GET /api/sensors", Response("GET /api/sensors", 200, "application/json",
            """[{"key":"k","source":"s","group":"g","name":"n","unit":"","status":"live","extra":1}]"""));
        Assert.NotNull(violation);
        Assert.Contains("GET /api/sensors (listSensors)", violation);
        Assert.Contains("/0", violation);
    }

    [Fact]
    public void AnotherMediaType_Fails() =>
        Assert.Contains("as text/plain", Check("GET /api/session", Response("GET /api/session", 200, "text/plain", "ok")));

    [Fact]
    public void ABodyWhereNoneIsDeclared_Fails() =>
        Assert.Contains("with a body", Check("DELETE /api/profiles/{id}", Response("DELETE /api/profiles/x", 204, "application/json", "{}")));

    [Fact]
    public void AMissingRequiredHeader_Fails() =>
        Assert.Contains("WWW-Authenticate", Check("GET /api/session", Response("GET /api/session", 401, "application/problem+json",
            """{"title":"Unauthorized","status":401}""")));

    [Fact]
    public void AProblemBody_IsCheckedAgainstItsDefinition()
    {
        Assert.Null(Check("GET /api/profiles/{id}", Response("GET /api/profiles/x", 404, "application/problem+json",
            """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404,"detail":"No profile 'x'.","traceId":"00-1"}""")));
        Assert.Contains("isn't problem", Check("GET /api/profiles/{id}", Response("GET /api/profiles/x", 404, "application/problem+json",
            """{"title":"Not Found","status":404,"code":"missing"}""")));
    }

    [Fact]
    public void ARequest_IsTheOperationWhoseTemplateItFills()
    {
        Assert.Equal("GET /api/profiles/{id}", Contract.Match("get", "/api/profiles/3dd19c0e-675a-4591-b096-83417853861f")?.Key);
        Assert.Equal("GET /api/profiles/{id}/thumbnail", Contract.Match("GET", "/API/Profiles/x/thumbnail/")?.Key);
        Assert.Equal("HEAD /api/blobs/{id}", Contract.Match("HEAD", "/api/blobs/sha256:00")?.Key);
        Assert.Null(Contract.Match("PUT", "/api/profiles/x"));
        Assert.Null(Contract.Match("GET", "/api/profiles/x/y/z"));
        Assert.Null(Contract.Match("GET", "/api//profiles"));
    }
}
