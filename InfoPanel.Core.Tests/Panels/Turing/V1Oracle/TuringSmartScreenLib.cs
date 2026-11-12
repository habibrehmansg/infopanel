// TuringSmartScreenLib: Copyright (c) 2021 machi_pon (https://github.com/usausa/turing-smart-screen; the
// full notice is in LICENSES.md). This is the source of 1.4.x's InfoPanel/Lib/TuringSmartScreenLib.dll and
// TuringSmartScreenLib.Helpers.SkiaSharp.dll (4.3.0, built from the library's commit df363d80), which 1.4.x drove its
// serial Turing panels with (Services/TuringPanelSerialTask.cs).

using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace InfoPanel.Core.Tests.Panels.Turing.V1Oracle;

// The library's files at df363d80, copied verbatim but for the port: System.IO.Ports.SerialPort is OracleSerialPort, a
// recorder, and the classes aren't marked unsafe (they hold no pointer; this project compiles no unsafe code). Left
// out: what 1.4.x never called (RevisionB; Reset, Clear and SetOrientation, whose screens 1.4.x never
// turned) and the buffers' Clear (Helper.Fill). Test-only; no device.

// TuringSmartScreenLib/RotateOption.cs
public enum RotateOption
{
    None,
    Rotate90,
    Rotate180,
    Rotate270
}

// TuringSmartScreenLib/RevisionA.cs
internal sealed class TuringSmartScreenRevisionA : IDisposable
{
    private readonly OracleSerialPort port;

    private byte[] writeBuffer;

#pragma warning disable CA1822
    public int Width => 320;

    public int Height => 480;
#pragma warning restore CA1822

    public OracleSerialPort Port => port;

    public TuringSmartScreenRevisionA(string name)
    {
        port = new OracleSerialPort(name)
        {
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 1000,
            BaudRate = 115200,
            DataBits = 8,
            StopBits = StopBits.One,
            Parity = Parity.None
        };
        writeBuffer = ArrayPool<byte>.Shared.Rent(16);
    }

    public void Dispose()
    {
        Close();

        if (writeBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(writeBuffer);
            writeBuffer = [];
        }
    }

    public void Close()
    {
        if (port.IsOpen)
        {
            try
            {
                port.Close();
            }
            catch (IOException)
            {
                // Ignore
            }
            catch (TargetInvocationException)
            {
                // Ignore
            }
        }
    }

    public void Open()
    {
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
    }

    private void WriteCommand(byte command)
    {
        const int commandLength = 6;
        writeBuffer.AsSpan(0, commandLength).Clear();
        writeBuffer[5] = command;

        port.Write(writeBuffer, 0, commandLength);
    }

    private void WriteCommand(byte command, int level)
    {
        const int commandLength = 6;
        writeBuffer.AsSpan(0, commandLength).Clear();
        writeBuffer[0] = (byte)(level >> 2);
        writeBuffer[1] = (byte)((level & 3) << 6);
        writeBuffer[5] = command;

        port.Write(writeBuffer, 0, commandLength);
    }

    private void WriteCommand(byte command, int x, int y, int width, int height, byte[] data)
    {
        var ex = x + width - 1;
        var ey = y + height - 1;

        const int commandLength = 6;
        writeBuffer.AsSpan(0, commandLength).Clear();
        writeBuffer[0] = (byte)(x >> 2);
        writeBuffer[1] = (byte)(((x & 3) << 6) + (y >> 4));
        writeBuffer[2] = (byte)(((y & 15) << 4) + (ex >> 6));
        writeBuffer[3] = (byte)(((ex & 63) << 2) + (ey >> 8));
        writeBuffer[4] = (byte)(ey & 255);
        writeBuffer[5] = command;

        port.Write(writeBuffer, 0, commandLength);
        port.Write(data, 0, width * height * 2);
    }

    public void ScreenOff() => WriteCommand(108);

    public void ScreenOn() => WriteCommand(109);

    public void SetBrightness(int level) => WriteCommand(110, level);

    public bool DisplayBitmap(int x, int y, byte[] bitmap, int width, int height)
    {
        WriteCommand(197, x, y, width, height, bitmap);
        return true;
    }
}

