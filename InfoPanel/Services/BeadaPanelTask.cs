using ControlzEx.Standard;
using InfoPanel.Extensions;
using InfoPanel.Models;
using MadWizard.WinUSBNet;
using System;
using System.Collections.Generic;
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
    public sealed class BeadaPanelTask
    {
        private static volatile BeadaPanelTask? _instance;
        private static readonly object _lock = new object();

        private CancellationTokenSource? _cts;
        private Task? _task;

        private int PanelWidth = 0;
        private int PanelHeight = 0;
        private volatile byte[]? LCD_BUFFER;

        private BeadaPanelTask()
        {
        }

        public static BeadaPanelTask Instance
        {
            get
            {
                if (_instance != null) return _instance;
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new BeadaPanelTask();
                    }
                }
                return _instance;
            }
        }

        public static byte[] BitmapToRgb16(Bitmap bitmap)
        {
            BitmapData bmpData = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format16bppRgb565);
            int stride = bmpData.Stride;
            int size = bmpData.Height * stride;
            byte[] data = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, data, 0, size);
            bitmap.UnlockBits(bmpData);
            return data;
        }

        public static byte[] BitmapToBgrX(Bitmap bitmap)
        {
            BitmapData bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = bmpData.Stride;
            int size = bmpData.Height * stride;
            byte[] data = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, data, 0, size);
            bitmap.UnlockBits(bmpData);
            return data;
        }

        public void UpdateBuffer(Bitmap bitmap)
        {
           
            if (LCD_BUFFER == null)
            {
                var rotation = ConfigModel.Instance.Settings.BeadaPanelRotation;
                if (rotation != ViewModels.LCD_ROTATION.RotateNone)
                {
                    var rotateFlipType = (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), rotation);
                    bitmap.RotateFlip(rotateFlipType);
                }

                Bitmap resizedBitmap;

                if(PanelWidth == 0 || PanelHeight == 0)
                {
                    resizedBitmap = BitmapExtensions.EnsureBitmapSize(bitmap, bitmap.Width, bitmap.Height);
                } else
                {
                    resizedBitmap = BitmapExtensions.EnsureBitmapSize(bitmap, PanelWidth, PanelHeight);
                }

                if (rotation != ViewModels.LCD_ROTATION.RotateNone)
                {
                    var rotateFlipType = (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), 4 - rotation);
                    bitmap.RotateFlip(rotateFlipType);
                }

                LCD_BUFFER = BitmapToRgb16(resizedBitmap);
            }
        }

        public bool IsRunning()
        {
            return _cts != null && !_cts.IsCancellationRequested;
        }

        public void Restart()
        {

            if (!IsRunning())
            {
                return;
            }

            if (_task != null)
            {
                Stop();
                while (!_task.IsCompleted)
                {
                    Task.Delay(50).Wait();
                }
            }

            Start();
        }

        public void Start()
        {
            if (_task != null && !_task.IsCompleted) return;
            _cts = new CancellationTokenSource();
            _task = Task.Factory.StartNew(() => DoWork(_cts.Token), _cts.Token);
        }

        public void Stop()
        {
            if (_cts != null)
            {
                _cts.Cancel();
            }
        }

        private void DoWork(CancellationToken token)
        {
            //todo exception handling
            Trace.WriteLine("Finding USB Device");
            USBDevice device = null;

            try
            {
                device = USBDevice.GetSingleDevice("{8E41214B-6785-4CFE-B992-037D68949A14}");
            }
            catch { }


            if (device == null)
            {
                ConfigModel.Instance.Settings.BeadaPanel = false;
                return;
            }

            Trace.WriteLine("USB Device Found - " + device.Descriptor.FullName);

            var match = Regex.Match(device.Descriptor.Product, @"(\d+)x(\d+)");
            if (match.Success)
            {
                PanelWidth = int.Parse(match.Groups[1].Value);
                PanelHeight = int.Parse(match.Groups[2].Value);

                Trace.WriteLine($"BeadaPanel Width: {PanelWidth}, Height: {PanelHeight}");

                USBInterface iface = device.Interfaces.First();

                var StartTag = new PANELLINK_STREAM_TAG(PanelWidth, PanelHeight);
                var EndTag = new PANELLINK_STREAM_TAG(2);
                var ResetTag = new PANELLINK_STREAM_TAG(3);
                var ClearTag = new PANELLINK_STREAM_TAG(4);

                iface.OutPipe.Write(ClearTag.toBuffer());
                Trace.WriteLine("Sent clearTag");

                iface.OutPipe.Write(StartTag.toBuffer());
                Trace.WriteLine("Sent startTag");

                var watch = new Stopwatch();

                while (!token.IsCancellationRequested)
                {
                    //watch.Start();

                    byte[]? buffer = LCD_BUFFER;

                    if (buffer != null)
                    {
                        iface.OutPipe.Write(buffer);
                        LCD_BUFFER = null;
                    }
                    else
                    {
                        Task.Delay(50).Wait();
                    }

                    //watch.Stop();
                    //Trace.WriteLine($"Panel Execution Time: {watch.ElapsedMilliseconds} ms");
                    //watch.Reset();
                }

                iface.OutPipe.Write(EndTag.toBuffer());
                Trace.WriteLine("Sent endTag");

                iface.OutPipe.Write(ResetTag.toBuffer());
                Trace.WriteLine("Sent ResetTag");

                device.Dispose();
                Trace.WriteLine("Dispose device");
            }

        }
    }



    struct PANELLINK_STREAM_TAG
    {
        public byte[] protocol_name = Encoding.ASCII.GetBytes("PANEL-LINK");
        public byte version = 1;
        public byte type;
        public byte[] fmtstr = new byte[256];
        public ushort checksum = 0;
        public int width = 0;
        public int height = 0;

        public PANELLINK_STREAM_TAG(int width, int height)
        {
            this.type = 1;
            this.width = width;
            this.height = height;

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Unsupported width/height");
            }

            byte[] header = Encoding.ASCII.GetBytes("video/x-raw, format=RGB16, width=" + width + ", height=" + height + ", framerate=0/1");
            header.CopyTo(fmtstr, 0);
        }

        public PANELLINK_STREAM_TAG(byte type)
        {
            this.type = type;

            if (type == 1)
            {
                throw new ArgumentException("Unsupported type");
            }
        }

        private byte[] toBufferWithoutChecksum()
        {
            byte[] buffer = new byte[268];
            protocol_name.CopyTo(buffer, 0);
            buffer[10] = version;
            buffer[11] = type;
            fmtstr.CopyTo(buffer, 12);
            return buffer;
        }

        private ushort checkSum(ushort[] buf, int nword)
        {
            uint sum = 0;
            for (int i = 0; i < nword; i++)
            {
                sum += buf[i];
            }
            sum = (sum >> 16) + (sum & 0xffff);
            sum += (sum >> 16);
            return (ushort)~sum;
        }

        public byte[] toBuffer()
        {
            byte[] buffer = new byte[270];

            byte[] bufferWithoutChecksum = toBufferWithoutChecksum();
            ushort[] ushortBufferWithoutChecksum = new ushort[bufferWithoutChecksum.Length / 2];
            Buffer.BlockCopy(bufferWithoutChecksum, 0, ushortBufferWithoutChecksum, 0, bufferWithoutChecksum.Length);
            checksum = checkSum(ushortBufferWithoutChecksum, 134);

            bufferWithoutChecksum.CopyTo(buffer, 0);
            BitConverter.GetBytes(checksum).CopyTo(buffer, 268);


            return buffer;
        }
    }
}
