


using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using InfoPanel.ViewModels;
using InfoPanel.Views.Components;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
using System.Windows.Threading;
using Wpf.Ui.Common.Interfaces;
using Wpf.Ui.Controls;
using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Mvvm.Contracts;
using Wpf.Ui.Mvvm.Interfaces;

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

        private static ConcurrentDictionary<Guid, Bitmap> BitmapCache = new ConcurrentDictionary<Guid, Bitmap>();
        public ObservableCollection<string> InstalledFonts { get; } = new ObservableCollection<string>();
        public ProfilesViewModel ViewModel { get; }

        public ProfilesPage(ProfilesViewModel viewModel, IDialogService dialogService, ISnackbarService snackbarService)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
            FetchInstalledFontNames();
            _dialogControl = dialogService.GetDialogControl();
            _snackbarControl = snackbarService.GetSnackbarControl();

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
            //todo investigate lockup
            //Parallel.ForEach(ConfigModel.Instance.GetProfilesCopy(), profile =>
            //{
            //    LockedBitmap? lockedBitmap = PanelDrawTask.Render(profile, 0, 30, false);
            //});

            foreach (var profile in ConfigModel.Instance.GetProfilesCopy())
            {
                await Task.Run(() =>
                {
                    LockedBitmap? lockedBitmap = PanelDrawTask.Render(profile, 0, 30, false);
                    if (lockedBitmap != null)
                    {
                        lockedBitmap.Access(bitmap =>
                        {
                            IntPtr backBuffer = IntPtr.Zero;

                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                if (profile.BitmapImage == null || profile.BitmapImage.Width != bitmap.Width || profile.BitmapImage.Height != bitmap.Height)
                                {
                                    profile.BitmapImage = new WriteableBitmap(bitmap.Width, bitmap.Height,
                                                                  96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                                }

                                profile.BitmapImage.Lock();
                                backBuffer = profile.BitmapImage.BackBuffer;
                            });

                            if (backBuffer == IntPtr.Zero)
                            {
                                return;
                            }

                            // copy the pixel data from the bitmap to the back buffer
                            BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                            int stride = bitmapData.Stride;
                            byte[] pixels = new byte[stride * bitmap.Height];
                            Marshal.Copy(bitmapData.Scan0, pixels, 0, pixels.Length);
                            Marshal.Copy(pixels, 0, backBuffer, pixels.Length);
                            bitmap.UnlockBits(bitmapData);

                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                profile.BitmapImage.AddDirtyRect(new Int32Rect(0, 0, profile.BitmapImage.PixelWidth, profile.BitmapImage.PixelHeight));
                                profile.BitmapImage.Unlock();
                            });
                        });
                    }
                });


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

        private void ButtonImportProfile_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new()
            {
                Multiselect = false,
                Filter = "Infopanel theme file (*.infopanel)|*.infopanel",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
            };
            if (openFileDialog.ShowDialog() == true)
            {
                SharedModel.Instance.ImportProfile(openFileDialog.FileName);
                _snackbarControl.ShowAsync("Profile Imported", $"{openFileDialog.FileName}");
            }
        }

        private void ButtonSave_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            ConfigModel.Instance.SaveProfiles();
            _snackbarControl.ShowAsync("Profile Saved", $"{ViewModel.Profile.Name}");
        }

        private void ButtonExportProfile_Click(object sender, RoutedEventArgs e)
        {
            FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
            if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
            {
                string selectedFolderPath = folderBrowserDialog.SelectedPath;
                string? result = SharedModel.Instance.ExportProfile(ViewModel.Profile, selectedFolderPath);
                if (result != null)
                {
                    _snackbarControl.ShowAsync("Profile Exported", $"{result}");
                }
            }
        }

        private async void ButtonDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (ConfigModel.Instance.Profiles.First() == ViewModel.Profile)
            {
                return;
            }

            var result = await _dialogControl.ShowAndWaitAsync("Confirm Deletion", "This will permanently delete the profile and all associated items.");

            if (result == IDialogControl.ButtonPressed.Left)
            {
                if (ConfigModel.Instance.RemoveProfile(ViewModel.Profile))
                {
                    var newSelectedProfile = ConfigModel.Instance.Profiles.FirstOrDefault(profile => { return profile.Active; }, ConfigModel.Instance.Profiles[0]);
                    SharedModel.Instance.SelectedProfile = newSelectedProfile;
                    ConfigModel.Instance.SaveProfiles();
                }
            }
        }

        private void ButtonResetPosition_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var screen = Screen.PrimaryScreen;
            ViewModel.Profile.TargetWindow = new TargetWindow(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
            ViewModel.Profile.WindowX = 0;
            ViewModel.Profile.WindowY = 0;
        }

        private void ButtonMaximise_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (System.Windows.Application.Current is App app)
            {
                app.MaximiseDisplayWindow(ViewModel.Profile);
            }
        }

        private void ButtonReload_Click(object sender, RoutedEventArgs e)
        {
            ConfigModel.Instance.ReloadProfile(ViewModel.Profile);
        }

        private void ToggleSwitch_Checked(object sender, RoutedEventArgs e)
        {
            ConfigModel.Instance.SaveProfiles();
        }
    }
}
