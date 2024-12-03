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
    public sealed class TuringPanelCTask : BackgroundTask
    {
        private static readonly Lazy<TuringPanelCTask> _instance = new(() => new TuringPanelCTask());
        public static TuringPanelCTask Instance => _instance.Value;

        private Bitmap? LCD_BUFFER;

        private TuringPanelCTask()
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
                var rotation = ConfigModel.Instance.Settings.TuringPanelCRotation;
                if (rotation != ViewModels.LCD_ROTATION.RotateNone)
                {
                    var rotateFlipType = (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), rotation);
                    bitmap.RotateFlip(rotateFlipType);
                }

                LCD_BUFFER = BitmapExtensions.EnsureBitmapSize(bitmap, 800, 480);

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
                using var screen = ScreenFactory.Create(ScreenType.RevisionC, ConfigModel.Instance.Settings.TuringPanelCPort);

                if (screen == null)
                {
                    Trace.WriteLine("TuringPanelC: Screen not found");
                    return;
                }

                Trace.WriteLine("TuringPanelC: Screen found");
                SharedModel.Instance.TuringPanelCRunning = true;

                screen.Orientation = TuringSmartScreenLib.ScreenOrientation.Landscape;
                var brightness = ConfigModel.Instance.Settings.TuringPanelCBrightness;
                screen.SetBrightness((byte)brightness);

                try
                {
                    Bitmap? sentBitmap = null;

                    while (!token.IsCancellationRequested)
                    {
                        if (brightness != ConfigModel.Instance.Settings.TuringPanelCBrightness)
                        {
                            brightness = ConfigModel.Instance.Settings.TuringPanelCBrightness;
                            screen.SetBrightness((byte)brightness);
                        }

                        var bitmap = LCD_BUFFER;
                        //var stopwatch = new Stopwatch();

                        if (bitmap != null)
                        {
                            if (sentBitmap == null || !screen.CanDisplayPartialBitmap())
                            {
                                sentBitmap = bitmap;
                                //stopwatch.Start();
                                screen.DisplayBuffer(screen.CreateBufferFrom(sentBitmap));
                                //Trace.WriteLine($"Full sector update: {stopwatch.ElapsedMilliseconds}ms");
                                //stopwatch.Stop();
                            }
                            else
                            {
                                //stopwatch.Start();
                                var sectors = BitmapExtensions.GetChangedSectors(sentBitmap, bitmap, 20, 20, 120, 80);
                                //Trace.WriteLine($"Sector detect: {sectors.Count} sectors {stopwatch.ElapsedMilliseconds}ms");
                                //stopwatch.Restart();

                                if (sectors.Count > 30)
                                {
                                    screen.DisplayBuffer(screen.CreateBufferFrom(bitmap));
                                    //Trace.WriteLine($"Full sector update: {stopwatch.ElapsedMilliseconds}ms");
                                }
                                else
                                {
                                    if (sectors.Count > 0)
                                    {
                                        foreach (var sector in sectors)
                                        {
                                            screen.DisplayBuffer(sector.X, sector.Y, screen.CreateBufferFrom(bitmap, sector.X, sector.Y, sector.Width, sector.Height));
                                        }

                                        //stopwatch.Stop();
                                        //Trace.WriteLine($"Sector update: {stopwatch.ElapsedMilliseconds}ms");
                                    }
                                    else
                                    {
                                        await Task.Delay(50, token);
                                    }
                                }

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
                    screen.Clear();
                    screen.Reset();
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine("TuringPanelC: Init error");
            }finally
            {
                SharedModel.Instance.TuringPanelCRunning = false;
            }
        }
    }
}
