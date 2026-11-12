// Ported from TuringSmartScreenLib (RevisionA.cs, RevisionC.cs, RevisionE.cs and their wrappers, ScreenBufferRgb353.cs,
// ScreenBufferBgr888.cs, ScreenBufferBgra8888.cs), the library 1.4.x drove its serial Turing panels with:
// Copyright (c) 2021 machi_pon (https://github.com/usausa/turing-smart-screen; the full notice is in
// LICENSES.md).

using System.Buffers.Binary;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Turing;

/// <summary>
/// A serial revision as 1.4 drove it (<c>Services/TuringPanelSerialTask.cs:43-72</c>): its pixels as the engine sends
/// them (A: RGB565 little-endian, truncated, as the library's <c>ScreenBufferRgb353</c> made it; C and E: BGR, which E
/// sends with 255 after each pixel), the sectors its changes are cut into, the merge limit, and the most sectors a
/// frame sends before it goes whole.
/// </summary>
internal sealed record SerialRevision(char Letter, PanelFrameFormat Format, int BytesPerPixel, int SectorWidth, int SectorHeight,
    int MaxSectorWidth, int MaxSectorHeight, int MaxSectors)
{
    public static readonly SerialRevision A = new('A', PanelFrameFormat.Rgb565LeTruncated, 2, 20, 20, 40, 40, 76);
    public static readonly SerialRevision C = new('C', PanelFrameFormat.Bgr888, 3, 20, 20, 120, 80, 30);
    public static readonly SerialRevision E = new('E', PanelFrameFormat.Bgr888, 3, 32, 32, 128, 96, 38);

    /// <summary>The revision <c>panel.json</c>'s <c>revision</c> names; null for any other (the XuanFang, which 1.4 didn't drive).</summary>
    public static SerialRevision? Of(string? letter) => letter switch
    {
        "A" => A,
        "C" => C,
        "E" => E,
        _ => null,
    };
}

/// <summary>
/// A serial Turing screen over its opened port, as the library spoke to it, its port's calls as statuses: a write that
/// fails ends what was under way with its status (the library's port threw); a read that times out or fails is no
/// answer (the library ignored both), but one that finds the device gone ends what was under way too. Every write and read keeps to the library's timeouts (C and E 1000 ms each;
/// A's writes had none, so a whole frame gets the seam's longest), and once <see cref="ScreenOff"/> gives a budget, to
/// what is left of it.
/// </summary>
internal abstract class TuringSerialScreen
{
    /// <summary>The library's <c>ReadTimeout</c>.</summary>
    public const int ReadTimeoutMs = 1000;

    protected readonly IPanelSerial Port;
    private Func<int>? _left;

    protected TuringSerialScreen(IPanelSerial port) => Port = port;

    public static TuringSerialScreen Create(SerialRevision revision, IPanelSerial port) =>
        revision.Letter == 'A' ? new RevisionAScreen(port) : new RevisionCeScreen(revision.Letter, port);

    /// <summary>Whether a call found the device gone.</summary>
    public bool Gone { get; protected set; }

    /// <summary>The port's input dropped; for C and E, Hello sent and its answer checked.</summary>
    public abstract PanelDeviceStatus Open(CancellationToken stop, out string why);

    public abstract PanelDeviceStatus ScreenOn(CancellationToken stop);

    /// <summary>The brightness, 0–100, as the library's wrapper mapped it.</summary>
    public abstract PanelDeviceStatus SetBrightness(int percent, CancellationToken stop);

    /// <summary>The screen off, each write within what <paramref name="left"/> says is left (none once nothing is).</summary>
    public PanelDeviceStatus ScreenOff(Func<int> left)
    {
        _left = left;
        return Off();
    }

    protected abstract PanelDeviceStatus Off();

    /// <summary>
    /// <c>DisplayBuffer(x, y, CreateBufferFrom(bitmap, x, y, w, h))</c>: the <paramref name="sector"/> of the frame (each
    /// row <paramref name="frameWidth"/> pixels) sent at its place; <paramref name="result"/> is the library's answer
    /// (false: the panel asked for the frame again).
    /// </summary>
    public abstract PanelDeviceStatus Display(ReadOnlySpan<byte> frame, int frameWidth, Sector sector, CancellationToken stop, out bool result);

