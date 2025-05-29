using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.TuringPanel;
using InfoPanel.Utils;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    public sealed class TuringPanelTask : BackgroundTask
    {
        private static readonly Lazy<TuringPanelTask> _instance = new(() => new TuringPanelTask());

        private volatile int _panelWidth = 480;
        private volatile int _panelHeight = 1920;

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
                using var bitmap = PanelDrawTask.RenderSK(profile, false, videoBackgroundFallback: true,
                    colorType: DateTime.Now > _downgradeRenderingUntil ? SKColorType.Rgba8888 : SKColorType.Argb4444);
                
                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                using var data = resizedBitmap.Encode(SKEncodedImageFormat.Png, 100);
                var result = data.ToArray();

                if (resizedBitmap.ColorType != SKColorType.Argb4444 && result.Length > _maxSize)
                {
                    Trace.WriteLine("Downgrading rendering to ARGB4444");
                    DateTime now = DateTime.Now;
                    DateTime targetTime = now.AddSeconds(5);
                    _downgradeRenderingUntil = targetTime;
                    return null;
                }
                else if(result.Length > _maxSize)
                {
                    Trace.WriteLine("DownscaleUpscaleAndEncode");
                    result = DownscaleUpscaleAndEncode(resizedBitmap);
                }

                Trace.WriteLine($"Size: {result.Length / 1024}kb");
                return result;
            }

            return null;
        }

        private static byte[] DownscaleUpscaleAndEncode(SKBitmap original, float scale = 0.5f)
        {
            int downWidth = (int)(original.Width * scale);
            int downHeight = (int)(original.Height * scale);

            using var downscaled = original.Resize(new SKImageInfo(downWidth, downHeight), SKSamplingOptions.Default);
            using var upscaled = downscaled.Resize(new SKImageInfo(original.Width, original.Height), SKSamplingOptions.Default);

            using var data = upscaled.Encode(SKEncodedImageFormat.Png, 100);
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

                    var fpsCounter = new FpsCounter();
                    var stopwatch = new Stopwatch();

                    var queue = new ConcurrentQueue<byte[]>();

                    var renderTask = Task.Run(async () =>
                    {
                        var stopwatch1 = new Stopwatch();
                        while (!token.IsCancellationRequested)
                        {
                            stopwatch1.Restart();
                            var frame = GenerateLcdBuffer();
                            //Trace.WriteLine($"GenerateBuffer {stopwatch1.ElapsedMilliseconds}ms");
                            if (frame != null)
                                queue.Enqueue(frame);

                            // Remove oldest if over capacity
                            while (queue.Count > 2)
                            {
                                queue.TryDequeue(out _);
                            }

                            var targetFrameTime = 1000.0 / ConfigModel.Instance.Settings.TargetFrameRate;
                            if (stopwatch1.ElapsedMilliseconds < targetFrameTime)
                            {
                                var sleep = (int)(targetFrameTime - stopwatch1.ElapsedMilliseconds);
                                //Trace.WriteLine($"Sleep {sleep}ms");
                                await Task.Delay(sleep, token);
                            }
                        }
                    }, token);

                    var sendTask = Task.Run(async () =>
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

                            if (queue.TryDequeue(out var frame))
                            {
                                stopwatch2.Restart();
                                SendPngBytes(device, frame);
                                //Trace.WriteLine($"Post render: {stopwatch2.ElapsedMilliseconds}ms.");
                                device.ReadFlush();
                                fpsCounter.Update();

                                //Trace.WriteLine($"FPS: {fpsCounter.FramesPerSecond}");

                                SharedModel.Instance.TuringPanelFrameRate = fpsCounter.FramesPerSecond;
                                SharedModel.Instance.TuringPanelFrameTime = stopwatch2.ElapsedMilliseconds;

                                var targetFrameTime = 1000.0 / ConfigModel.Instance.Settings.TargetFrameRate;
                                if (stopwatch2.ElapsedMilliseconds < targetFrameTime)
                                {
                                    var sleep = (int)(targetFrameTime - stopwatch2.ElapsedMilliseconds);
                                    //Trace.WriteLine($"Sleep {sleep}ms");
                                    await Task.Delay(sleep, token);
                                }
                            }
                            else
                            {
                                await Task.Delay(10);
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
