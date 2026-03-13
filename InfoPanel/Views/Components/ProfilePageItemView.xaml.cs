using InfoPanel.Services;
using SkiaSharp;
using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Wpf.Ui;
using Profile = InfoPanel.Models.Profile;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for ProfilePageItem.xaml
    /// </summary>
    public partial class ProfilePageItemView : UserControl
    {
        private readonly IContentDialogService _contentDialogService;
        private readonly ISnackbarService _snackbarService;

        public ProfilePageItemView()
        {
            _contentDialogService = App.GetService<IContentDialogService>() ?? throw new InvalidOperationException("ContentDialogService is not registered in the service collection.");
            _snackbarService = App.GetService<ISnackbarService>() ?? throw new InvalidOperationException("SnackbarService is not registered in the service collection.");

            InitializeComponent();

            Loaded += ProfilePageItemView_Loaded;
            Unloaded += ProfilePageItemView_Unloaded;
        }

        private void ProfilePageItemView_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is Profile profile)
            {
                var scrollViewer = FindParentScrollViewer(this);
                ProfilePreviewCoordinator.Instance.Register(profile, skElement, scrollViewer);
            }
        }

        private void ProfilePageItemView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is Profile profile)
            {
                ProfilePreviewCoordinator.Instance.Unregister(profile);

                profile.PreviewBitmap?.Dispose();
                profile.PreviewBitmap = null;
            }
        }

        private void skElement_PaintSurface(object sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            if (DataContext is Profile profile)
            {
                if (profile.PreviewBitmap is SKBitmap bitmap)
                {
                    var x = (e.Info.Width - bitmap.Width) / 2;
                    var y = (e.Info.Height - bitmap.Height) / 2;

                    e.Surface.Canvas.Clear();
                    e.Surface.Canvas.DrawBitmap(bitmap, x, y);
                }

                ProfilePreviewCoordinator.Instance.CompletePaint(profile);
            }
        }

        private static ScrollViewer? FindParentScrollViewer(DependencyObject child)
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is ScrollViewer scrollViewer)
                    return scrollViewer;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        private async void ButtonDelete_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is Profile profile)
            {
                if(ConfigModel.Instance.Profiles.Count <= 1)
                {
                    _snackbarService.Show("Cannot Delete Profile", "At least one profile must remain.", ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
                    return;
                }

                var dialog = new ContentDialog
                {
                    Title = "Confirm Deletion",
                    Content = "This will permanently delete the profile and all associated items.",
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel"
                };

                var result = await _contentDialogService.ShowAsync(dialog, CancellationToken.None);

                if (result == ContentDialogResult.Primary)
                {
                    if (ConfigModel.Instance.RemoveProfile(profile))
                    {
                        var newSelectedProfile = ConfigModel.Instance.Profiles.FirstOrDefault(profile => { return profile.Active; }, ConfigModel.Instance.Profiles[0]);
                        SharedModel.Instance.SelectedProfile = newSelectedProfile;
                        ConfigModel.Instance.SaveProfiles();
                    }
                }
            }
        }

        private async void ButtonExport_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is Profile profile)
            {
                Microsoft.Win32.OpenFolderDialog openFolderDialog = new();

                if (openFolderDialog.ShowDialog() == true)
                {
                    string selectedFolderPath = openFolderDialog.FolderName;
                    string? result = SharedModel.Instance.ExportProfile(profile, selectedFolderPath);
                    if (result != null)
                    {
                        _snackbarService.Show("Profile Exported", $"{result}", ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
                    }
                }
            }
        }

        private void ToggleSwitch_Checked(object sender, RoutedEventArgs e)
        {
            ConfigModel.Instance.SaveProfiles();
        }
    }
}
