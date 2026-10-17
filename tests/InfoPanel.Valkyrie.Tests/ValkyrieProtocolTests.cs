using InfoPanel.ValkyriePanel;
using System.Security.Cryptography;
using Xunit;

namespace InfoPanel.Valkyrie.Tests;

public sealed class ValkyrieProtocolTests
{
    [Theory]
    [InlineData(320, 240, true)]
    [InlineData(376, 960, false)] // Jonsbo DS339: same USB bridge, different panel.
    [InlineData(320, 960, false)] // VMAX
    [InlineData(360, 960, false)]
    [InlineData(640, 480, false)]
    public void DiscoveryUsesDetailedTimingInsteadOfSharedUsbId(int width, int height, bool expected)
    {
        var edid = Edid(width, height);
        Assert.Equal(expected, ValkyrieFrameProtocol.IsV360Edid(i => edid[i]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    public void MalformedHeaderIsRejected(int offset)
    {
        var edid = Edid(320, 240);
        edid[offset] ^= 1;
        Assert.False(ValkyrieFrameProtocol.IsV360Edid(i => edid[i]));
    }

    [Fact]
    public void MissingDetailedTimingIsRejected()
    {
        var edid = Edid(320, 240);
        edid[54] = edid[55] = 0;
        Assert.False(ValkyrieFrameProtocol.IsV360Edid(i => edid[i]));
    }

    [Fact]
    public void ColdStartMatchesThe77CommandSequenceVerifiedOnV360LinuxHardware()
    {
        var captured = new List<byte>();
        ValkyrieV360Protocol.Initialize((payload, reply) =>
        {
            var padded = new byte[9];
            payload.CopyTo(padded, 0);
            padded[8] = reply ? (byte)1 : (byte)0;
            captured.AddRange(padded);
        });
        Assert.Equal(77 * 9, captured.Count);
        Assert.Equal("A282A630A11B9B74813A3FC008BC4D7C95A06C072CBBAA59C9243F7E1FDB1D19",
            Convert.ToHexString(SHA256.HashData(captured.ToArray())));
    }

    [Fact]
    public void ScanoutEnableUsesPostFrameSequence()
    {
        var commands = new List<(byte[], bool)>();
        ValkyrieV360Protocol.PostFrame((payload, reply) => commands.Add((payload, reply)));
        Assert.Equal(4, commands.Count);
        Assert.Equal(new byte[] { 0xA6, 5, 1, 0 }, commands[2].Item1);
        Assert.False(commands[2].Item2);
        Assert.Equal(new byte[] { 0xB5, 0xC5, 0x55 }, commands[^1].Item1);
        Assert.True(commands[^1].Item2);
    }

    [Fact]
    public void FrameEnvelopePreservesPixelsAndAcceptsRepeatedStaticFrames()
    {
        var pixels = Enumerable.Range(0, 320 * 240 * 2).Select(i => (byte)i).ToArray();
        var frame = new byte[ValkyrieFrameProtocol.FrameLength];
        for (int i = 0; i < 2; i++)
        {
            ValkyrieFrameProtocol.PackFrame(pixels, frame);
            Assert.Equal(153616, frame.Length);
            Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 20, 0, 240 }, frame[..8]);
            Assert.Equal(pixels, frame[8..^8]);
            Assert.Equal(new byte[] { 255, 192, 0, 0, 0, 0, 0, 0 }, frame[^8..]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(153599)]
    [InlineData(153601)]
    public void IncorrectPixelCountIsRejected(int length) => Assert.Throws<ArgumentException>(() =>
        ValkyrieFrameProtocol.PackFrame(new byte[length], new byte[ValkyrieFrameProtocol.FrameLength]));

    [Theory]
    [InlineData(0, 0, 0, 128, 16, 128)]
    [InlineData(255, 255, 255, 128, 234, 128)]
    [InlineData(255, 0, 0, 90, 81, 239)]
    [InlineData(0, 255, 0, 54, 144, 34)]
    [InlineData(0, 0, 255, 239, 40, 109)]
    public void KnownColorsUseUyvyOrder(int r, int g, int b, int u, int y, int v)
    {
        var source = Solid(r, g, b, 1280);
        var output = new byte[153600];
        ValkyrieFrameProtocol.ConvertRgba(source, 1280, output, 100);
        for (int i = 0; i < output.Length; i += 4)
            Assert.Equal(new byte[] { (byte)u, (byte)y, (byte)v, (byte)y }, output[i..(i + 4)]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-20)]
    public void ZeroBrightnessProducesNeutralBlack(int brightness)
    {
        var output = new byte[153600];
        ValkyrieFrameProtocol.ConvertRgba(Solid(250, 130, 60, 1280), 1280, output, brightness);
        Assert.All(output.Chunk(2), pair => Assert.Equal(new byte[] { 128, 16 }, pair));
    }

    [Fact]
    public void RowPaddingIsIgnoredAndBrightnessIsClamped()
    {
        var expected = new byte[153600];
        var actual = new byte[153600];
        ValkyrieFrameProtocol.ConvertRgba(Solid(64, 125, 240, 1280), 1280, expected, 100);
        ValkyrieFrameProtocol.ConvertRgba(Solid(64, 125, 240, 1300), 1300, actual, 200);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1279, 307200, 153600)]
    [InlineData(1280, 307199, 153600)]
    [InlineData(1280, 307200, 153599)]
    public void InvalidConversionBuffersAreRejected(int stride, int sourceLength, int outputLength) =>
        Assert.Throws<ArgumentException>(() => ValkyrieFrameProtocol.ConvertRgba(new byte[sourceLength], stride, new byte[outputLength], 100));

    private static byte[] Solid(int r, int g, int b, int stride)
    {
        var source = Enumerable.Repeat((byte)0xEF, stride * 240).ToArray();
        for (int y = 0; y < 240; y++)
            for (int x = 0; x < 320; x++)
            {
                int i = y * stride + x * 4;
                source[i] = (byte)r; source[i + 1] = (byte)g; source[i + 2] = (byte)b; source[i + 3] = 255;
            }
        return source;
    }

    private static byte[] Edid(int width, int height)
    {
        var edid = new byte[128];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(edid, 0);
        edid[54] = 1;
        edid[56] = (byte)width; edid[58] = (byte)((width >> 8) << 4);
        edid[59] = (byte)height; edid[61] = (byte)((height >> 8) << 4);
        return edid;
    }
}