    /// <summary>
    /// The pieces <paramref name="area"/> (the whole frame when <paramref name="whole"/>) is sent in, in order, each a
    /// <see cref="Display"/> of its own that sends what the library's one <c>DisplayBuffer</c> of the area did for it, so
    /// a frame's write can stop between them and go on with the next write.
    /// </summary>
    public abstract IEnumerable<Sector> Pieces(Sector area, bool whole);

    protected PanelDeviceStatus Send(ReadOnlySpan<byte> data, int timeoutMs, CancellationToken stop)
    {
        if (!Within(ref timeoutMs))
            return PanelDeviceStatus.Timeout;
        return Remember(Port.Write(data, timeoutMs, stop));
    }

    protected PanelDeviceStatus Receive(Span<byte> buffer, CancellationToken stop, out int read)
    {
        read = 0;
        var timeoutMs = ReadTimeoutMs;
        if (!Within(ref timeoutMs))
            return PanelDeviceStatus.Timeout;
        return Remember(Port.Read(buffer, timeoutMs, out read, stop));
    }

    private bool Within(ref int timeoutMs)
    {
        if (_left == null)
            return true;
        var left = _left();
        if (left < 1)
            return false;
        timeoutMs = Math.Min(timeoutMs, left);
        return true;
    }

    private PanelDeviceStatus Remember(PanelDeviceStatus status)
    {
        if (status == PanelDeviceStatus.Gone)
            Gone = true;
        return status;
    }
}

/// <summary>Revision A (the 3.5"): six-byte commands, and a bitmap as its corners and its RGB565 pixels.</summary>
internal sealed class RevisionAScreen(IPanelSerial port) : TuringSerialScreen(port)
{
    public const byte ScreenOffCommand = 108;
    public const byte ScreenOnCommand = 109;
    public const byte BrightnessCommand = 110;
    public const byte BitmapCommand = 197;

    /// <summary>The library set no write timeout for revision A; a frame of 300 KB at 115200 baud takes about 27 s.</summary>
    private const int WriteTimeoutMs = IPanelDevices.MaxTimeoutMs;

    /// <summary>
    /// The most bytes of pixels in a band of a whole frame: 10,240 (16 rows of the 3.5"), about 0.9 s at 115200 baud. A
    /// whole frame goes as bands, each its own bitmap command at its place, as the library drew any part of the screen.
    /// </summary>
    public const int BandBytes = 10_240;

    public override IEnumerable<Sector> Pieces(Sector area, bool whole)
    {
        if (!whole)
        {
            yield return area;
            yield break;
        }
        var rows = Math.Max(1, BandBytes / (area.Width * 2));
        for (var top = area.Top; top < area.Bottom; top += rows)
            yield return new Sector(area.Left, top, area.Right, Math.Min(top + rows, area.Bottom));
    }

    public override PanelDeviceStatus Open(CancellationToken stop, out string why)
    {
        why = "";
        stop.ThrowIfCancellationRequested();
        return Port.DiscardInput();
    }

    public override PanelDeviceStatus ScreenOn(CancellationToken stop) => Send(Command(ScreenOnCommand), WriteTimeoutMs, stop);

    protected override PanelDeviceStatus Off() => Send(Command(ScreenOffCommand), WriteTimeoutMs, CancellationToken.None);

    /// <summary><c>RevisionA.Abstraction.cs</c>: the level is <c>255 - (byte)(percent / 100f * 255)</c>, 0 the brightest.</summary>
    public static int Level(int percent) => 255 - (byte)((float)percent / 100 * 255);

    public override PanelDeviceStatus SetBrightness(int percent, CancellationToken stop)
    {
        var level = Level(percent);
        var command = Command(BrightnessCommand);
        command[0] = (byte)(level >> 2);
        command[1] = (byte)((level & 3) << 6);
        return Send(command, WriteTimeoutMs, stop);
    }

    private static byte[] Command(byte command)
    {
        var bytes = new byte[6];
        bytes[5] = command;
        return bytes;
    }

