using System.Text;
using InfoPanel.Core.Tests.Panels.Turing.V1Oracle;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Turing;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Turing;

/// <summary>
/// The serial protocol's bytes (§2.2 (1)) against the library 1.4.x drove its serial panels with: TuringSmartScreenLib's
/// revisions A, C and E (<c>V1Oracle/TuringSmartScreenLib.cs</c>, its source at the commit 1.4.x's DLL was built from),
/// each fed the same frame and the same replies, every write compared; and a few vectors written by hand from the
/// library's source (cited).
/// </summary>
public sealed class TuringSerialProtocolTests
{
    private static readonly PanelCandidate Port = new(0x1A86, 0x5722, 1, 7, "1-4");

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static (FakePanelDevice Device, IPanelSerial Serial) Open()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Port, PanelTransport.Serial);
        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenSerial(Port, new PanelSerialOptions(115200, true, true), 1000, out var serial));
        return (device, serial!);
    }

    private static List<string> Writes(FakePanelDevice device) =>
        [.. device.Trace.Entries.Where(e => e.Op == PanelTraceOp.SerialWrite).Select(e => Hex(e.Data))];

    private static List<string> Writes(OracleSerialPort port) => [.. port.Writes.Select(Hex)];

    /// <summary>A picture: the bitmap 1.4.x drew (opaque), and the frame the engine sends for it in each format.</summary>
    internal static (SKBitmap Bitmap, byte[] Rgb565, byte[] Bgr) Picture(int width, int height, int seed, Func<int, int, bool>? keep = null, (SKBitmap, byte[], byte[])? from = null)
    {
        var random = new Random(seed);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var rgb565 = new byte[width * height * 2];
        var bgr = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                SKColor color;
                if (keep != null && from is var (old, _, _) && keep(x, y))
                    color = old.GetPixel(x, y);
                else
                    color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                bitmap.SetPixel(x, y, color);
                var v = ((color.Red >> 3) << 11) | ((color.Green >> 2) << 5) | (color.Blue >> 3);
                var i = (y * width) + x;
                rgb565[i * 2] = (byte)v;
                rgb565[(i * 2) + 1] = (byte)(v >> 8);
                bgr[i * 3] = color.Blue;
                bgr[(i * 3) + 1] = color.Green;
                bgr[(i * 3) + 2] = color.Red;
            }
        }
        return (bitmap, rgb565, bgr);
    }

    /// <summary>
    /// A revision C or E screen as its writes left it, as BGR in a frame of <paramref name="width"/> x
    /// <paramref name="height"/>: each UpdateBitmap's rows (a row's position counted on a screen
    /// <paramref name="screenWidth"/> wide, its width, its pixels of <paramref name="pixelBytes"/> each) put at their
    /// place; its payload is each 250-byte write's first 249 bytes until its size.
    /// </summary>
    internal static byte[] ScreenCe(IReadOnlyList<byte[]> writes, int screenWidth, int width, int height, int pixelBytes)
    {
        var screen = new byte[width * height * 3];
        for (var i = 0; i < writes.Count; i++)
        {
            var header = writes[i];
            if (header.Length != 250 || header[0] != 0xcc || header[1] != 0xef || header[2] != 0x69)
                continue;
            var size = (header[5] << 8) | header[6];
            var payload = new List<byte>(size);
            while (payload.Count < size && i + 1 < writes.Count)
                payload.AddRange(writes[++i].Take(Math.Min(249, size - payload.Count)));
            for (var offset = 0; offset < payload.Count - 2;) // the rows, then ef 69
            {
                var position = (payload[offset] << 16) | (payload[offset + 1] << 8) | payload[offset + 2];
                var rowWidth = (payload[offset + 3] << 8) | payload[offset + 4];
                offset += 5;
                var (x, y) = (position % screenWidth, position / screenWidth);
                for (var p = 0; p < rowWidth; p++, offset += pixelBytes)
                    for (var c = 0; c < 3; c++)
                        screen[((((y * width) + x + p) * 3)) + c] = payload[offset + c];
            }
        }
        return screen;
    }

    private static byte[] FrameOf(SerialRevision revision, (SKBitmap, byte[] Rgb565, byte[] Bgr) picture) =>
        revision.Letter == 'A' ? picture.Rgb565 : picture.Bgr;

    // ---- Revision A ----

    [Fact]
    public void RevisionA_ScreenOnBrightnessAndOff_AreTheLibrarys()
    {
        // RevisionA.cs: ScreenOn 109, SetBrightness 110 with the level in bytes 0-1, ScreenOff 108, six bytes each;
        // RevisionA.Abstraction.cs: the level is 255 - percent / 100 * 255.
        var (device, serial) = Open();
        var screen = TuringSerialScreen.Create(SerialRevision.A, serial);
        using var oracle = V1Screen.Make('A');

        Assert.Equal(PanelDeviceStatus.Ok, screen.ScreenOn(TestContext.Current.CancellationToken));
        oracle.ScreenOn();
        foreach (var percent in new[] { 0, 1, 50, 70, 99, 100 })
        {
            Assert.Equal(PanelDeviceStatus.Ok, screen.SetBrightness(percent, TestContext.Current.CancellationToken));
            oracle.SetBrightness((byte)percent);
        }
        Assert.Equal(PanelDeviceStatus.Ok, screen.ScreenOff(() => 1000));
        oracle.ScreenOff();

        Assert.Equal(Writes(oracle.Port), Writes(device));
    }

    [Fact]
    public void RevisionA_ByHand()
    {
        // RevisionA.cs WriteCommand(110, level): [level >> 2, (level & 3) << 6, 0, 0, 0, 110]; 100 % is level 0, 0 % 255.
        var (device, serial) = Open();
        var screen = TuringSerialScreen.Create(SerialRevision.A, serial);

        screen.ScreenOn(TestContext.Current.CancellationToken);
        screen.SetBrightness(100, TestContext.Current.CancellationToken);
        screen.SetBrightness(0, TestContext.Current.CancellationToken);
        screen.ScreenOff(() => 1000);

        Assert.Equal(["00000000006d", "00000000006e", "3fc00000006e", "00000000006c"], Writes(device));
    }

    public static TheoryData<int, int, int, int> RevisionASectors() => new()
    {
        { 0, 0, 320, 480 },     // the full frame
        { 20, 40, 40, 20 },
        { 300, 460, 20, 20 },   // the last corner
        { 0, 100, 40, 40 },
    };

    [Theory]
    [MemberData(nameof(RevisionASectors))]
    public void RevisionA_ABitmap_IsTheLibrarys(int x, int y, int w, int h)
    {
        // RevisionA.cs WriteCommand(197, x, y, w, h, data): the corners packed into five bytes, then the pixels as
        // RGB565 little-endian, truncated (ScreenBufferRgb353.SetPixel).
        var picture = Picture(320, 480, 1);
        var (device, serial) = Open();
        var screen = TuringSerialScreen.Create(SerialRevision.A, serial);
        using var oracle = V1Screen.Make('A');

        Assert.Equal(PanelDeviceStatus.Ok, screen.Display(picture.Rgb565, 320, new Sector(x, y, x + w, y + h), TestContext.Current.CancellationToken, out var result));
        var expected = oracle.DisplayBuffer(picture.Bitmap, x, y, w, h);

        Assert.Equal(expected, result);
        Assert.Equal(Writes(oracle.Port), Writes(device));
    }

    // ---- Revisions C and E ----

    private static byte[] Answer(string text) => [.. Encoding.ASCII.GetBytes(text), 0];

    [Theory]
    [InlineData('C', "chs_5inch.dev1_rom1.88", true)]
    [InlineData('C', "chs_88inch.dev1_rom1.88", false)]
    [InlineData('E', "chs_88inch.dev1_rom1.88", true)]
    [InlineData('E', "chs_21inch", true)]
    [InlineData('E', "hello", false)]
    [InlineData('E', "", false)]
    public void TheHello_IsTheLibrarys_AndItsAnswerChecked(char letter, string answer, bool accepted)
    {
        // RevisionC.cs / RevisionE.cs Open: the input discarded, Hello (01 ef 69 00 00 00 01 00 00 00 c5 d3) in a 250-byte
        // write, its answer read up to a 0; C wants it to start "chs_5inch", E "chs_".
        var revision = SerialRevision.Of(letter.ToString())!;
        var (device, serial) = Open();
        if (answer.Length > 0)
            device.Serial.Reply(Answer(answer));
        var screen = TuringSerialScreen.Create(revision, serial);
        using var oracle = V1Screen.Make(letter);
        if (answer.Length > 0)
            oracle.Port.Respond = _ => Answer(answer);

        var status = screen.Open(TestContext.Current.CancellationToken, out var why);
        var opened = true;
        try
        {
            oracle.Open();
        }
        catch (IOException)
        {
            opened = false;
        }

        Assert.Equal(accepted, opened);
        Assert.Equal(accepted, status == PanelDeviceStatus.Ok);
        if (!accepted)
            Assert.Contains("answer", why);
        Assert.Equal(Writes(oracle.Port), Writes(device));
        Assert.Equal("01ef6900000001000000c5d3" + new string('0', 238 * 2), Assert.Single(Writes(device)));
        Assert.Equal(PanelTraceOp.SerialDiscard, device.Trace.Entries[1].Op);
    }

    [Theory]
    [InlineData('C')]
    [InlineData('E')]
    public void RevisionsCAndE_ScreenOnBrightnessAndOff_AreTheLibrarys(char letter)
    {
        // SetBrightness: 7b ef 69 00 00 00 01 00 00 00 and the level, flushed to 250 bytes; on and off are 100 and 0
        // (RevisionC.Abstraction.cs, RevisionE.Abstractions.cs).
        var (device, serial) = Open();
        var screen = TuringSerialScreen.Create(SerialRevision.Of(letter.ToString())!, serial);
        using var oracle = V1Screen.Make(letter);

        screen.ScreenOn(TestContext.Current.CancellationToken);
        oracle.ScreenOn();
        foreach (var percent in new[] { 0, 50, 70, 100 })
        {
            screen.SetBrightness(percent, TestContext.Current.CancellationToken);
            oracle.SetBrightness((byte)percent);
        }
        screen.ScreenOff(() => 1000);
        oracle.ScreenOff();

        Assert.Equal(Writes(oracle.Port), Writes(device));
        Assert.Equal("7bef690000000100000046" + new string('0', 239 * 2), Writes(device)[3]);
    }

    public static TheoryData<char, int, int> FullFrames() => new()
    {
        { 'C', 800, 480 },    // the 5"
        { 'E', 480, 1920 },   // the 8.8" Rev 1.0
        { 'E', 480, 480 },    // the 2.1", on the 480 x 1920 screen 1.4.x made for it
    };

    /// <summary>
    /// The library's partial updates of <paramref name="area"/>, block by block: each 80 x 80 block a DisplayBuffer of
    /// its own at its place, in order, until one is refused, as DisplayBitmap cut an area that isn't the whole screen.
    /// </summary>
    internal static bool OracleBlocks(V1Screen oracle, SKBitmap bitmap, Sector area)
    {
        for (var y = area.Top; y < area.Bottom; y += 80)
            for (var x = area.Left; x < area.Right; x += 80)
                if (!oracle.DisplayBuffer(bitmap, x, y, Math.Min(80, area.Right - x), Math.Min(80, area.Bottom - y)))
                    return false;
        return true;
    }

    [Theory]
    [MemberData(nameof(FullFrames))]
    public void AFullFrame_IsTheLibrarysPartialBlocks(char letter, int width, int height)
    {
        // A frame of the screen's size goes as any other area did through DisplayBitmap's partial path: 80 x 80 blocks,
        // each UpdateBitmap, its rows, ef 69, QueryStatus and its answer read. The library's whole-screen command (0x2c,
        // the display command, the payload, PreUpdate and QueryStatus) took longer than a write may (seconds of payload
        // and two answers), so it isn't sent.
        var revision = SerialRevision.Of(letter.ToString())!;
        var picture = Picture(width, height, 2);
        var (device, serial) = Open();
        device.Serial.Respond(w => w.Data[0] == 0xcf ? Encoding.ASCII.GetBytes("needReSend:0") : null);
        var screen = TuringSerialScreen.Create(revision, serial);
        using var oracle = V1Screen.Make(letter);
        oracle.Port.Respond = w => w[0] == 0xcf ? Encoding.ASCII.GetBytes("needReSend:0") : null;

        Assert.Equal(PanelDeviceStatus.Ok, screen.Display(FrameOf(revision, picture), width, new Sector(0, 0, width, height), TestContext.Current.CancellationToken, out var result));
        var expected = OracleBlocks(oracle, picture.Bitmap, new Sector(0, 0, width, height));

        Assert.Equal(expected, result);
        var writes = Writes(device);
        Assert.Equal(Writes(oracle.Port).Count, writes.Count);
        Assert.Equal(Writes(oracle.Port), writes);
    }

    [Theory]
    [InlineData('C', "needReSend:0", true)]
    [InlineData('C', "needReSend:1", false)]
    [InlineData('C', "", false)]
    [InlineData('E', "needReSend:0", true)]
    [InlineData('E', "ok", true)]
    [InlineData('E', "", true)]
    [InlineData('E', "needReSend:1", false)]
    public void Sectors_AreTheLibrarys_AndTheirAnswerIsRead(char letter, string answer, bool accepted)
    {
        // DisplayPartialBitmap: blocks of at most 80 x 80, each UpdateBitmap with its size and the render count (counted
        // from the screen's start: a whole frame is blocks too), each row's position and width, the pixels (E with 255
        // after each), ef 69, QueryStatus, and the answer: C takes only "needReSend:0", E anything but "needReSend:1". A
        // block refused ends the sector.
        var revision = SerialRevision.Of(letter.ToString())!;
        var (width, height) = letter == 'C' ? (800, 480) : (480, 1920);
        var first = Picture(width, height, 3);
        var (device, serial) = Open();
        byte[]? Reply(byte[] w) => w[0] == 0xcf && answer.Length > 0 ? Encoding.ASCII.GetBytes(answer) : null;
        device.Serial.Respond(w => Reply(w.Data));
        var screen = TuringSerialScreen.Create(revision, serial);
        using var oracle = V1Screen.Make(letter);
        oracle.Port.Respond = Reply;

        screen.Display(FrameOf(revision, first), width, new Sector(0, 0, width, height), TestContext.Current.CancellationToken, out _);
        OracleBlocks(oracle, first.Bitmap, new Sector(0, 0, width, height));
        var results = new List<(bool, bool)>();
        foreach (var sector in new[] { new Sector(40, 20, 160, 100), new Sector(0, 0, 20, 20), new Sector(width - 32, height - 32, width, height) })
        {
            Assert.Equal(PanelDeviceStatus.Ok, screen.Display(FrameOf(revision, first), width, sector, TestContext.Current.CancellationToken, out var result));
            results.Add((oracle.DisplayBuffer(first.Bitmap, sector.Left, sector.Top, sector.Width, sector.Height), result));
        }

        Assert.All(results, r => Assert.Equal(r.Item1, r.Item2));
        Assert.All(results, r => Assert.Equal(accepted, r.Item2));
        Assert.Equal(Writes(oracle.Port), Writes(device));
    }

    [Fact]
    public void AWriteThatFails_EndsTheFrameThere()
    {
        // The library's port threw on a write that failed, ending the task's loop (TuringPanelSerialTask.cs:184-187):
        // here the frame answers the write's status and writes nothing more.
        var (device, serial) = Open();
        var screen = TuringSerialScreen.Create(SerialRevision.C, serial);
        device.Fail(device.Calls + 3, PanelDeviceStatus.Timeout);
        var before = device.Calls;

        var status = screen.Display(Picture(800, 480, 4).Bgr, 800, new Sector(0, 0, 800, 480), TestContext.Current.CancellationToken, out var result);

        Assert.Equal(PanelDeviceStatus.Timeout, status);
        Assert.False(result);
        Assert.Equal(before + 3, device.Calls);
    }
}
