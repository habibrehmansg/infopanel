using FlyleafLib;
using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.Services;
using InfoPanel.ViewModels;
using InfoPanel.Views.Common;
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
using System.Reflection;
using System.Runtime.InteropServices;
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

            // 1. Handle exceptions from background threads and Task.Run
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // 2. Handle unobserved task exceptions (async/await without proper handling)
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            SentrySdk.Init(o =>
            {
                o.Dsn = "https://5ca30f9d2faba70d50918db10cee0d26@o4508414465146880.ingest.us.sentry.io/4508414467833856";
                o.Debug = true;
                o.AutoSessionTracking = true;

                // Add Sentry-specific options
                o.SendDefaultPii = true; // Include user info
                o.AttachStacktrace = true; // Always attach stack traces
                o.Environment = "production"; // or "development"
                o.Release = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            });
        }

        void App_DispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            SentrySdk.AddBreadcrumb("WPF Dispatcher unhandled exception", "error");
            SentrySdk.CaptureException(e.Exception);

            // Log locally as well
            Trace.WriteLine($"DispatcherUnhandledException: {e.Exception}");

            // Decide whether to crash or continue
             e.Handled = true; // Uncomment to prevent crash
        }

        void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception ?? new Exception($"Non-exception thrown: {e.ExceptionObject}");

            SentrySdk.AddBreadcrumb($"AppDomain unhandled exception (IsTerminating: {e.IsTerminating})", "error");
            SentrySdk.CaptureException(exception);

            Trace.WriteLine($"CurrentDomain_UnhandledException: {exception}");
            Trace.WriteLine($"IsTerminating: {e.IsTerminating}");

            // Flush Sentry before potential termination
            SentrySdk.FlushAsync(TimeSpan.FromSeconds(5)).Wait();
        }

        void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            SentrySdk.AddBreadcrumb("Unobserved task exception", "error");

            // Handle aggregate exceptions
            if (e.Exception is AggregateException agg)
            {
                foreach (var innerEx in agg.InnerExceptions)
                {
                    SentrySdk.CaptureException(innerEx);
                    Trace.WriteLine($"TaskScheduler_UnobservedTaskException: {innerEx}");
                }
            }
            else
            {
                SentrySdk.CaptureException(e.Exception);
                Trace.WriteLine($"TaskScheduler_UnobservedTaskException: {e.Exception}");
            }

            // Mark as observed to prevent app termination
            e.SetObserved();
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



            Engine.Start(new EngineConfig()
            {
#if DEBUG
                LogOutput = ":debug",
                LogLevel = LogLevel.Debug,
                FFmpegLogLevel = Flyleaf.FFmpeg.LogLevel.Warn,
#endif
                PluginsPath = ":FlyleafPlugins",
                FFmpegPath = ":FFmpeg",
            });

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

                var textDisplayItem = new TextDisplayItem("Go to Design tab to start your journey.", profile);
                textDisplayItem.X = 50;
                textDisplayItem.Y = 100;
                textDisplayItem.Font = "Arial";
                textDisplayItem.Italic = true;

                SharedModel.Instance.AddDisplayItem(textDisplayItem);

                textDisplayItem = new TextDisplayItem("Drag this panel to reposition.", profile);
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
            Task.Run(async () =>
            {
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
                    Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        await StartPanels();
                    }).ConfigureAwait(false).GetAwaiter().GetResult();
                    break;
                case PowerModes.Suspend:
                    Task.Run(async () =>
                    {
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
            if (ConfigModel.Instance.Settings.BeadaPanel || ConfigModel.Instance.Settings.BeadaPanelMultiDeviceMode)
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
            DisplayWindowManager.Instance.CloseAll();
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

        public void MaximiseDisplayWindow(Profile profile)
        {
            DisplayWindowManager.Instance.GetWindow(profile.Guid)?.Fullscreen();
        }

        public void ShowDisplayWindow(Profile profile)
        {
            DisplayWindowManager.Instance.ShowDisplayWindow(profile);
        }

        public void CloseDisplayWindow(Profile profile)
        {
            DisplayWindowManager.Instance.CloseDisplayWindow(profile.Guid);
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