// TuringSmartScreenLib/RevisionC.cs
internal sealed class TuringSmartScreenRevisionC : IDisposable
{
    private const int WriteSize = 250;
    private const int ReadSize = 1024;
    private const int PartialBlockSize = 80;

    private static readonly byte[] CommandHello = [0x01, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xc5, 0xd3];
    private static readonly byte[] CommandSetBrightness = [0x7b, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];
    private static readonly byte[] CommandDisplayBitmap = [0xc8, 0xef, 0x69, 0x00, 0x17, 0x70];
    private static readonly byte[] CommandPreUpdateBitmap = [0x86, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01];
    private static readonly byte[] CommandUpdateBitmap = [0xcc, 0xef, 0x69, 0x00, 0x00];
    private static readonly byte[] CommandQueryStatus = [0xcf, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01];

    private static readonly byte[] CommandUpdateBitmapTerminate = [0xef, 0x69];

    private readonly OracleSerialPort port;

    private byte[] writeBuffer;

    private byte[] readBuffer;

    private int writeOffset;

    private int renderCount;

#pragma warning disable CA1822
    public int Width => 800;

    public int Height => 480;
#pragma warning restore CA1822

    public OracleSerialPort Port => port;

    public TuringSmartScreenRevisionC(string name)
    {
        port = new OracleSerialPort(name)
        {
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 1000,
            WriteTimeout = 1000,
            BaudRate = 115200,
            StopBits = StopBits.One,
            Parity = Parity.None
        };
        writeBuffer = ArrayPool<byte>.Shared.Rent(WriteSize);
        readBuffer = ArrayPool<byte>.Shared.Rent(ReadSize);
    }

    public void Dispose()
    {
        Close();

        if (writeBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(writeBuffer);
            writeBuffer = [];
        }
        if (readBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            readBuffer = [];
        }
    }

    public void Close()
    {
        if (port.IsOpen)
        {
            try
            {
                port.Close();
            }
            catch (IOException)
            {
                // Ignore
            }
            catch (TargetInvocationException)
            {
                // Ignore
            }
        }
    }

    public void Open()
    {
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();

        Write(CommandHello);
        Flush();

        var response = ReadTo();
        if (!response.StartsWith("chs_5inch"u8))
        {
            throw new IOException($"Unknown response. response=[{Convert.ToHexString(response)}]");
        }
    }

    private ReadOnlySpan<byte> ReadResponse(int length = ReadSize)
    {
        var offset = 0;
        try
        {
            while (offset < length)
            {
                var read = port.Read(readBuffer, offset, length - offset);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }
        }
        catch (TimeoutException)
        {
            // Ignore
        }
        catch (IOException)
        {
            // Ignore
        }

        return readBuffer.AsSpan(0, offset);
    }

    private ReadOnlySpan<byte> ReadTo(byte terminator = 0x00)
    {
        var offset = 0;
        try
        {
            while (offset < readBuffer.Length)
            {
                var value = port.ReadByte();
                if (value == terminator)
                {
                    break;
                }

                readBuffer[offset] = (byte)value;
                offset++;
            }
        }
        catch (TimeoutException)
        {
            // Ignore
        }
        catch (IOException)
        {
            // Ignore
        }

        return readBuffer.AsSpan(0, offset);
    }

    private void Write(ReadOnlySpan<byte> values, byte pad = 0x00)
    {
        while (writeOffset + values.Length > WriteSize - 1)
        {
            var block = values[..(WriteSize - 1 - writeOffset)];
            block.CopyTo(writeBuffer.AsSpan(writeOffset, block.Length));
            writeOffset += block.Length;

            FlushInternal(pad);

            values = values[block.Length..];
        }

        if (values.Length > 0)
        {
            values.CopyTo(writeBuffer.AsSpan(writeOffset));
            writeOffset += values.Length;
        }
    }

    private void Write(byte value, byte pad = 0x00)
    {
        if (writeOffset + 1 > WriteSize - 1)
        {
            FlushInternal(pad);
        }

        writeBuffer[writeOffset] = value;
        writeOffset++;
    }

