namespace InfoPanel.Panels.Thermalright;

/// <summary>The display mask a Wonder, Rainbow or Levita Vision 360 may draw over its camera's punch-hole.</summary>
public enum ThermalrightMask { None, RoundedLeft, RoundedAll }

/// <summary>
/// What InfoPanel 1.4 did to a frame's pixels for these panels: RGB565 by truncation (TRCC's, never dithered), the
/// flicker fix's crop to the top rows, the JPEG cap with quality lowered 5 at a time down to 50, and the display masks,
/// PNG overlays embedded in the package.
/// </summary>
public static class ThermalrightFrames
{
    public const int DefaultJpegQuality = 95;
    public const int JpegQualityStep = 5;
    public const int MinJpegQuality = 50;
    public const int HidMaxJpegBytes = 400_000;
    public const int TrofeoBulkMaxJpegBytes = 230_000;

    private const string MaskPrefix = "InfoPanel.Panels.Thermalright.Overlays.";

    /// <summary>
    /// RGBA rows (<paramref name="stride"/> bytes apart) as RGB565: red's top 5 bits, green's top 6, blue's top 5, the
    /// low bits dropped; each pixel's two bytes high first when <paramref name="bigEndian"/>.
    /// </summary>
    public static void ToRgb565(ReadOnlySpan<byte> rgba, int width, int height, int stride, bool bigEndian, Span<byte> destination)
    {
        CheckRows(rgba.Length, width, stride, height);
        if (destination.Length < checked((long)width * height * 2))
            throw new ArgumentException("The destination is shorter than the frame.", nameof(destination));
        for (var y = 0; y < height; y++)
        {
            var row = rgba.Slice(y * stride, width * 4);
            var output = destination.Slice(y * width * 2, width * 2);
            for (var x = 0; x < width; x++)
            {
                var r = row[x * 4];
                var g = row[x * 4 + 1];
                var b = row[x * 4 + 2];
                var hi = (byte)((r & 0xF8) | (g >> 5));
                var lo = (byte)(((g & 0x1C) << 3) | (b >> 3));
                output[x * 2] = bigEndian ? hi : lo;
                output[x * 2 + 1] = bigEndian ? lo : hi;
            }
        }
    }

    /// <summary>The top <paramref name="rows"/> rows of RGBA rows, packed (width × 4 bytes a row).</summary>
    public static byte[] CropTop(ReadOnlySpan<byte> rgba, int width, int stride, int rows)
    {
        CheckRows(rgba.Length, width, stride, rows);
        var length = (long)width * 4 * rows;
        if (length > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "The cropped frame would be larger than an array holds.");
        var cropped = new byte[length];
        for (var y = 0; y < rows; y++)
            rgba.Slice(y * stride, width * 4).CopyTo(cropped.AsSpan(y * width * 4));
        return cropped;
    }

    // RGBA rows `stride` bytes apart, each `width` pixels: a width of at least 1, a stride that holds a row, no negative
    // row count, and a source that holds every row asked for, in 64-bit arithmetic so nothing wraps.
    private static void CheckRows(int sourceLength, int width, int stride, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        var row = (long)width * 4;
        if (stride < row)
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"A stride of {stride} bytes doesn't hold a row of {width} pixels.");
        if (rows > 0 && (long)(rows - 1) * stride + row > sourceLength)
            throw new ArgumentException($"The source's {sourceLength} bytes don't hold {rows} rows of {width} pixels {stride} bytes apart.", nameof(sourceLength));
    }

    /// <summary>
    /// The most bytes a model's JPEG frame may be: 400,000 over HID (the panel's buffer; TRCC never sends 450,000),
    /// 230,000 over TrofeoBulk (LY and LY1); null for no cap (ChiZhu, ALi, SCSI, and HID models that take RGB565).
    /// </summary>
    public static int? MaxJpegBytes(ThermalrightModel model) => model switch
    {
        { Transport: ThermalrightTransport.Hid, Pixels: ThermalrightPixels.Jpeg } => HidMaxJpegBytes,
        { Transport: ThermalrightTransport.Usb, Protocol: ThermalrightProtocol.TrofeoBulk or ThermalrightProtocol.TrofeoBulkLy1 } => TrofeoBulkMaxJpegBytes,
        _ => null,
    };

    /// <summary>The qualities a frame over the cap is encoded at again, in turn, until one fits: 5 lower each time, down to 50.</summary>
    public static IEnumerable<int> LowerQualities(int quality)
    {
        for (var q = quality - JpegQualityStep; q >= MinJpegQuality; q -= JpegQualityStep)
            yield return q;
    }

    /// <summary>The overlay's turn: the panel's rotation (0, 90, 180 or 270; anything else 0) plus the model's.</summary>
    public static int MaskDegrees(int rotation, int maskTurn) =>
        ((rotation is 90 or 180 or 270 ? rotation : 0) + maskTurn) % 360;

    /// <summary>The overlay's file name (<c>mask_rounded_left_90.png</c>), null for no mask.</summary>
    public static string? MaskResource(ThermalrightMask mask, int degrees) => mask switch
    {
        ThermalrightMask.RoundedLeft => $"mask_rounded_left_{degrees}.png",
        ThermalrightMask.RoundedAll => $"mask_rounded_all_{degrees}.png",
        _ => null,
    };

    /// <summary>1.4's own PNG of the mask at <paramref name="degrees"/>, or null for no mask or a turn it has none for.</summary>
    public static Stream? OpenMask(ThermalrightMask mask, int degrees) =>
        MaskResource(mask, degrees) is { } name ? typeof(ThermalrightFrames).Assembly.GetManifestResourceStream(MaskPrefix + name) : null;

    /// <summary>
    /// The mask at <paramref name="degrees"/> as the engine draws it, a PNG of the panel's size (1600 × 720): 1.4's own at
    /// 0° and 180°; at 90° and 270°, what 1.4 drew of its portrait PNG (720 × 1600) at the frame's top left, clipped to
    /// the frame (<c>mask_rounded_*_{90,270}_clipped.png</c>, made from 1.4's by the tests' generator, which their oracle
    /// test proves). Null for no mask or a turn it has none for.
    /// </summary>
    public static Stream? OpenOverlay(ThermalrightMask mask, int degrees) =>
        MaskResource(mask, degrees) is { } name
            ? typeof(ThermalrightFrames).Assembly.GetManifestResourceStream(MaskPrefix + (degrees is 90 or 270 ? name.Replace(".png", "_clipped.png") : name))
            : null;
}
