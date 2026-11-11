using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InfoPanel.Panels.Sdk;

/// <summary>
/// A model a <c>panel.json</c> declares: its id in its family, its name for people, its size in pixels in its own
/// orientation, the 1.x model it was (null: none), and its whole JSON object, so a driver reads the members only it
/// knows (a detection code, say) from the one place its models are written.
/// </summary>
public sealed record PanelManifestModel(string Id, string Name, int Width, int Height, string? V1Model, JsonElement Json);

/// <summary>
/// A family a <c>panel.json</c> declares: its id (stored with every panel of it, never renamed), its name for people,
/// the devices its driver may drive (a driver's <see cref="IPanelDriver.Matches"/>), its models, and its whole JSON object.
/// </summary>
public sealed record PanelManifestFamily(string Id, string Name, IReadOnlyList<PanelMatch> Matches, IReadOnlyList<PanelManifestModel> Models, JsonElement Json)
{
    /// <summary>The model with the id <paramref name="id"/>, or null.</summary>
    public PanelManifestModel? Model(string id) => Models.FirstOrDefault(m => m.Id == id);
}

/// <summary>
/// A panel package's <c>panel.json</c> (<c>infopanel.panel-package/1</c>) as its own driver reads it: the one source of
/// its families, matches and models, so a driver builds its <see cref="IPanelDriver.Matches"/> and its model table from
/// it rather than from a second table in code. It holds the file to the rules the engine reads it by (the format, an
/// <c>sdk</c> of <c>major.minor</c>, 1 to 16 families, each with an id of a lower-case letter then at most 31 letters and
/// digits, a name of 1 to 64 characters, 1 to 64 matches of <c>usb</c> <c>vvvv:pppp</c> in lower-case hexadecimal with an
/// optional <c>transport</c> (<c>usb</c>, <c>serial</c> or <c>hid</c>) and <c>interface</c> (0 to 255), and at most 512
/// models, each with a unique id and a name of 1 to 64 characters, a width and height of 1 to 16384, and an optional
/// <c>v1Model</c>), so a manifest the engine would refuse fails the driver's own tests first. Members it doesn't know are
/// kept in each family's and model's <see cref="PanelManifestFamily.Json"/>.
/// </summary>
public sealed partial class PanelManifest
{
    /// <summary>The manifest's name, beside the driver's assembly.</summary>
    public const string FileName = "panel.json";

    /// <summary>The format it declares.</summary>
    public const string Format = "infopanel.panel-package/1";

    /// <summary>The largest manifest read (64 KiB).</summary>
    public const int MaxBytes = 64 * 1024;

    private const int MaxFamilies = 16;
    private const int MaxMatches = 64;
    private const int MaxModels = 512;
    private const int MaxNameChars = 64;

    private PanelManifest(string sdk, IReadOnlyList<PanelManifestFamily> families)
    {
        Sdk = sdk;
        Families = families;
    }

    /// <summary>The panel SDK the package is built for, <c>major.minor</c>.</summary>
    public string Sdk { get; }

    /// <summary>Its families, in the file's order.</summary>
    public IReadOnlyList<PanelManifestFamily> Families { get; }

    /// <summary>The family with the id <paramref name="id"/>; <see cref="KeyNotFoundException"/> when the manifest doesn't declare it.</summary>
    public PanelManifestFamily Family(string id) =>
        Families.FirstOrDefault(f => f.Id == id) ?? throw new KeyNotFoundException($"{FileName} declares no family {id}");

    /// <summary>
    /// Reads the <c>panel.json</c> beside <paramref name="driver"/> (the driver's own assembly:
    /// <c>typeof(MyDriver).Assembly</c>); <see cref="InvalidDataException"/>, naming the file, when it can't be read or
    /// isn't a manifest the engine would read.
    /// </summary>
    public static PanelManifest Read(Assembly driver)
    {
        var directory = string.IsNullOrEmpty(driver.Location) ? null : Path.GetDirectoryName(driver.Location);
        if (directory == null)
            throw new InvalidDataException($"{driver.GetName().Name} has no location on disk, so it has no {FileName} beside it");
        return Read(Path.Combine(directory, FileName));
    }

