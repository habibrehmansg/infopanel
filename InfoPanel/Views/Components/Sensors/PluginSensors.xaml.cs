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


        private void ButtonSelect_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is PluginSensorItem sensorItem)
            {
                var item = new SensorDisplayItem(sensorItem.Name)
                {
                    SensorType = Enums.SensorType.Plugin,
                    PluginSensorId = sensorItem.SensorId,
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
            if (ViewModel.SelectedItem is PluginSensorItem sensorItem)
            {
                if (SharedModel.Instance.SelectedItem is SensorDisplayItem displayItem)
                {
                    displayItem.Name = sensorItem.Name;
                    displayItem.SensorName = sensorItem.Name;
                    displayItem.SensorType = Enums.SensorType.Plugin;
                    displayItem.PluginSensorId = sensorItem.SensorId;
                    displayItem.Unit = sensorItem.Unit;
                }
                else if (SharedModel.Instance.SelectedItem is ChartDisplayItem chartDisplayItem)
                {
                    chartDisplayItem.Name = sensorItem.Name;
                    chartDisplayItem.SensorName = sensorItem.Name;
                    chartDisplayItem.SensorType = Enums.SensorType.Plugin;
                    chartDisplayItem.PluginSensorId = sensorItem.SensorId;
                }
                else if (SharedModel.Instance.SelectedItem is GaugeDisplayItem gaugeDisplayItem)
                {
                    gaugeDisplayItem.Name = sensorItem.Name;
                    gaugeDisplayItem.SensorName = sensorItem.Name;
                    gaugeDisplayItem.SensorType = Enums.SensorType.Plugin;
                    gaugeDisplayItem.PluginSensorId = sensorItem.SensorId;
                }
                else if (SharedModel.Instance.SelectedItem is SensorImageDisplayItem sensorImageDisplayItem)
                {
                    sensorImageDisplayItem.Name = sensorItem.Name;
                    sensorImageDisplayItem.SensorName = sensorItem.Name;
                    sensorImageDisplayItem.SensorType = Enums.SensorType.Plugin;
                    sensorImageDisplayItem.PluginSensorId = sensorItem.SensorId;
                }
                else if (SharedModel.Instance.SelectedItem is HttpImageDisplayItem httpImageDisplayItem)
                {
                    httpImageDisplayItem.Name = sensorItem.Name;
                    httpImageDisplayItem.SensorName = sensorItem.Name;
                    httpImageDisplayItem.SensorType = Enums.SensorType.Plugin;
                    httpImageDisplayItem.PluginSensorId = sensorItem.SensorId;
                }
                else if (SharedModel.Instance.SelectedItem is TableSensorDisplayItem tableSensorDisplayItem)
                {
                    tableSensorDisplayItem.Name = sensorItem.Name;
                    tableSensorDisplayItem.SensorName = sensorItem.Name;
                    tableSensorDisplayItem.SensorType = Enums.SensorType.Plugin;
                    tableSensorDisplayItem.PluginSensorId = sensorItem.SensorId;
                    if (SensorReader.ReadPluginSensor(sensorItem.SensorId) is SensorReading sensorReading && sensorReading.ValueTableFormat is string format)
                    {
                        tableSensorDisplayItem.TableFormat = format;
                    }
                }
            }
        }

        private void ButtonAddGraph_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is PluginSensorItem sensorItem)
            {
                var item = new GraphDisplayItem(sensorItem.Name, GraphDisplayItem.GraphType.LINE);
                item.PluginSensorId = sensorItem.SensorId;
                item.SensorType = Enums.SensorType.Plugin;
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddBar_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is PluginSensorItem sensorItem)
            {
                var item = new BarDisplayItem(sensorItem.Name);
                item.PluginSensorId = sensorItem.SensorId;
                item.SensorType = Enums.SensorType.Plugin;
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddDonut_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is PluginSensorItem sensorItem)
            {
                var item = new DonutDisplayItem(sensorItem.Name);
                item.PluginSensorId = sensorItem.SensorId;
                item.SensorType = Enums.SensorType.Plugin;
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddCustom_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is PluginSensorItem sensorItem)
            {
                var item = new GaugeDisplayItem(sensorItem.Name);
                item.PluginSensorId = sensorItem.SensorId;
                item.SensorType = Enums.SensorType.Plugin;
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddSensorImage_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is PluginSensorItem sensorItem && SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new SensorImageDisplayItem(sensorItem.Name, selectedProfile.Guid)
                {
                    Width = 100,
                    Height = 100,
                    PluginSensorId = sensorItem.SensorId,
                    SensorType = Enums.SensorType.Plugin
                };
                SharedModel.Instance.AddDisplayItem(item);
            }
        }

        private void ButtonAddHttpImage_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedItem is PluginSensorItem sensorItem && SharedModel.Instance.SelectedProfile is Profile selectedProfile)
            {
                var item = new HttpImageDisplayItem(sensorItem.Name, selectedProfile.Guid)
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
            if (ViewModel.SelectedItem is PluginSensorItem sensorItem)
            {
                var item = new TableSensorDisplayItem(sensorItem.Name, sensorItem.SensorId);
                if(SensorReader.ReadPluginSensor(sensorItem.SensorId) is SensorReading sensorReading && sensorReading.ValueTableFormat is string format){
                    item.TableFormat = format;
                }
                SharedModel.Instance.AddDisplayItem(item);
            }
        }
    }
}