    /// <summary><c>RevisionA.cs</c>'s <c>WriteCommand(197, x, y, width, height, data)</c> header.</summary>
    public static byte[] BitmapHeader(int x, int y, int width, int height)
    {
        var ex = x + width - 1;
        var ey = y + height - 1;
        var bytes = new byte[6];
        bytes[0] = (byte)(x >> 2);
        bytes[1] = (byte)(((x & 3) << 6) + (y >> 4));
        bytes[2] = (byte)(((y & 15) << 4) + (ex >> 6));
        bytes[3] = (byte)(((ex & 63) << 2) + (ey >> 8));
        bytes[4] = (byte)(ey & 255);
        bytes[5] = BitmapCommand;
        return bytes;
    }

    public override PanelDeviceStatus Display(ReadOnlySpan<byte> frame, int frameWidth, Sector sector, CancellationToken stop, out bool result)
    {
        result = false;
        var pixels = new byte[sector.Width * sector.Height * 2];
        for (var row = 0; row < sector.Height; row++)
            frame.Slice((((sector.Top + row) * frameWidth) + sector.Left) * 2, sector.Width * 2).CopyTo(pixels.AsSpan(row * sector.Width * 2));
        var status = Send(BitmapHeader(sector.Left, sector.Top, sector.Width, sector.Height), WriteTimeoutMs, stop);
        if (status == PanelDeviceStatus.Ok)
            status = Send(pixels, WriteTimeoutMs, stop);
        result = status == PanelDeviceStatus.Ok; // DisplayBitmap answered true
        return status;
    }
}

/// <summary>
/// Revisions C (the 5", 800 × 480) and E (the 8.8" Rev 1.0 and the 2.1", on the 480 × 1920 screen 1.4's factory made
/// for both): commands and pixels in 250-byte writes (at most 249 bytes of each, the rest padding), every frame, a whole
/// one too, in blocks of at most 80 × 80 pixels, each answered.
/// </summary>
internal sealed class RevisionCeScreen : TuringSerialScreen
{
    private const int WriteSize = 250;
    private const int ReadSize = 1024;
    private const int PartialBlockSize = 80;
    private const int WriteTimeoutMs = 1000;

    public static readonly byte[] CommandHello = [0x01, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xc5, 0xd3];
    private static readonly byte[] CommandSetBrightness = [0x7b, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];
    private static readonly byte[] CommandUpdateBitmap = [0xcc, 0xef, 0x69, 0x00, 0x00];
    private static readonly byte[] CommandQueryStatus = [0xcf, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01];
    private static readonly byte[] CommandUpdateBitmapTerminate = [0xef, 0x69];

    private readonly char _letter;
    private readonly int _width;
    private readonly int _pixelBytes; // C 3, E 4
    private readonly byte[] _writeBuffer = new byte[WriteSize];
    private readonly byte[] _readBuffer = new byte[ReadSize];
    private readonly List<byte> _pending = []; // what a read brought past the answer's end, for the next read
    private int _writeOffset;
    private int _renderCount;
    private PanelDeviceStatus _failure;
    private CancellationToken _stop;

    public RevisionCeScreen(char letter, IPanelSerial port) : base(port)
    {
        _letter = letter;
        // C's screen is 800 x 480; E's (ScreenFactory.Create(RevisionE, name)) 480 x 1920 when no size is given, as 1.4
        // gave none. A block's rows are placed by their position on it, so only its width matters here.
        (_width, _pixelBytes) = letter == 'C' ? (800, 3) : (480, 4);
    }

    private void Begin(CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        _stop = stop;
        _failure = PanelDeviceStatus.Ok;
        _writeOffset = 0;
    }

    public override PanelDeviceStatus Open(CancellationToken stop, out string why)
    {
        why = "";
        Begin(stop);
        _pending.Clear();
        var status = Port.DiscardInput();
        if (status != PanelDeviceStatus.Ok)
        {
            why = "its port's input couldn't be dropped";
            return status;
        }
        Write(CommandHello);
        Flush();
        if (_failure != PanelDeviceStatus.Ok)
        {
            why = "it took no Hello";
            return _failure;
        }
        var response = ReadTo();
        var expected = _letter == 'C' ? "chs_5inch"u8 : "chs_"u8;
        if (response.StartsWith(expected))
            return PanelDeviceStatus.Ok;
        if (response.Length == 0)
        {
            why = "it didn't answer Hello";
            return Gone ? PanelDeviceStatus.Gone : PanelDeviceStatus.Timeout;
        }
        why = $"its answer to Hello isn't a revision {_letter} panel's ({Convert.ToHexString(response[..Math.Min(response.Length, 32)])})";
        return PanelDeviceStatus.NotSupported;
    }

