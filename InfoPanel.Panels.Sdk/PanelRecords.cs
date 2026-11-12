using System;
using System.Collections.Generic;
using System.Text.Json;

namespace InfoPanel.Panels.Sdk;

/// <summary>What a driver's call came to. They map one to one to the reasons the engine gives for a panel that isn't running.</summary>
public enum PanelDeviceStatus
{
    Ok,
    /// <summary>Another program (or another engine) holds the device.</summary>
    Busy,
    /// <summary>The OS won't let the engine open the device (permissions, a driver in the way).</summary>
    Access,
    /// <summary>The device went away (unplugged).</summary>
    Gone,
    /// <summary>The device, or its OS driver, can't do what was asked.</summary>
    NotSupported,
    Timeout,
    /// <summary>Any other failure of a transfer or a call.</summary>
    Io,
}

/// <summary>How a matched device is reached: USB transfers, a USB CDC serial port, or HID reports.</summary>
public enum PanelTransport { Usb, Serial, Hid }

/// <summary>A device a driver may drive: its USB vendor and product id, with the interface and transport it uses.</summary>
public sealed record PanelMatch(ushort VendorId, ushort ProductId, int? Interface = null, PanelTransport Transport = PanelTransport.Usb);

/// <summary>
/// A listed USB device offered to a driver, as the OS has it cached (no I/O to it): its ids, its bus and address (which
/// change when it is plugged in again), and its port path (the bus and port chain, <c>1-2.3</c>, the same while it stays
/// in one port).
/// </summary>
public sealed record PanelCandidate(ushort VendorId, ushort ProductId, byte Bus, byte Address, string PortPath);

/// <summary>
/// A probe's answer: <see cref="Mine"/> with the panel's <see cref="Identity"/>, or not mine; or, with a status other
/// than <see cref="PanelDeviceStatus.Ok"/>, the device couldn't be asked now. <see cref="Message"/>: why, for the log.
/// </summary>
public sealed record PanelProbe(bool Mine, PanelIdentity? Identity, PanelDeviceStatus Status, string? Message = null);

/// <summary>
/// Which panel a probe found: its model's id in the family (stored with the panel), its size in pixels in its own
/// orientation, its serial when it has one that tells it from others of its model, and its firmware's version.
/// </summary>
public sealed record PanelIdentity(string Model, int Width, int Height, string? Serial, string? Firmware);

/// <summary>
/// How the engine asks a session to start: the <see cref="Brightness"/> (0–100), the <see cref="Rotation"/> (degrees
/// clockwise: 0, 90, 180 or 270; for a driver that turns frames itself, <see cref="PanelCapabilities.RotatesItself"/>),
/// the <see cref="Format"/> the panel's settings ask for (null: the driver's own choice) and the family's settings.
/// </summary>
public sealed record PanelOpenOptions(int Brightness, int Rotation = 0, PanelFrameFormat? Format = null, JsonElement FamilySettings = default)
{
    /// <summary>
    /// Told at once, on the opening thread, when the device doesn't take the start of the <see cref="Format"/> asked for,
    /// whatever the start then comes to (a session in another format, a failure, a cancellation): the engine doesn't ask
    /// for it again on that panel until it restarts.
    /// </summary>
    public Action<PanelDeviceStatus>? FormatRefused { get; init; }
}

/// <summary>What <see cref="IPanelDriver.Open"/> came to: a running session, or the status and why not.</summary>
public sealed record PanelOpen(IPanelSession? Session, PanelDeviceStatus Status, string Message);

/// <summary>A range of a device's own values, both ends included.</summary>
public sealed record PanelRange(int Min, int Max);

/// <summary>The pixels a panel takes.</summary>
public enum PanelFrameFormat
{
    /// <summary>One JPEG a frame, encoded by the engine at <see cref="PanelCapabilities.JpegQuality"/>.</summary>
    Jpeg,
    /// <summary>16 bits a pixel, little-endian, red in the top 5 bits, then 6 of green and 5 of blue.</summary>
    Rgb565Le,
    /// <summary>As <see cref="Rgb565Le"/>, big-endian.</summary>
    Rgb565Be,
    /// <summary>Three bytes a pixel: red, green, blue.</summary>
    Rgb888,
    /// <summary>Three bytes a pixel: blue, green, red.</summary>
    Bgr888,
    /// <summary>Four bytes a pixel, opaque: red, green, blue, then 255.</summary>
    Rgba8888,
    /// <summary>
    /// As <see cref="Rgb565Le"/>, each channel's low bits dropped with no rounding or dithering, as
    /// <see cref="Rgb565Be"/> is (<see cref="Rgb565Le"/> itself is the engine's rounded conversion).
    /// </summary>
    Rgb565LeTruncated,
}

