using System.Text.Json;
using InfoPanel.Documents;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Security;
using InfoPanel.Spec;
using static InfoPanel.Api.ApiResults;

namespace InfoPanel.Api;

/// <summary>
/// The USB panel devices (spec/docs/api.md "Devices", the <c>devices</c> feature): <c>GET /api/devices</c> (every
/// token's) lists the saved panels and their state from <see cref="PanelManager"/>; the session token's <c>PATCH
/// /api/devices/{id}</c> merges settings into a panel's (applied at once, saved to <c>devices.json</c>) and <c>DELETE</c>
/// forgets it. Where the engine drives no panels the list is empty and <c>supported</c> false with why; while
/// <c>devices.json</c> can't be held, every change is 409 and the file is left as it is; a change the file can't take
/// (written first) is 409 too, and changes nothing.
/// </summary>
public static class DeviceEndpoints
{
    private const string ChangeSchema = SpecSchemas.Api + "#/$defs/deviceChange";

    /// <summary>The settings a BeadaPanel honours, in the order the API lists them.</summary>
    private static readonly string[] BeadaPanelSettings = ["enabled", "profileId", "rotation", "brightness"];

    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var devices = app.MapGroup("/api/devices");

        devices.MapGet("", async (PanelService panels) =>
        {
            if (panels.Manager is not { } manager)
                return Json(new DeviceList { Supported = false, Reason = panels.Unsupported ?? "off", Version = 0, Devices = [] });
            // The version read first: a change after it shows in the list and moves the version on, so a client that
            // reads again on the next devices message misses nothing.
            var version = manager.Version;
            var list = await manager.ListAsync();
            return Json(new DeviceList { Supported = true, Reason = null, Version = version, Devices = [.. list.Select(EntryOf)] });
        }).RequireScope(ApiScope.Read);

        devices.MapPatch("/{id}", async (string id, HttpRequest request, PanelService panels, Workspace workspace, CancellationToken ct) =>
        {
            if (panels.Unsupported == "store")
                return StoreUnheld();
            if (panels.Manager is not { } manager || (await manager.ListAsync()).FirstOrDefault(s => s.Saved.Id == id) is not { } found)
                return NoDevice(id);

            var limits = workspace.Limits;
            var (bytes, over) = await ReadBodyAsync(request, limits.RequestBytes, ct);
            if (over)
                return Invalid("", "limit.requestBytes", $"The body is over the limit of {limits.RequestBytes} bytes (requestBytes).");
            using var json = ParseJson(bytes);
            if (json == null)
                return Invalid("", "json", "The body isn't JSON.");
            var check = SpecSchemas.Strict.Validate(json.RootElement, ChangeSchema, limits.JsonDepth);
            if (!check.IsValid)
                return InvalidBody([.. check.Errors.Select(ErrorBody)]);

            var settings = json.RootElement.GetProperty("settings");
            var supports = SupportsOf(found.Saved.Family);
            var refused = settings.EnumerateObject().Where(m => !supports.Contains(m.Name)).Select(m => new ValidationErrorBody
            {
                Path = $"/settings/{m.Name}",
                Code = "device.setting",
                Message = $"A {found.Saved.Family} device has no setting '{m.Name}'.",
            }).ToList();
            if (refused.Count > 0)
                return InvalidBody(refused);

            // Read through the format's model: an integer spelled 50.0 or 9e1 is one, and a null profileId clears it.
            var change = SpecJson.Read<DeviceChangeRequest>(json.RootElement).Settings;
            SavedPanel? changed;
            try
            {
                changed = await manager.UpdateAsync(id, saved => Merge(saved, change));
            }
            catch (PanelsNotSavedException)
            {
                return StoreUnwritten();
            }
            if (changed == null || (await manager.ListAsync()).FirstOrDefault(s => s.Saved.Id == id) is not { } entry)
                return NoDevice(id); // forgotten meanwhile
            return Json(EntryOf(entry));
        }).WithOwnBodyLimit().RequireScope(ApiScope.Session);

        devices.MapDelete("/{id}", async (string id, PanelService panels) =>
        {
            if (panels.Unsupported == "store")
                return StoreUnheld();
            if (panels.Manager is not { } manager)
                return NoDevice(id);
            try
            {
                return await manager.ForgetAsync(id) ? Results.NoContent() : NoDevice(id);
            }
            catch (PanelsNotSavedException)
            {
                return StoreUnwritten();
            }
        }).RequireScope(ApiScope.Session);
    }

    private static IReadOnlyList<string> SupportsOf(string family) => family == SavedPanel.BeadaPanelFamily ? BeadaPanelSettings : [];

    /// <summary>The settings given, over the saved ones; a member left out keeps its value. The body is checked already.</summary>
    private static SavedPanel Merge(SavedPanel saved, DeviceSettingsChange change) => saved with
    {
        Enabled = change.Enabled ?? saved.Enabled,
        ProfileId = change.Removed.Contains(nameof(DeviceSettingsChange.ProfileId)) ? null : change.ProfileId ?? saved.ProfileId,
        Rotation = change.Rotation is { } rotation ? (PanelRotation)rotation : saved.Rotation,
        Brightness = change.Brightness ?? saved.Brightness,
    };

    internal static DeviceEntryBody EntryOf(PanelStatus status)
    {
        var saved = status.Saved;
        var beadaPanel = saved.Family == SavedPanel.BeadaPanelFamily;
        var model = status.Info is { } info
            ? new DeviceModelBody { Id = info.Model.Id, Width = info.Width, Height = info.Height }
            : beadaPanel && BeadaPanelModel.All.FirstOrDefault(m => m.Id == saved.Model) is { } known
                ? new DeviceModelBody { Id = known.Id, Width = known.Width, Height = known.Height }
                : null;
        return new DeviceEntryBody
        {
            Id = saved.Id,
            Family = saved.Family,
            Model = model,
            Connected = status.Connected,
            // A start waiting for the panel to take a stream again is starting, its reason saying so.
            State = status.State == "waiting" ? "starting" : status.State,
            Reason = status.Reason is { } reason
                ? new DeviceReasonBody { Code = status.ReasonCode ?? "io", Message = reason.Length > 4096 ? reason[..4096] : reason }
                : null,
            Settings = new DeviceSettingsBody
            {
                Enabled = saved.Enabled,
                // A stored value that isn't a document's id names no profile: answered as none, and kept.
                ProfileId = DocumentStore.IsValidId(saved.ProfileId) ? saved.ProfileId : null,
                Rotation = (int)saved.Rotation,
                Brightness = saved.Brightness,
            },
            Supports = SupportsOf(saved.Family),
            Stats = status.Fps is { } fps && status.FrameTimeMs is { } frameTime ? new DeviceStatsBody { Fps = fps, FrameTimeMs = frameTime } : null,
            Details = new DeviceDetailsBody
            {
                UsbId = beadaPanel ? $"{BeadaPanelProtocol.VendorId:x4}:{BeadaPanelProtocol.ProductId:x4}" : null,
                Port = saved.Port,
                Serial = saved.Serial,
                Firmware = status.Info?.Firmware,
            },
        };
    }

    private static IResult NoDevice(string id) => Problems.NotFound($"No device '{id}'.");

    private static IResult StoreUnwritten() => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict",
        detail: "The engine couldn't write its devices file, so nothing changed; see its log.");

    private static IResult StoreUnheld() => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict",
        detail: "The engine couldn't read its devices file when it started, so it changes no device this run and leaves the file as it is; see its log.");
}
