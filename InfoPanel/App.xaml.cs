using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.Plugins.Loader;
using InfoPanel.Services;
using InfoPanel.Utils;
using InfoPanel.ViewModels;
using InfoPanel.Views;
using InfoPanel.Views.Common;
using InfoPanel.Views.Components;
using InfoPanel.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using Sentry;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Mvvm.Contracts;
using Wpf.Ui.Mvvm.Services;

namespace InfoPanel
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private static readonly IHost _host = Host
       .CreateDefaultBuilder()
       //.ConfigureAppConfiguration(c => { c.SetBasePath(Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)); })
       .ConfigureServices((context, services) =>
       {
           // App Host
           services.AddHostedService<ApplicationHostService>();

           // Theme manipulation
           services.AddSingleton<IThemeService, ThemeService>();

           // Taskbar manipulation
           services.AddSingleton<ITaskBarService, TaskBarService>();

           // Snackbar service
           services.AddSingleton<ISnackbarService, SnackbarService>();

           // Dialog service
           services.AddSingleton<IDialogService, DialogService>();

           //// Page resolver service
           services.AddSingleton<IPageService, PageService>();

           //// Page resolver service
           //services.AddSingleton<ITestWindowService, TestWindowService>();

           // Service containing navigation, same as INavigationWindow... but without window
           services.AddSingleton<INavigationService, NavigationService>();

           // Main window container with navigation
           services.AddScoped<INavigationWindow, FluentWindow>();
           //services.AddScoped<ContainerViewModel>();

           // Views and ViewModels
           services.AddScoped<Views.Pages.HomePage>();
           services.AddScoped<HomeViewModel>();
           services.AddScoped<Views.Pages.ProfilesPage>();
           services.AddScoped<ProfilesViewModel>();
           services.AddScoped<Views.Pages.DesignPage>();
           services.AddScoped<DesignViewModel>();
           services.AddScoped<Views.Pages.PluginsPage>();
           services.AddScoped<PluginsViewModel>();
           services.AddScoped<Views.Pages.AboutPage>();
           services.AddScoped<AboutViewModel>();
           services.AddScoped<Views.Pages.SettingsPage>();
           services.AddScoped<SettingsViewModel>();
           services.AddScoped<Views.Pages.UpdatesPage>();
           services.AddScoped<UpdatesViewModel>();

           // Configuration
           //services.Configure<AppConfig>(context.Configuration.GetSection(nameof(AppConfig)));
       }).Build();

        public Dictionary<Guid, DisplayWindow> DisplayWindows = [];

        public static T? GetService<T>()
        where T : class
        {
            return _host.Services.GetService(typeof(T)) as T;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public App()
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            SentrySdk.Init(o =>
            {
                o.Dsn = "https://5ca30f9d2faba70d50918db10cee0d26@o4508414465146880.ingest.us.sentry.io/4508414467833856";
                o.Debug = true;
                o.AutoSessionTracking = true;
            });
        }

        void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            SentrySdk.CaptureException(e.Exception);

            // If you want to avoid the application from crashing:
            //e.Handled = true;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;

            Process proc = Process.GetCurrentProcess();
            if (Process.GetProcesses().Where(p => p.ProcessName == proc.ProcessName).Count() > 1)
            {
                MessageBox.Show("InfoPanel is already running. Check your tray area if it is minimized.", "Error", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                Environment.Exit(0);
                return;
            }

            ShutdownMode = ShutdownMode.OnMainWindowClose;

            var cwd = Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);

            if (cwd != null)
            {
                Environment.CurrentDirectory = cwd;
            }

            base.OnStartup(e);

            var updateFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "updates", "InfoPanelSetup.exe");

            if (File.Exists(updateFilePath))
            {
                try
                {
                    File.Delete(updateFilePath);
                }
                catch { }
            }

            _host.Start();

            ConfigModel.Instance.Initialize();

            if (ConfigModel.Instance.Profiles.Count == 0)
            {
                var profile = new Profile()
                {
                    Name = "Profile 1",
                    Active = true,
                };

                ConfigModel.Instance.AddProfile(profile);
                SharedModel.Instance.SelectedProfile = profile;
                ConfigModel.Instance.SaveProfiles();

                var textDisplayItem = new TextDisplayItem("Go to Design tab to start your journey.");
                textDisplayItem.X = 50;
                textDisplayItem.Y = 100;
                textDisplayItem.Font = "Arial";
                textDisplayItem.Italic = true;

                SharedModel.Instance.AddDisplayItem(textDisplayItem);

                textDisplayItem = new TextDisplayItem("Drag this panel to reposition.");
                textDisplayItem.X = 50;
                textDisplayItem.Y = 150;
                textDisplayItem.Font = "Arial";
                textDisplayItem.Italic = true;
                SharedModel.Instance.AddDisplayItem(textDisplayItem);
                SharedModel.Instance.SaveDisplayItems();
            }

            HWHash.SetDelay(300);
            HWHash.Launch();

            if (ConfigModel.Instance.Settings.LibreHardwareMonitor)
            {
                LibreMonitor.Instance.SetRing0(ConfigModel.Instance.Settings.LibreHardMonitorRing0);
                await LibreMonitor.Instance.StartAsync();
            }

            await PluginMonitor.Instance.StartAsync();
            SystemEvents.PowerModeChanged += OnPowerChange;
            Exit += App_Exit;

            await StartPanels();

            //var window = new SkiaDisplayWindow();
            //window.Show();
        }

        void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            Task.Run(async () => {
                await BeadaPanelTask.Instance.StopAsync(true);
                await TuringPanelATask.Instance.StopAsync(true);
                await TuringPanelCTask.Instance.StopAsync(true);
                await TuringPanelETask.Instance.StopAsync(true);
            }).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private void OnPowerChange(object sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Resume:
                    Task.Run(async () => {
                        await Task.Delay(1000);
                        await StartPanels();
                    }).ConfigureAwait(false).GetAwaiter().GetResult();
                    break;
                case PowerModes.Suspend:
                    Task.Run(async () => {
                        await BeadaPanelTask.Instance.StopAsync(true);
                        await TuringPanelATask.Instance.StopAsync(true);
                        await TuringPanelCTask.Instance.StopAsync(true);
                        await TuringPanelETask.Instance.StopAsync(true);
                    }).ConfigureAwait(false).GetAwaiter().GetResult();
                    break;
            }
        }

        private static async Task StartPanels()
        {
            if (ConfigModel.Instance.Settings.BeadaPanel)
            {
                await BeadaPanelTask.Instance.StartAsync();
            }

            if (ConfigModel.Instance.Settings.TuringPanel)
            {
                await TuringPanelTask.Instance.StartAsync();
            }

            if (ConfigModel.Instance.Settings.TuringPanelA)
            {
                await TuringPanelATask.Instance.StartAsync();
            }

            if (ConfigModel.Instance.Settings.TuringPanelC)
            {
                await TuringPanelCTask.Instance.StartAsync();
            }

            if (ConfigModel.Instance.Settings.TuringPanelE)
            {
                await TuringPanelETask.Instance.StartAsync();
            }

            if (ConfigModel.Instance.Settings.WebServer)
            {
                await WebServerTask.Instance.StartAsync();
            }

        }

        private static async Task StopPanels()
        {
            await BeadaPanelTask.Instance.StopAsync();
            await TuringPanelTask.Instance.StopAsync();
            await TuringPanelATask.Instance.StopAsync();
            await TuringPanelCTask.Instance.StopAsync();
            await TuringPanelETask.Instance.StopAsync();
        }

        private void App_Exit(object sender, ExitEventArgs e)
        {

        }

        public static async Task CleanShutDown()
        {
            _displayManager.CloseAll();
            await StopPanels();
            await LibreMonitor.Instance.StopAsync();
            await PluginMonitor.Instance.StopAsync();
            //shutdown

            Application.Current.Dispatcher.Invoke(() =>
            {
                Application.Current.Shutdown();
            });
        }

        public void ShowDesign(Profile profile)
        {
            SharedModel.Instance.SelectedProfile = profile;
            var window = _host.Services.GetRequiredService<INavigationWindow>() as FluentWindow;
            window?.RestoreWindow();
            window?.Navigate(typeof(Views.Pages.DesignPage));
        }


        private static readonly DisplayWindowManager _displayManager = new();

        public DisplayWindow? GetDisplayWindow(Profile profile)
        {
            //DisplayWindows.TryGetValue(profile.Guid, out var displayWindow);
            //return displayWindow;
            //return _displayManager.GetDisplayThread(profile.Guid)?.Window;

            return null;
        }

        public void MaximiseDisplayWindow(Profile profile)
        {
            //var window = GetDisplayWindow(profile);
            //window?.Fullscreen();
            //_displayManager.GetDisplayThread(profile.Guid)?.Window?.Fullscreen();


        }

        public void ShowDisplayWindow(Profile profile)
        {
            _displayManager.ShowDisplayWindow(profile);
            //var window = GetDisplayWindow(profile);

            //if (window != null && window.Direct2DMode != profile.Direct2DMode)
            //{
            //    window.Close();
            //    window = null;
            //}

            //if (window == null)
            //{
            //    window = new DisplayWindow(profile);
            //    DisplayWindows[profile.Guid] = window;
            //    window.Closed += DisplayWindow_Closed;
            //}

            //window?.Show();
        }

        public void CloseDisplayWindow(Profile profile)
        {
            _displayManager.CloseDisplayWindow(profile.Guid);
            //var window = GetDisplayWindow(profile);
            //window?.Close();
        }

        private void DisplayWindow_Closed(object? sender, EventArgs e)
        {
            if (sender is DisplayWindow displayWindow)
            {
                displayWindow.Closed -= DisplayWindow_Closed;
                DisplayWindows.Remove(displayWindow.Profile.Guid);
            }
        }
    }
}
