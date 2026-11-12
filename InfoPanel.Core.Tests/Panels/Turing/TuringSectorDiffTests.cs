using InfoPanel.Core.Tests.Panels.Turing.V1Oracle;
using InfoPanel.Panels.Turing;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Turing;

/// <summary>
/// The changed sectors (§2.2 (1)) against 1.4.x's <c>Extensions/SKBitmapComparison.cs</c>
/// (<c>V1Oracle/SKBitmapComparison.cs</c>), with each revision's sector size, merge limit and cap as
/// <c>Services/TuringPanelSerialTask.cs:43-72</c> set them. 1.4.x compared the rendered bitmaps; the driver compares the
/// frames it is sent, which change where the bitmaps do (each picture here changes in every channel's kept bits).
/// </summary>
public sealed class TuringSectorDiffTests
{
    public static TheoryData<char, int, int> Revisions() => new()
    {
        { 'A', 320, 480 },
        { 'C', 800, 480 },
        { 'E', 480, 1920 },
        { 'E', 480, 480 },
    };

    [Fact]
    public void TheRevisions_Are1_4s()
    {
        // TuringPanelSerialTask.cs:46-51 (A: 20 x 20 sectors merged up to 40 x 40, at most 76), :54-59 (C: 20 x 20 up to
        // 120 x 80, at most 30), :62-67 (E: 32 x 32 up to 128 x 96, at most 38).
        Assert.Equal((20, 20, 40, 40, 76), Caps(SerialRevision.A));
        Assert.Equal((20, 20, 120, 80, 30), Caps(SerialRevision.C));
        Assert.Equal((32, 32, 128, 96, 38), Caps(SerialRevision.E));
    }

    private static (int, int, int, int, int) Caps(SerialRevision r) => (r.SectorWidth, r.SectorHeight, r.MaxSectorWidth, r.MaxSectorHeight, r.MaxSectors);

    /// <summary>A change: the pixels a rule picks take new colours (each channel moved by 64, so every kept bit changes).</summary>
    private static (SKBitmap, byte[], byte[]) Changed((SKBitmap Bitmap, byte[], byte[]) from, Func<int, int, bool> change)
    {
        var w = from.Bitmap.Width;
        var h = from.Bitmap.Height;
        var copy = from.Bitmap.Copy();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (change(x, y))
                {
                    var c = copy.GetPixel(x, y);
                    copy.SetPixel(x, y, new SKColor((byte)(c.Red ^ 0x40), (byte)(c.Green ^ 0x40), (byte)(c.Blue ^ 0x40)));
                }
        return TuringSerialProtocolTests.Picture(w, h, 0, (x, y) => true, (copy, [], []));
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var (letter, w, h) in new[] { ('A', 320, 480), ('C', 800, 480), ('E', 480, 1920), ('E', 480, 480) })
        {
            yield return [letter, w, h, "none"];
            yield return [letter, w, h, "one pixel"];
            yield return [letter, w, h, "a clock's digits"];
            yield return [letter, w, h, "scattered"];
            yield return [letter, w, h, "a band"];
            yield return [letter, w, h, "everything"];
        }
    }

    private static Func<int, int, bool> Rule(string name, int w, int h) => name switch
    {
        "none" => (_, _) => false,
        "one pixel" => (x, y) => x == w / 2 && y == h / 3,
        "a clock's digits" => (x, y) => x >= 13 && x < 97 && y >= 31 && y < 70,
        "scattered" => (x, y) => (x * 7 + y * 13) % 997 == 0,
        "a band" => (x, y) => y >= h / 2 && y < (h / 2) + 45,
        _ => (_, _) => true,
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheChangedSectors_AreGetChangedSectors(char letter, int width, int height, string change)
    {
        var revision = SerialRevision.Of(letter.ToString())!;
        var before = TuringSerialProtocolTests.Picture(width, height, 5);
        var after = Changed(before, Rule(change, width, height));

        var expected = V1SKBitmapComparison.GetChangedSectors(before.Item1, after.Item1, revision.SectorWidth, revision.SectorHeight, revision.MaxSectorWidth, revision.MaxSectorHeight)
            .Select(r => (r.Left, r.Top, r.Right, r.Bottom)).ToList();
        var (sent, frame) = letter == 'A' ? (before.Item2, after.Item2) : (before.Item3, after.Item3);
        var sectors = SectorDiff.Changed(sent, frame, width, height, revision.BytesPerPixel, revision.SectorWidth, revision.SectorHeight, revision.MaxSectorWidth, revision.MaxSectorHeight)
            .Select(s => (s.Left, s.Top, s.Right, s.Bottom)).ToList();

        Assert.Equal(expected, sectors);
        if (change == "none")
            Assert.Empty(sectors);
    }

    [Fact]
    public void Merging_IsCombineRectangles()
    {
        // CombineRectangles (:168-212): sorted by top then left, each merged with every later neighbour (sharing an
        // edge or a corner, or overlapping) while the union stays within the limit.
        var before = TuringSerialProtocolTests.Picture(320, 480, 6);
        var after = Changed(before, (x, y) => (x / 20 + y / 20) % 3 == 0 && y < 200);

        var expected = V1SKBitmapComparison.GetChangedSectors(before.Item1, after.Item1, 20, 20, 40, 40).Select(r => (r.Left, r.Top, r.Right, r.Bottom));
        var sectors = SectorDiff.Changed(before.Item2, after.Item2, 320, 480, 2, 20, 20, 40, 40).Select(s => (s.Left, s.Top, s.Right, s.Bottom));

        Assert.Equal(expected, sectors);
    }
}
