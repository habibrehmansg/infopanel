using InfoPanel.BeadaPanel;
using InfoPanel.BeadaPanel.PanelLink;
using InfoPanel.BeadaPanel.StatusLink;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using MadWizard.WinUSBNet;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;

namespace InfoPanel
{
    public sealed class BeadaPanelTask : BackgroundTask
    {
        private static readonly Lazy<BeadaPanelTask> _instance = new(() => new BeadaPanelTask());

        private volatile int _panelWidth = 0;
        private volatile int _panelHeight = 0;

        public static BeadaPanelTask Instance => _instance.Value;

        private BeadaPanelTask() { }

        public static byte[] BitmapToRgb16(Bitmap bitmap)
        {
            BitmapData bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format16bppRgb565);
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
                using var bitmap = PanelDrawTask.Render(profile, false, videoBackgroundFallback: true, pixelFormat: PixelFormat.Format16bppRgb565, overrideDpi: true);
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
                using var device = USBDevice.GetSingleDevice("{8E41214B-6785-4CFE-B992-037D68949A14}");
                if (device is null)
                {
                    Trace.WriteLine("USB Device not found.");
                    return;
                }

                Trace.WriteLine($"USB Device Found - {device.Descriptor.FullName}");

                var iface = device.Interfaces.First();

                var infoMessage = new StatusLinkMessage
                {
                    Type = StatusLinkMessageType.GetPanelInfo
                };

                iface.Pipes[0x2].Write(infoMessage.ToBuffer());
                Trace.WriteLine("Sent infoTag");

                byte[] responseBuffer = new byte[100]; // 20-byte header + 80-byte payload
                int bytesRead = iface.Pipes[0x82].Read(responseBuffer);

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
                iface.Pipes[0x2].Write(resetTag.ToBuffer());

                //wait for reset
                await Task.Delay(1000, token);

                //set brightness
                var brightness = ConfigModel.Instance.Settings.BeadaPanelBrightness;

                brightnessTag.Payload = panelInfo.PanelLinkVersion == 1 ? [(byte)((brightness / 100.0 * 75) + 25)] : [(byte)brightness];
                iface.Pipes[0x2].Write(brightnessTag.ToBuffer());

                //start stream
                iface.Pipes[0x1].Write(startTag.ToBuffer());
                Trace.WriteLine("Sent startTag");

                try
                {
                    var fpsCounter = new FpsCounter();
                    var stopwatch = new Stopwatch();

                    while (!token.IsCancellationRequested)
                    {
                        stopwatch.Restart();

                        if (brightness != ConfigModel.Instance.Settings.BeadaPanelBrightness)
                        {
                            brightness = ConfigModel.Instance.Settings.BeadaPanelBrightness;
                            brightnessTag.Payload = panelInfo.PanelLinkVersion == 1 ? [(byte)((brightness / 100.0 * 75) + 25)] : [(byte)brightness];
                            iface.Pipes[0x2].Write(brightnessTag.ToBuffer());
                        }

                        if (GenerateLcdBuffer() is byte[] buffer)
                        {
                            iface.Pipes[0x1].Write(buffer);
                            fpsCounter.Update();
                        }

                        SharedModel.Instance.BeadaPanelFrameRate = fpsCounter.FramesPerSecond;
                        SharedModel.Instance.BeadaPanelFrameTime = stopwatch.ElapsedMilliseconds;

                        var targetFrameTime = 1000.0 / ConfigModel.Instance.Settings.TargetFrameRate;
                        if (stopwatch.ElapsedMilliseconds < targetFrameTime)
                        {
                            var sleep = (int)(targetFrameTime - stopwatch.ElapsedMilliseconds);
                            //Trace.WriteLine($"Sleep {sleep}ms");
                            await Task.Delay(sleep, token);
                        }
                    }
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
                            //end stream
                            iface.Pipes[0x1].Write(endTag.ToBuffer());
                            Trace.WriteLine("Sent endTag");

                            brightnessTag.Payload = [0];
                            iface.Pipes[0x2].Write(brightnessTag.ToBuffer());
                        }
                        else
                        {
                            //draw splash
                            using var bitmap = PanelDrawTask.RenderSplash(_panelWidth, _panelHeight,
                            rotateFlipType: (RotateFlipType)Enum.ToObject(typeof(RotateFlipType), ConfigModel.Instance.Settings.BeadaPanelRotation));
                            iface.Pipes[0x1].Write(BitmapToRgb16(bitmap));

                            //end stream
                            iface.Pipes[0x1].Write(endTag.ToBuffer());
                            Trace.WriteLine("Sent endTag");
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
                SharedModel.Instance.BeadaPanelRunning = false;
            }
        }
    }


}
