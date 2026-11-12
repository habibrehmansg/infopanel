using System.Diagnostics;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Thermalright;

/// <summary>
/// The Thermalright family over the SDK's device seam: InfoPanel 1.4's ChiZhu and TrofeoBulk (LY, LY1) over USB bulk,
/// Trofeo over HID, and ALi over USB bulk. Which protocol a device speaks comes from <c>panel.json</c> (the models with
/// its USB ids); a device whose id has both an ALi and a SCSI model is told apart by its cached interfaces (mass storage
/// is SCSI). The probe sends a protocol's init and reads the model from the reply, as 1.4's scan did (ChiZhu, LY) or as
/// its session's start did (HID, ALi); <see cref="Open"/> runs 1.4's start sequence again and decides the model and size
/// as 1.4 did, from the panel's saved model. SCSI models answer <see cref="PanelDeviceStatus.NotSupported"/>: macOS and
/// Linux can't reach them, and on Windows their driver comes later.
/// </summary>
public sealed class ThermalrightDriver : IPanelDriver
{
    /// <summary>A probe's transfers: 1.4's scan waited 3 s for each (the engine's own deadline ends the probe first).</summary>
    public const int ProbeTimeoutMs = 3000;

    /// <summary>The most frames a second: 1.4's Thermalright frame rate slider stopped at 30.</summary>
    public const int MaxFps = 30;

    /// <summary>A start's init transfers (1.4: 5 s for a write, and for a ChiZhu or HID reply).</summary>
    public const int InitTimeoutMs = 5000;

    /// <summary>The longest a start waits for a TrofeoBulk or ALi reply (1.4: 10 s).</summary>
    public const int ReplyTimeoutMs = 10_000;

    /// <summary>A HID collection the panel's frames go to: an output report of 512 bytes and its report id (1.4's scan kept only these).</summary>
    public static readonly PanelHidFilter HidFilter = new(MinOutputReport: TrofeoProtocol.PacketSize + 1);

    private const byte MassStorageClass = 0x08;

    /// <summary>
    /// The most a probe spends on its device's calls (the engine ends a probe at 5 s): a HID panel's collections share it,
    /// each the part of what is left that falls to it, so one that never answers can't keep the next from being asked.
    /// </summary>
    public static readonly TimeSpan DefaultProbeBudget = TimeSpan.FromMilliseconds(4500);

    private readonly IPanelDevices _devices;

