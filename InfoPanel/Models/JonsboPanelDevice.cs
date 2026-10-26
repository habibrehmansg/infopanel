using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.JonsboPanel;
using InfoPanel.ViewModels;
using Serilog;
using System;
using System.Windows.Threading;

namespace InfoPanel.Models
{
    public partial class JonsboPanelDevice : ObservableObject
    {
        private static readonly ILogger Logger = Log.ForContext<JonsboPanelDevice>();

        [ObservableProperty]
        private string _deviceId = string.Empty;

        [ObservableProperty]
        private string _deviceLocation = string.Empty; // COM port e.g. "COM5"

        [ObservableProperty]
        private JonsboPanelModel _model = JonsboPanelModel.DS916;

        partial void OnModelChanged(JonsboPanelModel value)
        {
            OnPropertyChanged(nameof(ModelInfo));
            OnPropertyChanged(nameof(DisplayWidth));
            OnPropertyChanged(nameof(DisplayHeight));
        }

        [ObservableProperty]
        private bool _enabled = false;

        [ObservableProperty]
        private Guid _profileGuid = Guid.Empty;

        // The DS916 panel is natively portrait (462x1920); default to 90° so a
        // landscape 1920x462 profile fills it out of the box.
        [ObservableProperty]
        private LCD_ROTATION _rotation = LCD_ROTATION.Rotate90FlipNone;

        [ObservableProperty]
        private int _brightness = 100;

        [ObservableProperty]
        private int _targetFrameRate = 15;

        [ObservableProperty]
        private int _jpegQuality = 80;

        // Runtime properties (not persisted)
        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private string _id = Guid.NewGuid().ToString();

        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private JonsboPanelDeviceRuntimeProperties _runtimeProperties;

        public JonsboPanelDevice()
        {
            _runtimeProperties = new();
        }

        public JonsboPanelModelInfo? ModelInfo =>
            JonsboPanelModelDatabase.Models.TryGetValue(Model, out var info) ? info : null;

        public int DisplayWidth => ModelInfo?.Width ?? 0;
        public int DisplayHeight => ModelInfo?.Height ?? 0;

        public bool IsMatching(string deviceId, string deviceLocation, JonsboPanelModel model)
        {
            if (DeviceId.Equals(deviceId) && DeviceLocation.Equals(deviceLocation) && Model.Equals(model))
                return true;
            // Fallback: match by PNP device id alone (COM ports can change)
            if (DeviceId.Equals(deviceId) && Model.Equals(model))
                return true;
            return false;
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

            if (now - _lastUpdate < _throttleInterval) return;
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

        public partial class JonsboPanelDeviceRuntimeProperties : ObservableObject
        {
            [ObservableProperty]
            private bool _isRunning = false;

            [ObservableProperty]
            private string _name = "Jonsbo Panel";

            [ObservableProperty]
            private int _frameRate = 0;

            [ObservableProperty]
            private long _frameTime = 0;

            [ObservableProperty]
            private string _errorMessage = string.Empty;
        }
    }
}
