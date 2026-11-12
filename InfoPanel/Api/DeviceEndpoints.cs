using System.Text.Json;
using InfoPanel.Documents;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Security;
using InfoPanel.Spec;
using InfoPanel.Spec.Schema;
using static InfoPanel.Api.ApiResults;

namespace InfoPanel.Api;

/// <summary>
/// The USB panel devices (spec/docs/api.md "Devices", the <c>devices</c> feature): <c>GET /api/devices</c> (every
/// token's) lists the saved panels and their state from <see cref="PanelManager"/>; the session token's <c>POST
/// /api/devices</c> saves a panel that need not be plugged in (as given: its family, model, serial, 1.x device and settings), <c>PATCH
/// /api/devices/{id}</c> merges settings into a panel's (applied at once, saved to <c>devices.json</c>) and <c>DELETE</c>
/// forgets it. Where the engine drives no panels the list is empty and <c>supported</c> false with why; while
/// <c>devices.json</c> can't be held, every change is 409 and the file is left as it is; a change the file can't take
/// (written first) is 409 too, and changes nothing.
/// </summary>
public static class DeviceEndpoints
{
    private const string ChangeSchema = SpecSchemas.Api + "#/$defs/deviceChange";

    private const string CreateSchema = SpecSchemas.Api + "#/$defs/createDeviceRequest";

