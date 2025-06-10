using InfoPanel.BeadaPanel;
using InfoPanel.BeadaPanel.PanelLink;
using InfoPanel.BeadaPanel.StatusLink;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class BeadaPanelDeviceTask : BackgroundTask
    {
        private readonly BeadaPanelDevice _device;
        private volatile int _panelWidth = 0;
        private volatile int _panelHeight = 0;

        public BeadaPanelDevice Device => _device;

        public BeadaPanelDeviceTask(BeadaPanelDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        public byte[]? GenerateLcdBuffer()
        {
            var profileGuid = _device.Config.ProfileGuid;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = _device.Config.Rotation;
                using var bitmap = PanelDrawTask.RenderSK(profile, false, colorType: SKColorType.Rgb565, alphaType: SKAlphaType.Opaque);

                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                return resizedBitmap.Bytes;
            }

            return null;
        }

        private async Task<UsbDevice?> FindTargetDeviceAsync(IEnumerable<UsbRegistry> allDevices, int vendorId, int productId)
        {
            foreach (UsbRegistry deviceReg in allDevices)
            {
                if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                {
                    // Priority 1: Match by USB path (fastest, works if path hasn't changed)
                    if (!string.IsNullOrEmpty(_device.Config.UsbPath) && deviceReg.DevicePath == _device.Config.UsbPath)
                    {
                        Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Found device by USB path: {_device.Config.UsbPath}");
                        return deviceReg.Device;
                    }

                    // Priority 2: Match by hardware serial number (reliable across reboots)
                    if (!string.IsNullOrEmpty(_device.Config.SerialNumber))
                    {
                        var panelInfo = await QueryDeviceStatusLinkAsync(deviceReg);
                        if (panelInfo != null && panelInfo.SerialNumber == _device.Config.SerialNumber)
                        {
                            Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Found device by hardware serial: {_device.Config.SerialNumber}");
                            // Update USB path for future fast reconnection
                            _device.Config.UsbPath = deviceReg.DevicePath ?? _device.Config.UsbPath;
                            return deviceReg.Device;
                        }
                    }

                    // Priority 3: Match by model fingerprint (when serial isn't available)
                    if (_device.Config.ModelType.HasValue && _device.FirmwareVersion > 0)
                    {
                        var panelInfo = await QueryDeviceStatusLinkAsync(deviceReg);
                        if (panelInfo != null && 
                            panelInfo.Model == _device.Config.ModelType &&
                            panelInfo.FirmwareVersion == _device.FirmwareVersion &&
                            (panelInfo.ModelInfo?.Width ?? panelInfo.ResolutionX) == _device.NativeResolutionX &&
                            (panelInfo.ModelInfo?.Height ?? panelInfo.ResolutionY) == _device.NativeResolutionY)
                        {
                            Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Found device by model fingerprint: {_device.Config.ModelType}_{_device.FirmwareVersion}");
                            // Update identification info for future use
                            _device.Config.UsbPath = deviceReg.DevicePath ?? _device.Config.UsbPath;
                            if (!string.IsNullOrEmpty(panelInfo.SerialNumber))
                            {
                                _device.Config.SerialNumber = panelInfo.SerialNumber;
                                _device.Config.IdentificationMethod = DeviceIdentificationMethod.HardwareSerial;
                            }
                            return deviceReg.Device;
                        }
                    }

                    // Priority 4: Match by legacy serial number (backward compatibility)
                    if (!string.IsNullOrEmpty(_device.Config.SerialNumber) && deviceReg.SymbolicName.Contains(_device.Config.SerialNumber))
                    {
                        Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Found device by legacy serial: {_device.Config.SerialNumber}");
                        return deviceReg.Device;
                    }
                }
            }

            Trace.WriteLine($"BeadaPanelDevice {_device.Name}: No matching device found with any identification method");
            return null;
        }

        private async Task<BeadaPanelInfo?> QueryDeviceStatusLinkAsync(UsbRegistry deviceReg)
        {
            UsbDevice? usbDevice = null;
            try
            {
                usbDevice = deviceReg.Device;
                if (usbDevice == null) return null;

                if (usbDevice is IUsbDevice wholeUsbDevice)
                {
                    wholeUsbDevice.SetConfiguration(1);
                    wholeUsbDevice.ClaimInterface(0);
                }

                var infoMessage = new StatusLinkMessage { Type = StatusLinkMessageType.GetPanelInfo };

                using var writer = usbDevice.OpenEndpointWriter(WriteEndpointID.Ep02);
                var writeResult = writer.Write(infoMessage.ToBuffer(), 500, out int _);
                if (writeResult != ErrorCode.None) return null;

                byte[] responseBuffer = new byte[100];
                using var reader = usbDevice.OpenEndpointReader(ReadEndpointID.Ep02);
                var readResult = reader.Read(responseBuffer, 500, out int bytesRead);
                if (readResult != ErrorCode.None || bytesRead == 0) return null;

                return BeadaPanelParser.ParsePanelInfoResponse(responseBuffer);
            }
            catch
            {
                return null;
            }
            finally
            {
                try
                {
                    if (usbDevice is IUsbDevice wholeUsbDevice)
                        wholeUsbDevice.ReleaseInterface(0);
                    usbDevice?.Close();
                }
                catch { }
            }
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);
            
            // Validate device has a known model type
            if (!_device.Config.ModelType.HasValue || !BeadaPanelModelDatabase.Models.ContainsKey(_device.Config.ModelType.Value))
            {
                Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Cannot start - unknown model type: {_device.Config.ModelType}");
                SharedModel.Instance.UpdateBeadaPanelDeviceStatus(_device.Id, false, false, 0, 0, "Unknown model type");
                return;
            }
            
            try
            {
                int vendorId = 0x4e58;
                int productId = 0x1001;

                UsbDeviceFinder finder = new(vendorId, productId);
                
                var allDevices = UsbDevice.AllDevices;
                UsbDevice? targetDevice = null;

                // Enhanced device reconnection logic using hardware characteristics
                targetDevice = await FindTargetDeviceAsync(allDevices, vendorId, productId);

                if (targetDevice == null)
                {
                    targetDevice = UsbDevice.OpenUsbDevice(finder);
                }

                if (targetDevice == null)
                {
                    Trace.WriteLine($"BeadaPanelDevice {_device.Name}: USB Device not found.");
                    SharedModel.Instance.UpdateBeadaPanelDeviceStatus(_device.Id, false, false, 0, 0, "Device not found");
                    return;
                }

                using UsbDevice usbDevice = targetDevice;

                if (usbDevice is IUsbDevice wholeUsbDevice)
                {
                    wholeUsbDevice.SetConfiguration(1);
                    wholeUsbDevice.ClaimInterface(0);
                }

                var infoMessage = new StatusLinkMessage
                {
                    Type = StatusLinkMessageType.GetPanelInfo
                };

                using UsbEndpointWriter writer = usbDevice.OpenEndpointWriter(WriteEndpointID.Ep02);
                writer.Write(infoMessage.ToBuffer(), 2000, out int _);

                Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Sent infoTag");

                byte[] responseBuffer = new byte[100];

                using UsbEndpointReader reader = usbDevice.OpenEndpointReader(ReadEndpointID.Ep02);
                reader.Read(responseBuffer, 2000, out int bytesRead);

                var panelInfo = BeadaPanelParser.ParsePanelInfoResponse(responseBuffer);

                if (panelInfo == null)
                {
                    Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Failed to parse panel info.");
                    SharedModel.Instance.UpdateBeadaPanelDeviceStatus(_device.Id, false, false, 0, 0, "Failed to parse panel info");
                    return;
                }

                Trace.WriteLine($"BeadaPanelDevice {_device.Name}: {panelInfo}");

                bool writeThroughMode = panelInfo.Platform == 1 || panelInfo.Platform == 2;

                _panelWidth = panelInfo.ModelInfo?.Width ?? panelInfo.ResolutionX;
                _panelHeight = panelInfo.ModelInfo?.Height ?? panelInfo.ResolutionY;

                SharedModel.Instance.UpdateBeadaPanelDeviceStatus(_device.Id, true, true);

                var startTag = new PanelLinkMessage
                {
                    Type = panelInfo.PanelLinkVersion == 1 ? PanelLinkMessageType.LegacyCommand1 : PanelLinkMessageType.StartMediaStream,
                    FormatString = PanelLinkMessage.BuildFormatString(panelInfo, writeThroughMode)
                };

                var endTag = new PanelLinkMessage
                {
                    Type = panelInfo.PanelLinkVersion == 1 ? PanelLinkMessageType.LegacyCommand2 : PanelLinkMessageType.EndMediaStream,
                };

                var resetTag = new StatusLinkMessage
                {
                    Type = StatusLinkMessageType.PanelLinkReset
                };

                var brightnessTag = new StatusLinkMessage
                {
                    Type = StatusLinkMessageType.SetBacklight,
                    Payload = [100]
                };

                writer.Write(resetTag.ToBuffer(), 2000, out int _);

                await Task.Delay(1000, token);

                var brightness = _device.Config.Brightness;

                brightnessTag.Payload = panelInfo.PanelLinkVersion == 1 ? [(byte)((brightness / 100.0 * 75) + 25)] : [(byte)brightness];

                writer.Write(brightnessTag.ToBuffer(), 2000, out int _);

                using UsbEndpointWriter dataWriter = usbDevice.OpenEndpointWriter(WriteEndpointID.Ep01);
                dataWriter.Write(startTag.ToBuffer(), 2000, out int _);

                Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Sent startTag");

                try
                {
                    FpsCounter fpsCounter = new(60);
                    byte[]? _latestFrame = null;
                    AutoResetEvent _frameAvailable = new(false);

                    var frameBufferPool = new ConcurrentBag<byte[]>();

                    var renderTask = Task.Run(async () =>
                    {
                        var stopwatch1 = new Stopwatch();

                        while (!token.IsCancellationRequested)
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
                                await Task.Delay(adaptiveFrameTime, token);
                            }
                        }
                    }, token);

                    var sendTask = Task.Run(() =>
                    {
                        var stopwatch2 = new Stopwatch();

                        while (!token.IsCancellationRequested)
                        {
                            if (brightness != _device.Config.Brightness)
                            {
                                brightness = _device.Config.Brightness;
                                brightnessTag.Payload = panelInfo.PanelLinkVersion == 1 ? [(byte)((brightness / 100.0 * 75) + 25)] : [(byte)brightness];
                                writer.Write(brightnessTag.ToBuffer(), 2000, out int _);
                            }

                            if (_frameAvailable.WaitOne(100))
                            {
                                var frame = Interlocked.Exchange(ref _latestFrame, null);
                                if (frame != null)
                                {
                                    stopwatch2.Restart();
                                    dataWriter.Write(frame, 2000, out int _);

                                    fpsCounter.Update(stopwatch2.ElapsedMilliseconds);
                                    SharedModel.Instance.UpdateBeadaPanelDeviceStatus(_device.Id, true, true, fpsCounter.FramesPerSecond, fpsCounter.FrameTime);
                                }
                            }
                        }
                    }, token);

                    await Task.WhenAll(renderTask, sendTask);
                }
                catch (TaskCanceledException)
                {
                    Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Task cancelled");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Exception during work: {ex.Message}");
                    SharedModel.Instance.UpdateBeadaPanelDeviceStatus(_device.Id, false, false, 0, 0, ex.Message);
                }
                finally
                {
                    try
                    {
                        brightnessTag.Payload = [0];
                        writer.Write(brightnessTag.ToBuffer(), 2000, out int _);

                        using var blankFrame = new SKBitmap(new SKImageInfo(_panelWidth, _panelHeight, SKColorType.Rgb565, SKAlphaType.Opaque));
                        dataWriter.Write(blankFrame.Bytes, 2000, out int _);

                        dataWriter.Write(endTag.ToBuffer(), 2000, out int _);
                        Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Sent endTag");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Exception when sending endTag: {ex.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Init error: {e.Message}");
                SharedModel.Instance.UpdateBeadaPanelDeviceStatus(_device.Id, false, false, 0, 0, e.Message);
            }
            finally
            {
                SharedModel.Instance.UpdateBeadaPanelDeviceStatus(_device.Id, false, false);
            }
        }

        /// <summary>
        /// Updates the device configuration while the task is running.
        /// This allows for real-time updates without restarting the device task.
        /// </summary>
        /// <param name="newConfig">The updated configuration</param>
        public void UpdateConfiguration(BeadaPanelDeviceConfig newConfig)
        {
            if (newConfig == null) return;

            // Update configuration properties that can be changed at runtime
            bool profileChanged = _device.Config.ProfileGuid != newConfig.ProfileGuid;
            bool rotationChanged = _device.Config.Rotation != newConfig.Rotation;
            bool brightnessChanged = _device.Config.Brightness != newConfig.Brightness;

            // Apply the configuration changes to the device's config object
            _device.Config.ProfileGuid = newConfig.ProfileGuid;
            _device.Config.Rotation = newConfig.Rotation;
            _device.Config.Brightness = newConfig.Brightness;

            // Log the changes
            if (profileChanged)
            {
                Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Profile changed to {newConfig.ProfileGuid}");
            }
            if (rotationChanged)
            {
                Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Rotation changed to {newConfig.Rotation}");
            }
            if (brightnessChanged)
            {
                Trace.WriteLine($"BeadaPanelDevice {_device.Name}: Brightness changed to {newConfig.Brightness}");
            }
        }
    }
}