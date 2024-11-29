using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Serialization;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Diagnostics;
using System.Windows.Navigation;
using System.Windows.Threading;
using InfoPanel.Views.Common;

namespace InfoPanel
{
    public sealed class SharedModel : ObservableObject
    {
        private static readonly Lazy<SharedModel> lazy = new(() => new SharedModel());

        public static SharedModel Instance { get { return lazy.Value; } }

        private bool _hwInfoAvailable = false;

        public bool HwInfoAvailable
        {
            get { return _hwInfoAvailable; }
            set
            {
                SetProperty(ref _hwInfoAvailable, value);
            }
        }

        private int _currentFrameRate = 0;
        public int CurrentFrameRate
        {
            get { return _currentFrameRate; }
            set
            {
                SetProperty(ref _currentFrameRate, value);
            }
        }

        private long _currentFrameTime = 0;
        public long CurrentFrameTime
        {
            get { return _currentFrameTime; }
            set
            {
                SetProperty(ref _currentFrameTime, value);
                OnPropertyChanged(nameof(PerformanceRating));
            }
        }


        public string PerformanceRating
        {
            get
            {
                if (_currentFrameTime <= 33)
                {
                    return "Excellent";
                }

                if (_currentFrameTime <= 42)
                {
                    return "Very Good";
                }

                if (_currentFrameTime <= 67)
                {
                    return "Good";
                }

                if (_currentFrameTime <= 100)
                {
                    return "Average";
                }

                return "Poor";
            }
        }

        private bool _placementControlExpanded = false;
        public bool PlacementControlExpanded
        {
            get { return _placementControlExpanded; }
            set
            {
                SetProperty(ref _placementControlExpanded, value);
            }
        }

        private Profile? _selectedProfile;

        public Profile? SelectedProfile
        {
            get { return _selectedProfile; }
            set
            {
                if (_selectedProfile != value)
                {
                    SetProperty(ref _selectedProfile, value);
                    OnPropertyChanged(nameof(DisplayItems));
                }
            }
        }

        private ConcurrentDictionary<Guid, ObservableCollection<DisplayItem>> ProfileDisplayItems = new ConcurrentDictionary<Guid, ObservableCollection<DisplayItem>>();

        public ObservableCollection<DisplayItem>? DisplayItems
        {
            get
            {
                if (SelectedProfile != null)
                {
                    return GetProfileDisplayItems(SelectedProfile);
                }
                else { return null; }
            }
        }
        private readonly object _displayItemsLock = new object();

        private DisplayItem? _selectedItem;
        public DisplayItem? SelectedItem
        {
            get { return _selectedItem; }
            set
            {
                SetProperty(ref _selectedItem, value);
                IsItemSelected = value != null;
            }
        }

        public List<DisplayItem>? SelectedItems
        {
            get
            {
                return DisplayItems?.Where(item => item.Selected).ToList();                
            }
        }

        public List<DisplayItem>? SelectedVisibleItems
        {
            get
            {
                return DisplayItems?.Where(item => item.Selected && !item.Hidden).ToList();
            }
        }

        private bool _isItemSelected;
        public bool IsItemSelected
        {
            get { return _isItemSelected; }
            set
            {
                SetProperty(ref _isItemSelected, value);
            }
        }

        private int _moveValue = 5;
        public int MoveValue
        {
            get { return _moveValue; }
            set
            {
                SetProperty(ref _moveValue, value);
            }
        }

        private SharedModel()
        { }

        public void AddDisplayItem(DisplayItem displayItem)
        {
            lock (_displayItemsLock)
            {
                DisplayItems?.Add(displayItem);
            }
        }

        public void RemoveDisplayItem(DisplayItem displayItem)
        {
            lock (_displayItemsLock)
            {
                DisplayItems?.Remove(displayItem);
            }
        }

