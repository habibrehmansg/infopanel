using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.LianLiPanel;
using InfoPanel.ViewModels;
using Serilog;
using System;
using System.Windows.Threading;

namespace InfoPanel.Models
{
    public partial class LianLiPanelDevice : ObservableObject
    {
        private static readonly ILogger Logger = Log.ForContext<LianLiPanelDevice>();

        [ObservableProperty]
        private string _deviceId = string.Empty;

        [ObservableProperty]
        private string _deviceLocation = string.Empty;

        [ObservableProperty]
        private LianLiPanelModel _model = LianLiPanelModel.UniversalScreen88Inch;

        partial void OnModelChanged(LianLiPanelModel value)
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(ModelInfo));
            OnPropertyChanged(nameof(DisplayWidth));
            OnPropertyChanged(nameof(DisplayHeight));
        }

        public string Name => ModelInfo?.Name ?? "Unknown Lian Li Panel";

        public LianLiPanelModelInfo? ModelInfo =>
            LianLiPanelModelDatabase.Models.TryGetValue(Model, out var info) ? info : null;

        public int DisplayWidth => ModelInfo?.Width ?? 0;
        public int DisplayHeight => ModelInfo?.Height ?? 0;

        [ObservableProperty]
        private bool _enabled = false;

        [ObservableProperty]
        private Guid _profileGuid = Guid.Empty;

        [ObservableProperty]
        private LCD_ROTATION _rotation = LCD_ROTATION.RotateNone;

        [ObservableProperty]
        private int _brightness = 100;

        [ObservableProperty]
        private int _targetFrameRate = 30;

        [ObservableProperty]
        private int _jpegQuality = 90;

        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private string _id = Guid.NewGuid().ToString();

        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private LianLiPanelDeviceRuntimeProperties _runtimeProperties;

        public LianLiPanelDevice()
        {
            _runtimeProperties = new();
        }

        public bool IsMatching(string deviceId)
        {
            var matched = !string.IsNullOrEmpty(deviceId) &&
                DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase);

            Logger.Debug("Lian Li panel device match result: {Matched}, DeviceId: {DeviceId}",
                matched, DeviceId);

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
            {
                return;
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
                    if (isRunning != null) RuntimeProperties.IsRunning = isRunning.Value;
                    if (frameRate != null) RuntimeProperties.FrameRate = frameRate.Value;
                    if (frameTime != null) RuntimeProperties.FrameTime = frameTime.Value;
                    if (errorMessage != null) RuntimeProperties.ErrorMessage = errorMessage;
                });
            }
        }

        public override string ToString()
        {
            return $"LianLiPanel {DeviceId}";
        }

        public partial class LianLiPanelDeviceRuntimeProperties : ObservableObject
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
