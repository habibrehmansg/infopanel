using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace InfoPanel.JonsboPanel
{
    public class JonsboPanelDiscoveryInfo
    {
        public string DeviceId { get; set; } = "";
        public string DeviceLocation { get; set; } = ""; // COM port name (e.g. "COM5")
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public JonsboPanelModel Model { get; set; }
        public JonsboPanelModelInfo? ModelInfo { get; set; }
    }

    public static partial class JonsboPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(JonsboPanelHelper));

        /// <summary>
        /// Scans for Jonsbo panels: HLVMAX serial devices via Win32_SerialPort, and
        /// MacroSilicon MS9132 USB displays (DS339) via their HID control interface.
        /// </summary>
        public static List<JonsboPanelDiscoveryInfo> ScanDevices()
        {
            var devices = new List<JonsboPanelDiscoveryInfo>();

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_SerialPort");
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    string? comPort = obj["DeviceID"]?.ToString();
                    string? pnpDeviceId = obj["PNPDeviceID"]?.ToString();

                    if (comPort == null || pnpDeviceId == null) continue;
                    if (!TryParseVidPid(pnpDeviceId, out var vid, out var pid)) continue;

                    var modelInfo = JonsboPanelModelDatabase.GetModelByVidPid(vid, pid);
                    if (modelInfo == null) continue;

                    Logger.Information("JonsboPanelHelper: Found {Name} on {Port} (PNP={Pnp})",
                        modelInfo.Name, comPort, pnpDeviceId);

                    devices.Add(new JonsboPanelDiscoveryInfo
                    {
                        DeviceId = pnpDeviceId,
                        DeviceLocation = comPort,
                        VendorId = vid,
                        ProductId = pid,
                        Model = modelInfo.Model,
                        ModelInfo = modelInfo,
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "JonsboPanelHelper: Error scanning serial ports");
            }

            // MS9132-based panels (DS339): presence-detect via the HID control interface,
            // which binds to the inbox HID driver regardless of the video-interface driver.
            try
            {
                foreach (var hid in HidSharp.DeviceList.Local.GetHidDevices(
                    JonsboPanelModelDatabase.MS9132_VENDOR_ID, JonsboPanelModelDatabase.MS9132_PRODUCT_ID))
                {
                    var modelInfo = JonsboPanelModelDatabase.Models[JonsboPanelModel.DS339];

                    string serial = "";
                    try { serial = hid.GetSerialNumber(); } catch { }

                    string deviceId = $"USB\\VID_{JonsboPanelModelDatabase.MS9132_VENDOR_ID:X4}&PID_{JonsboPanelModelDatabase.MS9132_PRODUCT_ID:X4}\\{serial}";

                    Logger.Information("JonsboPanelHelper: Found {Name} (MS9132, serial {Serial})",
                        modelInfo.Name, serial);

                    devices.Add(new JonsboPanelDiscoveryInfo
                    {
                        DeviceId = deviceId,
                        DeviceLocation = string.IsNullOrEmpty(serial) ? "MS9132" : serial,
                        VendorId = JonsboPanelModelDatabase.MS9132_VENDOR_ID,
                        ProductId = JonsboPanelModelDatabase.MS9132_PRODUCT_ID,
                        Model = modelInfo.Model,
                        ModelInfo = modelInfo,
                    });
                    break; // one MS9132 device supported per scan for now
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "JonsboPanelHelper: Error scanning MS9132 devices");
            }

            Logger.Information("JonsboPanelHelper: Found {Count} Jonsbo panel device(s)", devices.Count);
            return devices;
        }

        private static bool TryParseVidPid(string pnpDeviceId, out int vid, out int pid)
        {
            vid = 0; pid = 0;
            var match = VidPidRegex().Match(pnpDeviceId);
            if (!match.Success) return false;
            vid = Convert.ToInt32(match.Groups[1].Value, 16);
            pid = Convert.ToInt32(match.Groups[2].Value, 16);
            return true;
        }

        [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})")]
        private static partial Regex VidPidRegex();
    }
}
