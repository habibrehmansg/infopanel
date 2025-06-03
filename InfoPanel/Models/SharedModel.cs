using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace InfoPanel
{
    public partial class SharedModel : ObservableObject
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

        private bool _beadaPanelRunning = false;

        public bool BeadaPanelRunning
        {
            get { return _beadaPanelRunning; }
            set
            {
                SetProperty(ref _beadaPanelRunning, value);
            }
        }

        private int _beadaPanelFrameRate = 0;
        public int BeadaPanelFrameRate
        {
            get { return _beadaPanelFrameRate; }
            set
            {
                SetProperty(ref _beadaPanelFrameRate, value);
            }
        }

        private long _beadaPanelFrameTime = 0;
        public long BeadaPanelFrameTime
        {
            get { return _beadaPanelFrameTime; }
            set
            {
                SetProperty(ref _beadaPanelFrameTime, value);
            }
        }


        private bool _turingPanelRunning = false;

        public bool TuringPanelRunning
        {
            get { return _turingPanelRunning; }
            set
            {
                SetProperty(ref _turingPanelRunning, value);
            }
        }

        private int _turingPanelFrameRate = 0;
        public int TuringPanelFrameRate
        {
            get { return _turingPanelFrameRate; }
            set
            {
                SetProperty(ref _turingPanelFrameRate, value);
            }
        }

        private long _turingPanelFrameTime = 0;
        public long TuringPanelFrameTime
        {
            get { return _turingPanelFrameTime; }
            set
            {
                SetProperty(ref _turingPanelFrameTime, value);
            }
        }

        private bool _turingPanelARunning = false;

        public bool TuringPanelARunning
        {
            get { return _turingPanelARunning; }
            set
            {
                SetProperty(ref _turingPanelARunning, value);
            }
        }

        private int _turingPanelAFrameRate = 0;
        public int TuringPanelAFrameRate
        {
            get { return _turingPanelAFrameRate; }
            set
            {
                SetProperty(ref _turingPanelAFrameRate, value);
            }
        }

        private long _turingPanelAFrameTime = 0;
        public long TuringPanelAFrameTime
        {
            get { return _turingPanelAFrameTime; }
            set
            {
                SetProperty(ref _turingPanelAFrameTime, value);
            }
        }

        private bool _turingPanelCRunning = false;

        public bool TuringPanelCRunning
        {
            get { return _turingPanelCRunning; }
            set
            {
                SetProperty(ref _turingPanelCRunning, value);
            }
        }

        private int _turingPanelCFrameRate = 0;

        public int TuringPanelCFrameRate
        {
            get { return _turingPanelCFrameRate; }
            set
            {
                SetProperty(ref _turingPanelCFrameRate, value);
            }
        }

        private long _turingPanelCFrameTime = 0;
        public long TuringPanelCFrameTime
        {
            get { return _turingPanelCFrameTime; }
            set
            {
                SetProperty(ref _turingPanelCFrameTime, value);
            }
        }

        private bool _turingPanelERunning = false;

        public bool TuringPanelERunning
        {
            get { return _turingPanelERunning; }
            set
            {
                SetProperty(ref _turingPanelERunning, value);
            }
        }

        private int _turingPanelEFrameRate = 0;

        public int TuringPanelEFrameRate
        {
            get { return _turingPanelEFrameRate; }
            set
            {
                SetProperty(ref _turingPanelEFrameRate, value);
            }
        }

        private long _turingPanelEFrameTime = 0;
        public long TuringPanelEFrameTime
        {
            get { return _turingPanelEFrameTime; }
            set
            {
                SetProperty(ref _turingPanelEFrameTime, value);
            }
        }

        private int _webserverFrameRate = 0;
        public int WebserverFrameRate
        {
            get { return _webserverFrameRate; }
            set
            {
                SetProperty(ref _webserverFrameRate, value);
            }
        }

        private int _webserverFrameTime = 0;
        public int WebserverFrameTime
        {
            get { return _webserverFrameTime; }
            set
            {
                SetProperty(ref _webserverFrameTime, value);
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

        public ObservableCollection<DisplayItem> DisplayItems
        {
            get
            {
                if (SelectedProfile != null)
                {
                    return GetProfileDisplayItems(SelectedProfile);
                }
                else { return []; }
            }
        }
        private readonly object _displayItemsLock = new object();

        public DisplayItem? SelectedItem
        {
            get
            {
                var items = SelectedItems.ToList();

                //do not allow multiple group items to be selected
                if (items.FindAll(item => item is GroupDisplayItem).Count > 1)
                {
                    return null;
                }

                var result = items.FirstOrDefault();

                if (result is GroupDisplayItem groupDisplayItem)
                {
                    //check if all selected items are in the group
                    foreach (var item in items)
                    {
                        if (item == result)
                        {
                            continue;
                        }
                        if (!groupDisplayItem.DisplayItems.Contains(item))
                        {
                            return null;
                        }
                    }

                }
                else if (items.Count > 1)
                {
                    return null;
                }

                return result;
            }
            set
            {
                foreach (var selectedItem in SelectedItems)
                {
                    if (selectedItem != value)
                    {
                        selectedItem.Selected = false;
                    }
                }

                if (value is DisplayItem displayItem)
                {
                    displayItem.Selected = true;
                }

                NotifySelectedItemChange();
            }
        }

        public void NotifySelectedItemChange()
        {
            OnPropertyChanged(nameof(SelectedItem));
            IsItemSelected = SelectedItem != null;

            var items = SelectedItems.ToList();

            IsSelectedItemsMovable = items.FindAll(item => item is not GroupDisplayItem).Count > 0;
            IsSelectedItemMovable = SelectedItem is not null && SelectedItem is not GroupDisplayItem;
        }

        public List<DisplayItem> SelectedItems
        {
            get
            {
                return [.. DisplayItems
            .SelectMany<DisplayItem, DisplayItem>(item =>
                item is GroupDisplayItem group && group.DisplayItems is { } items
                    ? [group, ..items]
                    : [item])
            .Where(item => item.Selected)];
            }
        }

        public List<DisplayItem> SelectedVisibleItems
        {
            get
            {
                if (DisplayItems == null)
                    return [];

                return [.. DisplayItems
                    .SelectMany(item =>
                    {
                        if (item is GroupDisplayItem group && group.DisplayItems != null)
                            return group.DisplayItems.Cast<DisplayItem>();
                        return [item];
                    })
                    .Where(item => item.Selected && !item.Hidden)];
            }
        }

        [ObservableProperty]
        private bool _isSelectedItemsMovable = false;

        [ObservableProperty]
        private bool _isSelectedItemMovable = false;


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

        public void AddDisplayItem(DisplayItem newDisplayItem)
        {
            lock (_displayItemsLock)
            {
                if (DisplayItems is not ObservableCollection<DisplayItem> displayItems)
                    return;

                bool addedInGroup = false;

                if (newDisplayItem is not GroupDisplayItem && SelectedItem is DisplayItem selectedItem)
                {
                    // SelectedItem is a group — add directly to it
                    if (selectedItem is GroupDisplayItem group && !group.IsLocked)
                    {
                        group.DisplayItems.Add(newDisplayItem);
                        addedInGroup = true;
                    }
                    else
                    {
                        //SelectedItem is inside a group — find its parent
                        _= FindParentCollection(selectedItem, out var parentGroup);
                        if (parentGroup is not null && !parentGroup.IsLocked)
                        {
                            parentGroup.DisplayItems.Add(newDisplayItem);
                            addedInGroup = true;
                        }
                    }
                }

                if (!addedInGroup)
                {
                    displayItems.Add(newDisplayItem);
                }

                SelectedItem = newDisplayItem;
            }
        }

        public void RemoveDisplayItem(DisplayItem displayItem)
        {
            lock (_displayItemsLock)
            {
                if (DisplayItems is not ObservableCollection<DisplayItem> displayItems)
                    return;

                // Check if the item is inside a group
                _= FindParentCollection(displayItem, out var parentGroup);
                if (parentGroup is not null)
                {
                    int index = parentGroup.DisplayItems.IndexOf(displayItem);
                    if (index >= 0)
                    {
                        parentGroup.DisplayItems.RemoveAt(index);

                        if (parentGroup.DisplayItems.Count > 0)
                        {
                            parentGroup.DisplayItems[Math.Clamp(index, 0, parentGroup.DisplayItems.Count - 1)].Selected = true;
                        } else
                        {
                            parentGroup.Selected = true;
                        }
                    }
                }
                else
                {
                    // Top-level item
                    int index = displayItems.IndexOf(displayItem);
                    if (index >= 0)
                    {
                        displayItems.RemoveAt(index);

                        if (displayItems.Count > 0)
                        {
                            displayItems[Math.Clamp(index, 0, displayItems.Count - 1)].Selected = true;
                        }
                    }
                }
            }
        }

        public GroupDisplayItem? GetParent(DisplayItem displayItem)
        {
            FindParentCollection(displayItem, out var result);
            return result;
        }

        private ObservableCollection<DisplayItem>? FindParentCollection(DisplayItem item, out GroupDisplayItem? parentGroup)
        {
            parentGroup = null;

            if (DisplayItems == null)
                return null;

            if (DisplayItems.Contains(item))
                return DisplayItems;

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
            if (displayItem == null || count == 0)
                return;

            lock (_displayItemsLock)
            {
                if (DisplayItems == null)
                    return;

                // Find the parent collection and group (if any)
                var parentCollection = FindParentCollection(displayItem, out GroupDisplayItem? parentGroup);

                if (parentCollection == null)
                    return;

                int index = parentCollection.IndexOf(displayItem);
                int newIndex = index + count;

                // Moving out of group (up or down)
                if (parentGroup != null && !parentGroup.IsLocked)
                {
                    if (newIndex < 0)
                    {
                        int groupIndex = DisplayItems.IndexOf(parentGroup);
                        if (groupIndex >= 0)
                        {
                            parentCollection.RemoveAt(index);
                            DisplayItems.Insert(groupIndex, displayItem);
                        }
                        return;
                    }

                    if (newIndex >= parentCollection.Count)
                    {
                        int groupIndex = DisplayItems.IndexOf(parentGroup);
                        if (groupIndex >= 0)
                        {
                            parentCollection.RemoveAt(index);
                            DisplayItems.Insert(groupIndex + 1, displayItem);
                        }
                        return;
                    }
                }

                // Moving into a group
                if (displayItem is not GroupDisplayItem && parentGroup == null)
                {
                    int targetIndex = index + count;
                    if (targetIndex >= 0 && targetIndex < DisplayItems.Count)
                    {
                        var target = DisplayItems[targetIndex];
                        if (target is GroupDisplayItem targetGroup && !targetGroup.IsLocked && targetGroup.DisplayItems != null)
                        {
                            parentCollection.RemoveAt(index);
                            targetGroup.DisplayItems.Insert(count > 0 ? 0 : targetGroup.DisplayItems.Count, displayItem);
                            targetGroup.IsExpanded = true;
                            return;
                        }
                    }
                }

                // Normal move within the same collection
                if (newIndex >= 0 && newIndex < parentCollection.Count)
                {
                    parentCollection.Move(index, newIndex);
                    if(parentGroup is GroupDisplayItem)
                    {
                        parentGroup.IsExpanded = true;
                    }
                }
            }
        }

        public void PushDisplayItemTo(DisplayItem displayItem, DisplayItem target)
        {
            if (displayItem == null || target == null || displayItem == target)
                return;

            lock (_displayItemsLock)
            {
                if (DisplayItems == null)
                    return;

                var sourceCollection = FindParentCollection(displayItem, out var sourceGroupDisplayItem);
                var targetCollection = FindParentCollection(target, out var targetGroupDisplayItem);

                if (sourceCollection == null || targetCollection == null || sourceCollection != targetCollection)
                    return;

                int sourceIndex = sourceCollection.IndexOf(displayItem);
                int targetIndex = targetCollection.IndexOf(target);

                if (sourceCollection == targetCollection)
                {
                    // Same collection: simple move
                    sourceCollection.Move(sourceIndex, targetIndex + 1);
                }
            }
        }

        public void PushDisplayItemToTop(DisplayItem displayItem)
        {
            if (displayItem == null)
                return;

            lock (_displayItemsLock)
            {
                if (DisplayItems == null)
                    return;

                var sourceCollection = FindParentCollection(displayItem, out var groupDisplayItem);
                if (sourceCollection == null)
                    return;

                int currentIndex = sourceCollection.IndexOf(displayItem);

                if (sourceCollection == DisplayItems)
                {
                    if (currentIndex > 0)
                    {
                        DisplayItems.Move(currentIndex, 0);
                    }
                }
                else
                {
                    if (groupDisplayItem != null && !groupDisplayItem.IsLocked)
                    {
                        sourceCollection.RemoveAt(currentIndex);
                        DisplayItems.Insert(0, displayItem);
                    }else
                    {
                        if (currentIndex != 0)
                        {
                            sourceCollection.Move(currentIndex, 0);
                        }
                    }
                }
            }
        }

        public void PushDisplayItemToEnd(DisplayItem displayItem)
        {
            if (displayItem == null)
                return;

            lock (_displayItemsLock)
            {
                if (DisplayItems == null)
                    return;

                var sourceCollection = FindParentCollection(displayItem, out var groupDisplayItem);
                if (sourceCollection == null)
                    return;

                int currentIndex = sourceCollection.IndexOf(displayItem);

                if (sourceCollection == DisplayItems)
                {
                    if (currentIndex < DisplayItems.Count - 1)
                    {
                        DisplayItems.Move(currentIndex, DisplayItems.Count - 1);
                    }
                }
                else
                {
                    if (groupDisplayItem != null && !groupDisplayItem.IsLocked)
                    {
                        sourceCollection.RemoveAt(currentIndex);
                        DisplayItems.Add(displayItem);
                    } else
                    {
                        if (currentIndex != sourceCollection.Count - 1)
                        {
                            sourceCollection.Move(currentIndex, sourceCollection.Count - 1);
                        }
                    }
                }
            }
        }

        public BitmapSource BitmapSource { get; set; }

        public void UpdatePanel(Profile profile, Bitmap bitmap)
        {
            //if (Application.Current is App app)
            //{
            //    var window = app.GetDisplayWindow(profile);

            //    if (window is DisplayWindow displayWindow && !window.Direct2DMode)
            //    {
            //        var writeableBitmap = displayWindow?.WriteableBitmap;

            //        if (writeableBitmap != null)
            //        {
            //            IntPtr backBuffer = IntPtr.Zero;

            //            writeableBitmap.Dispatcher.Invoke(() =>
            //             {
            //                 if (writeableBitmap.Width == bitmap.Width && writeableBitmap.Height == bitmap.Height)
            //                 {
            //                     writeableBitmap.Lock();
            //                     backBuffer = writeableBitmap.BackBuffer;
            //                 }
            //             });

            //            if (backBuffer == IntPtr.Zero)
            //            {
            //                return;
            //            }

            //            // copy the pixel data from the bitmap to the back buffer
            //            BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            //            int stride = bitmapData.Stride;
            //            byte[] pixels = new byte[stride * bitmap.Height];
            //            Marshal.Copy(bitmapData.Scan0, pixels, 0, pixels.Length);
            //            Marshal.Copy(pixels, 0, backBuffer, pixels.Length);
            //            bitmap.UnlockBits(bitmapData);

            //            writeableBitmap.Dispatcher.Invoke(() =>
            //            {
            //                writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, writeableBitmap.PixelWidth, writeableBitmap.PixelHeight));
            //                writeableBitmap.Unlock();
            //            });
            //        }
            //    }
            //}
        }

        public void SetPanelBitmap(Profile profile, Bitmap bitmap)
        {
            if (profile.Active)
            {
                UpdatePanel(profile, bitmap);
            }
        }

        private static void SaveDisplayItems(Profile profile, List<DisplayItem> displayItems)
        {
            var profileFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "profiles");
            if (!Directory.Exists(profileFolder))
            {
                Directory.CreateDirectory(profileFolder);
            }
            var fileName = Path.Combine(profileFolder, profile.Guid + ".xml");

            XmlSerializer xs = new(typeof(List<DisplayItem>), [typeof(GroupDisplayItem), typeof(BarDisplayItem), typeof(GraphDisplayItem), typeof(DonutDisplayItem), typeof(TableSensorDisplayItem), typeof(SensorDisplayItem), typeof(TextDisplayItem), typeof(ClockDisplayItem), typeof(CalendarDisplayItem), typeof(SensorImageDisplayItem), typeof(ImageDisplayItem), typeof(HttpImageDisplayItem), typeof(GaugeDisplayItem), typeof(ShapeDisplayItem)]);

            var settings = new XmlWriterSettings() { Encoding = Encoding.UTF8, Indent = true };
            using var wr = XmlWriter.Create(fileName, settings);
            xs.Serialize(wr, displayItems);
        }

        public void SaveDisplayItems(Profile profile)
        {
            if (profile != null)
            {
                var displayItems = GetProfileDisplayItemsCopy(profile);
                SaveDisplayItems(profile, displayItems);
            }
        }

        public void SaveDisplayItems()
        {
            if (SelectedProfile != null)
                SaveDisplayItems(SelectedProfile);
        }

        public string? ExportProfile(Profile profile, string outputFolder)
        {
            var SelectedProfile = profile;

            if (SelectedProfile != null)
            {
                var exportFilePath = Path.Combine(outputFolder, SelectedProfile.Name.SanitizeFileName().Replace(" ", "_") + "-" + DateTimeOffset.Now.ToUnixTimeSeconds() + ".infopanel");


                if (File.Exists(exportFilePath))
                {
                    File.Delete(exportFilePath);
                }

                using (ZipArchive archive = ZipFile.Open(exportFilePath, ZipArchiveMode.Create))
                {
                    //add profile settings
                    var exportProfile = new Profile(SelectedProfile.Name, SelectedProfile.Width, SelectedProfile.Height)
                    {
                        ShowFps = SelectedProfile.ShowFps,
                        BackgroundColor = SelectedProfile.BackgroundColor,
                        Font = SelectedProfile.Font,
                        FontSize = SelectedProfile.FontSize,
                        Color = SelectedProfile.Color,
                        Direct2DMode = SelectedProfile.Direct2DMode,
                        Direct2DFontScale = SelectedProfile.Direct2DFontScale,
                        Direct2DTextXOffset = SelectedProfile.Direct2DTextXOffset,
                        Direct2DTextYOffset = SelectedProfile.Direct2DTextYOffset,
                    };

                    var entry = archive.CreateEntry("Profile.xml");

                    using (Stream entryStream = entry.Open())
                    {
                        XmlSerializer xs = new(typeof(Profile));
                        var settings = new XmlWriterSettings() { Encoding = Encoding.UTF8, Indent = true };
                        using var wr = XmlWriter.Create(entryStream, settings);
                        xs.Serialize(wr, exportProfile);
                    }

                    //add displayitems
                    var profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "profiles", SelectedProfile.Guid + ".xml");
                    if (File.Exists(profilePath))
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



        public static async Task ImportSensorPanel(string importPath)
        {
            if (!File.Exists(importPath))
            {
                return;
            }

            var lines = File.ReadAllLines(importPath, Encoding.GetEncoding("iso-8859-1"));

            if (lines.Length < 2)
            {
                Console.WriteLine("Invalid file format");
                return;
            }

            int page = 0;
            var items = new List<Dictionary<string, string>>();
            string importBaseName = Path.GetFileNameWithoutExtension(importPath);

            Regex openTagRegex = new(@"<LCDPAGE(\d+)>", RegexOptions.Compiled);
            Regex closeTagRegex = new(@"</LCDPAGE(\d+)>", RegexOptions.Compiled);


            for (int i = 0; i < lines.Length; i++)
            {
                var openMatch = openTagRegex.Match(lines[i]);
                if (openMatch.Success)
                {
                    page = int.Parse(openMatch.Groups[1].Value);
                    continue;
                }

                var closeMatch = closeTagRegex.Match(lines[i]);
                if (closeMatch.Success)
                {
                    await ProcessSensorPanelImport($"[Import] {importBaseName} - Page {page}", items);
                    items.Clear();
                    continue;
                }

                try
                {
                    var rootElement = XElement.Parse($"<Root>{EscapeContentWithinLBL(lines[i])}</Root>");

                    var item = new Dictionary<string, string>();

                    foreach (XElement element in rootElement.Elements())
                    {
                        item[element.Name.LocalName] = element.Value;
                    }

                    items.Add(item);
                }
                catch (Exception ex)
                {
                    // Handle parsing errors here
                    Console.WriteLine($"Error parsing line {i}: {ex.Message}");
                }
            }

            if (items.Count > 2)
            {
                await ProcessSensorPanelImport($"[Import] {Path.GetFileNameWithoutExtension(importPath)}", items);
            }

        }

        private static async Task ProcessSensorPanelImport(string name, List<Dictionary<string, string>> items)
        {
            if (items.Count > 2)
            {
                var SPWIDTH = items[1].GetIntValue("SPWIDTH", 1024);
                var SPHEIGHT = items[1].GetIntValue("SPHEIGHT", 600);
                var LCDBGCOLOR = items[1].GetIntValue("LCDBGCOLOR", 0);
                var SPBGCOLOR = items[1].GetIntValue("SPBGCOLOR", LCDBGCOLOR);

                Profile profile = new(name, SPWIDTH, SPHEIGHT)
                {
                    BackgroundColor = DecimalBgrToHex(SPBGCOLOR)
                };

                List<DisplayItem> displayItems = [];

                for (int i = 2; i < items.Count; i++)
                {
                    var item = items[i];
                    var key = item.GetStringValue("ID", string.Empty);

                    var hidden = false;
                    var simple = false;
                    var gauge = false;
                    var graph = false;

                    if (key.StartsWith('-'))
                    {
                        hidden = true;
                        key = key[1..];
                    }

                    if (key.StartsWith("[SIMPLE]"))
                    {
                        simple = true;
                        key = key[8..];
                    }

                    if (key.StartsWith("[GAUGE]"))
                    {
                        gauge = true;
                        key = key[7..];
                    }

                    if (key.StartsWith("[GRAPH]"))
                    {
                        graph = true;
                        key = key[7..];
                    }

                    //global items
                    var ITMX = item.GetIntValue("ITMX", 0);
                    var ITMY = item.GetIntValue("ITMY", 0);

                    var LBL = item.GetStringValue("LBL", key);
                    var TXTBIR = item.GetStringValue("TXTBIR", string.Empty);
                    var FNTNAM = item.GetStringValue("FNTNAM", "Arial");
                    var WID = item.GetIntValue("WID", 0);
                    var HEI = item.GetIntValue("HEI", 0);
                    var TYP = item.GetStringValue("TYP", string.Empty);
                    var MINVAL = item.GetIntValue("MINVAL", 0);
                    var MAXVAL = item.GetIntValue("MAXVAL", 100);

                    var UNT = item.GetStringValue("UNT", string.Empty);
                    var SHWUNT = item.GetIntValue("SHWUNT", 1);
                    var UNTWID = item.GetIntValue("UNTWID", 0);
                    var TXTSIZ = item.GetIntValue("TXTSIZ", 12);
                    var LBLCOL = item.GetIntValue("LBLCOL", 0);
                    var TXTCOL = item.GetIntValue("TXTCOL", LBLCOL);
                    var VALCOL = item.GetIntValue("VALCOL", TXTCOL);

                    var bold = false;
                    var italic = false;
                    var rightAlign = false;

                    if (simple)
                    {
                        if (TXTBIR.Length == 3)
                        {
                            if (int.TryParse(TXTBIR.AsSpan(0, 1), out int _bold))
                            {
                                bold = _bold == 1;
                            }
                            if (int.TryParse(TXTBIR.AsSpan(1, 1), out int _italic))
                            {
                                italic = _italic == 1;
                            }
                            if (int.TryParse(TXTBIR.AsSpan(2, 1), out int _rightAlign))
                            {
                                rightAlign = _rightAlign == 1;
                            }
                        }
                    }
                    else
                    {
                        //all other non-simple items are right align
                        if (key != "LBL")
                        {
                            rightAlign = true;
                        }
                    }

                    if (graph)
                    {
                        if (WID != 0 && HEI != 0)
                        {
                            GraphDisplayItem.GraphType? graphType = null;
                            switch (TYP)
                            {
                                case "AG":
                                case "LG":
                                    graphType = GraphDisplayItem.GraphType.LINE;
                                    break;
                                case "HG":
                                    graphType = GraphDisplayItem.GraphType.HISTOGRAM;
                                    break;
                            }

                            if (graphType.HasValue)
                            {
                                var AUTSCL = item.GetIntValue("AUTSCL", 0);
                                var GPHCOL = item.GetIntValue("GPHCOL", 0);
                                var BGCOL = item.GetIntValue("BGCOL", 0);
                                var FRMCOL = item.GetIntValue("FRMCOL", 0);

                                //graph step
                                var GPHSTP = item.GetIntValue("GPHSTP", 1);

                                //graph thickness
                                var GPHTCK = item.GetIntValue("GPHTCK", 1);

                                //graph background, frame, grid
                                var GPHBFG = item.GetStringValue("GPHBFG", "000");

                                var background = false;
                                var frame = false;
                                if (GPHBFG.Length == 3)
                                {
                                    if (int.TryParse(GPHBFG.AsSpan(0, 1), out int _background))
                                    {
                                        background = _background == 1;
                                    }
                                    if (int.TryParse(GPHBFG.AsSpan(1, 1), out int _frame))
                                    {
                                        frame = _frame == 1;
                                    }
                                }

                                var libreSensorId = SensorMapping.FindMatchingIdentifier(key) ?? "unknown";
                                GraphDisplayItem graphDisplayItem = new(LBL, graphType.Value, libreSensorId)
                                {
                                    SensorName = key,
                                    Width = WID,
                                    Height = HEI,
                                    MinValue = MINVAL,
                                    MaxValue = MAXVAL,
                                    AutoValue = AUTSCL == 1,
                                    Step = GPHSTP,
                                    Thickness = GPHTCK,
                                    Background = background,
                                    Frame = frame,
                                    Fill = TYP != "LG",
                                    FillColor = TYP == "AG" ? $"#7F{DecimalBgrToHex(GPHCOL).Substring(1)}" : DecimalBgrToHex(GPHCOL),
                                    Color = DecimalBgrToHex(GPHCOL),
                                    BackgroundColor = DecimalBgrToHex(BGCOL),
                                    FrameColor = DecimalBgrToHex(FRMCOL),
                                    X = ITMX,
                                    Y = ITMY,
                                    Hidden = hidden,
                                };

                                displayItems.Add(graphDisplayItem);
                            }
                        }
                    }
                    else if (gauge)
                    {
                        var STAFLS = item.GetStringValue("STAFLS", string.Empty);

                        var RESIZW = item.GetIntValue("RESIZW", 0);
                        var RESIZH = item.GetIntValue("RESIZH", 0);

                        if (TYP == "Custom" && STAFLS != string.Empty)
                        {
                            //var libreSensorId = SensorMapping.SensorPanel.GetStringValue(key, "unknown");
                            var libreSensorId = SensorMapping.FindMatchingIdentifier(key) ?? "unknown";
                            GaugeDisplayItem gaugeDisplayItem = new(LBL, libreSensorId)
                            {
                                SensorName = key,
                                MinValue = MINVAL,
                                MaxValue = MAXVAL,
                                X = ITMX,
                                Y = ITMY,
                                Width = RESIZW,
                                Height = RESIZH,
                                Hidden = hidden
                            };

                            foreach (var image in STAFLS.Split('|'))
                            {
                                ImageDisplayItem imageDisplayItem = new(image, profile.Guid, image, true);
                                gaugeDisplayItem.Images.Add(imageDisplayItem);
                            }

                            displayItems.Add(gaugeDisplayItem);
                        }

                    }
                    else if (key == string.Empty)
                    {
                        var GAUSTAFNM = item.GetStringValue("GAUSTAFNM", string.Empty);
                        var GAUSTADAT = item.GetStringValue("GAUSTADAT", string.Empty);

                        if (GAUSTAFNM != string.Empty && GAUSTADAT != string.Empty)
                        {
                            var data = ConvertHexStringToByteArray(GAUSTADAT);
                            await FileUtil.SaveAsset(profile, GAUSTAFNM, data);
                        }
                    }
                    else if (key == "IMG")
                    {
                        var IMGFIL = item.GetStringValue("IMGFIL", string.Empty);
                        var IMGDAT = item.GetStringValue("IMGDAT", string.Empty);
                        var BGIMG = item.GetIntValue("BGIMG", 0);
                        var RESIZW = item.GetIntValue("RESIZW", 0);
                        var RESIZH = item.GetIntValue("RESIZH", 0);

                        if (IMGFIL != string.Empty && IMGDAT != string.Empty)
                        {
                            var data = ConvertHexStringToByteArray(IMGDAT);
                            var result = await FileUtil.SaveAsset(profile, IMGFIL, data);
                            if (result)
                            {
                                ImageDisplayItem imageDisplayItem = new(IMGFIL, profile.Guid, IMGFIL, true)
                                {
                                    X = ITMX,
                                    Y = ITMY,
                                    Width = BGIMG == 1 ? SPWIDTH : RESIZW,
                                    Height = BGIMG == 1 ? SPHEIGHT : RESIZH,
                                    Hidden = hidden
                                };
                                displayItems.Add(imageDisplayItem);
                            }
                        }
                    }
                    else
                    {
                        switch (key)
                        {
                            case "PROPERTIES":
                                //do nothing
                                break;
                            case "LBL":
                                TextDisplayItem textDisplayItem = new(LBL)
                                {
                                    Font = FNTNAM,
                                    FontSize = TXTSIZ,
                                    Color = DecimalBgrToHex(VALCOL),
                                    RightAlign = rightAlign,
                                    X = ITMX,
                                    Y = ITMY,
                                    Width = WID,
                                    Hidden = hidden
                                };
                                displayItems.Add(textDisplayItem);
                                break;
                            case "SDATE":
                                {
                                    CalendarDisplayItem calendarDisplayItem = new(LBL)
                                    {
                                        Font = FNTNAM,
                                        FontSize = TXTSIZ,
                                        Color = DecimalBgrToHex(VALCOL),
                                        Bold = bold,
                                        Italic = italic,
                                        RightAlign = rightAlign,
                                        X = ITMX,
                                        Y = ITMY,
                                        Width = WID,
                                        Hidden = hidden
                                    };
                                    displayItems.Add(calendarDisplayItem);
                                }
                                break;
                            case "STIME":
                            case "STIMENS":
                                {
                                    ClockDisplayItem clockDisplayItem = new(LBL)
                                    {
                                        Font = FNTNAM,
                                        FontSize = TXTSIZ,
                                        Format = key == "STIME" ? "H:mm:ss" : "H:mm",
                                        Color = DecimalBgrToHex(VALCOL),
                                        Bold = bold,
                                        Italic = italic,
                                        RightAlign = rightAlign,
                                        X = ITMX,
                                        Y = ITMY,
                                        Width = WID,
                                        Hidden = hidden
                                    };
                                    displayItems.Add(clockDisplayItem);
                                }
                                break;
                            default:
                                {
                                    var SHWLBL = item.GetIntValue("SHWLBL", 0);

                                    if (SHWLBL == 1)
                                    {
                                        var LBLBIS = item.GetStringValue("LBLBIS", string.Empty);

                                        if (LBLBIS.Length == 3)
                                        {
                                            if (int.TryParse(LBLBIS.AsSpan(0, 1), out int _bold))
                                            {
                                                bold = _bold == 1;
                                            }
                                            if (int.TryParse(LBLBIS.AsSpan(1, 1), out int _italic))
                                            {
                                                italic = _italic == 1;
                                            }
                                        }

                                        TextDisplayItem label = new TextDisplayItem(LBL)
                                        {
                                            Font = FNTNAM,
                                            FontSize = TXTSIZ,
                                            Color = DecimalBgrToHex(LBLCOL),
                                            Bold = bold,
                                            Italic = italic,
                                            X = ITMX,
                                            Y = ITMY,
                                            Width = WID,
                                            Hidden = hidden,
                                        };
                                        displayItems.Add(label);
                                    }

                                    var libreSensorId = SensorMapping.FindMatchingIdentifier(key) ?? "unknown";
                                    var SHWVAL = item.GetIntValue("SHWVAL", 0);

                                    if (simple || SHWVAL == 1)
                                    {
                                        var VALBIS = item.GetStringValue("VALBIS", string.Empty);

                                        if (VALBIS.Length == 3)
                                        {
                                            if (int.TryParse(VALBIS.AsSpan(0, 1), out int _bold))
                                            {
                                                bold = _bold == 1;
                                            }
                                            if (int.TryParse(VALBIS.AsSpan(1, 1), out int _italic))
                                            {
                                                italic = _italic == 1;
                                            }
                                        }

                                        SensorDisplayItem sensorDisplayItem = new(LBL, libreSensorId)
                                        {
                                            SensorName = key,
                                            Font = FNTNAM,
                                            FontSize = TXTSIZ,
                                            Color = DecimalBgrToHex(VALCOL),
                                            Unit = UNT,
                                            ShowUnit = SHWUNT == 1,
                                            OverrideUnit = SHWUNT == 1,
                                            Bold = bold,
                                            Italic = italic,
                                            RightAlign = rightAlign,
                                            X = ITMX,
                                            Y = ITMY,
                                            Width = WID,
                                            Hidden = hidden
                                        };
                                        displayItems.Add(sensorDisplayItem);
                                    }

                                    var SHWBAR = item.GetIntValue("SHWBAR", 0);

                                    if (SHWBAR == 1)
                                    {
                                        var BARWID = item.GetIntValue("BARWID", 400);
                                        var BARHEI = item.GetIntValue("BARHEI", 50);
                                        var BARMIN = item.GetIntValue("BARMIN", 0);
                                        var BARMAX = item.GetIntValue("BARMAX", 100);
                                        var BARFRMCOL = item.GetIntValue("BARFRMCOL", 0);
                                        var BARMINFGC = item.GetIntValue("BARMINFGC", 0);
                                        var BARMINBGC = item.GetIntValue("BARMINBGC", 0);

                                        var BARLIM3FGC = item.GetIntValue("BARLIM3FGC", 0);
                                        var BARLIM3BGC = item.GetIntValue("BARLIM3BGC", 0);

                                        //frame, shadow, 3d, right to left
                                        var BARFS = item.GetStringValue("BARFS", "0000");

                                        //bar placement
                                        var BARPLC = item.GetStringValue("BARPLC", "SEP");

                                        var offset = 0;

                                        if (BARPLC == "SEP" && SHWVAL == 1)
                                        {
                                            using Bitmap bitmap2 = new(1, 1);
                                            using Graphics g2 = Graphics.FromImage(bitmap2);
                                            using Font font2 = new(FNTNAM, TXTSIZ);
                                            var size2 = g2.MeasureString("HELLO WORLD", font2, 0, StringFormat.GenericTypographic);

                                            offset = (int)size2.Height;
                                        }

                                        using Bitmap bitmap = new(1, 1);
                                        using Graphics g = Graphics.FromImage(bitmap);
                                        using Font font = new(FNTNAM, TXTSIZ);
                                        var size = g.MeasureString(UNT, font, 0, StringFormat.GenericTypographic);

                                        var frame = false;
                                        var gradient = false;
                                        var flipX = false;

                                        if (BARFS.Length == 4)
                                        {
                                            if (int.TryParse(BARFS.AsSpan(0, 1), out int _frame))
                                            {
                                                frame = _frame == 1;
                                            }
                                            if (int.TryParse(BARFS.AsSpan(2, 1), out int _gradient))
                                            {
                                                gradient = _gradient == 1;
                                            }
                                            if (int.TryParse(BARFS.AsSpan(3, 1), out int _flipX))
                                            {
                                                flipX = _flipX == 1;
                                            }
                                        }

                                        BarDisplayItem barDisplayItem = new(LBL, libreSensorId)
                                        {
                                            SensorName = key,
                                            Width = BARWID,
                                            Height = BARHEI,
                                            MinValue = BARMIN,
                                            MaxValue = BARMAX,
                                            Frame = frame,
                                            FrameColor = DecimalBgrToHex(BARFRMCOL),
                                            Color = DecimalBgrToHex(BARLIM3FGC),
                                            Background = true,
                                            BackgroundColor = DecimalBgrToHex(BARLIM3BGC),
                                            Gradient = gradient,
                                            GradientColor = DecimalBgrToHex(BARLIM3BGC),
                                            FlipX = flipX,
                                            X = ITMX,
                                            Y = ITMY + offset,
                                            Hidden = hidden,
                                        };
                                        displayItems.Add(barDisplayItem);
                                    }
                                }
                                break;
                        }
                    }
                }

                Dispatcher.CurrentDispatcher.Invoke(() =>
                {
                    SaveDisplayItems(profile, displayItems);
                    ConfigModel.Instance.AddProfile(profile);
                    ConfigModel.Instance.SaveProfiles();
                    SharedModel.Instance.SelectedProfile = profile;
                });
            }
        }
        private static byte[] ConvertHexStringToByteArray(string hex)
        {
            if (hex.Length % 2 != 0)
                throw new ArgumentException("Hex string must have an even length.");

            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < hex.Length; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }

            return bytes;
        }
        private static string EscapeContentWithinLBL(string xmlContent)
        {
            // Regular expression to match content within <LBL>...</LBL>
            string pattern = @"<LBL>(.*?)</LBL>";

            // Use Regex.Replace to find each match and escape its content
            string result = Regex.Replace(xmlContent, pattern, match =>
            {
                // Escape the inner content
                string innerContent = match.Groups[1].Value;
                string escapedContent = innerContent
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;");
                return $"<LBL>{escapedContent}</LBL>";
            }, RegexOptions.Singleline);

            return result;
        }

        private static string DecimalBgrToHex(int bgrValue)
        {
            // Handle negative values explicitly
            if (bgrValue < 0)
            {
                return "#000000";
            }

            // Extract the individual B, G, R components from the BGR integer
            int blue = (bgrValue & 0xFF0000) >> 16;
            int green = (bgrValue & 0x00FF00) >> 8;
            int red = (bgrValue & 0x0000FF);

            // Convert to hexadecimal string with leading #
            return $"#{red:X2}{green:X2}{blue:X2}";
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
                                if (displayItem is SensorDisplayItem sensorDisplayItem && sensorDisplayItem.SensorType == Enums.SensorType.HwInfo)
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
                                else if (displayItem is ChartDisplayItem chartDisplayItem && chartDisplayItem.SensorType == Enums.SensorType.HwInfo)
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
                                else if (displayItem is GaugeDisplayItem gaugeDisplayItem && gaugeDisplayItem.SensorType == Enums.SensorType.HwInfo)
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
            lock (_displayItemsLock)
            {
                if (!ProfileDisplayItems.TryGetValue(profile.Guid, out ObservableCollection<DisplayItem>? displayItems))
                {
                    LoadDisplayItems(profile);
                    displayItems = ProfileDisplayItems[profile.Guid];
                }

                return displayItems;
            }
        }

        public List<DisplayItem> GetProfileDisplayItemsCopy()
        {
            lock (_displayItemsLock)
            {
                if (SelectedProfile == null) { return []; }
                return [.. GetProfileDisplayItems(SelectedProfile)];
            }
        }

        public List<DisplayItem> GetProfileDisplayItemsCopy(Profile profile)
        {
            lock (_displayItemsLock)
            {
                return [.. GetProfileDisplayItems(profile)];
            }
        }

        public void LoadDisplayItems()
        {
            if (SelectedProfile != null)
            {
                LoadDisplayItems(SelectedProfile);
            }
        }

        public void LoadDisplayItems(Profile profile)
        {
            if (!ProfileDisplayItems.TryGetValue(profile.Guid, out ObservableCollection<DisplayItem>? displayItems))
            {
                displayItems = [];
                ProfileDisplayItems[profile.Guid] = displayItems;
            }

            lock (_displayItemsLock)
            {
                displayItems.Clear();

                if (LoadDisplayItemsFromFile(profile) is List<DisplayItem> items)
                {
                    foreach (var item in items)
                    {
                        displayItems.Add(item);
                    }
                }
            }
        }

        public static List<DisplayItem>? LoadDisplayItemsFromFile(Profile profile)
        {
            var fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "profiles", profile.Guid + ".xml");
            if (File.Exists(fileName))
            {
                XmlSerializer xs = new(typeof(List<DisplayItem>),
                    [typeof(GroupDisplayItem), typeof(BarDisplayItem), typeof(GraphDisplayItem), typeof(DonutDisplayItem), typeof(TableSensorDisplayItem), typeof(SensorDisplayItem), typeof(ClockDisplayItem), typeof(CalendarDisplayItem), typeof(TextDisplayItem), typeof(SensorImageDisplayItem), typeof(ImageDisplayItem), typeof(HttpImageDisplayItem), typeof(GaugeDisplayItem), typeof(ShapeDisplayItem)]);

                using var rd = XmlReader.Create(fileName);
                try
                {
                    if (xs.Deserialize(rd) is List<DisplayItem> displayItems)
                    {
                        foreach (var displayItem in displayItems)
                        {
                            displayItem.SetProfileGuid(profile.Guid);
                        }

                        return displayItems;
                    }
                }
                catch { }
            }

            return null;
        }
    }

}
