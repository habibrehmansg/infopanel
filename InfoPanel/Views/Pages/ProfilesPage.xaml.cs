using InfoPanel.Models;
using InfoPanel.Utils;
using InfoPanel.ViewModels;
using InfoPanel.Views.Components;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Controls;
using Wpf.Ui;

namespace InfoPanel.Views.Pages
{
    /// <summary>
    /// Interaction logic for ProfilesPage.xaml
    /// </summary>
    public partial class ProfilesPage : INavigableView<ProfilesViewModel>
    {
        private readonly IContentDialogService _contentDialogService;
        private readonly ISnackbarService _snackbarService;

        public ObservableCollection<string> InstalledFonts { get; } = [];
        public ProfilesViewModel ViewModel { get; }

        public ProfilesPage(ProfilesViewModel viewModel, IContentDialogService contentDialogService, ISnackbarService snackbarService)
        {
            ViewModel = viewModel;
            DataContext = this;

            _contentDialogService = contentDialogService;
            _snackbarService = snackbarService;

            InitializeComponent();

            Loaded += ProfilesPage_Loaded;
        }

        private async void ProfilesPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (InstalledFonts.Count == 0)
            {
                var fonts = await FontCache.GetFontsAsync();
                foreach (var font in fonts)
                {
                    InstalledFonts.Add(font);
                }
            }
        }

        private void ButtonAdd_Click(object sender, RoutedEventArgs e)
        {
            var profile = new Profile()
            {
                Name = "Profile " + (ConfigModel.Instance.Profiles.Count + 1)
            };
            ConfigModel.Instance.AddProfile(profile);
            ConfigModel.Instance.SaveProfiles();
            SharedModel.Instance.SaveDisplayItems(profile);
            ViewModel.Profile = profile;
            ListViewProfiles.ScrollIntoView(profile);
        }

