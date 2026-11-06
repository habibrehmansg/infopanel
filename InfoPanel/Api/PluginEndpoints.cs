using System.Globalization;
using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Plugins;
using InfoPanel.Security;
using InfoPanel.Spec;
using static InfoPanel.Api.ApiResults;

namespace InfoPanel.Api;

/// <summary>
/// The plugins (spec/docs/api.md "Plugins", the <c>plugins</c> feature): <c>GET /api/plugins</c> (every token's) lists the
/// packages found at start, never a setting's value; the session token's requests turn a package on or off, reload it,
/// open its folder, run a plugin's action and read and change its settings. Every request naming a package answers 404
/// for a folder that names none (all of them under <c>--no-plugins</c>).
/// </summary>
public static class PluginEndpoints
{
    private const string SwitchSchema = SpecSchemas.Api + "#/$defs/pluginSwitch";
    private const string SettingChangeSchema = SpecSchemas.Api + "#/$defs/pluginSettingChange";

    public static void MapPluginEndpoints(this IEndpointRouteBuilder app)
    {
        var plugins = app.MapGroup("/api/plugins");

        plugins.MapGet("", (PluginHostManager manager, PluginSensorSource source) => Json(new PluginListBody
        {
            Enabled = manager.Options.Enabled,
            Packages = [.. manager.Runners.Select(r => PackageOf(manager, source, r))],
        })).RequireScope(ApiScope.Read);

        plugins.MapPut("/{folder}/on", async (string folder, HttpRequest request, PluginHostManager manager, PluginSensorSource source, Workspace workspace,
            CancellationToken ct) =>
        {
            if (manager.Find(folder) is null)
                return NoPackage(folder);
            var (body, invalid) = await ReadAsync(request, workspace, SwitchSchema, ct);
            if (invalid != null)
                return invalid;
            var on = SpecJson.Read<PluginSwitchRequest>(body!.Value).On;

            return await manager.SetOnAsync(folder, on) switch
            {
                PluginSwitch.Done => Json(PackageOf(manager, source, manager.Find(folder)!)),
                PluginSwitch.NotFound => NoPackage(folder),
                PluginSwitch.Unsupported => Conflict($"The plugin {folder} doesn't run on this operating system."),
                PluginSwitch.StateUnavailable => Conflict("The engine couldn't read its plugin state file when it started, so it turns no plugin on or off this run; see its log."),
                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Internal Server Error",
                    detail: "The engine couldn't write the choice; nothing was changed. See its log."),
            };
        }).WithOwnBodyLimit().RequireScope(ApiScope.Session);

        plugins.MapPost("/{folder}/reload", async (string folder, PluginHostManager manager, PluginSensorSource source) =>
            await manager.ReloadAsync(folder) switch
            {
                PluginSwitch.Done => Json(PackageOf(manager, source, manager.Find(folder)!), StatusCodes.Status202Accepted),
                PluginSwitch.NotFound => NoPackage(folder),
                PluginSwitch.Unsupported => Conflict($"The plugin {folder} doesn't run on this operating system."),
                _ => Conflict($"The plugin {folder} is turned off."),
            }).RequireScope(ApiScope.Session);