        public void PushDisplayItemBy(DisplayItem displayItem, int count)
        {
            lock (_displayItemsLock)
            {
                if (DisplayItems == null)
                {
                    return;
                }

                var index = DisplayItems.IndexOf(displayItem);

                var newIndex = index + count;

                if (newIndex >= 0 && newIndex < DisplayItems.Count)
                {
                    DisplayItems.Move(index, newIndex);
                }
            }
        }

        public void PushDisplayItemTo(DisplayItem displayItem, DisplayItem target)
        {
            lock (_displayItemsLock)
            {
                if (DisplayItems == null) { return; }
                var index = DisplayItems.IndexOf(displayItem);
                var targetIndex = DisplayItems.IndexOf(target);
                DisplayItems.Move(index, targetIndex + 1);
            }
        }

        public void PushDisplayItemTo(DisplayItem displayItem, int newIndex)
        {
            lock (_displayItemsLock)
            {
                if (DisplayItems == null) { return; }
                var index = DisplayItems.IndexOf(displayItem);
                DisplayItems.Move(index, newIndex);
            }
        }

        public BitmapSource BitmapSource { get; set; }

        public void UpdatePanel(Profile profile, Bitmap bitmap)
        {
            if (Application.Current is App app)
            {
                var window = app.GetDisplayWindow(profile);

                if (window is DisplayWindow displayWindow && !window.Direct2DMode)
                {
                    var writeableBitmap = displayWindow?.WriteableBitmap;

                    if (writeableBitmap != null)
                    {
                        IntPtr backBuffer = IntPtr.Zero;

                        writeableBitmap.Dispatcher.Invoke(() =>
                         {
                             if (writeableBitmap.Width == bitmap.Width && writeableBitmap.Height == bitmap.Height)
                             {
                                 writeableBitmap.Lock();
                                 backBuffer = writeableBitmap.BackBuffer;
                             }
                         });

                        if (backBuffer == IntPtr.Zero)
                        {
                            return;
                        }

                        // copy the pixel data from the bitmap to the back buffer
                        BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        int stride = bitmapData.Stride;
                        byte[] pixels = new byte[stride * bitmap.Height];
                        Marshal.Copy(bitmapData.Scan0, pixels, 0, pixels.Length);
                        Marshal.Copy(pixels, 0, backBuffer, pixels.Length);
                        bitmap.UnlockBits(bitmapData);

                        writeableBitmap.Dispatcher.Invoke(() =>
                        {
                            writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, writeableBitmap.PixelWidth, writeableBitmap.PixelHeight));
                            writeableBitmap.Unlock();
                        });
                    }
                }
            }
        }

        public void SetPanelBitmap(Profile profile, Bitmap bitmap)
        {
            if (profile.Active)
            {
                UpdatePanel(profile, bitmap);
            }

            if (ConfigModel.Instance.Settings.BeadaPanel)
            {
                if(ConfigModel.Instance.Settings.BeadaPanelProfile == profile.Guid)
                {
                    BeadaPanelTask.Instance.UpdateBuffer(bitmap);
                }
            }

            if(ConfigModel.Instance.Settings.TuringPanelA)
            {
                if (ConfigModel.Instance.Settings.TuringPanelAProfile == profile.Guid)
                {
                    TuringPanelATask.Instance.UpdateBuffer(bitmap);
                }
            }

            if (ConfigModel.Instance.Settings.TuringPanelC)
            {
                if (ConfigModel.Instance.Settings.TuringPanelCProfile == profile.Guid)
                {
                    TuringPanelCTask.Instance.UpdateBuffer(bitmap);
                }
            }

            if (ConfigModel.Instance.Settings.WebServer)
            {
                WebServerTask.Instance.UpdateBuffer(profile, bitmap);
            }
        }

        private void SaveDisplayItems(Profile profile, List<DisplayItem> displayItems)
        {
            var profileFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "profiles");
            if (!Directory.Exists(profileFolder))
            {
                Directory.CreateDirectory(profileFolder);
            }
            var fileName = Path.Combine(profileFolder, profile.Guid + ".xml");

            XmlSerializer xs = new XmlSerializer(typeof(List<DisplayItem>), new Type[] { typeof(BarDisplayItem), typeof(GraphDisplayItem), typeof(DonutDisplayItem), typeof(SensorDisplayItem), typeof(TextDisplayItem), typeof(ClockDisplayItem), typeof(CalendarDisplayItem), typeof(ImageDisplayItem), typeof(GaugeDisplayItem) });

            var settings = new XmlWriterSettings() { Encoding = Encoding.UTF8, Indent = true };
            using (var wr = XmlWriter.Create(fileName, settings))
            {
                xs.Serialize(wr, displayItems);
            }
        }

        public void SaveDisplayItems(Profile profile)
        {
            if (profile != null)
            {
                var displayItems = GetProfileDisplayItemsCopy(profile);
                SaveDisplayItems(profile, displayItems);

                //image cleanup
                var assetFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "assets", profile.Guid.ToString());
                if (Directory.Exists(assetFolder))
                {
                    var assetFiles = Directory.GetFiles(assetFolder).ToList();

                    foreach (var item in displayItems)
                    {
                        if (item is ImageDisplayItem imageDisplayItem)
                        {
                            if (imageDisplayItem.CalculatedPath != null)
                            {
                                assetFiles.Remove(imageDisplayItem.CalculatedPath);
                            }
                        }
                        else if (item is GaugeDisplayItem gaugeDisplayItem)
                        {
                            foreach (var image in gaugeDisplayItem.Images)
                            {
                                if (image.CalculatedPath != null)
                                {
                                    assetFiles.Remove(image.CalculatedPath);
                                }
                            }
                        }
                    }

                    foreach (var assetFile in assetFiles)
                    {
                        try
                        {
                            File.Delete(assetFile);
                        }
                        catch { }
                    }
                }
            }
        }

        public void SaveDisplayItems()
        {
            if(SelectedProfile != null)
            SaveDisplayItems(SelectedProfile);
        }

        public string? ExportProfile(Profile profile, string outputFolder)
        {
            var SelectedProfile = profile;

            if (SelectedProfile != null)
            {
                var exportFilePath = Path.Combine(outputFolder, SelectedProfile.Name.Replace(" ", "_") + "-" + DateTimeOffset.Now.ToUnixTimeSeconds() + ".infopanel");

                if (File.Exists(exportFilePath))
                {
                    File.Delete(exportFilePath);
                }

                using (ZipArchive archive = ZipFile.Open(exportFilePath, ZipArchiveMode.Create))
                {
                    //add profile settings
                    var exportProfile = new Profile(SelectedProfile.Name, SelectedProfile.Width, SelectedProfile.Height);
                    exportProfile.BackgroundColor = SelectedProfile.BackgroundColor;
                    exportProfile.Font = SelectedProfile.Font;
                    exportProfile.FontSize = SelectedProfile.FontSize;
                    exportProfile.Color = SelectedProfile.Color;

                    var entry = archive.CreateEntry("Profile.xml");

                    using (Stream entryStream = entry.Open())
                    {
                        XmlSerializer xs = new XmlSerializer(typeof(Profile));
                        var settings = new XmlWriterSettings() { Encoding = Encoding.UTF8, Indent = true };
                        using (var wr = XmlWriter.Create(entryStream, settings))
                        {
                            xs.Serialize(wr, exportProfile);
                        }
                    }

                    //add displayitems
                    var profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "profiles", SelectedProfile.Guid + ".xml");
                    if(File.Exists(profilePath))
                    {
                        archive.CreateEntryFromFile(profilePath, "DisplayItems.xml");
                    }

                    //add assets
                    var assetFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "assets", SelectedProfile.Guid.ToString());

                    if (Directory.Exists(assetFolder))
                    {
                        foreach (var file in Directory.GetFiles(assetFolder))
                        {
                            string entryName = file.Substring(assetFolder.Length + 1);
                            archive.CreateEntryFromFile(file, Path.Combine("assets", entryName));
                        }
                    }
                }

                return exportFilePath;
            }

            return null;
        }

        public void ImportProfile(string importPath)
        {
            using (ZipArchive archive = ZipFile.OpenRead(importPath))
            {
                ZipArchiveEntry? profileEntry = null;
                ZipArchiveEntry? displayItemsEntry = null;
                List<ZipArchiveEntry> assets = new List<ZipArchiveEntry>();

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.Equals("Profile.xml"))
                    {
                        profileEntry = entry;
                    }
                    else if (entry.FullName.Equals("DisplayItems.xml"))
                    {
                        displayItemsEntry = entry;
                    }
                    else if (entry.FullName.StartsWith("assets\\"))
                    {
                        assets.Add(entry);
                    }
                }

                if (profileEntry != null && displayItemsEntry != null)
                {
                    //read profile settings
                    Profile? profile = null;
                    using (Stream entryStream = profileEntry.Open())
                    {
                        XmlSerializer xs = new XmlSerializer(typeof(Profile));
                        using (var rd = XmlReader.Create(entryStream))
                        {
                            profile = xs.Deserialize(rd) as Profile;
                        }
                    }

                    if (profile != null)
                    {
                        //change profile GUID & Name
                        profile.Guid = Guid.NewGuid();
                        profile.Name = "[Import] " + profile.Name;

                        //extract displayitems
                        var profileFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "profiles");
                        if (!Directory.Exists(profileFolder))
                        {
                            Directory.CreateDirectory(profileFolder);
                        }
                        var profilePath = Path.Combine(profileFolder, profile.Guid + ".xml");
                        displayItemsEntry.ExtractToFile(profilePath);

                        //smart import
                        var displayItems = LoadDisplayItemsFromFile(profile);
                        if (displayItems != null)
                        {
                            foreach (DisplayItem displayItem in displayItems)
                            {
                                if (displayItem is SensorDisplayItem sensorDisplayItem)
                                {
                                    if (!HWHash.SENSORHASH.TryGetValue((sensorDisplayItem.Id, sensorDisplayItem.Instance, sensorDisplayItem.EntryId), out _))
                                    {
                                        var hash = HWHash.GetOrderedList().Find(hash => hash.NameDefault.Equals(sensorDisplayItem.SensorName));
                                        if (hash.NameDefault != null)
                                        {
                                            sensorDisplayItem.Id = hash.ParentID;
                                            sensorDisplayItem.Instance = hash.ParentInstance;
                                            sensorDisplayItem.EntryId = hash.SensorID;
                                            Trace.WriteLine("Smart imported " + hash.NameDefault);
                                        }
                                    }
                                }
                                else if (displayItem is ChartDisplayItem chartDisplayItem)
                                {
                                    if (!HWHash.SENSORHASH.TryGetValue((chartDisplayItem.Id, chartDisplayItem.Instance, chartDisplayItem.EntryId), out _))
                                    {
                                        var hash = HWHash.GetOrderedList().Find(hash => hash.NameDefault.Equals(chartDisplayItem.SensorName));
                                        if (hash.NameDefault != null)
                                        {
                                            chartDisplayItem.Id = hash.ParentID;
                                            chartDisplayItem.Instance = hash.ParentInstance;
                                            chartDisplayItem.EntryId = hash.SensorID;
                                            Trace.WriteLine("Smart imported " + hash.NameDefault);
                                        }
                                    }
                                }
                                else if (displayItem is GaugeDisplayItem gaugeDisplayItem)
                                {
                                    if (!HWHash.SENSORHASH.TryGetValue((gaugeDisplayItem.Id, gaugeDisplayItem.Instance, gaugeDisplayItem.EntryId), out _))
                                    {
                                        var hash = HWHash.GetOrderedList().Find(hash => hash.NameDefault.Equals(gaugeDisplayItem.SensorName));
                                        if (hash.NameDefault != null)
                                        {
                                            gaugeDisplayItem.Id = hash.ParentID;
                                            gaugeDisplayItem.Instance = hash.ParentInstance;
                                            gaugeDisplayItem.EntryId = hash.SensorID;
                                            Trace.WriteLine("Smart imported " + hash.NameDefault);
                                        }
                                    }
                                }
                            }
                            //save it back
                            SaveDisplayItems(profile, displayItems);
                        }

                        //extract assets
                        var assetFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "assets", profile.Guid.ToString());
                        if (!Directory.Exists(assetFolder))
                        {
                            Directory.CreateDirectory(assetFolder);
                        }
                        foreach (var asset in assets)
                        {
                            var assetPath = Path.Combine(assetFolder, asset.Name);
                            asset.ExtractToFile(assetPath);
                        }

                        //add profile
                        ConfigModel.Instance.AddProfile(profile);
                        ConfigModel.Instance.SaveProfiles();
                        SharedModel.Instance.SelectedProfile = profile;
                    }
                }
            }
        }

        private ObservableCollection<DisplayItem> GetProfileDisplayItems(Profile profile)
        {
            ObservableCollection<DisplayItem>? displayItems;

            if (!ProfileDisplayItems.TryGetValue(profile.Guid, out displayItems))
            {
                LoadDisplayItems(profile);
                displayItems = ProfileDisplayItems[profile.Guid];
            }

            return displayItems;
        }

        public List<DisplayItem> GetProfileDisplayItemsCopy()
        {
            lock (_displayItemsLock)
            {
                if (SelectedProfile == null) { return new List<DisplayItem>(); }
                return GetProfileDisplayItems(SelectedProfile).ToList();
            }
        }

        public List<DisplayItem> GetProfileDisplayItemsCopy(Profile profile)
        {
            lock (_displayItemsLock)
            {
                return GetProfileDisplayItems(profile).ToList();
            }
        }

        public void LoadDisplayItems()
        {
            LoadDisplayItems(SelectedProfile);
        }

        public void LoadDisplayItems(Profile profile)
        {
            ObservableCollection<DisplayItem>? displayItems;

            if (!ProfileDisplayItems.TryGetValue(profile.Guid, out displayItems))
            {
                displayItems = new ObservableCollection<DisplayItem>();
                ProfileDisplayItems[profile.Guid] = displayItems;
            }

            lock (_displayItemsLock)
            {
                displayItems.Clear();
                LoadDisplayItemsFromFile(profile)?.ForEach(item =>
                {
                    if (item is ImageDisplayItem)
                    {
                        item.ProfileGuid = profile.Guid;
                    }
                    else if (item is GaugeDisplayItem gaugeDisplayItem)
                    {
                        foreach (ImageDisplayItem imageDisplayItem in gaugeDisplayItem.Images)
                        {
                            imageDisplayItem.ProfileGuid = profile.Guid;
                        }
                    }

                    displayItems.Add(item);
                });
            }
        }

        private List<DisplayItem>? LoadDisplayItemsFromFile(Profile profile)
        {
            var fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "profiles", profile.Guid + ".xml");
            if (File.Exists(fileName))
            {
                XmlSerializer xs = new XmlSerializer(typeof(List<DisplayItem>),
                    new Type[] { typeof(BarDisplayItem), typeof(GraphDisplayItem), typeof(DonutDisplayItem), typeof(SensorDisplayItem), typeof(ClockDisplayItem), typeof(CalendarDisplayItem), typeof(TextDisplayItem), typeof(ImageDisplayItem), typeof(GaugeDisplayItem) });

                using var rd = XmlReader.Create(fileName);
                try
                {
                    return xs.Deserialize(rd) as List<DisplayItem>;
                }
                catch { }
            }

            return null;
        }
    }

}