/// <summary>
/// What a panel takes, fixed for a session's life: its size in pixels in its own orientation; its pixel formats, the
/// driver's preference first (the engine writes the first it produces); the most frames a second it is sent (1–60);
/// the most bytes a frame may be (a JPEG over it is encoded again at a lower quality, down to <see cref="MinJpegQuality"/>);
/// the JPEG quality; whether it turns frames itself (else the engine does); its brightness range (null: none); how often
/// the last frame is written again without a new one (null: after 1 s); and whether a write returns only once the frame
/// is shown. Then, as <c>init</c> properties (the positional parameters stay the SDK's first ones, so a driver built
/// against them keeps running): whether the engine dims the frame itself (<see cref="DimInSoftware"/>, for a panel
/// without a light of its own: <see cref="Brightness"/> stays null), the PNG overlays drawn over the turned frame before
/// it is encoded (<see cref="Overlays"/>, by rotation: 0, 90, 180 or 270; each the frame's size as turned, its alpha
/// kept), the part of the frame that is sent (<see cref="Crop"/>, null: all of it), and how much lower each new try at a
/// JPEG over <see cref="MaxFrameBytes"/> goes (<see cref="JpegQualityStep"/>).
/// </summary>
public sealed record PanelCapabilities(
    int Width,
    int Height,
    IReadOnlyList<PanelFrameFormat> Formats,
    int MaxFps,
    int? MaxFrameBytes = null,
    int JpegQuality = 90,
    int MinJpegQuality = 30,
    bool RotatesItself = false,
    PanelRange? Brightness = null,
    TimeSpan? KeepAliveEvery = null,
    bool WriteWaitsForDisplay = false)
{
    // Never a positional parameter: a new constructor or Deconstruct signature breaks every driver built before it.

    /// <summary>Whether the engine dims the frame itself, for a panel without a light of its own (<see cref="Brightness"/> stays null).</summary>
    public bool DimInSoftware { get; init; }

    /// <summary>The PNG overlays drawn over the turned frame before it is encoded, by rotation (0, 90, 180 or 270); each the frame's size as turned, its alpha kept. Null: none.</summary>
    public IReadOnlyDictionary<int, byte[]>? Overlays { get; init; }

    /// <summary>The part of the frame that is sent; null: all of it.</summary>
    public PanelCrop? Crop { get; init; }

    /// <summary>How much lower each new try at a JPEG over <see cref="MaxFrameBytes"/> goes (5 by default).</summary>
    public int JpegQualityStep { get; init; } = 5;
}

/// <summary>
/// The part of a frame that is sent: <see cref="Width"/> × <see cref="Height"/> pixels from (<see cref="X"/>,
/// <see cref="Y"/>) of the <see cref="PanelCapabilities.Width"/> × <see cref="PanelCapabilities.Height"/> frame, as
/// turned (a panel rendered at 480 rows and sent its top 462, say).
/// </summary>
public sealed record PanelCrop(int X, int Y, int Width, int Height);

/// <summary>
/// One frame for <see cref="IPanelSession.WriteFrame"/>, a view of the engine's buffer (no copy, valid only during the
/// call): its bytes in <see cref="Format"/>, the engine's frame id, and whether it is the last frame written again
/// (a keep-alive) rather than a new one.
/// </summary>
public readonly ref struct PanelFrameView
{
    public PanelFrameView(ReadOnlySpan<byte> bytes, PanelFrameFormat format, long frameId, bool repeat)
    {
        Bytes = bytes;
        Format = format;
        FrameId = frameId;
        Repeat = repeat;
    }

    public ReadOnlySpan<byte> Bytes { get; }

    public PanelFrameFormat Format { get; }

    public long FrameId { get; }

    public bool Repeat { get; }
}

/// <summary>
/// What a frame's write came to: its status, the <see cref="DeviceTime"/> the transfer alone took (measured by the
/// driver around its write, which the engine paces the panel by), and the frames the device reports it holds not yet
/// shown, when it says.
/// </summary>
public sealed record PanelWrite(PanelDeviceStatus Status, TimeSpan DeviceTime, int? Queued = null);