    public override PanelDeviceStatus ScreenOn(CancellationToken stop) => SetBrightness(100, stop);

    protected override PanelDeviceStatus Off() => SetBrightness(0, CancellationToken.None);

    public override PanelDeviceStatus SetBrightness(int percent, CancellationToken stop)
    {
        Begin(stop);
        Write(CommandSetBrightness);
        Write((byte)percent);
        Flush();
        return _failure;
    }

    public override PanelDeviceStatus Display(ReadOnlySpan<byte> frame, int frameWidth, Sector sector, CancellationToken stop, out bool result)
    {
        Begin(stop);
        // CreateBufferFrom: the sector's pixels as the screen's buffer, C's BGR as it is, E's with 255 after each.
        var width = sector.Width;
        var height = sector.Height;
        var bitmap = new byte[width * height * _pixelBytes];
        for (var y = 0; y < height; y++)
        {
            var source = frame.Slice((((sector.Top + y) * frameWidth) + sector.Left) * 3, width * 3);
            if (_pixelBytes == 3)
            {
                source.CopyTo(bitmap.AsSpan(y * width * 3));
                continue;
            }
            var target = bitmap.AsSpan(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                target[x * 4] = source[x * 3];
                target[(x * 4) + 1] = source[(x * 3) + 1];
                target[(x * 4) + 2] = source[(x * 3) + 2];
                target[(x * 4) + 3] = 255;
            }
        }
        result = DisplayBitmap(sector.Left, sector.Top, bitmap, width, height) && _failure == PanelDeviceStatus.Ok && !Gone;
        // A read that found the device gone is no answer to the library, but the device is gone: so is the frame.
        return Gone && _failure == PanelDeviceStatus.Ok ? PanelDeviceStatus.Gone : _failure;
    }

    /// <summary>
    /// Any area, the screen's whole size too, in the blocks the library's partial updates cut it into, in its order. The
    /// library sent a frame of the screen's own size as one command (0x2c, the display command, the whole payload, then
    /// PreUpdate and QueryStatus, each answer's read ending only when a read brings nothing in 1000 ms): the 8.8"'s
    /// 3.7 MB with its answers about 5.7 s at 1 MB/s, the 5"'s about 3.5 s, past what a write and a stop may take
    /// (<see cref="TuringSerialSession.Budget"/>). Neither needs that command first: the 2.1", on the same 480 × 1920
    /// screen, never got one from the library.
    /// </summary>
    public override IEnumerable<Sector> Pieces(Sector area, bool whole)
    {
        for (var oy = 0; oy < area.Height; oy += PartialBlockSize)
            for (var ox = 0; ox < area.Width; ox += PartialBlockSize)
                yield return new Sector(area.Left + ox, area.Top + oy, area.Left + ox + Math.Min(area.Width - ox, PartialBlockSize),
                    area.Top + oy + Math.Min(area.Height - oy, PartialBlockSize));
    }

    // DisplayBitmap, with RotateOption.None (1.4 never turned these screens), and its partial path for every area: the
    // whole-screen command isn't sent (Pieces), so the render count counts on from the screen's start.
    private bool DisplayBitmap(int x, int y, byte[] bitmap, int width, int height)
    {
        for (var oy = 0; oy < height; oy += PartialBlockSize)
        {
            for (var ox = 0; ox < width; ox += PartialBlockSize)
            {
                if (!DisplayPartialBitmap(x + ox, y + oy, Math.Min(width - ox, PartialBlockSize), Math.Min(height - oy, PartialBlockSize), bitmap, ox, oy, width))
                    return false;
                _renderCount++;
            }
        }

        return true;
    }

