using InfoPanel.Models;
using InfoPanel.TuringPanel;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class TuringPanelTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<TuringPanelTask>();
        private static readonly Lazy<TuringPanelTask> _instance = new(() => new TuringPanelTask());

        private readonly ConcurrentDictionary<string, BackgroundTask> _deviceTasks = new();

        public static TuringPanelTask Instance => _instance.Value;

        private TuringPanelTask() { }

        public async Task StartDevice(TuringPanelDevice device)
        {
            if (_deviceTasks.TryGetValue(device.Id, out var task))
            {
                await task.StopAsync();
                _deviceTasks.TryRemove(device.Id, out _);
            }

            var modelInfo = device.ModelInfo;

            if (modelInfo != null)
            {
                BackgroundTask deviceTask = modelInfo.IsUsbDevice 
                    ? new TuringPanelUsbDeviceTask(device) 
                    : new TuringPanelSerialTask(device);

                if (_deviceTasks.TryAdd(device.Id, deviceTask))
                {
                    await deviceTask.StartAsync(CancellationToken);
                    Logger.Information("Started TuringPanel device {Device}", device);
                }
            }
            else
            {
                Logger.Error("TuringPanel: Unknown device model {Model} for device {DeviceId}",
                    device.Model, device.Id);
                return;
            }
        }

        public async Task StopDevice(string deviceId)
        {
            if (_deviceTasks.TryRemove(deviceId, out var deviceTask))
            {
                await deviceTask.StopAsync();
                Logger.Information("Stopped TuringPanel device {DeviceId}", deviceId);
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
            Logger.Information("Stopped all TuringPanel devices");
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

                Logger.Debug("TuringPanel: DoWorkAsync starting - MultiDeviceMode: {MultiDeviceMode}",
                    settings.TuringPanelMultiDeviceMode);

                if (settings.TuringPanelMultiDeviceMode)
                {
                    await RunMultiDeviceMode(token);
                }
                else
                {
                    Logger.Debug("TuringPanel: Multi-device mode is disabled. No devices will be started.");
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "TuringPanel: Error in DoWorkAsync");
            }
        }

        private async Task RunMultiDeviceMode(CancellationToken token)
        {
            Logger.Debug("TuringPanel: Starting multi-device mode");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var settings = ConfigModel.Instance.Settings;

                    // Exit if multi-device mode was turned off
                    if (!settings.TuringPanelMultiDeviceMode)
                    {
                        Logger.Debug("TuringPanel: Multi-device mode turned off, exiting loop");
                        break;
                    }

                    // Start enabled devices that aren't running
                    var enabledConfigs = settings.TuringPanelDevices.Where(d => d.Enabled).ToList();

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
                    Logger.Error(ex, "TuringPanel: Error in RunMultiDeviceMode");
                    await Task.Delay(1000, token); // Wait longer on error
                }
            }
        }
    }
}