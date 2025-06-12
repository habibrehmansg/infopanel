using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.BeadaPanel;
using InfoPanel.ViewModels;
using Org.BouncyCastle.Bcpg.OpenPgp;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;

namespace InfoPanel.Models
{
    public partial class BeadaPanelDevice : ObservableObject
    {
        // Configuration properties
        [ObservableProperty]
        private string _deviceId = string.Empty;

        [ObservableProperty]
        private string _deviceLocation = string.Empty;

        [ObservableProperty]
        private string _model = string.Empty;

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

        // Runtime properties
        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private string _id = Guid.NewGuid().ToString();

        [ObservableProperty]
        [property: System.Xml.Serialization.XmlIgnore]
        private BeadaPanelDeviceRuntimeProperties _runtimeProperties;

        public BeadaPanelDevice()
        {
            _runtimeProperties = new();
        }

        public bool IsMatching(string deviceId, string deviceLocation, BeadaPanelInfo panelInfo)
        {
            // priority 1: match by serial
            if (!string.IsNullOrEmpty(panelInfo.SerialNumber) && DeviceId.EndsWith(panelInfo.SerialNumber)){
                Trace.WriteLine($"BeadaPanel {panelInfo.ModelInfo.Name} device matched by SerialNumber: {panelInfo.SerialNumber}");
                return true;
            }

            // priority 2: match by deviceId and location and model;
            if (DeviceId.Equals(deviceId) && DeviceLocation.Equals(deviceLocation) && Model.Equals(panelInfo.Model))
            {
                Trace.WriteLine($"BeadaPanel {panelInfo.ModelInfo.Name} device matched by DeviceId: {DeviceId} & DeviceLocation: {DeviceLocation} & model.");
                return true;
            }

            // priority 3: match by deviceId and port (location without hub) and model
            string port = deviceLocation.Split('.').FirstOrDefault() ?? string.Empty;
            if (DeviceId.Equals(deviceId) && DevicePort.Equals(port) && Model.Equals(panelInfo.Model))
            {
                Trace.WriteLine($"BeadaPanel {panelInfo.ModelInfo.Name} device matched by DeviceId: {DeviceId} & Port: {DevicePort} & model");
                return true;
            }

            // priority 4: match by deviceId and model
            if (DeviceId.Equals(deviceId) && DevicePort.Equals(port))
            {
                Trace.WriteLine($"BeadaPanel {panelInfo.ModelInfo.Name} device matched by DeviceId: {DeviceId} & Port: {DevicePort} & model");
                return true;
            }

            // priority 5: match by deviceId and location;
            if (DeviceId.Equals(deviceId) && DeviceLocation.Equals(deviceLocation) && Model.Equals(panelInfo.Model))
            {
                Trace.WriteLine($"BeadaPanel {panelInfo.ModelInfo.Name} device matched by DeviceId: {DeviceId} & DeviceLocation: {DeviceLocation}");
                return true;
            }

            // priority 6: match by deviceId and port (location without hub)
            if (DeviceId.Equals(deviceId) && DevicePort.Equals(port))
            {
                Trace.WriteLine($"BeadaPanel {panelInfo.ModelInfo.Name} device matched by DeviceId: {DeviceId} & Port: {DevicePort}");
                return true;
            }

            // fallback: match by deviceId only
            if (DeviceId.Equals(deviceId))
            {
                Trace.WriteLine($"BeadaPanel {panelInfo.ModelInfo.Name} device matched by DeviceId: {DeviceId}");
                return true;
            }

            Trace.WriteLine($"BeadaPanel device with DeviceId: {DeviceId} did not match.");
            return false;
        }

        private DateTime _lastUpdate = DateTime.MinValue;
        private readonly TimeSpan _throttleInterval = TimeSpan.FromSeconds(1);

        public void UpdateRuntimeProperties(bool? isRunning = null, BeadaPanelInfo? panelInfo = null, int? frameRate = null, long? frameTime = null, string? errorMessage = null)
        {
            var now = DateTime.UtcNow;

            // Always update critical properties immediately
            if (isRunning != null || panelInfo != null || errorMessage != null)
            {
                _lastUpdate = now;
                DispatchUpdate(isRunning, panelInfo, frameRate, frameTime, errorMessage);
                return;
            }

            // Throttle frequent updates (frameRate, frameTime)
            if (now - _lastUpdate < _throttleInterval)
            {
                return; // Skip this update
            }

            _lastUpdate = now;
            DispatchUpdate(isRunning, panelInfo, frameRate, frameTime, errorMessage);
        }

        private void DispatchUpdate(bool? isRunning, BeadaPanelInfo? panelInfo, int? frameRate, long? frameTime, string? errorMessage)
        {
            if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher)
            {
                dispatcher.BeginInvoke(() =>
                {
                    if (isRunning != null)
                    {
                        RuntimeProperties.IsRunning = isRunning.Value;
                    }

                    if (panelInfo != null)
                    {
                        RuntimeProperties.PanelInfo = panelInfo;
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

        public partial class BeadaPanelDeviceRuntimeProperties: ObservableObject
        {
            [ObservableProperty]
            private bool _isRunning = false;

            public string Name
            {
                get
                {
                    if(PanelInfo != null)
                    {
                        return $"BeadaPanel {PanelInfo.ModelInfo.Name}";
                    }

                    return "Device not detected";
                }
            }

            private BeadaPanelInfo? _panelInfo;
            public BeadaPanelInfo? PanelInfo
            {
                get { return _panelInfo; }
                set
                {
                    SetProperty(ref _panelInfo, value);
                    OnPropertyChanged(nameof(Name));
                }
            }

            [ObservableProperty]
            private int _frameRate = 0;

            [ObservableProperty]
            private long _frameTime = 0;

            [ObservableProperty]
            private string _errorMessage = string.Empty;
        }
    }
}