using AsyncKeyedLock;
using InfoPanel.BeadaPanel.StatusLink;
using InfoPanel.Utils;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using System;
using System.Threading.Tasks;

namespace InfoPanel.BeadaPanel
{
    internal static class BeadaPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(BeadaPanelHelper));
        private static readonly TypedMemoryCache<BeadaPanelInfo> _panelInfoCache = new();
        private static readonly AsyncNonKeyedLocker _lock = new(1);

        public static async Task<BeadaPanelInfo?> GetPanelInfoAsync(UsbRegistry usbRegistry)
        {
            using (await _lock.LockAsync())
            {
                try
                {
                    return await Task.Run(() =>
                    {
                        return GetPanelInfo(usbRegistry);
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "BeadaPanelHelper: Error claiming USB interface");
                }
            }
            return null;
        }

        private static BeadaPanelInfo? GetPanelInfo(UsbRegistry usbRegistry)
        {
            if (_panelInfoCache.TryGetValue(usbRegistry.DevicePath, out var result))
            {
                return result;
            }

            // Try to open the specific device
            using var usbDevice = usbRegistry.Device;

            if (usbDevice == null)
            {
                Logger.Warning("StatusLink Query: Could not open USB device {DevicePath}", usbRegistry.DevicePath);
                return null;
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

            using var writer = usbDevice.OpenEndpointWriter(WriteEndpointID.Ep02);
            var writeResult = writer.Write(infoMessage.ToBuffer(), 1000, out int _);

            if (writeResult != ErrorCode.None)
            {
                Logger.Error("StatusLink Query: Write failed with error {ErrorCode}", writeResult);
                return null;
            }

            // Read response
            byte[] responseBuffer = new byte[100];
            using var reader = usbDevice.OpenEndpointReader(ReadEndpointID.Ep02);
            var readResult = reader.Read(responseBuffer, 1000, out int bytesRead);

            if (readResult != ErrorCode.None || bytesRead == 0)
            {
                Log.Error("StatusLink Query: Read failed with error {ErrorCode}, bytes read: {BytesRead}", readResult, bytesRead);
                return null;
            }

            // Parse panel info
            var panelInfo = BeadaPanelParser.ParsePanelInfoResponse(responseBuffer);
            if (panelInfo != null)
            {

                _panelInfoCache.Set(usbRegistry.DevicePath, panelInfo, new MemoryCacheEntryOptions()
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(2)
                });

                Log.Information("StatusLink Query: Successfully parsed panel info for serial {SerialNumber}", panelInfo.SerialNumber);
            }

            return panelInfo;
        }
    }
}