    private void Flush(byte pad = 0x00)
    {
        if (writeOffset > 0)
        {
            FlushInternal(pad);
        }
    }

    private void FlushInternal(byte pad)
    {
        writeBuffer.AsSpan(writeOffset, WriteSize - writeOffset).Fill(pad);
        port.Write(writeBuffer, 0, WriteSize);
        writeOffset = 0;
    }

    public void SetBrightness(int level)
    {
        Write(CommandSetBrightness);
        Write((byte)level);
        Flush();
    }

    private bool IsFullBitmap(int width, int height, RotateOption option)
    {
        if (option is RotateOption.None or RotateOption.Rotate180)
        {
            return width == Width && height == Height;
        }
        return width == Height && height == Width;
    }

    public bool DisplayBitmap(int x, int y, byte[] bitmap, int width, int height, RotateOption option = RotateOption.None)
    {
        if ((x == 0) && (y == 0) && IsFullBitmap(width, height, option))
        {
            DisplayFullBitmap(bitmap, option);
            renderCount = 0;
            return true;
        }

        for (var oy = 0; oy < height; oy += PartialBlockSize)
        {
            for (var ox = 0; ox < width; ox += PartialBlockSize)
            {
                if (!DisplayPartialBitmap(x + ox, y + oy, Math.Min(width - ox, PartialBlockSize), Math.Min(height - oy, PartialBlockSize), bitmap, ox, oy, width, option))
                {
                    return false;
                }
                renderCount++;
            }
        }

        return true;
    }

    private void DisplayFullBitmap(byte[] bitmap, RotateOption option)
    {
        // Start
        Write(0x2c);
        Flush(0x2c);

        // DisplayBitmap
        Write(CommandDisplayBitmap);
        Flush();

        // Payload
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var offset = option switch
                {
                    RotateOption.Rotate90 => ((Width - 1 - x) * Height) + y,
                    RotateOption.Rotate270 => (x * Height) + (Height - 1 - y),
                    RotateOption.Rotate180 => ((Height - 1 - y) * Width) + (Width - 1 - x),
                    _ => (y * Width) + x
                };
                Write(bitmap.AsSpan(offset * 3, 3));
                Write(0xff);
            }
        }
        Flush();

        // PreUpdateBitmap
        Write(CommandPreUpdateBitmap);
        Flush();
        ReadResponse();

        // QueryStatus
        Write(CommandQueryStatus);
        Flush();
        ReadResponse();
    }

    private bool DisplayPartialBitmap(int x, int y, int w, int h, byte[] bitmap, int bitmapX, int bitmapY, int bitmapWidth, RotateOption option)
    {
        var (width, height, startX, startY) = option switch
        {
            RotateOption.Rotate90 => (h, w, Width - y - h, x),
            RotateOption.Rotate270 => (h, w, y, Height - x - w),
            RotateOption.Rotate180 => (w, h, Width - x - w, Height - y - h),
            _ => (w, h, x, y)
        };

        var header = (Span<byte>)stackalloc byte[5];
        header[3] = (byte)((width >> 8) & 0xff);
        header[4] = (byte)(width & 0xff);

        var bitmapSize = (((width * 3) + header.Length) * height) + CommandUpdateBitmapTerminate.Length;
        var size = (Span<byte>)stackalloc byte[2];
        size[0] = (byte)((bitmapSize >> 8) & 0xff);
        size[1] = (byte)(bitmapSize & 0xff);

        Span<byte> countBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(countBytes, renderCount);

        // UpdateBitmap
        Write(CommandUpdateBitmap);
        Write(size);
        Write(stackalloc byte[3]);
        Write(countBytes);
        Flush();

        // Payload
        for (var oy = 0; oy < height; oy++)
        {
            var position = ((startY + oy) * Width) + startX;
            header[0] = (byte)((position >> 16) & 0xff);
            header[1] = (byte)((position >> 8) & 0xff);
            header[2] = (byte)(position & 0xff);
            Write(header);

            for (var ox = 0; ox < width; ox++)
            {
                var (px, py) = option switch
                {
                    RotateOption.Rotate90 => (bitmapX + oy, bitmapY + h - 1 - ox),
                    RotateOption.Rotate270 => (bitmapX + w - 1 - oy, bitmapY + ox),
                    RotateOption.Rotate180 => (bitmapX + w - 1 - ox, bitmapY + h - 1 - oy),
                    _ => (bitmapX + ox, bitmapY + oy)
                };
                Write(bitmap.AsSpan(((py * bitmapWidth) + px) * 3, 3));
            }
        }
        Write(CommandUpdateBitmapTerminate);
        Flush();

        // QueryStatus
        Write(CommandQueryStatus);
        Flush();

        var response = ReadResponse();
        return response.StartsWith("needReSend:0"u8);
    }
}

