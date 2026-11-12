using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfoPanel.Panels.Sdk;
using InfoPanel.Persistence;
using Serilog;

namespace InfoPanel.Panels;

/// <summary>
/// A model a panel package declares: its id within its family, its name for people, its size, and the 1.x model it was
/// (null: none); <see cref="V1Aliases"/>, the other names 1.x saved it under (a model 1.x renamed), mapped as
/// <see cref="V1Model"/> is.
/// </summary>
public sealed record PanelPackageModel(string Id, string Name, int Width, int Height, string? V1Model)
{
    /// <summary>The other names 1.x saved this model under, none by default.</summary>
    public IReadOnlyList<string> V1Aliases { get; init; } = [];

    /// <summary>Whether a 1.x panel saved with the model name <paramref name="v1"/> is this model: its <see cref="V1Model"/> or one of its <see cref="V1Aliases"/>.</summary>
    public bool IsV1(string v1) => V1Model == v1 || V1Aliases.Contains(v1, StringComparer.Ordinal);

    // A record's equality would compare the aliases by reference.
    public bool Equals(PanelPackageModel? other) => other is not null && Id == other.Id && Name == other.Name && Width == other.Width
        && Height == other.Height && V1Model == other.V1Model && V1Aliases.SequenceEqual(other.V1Aliases);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Width, Height, V1Model, V1Aliases.Count);
}

/// <summary>A value of an <c>enum</c> family setting: its id (stored) and its name for people.</summary>
public sealed record PanelFamilySettingValue(string Id, string Name);

/// <summary>
/// A setting a family declares for its panels (<c>panel.json</c>'s <c>settings</c>): its <see cref="Id"/> (the key in a
/// panel's <c>familySettings</c>), its <see cref="Name"/> for people, its <see cref="Type"/> (<c>boolean</c>; <c>enum</c>,
/// one of <see cref="Values"/>; <c>integer</c>, <see cref="Min"/> to <see cref="Max"/>; <c>string</c>, at most
/// <see cref="PanelFamilySettings.MaxStringChars"/> characters; <c>model</c>, one of the family's models, the model a panel
/// whose handshake names a model the family doesn't know is tried as), its <see cref="Default"/> (null for <c>model</c>),
/// and <see cref="V1Field"/>, the 1.x element a 1.x panel's value comes from (null: none).
/// </summary>
public sealed record PanelFamilySetting(string Id, string Name, string Type, JsonElement Default)
{
    public IReadOnlyList<PanelFamilySettingValue> Values { get; init; } = [];

    public int? Min { get; init; }

    public int? Max { get; init; }

    public string? V1Field { get; init; }
}

/// <summary>
/// A family a panel package declares in its <c>panel.json</c>: its id (stored with every panel of it, never renamed),
/// its name for people, the USB devices its driver may drive, its models, and the settings its panels take
/// (<see cref="Settings"/>, none by default).
/// </summary>
public sealed record PanelPackageFamily(string Id, string Name, IReadOnlyList<PanelMatch> Matches, IReadOnlyList<PanelPackageModel> Models)
{
    public IReadOnlyList<PanelFamilySetting> Settings { get; init; } = [];

    /// <summary>
    /// Its <c>detect</c> patterns: the ids beyond its matches its vendor's panels may have, a device one names (and no
    /// driver's matches do) listed as detected (<see cref="PanelDetection"/>); none by default.
    /// </summary>
    public IReadOnlyList<UsbIdPattern> Detect { get; init; } = [];

    /// <summary>Whether its matches name the device's ids.</summary>
    public bool Names(ushort vendorId, ushort productId) => Matches.Any(m => m.VendorId == vendorId && m.ProductId == productId);

    /// <summary>
    /// The model a 1.x panel saved with the model name <paramref name="v1"/> (its <c>Model</c> element) is: the one whose
    /// <c>v1Model</c> it is, else the one with it among its <c>v1Aliases</c>; null when none.
    /// </summary>
    public PanelPackageModel? ModelOfV1(string v1) =>
        Models.FirstOrDefault(m => m.V1Model == v1) ?? Models.FirstOrDefault(m => m.V1Aliases.Contains(v1, StringComparer.Ordinal));
}