    /// <summary>A driver that reaches its devices through <paramref name="devices"/> (the panel host gives it the engine's).</summary>
    public ThermalrightDriver(IPanelDevices devices)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    }

    /// <summary>
    /// How the driver waits 1.4's delays (the start's settling, a booting ChiZhu's 3 s, the gaps between frames): until
    /// the time passes or the token is cancelled, which throws. Tests replace it so a trace doesn't take seconds.
    /// </summary>
    internal Action<TimeSpan, CancellationToken> Wait { get; set; } = (time, stop) =>
    {
        if (stop.WaitHandle.WaitOne(time))
            stop.ThrowIfCancellationRequested();
    };

    /// <summary>A probe's budget, <see cref="DefaultProbeBudget"/> unless a test sets a shorter one.</summary>
    internal TimeSpan ProbeBudget { get; set; } = DefaultProbeBudget;

    /// <summary>The probe's clock, a <see cref="Stopwatch"/> timestamp; tests replace it to end a share at a chosen reading.</summary>
    internal Func<long> Clock { get; set; } = Stopwatch.GetTimestamp;

    internal ThermalrightCatalog Catalog { get; } = ThermalrightCatalog.Default;

    public string Family => ThermalrightCatalog.FamilyId;

    public IReadOnlyList<PanelMatch> Matches => Catalog.Matches;

    /// <summary>How a device is reached, from the models with its ids.</summary>
    internal enum Way { None, ChiZhu, Trofeo, TrofeoBulk, TrofeoBulkLy1, Ali, Scsi }

    internal PanelDeviceStatus WayOf(PanelCandidate device, out Way way)
    {
        way = Way.None;
        var ways = Catalog.WithIds(device.VendorId, device.ProductId).Select(WayOf).Distinct().ToList();
        if (ways.Count == 1)
        {
            way = ways[0];
            return PanelDeviceStatus.Ok;
        }
        if (ways.Count == 2 && ways.Contains(Way.Ali) && ways.Contains(Way.Scsi))
        {
            var status = _devices.Describe(device, out var descriptor);
            if (status != PanelDeviceStatus.Ok)
                return status;
            way = descriptor!.Interfaces.Any(i => i.Class == MassStorageClass) ? Way.Scsi : Way.Ali;
        }
        return PanelDeviceStatus.Ok;
    }

    internal static Way WayOf(ThermalrightModel model) => model.Transport == ThermalrightTransport.Scsi ? Way.Scsi : model.Protocol switch
    {
        ThermalrightProtocol.ChiZhu => Way.ChiZhu,
        ThermalrightProtocol.Trofeo => Way.Trofeo,
        ThermalrightProtocol.TrofeoBulk => Way.TrofeoBulk,
        ThermalrightProtocol.TrofeoBulkLy1 => Way.TrofeoBulkLy1,
        ThermalrightProtocol.Ali => Way.Ali,
        _ => Way.None,
    };

    public PanelProbe Probe(PanelCandidate device, CancellationToken stop)
    {
        var status = WayOf(device, out var way);
        if (status != PanelDeviceStatus.Ok)
            return new PanelProbe(false, null, status, "its descriptors couldn't be read");
        return way switch
        {
            Way.ChiZhu => ProbeChiZhu(device, stop),
            Way.Trofeo => ProbeHid(device, stop),
            Way.TrofeoBulk => ProbeBulk(device, way, stop),
            Way.TrofeoBulkLy1 => ProbeBulk(device, way, stop),
            Way.Ali => ProbeBulk(device, way, stop),
            Way.Scsi => Mine(Catalog.WithIds(device.VendorId, device.ProductId).First(m => m.Transport == ThermalrightTransport.Scsi), null),
            _ => new PanelProbe(false, null, PanelDeviceStatus.Ok, "no model of the family has its ids"),
        };
    }

    /// <summary>The whole milliseconds left until <paramref name="ends"/> (a <see cref="Stopwatch"/> timestamp), at most a probe call's 3 s; 0 when none.</summary>
    private int MsLeft(long ends) =>
        (int)Math.Clamp(Stopwatch.GetElapsedTime(Clock(), ends).TotalMilliseconds, 0, ProbeTimeoutMs);

    private static PanelProbe Mine(ThermalrightModel model, string? serial) =>
        new(true, new PanelIdentity(model.Id, model.Width, model.Height, serial, null), PanelDeviceStatus.Ok);

    private static PanelProbe Failed(PanelDeviceStatus status, string why) => new(false, null, status, why);

    /// <summary>The identity of a model a reply doesn't name: its PM and SUB (<c>pm-200-sub-9</c>), else <c>unknown</c>.</summary>
    internal static string CodeOf(byte? pm, byte? sub) => pm is { } p && sub is { } s ? $"pm-{p}-sub-{s}" : "unknown";

    /// <summary>A device whose reply names no model of its protocol: mine, by its code, run once the <c>model</c> setting names one.</summary>
    private static PanelProbe Unknown(string code, int width, int height, string? serial) =>
        new(true, new PanelIdentity(code, width, height, serial, null), PanelDeviceStatus.Ok, $"its reply names no model it knows ({code})");

    /// <summary>
    /// 1.4's ChiZhu scan probe (<c>ThermalrightPanelHelper.ProbeWinUsbModelInner</c>): the init, a reply of up to 1024
    /// bytes, the model from its PM and SUB, else its identifier. A booting panel is asked again on a later pass; a reply
    /// that names no model makes an unknown model, named by its PM and SUB, which runs once the <c>model</c> setting names one.
    /// </summary>
    private PanelProbe ProbeChiZhu(PanelCandidate device, CancellationToken stop)
    {
        var status = OpenBulk(device, ProbeTimeoutMs, stop, out var usb, out var outEndpoint, out var inEndpoint);
        if (status != PanelDeviceStatus.Ok)
            return Failed(status, "it couldn't be opened");
        using (usb)
        {
            status = usb!.BulkWrite(outEndpoint, ChiZhuProtocol.InitCommand(), ProbeTimeoutMs, cancellationToken: stop);
            if (status != PanelDeviceStatus.Ok)
                return Failed(status, "its init wasn't taken");
            var reply = new byte[ChiZhuProtocol.ReplySize];
            status = usb.BulkRead(inEndpoint, reply, ProbeTimeoutMs, out var read, stop);
            if (status != PanelDeviceStatus.Ok)
                return Failed(status, "it didn't answer its init");
            var answer = reply.AsSpan(0, read);
            if (ChiZhuProtocol.IsBooting(answer))
                return Failed(PanelDeviceStatus.Timeout, "it is still starting");
            var serial = usb.SerialNumber ?? ChiZhuProtocol.ParseReply(answer).Serial;
            // 1.4's identifier fallback takes, for a reply with no identifier, the first model without one (a TrofeoBulk
            // panel's): such a model can't be driven over ChiZhu, so the reply counts as naming none.
            if (ThermalrightDetect.ChiZhuScan(Catalog, answer) is { Protocol: ThermalrightProtocol.ChiZhu } model)
                return Mine(model, serial);
            var parsed = ChiZhuProtocol.ParseReply(answer);
            return Unknown(CodeOf(parsed.Pm, parsed.Sub), ThermalrightDetect.DefaultWidth, ThermalrightDetect.DefaultHeight, serial);
        }
    }

    /// <summary>
    /// A HID panel: each of its data collections (an output report of 513 bytes) in turn, until one takes the init and
    /// answers it (a failed one is let go first); the model from the reply's PM and SUB, else its identifier, as 1.4's
    /// session start decided it. The first model with the device's ids (1.4's scan) is only where a start began: a reply
    /// that names no HID model makes an unknown model, named by its PM and SUB. No reply (a report of its id alone is
    /// none): asked again on a later pass. The collections share the probe's budget: each one's calls wait at most its
    /// share of what is left (that time split evenly among it and those after it), and never longer than 1.4's 3 s.
    /// </summary>
    private PanelProbe ProbeHid(PanelCandidate device, CancellationToken stop)
    {
        var ends = Clock() + (long)(ProbeBudget.TotalSeconds * Stopwatch.Frequency);
        var status = _devices.ListHid(device, HidFilter, out var collections);
        if (status != PanelDeviceStatus.Ok)
            return Failed(status, "its HID collections couldn't be listed");
        var failed = Failed(PanelDeviceStatus.NotSupported, "it has no HID collection with 513-byte output reports");
        for (var i = 0; i < collections.Count; i++)
        {
            var now = Clock();
            var share = now + Math.Max(0, ends - now) / (collections.Count - i);
            // Read once: a pause between a check and a second reading could hand the open 0, which the seam refuses.
            var openMs = MsLeft(share);
            if (openMs < 1)
            {
                failed = Failed(PanelDeviceStatus.Timeout, "the probe's time ran out before its next HID collection");
                break;
            }
            status = _devices.OpenHid(device, collections[i], openMs, out var hid, stop);
            if (status == PanelDeviceStatus.Gone)
                return Failed(status, "it went away");
            if (status != PanelDeviceStatus.Ok)
            {
                failed = Failed(status, "its HID collection couldn't be opened");
                continue;
            }
            using (hid)
            {
                status = MsLeft(share) is var writeMs and >= 1
                    ? hid!.Write(TrofeoProtocol.HidReport(TrofeoProtocol.InitPacket()), writeMs, stop)
                    : PanelDeviceStatus.Timeout;
                if (status == PanelDeviceStatus.Gone)
                    return Failed(status, "it went away");
                if (status != PanelDeviceStatus.Ok)
                {
                    failed = Failed(status, "its init wasn't taken");
                    continue;
                }
                var report = new byte[Math.Max(hid!.MaxInputReportLength, TrofeoProtocol.PacketSize + 1)];
                var read = 0;
                status = MsLeft(share) is var readMs and >= 1 ? hid.Read(report, readMs, out read, stop) : PanelDeviceStatus.Timeout;
                if (status == PanelDeviceStatus.Gone)
                    return Failed(status, "it went away");
                var reply = status == PanelDeviceStatus.Ok ? TrofeoProtocol.FromHidReport(report.AsSpan(0, read)) : null;
                if (reply is not { Length: > 0 })
                {
                    failed = Failed(status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status, "it didn't answer its init");
                    continue;
                }
                var detected = ThermalrightDetect.HidSession(Catalog, null, reply);
                var parsed = TrofeoProtocol.ParseReply(reply);
                return detected.Model is { } model && WayOf(model) == Way.Trofeo
                    ? new PanelProbe(true, new PanelIdentity(model.Id, detected.Width, detected.Height, parsed.Serial, null), PanelDeviceStatus.Ok)
                    : Unknown(CodeOf(parsed.Pm, parsed.Sub), detected.Width, detected.Height, parsed.Serial);
            }
        }
        return failed;
    }

    /// <summary>
    /// A bulk panel by its ids: TrofeoBulk LY (the init, then byte 20 of the reply, 1.4's scan probe; no reply, the model
    /// with the ids), LY1 (the model with the ids, nothing sent) and ALi (the init, then the device type, as 1.4's session
    /// start read it; no reply, the 320 × 320).
    /// </summary>
    private PanelProbe ProbeBulk(PanelCandidate device, Way way, CancellationToken stop)
    {
        var status = OpenBulk(device, ProbeTimeoutMs, stop, out var usb, out var outEndpoint, out var inEndpoint);
        if (status != PanelDeviceStatus.Ok)
            return Failed(status, "it couldn't be opened");
        using (usb)
        {
            var byIds = Catalog.ByVidPid(device.VendorId, device.ProductId);
            if (way == Way.TrofeoBulkLy1)
                return byIds == null ? Failed(PanelDeviceStatus.Ok, "no one model has its ids") : Mine(byIds, usb!.SerialNumber);

            var (init, replySize) = way == Way.Ali
                ? (AliProtocol.InitPacket(), 1024)
                : (TrofeoBulkProtocol.InitPacket(ThermalrightProtocol.TrofeoBulk), TrofeoBulkProtocol.ReplySize);
            status = usb!.BulkWrite(outEndpoint, init, ProbeTimeoutMs, cancellationToken: stop);
            if (status != PanelDeviceStatus.Ok)
                return Failed(status, "its init wasn't taken");
            var reply = new byte[replySize];
            status = usb.BulkRead(inEndpoint, reply, ProbeTimeoutMs, out var read, stop);
            if (status == PanelDeviceStatus.Gone)
                return Failed(status, "it went away");
            var answer = status == PanelDeviceStatus.Ok ? reply.AsSpan(0, read) : [];
            if (way == Way.Ali)
            {
                var ali = ThermalrightDetect.AliSession(Catalog, null, answer);
                var model = ali.Model ?? Catalog.Models.First(m => m.Protocol == ThermalrightProtocol.Ali && m.AliFallback);
                return Mine(model, usb.SerialNumber ?? (answer.Length > 0 ? AliProtocol.ParseReply(answer).Serial : null));
            }
            var ly = ThermalrightDetect.TrofeoBulkScan(Catalog, answer) ?? byIds;
            return ly == null
                ? Failed(PanelDeviceStatus.Ok, "no model of the family has its ids")
                : Mine(ly, usb.SerialNumber ?? (answer.Length >= 20 ? TrofeoBulkProtocol.ParseReply(answer).Serial : null));
        }
    }

    /// <summary>
    /// Opens interface 0 (the match's) and finds its first OUT and first IN endpoints, as 1.4 took them from the
    /// descriptors; <see cref="PanelDeviceStatus.NotSupported"/> when it has no pair.
    /// </summary>
    internal PanelDeviceStatus OpenBulk(PanelCandidate device, int timeoutMs, CancellationToken stop, out IPanelUsb? usb, out byte outEndpoint, out byte inEndpoint)
    {
        outEndpoint = inEndpoint = 0;
        var match = Matches.FirstOrDefault(m => m.VendorId == device.VendorId && m.ProductId == device.ProductId);
        var status = _devices.OpenUsb(device, match?.Interface ?? 0, timeoutMs, out usb, stop);
        if (status != PanelDeviceStatus.Ok)
            return status;
        var output = usb!.Endpoints.FirstOrDefault(e => !e.IsIn);
        var input = usb.Endpoints.FirstOrDefault(e => e.IsIn);
        if (output == null || input == null)
        {
            usb.Dispose();
            usb = null;
            return PanelDeviceStatus.NotSupported;
        }
        (outEndpoint, inEndpoint) = (output.Address, input.Address);
        return PanelDeviceStatus.Ok;
    }

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        var status = WayOf(device, out var way);
        if (status != PanelDeviceStatus.Ok)
            return new PanelOpen(null, status, "its descriptors couldn't be read");
        if (way == Way.Scsi)
            return new PanelOpen(null, PanelDeviceStatus.NotSupported, OperatingSystem.IsWindows()
                ? "Thermalright's SCSI panels aren't driven yet"
                : "Thermalright's SCSI panels are driven on Windows only");
        if (way == Way.None)
            return new PanelOpen(null, PanelDeviceStatus.NotSupported, "no model of the family has its ids");

        var settings = ThermalrightSettings.Read(options.FamilySettings);
        ThermalrightModel? chosen = null;
        if (settings.Model != null)
        {
            chosen = Catalog.ById(settings.Model);
            if (chosen == null || WayOf(chosen) != way)
                return new PanelOpen(null, PanelDeviceStatus.NotSupported, $"the model setting {settings.Model} isn't a model this panel's protocol drives");
        }
        // The saved model (an unknown one's code names none); one saved for another protocol: 1.4 started such a device
        // from no model.
        var start = Catalog.ById(identity.Model);
        if (start != null && WayOf(start) != way)
            start = null;

        // 1.4's device task waited 300 ms before it opened the device.
        Wait(TimeSpan.FromMilliseconds(300), stop);
        var session = new ThermalrightStart(this, _devices, device, way, start, chosen, settings);
        var opened = way switch
        {
            Way.ChiZhu => session.ChiZhu(stop),
            Way.Trofeo => session.Hid(stop),
            Way.TrofeoBulk or Way.TrofeoBulkLy1 => session.TrofeoBulk(way == Way.TrofeoBulkLy1, stop),
            _ => session.Ali(stop),
        };
        if (opened.Session != null && options.Format is { } asked && asked != opened.Session.Capabilities.Formats[0])
            options.FormatRefused?.Invoke(PanelDeviceStatus.NotSupported);
        return opened;
    }

    /// <summary>
    /// A model's frames: JPEG, or RGB565 in the model's byte order, by truncation as 1.4 converted (TRCC's, never
    /// rounded): the engine's <see cref="PanelFrameFormat.Rgb565LeTruncated"/> and <see cref="PanelFrameFormat.Rgb565Be"/>.
    /// </summary>
    internal static PanelFrameFormat FormatOf(ThermalrightPixels pixels) => pixels switch
    {
        ThermalrightPixels.Rgb565Le => PanelFrameFormat.Rgb565LeTruncated,
        ThermalrightPixels.Rgb565Be => PanelFrameFormat.Rgb565Be,
        _ => PanelFrameFormat.Jpeg,
    };

    /// <summary>
    /// What a session of <paramref name="model"/> at <paramref name="width"/> × <paramref name="height"/> takes: its one
    /// format; 30 fps at most; the JPEG cap (400,000 bytes over HID, 230,000 over TrofeoBulk) with 1.4's quality, 95,
    /// lowered 5 at a time to 50; dimmed by the engine (1.4 dimmed in software, these panels have no light of their own);
    /// the display mask's overlays, when the setting asks and the model has one; the flicker fix's crop, when the
    /// setting asks and the start offers it.
    /// </summary>
    internal static PanelCapabilities CapabilitiesOf(ThermalrightModel model, int width, int height, int? cropRows, ThermalrightSettings settings)
    {
        var format = FormatOf(model.Pixels);
        return new PanelCapabilities(width, height, [format], MaxFps,
            MaxFrameBytes: format == PanelFrameFormat.Jpeg ? ThermalrightFrames.MaxJpegBytes(model) : null,
            JpegQuality: ThermalrightFrames.DefaultJpegQuality,
            MinJpegQuality: ThermalrightFrames.MinJpegQuality)
        {
            DimInSoftware = true,
            JpegQualityStep = ThermalrightFrames.JpegQualityStep,
            Overlays = settings.Mask != ThermalrightMask.None && model.DisplayMask ? Overlays(settings.Mask, model.MaskTurn) : null,
            Crop = settings.FlickerFix && cropRows is { } rows && rows > 0 && rows < height ? new PanelCrop(0, 0, width, rows) : null,
        };
    }

    /// <summary>
    /// The mask's PNG for each of the panel's rotations, at the panel's turn plus the model's (the Levita Vision's 180°),
    /// as 1.4 drew it: at 0° and 180° its own PNG; at 90° and 270° its portrait PNG (720 × 1600) drawn at the top left of
    /// the landscape frame and clipped to it, which the package holds as a PNG of the panel's size
    /// (<see cref="ThermalrightFrames.OpenOverlay"/>). 1.4's clipped mask is kept as it was, a likely 1.4 bug.
    /// </summary>
    internal static IReadOnlyDictionary<int, byte[]> Overlays(ThermalrightMask mask, int maskTurn)
    {
        var overlays = new Dictionary<int, byte[]>();
        foreach (var rotation in new[] { 0, 90, 180, 270 })
        {
            using var png = ThermalrightFrames.OpenOverlay(mask, ThermalrightFrames.MaskDegrees(rotation, maskTurn))
                ?? throw new InvalidOperationException($"the mask {mask} at {rotation}° isn't embedded");
            using var copy = new MemoryStream();
            png.CopyTo(copy);
            overlays[rotation] = copy.ToArray();
        }
        return overlays;
    }
}
