using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using InfoPanel.Panels.Sdk;
using Serilog;
using SkiaSharp;

namespace InfoPanel.Panels;

/// <summary>
/// A session's frames as its panel takes them, from its <see cref="PanelCapabilities"/>, in 1.4.x's order
/// (<c>ThermalrightPanelDeviceTask</c>'s): the profile's frame turned and fitted to the panel (<see cref="PanelTransform.Render"/>),
/// dimmed when the panel has no light of its own (<see cref="PanelCapabilities.DimInSoftware"/>, below 100 %), the
/// overlay for its rotation drawn over it (<see cref="PanelCapabilities.Overlays"/>), cropped
/// (<see cref="PanelCapabilities.Crop"/>), then encoded: a JPEG within <see cref="PanelCapabilities.MaxFrameBytes"/>
/// (encoded again <see cref="PanelCapabilities.JpegQualityStep"/> lower down to <see cref="PanelCapabilities.MinJpegQuality"/>,
/// logged once a session; one still over it at the floor isn't sent), or a raw format (<see cref="PanelTransform.Convert"/>). A raw RGB565 (little-endian) frame with
/// none of those is drawn straight into its buffer, as BeadaPanel's always was. One writer a session, on the panel's
/// thread; <see cref="Frame"/> holds the last frame written until the next, for the keep-alive.
/// </summary>
public sealed class PanelFrameWriter : IDisposable
{
    private readonly PanelCapabilities _capabilities;
    private readonly Dictionary<int, SKImage> _overlays = [];
    private readonly ILogger _logger;
    private readonly string _label;
    private readonly bool _direct;
    private readonly byte[] _rgba;
    private readonly byte[]? _cropped;
    private byte[] _output;
    private byte[] _encoded = []; // a JPEG's encode, which becomes the frame once it fits
    private int _length;
    private bool _steppedLogged;
    private bool _overLogged;

    /// <summary>A writer for a session whose panel takes <paramref name="format"/>.</summary>
    /// <exception cref="ArgumentException">The capabilities can't be written (<see cref="Problem"/>).</exception>
    public PanelFrameWriter(PanelCapabilities capabilities, PanelFrameFormat format, ILogger logger, string label)
    {
        if (Problem(capabilities) is { } problem)
            throw new ArgumentException(problem, nameof(capabilities));
        _capabilities = capabilities;
        Format = format;
        _logger = logger;
        _label = label;
        try
        {
            foreach (var (rotation, png) in capabilities.Overlays ?? new Dictionary<int, byte[]>())
                _overlays[rotation] = SKImage.FromEncodedData(png) ?? throw new ArgumentException($"the overlay for {rotation}° isn't a picture Skia reads", nameof(capabilities));
        }
        catch
        {
            Dispose();
            throw;
        }

        var (width, height) = (capabilities.Width, capabilities.Height);
        _direct = format == PanelFrameFormat.Rgb565Le && !capabilities.DimInSoftware && _overlays.Count == 0 && capabilities.Crop == null;
        (Width, Height) = capabilities.Crop is { } crop ? (crop.Width, crop.Height) : (width, height);
        _rgba = _direct ? [] : new byte[PanelTransform.TargetLength(width, height, PanelPixelFormat.Rgba8888)];
        _cropped = capabilities.Crop != null ? new byte[PanelTransform.TargetLength(Width, Height, PanelPixelFormat.Rgba8888)] : null;
        _output = format == PanelFrameFormat.Jpeg ? [] : new byte[PanelTransform.FrameLength(Width, Height, format)];
    }