// TuringSmartScreenLib/RevisionE.cs
internal sealed class TuringSmartScreenRevisionE : IDisposable
{
    private const int WriteSize = 250;
    private const int ReadSize = 1024;
    private const int PartialBlockSize = 80;

    private static readonly byte[] CommandHello = [0x01, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xc5, 0xd3];
    private static readonly byte[] CommandSetBrightness = [0x7b, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];
    private static readonly byte[] CommandDisplayBitmapPrefix = [0xc8, 0xef, 0x69, 0x00];
    private static readonly byte[] CommandPreUpdateBitmap = [0x86, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01];
    private static readonly byte[] CommandUpdateBitmap = [0xcc, 0xef, 0x69, 0x00, 0x00];
    private static readonly byte[] CommandQueryStatus = [0xcf, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01];

    private static readonly byte[] CommandUpdateBitmapTerminate = [0xef, 0x69];

    private readonly byte[] commandDisplayBitmap;

    private readonly OracleSerialPort port;

    private byte[] writeBuffer;

    private byte[] readBuffer;

    private int writeOffset;

    private int renderCount;

    public int Width { get; }

    public int Height { get; }

    public OracleSerialPort Port => port;

    public TuringSmartScreenRevisionE(string name, int width = 480, int height = 1920)
    {
        Width = width;
        Height = height;

        var payloadSize = width * height * 4;
        commandDisplayBitmap = [
            .. CommandDisplayBitmapPrefix,
            (byte)((payloadSize >> 16) & 0xff),
            (byte)((payloadSize >> 8) & 0xff),
            (byte)(payloadSize & 0xff)
        ];

        port = new OracleSerialPort(name)
        {
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 1000,
            WriteTimeout = 1000,
            BaudRate = 115200,
            StopBits = StopBits.One,
            Parity = Parity.None
        };
        writeBuffer = ArrayPool<byte>.Shared.Rent(WriteSize);
        readBuffer = ArrayPool<byte>.Shared.Rent(ReadSize);
    }

    public void Dispose()
    {
        Close();

        if (writeBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(writeBuffer);
            writeBuffer = [];
        }
        if (readBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            readBuffer = [];
        }
    }

    public void Close()
    {
        if (port.IsOpen)
        {
            try
            {
                port.Close();
            }
            catch (IOException)
            {
                // Ignore
            }
            catch (TargetInvocationException)
            {
                // Ignore
            }
        }
    }

    public void Open()
    {
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();

        Write(CommandHello);
        Flush();

        var response = ReadTo();
        if (!response.StartsWith("chs_"u8))
        {
            throw new IOException($"Unknown response. response=[{Convert.ToHexString(response)}]");
        }
    }

    private ReadOnlySpan<byte> ReadResponse(int length = ReadSize)
    {
        var offset = 0;
        try
        {
            while (offset < length)
            {
                var read = port.Read(readBuffer, offset, length - offset);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }
        }
        catch (TimeoutException)
        {
            // Ignore
        }
        catch (IOException)
        {
            // Ignore
        }

        return readBuffer.AsSpan(0, offset);
    }

    private ReadOnlySpan<byte> ReadTo(byte terminator = 0x00)
    {
        var offset = 0;
        try
        {
            while (offset < readBuffer.Length)
            {
                var value = port.ReadByte();
                if (value == terminator)
                {
                    break;
                }

                readBuffer[offset] = (byte)value;
                offset++;
            }
        }
        catch (TimeoutException)
        {
            // Ignore
        }
        catch (IOException)
        {
            // Ignore
        }

        return readBuffer.AsSpan(0, offset);
    }

