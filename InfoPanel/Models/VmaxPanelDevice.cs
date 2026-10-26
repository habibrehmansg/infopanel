using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.ViewModels;
using InfoPanel.VmaxPanel;
using Serilog;
using System;
using System.Windows.Threading;

namespace InfoPanel.Models
{
    public partial class VmaxPanelDevice : ObservableObject
    {
        private static readonly ILogger Logger = Log.ForContext<VmaxPanelDevice>();

        [ObservableProperty]
        private string _deviceId = string.Empty;

        [ObservableProperty]
        private string _deviceLocation = string.Empty;

        [ObservableProperty]
        private VmaxPanelModel _model = VmaxPanelModel.Vmax46Inch;

        partial void OnModelChanged(VmaxPanelModel value)
        {
            OnPropertyChanged(nameof(ModelInfo));
            OnPropertyChanged(nameof(DisplayWidth));
            OnPropertyChanged(nameof(DisplayHeight));
        }

        [ObservableProperty]
        private bool _enabled = false;

        [ObservableProperty]
        private Guid _profileGuid = Guid.Empty;

        [ObservableProperty]
        private LCD_ROTATION _rotation = LCD_ROTATION.RotateNone;

        [ObservableProperty]
        private int _brightness = 99;

        partial void OnBrightnessChanged(int value)
        {
            var clamped = Math.Clamp(value, 0, 99);
            if (value != clamped)
                Brightness = clamped;
        }

        [ObservableProperty]
        private bool _screenSwitch = true;

        [ObservableProperty]
        private int _targetFrameRate = 1;

        partial void OnTargetFrameRateChanged(int value)
        {
            var clamped = Math.Clamp(value, 1, 30);
            if (value != clamped)
                TargetFrameRate = clamped;
        }

        [ObservableProperty]
        private int _jpegQuality = 85;

        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private string _id = Guid.NewGuid().ToString();

        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private VmaxPanelDeviceRuntimeProperties _runtimeProperties;

        public VmaxPanelDevice()
        {
            _runtimeProperties = new();
        }

        public VmaxPanelModelInfo? ModelInfo =>
            VmaxPanelModelDatabase.Models.TryGetValue(Model, out var info) ? info : null;

        public int DisplayWidth => ModelInfo?.Width ?? 0;
        public int DisplayHeight => ModelInfo?.Height ?? 0;

        public bool IsMatching(string deviceId, string deviceLocation, VmaxPanelModel model)
        {
            return DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)
                && (DeviceLocation.Equals(deviceLocation, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(DeviceLocation))
                && Model.Equals(model);
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

        public partial class VmaxPanelDeviceRuntimeProperties : ObservableObject
        {
            [ObservableProperty]
            private bool _isRunning = false;

            [ObservableProperty]
            private string _name = "VMAX LCD";

            [ObservableProperty]
            private int _frameRate = 0;

            [ObservableProperty]
            private long _frameTime = 0;

            [ObservableProperty]
            private string _errorMessage = string.Empty;
        }
    }
}
