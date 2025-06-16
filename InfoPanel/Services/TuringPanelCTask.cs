using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using SkiaSharp;
using System;
using Serilog;
using System.Threading;
using System.Threading.Tasks;
using TuringSmartScreenLib;
using TuringSmartScreenLib.Helpers.SkiaSharp;
using System.Diagnostics;

namespace InfoPanel
{
    public sealed class TuringPanelCTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<TuringPanelCTask>();

        private readonly TuringPanelDevice _device;

        private readonly int _panelWidth = 800;
        private readonly int _panelHeight = 480;

        public TuringPanelDevice Device => _device;
        public TuringPanelCTask(TuringPanelDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

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
                using var screen = ScreenFactory.Create(ScreenType.RevisionE, _device.DeviceLocation);

                if (screen == null)
                {
                    Logger.Warning("TuringPanelC: Screen not found on port {Port}", _device.DeviceLocation);
                    return;
                }

                Logger.Information("TuringPanelC: Screen found and initialized");
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
                    Logger.Debug("TuringPanelC task cancelled");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Exception during TuringPanelC execution");
                }
                finally
                {
                    sentBitmap?.Dispose();
                    screen.ScreenOff();
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "TuringPanelC: Initialization error");
            }finally
            {
                SharedModel.Instance.TuringPanelCRunning = false;
            }
        }
    }
}