    private void Write(ReadOnlySpan<byte> values, byte pad = 0x00)
    {
        while (writeOffset + values.Length > WriteSize - 1)
        {
            var block = values[..(WriteSize - 1 - writeOffset)];
            block.CopyTo(writeBuffer.AsSpan(writeOffset, block.Length));
            writeOffset += block.Length;

            FlushInternal(pad);

            values = values[block.Length..];
        }

        if (values.Length > 0)
        {
            values.CopyTo(writeBuffer.AsSpan(writeOffset));
            writeOffset += values.Length;
        }
    }

    private void Write(byte value, byte pad = 0x00)
    {
        if (writeOffset + 1 > WriteSize - 1)
        {
            FlushInternal(pad);
        }

        writeBuffer[writeOffset] = value;
        writeOffset++;
    }

    private void Flush(byte pad = 0x00)
    {
        if (writeOffset > 0)
        {
            FlushInternal(pad);
        }
    }

    private void FlushInternal(byte pad)
    {
        writeBuffer.AsSpan(writeOffset, WriteSize - writeOffset).Fill(pad);
        port.Write(writeBuffer, 0, WriteSize);
        writeOffset = 0;
    }

    public void SetBrightness(int level)
    {
        Write(CommandSetBrightness);
        Write((byte)level);
        Flush();
    }

    private bool IsFullBitmap(int width, int height, RotateOption option)
    {
        if (option is RotateOption.None or RotateOption.Rotate180)
        {
            return width == Width && height == Height;
        }
        return width == Height && height == Width;
    }

    public bool DisplayBitmap(int x, int y, byte[] bitmap, int width, int height, RotateOption option = RotateOption.None)
    {
        if ((x == 0) && (y == 0) && IsFullBitmap(width, height, option))
        {
            DisplayFullBitmap(bitmap, option);
            renderCount = 0;
            return true;
        }

        for (var oy = 0; oy < height; oy += PartialBlockSize)
        {
            for (var ox = 0; ox < width; ox += PartialBlockSize)
            {
                if (!DisplayPartialBitmap(x + ox, y + oy, Math.Min(width - ox, PartialBlockSize), Math.Min(height - oy, PartialBlockSize), bitmap, ox, oy, width, option))
                {
                    return false;
                }
                renderCount++;
            }
        }

        return true;
    }

    private void DisplayFullBitmap(byte[] bitmap, RotateOption option)
    {
        // Start
        Write(0x2c);
        Flush(0x2c);

        // DisplayBitmap
        Write(commandDisplayBitmap);
        Flush();

        // Payload
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var offset = option switch
                {
                    RotateOption.Rotate90 => ((Width - 1 - x) * Height) + y,
                    RotateOption.Rotate270 => (x * Height) + (Height - 1 - y),
                    RotateOption.Rotate180 => ((Height - 1 - y) * Width) + (Width - 1 - x),
                    _ => (y * Width) + x
                };
                Write(bitmap.AsSpan(offset * 4, 4));
            }
        }
        Flush();

        // PreUpdateBitmap
        Write(CommandPreUpdateBitmap);
        Flush();
        ReadResponse();

        // QueryStatus
        Write(CommandQueryStatus);
        Flush();
        ReadResponse();
    }

    private bool DisplayPartialBitmap(int x, int y, int w, int h, byte[] bitmap, int bitmapX, int bitmapY, int bitmapWidth, RotateOption option)
    {
        var (width, height, startX, startY) = option switch
        {
            RotateOption.Rotate90 => (h, w, Width - y - h, x),
            RotateOption.Rotate270 => (h, w, y, Height - x - w),
            RotateOption.Rotate180 => (w, h, Width - x - w, Height - y - h),
            _ => (w, h, x, y)
        };

        var header = (Span<byte>)stackalloc byte[5];
        header[3] = (byte)((width >> 8) & 0xff);
        header[4] = (byte)(width & 0xff);

        var bitmapSize = (((width * 4) + header.Length) * height) + CommandUpdateBitmapTerminate.Length;
        var size = (Span<byte>)stackalloc byte[2];
        size[0] = (byte)((bitmapSize >> 8) & 0xff);
        size[1] = (byte)(bitmapSize & 0xff);

        Span<byte> countBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(countBytes, renderCount);

        // UpdateBitmap
        Write(CommandUpdateBitmap);
        Write(size);
        Write(stackalloc byte[3]);
        Write(countBytes);
        Flush();

        // Payload
        for (var oy = 0; oy < height; oy++)
        {
            var position = ((startY + oy) * Width) + startX;
            header[0] = (byte)((position >> 16) & 0xff);
            header[1] = (byte)((position >> 8) & 0xff);
            header[2] = (byte)(position & 0xff);
            Write(header);

            for (var ox = 0; ox < width; ox++)
            {
                var (px, py) = option switch
                {
                    RotateOption.Rotate90 => (bitmapX + oy, bitmapY + h - 1 - ox),
                    RotateOption.Rotate270 => (bitmapX + w - 1 - oy, bitmapY + ox),
                    RotateOption.Rotate180 => (bitmapX + w - 1 - ox, bitmapY + h - 1 - oy),
                    _ => (bitmapX + ox, bitmapY + oy)
                };
                Write(bitmap.AsSpan(((py * bitmapWidth) + px) * 4, 4));
            }
        }
        Write(CommandUpdateBitmapTerminate);
        Flush();

        // QueryStatus
        Write(CommandQueryStatus);
        Flush();

        var response = ReadResponse();
        // Succeed unless the device explicitly requests a resend.
        // Different panel models return different success responses,
        // but all use "needReSend:1" to indicate failure.
        return !response.StartsWith("needReSend:1"u8);
    }
}

