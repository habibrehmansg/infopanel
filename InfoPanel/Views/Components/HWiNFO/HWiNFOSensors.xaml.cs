using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using static HWHash;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for HWiNFOSensors.xaml
    /// </summary>
    public partial class HWiNFOSensors : System.Windows.Controls.UserControl
    {
        private HWiNFOVM ViewModel { get; set; }

        private Timer? UpdateTimer;

        public HWiNFOSensors()
        {
            ViewModel = new HWiNFOVM();
            DataContext = ViewModel;

            InitializeComponent();

            Loaded += HWiNFOSensors_Loaded;
            Unloaded += HWiNFOSensors_Unloaded;

            UpdateTimer = new Timer
            {
                Interval = 1000
            };
            UpdateTimer.Tick += Timer_Tick;

            //tick once
            Timer_Tick(this, null);
            UpdateTimer.Start();
        }

        private void HWiNFOSensors_Loaded(object sender, RoutedEventArgs e)
        {
            if (UpdateTimer != null)
            {
                UpdateTimer.Tick += Timer_Tick;
                UpdateTimer.Start();
            }
        }

        private void HWiNFOSensors_Unloaded(object sender, RoutedEventArgs e)
        {
            if (UpdateTimer != null)
            {
                UpdateTimer.Stop();
                UpdateTimer.Tick -= Timer_Tick;
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (TreeViewInfo.Items.Count > 0)
            {
                UpdateSensorDetails();
            }
            else
            {
                LoadHWiNFO();
            }
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scrollViewer = (ScrollViewer)sender;
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        public void LoadHWiNFO()
        {
            TreeViewInfo.Items.Clear();

            var parentDict = new Dictionary<ulong, TreeViewItem>();

            foreach (HWINFO_HASH hash in HWHash.GetOrderedList())
            {
                TreeViewItem item;

                if (parentDict.ContainsKey(hash.ParentUniqueID))
                {
                    item = parentDict[hash.ParentUniqueID];
                }
                else
                {
                    item = new TreeViewItem();
                    item.SetResourceReference(TreeViewItem.ForegroundProperty, "TextFillColorSecondaryBrush");
                    item.Focusable = false;
                    item.Header = hash.ParentNameCustom;
                    item.Tag = (hash.ParentID, hash.ParentInstance);
                    item.Selected += delegate (object sender, RoutedEventArgs e)
                    {
                        TreeViewItem selectedItem = (TreeViewItem)TreeViewInfo.SelectedItem;
                        Trace.WriteLine("Selected " + selectedItem.Tag);

                        if (selectedItem.Parent is TreeViewItem)
                        {
                            GridActions.IsEnabled = true;
                        }
                        else
                        {
                            GridActions.IsEnabled = false;
                        }
                    };

                    parentDict.Add(hash.ParentUniqueID, item);
                    TreeViewInfo.Items.Add(item);
                }

                TreeViewItem subItem = new TreeViewItem();
                subItem.SetResourceReference(TreeViewItem.ForegroundProperty, "TextFillColorTertiaryBrush");
                subItem.Focusable = false;
                subItem.PreviewMouseDown += SubItem_PreviewMouseDown;
                subItem.Header = hash.NameCustom;
                subItem.Tag = hash.SensorID;

                bool added = false;
                foreach (TreeViewItem group in item.Items)
                {
                    if (group.Name == hash.ReadingType)
                    {
                        group.Items.Add(subItem);
                        added = true;
                        break;
                    }
                }

                if (!added)
                {
                    TreeViewItem group = new();
                    group.SetResourceReference(TreeViewItem.ForegroundProperty, "TextFillColorTertiaryBrush");
                    group.Focusable = false;
                    group.Name = hash.ReadingType;
                    group.Header = hash.ReadingType;
                    group.Tag = item.Tag;
                    group.Items.Add(subItem);
                    item.Items.Add(group);
                }
            }
        }

        private void SubItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            ((TreeViewItem)sender).IsSelected = true;
        }

        private void UpdateSensorDetails()
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;

            if (selectedTreeViewItem != null && selectedTreeViewItem.Tag is UInt32 && selectedTreeViewItem?.Parent is TreeViewItem parentItem)
            {
                var parentTag = ((UInt32, UInt32))parentItem.Tag;
                var item = new SensorDisplayItem()
                {
                    Name = (string)selectedTreeViewItem.Header,
                    Id = parentTag.Item1,
                    Instance = parentTag.Item2,
                    EntryId = (UInt32)selectedTreeViewItem.Tag,
                };

                ViewModel.SensorName = item.Name;
                ViewModel.Id = item.Id;
                ViewModel.Instance = item.Instance;
                ViewModel.EntryId = item.EntryId;
                ViewModel.SensorValue = item.EvaluateText();
            }
            else
            {
                ViewModel.SensorName = "No sensor selected";
                ViewModel.Id = 0;
                ViewModel.Instance = 0;
                ViewModel.EntryId = 0;
                ViewModel.SensorValue = String.Empty;
            }
        }

        private void TreeViewInfo_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            UpdateSensorDetails();
        }

        private void ButtonSelect_Click(object sender, RoutedEventArgs e)
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;
            if (selectedTreeViewItem?.Parent is TreeViewItem parentItem)
            {
                var parentTag = ((UInt32, UInt32))parentItem.Tag;
                var item = new SensorDisplayItem((string)selectedTreeViewItem.Header, parentTag.Item1, parentTag.Item2, (UInt32)selectedTreeViewItem.Tag)
                {
                    SensorName = (string)selectedTreeViewItem.Header,
                    Font = SharedModel.Instance.SelectedProfile!.Font,
                    FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                    Color = SharedModel.Instance.SelectedProfile!.Color,
                    Unit = " " + HWHash.SENSORHASH[(parentTag.Item1, parentTag.Item2, (UInt32)selectedTreeViewItem.Tag)].Unit
                };

                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.SelectedItem = item;
            }
        }

        private void ButtonReplace_Click(object sender, RoutedEventArgs e)
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;
            if (selectedTreeViewItem?.Parent is TreeViewItem parentItem)
            {
                var parentTag = ((UInt32, UInt32))parentItem.Tag;

                if (SharedModel.Instance.SelectedItem is SensorDisplayItem sensorDisplayItem)
                {
                    sensorDisplayItem.Name = (string)selectedTreeViewItem.Header;
                    sensorDisplayItem.SensorName = (string)selectedTreeViewItem.Header;
                    sensorDisplayItem.SensorType = SensorType.HwInfo;
                    sensorDisplayItem.Id = parentTag.Item1;
                    sensorDisplayItem.Instance = parentTag.Item2;
                    sensorDisplayItem.EntryId = (UInt32)selectedTreeViewItem.Tag;
                    sensorDisplayItem.Unit = " " + HWHash.SENSORHASH[(parentTag.Item1, parentTag.Item2, (UInt32)selectedTreeViewItem.Tag)].Unit;
                }
                else if (SharedModel.Instance.SelectedItem is ChartDisplayItem chartDisplayItem)
                {
                    chartDisplayItem.Name = (string)selectedTreeViewItem.Header;
                    chartDisplayItem.SensorName = (string)selectedTreeViewItem.Header;
                    chartDisplayItem.SensorType = SensorType.HwInfo;
                    chartDisplayItem.Id = parentTag.Item1;
                    chartDisplayItem.Instance = parentTag.Item2;
                    chartDisplayItem.EntryId = (UInt32)selectedTreeViewItem.Tag;
                }
                else if (SharedModel.Instance.SelectedItem is GaugeDisplayItem gaugeDisplayItem)
                {
                    gaugeDisplayItem.Name = (string)selectedTreeViewItem.Header;
                    gaugeDisplayItem.SensorName = (string)selectedTreeViewItem.Header;
                    gaugeDisplayItem.SensorType = SensorType.HwInfo;
                    gaugeDisplayItem.Id = parentTag.Item1;
                    gaugeDisplayItem.Instance = parentTag.Item2;
                    gaugeDisplayItem.EntryId = (UInt32)selectedTreeViewItem.Tag;
                }
            }
        }

        private void ButtonAddGraph_Click(object sender, RoutedEventArgs e)
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;
            if (selectedTreeViewItem?.Parent is TreeViewItem parentItem)
            {
                var parentTag = ((UInt32, UInt32))parentItem.Tag;
                var item = new GraphDisplayItem((string)selectedTreeViewItem.Header, GraphDisplayItem.GraphType.LINE, parentTag.Item1, parentTag.Item2, (uint)selectedTreeViewItem.Tag);
                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.SelectedItem = item;
            }
        }

        private void ButtonAddBar_Click(object sender, RoutedEventArgs e)
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;
            if (selectedTreeViewItem?.Parent is TreeViewItem parentItem)
            {
                var parentTag = ((UInt32, UInt32))parentItem.Tag;
                var item = new BarDisplayItem((string)selectedTreeViewItem.Header, parentTag.Item1, parentTag.Item2, (uint)selectedTreeViewItem.Tag);
                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.SelectedItem = item;
            }
        }

        private void ButtonAddDonut_Click(object sender, RoutedEventArgs e)
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;
            if (selectedTreeViewItem?.Parent is TreeViewItem parentItem)
            {
                var parentTag = ((UInt32, UInt32))parentItem.Tag;
                var item = new DonutDisplayItem((string)selectedTreeViewItem.Header, parentTag.Item1, parentTag.Item2, (uint)selectedTreeViewItem.Tag);
                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.SelectedItem = item;
            }
        }

        private void ButtonAddCustom_Click(object sender, RoutedEventArgs e)
        {
            TreeViewItem? selectedTreeViewItem = (TreeViewItem)TreeViewInfo.SelectedItem;
            if (selectedTreeViewItem?.Parent is TreeViewItem parentItem)
            {
                var parentTag = ((UInt32, UInt32))parentItem.Tag;
                var item = new GaugeDisplayItem((string)selectedTreeViewItem.Header, parentTag.Item1, parentTag.Item2, (uint)selectedTreeViewItem.Tag);
                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.SelectedItem = item;
            }
        }

        private void ButtonNewText_Click(object sender, RoutedEventArgs e)
        {
            var item = new TextDisplayItem("Custom Text")
            {
                Font = SharedModel.Instance.SelectedProfile!.Font,
                FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                Color = SharedModel.Instance.SelectedProfile!.Color
            };
            SharedModel.Instance.AddDisplayItem(item);
            SharedModel.Instance.SelectedItem = item;

        }

        private void ButtonNewImage_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedProfile != null)
            {
                var item = new ImageDisplayItem("Image", SharedModel.Instance.SelectedProfile.Guid);
                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.SelectedItem = item;
            }
        }

        private void ButtonNewClock_Click(object sender, RoutedEventArgs e)
        {
            var item = new ClockDisplayItem("Clock")
            {
                Font = SharedModel.Instance.SelectedProfile!.Font,
                FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                Color = SharedModel.Instance.SelectedProfile!.Color

            };
            SharedModel.Instance.AddDisplayItem(item);
            SharedModel.Instance.SelectedItem = item;
        }

        private void ButtonNewCalendar_Click(object sender, RoutedEventArgs e)
        {
            var item = new CalendarDisplayItem("Calendar")
            {
                Font = SharedModel.Instance.SelectedProfile!.Font,
                FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                Color = SharedModel.Instance.SelectedProfile!.Color
            };
            SharedModel.Instance.AddDisplayItem(item);
            SharedModel.Instance.SelectedItem = item;

        }

        private void ButtonDuplicate_Click(object sender, RoutedEventArgs e)
        {
            if (SharedModel.Instance.SelectedItem != null)
            {
                var item = (DisplayItem)SharedModel.Instance.SelectedItem.Clone();
                SharedModel.Instance.AddDisplayItem(item);
                SharedModel.Instance.PushDisplayItemTo(item, SharedModel.Instance.SelectedItem);
                SharedModel.Instance.SelectedItem = item;
            }
        }

        private void ImageLogo_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            Process.Start("explorer.exe", "https://www.hwinfo.com/");
        }
    }
}
