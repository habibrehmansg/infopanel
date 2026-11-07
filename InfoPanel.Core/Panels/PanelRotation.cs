namespace InfoPanel.Panels;

/// <summary>How far a panel turns the frame, clockwise, before fitting it (1.4.x's LCD rotation).</summary>
public enum PanelRotation { None = 0, Rotate90 = 90, Rotate180 = 180, Rotate270 = 270 }

/// <summary>The pixels a panel takes.</summary>
public enum PanelPixelFormat
{
    /// <summary>16 bits a pixel, little-endian, red in the top 5 bits, then 6 of green and 5 of blue.</summary>
    Rgb565Le,

    /// <summary>32 bits a pixel, opaque: red, green, blue, then 255 (the frame a JPEG is encoded from).</summary>
    Rgba8888,
}