        plugins.MapPost("/{folder}/open", (string folder, PluginHostManager manager, IFolderOpener opener, ILoggerFactory loggers) =>
        {
            if (manager.Find(folder) is not { } runner)
                return NoPackage(folder);
            try
            {
                opener.Open(runner.Package.Directory);
            }
            catch (Exception ex)
            {
                loggers.CreateLogger(typeof(PluginEndpoints).FullName!).LogError(ex, "Could not open the plugin folder {Path} in the file manager", runner.Package.Directory);
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Internal Server Error",
                    detail: $"The engine couldn't open the plugin {runner.Package.Folder}'s folder; see its log.");
            }
            return Results.NoContent();
        }).RequireScope(ApiScope.Session);

        plugins.MapPost("/{folder}/plugins/{id}/actions/{method}", async (string folder, string id, string method, PluginHostManager manager) =>
        {
            if (manager.Find(folder) is not { } runner)
                return NoPackage(folder);
            return await runner.RunActionAsync(id, method) switch
            {
                PluginCall.Done => Results.Accepted(),
                PluginCall.NotFound => Problems.NotFound($"The plugin {folder} has no running plugin '{id}' with the action '{method}'."),
                _ => NotRunning(folder),
            };
        }).RequireScope(ApiScope.Session);

        plugins.MapGet("/{folder}/plugins/{id}/settings", async (string folder, string id, PluginHostManager manager, HttpContext context) =>
        {
            if (manager.Find(folder) is not { } runner)
                return NoPackage(folder);
            context.Response.Headers.CacheControl = "no-store";
            var (outcome, settings) = await runner.GetSettingsAsync(id);
            return outcome switch
            {
                PluginCall.Done => Json(settings!),
                PluginCall.NotFound => Problems.NotFound($"The plugin {folder} has no running plugin '{id}' with settings."),
                PluginCall.NoAnswer => NoAnswer(folder),
                _ => NotRunning(folder),
            };
        }).RequireScope(ApiScope.Session);

        plugins.MapPatch("/{folder}/plugins/{id}/settings", async (string folder, string id, HttpRequest request, PluginHostManager manager, Workspace workspace,
            HttpContext context, CancellationToken ct) =>
        {
            if (manager.Find(folder) is not { } runner)
                return NoPackage(folder);
            // The body is held to its limit and definition as every body; no error carries the value.
            var (body, invalid) = await ReadAsync(request, workspace, SettingChangeSchema, ct);
            if (invalid != null)
                return invalid;
            var change = SpecJson.Read<PluginSettingChangeRequest>(body!.Value);

            context.Response.Headers.CacheControl = "no-store";
            var (outcome, problem, settings) = await runner.ApplySettingAsync(id, change.Key, change.Value);
            return outcome switch
            {
                PluginCall.Done => Json(settings!),
                PluginCall.Invalid => Invalid("/value", "setting.value", problem!),
                PluginCall.NotFound => Problems.NotFound($"The plugin {folder} has no running plugin '{id}' with a setting '{change.Key}'."),
                PluginCall.NoAnswer => NoAnswer(folder),
                _ => NotRunning(folder),
            };
        }).WithOwnBodyLimit().RequireScope(ApiScope.Session);
    }

    /// <summary>A package as the API lists it: never a setting's value.</summary>
    internal static PluginPackageBody PackageOf(PluginHostManager manager, PluginSensorSource source, PluginHostRunner runner)
    {
        var package = runner.Package;
        var info = package.Info;
        var supported = manager.IsSupported(runner);
        var state = runner.State;
        return new PluginPackageBody
        {
            Folder = package.Folder,
            Name = info.Name,
            Version = info.Version,
            Author = info.Author,
            Description = info.Description,
            Website = info.Website,
            Bundled = package.Bundled,
            Platforms = info.Platforms,
            On = manager.IsOn(runner),
            State = !supported ? "unsupported" : state switch
            {
                PluginHostState.Starting or PluginHostState.Restarting => "starting",
                PluginHostState.Running => "running",
                PluginHostState.Failed => "failed",
                _ => "off",
            },
            Reason = !supported
                ? new PluginReasonBody { Code = "platform", Message = $"its PluginInfo.ini names {string.Join(", ", info.Platforms)}, not {manager.Options.Platform}" }
                : state == PluginHostState.Failed
                    ? new PluginReasonBody { Code = runner.ReasonCode ?? "gaveUp", Message = Message(runner.Reason) }
                    : null,
            Cpu = runner.Cpu,
            Memory = runner.Memory,
            Sensors = source.CountOf(package.Folder),
            Plugins = [.. runner.Plugins.Select(p => new PluginInfoBody
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                Sensors = source.CountOf(package.Folder, p.Id),
                Actions = [.. p.Actions.Select(a => new PluginActionBody { Method = a.Method, Name = a.Name })],
                Configurable = p.Configurable,
            })],
            Errors = [.. runner.ErrorLog.Take(20).Select(e => new PluginErrorBody
            {
                Time = e.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                Plugin = e.Plugin,
                Message = e.Message,
            })],
        };
    }

    private static string Message(string? reason) =>
        string.IsNullOrEmpty(reason) ? "it failed" : reason.Length > 4096 ? reason[..4096] : reason;

    /// <summary>The body, held to <c>requestBytes</c>, the formats' JSON rules and <paramref name="schema"/>; else the 422 to answer.</summary>
    private static async Task<(System.Text.Json.JsonElement? Body, IResult? Invalid)> ReadAsync(HttpRequest request, Workspace workspace, string schema, CancellationToken ct)
    {
        var limits = workspace.Limits;
        var (bytes, over) = await ReadBodyAsync(request, limits.RequestBytes, ct);
        if (over)
            return (null, Invalid("", "limit.requestBytes", $"The body is over the limit of {limits.RequestBytes} bytes (requestBytes)."));
        using var json = ParseJson(bytes);
        if (json == null)
            return (null, Invalid("", "json", "The body isn't JSON."));
        var check = SpecSchemas.Strict.Validate(json.RootElement, schema, limits.JsonDepth);
        if (!check.IsValid)
            return (null, InvalidBody([.. check.Errors.Select(ErrorBody)]));
        return (json.RootElement.Clone(), null);
    }

    private static IResult NoPackage(string folder) => Problems.NotFound($"No plugin package '{folder}'.");

    private static IResult NotRunning(string folder) => Conflict($"The plugin {folder}'s host isn't running.");

    private static IResult NoAnswer(string folder) => Conflict($"The plugin {folder}'s host didn't answer within 10 seconds.");

    private static IResult Conflict(string detail) =>
        TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict");
}
