using InfoPanel.Drawing;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.ThermaltakePanel;
using InfoPanel.Utils;
using Serilog;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class ThermaltakePanelDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<ThermaltakePanelDeviceTask>();

        private readonly ThermaltakePanelDevice _device;
        private int _panelWidth;
        private int _panelHeight;
        private int _lastBrightness;

        public ThermaltakePanelDeviceTask(ThermaltakePanelDevice device)
        {
            _device = device;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            var modelInfo = _device.ModelInfo;
            if (modelInfo == null)
            {
                _device.UpdateRuntimeProperties(errorMessage: "Unknown model");
                return;
            }

            _panelWidth = modelInfo.Width;
            _panelHeight = modelInfo.Height;
            _lastBrightness = _device.Brightness;

            _device.UpdateRuntimeProperties(isRunning: false, errorMessage: string.Empty);
            _device.RuntimeProperties.Name = $"{modelInfo.Name} ({_panelWidth}x{_panelHeight})";

            // Retry loop with backoff
            int retryCount = 0;
            while (!token.IsCancellationRequested)
            {
                ThermaltakeHidDevice? hidDevice = null;
                try
                {
                    Logger.Information("ThermaltakeDevice {Device}: Opening (attempt {Retry})", _device, retryCount + 1);
                    hidDevice = ThermaltakeHidDevice.Open(modelInfo.VendorId, modelInfo.ProductId);

                    if (hidDevice == null)
                    {
                        _device.UpdateRuntimeProperties(errorMessage: "Device not found");
                        await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
                        retryCount++;
                        continue;
                    }

                    // Handshake
                    Logger.Information("ThermaltakeDevice {Device}: Handshake", _device);
                    if (!hidDevice.Handshake())
                    {
                        _device.UpdateRuntimeProperties(errorMessage: "Handshake failed");
                        hidDevice.Dispose();
                        await Task.Delay(2000, token);
                        retryCount++;
                        continue;
                    }

                    retryCount = 0;

                    // Render and send loop
                    await RunRenderSendLoop(hidDevice, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "ThermaltakeDevice {Device}: Error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                    retryCount++;
                }
                finally
                {
                    hidDevice?.Dispose();
                    _device.UpdateRuntimeProperties(isRunning: false);
                }

                if (!token.IsCancellationRequested)
                    await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
            }
        }

        private async Task RunRenderSendLoop(ThermaltakeHidDevice hidDevice, CancellationToken token)
        {
            FpsCounter fpsCounter = new(60);
            byte[]? latestFrame = null;
            AutoResetEvent frameAvailable = new(false);

            var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var renderToken = renderCts.Token;

            _device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);

            var renderTask = Task.Run(async () =>
            {
                Thread.CurrentThread.Name ??= $"Thermaltake-Render-{_device.DeviceLocation}";
                try
                {
                    var stopwatch = new Stopwatch();
                    while (!renderToken.IsCancellationRequested)
                    {
                        stopwatch.Restart();

                        var frame = GenerateJpegBuffer();
                        Interlocked.Exchange(ref latestFrame, frame);
                        frameAvailable.Set();

                        var targetFrameTime = 1000 / Math.Max(1, _device.TargetFrameRate);
                        var elapsedMs = (int)stopwatch.ElapsedMilliseconds;
                        var adaptiveFrameTime = targetFrameTime - elapsedMs;
                        if (adaptiveFrameTime > 0)
                            await Task.Delay(adaptiveFrameTime, token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    Logger.Error(e, "ThermaltakeDevice {Device}: Render error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: e.Message);
                    renderCts.Cancel();
                }
            }, renderToken);

            var sendTask = Task.Run(() =>
            {
                Thread.CurrentThread.Name ??= $"Thermaltake-Send-{_device.DeviceLocation}";
                try
                {
                    var stopwatch = new Stopwatch();
                    while (!token.IsCancellationRequested)
                    {
                        if (frameAvailable.WaitOne(100))
                        {
                            var jpegData = Interlocked.Exchange(ref latestFrame, null);
                            if (jpegData != null)
                            {
                                stopwatch.Restart();

                                hidDevice.SendJpegFrame(jpegData);

                                fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                                _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e, "ThermaltakeDevice {Device}: Send error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: e.Message);
                }
                finally
                {
                    renderCts.Cancel();
                }
            }, token);

            await Task.WhenAll(renderTask, sendTask);

            frameAvailable.Dispose();
            renderCts.Dispose();
        }

        private byte[] GenerateJpegBuffer()
        {
            var profileGuid = _device.ProfileGuid;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = _device.Rotation;

                using var bitmap = PanelDrawTask.RenderSK(profile, false,
                    colorType: SKColorType.Rgba8888,
                    alphaType: SKAlphaType.Opaque);

                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                SKBitmap encodeBitmap = resizedBitmap;
                SKBitmap? dimmed = null;
                try
                {
                    if (_device.Brightness < 100)
                    {
                        dimmed = ApplyBrightness(resizedBitmap);
                        encodeBitmap = dimmed;
                    }

                int quality = _device.JpegQuality;
                using var image = SKImage.FromBitmap(encodeBitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                return data.ToArray();
                }
                finally
                {
                    dimmed?.Dispose();
                }
            }

            // No profile selected: return a black JPEG
            return GenerateBlackJpeg();
        }

        private SKBitmap ApplyBrightness(SKBitmap source)
        {
            float scale = Math.Clamp(_device.Brightness, 0, 100) / 100f;
            var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
            using var canvas = new SKCanvas(result);
            using var paint = new SKPaint();
            paint.ColorFilter = SKColorFilter.CreateColorMatrix(
            [
                scale, 0,     0,     0, 0,
                0,     scale, 0,     0, 0,
                0,     0,     scale, 0, 0,
                0,     0,     0,     1, 0
            ]);
            canvas.DrawBitmap(source, 0, 0, paint);
            return result;
        }

        private byte[]? _cachedBlackJpeg;

        private byte[] GenerateBlackJpeg()
        {
            if (_cachedBlackJpeg != null) return _cachedBlackJpeg;

            using var bitmap = new SKBitmap(_panelWidth, _panelHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 50);
            _cachedBlackJpeg = data.ToArray();
            return _cachedBlackJpeg;
        }
    }
}
