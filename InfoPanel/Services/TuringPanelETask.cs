using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using TuringSmartScreenLib;

namespace InfoPanel
{
    public sealed class TuringPanelETask : BackgroundTask
    {
        private static readonly Lazy<TuringPanelETask> _instance = new(() => new TuringPanelETask());
        public static TuringPanelETask Instance => _instance.Value;

        private readonly int _panelWidth = 480;
        private readonly int _panelHeight = 1920;

        private TuringPanelETask()
        { }

        public Bitmap? GenerateLcdBitmap()
        {
            var profileGuid = ConfigModel.Instance.Settings.TuringPanelEProfile;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var bitmap = PanelDrawTask.Render(profile, false);
                var rotation = ConfigModel.Instance.Settings.TuringPanelERotation;
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
                using var screen = ScreenFactory.Create(ScreenType.RevisionE, ConfigModel.Instance.Settings.TuringPanelEPort);

                if (screen == null)
                {
                    Trace.WriteLine("TuringPanelE: Screen not found");
                    return;
                }

                Trace.WriteLine("TuringPanelE: Screen found");
                SharedModel.Instance.TuringPanelERunning = true;

                var brightness = ConfigModel.Instance.Settings.TuringPanelEBrightness;
                screen.SetBrightness((byte)brightness);

                Bitmap? sentBitmap = null;

                try
                {
                    var fpsCounter = new FpsCounter();
                    var stopwatch = new Stopwatch(); 
                    var canDisplayPartialBitmap = true;

                    while (!token.IsCancellationRequested)
                    {
                        stopwatch.Restart();

                        if (brightness != ConfigModel.Instance.Settings.TuringPanelEBrightness)
                        {
                            brightness = ConfigModel.Instance.Settings.TuringPanelEBrightness;
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
                                var sectors = BitmapExtensions.GetChangedSectors(sentBitmap, bitmap, 20, 20, 160, 100);
                                //Trace.WriteLine($"Sector detect: {sectors.Count} sectors {stopwatch.ElapsedMilliseconds}ms");

                                if (sectors.Count > 46)
                                {
                                    //Trace.WriteLine($"Full sector update: {stopwatch.ElapsedMilliseconds}ms");
                                    canDisplayPartialBitmap = screen.DisplayBuffer(screen.CreateBufferFrom(bitmap));
                                }
                                else
                                {
                                    foreach (var sector in sectors)
                                    {
                                        canDisplayPartialBitmap = screen.DisplayBuffer(sector.X, sector.Y, screen.CreateBufferFrom(bitmap, sector.X, sector.Y, sector.Width, sector.Height));
                                    }

                                    //Trace.WriteLine($"Sector update: {stopwatch.ElapsedMilliseconds}ms");
                                }
                                sentBitmap?.Dispose();
                                sentBitmap = bitmap;
                            }
                        }

                        fpsCounter.Update();
                        SharedModel.Instance.TuringPanelEFrameRate = fpsCounter.FramesPerSecond;
                        SharedModel.Instance.TuringPanelEFrameTime = stopwatch.ElapsedMilliseconds;

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
                        rotateFlipType: (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), ConfigModel.Instance.Settings.TuringPanelERotation));
                    screen.DisplayBuffer(screen.CreateBufferFrom(bitmap));
                    //screen.Reset();
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine("TuringPanelE: Init error");
            }
            finally
            {
                SharedModel.Instance.TuringPanelERunning = false;
            }
        }
    }
}
