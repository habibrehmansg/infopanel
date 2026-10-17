using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using InfoPanel.ValkyriePanel;
using Serilog;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class ValkyriePanelDeviceTask(ValkyriePanelDevice device) : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<ValkyriePanelDeviceTask>();
        private readonly byte[] _pixels = new byte[ValkyrieV360Protocol.PixelDataLength];

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            if (device.Model != ValkyriePanelModel.V360)
            {
                device.UpdateRuntimeProperties(isRunning: false, errorMessage: "Unsupported Valkyrie model");
                return;
            }
            int retries = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var usb = ValkyrieUsbDevice.Open(device.DeviceId, device.DeviceLocation);
                    retries = 0;
                    device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);
                    var fps = new FpsCounter(30);
                    while (!token.IsCancellationRequested)
                    {
                        var elapsed = Stopwatch.StartNew();
                        GenerateFrame();
                        // The bridge needs a continuous stream, including unchanged/static frames.
                        usb.SendFrame(_pixels);
                        fps.Update(elapsed.ElapsedMilliseconds);
                        device.UpdateRuntimeProperties(frameRate: fps.FramesPerSecond, frameTime: fps.FrameTime);
                        int delay = 1000 / Math.Clamp(device.TargetFrameRate, 1, 30) - (int)elapsed.ElapsedMilliseconds;
                        if (delay > 0) await Task.Delay(delay, token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    retries++;
                    Logger.Warning(ex, "Valkyrie V360 {Serial}: streaming failed", device.DeviceLocation);
                    device.UpdateRuntimeProperties(errorMessage: ex.Message);
                }
                finally { device.UpdateRuntimeProperties(isRunning: false); }
                await Task.Delay(retries < 3 ? 1000 : 5000, token);
            }
        }

        private void GenerateFrame()
        {
            if (ConfigModel.Instance.GetProfile(device.ProfileGuid) is not Profile profile)
            {
                for (int i = 0; i < _pixels.Length; i += 2) { _pixels[i] = 128; _pixels[i + 1] = 16; }
                return;
            }
            using var bitmap = PanelDrawTask.RenderSK(profile, false, colorType: SKColorType.Rgba8888, alphaType: SKAlphaType.Opaque);
            var resized = SKBitmapExtensions.EnsureBitmapSize(bitmap, ValkyrieV360Protocol.Width, ValkyrieV360Protocol.Height, device.Rotation);
            try { ValkyrieFrameProtocol.ConvertRgba(resized.GetPixelSpan(), resized.RowBytes, _pixels, device.Brightness); }
            finally { if (!ReferenceEquals(resized, bitmap)) resized.Dispose(); }
        }
    }
}
