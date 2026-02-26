using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.ThermalrightPanel;
using InfoPanel.ViewModels;
using System;
using Serilog;
using System.Linq;
using System.Windows.Threading;

namespace InfoPanel.Models
{
    public partial class ThermalrightPanelDevice : ObservableObject
    {
        private static readonly ILogger Logger = Log.ForContext<ThermalrightPanelDevice>();

        // Configuration properties
        [ObservableProperty]
        private string _deviceId = string.Empty;

        [ObservableProperty]
        private string _deviceLocation = string.Empty;

        [ObservableProperty]
        private ThermalrightPanelModel _model = ThermalrightPanelModel.WonderVision360;

        partial void OnModelChanged(ThermalrightPanelModel value)
        {
            OnPropertyChanged(nameof(ModelInfo));
            OnPropertyChanged(nameof(IsJpegQualityConfigurable));
            OnPropertyChanged(nameof(HasDisplayMask));
            OnPropertyChanged(nameof(HasFlickerFix));
        }

        partial void OnDeviceLocationChanged(string value)
        {
            OnPropertyChanged(DevicePort);
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
        private int _jpegQuality = 95;  // TRCC default is 95

        [ObservableProperty]
        private ThermalrightDisplayMask _displayMask = ThermalrightDisplayMask.None;

        [ObservableProperty]
        private bool _flickerFix = false;

        // Runtime properties
        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private string _id = Guid.NewGuid().ToString();

        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private ThermalrightPanelDeviceRuntimeProperties _runtimeProperties;

        public ThermalrightPanelDevice()
        {
            _runtimeProperties = new();
        }

        public ThermalrightPanelModelInfo? ModelInfo => ThermalrightPanelModelDatabase.Models.TryGetValue(Model, out var info) ? info : null;

        /// <summary>
        /// Wonder/Rainbow Vision 360 panels have a camera punch-hole that can be hidden with an overlay mask.
        /// </summary>
        public bool HasDisplayMask
        {
            get
            {
                return Model == ThermalrightPanelModel.WonderVision360
                    || Model == ThermalrightPanelModel.RainbowVision360;
            }
        }

        /// <summary>
        /// JPEG quality is configurable for all JPEG-based protocols.
        /// Only RGB565-only panels (ALi) don't use JPEG.
        /// </summary>
        public bool IsJpegQualityConfigurable
        {
            get
            {
                var pixelFormat = ModelInfo?.PixelFormat;
                return pixelFormat is null or ThermalrightPixelFormat.Jpeg;
            }
        }

        /// <summary>
        /// TrofeoBulk 5408 panels may need a flicker fix (crop JPEG to 462 rows).
        /// Show the toggle only for that model.
        /// </summary>
        public bool HasFlickerFix => Model == ThermalrightPanelModel.TrofeoVision916;

        /// <summary>
        /// Effective display height accounting for flicker fix crop.
        /// </summary>
        public int DisplayHeight => (FlickerFix && HasFlickerFix) ? 462 : (ModelInfo?.RenderHeight ?? 0);

        public int DisplayWidth => ModelInfo?.RenderWidth ?? 0;

        partial void OnFlickerFixChanged(bool value)
        {
            OnPropertyChanged(nameof(DisplayHeight));
        }

        public bool IsMatching(string deviceId, string deviceLocation, ThermalrightPanelModel model)
        {
            string matchRule = "None";
            bool matched = false;

            // priority 1: match by deviceId and location and model
            if (DeviceId.Equals(deviceId) && DeviceLocation.Equals(deviceLocation) && Model.Equals(model))
            {
                matchRule = "DeviceId+Location+Model";
                matched = true;
            }
            // priority 2: match by deviceId and port (location without hub) and model
            else
            {
                string port = deviceLocation.Split('.').FirstOrDefault() ?? string.Empty;
                if (DeviceId.Equals(deviceId) && DevicePort.Equals(port) && Model.Equals(model))
                {
                    matchRule = "DeviceId+Port+Model";
                    matched = true;
                }
                // priority 3: match by deviceId and port
                else if (DeviceId.Equals(deviceId) && DevicePort.Equals(port))
                {
                    matchRule = "DeviceId+Port";
                    matched = true;
                }
                // priority 4: match by deviceId and location
                else if (DeviceId.Equals(deviceId) && DeviceLocation.Equals(deviceLocation))
                {
                    matchRule = "DeviceId+Location";
                    matched = true;
                }
                // fallback: match by deviceId only
                else if (DeviceId.Equals(deviceId))
                {
                    matchRule = "DeviceId";
                    matched = true;
                }
            }

            Logger.Debug("ThermalrightPanel {Model} match result: {Matched}, Rule: {MatchRule}, DeviceId: {DeviceId}, Location: {Location}",
                model, matched, matchRule, DeviceId, deviceLocation);

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
            return DeviceLocation;
        }

        public partial class ThermalrightPanelDeviceRuntimeProperties : ObservableObject
        {
            [ObservableProperty]
            private bool _isRunning = false;

            [ObservableProperty]
            private string _name = "Thermalright Panel";

            [ObservableProperty]
            private int _frameRate = 0;

            [ObservableProperty]
            private long _frameTime = 0;

            [ObservableProperty]
            private string _errorMessage = string.Empty;

            [ObservableProperty]
            private string _serialNumber = string.Empty;
        }
    }
}
