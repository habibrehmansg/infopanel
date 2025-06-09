using InfoPanel.BeadaPanel;
using InfoPanel.BeadaPanel.PanelLink;
using InfoPanel.BeadaPanel.StatusLink;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Services;
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

namespace InfoPanel
{
    public sealed class BeadaPanelTask : BackgroundTask
    {
        private static readonly Lazy<BeadaPanelTask> _instance = new(() => new BeadaPanelTask());

        private volatile int _panelWidth = 0;
        private volatile int _panelHeight = 0;
        
        private readonly ConcurrentDictionary<string, BeadaPanelDeviceTask> _deviceTasks = new();

        public static BeadaPanelTask Instance => _instance.Value;

        private BeadaPanelTask() { }

        [Obsolete("Use multi-device support instead. This method is maintained for backward compatibility.")]
        public byte[]? GenerateLcdBuffer()
        {
            var profileGuid = ConfigModel.Instance.Settings.BeadaPanelProfile;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = ConfigModel.Instance.Settings.BeadaPanelRotation;
                using var bitmap = PanelDrawTask.RenderSK(profile, false, colorType: SKColorType.Rgb565, alphaType: SKAlphaType.Opaque);

                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                return resizedBitmap.Bytes;
            }

            return null;
        }

        public async Task<List<BeadaPanelDevice>> DiscoverDevicesAsync()
        {
            var discoveredDevices = new List<BeadaPanelDevice>();
            
            try
            {
                int vendorId = 0x4e58;
                int productId = 0x1001;

                var allDevices = UsbDevice.AllDevices;
                Trace.WriteLine($"BeadaPanel Discovery: Scanning {allDevices.Count} USB devices for VID={vendorId:X4} PID={productId:X4}");

                int deviceIndex = 1;
                foreach (UsbRegistry deviceReg in allDevices)
                {
                    if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                    {
                        Trace.WriteLine($"BeadaPanel Discovery: Found matching device - FullName: '{deviceReg.FullName}', DevicePath: '{deviceReg.DevicePath}', SymbolicName: '{deviceReg.SymbolicName}'");
                        
                        try
                        {
                            // Create initial device with USB path as fallback identifier
                            var usbPath = deviceReg.DevicePath ?? $"USB_{deviceReg.DeviceInterfaceGuids}_{deviceIndex}";
                            var tempSerialNumber = deviceReg.DevicePath ?? deviceReg.DeviceInterfaceGuids.ToString();
                            
                            var device = new BeadaPanelDevice(
                                serialNumber: tempSerialNumber,
                                usbPath: usbPath,
                                name: $"BeadaPanel Device {deviceIndex}"
                            );

                            // Try to query StatusLink for detailed device information
                            var panelInfo = await QueryDeviceStatusLinkAsync(deviceReg);
                            if (panelInfo != null)
                            {
                                device.UpdateFromStatusLink(panelInfo);
                                Trace.WriteLine($"StatusLink Query Success: {device.Name} - Hardware Serial: {device.HardwareSerialNumber}, Model: {device.ModelName}");
                            }
                            else
                            {
                                // Fallback naming when StatusLink is not available
                                var deviceName = !string.IsNullOrEmpty(deviceReg.FullName) && deviceReg.FullName.Trim() != ""
                                    ? $"BeadaPanel {deviceReg.FullName} #{deviceIndex}"
                                    : $"BeadaPanel Device {deviceIndex}";
                                device.Name = deviceName;
                                Trace.WriteLine($"StatusLink Query Failed: Using USB-based identification for {device.Name}");
                            }

                            discoveredDevices.Add(device);
                            Trace.WriteLine($"Discovered BeadaPanel device: {device.Name} (ID Method: {device.IdentificationMethod}) at {device.UsbPath}");
                            
                            deviceIndex++;
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"Error discovering BeadaPanel device: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error during BeadaPanel device discovery: {ex.Message}");
            }

            Trace.WriteLine($"BeadaPanel Discovery: Complete - Found {discoveredDevices.Count} devices total");
            return discoveredDevices;
        }

        private async Task<BeadaPanelInfo?> QueryDeviceStatusLinkAsync(UsbRegistry deviceReg)
        {
            UsbDevice? usbDevice = null;
            try
            {
                // Try to open the specific device
                usbDevice = deviceReg.Device;
                if (usbDevice == null)
                {
                    Trace.WriteLine($"StatusLink Query: Could not open USB device {deviceReg.DevicePath}");
                    return null;
                }

                if (usbDevice is IUsbDevice wholeUsbDevice)
                {
                    wholeUsbDevice.SetConfiguration(1);
                    wholeUsbDevice.ClaimInterface(0);
                }

                // Send StatusLink GetPanelInfo request
                var infoMessage = new StatusLinkMessage
                {
                    Type = StatusLinkMessageType.GetPanelInfo
                };

                using var writer = usbDevice.OpenEndpointWriter(WriteEndpointID.Ep02);
                var writeResult = writer.Write(infoMessage.ToBuffer(), 1000, out int _);
                
                if (writeResult != ErrorCode.None)
                {
                    Trace.WriteLine($"StatusLink Query: Write failed with error {writeResult}");
                    return null;
                }

                // Read response
                byte[] responseBuffer = new byte[100];
                using var reader = usbDevice.OpenEndpointReader(ReadEndpointID.Ep02);
                var readResult = reader.Read(responseBuffer, 1000, out int bytesRead);

                if (readResult != ErrorCode.None || bytesRead == 0)
                {
                    Trace.WriteLine($"StatusLink Query: Read failed with error {readResult}, bytes read: {bytesRead}");
                    return null;
                }

                // Parse panel info
                var panelInfo = BeadaPanelParser.ParsePanelInfoResponse(responseBuffer);
                if (panelInfo != null)
                {
                    Trace.WriteLine($"StatusLink Query: Successfully parsed panel info for serial {panelInfo.SerialNumber}");
                }

                return panelInfo;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"StatusLink Query Exception: {ex.Message}");
                return null;
            }
            finally
            {
                try
                {
                    if (usbDevice is IUsbDevice wholeUsbDevice)
                    {
                        wholeUsbDevice.ReleaseInterface(0);
                    }
                    usbDevice?.Close();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"StatusLink Query Cleanup Exception: {ex.Message}");
                }
            }
        }

