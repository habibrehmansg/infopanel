using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using InfoPanel.Models;
using InfoPanel.Utils;
using Serilog;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace InfoPanel.Views.Windows
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow: FluentWindow, INavigationWindow
    {
        private static readonly ILogger Logger = Log.ForContext<MainWindow>();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeFilterStruct);

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        private const uint MSGFLT_ALLOW = 1;

        private readonly ITaskBarService _taskBarService;
        private uint _taskbarCreatedMessageId;
        private HwndSource? _hwndSource;
        private bool _isExiting;
        private readonly IContentDialogService _contentDialogService;

        public MainWindow(INavigationService navigationService, INavigationViewPageProvider pageProvider, ITaskBarService taskBarService, ISnackbarService snackbarService, IContentDialogService contentDialogService)
        {
            // Assign the view model
            //ViewModel = viewModel;
            DataContext = this;

            _contentDialogService = contentDialogService;
            // Attach the taskbar service
            _taskBarService = taskBarService;

            InitializeComponent();

            // Apply saved theme after InitializeComponent so it overrides the
            // ThemesDictionary default from App.xaml.
            var savedTheme = ConfigModel.Instance.Settings.AppTheme switch
            {
                1 => ApplicationTheme.Dark,
                _ => ApplicationTheme.Light
            };
            ApplicationThemeManager.Apply(savedTheme, WindowBackdropType.Mica, true);
            App.SyncMahAppsTheme(savedTheme);

            ApplicationThemeManager.Changed += (theme, _) =>
            {
                App.SyncMahAppsTheme(theme);
            };

            // We define a page provider for navigation
            SetPageService(pageProvider);

            // If you want to use INavigationService instead of INavigationWindow you can define its navigation here.
            navigationService.SetNavigationControl(RootNavigation);

            snackbarService.SetSnackbarPresenter(RootSnackbar);
            contentDialogService.SetDialogHost(RootContentDialog);

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);

            if (version != null)
            {
                RootTitleBar.Title = $"InfoPanel - v{version}";
            }

            Loaded += MainWindow_Loaded;
            StateChanged += MainWindow_StateChanged;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            SizeChanged += MainWindow_SizeChanged;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            var desiredHeight = screenHeight * 0.80;
            if (desiredHeight > MinHeight)
            {
                Height = desiredHeight;
            }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                SharedModel.Instance.SelectedItem = null;

                if (ConfigModel.Instance.Settings.MinimizeToTray)
                {
                    Hide();
                }

                // Trim working set — releases physical pages the OS can reclaim
                SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    SharedModel.Instance.Redo();
                else
                    SharedModel.Instance.Undo();
                e.Handled = true;
            }
            else if (e.Key == Key.Y && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                SharedModel.Instance.Redo();
                e.Handled = true;
            }
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (WindowState != WindowState.Normal)
                return;

            if (e.NewSize.Width >= MinWidth && e.NewSize.Height >= MinHeight)
            {
                ConfigModel.Instance.Settings.UiWidth = (float)e.NewSize.Width;
                ConfigModel.Instance.Settings.UiHeight = (float)e.NewSize.Height;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _taskbarCreatedMessageId = RegisterWindowMessage("TaskbarCreated");
            var hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);

            // Allow the TaskbarCreated message through UIPI since we run elevated
            if (_taskbarCreatedMessageId != 0)
            {
                ChangeWindowMessageFilterEx(hwnd, _taskbarCreatedMessageId, MSGFLT_ALLOW, IntPtr.Zero);
            }

            SystemThemeWatcher.Watch(this);

            if (ConfigModel.Instance.Settings.StartMinimized)
            {
                this.WindowState = WindowState.Minimized;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_taskbarCreatedMessageId != 0 && msg == (int)_taskbarCreatedMessageId)
            {
                Logger.Information("Taskbar recreated (explorer.exe restarted), re-registering tray icon");
                try
                {
                    TrayIcon.Unregister();
                    TrayIcon.Register();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to re-register tray icon after taskbar recreation");
                }

                handled = false;
            }

            return IntPtr.Zero;
        }

        private async void Window_ContentRendered(object sender, EventArgs e)
        {
            MinWidth = 900;
            MinHeight = 600;

            Navigate(typeof(Pages.HomePage));

            if (ConfigModel.Instance.Settings.StartMinimized && ConfigModel.Instance.Settings.MinimizeToTray)
            {
                Hide();
            }

            // Offer to restore from autosave if a backup exists
            var profilesWithBackup = InfoPanel.ConfigModel.Instance.GetProfilesWithAutosaveBackup();
            if (profilesWithBackup.Count == 0)
                return;

            // Bring main window to foreground so the restore dialog is visible when app started minimized to tray
            RestoreWindow();
            Activate();

            if (profilesWithBackup.Count == 1)
            {
                var profileToRestore = profilesWithBackup[0];
                var result = System.Windows.MessageBox.Show(
                    $"An autosave backup was found for \"{profileToRestore.Name}\". Restore from autosave?",
                    "Restore autosave",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question,
                    System.Windows.MessageBoxResult.Yes);
                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    if (InfoPanel.ConfigModel.Instance.RestoreProfileFromAutosave(profileToRestore))
                    {
                        InfoPanel.SharedModel.Instance.SelectedProfile = profileToRestore;
                        InfoPanel.ConfigModel.Instance.DiscardAutosaveBackup(profileToRestore);
                    }
                }
                else
                {
                    InfoPanel.ConfigModel.Instance.DiscardAutosaveBackup(profileToRestore);
                }
                return;
            }

            // Multiple profiles with backups: single dialog with multi-select
            var selection = new List<(Profile Profile, CheckBox CheckBox)>();
            var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var profile in profilesWithBackup)
            {
                var cb = new CheckBox
                {
                    Content = profile.Name,
                    IsChecked = true,
                    Tag = profile,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                selection.Add((profile, cb));
                stack.Children.Add(cb);
            }
            var dialog = new ContentDialog
            {
                Title = "Restore from autosave",
                Content = stack,
                PrimaryButtonText = "Restore selected",
                SecondaryButtonText = "Don't restore",
                CloseButtonText = "Cancel"
            };
            var dialogResult = await _contentDialogService.ShowAsync(dialog, CancellationToken.None);
            if (dialogResult == ContentDialogResult.Primary)
            {
                Profile? firstRestored = null;
                foreach (var (profile, checkBox) in selection)
                {
                    if (checkBox.IsChecked == true && InfoPanel.ConfigModel.Instance.RestoreProfileFromAutosave(profile))
                    {
                        firstRestored ??= profile;
                        InfoPanel.ConfigModel.Instance.DiscardAutosaveBackup(profile);
                    }
                    else
                        InfoPanel.ConfigModel.Instance.DiscardAutosaveBackup(profile);
                }
                if (firstRestored != null)
                    InfoPanel.SharedModel.Instance.SelectedProfile = firstRestored;
            }
            else
            {
                foreach (var p in profilesWithBackup)
                    InfoPanel.ConfigModel.Instance.DiscardAutosaveBackup(p);
            }
        }


        private void TrayMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.MenuItem menuItem)
                return;

            if(menuItem.Tag is string tag)
            {
                switch(tag)
                {
                    case "open":
                        RestoreWindow();
                        break;
                    case "profiles":
                        RestoreWindow();
                        Navigate(typeof(Pages.ProfilesPage));
                        break;
                    case "design":
                        RestoreWindow();
                        Navigate(typeof(Pages.DesignPage));
                        break;
                    case "plugins":
                        RestoreWindow();
                        Navigate(typeof(Pages.PluginsPage));
                        break;
                    case "usb":
                        RestoreWindow();
                        Navigate(typeof(Pages.UsbPanelsPage));
                        break;
                    case "settings":
                        RestoreWindow();
                        Navigate(typeof(Pages.SettingsPage));
                        break;
                    case "updates":
                        RestoreWindow();
                        Navigate(typeof(Pages.UpdatesPage));
                        break;
                    case "about":
                        RestoreWindow();
                        Navigate(typeof(Pages.AboutPage));
                        break;
                    case "close":
                        _isExiting = true;
                        Close();
                        break;
                    default:
                        RestoreWindow();
                        break;
                }
            }

            System.Diagnostics.Debug.WriteLine($"DEBUG | WPF UI Tray clicked: {menuItem.Tag}", "Wpf.Ui.Demo");
        }

        private void TrayIcon_LeftClick(object sender, RoutedEventArgs e)
        {
            RestoreWindow();
        }

        public void RestoreWindow()
        {
            if (WindowState != WindowState.Minimized && Visibility == Visibility.Visible)
                return;
            Show();
            WindowState = WindowState.Normal;
        }

        #region INavigationWindow methods

        public INavigationView GetNavigation()
            => RootNavigation;

        public bool Navigate(Type pageType)
            => RootNavigation.Navigate(pageType);

        public void SetPageService(INavigationViewPageProvider pageProvider)
            => RootNavigation.SetPageProviderService(pageProvider);

        public void ShowWindow()
            => Show();

        public void CloseWindow()
            => Close();

        public void SetServiceProvider(IServiceProvider serviceProvider)
        {
        }

        #endregion INavigationWindow methods

        private async void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!_isExiting && ConfigModel.Instance.Settings.CloseToMinimize)
            {
                e.Cancel = true;
                WindowState = WindowState.Minimized;
                return;
            }

            _hwndSource?.RemoveHook(WndProc);

            var isDirty = InfoPanel.SharedModel.Instance.IsDirty;
            if (isDirty)
            {
                var result = System.Windows.MessageBox.Show(
                    "You have unsaved changes. Do you want to save before closing?",
                    "Unsaved changes",
                    System.Windows.MessageBoxButton.YesNoCancel,
                    System.Windows.MessageBoxImage.Question,
                    System.Windows.MessageBoxResult.Yes);

                if (result == System.Windows.MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    InfoPanel.ConfigModel.Instance.SaveProfiles();
                    InfoPanel.SharedModel.Instance.SaveDisplayItems();
                }
            }

            e.Cancel = true;

            if (WindowState != WindowState.Minimized)
            {
                var loadingWindow = new LoadingWindow
                {
                    Owner = this
                };
                loadingWindow.SetText("Cleaning up..");
                loadingWindow.Show();
            }

            await FileUtil.CleanupAssets();
            await App.CleanShutDown();
        }
    }
}
