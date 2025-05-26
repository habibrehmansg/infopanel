using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.ViewModels;
using InfoPanel.Views.Windows;
using SkiaSharp;
using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
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
        private Timer? UpdateTimer;

        public ObservableCollection<string> InstalledFonts { get; } = new ObservableCollection<string>();
        public ProfilesViewModel ViewModel { get; }

        public ProfilesPage(ProfilesViewModel viewModel, IDialogService dialogService, ISnackbarService snackbarService)
        {
            ViewModel = viewModel;
            DataContext = this;

            FetchInstalledFontNames();
            _dialogControl = dialogService.GetDialogControl();
            _snackbarControl = snackbarService.GetSnackbarControl();

            InitializeComponent();

            Loaded += ProfilesPage_Loaded;
            Unloaded += ProfilesPage_Unloaded;

            UpdateTimer = new Timer();
            UpdateTimer.Interval = 1000;
        }

        private void FetchInstalledFontNames()
        {
            InstalledFontCollection installedFonts = new InstalledFontCollection();
            foreach (var font in installedFonts.Families.Select(f => f.Name))
            {
                InstalledFonts.Add(font);
            }
        }

        private void ProfilesPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadPreviews();

            if (UpdateTimer != null)
            {
                UpdateTimer.Tick += Timer_Tick;
                UpdateTimer.Start();
            }
        }

        private void ProfilesPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (UpdateTimer != null)
            {
                UpdateTimer.Stop();
                UpdateTimer.Tick -= Timer_Tick;
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            LoadPreviews();
        }

        private static async void LoadPreviews()
        {
            var profiles = ConfigModel.Instance.GetProfilesCopy();

            await Parallel.ForEachAsync(profiles, async (profile, ct) =>
            {
                await Task.Run(() =>
                {
                    var scale = 1.0;

                    if (profile.Height > 150)
                    {
                        scale = 150f / profile.Height;
                    }

                    var width = (int)(profile.Width * scale);
                    var height = (int)(profile.Height * scale);

                    ////using var bitmap = new Bitmap(width, height);
                    ////bitmap.SetResolution(96, 96);
                    //var bitmap = new SKBitmap(width, height);

                    ////using var g = CompatGraphics.FromBitmap(bitmap);
                    //using var g = SkiaGraphics.FromBitmap(bitmap);
                    //PanelDraw.Run(profile, g, false, scale, false, true);

                    //profile.SkPreviewBitmap?.Dispose();
                    //profile.SkPreviewBitmap = bitmap;


                    //System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    //{
                    //    if (profile.BitmapImagePreview == null || profile.BitmapImagePreview.Width != bitmap.Width || profile.BitmapImagePreview.Height != bitmap.Height)
                    //    {
                    //        profile.BitmapImagePreview = new WriteableBitmap(bitmap.Width, bitmap.Height,
                    //                                      96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                    //    }

                    //    profile.BitmapImagePreview.Lock();
                    //    IntPtr backBuffer = profile.BitmapImagePreview.BackBuffer;

                    //    if (backBuffer == IntPtr.Zero)
                    //    {
                    //        return;
                    //    }

                    //    // copy the pixel data from the bitmap to the back buffer
                    //    BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    //    int stride = bitmapData.Stride;
                    //    byte[] pixels = new byte[stride * bitmap.Height];
                    //    Marshal.Copy(bitmapData.Scan0, pixels, 0, pixels.Length);
                    //    Marshal.Copy(pixels, 0, backBuffer, pixels.Length);
                    //    bitmap.UnlockBits(bitmapData);


                    //    profile.BitmapImagePreview?.AddDirtyRect(new Int32Rect(0, 0, profile.BitmapImagePreview.PixelWidth, profile.BitmapImagePreview.PixelHeight));
                    //    profile.BitmapImagePreview?.Unlock();
                    //});
                }, ct);
            });
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

        private async void ButtonVideoBackground_Click(object sender, RoutedEventArgs e)
        {
            if(ViewModel.Profile is Profile profile)
            {
                Microsoft.Win32.OpenFileDialog openFileDialog = new()
                {
                    Multiselect = false,
                    Filter = "Video files (*.mp4)|*.mp4",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
                };
                if (openFileDialog.ShowDialog() == true)
                {
                    var imageFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "assets", profile.Guid.ToString());
                    if (!Directory.Exists(imageFolder))
                    {
                        Directory.CreateDirectory(imageFolder);
                    }

                    try
                    {
                        var loadingWindow = new LoadingWindow
                        {
                            Owner = App.Current.MainWindow
                        };
                        loadingWindow.SetText("Processing video..");
                        loadingWindow.Show();

                        var videoFilePath = Path.Combine(imageFolder, openFileDialog.SafeFileName);
                        await VideoBackgroundHelper.GenerateMP4(openFileDialog.FileName, videoFilePath);

                        loadingWindow.SetText("Almost there..");

                        var webPFilePath = $"{videoFilePath}.webp";
                        await VideoBackgroundHelper.GenerateWebP(videoFilePath, webPFilePath);

                        loadingWindow.Close();
                        profile.VideoBackgroundFilePath = openFileDialog.SafeFileName;
                    }
                    catch
                    {

                    }

                }
            }
        }

        private void ButtonRemoveVideoBackground_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Profile is Profile profile)
            {
                profile.VideoBackgroundFilePath = null;
            }
        }
    }
}