        public void StartDevice(BeadaPanelDevice device)
        {
            if (_deviceTasks.ContainsKey(device.Id))
            {
                Trace.WriteLine($"BeadaPanel device {device.Name} is already running");
                return;
            }

            var deviceTask = new BeadaPanelDeviceTask(device);
            if (_deviceTasks.TryAdd(device.Id, deviceTask))
            {
                _ = deviceTask.StartAsync();
                Trace.WriteLine($"Started BeadaPanel device {device.Name}");
            }
        }

        public async Task StopDevice(string deviceId)
        {
            if (_deviceTasks.TryRemove(deviceId, out var deviceTask))
            {
                await deviceTask.StopAsync();
                //deviceTask.Dispose();
                SharedModel.Instance.RemoveBeadaPanelDeviceStatus(deviceId);
                Trace.WriteLine($"Stopped BeadaPanel device {deviceId}");
            }
        }

        public async Task StopAllDevices()
        {
            var tasks = new List<Task>();
            
            foreach (var kvp in _deviceTasks.ToList())
            {
                if (_deviceTasks.TryRemove(kvp.Key, out var deviceTask))
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await deviceTask.StopAsync();
                        //deviceTask.Dispose();
                        SharedModel.Instance.RemoveBeadaPanelDeviceStatus(kvp.Key);
                    }));
                }
            }

            await Task.WhenAll(tasks);
            Trace.WriteLine("Stopped all BeadaPanel devices");
        }

        public bool IsDeviceRunning(string deviceId)
        {
            return _deviceTasks.ContainsKey(deviceId);
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            try
            {
                var settings = ConfigModel.Instance.Settings;
                
                Trace.WriteLine($"BeadaPanel: DoWorkAsync starting - MultiDeviceMode: {settings.BeadaPanelMultiDeviceMode}, LegacyMode: {settings.BeadaPanel}");

                if (settings.BeadaPanelMultiDeviceMode)
                {
                    await RunMultiDeviceMode(token);
                }
                else
                {
                    await RunLegacySingleDeviceMode(token);
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine($"BeadaPanel: Error in DoWorkAsync: {e.Message}");
            }
        }

        private async Task RunMultiDeviceMode(CancellationToken token)
        {
            Trace.WriteLine("BeadaPanel: Starting multi-device mode");
            
            while (!token.IsCancellationRequested)
            {
                var settings = ConfigModel.Instance.Settings;
                var enabledDevices = settings.BeadaPanelDevices.Where(d => d.Enabled).ToList();
                
                if (enabledDevices.Count > 0)
                {
                    Trace.WriteLine($"BeadaPanel: Found {enabledDevices.Count} enabled devices");
                }

                foreach (var device in enabledDevices)
                {
                    if (!IsDeviceRunning(device.Id))
                    {
                        StartDevice(device);
                    }
                }

                var runningDeviceIds = _deviceTasks.Keys.ToList();
                foreach (var deviceId in runningDeviceIds)
                {
                    if (!enabledDevices.Any(d => d.Id == deviceId))
                    {
                        await StopDevice(deviceId);
                    }
                }

                await Task.Delay(250, token);
            }
        }

        private async Task RunLegacySingleDeviceMode(CancellationToken token)
        {
            try
            {
                int vendorId = 0x4e58;
                int productId = 0x1001;

                var devices = UsbDevice.AllDevices;

                UsbDeviceFinder finder = new(vendorId, productId);
                using UsbDevice usbDevice = UsbDevice.OpenUsbDevice(finder);

                if (usbDevice == null)
                {
                    Trace.WriteLine("USB Device not found.");
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

                Trace.WriteLine("Sent infoTag");

                byte[] responseBuffer = new byte[100];

                using UsbEndpointReader reader = usbDevice.OpenEndpointReader(ReadEndpointID.Ep02);
                reader.Read(responseBuffer, 2000, out int bytesRead);

                var panelInfo = BeadaPanelParser.ParsePanelInfoResponse(responseBuffer);

                if (panelInfo == null)
                {
                    Trace.WriteLine("Failed to parse panel info.");
                    return;
                }

                Trace.WriteLine(panelInfo.ToString());

                bool writeThroughMode = panelInfo.Platform == 1 || panelInfo.Platform == 2;

                _panelWidth = panelInfo.ModelInfo?.Width ?? panelInfo.ResolutionX;
                _panelHeight = panelInfo.ModelInfo?.Height ?? panelInfo.ResolutionY;

                SharedModel.Instance.BeadaPanelRunning = true;

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

                var brightness = ConfigModel.Instance.Settings.BeadaPanelBrightness;

                brightnessTag.Payload = panelInfo.PanelLinkVersion == 1 ? [(byte)((brightness / 100.0 * 75) + 25)] : [(byte)brightness];

                writer.Write(brightnessTag.ToBuffer(), 2000, out int _);

                using UsbEndpointWriter dataWriter = usbDevice.OpenEndpointWriter(WriteEndpointID.Ep01);
                dataWriter.Write(startTag.ToBuffer(), 2000, out int _);

                Trace.WriteLine("Sent startTag");

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
                            if (brightness != ConfigModel.Instance.Settings.BeadaPanelBrightness)
                            {
                                brightness = ConfigModel.Instance.Settings.BeadaPanelBrightness;
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
                                    SharedModel.Instance.BeadaPanelFrameRate = fpsCounter.FramesPerSecond;
                                    SharedModel.Instance.BeadaPanelFrameTime = fpsCounter.FrameTime;
                                }
                            }
                        }
                    }, token);

                    await Task.WhenAll(renderTask, sendTask);
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
                    try
                    {
                        brightnessTag.Payload = [0];
                        writer.Write(brightnessTag.ToBuffer(), 2000, out int _);

                        using var blankFrame = new SKBitmap(new SKImageInfo(_panelWidth, _panelHeight, SKColorType.Rgb565, SKAlphaType.Opaque));
                        dataWriter.Write(blankFrame.Bytes, 2000, out int _);

                        dataWriter.Write(endTag.ToBuffer(), 2000, out int _);
                        Trace.WriteLine("Sent endTag");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Exception when sending ResetTag: {ex.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine("BeadaPanel: Init error");
            }
            finally
            {
                SharedModel.Instance.BeadaPanelRunning = false;
                await StopAllDevices();
            }
        }

        //protected override async ValueTask DisposeAsync()
        //{
        //    await StopAllDevices();
        //    await base.DisposeAsync();
        //}
    }
}