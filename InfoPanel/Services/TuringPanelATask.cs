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
    public sealed class TuringPanelATask : BackgroundTask
    {
        private static readonly Lazy<TuringPanelATask> _instance = new(() => new TuringPanelATask());
        public static TuringPanelATask Instance => _instance.Value;

        private readonly int _panelWidth = 480;
        private readonly int _panelHeight = 320;

        private TuringPanelATask()
        { }

        public SKBitmap? GenerateLcdBitmap()
        {
            var profileGuid = ConfigModel.Instance.Settings.TuringPanelAProfile;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var bitmap = PanelDrawTask.RenderSK(profile, false, alphaType: SKAlphaType.Opaque);
                var rotation = ConfigModel.Instance.Settings.TuringPanelARotation;

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
                using var screen = ScreenFactory.Create(ScreenType.RevisionA, ConfigModel.Instance.Settings.TuringPanelAPort);

                if (screen == null)
                {
                    Trace.WriteLine("TuringPanelA: Screen not found");
                    return;
                }

                Trace.WriteLine("TuringPanelA: Screen found");
                SharedModel.Instance.TuringPanelARunning = true;

                screen.Clear();
                screen.Orientation = ScreenOrientation.Landscape;

                var brightness = ConfigModel.Instance.Settings.TuringPanelABrightness;
                screen.SetBrightness((byte)brightness);

                SKBitmap? sentBitmap = null;

                try
                {
                    var fpsCounter = new FpsCounter();
                    var stopwatch = new Stopwatch();

                    while (!token.IsCancellationRequested)
                    {
                        stopwatch.Restart();

                        if (brightness != ConfigModel.Instance.Settings.TuringPanelABrightness)
                        {
                            brightness = ConfigModel.Instance.Settings.TuringPanelABrightness;
                            screen.SetBrightness((byte)brightness);
                        }

                        var bitmap = GenerateLcdBitmap();

                        if (bitmap != null)
                        {
                            if (sentBitmap == null)
                            {
                                sentBitmap = bitmap;
                                screen.DisplayBuffer(screen.CreateBufferFrom(sentBitmap));
                            }
                            else
                            {
                                var sectors = SKBitmapComparison.GetChangedSectors(sentBitmap, bitmap, 10, 10, 20, 80);
                                //Trace.WriteLine($"Sector detect: {sectors.Count} sectors {stopwatch.ElapsedMilliseconds}ms");

                                if (sectors.Count > 76)
                                {
                                    screen.DisplayBuffer(screen.CreateBufferFrom(bitmap));
                                }
                                else
                                {
                                    foreach (var sector in sectors)
                                    {
                                        screen.DisplayBuffer(sector.Left, sector.Top, screen.CreateBufferFrom(bitmap, sector.Left, sector.Top, sector.Width, sector.Height));
                                    }

                                    //Trace.WriteLine($"Sector update: {stopwatch.ElapsedMilliseconds}ms");
                                }
                                sentBitmap?.Dispose();
                                sentBitmap = bitmap;
                            }
                        }

                        fpsCounter.Update();
                        SharedModel.Instance.TuringPanelAFrameRate = fpsCounter.FramesPerSecond;
                        SharedModel.Instance.TuringPanelAFrameTime = stopwatch.ElapsedMilliseconds;

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
                Trace.WriteLine("TuringPanelA: Init error");
            }
            finally
            {
                SharedModel.Instance.TuringPanelARunning = false;
            }
        }
    }
}
