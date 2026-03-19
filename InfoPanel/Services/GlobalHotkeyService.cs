using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace InfoPanel.Services
{
    public class GlobalHotkeyService
    {
        private static readonly ILogger Logger = Log.ForContext<GlobalHotkeyService>();

        public static GlobalHotkeyService Instance { get; } = new();

        private const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Win32 modifier flags (different from WPF ModifierKeys enum values)
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private HwndSource? _hwndSource;
        private readonly Dictionary<int, HotkeyBinding> _registeredHotkeys = new();
        private readonly HashSet<int> _failedHotkeyIds = new();
        private int _nextId = 1;
        private bool _started;

        public void Start()
        {
            if (_started) return;
            _started = true;

            Application.Current.Dispatcher.Invoke(() =>
            {
                // Create a hidden message-only window for receiving WM_HOTKEY
                var parameters = new HwndSourceParameters("InfoPanel_HotkeyWindow")
                {
                    Width = 0,
                    Height = 0,
                    WindowStyle = 0,
                    ParentWindow = new IntPtr(-3) // HWND_MESSAGE
                };
                _hwndSource = new HwndSource(parameters);
                _hwndSource.AddHook(WndProc);

                // Register all configured hotkeys
                var bindings = ConfigModel.Instance.Settings.HotkeyBindings;
                foreach (var binding in bindings)
                {
                    RegisterBinding(binding);
                }

                bindings.CollectionChanged += OnBindingsCollectionChanged;
            });

            Logger.Information("GlobalHotkeyService started with {Count} hotkeys", _registeredHotkeys.Count);
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;

            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ConfigModel.Instance.Settings.HotkeyBindings.CollectionChanged -= OnBindingsCollectionChanged;
                    UnregisterAll();
                    _hwndSource?.RemoveHook(WndProc);
                    _hwndSource?.Dispose();
                    _hwndSource = null;
                });
            }
            catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
            {
                Logger.Debug("Dispatcher shutdown during GlobalHotkeyService stop: {Message}", ex.Message);
            }

            Logger.Information("GlobalHotkeyService stopped");
        }

        public void RefreshAll()
        {
            if (!_started) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                UnregisterAll();
                foreach (var binding in ConfigModel.Instance.Settings.HotkeyBindings)
                {
                    RegisterBinding(binding);
                }
            });
        }

        private void OnBindingsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshAll();
        }

        private void RegisterBinding(HotkeyBinding binding)
        {
            if (_hwndSource == null || binding.Key == Key.None) return;

            // Require at least one modifier to avoid intercepting bare keys system-wide
            if (binding.ModifierKeys == ModifierKeys.None)
            {
                Logger.Warning("Skipping hotkey {Hotkey}: at least one modifier key (Ctrl, Alt, Shift, Win) is required",
                    binding.HotkeyDisplayText);
                return;
            }

            int id = _nextId++;
            uint modifiers = ToWin32Modifiers(binding.ModifierKeys) | MOD_NOREPEAT;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(binding.Key);

            if (RegisterHotKey(_hwndSource.Handle, id, modifiers, vk))
            {
                _registeredHotkeys[id] = binding;
                Logger.Debug("Registered hotkey {Id}: {Hotkey} -> {DeviceType} {DeviceId} -> Profile {Profile}",
                    id, binding.HotkeyDisplayText, binding.DeviceType, binding.DeviceId, binding.ProfileGuid);
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                _failedHotkeyIds.Add(id);
                Logger.Warning("Failed to register hotkey {Hotkey} (error {Error}). Key combination may be in use by another application.",
                    binding.HotkeyDisplayText, error);
            }
        }

        public bool IsBindingRegistered(HotkeyBinding binding)
        {
            return _registeredHotkeys.ContainsValue(binding);
        }

        private void UnregisterAll()
        {
            if (_hwndSource == null) return;

            foreach (var id in _registeredHotkeys.Keys)
            {
                UnregisterHotKey(_hwndSource.Handle, id);
            }
            _registeredHotkeys.Clear();
            _failedHotkeyIds.Clear();
            _nextId = 1;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (_registeredHotkeys.TryGetValue(id, out var binding))
                {
                    handled = true;
                    ApplyHotkey(binding);
                }
            }
            return IntPtr.Zero;
        }

        private void ApplyHotkey(HotkeyBinding binding)
        {
            Logger.Information("Hotkey {Hotkey} pressed: switching {DeviceType} {DeviceId} to profile {Profile}",
                binding.HotkeyDisplayText, binding.DeviceType, binding.DeviceId, binding.ProfileGuid);

            var profile = ConfigModel.Instance.GetProfile(binding.ProfileGuid);
            if (profile == null)
            {
                Logger.Warning("Hotkey target profile {Profile} not found", binding.ProfileGuid);
                return;
            }

            switch (binding.DeviceType)
            {
                case "Beada":
                    foreach (var device in ConfigModel.Instance.Settings.BeadaPanelDevices)
                    {
                        if (device.DeviceId == binding.DeviceId)
                        {
                            device.ProfileGuid = binding.ProfileGuid;
                            Logger.Information("Switched Beada device {Device} to profile {Profile}", device.DeviceId, profile.Name);
                            return;
                        }
                    }
                    break;

                case "Turing":
                    foreach (var device in ConfigModel.Instance.Settings.TuringPanelDevices)
                    {
                        if (device.DeviceId == binding.DeviceId)
                        {
                            device.ProfileGuid = binding.ProfileGuid;
                            Logger.Information("Switched Turing device {Device} to profile {Profile}", device.DeviceId, profile.Name);
                            return;
                        }
                    }
                    break;

                case "Thermalright":
                    foreach (var device in ConfigModel.Instance.Settings.ThermalrightPanelDevices)
                    {
                        if (device.DeviceId == binding.DeviceId)
                        {
                            device.ProfileGuid = binding.ProfileGuid;
                            Logger.Information("Switched Thermalright device {Device} to profile {Profile}", device.DeviceId, profile.Name);
                            return;
                        }
                    }
                    break;
            }

            Logger.Warning("Hotkey target device {DeviceType} {DeviceId} not found", binding.DeviceType, binding.DeviceId);
        }

        private static uint ToWin32Modifiers(ModifierKeys modifiers)
        {
            uint result = 0;
            if (modifiers.HasFlag(ModifierKeys.Alt)) result |= MOD_ALT;
            if (modifiers.HasFlag(ModifierKeys.Control)) result |= MOD_CONTROL;
            if (modifiers.HasFlag(ModifierKeys.Shift)) result |= MOD_SHIFT;
            if (modifiers.HasFlag(ModifierKeys.Windows)) result |= MOD_WIN;
            return result;
        }
    }
}
