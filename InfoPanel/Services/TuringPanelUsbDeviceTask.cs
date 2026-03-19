using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using LcdDriver.TuringSmartScreen;
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
        private readonly int _panelWidth;
        private readonly int _panelHeight;

        public TuringPanelDevice Device => _device;

        public TuringPanelUsbDeviceTask(TuringPanelDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            if(device.ModelInfo == null)
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
                var rotation = _device.Rotation;
                using var bitmap = PanelDrawTask.RenderSK(profile, false);

                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                using var pixmap = resizedBitmap.PeekPixels();
                using var data = pixmap.Encode(SKEncodedImageFormat.Jpeg, _device.JpegQuality);

                if (data == null || data.IsEmpty)
                {
                    Logger.Error("TuringPanelDevice {Device}: Failed to encode bitmap to JPEG", _device);
                    return null;
                }

                return data.ToArray();
            }

            return null;
        }

        private async Task<UsbRegistry?> FindTargetDeviceAsync()
        {
            if(_device.ModelInfo == null)
            {
                Logger.Error("TuringPanelDevice {Device}: ModelInfo is null", _device);
                return null;
            }

            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid == _device.ModelInfo.VendorId && deviceReg.Pid == _device.ModelInfo.ProductId)
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

                if (!usbRegistry.Open(out var usbDevice))
                {
                    Logger.Error("TuringPanelDevice {Device}: Failed to open USB device", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "Failed to open USB device");
                    return;
                }

                using var screenDevice = new ScreenDevice(usbDevice);

                Logger.Information("TuringPanelDevice {Device}: Initialized successfully", _device);
                _device.UpdateRuntimeProperties(isRunning: true);

                try
                {
                    // Sync
                    screenDevice.Sync();
                    Thread.Sleep(200);
                    screenDevice.Sync();
                    Thread.Sleep(200);

                    // Stop any video playback to prevent flickering
                    screenDevice.StopMedia();
                    Thread.Sleep(200);

                    // Set brightness
                    var brightness = _device.Brightness;
                    screenDevice.SetBrightness((byte)brightness);

                    screenDevice.Sync();
                    Thread.Sleep(200);

                    FpsCounter fpsCounter = new(60);
                    byte[]? _latestFrame = null;
                    AutoResetEvent _frameAvailable = new(false);

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

                            var targetFrameTime = 1000 / Math.Max(1, _device.TargetFrameRate);
                            var desiredFrameTime = Math.Max((int)(fpsCounter.FrameTime), targetFrameTime);
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
                                    screenDevice.SetBrightness((byte)brightness);
                                    screenDevice.Sync();
                                    Thread.Sleep(200);
                                }

                                if (_frameAvailable.WaitOne(100))
                                {
                                    var frame = Interlocked.Exchange(ref _latestFrame, null);
                                    if (frame != null)
                                    {
                                        stopwatch2.Restart();
                                        screenDevice.DrawJpeg(frame);

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
                        screenDevice.SetBrightness(0);
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
