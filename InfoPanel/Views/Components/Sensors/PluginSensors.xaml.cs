using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.ViewModels.Components;
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
    public partial class PluginSensors : System.Windows.Controls.UserControl
    {
        private PluginSensorsVM ViewModel { get; set; }

        private readonly DispatcherTimer UpdateTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        public PluginSensors()
        {
            ViewModel = new PluginSensorsVM();
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

            foreach (PluginMonitor.PluginReading reading in PluginMonitor.GetOrderedList())
            {
                //construct plugin
                var parent = ViewModel.FindParentSensorItem(reading.PluginId);
                if (parent == null)
                {
                    parent = new PluginTreeItem(reading.PluginId, reading.PluginName);
                    ViewModel.Sensors.Add(parent);
                }

                //construct container
                var container = parent.FindChild(reading.ContainerId);

                if (container == null)
                {
                    container = new PluginTreeItem(reading.ContainerId, reading.ContainerName);
                    parent.Children.Add(container);
                }

                //construct actual sensor
                var child = container.FindChild(reading.Id);
                if (child == null)
                {
                    child = new PluginSensorItem(reading.Id, reading.Name, reading.Id);
                    container.Children.Add(child);
                }
            }
        }

        private void TreeViewInfo_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is PluginSensorItem sensorItem)
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



        private void ButtonAddHttpImage_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is PluginSensorItem sensorItem && SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new HttpImageDisplayItem(sensorItem.Name, selectedProfile)
                {
                    Width = 100,
                    Height = 100,
                    PluginSensorId = sensorItem.SensorId,
                    SensorType = Enums.SensorType.Plugin
                };
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddTableSensor_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is PluginSensorItem sensorItem && SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new TableSensorDisplayItem(sensorItem.Name, selectedProfile, sensorItem.SensorId);
                if(SensorReader.ReadPluginSensor(sensorItem.SensorId) is SensorReading sensorReading && sensorReading.ValueTableFormat is string format){
                    item.TableFormat = format;
                }
                SharedModel.Instance.AddDisplayItem(item);
            }
        }
    }
}
