using InfoPanel.Drawing;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Mvvm.Contracts;
using Profile = InfoPanel.Models.Profile;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for ProfilePageItem.xaml
    /// </summary>
    public partial class ProfilePageItemView : UserControl
    {
        private readonly IDialogControl _dialogControl;
        private readonly ISnackbarControl _snackbarControl;

        private DispatcherTimer? timer;
        private TaskCompletionSource<bool>? _paintCompletionSource;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly SemaphoreSlim _updateSemaphore = new(1, 1);

        public ProfilePageItemView()
        {
            var dialogService = App.GetService<IDialogService>() ?? throw new InvalidOperationException("DialogService is not registered in the service collection.");
            _dialogControl = dialogService.GetDialogControl();

            var snackbarService = App.GetService<ISnackbarService>() ?? throw new InvalidOperationException("SnackbarService is not registered in the service collection.");
            _snackbarControl = snackbarService.GetSnackbarControl();

            InitializeComponent();

            Loaded += ProfilePageItemView_Loaded;
            Unloaded += ProfilePageItemView_Unloaded;
        }
        private async void ProfilePageItemView_Loaded(object sender, RoutedEventArgs e)
        {
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                await UpdateAsync(_cancellationTokenSource.Token);

                timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                timer.Tick += Timer_Tick;
                timer.Start();
            }
            catch (OperationCanceledException) { }
        }

        private void ProfilePageItemView_Unloaded(object sender, RoutedEventArgs e)
        {
            _cancellationTokenSource?.Cancel();

            if (timer != null)
            {
                timer.Stop();
                timer.Tick -= Timer_Tick;
                timer = null;
            }

            if(DataContext is Profile profile)
            {
                profile.PreviewBitmap?.Dispose();
                profile.PreviewBitmap = null;
            }

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }

        private async void Timer_Tick(object? sender, EventArgs e)
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    await UpdateAsync(_cancellationTokenSource.Token);
                }
                catch (OperationCanceledException) { }
            }
        }

        private async Task UpdateAsync(CancellationToken cancellationToken)
        {
            await _updateSemaphore.WaitAsync(cancellationToken);

            try
            {
                if (DataContext is Profile profile)
                {
                    var canvasWidth = skElement.CanvasSize.Width;
                    var canvasHeight = skElement.CanvasSize.Height;

                    var scale = 1.0;

                    if (profile.Height > canvasHeight)
                    {
                        scale = canvasHeight / profile.Height;
                    }

                    if (profile.Width > canvasWidth)
                    {
                        scale = Math.Min(scale, canvasWidth / profile.Width);
                    }

                    var width = (int)(profile.Width * scale);
                    var height = (int)(profile.Height * scale);

                    if (profile.PreviewBitmap != null && (profile.PreviewBitmap.Width != width || profile.PreviewBitmap.Height != height))
                    {
                        profile.PreviewBitmap.Dispose();
                        profile.PreviewBitmap = null;
                    }

                    profile.PreviewBitmap ??= new SKBitmap(width, height);

                    await Task.Run(() =>
                    {
                        using var g = SkiaGraphics.FromBitmap(profile.PreviewBitmap);
                        PanelDraw.Run(profile, g, false, scale, true, $"PREVIEW-{profile.Guid}");
                    }, cancellationToken);

                    _paintCompletionSource = new TaskCompletionSource<bool>();

                    skElement.InvalidateVisual();

                    await _paintCompletionSource.Task;
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _updateSemaphore.Release();
            }
        }

        private void skElement_PaintSurface(object sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            if (DataContext is Profile profile)
            {
                if (profile.PreviewBitmap is SKBitmap bitmap)
                {

                    //draw bitmap to center of canvas
                    var x = (e.Info.Width - bitmap.Width) / 2;
                    var y = (e.Info.Height - bitmap.Height) / 2;


                    e.Surface.Canvas.Clear();
                    e.Surface.Canvas.DrawBitmap(bitmap, x, y);
                }
            }

            _paintCompletionSource?.TrySetResult(true);
        }

        private async void ButtonDelete_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is Profile profile)
            {
                if (ConfigModel.Instance.Profiles.First() == profile)
                {
                    return;
                }

                var result = await _dialogControl.ShowAndWaitAsync("Confirm Deletion", "This will permanently delete the profile and all associated items.");

                if (result == IDialogControl.ButtonPressed.Left)
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

        private void ButtonExport_Click(object sender, RoutedEventArgs e)
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
                        _snackbarControl.ShowAsync("Profile Exported", $"{result}");
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
