namespace TuringSmartScreenLib;

using System;
using System.Buffers;
using System.IO.Ports;
using System.Reflection;

public sealed class TuringSmartScreenRevisionB : IDisposable
{
    private static readonly byte[] CommandHello = [0xCA, (byte)'H', (byte)'E', (byte)'L', (byte)'L', (byte)'O', 0, 0, 0, 0xCA];

    public enum Orientation : byte
    {
        Portrait = 0,
        Landscape = 1
    }

    private readonly SerialPort port;

    private byte[] writeBuffer;

    private byte[] readBuffer;

    public byte Version { get; private set; }

#pragma warning disable CA1822
    public int Width => 320;

    public int Height => 480;
#pragma warning restore CA1822

    public TuringSmartScreenRevisionB(string name)
    {
        port = new SerialPort(name)
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
        readBuffer = ArrayPool<byte>.Shared.Rent(16);
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

    public void Dispose()
    {
        Close();
    }

    public void Open()
    {
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();

        port.Write(CommandHello, 0, CommandHello.Length);

        var response = ReadResponse(10);
        if ((response.Length == 10) &&
            (response[0] == 0xCA) &&
            (response[1] == (byte)'H') &&
            (response[2] == (byte)'E') &&
            (response[3] == (byte)'L') &&
            (response[4] == (byte)'L') &&
            (response[5] == (byte)'O') &&
            (response[9] == 0xCA))
        {
            if (response[6] == 0x0A)
            {
                Version = response[7];
            }
        }

        port.DiscardInBuffer();
    }

    private ReadOnlySpan<byte> ReadResponse(int length)
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

    private void WriteCommand(byte command, byte value)
    {
        const int commandLength = 10;
        writeBuffer.AsSpan(0, commandLength).Clear();
        writeBuffer[0] = command;
        writeBuffer[1] = value;
        writeBuffer[9] = command;

        port.Write(writeBuffer, 0, commandLength);
    }

    private void WriteCommand(byte command, int x, int y, int width, int height)
    {
        var ex = x + width - 1;
        var ey = y + height - 1;

        const int commandLength = 10;
        writeBuffer.AsSpan(0, commandLength).Clear();
        writeBuffer[0] = command;
        writeBuffer[1] = (byte)((x >> 8) & 0xFF);
        writeBuffer[2] = (byte)(x & 0xFF);
        writeBuffer[3] = (byte)((y >> 8) & 0xFF);
        writeBuffer[4] = (byte)(y & 0xFF);
        writeBuffer[5] = (byte)((ex >> 8) & 0xFF);
        writeBuffer[6] = (byte)(ex & 0xFF);
        writeBuffer[7] = (byte)((ey >> 8) & 0xFF);
        writeBuffer[8] = (byte)(ey & 0xFF);
        writeBuffer[9] = command;

        port.Write(writeBuffer, 0, commandLength);
    }

    public void SetBrightness(byte level) => WriteCommand(0xCE, level);

    public void SetOrientation(Orientation orientation) => WriteCommand(0xCB, (byte)orientation);

    public bool DisplayBitmap(int x, int y, byte[] bitmap, int width, int height)
    {
        WriteCommand(0xCC, x, y, width, height);
        port.Write(bitmap, 0, width * height * 2);
        return true;
    }
}
