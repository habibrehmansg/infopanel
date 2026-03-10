using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Drawing;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Services;
using InfoPanel.Utils;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace InfoPanel
{
    public partial class SharedModel : ObservableObject
    {
        private static readonly ILogger Logger = Log.ForContext<SharedModel>();
        private static readonly Lazy<SharedModel> Lazy = new(() => new SharedModel());

        public static SharedModel Instance => Lazy.Value;

        private bool _hwInfoAvailable = false;
        private bool _isUndoRedoInProgress;
        private bool _isDirty;
        private readonly ConcurrentDictionary<Guid, Debouncer> _propertyChangeDebouncers = [];

        public bool IsDirty => _isDirty;
        public void MarkDirty() { _isDirty = true; OnPropertyChanged(nameof(IsDirty)); }
        public void ClearDirty() { _isDirty = false; OnPropertyChanged(nameof(IsDirty)); }

        public bool HwInfoAvailable
        {
            get => _hwInfoAvailable;
            set => SetProperty(ref _hwInfoAvailable, value);
        }

        private int _webserverFrameRate = 0;
        public int WebserverFrameRate
        {
            get => _webserverFrameRate;
            set => SetProperty(ref _webserverFrameRate, value);
        }

        private int _webserverFrameTime = 0;
        public int WebserverFrameTime
        {
            get => _webserverFrameTime;
            set => SetProperty(ref _webserverFrameTime, value);
        }

        private bool _placementControlExpanded = false;
        public bool PlacementControlExpanded
        {
            get => _placementControlExpanded;
            set => SetProperty(ref _placementControlExpanded, value);
        }

        private Profile? _selectedProfile;

        public Profile? SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                SetProperty(ref _selectedProfile, value);
                OnPropertyChanged(nameof(DisplayItems));
                NotifySelectedItemChange();
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
            }
        }

        private readonly ConcurrentDictionary<Guid, ObservableCollection<DisplayItem>> ProfileDisplayItems = [];
        private readonly ConcurrentDictionary<Guid, Debouncer> _debouncers = [];
        private readonly ConcurrentDictionary<Guid, ImmutableList<DisplayItem>> ProfileDisplayItemsCopy = [];

        public ObservableCollection<DisplayItem> DisplayItems => GetProfileDisplayItems();

        private ObservableCollection<DisplayItem> GetProfileDisplayItems()
        {
            if (SelectedProfile is Profile profile)
                return GetProfileDisplayItems(profile);
            return [];
        }

        private ObservableCollection<DisplayItem> GetProfileDisplayItems(Profile profile)
        {
            return ProfileDisplayItems.GetOrAdd(profile.Guid, guid =>
            {
                var collection = new ObservableCollection<DisplayItem>();
                collection.CollectionChanged += (s, e) =>
                {
                    if (s is ObservableCollection<DisplayItem> observableCollection)
                    {
                        var debouncer = _debouncers.GetOrAdd(guid, _ => new Debouncer());
                        debouncer.Debounce(() => ProfileDisplayItemsCopy[guid] = [.. observableCollection]);
                    }
                    if (e.NewItems != null)
                    {
                        foreach (var item in e.NewItems)
                            SubscribeDisplayItemForUndo(item as DisplayItem, profile);
                    }
                    if (e.OldItems != null)
                    {
                        foreach (var item in e.OldItems)
                            UnsubscribeDisplayItemForUndo(item as DisplayItem);
                    }
                };
                _ = ReloadDisplayItems(profile);
                return collection;
            });
        }

        private void SubscribeDisplayItemForUndo(DisplayItem? item, Profile profile)
        {
            if (item == null) return;
            item.PropertyChanged += DisplayItem_PropertyChanged;
            if (item is GroupDisplayItem group && group.DisplayItems != null)
            {
                group.DisplayItems.CollectionChanged += (_, ce) =>
                {
                    if (ce.NewItems != null)
                    {
                        foreach (var child in ce.NewItems)
                            SubscribeDisplayItemForUndo(child as DisplayItem, profile);
                    }
                    if (ce.OldItems != null)
                    {
                        foreach (var child in ce.OldItems)
                            UnsubscribeDisplayItemForUndo(child as DisplayItem);
                    }
                };
                foreach (var child in group.DisplayItems)
                    SubscribeDisplayItemForUndo(child, profile);
            }
        }

        private void UnsubscribeDisplayItemForUndo(DisplayItem? item)
        {
            if (item == null) return;
            item.PropertyChanged -= DisplayItem_PropertyChanged;
            if (item is GroupDisplayItem group && group.DisplayItems != null)
            {
                foreach (var child in group.DisplayItems)
                    UnsubscribeDisplayItemForUndo(child);
            }
        }

        private void DisplayItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isUndoRedoInProgress || SelectedProfile is not Profile profile) return;
            var debouncer = _propertyChangeDebouncers.GetOrAdd(profile.Guid, _ => new Debouncer());
            debouncer.Debounce(() =>
            {
                if (_isUndoRedoInProgress) return;
                var copy = GetProfileDisplayItemsCopy(profile);
                if (copy.Count > 0)
                {
                    UndoManager.Instance.PushUndo(profile, copy.ToList());
                    MarkDirty();
                }
            }, 400);
        }

        public bool CanUndo => SelectedProfile != null && UndoManager.Instance.CanUndo(SelectedProfile);
        public bool CanRedo => SelectedProfile != null && UndoManager.Instance.CanRedo(SelectedProfile);

        public async Task ReloadDisplayItems()
        {
            if (SelectedProfile is Profile profile)
                await ReloadDisplayItems(profile);
        }

        public void AccessDisplayItems(Action<ObservableCollection<DisplayItem>> action)
        {
            if (SelectedProfile is not Profile profile) return;
            AccessDisplayItems(profile, action);
        }

        public void AccessDisplayItems(Profile profile, Action<ObservableCollection<DisplayItem>> action)
        {
            var collection = GetProfileDisplayItems(profile);
            if (collection == null) return;
            if (Application.Current.Dispatcher is Dispatcher dispatcher)
            {
                if (dispatcher.CheckAccess())
                    action(collection);
                else
                    dispatcher.Invoke(() => action(collection));
            }
        }

        private async Task ReloadDisplayItems(Profile profile)
        {
            var displayItems = await LoadDisplayItemsAsync(profile);
            UndoManager.Instance.ClearHistory(profile);
            AccessDisplayItems(profile, collection =>
            {
                foreach (var item in collection)
                    UnsubscribeDisplayItemForUndo(item);
                collection.Clear();
                foreach (var item in displayItems)
                {
                    collection.Add(item);
                    SubscribeDisplayItemForUndo(item, profile);
                }
            });
        }

        public void Undo()
        {
            if (SelectedProfile is not Profile profile) return;
            var currentXml = UndoManager.SerializeDisplayItemsForProfile(GetProfileDisplayItemsCopy(profile).ToList());
            var items = UndoManager.Instance.Undo(profile, currentXml);
            if (items == null) return;
            _isUndoRedoInProgress = true;
            try
            {
                AccessDisplayItems(profile, collection =>
                {
                    foreach (var item in collection)
                        UnsubscribeDisplayItemForUndo(item);
                    collection.Clear();
                    foreach (var item in items)
                    {
                        collection.Add(item);
                        SubscribeDisplayItemForUndo(item, profile);
                    }
                });
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
            }
            finally
            {
                _isUndoRedoInProgress = false;
            }
        }

        public void Redo()
        {
            if (SelectedProfile is not Profile profile) return;
            var items = UndoManager.Instance.Redo(profile);
            if (items == null) return;
            _isUndoRedoInProgress = true;
            try
            {
                AccessDisplayItems(profile, collection =>
                {
                    foreach (var item in collection)
                        UnsubscribeDisplayItemForUndo(item);
                    collection.Clear();
                    foreach (var item in items)
                    {
                        collection.Add(item);
                        SubscribeDisplayItemForUndo(item, profile);
                    }
                });
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
            }
            finally
            {
                _isUndoRedoInProgress = false;
            }
        }

        public DisplayItem? SelectedItem
        {
            get => SelectedItems.FirstOrDefault();
            set
            {
                foreach (var selectedItem in SelectedItems)
                {
                    if (selectedItem != value)
                        selectedItem.Selected = false;
                }
                if (value is DisplayItem displayItem)
                    displayItem.Selected = true;
                NotifySelectedItemChange();
            }
        }

        public void NotifySelectedItemChange()
        {
            OnPropertyChanged(nameof(SelectedItem));
            OnPropertyChanged(nameof(IsItemSelected));
            OnPropertyChanged(nameof(IsSingleItemSelected));
            OnPropertyChanged(nameof(IsSelectedItemMovable));
            OnPropertyChanged(nameof(IsSelectedItemsMovable));
        }

        public ImmutableList<DisplayItem> SelectedItems
        {
            get
            {
                ImmutableList<DisplayItem> result = [];
                AccessDisplayItems(items =>
                {
                    result = result.AddRange(items
                        .SelectMany<DisplayItem, DisplayItem>(item =>
                            item is GroupDisplayItem group && group.DisplayItems is { } groupItems
                                ? groupItems.Prepend(group)
                                : [item])
                        .Where(item => item.Selected));
                });
                return result;
            }
        }

        public ImmutableList<DisplayItem> SelectedVisibleItems =>
            [.. SelectedItems.Where(item => item.Selected && !item.Hidden)];

        public bool IsSelectedItemsMovable => SelectedItems.FindAll(item => item is not GroupDisplayItem).Count > 0;
        public bool IsSelectedItemMovable => SelectedItem is not null && SelectedItem is not GroupDisplayItem;
        public bool IsItemSelected => SelectedItem != null;
        public bool IsSingleItemSelected => SelectedItems.Count == 1;

        [ObservableProperty]
        private int _moveValue = 5;

        private SharedModel() { }

        private void PushUndoSnapshot(Profile profile, ObservableCollection<DisplayItem> displayItems)
        {
            if (_isUndoRedoInProgress) return;
            UndoManager.Instance.PushUndo(profile, displayItems.ToList());
            MarkDirty();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        public void AddDisplayItem(DisplayItem newDisplayItem)
        {
            if (SelectedProfile is not Profile profile) return;
            AccessDisplayItems(profile, displayItems =>
            {
                PushUndoSnapshot(profile, displayItems);
                bool addedInGroup = false;
                if (newDisplayItem is not GroupDisplayItem && SelectedItem is DisplayItem selectedItem)
                {
                    if (selectedItem is GroupDisplayItem group && !group.IsLocked)
                    {
                        group.DisplayItems.Add(newDisplayItem);
                        addedInGroup = true;
                    }
                    else
                    {
                        _ = FindParentCollection(selectedItem, out var parentGroup);
                        if (parentGroup is not null && !parentGroup.IsLocked)
                        {
                            parentGroup.DisplayItems.Add(newDisplayItem);
                            addedInGroup = true;
                        }
                    }
                }
                if (!addedInGroup)
                    displayItems.Add(newDisplayItem);
                SelectedItem = newDisplayItem;
            });
        }

        public void RemoveDisplayItem(DisplayItem displayItem)
        {
            if (SelectedProfile is not Profile profile) return;
            AccessDisplayItems(profile, displayItems =>
            {
                PushUndoSnapshot(profile, displayItems);
                _ = FindParentCollection(displayItem, out var parentGroup);
                if (parentGroup is not null)
                {
                    int index = parentGroup.DisplayItems.IndexOf(displayItem);
                    if (index >= 0)
                    {
                        parentGroup.DisplayItems.RemoveAt(index);
                        if (parentGroup.DisplayItems.Count > 0)
                            parentGroup.DisplayItems[Math.Clamp(index, 0, parentGroup.DisplayItems.Count - 1)].Selected = true;
                        else
                            parentGroup.Selected = true;
                    }
                }
                else
                {
                    int index = displayItems.IndexOf(displayItem);
                    if (index >= 0)
                    {
                        displayItems.RemoveAt(index);
                        if (displayItems.Count > 0)
                            displayItems[Math.Clamp(index, 0, displayItems.Count - 1)].Selected = true;
                    }
                }
            });
        }

        public GroupDisplayItem? GetParent(DisplayItem displayItem)
        {
            FindParentCollection(displayItem, out var result);
            return result;
        }

        private ObservableCollection<DisplayItem>? FindParentCollection(DisplayItem item, out GroupDisplayItem? parentGroup)
        {
            parentGroup = null;
            if (DisplayItems == null) return null;
            if (DisplayItems.Contains(item)) return DisplayItems;
            foreach (var group in DisplayItems.OfType<GroupDisplayItem>())
            {
                if (group.DisplayItems != null && group.DisplayItems.Contains(item))
                {
                    parentGroup = group;
                    return group.DisplayItems;
                }
            }
            return null;
        }

        public void PushDisplayItemBy(DisplayItem displayItem, int count)
        {
            if (SelectedProfile is not Profile profile) return;
            AccessDisplayItems(profile, displayItems =>
            {
                PushUndoSnapshot(profile, displayItems);
                var parentCollection = FindParentCollection(displayItem, out GroupDisplayItem? parentGroup);
                if (parentCollection == null) return;
                int index = parentCollection.IndexOf(displayItem);
                int newIndex = index + count;
                if (parentGroup != null && !parentGroup.IsLocked)
                {
                    if (newIndex < 0)
                    {
                        int groupIndex = displayItems.IndexOf(parentGroup);
                        if (groupIndex >= 0)
                        {
                            parentCollection.RemoveAt(index);
                            displayItems.Insert(groupIndex, displayItem);
                        }
                        return;
                    }
                    if (newIndex >= parentCollection.Count)
                    {
                        int groupIndex = displayItems.IndexOf(parentGroup);
                        if (groupIndex >= 0)
                        {
                            parentCollection.RemoveAt(index);
                            displayItems.Insert(groupIndex + 1, displayItem);
                        }
                        return;
                    }
                }
                if (displayItem is not GroupDisplayItem && parentGroup == null)
                {
                    int targetIndex = index + count;
                    if (targetIndex >= 0 && targetIndex < displayItems.Count)
                    {
                        var target = displayItems[targetIndex];
                        if (target is GroupDisplayItem targetGroup && !targetGroup.IsLocked && targetGroup.DisplayItems != null)
                        {
                            parentCollection.RemoveAt(index);
                            targetGroup.DisplayItems.Insert(count > 0 ? 0 : targetGroup.DisplayItems.Count, displayItem);
                            targetGroup.IsExpanded = true;
                            return;
                        }
                    }
                }
                if (newIndex >= 0 && newIndex < parentCollection.Count)
                {
                    parentCollection.Move(index, newIndex);
                    if (parentGroup is GroupDisplayItem g)
                        g.IsExpanded = true;
                }
            });
        }

        public void PushDisplayItemTo(DisplayItem displayItem, DisplayItem target)
        {
            if (displayItem == null || target == null || displayItem == target) return;
            if (SelectedProfile is not Profile profile) return;
            AccessDisplayItems(profile, displayItems =>
            {
                PushUndoSnapshot(profile, displayItems);
                var sourceCollection = FindParentCollection(displayItem, out var sourceGroupDisplayItem);
                var targetCollection = FindParentCollection(target, out var targetGroupDisplayItem);
                if (sourceCollection == null || targetCollection == null || sourceCollection != targetCollection) return;
                int sourceIndex = sourceCollection.IndexOf(displayItem);
                int targetIndex = targetCollection.IndexOf(target);
                if (sourceCollection == targetCollection)
                    sourceCollection.Move(sourceIndex, targetIndex + 1);
            });
        }

        public void PushDisplayItemToTop(DisplayItem displayItem)
        {
            if (SelectedProfile is not Profile profile) return;
            AccessDisplayItems(profile, displayItems =>
            {
                PushUndoSnapshot(profile, displayItems);
                var sourceCollection = FindParentCollection(displayItem, out var groupDisplayItem);
                if (sourceCollection == null) return;
                int currentIndex = sourceCollection.IndexOf(displayItem);
                if (sourceCollection == displayItems)
                {
                    if (currentIndex > 0) displayItems.Move(currentIndex, 0);
                }
                else
                {
                    if (groupDisplayItem != null && !groupDisplayItem.IsLocked)
                    {
                        sourceCollection.RemoveAt(currentIndex);
                        displayItems.Insert(0, displayItem);
                    }
                    else if (currentIndex != 0)
                    {
                        sourceCollection.Move(currentIndex, 0);
                    }
                }
            });
        }

        public void PushDisplayItemToEnd(DisplayItem displayItem)
        {
            if (SelectedProfile is not Profile profile) return;
            AccessDisplayItems(profile, displayItems =>
            {
                PushUndoSnapshot(profile, displayItems);
                var sourceCollection = FindParentCollection(displayItem, out var groupDisplayItem);
                if (sourceCollection == null) return;
                int currentIndex = sourceCollection.IndexOf(displayItem);
                if (sourceCollection == displayItems)
                {
                    if (currentIndex < displayItems.Count - 1) displayItems.Move(currentIndex, displayItems.Count - 1);
                }
                else
                {
                    if (groupDisplayItem != null && !groupDisplayItem.IsLocked)
                    {
                        sourceCollection.RemoveAt(currentIndex);
                        displayItems.Add(displayItem);
                    }
                    else if (currentIndex != sourceCollection.Count - 1)
                    {
                        sourceCollection.Move(currentIndex, sourceCollection.Count - 1);
                    }
                }
            });
        }

        public ImmutableList<DisplayItem> GetProfileDisplayItemsCopy()
        {
            if (SelectedProfile is Profile profile)
                return GetProfileDisplayItemsCopy(profile);
            return [];
        }

        public ImmutableList<DisplayItem> GetProfileDisplayItemsCopy(Profile profile)
        {
            if (!ProfileDisplayItemsCopy.TryGetValue(profile.Guid, out var displayItemsCopy))
            {
                AccessDisplayItems(profile, displayItems =>
                {
                    displayItemsCopy = [.. displayItems];
                    ProfileDisplayItemsCopy[profile.Guid] = displayItemsCopy;
                });
            }
            return displayItemsCopy ?? [];
        }

        private static void SaveDisplayItems(Profile profile, ICollection<DisplayItem> displayItems)
        {
            var profileFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "profiles");
            if (!Directory.Exists(profileFolder))
                Directory.CreateDirectory(profileFolder);
            var fileName = Path.Combine(profileFolder, profile.Guid + ".xml");
            var xs = new XmlSerializer(typeof(List<DisplayItem>), [typeof(GroupDisplayItem), typeof(BarDisplayItem), typeof(GraphDisplayItem), typeof(DonutDisplayItem), typeof(TableSensorDisplayItem), typeof(SensorDisplayItem), typeof(ClockDisplayItem), typeof(CalendarDisplayItem), typeof(TextDisplayItem), typeof(SensorImageDisplayItem), typeof(ImageDisplayItem), typeof(HttpImageDisplayItem), typeof(GaugeDisplayItem), typeof(ShapeDisplayItem)]);
            var settings = new XmlWriterSettings() { Encoding = Encoding.UTF8, Indent = true };
            using var wr = XmlWriter.Create(fileName, settings);
            xs.Serialize(wr, displayItems.ToList());
        }

        public void SaveDisplayItems(Profile profile)
        {
            if (profile != null)
            {
                var displayItems = GetProfileDisplayItemsCopy(profile);
                SaveDisplayItems(profile, displayItems);
                ClearDirty();
            }
        }

        public void SaveDisplayItems()
        {
            if (SelectedProfile != null)
            {
                SaveDisplayItems(SelectedProfile);
                ClearDirty();
            }
        }

        public static async Task<List<DisplayItem>> LoadDisplayItemsAsync(Profile profile)
        {
            return await Task.Run(() => LoadDisplayItemsFromFile(profile));
        }

        private static List<DisplayItem> LoadDisplayItemsFromFile(Profile profile)
        {
            var fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "profiles", profile.Guid + ".xml");
            if (File.Exists(fileName))
            {
                var xs = new XmlSerializer(typeof(List<DisplayItem>), [typeof(GroupDisplayItem), typeof(BarDisplayItem), typeof(GraphDisplayItem), typeof(DonutDisplayItem), typeof(TableSensorDisplayItem), typeof(SensorDisplayItem), typeof(ClockDisplayItem), typeof(CalendarDisplayItem), typeof(TextDisplayItem), typeof(SensorImageDisplayItem), typeof(ImageDisplayItem), typeof(HttpImageDisplayItem), typeof(GaugeDisplayItem), typeof(ShapeDisplayItem)]);
                using var rd = XmlReader.Create(fileName);
                try
                {
                    if (xs.Deserialize(rd) is List<DisplayItem> displayItems)
                    {
                        foreach (var displayItem in displayItems)
                            displayItem.SetProfile(profile);
                        return displayItems;
                    }
                }
                catch { }
            }
            return [];
        }

        /// <summary>
        /// Import a profile from an .infopanel file (zip archive).
        /// </summary>
        public void ImportProfile(string fileName)
        {
            if (!File.Exists(fileName)) return;
            try
            {
                using var archive = ZipFile.OpenRead(fileName);
                var extractPath = Path.Combine(Path.GetTempPath(), "InfoPanelImport", Guid.NewGuid().ToString());
                Directory.CreateDirectory(extractPath);
                try
                {
                    archive.ExtractToDirectory(extractPath);
                    var profilesFile = Path.Combine(extractPath, "profiles.xml");
                    if (File.Exists(profilesFile))
                    {
                        var xs = new XmlSerializer(typeof(List<Profile>));
                        using var rd = XmlReader.Create(profilesFile);
                        if (xs.Deserialize(rd) is List<Profile> importedProfiles)
                        {
                            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel");
                            var profilesFolder = Path.Combine(folder, "profiles");
                            var assetsFolder = Path.Combine(folder, "assets");
                            Directory.CreateDirectory(profilesFolder);
                            Directory.CreateDirectory(assetsFolder);
                            foreach (var profile in importedProfiles)
                            {
                                ConfigModel.Instance.AddProfile(profile);
                                var srcProfileXml = Path.Combine(extractPath, "profiles", profile.Guid + ".xml");
                                if (File.Exists(srcProfileXml))
                                    File.Copy(srcProfileXml, Path.Combine(profilesFolder, profile.Guid + ".xml"), true);
                                var srcAssets = Path.Combine(extractPath, "assets", profile.Guid.ToString());
                                if (Directory.Exists(srcAssets))
                                {
                                    var dstAssets = Path.Combine(assetsFolder, profile.Guid.ToString());
                                    if (!Directory.Exists(dstAssets)) Directory.CreateDirectory(dstAssets);
                                    foreach (var file in Directory.GetFiles(srcAssets))
                                        File.Copy(file, Path.Combine(dstAssets, Path.GetFileName(file)), true);
                                }
                            }
                            ConfigModel.Instance.SaveProfiles();
                            SharedModel.Instance.SelectedProfile = importedProfiles.FirstOrDefault();
                        }
                    }
                }
                finally
                {
                    if (Directory.Exists(extractPath))
                        Directory.Delete(extractPath, true);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to import profile from {FileName}", fileName);
                throw;
            }
        }

        /// <summary>
        /// Export a profile to an .infopanel file in the specified folder.
        /// Returns the path to the exported file, or null on failure.
        /// </summary>
        public string? ExportProfile(Profile profile, string folderPath)
        {
            if (profile == null || !Directory.Exists(folderPath)) return null;
            try
            {
                var exportPath = Path.Combine(Path.GetTempPath(), "InfoPanelExport", Guid.NewGuid().ToString());
                Directory.CreateDirectory(exportPath);
                try
                {
                    var profiles = new List<Profile> { profile };
                    var profilesFile = Path.Combine(exportPath, "profiles.xml");
                    var xs = new XmlSerializer(typeof(List<Profile>));
                    var settings = new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = true };
                    using (var wr = XmlWriter.Create(profilesFile, settings))
                        xs.Serialize(wr, profiles);
                    var profilesSubdir = Path.Combine(exportPath, "profiles");
                    Directory.CreateDirectory(profilesSubdir);
                    var displayItems = GetProfileDisplayItemsCopy(profile);
                    var diSerializer = new XmlSerializer(typeof(List<DisplayItem>), [typeof(GroupDisplayItem), typeof(BarDisplayItem), typeof(GraphDisplayItem), typeof(DonutDisplayItem), typeof(TableSensorDisplayItem), typeof(SensorDisplayItem), typeof(ClockDisplayItem), typeof(CalendarDisplayItem), typeof(TextDisplayItem), typeof(SensorImageDisplayItem), typeof(ImageDisplayItem), typeof(HttpImageDisplayItem), typeof(GaugeDisplayItem), typeof(ShapeDisplayItem)]);
                    using (var wr = XmlWriter.Create(Path.Combine(profilesSubdir, profile.Guid + ".xml"), settings))
                        diSerializer.Serialize(wr, displayItems.ToList());
                    var appAssets = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "assets", profile.Guid.ToString());
                    if (Directory.Exists(appAssets))
                    {
                        var exportAssets = Path.Combine(exportPath, "assets", profile.Guid.ToString());
                        Directory.CreateDirectory(exportAssets);
                        foreach (var file in Directory.GetFiles(appAssets))
                            File.Copy(file, Path.Combine(exportAssets, Path.GetFileName(file)), true);
                    }
                    var outFile = Path.Combine(folderPath, $"{profile.Name}.infopanel".Replace(" ", "_"));
                    if (File.Exists(outFile)) File.Delete(outFile);
                    ZipFile.CreateFromDirectory(exportPath, outFile);
                    return outFile;
                }
                finally
                {
                    if (Directory.Exists(exportPath))
                        Directory.Delete(exportPath, true);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to export profile {ProfileName}", profile.Name);
                return null;
            }
        }

        /// <summary>
        /// Import from Aida64 SensorPanel or RemoteSensor LCD format.
        /// </summary>
        public static async Task ImportSensorPanel(string fileName)
        {
            await Task.Run(() =>
            {
                // TODO: Implement SensorPanel/RSLCD import - format parsing required
                Logger.Warning("ImportSensorPanel not yet implemented for {FileName}", fileName);
            });
        }
    }
}
