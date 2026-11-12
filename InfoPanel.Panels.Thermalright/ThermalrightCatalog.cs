using System.Globalization;
using System.Text.Json;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Thermalright;

/// <summary>
/// The family's matches and models, read from <c>panel.json</c> (the package's only table of them), and InfoPanel 1.4's
/// lookups over them. Models keep the file's order, which is 1.4's: a lookup takes the first model that fits.
/// </summary>
public sealed class ThermalrightCatalog
{
    public const string FamilyId = "thermalright";

    private const string ResourceName = "InfoPanel.Panels.Thermalright.panel.json";

    private static readonly Lazy<ThermalrightCatalog> Embedded = new(() =>
    {
        using var stream = typeof(ThermalrightCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} isn't embedded");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Parse(copy.ToArray());
    });

    private ThermalrightCatalog(IReadOnlyList<PanelMatch> matches, IReadOnlyList<ThermalrightModel> models, IReadOnlyList<HidPmSize> hidPmSizes)
    {
        Matches = matches;
        Models = models;
        HidPmSizes = hidPmSizes;
    }

    /// <summary>The USB ids the family's driver is offered, with their transport and interface.</summary>
    public IReadOnlyList<PanelMatch> Matches { get; }

    /// <summary>Every model, in <c>panel.json</c>'s order.</summary>
    public IReadOnlyList<ThermalrightModel> Models { get; }

    /// <summary>The sizes a HID init reply's PM byte names (used when no model has the PM).</summary>
    public IReadOnlyList<HidPmSize> HidPmSizes { get; }

    /// <summary>The package's own <c>panel.json</c>, embedded in its assembly.</summary>
    public static ThermalrightCatalog Default => Embedded.Value;