    /// <summary>The most bytes an overlay may take decoded (four a pixel).</summary>
    public const long MaxOverlayBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Why a session with <paramref name="capabilities"/> can't be written, or null when it can: a crop outside the frame,
    /// an overlay for a rotation that isn't 0, 90, 180 or 270, one that isn't a PNG, one whose header declares a size other
    /// than the panel's (the frame it is drawn on is the panel's own at every rotation: the rotation turns the picture, not
    /// the frame) or more than <see cref="MaxOverlayBytes"/> decoded, all read from its header before anything decodes it,
    /// or a JPEG step under 1.
    /// </summary>
    public static string? Problem(PanelCapabilities capabilities)
    {
        if (capabilities.Crop is { } crop && PanelTransform.CropProblem(crop, capabilities.Width, capabilities.Height) is { } cropProblem)
            return cropProblem;
        foreach (var rotation in capabilities.Overlays?.Keys ?? [])
            if (rotation is not (0 or 90 or 180 or 270))
                return $"an overlay is for {rotation}°; a rotation is 0, 90, 180 or 270";
        foreach (var (rotation, png) in capabilities.Overlays ?? new Dictionary<int, byte[]>())
        {
            if (png is not { Length: > 0 })
                return "an overlay has no bytes";
            if (PngSize(png) is not var (width, height))
                return $"the overlay for {rotation}° isn't a PNG";
            if ((long)width * height * 4 > MaxOverlayBytes)
                return $"the overlay for {rotation}° is {width} x {height}, over {MaxOverlayBytes} bytes decoded";
            if (width != capabilities.Width || height != capabilities.Height)
                return $"the overlay for {rotation}° is {width} x {height}; the panel's frame is {capabilities.Width} x {capabilities.Height}";
        }
        return capabilities.JpegQualityStep < 1 ? $"the JPEG step is {capabilities.JpegQualityStep}; it is at least 1" : null;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The size a PNG's header (its first chunk, <c>IHDR</c>) declares; null when it doesn't start as a PNG does.</summary>
    public static (int Width, int Height)? PngSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || !png[..8].SequenceEqual(PngSignature) || BinaryPrimitives.ReadUInt32BigEndian(png[8..]) != 13 || !png[12..16].SequenceEqual("IHDR"u8))
            return null;
        var (width, height) = (BinaryPrimitives.ReadUInt32BigEndian(png[16..]), BinaryPrimitives.ReadUInt32BigEndian(png[20..]));
        return width is >= 1 and <= int.MaxValue && height is >= 1 and <= int.MaxValue ? ((int)width, (int)height) : null;
    }

    /// <summary>The format written.</summary>
    public PanelFrameFormat Format { get; }

    /// <summary>The frame's size as sent (the crop's, else the panel's).</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>The last frame written (empty before the first).</summary>
    public ReadOnlySpan<byte> Frame => _output.AsSpan(0, _length);

    /// <summary>The quality the last JPEG took (0 before the first, and on a raw stream).</summary>
    public int Quality { get; private set; }

    /// <summary>
    /// Writes the profile's frame <paramref name="source"/> (RGBA, premultiplied) turned by <paramref name="rotation"/>, at
    /// <paramref name="brightness"/> (0–100, applied only when the panel dims in software), into <see cref="Frame"/>.
    /// </summary>
    public bool Write(SKPixmap source, PanelRotation rotation, int brightness)
    {
        Prepare(source, rotation, brightness);
        return Encode();
    }

    /// <summary><see cref="Write"/>'s first part: the frame turned, dimmed, overlaid and cropped, not yet encoded (a raw RGB565 frame with none of those is written whole).</summary>
    public void Prepare(SKPixmap source, PanelRotation rotation, int brightness)
    {
        var (width, height) = (_capabilities.Width, _capabilities.Height);
        if (_direct)
        {
            PanelTransform.Render(source, _output, width, height, rotation, PanelPixelFormat.Rgb565Le);
            _length = _output.Length;
            return;
        }

        PanelTransform.Render(source, _rgba, width, height, rotation, PanelPixelFormat.Rgba8888);
        if (_capabilities.DimInSoftware)
            PanelTransform.Dim(_rgba, width, height, brightness);
        if (_overlays.TryGetValue((int)rotation, out var overlay))
            PanelTransform.Overlay(_rgba, width, height, overlay);
        if (_capabilities.Crop is { } crop)
            PanelTransform.Crop(_rgba, width, height, crop, _cropped!);
    }

    /// <summary>
    /// <see cref="Write"/>'s second part: the prepared frame encoded into <see cref="Frame"/> (a JPEG, or a raw format).
    /// False when it isn't: a JPEG still over <see cref="PanelCapabilities.MaxFrameBytes"/> at
    /// <see cref="PanelCapabilities.MinJpegQuality"/>, which the driver never gets (a hosted session's slot holds no more),
    /// <see cref="Frame"/> keeping the last frame that fitted (logged once a session).
    /// </summary>
    public bool Encode()
    {
        if (_direct)
            return true;
        var frame = _cropped ?? _rgba;
        if (Format == PanelFrameFormat.Jpeg)
        {
            var length = PanelJpeg.EncodeWithin(frame, Width, Height, _capabilities.JpegQuality, _capabilities.MinJpegQuality,
                _capabilities.JpegQualityStep, _capabilities.MaxFrameBytes, ref _encoded, out var used);
            Quality = used;
            if (used != _capabilities.JpegQuality && !_steppedLogged)
            {
                _steppedLogged = true;
                _logger.Information("Panel {Panel}: a frame over {Max} bytes was encoded again at q{Quality} ({Length} bytes), {Step} lower a try down to q{Min} (logged once a session)",
                    _label, _capabilities.MaxFrameBytes, used, length, _capabilities.JpegQualityStep, _capabilities.MinJpegQuality);
            }
            if (_capabilities.MaxFrameBytes is { } max && length > max)
            {
                if (!_overLogged)
                {
                    _overLogged = true;
                    _logger.Information("Panel {Panel}: a frame of {Length} bytes at q{Quality}, its floor, is over the panel's {Max}: it isn't sent, and the panel keeps its last frame (logged once a session)",
                        _label, length, used, max);
                }
                return false;
            }
            // The encode's buffer becomes the frame; the old frame's, the next encode's.
            (_output, _encoded) = (_encoded, _output);
            _length = length;
            return true;
        }

        PanelTransform.Convert(frame, Width, Height, Format, _output);
        _length = _output.Length;
        return true;
    }

    public void Dispose()
    {
        foreach (var overlay in _overlays.Values)
            overlay.Dispose();
        _overlays.Clear();
    }
}
