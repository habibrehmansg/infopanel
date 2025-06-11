using InfoPanel.BeadaPanel.StatusLink;
using InfoPanel.Utils;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.BeadaPanel
{
    internal static class BeadaPanelHelper
    {
        private static readonly TypedMemoryCache<BeadaPanelInfo> _panelInfoCache = new();
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

        public static async Task<BeadaPanelInfo?> GetPanelInfoAsync(UsbRegistry usbRegistry)
        {
            await _semaphore.WaitAsync();

            try
            {
                return await Task.Run(() => {
                    return GetPanelInfo(usbRegistry);
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"BeadaPanelHelper: Error claiming USB interface - {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
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
                Trace.WriteLine($"StatusLink Query: Could not open USB device {usbRegistry.DevicePath}");
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

                _panelInfoCache.Set(usbRegistry.DevicePath, panelInfo, new MemoryCacheEntryOptions()
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(2)
                });

                Trace.WriteLine($"StatusLink Query: Successfully parsed panel info for serial {panelInfo.SerialNumber}");
            }

            return panelInfo;
        }
    }
}
