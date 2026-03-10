using InfoPanel.BeadaPanel;
using InfoPanel.BeadaPanel.PanelLink;
using InfoPanel.BeadaPanel.StatusLink;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Services;
using InfoPanel.Utils;
using InfoPanel.ViewModels;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace InfoPanel
{
    public sealed class BeadaPanelTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<BeadaPanelTask>();
        private static readonly Lazy<BeadaPanelTask> _instance = new(() => new BeadaPanelTask());
        
        private readonly ConcurrentDictionary<string, BeadaPanelDeviceTask> _deviceTasks = new();

        public static BeadaPanelTask Instance => _instance.Value;

        private BeadaPanelTask() { }

        public async Task StartDevice(BeadaPanelDevice device)
        {
            if(_deviceTasks.TryGetValue(device.Id, out var task))
            {
                await task.StopAsync();
                _deviceTasks.TryRemove(device.Id, out _);
            }

            var deviceTask = new BeadaPanelDeviceTask(device);
            if (_deviceTasks.TryAdd(device.Id, deviceTask))
            {
                await deviceTask.StartAsync(CancellationToken);
                Logger.Information("Started BeadaPanel device {Device}", device);
            }
        }

        public async Task StopDevice(string deviceId)
        {
            if (_deviceTasks.TryRemove(deviceId, out var deviceTask))
            {
                await deviceTask.StopAsync();
                //deviceTask.Dispose();
                Logger.Information("Stopped BeadaPanel device {DeviceId}", deviceId);
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
                        //deviceTask.Dispose();
                    }));
                }
            }

            await Task.WhenAll(tasks);
            Logger.Information("Stopped all BeadaPanel devices");
        }

        public bool IsDeviceRunning(string deviceId)
        {
            if(_deviceTasks.TryGetValue(deviceId, out var task)){
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
                
                Logger.Debug("BeadaPanel: DoWorkAsync starting - MultiDeviceMode: {MultiDeviceMode}", settings.BeadaPanelMultiDeviceMode);

                if (settings.BeadaPanelMultiDeviceMode)
                {
                    await RunMultiDeviceMode(token);
                }
                else
                {
                    Logger.Debug("BeadaPanel: Multi-device mode is disabled. No devices will be started.");
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "BeadaPanel: Error in DoWorkAsync");
            }
        }

        private async Task RunMultiDeviceMode(CancellationToken token)
        {
            Logger.Debug("BeadaPanel: Starting multi-device mode");
            
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var settings = ConfigModel.Instance.Settings;
                    
                    // Exit if multi-device mode was turned off
                    if (!settings.BeadaPanelMultiDeviceMode)
                    {
                        Logger.Debug("BeadaPanel: Multi-device mode turned off, exiting loop");
                        break;
                    }

                    // Start enabled devices that aren't running
                    var enabledConfigs = settings.BeadaPanelDevices.Where(d => d.Enabled).ToList();
                    
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
                    Logger.Error(ex, "BeadaPanel: Error in RunMultiDeviceMode");
                    await Task.Delay(1000, token); // Wait longer on error
                }
            }
        }
    }
}