using HidSharp;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.ValkyriePanel
{
    public sealed class ValkyriePanelDiscoveryInfo
    {
        public string DeviceId { get; init; } = string.Empty;
        public string DeviceLocation { get; init; } = string.Empty;
        public ValkyriePanelModel Model => ValkyriePanelModel.V360;
        public ValkyriePanelModelInfo ModelInfo => ValkyriePanelModelDatabase.Models[Model];
    }

    public static class ValkyriePanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(ValkyriePanelHelper));

        // VID/PID identifies the MS9132 bridge, which is also used by unrelated displays.
        // Never run the panel initialization sequence during discovery.
        public static List<ValkyriePanelDiscoveryInfo> ScanDevices(ISet<string>? streamingSerials = null)
        {
            var result = new List<ValkyriePanelDiscoveryInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hid in DeviceList.Local.GetHidDevices(ValkyriePanelModelDatabase.VendorId, ValkyriePanelModelDatabase.ProductId))
            {
                try
                {
                    var serial = hid.GetSerialNumber();
                    if (string.IsNullOrWhiteSpace(serial) || seen.Contains(serial) || streamingSerials?.Contains(serial) == true)
                        continue;
                    using var control = new ValkyrieHidDevice(hid);
                    if (!control.IsV360()) continue;
                    seen.Add(serial);
                    result.Add(new() { DeviceId = DeviceId(serial), DeviceLocation = serial });
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Valkyrie: could not identify HID panel {Path}", hid.DevicePath);
                }
            }
            return result;
        }

        internal static HidDevice? FindHid(string serial)
        {
            // Refuse ambiguous serials rather than initialize another attached panel.
            var matches = DeviceList.Local.GetHidDevices(ValkyriePanelModelDatabase.VendorId, ValkyriePanelModelDatabase.ProductId)
                .Where(h => h.GetMaxFeatureReportLength() == 9 && string.Equals(h.GetSerialNumber(), serial, StringComparison.Ordinal))
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        internal static bool IsDisplayInterface(UsbRegistry registry)
        {
            if (registry.Vid != ValkyriePanelModelDatabase.VendorId || registry.Pid != ValkyriePanelModelDatabase.ProductId)
                return false;
            // Composite WinUSB paths contain MI_03; whole-device libusb paths do not contain MI_.
            var path = registry.DevicePath ?? string.Empty;
            return !path.Contains("&mi_", StringComparison.OrdinalIgnoreCase)
                || path.Contains("&mi_03", StringComparison.OrdinalIgnoreCase);
        }

        public static string DeviceId(string serial) => $@"USB\VID_345F&PID_9132\{serial}";
    }
}
