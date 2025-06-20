using System.Reflection;
using System;
using System.Windows;
using Wpf.Ui;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using System.ComponentModel;
using System.Threading.Tasks;
using InfoPanel.Utils;

namespace InfoPanel.Views.Windows
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow: FluentWindow, INavigationWindow
    {
        private readonly ITaskBarService _taskBarService;

        public MainWindow(INavigationService navigationService, IPageService pageService, ITaskBarService taskBarService, ISnackbarService snackbarService, IContentDialogService contentDialogService)
        {
            // Assign the view model
            //ViewModel = viewModel;
            DataContext = this;

            // Attach the taskbar service
            _taskBarService = taskBarService;

            InitializeComponent();

            // We define a page provider for navigation
            SetPageService(pageService);

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
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (ConfigModel.Instance.Settings.StartMinimized)
            {
                this.WindowState = WindowState.Minimized;
            }
        }

        private void Window_ContentRendered(object sender, EventArgs e)
        {
            MinWidth = 1300;
            MinHeight = 900;

            Navigate(typeof(Pages.HomePage));

            if (ConfigModel.Instance.Settings.StartMinimized && ConfigModel.Instance.Settings.MinimizeToTray)
            {
                Hide();
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

        public Frame GetFrame()
        {
            // In WPF-UI v3, NavigationView manages its own internal frame
            // We need to return a Frame for compatibility, so we'll create one if needed
            if (_navigationFrame == null)
            {
                _navigationFrame = new Frame();
            }
            return _navigationFrame;
        }
        
        private Frame? _navigationFrame;

        public INavigationView GetNavigation()
            => RootNavigation;

        public bool Navigate(Type pageType)
            => RootNavigation.Navigate(pageType) != null;

        public void SetPageService(IPageService pageService)
            => RootNavigation.SetPageService(pageService);

        public void ShowWindow()
            => Show();

        public void CloseWindow()
            => Close();

        public void SetServiceProvider(IServiceProvider serviceProvider)
        {
            // This is used to provide services to the navigation window
            // Implementation depends on your specific needs
        }

        #endregion INavigationWindow methods

        private async void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
            //perform shutdown operations here

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
