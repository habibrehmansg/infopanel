using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.ThermaltakePanel;
using InfoPanel.ViewModels;
using Serilog;
using System;
using System.Linq;
using System.Windows.Threading;

namespace InfoPanel.Models
{
    public partial class ThermaltakePanelDevice : ObservableObject
    {
        private static readonly ILogger Logger = Log.ForContext<ThermaltakePanelDevice>();

        [ObservableProperty]
        private string _deviceId = string.Empty;

        [ObservableProperty]
        private string _deviceLocation = string.Empty;

        [ObservableProperty]
        private ThermaltakePanelModel _model = ThermaltakePanelModel.ToughLiquid6Inch;

        partial void OnModelChanged(ThermaltakePanelModel value)
        {
            OnPropertyChanged(nameof(ModelInfo));
            OnPropertyChanged(nameof(DisplayWidth));
            OnPropertyChanged(nameof(DisplayHeight));
        }

        partial void OnDeviceLocationChanged(string value)
        {
            OnPropertyChanged(nameof(DevicePort));
        }

        public string DevicePort => DeviceLocation.Split('.').FirstOrDefault() ?? string.Empty;

        [ObservableProperty]
        private bool _enabled = false;

        [ObservableProperty]
        private Guid _profileGuid = Guid.Empty;

        [ObservableProperty]
        private LCD_ROTATION _rotation = LCD_ROTATION.RotateNone;

        [ObservableProperty]
        private int _brightness = 100;

        [ObservableProperty]
        private int _targetFrameRate = 15;

        [ObservableProperty]
        private int _jpegQuality = 85;

        // Runtime properties (not persisted)
        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private string _id = Guid.NewGuid().ToString();

        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private ThermaltakePanelDeviceRuntimeProperties _runtimeProperties;

        public ThermaltakePanelDevice()
        {
            _runtimeProperties = new();
        }

        public ThermaltakePanelModelInfo? ModelInfo =>
            ThermaltakePanelModelDatabase.Models.TryGetValue(Model, out var info) ? info : null;

        public int DisplayWidth => ModelInfo?.Width ?? 0;
        public int DisplayHeight => ModelInfo?.Height ?? 0;

        public bool IsMatching(string deviceId, string deviceLocation, ThermaltakePanelModel model)
        {
            bool matched = false;

            if (DeviceId.Equals(deviceId) && DeviceLocation.Equals(deviceLocation) && Model.Equals(model))
                matched = true;
            else
            {
                string port = deviceLocation.Split('.').FirstOrDefault() ?? string.Empty;
                if (DeviceId.Equals(deviceId) && DevicePort.Equals(port) && Model.Equals(model))
                    matched = true;
                else if (DeviceId.Equals(deviceId) && DevicePort.Equals(port))
                    matched = true;
                else if (DeviceId.Equals(deviceId) && DeviceLocation.Equals(deviceLocation))
                    matched = true;
            }

            return matched;
        }

        private DateTime _lastUpdate = DateTime.MinValue;
        private readonly TimeSpan _throttleInterval = TimeSpan.FromSeconds(1);

        public void UpdateRuntimeProperties(bool? isRunning = null, int? frameRate = null, long? frameTime = null, string? errorMessage = null)
        {
            var now = DateTime.UtcNow;

            if (isRunning != null || errorMessage != null)
            {
                _lastUpdate = now;
                DispatchUpdate(isRunning, frameRate, frameTime, errorMessage);
                return;
            }

            if (now - _lastUpdate < _throttleInterval)
                return;

            _lastUpdate = now;
            DispatchUpdate(isRunning, frameRate, frameTime, errorMessage);
        }

        private void DispatchUpdate(bool? isRunning, int? frameRate, long? frameTime, string? errorMessage)
        {
            if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher)
            {
                dispatcher.BeginInvoke(() =>
                {
                    if (isRunning != null) RuntimeProperties.IsRunning = isRunning.Value;
                    if (frameRate != null) RuntimeProperties.FrameRate = frameRate.Value;
                    if (frameTime != null) RuntimeProperties.FrameTime = frameTime.Value;
                    if (errorMessage != null) RuntimeProperties.ErrorMessage = errorMessage;
                });
            }
        }

        public override string ToString() => DeviceLocation;

        public partial class ThermaltakePanelDeviceRuntimeProperties : ObservableObject
        {
            [ObservableProperty]
            private bool _isRunning = false;

            [ObservableProperty]
            private string _name = "Thermaltake LCD";

            [ObservableProperty]
            private int _frameRate = 0;

            [ObservableProperty]
            private long _frameTime = 0;

            [ObservableProperty]
            private string _errorMessage = string.Empty;
        }
    }
}