// TuringSmartScreenLib/ScreenBufferRgb353.cs (its Clear left out)
internal sealed class ScreenBufferRgb353
{
    private readonly int width;

    private readonly int height;

    private byte[] buffer;

    public int Width => width;

    public int Height => height;

    internal byte[] Buffer => buffer;

    public ScreenBufferRgb353(int width, int height)
    {
        this.width = width;
        this.height = height;
        buffer = ArrayPool<byte>.Shared.Rent(width * height * 2);
        buffer.AsSpan().Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPixel(int x, int y, byte r, byte g, byte b)
    {
        var rgb = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
        var offset = ((y * width) + x) * 2;
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset), (short)rgb);
    }
}

// TuringSmartScreenLib/ScreenBufferBgr888.cs (its Clear left out)
internal sealed class ScreenBufferBgr888
{
    private readonly int width;

    private readonly int height;

    private byte[] buffer;

    public int Width => width;

    public int Height => height;

    internal byte[] Buffer => buffer;

    public ScreenBufferBgr888(int width, int height)
    {
        this.width = width;
        this.height = height;
        buffer = ArrayPool<byte>.Shared.Rent(width * height * 3);
        buffer.AsSpan().Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPixel(int x, int y, byte r, byte g, byte b)
    {
        var offset = ((y * width) + x) * 3;
        buffer[offset] = b;
        buffer[offset + 1] = g;
        buffer[offset + 2] = r;
    }
}

// TuringSmartScreenLib/ScreenBufferBgra8888.cs (its Clear left out)
internal sealed class ScreenBufferBgra8888
{
    private readonly int width;

    private readonly int height;

    private byte[] buffer;

    public int Width => width;

    public int Height => height;

    internal byte[] Buffer => buffer;

