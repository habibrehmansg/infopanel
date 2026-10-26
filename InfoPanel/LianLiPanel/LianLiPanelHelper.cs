using InfoPanel.Models;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace InfoPanel.LianLiPanel
{
    public static class LianLiPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(LianLiPanelHelper));

        public static Task<List<LianLiPanelDevice>> GetUsbDevices()
        {
            try
            {
                List<LianLiPanelDevice> devices = [];

                foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
                {
                    var modelInfo = LianLiPanelModelDatabase.GetModelByVidPid(deviceReg.Vid, deviceReg.Pid);
                    if (modelInfo == null)
                    {
                        continue;
                    }

                    if (deviceReg.DeviceProperties["DeviceID"] is not string deviceId ||
                        deviceReg.DeviceProperties["LocationInformation"] is not string deviceLocation)
                    {
                        Logger.Warning(
                            "LianLiPanel Discovery: Skipping device with missing properties - DeviceID: '{DeviceId}', LocationInformation: '{DeviceLocation}'",
                            deviceReg.DeviceProperties["DeviceID"],
                            deviceReg.DeviceProperties["LocationInformation"]);
                        continue;
                    }

                    Logger.Information("Found Lian Li panel device: {Name} at {Location} (ID: {DeviceId})",
                        modelInfo.Name, deviceLocation, deviceId);

                    devices.Add(new LianLiPanelDevice
                    {
                        DeviceId = deviceId,
                        DeviceLocation = deviceLocation,
                        Model = modelInfo.Model,
                    });
                }

                return Task.FromResult(devices);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "LianLiPanelHelper: Error getting USB devices");
                return Task.FromResult(new List<LianLiPanelDevice>());
            }
        }
    }
}
