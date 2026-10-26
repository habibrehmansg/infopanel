using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class LianLiPanelTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<LianLiPanelTask>();
        private static readonly Lazy<LianLiPanelTask> _instance = new(() => new LianLiPanelTask());

        private readonly ConcurrentDictionary<string, LianLiPanelDeviceTask> _deviceTasks = new();

        public static LianLiPanelTask Instance => _instance.Value;

        private LianLiPanelTask() { }

        public override async Task StopAsync(bool shutdown = false)
        {
            await base.StopAsync(shutdown);
            await StopAllDevices();
        }

        public async Task StartDevice(LianLiPanelDevice device)
        {
            if (_deviceTasks.TryGetValue(device.Id, out var task))
            {
                await task.StopAsync();
                _deviceTasks.TryRemove(device.Id, out _);
            }

            var deviceTask = new LianLiPanelDeviceTask(device);
            if (_deviceTasks.TryAdd(device.Id, deviceTask))
            {
                await deviceTask.StartAsync(CancellationToken);
                Logger.Information("Started Lian Li panel device {Device}", device);
            }
        }

        public async Task StopDevice(string deviceId)
        {
            if (_deviceTasks.TryRemove(deviceId, out var deviceTask))
            {
                await deviceTask.StopAsync();
                Logger.Information("Stopped Lian Li panel device {DeviceId}", deviceId);
            }
        }

        public async Task StopAllDevices()
        {
            var tasks = new List<Task>();
            foreach (var kvp in _deviceTasks.ToList())
            {
                if (_deviceTasks.TryRemove(kvp.Key, out var deviceTask))
                {
                    tasks.Add(Task.Run(async () => await deviceTask.StopAsync()));
                }
            }

            await Task.WhenAll(tasks);
            Logger.Information("Stopped all Lian Li panel devices");
        }

        public bool IsDeviceRunning(string deviceId)
        {
            return _deviceTasks.TryGetValue(deviceId, out var task) && task.IsRunning;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            try
            {
                var settings = ConfigModel.Instance.Settings;
                if (settings.LianLiPanelMultiDeviceMode)
                {
                    await RunMultiDeviceMode(token);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error(ex, "LianLiPanel: Error in DoWorkAsync");
            }
        }

        private async Task RunMultiDeviceMode(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var settings = ConfigModel.Instance.Settings;
                    if (!settings.LianLiPanelMultiDeviceMode)
                    {
                        break;
                    }

                    var enabledConfigs = settings.LianLiPanelDevices.Where(d => d.Enabled).ToList();

                    foreach (var config in enabledConfigs)
                    {
                        if (!IsDeviceRunning(config.Id))
                        {
                            await StartDevice(config);
                        }
                    }

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
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "LianLiPanel: Error in RunMultiDeviceMode");
                    await Task.Delay(1000, token);
                }
            }
        }
    }
}
