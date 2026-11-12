namespace InfoPanel.Panels.Thermalright;

/// <summary>How a model is reached: USB bulk transfers, HID reports, or SCSI pass-through (Windows only).</summary>
public enum ThermalrightTransport { Usb, Hid, Scsi }

/// <summary>
/// The five protocols InfoPanel 1.4 spoke over USB and HID (a SCSI model has none of them: <see cref="None"/>).
/// ChiZhu: <c>12 34 56 78</c>, 64-byte headers. Trofeo: <c>DA DB DC DD</c>, 512-byte packets. TrofeoBulk (LY) and
/// TrofeoBulkLy1: <c>02 FF</c> init, 512-byte sub-packets. Ali: <c>F5</c> headers and RGB565 pixels.
/// </summary>
public enum ThermalrightProtocol { None, ChiZhu, Trofeo, TrofeoBulk, TrofeoBulkLy1, Ali }

/// <summary>The pixels a model takes: JPEG, or 16-bit RGB565 little- or big-endian (truncated, never dithered).</summary>
public enum ThermalrightPixels { Jpeg, Rgb565Le, Rgb565Be }

/// <summary>
/// One ChiZhu rule of <c>panel.json</c>: the PM byte (reply byte 24) and the SUB byte (byte 28) exactly, from
/// <see cref="SubFrom"/> up, or any SUB when both are null. An exact rule wins over a range, a range over any.
/// </summary>
public sealed record ChiZhuRule(byte Pm, byte? Sub = null, byte? SubFrom = null);

/// <summary>
/// A model as <c>panel.json</c> declares it: its id, name and render size (what the engine draws), its 1.x name
/// (<see cref="V1Model"/>, with any older spellings in <see cref="V1Aliases"/>), the native size when it differs, its
/// USB ids (none for a variant told apart only after its init), transport, protocol and pixels, and the keys that
/// identify it: the init reply's identifier string, PM and SUB bytes, ChiZhu rules, TrofeoBulk's byte 20 and reported
/// height (<see cref="VariantOf"/> names the model a reported height turns into this one), ALi's device type. The
/// family settings it offers: the display mask (<see cref="MaskTurn"/>, degrees added to the panel's rotation) and the
/// flicker fix's rows.
/// </summary>
public sealed record ThermalrightModel(
    string Id,
    string Name,
    int Width,
    int Height,
    string V1Model,
    IReadOnlyList<string> V1Aliases,
    int NativeWidth,
    int NativeHeight,
    ushort? VendorId,
    ushort? ProductId,
    ThermalrightTransport Transport,
    ThermalrightProtocol Protocol,
    ThermalrightPixels Pixels,
    string Identifier,
    byte? Pm,
    byte? Sub,
    IReadOnlyList<ChiZhuRule> ChiZhu,
    IReadOnlyList<byte> Byte20,
    int? ReportedHeightNot,
    string? VariantOf,
    IReadOnlyList<byte> AliTypes,
    bool AliFallback,
    bool DisplayMask,
    int MaskTurn,
    int? FlickerFixRows);

/// <summary>A size the HID init reply's PM byte names, with 1.4's label for it.</summary>
public sealed record HidPmSize(byte Pm, int Width, int Height, string Size);

/// <summary>What a detection came to: the model (null: none known) and the size frames are drawn at.</summary>
public sealed record ThermalrightDetection(ThermalrightModel? Model, int Width, int Height, int? CropRows = null);