/// <summary>
/// A panel package: a folder <c>InfoPanel.{Name}</c> holding <c>InfoPanel.{Name}.dll</c> and a <c>panel.json</c>, bundled
/// beside the engine or installed in the data folder's <c>plugins/</c>. Its host runs it in a process of its own.
/// <see cref="ProblemCode"/>: why the engine runs none of it (<c>sdk</c>, a panel SDK it doesn't have; <c>duplicateId</c>,
/// a family id a built-in family or a package earlier in the order has), with <see cref="Problem"/> saying so; null when
/// it is usable. A refused package's families aren't known.
/// </summary>
public sealed record PanelPackage(string Folder, string Directory, string DllPath, bool Bundled, string Sdk, IReadOnlyList<PanelPackageFamily> Families)
{
    public string? ProblemCode { get; init; }

    public string? Problem { get; init; }

    // A record's equality would compare the list by reference.
    public bool Equals(PanelPackage? other) => other is not null && Folder == other.Folder && Directory == other.Directory && ProblemCode == other.ProblemCode;

    public override int GetHashCode() => HashCode.Combine(Folder, Directory, ProblemCode);
}

/// <summary>
/// Finds and reads the panel packages (<c>panel.json</c>, <c>infopanel.panel-package/1</c>): bundled ones first, then the
/// installed ones, each in folder-name order ignoring case. A manifest is read once, at start: a regular file, not a link,
/// of at most <see cref="MaxManifestBytes"/>, JSON within its bounds (<see cref="ReadManifest"/>); one that isn't is
/// skipped and logged. Its <c>sdk</c> (major.minor) must have the engine's major and a minor no higher than its own, else
/// the package is <c>sdk</c>. Family ids are unique across the built-in families, then the bundled packages, then the
/// installed ones: a package declaring an id one earlier has (whatever either's sdk) is refused whole, <c>duplicateId</c>.
/// </summary>
public static partial class PanelPackages
{
    public const string ManifestName = "panel.json";

    /// <summary>The largest <c>panel.json</c> read (64 KiB).</summary>
    public const int MaxManifestBytes = 64 * 1024;

    public const string Format = "infopanel.panel-package/1";

    /// <summary>The panel SDK this engine has: packages built for its major and a minor no higher run.</summary>
    public const int SdkMajor = 1;

    public const int SdkMinor = 0;

    public const int MaxFamilies = 16;
    public const int MaxMatches = 64;
    public const int MaxModels = 512;
    public const int MaxNameChars = 64;

    /// <summary>The most other 1.x names (<c>v1Aliases</c>) a model has.</summary>
    public const int MaxV1Aliases = 16;

    /// <summary>At most this many entries are looked at in the installed folder.</summary>
    public const int ListingEntries = 10_000;

