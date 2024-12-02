using InfoPanel.Extensions;
using MadWizard.WinUSBNet;
using System;
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

            using var device = USBDevice.GetSingleDevice("{8E41214B-6785-4CFE-B992-037D68949A14}");
            if (device is null)
            {
                Trace.WriteLine("USB Device not found.");
                ConfigModel.Instance.Settings.BeadaPanel = false;
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

            var iface = device.Interfaces.First();
            var startTag = new PanelLinkStreamTag(_panelWidth, _panelHeight);
            var clearTag = new PanelLinkStreamTag(4);

            iface.OutPipe.Write(clearTag.ToBuffer());
            Trace.WriteLine("Sent clearTag");

            iface.OutPipe.Write(startTag.ToBuffer());
            Trace.WriteLine("Sent startTag");

            try
            {
                while (!token.IsCancellationRequested)
                {
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

        private readonly byte[] ToBufferWithoutChecksum()
        {
            // Initialize buffer with a defined size and use a single array copy operation
            byte[] buffer = new byte[268];
            Array.Copy(_protocolName, 0, buffer, 0, _protocolName.Length);
            buffer[10] = _version;
            buffer[11] = _type;
            Array.Copy(_fmtstr, 0, buffer, 12, _fmtstr.Length);
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

        public byte[] ToBuffer()
        {
            byte[] buffer = new byte[270];
            byte[] bufferWithoutChecksum = ToBufferWithoutChecksum();

            ushort[] ushortBufferWithoutChecksum = new ushort[bufferWithoutChecksum.Length / 2];
            Buffer.BlockCopy(bufferWithoutChecksum, 0, ushortBufferWithoutChecksum, 0, bufferWithoutChecksum.Length);

            // Calculate checksum
            _checksum = CalculateChecksum(ushortBufferWithoutChecksum, ushortBufferWithoutChecksum.Length);
            Buffer.BlockCopy(bufferWithoutChecksum, 0, buffer, 0, bufferWithoutChecksum.Length);
            BitConverter.GetBytes(_checksum).CopyTo(buffer, 268);

            return buffer;
        }
    }
}
