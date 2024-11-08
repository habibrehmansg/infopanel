﻿using System.Reflection;
using System;
using System.Windows;
using Wpf.Ui.Mvvm.Contracts;
using System.Windows.Controls;
using Wpf.Ui.Controls.Interfaces;
using System.ComponentModel;

namespace InfoPanel.Views.Windows
{
    /// <summary>
    /// Interaction logic for FluentWindow.xaml
    /// </summary>
    public partial class FluentWindow: INavigationWindow
    {
        private readonly ITaskBarService _taskBarService;

        public FluentWindow(INavigationService navigationService, IPageService pageService, ITaskBarService taskBarService, ISnackbarService snackbarService, IDialogService dialogService)
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

            // Allows you to use the Snackbar control defined in this window in other pages or windows
            snackbarService.SetSnackbarControl(RootSnackbar);

            // Allows you to use the Dialog control defined in this window in other pages or windows
            dialogService.SetDialogControl(RootDialog);

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);

            if (version != null)
            {
                RootTitleBar.Title = $"InfoPanel - v{version}";
            }

            Loaded += FluentWindow_Loaded;
            StateChanged += FluentWindow_StateChanged;
        }

        private void FluentWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
               SharedModel.Instance.SelectedItem = null;
            }
        }

        private void FluentWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (ConfigModel.Instance.Settings.StartMinimized)
            {
                this.WindowState = WindowState.Minimized;
            }
        }

        private void Window_ContentRendered(object sender, EventArgs e)
        {
            MinWidth = ActualWidth;
            MinHeight = ActualHeight;
            MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
            MaxWidth = ActualWidth;

            Navigate(typeof(Pages.HomePage));

            if (ConfigModel.Instance.Settings.StartMinimized && ConfigModel.Instance.Settings.MinimizeToTray)
            {
                Hide();
            }
        }

        private void RootDialog_ButtonClick(object sender, RoutedEventArgs e)
        {
            var dialogControl = (IDialogControl)sender;
            dialogControl.Hide();
        }

        private void TrayMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Wpf.Ui.Controls.MenuItem menuItem)
                return;

            if(menuItem.Tag is string tag)
            {
                switch(tag)
                {
                    case "profiles":
                        RestoreWindow();
                        Navigate(typeof(Pages.ProfilesPage));
                        break;
                    case "design":
                        RestoreWindow();
                        Navigate(typeof(Pages.DesignPage));
                        break;
                    case "settings":
                        RestoreWindow();
                        Navigate(typeof(Pages.SettingsPage));
                        break;
                    case "about":
                        RestoreWindow();
                        Navigate(typeof(Pages.AboutPage));
                        break;
                    case "close":
                        Application.Current.Shutdown();
                        break;
                    default:
                        RestoreWindow();
                        break;
                }
            }

            System.Diagnostics.Debug.WriteLine($"DEBUG | WPF UI Tray clicked: {menuItem.Tag}", "Wpf.Ui.Demo");
        }

        private void RootTitleBar_OnNotifyIconClick(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"DEBUG | WPF UI Tray double clicked", "Wpf.Ui.Demo");


            this.RestoreWindow();
            //RootNavigation.Navigate(typeof(Pages.HomePage));
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
            => RootFrame;

        public INavigation GetNavigation()
            => RootNavigation;

        public bool Navigate(Type pageType)
            => RootNavigation.Navigate(pageType);

        public void SetPageService(IPageService pageService)
            => RootNavigation.PageService = pageService;

        public void ShowWindow()
            => Show();

        public void CloseWindow()
            => Close();

        #endregion INavigationWindow methods

        private void UiWindow_Closing(object sender, CancelEventArgs e)
        {
            PanelDrawTask.Instance.Stop();
            GraphDrawTask.Instance.Stop();
            BeadaPanelTask.Instance.Stop();

            Application.Current.Shutdown();
        }
    }
}
