namespace InfoPanel.Panels.Thermalright;

/// <summary>
/// Which model an init reply makes, as InfoPanel 1.4 decided it: the scan's probe (its <c>ThermalrightPanelHelper</c>)
/// and the session's start (its <c>ThermalrightPanelDeviceTask</c>), every rule's values from <c>panel.json</c>. A
/// session starts from the model 1.4 had for the device (<paramref name="start"/>: the scan's, or one saved before;
/// null for none) at that model's size, else 480 × 480, and keeps it when the reply decides nothing.
/// </summary>
public static class ThermalrightDetect
{
    public const int DefaultWidth = 480;
    public const int DefaultHeight = 480;

    /// <summary>The flicker fix's crop: the top rows a 480-row TrofeoBulk frame is cut to.</summary>
    public const int FlickerFixRows = 462;

    private static ThermalrightDetection Start(ThermalrightModel? start) =>
        new(start, start?.Width ?? DefaultWidth, start?.Height ?? DefaultHeight);

    private static ThermalrightDetection As(ThermalrightModel model) => new(model, model.Width, model.Height);

    /// <summary>
    /// The ChiZhu scan's probe of a reply (null under 12 bytes or while booting): the PM and SUB rules, then the
    /// identifier in bytes 4–11 with the SUB, then alone.
    /// </summary>
    public static ThermalrightModel? ChiZhuScan(ThermalrightCatalog catalog, ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 12 || ChiZhuProtocol.IsBooting(reply))
            return null;
        byte? pm = reply.Length >= 25 ? reply[24] : null;
        byte? sub = reply.Length >= 29 ? reply[28] : null;
        if (pm.HasValue && sub.HasValue && catalog.ByChiZhuPm(pm.Value, sub.Value) is { } model)
            return model;
        var identifier = System.Text.Encoding.ASCII.GetString(reply.Slice(4, 8)).TrimEnd('\0');
        return catalog.ByIdentifier(identifier, sub);
    }

    /// <summary>
    /// A ChiZhu session's start, in three passes over the reply: the identifier with the SUB (which replaces the
    /// starting model, by none when it finds none); then, with no model, the PM and SUB rules; then, still none, the
    /// identifier alone. An empty reply keeps the start. A reply still booting after <see cref="ChiZhuProtocol.BootTries"/>
    /// is read as any other, as 1.4 did.
    /// </summary>
    public static ThermalrightDetection ChiZhuSession(ThermalrightCatalog catalog, ThermalrightModel? start, ReadOnlySpan<byte> reply)
    {
        var detected = Start(start);
        if (reply.Length == 0)
            return detected;

        var parsed = ChiZhuProtocol.ParseReply(reply);
        var identifier = parsed.Identifier;
        if (!string.IsNullOrEmpty(identifier) && parsed.Sub.HasValue)
        {
            var strict = catalog.ByIdentifierAndSub(identifier, parsed.Sub.Value);
            detected = strict != null ? As(strict) : detected with { Model = null };
        }
        if (detected.Model == null && parsed.Pm.HasValue && parsed.Sub.HasValue && catalog.ByChiZhuPm(parsed.Pm.Value, parsed.Sub.Value) is { } byPm)
            detected = As(byPm);
        if (detected.Model == null && !string.IsNullOrEmpty(identifier) && catalog.ByIdentifier(identifier) is { } byIdentifier)
            detected = As(byIdentifier);
        return detected;
    }

    /// <summary>The model a HID device's session starts from: the one model with its ids, else the first with them.</summary>
    public static ThermalrightModel? HidScan(ThermalrightCatalog catalog, ushort vendorId, ushort productId) =>
        catalog.ByVidPid(vendorId, productId)
        ?? catalog.Models.FirstOrDefault(m => (m.VendorId ?? 0) == vendorId && (m.ProductId ?? 0) == productId);

    /// <summary>
    /// A HID session's start from its init reply (the report id gone): the model of the PM and SUB, else only the size the
    /// PM names; then, with no model at all, the identifier string's.
    /// </summary>
    public static ThermalrightDetection HidSession(ThermalrightCatalog catalog, ThermalrightModel? start, ReadOnlySpan<byte> reply)
    {
        var detected = Start(start);
        var parsed = TrofeoProtocol.ParseReply(reply);
        if (parsed.Pm is not { } pm)
            return detected;

        if (catalog.ByHidPm(pm, parsed.Sub) is { } model)
            detected = As(model);
        else if (catalog.HidSize(pm) is { } size)
            detected = detected with { Width = size.Width, Height = size.Height };

        if (detected.Model == null && parsed.Identifier != null && catalog.ByIdentifier(parsed.Identifier) is { } byIdentifier)
            detected = detected with { Model = byIdentifier };
        return detected;
    }

    /// <summary>
    /// The TrofeoBulk scan's probe of a reply (null under 21 bytes): the model whose byte 20 it is, else the one with the
    /// device's USB ids.
    /// </summary>
    public static ThermalrightModel? TrofeoBulkScan(ThermalrightCatalog catalog, ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 21)
            return null;
        var b20 = reply[20];
        return catalog.Models.FirstOrDefault(m => m.Protocol == ThermalrightProtocol.TrofeoBulk && m.Byte20.Contains(b20))
            ?? catalog.Models.FirstOrDefault(m => m.Protocol == ThermalrightProtocol.TrofeoBulk && m.VendorId != null && m.Byte20.Count == 0);
    }

    /// <summary>
    /// A TrofeoBulk session's start: the size the reply reports; a byte 20 that is its own model's alone (the 11.3")
    /// makes it, at its size; a start that is such a model keeps its size; a start whose variant a reported height other
    /// than its own names (the 9.16" reporting 599: v2) becomes that variant, at its size, and the variant keeps its size.
    /// A 480-row frame of anything but a byte-20 model may be cut to <see cref="FlickerFixRows"/>.
    /// </summary>
    public static ThermalrightDetection TrofeoBulkSession(ThermalrightCatalog catalog, ThermalrightModel? start, ReadOnlySpan<byte> reply)
    {
        var detected = Start(start);
        var parsed = reply.Length > 0 ? TrofeoBulkProtocol.ParseReply(reply) : null;
        if (parsed is { Width: { } width, Height: { } height })
            detected = detected with { Width = width, Height = height };

        var model = start;
        var variant = start == null ? null : catalog.Models.FirstOrDefault(m => m.VariantOf == start.Id);
        var byByte20 = parsed?.Byte20 is { } b20
            ? catalog.Models.FirstOrDefault(m => m.Protocol == ThermalrightProtocol.TrofeoBulk && m.ReportedHeightNot == null && m.Byte20.Contains(b20))
            : null;

        if (byByte20 != null)
        {
            model = byByte20;
            detected = As(byByte20);
        }
        else if (start is { Byte20.Count: > 0, ReportedHeightNot: null })
            detected = detected with { Width = start.Width, Height = start.Height };
        else if (variant?.ReportedHeightNot is { } own && detected.Height != own)
        {
            model = variant;
            detected = As(variant);
        }
        else if (start is { ReportedHeightNot: not null })
            detected = detected with { Width = start.Width, Height = start.Height };

        var byte20Model = model is { Byte20.Count: > 0, ReportedHeightNot: null };
        return detected with { CropRows = detected.Height == 480 && !byte20Model ? FlickerFixRows : null };
    }

    /// <summary>
    /// An LY1 session's start: the starting model kept, at the size the reply reports when it reports one (1 to 4096
    /// each way), else the model's; never LY's byte-20 or reported-height switch, and no crop (1.4's LY1 path).
    /// </summary>
    public static ThermalrightDetection TrofeoBulkLy1Session(ThermalrightCatalog catalog, ThermalrightModel? start, ReadOnlySpan<byte> reply)
    {
        _ = catalog; // the same shape as the other sessions; LY1 looks nothing up
        var detected = Start(start);
        if (reply.Length > 0 && TrofeoBulkProtocol.ParseReply(reply) is { Width: { } width, Height: { } height })
            detected = detected with { Width = width, Height = height };
        return detected;
    }

    /// <summary>An ALi session's start: device type 54 or another (the models' <c>aliTypes</c> and <c>aliFallback</c>).</summary>
    public static ThermalrightDetection AliSession(ThermalrightCatalog catalog, ThermalrightModel? start, ReadOnlySpan<byte> reply)
    {
        if (reply.Length == 0)
            return Start(start);
        var type = AliProtocol.ParseReply(reply).DeviceType;
        var model = catalog.Models.FirstOrDefault(m => m.Protocol == ThermalrightProtocol.Ali && m.AliTypes.Contains(type))
            ?? catalog.Models.First(m => m.Protocol == ThermalrightProtocol.Ali && m.AliFallback);
        return As(model);
    }

    /// <summary>
    /// The pixel bytes an ALi session's frames carry: the size driven's RGB565 after a reply; with none, 1.4's default
    /// <see cref="AliProtocol.DefaultFrameBytes"/> whatever size the start keeps (a smaller frame padded with zeros).
    /// </summary>
    public static int AliFrameBytes(int replyLength, int width, int height) =>
        replyLength == 0 ? AliProtocol.DefaultFrameBytes : checked(width * height * 2);
}
