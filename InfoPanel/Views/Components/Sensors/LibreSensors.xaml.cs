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


    }
}
