using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace InfoPanel.AX206Panel
{
    public sealed class AX206PanelTask : BackgroundTask
    {
        private static readonly Lazy<AX206PanelTask> _instance = new(() => new AX206PanelTask());
        public static AX206PanelTask Instance => _instance.Value;

        // Constants from dpfcore4driver.c
        private const int AX206_VID = 0x1908;
        private const int AX206_PID = 0x0102;

        private volatile int _panelWidth = AX206PanelInfo.DefaultWidth;
        private volatile int _panelHeight = AX206PanelInfo.DefaultHeight;
        private AX206PanelInfo _panelInfo = null;
        
        private AX206PanelTask() { }

        public static byte[] BitmapToRgb16(Bitmap bitmap)
        {
            BitmapData bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), 
                ImageLockMode.ReadOnly, PixelFormat.Format16bppRgb565);
            try
            {
                int stride = bmpData.Stride;
                int size = bmpData.Height * stride;
                byte[] data = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, data, 0, size);
                return data;
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }

        public byte[]? GenerateLcdBuffer()
        {
            var profileGuid = ConfigModel.Instance.Settings.BeadaPanelProfile;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                using var bitmap = PanelDrawTask.Render(profile, false, videoBackgroundFallback: true, 
                    pixelFormat: PixelFormat.Format16bppRgb565, overrideDpi: true);
                var rotation = ConfigModel.Instance.Settings.BeadaPanelRotation;
                if (rotation != ViewModels.LCD_ROTATION.RotateNone)
                {
                    var rotateFlipType = (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), rotation);
                    bitmap.RotateFlip(rotateFlipType);
                }

                using var resizedBitmap = (_panelWidth == 0 || _panelHeight == 0)
                   ? BitmapExtensions.EnsureBitmapSize(bitmap, bitmap.Width, bitmap.Height)
                   : BitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight);

                return BitmapToRgb16(resizedBitmap);
            }

            return null;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);
            
            try
            {
                UsbDeviceFinder finder = new(AX206_VID, AX206_PID);
                using UsbDevice usbDevice = UsbDevice.OpenUsbDevice(finder);

                if (usbDevice == null)
                {
                    Trace.WriteLine("AX206 Panel: USB Device not found.");
                    return;
                }

                Trace.WriteLine("AX206 Panel: Device found.");

                IUsbDevice? wholeUsbDevice = usbDevice as IUsbDevice;
                if (wholeUsbDevice != null)
                {
                    wholeUsbDevice.SetConfiguration(1);
                    wholeUsbDevice.ClaimInterface(0);
                }

                // Initialize panel info
                _panelInfo = new AX206PanelInfo();
                _panelWidth = _panelInfo.Width;
                _panelHeight = _panelInfo.Height;

                Trace.WriteLine(_panelInfo.ToString());

                // Setup endpoints for communication
                using UsbEndpointWriter writer = usbDevice.OpenEndpointWriter(WriteEndpointID.Ep01);
                
                // Set brightness
                var brightness = ConfigModel.Instance.Settings.BeadaPanelBrightness;
                byte ax206Brightness = (byte)Math.Min(7, Math.Max(0, (brightness * 7 / 100)));
                
                // Convert brightness from 0-100 to 0-7
                byte[] brightnessCmd = AX206Commands.BuildSetBacklightCommand(ax206Brightness);
                writer.Write(brightnessCmd, 2000, out int _);
                
                Trace.WriteLine($"AX206 Panel: Set brightness to {ax206Brightness}");

                SharedModel.Instance.BeadaPanelRunning = true;
                
                try
                {
                    var fpsCounter = new FpsCounter();
                    
                    var queue = new ConcurrentQueue<byte[]>();

                    var renderTask = Task.Run(async () =>
                    {
                        var stopwatch = new Stopwatch();
                        while (!token.IsCancellationRequested)
                        {
                            stopwatch.Restart();
                            var frame = GenerateLcdBuffer();
                            
                            if (frame != null)
                                queue.Enqueue(frame);

                            // Remove oldest if over capacity
                            while (queue.Count > 2)
                            {
                                queue.TryDequeue(out _);
                            }

                            var targetFrameTime = 1000.0 / ConfigModel.Instance.Settings.TargetFrameRate;
                            if (stopwatch.ElapsedMilliseconds < targetFrameTime)
                            {
                                var sleep = (int)(targetFrameTime - stopwatch.ElapsedMilliseconds);
                                await Task.Delay(sleep, token);
                            }
                        }
                    }, token);

                    var sendTask = Task.Run(async () =>
                    {
                        var stopwatch = new Stopwatch();
                        byte lastBrightnessValue = ax206Brightness;
                        
                        while (!token.IsCancellationRequested)
                        {
                            // Check if brightness has changed
                            byte newBrightness = (byte)Math.Min(7, Math.Max(0, 
                                (ConfigModel.Instance.Settings.BeadaPanelBrightness * 7 / 100)));
                            
                            if (newBrightness != lastBrightnessValue)
                            {
                                lastBrightnessValue = newBrightness;
                                byte[] brightnessCmd = AX206Commands.BuildSetBacklightCommand(newBrightness);
                                writer.Write(brightnessCmd, 2000, out int _);
                            }

                            if (queue.TryDequeue(out var frame))
                            {
                                stopwatch.Restart();
                                
                                // Create blit command for full screen update
                                byte[] blitCmd = AX206Commands.BuildBlitCommand(
                                    0, 0, _panelWidth - 1, _panelHeight - 1);
                                
                                // Send the command and frame data
                                writer.Write(blitCmd, 2000, out int _);
                                writer.Write(frame, 2000, out int _);
                                
                                fpsCounter.Update();
                                
                                SharedModel.Instance.BeadaPanelFrameRate = fpsCounter.FramesPerSecond;
                                SharedModel.Instance.BeadaPanelFrameTime = stopwatch.ElapsedMilliseconds;

                                var targetFrameTime = 1000.0 / ConfigModel.Instance.Settings.TargetFrameRate;
                                if (stopwatch.ElapsedMilliseconds < targetFrameTime)
                                {
                                    var sleep = (int)(targetFrameTime - stopwatch.ElapsedMilliseconds);
                                    await Task.Delay(sleep, token);
                                }
                            }
                            else
                            {
                                await Task.Delay(1);
                            }
                        }
                    }, token);

                    await Task.WhenAll(renderTask, sendTask);
                }
                catch (TaskCanceledException)
                {
                    Trace.WriteLine("AX206 Panel: Task cancelled");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"AX206 Panel: Exception during work: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (_shutdown)
                        {
                            // Turn off backlight
                            byte[] brightnessCmd = AX206Commands.BuildSetBacklightCommand(0);
                            writer.Write(brightnessCmd, 2000, out int _);
                        }
                        else
                        {
                            // Draw splash screen before exiting
                            using var bitmap = PanelDrawTask.RenderSplash(_panelWidth, _panelHeight, 
                                pixelFormat: PixelFormat.Format16bppRgb565,
                                rotateFlipType: (RotateFlipType)Enum.ToObject(
                                    typeof(RotateFlipType), ConfigModel.Instance.Settings.BeadaPanelRotation));
                            
                            byte[] blitCmd = AX206Commands.BuildBlitCommand(0, 0, _panelWidth - 1, _panelHeight - 1);
                            writer.Write(blitCmd, 2000, out int _);
                            writer.Write(BitmapToRgb16(bitmap), 2000, out int _);
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"AX206 Panel: Exception during cleanup: {ex.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine($"AX206 Panel: Init error - {e.Message}");
            }
            finally
            {
                SharedModel.Instance.BeadaPanelRunning = false;
            }
        }
    }
}