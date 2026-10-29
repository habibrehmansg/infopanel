using InfoPanel.Documents;
using InfoPanel.Security;
using InfoPanel.Settings;
using InfoPanel.Spec;
using static InfoPanel.Api.ApiResults;

namespace InfoPanel.Api;

/// <summary>
/// The engine settings (spec/docs/api.md, "Settings"), for the session token only: <c>GET /api/settings</c> answers them
/// in effect (<c>engineSettings</c>); <c>PATCH /api/settings</c> merges a <c>settingsChange</c> into them through the
/// <see cref="SettingsStore"/>, which stores it before the answer. Checked in order: the settings can be changed (else 409
/// <c>settingsStatusProblem</c>), the body's size as received (<c>settingsRequestBytes</c>), the formats' JSON rules and
/// the schema, then the store's own checks (its limits); each failure is 422 <c>invalidProblem</c> and changes nothing.
/// </summary>
public static class SettingsEndpoints
{
    private const string ChangeSchema = SpecSchemas.Api + "#/$defs/settingsChange";

    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/settings");

        api.MapGet("", (SettingsStore settings) => Json(BodyOf(settings, settings.Current))).RequireScope(ApiScope.Session);

        api.MapPatch("", async (HttpRequest request, SettingsStore settings, Workspace workspace, CancellationToken ct) =>
        {
            // Settings the engine couldn't read at start-up are never changed: answered before the body is read, as a
            // profile that can't be opened is.
            if (settings.Status != SettingsStatus.Ok)
                return Unavailable(settings.Status, settings.Problem!);

            var limits = workspace.Limits;
            var (body, over) = await ReadBodyAsync(request, limits.SettingsRequestBytes, ct);
            if (over)
                return Invalid("", "limit.settingsRequestBytes", $"The body is over the limit of {limits.SettingsRequestBytes} bytes (settingsRequestBytes).");
            using var json = ParseJson(body);
            if (json == null)
                return Invalid("", "json", "The body isn't JSON.");

            var check = SpecSchemas.Strict.Validate(json.RootElement, ChangeSchema, limits.JsonDepth);
            if (!check.IsValid)
                return InvalidBody([.. check.Errors.Select(ErrorBody)]);

            try
            {
                return Json(BodyOf(settings, settings.Apply(json.RootElement)));
            }
            catch (SettingsChangeException refused)
            {
                return InvalidBody([.. refused.Errors.Select(e => new ValidationErrorBody { Path = e.Path, Code = e.Code, Message = e.Message })]);
            }
        }).WithOwnBodyLimit().RequireScope(ApiScope.Session);
    }

    /// <summary>The settings in effect as the API answers them: every value, the version, the status and its problem.</summary>
    internal static EngineSettingsBody BodyOf(SettingsStore store, SettingsSnapshot snapshot)
    {
        var settings = snapshot.Settings;
        return new EngineSettingsBody
        {
            Status = StatusName(store.Status),
            Problem = store.Problem is { } problem ? new ValidationErrorBody { Path = problem.Path, Code = problem.Code, Message = problem.Message } : null,
            Version = snapshot.Version,
            FrameRate = settings.FrameRate,
            GraphRate = settings.GraphRate,
            LogLevel = SettingsLimits.NameOf(settings.LogLevel),
            Sources = new SettingsSources
            {
                Libre = new LibreSettingsBody
                {
                    Enabled = settings.Libre.Enabled,
                    Storage = settings.Libre.Storage,
                    StorageInterval = settings.Libre.StorageInterval,
                },
            },
            Clients = settings.Clients,
        };
    }

    private static string StatusName(SettingsStatus status) => status switch
    {
        SettingsStatus.Ok => "ok",
        SettingsStatus.Unreadable => "unreadable",
        SettingsStatus.Unsupported => "unsupported",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>409 <c>settingsStatusProblem</c>: the settings' status and why. Their status is fixed at start-up.</summary>
    private static IResult Unavailable(SettingsStatus status, SpecError problem) => Problem(new SettingsStatusProblem
    {
        Title = "Settings can't be changed",
        Detail = "The engine couldn't read its stored settings when it started; it changes none until they are fixed or removed and it restarts.",
        Status = StatusCodes.Status409Conflict,
        Code = StatusName(status),
        Problem = new ValidationErrorBody { Path = problem.Path, Code = problem.Code, Message = problem.Message },
    }, StatusCodes.Status409Conflict);
}
