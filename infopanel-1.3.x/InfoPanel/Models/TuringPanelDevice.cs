using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.TuringPanel;
using InfoPanel.ViewModels;
using Serilog;
using System;
using System.Drawing.Text;
using System.Windows.Threading;

namespace InfoPanel.Models
{
    public partial class TuringPanelDevice : ObservableObject
    {
        private static readonly ILogger Logger = Log.ForContext<TuringPanelDevice>();
        
        // Configuration properties
        [ObservableProperty]
        private string _deviceId = string.Empty;

        [ObservableProperty]
        private string _deviceLocation = string.Empty;

        [ObservableProperty]
        private string _model = string.Empty;

        public string Name => GetName();

        private string GetName()
        {
            if(Enum.TryParse<TuringPanelModel>(Model, out var model))
            {
                return TuringPanelModelDatabase.Models[model].Name;
            }

            return "Unknown Model";
        }

        public TuringPanelModelInfo? ModelInfo => GetModelInfo();

        private TuringPanelModelInfo? GetModelInfo()
        {
            if (Enum.TryParse<TuringPanelModel>(Model, out var model))
            {
                return TuringPanelModelDatabase.Models[model];
            }
            
            return null;
        }

        [ObservableProperty]
        private bool _enabled = false;

        [ObservableProperty]
        private Guid _profileGuid = Guid.Empty;

        [ObservableProperty]
        private LCD_ROTATION _rotation = LCD_ROTATION.RotateNone;

        [ObservableProperty]
        private int _brightness = 100;

        // Runtime properties
        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private string _id = Guid.NewGuid().ToString();

        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private TuringPanelDeviceRuntimeProperties _runtimeProperties;

        public TuringPanelDevice()
        {
            _runtimeProperties = new();
        }

        public bool IsMatching(string deviceId)
        {
            // Simple matching by device ID
            bool matched = !string.IsNullOrEmpty(deviceId) && DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase);
            
            Logger.Debug("TuringPanel device match result: {Matched}, DeviceId: {DeviceId}", 
                matched, DeviceId);
            
            return matched;
        }

        private DateTime _lastUpdate = DateTime.MinValue;
        private readonly TimeSpan _throttleInterval = TimeSpan.FromSeconds(1);

        public void UpdateRuntimeProperties(bool? isRunning = null, int? frameRate = null, long? frameTime = null, string? errorMessage = null)
        {
            var now = DateTime.UtcNow;

            // Always update critical properties immediately
            if (isRunning != null || errorMessage != null)
            {
                _lastUpdate = now;
                DispatchUpdate(isRunning, frameRate, frameTime, errorMessage);
                return;
            }

            // Throttle frequent updates (frameRate, frameTime)
            if (now - _lastUpdate < _throttleInterval)
            {
                return; // Skip this update
            }

            _lastUpdate = now;
            DispatchUpdate(isRunning, frameRate, frameTime, errorMessage);
        }

        private void DispatchUpdate(bool? isRunning, int? frameRate, long? frameTime, string? errorMessage)
        {
            if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher)
            {
                dispatcher.BeginInvoke(() =>
                {
                    if (isRunning != null)
                    {
                        RuntimeProperties.IsRunning = isRunning.Value;
                    }

                    if (frameRate != null)
                    {
                        RuntimeProperties.FrameRate = frameRate.Value;
                    }

                    if (frameTime != null)
                    {
                        RuntimeProperties.FrameTime = frameTime.Value;
                    }

                    if (errorMessage != null)
                    {
                        RuntimeProperties.ErrorMessage = errorMessage;
                    }
                });
            }
        }

        public override string ToString()
        {
            return $"TuringPanel {DeviceId}";
        }

        public partial class TuringPanelDeviceRuntimeProperties : ObservableObject
        {
            [ObservableProperty]
            private bool _isRunning = false;

            [ObservableProperty]
            private int _frameRate = 0;

            [ObservableProperty]
            private long _frameTime = 0;

            [ObservableProperty]
            private string _errorMessage = string.Empty;
        }
    }
}