using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.TuringPanel;
using InfoPanel.Utils;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
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

        public byte[]? GenerateLcdBuffer()
        {
            var profileGuid = ConfigModel.Instance.Settings.TuringPanelProfile;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                using var bitmap = PanelDrawTask.Render(profile, false, videoBackgroundFallback: true, pixelFormat: PixelFormat.Format16bppRgb565, overrideDpi: true);
                var rotation = ConfigModel.Instance.Settings.TuringPanelRotation;
                if (rotation != ViewModels.LCD_ROTATION.RotateNone)
                {
                    var rotateFlipType = (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), rotation);
                    bitmap.RotateFlip(rotateFlipType);
                }

                using var resizedBitmap = (_panelWidth == 0 || _panelHeight == 0)
                   ? BitmapExtensions.EnsureBitmapSize(bitmap, bitmap.Width, bitmap.Height)
                   : BitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight);

                return BlackOutEdgesUntilUnderSize(resizedBitmap, _maxSize);

            }

            return null;
        }

        public static byte[] BitmapToPngBytes(Bitmap bitmap)
        {
            //var sw = new Stopwatch();
            //sw.Start();
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            //Trace.WriteLine($"BitmapToPngBytes: {sw.ElapsedMilliseconds}ms");
            return ms.ToArray();
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
                    try
                    {
                        if (_shutdown)
                        {
                            device.SendBrightnessCommand(0);
                        }
                        else
                        {
                            //draw splash
                            using var bitmap = PanelDrawTask.RenderSplash(_panelWidth, _panelHeight, pixelFormat: PixelFormat.Format16bppRgb565,
                            rotateFlipType: (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), ConfigModel.Instance.Settings.TuringPanelRotation));
                            SendPngBytes(device, BitmapToPngBytes(bitmap));
                            device.ReadFlush();
                        }
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
                SharedModel.Instance.TuringPanelRunning = false;
            }
        }

        public static byte[] BlackOutEdgesUntilUnderSize(Bitmap original, int maxSizeBytes)
        {
            byte[] pngBytes = BitmapToPngBytes(original);

            if (pngBytes.Length <= maxSizeBytes)
            {
                return pngBytes;
            }

            int width = original.Width;
            int height = original.Height;

            int border = 10;
            int passCount = 0;
            int totalBorder = 0;

            using Graphics g = Graphics.FromImage(original);

            int previousSize = pngBytes.Length;

            while (true)
            {
                passCount++;
                totalBorder += border;

                if (totalBorder * 2 >= width || totalBorder * 2 >= height)
                    throw new Exception("Cannot reduce image size under limit by blacking out edges.");

                ApplyBlackBorder(g, width, height, totalBorder);
                pngBytes = BitmapToPngBytes(original);
                int newSize = pngBytes.Length;
                int sizeDiff = previousSize - newSize;

                Trace.WriteLine($"Pass {passCount}: PNG size = {newSize} bytes, reduced by {sizeDiff} bytes");

                if (newSize <= maxSizeBytes)
                {
                    Trace.WriteLine($"Image reduced under size limit in {passCount} passes.");
                    return pngBytes;
                }

                if (sizeDiff <= 0)
                    throw new Exception("Blackout no longer reducing size. Cannot proceed.");

                // Re-estimate how much more needs to be reduced
                int bytesToReduce = newSize - maxSizeBytes;

                // Estimate how many more passes needed at current effectiveness
                int estimatedPasses = (int)Math.Ceiling((double)bytesToReduce / sizeDiff);

                // Estimate next border increment
                int baseBorder = Math.Max(10, (int)Math.Ceiling((double)(width + height) / 200));

                // Cap estimated passes to avoid over-aggressive blackout
                estimatedPasses = Math.Min(estimatedPasses, 3);

                // Grow border more gradually
                border = baseBorder + (estimatedPasses - 1) * (baseBorder / 2);

                // Clamp to avoid over-blackout
                int maxBorder = Math.Min(width, height) / 2 - totalBorder - 1;
                border = Math.Min(border, maxBorder);

                previousSize = newSize;
            }

        }

        private static void ApplyBlackBorder(Graphics g, int width, int height, int border)
        {
            // Fill top
            g.FillRectangle(Brushes.Black, 0, 0, width, border);
            // Fill bottom
            g.FillRectangle(Brushes.Black, 0, height - border, width, border);
            // Fill left
            g.FillRectangle(Brushes.Black, 0, border, border, height - 2 * border);
            // Fill right
            g.FillRectangle(Brushes.Black, width - border, border, border, height - 2 * border);
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
