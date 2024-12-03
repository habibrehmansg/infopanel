using InfoPanel.Extensions;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;

using TuringSmartScreenLib;

namespace InfoPanel
{
    public sealed class TuringPanelATask: BackgroundTask
    {
        private static readonly Lazy<TuringPanelATask> _instance = new(() => new TuringPanelATask());
        public static TuringPanelATask Instance => _instance.Value;

        private Bitmap? LCD_BUFFER;

        private TuringPanelATask()
        { }

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


        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);
            try
            {
                using var screen = ScreenFactory.Create(ScreenType.RevisionA, ConfigModel.Instance.Settings.TuringPanelAPort);

                if (screen == null)
                {
                    Trace.WriteLine("TuringPanelA: Screen not found");
                    return;
            }

            Trace.WriteLine("TuringPanelA: Screen found"); 
            SharedModel.Instance.TuringPanelARunning = true;
                //var watch = new Stopwatch();

            screen.Orientation = ScreenOrientation.Landscape;

            var brightness = ConfigModel.Instance.Settings.TuringPanelABrightness;
            screen.SetBrightness((byte)brightness);

            try
            {
                Bitmap? sentBitmap = null;

                while (!token.IsCancellationRequested)
                {
                    if (brightness != ConfigModel.Instance.Settings.TuringPanelABrightness)
                    {
                        brightness = ConfigModel.Instance.Settings.TuringPanelABrightness;
                        screen.SetBrightness((byte)brightness);
                    }

                    var bitmap = LCD_BUFFER;
                    //var stopwatch = new Stopwatch();

                    if (bitmap != null)
                    {
                        if (sentBitmap == null)
                        {
                            sentBitmap = bitmap;
                            screen.DisplayBuffer(screen.CreateBufferFrom(sentBitmap));
                        }
                        else
                        {
                            //stopwatch.Start();
                            var sectors = BitmapExtensions.GetChangedSectors(sentBitmap, bitmap, 10, 10, 20, 80);
                            //Trace.WriteLine($"Sector detect: {sectors.Count} sectors {stopwatch.ElapsedMilliseconds}ms");
                            //stopwatch.Restart();

                            if (sectors.Count > 76)
                            {
                                screen.DisplayBuffer(screen.CreateBufferFrom(bitmap));
                            }
                            else
                            {
                                foreach (var sector in sectors)
                                {
                                   screen.DisplayBuffer(sector.X, sector.Y, screen.CreateBufferFrom(bitmap, sector.X, sector.Y, sector.Width, sector.Height));
                                }
                            }

                            //stopwatch.Stop();
                            //Trace.WriteLine($"Sector update: {stopwatch.ElapsedMilliseconds}ms");

                            sentBitmap = bitmap;
                        }

                        LCD_BUFFER = null;
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
                Trace.WriteLine("Resetting screen");
                screen.Reset();
            }
            }
            catch (Exception e)
            {
                Trace.WriteLine("TuringPanelA: Init error");
            }
            finally
            {
                SharedModel.Instance.TuringPanelARunning = false;
            }
        }
    }
}
