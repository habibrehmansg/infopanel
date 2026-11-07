using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using Serilog;

namespace InfoPanel.Panels;

/// <summary>
/// A panel the engine has identified once, as <c>devices.json</c> keeps it: <see cref="Id"/> (a lower-case UUID the
/// engine made), the <see cref="Family"/> (<c>beadapanel</c>), the <see cref="Model"/> id (<c>8</c>), the panel's
/// <see cref="Serial"/> and the USB <see cref="Port"/> path it was last seen at, and the settings: <see cref="Enabled"/>,
/// <see cref="ProfileId"/>, <see cref="Rotation"/>, <see cref="Brightness"/> (0–100), <see cref="Encoding"/>.
/// <see cref="Kept"/> holds the members this engine doesn't know, written back as read.
/// </summary>
public sealed record SavedPanel(
    string Id,
    string Family,
    string? Model,
    string? Serial,
    string? Port,
    bool Enabled = false,
    string? ProfileId = null,
    PanelRotation Rotation = PanelRotation.None,
    int Brightness = 100,
    PanelEncoding Encoding = PanelEncoding.Auto)
{
    public const string BeadaPanelFamily = "beadapanel";

    public IReadOnlyList<JsonMemberText> Kept { get; init; } = [];

    /// <summary>The encoding in effect; setting it (<c>with { Encoding = … }</c>) replaces an <see cref="UnknownEncoding"/>.</summary>
    public PanelEncoding Encoding
    {
        get;
        init
        {
            field = value;
            UnknownEncoding = null;
        }
    } = Encoding;

    /// <summary>
    /// The stored <c>encoding</c> as written (its JSON value) when it isn't one this engine knows, such as a later engine's:
    /// it reads as <see cref="PanelEncoding.Auto"/> and is written back unchanged until the encoding is set.
    /// </summary>
    public byte[]? UnknownEncoding { get; init; }
}

/// <summary>
/// <c>devices.json</c> in the data folder (docs/2.0/data-folder.md): <c>{"format": "infopanel.devices/1", "devices":
/// [{"id", "family", "model", "serial", "port", "enabled", "profileId", "rotation", "brightness", "encoding"}]}</c>, in the order the
/// panels were first seen. Read once at start through <see cref="EngineJson"/>, members it doesn't know (in the file and
/// in each device) kept as written; written through <see cref="AtomicFile"/>. A file it can't hold whole (over
/// <see cref="MaxBytes"/>, nested deeper than it reads, a link, not a regular file, not JSON, another format or major
/// version, or a member it knows that is missing or of the wrong kind) is never rewritten, and panels are off for the run
/// (<see cref="Problem"/>), as <c>plugin-state.json</c>. The one exception is <c>encoding</c>: any value but
/// <c>"auto"</c>, <c>"jpeg"</c> or <c>"raw"</c> reads as <c>auto</c>, so a value a later engine writes keeps panels running, and
/// is written back as it was until the encoding is set (<see cref="SavedPanel.UnknownEncoding"/>).
/// </summary>
public sealed partial class PanelStore
{
    public const string FileName = DataPaths.DevicesFileName;

    public const string Format = "infopanel.devices/1";

    /// <summary>The most the file may hold, its kept members included (1 MiB).</summary>
    public const int MaxBytes = 1024 * 1024;

    private readonly string? _path;
    private readonly List<JsonMemberText> _kept;
    private List<SavedPanel> _devices;

    private PanelStore(string? path, List<SavedPanel> devices, List<JsonMemberText> kept, string? problem)
    {
        _path = path;
        _devices = devices;
        _kept = kept;
        Problem = problem;
    }

    /// <summary>Why the engine can't hold the file (it is then never rewritten, and panels are off), or null.</summary>
    public string? Problem { get; }

    /// <summary>The saved panels, in the order first seen.</summary>
    public IReadOnlyList<SavedPanel> Devices => _devices;

    /// <summary>How the file is written: <see cref="AtomicFile"/> (tests give a storage fault).</summary>
    public Action<string, byte[]> Writer { get; set; } = (path, bytes) => AtomicFile.Write(path, bytes);

    [GeneratedRegex(@"\Ainfopanel\.devices/([0-9]{1,4})\z", RegexOptions.CultureInvariant)]
    private static partial Regex FormatString();

