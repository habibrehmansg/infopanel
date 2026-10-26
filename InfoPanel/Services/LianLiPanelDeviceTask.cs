using InfoPanel.Extensions;
using InfoPanel.LianLiPanel;
using InfoPanel.Models;
using InfoPanel.Utils;
using InfoPanel.ViewModels;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class LianLiPanelDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<LianLiPanelDeviceTask>();
        private readonly LianLiPanelDevice _device;
        private readonly int _panelWidth;
        private readonly int _panelHeight;
        private LCD_ROTATION? _lastLoggedRotation;

        public LianLiPanelDeviceTask(LianLiPanelDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            if (device.ModelInfo == null)
            {
                throw new ArgumentException("Device model info cannot be null", nameof(device));
            }

            _panelWidth = device.ModelInfo.Width;
            _panelHeight = device.ModelInfo.Height;
        }

        public byte[]? GenerateLcdBuffer()
        {
            var profileGuid = _device.ProfileGuid;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = GetDeviceRotation();

                if (_lastLoggedRotation != rotation)
                {
                    _lastLoggedRotation = rotation;
                    Logger.Information("LianLiPanelDevice {Device}: Rotation changed. UI setting={UISetting}, mapped rotation={MappedRotation}",
                        _device.Id, _device.Rotation, rotation);
                }

                using var bitmap = PanelDrawTask.RenderSK(profile, false);
                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);
                using var pixmap = resizedBitmap.PeekPixels();
                using var data = pixmap.Encode(SKEncodedImageFormat.Jpeg, _device.JpegQuality);

                if (data == null || data.IsEmpty)
                {
                    Logger.Error("LianLiPanelDevice {Device}: Failed to encode bitmap to JPEG", _device);
                    return null;
                }

                return data.ToArray();
            }

            return null;
        }

        private LCD_ROTATION GetDeviceRotation()
        {
            if (_device.ModelInfo?.RequiresPortraitRotationOffset != true)
            {
                return _device.Rotation;
            }

            return _device.Rotation switch
            {
                LCD_ROTATION.RotateNone => LCD_ROTATION.Rotate90FlipNone,
                LCD_ROTATION.Rotate90FlipNone => LCD_ROTATION.Rotate180FlipNone,
                LCD_ROTATION.Rotate180FlipNone => LCD_ROTATION.Rotate270FlipNone,
                LCD_ROTATION.Rotate270FlipNone => LCD_ROTATION.RotateNone,
                _ => _device.Rotation
            };
        }

        private static byte[] EncodeSolidFrame(int width, int height, SKColor color, SKEncodedImageFormat format, int quality)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(color);

            using var pixmap = bitmap.PeekPixels();
            using var data = pixmap.Encode(format, quality);
            return data?.ToArray() ?? Array.Empty<byte>();
        }

        private void PrepareImageLayers(LianLiUsbScreenDevice screenDevice)
        {
            try
            {
                var syncClockOk = screenDevice.SyncClockOnly();
                Thread.Sleep(50);

                var stopClockOk = screenDevice.StopClock();
                Thread.Sleep(50);

                var clearPng = EncodeSolidFrame(_panelWidth, _panelHeight, SKColors.Transparent, SKEncodedImageFormat.Png, 100);
                var clearPngOk = clearPng.Length > 0 && screenDevice.DrawPngLayer(clearPng);
                Thread.Sleep(50);

                var clearJpeg = EncodeSolidFrame(_panelWidth, _panelHeight, SKColors.Black, SKEncodedImageFormat.Jpeg, 95);
                var clearJpegOk = clearJpeg.Length > 0 && screenDevice.DrawJpegLayer(clearJpeg);
                Thread.Sleep(50);

                var frameRateOk = screenDevice.SetFrameRate((byte)_device.TargetFrameRate);

                Logger.Information(
                    "LianLiPanelDevice {Device}: Prep sequence results: SyncClockOnly={SyncClockOk}, StopClock={StopClockOk}, ClearPng={ClearPngOk} ({ClearPngBytes} bytes), ClearJpeg={ClearJpegOk} ({ClearJpegBytes} bytes), SetFrameRate={FrameRateOk}",
                    _device,
                    syncClockOk,
                    stopClockOk,
                    clearPngOk,
                    clearPng.Length,
                    clearJpegOk,
                    clearJpeg.Length,
                    frameRateOk);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "LianLiPanelDevice {Device}: Prep sequence failed", _device);
            }
        }

        private Task<UsbRegistry?> FindTargetDeviceAsync()
        {
            if (_device.ModelInfo == null)
            {
                Logger.Error("LianLiPanelDevice {Device}: ModelInfo is null", _device);
                return Task.FromResult<UsbRegistry?>(null);
            }

            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid == _device.ModelInfo.VendorId && deviceReg.Pid == _device.ModelInfo.ProductId)
                {
                    var deviceId = deviceReg.DeviceProperties["DeviceID"] as string;

                    if (string.IsNullOrEmpty(deviceId))
                    {
                        Logger.Debug("LianLiPanelDevice {Device}: Unable to get DeviceId for device {DevicePath}", _device, deviceReg.DevicePath);
                        continue;
                    }

                    if (_device.IsMatching(deviceId))
                    {
                        Logger.Information("LianLiPanelDevice {Device}: Found matching device with DeviceId {DeviceId}", _device, deviceId);
                        return Task.FromResult<UsbRegistry?>(deviceReg);
                    }
                }
            }

            return Task.FromResult<UsbRegistry?>(null);
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            try
            {
                var usbRegistry = await FindTargetDeviceAsync();

                if (usbRegistry == null)
                {
                    Logger.Warning("LianLiPanelDevice {Device}: USB device not found.", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "Device not found");
                    return;
                }

                if (!usbRegistry.Open(out var usbDevice))
                {
                    Logger.Error("LianLiPanelDevice {Device}: Failed to open USB device", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "Failed to open USB device");
                    return;
                }

                using var screenDevice = new LianLiUsbScreenDevice(usbDevice);

                Logger.Information("LianLiPanelDevice {Device}: Initialized successfully", _device);
                _device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);

                try
                {
                    if (!screenDevice.Sync())
                    {
                        Logger.Warning("LianLiPanelDevice {Device}: Sync failed (1st attempt), device not ready", _device);
                        _device.UpdateRuntimeProperties(errorMessage: "Device not ready (sync failed)");
                        return;
                    }
                    Thread.Sleep(200);

                    if (!screenDevice.Sync())
                    {
                        Logger.Warning("LianLiPanelDevice {Device}: Sync failed (2nd attempt), device not ready", _device);
                        _device.UpdateRuntimeProperties(errorMessage: "Device not ready (sync failed)");
                        return;
                    }
                    Thread.Sleep(200);

                    screenDevice.StopMedia();
                    Thread.Sleep(200);

                    PrepareImageLayers(screenDevice);
                    Thread.Sleep(200);

                    var brightness = _device.Brightness;
                    screenDevice.SetBrightness((byte)brightness);

                    if (!screenDevice.Sync())
                    {
                        Logger.Warning("LianLiPanelDevice {Device}: Sync failed after brightness set, device not ready", _device);
                        _device.UpdateRuntimeProperties(errorMessage: "Device not ready (sync failed)");
                        return;
                    }
                    Thread.Sleep(200);

                    FpsCounter fpsCounter = new(60);
                    byte[]? latestFrame = null;
                    AutoResetEvent frameAvailable = new(false);

                    var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var renderToken = renderCts.Token;

                    var renderTask = Task.Run(async () =>
                    {
                        Thread.CurrentThread.Name ??= $"LianLiPanel-Render-{_device.DeviceId}";
                        var stopwatch = new Stopwatch();

                        while (!renderToken.IsCancellationRequested)
                        {
                            stopwatch.Restart();
                            var frame = GenerateLcdBuffer();

                            if (frame != null)
                            {
                                Interlocked.Exchange(ref latestFrame, frame);
                                frameAvailable.Set();
                            }

                            var targetFrameTime = 1000 / Math.Max(1, _device.TargetFrameRate);
                            var desiredFrameTime = Math.Max((int)fpsCounter.FrameTime, targetFrameTime);
                            var elapsedMs = (int)stopwatch.ElapsedMilliseconds;
                            var adaptiveFrameTime = desiredFrameTime - elapsedMs;

                            if (adaptiveFrameTime > 0)
                            {
                                await Task.Delay(adaptiveFrameTime, renderToken);
                            }
                        }
                    }, renderToken);

                    var sendTask = Task.Run(() =>
                    {
                        Thread.CurrentThread.Name ??= $"LianLiPanel-Send-{_device.DeviceId}";
                        try
                        {
                            var stopwatch = new Stopwatch();

                            while (!token.IsCancellationRequested)
                            {
                                if (brightness != _device.Brightness)
                                {
                                    brightness = _device.Brightness;
                                    screenDevice.SetBrightness((byte)brightness);
                                    if (!screenDevice.Sync())
                                    {
                                        Logger.Warning("LianLiPanelDevice {Device}: Sync failed during brightness update", _device);
                                        _device.UpdateRuntimeProperties(errorMessage: "Sync failed");
                                        break;
                                    }
                                    Thread.Sleep(200);
                                }

                                if (frameAvailable.WaitOne(100))
                                {
                                    var frame = Interlocked.Exchange(ref latestFrame, null);
                                    if (frame != null)
                                    {
                                        stopwatch.Restart();
                                        if (!screenDevice.DrawJpeg(frame))
                                        {
                                            Logger.Warning("LianLiPanelDevice {Device}: DrawJpeg failed", _device);
                                            _device.UpdateRuntimeProperties(errorMessage: "Draw failed");
                                            break;
                                        }

                                        fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                                        _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "LianLiPanelDevice {Device}: Error in send task", _device);
                        }
                        finally
                        {
                            renderCts.Cancel();
                        }
                    }, token);

                    await Task.WhenAll(renderTask, sendTask);
                }
                catch (TaskCanceledException)
                {
                    Logger.Debug("LianLiPanelDevice {Device}: Task cancelled", _device);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "LianLiPanelDevice {Device}: Exception during work", _device);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                }
                finally
                {
                    try
                    {
                        screenDevice.StopMedia();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "LianLiPanelDevice {Device}: Exception when stopping media", _device);
                    }

                    try
                    {
                        screenDevice.SetBrightness(0);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "LianLiPanelDevice {Device}: Exception when setting brightness to 0", _device);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "LianLiPanelDevice {Device}: Init error", _device);
                _device.UpdateRuntimeProperties(errorMessage: ex.Message);
            }
            finally
            {
                _device.UpdateRuntimeProperties(isRunning: false);
            }
        }
    }
}
