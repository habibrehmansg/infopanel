using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using InfoPanel.Spec;
using InfoPanel.Spec.Schema;
using SkiaSharp;
using Svg.Skia;
using Xunit;

namespace InfoPanel.Core.Tests.Spec;

/// <summary>
/// An accepted SVG is rebuilt (spec/docs/svg.md, "The rebuilt document"): <see cref="MediaCheck.Sanitized"/> is a canonical
/// document regenerated from the parsed values, and it is the only form an engine decodes. These tests hold the value
/// grammar, the cost model and the rebuilt output to the review's reproductions, and the rebuilt output of normal icons to
/// the pixels of the original.
/// </summary>
public class SvgRebuildTests
{
    private const string Head = "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' width='64' height='64'>";

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static MediaCheck Accept(string svg, SpecLimits? limits = null)
    {
        var result = MediaValidator.Check(Utf8(svg), limits);
        Assert.Empty(result.Errors);
        Assert.Equal(MediaFormat.Svg, result.Format);
        Assert.NotNull(result.Sanitized);
        return result;
    }

    private static SpecError Refuse(string svg, SpecLimits? limits = null)
    {
        var result = MediaValidator.Check(Utf8(svg), limits);
        Assert.Null(result.Sanitized);
        return Assert.Single(result.Errors);
    }

    private static string Rebuilt(string svg) => Encoding.UTF8.GetString(Accept(svg).Sanitized!);

    private static SKBitmap Render(byte[] svg, int width, int height)
    {
        using var stream = new MemoryStream(svg);
        using var sk = new SKSvg();
        sk.Load(stream);
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        if (sk.Picture is { } picture) canvas.DrawPicture(picture);
        return bitmap;
    }

    // ------------------------------------------------------------ the rebuilt document

    [Fact]
    public void Rebuilt_IsCanonical()
    {
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' xmlns:s='http://www.w3.org/2000/svg' version='1.1' width='64px' height='64' xml:space='preserve'>" +
                  "<title>T &amp; t</title>" +
                  "<g transform='translate(4,4)  scale(.5)' style='FILL: hsl(120, 100%, 25%)' class='x' fill='blue'>" +
                  "<path d='m0 0 10 10 h5v-5zM20 20 30 30'/>" +
                  "<s:rect x='1e1' y='.5' width='10px' height='50%' fill='Red' fill-opacity='50%'/>" +
                  "<circle r='5' fill='rgba(0,0,255,.5)' stroke='#abc' stroke-linecap='ROUND'/>" +
                  "<polygon points='0,0 1,1,2 2'/></g></svg>";
        Assert.Equal(
            "<svg xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"64\" height=\"64\" xmlns=\"http://www.w3.org/2000/svg\">" +
            "<title>T &amp; t</title>" +
            "<g transform=\"translate(4 4) scale(0.5)\" fill=\"#008000\">" +
            "<path d=\"m0 0l10 10h5v-5zM20 20L30 30\" />" +
            "<rect x=\"10\" y=\"0.5\" width=\"10\" height=\"50%\" fill=\"#ff0000\" fill-opacity=\"0.5\" />" +
            "<circle r=\"5\" fill=\"rgba(0,0,255,0.50196)\" stroke=\"#aabbcc\" stroke-linecap=\"round\" />" +
            "<polygon points=\"0 0 1 1 2 2\" /></g></svg>",
            Rebuilt(svg));
    }

    [Fact]
    public void Rebuilt_IsAFixedPoint()
    {
        foreach (var svg in NormalIcons())
        {
            var once = Accept(svg.Text).Sanitized!;
            var twice = MediaValidator.Check(once).Sanitized!;
            Assert.Equal(Encoding.UTF8.GetString(once), Encoding.UTF8.GetString(twice));
        }
    }

