using InfoPanel.Extensions;
using MadWizard.WinUSBNet;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    public sealed class BeadaPanelTask : BackgroundTask
    {
        private static readonly Lazy<BeadaPanelTask> _instance = new(() => new BeadaPanelTask());

        private volatile int _panelWidth = 0;
        private volatile int _panelHeight = 0;
        private volatile byte[]? _lcdBuffer;

        public static BeadaPanelTask Instance => _instance.Value;

        private BeadaPanelTask() { }

        public static byte[] BitmapToRgb16(Bitmap bitmap)
        {
            BitmapData bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format16bppRgb565);
            try
            {
                int stride = bmpData.Stride;
                int size = bmpData.Height * stride;
                byte[] data = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, data, 0, size);
                return data;
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }

        public static byte[] BitmapToBgrX(Bitmap bitmap)
        {
            BitmapData bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bmpData.Stride;
                int size = bmpData.Height * stride;
                byte[] data = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, data, 0, size);
                return data;
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }

        public void UpdateBuffer(Bitmap bitmap)
        {
            if (_lcdBuffer == null)
            {
                var rotation = ConfigModel.Instance.Settings.BeadaPanelRotation;
                if (rotation != ViewModels.LCD_ROTATION.RotateNone)
                {
                    var rotateFlipType = (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), rotation);
                    bitmap.RotateFlip(rotateFlipType);
                }

                using var resizedBitmap = (_panelWidth == 0 || _panelHeight == 0)
                    ? BitmapExtensions.EnsureBitmapSize(bitmap, bitmap.Width, bitmap.Height)
                    : BitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight);

                if (rotation != ViewModels.LCD_ROTATION.RotateNone)
                {
                    var rotateFlipType = (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), 4 - rotation);
                    bitmap.RotateFlip(rotateFlipType);
                }

                _lcdBuffer = BitmapToRgb16(resizedBitmap);
            }
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);
            try
            {
                using var device = USBDevice.GetSingleDevice("{8E41214B-6785-4CFE-B992-037D68949A14}");
                if (device is null)
                {
                    Trace.WriteLine("USB Device not found.");
                    return;
                }

                Trace.WriteLine($"USB Device Found - {device.Descriptor.FullName}");

                var match = Regex.Match(device.Descriptor.Product, @"(\d+)x(\d+)");
                if (!match.Success)
                {
                    return;
                }

                (_panelWidth, _panelHeight) = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
                Trace.WriteLine($"BeadaPanel Width: {_panelWidth}, Height: {_panelHeight}");

                SharedModel.Instance.BeadaPanelRunning = true;

                var iface = device.Interfaces.First();
                var startTag = new PanelLinkStreamTag(_panelWidth, _panelHeight);
                var clearTag = new PanelLinkStreamTag(4);
                var brightnessTag = new StatusLinkBrightnessTag();

                var brightness = ConfigModel.Instance.Settings.BeadaPanelBrightness;
                brightnessTag.SetBrightness((byte)((brightness / 100.0 * 75) + 25));
                iface.Pipes[0x2].Write(brightnessTag.ToBuffer());

                iface.OutPipe.Write(clearTag.ToBuffer());
                Trace.WriteLine("Sent clearTag");

                iface.OutPipe.Write(startTag.ToBuffer());
                Trace.WriteLine("Sent startTag");

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (brightness != ConfigModel.Instance.Settings.BeadaPanelBrightness)
                        {
                            brightness = ConfigModel.Instance.Settings.BeadaPanelBrightness;
                            brightnessTag.SetBrightness((byte)((brightness / 100.0 * 75) + 25));
                            iface.Pipes[0x2].Write(brightnessTag.ToBuffer());
                        }

                        byte[]? buffer = _lcdBuffer;

                        if (buffer != null)
                        {
                            iface.OutPipe.Write(buffer);
                            _lcdBuffer = null;
                        }
                        else
                        {
                            await Task.Delay(50, token);
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    Trace.WriteLine("Task cancelled");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Exception during work: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        var resetTag = new PanelLinkStreamTag(3);
                        iface.OutPipe.Write(resetTag.ToBuffer());
                        Trace.WriteLine("Sent ResetTag to clear screen");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Exception when sending ResetTag: {ex.Message}");
                    }

                }
            }
            catch (Exception e)
            {
                Trace.WriteLine("BeadaPanel: Init error");
            }
            finally
            {
                SharedModel.Instance.BeadaPanelRunning = false;
            }
        }
    }

    struct StatusLinkBrightnessTag
    {
        private readonly byte[] _protocolName = Encoding.ASCII.GetBytes("STATUS-LINK");
        private readonly byte _version = 1;
        private readonly byte _type = 3;
        private ushort _checksum = 0;
        private byte _brightness = 100;

        public StatusLinkBrightnessTag() { }

        public void SetBrightness(byte brightness)
        {
            _brightness = brightness;
        }

        public byte[] ToBuffer()
        {
            // Initialize buffer with a total size of 21 bytes
            byte[] buffer = new byte[21];

            // Copy protocol name to buffer
            Array.Copy(_protocolName, 0, buffer, 0, _protocolName.Length);

            // Set the version, type, and reserved bytes
            buffer[11] = _version;
            buffer[12] = _type;
            buffer[13] = 0; // Reserved for future purpose
            buffer[14] = 0; // Sequence number for future use
            buffer[15] = 0; // Sequence number for future use

            // Write the length as a 16-bit little-endian integer
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(16), 21);

            // Calculate checksum
            ushort[] ushortBufferWithoutChecksum = new ushort[(buffer.Length - 3) / 2];
            Buffer.BlockCopy(buffer, 0, ushortBufferWithoutChecksum, 0, buffer.Length - 3);
            _checksum = CalculateChecksum(ushortBufferWithoutChecksum, ushortBufferWithoutChecksum.Length);

            // Add checksum to the end of the buffer
            BitConverter.GetBytes(_checksum).CopyTo(buffer, 18);

            buffer[20] = _brightness;

            return buffer;
        }

        private static ushort CalculateChecksum(ushort[] buffer, int wordCount)
        {
            uint sum = 0;
            for (int i = 0; i < wordCount; i++)
            {
                sum += buffer[i];
            }
            // Fold the sum to 16 bits
            sum = (sum >> 16) + (sum & 0xffff);
            sum += (sum >> 16);
            return (ushort)~sum;
        }
    }

    struct PanelLinkStreamTag
    {
        private readonly byte[] _protocolName = Encoding.ASCII.GetBytes("PANEL-LINK");
        private readonly byte _version = 1;
        private readonly byte _type;
        private readonly byte[] _fmtstr = new byte[256];
        private ushort _checksum = 0;

        public PanelLinkStreamTag(int width, int height)
        {
            _type = 1;

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Unsupported width/height");
            }

            byte[] header = Encoding.ASCII.GetBytes($"video/x-raw, format=RGB16, width={width}, height={height}, framerate=0/1");
            header.CopyTo(_fmtstr, 0);
        }

        public PanelLinkStreamTag(byte type)
        {
            _type = type;

            if (type == 1)
            {
                throw new ArgumentException("Unsupported type");
            }
        }

        public byte[] ToBuffer()
        {
            // Create a buffer with the full size including space for the checksum
            byte[] buffer = new byte[270];

            // Copy _protocolName into the buffer
            Array.Copy(_protocolName, 0, buffer, 0, _protocolName.Length);

            // Set _version and _type
            buffer[10] = _version;
            buffer[11] = _type;

            // Copy _fmtstr into the buffer
            Array.Copy(_fmtstr, 0, buffer, 12, _fmtstr.Length);

            // Calculate checksum using the first 268 bytes of the buffer
            ushort[] ushortBuffer = new ushort[134]; // 268 bytes divided by 2 (size of ushort)
            Buffer.BlockCopy(buffer, 0, ushortBuffer, 0, 268);

            // Call the external checksum function
            _checksum = CalculateChecksum(ushortBuffer, ushortBuffer.Length);

            // Copy the checksum into the buffer at the end
            BitConverter.GetBytes(_checksum).CopyTo(buffer, 268);

            return buffer;
        }

        private static ushort CalculateChecksum(ushort[] buffer, int wordCount)
        {
            uint sum = 0;
            for (int i = 0; i < wordCount; i++)
            {
                sum += buffer[i];
            }
            // Fold the sum to 16 bits
            sum = (sum >> 16) + (sum & 0xffff);
            sum += (sum >> 16);
            return (ushort)~sum;
        }
    }
}
