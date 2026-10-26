using InfoPanel.Drawing;
using InfoPanel.Extensions;
using InfoPanel.JlPanel;
using InfoPanel.Models;
using InfoPanel.Utils;
using Serilog;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class JlPanelDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<JlPanelDeviceTask>();

        private readonly JlPanelDevice _device;
        private int _panelWidth;
        private int _panelHeight;
        private int _lastBrightness;

        public JlPanelDeviceTask(JlPanelDevice device)
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

            int retryCount = 0;
            while (!token.IsCancellationRequested)
            {
                JlSerialDevice? serial = null;
                try
                {
                    Logger.Information("JlDevice {Device}: Opening (attempt {Retry})", _device, retryCount + 1);
                    serial = JlSerialDevice.Open(_device.DeviceLocation);

                    if (serial == null)
                    {
                        _device.UpdateRuntimeProperties(errorMessage: $"Cannot open {_device.DeviceLocation}");
                        await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
                        retryCount++;
                        continue;
                    }

                    // Handshake: getDeviceInfo. The reply confirms the panel is responsive
                    // and tells us its actual width/height (in case the model database is wrong).
                    var info = serial.GetDeviceInfo();
                    if (info == null || info.Status != 200)
                    {
                        _device.UpdateRuntimeProperties(errorMessage: "getDeviceInfo failed");
                        serial.Dispose();
                        await Task.Delay(2000, token);
                        retryCount++;
                        continue;
                    }

                    Logger.Information("JlDevice {Device}: model={Model} ver={Ver} {W}x{H} angle={Angle}",
                        _device, info.Model, info.Version, info.Width, info.Height, info.Angle);

                    // Trust the device-reported resolution if it differs from our database.
                    if (info.Width > 0 && info.Height > 0)
                    {
                        _panelWidth = info.Width;
                        _panelHeight = info.Height;
                    }

                    // Push initial brightness.
                    try { serial.SetBrightness(_device.Brightness); }
                    catch (Exception ex) { Logger.Warning(ex, "JlDevice {Device}: SetBrightness failed", _device); }

                    retryCount = 0;
                    await RunRenderSendLoop(serial, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.Error(ex, "JlDevice {Device}: Error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                    retryCount++;
                }
                finally
                {
                    serial?.Dispose();
                    _device.UpdateRuntimeProperties(isRunning: false);
                }

                if (!token.IsCancellationRequested)
                    await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
            }
        }

        private async Task RunRenderSendLoop(JlSerialDevice serial, CancellationToken token)
        {
            FpsCounter fpsCounter = new(60);
            byte[]? latestFrame = null;
            AutoResetEvent frameAvailable = new(false);

            var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var renderToken = renderCts.Token;

            _device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);

            // Render thread: produce JPEG frames at the target frame rate.
            var renderTask = Task.Run(async () =>
            {
                Thread.CurrentThread.Name ??= $"Jl-Render-{_device.DeviceLocation}";
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
                        var remaining = targetFrameTime - elapsedMs;
                        if (remaining > 0) await Task.Delay(remaining, renderToken);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    Logger.Error(e, "JlDevice {Device}: Render error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: e.Message);
                    renderCts.Cancel();
                }
            }, renderToken);

            // Send thread: drain frames to the serial port.
            // Also drives the keep-alive: the firmware needs cmd 0x11 at least every 1500 ms,
            // and SendJpegFrame already prepends one keep-alive per frame, so any framerate
            // >= ~1 fps is safe. We add a fallback ping if frames stop being produced.
            var sendTask = Task.Run(() =>
            {
                Thread.CurrentThread.Name ??= $"Jl-Send-{_device.DeviceLocation}";
                try
                {
                    var stopwatch = new Stopwatch();
                    while (!token.IsCancellationRequested)
                    {
                        if (frameAvailable.WaitOne(1000))
                        {
                            var jpegData = Interlocked.Exchange(ref latestFrame, null);
                            if (jpegData != null)
                            {
                                stopwatch.Restart();

                                // Push brightness if it changed.
                                if (_lastBrightness != _device.Brightness)
                                {
                                    _lastBrightness = _device.Brightness;
                                    try { serial.SetBrightness(_lastBrightness); }
                                    catch (Exception ex) { Logger.Warning(ex, "JlDevice {Device}: SetBrightness failed", _device); }
                                }

                                serial.SendJpegFrame(jpegData);

                                fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                                _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                            }
                        }
                        else
                        {
                            // No frame in 1s — send a keep-alive so the live pipeline doesn't time out.
                            try { serial.SendKeepAlive(); }
                            catch (Exception ex)
                            {
                                Logger.Warning(ex, "JlDevice {Device}: keep-alive failed", _device);
                                break;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e, "JlDevice {Device}: Send error", _device);
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
            var modelInfo = _device.ModelInfo;
            int maxBytes = modelInfo?.MaxJpegBytes ?? 80 * 1024;

            if (ConfigModel.Instance.GetProfile(_device.ProfileGuid) is Profile profile)
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
                        // The firmware also has a hardware backlight (cmd 0x03), but applying
                        // brightness in-pixel preserves the look of dim profiles below the
                        // backlight floor.
                        dimmed = ApplyBrightness(resizedBitmap);
                        encodeBitmap = dimmed;
                    }

                    int quality = _device.JpegQuality;
                    using var image = SKImage.FromBitmap(encodeBitmap);

                    // Drop quality until the JPEG fits in the firmware's max payload.
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                        if (data.Size <= maxBytes || quality <= 30)
                            return data.ToArray();
                        quality = Math.Max(30, quality - 15);
                    }
                    using var fallback = image.Encode(SKEncodedImageFormat.Jpeg, 30);
                    return fallback.ToArray();
                }
                finally
                {
                    dimmed?.Dispose();
                }
            }

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
