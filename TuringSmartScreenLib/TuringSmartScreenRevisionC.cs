

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Text;

namespace TuringSmartScreenLib
{
    public sealed class TuringSmartScreenRevisionC : IDisposable
    {
        public enum Orientation : byte
        {
            Portrait = 0,
            Landscape = 1
        }

        private readonly SerialPort port;

        public byte Version { get; private set; }

        byte[] HELLO = new byte[] { 0x01, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xc5, 0xd3 };
        byte[] OPTIONS = new byte[] { 0x7d, 0xef, 0x69, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x2d };
        byte[] RESTART = new byte[] { 0x84, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01 };
        byte[] TURNOFF = new byte[] { 0x83, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01 };
        byte[] TURNON = new byte[] { 0x83, 0xef, 0x69, 0x00, 0x00, 0x00, 0x00 };

        byte[] SET_BRIGHTNESS = new byte[] { 0x7b, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };

        byte[] STOP_VIDEO = new byte[] { 0x79, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01 };
        byte[] STOP_MEDIA = new byte[] { 0x96, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01 };

        byte[] QUERY_STATUS = new byte[] { 0xcf, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01 };

        byte[] START_DISPLAY_BITMAP = new byte[] { 0x2c };
        byte[] PRE_UPDATE_BITMAP = new byte[] { 0x86, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01 };
        byte[] UPDATE_BITMAP = new byte[] { 0xcc, 0xef, 0x69, 0x00, 0x00 };

        byte[] DISPLAY_BITMAP = new byte[] { 0xc8, 0xef, 0x69, 0x00, 0x17, 0x70 };

        byte[] STARTMODE_DEFAULT = new byte[] { 0x00 };
        byte[] STARTMODE_IMAGE = new byte[] { 0x01 };
        byte[] STARTMODE_VIDEO = new byte[] { 0x02 };
        byte[] FLIP_180 = new byte[] { 0x01 };
        byte[] NO_FLIP = new byte[] { 0x00 };

        public TuringSmartScreenRevisionC(string name)
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

            //Trace.WriteLine("Send HELLO");
            WriteCommand(HELLO, null, Padding.NULL);
            ReadMessage(23);
            WriteCommand(STOP_VIDEO);

            //Trace.WriteLine("Send STOP_MEDIA");
            WriteCommand(STOP_MEDIA, null, Padding.NULL);
            ReadMessage();
            //Trace.WriteLine("Send QUERY_STATUS");
            WriteCommand(QUERY_STATUS, null, Padding.NULL);
            ReadMessage();
            //Trace.WriteLine("Open done");
        }

