using InfoPanel.Documents;
using InfoPanel.Security;
using InfoPanel.Service;
using InfoPanel.Spec;
using static InfoPanel.Api.ApiResults;

namespace InfoPanel.Api;

/// <summary>
/// The configured service (spec/docs/api.md, "The configured service"), for the session token only: <c>GET
/// /api/service</c> answers its address and the state of the credential a client provides for it (<c>serviceState</c>),
/// never the credential; <c>PUT /api/service/credential</c> keeps one (204; 409 <c>noService</c> without an address; 422
/// for a body that breaks <c>serviceCredential</c> or <c>requestBytes</c>, never echoing the credential; 503
/// <c>storeUnavailable</c> when the store refuses); <c>DELETE /api/service/credential</c> removes it (204; 503).
/// </summary>
public static class ServiceEndpoints
{
    private const string CredentialSchema = SpecSchemas.Api + "#/$defs/serviceCredential";

    public static void MapServiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/service", (ServiceCredentials service, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Json(StateOf(service.Status));
        }).RequireScope(ApiScope.Session);

        var credential = app.MapGroup("/api/service/credential");

        credential.MapPut("", async (HttpRequest request, ServiceCredentials service, Workspace workspace, CancellationToken ct) =>
        {
            // The body's size as received, then the formats' JSON rules and the schema, as every body. No error carries
            // the value: the schema's messages name the rule, never the string.
            var limits = workspace.Limits;
            var (body, over) = await ReadBodyAsync(request, limits.RequestBytes, ct);
            if (over)
                return Invalid("", "limit.requestBytes", $"The body is over the limit of {limits.RequestBytes} bytes (requestBytes).");
            using var json = ParseJson(body);
            if (json == null)
                return Invalid("", "json", "The body isn't JSON.");
            var check = SpecSchemas.Strict.Validate(json.RootElement, CredentialSchema, limits.JsonDepth);
            if (!check.IsValid)
                return InvalidBody([.. check.Errors.Select(ErrorBody)]);

            // The schema's pattern and length are ServiceCredentials.IsWellFormed's (a trailing line break refused too).
            var given = SpecJson.Read<ServiceCredentialRequest>(json.RootElement).Credential;

            try
            {
                service.Put(given);
            }
            catch (NoServiceException)
            {
                return Problem(NoService(), StatusCodes.Status409Conflict);
            }
            catch (ServiceStoreException refused)
            {
                return Problem(StoreUnavailable(refused), StatusCodes.Status503ServiceUnavailable);
            }

            return Results.NoContent();
        }).WithOwnBodyLimit().RequireScope(ApiScope.Session);

        credential.MapDelete("", (ServiceCredentials service) =>
        {
            try
            {
                service.Delete();
            }
            catch (ServiceStoreException refused)
            {
                return Problem(StoreUnavailable(refused), StatusCodes.Status503ServiceUnavailable);
            }

            return Results.NoContent();
        }).RequireScope(ApiScope.Session);
    }

    internal static ServiceStateBody StateOf(ServiceStatus status) => new()
    {
        Address = status.Address,
        Credential = status.Credential switch
        {
            ServiceCredentialState.Absent => "absent",
            ServiceCredentialState.Present => "present",
            ServiceCredentialState.Rejected => "rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        },
        Store = status.Store,
        Fingerprint = status.Fingerprint,
    };

    private static ServiceProblem NoService() => new()
    {
        Title = "No service",
        Detail = "The engine has no service address (the service.address setting), so it keeps no credential.",
        Status = StatusCodes.Status409Conflict,
        Code = "noService",
    };

    internal static ServiceProblem StoreUnavailable(ServiceStoreException refused) => new()
    {
        Title = "Store unavailable",
        Detail = refused.Message,
        Status = StatusCodes.Status503ServiceUnavailable,
        Code = "storeUnavailable",
    };
}
