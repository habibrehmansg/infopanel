using InfoPanel.Models;
using InfoPanel.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for PluginProperties.xaml
    /// </summary>
    public partial class PluginProperties : UserControl
    {
        public static readonly DependencyProperty ItemProperty =
     DependencyProperty.Register("PluginDisplayModel", typeof(PluginViewModel), typeof(PluginProperties));

        public PluginViewModel PluginDisplayModel
        {
            get { return (PluginViewModel)GetValue(ItemProperty); }
            set { SetValue(ItemProperty, value); }
        }

        public PluginProperties()
        {
            InitializeComponent();
            Loaded += (_, _) => TryRefreshStopwatchHotkeyTexts();
            DataContextChanged += (_, _) => TryRefreshStopwatchHotkeyTexts();
        }

        private void NumberBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is NumberBox numBox
                && numBox.DataContext is PluginConfigPropertyViewModel vm
                && numBox.Value is double newValue
                && newValue != vm.NumericValue)
            {
                // Spinner click — NumberBox already updated Value, push to VM
                vm.NumericValue = newValue;
            }
        }

        private void TryRefreshStopwatchHotkeyTexts()
        {
            if (DataContext is not PluginViewModel vm || !vm.ShowStopwatchHotkeys)
                return;
            RefreshStopwatchHotkeyDisplayTexts();
        }

        private void RefreshStopwatchHotkeyDisplayTexts()
        {
            var s = ConfigModel.Instance.Settings;
            StopwatchHotkeyStartCapture.Text =
                FormatOptionalHotkeyChord(s.StopwatchHotkeyStartModifiers, s.StopwatchHotkeyStartKey);
            StopwatchHotkeyStopCapture.Text =
                FormatOptionalHotkeyChord(s.StopwatchHotkeyStopModifiers, s.StopwatchHotkeyStopKey);
            StopwatchHotkeyResetCapture.Text =
                FormatOptionalHotkeyChord(s.StopwatchHotkeyResetModifiers, s.StopwatchHotkeyResetKey);
        }

        private static string FormatOptionalHotkeyChord(ModifierKeys modifiers, Key key)
        {
            if (key == Key.None)
                return "Click and press a key combo (optional)";
            var hint = new HotkeyBinding { ModifierKeys = modifiers, Key = key };
            return hint.HotkeyDisplayText;
        }

        private void StopwatchHotkeyCapture_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBox tb)
                tb.Text = "Press a key combo...";
        }

        private void StopwatchHotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            if (sender is not System.Windows.Controls.TextBox tb || tb.Tag is not string slot)
                return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                return;

            var modifiers = Keyboard.Modifiers;
            if (modifiers == ModifierKeys.None)
            {
                var snackbar = App.GetService<ISnackbarService>();
                snackbar?.Show(
                    "Error",
                    "At least one modifier key (Ctrl, Alt, Shift, Win) is required.",
                    ControlAppearance.Caution,
                    null,
                    TimeSpan.FromSeconds(3));
                return;
            }

            ApplyStopwatchHotkeySlot(slot, modifiers, key);
            RefreshStopwatchHotkeyDisplayTexts();
        }

        private void ApplyStopwatchHotkeySlot(string slot, ModifierKeys modifiers, Key key)
        {
            var s = ConfigModel.Instance.Settings;
            switch (slot)
            {
                case "Start":
                    s.StopwatchHotkeyStartModifiers = modifiers;
                    s.StopwatchHotkeyStartKey = key;
                    break;
                case "Stop":
                    s.StopwatchHotkeyStopModifiers = modifiers;
                    s.StopwatchHotkeyStopKey = key;
                    break;
                case "Reset":
                    s.StopwatchHotkeyResetModifiers = modifiers;
                    s.StopwatchHotkeyResetKey = key;
                    break;
                default:
                    return;
            }

            _ = ConfigModel.Instance.SaveSettingsAsync();
        }

        private void StopwatchHotkeyClear_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Wpf.Ui.Controls.Button b || b.Tag is not string slot)
                return;

            var s = ConfigModel.Instance.Settings;
            switch (slot)
            {
                case "Start":
                    s.StopwatchHotkeyStartModifiers = ModifierKeys.None;
                    s.StopwatchHotkeyStartKey = Key.None;
                    break;
                case "Stop":
                    s.StopwatchHotkeyStopModifiers = ModifierKeys.None;
                    s.StopwatchHotkeyStopKey = Key.None;
                    break;
                case "Reset":
                    s.StopwatchHotkeyResetModifiers = ModifierKeys.None;
                    s.StopwatchHotkeyResetKey = Key.None;
                    break;
                default:
                    return;
            }

            _ = ConfigModel.Instance.SaveSettingsAsync();
            RefreshStopwatchHotkeyDisplayTexts();
        }
    }
}
