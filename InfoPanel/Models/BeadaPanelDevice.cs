using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.BeadaPanel;
using InfoPanel.ViewModels;
using System;
using System.Windows.Threading;
using System.Xml.Serialization;

namespace InfoPanel.Models
{
    public partial class BeadaPanelDevice : ObservableObject
    {
        // Configuration properties
        [ObservableProperty]
        private string _deviceLocation = string.Empty;

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
        [property: XmlIgnore]
        private BeadaPanelDeviceRuntimeProperties _runtimeProperties;

        public BeadaPanelDevice()
        {
            _runtimeProperties = new();
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