using System.Buffers.Binary;
using System.Text;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Spec;

public class MediaProbeTests
{
    // ------------------------------------------------------------ builders

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Be32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] Be16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return b; }
    private static byte[] Le32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    private static byte[] Le16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); return b; }
    private static byte[] Le24(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16)];
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static readonly byte[] PngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] PngChunk(string type, byte[] data) => Concat(Be32((uint)data.Length), Ascii(type), data, new byte[4]); // CRC not checked

    private static byte[] PngIhdr(uint w, uint h) => PngChunk("IHDR", Concat(Be32(w), Be32(h), [8, 2, 0, 0, 0]));

    private static byte[] Png(uint w, uint h, params byte[][] chunks) =>
        Concat(PngSig, PngIhdr(w, h), Concat(chunks), PngChunk("IEND", []));

    private static byte[] PngActl(uint frames) => PngChunk("acTL", Concat(Be32(frames), Be32(0)));

    private static byte[] PngFctl(int seq, int num, int den) =>
        PngChunk("fcTL", Concat(Be32((uint)seq), Be32(1), Be32(1), Be32(0), Be32(0), Be16(num), Be16(den), [0, 0]));

    private static byte[] Jpeg(int w, int h, bool withSof = true, byte sofMarker = 0xC0)
    {
        var app0 = Concat([0xFF, 0xE0], Be16(16), Ascii("JFIF\0"), [1, 1, 0, 0, 1, 0, 1, 0, 0]);
        var dqt = Concat([0xFF, 0xDB], Be16(67), [0], new byte[64]);
        var sof = Concat([0xFF, sofMarker], Be16(11), [8], Be16(h), Be16(w), [1, 1, 0x11, 0]);
        var sos = Concat([0xFF, 0xDA], Be16(8), [1, 1, 0, 0, 63, 0], [0x12, 0x34, 0xFF, 0x00, 0x56]);
        return Concat([0xFF, 0xD8], app0, dqt, withSof ? sof : [], sos, [0xFF, 0xD9]);
    }

    private static byte[] GifFrame(int delayCs, bool localTable = false, bool gce = true)
    {
        var g = gce ? Concat([0x21, 0xF9, 4, 0], Le16(delayCs), [0, 0]) : [];
        var desc = Concat([0x2C], Le16(0), Le16(0), Le16(1), Le16(1), [(byte)(localTable ? 0x80 : 0)]);
        var lct = localTable ? new byte[6] : []; // 2 colours
        // LZW min size 2, a sub-block that contains 0x3B / 0x21 bytes to prove they are skipped, terminator
        var img = new byte[] { 2, 3, 0x3B, 0x21, 0x2C, 0 };
        return Concat(g, desc, lct, img);
    }

    private static byte[] Gif(int w, int h, bool globalTable, params byte[][] blocks)
    {
        var lsd = Concat(Le16(w), Le16(h), [(byte)(globalTable ? 0x80 : 0), 0, 0]);
        return Concat(Ascii("GIF89a"), lsd, globalTable ? new byte[6] : [], Concat(blocks), [0x3B]);
    }

    private static byte[] GifComment() => [0x21, 0xFE, 3, (byte)'a', (byte)'b', (byte)'c', 0];

    private static byte[] RiffWebP(params byte[][] chunks)
    {
        var body = Concat(Ascii("WEBP"), Concat(chunks));
        return Concat(Ascii("RIFF"), Le32((uint)body.Length), body);
    }

    private static byte[] WebPChunk(string type, byte[] data) =>
        Concat(Ascii(type), Le32((uint)data.Length), data, data.Length % 2 == 1 ? new byte[1] : []);

    private static byte[] WebPLossy(int w, int h) =>
        WebPChunk("VP8 ", Concat([0x10, 0x02, 0x00, 0x9D, 0x01, 0x2A], Le16(w), Le16(h), new byte[6]));

    private static byte[] WebPLossless(int w, int h)
    {
        uint bits = (uint)(w - 1) | (uint)(h - 1) << 14;
        return WebPChunk("VP8L", Concat([0x2F], Le32(bits), new byte[3]));
    }

    private static byte[] WebPVp8x(int w, int h, bool animated) =>
        WebPChunk("VP8X", Concat([(byte)(animated ? 0x02 : 0), 0, 0, 0], Le24(w - 1), Le24(h - 1)));

    private static byte[] WebPAnmf(int durationMs) =>
        WebPChunk("ANMF", Concat(Le24(0), Le24(0), Le24(0), Le24(0), Le24(durationMs), [0], WebPLossy(1, 1)));

    private static MediaInfo Ok(byte[] bytes)
    {
        var info = MediaProbe.Inspect(bytes, out var problem);
        Assert.Null(problem);
        Assert.NotNull(info);
        return info!;
    }

    private static MediaProblem Bad(byte[] bytes)
    {
        var info = MediaProbe.Inspect(bytes, out var problem);
        Assert.Null(info);
        Assert.NotNull(problem);
        return problem!;
    }

    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    // ------------------------------------------------------------ PNG

    [Fact]
    public void Png_Plain()
    {
        var info = Ok(Png(300, 200, PngChunk("IDAT", new byte[10])));
        Assert.Equal(new MediaInfo(MediaFormat.Png, 300, 200, 1, 0), info);
    }

    [Fact]
    public void Png_Animated_CountsFramesAndDelays()
    {
        var bytes = Png(8, 8,
            PngActl(3),
            PngFctl(0, 1, 10), PngChunk("IDAT", new byte[4]),
            PngFctl(1, 1, 4), PngChunk("fdAT", new byte[8]),
            PngFctl(2, 3, 0), PngChunk("fdAT", new byte[8])); // den 0 means 100
        var info = Ok(bytes);
        Assert.Equal(3, info.Frames);
        Assert.Equal(0.1 + 0.25 + 0.03, info.DurationSeconds, 9);
    }

    [Fact]
    public void Png_AcTlAfterIdat_IsIgnored()
    {
        var info = Ok(Png(8, 8, PngChunk("IDAT", new byte[4]), PngActl(5)));
        Assert.Equal(1, info.Frames);
        Assert.Equal(0, info.DurationSeconds);
    }

    [Fact]
    public void Png_HugeHeader_ReportedWithoutAllocation()
    {
        var bytes = Png(100000, 100000, PngChunk("IDAT", new byte[4]));
        Ok(bytes); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        var info = MediaProbe.Inspect(bytes, out var problem);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Null(problem);
        Assert.Equal(100000, info!.Width);
        Assert.Equal(100000, info.Height);
        Assert.True(allocated < 1024, $"allocated {allocated} bytes");
    }

    [Fact]
    public void Png_ClaimedHugeChunkLengthAndFrameCount_AreHarmless()
    {
        var evil = Concat(PngSig, PngIhdr(int.MaxValue, int.MaxValue), PngChunk("acTL", Concat(Be32(int.MaxValue), Be32(0))),
            Be32(uint.MaxValue), Ascii("IDAT"), new byte[3]);
        var info = Ok(evil);
        Assert.Equal(int.MaxValue, info.Width);
        Assert.Equal(int.MaxValue, info.Frames);
    }

    [Theory]
    [InlineData(0u, 5u)]
    [InlineData(5u, 0u)]
    [InlineData(0x80000000u, 5u)]
    [InlineData(5u, uint.MaxValue)]
    public void Png_BadDimensions_Malformed(uint w, uint h) =>
        Assert.Equal("media.malformed", Bad(Png(w, h)).Code);

    [Fact]
    public void Png_FirstChunkNotIhdr_Malformed()
    {
        var bytes = Concat(PngSig, PngChunk("IDAT", new byte[13]), PngChunk("IEND", []));
        Assert.Equal("media.malformed", Bad(bytes).Code);
    }

    [Fact]
    public void Png_ZeroFrameAcTl_Malformed() =>
        Assert.Equal("media.malformed", Bad(Png(4, 4, PngActl(0))).Code);

    // ------------------------------------------------------------ JPEG

    [Fact]
    public void Jpeg_Plain() =>
        Assert.Equal(new MediaInfo(MediaFormat.Jpeg, 640, 480, 1, 0), Ok(Jpeg(640, 480)));

    [Theory]
    [InlineData((byte)0xC0)]
    [InlineData((byte)0xC1)]
    [InlineData((byte)0xC2)]
    [InlineData((byte)0xC3)]
    [InlineData((byte)0xC5)]
    [InlineData((byte)0xC9)]
    [InlineData((byte)0xCB)]
    [InlineData((byte)0xCD)]
    [InlineData((byte)0xCF)]
    public void Jpeg_AllSofMarkers(byte marker) =>
        Assert.Equal(new MediaInfo(MediaFormat.Jpeg, 123, 45, 1, 0), Ok(Jpeg(123, 45, sofMarker: marker)));

    [Fact]
    public void Jpeg_FillBytesAndStandaloneMarkersAreSkipped()
    {
        var sof = Concat([0xFF, 0xC2], Be16(11), [8], Be16(7), Be16(9), [1, 1, 0x11, 0]);
        var bytes = Concat([0xFF, 0xD8, 0xFF, 0xFF, 0xFF, 0x01, 0xFF, 0xD3], sof);
        Assert.Equal(new MediaInfo(MediaFormat.Jpeg, 9, 7, 1, 0), Ok(bytes));
    }

    [Fact]
    public void Jpeg_HugeHeader() =>
        Assert.Equal(65535, Ok(Jpeg(65535, 65535)).Width);

    [Fact]
    public void Jpeg_NoSof_Malformed() =>
        Assert.Equal("media.malformed", Bad(Jpeg(10, 10, withSof: false)).Code);

    [Fact]
    public void Jpeg_OnlySoi_Malformed() =>
        Assert.Equal("media.malformed", Bad([0xFF, 0xD8]).Code);

    [Fact]
    public void Jpeg_IncompleteSof_Truncated()
    {
        var bytes = Jpeg(10, 10);
        int sof = bytes.AsSpan().IndexOf((byte)0xC0) - 1;
        Assert.Equal("media.truncated", Bad(bytes[..(sof + 6)]).Code);
    }

    // ------------------------------------------------------------ GIF

    [Fact]
    public void Gif_SingleFrame()
    {
        var info = Ok(Gif(20, 10, true, GifFrame(0, gce: false)));
        Assert.Equal(new MediaInfo(MediaFormat.Gif, 20, 10, 1, 0), info);
    }

    [Fact]
    public void Gif_Animated_CountsFramesAndSumsDelays()
    {
        var info = Ok(Gif(20, 10, true, GifComment(), GifFrame(10), GifFrame(25, localTable: true), GifFrame(5)));
        Assert.Equal(3, info.Frames);
        Assert.Equal(0.4, info.DurationSeconds, 9);
    }

    [Fact]
    public void Gif_HugeHeader() =>
        Assert.Equal(65535, Ok(Gif(65535, 65535, false, GifFrame(1))).Height);

    [Fact]
    public void Gif_NoFrames_Malformed() =>
        Assert.Equal("media.malformed", Bad(Gif(5, 5, true, GifComment())).Code);

    [Fact]
    public void Gif_ShortHeader_Truncated() =>
        Assert.Equal("media.truncated", Bad(Ascii("GIF89a").Concat(new byte[] { 1, 0 }).ToArray()).Code);

    // ------------------------------------------------------------ WebP

    [Fact]
    public void WebP_Lossy() =>
        Assert.Equal(new MediaInfo(MediaFormat.WebP, 321, 123, 1, 0), Ok(RiffWebP(WebPLossy(321, 123))));

    [Fact]
    public void WebP_Lossy_IgnoresScaleBits()
    {
        var chunk = WebPChunk("VP8 ", Concat([0x10, 0x02, 0x00, 0x9D, 0x01, 0x2A], Le16(0xC000 | 50), Le16(0x4000 | 60), new byte[6]));
        var info = Ok(RiffWebP(chunk));
        Assert.Equal((50, 60), (info.Width, info.Height));
    }

    [Fact]
    public void WebP_Lossless() =>
        Assert.Equal(new MediaInfo(MediaFormat.WebP, 16384, 1, 1, 0), Ok(RiffWebP(WebPLossless(16384, 1))));

    [Fact]
    public void WebP_Extended_Static() =>
        Assert.Equal(new MediaInfo(MediaFormat.WebP, 100000, 70000, 1, 0),
            Ok(RiffWebP(WebPVp8x(100000, 70000, false), WebPLossy(1, 1))));

    [Fact]
    public void WebP_Extended_Animated()
    {
        var info = Ok(RiffWebP(WebPVp8x(64, 32, true), WebPChunk("ANIM", new byte[6]), WebPAnmf(100), WebPAnmf(250), WebPAnmf(51)));
        Assert.Equal(3, info.Frames);
        Assert.Equal(0.401, info.DurationSeconds, 9);
        Assert.Equal((64, 32), (info.Width, info.Height));
    }

    [Fact]
    public void WebP_AnimatedWithoutFrames_Malformed() =>
        Assert.Equal("media.malformed", Bad(RiffWebP(WebPVp8x(8, 8, true))).Code);

    [Fact]
    public void WebP_RiffButNotWebP_Format()
    {
        var wav = Concat(Ascii("RIFF"), Le32(4), Ascii("WAVE"), new byte[8]);
        Assert.Equal("media.format", Bad(wav).Code);
    }

    // ------------------------------------------------------------ SVG

    [Fact]
    public void Svg_WidthHeight()
    {
        var info = Ok(Svg("<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80px' viewBox='0 0 10 10'><rect/><g><circle/></g></svg>"));
        Assert.Equal(new MediaInfo(MediaFormat.Svg, 120, 80, 1, 0, 4), info);
    }

    [Fact]
    public void Svg_ViewBoxOnly_WithBomXmlDeclarationAndComment()
    {
        var text = "<?xml version=\"1.0\"?>\n<!-- c -->\n<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24.5, 16\"/>";
        var bytes = Concat([0xEF, 0xBB, 0xBF], Svg(text));
        var info = Ok(bytes);
        Assert.Equal((25, 16, 1), (info.Width, info.Height, info.Elements));
    }

    [Fact]
    public void Svg_NoSize_IsZero() =>
        Assert.Equal(0, Ok(Svg("<svg xmlns='http://www.w3.org/2000/svg'/>")).Width);

    [Theory]
    [InlineData("50%")]
    [InlineData("10mm")]
    [InlineData("1in")]
    [InlineData("2em")]
    [InlineData("100pt")]
    [InlineData("-5")]
    [InlineData("auto")]
    [InlineData("")]
    public void Svg_OtherUnits_AreUnsupported(string width) =>
        Assert.Equal("media.svg-size", Bad(Svg($"<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='10' viewBox='0 0 10 10'/>")).Code);

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='9000' height='10px'/>", 9000, 10)]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width=' 12.5 PX ' height='1e1'/>", 13, 10)]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' height='7' viewBox='0 0 30 40'/>", 30, 7)]
    public void Svg_SizeIsUnitlessOrPx_ElseTheViewBox(string svg, int width, int height) =>
        Assert.Equal((width, height), (Ok(Svg(svg)).Width, Ok(Svg(svg)).Height));

    [Fact]
    public void Svg_PhysicalUnits_CantDodgeTheSizeLimit()
    {
        // 100in would be 9,600 px: refused as unsupported rather than read as 100.
        var (info, errors) = MediaValidator.Check(Svg("<svg xmlns='http://www.w3.org/2000/svg' width='100in' height='100in'/>"));
        Assert.Null(info);
        Assert.Equal("media.svg-size", Assert.Single(errors).Code);
        // In px, the resolved size meets the limits.
        Assert.Equal("limit.mediaSide", Assert.Single(MediaValidator.Check(Svg("<svg xmlns='http://www.w3.org/2000/svg' width='9600px' height='1'/>")).Errors).Code);
        Assert.Equal("limit.mediaSide", Assert.Single(MediaValidator.Check(Svg("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 9600 1'/>")).Errors).Code);
    }

    /// <summary>A normal icon: gradients (one inheriting another by href), a clip path, a mask, a symbol drawn by use.</summary>
    private const string Icon =
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
        "<g clip-path='url(#round)' class='face'>" +
        "<rect width='48' height='48' fill='url(#face)' style='stroke: url( \"#rim\" ) #000; stroke-width: 2'/>" +
        "<circle cx='24' cy='24' r='10' fill=\"url('#glow')\" mask='url(#fade)'/>" +
        "</g>" +
        "<use href='#tick' x='23' y='4' width='2' height='6' fill='currentColor'/>" +
        "<use xlink:href='#tick' transform='rotate(45 24 24)'/>" +
        "<path d='M24 24 L36 30' stroke='#fdd835' stroke-linecap='round' fill='none'/>" +
        "<polyline points='0,0 1,1'/><polygon points='0,0 1,1 1,0'/><line x1='0' y1='0' x2='1' y2='1'/><ellipse rx='1' ry='2'/>" +
        "</svg>";

    [Fact]
    public void Svg_NormalIcon_IsInTheProfile()
    {
        var info = Ok(Svg(Icon));
        Assert.Equal((48, 48), (info.Width, info.Height));
        // 25 elements, plus each use drawing the symbol and its rect (2 × 2), the clip path and its circle (2), the mask and
        // its rect (2), and the gradient references: #face (1), #rim and #glow (1 each, plus 1 for the href they inherit through).
        Assert.Equal(25 + 4 + 2 + 2 + 1 + 2 + 2, info.Elements);
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><image href='http://example.com/a.png'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'><image xlink:href='file:///etc/passwd'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><a href='other.svg'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><image href='data:image/png;base64,AAAA'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><image href=' DATA:image/svg+xml,&lt;svg/&gt;'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><use href=''/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><SCRIPT/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><foreignObject><p/></foreignObject></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><iframe/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><embed/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><object/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' onload='x()'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect ONCLICK='x()'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><style>@import 'x.css';</style></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><style><![CDATA[rect { fill: url(http://example.com/p.svg#a) }]]></style></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><style>rect { background: url(data:image/png;base64,AAAA) }</style></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><style>rect { fill: \\75 rl(x.svg) }</style></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><style>rect { background: image-set('x.png' 1x) }</style></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect style='fill: url(x.svg#a)'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect style='@import url(#a)'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect fill='url(https://example.com/a.svg#g)'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect filter='url( \"x.svg#f\")'/></svg>")]
    [InlineData("<?xml-stylesheet href='x.css'?><svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><a><set attributeName='href' to='http://example.com'/></a></svg>")]
    // The review's bypasses of the old screening: a CDATA-split @import and url( in <style>, xml:base under a #id use.
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><style>@im<![CDATA[]]>port 'x.css'; rect { fill: ur<![CDATA[]]>l(x.svg#a) }</style></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><g xml:base='https://example.com/a.svg'><use href='#a'/></g><rect id='a'/></svg>")]
    // Outside the allowlist.
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><text>Hi</text></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><image/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><filter id='f'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><Rect/></svg>")]
    [InlineData("<svg><rect/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' xmlns:x='urn:x'><x:rect/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' xmlns:x='urn:x' x:y='1'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'><use xlink:title='t'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' xml:lang='en'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect filter='none'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect href='#a' id='a'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><use href=' #a'/><rect id='a'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><use href='#missing'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect id='a'/><rect id='a'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect id='a b'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect id='a'/><rect clip-path='url(#a)'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect id='a'/><rect fill='url(#a)'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect fill='image(x.png)'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect fill='red /* c */'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><mask id='m'/><rect d='url(#m)'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect style='width: 10in'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect style='transform: scale(9)'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect style='fill'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect style='fill: red; } * { fill: blue'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><g>text</g></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><title><g/></title></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><title><![CDATA[x]]></title></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><?pi x?></svg>")]
    // Reference cycles: through an ancestor, between definitions, and a gradient inheriting itself.
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><g id='a'><use href='#a'/></g></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><defs><g id='a'><use href='#b'/></g><g id='b'><use href='#a'/></g></defs></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><clipPath id='c'><rect clip-path='url(#c)'/></clipPath></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><linearGradient id='g' href='#g'/></svg>")]
    public void Svg_OutsideTheProfile_Rejected(string svg) =>
        Assert.Equal("media.svg-external", Bad(Svg(svg)).Code);

    [Fact]
    public void Svg_ElementLimit_StopsReading()
    {
        // The element limit is applied while reading: a document whose tail is broken is refused for its size, not read to the end.
        var sb = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>");
        for (int i = 0; i < 20; i++) sb.Append("<g/>");
        sb.Append("<g><broken");
        var info = MediaProbe.Inspect(Svg(sb.ToString()), out var problem, maxSvgElements: 10);
        Assert.Null(problem);
        Assert.Equal(11, info!.Elements);
        var errors = MediaValidator.Check(Svg(sb.ToString()), SpecLimits.Default with { SvgElements = 10 }).Errors;
        Assert.Equal("limit.svgElements", Assert.Single(errors).Code);
    }

    [Theory]
    [InlineData("use")]
    [InlineData("mask")]
    [InlineData("clip")]
    public void Svg_ReferenceExpansion_CountsAgainstTheElementLimit(string how)
    {
        // 20 definitions, each drawing the previous one twice: 2^20 elements drawn from 62 written.
        var sb = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><defs><rect id='d0' width='1' height='1'/>");
        for (int i = 1; i <= 20; i++)
            sb.Append(how switch
            {
                "use" => $"<g id='d{i}'><use href='#d{i - 1}'/><use href='#d{i - 1}'/></g>",
                "mask" => i == 1 ? "<mask id='d1'><rect/><rect/></mask>" : $"<mask id='d{i}'><rect mask='url(#d{i - 1})'/><rect mask='url(#d{i - 1})'/></mask>",
                _ => i == 1 ? "<clipPath id='d1'><rect/><rect/></clipPath>" : $"<clipPath id='d{i}'><rect clip-path='url(#d{i - 1})'/><rect clip-path='url(#d{i - 1})'/></clipPath>",
            });
        sb.Append(how switch { "use" => "</defs><use href='#d20'/>", "mask" => "</defs><rect mask='url(#d20)'/>", _ => "</defs><rect clip-path='url(#d20)'/>" });
        var svg = Svg(sb.Append("</svg>").ToString());

        var info = MediaProbe.Inspect(svg, out var problem, maxSvgElements: 10_000);
        Assert.Null(problem);
        Assert.Equal(10_001, info!.Elements);
        var (refused, errors) = MediaValidator.Check(svg);
        Assert.Null(refused);
        Assert.Equal("limit.svgElements", Assert.Single(errors).Code);
    }

    [Fact]
    public void Svg_ReferenceExpansion_WithinTheLimit_IsCounted()
    {
        // A symbol of 3 elements drawn by 3 uses: 3 + 3 written (svg, defs and the uses) + 3 × 3 drawn.
        var svg = Svg("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><defs><symbol id='s'><rect/><rect/></symbol></defs>" +
                      "<use href='#s'/><use href='#s'/><use href='#s'/></svg>");
        Assert.Equal(2 + 3 + 3 + 3 * 3, Ok(svg).Elements);
        Assert.Empty(MediaValidator.Check(svg, SpecLimits.Default with { SvgElements = 17 }).Errors);
        Assert.Equal("limit.svgElements", Assert.Single(MediaValidator.Check(svg, SpecLimits.Default with { SvgElements = 16 }).Errors).Code);
    }

    [Fact]
    public void Svg_CssNeverSizesTheRoot()
    {
        // The review's bypass: a style width in inches over unitless size attributes.
        Assert.Equal("media.svg-external", Bad(Svg("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10' style='width: 100in; height: 100in'/>")).Code);
        // Presentation properties in style are fine, and the size still comes from the attributes.
        Assert.Equal((10, 10), (Ok(Svg("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10' style='fill: #fff'/>")).Width, 10));
    }

    [Fact]
    public void Svg_ByteLimit_ComesBeforeParsing()
    {
        // Over the byte limit and not even well-formed: the byte limit is what is reported.
        var bytes = Svg("<svg xmlns='http://www.w3.org/2000/svg'>" + new string(' ', 200) + "<g><broken");
        var errors = MediaValidator.Check(bytes, SpecLimits.Default with { SvgBytes = 100 }).Errors;
        Assert.Equal("limit.svgBytes", Assert.Single(errors).Code);
    }

    [Theory]
    [InlineData("<?xml version='1.0'?><!DOCTYPE svg [<!ENTITY a 'b'>]><svg xmlns='http://www.w3.org/2000/svg'>&a;</svg>")]
    [InlineData("<?xml version='1.0'?><!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' 'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'><svg/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><g></svg>")]
    [InlineData("<svg")]
    public void Svg_ParseErrors(string svg) =>
        Assert.Equal("media.svg-parse", Bad(Svg(svg)).Code);

    [Fact]
    public void Svg_OverCharacterLimit_ParseError()
    {
        var sb = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg'><!--");
        sb.Append('x', MediaProbe.MaxSvgCharacters).Append("--></svg>");
        Assert.Equal("media.svg-parse", Bad(Svg(sb.ToString())).Code);
    }

    [Fact]
    public void Svg_DeepNesting_IsRefusedAtDepth33()
    {
        static byte[] Nested(int depth) =>
            Svg("<svg xmlns='http://www.w3.org/2000/svg'>" + string.Concat(Enumerable.Repeat("<g>", depth - 1)) +
                string.Concat(Enumerable.Repeat("</g>", depth - 1)) + "</svg>");
        Assert.Equal(32, Ok(Nested(32)).Elements);
        Assert.Contains("more than 32 deep", Bad(Nested(33)).Message);
        // Reading stops at the first element too deep: 50,000 levels cost no more than 33.
        Assert.Equal("media.svg-external", Bad(Nested(50_000)).Code);
    }

    [Theory]
    [InlineData("<html><body/></html>")]
    [InlineData("hello world")]
    [InlineData("<?xml version='1.0'?><root/>")]
    public void NotAnImage_Format(string text) =>
        Assert.Equal("media.format", Bad(Svg(text)).Code);

    [Fact]
    public void Empty_IsFormat() => Assert.Equal("media.format", Bad([]).Code);

    // ------------------------------------------------------------ robustness

    private static IEnumerable<byte[]> SmallValidFiles()
    {
        yield return Png(4, 4, PngActl(2), PngFctl(0, 1, 10), PngChunk("IDAT", new byte[5]), PngFctl(1, 1, 10), PngChunk("fdAT", new byte[5]));
        yield return Jpeg(33, 22);
        yield return Gif(3, 3, true, GifFrame(5), GifFrame(7, localTable: true));
        yield return RiffWebP(WebPLossy(5, 6));
        yield return RiffWebP(WebPLossless(5, 6));
        yield return RiffWebP(WebPVp8x(8, 8, true), WebPAnmf(10), WebPAnmf(20));
        yield return Svg("<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4'><rect/></svg>");
    }

    private static void AssertSane(byte[] bytes)
    {
        var info = MediaProbe.Inspect(bytes, out var problem);
        Assert.True((info is null) != (problem is null));
        if (problem is not null)
            Assert.Contains(problem.Code, new[] { "media.format", "media.truncated", "media.malformed", "media.svg-external", "media.svg-size", "media.svg-parse" });
        else
        {
            Assert.True(info!.Frames >= 1);
            Assert.True(info.Width >= 0 && info.Height >= 0 && info.DurationSeconds >= 0);
        }
    }

    [Fact]
    public void EveryTruncation_DoesNotThrow()
    {
        foreach (var file in SmallValidFiles())
        {
            Ok(file);
            for (int n = 0; n <= file.Length; n++)
                AssertSane(file[..n]);
        }
    }

    [Fact]
    public void Truncated_HeadersReportTruncated()
    {
        var png = Png(4, 4);
        Assert.Equal("media.truncated", Bad(png[..20]).Code);
        Assert.Equal("media.truncated", Bad(png[..4]).Code);
        var webp = RiffWebP(WebPLossy(5, 6));
        Assert.Equal("media.truncated", Bad(webp[..16]).Code);
        Assert.Equal("media.truncated", Bad(webp[..25]).Code);
        Assert.Equal("media.truncated", Bad(Ascii("GIF8")).Code);
    }

    [Fact]
    public void RandomBytes_DoNotThrow()
    {
        var rng = new Random(20240607);
        for (int i = 0; i < 4000; i++)
        {
            var bytes = new byte[rng.Next(0, 300)];
            rng.NextBytes(bytes);
            AssertSane(bytes);
        }
    }

    [Fact]
    public void RandomBytesBehindRealSignatures_DoNotThrow()
    {
        var rng = new Random(77);
        byte[][] prefixes =
        [
            PngSig, Concat(PngSig, PngIhdr(5, 5)), [0xFF, 0xD8], Ascii("GIF89a"), Ascii("GIF87a"),
            Concat(Ascii("RIFF"), Le32(100), Ascii("WEBP")), Concat(Ascii("RIFF"), Le32(100), Ascii("WEBPVP8X")),
            Concat(Ascii("RIFF"), Le32(100), Ascii("WEBPVP8 ")), Concat(Ascii("RIFF"), Le32(100), Ascii("WEBPVP8L")),
            Svg("<svg "),
        ];
        for (int i = 0; i < 4000; i++)
        {
            var tail = new byte[rng.Next(0, 200)];
            rng.NextBytes(tail);
            AssertSane(Concat(prefixes[i % prefixes.Length], tail));
        }
    }

    [Fact]
    public void MutatedValidFiles_DoNotThrow()
    {
        var rng = new Random(99);
        var files = SmallValidFiles().ToArray();
        for (int i = 0; i < 4000; i++)
        {
            var copy = (byte[])files[i % files.Length].Clone();
            int flips = rng.Next(1, 5);
            for (int f = 0; f < flips; f++)
                copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
            AssertSane(copy);
        }
    }

    // ------------------------------------------------------------ real files

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "assets", name);

    [Theory]
    [InlineData("logo.png")]
    [InlineData("gauge-0.png")]
    [InlineData("gauge-1.png")]
    [InlineData("gauge-2.png")]
    [InlineData("gauge-3.png")]
    [InlineData("gauge-4.png")]
    [InlineData("blink.gif")]
    public void FixtureImages_MatchSkiaCodec(string name)
    {
        var path = Asset(name);
        var info = Ok(File.ReadAllBytes(path));
        using var codec = SKCodec.Create(path);
        Assert.NotNull(codec);
        Assert.Equal(codec.Info.Width, info.Width);
        Assert.Equal(codec.Info.Height, info.Height);
        Assert.Equal(Math.Max(1, codec.FrameCount), info.Frames);
    }

    [Fact]
    public void FixtureGif_HasThreeFrames()
    {
        var info = Ok(File.ReadAllBytes(Asset("blink.gif")));
        Assert.Equal(MediaFormat.Gif, info.Format);
        Assert.Equal(3, info.Frames);
        Assert.True(info.DurationSeconds > 0);
    }

    [Fact]
    public void FixtureSvg_Is64Square()
    {
        var info = Ok(File.ReadAllBytes(Asset("icon.svg")));
        Assert.Equal(MediaFormat.Svg, info.Format);
        Assert.Equal((64, 64), (info.Width, info.Height));
        Assert.Equal(4, info.Elements);
    }

    [Fact]
    public void SkiaEncodedFiles_MatchProbe()
    {
        using var bitmap = new SKBitmap(37, 21);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        foreach (var (fmt, expected) in new[] { (SKEncodedImageFormat.Png, MediaFormat.Png), (SKEncodedImageFormat.Jpeg, MediaFormat.Jpeg), (SKEncodedImageFormat.Webp, MediaFormat.WebP) })
        {
            using var data = image.Encode(fmt, 90);
            var info = Ok(data.ToArray());
            Assert.Equal(expected, info.Format);
            Assert.Equal((37, 21), (info.Width, info.Height));
        }
    }
}