    // InfoPanel.{Name}, its name dot-separated parts (InfoPanel.Panels.Turing), 128 characters at most.
    [GeneratedRegex(@"\A(?=.{1,128}\z)InfoPanel(\.[A-Za-z0-9_-]+)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex FolderPattern();

    [GeneratedRegex(@"\A[a-z][a-z0-9]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FamilyIdPattern();

    [GeneratedRegex(@"\A([0-9a-f]{4}):([0-9a-f]{4})\z", RegexOptions.CultureInvariant)]
    private static partial Regex UsbIdPattern();

    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z", RegexOptions.CultureInvariant)]
    private static partial Regex SdkPattern();

    /// <summary>Whether <paramref name="directory"/> holds a <c>panel.json</c> (anything there: a panel package's folder, never a sensor plugin's).</summary>
    public static bool HasManifest(string directory)
    {
        try
        {
            return RegularFile.StateOf(Path.Combine(directory, ManifestName)) != PathState.Absent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // something is there that can't be looked at: not a sensor plugin's either
        }
    }

    /// <summary>
    /// The panel packages in <paramref name="bundled"/> (beside the engine; may be null or absent), then in
    /// <paramref name="installed"/> (links never followed), each in name order ignoring case; an installed one with a
    /// bundled one's folder name is skipped. Family ids are claimed in that order after <paramref name="builtInFamilies"/>.
    /// </summary>
    public static IReadOnlyList<PanelPackage> Find(string? bundled, string? installed, IReadOnlyCollection<string> builtInFamilies, ILogger logger)
    {
        var packages = new List<PanelPackage>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taken = new Dictionary<string, string?>(StringComparer.Ordinal); // family id → the package that has it (null: built in)
        foreach (var family in builtInFamilies)
            taken[family] = null;

        void Add(DirectoryInfo dir, bool isBundled)
        {
            if (dir.Name.StartsWith('.') || !FolderPattern().IsMatch(dir.Name) || !HasManifest(dir.FullName))
                return;
            if (!names.Add(dir.Name))
            {
                logger.Warning("Skipped the installed panel package {Folder:l}: a bundled package has its name", dir.Name);
                return;
            }
            var dll = Path.Combine(dir.FullName, dir.Name + ".dll");
            try
            {
                if (RegularFile.StateOf(dll) != PathState.Other || new FileInfo(dll).LinkTarget != null)
                {
                    logger.Warning("Skipped the panel package {Folder:l}: it has no {Dll:l}", dir.Name, dir.Name + ".dll");
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Warning("Skipped the panel package {Folder:l}: it can't be read: {Error:l}", dir.Name, ex.Message);
                return;
            }
            if (ReadManifest(dir.FullName, out var error) is not { } manifest)
            {
                logger.Warning("Skipped the panel package {Folder:l}: its {File:l} {Error:l}", dir.Name, ManifestName, error);
                return;
            }

            // Its ids are checked and claimed whatever its sdk: a package the engine can't run still names its families
            // (listed as failed), so no later package, and no package at all for a built-in family, takes them.
            var package = new PanelPackage(dir.Name, dir.FullName, dll, isBundled, manifest.Sdk, manifest.Families);
            if (manifest.Families.FirstOrDefault(f => taken.ContainsKey(f.Id)) is { } clash)
                package = package with
                {
                    ProblemCode = "duplicateId",
                    Problem = taken[clash.Id] is { } earlier
                        ? $"its family {clash.Id} is the panel package {earlier}'s, earlier in the list"
                        : $"its family {clash.Id} is built into the engine",
                };
            else
            {
                foreach (var family in manifest.Families)
                    taken[family.Id] = dir.Name;
                if (SdkProblem(manifest.Sdk) is { } sdk)
                    package = package with { ProblemCode = "sdk", Problem = sdk };
            }
            if (package.Problem != null)
                logger.Warning("The panel package {Folder:l} isn't run: {Problem:l}", dir.Name, package.Problem);
            else
                logger.Information("Panel package {Folder:l}: {Families:l}", dir.Name, string.Join(", ", manifest.Families.Select(f => $"{f.Id} ({f.Models.Count} models)")));
            packages.Add(package);
        }

        if (bundled != null && System.IO.Directory.Exists(bundled))
        {
            try
            {
                foreach (var dir in new DirectoryInfo(bundled).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList())
                    Add(dir, isBundled: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Warning("The bundled plugins folder {Folder:l} can't be listed, so no bundled panel package runs: {Error:l}", bundled, ex.Message);
            }
        }

        if (installed == null)
            return packages;
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. RegularFile.List(installed, new ListingBudget(ListingEntries, "The plugins folder"))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning("The plugins folder {Folder:l} can't be listed, so no installed panel package runs: {Error:l}", installed, ex.Message);
            return packages;
        }
        foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            if (entry is DirectoryInfo { LinkTarget: null } dir)
                Add(dir, isBundled: false);
        return packages;
    }

    /// <summary>Why a package built for <paramref name="sdk"/> doesn't run on this engine, or null when it does.</summary>
    public static string? SdkProblem(string sdk)
    {
        var parts = sdk.Split('.');
        var major = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minor = int.Parse(parts[1], CultureInfo.InvariantCulture);
        return major == SdkMajor && minor <= SdkMinor ? null : $"it is built for panel SDK {sdk}; this engine has {SdkMajor}.{SdkMinor}";
    }

    /// <summary>What a <c>panel.json</c> declares.</summary>
    public sealed record Manifest(string Sdk, IReadOnlyList<PanelPackageFamily> Families);

    /// <summary>Tests: called on this thread between the check that the manifest is a regular file and its read (to put a link there in between).</summary>
    [ThreadStatic]
    internal static Action<string>? ManifestChecked;

    /// <summary>
    /// Reads <c>{directory}/panel.json</c>: a regular file, not a link, of at most <see cref="MaxManifestBytes"/>, UTF-8
    /// JSON (a BOM allowed) of depth 16 at most: <c>format</c> <see cref="Format"/>; <c>sdk</c>, <c>major.minor</c>;
    /// <c>families</c>, 1 to <see cref="MaxFamilies"/>, each with <c>id</c> (a lower-case letter, then letters and digits, 32
    /// at most; unique in the package), <c>name</c> (1 to <see cref="MaxNameChars"/> characters), <c>match</c> (1 to
    /// <see cref="MaxMatches"/>: <c>usb</c> <c>vvvv:pppp</c> in lower-case hex, an optional <c>transport</c>, <c>usb</c>,
    /// <c>serial</c> or <c>hid</c>, and an optional <c>interface</c>, 0 to 255) and <c>models</c> (at most
    /// <see cref="MaxModels"/>, each <c>id</c> and <c>name</c> of 1 to 64 characters, unique ids, <c>width</c> and
    /// <c>height</c> 1 to 16384, an optional <c>v1Model</c> of 1 to 64) and an optional <c>detect</c> (at most
    /// <see cref="PanelDetection.MaxPatterns"/>, each <c>usb</c> a <see cref="UsbIdPattern"/>). Members it doesn't know are ignored. Null, with
    /// <paramref name="error"/> saying why, for anything else. Never throws.
    /// </summary>
    public static Manifest? ReadManifest(string directory, out string? error)
    {
        var path = Path.Combine(directory, ManifestName);
        byte[] bytes;
        try
        {
            if (RegularFile.StateOf(path) != PathState.Other || new FileInfo(path).LinkTarget != null)
            {
                error = "is a link or isn't a regular file";
                return null;
            }
            ManifestChecked?.Invoke(path);
            // One handle, opened without following a link and checked as a regular file itself (O_NOFOLLOW; on Windows
            // FILE_FLAG_OPEN_REPARSE_POINT and its reparse tag), then read: a link put there since the check is refused.
            bytes = ManifestFile.ReadAtMost(path, MaxManifestBytes);
        }
        catch (InvalidDataException)
        {
            error = $"is larger than {MaxManifestBytes} bytes";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"can't be read: {ex.Message}";
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(bytes.AsMemory(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0),
                new JsonDocumentOptions { MaxDepth = 16 });
            return Parse(json.RootElement, out error);
        }
        catch (JsonException ex)
        {
            error = $"isn't JSON: {ex.Message}";
            return null;
        }
        catch (InvalidOperationException)
        {
            // JSON lets a string escape half a surrogate pair (\uD800), which no string can be read from.
            error = "has a string that isn't text (a lone surrogate)";
            return null;
        }
    }

    private static Manifest? Parse(JsonElement root, out string? error)
    {
        error = null;
        if (root.ValueKind != JsonValueKind.Object)
            return Fail("isn't a JSON object", out error);
        if (Text(root, "format") != Format)
            return Fail($"has no format \"{Format}\"", out error);
        if (Text(root, "sdk") is not { } sdk || !SdkPattern().IsMatch(sdk))
            return Fail("has no sdk major.minor", out error);
        if (!root.TryGetProperty("families", out var families) || families.ValueKind != JsonValueKind.Array
            || families.GetArrayLength() is 0 or > MaxFamilies)
            return Fail($"has no families, or more than {MaxFamilies}", out error);

        var parsed = new List<PanelPackageFamily>();
        var index = 0;
        foreach (var family in families.EnumerateArray())
        {
            var at = $"families[{index++}]";
            if (family.ValueKind != JsonValueKind.Object)
                return Fail($"{at} isn't an object", out error);
            if (Text(family, "id") is not { } id || !FamilyIdPattern().IsMatch(id))
                return Fail($"{at}.id isn't a lower-case letter followed by at most 31 letters and digits", out error);
            if (parsed.Any(f => f.Id == id))
                return Fail($"declares the family {id} twice", out error);
            if (Name(family, "name") is not { } name)
                return Fail($"{at}.name isn't 1 to {MaxNameChars} characters", out error);
            if (!family.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Array || match.GetArrayLength() is 0 or > MaxMatches)
                return Fail($"{at}.match has no devices, or more than {MaxMatches}", out error);
            var matches = new List<PanelMatch>();
            foreach (var m in match.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object || Text(m, "usb") is not { } usb || UsbIdPattern().Match(usb) is not { Success: true } ids)
                    return Fail($"{at}.match has a device that isn't {{\"usb\": \"vvvv:pppp\"}} in lower-case hexadecimal", out error);
                var transport = PanelTransport.Usb;
                if (m.TryGetProperty("transport", out var t))
                {
                    if (t.ValueKind != JsonValueKind.String || TransportOf(t.GetString()!) is not { } known)
                        return Fail($"{at}.match has a transport that isn't usb, serial or hid", out error);
                    transport = known;
                }
                int? usbInterface = null;
                if (m.TryGetProperty("interface", out var i))
                {
                    if (i.ValueKind != JsonValueKind.Number || !i.TryGetInt32(out var number) || number is < 0 or > 255)
                        return Fail($"{at}.match has an interface that isn't 0 to 255", out error);
                    usbInterface = number;
                }
                matches.Add(new PanelMatch(ushort.Parse(ids.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    ushort.Parse(ids.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), usbInterface, transport));
            }
            var models = new List<PanelPackageModel>();
            if (family.TryGetProperty("models", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxModels)
                    return Fail($"{at}.models isn't a list of at most {MaxModels}", out error);
                foreach (var model in list.EnumerateArray())
                {
                    if (model.ValueKind != JsonValueKind.Object || Name(model, "id") is not { } modelId || Name(model, "name") is not { } modelName
                        || Size(model, "width") is not { } width || Size(model, "height") is not { } height)
                        return Fail($"{at}.models has a model without an id and a name of 1 to {MaxNameChars} characters, and a width and height of 1 to 16384", out error);
                    string? v1 = null;
                    if (model.TryGetProperty("v1Model", out var v1Model) && v1Model.ValueKind != JsonValueKind.Null && (v1 = Name(model, "v1Model")) == null)
                        return Fail($"{at}.models has a v1Model that isn't 1 to {MaxNameChars} characters", out error);
                    if (models.Any(x => x.Id == modelId))
                        return Fail($"{at}.models has the model {modelId} twice", out error);
                    var aliases = new List<string>();
                    if (model.TryGetProperty("v1Aliases", out var v1Aliases))
                    {
                        if (v1Aliases.ValueKind != JsonValueKind.Array || v1Aliases.GetArrayLength() > MaxV1Aliases)
                            return Fail($"{at}.models has a v1Aliases that isn't a list of at most {MaxV1Aliases} names", out error);
                        foreach (var alias in v1Aliases.EnumerateArray())
                        {
                            if (alias.ValueKind != JsonValueKind.String || alias.GetString() is not { Length: > 0 } text || text.EnumerateRunes().Count() > MaxNameChars)
                                return Fail($"{at}.models has a v1Aliases name that isn't 1 to {MaxNameChars} characters", out error);
                            aliases.Add(text);
                        }
                    }
                    models.Add(new PanelPackageModel(modelId, modelName, width, height, v1) { V1Aliases = aliases });
                }
            }
            IReadOnlyList<PanelFamilySetting> settings = [];
            if (family.TryGetProperty("settings", out var declared))
            {
                if (PanelFamilySettings.Parse(declared, at, out error) is not { } read)
                    return null;
                settings = read;
            }
            var detect = new List<UsbIdPattern>();
            if (family.TryGetProperty("detect", out var patterns))
            {
                if (patterns.ValueKind != JsonValueKind.Array || patterns.GetArrayLength() > PanelDetection.MaxPatterns)
                    return Fail($"{at}.detect isn't a list of at most {PanelDetection.MaxPatterns} patterns", out error);
                foreach (var p in patterns.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object || Text(p, "usb") is not { } text || InfoPanel.Panels.UsbIdPattern.Parse(text) is not { } pattern)
                        return Fail($"{at}.detect has a pattern that isn't {{\"usb\": \"vvvv:pppp\"}} in lower-case hexadecimal, with ? for any digit of the product or * for any product", out error);
                    detect.Add(pattern);
                }
            }
            parsed.Add(new PanelPackageFamily(id, name, matches, models) { Settings = settings, Detect = detect });
        }
        return new Manifest(sdk, parsed);
    }

    private static Manifest? Fail(string why, out string? error)
    {
        error = why;
        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Name(JsonElement element, string name) =>
        Text(element, name) is { } text && text.Length > 0 && text.EnumerateRunes().Count() <= MaxNameChars ? text : null;

    private static int? Size(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var size) && size is >= 1 and <= 16384 ? size : null;

    /// <summary>A transport as <c>panel.json</c> and the API name it, or null.</summary>
    public static PanelTransport? TransportOf(string name) => name switch
    {
        "usb" => PanelTransport.Usb,
        "serial" => PanelTransport.Serial,
        "hid" => PanelTransport.Hid,
        _ => null,
    };

    /// <summary>A transport's name in <c>panel.json</c> and the API.</summary>
    public static string NameOf(PanelTransport transport) => transport switch
    {
        PanelTransport.Serial => "serial",
        PanelTransport.Hid => "hid",
        _ => "usb",
    };
}