    [GeneratedRegex(@"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>A store held in memory only (no file): tests, and engines without a data folder.</summary>
    public static PanelStore InMemory(IEnumerable<SavedPanel>? devices = null) => new(null, [.. devices ?? []], [], null);

    /// <summary>
    /// Reads the file at <paramref name="path"/> (absent: no panels saved yet). Never throws: a file it can't hold is
    /// logged once, and panels are off for the run.
    /// </summary>
    public static PanelStore Read(string path, ILogger logger)
    {
        byte[] bytes;
        try
        {
            var state = RegularFile.StateOf(path);
            if (state == PathState.Absent)
                return new PanelStore(path, [], [], null);
            if (new FileInfo(path).LinkTarget != null)
                return Unheld(path, "it is a link, which the engine doesn't read or write through", logger);
            if (state != PathState.Other)
                return Unheld(path, "it isn't a regular file", logger);
            bytes = RegularFile.ReadAtMost(path, MaxBytes);
        }
        catch (InvalidDataException)
        {
            return Unheld(path, $"it is larger than {MaxBytes} bytes", logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unheld(path, ex.Message, logger);
        }

        if (EngineJson.IsTooDeep(bytes))
            return Unheld(path, $"it nests deeper than {EngineJson.MaxDepth} levels", logger);

        List<JsonMemberText> members;
        try
        {
            members = EngineJson.Members(bytes);
        }
        catch (JsonException)
        {
            return Unheld(path, "it isn't a JSON object", logger);
        }

        string? format = null;
        List<SavedPanel>? devices = null;
        var kept = new List<JsonMemberText>();
        foreach (var member in members)
        {
            switch (member.Name)
            {
                case "format" when format == null:
                    if (!Text(Parse(member.Value), nullable: false, out format))
                        return Unheld(path, "its format isn't a string", logger);
                    break;
                case "devices" when devices == null:
                    devices = ReadDevices(member.Value, out var why);
                    if (devices == null)
                        return Unheld(path, why!, logger);
                    break;
                case "format" or "devices":
                    return Unheld(path, $"it has {member.Name} twice", logger);
                default:
                    kept.Add(member);
                    break;
            }
        }

        if (format == null)
            return Unheld(path, "it has no format", logger);
        var match = FormatString().Match(format);
        if (!match.Success)
            return Unheld(path, "its format isn't infopanel.devices", logger);
        if (match.Groups[1].Value != "1")
            return Unheld(path, $"it is another major version ({format})", logger);

        return new PanelStore(path, devices ?? [], kept, null);
    }

    private static PanelStore Unheld(string path, string why, ILogger logger)
    {
        logger.Error("The panels file {Path} can't be read ({Why}): USB panels are off this run, and the file is left as it is", path, why);
        return new PanelStore(path, [], [], why);
    }

    private static JsonElement? Parse(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, EngineJson.DocumentOptions);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<SavedPanel>? ReadDevices(byte[] json, out string? why)
    {
        List<byte[]> elements;
        try
        {
            elements = EngineJson.Elements(json);
        }
        catch (JsonException)
        {
            why = "its devices isn't a list";
            return null;
        }

        var devices = new List<SavedPanel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            var device = ReadDevice(element, out why);
            if (device == null)
                return null;
            if (!ids.Add(device.Id))
            {
                why = $"it has device {device.Id} twice";
                return null;
            }
            devices.Add(device);
        }

        why = null;
        return devices;
    }

    private static SavedPanel? ReadDevice(byte[] json, out string? why)
    {
        List<JsonMemberText> members;
        try
        {
            members = EngineJson.Members(json);
        }
        catch (JsonException)
        {
            why = "a device isn't a JSON object";
            return null;
        }

        string? id = null, family = null, model = null, serial = null, port = null, profileId = null;
        var enabled = false;
        var rotation = PanelRotation.None;
        var brightness = 100;
        var encoding = PanelEncoding.Auto;
        byte[]? unknownEncoding = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<JsonMemberText>();
        foreach (var member in members)
        {
            if (member.Name is not ("id" or "family" or "model" or "serial" or "port" or "enabled" or "profileId" or "rotation" or "brightness" or "encoding"))
            {
                kept.Add(member);
                continue;
            }
            if (!seen.Add(member.Name))
            {
                why = $"a device has {member.Name} twice";
                return null;
            }

            var value = Parse(member.Value);
            var ok = member.Name switch
            {
                "id" => Text(value, nullable: false, out id),
                "family" => Text(value, nullable: false, out family),
                "model" => Text(value, nullable: true, out model),
                "serial" => Text(value, nullable: true, out serial),
                "port" => Text(value, nullable: true, out port),
                "profileId" => Text(value, nullable: true, out profileId),
                "enabled" => Bool(value, out enabled),
                "rotation" => Rotation(value, out rotation),
                "brightness" => Brightness(value, out brightness),
                "encoding" => Encoding(value, member.Value, out encoding, out unknownEncoding),
                _ => false,
            };
            if (!ok)
            {
                why = $"a device's {member.Name} isn't valid";
                return null;
            }
        }

        if (id == null || !IdPattern().IsMatch(id))
        {
            why = "a device has no id, or one that isn't a lower-case UUID";
            return null;
        }
        if (string.IsNullOrEmpty(family))
        {
            why = $"device {id} has no family";
            return null;
        }

        why = null;
        return new SavedPanel(id, family, model, serial, port, enabled, profileId, rotation, brightness, encoding) { Kept = kept, UnknownEncoding = unknownEncoding };
    }

    /// <summary>A JSON string (or null, when <paramref name="nullable"/>); a string that isn't valid text (a lone surrogate) isn't one.</summary>
    private static bool Text(JsonElement? value, bool nullable, out string? text)
    {
        text = null;
        if (value is { ValueKind: JsonValueKind.String } s)
        {
            try
            {
                text = s.GetString();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false; // it can't be decoded as UTF-16
            }
        }
        return nullable && value is { ValueKind: JsonValueKind.Null };
    }

    private static bool Bool(JsonElement? value, out bool flag)
    {
        flag = value is { ValueKind: JsonValueKind.True };
        return value is { ValueKind: JsonValueKind.True or JsonValueKind.False };
    }

    private static bool Rotation(JsonElement? value, out PanelRotation rotation)
    {
        rotation = PanelRotation.None;
        if (value is not { ValueKind: JsonValueKind.Number } n || !n.TryGetInt32(out var degrees) || degrees is not (0 or 90 or 180 or 270))
            return false;
        rotation = (PanelRotation)degrees;
        return true;
    }

    private static bool Brightness(JsonElement? value, out int brightness)
    {
        brightness = 100;
        if (value is not { ValueKind: JsonValueKind.Number } n || !n.TryGetInt32(out var b) || b is < 0 or > 100)
            return false;
        brightness = b;
        return true;
    }

    /// <summary>
    /// Always read: <c>"auto"</c>, <c>"jpeg"</c> and <c>"raw"</c> as written; anything else (another string, another kind)
    /// as auto, with its JSON value (<paramref name="written"/>) kept in <paramref name="unknown"/> to be written back.
    /// </summary>
    private static bool Encoding(JsonElement? value, byte[] written, out PanelEncoding encoding, out byte[]? unknown)
    {
        unknown = null;
        if (value is { ValueKind: JsonValueKind.String } s && (s.ValueEquals("auto") || s.ValueEquals("jpeg") || s.ValueEquals("raw")))
        {
            encoding = s.ValueEquals("jpeg") ? PanelEncoding.Jpeg : s.ValueEquals("raw") ? PanelEncoding.Raw : PanelEncoding.Auto;
            return true;
        }
        encoding = PanelEncoding.Auto;
        unknown = written;
        return true;
    }

    private static string EncodingText(PanelEncoding encoding) => encoding switch
    {
        PanelEncoding.Jpeg => "jpeg",
        PanelEncoding.Raw => "raw",
        _ => "auto",
    };

    /// <summary>
    /// Replaces the saved panels with <paramref name="devices"/> and writes the file. The change counts only once written:
    /// a write that fails throws and changes nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The engine can't hold the file (<see cref="Problem"/>), or the file would be over <see cref="MaxBytes"/>.</exception>
    /// <exception cref="IOException">The write failed; nothing changed.</exception>
    /// <exception cref="UnauthorizedAccessException">The write was refused; nothing changed.</exception>
    public void Save(IReadOnlyList<SavedPanel> devices)
    {
        if (Problem != null)
            throw new InvalidOperationException($"The panels file can't be read ({Problem}), so it isn't written this run.");
        var next = devices.ToList();
        if (_path != null)
        {
            var bytes = Compose(next);
            if (bytes.Length > MaxBytes)
                throw new InvalidOperationException($"The panels file would be larger than {MaxBytes} bytes.");
            Writer(_path, bytes);
        }
        _devices = next;
    }

    /// <summary>The file: the engine's own members, then the members it kept, each as it was read.</summary>
    private byte[] Compose(List<SavedPanel> devices)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteStartArray("devices");
            foreach (var device in devices)
                writer.WriteRawValue(DeviceJson(device), skipInputValidation: true);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return EngineJson.Append(buffer.ToArray(), _kept);
    }

    private static byte[] DeviceJson(SavedPanel device)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", device.Id);
            writer.WriteString("family", device.Family);
            WriteOrNull(writer, "model", device.Model);
            WriteOrNull(writer, "serial", device.Serial);
            WriteOrNull(writer, "port", device.Port);
            writer.WriteBoolean("enabled", device.Enabled);
            WriteOrNull(writer, "profileId", device.ProfileId);
            writer.WriteNumber("rotation", (int)device.Rotation);
            writer.WriteNumber("brightness", device.Brightness);
            if (device.UnknownEncoding is { } unknown)
            {
                writer.WritePropertyName("encoding");
                writer.WriteRawValue(unknown, skipInputValidation: true); // as it was read: a JSON value
            }
            else
                writer.WriteString("encoding", EncodingText(device.Encoding));
            writer.WriteEndObject();
        }

        return EngineJson.Append(buffer.ToArray(), device.Kept);
    }

    private static void WriteOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value == null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }
}
