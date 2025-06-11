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
            var profileGuid = _device.ProfileGuid;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = _device.Rotation;
                using var bitmap = PanelDrawTask.RenderSK(profile, false, colorType: SKColorType.Rgb565, alphaType: SKAlphaType.Opaque);

                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                return resizedBitmap.Bytes;
            }

            return null;
        }

        private async Task<UsbRegistry?> FindTargetDeviceAsync()
        {
            int vendorId = 0x4e58;
            int productId = 0x1001;

            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                {
                    // Always query first
                    var panelInfo = await BeadaPanelHelper.GetPanelInfoAsync(deviceReg);

                    if(panelInfo == null)
                    {
                        continue; // Skip this device if we can't get info
                    }

                    var deviceLocation = deviceReg.DeviceProperties["LocationInformation"] as string;

                    if (!string.IsNullOrEmpty(deviceLocation) && deviceLocation == _device.DeviceLocation)
                    {
                        Trace.WriteLine($"BeadaPanelDevice {_device}: Found device");
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
                    Trace.WriteLine($"BeadaPanelDevice {_device}: USB Device not found.");
                    //SharedModel.Instance.UpdateBeadaPanelDeviceStatus(_device.Id, false, false, 0, 0, "Device not found");
                    return;
                }

                using var usbDevice = usbRegistry.Device;

                if(usbDevice == null)
                {
                    //SharedModel.Instance.UpdateBeadaPanelDeviceStatus(_device.Id, false, false, 0, 0, "Unable to open device");
                    return;
                }

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

                Trace.WriteLine($"BeadaPanelDevice {_device}: Sent infoTag");

                byte[] responseBuffer = new byte[100];

                using UsbEndpointReader reader = usbDevice.OpenEndpointReader(ReadEndpointID.Ep02);
                reader.Read(responseBuffer, 2000, out int bytesRead);

                var panelInfo = BeadaPanelParser.ParsePanelInfoResponse(responseBuffer);

                if (panelInfo == null)
                {
                    return;
                }

                Trace.WriteLine($"BeadaPanelDevice {_device}: {panelInfo}");
                _device.UpdateRuntimeProperties(panelInfo: panelInfo);

                bool writeThroughMode = panelInfo.Platform == 1 || panelInfo.Platform == 2;

                _panelWidth = panelInfo.ModelInfo?.Width ?? panelInfo.ResolutionX;
                _panelHeight = panelInfo.ModelInfo?.Height ?? panelInfo.ResolutionY;

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

                var brightness = _device.Brightness;

                brightnessTag.Payload = panelInfo.PanelLinkVersion == 1 ? [(byte)((brightness / 100.0 * 75) + 25)] : [(byte)brightness];

                writer.Write(brightnessTag.ToBuffer(), 2000, out int _);

                using UsbEndpointWriter dataWriter = usbDevice.OpenEndpointWriter(WriteEndpointID.Ep01);
                dataWriter.Write(startTag.ToBuffer(), 2000, out int _);

                Trace.WriteLine($"BeadaPanelDevice {_device}: Sent startTag");

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
                            if (brightness != _device.Brightness)
                            {
                                brightness = _device.Brightness;
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
                                    _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                                }
                            }
                        }
                    }, token);

                    _device.UpdateRuntimeProperties(isRunning: true);
                    await Task.WhenAll(renderTask, sendTask);
                }
                catch (TaskCanceledException)
                {
                    Trace.WriteLine($"BeadaPanelDevice {_device}: Task cancelled");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"BeadaPanelDevice {_device}: Exception during work: {ex.Message}");
                    //SharedModel.Instance.UpdateBeadaPanelDeviceStatus(_device.Id, false, false, 0, 0, ex.Message);
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
                        Trace.WriteLine($"BeadaPanelDevice {_device}: Sent endTag");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"BeadaPanelDevice {_device}: Exception when sending endTag: {ex.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine($"BeadaPanelDevice {_device}: Init error: {e.Message}");
                //SharedModel.Instance.UpdateBeadaPanelDeviceStatus(_device.Id, false, false, 0, 0, e.Message);
            }
            finally
            {
                //SharedModel.Instance.UpdateBeadaPanelDeviceStatus(_device.Id, false, false);
            }
        }
    }
}