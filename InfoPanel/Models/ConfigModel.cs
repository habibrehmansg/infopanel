using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

namespace InfoPanel
{
    public sealed class ConfigModel : ObservableObject
    {
        private const int CurrentVersion = 123;
        private const string RegistryRunKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        private static readonly Lazy<ConfigModel> lazy = new Lazy<ConfigModel>(() => new ConfigModel());

        public static ConfigModel Instance { get { return lazy.Value; } }

        public ObservableCollection<Profile> Profiles { get; private set; }
        private readonly object _profilesLock = new object();

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

            Profiles = new ObservableCollection<Profile>();
            Profiles.CollectionChanged += Profiles_CollectionChanged;

            LoadProfiles();

            Settings.PropertyChanged += Settings_PropertyChanged;

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
                if (e.PropertyName == nameof(Profile.Active) || e.PropertyName == nameof(Profile.CompatMode))
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
            var registry = Registry.CurrentUser;

            if (registry != null)
            {
                using (var registryKey = registry.OpenSubKey(RegistryRunKey, true))
                {
                    if (registryKey != null)
                    {
                        if (Settings.AutoStart)
                        {
                            registryKey.SetValue("InfoPanel", "\"" + Application.ExecutablePath + "\"");
                        }
                        else
                        {
                            registryKey.DeleteValue("InfoPanel", false);
                        }
                    }

                }
            }
        }

        private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.AutoStart))
            {
                ValidateStartup();
            }
            else if (e.PropertyName == nameof(Settings.BeadaPanel))
            {
                if (Settings.BeadaPanel)
                {
                    BeadaPanelTask.Instance.Start();
                }
                else
                {
                    BeadaPanelTask.Instance.Stop();
                }
            }
            else if (e.PropertyName == nameof(Settings.TuringPanelA))
            {
                if (Settings.TuringPanelA)
                {
                    TuringPanelATask.Instance.Start();
                }
                else
                {
                    TuringPanelATask.Instance.Stop();
                }
            }
            else if (e.PropertyName == nameof(Settings.TuringPanelC))
            {
                if (Settings.TuringPanelC)
                {
                    TuringPanelCTask.Instance.Start();
                }
                else
                {
                    TuringPanelCTask.Instance.Stop();
                }
            }
            else if (e.PropertyName == nameof(Settings.WebServer))
            {
                if (Settings.WebServer)
                {
                    WebServerTask.Instance.Start();
                }
                else
                {
                    WebServerTask.Instance.Stop();
                }
            }

            SaveSettings();
        }

        public List<Profile> GetProfilesCopy()
        {
            lock (_profilesLock)
            {
                return Profiles.ToList();
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
                XmlSerializer xs = new XmlSerializer(typeof(Settings));

                var settings = new XmlWriterSettings() { Encoding = Encoding.UTF8, Indent = true };
                using (var wr = XmlWriter.Create(fileName, settings))
                {
                    xs.Serialize(wr, Settings);
                }
            }
        }

        public void LoadSettings()
        {

            var fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "settings.xml");
            if (File.Exists(fileName))
            {
                XmlSerializer xs = new XmlSerializer(typeof(Settings));
                using (var rd = XmlReader.Create(fileName))
                {
                    try
                    {
                        var settings = xs.Deserialize(rd) as Settings;

                        if (settings != null)
                        {
                            lock (_settingsLock)
                            {
                                Settings.AutoStart = settings.AutoStart;
                                Settings.StartMinimized = settings.StartMinimized;
                                Settings.MinimizeToTray = settings.MinimizeToTray;
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
                                Settings.TuringPanelA = settings.TuringPanelA;
                                Settings.TuringPanelAProfile = settings.TuringPanelAProfile;
                                Settings.TuringPanelAPort = settings.TuringPanelAPort;
                                Settings.TuringPanelARotation = settings.TuringPanelARotation;
                                Settings.TuringPanelC = settings.TuringPanelC;
                                Settings.TuringPanelCProfile = settings.TuringPanelCProfile;
                                Settings.TuringPanelCPort = settings.TuringPanelCPort;
                                Settings.TuringPanelCRotation = settings.TuringPanelCRotation;
                            }

                            ValidateStartup();
                        }
                    }
                    catch { }
                }
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

        private List<Profile>? LoadProfilesFromFile()
        {
            var fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "profiles.xml");
            if (File.Exists(fileName))
            {
                XmlSerializer xs = new XmlSerializer(typeof(List<Profile>));
                using (var rd = XmlReader.Create(fileName))
                {
                    try
                    {
                        return xs.Deserialize(rd) as List<Profile>;
                    }
                    catch { }
                }
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
                    XmlSerializer xs = new XmlSerializer(typeof(List<DisplayItem>),
                       new Type[] { typeof(BarDisplayItem), typeof(GraphDisplayItem), typeof(SensorDisplayItem), typeof(ClockDisplayItem), typeof(CalendarDisplayItem), typeof(TextDisplayItem), typeof(ImageDisplayItem) });

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
                        using (var wr = XmlWriter.Create(file, settings))
                        {
                            xs.Serialize(wr, displayItems);
                        }
                    }
                }
            }
        }
    }
}
