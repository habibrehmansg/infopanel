
using System;
using System.IO.Ports;

namespace TuringSmartScreenLib
{
    public sealed class TuringSmartScreenRevisionB : IDisposable
    {
        public enum Orientation : byte
        {
            Portrait = 0,
            Landscape = 1
        }

        private readonly SerialPort port;

        public byte Version { get; private set; }

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

            var hello = new byte[] { 0xCA, (byte)'H', (byte)'E', (byte)'L', (byte)'L', (byte)'O', 0, 0, 0, 0xCA };
            port.Write(hello, 0, hello.Length);

            var response = new byte[10];
            var read = port.Read(response, 0, response.Length);
            if ((read == 10) &&
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
        }

        public void Close()
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }

        private void WriteCommand(byte command, byte value)
        {
            var buffer = new byte[10];
            buffer[0] = command;
            buffer[1] = value;
            buffer[9] = command;
            port.Write(buffer, 0, buffer.Length);
        }

        private void WriteCommand(byte command, int x, int y, int width, int height, byte[] data)
        {
            var ex = x + width - 1;
            var ey = y + height - 1;
            var buffer = new byte[10];
            buffer[0] = command;
            buffer[1] = (byte)((x >> 8) & 0xFF);
            buffer[2] = (byte)(x & 0xFF);
            buffer[3] = (byte)((y >> 8) & 0xFF);
            buffer[4] = (byte)(y & 0xFF);
            buffer[5] = (byte)((ex >> 8) & 0xFF);
            buffer[6] = (byte)(ex & 0xFF);
            buffer[7] = (byte)((ey >> 8) & 0xFF);
            buffer[8] = (byte)(ey & 0xFF);
            buffer[9] = command;
            port.Write(buffer, 0, buffer.Length);
            port.Write(data, 0, width * height * 2);
        }

        public void SetBrightness(byte level) => WriteCommand(0xCE, level);

        public void SetOrientation(Orientation orientation) => WriteCommand(0xCB, (byte)orientation);

        public void DisplayBitmap(int x, int y, int width, int height, byte[] bitmap) =>
            WriteCommand(0xCC, x, y, width, height, bitmap);
    }
}