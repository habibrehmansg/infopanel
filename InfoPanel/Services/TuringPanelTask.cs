using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.TuringPanel;
using InfoPanel.Utils;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    public sealed class TuringPanelTask : BackgroundTask
    {
        private static readonly Lazy<TuringPanelTask> _instance = new(() => new TuringPanelTask());

        private readonly int _panelWidth = 480;
        private readonly int _panelHeight = 1920;

        public static TuringPanelTask Instance => _instance.Value;

        private TuringPanelTask() { }

        private static int _maxSize = 1024 * 1024; // 1MB
        private DateTime _downgradeRenderingUntil = DateTime.MinValue;

        public byte[]? GenerateLcdBuffer()
        {
            var profileGuid = ConfigModel.Instance.Settings.TuringPanelProfile;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = ConfigModel.Instance.Settings.TuringPanelRotation;
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
                    Trace.WriteLine("Failed to encode bitmap to PNG");
                    return null;
                }

                //using var data = resizedBitmap.Encode(SKEncodedImageFormat.Png, 100);
                var result = data.ToArray();

                if (resizedBitmap.ColorType != SKColorType.Argb4444 && result.Length > _maxSize)
                {
                    Trace.WriteLine("Downgrading rendering to ARGB4444");
                    DateTime now = DateTime.Now;
                    DateTime targetTime = now.AddSeconds(10);
                    _downgradeRenderingUntil = targetTime;
                    return null;
                }
                else if (result.Length > _maxSize)
                {
                    result = DownscaleUpscaleAndEncode(resizedBitmap);
                }

                //Trace.WriteLine($"Size: {result.Length / 1024}kb");
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
                Trace.WriteLine("Failed to encode bitmap to PNG");
                return null;
            }
            return data.ToArray();
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);
            try
            {
                using var device = new TuringDevice();

                if (!device.Initialize())
                {
                    Trace.WriteLine("Failed to initialize the device.");
                    return;
                }

                SharedModel.Instance.TuringPanelRunning = true;

                try
                {
                    device.DelaySync();
                    device.DelaySync();

                    //set brightness
                    var brightness = ConfigModel.Instance.Settings.TuringPanelBrightness;
                    device.SendBrightnessCommand((byte)brightness);

                    device.DelaySync();

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

                            //Trace.WriteLine($"elapsed={elapsedMs}ms");

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
                            if (brightness != ConfigModel.Instance.Settings.TuringPanelBrightness)
                            {
                                brightness = ConfigModel.Instance.Settings.TuringPanelBrightness;
                                device.SendBrightnessCommand((byte)brightness);
                                device.DelaySync();
                            }

                            if (_frameAvailable.WaitOne(100))
                            {
                                var frame = Interlocked.Exchange(ref _latestFrame, null);
                                if (frame != null)
                                {
                                    stopwatch2.Restart();
                                    SendPngBytes(device, frame);
                                    device.ReadFlush();

                                    fpsCounter.Update(stopwatch2.ElapsedMilliseconds);
                                    SharedModel.Instance.TuringPanelFrameRate = fpsCounter.FramesPerSecond;
                                    SharedModel.Instance.TuringPanelFrameTime = fpsCounter.FrameTime;
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
                    device.SendBrightnessCommand(0);
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine("BeadaPanel: Init error");
            }
            finally
            {
                SharedModel.Instance.TuringPanelRunning = false;
            }
        }

        private static bool SendPngBytes(TuringDevice device, byte[] pngData)
        {
            int imgSize = pngData.Length;
            byte[] cmdPacket = device.BuildCommandPacketHeader(102);

            // Set image size in the packet (big-endian)
            cmdPacket[8] = (byte)((imgSize >> 24) & 0xFF);
            cmdPacket[9] = (byte)((imgSize >> 16) & 0xFF);
            cmdPacket[10] = (byte)((imgSize >> 8) & 0xFF);
            cmdPacket[11] = (byte)(imgSize & 0xFF);

            // Encrypt the command packet
            byte[] encryptedPacket = device.EncryptCommandPacket(cmdPacket);

            // Combine the encrypted packet with the image data
            byte[] fullPayload = new byte[encryptedPacket.Length + pngData.Length];
            Buffer.BlockCopy(encryptedPacket, 0, fullPayload, 0, encryptedPacket.Length);
            Buffer.BlockCopy(pngData, 0, fullPayload, encryptedPacket.Length, pngData.Length);

            // Write the payload to the device
            return device.WriteToDevice(fullPayload);
        }
    }


}
