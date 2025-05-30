using InfoPanel.Models;
using InfoPanel.ViewModels;
using SkiaSharp;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing.Text;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using Wpf.Ui.Common.Interfaces;
using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Mvvm.Contracts;

namespace InfoPanel.Views.Pages
{
    /// <summary>
    /// Interaction logic for ProfilesPage.xaml
    /// </summary>
    public partial class ProfilesPage : INavigableView<ProfilesViewModel>
    {
        private readonly IDialogControl _dialogControl;
        private readonly ISnackbarControl _snackbarControl;

        public ObservableCollection<string> InstalledFonts { get; } = [];
        public ProfilesViewModel ViewModel { get; }

        public ProfilesPage(ProfilesViewModel viewModel, IDialogService dialogService, ISnackbarService snackbarService)
        {
            ViewModel = viewModel;
            DataContext = this;

            LoadAllFonts();
            _dialogControl = dialogService.GetDialogControl();
            _snackbarControl = snackbarService.GetSnackbarControl();

            InitializeComponent();

            Loaded += ProfilesPage_Loaded;
            Unloaded += ProfilesPage_Unloaded;
        }

        private void LoadAllFonts()
        {
            var allFonts = SKFontManager.Default.GetFontFamilies()
                .OrderBy(f => f)
                .ToList();

            foreach (var font in allFonts)
            {
                InstalledFonts.Add(font);
            }
        }

        private void ProfilesPage_Loaded(object sender, RoutedEventArgs e)
        {
        }

        private void ProfilesPage_Unloaded(object sender, RoutedEventArgs e)
        {
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
                    await _snackbarControl.ShowAsync("Profile Imported", $"{openFileDialog.FileName}");
                }
                else if (openFileDialog.FileName.EndsWith(".sensorpanel") || openFileDialog.FileName.EndsWith(".rslcd"))
                {
                   await SharedModel.ImportSensorPanel(openFileDialog.FileName);
                   await _snackbarControl.ShowAsync("Profile Imported", $"{openFileDialog.FileName}");
                }
            }
        }

        private void ButtonSave_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if(ViewModel.Profile is Profile profile)
            {
                ConfigModel.Instance.SaveProfiles();
                SharedModel.Instance.SaveDisplayItems(profile);
                _snackbarControl.ShowAsync("Profile Saved", $"{profile.Name}");
            }
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

        private void ButtonClose_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.Profile = null;
        }
    }
}
