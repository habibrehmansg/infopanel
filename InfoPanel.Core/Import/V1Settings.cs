using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using InfoPanel.Spec;

namespace InfoPanel.Import;

/// <summary>
/// Reads the user data of 1.x's <c>settings.xml</c> (Core has no 1.x settings model; 1.x's is WPF-typed): the frame and
/// graph rates, the Libre toggles, the program-specific panel toggles, the device list of the eight panel families, the
/// multi-device flags and the hotkeys, each as 1.4.x would use it. An absent element gives 1.4.x's default
/// (<c>Settings.cs</c>); a value that doesn't parse gives the default too, with an <c>import.setting</c> warning. Theme, UI
/// scale, window size and pane state are left out.
/// </summary>
internal static class V1SettingsReader
{
    /// <summary>1.4.x's default <c>Version</c>: what an absent element reads as, and what 1.1.x data carries.</summary>
    public const int DefaultVersion = 114;

    /// <summary>The device families: their name here, their list element and their multi-device element in 1.4.x.</summary>
    internal static readonly (string Family, string List, string MultiDevice)[] Families =
    [
        ("beada", "BeadaPanelDevices", "BeadaPanelMultiDeviceMode"),
        ("turing", "TuringPanelDevices", "TuringPanelMultiDeviceMode"),
        ("lianLi", "LianLiPanelDevices", "LianLiPanelMultiDeviceMode"),
        ("thermalright", "ThermalrightPanelDevices", "ThermalrightPanelMultiDeviceMode"),
        ("thermaltake", "ThermaltakePanelDevices", "ThermaltakePanelMultiDeviceMode"),
        ("jl", "JlPanelDevices", "JlPanelMultiDeviceMode"),
        ("jonsbo", "JonsboPanelDevices", "JonsboPanelMultiDeviceMode"),
        ("vmax", "VmaxPanelDevices", "VmaxPanelMultiDeviceMode"),
    ];

    /// <param name="root">The <c>Settings</c> element.</param>
    /// <param name="file">The file it came from, for diagnostics.</param>
    public static V1Settings Read(XElement root, string file, ImportLimits limits, ImportDiagnostics diagnostics)
    {
        void Warn(string path, string message, XObject? at) =>
            diagnostics.Add(ImportDiagnostics.Warning, "import.setting", path, message, file, at is null ? null : SafeXml.LineOf(at));

        int Int(string element, string path, int fallback)
        {
            if (root.Element(element) is not { } e)
                return fallback;
            try { return XmlConvert.ToInt32(e.Value.Trim()); }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                Warn(path, $"{element} '{Clip(e.Value)}' doesn't parse; 1.4.x's default, {fallback}, is used.", e);
                return fallback;
            }
        }

        bool Bool(string element, string path, bool fallback)
        {
            if (root.Element(element) is not { } e)
                return fallback;
            try { return XmlConvert.ToBoolean(e.Value.Trim()); }
            catch (FormatException)
            {
                Warn(path, $"{element} '{Clip(e.Value)}' doesn't parse; 1.4.x's default, {(fallback ? "true" : "false")}, is used.", e);
                return fallback;
            }
        }