        private async void ButtonImportProfile_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new()
            {
                Multiselect = false,
                Filter = "All Supported Files|*.infopanel;*.sensorpanel;*.rslcd|InfoPanel Files (*.infopanel)|*.infopanel|SensorPanel Files (*.sensorpanel)|*.sensorpanel|RemoteSensor LCD Files (*.rslcd)|*.rslcd",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
            };
            if (openFileDialog.ShowDialog() == true)
            {
                if (openFileDialog.FileName.EndsWith(".infopanel"))
                {
                    SharedModel.Instance.ImportProfile(openFileDialog.FileName);
                    _snackbarService.Show("Profile Imported", $"{openFileDialog.FileName}", ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
                }
                else if (openFileDialog.FileName.EndsWith(".sensorpanel") || openFileDialog.FileName.EndsWith(".rslcd"))
                {
                   await SharedModel.ImportSensorPanel(openFileDialog.FileName);
                   _snackbarService.Show("Profile Imported", $"{openFileDialog.FileName}", ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
                }
            }
        }

        private async void ButtonSave_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if(ViewModel.Profile is Profile profile)
            {
                ConfigModel.Instance.SaveProfiles();
                SharedModel.Instance.SaveDisplayItems(profile);
                _snackbarService.Show("Profile Saved", $"{profile.Name}", ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
            }
        }

        // ---- Display assignment dropdown ----
        // The ComboBox lives inside the profile DataTemplate, so its DataContext is the Profile
        // (exposed via Tag). We sync it manually rather than two-way binding SelectedItem because
        // the selectable objects (DisplayOption) are not the stored object (TargetWindow), and
        // because a profile's saved display may not be connected right now (leave nothing selected).

        private bool _suppressDisplaySelection;
        private Profile? _displayComboProfile;
        private System.Windows.Controls.ComboBox? _displayCombo;

        private void ComboBoxDisplay_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox combo)
                AttachDisplayCombo(combo, combo.DataContext as Profile);
        }

        private void ComboBoxDisplay_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // The detail ContentControl reuses the same visual tree when switching profiles,
            // so re-sync whenever the bound Profile changes.
            if (sender is System.Windows.Controls.ComboBox combo)
                AttachDisplayCombo(combo, e.NewValue as Profile);
        }

        private void AttachDisplayCombo(System.Windows.Controls.ComboBox combo, Profile? profile)
        {
            if (_displayComboProfile != null)
                _displayComboProfile.PropertyChanged -= DisplayComboProfile_PropertyChanged;

            _displayCombo = combo;
            _displayComboProfile = profile;

            if (profile != null)
                profile.PropertyChanged += DisplayComboProfile_PropertyChanged;

            SyncDisplayCombo();
        }

        private void DisplayComboProfile_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Keep the dropdown in step when the window is dragged onto another screen
            // (DisplayWindow writes Profile.TargetWindow) or reset via the button.
            if (e.PropertyName == nameof(Profile.TargetWindow))
                Dispatcher.BeginInvoke(SyncDisplayCombo);
        }

        private void SyncDisplayCombo()
        {
            if (_displayCombo == null) return;
            _suppressDisplaySelection = true;
            try
            {
                _displayCombo.SelectedItem = ViewModel.FindDisplayFor(_displayComboProfile?.TargetWindow);
            }
            finally
            {
                _suppressDisplaySelection = false;
            }
        }

        private void ComboBoxDisplay_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.ComboBox combo) return;
            if (_suppressDisplaySelection) return;
            if (combo.DataContext is not Profile profile) return;
            if (combo.SelectedItem is not DisplayOption option) return;

            var current = profile.TargetWindow;
            if (current != null
                && string.Equals(current.DeviceName, option.DeviceName, StringComparison.OrdinalIgnoreCase)
                && current.X == option.X && current.Y == option.Y
                && current.Width == option.Width && current.Height == option.Height)
            {
                return; // no change
            }

            // Assign the display and park the window at its top-left. DisplayWindow listens for
            // TargetWindow/WindowX/WindowY changes and repositions a running panel immediately.
            profile.TargetWindow = option.ToTargetWindow();
            profile.WindowX = 0;
            profile.WindowY = 0;
        }

        private void ButtonResetPosition_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var screen = Screen.PrimaryScreen;
            if (screen != null && ViewModel.Profile is Profile profile)
            {
                profile.TargetWindow = new TargetWindow(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height, screen.DeviceName);
                profile.WindowX = 0;
                profile.WindowY = 0;
            }
        }

        private void ButtonMaximise_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (System.Windows.Application.Current is App app && ViewModel.Profile is Profile profile)
            {
                app.MaximiseDisplayWindow(profile);
            }
        }

        private void ButtonReload_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Profile is Profile profile)
            {
                ConfigModel.Instance.ReloadProfile(ViewModel.Profile);
            }
        }

        private void ListViewProfiles_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ListViewProfiles.SelectedItem != null)
            {
                ProfileDetailOverlay.Visibility = Visibility.Visible;
            }
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.Profile = null;
            ProfileDetailOverlay.Visibility = Visibility.Collapsed;
        }

        private async void ButtonSelectFromList_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Profile is not Profile profile)
                return;

            var picker = new ProcessPickerControl();
            var dialog = new ContentDialog
            {
                Title = "Select running program",
                Content = picker,
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel",
                IsPrimaryButtonEnabled = false,
            };

            picker.SelectionChanged += (_, _) =>
                dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(picker.SelectedProcessName);
            picker.ItemActivated += (_, _) => dialog.Hide(ContentDialogResult.Primary);

            var result = await _contentDialogService.ShowAsync(dialog, CancellationToken.None);

            if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(picker.SelectedProcessName))
            {
                AppendTriggerProcessName(profile, picker.SelectedProcessName);
                _snackbarService.Show("Trigger app", $"Added '{picker.SelectedProcessName}' to {profile.Name}.", ControlAppearance.Success, null, TimeSpan.FromSeconds(2));
            }
        }

        private static void AppendTriggerProcessName(Profile profile, string name)
        {
            var existing = profile.TriggerProcessNames?.Trim();
            if (!string.IsNullOrEmpty(existing))
                profile.TriggerProcessNames = existing + ", " + name;
            else
                profile.TriggerProcessNames = name;
        }
    }
}
