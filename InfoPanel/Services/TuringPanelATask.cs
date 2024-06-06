using InfoPanel.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Ports;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

using TuringSmartScreenLib;

namespace InfoPanel
{
    public sealed class TuringPanelATask
    {
        private static volatile TuringPanelATask? _instance;
        private static readonly object _lock = new object();

        private CancellationTokenSource? _cts;
        private Task? _task;

        private Bitmap? LCD_BUFFER;

        private TuringPanelATask()
        { }

        public static TuringPanelATask Instance
        {
            get
            {
                if (_instance != null) return _instance;
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new TuringPanelATask();
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

        public void UpdateBuffer(Bitmap bitmap)
        {
            if (LCD_BUFFER == null)
            {
                var rotation = ConfigModel.Instance.Settings.TuringPanelARotation;
                if (rotation != ViewModels.LCD_ROTATION.RotateNone)
                {
                    var rotateFlipType = (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), rotation);
                    bitmap.RotateFlip(rotateFlipType);
                }

                LCD_BUFFER = BitmapExtensions.EnsureBitmapSize(bitmap, 480, 320);

                if (rotation != ViewModels.LCD_ROTATION.RotateNone)
                {
                    var rotateFlipType = (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), 4 - rotation);
                    bitmap.RotateFlip(rotateFlipType);
                }
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
            IScreen? screen = null;

            try
            {
                screen = ScreenFactory.Create(ScreenType.RevisionA, ConfigModel.Instance.Settings.TuringPanelAPort);
            }
            catch (Exception e){ 
                Trace.WriteLine(e.ToString());
            }

            if (screen == null)
            {
                ConfigModel.Instance.Settings.TuringPanelA = false;
                return;
            }

            var watch = new Stopwatch();

            screen.SetBrightness(100);
            screen.Orientation = ScreenOrientation.Landscape;

            Bitmap? sentBitmap = null;

            while (!token.IsCancellationRequested)
            {
                watch.Start();

                var bitmap = LCD_BUFFER;

                if (bitmap != null)
                {
                    if (sentBitmap == null)
                    {
                        sentBitmap = bitmap;
                        screen.DisplayBitmap(0, 0, sentBitmap.Width, sentBitmap.Height, BitmapToRgb16(sentBitmap));
                    }
                    else
                    {
                        var sectors = GetChangedSectors(sentBitmap, bitmap, 32, 32);

                        foreach (var sector in sectors)
                        {
                            var sectorBitmap = GetSectorBitmap(bitmap, sector, 32, 32);
                            screen.DisplayBitmap(sector.X, sector.Y, sectorBitmap.Width, sectorBitmap.Height, BitmapToRgb16(sectorBitmap));
                            sectorBitmap.Dispose();
                        }

                        //Trace.WriteLine($"{sectors.Count} Sector changed");
                        sentBitmap = bitmap;
                    }

                    LCD_BUFFER = null;
                }
                else
                {
                    Task.Delay(50).Wait();
                }

                watch.Stop();
                if (watch.ElapsedMilliseconds < 300)
                {
                    Task.Delay((int)(300 - watch.ElapsedMilliseconds)).Wait();
                }
                //Trace.WriteLine($"TuringPanelA Execution Time: {watch.ElapsedMilliseconds} ms");
                watch.Reset();

            }

            screen.Reset();

        }

        public static List<Point> GetChangedSectors(Bitmap bitmap1, Bitmap bitmap2, int sectorWidth, int sectorHeight)
        {
            List<Point> changedSectors = new List<Point>();

            // Ensure bitmaps are the same size
            if (bitmap1.Width == bitmap2.Width && bitmap1.Height == bitmap2.Height)
            {
                for (int y = 0; y < bitmap1.Height; y += sectorHeight)
                {
                    for (int x = 0; x < bitmap1.Width; x += sectorWidth)
                    {
                        if (!AreSectorsEqual(bitmap1, bitmap2, x, y, sectorWidth, sectorHeight))
                        {
                            changedSectors.Add(new Point(x, y));
                        }
                    }
                }
            }
            else
            {
                throw new ArgumentException("Bitmaps are not the same size.");
            }

            return changedSectors;
        }

        public static bool AreSectorsEqual(Bitmap bitmap1, Bitmap bitmap2, int startX, int startY, int sectorWidth, int sectorHeight)
        {
            for (int y = startY; y < startY + sectorHeight; y++)
            {
                for (int x = startX; x < startX + sectorWidth; x++)
                {
                    if (bitmap1.GetPixel(x, y) != bitmap2.GetPixel(x, y))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public static Bitmap GetSectorBitmap(Bitmap sourceBitmap, Point sectorTopLeft, int sectorWidth, int sectorHeight)
        {
            // Define the rectangle for the sector
            Rectangle sector = new Rectangle(sectorTopLeft.X, sectorTopLeft.Y, sectorWidth, sectorHeight);

            // Clone the sector into a new Bitmap
            Bitmap sectorBitmap = sourceBitmap.Clone(sector, sourceBitmap.PixelFormat);

            return sectorBitmap;
        }
    }
}
