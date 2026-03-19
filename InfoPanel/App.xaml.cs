using FlyleafLib;
using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.Services;
using InfoPanel.Utils;
using InfoPanel.ViewModels;
using InfoPanel.Views.Common;
using InfoPanel.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using Sentry;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.DependencyInjection;

namespace InfoPanel
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private static readonly ILogger Logger = Log.ForContext<App>();

        /// <summary>
        /// Switches the MahApps.Metro theme resource dictionary and brush overrides
        /// to match the current WPF-UI application theme.
        /// </summary>
        public static void SyncMahAppsTheme(ApplicationTheme theme)
        {
            var app = Application.Current;
            var mahAppsThemeName = theme == ApplicationTheme.Light ? "Light.Blue" : "Dark.Blue";

            var oldDict = app.Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source?.OriginalString.Contains("/Styles/Themes/") == true);

            if (oldDict != null)
            {
                var newDict = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/MahApps.Metro;component/Styles/Themes/{mahAppsThemeName}.xaml")
                };

                var index = app.Resources.MergedDictionaries.IndexOf(oldDict);
                app.Resources.MergedDictionaries.RemoveAt(index);
                app.Resources.MergedDictionaries.Insert(index, newDict);
            }

            UpdateMahAppsBrushOverrides(theme);
        }

        private static void UpdateMahAppsBrushOverrides(ApplicationTheme theme)
        {
            var resources = Application.Current.Resources;

            if (theme == ApplicationTheme.Light)
            {
                resources["MahApps.Brushes.Text"] = new SolidColorBrush(Colors.Black);
                resources["MahApps.Brushes.Selected.Foreground"] = new SolidColorBrush(Colors.Black);
                resources["MahApps.Brushes.TextBox.Border"] = new SolidColorBrush(Colors.Gray);
            }
            else
            {
                resources["MahApps.Brushes.Text"] = new SolidColorBrush(Colors.LightGray);
                resources["MahApps.Brushes.Selected.Foreground"] = new SolidColorBrush(Colors.LightGray);
                resources["MahApps.Brushes.TextBox.Border"] = new SolidColorBrush(Colors.DarkGray);
            }
        }

        private static readonly IHost _host = Host
       .CreateDefaultBuilder()
       //.ConfigureAppConfiguration(c => { c.SetBasePath(Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)); })
       .UseSerilog((context, services, configuration) => configuration
#if DEBUG
           .MinimumLevel.Debug()
#else
           .MinimumLevel.Information()
#endif
           .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
           .Enrich.WithThreadId()
           .Enrich.WithThreadName()
           .Enrich.WithMachineName()
           .Enrich.FromLogContext()
           .WriteTo.Debug()
           .WriteTo.File(
               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "logs", "infopanel-.log"),
               rollingInterval: RollingInterval.Day,
               retainedFileCountLimit: 7,
               fileSizeLimitBytes: 104857600, // 100MB
               rollOnFileSizeLimit: true,
               outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] [{ThreadName}] - [{SourceContext}] {Message:lj}{NewLine}{Exception}"
           ))
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
           services.AddSingleton<IContentDialogService, ContentDialogService>();

           // Page resolver service (WPF-UI 4.x DI integration)
           services.AddNavigationViewPageProvider();

           // Service containing navigation, same as INavigationWindow... but without window
           services.AddSingleton<INavigationService, NavigationService>();

           // Main window container with navigation
           services.AddScoped<INavigationWindow, MainWindow>();
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
           services.AddScoped<PluginBrowserViewModel>();
           services.AddScoped<Views.Pages.AboutPage>();
           services.AddScoped<AboutViewModel>();
           services.AddScoped<Views.Pages.SettingsPage>();
           services.AddScoped<SettingsViewModel>();
           services.AddScoped<Views.Pages.UpdatesPage>();
           services.AddScoped<UpdatesViewModel>();
           services.AddScoped<Views.Pages.UsbPanelsPage>();
           services.AddScoped<UsbPanelsViewModel>();
           services.AddScoped<Views.Pages.AccountPage>();
           services.AddSingleton<AccountViewModel>();

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
                o.Release = Utils.VersionHelper.AppVersion;
            });
        }

        void App_DispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            SentrySdk.AddBreadcrumb("WPF Dispatcher unhandled exception", "error");
            SentrySdk.CaptureException(e.Exception);

            // Log locally as well
            Logger.Error(e.Exception, "DispatcherUnhandledException occurred");

            // Decide whether to crash or continue
            e.Handled = true; // Uncomment to prevent crash
        }

        void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception ?? new Exception($"Non-exception thrown: {e.ExceptionObject}");

            SentrySdk.AddBreadcrumb($"AppDomain unhandled exception (IsTerminating: {e.IsTerminating})", "error");
            SentrySdk.CaptureException(exception);

            Logger.Fatal(exception, "CurrentDomain_UnhandledException occurred. IsTerminating: {IsTerminating}", e.IsTerminating);

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
                    Logger.Error(innerEx, "TaskScheduler_UnobservedTaskException: Unobserved task exception in aggregate");
                }
            }
            else
            {
                SentrySdk.CaptureException(e.Exception);
                Logger.Error(e.Exception, "TaskScheduler_UnobservedTaskException: Unobserved task exception");
            }

            // Mark as observed to prevent app termination
            e.SetObserved();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Logger.Information("Application exiting");
            ConfigModel.Instance.Cleanup();
            Log.CloseAndFlush();
            base.OnExit(e);
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            Logger.Information("InfoPanel starting up");
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;

            Process proc = Process.GetCurrentProcess();
            var pluginHostIds = GetPluginHostProcessIds();
            var otherInstances = Process.GetProcesses()
                .Where(p => p.ProcessName == proc.ProcessName && p.Id != proc.Id)
                .Where(p => !pluginHostIds.Contains(p.Id))
                .ToList();

            if (otherInstances.Count > 0)
            {
                var result = System.Windows.MessageBox.Show(
                    $"Another instance of InfoPanel is running (Process ID: {string.Join(", ", otherInstances.Select(p => p.Id))}).\n\n" +
                    "This may be from a previous crash or hung process.\n\n" +
                    "Would you like to terminate the other instance(s) and continue?",
                    "InfoPanel Already Running",
                    System.Windows.MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    Logger.Information("User chose to terminate {Count} other InfoPanel instance(s)", otherInstances.Count);
                    foreach (var otherProcess in otherInstances)
                    {
                        try
                        {
                            Logger.Information("Terminating process ID: {ProcessId}", otherProcess.Id);
                            otherProcess.Kill();
                            otherProcess.WaitForExit(5000); // Wait up to 5 seconds for process to exit
                            Logger.Information("Successfully terminated process ID: {ProcessId}", otherProcess.Id);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Failed to terminate process ID: {ProcessId}", otherProcess.Id);
                        }
                    }
                    Logger.Information("Continuing with InfoPanel startup after terminating other instances");
                    // Continue with startup
                }
                else
                {
                    Logger.Information("User chose not to terminate other instances, exiting");
                    Environment.Exit(0);
                    return;
                }
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

            await _host.StartAsync();
            Logger.Debug("Application host started");

            await Task.Run(() => Engine.Start(new EngineConfig()
            {
#if DEBUG
                LogOutput = ":debug",
                LogLevel = LogLevel.Debug,
                FFmpegLogLevel = Flyleaf.FFmpeg.LogLevel.Warn,
#endif
                PluginsPath = ":FlyleafPlugins",
                FFmpegPath = ":FFmpeg",
            }));
            Logger.Debug("Flyleaf engine started");

            ConfigModel.Instance.Initialize();
            Logger.Debug("Configuration initialized");

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

            HWHash.SetDelay(500);
            HWHash.Launch();

            // Check PawniO status before starting LibreHardwareMonitor
            if (ConfigModel.Instance.Settings.LibreHardwareMonitor)
            {
                CheckAndPromptPawnIO();
                await LibreMonitor.Instance.StartAsync();
            }

            await PluginMonitor.Instance.StartAsync();
            SystemEvents.PowerModeChanged += OnPowerChange;
            Exit += App_Exit;

            await StartPanels();

            _ = Task.Run(() => GetService<AccountViewModel>()?.TryRestoreSessionAsync());

            _ = Task.Run(() => Services.UpdateChecker.Instance.CheckAsync(showNotification: true));
        }

        void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                await BeadaPanelTask.Instance.StopAsync(true);
                await TuringPanelTask.Instance.StopAsync(true);
                await ThermalrightPanelTask.Instance.StopAsync(true);
            });
        }

        private void OnPowerChange(object sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Resume:
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        await StartPanels();
                    });
                    break;
                case PowerModes.Suspend:
                    _ = Task.Run(async () =>
                    {
                        await BeadaPanelTask.Instance.StopAsync(true);
                        await TuringPanelTask.Instance.StopAsync(true);
                        await ThermalrightPanelTask.Instance.StopAsync(true);
                    });
                    break;
            }
        }

        private static async Task StartPanels()
        {
            if (ConfigModel.Instance.Settings.BeadaPanelMultiDeviceMode)
            {
                await BeadaPanelTask.Instance.StartAsync();
            }

            if (ConfigModel.Instance.Settings.TuringPanelMultiDeviceMode)
            {
                await TuringPanelTask.Instance.StartAsync();
            }

            if (ConfigModel.Instance.Settings.ThermalrightPanelMultiDeviceMode)
            {
                await ThermalrightPanelTask.Instance.StartAsync();
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
            await ThermalrightPanelTask.Instance.StopAsync();
        }

        private void App_Exit(object sender, ExitEventArgs e)
        {

        }

        /// <summary>
        /// Checks PawniO installation status and prompts user to install/update if needed.
        /// </summary>
        private static void CheckAndPromptPawnIO()
        {
            try
            {
                if (PawnIoHelper.IsInstalled && !PawnIoHelper.RequiresUpdate)
                {
                    Logger.Information("PawniO is installed and up to date: {Version}", PawnIoHelper.Version);
                    return;
                }

                string message;
                string title = "PawniO Driver";

                if (PawnIoHelper.RequiresUpdate)
                {
                    message = $"PawniO is outdated (v{PawnIoHelper.Version}).\n\n" +
                             $"LibreHardwareMonitor requires PawniO v{2}.0.0.0 or higher for low-level hardware access.\n\n" +
                             "Would you like to update it now?";
                    Logger.Information("PawniO update available: current v{Current}, required v{Required}",
                        PawnIoHelper.Version, "2.0.0.0");
                }
                else
                {
                    message = "PawniO is not installed.\n\n" +
                             "LibreHardwareMonitor requires PawniO for low-level hardware access (CPU temperatures, voltages, etc.).\n\n" +
                             "Would you like to install it now?";
                    Logger.Information("PawniO is not installed");
                }

                var result = System.Windows.MessageBox.Show(
                    message,
                    title,
                    System.Windows.MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (result == System.Windows.MessageBoxResult.OK)
                {
                    Logger.Information("User chose to install/update PawniO");
                    bool success = PawnIoHelper.InstallOrUpdate();

                    if (success && PawnIoHelper.IsInstalled)
                    {
                        Logger.Information("PawniO installation/update successful");
                        System.Windows.MessageBox.Show(
                            $"PawniO v{PawnIoHelper.Version} has been installed successfully.",
                            "Installation Complete",
                            System.Windows.MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else if (!success)
                    {
                        Logger.Warning("PawniO installation/update failed or was cancelled");
                    }
                }
                else
                {
                    Logger.Information("User cancelled PawniO installation/update");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error checking or installing PawniO");
            }
        }

        private static HashSet<int> GetPluginHostProcessIds()
        {
            var ids = new HashSet<int>();
            try
            {
                var currentName = Process.GetCurrentProcess().ProcessName;
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{currentName}.exe'");
                foreach (var obj in searcher.Get())
                {
                    var cmd = obj["CommandLine"]?.ToString();
                    if (cmd != null && cmd.Contains("--plugin-host"))
                    {
                        ids.Add(Convert.ToInt32(obj["ProcessId"]));
                    }
                }
            }
            catch
            {
                // If WMI fails, return empty set — no processes will be excluded
            }
            return ids;
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
            var window = _host.Services.GetRequiredService<INavigationWindow>() as MainWindow;
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
