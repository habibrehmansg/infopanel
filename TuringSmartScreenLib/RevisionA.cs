namespace TuringSmartScreenLib;

using System.Buffers;
using System.IO.Ports;
using System.Reflection;

public sealed class TuringSmartScreenRevisionA : IDisposable
{
    public enum Orientation : byte
    {
        Portrait = 0,
        ReversePortrait = 1,
        Landscape = 2,
        ReverseLandscape = 3
    }

    private readonly SerialPort port;

    private byte[] writeBuffer;

#pragma warning disable CA1822
    public int Width => 320;

    public int Height => 480;
#pragma warning restore CA1822

    public TuringSmartScreenRevisionA(string name)
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

    private void WriteCommand(byte command, byte orientation, int width, int height)
    {
        const int commandLength = 11;
        writeBuffer.AsSpan(0, commandLength).Clear();
        writeBuffer[5] = command;
        writeBuffer[6] = (byte)(orientation + 100);
        writeBuffer[7] = (byte)(width >> 8);
        writeBuffer[8] = (byte)(width & 255);
        writeBuffer[9] = (byte)(height >> 8);
        writeBuffer[10] = (byte)(height & 255);

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

    public void Reset() => WriteCommand(101);

    public void Clear() => WriteCommand(102);

    public void ScreenOff() => WriteCommand(108);

    public void ScreenOn() => WriteCommand(109);

    public void SetBrightness(int level) => WriteCommand(110, level);

    public void SetOrientation(Orientation orientation) =>
        WriteCommand(121, (byte)orientation, Width, Height);

    public bool DisplayBitmap(int x, int y, byte[] bitmap, int width, int height)
    {
        WriteCommand(197, x, y, width, height, bitmap);
        return true;
    }
}
