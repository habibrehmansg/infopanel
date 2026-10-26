using InfoPanel.Drawing;
using InfoPanel.Extensions;
using InfoPanel.JonsboPanel;
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
    public sealed class JonsboPanelDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<JonsboPanelDeviceTask>();

        private readonly JonsboPanelDevice _device;
        private int _panelWidth;   // native portrait width  (462 on DS916)
        private int _panelHeight;  // native portrait height (1920 on DS916)

        public JonsboPanelDeviceTask(JonsboPanelDevice device)
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

            _device.UpdateRuntimeProperties(isRunning: false, errorMessage: string.Empty);
            _device.RuntimeProperties.Name = $"{modelInfo.Name} ({_panelWidth}x{_panelHeight})";

            if (modelInfo.TransportType == JonsboTransportType.Ms9132)
            {
                await DoMs9132WorkAsync(modelInfo, token);
                return;
            }

            int retryCount = 0;
            while (!token.IsCancellationRequested)
            {
                JonsboSerialDevice? serial = null;
                try
                {
                    Logger.Information("JonsboDevice {Device}: Opening (attempt {Retry})", _device, retryCount + 1);
                    serial = JonsboSerialDevice.Open(_device.DeviceLocation);

                    if (serial == null)
                    {
                        _device.UpdateRuntimeProperties(errorMessage: $"Cannot open {_device.DeviceLocation}");
                        await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
                        retryCount++;
                        continue;
                    }

                    // Handshake: F0 A5 5A 0F -> ASCII identity with model, native resolution, serial.
                    // The OEM app retries after 1 s of silence; do the same once before giving up.
                    var identity = serial.GetIdentity();
                    if (identity == null)
                    {
                        await Task.Delay(1000, token);
                        identity = serial.GetIdentity();
                    }

                    if (identity == null)
                    {
                        _device.UpdateRuntimeProperties(errorMessage: "No identity reply");
                        serial.Dispose();
                        await Task.Delay(2000, token);
                        retryCount++;
                        continue;
                    }

                    Logger.Information("JonsboDevice {Device}: model={Model} serial={Serial} native {W}x{H}",
                        _device, identity.Model, identity.Serial, identity.Width, identity.Height);

                    // Trust the device-reported native resolution.
                    if (identity.Width > 0 && identity.Height > 0)
                    {
                        _panelWidth = identity.Width;
                        _panelHeight = identity.Height;
                    }

                    _device.RuntimeProperties.Name = $"{modelInfo.Name} {identity.Model} ({_panelWidth}x{_panelHeight})";

                    retryCount = 0;
                    await RunRenderSendLoop(serial, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.Error(ex, "JonsboDevice {Device}: Error", _device);
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

        private async Task RunRenderSendLoop(JonsboSerialDevice serial, CancellationToken token)
        {
            FpsCounter fpsCounter = new(60);
            byte[]? latestFrame = null;
            byte[]? lastSentFrame = null;
            AutoResetEvent frameAvailable = new(false);

            var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var renderToken = renderCts.Token;

            _device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);

            // Render thread: produce JPEG frames at the target frame rate.
            var renderTask = Task.Run(async () =>
            {
                Thread.CurrentThread.Name ??= $"Jonsbo-Render-{_device.DeviceLocation}";
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
                    Logger.Error(e, "JonsboDevice {Device}: Render error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: e.Message);
                    renderCts.Cancel();
                }
            }, renderToken);

            // Send thread: drain frames to the serial port. The firmware has no keep-alive
            // command; the OEM app simply resends the current image every ~500 ms when the
            // content is static, so we resend the last frame if nothing new shows up.
            var sendTask = Task.Run(() =>
            {
                Thread.CurrentThread.Name ??= $"Jonsbo-Send-{_device.DeviceLocation}";
                try
                {
                    var stopwatch = new Stopwatch();
                    while (!token.IsCancellationRequested)
                    {
                        byte[]? jpegData;
                        if (frameAvailable.WaitOne(1000))
                        {
                            jpegData = Interlocked.Exchange(ref latestFrame, null);
                        }
                        else
                        {
                            // No new frame in 1 s — resend the previous one so the panel
                            // doesn't fall back to its boot animation.
                            jpegData = lastSentFrame;
                        }

                        if (jpegData != null)
                        {
                            stopwatch.Restart();
                            serial.SendJpegFrame(jpegData);
                            lastSentFrame = jpegData;

                            fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                            _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e, "JonsboDevice {Device}: Send error", _device);
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
            int maxBytes = modelInfo?.MaxJpegBytes ?? 256 * 1024;

            if (ConfigModel.Instance.GetProfile(_device.ProfileGuid) is Profile profile)
            {
                var rotation = _device.Rotation;

                using var bitmap = PanelDrawTask.RenderSK(profile, false,
                    colorType: SKColorType.Rgba8888,
                    alphaType: SKAlphaType.Opaque);

                // The JPEG must be in the panel's native portrait orientation; the rotation
                // setting maps the (usually landscape) profile onto it.
                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                SKBitmap encodeBitmap = resizedBitmap;
                SKBitmap? dimmed = null;
                try
                {
                    if (_device.Brightness < 100)
                    {
                        // No hardware backlight command is known for this firmware —
                        // brightness is applied in-pixel.
                        dimmed = ApplyBrightness(resizedBitmap);
                        encodeBitmap = dimmed;
                    }

                    int quality = _device.JpegQuality;
                    using var image = SKImage.FromBitmap(encodeBitmap);

                    // Drop quality until the JPEG fits the soft payload cap.
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

        // ==================== MS9132 transport (DS339) ====================

        private async Task DoMs9132WorkAsync(JonsboPanelModelInfo modelInfo, CancellationToken token)
        {
            int retryCount = 0;
            while (!token.IsCancellationRequested)
            {
                JonsboMs9132Device? ms = null;
                try
                {
                    Logger.Information("JonsboDevice {Device}: Opening MS9132 (attempt {Retry})", _device, retryCount + 1);
                    ms = JonsboMs9132Device.Open();

                    if (ms == null)
                    {
                        _device.UpdateRuntimeProperties(errorMessage: "Cannot open MS9132 (video interface driver missing?)");
                        await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
                        retryCount++;
                        continue;
                    }

                    if (!ms.IsPanelConnected())
                    {
                        Logger.Warning("JonsboDevice {Device}: MS9132 reports no panel connected (reg 0x32)", _device);
                        // Continue anyway — some firmware revisions may not report status.
                    }

                    // The panel reports its own native timing in its EDID, which the startup
                    // block reads. Prefer it over the model database: the same 345F:9132
                    // bridge ships behind several panel sizes, and driving one at the wrong
                    // geometry paints a sheared, washed-out image (#166).
                    var vic = modelInfo.Vic;
                    if (ms.EdidResolution is (int edidWidth, int edidHeight)
                        && JonsboMs9132Device.GetVicForResolution(edidWidth, edidHeight) is byte edidVic)
                    {
                        if (edidWidth != _panelWidth || edidHeight != _panelHeight)
                        {
                            Logger.Information(
                                "JonsboDevice {Device}: Using EDID timing {W}x{H} (VIC {Vic}) instead of {Model}'s {DbW}x{DbH}",
                                _device, edidWidth, edidHeight, edidVic, modelInfo.Name, _panelWidth, _panelHeight);
                        }
                        _panelWidth = edidWidth;
                        _panelHeight = edidHeight;
                        vic = edidVic;
                    }
                    else if (ms.EdidResolution is (int rawWidth, int rawHeight))
                    {
                        Logger.Warning(
                            "JonsboDevice {Device}: EDID reports {W}x{H}, which has no known mode index. Falling back to {DbW}x{DbH} VIC {Vic}",
                            _device, rawWidth, rawHeight, _panelWidth, _panelHeight, vic);
                    }

                    ms.SetMode(_panelWidth, _panelHeight, vic);

                    var panelName = string.IsNullOrEmpty(ms.EdidPanelName) ? modelInfo.Name : $"{modelInfo.Name} {ms.EdidPanelName}";
                    _device.RuntimeProperties.Name = $"{panelName} ({_panelWidth}x{_panelHeight})";
                    _device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);

                    retryCount = 0;
                    await RunMs9132RenderSendLoop(ms, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.Error(ex, "JonsboDevice {Device}: MS9132 error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                    // Leave the panel powered while retrying, otherwise a transport error
                    // turns into a visible on/off flicker loop on the panel.
                    if (ms != null) ms.PowerOffOnDispose = false;
                    retryCount++;
                }
                finally
                {
                    ms?.Dispose();
                    _device.UpdateRuntimeProperties(isRunning: false);
                }

                if (!token.IsCancellationRequested)
                    await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
            }
        }

        private async Task RunMs9132RenderSendLoop(JonsboMs9132Device ms, CancellationToken token)
        {
            FpsCounter fpsCounter = new(60);
            var stopwatch = new Stopwatch();
            byte[]? bgrBuffer = null;
            int framesSent = 0;

            while (!token.IsCancellationRequested)
            {
                stopwatch.Restart();

                GenerateBgrFrame(ref bgrBuffer);
                ms.SendFrame(bgrBuffer!, _panelWidth, _panelHeight);
                framesSent++;

                // The OEM app turns the output on only after the first frames are on the
                // wire, then polls the panel status once per frame. Enabling video earlier
                // leaves the scaler refusing to drain the bulk pipe on some units (#166).
                ms.PollPanelStatus();
                if (framesSent == 2) ms.EnableVideo();

                fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);

                var targetFrameTime = 1000 / Math.Max(1, _device.TargetFrameRate);
                var remaining = targetFrameTime - (int)stopwatch.ElapsedMilliseconds;
                if (remaining > 0) await Task.Delay(remaining, token);
            }
        }

        /// <summary>
        /// Renders the profile and converts to BGR888 at the panel's native portrait
        /// resolution, reusing <paramref name="bgrBuffer"/> across frames.
        /// </summary>
        private void GenerateBgrFrame(ref byte[]? bgrBuffer)
        {
            int pixelCount = _panelWidth * _panelHeight;
            bgrBuffer ??= new byte[pixelCount * 3];

            SKBitmap? rendered = null;
            try
            {
                if (ConfigModel.Instance.GetProfile(_device.ProfileGuid) is Profile profile)
                {
                    using var bitmap = PanelDrawTask.RenderSK(profile, false,
                        colorType: SKColorType.Rgba8888,
                        alphaType: SKAlphaType.Opaque);
                    rendered = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, _device.Rotation);
                }
                else
                {
                    rendered = new SKBitmap(_panelWidth, _panelHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    rendered.Erase(SKColors.Black);
                }

                float scale = Math.Clamp(_device.Brightness, 0, 100) / 100f;
                bool dim = scale < 1f;

                unsafe
                {
                    byte* src = (byte*)rendered.GetPixels().ToPointer();
                    int srcRowBytes = rendered.RowBytes;
                    fixed (byte* dstBase = bgrBuffer)
                    {
                        byte* dst = dstBase;
                        for (int y = 0; y < _panelHeight; y++)
                        {
                            byte* row = src + y * srcRowBytes;
                            for (int x = 0; x < _panelWidth; x++)
                            {
                                byte r = row[x * 4];      // Rgba8888: R,G,B,A
                                byte g = row[x * 4 + 1];
                                byte b = row[x * 4 + 2];
                                if (dim)
                                {
                                    r = (byte)(r * scale);
                                    g = (byte)(g * scale);
                                    b = (byte)(b * scale);
                                }
                                // Wire format is BGR888 (DRM_FORMAT_RGB888 memory order)
                                *dst++ = b;
                                *dst++ = g;
                                *dst++ = r;
                            }
                        }
                    }
                }
            }
            finally
            {
                rendered?.Dispose();
            }
        }
    }
}
