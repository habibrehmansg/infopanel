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
using Serilog;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Serialization;
using Task = System.Threading.Tasks.Task;
using Timer = System.Threading.Timer;
using InfoPanel.Services;
using InfoPanel.TuringPanel;
using HidSharp;

namespace InfoPanel
{
    public sealed class ConfigModel : ObservableObject
    {
        private static readonly ILogger Logger = Log.ForContext<ConfigModel>();
        private const int CurrentVersion = 131;
        private const string RegistryRunKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        private static readonly Lazy<ConfigModel> lazy = new(() => new ConfigModel());

        public static ConfigModel Instance { get { return lazy.Value; } }

        public ObservableCollection<Profile> Profiles { get; private set; } = [];
        private readonly object _profilesLock = new();

        public Settings Settings { get; private set; }
        private readonly object _settingsLock = new object();

        // Debouncing and async save fields
        private Timer? _saveDebounceTimer;
        private readonly SemaphoreSlim _saveSemaphore = new(1, 1);
        private const int SaveDebounceDelayMs = 500;

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
                    _ = SaveSettingsAsync(batch: false);
                }

                if (Settings.Version < 131)
                {
                    Upgrade_File_Structure_For_1_3_1();
                    Settings.Version = 131;
                    _ = SaveSettingsAsync(batch: false);
                }

            }

            Settings.PropertyChanged += Settings_PropertyChanged;
            Profiles.CollectionChanged += Profiles_CollectionChanged;
        }

        public void Initialize()
        {
            LoadProfiles();
        }

        public void AccessSettings(Action<Settings> action)
        {
            if (System.Windows.Application.Current.Dispatcher is Dispatcher dispatcher)
            {
                if (dispatcher.CheckAccess())
                {
                    action(Settings);
                }
                else
                {
                    dispatcher.Invoke(() =>
                    {
                        action(Settings);
                    });
                }
            }
        }

        private void Profiles_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
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
                if (e.PropertyName == nameof(Profile.Active) || e.PropertyName == nameof(Profile.OpenGL))
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
                taskDefinition.Triggers.Add(new LogonTrigger { Delay = TimeSpan.FromSeconds(Settings.AutoStartDelay) });
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
            else if (e.PropertyName == nameof(Settings.LibreHardwareMonitor))
            {
                await LibreMonitor.Instance.StopAsync();

                if (Settings.LibreHardwareMonitor)
                {
                    await LibreMonitor.Instance.StartAsync();
                }
            }
            else if (e.PropertyName == nameof(Settings.TuringPanelMultiDeviceMode))
            {
                if (Settings.TuringPanelMultiDeviceMode)
                {
                    await TuringPanelTask.Instance.StartAsync();
                }
                else
                {
                    await TuringPanelTask.Instance.StopAsync();
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
            else if (e.PropertyName == nameof(Settings.BeadaPanelMultiDeviceMode))
            {
                if (Settings.BeadaPanelMultiDeviceMode)
                {
                    await BeadaPanelTask.Instance.StartAsync();
                }
                else
                {
                    await BeadaPanelTask.Instance.StopAsync();
                }
            }

            await SaveSettingsAsync();
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
            // Synchronous wrapper for backward compatibility
            Task.Run(async () => await SaveSettingsAsync(batch: false)).Wait();
        }

        public async Task SaveSettingsAsync(bool batch = true)
        {
            if (!batch)
            {
                await SaveSettingsInternalAsync();
                return;
            }

            // Reset debounce timer
            _saveDebounceTimer?.Dispose();
            _saveDebounceTimer = new Timer(
                async _ => await SaveSettingsInternalAsync(),
                null,
                SaveDebounceDelayMs,
                Timeout.Infinite);
        }

        private async Task SaveSettingsInternalAsync()
        {
            await _saveSemaphore.WaitAsync();
            try
            {
                Logger.Debug("Saving settings...");
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel");
                Directory.CreateDirectory(folder);

                var fileName = Path.Combine(folder, "settings.xml");
                var tempFileName = fileName + ".tmp";
                var backupFileName = fileName + ".bak";

                // Serialize settings to memory first to ensure it's valid
                using var ms = new MemoryStream();
                lock (_settingsLock)
                {
                    var xs = new XmlSerializer(typeof(Settings));
                    xs.Serialize(ms, Settings);
                }

                ms.Position = 0;
                await using var stream = new FileStream(tempFileName, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);

                // Copy memory stream directly to file stream
                await ms.CopyToAsync(stream);
                await stream.FlushAsync();
                stream.Close();

                // Atomic replace with backup
                // File.Replace automatically creates a backup and atomically replaces the file
                if (File.Exists(fileName))
                {
                    File.Replace(tempFileName, fileName, backupFileName, ignoreMetadataErrors: true);
                }
                else
                {
                    // First time save, no backup needed
                    File.Move(tempFileName, fileName, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                // Log error
                Logger.Error(ex, "Error saving settings");

                // Try to restore from backup if available
                var backupFileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "settings.xml.bak");
                if (File.Exists(backupFileName))
                {
                    try
                    {
                        var fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "settings.xml");
                        File.Copy(backupFileName, fileName, overwrite: true);
                    }
                    catch
                    {
                        // Failed to restore backup
                    }
                }
                throw;
            }
            finally
            {
                _saveSemaphore.Release();
            }
        }

        public void LoadSettings()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel");
            var fileName = Path.Combine(folder, "settings.xml");
            var backupFileName = Path.Combine(folder, "settings.xml.bak");

            bool loadedFromBackup = false;
            string fileToLoad = fileName;

            // Try to load the main settings file first
            if (!TryLoadSettingsFromFile(fileName))
            {
                // If main file fails, try backup
                if (File.Exists(backupFileName) && TryLoadSettingsFromFile(backupFileName))
                {
                    loadedFromBackup = true;
                    fileToLoad = backupFileName;

                    // Try to restore the backup to the main file
                    try
                    {
                        File.Copy(backupFileName, fileName, overwrite: true);
                        Logger.Information("Settings restored from backup file.");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to restore backup");
                    }
                }
            }

            // Actually load the settings if successful
            if (File.Exists(fileToLoad))
            {
                XmlSerializer xs = new XmlSerializer(typeof(Settings));
                using var rd = XmlReader.Create(fileToLoad);
                try
                {
                    if (xs.Deserialize(rd) is Settings settings)
                    {
                        lock (_settingsLock)
                        {
                            Settings.UiWidth = settings.UiWidth;
                            Settings.UiHeight = settings.UiHeight;
                            Settings.UiScale = settings.UiScale;
                            Settings.IsPaneOpen = settings.IsPaneOpen;
                            Settings.AutoStart = settings.AutoStart;
                            Settings.AutoStartDelay = settings.AutoStartDelay;
                            Settings.StartMinimized = settings.StartMinimized;
                            Settings.MinimizeToTray = settings.MinimizeToTray;

                            Settings.SelectedItemColor = settings.SelectedItemColor;
                            Settings.ShowGridLines = settings.ShowGridLines;
                            Settings.GridLinesColor = settings.GridLinesColor;
                            Settings.GridLinesSpacing = settings.GridLinesSpacing;

                            Settings.LibreHardwareMonitor = settings.LibreHardwareMonitor;
                            Settings.WebServer = settings.WebServer;
                            Settings.WebServerListenIp = settings.WebServerListenIp;
                            Settings.WebServerListenPort = settings.WebServerListenPort;
                            Settings.WebServerRefreshRate = settings.WebServerRefreshRate;
                            Settings.TargetFrameRate = settings.TargetFrameRate;
                            Settings.TargetGraphUpdateRate = settings.TargetGraphUpdateRate;
                            Settings.Version = settings.Version;

                            // Load BeadaPanel multi-device settings
                            Settings.BeadaPanelMultiDeviceMode = settings.BeadaPanelMultiDeviceMode;

                            // Clear existing devices and add loaded ones
                            Settings.BeadaPanelDevices.Clear();
                            foreach (var device in settings.BeadaPanelDevices)
                            {
                                Settings.BeadaPanelDevices.Add(device);
                            }

                            Settings.TuringPanelMultiDeviceMode = settings.TuringPanelMultiDeviceMode;

                            Settings.TuringPanelDevices.Clear();
                            foreach (var device in settings.TuringPanelDevices)
                            {
                                Settings.TuringPanelDevices.Add(device);
                            }
                        }

                        ValidateStartup();

                        if (loadedFromBackup)
                        {
                            Log.Information("Settings loaded from backup file.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error loading settings");
                }
            }
        }

        private bool TryLoadSettingsFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            try
            {
                XmlSerializer xs = new XmlSerializer(typeof(Settings));
                using var rd = XmlReader.Create(filePath);
                var testSettings = xs.Deserialize(rd) as Settings;
                return testSettings != null;
            }
            catch
            {
                return false;
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

        private void Upgrade_File_Structure_For_1_3_1()
        {
            //validate devices models for enum change in versions >1.3.0
            foreach (TuringPanelDevice device in Settings.TuringPanelDevices)
            {
                if (device.Model == "REV_8INCH_USB" && device.DeviceId.StartsWith(@"USB\VID_1CBE&PID_0088\"))
                {
                    device.Model = TuringPanelModel.REV_88INCH_USB.ToString();
                }
            }
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

        /// <summary>
        /// Cleanup resources when application shuts down
        /// </summary>
        public void Cleanup()
        {
            // Dispose the debounce timer
            _saveDebounceTimer?.Dispose();
            _saveDebounceTimer = null;

            // Ensure any pending saves are completed
            try
            {
                SaveSettingsAsync(batch: false).Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best effort - don't throw on shutdown
            }

            // Dispose the semaphore
            _saveSemaphore?.Dispose();
        }
    }
}
