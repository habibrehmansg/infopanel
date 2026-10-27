using InfoPanel.Api;
using InfoPanel.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Contract;

/// <summary>
/// The engine's routes against spec/openapi.json, both ways: every route the engine serves is an operation of the
/// document and every operation is served, with the scope its endpoint declares. The catch-all fallbacks (the 404s under
/// <c>/api</c>, <c>/ws</c> and <c>/auth</c>, and the web client's pages) are the only routes left out. A route is its
/// methods (<see cref="ContractCheck.Methods"/>: an endpoint for any method is reported, unless it is the socket, which is
/// its <c>GET</c>; <c>HEAD</c> as mapped) and its template with parameters as <c>{name}</c> and no trailing slash.
/// </summary>
public class RouteCoverageTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static ApiContract Contract => ApiContract.Instance;

    private List<RouteEndpoint> Endpoints() =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];

    private List<(string Key, string? Scope, string Name)> Served() => Served(Endpoints());

    /// <summary>Every served route: its key (<c>GET /api/session</c>), the scope it declares, and its endpoint's name.</summary>
    internal static List<(string Key, string? Scope, string Name)> Served(IEnumerable<RouteEndpoint> endpoints) =>
    [
        .. endpoints.Where(e => !ContractCheck.IsFallback(e)).SelectMany(e =>
            ContractCheck.Methods(e).Select(method =>
                ($"{method} {ContractCheck.Template(e.RoutePattern)}",
                    e.Metadata.GetMetadata<ApiScopeMetadata>() is { } scope ? ApiScopes.Name(scope.Scope) : null,
                    e.DisplayName ?? e.RoutePattern.RawText ?? "?"))),
    ];

    /// <summary>The served routes that aren't operations, as <c>KEY (endpoint)</c>.</summary>
    internal static List<string> NotOperations(IEnumerable<RouteEndpoint> endpoints) =>
        [.. Served(endpoints).Where(r => Contract.Find(r.Key) is null).Select(r => $"{r.Key} ({r.Name})")];

    [Fact]
    public void EveryServedRoute_IsAnOperation()
    {
        var missing = NotOperations(Endpoints());
        Assert.True(missing.Count == 0, $"Served by the engine, but not an operation in spec/openapi.json: {string.Join(", ", missing)}");
    }

    private static RouteEndpoint Endpoint(string pattern, params object[] metadata) =>
        new(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, new EndpointMetadataCollection(metadata), pattern);

    [Fact]
    public void AnEndpointForAnyMethod_IsReported_ExceptTheSocket()
    {
        // ASP.NET serves an endpoint with no methods, or an empty list, for every method: only the socket may be one.
        Assert.Equal(["ANY /api/probe (/api/probe)"], NotOperations([Endpoint("/api/probe", new HttpMethodMetadata([]))]));
        Assert.Equal(["ANY /api/probe (/api/probe)"], NotOperations([Endpoint("/api/probe")]));
        Assert.Empty(NotOperations([Endpoint("/ws"), Endpoint("/api/session", new HttpMethodMetadata(["GET"]))]));
    }

    [Fact]
    public void EveryOperation_IsServed()
    {
        var served = Served().Select(r => r.Key).ToHashSet();
        var missing = Contract.Operations.Where(o => !served.Contains(o.Key)).Select(o => o.ToString()).ToList();
        Assert.True(missing.Count == 0, $"In spec/openapi.json, but not served by the engine: {string.Join(", ", missing)}");
        Assert.Equal(Contract.Operations.Count, Contract.Operations.Select(o => o.Key).Distinct().Count());
    }

    [Fact]
    public void EveryOperation_NamesTheScopeItsEndpointDeclares()
    {
        // read, edit or session as the security requirement's role; none for what needs no token.
        var wrong = Served().Where(r => Contract.Find(r.Key) is { } op && op.Scope != r.Scope)
            .Select(r => $"{r.Key}: spec/openapi.json says {Contract.Find(r.Key)!.Scope ?? "none"}, the endpoint {r.Scope ?? "none"}").ToList();
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public void OnlyTheFallbacks_AreLeftOut()
    {
        var left = Endpoints().Where(ContractCheck.IsFallback).Select(e => e.RoutePattern.RawText).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(4, left.Count);
        Assert.Contains("/api/{**rest}", left);
        Assert.Contains("/ws/{**rest}", left);
        Assert.Contains("/auth/{**rest}", left);
    }

    [Fact]
    public void InfoVersion_IsTheProtocol() =>
        Assert.Equal(ReadEndpoints.Protocol.ToString(System.Globalization.CultureInfo.InvariantCulture), Contract.Version);
}
