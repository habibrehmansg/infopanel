using InfoPanel.Core.Tests.Golden;
using SkiaSharp;

namespace InfoPanel.Core.Tests;

/// <summary>
/// The video fixtures in Fixtures/video/, made by native/ffmpeg/make-fixtures.sh: 64 x 48, 10 frames at 10 fps, frame i
/// one solid colour (<see cref="Colour"/>), so the colour drawn says which frame was shown.
/// </summary>
internal static class VideoFixtures
{
    public static string Directory => Path.Combine(TestPaths.ProjectDirectory, "Fixtures", "video");

    public static string PathOf(string name) => Path.Combine(Directory, name);

    public static byte[] Read(string name) => File.ReadAllBytes(PathOf(name));

    /// <summary>Each clip the engine decodes: its file, container format and codec.</summary>
    public static readonly (string File, string Format, string Codec)[] Clips =
    [
        ("clip-h264.mp4", "mp4", "h264"),
        ("clip-h264.mov", "mp4", "h264"),
        ("clip-hevc.mkv", "matroska", "hevc"),
        ("clip-vp8.webm", "matroska", "vp8"),
        ("clip-vp9.webm", "matroska", "vp9"),
        ("clip-av1.webm", "matroska", "av1"),
        ("clip-mpeg4.avi", "avi", "mpeg4"),
        ("clip-mjpeg.avi", "avi", "mjpeg"),
        ("clip-prores.mov", "mp4", "prores"),
    ];

    /// <summary>Clips in a container the probe reads, with a codec outside the list.</summary>
    public static readonly string[] OutsideCodecs = ["outside-mpeg2.mkv", "outside-qtrle.mov", "outside-huffyuv.avi"];

    public const int Frames = 10;
    public const int Width = 64;
    public const int Height = 48;

    /// <summary>Frame <paramref name="i"/>'s colour, as make-fixtures.sh draws it.</summary>
    public static SKColor Colour(int i) => new((byte)(25 * i), (byte)(250 - 25 * i), (byte)(i % 2 == 0 ? 40 : 210));

    /// <summary>The frame whose colour is nearest <paramref name="c"/>.</summary>
    public static int FrameOf(SKColor c)
    {
        var best = -1;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < Frames; i++)
        {
            var f = Colour(i);
            int dr = f.Red - c.Red, dg = f.Green - c.Green, db = f.Blue - c.Blue;
            var d = dr * dr + dg * dg + db * db;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }
}