    public ScreenBufferBgra8888(int width, int height)
    {
        this.width = width;
        this.height = height;
        buffer = ArrayPool<byte>.Shared.Rent(width * height * 4);
        buffer.AsSpan().Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPixel(int x, int y, byte r, byte g, byte b)
    {
        var offset = ((y * width) + x) * 4;
        buffer[offset] = b;
        buffer[offset + 1] = g;
        buffer[offset + 2] = r;
        buffer[offset + 3] = 255;
    }
}

/// <summary>
/// The three screens as 1.4.x drove them through the library's <c>IScreen</c>: <c>ScreenFactory.Create</c> (the screen
/// made and opened; revision E with the factory's default 480 × 1920, 1.4.x passing no size), and each wrapper's
/// <c>ScreenOn</c>, <c>ScreenOff</c>, <c>SetBrightness</c> and <c>DisplayBuffer</c> (<c>RevisionA.Abstraction.cs</c>,
/// <c>RevisionC.Abstraction.cs</c>, <c>RevisionE.Abstractions.cs</c>: A's brightness <c>255 - level / 100 * 255</c>; C's
/// and E's screen on and off emulated by brightness 100 and 0; C's orientation Landscape and E's Portrait, so no turn),
/// with <c>CreateBufferFrom</c> (<c>TuringSmartScreenLib.Helpers.SkiaSharp/Extensions.cs</c>).
/// </summary>
internal sealed class V1Screen : IDisposable
{
    private readonly TuringSmartScreenRevisionA? _a;
    private readonly TuringSmartScreenRevisionC? _c;
    private readonly TuringSmartScreenRevisionE? _e;

    private V1Screen(TuringSmartScreenRevisionA? a, TuringSmartScreenRevisionC? c, TuringSmartScreenRevisionE? e) => (_a, _c, _e) = (a, c, e);

    public OracleSerialPort Port => _a?.Port ?? _c?.Port ?? _e!.Port;

    /// <summary>ScreenFactory.Create's screen, not yet opened (so a test can script the port first).</summary>
    public static V1Screen Make(char revision) => revision switch
    {
        'A' => new(new TuringSmartScreenRevisionA("COM9"), null, null),
        'C' => new(null, new TuringSmartScreenRevisionC("COM9"), null),
        'E' => new(null, null, new TuringSmartScreenRevisionE("COM9", 480, 1920)),
        _ => throw new NotSupportedException("Unsupported type."),
    };

    public void Open()
    {
        if (_a != null) _a.Open();
        else if (_c != null) _c.Open();
        else _e!.Open();
    }

    public void ScreenOn()
    {
        if (_a != null) _a.ScreenOn();
        else SetBrightness(100);
    }

    public void ScreenOff()
    {
        if (_a != null) _a.ScreenOff();
        else SetBrightness(0);
    }

    public void SetBrightness(byte level)
    {
        if (_a != null) _a.SetBrightness(255 - (byte)((float)level / 100 * 255));
        else if (_c != null) _c.SetBrightness(level);
        else _e!.SetBrightness(level);
    }

    /// <summary><c>screen.DisplayBuffer(x, y, screen.CreateBufferFrom(bitmap, x, y, w, h))</c>.</summary>
    public bool DisplayBuffer(SKBitmap bitmap, int x, int y, int w, int h)
    {
        if (_a != null)
        {
            var buffer = new ScreenBufferRgb353(w, h);
            ReadFrom(bitmap, x, y, w, h, buffer.SetPixel);
            return _a.DisplayBitmap(x, y, buffer.Buffer, buffer.Width, buffer.Height);
        }
        if (_c != null)
        {
            var buffer = new ScreenBufferBgr888(w, h);
            ReadFrom(bitmap, x, y, w, h, buffer.SetPixel);
            return _c.DisplayBitmap(x, y, buffer.Buffer, buffer.Width, buffer.Height, RotateOption.None);
        }
        var bgra = new ScreenBufferBgra8888(w, h);
        ReadFrom(bitmap, x, y, w, h, bgra.SetPixel);
        return _e!.DisplayBitmap(x, y, bgra.Buffer, bgra.Width, bgra.Height, RotateOption.None);
    }

    // TuringSmartScreenLib.Helpers.SkiaSharp/Extensions.cs, ReadFrom
    private static void ReadFrom(SKBitmap bitmap, int sx, int sy, int sw, int sh, Action<int, int, byte, byte, byte> setPixel)
    {
        for (var y = 0; y < sh; y++)
        {
            for (var x = 0; x < sw; x++)
            {
                var color = bitmap.GetPixel(x + sx, y + sy);
                setPixel(x, y, color.Red, color.Green, color.Blue);
            }
        }
    }

    public void Dispose()
    {
        _a?.Dispose();
        _c?.Dispose();
        _e?.Dispose();
    }
}