    /// <summary>Reads the <c>thermalright</c> family of a <c>panel.json</c>; <see cref="FormatException"/> says what is wrong.</summary>
    public static ThermalrightCatalog Parse(ReadOnlySpan<byte> panelJson)
    {
        try
        {
            // First as the SDK reads a package's manifest (the engine's rules): a file it would refuse fails here too.
            _ = PanelManifest.Parse(panelJson).Family(FamilyId);
            using var json = JsonDocument.Parse(panelJson.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var family = json.RootElement.GetProperty("families").EnumerateArray()
                .FirstOrDefault(f => f.TryGetProperty("id", out var id) && id.GetString() == FamilyId);
            if (family.ValueKind != JsonValueKind.Object)
                throw new FormatException($"panel.json has no family {FamilyId}");

            var matches = family.GetProperty("match").EnumerateArray().Select(ReadMatch).ToList();
            var models = family.GetProperty("models").EnumerateArray().Select(ReadModel).ToList();
            var sizes = family.TryGetProperty("hidPmSizes", out var list)
                ? list.EnumerateArray().Select(s => new HidPmSize(s.GetProperty("pm").GetByte(), s.GetProperty("width").GetInt32(),
                    s.GetProperty("height").GetInt32(), s.GetProperty("size").GetString()!)).ToList()
                : [];
            if (models.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != models.Count)
                throw new FormatException("panel.json names a model id twice");
            return new ThermalrightCatalog(matches, models, sizes);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
        {
            throw new FormatException($"panel.json isn't the family's manifest: {ex.Message}", ex);
        }
    }

    public ThermalrightModel? ById(string id) => Models.FirstOrDefault(m => m.Id == id);

    /// <summary>Every model with these USB ids, in <c>panel.json</c>'s order (a variant told apart only by its reply has none).</summary>
    public IReadOnlyList<ThermalrightModel> WithIds(ushort vendorId, ushort productId) =>
        [.. Models.Where(m => m.VendorId == vendorId && m.ProductId == productId)];

    /// <summary>The model a 1.x device's <c>Model</c> names, by its name or an older spelling of it.</summary>
    public ThermalrightModel? ByV1Model(string v1Name) =>
        Models.FirstOrDefault(m => m.V1Model == v1Name || m.V1Aliases.Contains(v1Name));

    /// <summary>The one model with these USB ids, or null when none or several have them (1.4's <c>GetModelByVidPid</c>).</summary>
    public ThermalrightModel? ByVidPid(ushort vendorId, ushort productId)
    {
        ThermalrightModel? match = null;
        foreach (var model in Models)
        {
            if ((model.VendorId ?? 0) == vendorId && (model.ProductId ?? 0) == productId)
            {
                if (match != null)
                    return null;
                match = model;
            }
        }
        return match;
    }

    /// <summary>The size a HID PM byte names (1.4's <c>GetResolutionFromPM</c>).</summary>
    public HidPmSize? HidSize(byte pm) => HidPmSizes.FirstOrDefault(s => s.Pm == pm);

    /// <summary>
    /// The model of a HID PM byte: with a SUB, the first model with both; else the first with the PM and no SUB of its
    /// own (1.4's <c>GetModelByPM</c>).
    /// </summary>
    public ThermalrightModel? ByHidPm(byte pm, byte? sub = null)
    {
        if (sub.HasValue)
        {
            foreach (var model in Models)
                if (model.Pm == pm && model.Sub == sub.Value)
                    return model;
        }
        foreach (var model in Models)
            if (model.Pm == pm && model.Sub == null)
                return model;
        return null;
    }

    /// <summary>The first model with this identifier string and SUB byte (1.4's <c>GetModelByIdentifierAndSub</c>).</summary>
    public ThermalrightModel? ByIdentifierAndSub(string identifier, byte sub) =>
        Models.FirstOrDefault(m => m.Identifier == identifier && m.Sub == sub);

    /// <summary>With a SUB, <see cref="ByIdentifierAndSub"/> first; then the first model with the identifier (1.4's <c>GetModelByIdentifier</c>).</summary>
    public ThermalrightModel? ByIdentifier(string identifier, byte? sub = null)
    {
        if (sub.HasValue && ByIdentifierAndSub(identifier, sub.Value) is { } strict)
            return strict;
        return Models.FirstOrDefault(m => m.Identifier == identifier);
    }

    /// <summary>
    /// The ChiZhu model of a PM and SUB byte (reply bytes 24 and 28; 1.4's <c>GetModelByChiZhuPM</c>): a rule with the
    /// exact SUB first, then one whose SUB range holds it, then one for any SUB.
    /// </summary>
    public ThermalrightModel? ByChiZhuPm(byte pm, byte sub)
    {
        ThermalrightModel? range = null, any = null;
        foreach (var model in Models)
        {
            foreach (var rule in model.ChiZhu)
            {
                if (rule.Pm != pm)
                    continue;
                if (rule.Sub is { } exact)
                {
                    if (exact == sub)
                        return model;
                }
                else if (rule.SubFrom is { } from)
                {
                    if (sub >= from)
                        range ??= model;
                }
                else
                    any ??= model;
            }
        }
        return range ?? any;
    }

    private static PanelMatch ReadMatch(JsonElement m)
    {
        var usb = m.GetProperty("usb").GetString()!.Split(':');
        var transport = (m.TryGetProperty("transport", out var t) ? t.GetString() : "usb") switch
        {
            "usb" => PanelTransport.Usb,
            "hid" => PanelTransport.Hid,
            "serial" => PanelTransport.Serial,
            var other => throw new FormatException($"panel.json has a match with the transport {other}"),
        };
        int? usbInterface = m.TryGetProperty("interface", out var i) ? i.GetInt32() : null;
        return new PanelMatch(Hex(usb[0]), Hex(usb[1]), usbInterface, transport);
    }

    private static ThermalrightModel ReadModel(JsonElement m)
    {
        string? Text(string name) => m.TryGetProperty(name, out var v) ? v.GetString() : null;
        int? Int(string name) => m.TryGetProperty(name, out var v) ? v.GetInt32() : null;
        byte? Byte(string name) => m.TryGetProperty(name, out var v) ? v.GetByte() : null;
        bool Flag(string name) => m.TryGetProperty(name, out var v) && v.GetBoolean();
        IReadOnlyList<byte> Bytes(string name) => m.TryGetProperty(name, out var v) ? v.EnumerateArray().Select(b => b.GetByte()).ToList() : [];

        var width = m.GetProperty("width").GetInt32();
        var height = m.GetProperty("height").GetInt32();
        ushort? vendorId = null, productId = null;
        if (Text("usb") is { } usb)
        {
            var ids = usb.Split(':');
            (vendorId, productId) = (Hex(ids[0]), Hex(ids[1]));
        }

        return new ThermalrightModel(
            Id: m.GetProperty("id").GetString()!,
            Name: m.GetProperty("name").GetString()!,
            Width: width,
            Height: height,
            V1Model: m.GetProperty("v1Model").GetString()!,
            V1Aliases: m.TryGetProperty("v1Aliases", out var aliases) ? aliases.EnumerateArray().Select(a => a.GetString()!).ToList() : [],
            NativeWidth: Int("nativeWidth") ?? width,
            NativeHeight: Int("nativeHeight") ?? height,
            VendorId: vendorId,
            ProductId: productId,
            Transport: Text("transport") switch
            {
                "usb" => ThermalrightTransport.Usb,
                "hid" => ThermalrightTransport.Hid,
                "scsi" => ThermalrightTransport.Scsi,
                var other => throw new FormatException($"panel.json has a model with the transport {other}"),
            },
            Protocol: Text("protocol") switch
            {
                null => ThermalrightProtocol.None,
                "chiZhu" => ThermalrightProtocol.ChiZhu,
                "trofeo" => ThermalrightProtocol.Trofeo,
                "trofeoBulk" => ThermalrightProtocol.TrofeoBulk,
                "trofeoBulkLy1" => ThermalrightProtocol.TrofeoBulkLy1,
                "ali" => ThermalrightProtocol.Ali,
                var other => throw new FormatException($"panel.json has a model with the protocol {other}"),
            },
            Pixels: Text("pixels") switch
            {
                "jpeg" => ThermalrightPixels.Jpeg,
                "rgb565le" => ThermalrightPixels.Rgb565Le,
                "rgb565be" => ThermalrightPixels.Rgb565Be,
                var other => throw new FormatException($"panel.json has a model with the pixels {other}"),
            },
            Identifier: Text("identifier") ?? "",
            Pm: Byte("pm"),
            Sub: Byte("sub"),
            ChiZhu: m.TryGetProperty("chizhu", out var rules)
                ? rules.EnumerateArray().Select(r => new ChiZhuRule(r.GetProperty("pm").GetByte(),
                    r.TryGetProperty("sub", out var s) ? s.GetByte() : null,
                    r.TryGetProperty("subFrom", out var f) ? f.GetByte() : null)).ToList()
                : [],
            Byte20: Bytes("byte20"),
            ReportedHeightNot: Int("reportedHeightNot"),
            VariantOf: Text("variantOf"),
            AliTypes: Bytes("aliTypes"),
            AliFallback: Flag("aliFallback"),
            DisplayMask: Flag("displayMask"),
            MaskTurn: Int("maskTurn") ?? 0,
            FlickerFixRows: Int("flickerFixRows"));
    }

    private static ushort Hex(string text) => ushort.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
