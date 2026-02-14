using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.ThermalrightPanel;
using InfoPanel.Utils;
using Serilog;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class ThermalrightPanelDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<ThermalrightPanelDeviceTask>();

        // ChiZhu Tech USBDISPLAY Protocol constants
        // Based on USB capture analysis of TRCC software at boot
        private static readonly byte[] MAGIC_BYTES = { 0x12, 0x34, 0x56, 0x78 };
        private const int HEADER_SIZE = 64;
        private const int COMMAND_DISPLAY = 0x02;
        private const int JPEG_QUALITY = 85;

        // TRCC uses 1600x720 (2/3 of native 2400x1080)
        private const int TRCC_WIDTH = 1600;
        private const int TRCC_HEIGHT = 720;

        private readonly ThermalrightPanelDevice _device;
        private int _panelWidth;
        private int _panelHeight;

        public ThermalrightPanelDevice Device => _device;

        public ThermalrightPanelDeviceTask(ThermalrightPanelDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            // Use TRCC resolution (1600x720) - this is what the official software uses
            _panelWidth = TRCC_WIDTH;
            _panelHeight = TRCC_HEIGHT;
        }

        /// <summary>
        /// Builds the 64-byte init command for the ChiZhu Tech USB Display protocol.
        /// Based on actual USB capture: magic + zeros + 0x01 at offset 56
        /// </summary>
        private byte[] BuildInitCommand()
        {
            var header = new byte[HEADER_SIZE];

            // Bytes 0-3: Magic 0x12345678
            Array.Copy(MAGIC_BYTES, 0, header, 0, 4);

            // Bytes 4-55: All zeros (already zeroed)

            // Bytes 56-59: Init flag = 0x01 (critical!)
            BitConverter.GetBytes(1).CopyTo(header, 56);

            // Bytes 60-63: Zero (already zeroed)

            return header;
        }

        /// <summary>
        /// Builds a 64-byte display header for the ChiZhu Tech USB Display protocol.
        /// </summary>
        private byte[] BuildDisplayHeader(int jpegSize)
        {
            var header = new byte[HEADER_SIZE];

            // Bytes 0-3: Magic 0x12345678
            Array.Copy(MAGIC_BYTES, 0, header, 0, 4);

            // Bytes 4-7: Command = 2 (display frame)
            BitConverter.GetBytes(COMMAND_DISPLAY).CopyTo(header, 4);

            // Bytes 8-11: Width
            BitConverter.GetBytes(_panelWidth).CopyTo(header, 8);

            // Bytes 12-15: Height
            BitConverter.GetBytes(_panelHeight).CopyTo(header, 12);

            // Bytes 16-55: Zero padding (already zeroed)

            // Bytes 56-59: Command repeated = 2
            BitConverter.GetBytes(COMMAND_DISPLAY).CopyTo(header, 56);

            // Bytes 60-63: JPEG size (little-endian)
            BitConverter.GetBytes(jpegSize).CopyTo(header, 60);

            return header;
        }

        public byte[]? GenerateJpegBuffer()
        {
            var profileGuid = _device.ProfileGuid;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = _device.Rotation;

                // Render to RGBA bitmap
                using var bitmap = PanelDrawTask.RenderSK(profile, false,
                    colorType: SKColorType.Rgba8888,
                    alphaType: SKAlphaType.Opaque);

                // Resize to panel resolution with rotation
                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                // Encode as JPEG
                using var image = SKImage.FromBitmap(resizedBitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, JPEG_QUALITY);

                return data.ToArray();
            }

            return null;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            try
            {
                // Use direct WinUSB API to open device (bypasses LibUsbDotNet issues)
                Logger.Information("ThermalrightPanelDevice {Device}: Opening device via WinUSB API...", _device);

                using var usbDevice = WinUsbDevice.Open(
                    ThermalrightPanelModelDatabase.THERMALRIGHT_VENDOR_ID,
                    ThermalrightPanelModelDatabase.THERMALRIGHT_PRODUCT_ID);

                if (usbDevice == null)
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: Failed to open device via WinUSB", _device);
                    _device.UpdateRuntimeProperties(errorMessage:
                        "Failed to open USB device. Make sure:\n" +
                        "1. WinUSB driver is installed (use Zadig)\n" +
                        "2. No other application is using the device\n" +
                        "3. Try running as Administrator");
                    return;
                }

                Logger.Information("ThermalrightPanelDevice {Device}: Device opened successfully!", _device);
                Logger.Information("ThermalrightPanelDevice {Device}: Connected to {Width}x{Height} panel",
                    _device, _panelWidth, _panelHeight);

                _device.RuntimeProperties.Name = $"Thermalright {_device.ModelInfo?.Name ?? "Panel"} ({_panelWidth}x{_panelHeight})";

                // Send initialization command (magic + zeros + 0x01 at offset 56)
                var initCommand = BuildInitCommand();
                Logger.Information("ThermalrightPanelDevice {Device}: Sending init command (64 bytes)", _device);
                Logger.Debug("ThermalrightPanelDevice {Device}: Init bytes: {Hex}", _device,
                    BitConverter.ToString(initCommand).Replace("-", ""));

                if (!usbDevice.Write(initCommand, out int initWritten))
                {
                    Logger.Error("ThermalrightPanelDevice {Device}: Init command failed", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "Init command failed");
                    return;
                }
                Logger.Information("ThermalrightPanelDevice {Device}: Init command sent ({Bytes} bytes)", _device, initWritten);

                // Read device response (should contain "SSCRM-V3" identifier)
                var responseBuffer = new byte[64];
                if (usbDevice.Read(responseBuffer, out int bytesRead) && bytesRead > 0)
                {
                    var responseHex = BitConverter.ToString(responseBuffer, 0, Math.Min(bytesRead, 32)).Replace("-", "");
                    Logger.Information("ThermalrightPanelDevice {Device}: Device response ({Bytes} bytes): {Hex}",
                        _device, bytesRead, responseHex);

                    // Check for device identifier (SSCRM-V3 at offset 4)
                    if (bytesRead >= 12)
                    {
                        var identifier = System.Text.Encoding.ASCII.GetString(responseBuffer, 4, 8).TrimEnd('\0');
                        Logger.Information("ThermalrightPanelDevice {Device}: Device identifier: {Id}", _device, identifier);
                    }
                }
                else
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: No response from device", _device);
                }

                await Task.Delay(100, token); // Small delay after init

                // Main rendering loop
                FpsCounter fpsCounter = new(60);
                byte[]? _latestFrame = null;
                AutoResetEvent _frameAvailable = new(false);

                var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var renderToken = renderCts.Token;

                _device.UpdateRuntimeProperties(isRunning: true);

                var renderTask = Task.Run(async () =>
                {
                    Thread.CurrentThread.Name ??= $"Thermalright-Render-{_device.DeviceLocation}";
                    var stopwatch = new Stopwatch();

                    while (!renderToken.IsCancellationRequested)
                    {
                        stopwatch.Restart();
                        var frame = GenerateJpegBuffer();

                        if (frame != null)
                        {
                            Interlocked.Exchange(ref _latestFrame, frame);
                            _frameAvailable.Set();
                        }

                        var targetFrameTime = 1000 / ConfigModel.Instance.Settings.TargetFrameRate;
                        var desiredFrameTime = Math.Max((int)(fpsCounter.FrameTime * 0.9), targetFrameTime);
                        var adaptiveFrameTime = 0;

                        var elapsedMs = (int)stopwatch.ElapsedMilliseconds;

                        if (elapsedMs < desiredFrameTime)
                        {
                            adaptiveFrameTime = desiredFrameTime - elapsedMs;
                        }

                        if (adaptiveFrameTime > 0)
                        {
                            await Task.Delay(adaptiveFrameTime, token);
                        }
                    }
                }, renderToken);

                var sendTask = Task.Run(() =>
                {
                    Thread.CurrentThread.Name ??= $"Thermalright-Send-{_device.DeviceLocation}";
                    try
                    {
                        var stopwatch = new Stopwatch();

                        while (!token.IsCancellationRequested)
                        {
                            if (_frameAvailable.WaitOne(100))
                            {
                                var jpegData = Interlocked.Exchange(ref _latestFrame, null);
                                if (jpegData != null)
                                {
                                    stopwatch.Restart();

                                    // Build display header with JPEG size
                                    var header = BuildDisplayHeader(jpegData.Length);

                                    // Combine header + JPEG into single buffer
                                    var packet = new byte[HEADER_SIZE + jpegData.Length];
                                    Array.Copy(header, 0, packet, 0, HEADER_SIZE);
                                    Array.Copy(jpegData, 0, packet, HEADER_SIZE, jpegData.Length);

                                    // Send as single bulk transfer
                                    if (!usbDevice.Write(packet, out int bytesWritten))
                                    {
                                        throw new Exception("USB write failed");
                                    }

                                    fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                                    _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, "ThermalrightPanelDevice {Device}: Error in send task", _device);
                        _device.UpdateRuntimeProperties(errorMessage: e.Message);
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
                Logger.Debug("ThermalrightPanelDevice {Device}: Task cancelled", _device);
            }
            catch (Exception e)
            {
                Logger.Error(e, "ThermalrightPanelDevice {Device}: Error", _device);
                _device.UpdateRuntimeProperties(errorMessage: e.Message);
            }
            finally
            {
                _device.UpdateRuntimeProperties(isRunning: false);
            }
        }
    }
}
