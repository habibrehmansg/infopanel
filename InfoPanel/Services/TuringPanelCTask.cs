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
    public sealed class TuringPanelCTask : BackgroundTask
    {
        private static readonly Lazy<TuringPanelCTask> _instance = new(() => new TuringPanelCTask());
        public static TuringPanelCTask Instance => _instance.Value;

        private readonly int _panelWidth = 800;
        private readonly int _panelHeight = 480;

        private TuringPanelCTask()
        { }

        public SKBitmap? GenerateLcdBitmap()
        {
            var profileGuid = ConfigModel.Instance.Settings.TuringPanelCProfile;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = ConfigModel.Instance.Settings.TuringPanelCRotation;
                var bitmap = PanelDrawTask.RenderSK(profile, false, alphaType: SKAlphaType.Opaque);
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
                using var screen = ScreenFactory.Create(ScreenType.RevisionC, ConfigModel.Instance.Settings.TuringPanelCPort);

                if (screen == null)
                {
                    Trace.WriteLine("TuringPanelC: Screen not found");
                    return;
                }

                Trace.WriteLine("TuringPanelC: Screen found");
                SharedModel.Instance.TuringPanelCRunning = true;

                screen.Clear();
                screen.Orientation = TuringSmartScreenLib.ScreenOrientation.Landscape;
                var brightness = ConfigModel.Instance.Settings.TuringPanelCBrightness;
                screen.SetBrightness((byte)brightness);

                SKBitmap? sentBitmap = null;

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
                                var sectors = SKBitmapComparison.GetChangedSectors(sentBitmap, bitmap, 20, 20, 120, 80);
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
                                        canDisplayPartialBitmap = screen.DisplayBuffer(sector.Left, sector.Top, screen.CreateBufferFrom(bitmap, sector.Left, sector.Top, sector.Width, sector.Height));
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
                    screen.ScreenOff();
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
