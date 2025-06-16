using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.TuringPanel;
using InfoPanel.Utils;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class TuringPanelUsbDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<TuringPanelUsbDeviceTask>();
        private readonly TuringPanelDevice _device;
        private readonly int _panelWidth = 480;
        private readonly int _panelHeight = 1920;
        private static int _maxSize = 1024 * 1024; // 1MB
        private DateTime _downgradeRenderingUntil = DateTime.MinValue;

        public TuringPanelDevice Device => _device;

        public TuringPanelUsbDeviceTask(TuringPanelDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        public byte[]? GenerateLcdBuffer()
        {
            var profileGuid = _device.ProfileGuid;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = _device.Rotation;
                using var bitmap = PanelDrawTask.RenderSK(profile, false,
                    colorType: DateTime.Now > _downgradeRenderingUntil ? SKColorType.Rgba8888 : SKColorType.Argb4444);

                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                var options = new SKPngEncoderOptions(
                        filterFlags: SKPngEncoderFilterFlags.NoFilters,
                        zLibLevel: 3
                        );

                using var pixmap = resizedBitmap.PeekPixels();
                using var data = pixmap.Encode(options);

                if (data == null || data.IsEmpty)
                {
                    Logger.Error("TuringPanelDevice {Device}: Failed to encode bitmap to PNG", _device);
                    return null;
                }

                var result = data.ToArray();

                if (resizedBitmap.ColorType != SKColorType.Argb4444 && result.Length > _maxSize)
                {
                    Logger.Warning("TuringPanelDevice {Device}: Downgrading rendering to ARGB4444 due to size constraints. Size: {Size} bytes, max: {MaxSize} bytes", 
                        _device, result.Length, _maxSize);
                    DateTime now = DateTime.Now;
                    DateTime targetTime = now.AddSeconds(10);
                    _downgradeRenderingUntil = targetTime;
                    return null;
                }
                else if (result.Length > _maxSize)
                {
                    result = DownscaleUpscaleAndEncode(resizedBitmap);
                }

                return result;
            }

            return null;
        }

        private static byte[]? DownscaleUpscaleAndEncode(SKBitmap original, float scale = 0.5f)
        {
            int downWidth = (int)(original.Width * scale);
            int downHeight = (int)(original.Height * scale);

            using var downscaled = original.Resize(new SKImageInfo(downWidth, downHeight), SKSamplingOptions.Default);
            using var upscaled = downscaled.Resize(new SKImageInfo(original.Width, original.Height), SKSamplingOptions.Default);

            var options = new SKPngEncoderOptions(
                      filterFlags: SKPngEncoderFilterFlags.NoFilters,
                      zLibLevel: 3
                      );

            using var pixmap = upscaled.PeekPixels();
            using var data = pixmap.Encode(options);

            if (data == null || data.IsEmpty)
            {
                Logger.Error("Failed to encode bitmap to PNG");
                return null;
            }
            return data.ToArray();
        }

        private async Task<UsbRegistry?> FindTargetDeviceAsync()
        {
            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid == 0x1cbe && deviceReg.Pid == 0x0088) // VENDOR_ID and PRODUCT_ID from TuringDevice
                {
                    var deviceId = deviceReg.DeviceProperties["DeviceID"] as string;

                    if (string.IsNullOrEmpty(deviceId))
                    {
                        Logger.Debug("TuringPanelDevice {Device}: Unable to get DeviceId for device {DevicePath}", _device, deviceReg.DevicePath);
                        continue;
                    }

                    if(_device.IsMatching(deviceId))
                    {
                        Logger.Information("TuringPanelDevice {Device}: Found matching device with DeviceId {DeviceId}", _device, deviceId);
                        return deviceReg;
                    }
                }
            }

            return null;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);
            
            try
            {
                var usbRegistry = await FindTargetDeviceAsync();

                if (usbRegistry == null)
                {
                    Logger.Warning("TuringPanelDevice {Device}: USB Device not found.", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "Device not found");
                    return;
                }

                using var device = new TuringDevice();
                
                try
                {
                    device.Initialize(usbRegistry);
                }
                catch (TuringDeviceException ex)
                {
                    Logger.Error("TuringPanelDevice {Device}: Failed to initialize - {Error}", _device, ex.Message);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                    return;
                }
                
                Logger.Information("TuringPanelDevice {Device}: Initialized successfully", _device);
                _device.UpdateRuntimeProperties(isRunning: true);

                try
                {
                    // Delay for sync
                    device.SendSyncCommand();
                    Thread.Sleep(200);
                    device.SendSyncCommand();
                    Thread.Sleep(200);

                    // Set brightness
                    var brightness = _device.Brightness;
                    device.SendBrightnessCommand((byte)brightness);

                    device.SendSyncCommand();
                    Thread.Sleep(200);

                    FpsCounter fpsCounter = new(60);
                    byte[]? _latestFrame = null;
                    AutoResetEvent _frameAvailable = new(false);

                    var frameBufferPool = new ConcurrentBag<byte[]>();

                    var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var renderToken = renderCts.Token;

                    var renderTask = Task.Run(async () =>
                    {
                        Thread.CurrentThread.Name ??= $"TuringPanel-Render-{_device.DeviceId}";
                        var stopwatch1 = new Stopwatch();

                        while (!renderToken.IsCancellationRequested)
                        {
                            stopwatch1.Restart();
                            var frame = GenerateLcdBuffer();

                            if (frame != null)
                            {
                                var oldFrame = Interlocked.Exchange(ref _latestFrame, frame);
                                _frameAvailable.Set();
                            }

                            var targetFrameTime = 1000 / ConfigModel.Instance.Settings.TargetFrameRate;
                            var desiredFrameTime = Math.Max((int)(fpsCounter.FrameTime * 0.9), targetFrameTime);
                            var adaptiveFrameTime = 0;

                            var elapsedMs = (int)stopwatch1.ElapsedMilliseconds;

                            if (elapsedMs < desiredFrameTime)
                            {
                                adaptiveFrameTime = desiredFrameTime - elapsedMs;
                            }

                            if (adaptiveFrameTime > 0)
                            {
                                await Task.Delay(adaptiveFrameTime, renderToken);
                            }
                        }
                    }, renderToken);

                    var sendTask = Task.Run(() =>
                    {
                        Thread.CurrentThread.Name ??= $"TuringPanel-Send-{_device.DeviceId}";
                        try
                        {
                            var stopwatch2 = new Stopwatch();

                            while (!token.IsCancellationRequested)
                            {
                                if (brightness != _device.Brightness)
                                {
                                    brightness = _device.Brightness;
                                    device.SendBrightnessCommand((byte)brightness);
                                    device.SendSyncCommand();
                    Thread.Sleep(200);
                                }

                                if (_frameAvailable.WaitOne(100))
                                {
                                    var frame = Interlocked.Exchange(ref _latestFrame, null);
                                    if (frame != null)
                                    {
                                        stopwatch2.Restart();
                                        device.SendPngBytes(frame);

                                        fpsCounter.Update(stopwatch2.ElapsedMilliseconds);
                                        _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                                    }
                                }
                            }
                        }
                        catch(Exception e)
                        {
                            Logger.Error(e, "TuringPanelDevice {Device}: Error in send task", _device);
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
                    Logger.Debug("TuringPanelDevice {Device}: Task cancelled", _device);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "TuringPanelDevice {Device}: Exception during work", _device);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                }
                finally
                {
                    try
                    {
                        device.SendBrightnessCommand(0);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "TuringPanelDevice {Device}: Exception when setting brightness to 0", _device);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "TuringPanelDevice {Device}: Init error", _device);
                _device.UpdateRuntimeProperties(errorMessage: e.Message);
            }
            finally
            {
                _device.UpdateRuntimeProperties(isRunning: false);
            }
        }
    }
}