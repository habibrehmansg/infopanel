using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Golden;

internal static class TestPaths
{
    /// <summary>The test project's source folder (where goldens and fixtures are committed), found from the output folder.</summary>
    public static string ProjectDirectory { get; } = FindProjectDirectory();

    private static string FindProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InfoPanel.Core.Tests.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"InfoPanel.Core.Tests.csproj not found above {AppContext.BaseDirectory}");
    }
}

/// <summary>
/// Compares a rendered frame with its golden PNG in <c>Golden/{os}/</c>.
/// A pixel differs when any channel (RGBA, unpremultiplied) is more than <see cref="ChannelTolerance"/> apart;
/// at most <see cref="DifferingPixelFraction"/> of the pixels may differ. On failure the frame and a diff image
/// are written to <c>TestResults/golden/</c>. With <c>UPDATE_GOLDENS=1</c> a missing or failing golden is
/// (re)written instead, and one within tolerance is left untouched. Without a folder for the current OS the goldens
/// fail on macOS (the reference OS, whose goldens are committed) and are skipped on other OSes.
/// </summary>
/// <summary>Pixels that differ by more than <see cref="GoldenImage.ChannelTolerance"/>, of <see cref="Total"/>, and the largest channel difference.</summary>
internal readonly record struct ImageDiff(int MaxDelta, int Differing, int Total)
{
    public double Fraction => (double)Differing / Total;

    public override string ToString() => $"max channel diff {MaxDelta}, {Differing} of {Total} pixels ({100.0 * Fraction:0.###}%) differ by more than {GoldenImage.ChannelTolerance}";
}

internal static class GoldenImage
{
    public const int ChannelTolerance = 2;
    public const double DifferingPixelFraction = 0.0005;

    public static string OsFolder { get; } =
        OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "other";

    public static string GoldenDirectory { get; } = Path.Combine(TestPaths.ProjectDirectory, "Golden", OsFolder);

    public static string ResultsDirectory { get; } = Path.Combine(TestPaths.ProjectDirectory, "TestResults", "golden");

    private static bool Update => Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1";

    /// <summary>
    /// Compares with <c>Golden/{os}/{name}.png</c>, or <c>Golden/{folder}/{os}/{name}.png</c> when
    /// <paramref name="folder"/> is given (the scene runtime's goldens live in <c>Golden/scene/</c>).
    /// </summary>
    public static void Verify(string name, SKBitmap rendered, string? folder = null)
    {
        var actualPng = EncodePng(rendered);
        var directory = folder is null ? GoldenDirectory : Path.Combine(TestPaths.ProjectDirectory, "Golden", folder, OsFolder);
        var shown = folder is null ? $"Golden/{OsFolder}/" : $"Golden/{folder}/{OsFolder}/";
        var goldenPath = Path.Combine(directory, name + ".png");
        if (folder is not null) name = folder + "-" + name;

        if (Update)
        {
            if (!File.Exists(goldenPath) || Compare(File.ReadAllBytes(goldenPath), actualPng, out _) is not null)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(goldenPath, actualPng);
            }

            return;
        }

        RequireGoldenDirectory(directory);

        if (!File.Exists(goldenPath))
        {
            WriteResults(name, actualPng, diff: null);
            Assert.Fail($"Golden {Path.GetFileName(goldenPath)} is missing from {shown}. Create it with UPDATE_GOLDENS=1 "
                + $"(the rendered frame is in TestResults/golden/{name}.actual.png).");
        }

