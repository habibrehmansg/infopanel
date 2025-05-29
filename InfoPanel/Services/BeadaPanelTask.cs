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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    public sealed class BeadaPanelTask : BackgroundTask
    {
        private static readonly Lazy<BeadaPanelTask> _instance = new(() => new BeadaPanelTask());

        private volatile int _panelWidth = 0;
        private volatile int _panelHeight = 0;

        public static BeadaPanelTask Instance => _instance.Value;

        private BeadaPanelTask() { }

        public byte[]? GenerateLcdBuffer()
        {
            var profileGuid = ConfigModel.Instance.Settings.BeadaPanelProfile;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = ConfigModel.Instance.Settings.BeadaPanelRotation;
                using var bitmap = PanelDrawTask.RenderSK(profile, false, videoBackgroundFallback: true, colorType: SKColorType.Rgb565, alphaType: SKAlphaType.Opaque);
                
                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                return resizedBitmap.Bytes;
            }

            return null;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);
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

                byte[] responseBuffer = new byte[100]; // 20-byte header + 80-byte payload

                using UsbEndpointReader reader = usbDevice.OpenEndpointReader(ReadEndpointID.Ep02);
                reader.Read(responseBuffer, 2000, out int bytesRead);

                var panelInfo = BeadaPanelParser.ParsePanelInfoResponse(responseBuffer);

                if (panelInfo == null)
                {
                    Trace.WriteLine("Failed to parse panel info.");
                    return;
                }

                Trace.WriteLine(panelInfo.ToString());

                //only supported on i.mx6ul && i.mx6ull
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

                //always reset first
                //iface.Pipes[0x2].Write(resetTag.ToBuffer());

                writer.Write(resetTag.ToBuffer(), 2000, out int _);

                //wait for reset
                await Task.Delay(1000, token);

                //set brightness
                var brightness = ConfigModel.Instance.Settings.BeadaPanelBrightness;

                brightnessTag.Payload = panelInfo.PanelLinkVersion == 1 ? [(byte)((brightness / 100.0 * 75) + 25)] : [(byte)brightness];

                writer.Write(brightnessTag.ToBuffer(), 2000, out int _);

                //start stream
                using UsbEndpointWriter dataWriter = usbDevice.OpenEndpointWriter(WriteEndpointID.Ep01);
                dataWriter.Write(startTag.ToBuffer(), 2000, out int _);

                Trace.WriteLine("Sent startTag");

                try
                {
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
                            if (brightness != ConfigModel.Instance.Settings.BeadaPanelBrightness)
                            {
                                brightness = ConfigModel.Instance.Settings.BeadaPanelBrightness;
                                brightnessTag.Payload = panelInfo.PanelLinkVersion == 1 ? [(byte)((brightness / 100.0 * 75) + 25)] : [(byte)brightness];
                                //iface.Pipes[0x2].Write(brightnessTag.ToBuffer());
                                writer.Write(brightnessTag.ToBuffer(), 2000, out int _);
                            }

                            if (queue.TryDequeue(out var frame))
                            {
                                stopwatch2.Restart();
                                dataWriter.Write(frame, 2000, out int _);
                                //Trace.WriteLine($"Post render: {stopwatch2.ElapsedMilliseconds}ms.");
                                fpsCounter.Update();
                                //Trace.WriteLine($"FPS: {fpsCounter.FramesPerSecond}");

                                SharedModel.Instance.BeadaPanelFrameRate = fpsCounter.FramesPerSecond;
                                SharedModel.Instance.BeadaPanelFrameTime = stopwatch2.ElapsedMilliseconds;

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
                                await Task.Delay(1);
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
            }
        }
    }


}
