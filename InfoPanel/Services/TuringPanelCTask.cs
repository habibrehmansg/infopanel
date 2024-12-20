using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
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

        private readonly int _panelWidth = 800;
        private readonly int _panelHeight = 480;

        private TuringPanelCTask()
        { }

        public Bitmap? GenerateLcdBitmap()
        {
            var profileGuid = ConfigModel.Instance.Settings.TuringPanelCProfile;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var bitmap = PanelDrawTask.Render(profile, false, videoBackgroundFallback: true);
                var rotation = ConfigModel.Instance.Settings.TuringPanelCRotation;
                if (rotation != ViewModels.LCD_ROTATION.RotateNone)
                {
                    var rotateFlipType = (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), rotation);
                    bitmap.RotateFlip(rotateFlipType);
                }

                var ensuredBitmap = BitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight);

                if (!ReferenceEquals(bitmap, ensuredBitmap))
                {
                    bitmap.Dispose();
                }

                return ensuredBitmap;
            }

            return null;
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

                Bitmap? sentBitmap = null;

                try
                {
                    var fpsCounter = new FpsCounter();
                    var stopwatch = new Stopwatch();
                    var canDisplayPartialBitmap = false;

                    while (!token.IsCancellationRequested)
                    {
                        stopwatch.Restart();

                        if (brightness != ConfigModel.Instance.Settings.TuringPanelCBrightness)
                        {
                            brightness = ConfigModel.Instance.Settings.TuringPanelCBrightness;
                            screen.SetBrightness((byte)brightness);
                        }

                        var bitmap = GenerateLcdBitmap();

                        if (bitmap != null)
                        {
                            if (sentBitmap == null || !canDisplayPartialBitmap)
                            {
                                sentBitmap = bitmap;
                                canDisplayPartialBitmap = screen.DisplayBuffer(screen.CreateBufferFrom(sentBitmap));
                                //Trace.WriteLine($"Full sector update: {stopwatch.ElapsedMilliseconds}ms");
                            }
                            else
                            {
                                var sectors = BitmapExtensions.GetChangedSectors(sentBitmap, bitmap, 20, 20, 120, 80);
                                //Trace.WriteLine($"Sector detect: {sectors.Count} sectors {stopwatch.ElapsedMilliseconds}ms");

                                if (sectors.Count > 30)
                                {
                                    canDisplayPartialBitmap = screen.DisplayBuffer(screen.CreateBufferFrom(bitmap));
                                    //Trace.WriteLine($"Full sector update: {stopwatch.ElapsedMilliseconds}ms");
                                }
                                else
                                {
                                    foreach (var sector in sectors)
                                    {
                                        canDisplayPartialBitmap = screen.DisplayBuffer(sector.X, sector.Y, screen.CreateBufferFrom(bitmap, sector.X, sector.Y, sector.Width, sector.Height));
                                    }

                                    //stopwatch.Stop();
                                    //Trace.WriteLine($"Sector update: {stopwatch.ElapsedMilliseconds}ms");
                                }
                                sentBitmap?.Dispose();
                                sentBitmap = bitmap;
                            }
                        }

                        fpsCounter.Update();
                        SharedModel.Instance.TuringPanelCFrameRate = fpsCounter.FramesPerSecond;
                        SharedModel.Instance.TuringPanelCFrameTime = stopwatch.ElapsedMilliseconds;

                        var targetFrameTime = 1000.0 / ConfigModel.Instance.Settings.TargetFrameRate;
                        if (stopwatch.ElapsedMilliseconds < targetFrameTime)
                        {
                            var sleep = (int)(targetFrameTime - stopwatch.ElapsedMilliseconds);
                            //Trace.WriteLine($"Sleep {sleep}ms");
                            await Task.Delay(sleep, token);
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
                    sentBitmap?.Dispose();
                    Trace.WriteLine("Resetting screen");
                    //screen.Clear();

                    using var bitmap = PanelDrawTask.RenderSplash(screen.Width, screen.Height,
                        rotateFlipType: (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), ConfigModel.Instance.Settings.TuringPanelCRotation));
                    screen.DisplayBuffer(screen.CreateBufferFrom(bitmap));
                    //screen.Reset();
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