        int? version = DefaultVersion;
        if (root.Element("Version") is { } versionElement)
        {
            try { version = XmlConvert.ToInt32(versionElement.Value.Trim()); }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                Warn("/settings/version", $"Version '{Clip(versionElement.Value)}' doesn't parse: which 1.x wrote this data can't be told.", versionElement);
                version = null;
            }
        }

        var devices = new List<V1Device>();
        var multiDevice = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (family, list, multi) in Families)
        {
            multiDevice[family] = Bool(multi, $"/settings/multiDevice/{family}", false);
            if (root.Element(list) is not { } listElement)
                continue;

            var read = 0;
            foreach (var device in listElement.Elements())
            {
                if (read == limits.DevicesPerFamily)
                {
                    Warn("/settings/devices", $"{list} has more than {limits.DevicesPerFamily} devices; the rest are left out.", device);
                    break;
                }
                read++;
                devices.Add(Device(family, device, limits, Warn));
            }
        }

        var hotkeys = new List<V1Hotkey>();
        if (root.Element("HotkeyBindings") is { } bindings)
        {
            foreach (var binding in bindings.Elements())
            {
                if (hotkeys.Count == limits.Hotkeys)
                {
                    Warn("/settings/hotkeys", $"There are more than {limits.Hotkeys} hotkeys; the rest are left out.", binding);
                    break;
                }
                if (Hotkey(binding, limits, Warn) is { } hotkey)
                    hotkeys.Add(hotkey);
            }
        }

        return new V1Settings
        {
            Version = version,
            TargetFrameRate = Int("TargetFrameRate", "/settings/targetFrameRate", 15),
            TargetGraphUpdateRate = Int("TargetGraphUpdateRate", "/settings/targetGraphUpdateRate", 1000),
            Libre = new V1LibreSettings
            {
                Enabled = Bool("LibreHardwareMonitor", "/settings/libre/enabled", true),
                Storage = Bool("LibreHardwareMonitorStorage", "/settings/libre/storage", true),
                StorageInterval = Int("LibreHardwareMonitorStorageInterval", "/settings/libre/storageInterval", 30),
            },
            ProgramSpecific = new V1ProgramSpecificSettings
            {
                Enabled = Bool("ProgramSpecificPanelsEnabled", "/settings/programSpecific/enabled", false),
                HideOthers = Bool("HideOtherProfilesWhenProgramSpecificShown", "/settings/programSpecific/hideOthers", true),
            },
            Devices = devices,
            MultiDevice = multiDevice,
            Hotkeys = hotkeys,
        };
    }

    private static V1Device Device(string family, XElement device, ImportLimits limits, Action<string, string, XObject?> warn)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in device.Elements().Where(e => !e.HasElements))
        {
            var name = field.Name.LocalName;
            if (fields.Count == limits.FieldsPerDevice)
            {
                warn("/settings/devices", $"A {family} device has more than {limits.FieldsPerDevice} fields; the rest are left out.", field);
                break;
            }
            if (field.Value.Length > limits.SettingValueLength)
            {
                warn("/settings/devices", $"A {family} device's {name} is longer than {limits.SettingValueLength} characters; it is left out.", field);
                continue;
            }
            fields.TryAdd(name, field.Value);
        }

        return new V1Device { Family = family, Profile = ProfileOf(fields.GetValueOrDefault("ProfileGuid")), Fields = fields };
    }

    private static V1Hotkey? Hotkey(XElement binding, ImportLimits limits, Action<string, string, XObject?> warn)
    {
        string? Value(string name, string fallback)
        {
            var value = binding.Element(name)?.Value ?? fallback;
            if (value.Length <= limits.SettingValueLength)
                return value;
            warn("/settings/hotkeys", $"A hotkey's {name} is longer than {limits.SettingValueLength} characters; the hotkey is left out.", binding);
            return null;
        }

        var modifiers = Value("ModifierKeys", "None");
        var key = Value("Key", "None");
        var deviceType = Value("DeviceType", "");
        var deviceId = Value("DeviceId", "");
        var deviceLocation = Value("DeviceLocation", "");
        var profile = Value("ProfileGuid", "");
        if (modifiers is null || key is null || deviceType is null || deviceId is null || deviceLocation is null || profile is null)
            return null;
        return new V1Hotkey
        {
            Modifiers = modifiers,
            Key = key,
            DeviceType = deviceType,
            DeviceId = deviceId,
            DeviceLocation = deviceLocation,
            Profile = ProfileOf(profile),
        };
    }

    /// <summary>A 1.x profile reference: the Guid in lower case, or null for none (empty, the empty Guid, or not a Guid).</summary>
    private static string? ProfileOf(string? value) =>
        Guid.TryParse(value?.Trim(), out var guid) && guid != Guid.Empty ? guid.ToString() : null;

    private static string Clip(string value) => value.Length <= 64 ? value : value[..64] + "…";
}
