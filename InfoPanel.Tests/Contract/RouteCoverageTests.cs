using InfoPanel.Api;
using InfoPanel.Security;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Contract;

/// <summary>
/// The engine's routes against spec/openapi.json, both ways: every route the engine serves is an operation of the
/// document and every operation is served, with the scope its endpoint declares. The catch-all fallbacks (the 404s under
/// <c>/api</c>, <c>/ws</c> and <c>/auth</c>, and the web client's pages) are the only routes left out. A route is its
/// methods (an endpoint without methods, the socket, is its <c>GET</c>; <c>HEAD</c> as mapped) and its template with
/// parameters as <c>{name}</c> and no trailing slash.
/// </summary>
public class RouteCoverageTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static ApiContract Contract => ApiContract.Instance;

    private List<RouteEndpoint> Endpoints() =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];

    /// <summary>Every served route: its key (<c>GET /api/session</c>), the scope it declares, and its endpoint's name.</summary>
    private List<(string Key, string? Scope, string Name)> Served() =>
    [
        .. Endpoints().Where(e => !ContractCheck.IsFallback(e)).SelectMany(e =>
            (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(method =>
                ($"{method} {ContractCheck.Template(e.RoutePattern)}",
                    e.Metadata.GetMetadata<ApiScopeMetadata>() is { } scope ? ApiScopes.Name(scope.Scope) : null,
                    e.DisplayName ?? e.RoutePattern.RawText ?? "?"))),
    ];

    [Fact]
    public void EveryServedRoute_IsAnOperation()
    {
        var missing = Served().Where(r => Contract.Find(r.Key) is null).Select(r => $"{r.Key} ({r.Name})").ToList();
        Assert.True(missing.Count == 0, $"Served by the engine, but not an operation in spec/openapi.json: {string.Join(", ", missing)}");
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
