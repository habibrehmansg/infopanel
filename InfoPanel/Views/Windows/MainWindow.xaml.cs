using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
        private readonly ISnackbarService _snackbarService;

        // Capture-foreground-app hotkey (Ctrl+Alt+G)
        private const int WM_HOTKEY = 0x0312;
        private const int MOD_ALT = 0x0001;
        private const int MOD_CONTROL = 0x0002;
        private const int HOTKEY_ID_CAPTURE_FOREGROUND = 1;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public MainWindow(INavigationService navigationService, INavigationViewPageProvider pageProvider, ITaskBarService taskBarService, ISnackbarService snackbarService, IContentDialogService contentDialogService)
        {
            // Assign the view model
            //ViewModel = viewModel;
            DataContext = this;

            _contentDialogService = contentDialogService;
            _snackbarService = snackbarService;
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

            _snackbarService.SetSnackbarPresenter(RootSnackbar);
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
            if (e.OriginalSource is System.Windows.Controls.TextBox or
                System.Windows.Controls.Primitives.TextBoxBase)
                return;

            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    if (SharedModel.Instance.CanRedo)
                        SharedModel.Instance.Redo();
                }
                else
                {
                    if (SharedModel.Instance.CanUndo)
                        SharedModel.Instance.Undo();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Y && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (SharedModel.Instance.CanRedo)
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

            // Register Ctrl+Alt+G to capture foreground app for program-specific panels (always register so it works as soon as feature is enabled)
            try
            {
                RegisterHotKey(hwnd, HOTKEY_ID_CAPTURE_FOREGROUND, MOD_CONTROL | MOD_ALT, 0x47); // VK_G
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Could not register capture-foreground-app hotkey");
            }

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
            else if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID_CAPTURE_FOREGROUND)
            {
                Dispatcher.BeginInvoke(() => CaptureForegroundAppToTargetProfile());
                handled = true;
            }

            return IntPtr.Zero;
        }

        private void CaptureForegroundAppToTargetProfile()
        {
            var profile = Services.ProgramSpecificPanelsCaptureService.TargetProfile;
            if (profile == null)
            {
                _snackbarService.Show("Trigger app", "Select a profile on the Profiles page first, then press Ctrl+Alt+G while the app is in focus.", Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
                return;
            }
            var name = Utils.ForegroundWindowHelper.GetForegroundProcessName();
            if (string.IsNullOrWhiteSpace(name))
            {
                _snackbarService.Show("Trigger app", "Could not get foreground process.", Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(2));
                return;
            }
            var existing = profile.TriggerProcessNames?.Trim();
            profile.TriggerProcessNames = string.IsNullOrEmpty(existing) ? name : existing + ", " + name;
            _snackbarService.Show("Trigger app", $"Added '{name}' to {profile.Name}.", Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(2));
        }

        private void Window_ContentRendered(object sender, EventArgs e)
        {
            MinWidth = 900;
            MinHeight = 600;

            Navigate(typeof(Pages.HomePage));

            if (ConfigModel.Instance.Settings.StartMinimized && ConfigModel.Instance.Settings.MinimizeToTray)
            {
                Hide();
            }
        }


        private void RootNavigation_ItemInvoked(object sender, RoutedEventArgs e)
        {
            // Explicit navigation for Settings when the sidebar item is clicked (fallback if default navigation fails).
            var item = (e.OriginalSource as DependencyObject) != null ? FindNavigationViewItemAncestor(e.OriginalSource as DependencyObject) : null
                ?? RootNavigation.SelectedItem as Wpf.Ui.Controls.NavigationViewItem;
            if (item?.Tag is string tag && tag == "settings")
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        Navigate(typeof(Pages.SettingsPage));
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to navigate to Settings page");
                    }
                }, System.Windows.Threading.DispatcherPriority.Normal);
            }
        }

        private static Wpf.Ui.Controls.NavigationViewItem? FindNavigationViewItemAncestor(DependencyObject? current)
        {
            while (current != null)
            {
                if (current is Wpf.Ui.Controls.NavigationViewItem item)
                    return item;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
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
            try
            {
                UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID_CAPTURE_FOREGROUND);
            }
            catch { /* ignore */ }

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