    [Fact]
    public void RasterMedia_HasNoRebuiltForm()
    {
        var png = File.ReadAllBytes(Path.Combine(SpecExampleTests.SpecDir, "examples", "media", "valid", "logo.png"));
        var result = MediaValidator.Check(png);
        Assert.Empty(result.Errors);
        Assert.Equal(MediaFormat.Png, result.Format);
        Assert.Null(result.Sanitized);
    }

    /// <summary>Normal icons: gradients (inherited by href), a clip path, a mask, symbols drawn by use, transforms, style.</summary>
    public static IEnumerable<(string Name, string Text)> NormalIcons()
    {
        yield return ("gauge", MediaProbeTestsIcon);
        foreach (var file in new[] { "icon.svg", "badge.svg" })
            yield return (file, File.ReadAllText(Path.Combine(SpecExampleTests.SpecDir, "examples", "media", "valid", file)));
        yield return ("fixture icon.svg", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "assets", "icon.svg")));
    }

    /// <summary>The icon of <see cref="MediaProbeTests"/>: every element of the profile but nesting.</summary>
    private const string MediaProbeTestsIcon =
        "<?xml version='1.0' encoding='UTF-8'?>" +
        "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' version='1.1' width='48' height='48' viewBox='0 0 48 48' xml:space='preserve'>" +
        "<title>Gauge</title><desc>A badge &amp; a needle</desc>" +
        "<defs>" +
        "<linearGradient id='face' x1='0' y1='0' x2='0' y2='1'><stop offset='0' stop-color='#4f8ef7'/><stop offset='1' style='stop-color: rgb(30, 64, 175); stop-opacity: 1'/></linearGradient>" +
        "<linearGradient id='rim' xlink:href='#face' gradientTransform='rotate(90)'/>" +
        "<radialGradient id='glow' cx='0.5' cy='0.5' r='0.5' href='#face'/>" +
        "<clipPath id='round' clipPathUnits='userSpaceOnUse'><circle cx='24' cy='24' r='22'/></clipPath>" +
        "<mask id='fade'><rect width='48' height='48' fill='#fff' opacity='0.8'/></mask>" +
        "<symbol id='tick' viewBox='0 0 2 6'><rect width='2' height='6' rx='1'/></symbol>" +
        "</defs>" +
        "<g clip-path='url(#round)' class='face' transform='translate(1, 1) scale(0.95)'>" +
        "<rect width='48' height='48' fill='url(#face)' style='stroke: url( \"#rim\" ) #000; stroke-width: 2'/>" +
        "<circle cx='24' cy='24' r='10' fill=\"url('#glow')\" mask='url(#fade)'/>" +
        "</g>" +
        "<use href='#tick' x='23' y='4' width='2' height='6' fill='currentColor'/>" +
        "<use xlink:href='#tick' transform='rotate(45 24 24)'/>" +
        "<path d='M24 24 L36 30 q 2 2 4 0 A 3 3 0 0 1 44 36' stroke='#fdd835' stroke-linecap='round' fill='none'/>" +
        "<polyline points='0,0 1,1'/><polygon points='0,0 1,1 1,0'/><line x1='0' y1='0' x2='1' y2='1'/><ellipse rx='1' ry='2'/>" +
        "</svg>";

    [Fact]
    public void NormalIcons_RenderTheSameFromTheirRebuiltForm()
    {
        foreach (var (name, text) in NormalIcons())
        {
            var original = Utf8(text);
            var check = Accept(text);
            const int size = 96; // larger than the icons, so the whole picture is compared
            using var before = Render(original, size, size);
            using var after = Render(check.Sanitized!, size, size);
            int worst = 0, inked = 0;
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    SKColor a = before.GetPixel(x, y), b = after.GetPixel(x, y);
                    if (a != SKColors.White) inked++;
                    worst = Math.Max(worst, new[] { Math.Abs(a.Red - b.Red), Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue), Math.Abs(a.Alpha - b.Alpha) }.Max());
                }
            Assert.True(inked > 500, $"{name}: the original drew only {inked} pixels.");
            Assert.True(worst <= 2, $"{name}: a pixel differs by {worst}.");
        }
    }

    // ------------------------------------------------------------ 1: values are parsed, never screened

    public static TheoryData<string> BareUris() =>
    [
        "<rect width='64' height='64' fill='red' clip-path='{0}#c'/>",
        "<rect width='64' height='64' fill='red' mask='{0}#m'/>",
        "<rect width='64' height='64' fill='red' style='clip-path:{0}#c'/>",
        "<rect width='64' height='64' fill='red' style='mask: {0}#m'/>",
        "<rect width='64' height='64' fill='{0}#g'/>",
        "<rect width='64' height='64' stroke='{0}#g'/>",
        "<rect width='64' height='64' style='fill: {0}#g'/>",
        "<rect width='64' height='64' fill='url(#g) {0}#g'/>",
        "<rect width='64' height='64' fill='red' clip-path='url({0}#c)'/>",
        "<rect width='64' height='64' fill='red' clip-path='&#117;rl({0}#c)'/>",
        "<linearGradient id='g'><stop offset='0' stop-color='{0}#c'/></linearGradient>",
        "<rect width='64' height='64' color='{0}#c'/>",
        "<rect width='64' height='64' transform='{0}#c'/>",
    ];

    [Theory]
    [MemberData(nameof(BareUris))]
    public void BareUris_AreRefused_AndNothingIsFetchedOrOpened(string body)
    {
        // The review's reproduction: a bare URI where a reference goes made the renderer open the file or fetch the URL.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var missing = Path.Combine(Path.GetTempPath(), $"infopanel-missing-{Guid.NewGuid():N}", "ext.svg");
        foreach (var uri in new[] { $"http://127.0.0.1:{port}/x.svg", new Uri(missing).AbsoluteUri })
        {
            var svg = Head + string.Format(body, uri) + "</svg>";
            var result = MediaValidator.Check(Utf8(svg));
            Assert.Equal("media.svg-external", Assert.Single(result.Errors).Code);
            Assert.Null(result.Sanitized);
            // Even were it accepted, the engine renders only the rebuilt document, which has no URI to follow.
            if (result.Sanitized is { } rebuilt) RenderWithoutRequests(rebuilt, listener);
        }
        Assert.False(listener.Pending());
    }

    [Fact]
    public void ReferencesThatAreInTheProfile_RenderWithoutRequests()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var svg = Head + "<defs><clipPath id='c'><rect width='32' height='32'/></clipPath><mask id='m'><rect width='64' height='64' fill='white'/></mask>" +
                  "<linearGradient id='g'><stop offset='0' stop-color='blue'/></linearGradient></defs>" +
                  "<rect width='64' height='64' fill='url( \"#g\" ) red' clip-path='url(#c)' style='mask: URL(#m)'/></svg>";
        var rebuilt = Accept(svg).Sanitized!;
        Assert.DoesNotContain("://", Encoding.UTF8.GetString(rebuilt).Replace("http://www.w3.org/", ""));
        RenderWithoutRequests(rebuilt, listener);
    }

    private static void RenderWithoutRequests(byte[] rebuilt, TcpListener listener)
    {
        using var bitmap = Render(rebuilt, 64, 64); // a FileNotFoundException or HttpRequestException would surface here
        Assert.False(listener.Pending(), "Rendering the rebuilt document connected to the listener.");
    }

    [Theory]
    [InlineData("<rect fill='red; stroke: blue'/>")]
    [InlineData("<rect fill='inherit'/>")]
    [InlineData("<rect fill='var(--x)'/>")]
    [InlineData("<rect fill='red !important'/>")]
    [InlineData("<rect fill='url(#g) url(#g)'/>")]
    [InlineData("<rect fill='#ff0000f'/>")]
    [InlineData("<rect fill='rgb(1, 2 3)'/>")]
    [InlineData("<rect fill='rgb(1deg, 2, 3)'/>")]
    [InlineData("<rect fill='notacolour'/>")]
    [InlineData("<rect width='10mm'/>")]
    [InlineData("<rect width='-1'/>")]
    [InlineData("<rect x='1e10'/>")]
    [InlineData("<rect x='1 2'/>")]
    [InlineData("<rect opacity='half'/>")]
    [InlineData("<rect stroke-miterlimit='0.5'/>")]
    [InlineData("<rect transform='rotate(1, 2)'/>")]
    [InlineData("<rect transform='rotate(1),'/>")]
    [InlineData("<rect transform='Rotate(1)'/>")]
    [InlineData("<rect transform='perspective(1)'/>")]
    [InlineData("<rect display='contents'/>")]
    [InlineData("<rect clip-path='circle(5)'/>")]
    [InlineData("<path d='L 1 1'/>")]
    [InlineData("<path d='M 1'/>")]
    [InlineData("<path d='M 0 0 L 1e10 0'/>")]
    [InlineData("<polygon points='0 0 1'/>")]
    [InlineData("<polygon points='0, 0,'/>")]
    [InlineData("<svg viewBox='0 0 -1 1'/>")]
    [InlineData("<linearGradient gradientUnits='userspaceonuse'/>")]
    [InlineData("<svg preserveAspectRatio='defer xMidYMid'/>")]
    [InlineData("<stop offset='a'/>")]
    [InlineData("<g version='x'/>")]
    public void ValuesOutsideTheGrammar_AreRefused(string element) =>
        Assert.Equal("media.svg-external", Refuse(Head + element + "</svg>").Code);

    // ------------------------------------------------------------ 2: nesting depth

    [Fact]
    public void DrawnDepth_IsHeldTo64()
    {
        // svg > use > u61 > ... > u1 > u0: 64 levels drawn; one more use is refused.
        static string Chain(int uses)
        {
            var sb = new StringBuilder(Head + "<defs><rect id='u0' width='9' height='9'/>");
            for (var i = 1; i <= uses; i++) sb.Append($"<use id='u{i}' href='#u{i - 1}'/>");
            return sb.Append($"</defs><use href='#u{uses}'/></svg>").ToString();
        }
        Accept(Chain(61));
        Assert.Contains("more than 64 levels deep", Refuse(Chain(62)).Message);
    }

    // ------------------------------------------------------------ 3: gradient chains

    [Fact]
    public void GradientChains_AreHeldTo4_AndEachPaintReferenceIsCharged()
    {
        static string Chain(int hrefs, string paint)
        {
            var sb = new StringBuilder(Head + "<linearGradient id='g0'><stop offset='0' stop-color='blue'/></linearGradient>");
            for (var i = 1; i <= hrefs; i++) sb.Append($"<linearGradient id='g{i}' href='#g{i - 1}'/>");
            return sb.Append($"<rect width='9' height='9' {paint}/></svg>").ToString();
        }
        // svg, 5 gradients, a stop and the rect: 8; the fill costs 1 + its 4 hrefs, the stroke as much again.
        Assert.Equal(8 + 5, Accept(Chain(4, "fill='url(#g4)'")).Info!.Elements);
        Assert.Equal(8 + 5 + 5, Accept(Chain(4, "fill='url(#g4)' stroke='url(#g4) red'")).Info!.Elements);
        Assert.Equal(8 + 1, Accept(Chain(4, "fill='url(#g0)'")).Info!.Elements);
        Assert.Contains("more than 4 href", Refuse(Chain(5, "fill='url(#g5)'")).Message);
    }

    // ------------------------------------------------------------ 4: path data cost

    [Fact]
    public void PathNumbers_CountAgainstTheElementLimit_ThroughUse()
    {
        // 64 numbers in d and 32 in points: 2 and 1 more elements, counted again by each use that draws them.
        var d = "M0 0" + string.Concat(Enumerable.Repeat(" l1 1", 31));
        var points = string.Join(' ', Enumerable.Range(0, 32));
        var svg = Head + $"<defs><g id='p'><path d='{d}'/><polyline points='{points}'/></g></defs><use href='#p'/><use href='#p'/></svg>";
        // svg, defs, g (1 + 3 + 2 for its children), 2 uses each drawing the g.
        var g = 1 + (1 + 2) + (1 + 1);
        Assert.Equal(2 + g + 2 * (1 + g), Accept(svg).Info!.Elements);
        Assert.Equal("limit.svgElements", Refuse(svg, SpecLimits.Default with { SvgElements = 2 + g + 2 * (1 + g) - 1 }).Code);
    }

    // ------------------------------------------------------------ 5: dashes

    [Theory]
    [InlineData("stroke-dasharray='1'")]
    [InlineData("stroke-dashoffset='1'")]
    [InlineData("style='stroke-dasharray: 1 2'")]
    [InlineData("style='stroke-dashoffset: 1'")]
    public void Dashes_AreNotInTheProfile(string attribute) =>
        Assert.Equal("media.svg-external", Refuse(Head + $"<line x2='900000' stroke='black' {attribute} transform='scale(0.0001)'/></svg>").Code);

    // ------------------------------------------------------------ 7: format and encoding

    [Fact]
    public void AGifWithSvgInside_IsAGif_AndHasNoSvgForm()
    {
        var gif = Encoding.Latin1.GetBytes("GIF89a\x01\x00\x01\x00\x00\x00\x00,\x00\x00\x00\x00\x01\x00\x01\x00\x00\x02\x00;" + Head + "<rect width='64' height='64'/></svg>");
        var result = MediaValidator.Check(gif);
        Assert.Empty(result.Errors);
        Assert.Equal(MediaFormat.Gif, result.Format);
        Assert.Null(result.Sanitized);
    }

    [Theory]
    [InlineData("<?xml version='1.0' encoding='UTF-8'?>", true)]
    [InlineData("<?xml version='1.0' encoding='utf-8'?>", true)]
    [InlineData("<?xml version='1.0'?>", true)]
    [InlineData("<?xml version='1.0' encoding='ISO-8859-1'?>", false)]
    [InlineData("<?xml version='1.0' encoding='UTF-16'?>", false)]
    [InlineData("<?xml version='1.0' encoding='x-unknown'?>", false)]
    public void OnlyUtf8_IsRead(string declaration, bool accepted)
    {
        var svg = declaration + Head + "<title>é</title></svg>";
        if (accepted) Accept(svg);
        else Assert.Equal("media.svg-parse", Refuse(svg).Code);
    }

    [Fact]
    public void InvalidUtf8_IsAParseError()
    {
        var bytes = Utf8(Head + "<title>x</title></svg>");
        bytes[bytes.Length - 15] = 0xFF; // inside the title text
        Assert.Equal("media.svg-parse", Assert.Single(MediaValidator.Check(bytes).Errors).Code);
    }

    // ------------------------------------------------------------ 8: the root

    [Theory]
    [InlineData("<s:svg xmlns:s='http://www.w3.org/2000/svg' width='64' height='64'><s:rect width='64' height='64'/></s:svg>")]
    [InlineData("<!-- <svg --><s:svg xmlns:s='http://www.w3.org/2000/svg' width='64' height='64'/>")]
    public void APrefixedRoot_IsRefused(string svg) =>
        Assert.Equal("media.format", Refuse(svg).Code);

    // ------------------------------------------------------------ 9: linear-time patterns

    [Fact]
    public void Patterns_AreMatchedWithoutBacktracking()
    {
        Assert.True(ValidationText.Capability.Options.HasFlag(RegexOptions.NonBacktracking));
        Assert.True(ValidationText.FormatString.Options.HasFlag(RegexOptions.NonBacktracking));
        // A backreference needs backtracking, so a schema that uses one doesn't load.
        Assert.Throws<SchemaException>(() => SchemaSet.Load(["""{"$id": "urn:t:backref", "pattern": "^(a)\\1$"}"""]));
        // The engine's own schemas all load that way.
        Assert.NotNull(SpecSchemas.Strict);
        Assert.NotNull(SpecSchemas.StructureOnly);
    }
}
