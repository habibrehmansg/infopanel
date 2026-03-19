using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.BeadaPanel;
using System;
using System.Linq;
using System.Windows.Input;
using System.Xml.Serialization;

namespace InfoPanel.Models
{
    public partial class HotkeyBinding : ObservableObject
    {
        [ObservableProperty]
        private ModifierKeys _modifierKeys = ModifierKeys.None;

        [ObservableProperty]
        private Key _key = Key.None;

        /// <summary>
        /// Device type: "Beada", "Turing", or "Thermalright"
        /// </summary>
        [ObservableProperty]
        private string _deviceType = string.Empty;

        /// <summary>
        /// Stable device identifier (persists across restarts).
        /// </summary>
        [ObservableProperty]
        private string _deviceId = string.Empty;

        /// <summary>
        /// Device location (used with DeviceId to disambiguate Beada/Thermalright devices).
        /// </summary>
        [ObservableProperty]
        private string _deviceLocation = string.Empty;

        /// <summary>
        /// Target profile to switch to when the hotkey is pressed.
        /// </summary>
        [ObservableProperty]
        private Guid _profileGuid = Guid.Empty;

        [XmlIgnore]
        public string HotkeyDisplayText
        {
            get
            {
                if (Key == Key.None) return "(not set)";
                var parts = new System.Collections.Generic.List<string>();
                if (ModifierKeys.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
                if (ModifierKeys.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
                if (ModifierKeys.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
                if (ModifierKeys.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
                parts.Add(Key.ToString());
                return string.Join("+", parts);
            }
        }

        [XmlIgnore]
        public string ProfileDisplayName
        {
            get
            {
                var profile = ConfigModel.Instance.Profiles.FirstOrDefault(p => p.Guid == ProfileGuid);
                return profile?.Name ?? ProfileGuid.ToString("D")[..8];
            }
        }

        [XmlIgnore]
        public string DeviceDisplayName
        {
            get
            {
                return DeviceType switch
                {
                    "Beada" => ConfigModel.Instance.Settings.BeadaPanelDevices
                        .Where(d => d.DeviceId == DeviceId)
                        .Select(d =>
                        {
                            var name = d.RuntimeProperties?.PanelInfo?.ModelInfo?.Name;
                            if (name == null && Enum.TryParse<BeadaPanelModel>(d.Model, out var model)
                                && BeadaPanelModelDatabase.Models.TryGetValue(model, out var info))
                            {
                                name = info.Name;
                            }
                            return $"BeadaPanel {name ?? d.Model}";
                        })
                        .FirstOrDefault() ?? DeviceId,
                    "Turing" => ConfigModel.Instance.Settings.TuringPanelDevices
                        .Where(d => d.DeviceId == DeviceId)
                        .Select(d => d.Name ?? d.DeviceId)
                        .FirstOrDefault() ?? DeviceId,
                    "Thermalright" => ConfigModel.Instance.Settings.ThermalrightPanelDevices
                        .Where(d => d.DeviceId == DeviceId)
                        .Select(d => d.RuntimeProperties?.Name ?? d.DeviceId)
                        .FirstOrDefault() ?? DeviceId,
                    _ => DeviceId
                };
            }
        }

        partial void OnModifierKeysChanged(ModifierKeys value) => OnPropertyChanged(nameof(HotkeyDisplayText));
        partial void OnKeyChanged(Key value) => OnPropertyChanged(nameof(HotkeyDisplayText));
        partial void OnProfileGuidChanged(Guid value) => OnPropertyChanged(nameof(ProfileDisplayName));
        partial void OnDeviceIdChanged(string value) => OnPropertyChanged(nameof(DeviceDisplayName));
        partial void OnDeviceTypeChanged(string value) => OnPropertyChanged(nameof(DeviceDisplayName));
    }
}