        public void Close()
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }



        public enum Padding
        {
            NULL = 0x00,
            START_DISPLAY_BITMAP = 0x2c
        }

        public void WriteCommand(byte[] command, byte[]? payload = null, Padding padding = Padding.NULL)
        {
            var message = new List<byte>();

            message.AddRange(command);

            if (payload != null)
            {
                message.AddRange(payload);
            }

            var msgSize = message.Count;

            if (msgSize % 250 != 0)
            {
                var padSize = (250 * (int)Math.Ceiling((double)msgSize / 250)) - msgSize;
                message.AddRange(Enumerable.Repeat((byte)padding, padSize));
            }

            var buffer = message.ToArray();
            port.Write(buffer, 0, buffer.Length);
        }

        public void ReadMessage(int readSize = 1024)
        {
            var response = new byte[readSize];

            int totalBytesRead = 0;

            while (totalBytesRead < response.Length)
            {
                var bytesRead = port.Read(response, totalBytesRead, response.Length - totalBytesRead);
                if (bytesRead == 0)
                {
                    // No more to read or a timeout occurred.
                    break;
                }
                totalBytesRead += bytesRead;
            }

            //Trace.WriteLine($"Read: " + Encoding.UTF8.GetString(response));
            //Trace.WriteLine(string.Empty);
        }

        public void Reset() => WriteCommand(RESTART);
        public void SetBrightness(byte level) => WriteCommand(SET_BRIGHTNESS, new byte[] { level });

        //public void SetOrientation(Orientation orientation) => WriteCommand(0xCB, (byte)orientation);

        private int count = 0;
        public void DisplayBitmap(int x, int y, int width, int height, byte[] bitmap)
        {
            if (x == 0 && y == 0 && width == 800 && height == 480)
            {
                //Trace.WriteLine("Send DISPLAY_BITMAP");
                WriteCommand(DISPLAY_BITMAP);
                //Trace.WriteLine("Send SEND_PAYLOAD");
                var bgra = RGB16ToBGRA32(bitmap);
                var chunkedBgra = ChunkWithNullByte(bgra);
                WriteCommand(new byte[0], chunkedBgra, Padding.NULL);
                ReadMessage();
                //Trace.WriteLine("Send QUERY_STATUS");
                WriteCommand(QUERY_STATUS);
                ReadMessage();

                count = 0;
            }
            else
            {
                var bgra = RGB16ToBGRA32(bitmap);
                (var msg, var upd_size) = GenerateUpdateImage(bgra, width, height, x, y, count);
                //Trace.WriteLine("Send UPDATE_BITMAP");
                WriteCommand(UPDATE_BITMAP, upd_size, Padding.NULL);
                //Trace.WriteLine("Send SEND_PAYLOAD");
                WriteCommand(new byte[0], msg, Padding.NULL);
                //Trace.WriteLine("Send QUERY_STATUS");
                WriteCommand(QUERY_STATUS);
                ReadMessage();

                count += 1;
            }
        }

        public byte[] RGB16ToBGRA32(byte[] rgb16Data)
        {
            int pixelCount = rgb16Data.Length / 2; // each pixel in RGB16 takes 2 bytes
            byte[] bgra32Data = new byte[pixelCount * 4]; // each pixel in BGRA32 takes 4 bytes

            for (int i = 0; i < pixelCount; i++)
            {
                ushort rgb16 = BitConverter.ToUInt16(rgb16Data, i * 2);
                byte r = (byte)((rgb16 >> 11) & 0x1F); // take the top 5 bits
                byte g = (byte)((rgb16 >> 5) & 0x3F); // take the next 6 bits
                byte b = (byte)(rgb16 & 0x1F); // take the bottom 5 bits

                // scale up to the full range of byte (0-255) from the smaller bit depths
                r = (byte)((r * 255 + 15) / 31);
                g = (byte)((g * 255 + 31) / 63);
                b = (byte)((b * 255 + 15) / 31);

                bgra32Data[i * 4 + 0] = b; // Blue
                bgra32Data[i * 4 + 1] = g; // Green
                bgra32Data[i * 4 + 2] = r; // Red
                bgra32Data[i * 4 + 3] = 255; // Alpha (full opacity)
            }

            return bgra32Data;
        }

        public static (byte[], byte[]) GenerateUpdateImage(byte[] bgra, int width, int height, int x, int y, int count)
        {
            StringBuilder msg = new StringBuilder();

            for (int h = 0; h < height; h++)
            {
                msg.AppendFormat("{0:x6}", (y + h) * 800 + x);
                msg.AppendFormat("{0:x4}", width);

                for (int w = 0; w < width; w++)
                {
                    int index = (h * width + w) * 4; // 4 channels per pixel (BGRA)
                    byte b = bgra[index];
                    byte g = bgra[index + 1];
                    byte r = bgra[index + 2];

                    msg.AppendFormat("{0:x2}", b);
                    msg.AppendFormat("{0:x2}", g);
                    msg.AppendFormat("{0:x2}", r);
                }
            }

            string updSize = string.Format("{0:x4}", ((msg.Length / 2) + 2)); //The +2 is for the "ef69" that will be added later

            if (msg.Length > 500)
            {
                StringBuilder newMsg = new StringBuilder();
                for (int i = 0; i < msg.Length; i += 498)
                {
                    newMsg.Append(msg.ToString().Substring(i, Math.Min(498, msg.Length - i)));
                    if (i + 498 < msg.Length)
                        newMsg.Append("00");
                }

                msg = newMsg;
            }

            msg.Append("ef69");

            // Convert hex string to byte array
            byte[] msgBytes = new byte[msg.Length / 2];
            for (int i = 0; i < msgBytes.Length; i++)
            {
                msgBytes[i] = Convert.ToByte(msg.ToString().Substring(i * 2, 2), 16);
            }

            // Convert hex string to byte array
            byte[] updSizeBytes = new byte[(updSize.Length / 2)];
            for (int i = 0; i < updSizeBytes.Length; i++)
            {
                updSizeBytes[i] = Convert.ToByte(updSize.Substring(i * 2, 2), 16);
            }

            var payload = new List<byte>();
            payload.AddRange(updSizeBytes);
            payload.AddRange(new byte[3] { 0x00, 0x00, 0x00 });

            byte[] countBytes = BitConverter.GetBytes(count);
            Array.Reverse(countBytes);
            payload.AddRange(countBytes);

            return (msgBytes, payload.ToArray());
        }

        public static byte[] ChunkWithNullByte(byte[] data, int chunkSize = 249)
        {
            var result = new List<byte>();

            for (var i = 0; i < data.Length; i += chunkSize)
            {
                // Get chunkSize bytes from the source, or less if not enough remaining
                int actualChunkSize = Math.Min(chunkSize, data.Length - i);
                var chunk = new byte[actualChunkSize];
                Array.Copy(data, i, chunk, 0, actualChunkSize);
                result.AddRange(chunk);

                // Add null byte except after the final chunk
                if (i + actualChunkSize < data.Length)
                {
                    result.Add(0);
                }
            }

            return result.ToArray();
        }
    }
}
