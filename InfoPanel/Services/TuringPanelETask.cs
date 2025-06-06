using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TuringSmartScreenLib;
using TuringSmartScreenLib.Helpers.SkiaSharp;

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

        public SKBitmap? GenerateLcdBitmap()
        {
            var profileGuid = ConfigModel.Instance.Settings.TuringPanelEProfile;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = ConfigModel.Instance.Settings.TuringPanelERotation;
                var bitmap = PanelDrawTask.RenderSK(profile, false);

                var ensuredBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

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

                screen.Clear();
                screen.Orientation = ScreenOrientation.Portrait;
                var brightness = ConfigModel.Instance.Settings.TuringPanelEBrightness;
                screen.SetBrightness((byte)brightness);

                SKBitmap? sentBitmap = null;

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
                                var sectors = SKBitmapComparison.GetChangedSectors(sentBitmap, bitmap, 32, 32, 128, 96);
                                //Trace.WriteLine($"Sector detect: {sectors.Count} sectors {stopwatch.ElapsedMilliseconds}ms");

                                if (sectors.Count > 38)
                                {
                                    canDisplayPartialBitmap = screen.DisplayBuffer(screen.CreateBufferFrom(bitmap));
                                    //Trace.WriteLine($"Full sector update: {stopwatch.ElapsedMilliseconds}ms");
                                }
                                else
                                {
                                    foreach (var sector in sectors)
                                    {
                                        canDisplayPartialBitmap = screen.DisplayBuffer(sector.Left, sector.Top, screen.CreateBufferFrom(bitmap, sector.Left, sector.Top, sector.Width, sector.Height));
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
                    screen.ScreenOff();
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
