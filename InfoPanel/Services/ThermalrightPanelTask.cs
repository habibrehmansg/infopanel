using InfoPanel.Models;
using InfoPanel.ThermalrightPanel;
using LibUsbDotNet;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class ThermalrightPanelTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<ThermalrightPanelTask>();
        private static readonly Lazy<ThermalrightPanelTask> _instance = new(() => new ThermalrightPanelTask());

        private readonly ConcurrentDictionary<string, ThermalrightPanelDeviceTask> _deviceTasks = new();

        public static ThermalrightPanelTask Instance => _instance.Value;

        private ThermalrightPanelTask() { }

        public async Task StartDevice(ThermalrightPanelDevice device)
        {
            if (_deviceTasks.TryGetValue(device.Id, out var task))
            {
                await task.StopAsync();
                _deviceTasks.TryRemove(device.Id, out _);
            }

            var deviceTask = new ThermalrightPanelDeviceTask(device);
            if (_deviceTasks.TryAdd(device.Id, deviceTask))
            {
                await deviceTask.StartAsync(CancellationToken);
                Logger.Information("Started Thermalright panel device {Device}", device);
            }
        }

        public async Task StopDevice(string deviceId)
        {
            if (_deviceTasks.TryRemove(deviceId, out var deviceTask))
            {
                await deviceTask.StopAsync();
                Logger.Information("Stopped Thermalright panel device {DeviceId}", deviceId);
            }
        }

        public async Task StopAllDevices()
        {
            var tasks = new List<Task>();

            foreach (var kvp in _deviceTasks.ToList())
            {
                if (_deviceTasks.TryRemove(kvp.Key, out var deviceTask))
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await deviceTask.StopAsync();
                    }));
                }
            }

            await Task.WhenAll(tasks);
            Logger.Information("Stopped all Thermalright panel devices");
        }

        public bool IsDeviceRunning(string deviceId)
        {
            if (_deviceTasks.TryGetValue(deviceId, out var task))
            {
                return task.IsRunning;
            }

            return false;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            try
            {
                var settings = ConfigModel.Instance.Settings;

                Logger.Debug("ThermalrightPanel: DoWorkAsync starting - MultiDeviceMode: {MultiDeviceMode}", settings.ThermalrightPanelMultiDeviceMode);

                if (settings.ThermalrightPanelMultiDeviceMode)
                {
                    await RunMultiDeviceMode(token);
                }
                else
                {
                    Logger.Debug("ThermalrightPanel: Multi-device mode is disabled. No devices will be started.");
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "ThermalrightPanel: Error in DoWorkAsync");
            }
        }

        private async Task RunMultiDeviceMode(CancellationToken token)
        {
            Logger.Debug("ThermalrightPanel: Starting multi-device mode");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var settings = ConfigModel.Instance.Settings;

                    // Exit if multi-device mode was turned off
                    if (!settings.ThermalrightPanelMultiDeviceMode)
                    {
                        Logger.Debug("ThermalrightPanel: Multi-device mode turned off, exiting loop");
                        break;
                    }

                    // Start enabled devices that aren't running
                    var enabledConfigs = settings.ThermalrightPanelDevices.Where(d => d.Enabled).ToList();

                    foreach (var config in enabledConfigs)
                    {
                        var configId = config.Id;
                        if (!IsDeviceRunning(configId))
                        {
                            await StartDevice(config);
                        }
                    }

                    // Stop devices that are no longer enabled
                    var runningDeviceIds = _deviceTasks.Keys.ToList();
                    foreach (var deviceId in runningDeviceIds)
                    {
                        if (!enabledConfigs.Any(c => c.Id == deviceId))
                        {
                            await StopDevice(deviceId);
                        }
                    }

                    await Task.Delay(1000, token);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "ThermalrightPanel: Error in RunMultiDeviceMode");
                    await Task.Delay(1000, token);
                }
            }
        }
    }
}