    /// <summary>
    /// Reads the manifest at <paramref name="path"/> as the engine reads it: a regular file only, never a link to one
    /// (the engine refuses a link), opened without waiting (a FIFO or device is refused, never waited on) and read at
    /// most <see cref="MaxBytes"/> + 1 bytes; <see cref="InvalidDataException"/>, naming the file, when it can't be read
    /// or isn't a manifest.
    /// </summary>
    public static PanelManifest Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = ManifestFile.ReadAtMost(path, MaxBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"{path} can't be read: {ex.Message}", ex);
        }
        try
        {
            return Parse(bytes);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reads a manifest from its UTF-8 bytes (a BOM allowed); <see cref="InvalidDataException"/> saying why when it isn't
    /// one. The engine's reader (<c>PanelPackages</c>) applies the same rules; once the engine side lands, both use this
    /// one parser.
    /// </summary>
    public static PanelManifest Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxBytes)
            throw new InvalidDataException($"it is larger than {MaxBytes} bytes");
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            utf8 = utf8[3..];
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            root = document.RootElement.Clone(); // its own copy, which outlives the document
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"it isn't JSON: {ex.Message}", ex);
        }
        try
        {
            return Parse(root);
        }
        catch (InvalidOperationException ex)
        {
            // JSON lets a string escape half a surrogate pair (\uD800), which no string can be read from.
            throw new InvalidDataException("it has a string that isn't text (a lone surrogate)", ex);
        }
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FamilyIdPattern();

    [GeneratedRegex(@"\A([0-9a-f]{4}):([0-9a-f]{4})\z", RegexOptions.CultureInvariant)]
    private static partial Regex UsbIdPattern();

    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z", RegexOptions.CultureInvariant)]
    private static partial Regex SdkPattern();

    private static PanelManifest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Invalid("it isn't a JSON object");
        if (Text(root, "format") != Format)
            throw Invalid($"it has no format \"{Format}\"");
        if (Text(root, "sdk") is not { } sdk || !SdkPattern().IsMatch(sdk))
            throw Invalid("it has no sdk major.minor");
        if (!root.TryGetProperty("families", out var families) || families.ValueKind != JsonValueKind.Array
            || families.GetArrayLength() is 0 or > MaxFamilies)
            throw Invalid($"it has no families, or more than {MaxFamilies}");

        var parsed = new List<PanelManifestFamily>();
        var index = 0;
        foreach (var family in families.EnumerateArray())
        {
            var at = $"families[{index++}]";
            if (family.ValueKind != JsonValueKind.Object)
                throw Invalid($"{at} isn't an object");
            if (Text(family, "id") is not { } id || !FamilyIdPattern().IsMatch(id))
                throw Invalid($"{at}.id isn't a lower-case letter followed by at most 31 letters and digits");
            if (parsed.Any(f => f.Id == id))
                throw Invalid($"it declares the family {id} twice");
            if (Name(family, "name") is not { } name)
                throw Invalid($"{at}.name isn't 1 to {MaxNameChars} characters");
            if (!family.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Array || match.GetArrayLength() is 0 or > MaxMatches)
                throw Invalid($"{at}.match has no devices, or more than {MaxMatches}");
            var matches = new List<PanelMatch>();
            foreach (var m in match.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object || Text(m, "usb") is not { } usb || UsbIdPattern().Match(usb) is not { Success: true } ids)
                    throw Invalid($"{at}.match has a device that isn't {{\"usb\": \"vvvv:pppp\"}} in lower-case hexadecimal");
                var transport = PanelTransport.Usb;
                if (m.TryGetProperty("transport", out var t))
                {
                    if (t.ValueKind != JsonValueKind.String || TransportOf(t.GetString()!) is not { } known)
                        throw Invalid($"{at}.match has a transport that isn't usb, serial or hid");
                    transport = known;
                }
                int? usbInterface = null;
                if (m.TryGetProperty("interface", out var i))
                {
                    if (i.ValueKind != JsonValueKind.Number || !i.TryGetInt32(out var number) || number is < 0 or > 255)
                        throw Invalid($"{at}.match has an interface that isn't 0 to 255");
                    usbInterface = number;
                }
                matches.Add(new PanelMatch(ushort.Parse(ids.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    ushort.Parse(ids.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), usbInterface, transport));
            }
            var models = new List<PanelManifestModel>();
            if (family.TryGetProperty("models", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxModels)
                    throw Invalid($"{at}.models isn't a list of at most {MaxModels}");
                foreach (var model in list.EnumerateArray())
                {
                    if (model.ValueKind != JsonValueKind.Object || Name(model, "id") is not { } modelId || Name(model, "name") is not { } modelName
                        || Size(model, "width") is not { } width || Size(model, "height") is not { } height)
                        throw Invalid($"{at}.models has a model without an id and a name of 1 to {MaxNameChars} characters, and a width and height of 1 to 16384");
                    string? v1 = null;
                    if (model.TryGetProperty("v1Model", out var v1Model) && v1Model.ValueKind != JsonValueKind.Null && (v1 = Name(model, "v1Model")) == null)
                        throw Invalid($"{at}.models has a v1Model that isn't 1 to {MaxNameChars} characters");
                    if (models.Any(x => x.Id == modelId))
                        throw Invalid($"{at}.models has the model {modelId} twice");
                    models.Add(new PanelManifestModel(modelId, modelName, width, height, v1, model));
                }
            }
            parsed.Add(new PanelManifestFamily(id, name, matches, models, family));
        }
        return new PanelManifest(sdk, parsed);
    }

    private static InvalidDataException Invalid(string why) => new(why);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Name(JsonElement element, string name) =>
        Text(element, name) is { } text && text.Length > 0 && text.EnumerateRunes().Count() <= MaxNameChars ? text : null;

    private static int? Size(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var size) && size is >= 1 and <= 16384 ? size : null;

    private static PanelTransport? TransportOf(string name) => name switch
    {
        "usb" => PanelTransport.Usb,
        "serial" => PanelTransport.Serial,
        "hid" => PanelTransport.Hid,
        _ => null,
    };
}