    /// <summary>The settings a BeadaPanel honours, in the order the API lists them (every family the engine drives honours them: the engine paces, turns and dims).</summary>
    private static readonly string[] BeadaPanelSettings = ["enabled", "profileId", "rotation", "brightness", "frameRate"];

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
            var detected = await manager.ListDetectedAsync();
            return Json(new DeviceList
            {
                Supported = true,
                Reason = null,
                Version = version,
                Devices = [.. list.Select(s => EntryOf(s, panels.Families))],
                Families = [.. panels.Families.Select(FamilyOf)],
                Detected = [.. detected.Select(DetectedOf)],
            });
        }).RequireScope(ApiScope.Read);

        devices.MapPost("", async (HttpRequest request, PanelService panels, Workspace workspace, CancellationToken ct) =>
        {
            if (panels.Unsupported == "store")
                return StoreUnheld();
            if (panels.Manager is not { } manager)
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict",
                    detail: "The engine drives no USB panels this run, so it saves none; see GET /api/devices for why.");

            var limits = workspace.Limits;
            var (bytes, over) = await ReadBodyAsync(request, limits.RequestBytes, ct);
            if (over)
                return Invalid("", "limit.requestBytes", $"The body is over the limit of {limits.RequestBytes} bytes (requestBytes).");
            using var json = ParseJson(bytes);
            if (json == null)
                return Invalid("", "json", "The body isn't JSON.");
            var check = SpecSchemas.Strict.Validate(json.RootElement, CreateSchema, limits.JsonDepth);
            if (!check.IsValid)
                return InvalidBody(ErrorsOf(check.Errors));

            // The engine stores what it is given: a family it drives, one of its models, the settings it honours.
            var body = SpecJson.Read<CreateDeviceRequest>(json.RootElement);
            // A family the engine knows: built in, or a panel package's (on, off or failed alike).
            if (panels.Families.FirstOrDefault(f => f.Id == body.Family) is not { } family)
                return Invalid("/family", "device.family", $"The engine drives no '{body.Family}' devices.");
            if (family.Models.All(m => m.Id != body.Model))
                return Invalid("/model", "device.model", $"A {body.Family} device has no model '{body.Model}'.");
            var supports = SupportsOf(body.Family, panels.Families);
            var refused = json.RootElement.GetProperty("settings").EnumerateObject().Where(m => !supports.Contains(m.Name)).Select(m => new ValidationErrorBody
            {
                Path = $"/settings/{m.Name}",
                Code = "device.setting",
                Message = $"A {body.Family} device has no setting '{m.Name}'.",
            }).ToList();
            if (refused.Count > 0)
                return InvalidBody(refused);
            if (CheckFamilySettings(null, json.RootElement, family, out var familySettings) is { } wrong)
                return wrong;

            var fresh = new SavedPanel(Guid.NewGuid().ToString("D"), body.Family, body.Model, body.Serial, Port: null)
            {
                V1 = body.V1 is { } v1 ? new PanelV1Identity(v1.DeviceId, v1.DeviceLocation) : null,
                FamilySettings = familySettings,
            };
            PanelSave save;
            try
            {
                save = await manager.SaveAsync(Merge(fresh, body.Settings));
            }
            catch (PanelsNotSavedException)
            {
                return StoreUnwritten();
            }
            if (save.Saved is not { } saved)
                return Problem(new ExistsProblem
                {
                    Title = "The device already exists",
                    Status = StatusCodes.Status409Conflict,
                    Code = "exists",
                    Detail = $"Device {save.Existing} is saved already, as the same 1.x device or with the same family and serial; nothing was saved.",
                    Id = save.Existing,
                }, StatusCodes.Status409Conflict);
            // The device as saved, from the save itself: a change since (another client's DELETE) doesn't change the answer.
            return Json(EntryOf(saved, panels.Families), StatusCodes.Status201Created);
        }).WithOwnBodyLimit().RequireScope(ApiScope.Session);

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
                return InvalidBody(ErrorsOf(check.Errors));

            var settings = json.RootElement.GetProperty("settings");
            var supports = SupportsOf(found.Saved.Family, panels.Families);
            var refused = settings.EnumerateObject().Where(m => !supports.Contains(m.Name)).Select(m => new ValidationErrorBody
            {
                Path = $"/settings/{m.Name}",
                Code = "device.setting",
                Message = $"A {found.Saved.Family} device has no setting '{m.Name}'.",
            }).ToList();
            if (refused.Count > 0)
                return InvalidBody(refused);
            // Checked against the family's declared settings here; merged over the panel's own values in the update.
            var family = panels.Families.FirstOrDefault(f => f.Id == found.Saved.Family);
            if (CheckFamilySettings(found.Saved.FamilySettings, json.RootElement, family, out _) is { } wrong)
                return wrong;

            // Read through the format's model: an integer spelled 50.0 or 9e1 is one, and a null profileId clears it.
            var change = SpecJson.Read<DeviceChangeRequest>(json.RootElement).Settings;
            var familyChange = json.RootElement.TryGetProperty("familySettings", out var members) ? members.Clone() : (JsonElement?)null;
            SavedPanel? changed;
            try
            {
                changed = await manager.UpdateAsync(id, saved => Merge(saved, change) with
                {
                    FamilySettings = familyChange is { } given && family != null
                        ? PanelFamilySettings.Apply(saved.FamilySettings, given, family.Settings, [.. family.Models.Select(m => m.Id)], out _)
                        : saved.FamilySettings,
                });
            }
            catch (PanelsNotSavedException)
            {
                return StoreUnwritten();
            }
            if (changed == null || (await manager.ListAsync()).FirstOrDefault(s => s.Saved.Id == id) is not { } entry)
                return NoDevice(id); // forgotten meanwhile
            return Json(EntryOf(entry, panels.Families));
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

    /// <summary>
    /// The settings a family honours: all four for every family the engine knows (it turns frames itself for every
    /// family, and sends the brightness to each), none for one it doesn't.
    /// </summary>
    private static IReadOnlyList<string> SupportsOf(string family, IReadOnlyList<PanelFamilyInfo> families) =>
        family == SavedPanel.BeadaPanelFamily || families.Any(f => f.Id == family) ? BeadaPanelSettings : [];

    /// <summary>
    /// A save's or change's schema errors as the body reports them (<see cref="ErrorBody"/>), but one about a single
    /// family setting's value (at <c>/familySettings/{id}</c> or within it: a value of a type no setting takes, a string
    /// too long) is that setting's: one <c>device.setting</c> error a setting, at its own path, as every other refused
    /// family setting is (spec/docs/api.md, Family settings).
    /// </summary>
    private static List<ValidationErrorBody> ErrorsOf(IEnumerable<SchemaError> errors)
    {
        const string Prefix = "/familySettings/";
        var bodies = new List<ValidationErrorBody>();
        var settings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var error in errors)
        {
            if (!error.InstancePath.StartsWith(Prefix, StringComparison.Ordinal) || error.Keyword is "more" or "json" or "depth")
            {
                bodies.Add(ErrorBody(error));
                continue;
            }
            var key = error.InstancePath[Prefix.Length..].Split('/')[0];
            if (settings.Add(key))
                bodies.Add(new ValidationErrorBody
                {
                    Path = Prefix + key,
                    Code = "device.setting",
                    Message = $"A family setting's value is true or false, a number, a string of at most {PanelFamilySettings.MaxStringChars} characters, or null: {error.Message} ({error.Keyword})",
                });
        }
        return bodies;
    }

    /// <summary>
    /// The body's <c>familySettings</c> (when it has them) checked against <paramref name="family"/>'s declared settings and
    /// models, over <paramref name="saved"/>: null with the values set after it in <paramref name="merged"/>, or the 422
    /// (<c>device.setting</c>, one error per member refused). A family the engine doesn't know declares none.
    /// </summary>
    private static IResult? CheckFamilySettings(string? saved, JsonElement body, PanelFamilyInfo? family, out string? merged)
    {
        merged = saved;
        if (!body.TryGetProperty("familySettings", out var given))
            return null;
        merged = PanelFamilySettings.Apply(saved, given, family?.Settings ?? [], [.. family?.Models.Select(m => m.Id) ?? []], out var refusals);
        return refusals.Count == 0 ? null : InvalidBody([.. refusals.Select(r => new ValidationErrorBody
        {
            Path = $"/familySettings/{r.Key}",
            Code = "device.setting",
            Message = r.Message,
        })]);
    }

    private static DeviceFamilySettingBody SettingOf(PanelFamilySetting setting) => new()
    {
        Id = setting.Id,
        Name = setting.Name,
        Type = setting.Type,
        Default = setting.Default,
        Values = setting.Type == "enum" ? [.. setting.Values.Select(v => new DeviceFamilySettingValueBody { Id = v.Id, Name = v.Name })] : null,
        Min = setting.Min,
        Max = setting.Max,
        V1Field = setting.V1Field,
    };

    /// <summary>A family as the list gives it: its id, name, driver, state and reason, models, settings and transports.</summary>
    private static DeviceFamilyBody FamilyOf(PanelFamilyInfo family) => new()
    {
        Id = family.Id,
        Name = family.Name,
        Driver = family.Driver,
        State = family.State,
        Reason = family.Reason is { } reason ? new DeviceReasonBody { Code = family.ReasonCode ?? "gaveUp", Message = reason.Length > 4096 ? reason[..4096] : reason } : null,
        // A built-in family's names are the client's; a package's come from its panel.json.
        Models = [.. family.Models.Select(m => new DeviceFamilyModelBody
        {
            Id = m.Id,
            Name = family.Runner == null ? null : m.Name,
            Width = m.Width,
            Height = m.Height,
            V1Model = m.V1Model,
            V1Aliases = m.V1Aliases.Count > 0 ? m.V1Aliases : null,
        })],
        Supports = BeadaPanelSettings,
        Transports = family.Transports,
        Settings = family.Settings.Count > 0 ? [.. family.Settings.Select(SettingOf)] : null,
    };

    /// <summary>A detected device as the list gives it: its family, ids, port, release and its interfaces with their endpoints.</summary>
    private static DeviceDetectedBody DetectedOf(DetectedDevice device) => new()
    {
        Family = device.Family,
        Usb = $"{device.Usb.VendorId:x4}:{device.Usb.ProductId:x4}",
        Port = device.Usb.PortPath,
        BcdDevice = device.BcdDevice,
        Interfaces = [.. device.Interfaces.Select(i => new DeviceDetectedInterfaceBody
        {
            Class = i.Class,
            Subclass = i.SubClass,
            Protocol = i.Protocol,
            Endpoints = [.. i.Endpoints.Select(e => new DeviceDetectedEndpointBody
            {
                Address = e.Address,
                Type = e.Type switch { 0 => "control", 1 => "isochronous", 2 => "bulk", _ => "interrupt" },
                MaxPacketSize = e.MaxPacketSize,
            })],
        })],
    };

    /// <summary>The settings given, over the saved ones; a member left out keeps its value. The body is checked already.</summary>
    private static SavedPanel Merge(SavedPanel saved, DeviceSettingsChange change) => saved with
    {
        Enabled = change.Enabled ?? saved.Enabled,
        ProfileId = change.Removed.Contains(nameof(DeviceSettingsChange.ProfileId)) ? null : change.ProfileId ?? saved.ProfileId,
        Rotation = change.Rotation is { } rotation ? (PanelRotation)rotation : saved.Rotation,
        Brightness = change.Brightness ?? saved.Brightness,
        FrameRate = change.Removed.Contains(nameof(DeviceSettingsChange.FrameRate)) ? null : change.FrameRate ?? saved.FrameRate,
    };

    internal static DeviceEntryBody EntryOf(PanelStatus status, IReadOnlyList<PanelFamilyInfo> families)
    {
        var saved = status.Saved;
        var beadaPanel = saved.Family == SavedPanel.BeadaPanelFamily;
        // A package's family: its model from panel.json, else the size its driver found; its names from the package.
        var family = beadaPanel ? null : families.FirstOrDefault(f => f.Id == saved.Family);
        var declared = family?.Models.FirstOrDefault(m => m.Id == saved.Model);
        var model = status.Info is { } info
            ? new DeviceModelBody { Id = info.Model.Id, Width = info.Width, Height = info.Height }
            : beadaPanel && BeadaPanelModel.All.FirstOrDefault(m => m.Id == saved.Model) is { } known
                ? new DeviceModelBody { Id = known.Id, Width = known.Width, Height = known.Height }
                : declared != null
                    ? new DeviceModelBody { Id = declared.Id, Width = declared.Width, Height = declared.Height }
                    : family != null && status.Identity is { Model.Length: >= 1 and <= 64 } identity && identity.Model == saved.Model
                        ? new DeviceModelBody { Id = identity.Model, Width = identity.Width, Height = identity.Height }
                        : null;
        var usbIds = family?.Runner?.Families.FirstOrDefault(f => f.Id == saved.Family)?.Matches;
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
                FrameRate = saved.FrameRate,
            },
            Supports = SupportsOf(saved.Family, families),
            Stats = status.Fps is { } fps && status.FrameTimeMs is { } frameTime
                ? new DeviceStatsBody { Fps = fps, FrameTimeMs = frameTime, MaxFps = status.MaxFps is >= 1 and <= 60 ? status.MaxFps : null }
                : null,
            Details = new DeviceDetailsBody
            {
                UsbId = beadaPanel ? $"{BeadaPanelProtocol.VendorId:x4}:{BeadaPanelProtocol.ProductId:x4}"
                    : usbIds is [var only] ? $"{only.VendorId:x4}:{only.ProductId:x4}" : null,
                Port = saved.Port,
                Serial = saved.Serial,
                Firmware = status.Info?.Firmware
                    ?? (status.Identity?.Firmware is { } firmware && int.TryParse(firmware, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                        && number is >= 0 and <= 65535 ? number : (int?)null),
            },
            // A stored identity the answer can't carry (edited by hand) is left out of it, and kept.
            V1 = saved.V1 is { } v1 && Fits(v1.DeviceId, 1) && Fits(v1.DeviceLocation, 0)
                ? new V1DeviceIdentity { DeviceId = v1.DeviceId, DeviceLocation = v1.DeviceLocation }
                : null,
            Driver = beadaPanel ? "builtIn" : family?.Driver,
            FamilyName = family?.Runner != null ? family.Name : null,
            ModelName = family?.Runner != null ? declared?.Name : null,
            FamilySettings = saved.FamilySettings is { } set ? PanelFamilySettings.ElementOf(set) : null,
        };
    }

    /// <summary>The most characters (code points) each of a 1.x identity's strings has: <c>v1DeviceIdentity</c>'s <c>maxLength</c>.</summary>
    private const int V1TextLength = 1024;

    private static bool Fits(string text, int least)
    {
        var length = text.EnumerateRunes().Count();
        return length >= least && length <= V1TextLength;
    }

    private static IResult NoDevice(string id) => Problems.NotFound($"No device '{id}'.");

    private static IResult StoreUnwritten() => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict",
        detail: "The engine couldn't write its devices file, so nothing changed; see its log.");

    private static IResult StoreUnheld() => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict",
        detail: "The engine couldn't read its devices file when it started, so it changes no device this run and leaves the file as it is; see its log.");
}
