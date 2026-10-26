using System;

namespace InfoPanel.Output;

/// <summary>How a requested scale becomes the scale a render loop draws at, and the frame's pixel size.</summary>
public static class FrameScale
{
    public const float Step = 0.25f;
    public const float Min = 0.1f;
    public const float Max = 4f;

    /// <summary>The most pixels one frame may have: 16 Mpx (16 × 2^20), 64 MB of RGBA.</summary>
    public const long MaxPixels = 16L * 1024 * 1024;

    /// <summary>
    /// The scale drawn for <paramref name="requested"/>, or null when the profile can't be drawn within
    /// <see cref="MaxPixels"/> at all. Rounded to the nearest <see cref="Step"/>, then clamped to
    /// <see cref="Min"/>–<see cref="Max"/> (so 0.1 and 0.12 draw at 0.1, 0.2 at 0.25); not finite or not positive
    /// means 1. Then reduced until the frame has at most <see cref="MaxPixels"/>: to the largest multiple of
    /// <see cref="Step"/> that fits, or, below 0.25, the largest hundredth down to <see cref="Min"/> that fits. The cap
    /// always wins over <see cref="Min"/>: a profile too large for 0.1 (only one over <c>Workspace.MaxDimension</c>)
    /// gets the largest scale under 0.1 that fits, and null if at that scale a side of the profile would be under
    /// 1 pixel (an extreme aspect ratio).
    /// </summary>
    public static float? Normalize(float requested, int profileWidth, int profileHeight)
    {
        var scale = float.IsFinite(requested) && requested > 0 ? requested : 1f;
        scale = Math.Clamp(MathF.Round(scale / Step, MidpointRounding.AwayFromZero) * Step, Min, Max);

        if (Fits(scale, profileWidth, profileHeight))
            return scale;

        for (var steps = (int)MathF.Floor(scale / Step); steps >= 1; steps--)
        {
            if (Fits(steps * Step, profileWidth, profileHeight))
                return steps * Step;
        }

        for (var hundredths = 24; hundredths >= 10; hundredths--)
        {
            if (Fits(hundredths / 100f, profileWidth, profileHeight))
                return hundredths / 100f;
        }

        // Below Min: start from the area bound and shrink 1 % at a time until the frame fits.
        double width = Math.Max(1, profileWidth);
        double height = Math.Max(1, profileHeight);
        var small = (float)Math.Min(Min, Math.Sqrt(MaxPixels / (width * height)));
        for (var i = 0; i < 4096 && small > 0; i++, small *= 0.99f)
        {
            if (Fits(small, profileWidth, profileHeight))
                return width * small >= 1 && height * small >= 1 ? small : null;
        }

        return null;
    }

    /// <summary>
    /// The pixel size of a frame drawn at <paramref name="scale"/>: each side rounded, at least 1. Call it with a
    /// scale from <see cref="Normalize"/>, which keeps both sides (and their product) in range.
    /// </summary>
    public static (int Width, int Height) PixelSize(float scale, int profileWidth, int profileHeight)
    {
        var (width, height) = Sides(scale, profileWidth, profileHeight);
        return ((int)Math.Min(width, int.MaxValue), (int)Math.Min(height, int.MaxValue));
    }

    /// <summary>Both sides in double, so a huge profile can never overflow into a size that seems to fit.</summary>
    private static (double Width, double Height) Sides(float scale, int profileWidth, int profileHeight) =>
        (Math.Max(1, Math.Round(Math.Max(1, profileWidth) * (double)scale)),
         Math.Max(1, Math.Round(Math.Max(1, profileHeight) * (double)scale)));

    private static bool Fits(float scale, int profileWidth, int profileHeight)
    {
        var (width, height) = Sides(scale, profileWidth, profileHeight);
        return width * height <= MaxPixels;
    }
}