    private bool DisplayPartialBitmap(int x, int y, int w, int h, byte[] bitmap, int bitmapX, int bitmapY, int bitmapWidth)
    {
        if (_failure != PanelDeviceStatus.Ok)
            return false;
        Span<byte> header = stackalloc byte[5];
        header[3] = (byte)((w >> 8) & 0xff);
        header[4] = (byte)(w & 0xff);

        var bitmapSize = (((w * _pixelBytes) + header.Length) * h) + CommandUpdateBitmapTerminate.Length;
        Span<byte> size = [(byte)((bitmapSize >> 8) & 0xff), (byte)(bitmapSize & 0xff)];

        Span<byte> countBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(countBytes, _renderCount);

        // UpdateBitmap
        Write(CommandUpdateBitmap);
        Write(size);
        Write(stackalloc byte[3]);
        Write(countBytes);
        Flush();

        // Payload
        for (var oy = 0; oy < h; oy++)
        {
            var position = ((y + oy) * _width) + x;
            header[0] = (byte)((position >> 16) & 0xff);
            header[1] = (byte)((position >> 8) & 0xff);
            header[2] = (byte)(position & 0xff);
            Write(header);
            Write(bitmap.AsSpan((((bitmapY + oy) * bitmapWidth) + bitmapX) * _pixelBytes, w * _pixelBytes));
        }
        Write(CommandUpdateBitmapTerminate);
        Flush();

        // QueryStatus
        Write(CommandQueryStatus);
        Flush();

        var response = ReadResponse();
        // C wants "needReSend:0"; E takes anything but "needReSend:1" (its panels answer differently).
        return _letter == 'C' ? response.StartsWith("needReSend:0"u8) : !response.StartsWith("needReSend:1"u8);
    }

    // ReadResponse: what comes, up to 1024 bytes, until a read brings nothing in its time
    private ReadOnlySpan<byte> ReadResponse()
    {
        if (_failure != PanelDeviceStatus.Ok)
            return [];
        var offset = Math.Min(_pending.Count, ReadSize);
        _pending.CopyTo(0, _readBuffer, 0, offset);
        _pending.RemoveRange(0, offset);
        while (offset < ReadSize)
        {
            if (Answered(Receive(_readBuffer.AsSpan(offset), _stop, out var read)) != PanelDeviceStatus.Ok || read <= 0)
                break;
            offset += read;
        }
        return _readBuffer.AsSpan(0, offset);
    }

    // A read that timed out or failed is no answer, as the library took it; one that found the device gone ends what is
    // under way as a failed write does: nothing more is sent.
    private PanelDeviceStatus Answered(PanelDeviceStatus status)
    {
        if (status == PanelDeviceStatus.Gone && _failure == PanelDeviceStatus.Ok)
            _failure = PanelDeviceStatus.Gone;
        return status;
    }

    // ReadTo(0): the bytes before the first 0, at most 1024; what came after it stays for the next read
    private ReadOnlySpan<byte> ReadTo(byte terminator = 0x00)
    {
        var offset = 0;
        var chunk = new byte[ReadSize];
        while (offset < ReadSize)
        {
            if (_pending.Count == 0)
            {
                if (Answered(Receive(chunk, _stop, out var read)) != PanelDeviceStatus.Ok || read <= 0)
                    break;
                _pending.AddRange(chunk.AsSpan(0, read));
            }
            var value = _pending[0];
            _pending.RemoveAt(0);
            if (value == terminator)
                break;
            _readBuffer[offset++] = value;
        }
        return _readBuffer.AsSpan(0, offset);
    }

    private void Write(ReadOnlySpan<byte> values, byte pad = 0x00)
    {
        while (_writeOffset + values.Length > WriteSize - 1)
        {
            var block = values[..(WriteSize - 1 - _writeOffset)];
            block.CopyTo(_writeBuffer.AsSpan(_writeOffset, block.Length));
            _writeOffset += block.Length;

            FlushInternal(pad);

            values = values[block.Length..];
        }

        if (values.Length > 0)
        {
            values.CopyTo(_writeBuffer.AsSpan(_writeOffset));
            _writeOffset += values.Length;
        }
    }

    private void Write(byte value, byte pad = 0x00)
    {
        if (_writeOffset + 1 > WriteSize - 1)
            FlushInternal(pad);

        _writeBuffer[_writeOffset] = value;
        _writeOffset++;
    }

    private void Flush(byte pad = 0x00)
    {
        if (_writeOffset > 0)
            FlushInternal(pad);
    }

    private void FlushInternal(byte pad)
    {
        _writeBuffer.AsSpan(_writeOffset, WriteSize - _writeOffset).Fill(pad);
        _writeOffset = 0;
        if (_failure != PanelDeviceStatus.Ok)
            return; // the library's port had thrown: nothing more goes
        _failure = Send(_writeBuffer, WriteTimeoutMs, _stop);
    }
}
