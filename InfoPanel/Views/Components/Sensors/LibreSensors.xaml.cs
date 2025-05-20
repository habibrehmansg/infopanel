using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.ViewModels.Components;
using LibreHardwareMonitor.Hardware;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for HWiNFOSensors.xaml
    /// </summary>
    public partial class LibreSensors : System.Windows.Controls.UserControl
    {
        private LibreSensorsVM ViewModel { get; set; }

        private readonly DispatcherTimer UpdateTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        public LibreSensors()
        {
            ViewModel = new LibreSensorsVM();
            DataContext = ViewModel;

            InitializeComponent();

            Loaded += LibreSensors_Loaded;
            Unloaded += LibreSensors_Unloaded;
        }

        private void LibreSensors_Loaded(object sender, RoutedEventArgs e)
        {
            if (UpdateTimer != null)
            {
                UpdateTimer.Tick += Timer_Tick;

                Timer_Tick(this, new EventArgs());
                UpdateTimer.Start();
            }
        }

        private void LibreSensors_Unloaded(object sender, RoutedEventArgs e)
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
            //for (int parentIndex = ViewModel.Sensors.Count - 1; parentIndex >= 0; parentIndex--)
            //{
            //    var parent = ViewModel.Sensors[parentIndex];

            //    for (int typeIndex = parent.Children.Count - 1; typeIndex >= 0; typeIndex--)
            //    {
            //        var type = parent.Children[typeIndex];

            //        for (int i = type.Children.Count - 1; i >= 0; i--)
            //        {
            //            if (type.Children[i] is LibreSensorItem item)
            //            {
            //                if (!LibreMonitor.SENSORHASH.ContainsKey(item.SensorId))
            //                {
            //                    type.Children.RemoveAt(i);
            //                }
            //            }
            //        }

            //        if (type.Children.Count == 0)
            //        {
            //            parent.Children.RemoveAt(typeIndex);
            //        }
            //    }

            //    if (parent.Children.Count == 0)
            //    {
            //        ViewModel.Sensors.RemoveAt(parentIndex);
            //    }
            //}


            foreach (ISensor hash in LibreMonitor.GetOrderedList())
            {
                var parentIdentifier = hash.Hardware.Parent?.Identifier ?? hash.Hardware.Identifier;
                var parentName = hash.Hardware.Parent?.Name ?? hash.Hardware.Name;
                var hardwareType = hash.Hardware.Parent?.HardwareType ?? hash.Hardware.HardwareType;

                //construct parent
                var parent = ViewModel.FindParentSensorItem(parentIdentifier);
                if (parent == null)
                {
                    parent = new LibreHardwareTreeItem(parentIdentifier, parentName, hardwareType);
                    ViewModel.Sensors.Add(parent);
                }

                //construct type grouping
                var group = parent.FindChild(hash.SensorType);

                if (group == null)
                {
                    group = new LibreGroupTreeItem(hash.SensorType, hash.SensorType.ToString(), hash.SensorType);
                    parent.Children.Add(group);
                }

                //construct actual sensor
                var child = group.FindChild(hash.Identifier);
                if (child == null)
                {
                    child = new LibreSensorItem(hash.Identifier, hash.Name, hash.Identifier.ToString());
                    group.Children.Add(child);
                }
            }
        }

        private void TreeViewInfo_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is LibreSensorItem sensorItem)
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
            if (ViewModel.SelectedItem is LibreSensorItem sensorItem)
            {
                var item = new SensorDisplayItem(sensorItem.Name, sensorItem.SensorId)
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
            if (ViewModel.SelectedItem is LibreSensorItem sensorItem)
            {
                if (SharedModel.Instance.SelectedItem is SensorDisplayItem displayItem)
                {
                    displayItem.Name = sensorItem.Name;
                    displayItem.SensorName = sensorItem.Name;
                    displayItem.SensorType = Enums.SensorType.Libre;
                    displayItem.LibreSensorId = sensorItem.SensorId;
                    displayItem.Unit = sensorItem.Unit;
                }
                else if (SharedModel.Instance.SelectedItem is ChartDisplayItem chartDisplayItem)
                {
                    chartDisplayItem.Name = sensorItem.Name;
                    chartDisplayItem.SensorName = sensorItem.Name;
                    chartDisplayItem.SensorType = Enums.SensorType.Libre;
                    chartDisplayItem.LibreSensorId = sensorItem.SensorId;
                }
                else if (SharedModel.Instance.SelectedItem is GaugeDisplayItem gaugeDisplayItem)
                {
                    gaugeDisplayItem.Name = sensorItem.Name;
                    gaugeDisplayItem.SensorName = sensorItem.Name;
                    gaugeDisplayItem.SensorType = Enums.SensorType.Libre;
                    gaugeDisplayItem.LibreSensorId = sensorItem.SensorId;
                }
                else if (SharedModel.Instance.SelectedItem is SensorImageDisplayItem sensorImageDisplayItem)
                {
                    sensorImageDisplayItem.Name = sensorItem.Name;
                    sensorImageDisplayItem.SensorName = sensorItem.Name;
                    sensorImageDisplayItem.SensorType = Enums.SensorType.Libre;
                    sensorImageDisplayItem.LibreSensorId = sensorItem.SensorId;
                }
            }
        }

        private void ButtonAddGraph_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is LibreSensorItem sensorItem)
            {
                var item = new GraphDisplayItem(sensorItem.Name, GraphDisplayItem.GraphType.LINE, sensorItem.SensorId);
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddBar_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is LibreSensorItem sensorItem)
            {
                var item = new BarDisplayItem(sensorItem.Name, sensorItem.SensorId);
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddDonut_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is LibreSensorItem sensorItem)
            {
                var item = new DonutDisplayItem(sensorItem.Name, sensorItem.SensorId);
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddCustom_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is LibreSensorItem sensorItem)
            {
                var item = new GaugeDisplayItem(sensorItem.Name, sensorItem.SensorId);
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddSensorImage_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is LibreSensorItem sensorItem && SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new SensorImageDisplayItem(sensorItem.Name, selectedProfile.Guid, sensorItem.SensorId)
                {
                    Width = 100,
                    Height = 100,
                };
                SharedModel.Instance.AddDisplayItem(item);
            }
        }
    }
}