        var failure = Compare(File.ReadAllBytes(goldenPath), actualPng, out var diffPng);
        if (failure is not null)
        {
            WriteResults(name, actualPng, diffPng);
            Assert.Fail($"{name}: {failure} See TestResults/golden/{name}.actual.png and {name}.diff.png.");
        }
    }

    /// <summary>Fails on macOS and skips elsewhere when <paramref name="directory"/> (the current OS's goldens) is missing.</summary>
    internal static void RequireGoldenDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            // macOS is the reference OS: its goldens are committed, so a missing folder means verification was lost.
            if (OperatingSystem.IsMacOS())
            {
                Assert.Fail($"Golden/{OsFolder}/ is missing. The macOS goldens are the reference set and must be committed; "
                    + "restore them from git, or recreate them with UPDATE_GOLDENS=1 dotnet test InfoPanel.slnx --filter Category=Golden and review them.");
            }

            Assert.Skip($"No goldens for this OS (Golden/{OsFolder}/ is missing). Create them with "
                + "UPDATE_GOLDENS=1 dotnet test InfoPanel.slnx --filter Category=Golden, review the PNGs and commit them.");
        }
    }

    public static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// How far <paramref name="actual"/> is from the PNG at <paramref name="goldenPath"/>, by <see cref="Verify"/>'s per-pixel
    /// rule; pixels inside <paramref name="exclude"/> (frame pixels) are left out of the count, not of the total.
    /// </summary>
    public static ImageDiff Measure(string goldenPath, SKBitmap actual, IReadOnlyList<SKRectI>? exclude = null)
    {
        using var golden = Decode(File.ReadAllBytes(goldenPath));
        using var rendered = Decode(EncodePng(actual));
        if (golden.Width != rendered.Width || golden.Height != rendered.Height)
        {
            throw new InvalidOperationException($"size {rendered.Width}×{rendered.Height} differs from the golden's {golden.Width}×{golden.Height}");
        }

        var expected = golden.GetPixelSpan();
        var pixels = rendered.GetPixelSpan();
        int differing = 0, maxDelta = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var delta = 0;
            for (var c = 0; c < 4; c++) delta = Math.Max(delta, Math.Abs(pixels[i + c] - expected[i + c]));
            if (exclude is not null)
            {
                var x = i / 4 % rendered.Width;
                var y = i / 4 / rendered.Width;
                if (exclude.Any(r => r.Contains(x, y))) continue;
            }

            maxDelta = Math.Max(maxDelta, delta);
            if (delta > ChannelTolerance) differing++;
        }

        return new ImageDiff(maxDelta, differing, rendered.Width * rendered.Height);
    }

    /// <summary>A PNG file's pixels as <see cref="Measure"/> reads them: RGBA, unpremultiplied.</summary>
    public static SKBitmap Load(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>A frame's pixels as <see cref="Measure"/> reads them: RGBA, unpremultiplied.</summary>
    public static SKBitmap Normalize(SKBitmap bitmap) => Decode(EncodePng(bitmap));

    /// <summary>Writes the frame and a diff against the golden to <c>TestResults/golden/</c> (for a case compared with <see cref="Measure"/>).</summary>
    public static void WriteComparison(string name, string goldenPath, SKBitmap actual)
    {
        var png = EncodePng(actual);
        Compare(File.ReadAllBytes(goldenPath), png, out var diff);
        WriteResults(name, png, diff);
    }

    /// <summary>Null when the images match within tolerance; otherwise why not, with a diff image when the sizes match.</summary>
    private static string? Compare(byte[] goldenPng, byte[] actualPng, out byte[]? diffPng)
    {
        diffPng = null;

        using var golden = Decode(goldenPng);
        using var actual = Decode(actualPng);

        if (golden.Width != actual.Width || golden.Height != actual.Height)
        {
            return $"size {actual.Width}×{actual.Height} differs from the golden's {golden.Width}×{golden.Height}.";
        }

        var expected = golden.GetPixelSpan();
        var rendered = actual.GetPixelSpan();

        using var diff = new SKBitmap(new SKImageInfo(actual.Width, actual.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var diffPixels = diff.GetPixelSpan();

        var differing = 0;
        var maxDelta = 0;

        for (var i = 0; i < rendered.Length; i += 4)
        {
            var delta = 0;
            for (var c = 0; c < 4; c++)
            {
                delta = Math.Max(delta, Math.Abs(rendered[i + c] - expected[i + c]));
            }

            maxDelta = Math.Max(maxDelta, delta);

            if (delta > ChannelTolerance)
            {
                differing++;
                diffPixels[i] = 255;
                diffPixels[i + 1] = 0;
                diffPixels[i + 2] = 0;
            }
            else
            {
                // Faded greyscale of the frame, so the red differences stand out.
                var grey = (byte)(40 + (rendered[i] + rendered[i + 1] + rendered[i + 2]) / 3 * 60 / 255);
                diffPixels[i] = grey;
                diffPixels[i + 1] = grey;
                diffPixels[i + 2] = grey;
            }

            diffPixels[i + 3] = 255;
        }

        var total = actual.Width * actual.Height;
        if (differing <= total * DifferingPixelFraction)
        {
            return null;
        }

        diffPng = EncodePng(diff);
        return $"{differing} of {total} pixels ({100.0 * differing / total:0.###}%) differ by more than {ChannelTolerance} "
            + $"(largest channel difference {maxDelta}; at most {DifferingPixelFraction * 100}% may).";
    }

    private static SKBitmap Decode(byte[] png)
    {
        using var codec = SKCodec.Create(new MemoryStream(png)) ?? throw new InvalidDataException("Not a PNG.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new InvalidDataException($"PNG decode failed: {result}");
        }

        return bitmap;
    }

    private static void WriteResults(string name, byte[] actualPng, byte[]? diff)
    {
        Directory.CreateDirectory(ResultsDirectory);
        File.WriteAllBytes(Path.Combine(ResultsDirectory, name + ".actual.png"), actualPng);
        if (diff != null)
        {
            File.WriteAllBytes(Path.Combine(ResultsDirectory, name + ".diff.png"), diff);
        }
    }
}
