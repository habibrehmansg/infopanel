using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using InfoPanel.Monitors;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace InfoPanel
{
    public sealed class ConfigModel : ObservableObject
    {
        private const int CurrentVersion = 123;
        private const string RegistryRunKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        private static readonly Lazy<ConfigModel> lazy = new(() => new ConfigModel());

        public static ConfigModel Instance { get { return lazy.Value; } }

        public ObservableCollection<Profile> Profiles { get; private set; } = [];
        private readonly object _profilesLock = new();

        public Settings Settings { get; private set; }
        private readonly object _settingsLock = new object();

        private ConfigModel()
        {
            Settings = new Settings();
            LoadSettings();

            if (Settings.Version != CurrentVersion)
            {
                if (Settings.Version == 114)
                {
                    Upgrade_File_Structure_From_1_1_4();
                    Settings.Version = 115;
                    SaveSettings();
                }
            }

            Settings.PropertyChanged += Settings_PropertyChanged;
            Profiles.CollectionChanged += Profiles_CollectionChanged;
        }

        public void Initialize()
        {
            LoadProfiles();
        }

        private void Profiles_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (Profile profile in e.NewItems)
                {
                    if (profile.Active)
                    {
                        if (System.Windows.Application.Current is App app)
                        {
                            app.ShowDisplayWindow(profile);
                        }
                    }

                    profile.PropertyChanged += Profile_PropertyChanged;
                }
            }

            if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
            {
                foreach (Profile profile in e.OldItems)
                {
                    if (System.Windows.Application.Current is App app)
                    {
                        app.CloseDisplayWindow(profile);
                    }

                    profile.PropertyChanged -= Profile_PropertyChanged;
                }
            }
        }

        private void Profile_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is Profile profile)
            {
                if (e.PropertyName == nameof(Profile.Active) || e.PropertyName == nameof(Profile.Direct2DMode))
                {
                    if (profile.Active)
                    {
                        if (System.Windows.Application.Current is App app)
                        {
                            app.ShowDisplayWindow(profile);
                        }
                    }
                    else
                    {
                        if (System.Windows.Application.Current is App app)
                        {
                            app.CloseDisplayWindow(profile);
                        }
                    }
                }
            }
        }

        private void ValidateStartup()
        {
            //legacy startup removal
            using var registryKey = Registry.CurrentUser?.OpenSubKey(RegistryRunKey, true);
            registryKey?.DeleteValue("InfoPanel", false);

            //new startup removal
            using var taskService = new TaskService();

            if (!Settings.AutoStart)
            {
                //delete task if exists
                taskService.RootFolder.DeleteTask("InfoPanel", false);
            }
            else
            {
                using var taskDefinition = taskService.NewTask();
                taskDefinition.RegistrationInfo.Description = "Runs InfoPanel on startup.";
                taskDefinition.RegistrationInfo.Author = "Habib Rehman";
                taskDefinition.Triggers.Add(new LogonTrigger());
                taskDefinition.Actions.Add(new ExecAction(Application.ExecutablePath));
                taskDefinition.Principal.RunLevel = TaskRunLevel.Highest;
                taskDefinition.Settings.DisallowStartIfOnBatteries = false;
                taskDefinition.Settings.StopIfGoingOnBatteries = false;
                taskDefinition.Settings.AllowDemandStart = true;
                taskDefinition.Settings.AllowHardTerminate = true;
                taskDefinition.Settings.ExecutionTimeLimit = TimeSpan.Zero;

                taskService.RootFolder.RegisterTaskDefinition("InfoPanel", taskDefinition, TaskCreation.CreateOrUpdate,
                    System.Security.Principal.WindowsIdentity.GetCurrent().Name, null, TaskLogonType.InteractiveToken);
            }
        }

        private async void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.AutoStart))
            {
                ValidateStartup();
            }
            else if (e.PropertyName == nameof(Settings.LibreHardwareMonitor) || e.PropertyName == nameof(Settings.LibreHardMonitorRing0))
            {
                await LibreMonitor.Instance.StopAsync();

                if (Settings.LibreHardwareMonitor)
                {
                    LibreMonitor.Instance.SetRing0(Settings.LibreHardMonitorRing0);
                    await LibreMonitor.Instance.StartAsync();
                }
            }
            else if (e.PropertyName == nameof(Settings.BeadaPanel))
            {
                if (Settings.BeadaPanel)
                {
                    await BeadaPanelTask.Instance.StartAsync();
                }
                else
                {
                    await BeadaPanelTask.Instance.StopAsync();
                }
            }
            else if (e.PropertyName == nameof(Settings.TuringPanel))
            {
                if (Settings.TuringPanel)
                {
                    await TuringPanelTask.Instance.StartAsync();
                }
                else
                {
                    await TuringPanelTask.Instance.StopAsync();
                }
            }
            else if (e.PropertyName == nameof(Settings.TuringPanelA))
            {
                if (Settings.TuringPanelA)
                {
                    await TuringPanelATask.Instance.StartAsync();
                }
                else
                {
                    await TuringPanelATask.Instance.StopAsync();
                }
            }
            else if (e.PropertyName == nameof(Settings.TuringPanelC))
            {
                if (Settings.TuringPanelC)
                {
                    await TuringPanelCTask.Instance.StartAsync();
                }
                else
                {
                    await TuringPanelCTask.Instance.StopAsync();
                }
            }
            else if (e.PropertyName == nameof(Settings.TuringPanelE))
            {
                if (Settings.TuringPanelE)
                {
                    await TuringPanelETask.Instance.StartAsync();
                }
                else
                {
                    await TuringPanelETask.Instance.StopAsync();
                }
            }
            else if (e.PropertyName == nameof(Settings.WebServer))
            {
                if (Settings.WebServer)
                {
                    await WebServerTask.Instance.StartAsync();
                }
                else
                {
                   await WebServerTask.Instance.StopAsync();
                }
            }

            SaveSettings();
        }

        public List<Profile> GetProfilesCopy()
        {
            lock (_profilesLock)
            {
                return [.. Profiles];
            }
        }

        public Profile? GetProfile(Guid guid)
        {
            lock (_profilesLock)
            {
                return Profiles.FirstOrDefault(p => p.Guid == guid);
            }
        }

        public void SaveSettings()
        {
            lock (_settingsLock)
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel");
                Directory.CreateDirectory(folder);
                var fileName = Path.Combine(folder, "settings.xml");
                XmlSerializer xs = new(typeof(Settings));

                var settings = new XmlWriterSettings() { Encoding = Encoding.UTF8, Indent = true };
                using var wr = XmlWriter.Create(fileName, settings);
                xs.Serialize(wr, Settings);
            }
        }

        public void LoadSettings()
        {

            var fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "settings.xml");
            if (File.Exists(fileName))
            {
                XmlSerializer xs = new XmlSerializer(typeof(Settings));
                using var rd = XmlReader.Create(fileName);
                try
                {
                    if (xs.Deserialize(rd) is Settings settings)
                    {
                        lock (_settingsLock)
                        {
                            Settings.AutoStart = settings.AutoStart;
                            Settings.StartMinimized = settings.StartMinimized;
                            Settings.MinimizeToTray = settings.MinimizeToTray;

                            Settings.SelectedItemColor = settings.SelectedItemColor;
                            Settings.ShowGridLines = settings.ShowGridLines;
                            Settings.GridLinesColor = settings.GridLinesColor;
                            Settings.GridLinesSpacing = settings.GridLinesSpacing;

                            Settings.LibreHardwareMonitor = settings.LibreHardwareMonitor;
                            Settings.LibreHardMonitorRing0 = settings.LibreHardMonitorRing0;
                            Settings.WebServer = settings.WebServer;
                            Settings.WebServerListenIp = settings.WebServerListenIp;
                            Settings.WebServerListenPort = settings.WebServerListenPort;
                            Settings.WebServerRefreshRate = settings.WebServerRefreshRate;
                            Settings.TargetFrameRate = settings.TargetFrameRate;
                            Settings.TargetGraphUpdateRate = settings.TargetGraphUpdateRate;
                            Settings.Version = settings.Version;

                            Settings.BeadaPanel = settings.BeadaPanel;
                            Settings.BeadaPanelProfile = settings.BeadaPanelProfile;
                            Settings.BeadaPanelRotation = settings.BeadaPanelRotation;
                            Settings.BeadaPanelBrightness = settings.BeadaPanelBrightness;

                            Settings.TuringPanel = settings.TuringPanel;
                            Settings.TuringPanelProfile = settings.TuringPanelProfile;
                            Settings.TuringPanelRotation = settings.TuringPanelRotation;
                            Settings.TuringPanelBrightness = settings.TuringPanelBrightness;

                            Settings.TuringPanelA = settings.TuringPanelA;
                            Settings.TuringPanelAProfile = settings.TuringPanelAProfile;
                            Settings.TuringPanelAPort = settings.TuringPanelAPort;
                            Settings.TuringPanelARotation = settings.TuringPanelARotation;
                            Settings.TuringPanelABrightness = settings.TuringPanelABrightness;

                            Settings.TuringPanelC = settings.TuringPanelC;
                            Settings.TuringPanelCProfile = settings.TuringPanelCProfile;
                            Settings.TuringPanelCPort = settings.TuringPanelCPort;
                            Settings.TuringPanelCRotation = settings.TuringPanelCRotation;
                            Settings.TuringPanelCBrightness = settings.TuringPanelCBrightness;

                            Settings.TuringPanelE = settings.TuringPanelE;
                            Settings.TuringPanelEProfile = settings.TuringPanelEProfile;
                            Settings.TuringPanelEPort = settings.TuringPanelEPort;
                            Settings.TuringPanelERotation = settings.TuringPanelERotation;
                            Settings.TuringPanelEBrightness = settings.TuringPanelEBrightness;
                        }

                        ValidateStartup();
                    }
                }
                catch { }
            }
        }

        public void AddProfile(Profile profile)
        {
            lock (_profilesLock)
            {
                Profiles.Add(profile);
            }
        }

        public bool RemoveProfile(Profile profile)
        {
            lock (_profilesLock)
            {
                if (Profiles.Count > 1)
                {
                    Profiles.Remove(profile);
                    return true;
                }
            }

            return false;
        }

        public void SaveProfiles()
        {
            lock (_profilesLock)
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel");
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                var profiles = GetProfilesCopy();
                var fileName = Path.Combine(folder, "profiles.xml");
                XmlSerializer xs = new XmlSerializer(typeof(List<Profile>));
                var settings = new XmlWriterSettings() { Encoding = Encoding.UTF8, Indent = true };
                using (var wr = XmlWriter.Create(fileName, settings))
                {

                    xs.Serialize(wr, profiles);
                }

                //clean up profile xml
                var profilesFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "profiles");
                if (Directory.Exists(profilesFolder))
                {
                    var files = Directory.GetFiles(profilesFolder).ToList();
                    foreach (var profile in profiles)
                    {
                        files.Remove(Path.Combine(profilesFolder, profile.Guid.ToString() + ".xml"));
                    }

                    foreach (var file in files)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch { }
                    }
                }

                //clean up profile asset folder
                var assetsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "assets");
                if (Directory.Exists(assetsFolder))
                {
                    var directories = Directory.GetDirectories(assetsFolder).ToList();
                    foreach (var profile in profiles)
                    {
                        directories.Remove(Path.Combine(assetsFolder, profile.Guid.ToString()));
                    }

                    foreach (var directory in directories)
                    {
                        try
                        {
                            Directory.Delete(directory, true);
                        }
                        catch { }
                    }
                }
            }
        }

        public void ReloadProfile(Profile profile)
        {
            if (LoadProfilesFromFile()?.Find(p => p.Guid == profile.Guid) is Profile originalProfile)
            {
                var config = new AutoMapper.MapperConfiguration(cfg =>
                {
                    cfg.CreateMap<Profile, Profile>();
                });

                var mapper = config.CreateMapper();

                mapper.Map(originalProfile, profile);
            }

        }

        public void LoadProfiles()
        {
            var profiles = LoadProfilesFromFile();
            if (profiles != null)
            {
                lock (_profilesLock)
                {
                    Profiles.Clear();
                    profiles?.ForEach(Profiles.Add);
                }

                SharedModel.Instance.SelectedProfile = Profiles.FirstOrDefault(profile => { return profile.Active; }, Profiles[0]);
            }
        }

        public static List<Profile>? LoadProfilesFromFile()
        {
            var fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "profiles.xml");
            if (File.Exists(fileName))
            {
                XmlSerializer xs = new(typeof(List<Profile>));
                using var rd = XmlReader.Create(fileName);
                try
                {
                    return xs.Deserialize(rd) as List<Profile>;
                }
                catch { }
            }

            return null;
        }

        private void Upgrade_File_Structure_From_1_1_4()
        {
            var profilesFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "profiles");

            if (Directory.Exists(profilesFolder))
            {
                foreach (var file in Directory.GetFiles(profilesFolder))
                {
                    //read the file
                    XmlSerializer xs = new(typeof(List<DisplayItem>),
                       [typeof(BarDisplayItem), typeof(GraphDisplayItem), typeof(TableSensorDisplayItem), typeof(SensorDisplayItem), typeof(ClockDisplayItem), typeof(CalendarDisplayItem), typeof(TextDisplayItem), typeof(ImageDisplayItem)]);

                    List<DisplayItem>? displayItems = null;
                    using (var rd = XmlReader.Create(file))
                    {
                        displayItems = xs.Deserialize(rd) as List<DisplayItem>;
                    }

                    if (displayItems != null)
                    {
                        var assetsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "assets", Path.GetFileNameWithoutExtension(file));

                        if (!Directory.Exists(assetsFolder))
                        {
                            Directory.CreateDirectory(assetsFolder);
                        }

                        foreach (var displayItem in displayItems)
                        {
                            if (displayItem is ImageDisplayItem imageDisplayItem)
                            {
                                if (!imageDisplayItem.RelativePath && imageDisplayItem.FilePath != null)
                                {
                                    if (File.Exists(imageDisplayItem.FilePath))
                                    {
                                        //copy and update
                                        var fileName = Path.GetFileName(imageDisplayItem.FilePath);
                                        File.Copy(imageDisplayItem.FilePath, Path.Combine(assetsFolder, fileName), true);
                                        imageDisplayItem.FilePath = fileName;
                                        imageDisplayItem.RelativePath = true;
                                    }
                                }
                            }
                        }

                        //write back
                        var settings = new XmlWriterSettings() { Encoding = Encoding.UTF8, Indent = true };
                        using var wr = XmlWriter.Create(file, settings);
                        xs.Serialize(wr, displayItems);
                    }
                }
            }
        }
    }
}
