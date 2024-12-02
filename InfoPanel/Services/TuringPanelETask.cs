using InfoPanel.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using TuringSmartScreenLib;

namespace InfoPanel
{
    public sealed class TuringPanelETask: BackgroundTask
    {
        private static readonly Lazy<TuringPanelETask> _instance = new(() => new TuringPanelETask());
        public static TuringPanelETask Instance => _instance.Value;

        private Bitmap? LCD_BUFFER;

        private TuringPanelETask()
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
                var rotation = ConfigModel.Instance.Settings.TuringPanelERotation;
                if (rotation != ViewModels.LCD_ROTATION.RotateNone)
                {
                    var rotateFlipType = (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), rotation);
                    bitmap.RotateFlip(rotateFlipType);
                }

                LCD_BUFFER = BitmapExtensions.EnsureBitmapSize(bitmap, 480, 1920);

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
            using var screen = ScreenFactory.Create(ScreenType.RevisionE, ConfigModel.Instance.Settings.TuringPanelEPort);

            if (screen == null)
            {
                Trace.WriteLine("TuringPanelE: Screen not found");
                ConfigModel.Instance.Settings.TuringPanelE = false;
                return;
            }

            Trace.WriteLine("TuringPanelE: Screen found");

            screen.SetBrightness(100);
            screen.Orientation = TuringSmartScreenLib.ScreenOrientation.Landscape;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var bitmap = LCD_BUFFER;

                    if (bitmap != null)
                    {
                        screen.DisplayBuffer(screen.CreateBufferFrom(bitmap));
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
    }
 }
