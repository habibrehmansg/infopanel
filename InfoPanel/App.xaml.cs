using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.Services;
using InfoPanel.ViewModels;
using InfoPanel.Views.Common;
using InfoPanel.Views.Windows;
using Microsoft.AppCenter;
using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using Prise.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
           //add plugins
           services.AddPrise();
           
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

        public static T GetService<T>()
        where T : class
        {
            return _host.Services.GetService(typeof(T)) as T;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        protected override void OnStartup(StartupEventArgs e)
        {
            //WpfSingleInstance.Make("InfoPanel");

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

            System.Windows.Forms.Application.ThreadException += (sender, args) =>
            {
                Crashes.TrackError(args.Exception);
            };
            var countryCode = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            AppCenter.SetCountryCode(countryCode);
            AppCenter.Start("c955c460-19db-487a-abe1-dff3dd59bb56",
                  typeof(Analytics), typeof(Crashes));

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

            HWHash.SetDelay(100);
            HWHash.Launch();

            PanelDrawTask.Instance.Start();
            //GraphDrawTask.Instance.Start();

            StartPanels();

            SystemEvents.SessionEnding += OnSessionEnding;
            SystemEvents.PowerModeChanged += OnPowerChange;
            Exit += App_Exit;

            LibreMonitor.Launch();
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            Shutdown();
        }

        private void OnPowerChange(object sender, PowerModeChangedEventArgs e)
        {

            switch (e.Mode)
            {
                case PowerModes.Resume:
                    StartPanels();
                    break;
                case PowerModes.Suspend:
                    StopPanels();
                    break;
            }
        }

        private void StartPanels()
        {
            if (ConfigModel.Instance.Settings.BeadaPanel)
            {
                BeadaPanelTask.Instance.Start();
            }

            if (ConfigModel.Instance.Settings.TuringPanelA)
            {
                TuringPanelATask.Instance.Start();
            }

            if (ConfigModel.Instance.Settings.TuringPanelC)
            {
                TuringPanelCTask.Instance.Start();
            }

            if (ConfigModel.Instance.Settings.WebServer)
            {
                WebServerTask.Instance.Start();
            }
        }

        private void App_Exit(object sender, ExitEventArgs e)
        {
            ShutDown();
        }

        void MenuExit_Click(object? sender, EventArgs e)
        {
            Shutdown();
            Environment.Exit(0);
        }

        private void ShutDown()
        {
            PanelDrawTask.Instance.Stop();
            GraphDrawTask.Instance.Stop();
            StopPanels();
            Task.Delay(500).Wait();
        }

        private void StopPanels()
        {
            BeadaPanelTask.Instance.Stop();
            TuringPanelATask.Instance.Stop();
            TuringPanelCTask.Instance.Stop();
            WebServerTask.Instance.Stop();
        }

        public void ShowDesign(Profile profile)
        {
            SharedModel.Instance.SelectedProfile = profile;
            var window = _host.Services.GetRequiredService<INavigationWindow>() as FluentWindow;
            window?.RestoreWindow();
            window?.Navigate(typeof(Views.Pages.DesignPage));
        }

        public DisplayWindow? GetDisplayWindow(Profile profile)
        {
            DisplayWindows.TryGetValue(profile.Guid, out var displayWindow);
            return displayWindow;
        }

        public void MaximiseDisplayWindow(Profile profile)
        {
            var window = GetDisplayWindow(profile);
            window?.Fullscreen();

        }

        public void ShowDisplayWindow(Profile profile)
        {
            var window = GetDisplayWindow(profile);

            if(window != null && window.Direct2DMode != profile.Direct2DMode)
            {
                window.Close();
                window = null;
            }

            if (window == null)
            {
                    window = new DisplayWindow(profile);
                    DisplayWindows[profile.Guid] = window;
                    window.Closed += DisplayWindow_Closed;
            }

            window?.Show();
        }


        public void CloseDisplayWindow(Profile profile)
        {
            var window = GetDisplayWindow(profile);
            window?.Close();
        }

        private void DisplayWindow_Closed(object? sender, EventArgs e)
        {
            if (sender is DisplayWindow displayWindow)
            {
                displayWindow.Closed -= DisplayWindow_Closed;
                DisplayWindows.Remove(displayWindow.Profile.Guid);
            }
        }

        public void testPlugin()
        {
           // var pluginPath = Path.Combine(AppContext.BaseDirectory, "plugins"); //set the path where people should put plugins. I chose a folder called plugins in the same directory as the exe

            //var scanResult = await this._pluginLoader.FindPlugin<IPanelData>(pluginPath);
        }
    }
}
