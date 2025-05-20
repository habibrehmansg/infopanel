using InfoPanel.Models;
using InfoPanel.ViewModels.Components;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for HWiNFOSensors.xaml
    /// </summary>
    public partial class HwInfoSensors : System.Windows.Controls.UserControl
    {
        private HwInfoSensorsVM ViewModel { get; set; }

        private readonly DispatcherTimer UpdateTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        public HwInfoSensors()
        {
            ViewModel = new HwInfoSensorsVM();
            DataContext = ViewModel;

            InitializeComponent();

            Loaded += HwInfoSensors_Loaded;
            Unloaded += HwInfoSensors_Unloaded;
        }

        private void HwInfoSensors_Loaded(object sender, RoutedEventArgs e)
        {
            if (UpdateTimer != null)
            {
                UpdateTimer.Tick += Timer_Tick;

                Timer_Tick(this, new EventArgs());
                UpdateTimer.Start();
            }
        }

        private void HwInfoSensors_Unloaded(object sender, RoutedEventArgs e)
        {
            if (UpdateTimer != null)
            {
                UpdateTimer.Stop();
                UpdateTimer.Tick -= Timer_Tick;
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            LoadSensorTree();
            UpdateSensorDetails();
        }

        private void LoadSensorTree()
        {
            foreach (HWHash.HWINFO_HASH hash in HWHash.GetOrderedList())
            {
                //construct parent
                var parent = ViewModel.FindParentSensorItem(hash.ParentUniqueID);
                if(parent == null)
                {
                    parent = new TreeItem(hash.ParentUniqueID, hash.ParentNameDefault);
                    ViewModel.Sensors.Add(parent);
                }

                TreeItem? group;
                if(hash.ReadingType != "Other" && hash.ReadingType != "None")
                {
                    //construct type grouping
                    group = parent.FindChild(hash.ReadingType);

                    if (group == null)
                    {
                        group = new TreeItem(hash.ReadingType, hash.ReadingType);
                        parent.Children.Add(group);
                    }
                } else
                {
                    group = parent;
                }

                //construct actual sensor
                var child = group.FindChild(hash.UniqueID);
                if (child == null)
                {
                    child = new HwInfoSensorItem(hash.UniqueID, hash.NameDefault, hash.ParentID, hash.ParentInstance, hash.SensorID);
                    group.Children.Add(child);
                }
            }
        }

        private void TreeViewInfo_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is HwInfoSensorItem sensorItem)
            {
                ViewModel.SelectedItem = sensorItem;
                sensorItem.Update();
            }
            else
            {
                ViewModel.SelectedItem = null;
            }
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scrollViewer = (ScrollViewer)sender;
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        private void UpdateSensorDetails()
        {
            ViewModel.SelectedItem?.Update();
        }

        private void ButtonSelect_Click(object sender, RoutedEventArgs e)
        {
            if(ViewModel.SelectedItem is HwInfoSensorItem sensorItem)
            {
                var item = new SensorDisplayItem(sensorItem.Name, sensorItem.ParentId, sensorItem.ParentInstance, sensorItem.SensorId)
                {
                    Font = SharedModel.Instance.SelectedProfile!.Font,
                    FontSize = SharedModel.Instance.SelectedProfile!.FontSize,
                    Color = SharedModel.Instance.SelectedProfile!.Color,
                    Unit = sensorItem.Unit,
                };

                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonReplace_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is HwInfoSensorItem sensorItem)
            {
                if (SharedModel.Instance.SelectedItem is SensorDisplayItem displayItem)
                {
                    displayItem.Name = sensorItem.Name;
                    displayItem.SensorName = sensorItem.Name;
                    displayItem.SensorType = Enums.SensorType.HwInfo;
                    displayItem.Id = sensorItem.ParentId;
                    displayItem.Instance = sensorItem.ParentInstance;
                    displayItem.EntryId = sensorItem.SensorId;
                    displayItem.Unit = sensorItem.Unit;
                }
                else if (SharedModel.Instance.SelectedItem is ChartDisplayItem chartDisplayItem)
                {
                    chartDisplayItem.Name = sensorItem.Name;
                    chartDisplayItem.SensorName = sensorItem.Name;
                    chartDisplayItem.SensorType = Enums.SensorType.HwInfo;
                    chartDisplayItem.Id = sensorItem.ParentId;
                    chartDisplayItem.Instance = sensorItem.ParentInstance;
                    chartDisplayItem.EntryId = sensorItem.SensorId;
                }
                else if (SharedModel.Instance.SelectedItem is GaugeDisplayItem gaugeDisplayItem)
                {
                    gaugeDisplayItem.Name = sensorItem.Name;
                    gaugeDisplayItem.SensorName = sensorItem.Name;
                    gaugeDisplayItem.SensorType = Enums.SensorType.HwInfo;
                    gaugeDisplayItem.Id = sensorItem.ParentId;
                    gaugeDisplayItem.Instance = sensorItem.ParentInstance;
                    gaugeDisplayItem.EntryId = sensorItem.SensorId;
                } else if(SharedModel.Instance.SelectedItem is SensorImageDisplayItem sensorImageDisplayItem)
                {
                    sensorImageDisplayItem.Name = sensorItem.Name;
                    sensorImageDisplayItem.SensorName = sensorItem.Name;
                    sensorImageDisplayItem.SensorType = Enums.SensorType.HwInfo;
                    sensorImageDisplayItem.Id = sensorItem.ParentId;
                    sensorImageDisplayItem.Instance = sensorItem.ParentInstance;
                    sensorImageDisplayItem.EntryId = sensorItem.SensorId;
                }
            }
        }

        private void ButtonAddGraph_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is HwInfoSensorItem sensorItem)
            {
                var item = new GraphDisplayItem(sensorItem.Name, GraphDisplayItem.GraphType.LINE, sensorItem.ParentId, sensorItem.ParentInstance, sensorItem.SensorId);
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddBar_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is HwInfoSensorItem sensorItem)
            {
                var item = new BarDisplayItem(sensorItem.Name, sensorItem.ParentId, sensorItem.ParentInstance, sensorItem.SensorId);
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddDonut_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is HwInfoSensorItem sensorItem)
            {
                var item = new DonutDisplayItem(sensorItem.Name, sensorItem.ParentId, sensorItem.ParentInstance, sensorItem.SensorId);
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddCustom_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is HwInfoSensorItem sensorItem)
            {
                var item = new GaugeDisplayItem(sensorItem.Name, sensorItem.ParentId, sensorItem.ParentInstance, sensorItem.SensorId);
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddSensorImage_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is HwInfoSensorItem sensorItem && SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new SensorImageDisplayItem(sensorItem.Name, selectedProfile.Guid, sensorItem.ParentId, sensorItem.ParentInstance, sensorItem.SensorId)
                {
                    Width = 100,
                    Height = 100,
                };
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ImageLogo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Process.Start("explorer.exe", "https://www.hwinfo.com/");
        }
    }
}
